using WinBox.Core.Boxes;
using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Packaging;
using WinBox.Platform;
using WinBox.Platform.Import;
using WinBox.Platform.Packaging;

namespace WinBox.App.Library;

/// <summary>What a box is doing. Every "can the user do X now" question is answered from this alone.</summary>
public enum BoxActivity
{
    Idle,
    Starting,
    Running,
    Stopping,

    /// <summary>The app has exited and its state is saved; the sandbox is still being torn down.</summary>
    Closing,
    Removing,
}

public sealed record BoxEntry(LoadedBox Box, BoxActivity Activity, string? IconPath, string? Problem)
{
    public Guid Id => Box.Config.Id;
    public bool CanOpen => Activity == BoxActivity.Idle;
    public bool CanStop => Activity is BoxActivity.Starting or BoxActivity.Running;
    public bool CanRemove => Activity is BoxActivity.Idle or BoxActivity.Running;
}

public sealed record ImportJob(Guid Key, string FileName, ImportStep Step, DownloadProgress? Download, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>Import progress as the library shows it: the pipeline's stages plus registering the box's package.</summary>
public enum ImportStep
{
    Inspecting,
    Downloading,
    Extracting,
    CheckingCompatibility,
    Finishing,
    Registering,
}

public sealed record LibrarySnapshot(
    IReadOnlyList<BoxEntry> Boxes,
    IReadOnlyList<BrokenBox> Broken,
    IReadOnlyList<ImportJob> Imports,
    IReadOnlyList<HostReference> Leaks,
    string? Problem);

/// <summary>
/// The library behind the UI: boxes, what each is doing, imports in flight and the host guard. All work
/// runs off the UI thread; <see cref="Changed"/> fires (on any thread) whenever <see cref="Snapshot"/> differs.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly WinBoxHost _host;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, BoxActivity> _activity = [];
    private readonly HashSet<Guid> _ownRuns = [];
    private readonly Dictionary<Guid, string> _problems = [];
    private readonly Dictionary<Guid, string> _icons = [];
    private readonly List<ImportJob> _imports = [];
    private List<LoadedBox> _boxes = [];
    private List<BrokenBox> _broken = [];
    private IReadOnlyList<HostReference> _leaks = [];
    private string? _problem;
    private readonly CancellationTokenSource _stopping = new();
    private int _started;

    public LibraryService(WinBoxHost host) => _host = host;

    public event Action? Changed;

    public LibrarySnapshot Snapshot()
    {
        lock (_lock)
        {
            var boxes = _boxes
                .OrderBy(b => b.Config.CreatedAt)
                .Select(b => new BoxEntry(
                    b,
                    _activity.GetValueOrDefault(b.Config.Id),
                    _icons.GetValueOrDefault(b.Config.Id),
                    _problems.GetValueOrDefault(b.Config.Id)))
                .ToList();
            return new LibrarySnapshot(boxes, [.. _broken], [.. _imports], _leaks, _problem);
        }
    }

    /// <summary>Loads the library, checks the host and starts following boxes run from elsewhere. Runs once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;
        _host.Paths.EnsureCreated();
        _host.Store.CleanUpInterruptedImports();
        var sandbox = _host.Sandbox.GetSupport();
        var packages = _host.Packages.GetUnavailableReason();
        lock (_lock)
            _problem = !sandbox.IsSupported ? Texts.SandboxUnavailable(sandbox.Reason) : packages is null ? null : Texts.PackagesUnavailable(packages);
        Reload();
        _ = Task.Run(ScanHostAsync);
        _ = Task.Run(FollowRunsAsync);
    }

    public void Reload()
    {
        var entries = _host.Store.Load();
        lock (_lock)
        {
            _boxes = entries.OfType<LoadedBox>().ToList();
            _broken = entries.OfType<BrokenBox>().ToList();
            foreach (var box in _boxes)
            {
                if (File.Exists(box.Paths.Icon))
                    _icons[box.Config.Id] = box.Paths.Icon;
                if (!_activity.ContainsKey(box.Config.Id))
                    _activity[box.Config.Id] = PackagedBoxRunner.IsRunning(box) ? BoxActivity.Running : BoxActivity.Idle;
            }
        }
        foreach (var box in _boxes.Where(b => !File.Exists(b.Paths.Icon)))
            _ = Task.Run(() => ExtractIconAsync(box));
        Notify();
    }

    public void Import(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var job = new ImportJob(Guid.NewGuid(), Path.GetFileName(file), ImportStep.Inspecting, null, null);
            lock (_lock)
                _imports.Add(job);
            Notify();
            _ = Task.Run(() => ImportAsync(job, file));
        }
    }

    public void DismissImport(Guid key)
    {
        lock (_lock)
            _imports.RemoveAll(j => j.Key == key);
        Notify();
    }

    private async Task ImportAsync(ImportJob job, string file)
    {
        void Update(Func<ImportJob, ImportJob> change)
        {
            lock (_lock)
            {
                var at = _imports.FindIndex(j => j.Key == job.Key);
                if (at >= 0)
                    _imports[at] = change(_imports[at]);
            }
            Notify();
        }

        try
        {
            var progress = new InlineProgress<ImportProgress>(p => Update(j => j with { Step = StepOf(p.Stage), Download = p.Download }));
            var box = await _host.Import.ImportAsync(file, progress, _stopping.Token);
            Update(j => j with { Step = ImportStep.Registering, Download = null });
            await RegisterAsync(box);
            await ExtractIconAsync(box);
            lock (_lock)
                _imports.RemoveAll(j => j.Key == job.Key);
            Reload();
        }
        catch (ImportException e)
        {
            Update(j => j with { Error = Texts.ImportFailed(e.Failure, e.Detail) });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Update(j => j with { Error = Texts.Unexpected(e) });
        }
    }

    private static ImportStep StepOf(ImportStage stage) => stage switch
    {
        ImportStage.Inspecting => ImportStep.Inspecting,
        ImportStage.Downloading => ImportStep.Downloading,
        ImportStage.Extracting => ImportStep.Extracting,
        ImportStage.CheckingCompatibility => ImportStep.CheckingCompatibility,
        _ => ImportStep.Finishing,
    };

    private async Task<LoadedBox> RegisterAsync(LoadedBox box)
    {
        var package = await _host.Packages.RegisterAsync(box);
        var updated = box with { Config = box.Config with { Package = package } };
        _host.Store.Save(updated);
        return updated;
    }

    public void Open(Guid id)
    {
        LoadedBox? box;
        lock (_lock)
        {
            box = _boxes.FirstOrDefault(b => b.Config.Id == id);
            if (box is null || _activity.GetValueOrDefault(id) != BoxActivity.Idle)
                return;
            _activity[id] = BoxActivity.Starting;
            _ownRuns.Add(id);
            _problems.Remove(id);
        }
        Notify();
        _ = Task.Run(() => RunAsync(box));
    }

    private async Task RunAsync(LoadedBox box)
    {
        var id = box.Config.Id;
        string? problem = null;
        try
        {
            if (box.Config.Package is not { } package || !_host.Packages.IsRegistered(package))
                box = await RegisterAsync(box);
            var spec = _host.Planner.Plan(box, DateTimeOffset.Now);
            var record = await _host.PackagedRunner.RunAsync(box, spec, onUpdate: r =>
            {
                if (r.State == BoxRunState.Running)
                    SetActivity(id, current => current == BoxActivity.Starting ? BoxActivity.Running : current);
                else if (r.State == BoxRunState.Closing)
                    SetActivity(id, _ => BoxActivity.Closing);
            });
            problem = Texts.RunProblem(record);
        }
        catch (Exception e)
        {
            problem = Texts.Unexpected(e);
        }
        lock (_lock)
        {
            _ownRuns.Remove(id);
            _activity[id] = BoxActivity.Idle;
            if (problem is not null)
                _problems[id] = problem;
        }
        Notify();
        await ScanHostAsync();
    }

    /// <summary>Force quit: the app's processes are killed, BoxShim still saves the box's state.</summary>
    public void Stop(Guid id)
    {
        if (!SetActivity(id, current => current is BoxActivity.Starting or BoxActivity.Running ? BoxActivity.Stopping : current))
            return;
        if (!PackagedBoxRunner.RequestStop(id))
            SetActivity(id, _ => BoxActivity.Idle);
    }

    public async Task RemoveAsync(Guid id)
    {
        LoadedBox? box;
        bool wasRunning;
        lock (_lock)
        {
            box = _boxes.FirstOrDefault(b => b.Config.Id == id);
            wasRunning = _activity.GetValueOrDefault(id) != BoxActivity.Idle;
        }
        if (box is null)
            return;
        SetActivity(id, _ => BoxActivity.Removing);
        try
        {
            await Task.Run(async () =>
            {
                if (wasRunning && PackagedBoxRunner.RequestStop(id))
                {
                    var deadline = DateTimeOffset.Now.AddSeconds(30);
                    while (PackagedBoxRunner.IsRunning(box) && DateTimeOffset.Now < deadline)
                        await Task.Delay(500);
                }
                if (box.Config.Package is { } package)
                    await _host.Packages.UnregisterAsync(package);
                _host.Store.Delete(box);
                var references = _host.Guard.Scan(box.Paths.Root);
                if (references.Count > 0)
                    _host.Guard.Remove(references, GuardBackup($"remove-{id:N}"));
            });
            lock (_lock)
            {
                _activity.Remove(id);
                _problems.Remove(id);
                _icons.Remove(id);
            }
        }
        catch (Exception e)
        {
            lock (_lock)
            {
                _activity[id] = BoxActivity.Idle;
                _problems[id] = Texts.RemoveFailed(e);
            }
        }
        Reload();
        await ScanHostAsync();
    }

    public async Task<GuardCleanup> FixLeaksAsync()
    {
        IReadOnlyList<HostReference> leaks;
        lock (_lock)
            leaks = _leaks;
        var cleanup = await Task.Run(() => _host.Guard.Remove(leaks, GuardBackup("fix")));
        await ScanHostAsync();
        return cleanup;
    }

    public async Task ScanHostAsync()
    {
        try
        {
            var leaks = await Task.Run(() => _host.Guard.Scan(_host.Paths.Boxes).Where(r => r.Kind == HostReferenceKind.Leak).ToList());
            lock (_lock)
                _leaks = leaks;
            Notify();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The next scan (after the next run) tries again.
        }
    }

    /// <summary>Picks up boxes started or stopped outside this WinBox (the CLI, a previous session).</summary>
    private async Task FollowRunsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                var changed = false;
                lock (_lock)
                {
                    foreach (var box in _boxes)
                    {
                        var id = box.Config.Id;
                        if (_ownRuns.Contains(id) || _activity.GetValueOrDefault(id) == BoxActivity.Removing)
                            continue;
                        var current = _activity.GetValueOrDefault(id);
                        var next = !PackagedBoxRunner.IsRunning(box) ? BoxActivity.Idle
                            : PackagedBoxRunner.ReadRun(box)?.State == BoxRunState.Closing ? BoxActivity.Closing
                            : current == BoxActivity.Stopping ? current
                            : BoxActivity.Running;
                        if (next != current)
                        {
                            _activity[id] = next;
                            changed = true;
                        }
                    }
                }
                if (changed)
                    Notify();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExtractIconAsync(LoadedBox box)
    {
        var exe = Path.Combine(box.Paths.App, box.Config.Entry);
        if (await IconExtractor.TrySaveAsync(exe, box.Paths.Icon))
        {
            lock (_lock)
                _icons[box.Config.Id] = box.Paths.Icon;
            Notify();
        }
    }

    private bool SetActivity(Guid id, Func<BoxActivity, BoxActivity> change)
    {
        bool changed;
        lock (_lock)
        {
            var current = _activity.GetValueOrDefault(id);
            var next = change(current);
            changed = next != current;
            _activity[id] = next;
        }
        if (changed)
            Notify();
        return changed;
    }

    private string GuardBackup(string reason) =>
        Path.Combine(_host.Paths.Logs, "guard", $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{reason}");

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>Reports on the calling thread, so stages arrive in order (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
