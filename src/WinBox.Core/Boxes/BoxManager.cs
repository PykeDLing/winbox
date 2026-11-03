using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Boxes;

/// <summary>How a removal went. The box is out of the library either way.</summary>
/// <param name="Cleanup">Windows' records of the box that were cleared, if there were any.</param>
/// <param name="FolderLeftOver">Some of the box's files were still in use; they go the next time WinBox starts.</param>
public sealed record RemoveOutcome(GuardCleanup? Cleanup, bool FolderLeftOver)
{
    /// <summary>Records of the box that could not be cleared.</summary>
    public IReadOnlyList<string> Failures => Cleanup?.Failures ?? [];
}

/// <summary>A new box. <see cref="RegistrationError"/> says why its package is not registered yet; opening it tries again.</summary>
public sealed record ImportedBox(LoadedBox Box, Exception? RegistrationError);

public sealed record RunOptions
{
    /// <summary>Changes the planned launch (the CLI's diagnostics).</summary>
    public Func<LaunchSpec, LaunchSpec>? Customize { get; init; }

    /// <summary>Asks the app to quit once this much time has passed.</summary>
    public TimeSpan? Limit { get; init; }

    /// <summary>Called when the box's package had to be registered first, with the box as it is now.</summary>
    public Action<LoadedBox>? Registered { get; init; }

    /// <summary>Asked right before the box starts: false calls the start off (it was stopped while its package was registered).</summary>
    public Func<bool>? StillWanted { get; init; }

    /// <summary>Each new state of the run (run.json).</summary>
    public Action<BoxRunRecord>? Update { get; init; }
}

/// <summary>
/// What WinBox does to boxes, for the app and the CLI alike: import, register, run, remove, and clear the host of
/// what points into the boxes. Starting, registering and removing one box take its <see cref="BoxLock"/>, so two
/// processes never do them at once.
/// </summary>
public sealed class BoxManager(
    AppPaths paths,
    BoxStore store,
    ImportPipeline import,
    IBoxPackageManager packages,
    IBoxRunner runner,
    IHostGuard guard,
    LaunchPlanner planner,
    TimeSpan? stopWait = null,
    TimeSpan? lockWait = null)
{
    /// <summary>BoxHost's own worst case for a stop: the quit and save graces, then killing the sandbox and MXC tearing it down.</summary>
    public static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(75);

    /// <summary>How long to wait for another process that is starting, registering or removing the same box.</summary>
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _stopWait = stopWait ?? DefaultStopWait;
    private readonly TimeSpan _lockWait = lockWait ?? DefaultLockWait;

    public IBoxRunner Runner => runner;

    /// <summary>
    /// Imports an installer and registers the new box's package. A box whose package cannot be registered (Developer
    /// Mode off, say) is kept all the same: it is in the library, and opening it tries again, so trying the import
    /// again never makes a second box.
    /// </summary>
    /// <param name="confirm">Asked when WinBox expects the app not to run in a box (see <see cref="ImportConfirmation"/>).</param>
    /// <param name="register">False leaves the package to the first run (the CLI's import --no-package).</param>
    public async Task<ImportedBox> ImportAsync(
        string file,
        IProgress<ImportProgress>? progress = null,
        ImportConfirmation? confirm = null,
        bool register = true,
        CancellationToken cancellationToken = default)
    {
        var box = await import.ImportAsync(file, progress, confirm, cancellationToken).ConfigureAwait(false);
        if (!register)
            return new ImportedBox(box, null);
        if (packages.GetUnavailableReason() is { } reason)
            return new ImportedBox(box, new InvalidOperationException(reason));
        progress?.Report(new ImportProgress(ImportStage.Registering));
        try
        {
            // The box exists now: a cancel no longer undoes the import, and registering is quick.
            return new ImportedBox(await RegisterAsync(box, CancellationToken.None).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is not BoxBusyException)
        {
            return new ImportedBox(box, e);
        }
    }

    /// <summary>(Re)registers the box's package and records it in box.json. Refused while the box runs: re-registering ends its BoxHost.</summary>
    public async Task<LoadedBox> RegisterAsync(LoadedBox box, CancellationToken cancellationToken = default)
    {
        using (await BoxLock.AcquireAsync(paths, box.Config.Id, _lockWait, cancellationToken).ConfigureAwait(false))
        {
            if (runner.IsRunning(box))
                throw new BoxBusyException(box.Config.Id, BoxBusyReason.Running);
            return await RegisterLockedAsync(box, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<LoadedBox> RegisterLockedAsync(LoadedBox box, CancellationToken cancellationToken)
    {
        var package = await packages.RegisterAsync(box, cancellationToken).ConfigureAwait(false);
        var updated = box with { Config = box.Config with { Package = package } };
        store.Save(updated);
        return updated;
    }

    /// <summary>
    /// Runs the box to the end: registers its package first if it has none, starts it, and follows it until its
    /// sandbox has closed. Throws <see cref="BoxBusyException"/> when the box runs already, wherever it was started.
    /// Null when <see cref="RunOptions.StillWanted"/> called the start off.
    /// </summary>
    public async Task<BoxRunRecord?> RunAsync(LoadedBox box, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new RunOptions();
        IBoxRun run;
        // Held until BoxHost is listening: from then on the box counts as running for every other process.
        using (await BoxLock.AcquireAsync(paths, box.Config.Id, _lockWait, cancellationToken).ConfigureAwait(false))
        {
            if (runner.IsRunning(box))
                throw new BoxBusyException(box.Config.Id, BoxBusyReason.Running);
            if (box.Config.Package is not { } package || !packages.IsRegistered(package))
            {
                box = await RegisterLockedAsync(box, cancellationToken).ConfigureAwait(false);
                options.Registered?.Invoke(box);
            }
            if (options.StillWanted?.Invoke() == false)
                return null;
            var spec = planner.Plan(box, DateTimeOffset.Now);
            run = await runner.StartAsync(box, options.Customize?.Invoke(spec) ?? spec, cancellationToken).ConfigureAwait(false);
        }
        using (run)
            return await run.WaitAsync(options.Limit, options.Update, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a box: quits it if it runs, unregisters its package (Windows deletes the package's private storage
    /// with it), clears Windows' records of it and deletes its folder. Throws <see cref="BoxBusyException"/> when the
    /// box did not stop in time; nothing is removed then.
    /// </summary>
    public async Task<RemoveOutcome> RemoveAsync(LoadedBox box, CancellationToken cancellationToken = default)
    {
        var id = box.Config.Id;
        using (await BoxLock.AcquireAsync(paths, id, _lockWait, cancellationToken).ConfigureAwait(false))
        {
            if (runner.RequestStop(id))
            {
                var deadline = DateTimeOffset.UtcNow + _stopWait;
                while (runner.IsRunning(box) && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            // Unregistering or deleting under a live sandbox would pull the app's files out from under it.
            if (runner.IsRunning(box))
                throw new BoxBusyException(id, BoxBusyReason.Running);
            if (box.Config.Package is { } package)
                await packages.UnregisterAsync(package, cancellationToken).ConfigureAwait(false);
            // Windows' records of the box: those naming its files, and those it files under the package's name and
            // keeps after the package is gone. Cleared before the folder goes, so a folder that cannot be deleted
            // right away does not keep them around too.
            var family = box.Config.Package?.FamilyName;
            var references = guard.Scan(box.Paths.Root, f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
            var cleanup = references.Count > 0 ? guard.Remove(references, box.Paths.Root, GuardBackup($"remove-{id:N}")) : null;
            var deleted = store.Delete(box);
            return new RemoveOutcome(cleanup, FolderLeftOver: !deleted);
        }
    }

    /// <summary>Host settings that point into the boxes and would start a boxed program outside its sandbox.</summary>
    public IReadOnlyList<HostReference> FindLeaks() =>
        [.. guard.Scan(paths.Boxes).Where(r => r.Kind == HostReferenceKind.Leak)];

    /// <summary>
    /// Windows' records of boxes that are gone: those filed under the name of a box package that is no longer
    /// registered (Windows writes some of them only after the package is removed, so a removal cannot catch them
    /// all), and those naming a box folder that no longer exists. Packages still registered count as in use whoever
    /// registered them, so another WinBox's boxes are never taken for leftovers.
    /// </summary>
    public IReadOnlyList<HostReference> FindLeftovers()
    {
        var inUse = store.Load().OfType<LoadedBox>()
            .Select(b => b.Config.Package?.FamilyName).OfType<string>()
            .Concat(packages.RegisteredBoxFamilies())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. guard.Scan(paths.Boxes, family => !inUse.Contains(family))
            .Where(r => r.Kind == HostReferenceKind.Trace &&
                (r.Package is not null ||
                 (HostReferenceRules.BoxFolderIn($"{r.Name}\0{r.Detail}", paths.Boxes) is { } folder &&
                  !Directory.Exists(Path.Combine(paths.Boxes, folder)))))];
    }

    /// <summary>Removes <paramref name="references"/> after backing them up in Logs\guard\&lt;time&gt;-&lt;reason&gt;.</summary>
    public GuardCleanup Clear(IReadOnlyCollection<HostReference> references, string reason) =>
        guard.Remove(references, paths.Boxes, GuardBackup(reason));

    private string GuardBackup(string reason) =>
        Path.Combine(paths.Logs, "guard", $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{reason}");
}
