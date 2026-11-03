using WinBox.Core.Boxes;
using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class BoxManagerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private sealed class Setup : IDisposable
    {
        public TestBox T { get; } = new();
        public FakePackages Packages { get; } = new();
        public FakeRunner Runner { get; } = new();
        public FakeGuard Guard { get; } = new();

        public BoxManager Manager(TimeSpan? stopWait = null, TimeSpan? lockWait = null) => new(
            T.Paths, T.Store,
            new ImportPipeline(T.Paths, T.Store, new NoInspector(), [], new NoDownloader(), []),
            Packages, Runner, Guard,
            new LaunchPlanner([], @"C:\Users\test\AppData\Local"),
            stopWait ?? Short, lockWait ?? Short);

        public LoadedBox Box(bool registered = true)
        {
            var id = Guid.NewGuid();
            var package = registered ? Packages.Add(id) : null;
            return T.Create(TestBox.Config(id, package: package));
        }

        public void Dispose() => T.Dispose();
    }

    [TestMethod]
    public async Task RunRegistersTheBoxPackageFirst()
    {
        using var s = new Setup();
        var box = s.Box(registered: false);
        LoadedBox? registered = null;

        var run = s.Manager().RunAsync(box, new RunOptions { Registered = b => registered = b });
        await s.Runner.Started(box.Config.Id);
        s.Runner.Finish(box.Config.Id);
        var record = await run;

        Assert.AreEqual(BoxRunState.Exited, record!.State);
        Assert.IsNotNull(registered?.Config.Package);
        // box.json records the package, so a removal later unregisters it.
        var saved = s.T.Store.Load().OfType<LoadedBox>().Single();
        Assert.AreEqual(registered!.Config.Package, saved.Config.Package);
        Assert.AreEqual(registered.Config.Package, s.Runner.StartedWith[0].Config.Package);
    }

    [TestMethod]
    public async Task AStopWhileThePackageIsRegisteredCallsTheStartOff()
    {
        using var s = new Setup();
        var box = s.Box(registered: false);

        var record = await s.Manager().RunAsync(box, new RunOptions { StillWanted = () => false });

        Assert.IsNull(record);
        Assert.AreEqual(0, s.Runner.StartedWith.Count);
    }

    [TestMethod]
    public async Task ARunningBoxIsNotStartedAgain()
    {
        using var s = new Setup();
        var box = s.Box();
        s.Runner.Running.Add(box.Config.Id);

        var busy = await Assert.ThrowsExactlyAsync<BoxBusyException>(() => s.Manager().RunAsync(box));

        Assert.AreEqual(BoxBusyReason.Running, busy.Reason);
        Assert.AreEqual(0, s.Runner.StartedWith.Count);
    }

    [TestMethod]
    public async Task TwoStartsOfOneBoxNeverOverlap()
    {
        using var s = new Setup();
        var box = s.Box();
        var manager = s.Manager(lockWait: TimeSpan.FromSeconds(10));
        s.Runner.HoldStarts = new TaskCompletionSource();

        // The first start is inside StartAsync when the second one comes: the second waits for the box's lock,
        // then finds the box running instead of starting it again.
        var first = manager.RunAsync(box);
        await s.Runner.Started(box.Config.Id);
        var second = manager.RunAsync(box);
        await Task.Delay(200);
        s.Runner.HoldStarts.SetResult();

        var busy = await Assert.ThrowsExactlyAsync<BoxBusyException>(() => second);
        Assert.AreEqual(BoxBusyReason.Running, busy.Reason);
        Assert.AreEqual(1, s.Runner.StartedWith.Count);
        s.Runner.Finish(box.Config.Id);
        await first;
    }

    [TestMethod]
    public async Task RegisteringARunningBoxIsRefused()
    {
        using var s = new Setup();
        var box = s.Box();
        s.Runner.Running.Add(box.Config.Id);

        await Assert.ThrowsExactlyAsync<BoxBusyException>(() => s.Manager().RegisterAsync(box));
        Assert.AreEqual(0, s.Packages.Registrations);
    }

    [TestMethod]
    public async Task RemoveUnregistersClearsTheHostAndDeletesTheFolder()
    {
        using var s = new Setup();
        var box = s.Box();
        var trace = new HostReference(HostReferenceKind.Trace, HostLocationKind.RegistryValue, @"HKCU\Software\X", "v", box.Paths.App);
        s.Guard.Found.Add(trace);

        var outcome = await s.Manager().RemoveAsync(box);

        CollectionAssert.Contains(s.Packages.Unregistered, box.Config.Package);
        CollectionAssert.AreEqual(new[] { trace }, s.Guard.Removed);
        StringAssert.StartsWith(s.Guard.BackupDirectory, Path.Combine(s.T.Paths.Logs, "guard"));
        Assert.IsFalse(Directory.Exists(box.Paths.Root));
        Assert.IsFalse(outcome.FolderLeftOver);
        Assert.AreEqual(0, s.T.Store.Load().Count);
    }

    [TestMethod]
    public async Task RemoveQuitsARunningBoxFirst()
    {
        using var s = new Setup();
        var box = s.Box();
        s.Runner.Running.Add(box.Config.Id);
        s.Runner.StopsWhenAsked = true;

        await s.Manager(stopWait: TimeSpan.FromSeconds(5)).RemoveAsync(box);

        CollectionAssert.Contains(s.Runner.StopRequests, box.Config.Id);
        Assert.IsFalse(Directory.Exists(box.Paths.Root));
    }

    [TestMethod]
    public async Task ABoxThatDoesNotStopIsNotRemoved()
    {
        using var s = new Setup();
        var box = s.Box();
        s.Runner.Running.Add(box.Config.Id);

        var busy = await Assert.ThrowsExactlyAsync<BoxBusyException>(() => s.Manager().RemoveAsync(box));

        Assert.AreEqual(BoxBusyReason.Running, busy.Reason);
        Assert.IsTrue(Directory.Exists(box.Paths.Root));
        Assert.AreEqual(0, s.Packages.Unregistered.Count);
    }

    [TestMethod]
    public async Task ABoxLockedByAnotherProcessIsBusy()
    {
        using var s = new Setup();
        var box = s.Box();
        using var other = await BoxLock.AcquireAsync(s.T.Paths, box.Config.Id, Short);

        var busy = await Assert.ThrowsExactlyAsync<BoxBusyException>(() => s.Manager().RemoveAsync(box));

        Assert.AreEqual(BoxBusyReason.Locked, busy.Reason);
        Assert.IsTrue(Directory.Exists(box.Paths.Root));
    }

    [TestMethod]
    public async Task TheLockIsFreeAgainOnceReleased()
    {
        using var t = new TestBox();
        var id = Guid.NewGuid();
        (await BoxLock.AcquireAsync(t.Paths, id, Short)).Dispose();

        using var again = await BoxLock.AcquireAsync(t.Paths, id, Short);
        Assert.IsNotNull(again);
    }

    internal sealed class FakePackages : IBoxPackageManager
    {
        private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);

        public string? Unavailable { get; set; }
        public Exception? RegisterFailure { get; set; }
        public int Registrations { get; private set; }
        public List<BoxPackage> Unregistered { get; } = [];

        public BoxPackage Add(Guid id)
        {
            var package = new BoxPackage(BoxPackageManifest.NameFor(id), BoxPackageManifest.NameFor(id) + "_test");
            _registered.Add(package.FamilyName);
            return package;
        }

        public string? GetUnavailableReason() => Unavailable;

        public Task<BoxPackage> RegisterAsync(LoadedBox box, CancellationToken cancellationToken = default)
        {
            Registrations++;
            if (RegisterFailure is not null)
                throw RegisterFailure;
            return Task.FromResult(Add(box.Config.Id));
        }

        public Task UnregisterAsync(BoxPackage package, CancellationToken cancellationToken = default)
        {
            Unregistered.Add(package);
            _registered.Remove(package.FamilyName);
            return Task.CompletedTask;
        }

        public bool IsRegistered(BoxPackage package) => _registered.Contains(package.FamilyName);
        public IReadOnlySet<string> RegisteredBoxFamilies() => _registered;
        public void RefreshHost(LoadedBox box) { }
        public uint Activate(BoxPackage package) => throw new NotSupportedException();
    }

    internal sealed class FakeRunner : IBoxRunner
    {
        private readonly Dictionary<Guid, TaskCompletionSource> _runs = [];
        private readonly Dictionary<Guid, TaskCompletionSource> _started = [];
        private readonly Lock _lock = new();

        public HashSet<Guid> Running { get; } = [];
        public List<LoadedBox> StartedWith { get; } = [];
        public List<Guid> StopRequests { get; } = [];

        /// <summary>Starts wait for this before the box counts as running, as a real BoxHost takes a moment to listen.</summary>
        public TaskCompletionSource? HoldStarts { get; set; }

        public bool StopsWhenAsked { get; set; }

        public async Task<IBoxRun> StartAsync(LoadedBox box, LaunchSpec spec, CancellationToken cancellationToken = default)
        {
            var id = box.Config.Id;
            lock (_lock)
            {
                StartedWith.Add(box);
                _runs[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                StartedSignal(id).TrySetResult();
            }
            if (HoldStarts is { } hold)
                await hold.Task;
            lock (_lock)
                Running.Add(id);
            return new Run(this, id);
        }

        public Task Started(Guid id)
        {
            lock (_lock)
                return StartedSignal(id).Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private TaskCompletionSource StartedSignal(Guid id)
        {
            if (!_started.TryGetValue(id, out var signal))
                _started[id] = signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return signal;
        }

        public void Finish(Guid id)
        {
            TaskCompletionSource? run;
            lock (_lock)
            {
                Running.Remove(id);
                _runs.TryGetValue(id, out run);
            }
            run?.TrySetResult();
        }

        public bool RequestStop(Guid boxId)
        {
            lock (_lock)
            {
                if (!Running.Contains(boxId))
                    return false;
                StopRequests.Add(boxId);
            }
            if (StopsWhenAsked)
                Finish(boxId);
            return true;
        }

        public bool RequestShow(LoadedBox box) => IsRunning(box);

        public bool IsRunning(LoadedBox box)
        {
            lock (_lock)
                return Running.Contains(box.Config.Id);
        }

        public BoxRunRecord? ReadRun(LoadedBox box) => null;

        private sealed class Run(FakeRunner runner, Guid id) : IBoxRun
        {
            public async Task<BoxRunRecord> WaitAsync(TimeSpan? limit = null, Action<BoxRunRecord>? onUpdate = null, CancellationToken cancellationToken = default)
            {
                Task done;
                lock (runner._lock)
                    done = runner._runs[id].Task;
                await done.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return new BoxRunRecord { State = BoxRunState.Exited, HostProcessId = 1, StartedAt = DateTimeOffset.Now, ExitCode = 0 };
            }

            public void Dispose() { }
        }
    }

    internal sealed class FakeGuard : IHostGuard
    {
        public List<HostReference> Found { get; } = [];
        public List<HostReference> Removed { get; } = [];
        public string? BackupDirectory { get; private set; }

        public IReadOnlyList<HostReference> Scan(string root, Func<string, bool>? includePackage = null) => [.. Found];

        public GuardCleanup Remove(IReadOnlyCollection<HostReference> references, string root, string backupDirectory)
        {
            Removed.AddRange(references);
            BackupDirectory = backupDirectory;
            return new GuardCleanup(references.Count, [], backupDirectory);
        }
    }

    internal sealed class NoInspector : IFileInspector
    {
        public ExecutableInfo? ReadExecutableInfo(string path) => null;
        public SignatureInfo ReadSignature(string path) => new(false, null);
    }

    internal sealed class NoDownloader : IDownloader
    {
        public Task<string> DownloadAsync(Uri url, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
