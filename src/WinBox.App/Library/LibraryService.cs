using WinBox.Core.Boxes;
using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;
using WinBox.Platform;
using WinBox.Platform.Import;
using WinBox.Platform.Packaging;
using WinBox.Platform.Sandbox;

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
    public bool CanShow => Activity == BoxActivity.Running;
    public bool CanStop => Activity is BoxActivity.Starting or BoxActivity.Running;
    public bool CanRemove => Activity is BoxActivity.Idle or BoxActivity.Running;
}

/// <summary>One of the user's places a box may reach, as the card's menu lists it.</summary>
public sealed record HostPlace(string Label, string Path);

/// <summary>How a removal went: whether the box is gone, and what of it could not be cleaned up.</summary>
public sealed record RemoveOutcome(bool Removed, IReadOnlyList<string> Leftovers);

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

/// <param name="Unfinished">
/// Something goes wrong if WinBox exits now: an app is running or starting (only WinBox can bring back its window
/// and report on its run), a stop has not reached its box yet, or an import or a removal is under way.
/// </param>
/// <param name="Settled">Nothing is going on at all, down to the host scan that follows each run.</param>
public sealed record LibrarySnapshot(
    IReadOnlyList<BoxEntry> Boxes,
    IReadOnlyList<BrokenBox> Broken,
    IReadOnlyList<ImportJob> Imports,
    IReadOnlyList<HostReference> Leaks,
    string? Problem,
    bool Unfinished,
    bool Settled)
{
    /// <summary>Boxes whose app is up or coming up: what the tray lists, and what quitting WinBox stops.</summary>
    public IReadOnlyList<BoxEntry> Running => [.. Boxes.Where(b => b.Activity is BoxActivity.Starting or BoxActivity.Running)];

    public int Importing => Imports.Count(j => !j.Failed);
}

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

    /// <summary>Stops asked for before the box's BoxHost was listening; sent as soon as it reports in.</summary>
    private readonly HashSet<Guid> _pendingStops = [];
    private readonly Dictionary<Guid, string> _problems = [];
    private readonly Dictionary<Guid, string> _icons = [];
    private readonly List<ImportJob> _imports = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _importCancels = [];
    private List<LoadedBox> _boxes = [];
    private List<BrokenBox> _broken = [];
    private IReadOnlyList<HostReference> _leaks = [];
    private string? _problem;
    private readonly CancellationTokenSource _stopping = new();
    private int _started;

    /// <summary>Runs and removals not yet followed to the end, the host scan after each included.</summary>
    private int _followUps;

    /// <summary>Set by <see cref="QuitAll"/>: nothing new starts.</summary>
    private bool _quitting;

    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(75);

    public LibraryService(WinBoxHost host) => _host = host;

    /// <summary>
    /// The user's own places every box may reach: the personal folders and the data drives (their top-level
    /// folders are reached from the drive). The same for all boxes; looked up once.
    /// </summary>
    public IReadOnlyList<HostPlace> HostPlaces => _places.Value;

    private readonly Lazy<IReadOnlyList<HostPlace>> _places = new(() =>
        [.. WindowsHostAreas.PersonalFolders().Concat(WindowsHostAreas.DataDrives()).Select(p => new HostPlace(Texts.Place(p), p))]);

    /// <summary>Opens one of the box's folders in Explorer, outside the box. Null when it was opened.</summary>
    public Task<ManualOpenRefusal?> OpenFolderOutsideAsync(Guid id, string folder) => OpenOutsideAsync(id, folder, HostOpener.OpenFolder);

    /// <summary>Opens a file the box may reach with its program on this computer, outside the box. Null when it was opened.</summary>
    public Task<ManualOpenRefusal?> OpenFileOutsideAsync(Guid id, string file) => OpenOutsideAsync(id, file, HostOpener.OpenFile);

    private Task<ManualOpenRefusal?> OpenOutsideAsync(Guid id, string path, Func<LaunchSpec, string, ManualOpenRefusal?> open)
    {
        LoadedBox? box;
        lock (_lock)
            box = _boxes.FirstOrDefault(b => b.Config.Id == id);
        if (box is null)
            return Task.FromResult<ManualOpenRefusal?>(ManualOpenRefusal.Missing);
        return Task.Run(() =>
        {
            // The grants the box's next run gets: what WinBox opens for the user is what the sandbox lets the box reach.
            var refusal = open(_host.Planner.Plan(box, DateTimeOffset.Now), path);
            AppLog.Write($"open outside the box {id:N}: {path} -> {refusal?.ToString() ?? "opened"}");
            return refusal;
        });
    }

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
            var unfinished = boxes.Any(b => b.Activity is BoxActivity.Starting or BoxActivity.Running or BoxActivity.Removing)
                || _pendingStops.Count > 0 || _importCancels.Count > 0;
            var settled = !unfinished && boxes.All(b => b.Activity == BoxActivity.Idle) && _followUps == 0;
            return new LibrarySnapshot(boxes, [.. _broken], [.. _imports], _leaks, _problem, unfinished, settled);
        }
    }

    /// <summary>Loads the library, checks the host and starts following boxes run from elsewhere. Runs once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;
        _host.Paths.EnsureCreated();
        try
        {
            _host.Store.CleanUpInterruptedImports();
            _host.Store.CleanUpInterruptedDeletes();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An extraction from a previous WinBox may still hold files; the library loads all the same.
            AppLog.Write($"cleaning up after an earlier run failed: {e.Message}");
        }
        var sandbox = _host.Sandbox.GetSupport();
        var packages = _host.Packages.GetUnavailableReason();
        lock (_lock)
            _problem = !sandbox.IsSupported ? Texts.SandboxUnavailable(sandbox.Reason) : packages is null ? null : Texts.PackagesUnavailable(packages);
        Reload();
        _ = Task.Run(ScanHostAsync);
        _ = Task.Run(FollowRunsAsync);
        _ = Task.Run(() => SweepLeftoversAsync(TimeSpan.Zero));
    }

    /// <summary>
    /// Clears Windows' records of boxes that are gone (see <see cref="WinBoxHost.FindLeftovers"/>), backing them
    /// up first. Runs at startup and again a little after each removal: Windows files some of these records
    /// only once the package has been removed.
    /// </summary>
    private async Task SweepLeftoversAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stopping.Token);
            var leftovers = await Task.Run(_host.FindLeftovers);
            if (leftovers.Count == 0)
                return;
            var cleanup = await Task.Run(() => _host.Guard.Remove(leftovers, _host.Paths.Boxes, GuardBackup("leftovers")));
            AppLog.Write($"cleared {cleanup.Removed} records of deleted boxes (backup: {cleanup.BackupDirectory})");
            foreach (var failure in cleanup.Failures)
                AppLog.Write($"  could not clear {failure}");
        }
        catch (OperationCanceledException)
        {
            // WinBox is closing.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write($"clearing records of deleted boxes failed: {e.Message}");
        }
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

    /// <summary>
    /// Imports what was dropped on the window. Only plain files on a local disk are taken; anything else gets
    /// a card saying why, so nothing dropped disappears silently.
    /// </summary>
    public void Drop(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (DropRejection(path) is { } reason)
            {
                lock (_lock)
                    _imports.Add(new ImportJob(Guid.NewGuid(), Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), ImportStep.Inspecting, null, reason));
                Notify();
            }
            else
                files.Add(path);
        }
        Import(files);
    }

    private static string? DropRejection(string path)
    {
        // UNC and device paths (\\server\share, \\?\...) are not local disks: opening them can authenticate
        // to another machine.
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return Texts.DropNotLocal;
        if (Directory.Exists(path))
            return Texts.DropFolder;
        if (!File.Exists(path))
            return Texts.DropMissing;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            return Texts.DropNotLocal;
        if (Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url")
            return Texts.DropShortcut;
        return null;
    }

    public void Import(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var job = new ImportJob(Guid.NewGuid(), Path.GetFileName(file), ImportStep.Inspecting, null, null);
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            lock (_lock)
            {
                if (_quitting)
                {
                    cancel.Dispose();
                    return;
                }
                _imports.Add(job);
                _importCancels[job.Key] = cancel;
            }
            Notify();
            _ = Task.Run(() => ImportAsync(job, file, cancel));
        }
    }

    /// <summary>Stops an import in progress; its card goes once the import has cleaned up after itself.</summary>
    public void CancelImport(Guid key)
    {
        lock (_lock)
            _importCancels.GetValueOrDefault(key)?.Cancel();
    }

    public void DismissImport(Guid key)
    {
        lock (_lock)
            _imports.RemoveAll(j => j.Key == key);
        Notify();
    }

    private async Task ImportAsync(ImportJob job, string file, CancellationTokenSource cancel)
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
            var box = await _host.Import.ImportAsync(file, progress, cancel.Token);
            Update(j => j with { Step = ImportStep.Registering, Download = null });
            await RegisterAsync(box);
            await ExtractIconAsync(box);
            lock (_lock)
                _imports.RemoveAll(j => j.Key == job.Key);
            Reload();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled from its card, or WinBox is closing; the half-made box is already gone.
            lock (_lock)
                _imports.RemoveAll(j => j.Key == job.Key);
            Notify();
        }
        catch (ImportException e)
        {
            Update(j => j with { Error = Texts.ImportFailed(e.Failure, e.Detail) });
        }
        catch (TimeoutException)
        {
            Update(j => j with { Error = Texts.DownloadStalled });
        }
        catch (OperationCanceledException)
        {
            // Nobody cancelled it: HttpClient's own timeout ran out before the server answered.
            Update(j => j with { Error = Texts.NetworkTimeout });
        }
        catch (HttpRequestException e)
        {
            Update(j => j with { Error = Texts.NetworkFailed(e.Message) });
        }
        catch (Exception e)
        {
            Update(j => j with { Error = Texts.Unexpected(e) });
        }
        finally
        {
            lock (_lock)
                _importCancels.Remove(job.Key);
            cancel.Dispose();
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
        // Removal unregisters whatever package the library holds, so it must see this one.
        lock (_lock)
            _boxes = [.. _boxes.Select(b => b.Config.Id == updated.Config.Id ? updated : b)];
        return updated;
    }

    public void Open(Guid id)
    {
        LoadedBox? box;
        lock (_lock)
        {
            box = _boxes.FirstOrDefault(b => b.Config.Id == id);
            if (_quitting || box is null || _activity.GetValueOrDefault(id) != BoxActivity.Idle)
                return;
            _activity[id] = BoxActivity.Starting;
            _ownRuns.Add(id);
            _problems.Remove(id);
            _followUps++;
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
            bool stopped;
            lock (_lock)
                stopped = _pendingStops.Contains(id);
            // Quit while its package was being registered (WinBox is quitting, say): there is nothing to start.
            if (!stopped)
            {
                var spec = _host.Planner.Plan(box, DateTimeOffset.Now);
                var record = await _host.PackagedRunner.RunAsync(box, spec, onUpdate: r =>
                {
                    DeliverPendingStops();
                    if (r.State == BoxRunState.Running)
                        SetActivity(id, current => current == BoxActivity.Starting ? BoxActivity.Running : current);
                    // "Saved" is only said when BoxShim says so; a failed save shows up as a problem once the run ends.
                    else if (r.State == BoxRunState.Closing && r.StateSaved == true)
                        SetActivity(id, current => current == BoxActivity.Removing ? current : BoxActivity.Closing);
                });
                problem = Texts.RunProblem(record);
            }
        }
        catch (Exception e)
        {
            problem = Texts.Unexpected(e);
        }
        lock (_lock)
        {
            _ownRuns.Remove(id);
            _pendingStops.Remove(id);
            // A removal that stopped this run owns the box's state until it is done.
            if (_activity.GetValueOrDefault(id) != BoxActivity.Removing)
                _activity[id] = BoxActivity.Idle;
            if (problem is not null)
                _problems[id] = problem;
        }
        Notify();
        try
        {
            await ScanHostAsync();
        }
        finally
        {
            lock (_lock)
                _followUps--;
            Notify();
        }
    }

    /// <summary>
    /// Quits the app: it is asked to end as at sign-out and saves its own data, whatever does not end in time is
    /// ended, and BoxShim saves the box's state either way.
    /// </summary>
    public void Stop(Guid id)
    {
        if (!SetActivity(id, current => current is BoxActivity.Starting or BoxActivity.Running ? BoxActivity.Stopping : current))
            return;
        if (PackagedBoxRunner.RequestStop(id))
            return;
        // Still starting and BoxHost not listening yet: the stop goes out as soon as it reports in. Dropping it
        // instead would leave an app running that the card says is not.
        lock (_lock)
            _pendingStops.Add(id);
        Notify();
    }

    /// <summary>Sends the stops asked for before their BoxHost was listening to those listening now.</summary>
    private void DeliverPendingStops()
    {
        int delivered;
        lock (_lock)
            delivered = _pendingStops.RemoveWhere(PackagedBoxRunner.RequestStop);
        if (delivered > 0)
            Notify();
    }

    /// <summary>
    /// Winds WinBox's work down before it exits: every app running or starting is asked to quit and every import
    /// is cancelled, and nothing new starts. <see cref="WaitForQuitAsync"/> says when WinBox may go.
    /// </summary>
    public void QuitAll()
    {
        List<Guid> running;
        List<CancellationTokenSource> imports;
        lock (_lock)
        {
            _quitting = true;
            running = [.. _boxes.Select(b => b.Config.Id)
                .Where(id => _activity.GetValueOrDefault(id) is BoxActivity.Starting or BoxActivity.Running)];
            imports = [.. _importCancels.Values];
        }
        AppLog.Write($"quitting: stopping {running.Count} boxes, cancelling {imports.Count} imports");
        foreach (var id in running)
            Stop(id);
        // Outside the lock: a cancelled import may finish on this very thread, and it takes the lock.
        foreach (var cancel in imports)
            cancel.Cancel();
    }

    /// <summary>
    /// Waits after <see cref="QuitAll"/> until WinBox may exit: every stop has reached its box, whose BoxHost then
    /// quits the app and saves its state on its own; every import has cleaned up after itself (innoextract
    /// ended, the half-made box deleted); no removal is half done. False if that took longer than
    /// <paramref name="limit"/>.
    /// </summary>
    public async Task<bool> WaitForQuitAsync(TimeSpan limit)
    {
        var deadline = DateTimeOffset.Now + limit;
        while (true)
        {
            DeliverPendingStops();
            lock (_lock)
            {
                if (_pendingStops.Count == 0 && _importCancels.Count == 0 && !_activity.ContainsValue(BoxActivity.Removing))
                    return true;
            }
            if (DateTimeOffset.Now >= deadline)
                return false;
            await Task.Delay(250);
        }
    }

    /// <summary>Brings back the window of a running app that hid itself, typically in the notification area.</summary>
    public void Show(Guid id)
    {
        LoadedBox? box;
        lock (_lock)
        {
            box = _boxes.FirstOrDefault(b => b.Config.Id == id);
            if (box is null || _activity.GetValueOrDefault(id) != BoxActivity.Running)
                return;
            _problems.Remove(id);
        }
        try
        {
            PackagedBoxRunner.RequestShow(box);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"showing {id:N} failed: {e}");
            lock (_lock)
                _problems[id] = Texts.Unexpected(e);
        }
        Notify();
    }

    /// <summary>Deletes a box. The outcome says whether it is gone and what of it could not be cleaned up.</summary>
    public async Task<RemoveOutcome> RemoveAsync(Guid id)
    {
        LoadedBox? box;
        bool wasRunning;
        lock (_lock)
        {
            box = _boxes.FirstOrDefault(b => b.Config.Id == id);
            wasRunning = _activity.GetValueOrDefault(id) != BoxActivity.Idle;
            if (box is not null)
                _followUps++;
        }
        if (box is null)
            return new RemoveOutcome(false, []);
        try
        {
            return await RemoveAsync(box, wasRunning);
        }
        finally
        {
            lock (_lock)
                _followUps--;
            Notify();
        }
    }

    private async Task<RemoveOutcome> RemoveAsync(LoadedBox box, bool wasRunning)
    {
        var id = box.Config.Id;
        SetActivity(id, _ => BoxActivity.Removing);
        RemoveOutcome outcome;
        try
        {
            outcome = await Task.Run(async () =>
            {
                if (wasRunning && PackagedBoxRunner.RequestStop(id))
                {
                    // BoxHost's own worst case: the quit and save graces, then killing the sandbox and MXC tearing it down.
                    var deadline = DateTimeOffset.Now + StopWait;
                    while (PackagedBoxRunner.IsRunning(box) && DateTimeOffset.Now < deadline)
                        await Task.Delay(500);
                }
                // Unregistering or deleting under a live sandbox would pull the app's files out from under it.
                if (PackagedBoxRunner.IsRunning(box))
                    throw new InvalidOperationException(Texts.StillRunning);
                if (box.Config.Package is { } package)
                    await _host.Packages.UnregisterAsync(package);
                // Windows' records of the box: those naming its files, and those it files under the package's
                // name and keeps after the package is gone. Cleared before the folder goes, so a folder that
                // cannot be deleted right away does not keep them around too.
                var family = box.Config.Package?.FamilyName;
                var references = _host.Guard.Scan(box.Paths.Root, f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
                var cleanup = references.Count > 0 ? _host.Guard.Remove(references, box.Paths.Root, GuardBackup($"remove-{id:N}")) : null;
                var leftovers = new List<string>(cleanup?.Failures ?? []);
                if (!_host.Store.Delete(box))
                    leftovers.Add(Texts.FolderLeftOver);
                return new RemoveOutcome(true, leftovers);
            });
            foreach (var leftover in outcome.Leftovers)
                AppLog.Write($"removing {id:N} left behind: {leftover}");
            _ = SweepLeftoversAsync(TimeSpan.FromSeconds(30));
            lock (_lock)
            {
                _activity.Remove(id);
                _problems.Remove(id);
                _icons.Remove(id);
            }
        }
        catch (Exception e)
        {
            AppLog.Write($"removing {id:N} failed: {e}");
            lock (_lock)
            {
                // The sandbox may still be up (it did not stop in time): the card must say so rather than offer Open.
                _activity[id] = PackagedBoxRunner.IsRunning(box) ? BoxActivity.Running : BoxActivity.Idle;
                _problems[id] = Texts.RemoveFailed(e);
            }
            outcome = new RemoveOutcome(false, []);
        }
        Reload();
        await ScanHostAsync();
        return outcome;
    }

    public async Task<GuardCleanup> FixLeaksAsync()
    {
        // Scanned again rather than taken from the banner, which may be hours old.
        var root = _host.Paths.Boxes;
        var cleanup = await Task.Run(() => _host.Guard.Remove(
            _host.Guard.Scan(root).Where(r => r.Kind == HostReferenceKind.Leak).ToList(), root, GuardBackup("fix")));
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
                // A BoxHost that reported nothing new since it started listening still gets its stop.
                DeliverPendingStops();
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
