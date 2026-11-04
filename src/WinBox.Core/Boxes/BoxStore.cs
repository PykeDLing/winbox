using System.Text.Json;
using WinBox.Core.Import;
using WinBox.Core.IO;

namespace WinBox.Core.Boxes;

/// <summary>A box directory on disk: either readable, or broken and left untouched.</summary>
public abstract record BoxEntry(BoxPaths Paths);

public sealed record LoadedBox(BoxPaths Paths, BoxConfig Config) : BoxEntry(Paths);

/// <summary>A box whose box.json could not be read. It is never overwritten or deleted automatically.</summary>
public sealed record BrokenBox(BoxPaths Paths, BrokenReason Reason, string Message) : BoxEntry(Paths);

public enum BrokenReason
{
    MissingConfig,
    /// <summary>Corrupt, or written by a newer WinBox with an incompatible schema.</summary>
    UnreadableConfig,
    Other,
}

/// <summary>What <see cref="BoxStore.FinishInterruptedUpdate"/> found and did.</summary>
public enum UpdateRecovery
{
    /// <summary>No update was cut short.</summary>
    None,

    /// <summary>The swap had not finished: the app as it was is back, and box.json never changed.</summary>
    RolledBack,

    /// <summary>The new app was in place: box.json now describes it.</summary>
    Finished,

    /// <summary>Only the replaced app was left over, and it was deleted (or still waits to be).</summary>
    CleanedUp,

    /// <summary>Going back to the backup had begun: the swap was finished, and box.json describes the app it brought back.</summary>
    WentBack,
}

/// <summary>Owns the Boxes directory: listing, importing through a staging directory, and deleting.</summary>
public sealed class BoxStore
{
    /// <summary>The room a backup's copy of a box's data must leave on its drive; below it, an update keeps no backup.</summary>
    public const long BackupHeadroom = 1L << 30;

    private readonly AppPaths _paths;
    private readonly Func<string, long> _freeSpace;
    private readonly Func<string, string, bool>? _link;

    /// <param name="freeSpace">The bytes free on the drive of the folder it is given; what the drive says by default.</param>
    /// <param name="link">
    /// Makes its first argument a hard link to the existing file named by its second, false if it cannot (as for
    /// <see cref="Sandbox.StateFolder"/>). Without it, backups copy what they could have linked.
    /// </param>
    public BoxStore(AppPaths paths, Func<string, long>? freeSpace = null, Func<string, string, bool>? link = null)
    {
        _paths = paths;
        _freeSpace = freeSpace ?? (dir => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace);
        _link = link;
        _paths.EnsureCreated();
    }

    public IReadOnlyList<BoxEntry> Load()
    {
        var entries = new List<BoxEntry>();
        foreach (var dir in Directory.EnumerateDirectories(_paths.Boxes, "*" + BoxPaths.Extension))
        {
            var paths = new BoxPaths(dir);
            entries.Add(LoadOne(paths));
        }
        return entries
            .OrderBy(e => e is LoadedBox loaded ? loaded.Config.CreatedAt : DateTimeOffset.MaxValue)
            .ToList();
    }

    private static BoxEntry LoadOne(BoxPaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
            return new BrokenBox(paths, BrokenReason.MissingConfig, "box.json is missing.");
        try
        {
            var config = JsonFile.Read(paths.ConfigFile, WinBoxJson.Files.BoxConfig);
            if (config.SchemaVersion > BoxConfig.CurrentSchemaVersion)
                return new BrokenBox(paths, BrokenReason.UnreadableConfig,
                    $"box.json schema {config.SchemaVersion} is newer than this WinBox understands.");
            return new LoadedBox(paths, config);
        }
        catch (JsonException e)
        {
            return new BrokenBox(paths, BrokenReason.UnreadableConfig, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new BrokenBox(paths, BrokenReason.Other, e.Message);
        }
    }

    public void Save(LoadedBox box) => JsonFile.Write(box.Paths.ConfigFile, box.Config, WinBoxJson.Files.BoxConfig);

    /// <summary>The box as its box.json says now; null when it is gone or cannot be read.</summary>
    public LoadedBox? Reload(BoxPaths paths) => Directory.Exists(paths.Root) ? LoadOne(paths) as LoadedBox : null;

    /// <summary>
    /// Puts <paramref name="update"/>'s app in the box's place and <paramref name="config"/> in its box.json, leaving
    /// the rest of the box as it is, and keeps the box as it was in Backup\ to go back to (see <see cref="BoxBackup"/>).
    /// The backup is made in Backup.new first, all but the app (see <see cref="MakeBackup"/>). Then the new app comes
    /// in as App.new, App goes to App.old, App.new becomes App; the new box.json waits in .unfinished until then, so
    /// <see cref="FinishInterruptedUpdate"/> can tell how far a crash got and either finish the update or put the old
    /// app back. Finishing moves App.old into Backup.new and puts Backup.new in place of the backup before. Callers
    /// hold the box's lock and make sure it does not run.
    /// </summary>
    /// <returns>The box with its new app, and why no backup was kept (null when one was).</returns>
    public (LoadedBox Box, BackupSkip? BackupSkipped) ReplaceApp(LoadedBox box, PreparedImport update, BoxConfig config,
        IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var paths = box.Paths;
        FinishInterruptedUpdate(paths);
        if (Directory.Exists(paths.AppPrevious))
            throw new IOException($"The app an earlier update replaced is still in use: {paths.AppPrevious}");
        progress?.Report(new ImportProgress(ImportStage.BackingUp));
        var skipped = MakeBackup(box, config.UpdatedAt ?? DateTimeOffset.Now, cancellationToken);
        progress?.Report(new ImportProgress(ImportStage.Updating));
        Retry(() => Directory.Move(update.Paths.App, paths.AppStaging));
        JsonFile.Write(paths.UnfinishedMarker, config, WinBoxJson.Files.BoxConfig);
        try
        {
            // A virus scanner looking at the files just unpacked can hold them for a moment.
            Retry(() => Directory.Move(paths.App, paths.AppPrevious));
            Retry(() => Directory.Move(paths.AppStaging, paths.App));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FinishInterruptedUpdate(paths);
            throw;
        }
        FinishInterruptedUpdate(paths);
        ReplaceSource(paths.Source, update.Paths.Source);
        return (new LoadedBox(paths, config), skipped);
    }

    /// <summary>
    /// Makes Backup.new of the box as it is: a copy of its data, its saved state and its installer, and its box.json
    /// then, written last to say the rest is complete. Its app joins it once the update has swapped the app out. The
    /// backup before goes first when the drive has no room for both. When there is no room even so, or the copy
    /// fails, no backup is kept at all: the one before would no longer be the box as it was before this update.
    /// </summary>
    private BackupSkip? MakeBackup(LoadedBox box, DateTimeOffset madeAt, CancellationToken cancellationToken)
    {
        var paths = box.Paths;
        TryDeleteTree(paths.BackupStaging);
        var data = BackupFiles.Measure(paths.Data);
        if (_freeSpace(paths.Root) - data < BackupHeadroom)
        {
            TryDeleteBackup(paths);
            if (_freeSpace(paths.Root) - data < BackupHeadroom)
                return BackupSkip.NoSpace;
        }
        try
        {
            BackupFiles.Copy(paths.Data, Path.Combine(paths.BackupStaging, "Data"), link: null, cancellationToken);
            // Saved state and installers are never written to in place, so links to them are as good as copies.
            foreach (var item in BoxPaths.BackedUpItems.Where(i => i is not ("App" or "Data")))
                BackupFiles.Copy(Path.Combine(paths.Root, item), Path.Combine(paths.BackupStaging, item), _link, cancellationToken);
            var backup = new BoxBackup { Config = box.Config, MadeAt = madeAt, Bytes = data + BackupFiles.Measure(paths.App) };
            JsonFile.Write(BackupFile(paths.BackupStaging), backup, WinBoxJson.Files.BoxBackup);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryDeleteTree(paths.BackupStaging);
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteTree(paths.BackupStaging);
            TryDeleteBackup(paths);
            return BackupSkip.Failed;
        }
    }

    /// <summary>
    /// Settles an update a crash cut short, by where it got to: while App.new is there the swap never finished and the
    /// app as it was comes back; once App.new has become App, box.json is brought in line with it. The replaced app
    /// joins the backup the update made, which then takes the place of the backup before; without one it is deleted,
    /// or left for the next time while something holds it. Going back to the backup is always finished
    /// (<see cref="GoBack"/>).
    /// </summary>
    public static UpdateRecovery FinishInterruptedUpdate(BoxPaths paths)
    {
        if (File.Exists(paths.GoingBackMarker))
        {
            FinishGoingBack(paths);
            return UpdateRecovery.WentBack;
        }
        // Made just before the marker: nothing has moved into it yet.
        TryDeleteTree(paths.BackupSwap);
        if (Directory.Exists(paths.AppStaging))
        {
            if (!Directory.Exists(paths.App) && Directory.Exists(paths.AppPrevious))
                Directory.Move(paths.AppPrevious, paths.App);
            DeleteTree(paths.AppStaging);
            File.Delete(paths.UnfinishedMarker);
            TryDeleteTree(paths.AppPrevious);
            // The backup of an update that did not happen.
            TryDeleteTree(paths.BackupStaging);
            return UpdateRecovery.RolledBack;
        }
        var recovery = UpdateRecovery.None;
        if (File.Exists(paths.UnfinishedMarker))
        {
            if (Directory.Exists(paths.App))
            {
                JsonFile.Write(paths.ConfigFile, JsonFile.Read(paths.UnfinishedMarker, WinBoxJson.Files.BoxConfig), WinBoxJson.Files.BoxConfig);
                recovery = UpdateRecovery.Finished;
            }
            File.Delete(paths.UnfinishedMarker);
        }
        var staged = Path.Combine(paths.BackupStaging, "App");
        if (Directory.Exists(paths.AppPrevious))
        {
            if (!Directory.Exists(paths.App))
            {
                Directory.Move(paths.AppPrevious, paths.App);
                TryDeleteTree(paths.BackupStaging);
                return UpdateRecovery.RolledBack;
            }
            if (File.Exists(BackupFile(paths.BackupStaging)) && !Directory.Exists(staged))
                TryMove(paths.AppPrevious, staged);
            else
                TryDeleteTree(paths.AppPrevious);
            if (recovery == UpdateRecovery.None)
                recovery = UpdateRecovery.CleanedUp;
        }
        // The replaced app may still be on its way into the backup: then the backup waits for it.
        if (Directory.Exists(paths.BackupStaging) && !Directory.Exists(paths.AppPrevious))
        {
            if (File.Exists(BackupFile(paths.BackupStaging)) && Directory.Exists(staged))
            {
                // Left for the next time while the backup before is held.
                if (TryDeleteBackup(paths))
                    TryMove(paths.BackupStaging, paths.Backup);
            }
            else
                TryDeleteTree(paths.BackupStaging);
        }
        return recovery;
    }

    /// <summary>Boxes an update was cut short in (see <see cref="FinishInterruptedUpdate"/>), with their ids.</summary>
    public IReadOnlyList<(Guid Id, BoxPaths Paths)> InterruptedUpdates() =>
        [.. Directory.EnumerateDirectories(_paths.Boxes, "*" + BoxPaths.Extension)
            .Select(dir => (Dir: dir, Paths: new BoxPaths(dir)))
            .Where(b => Directory.Exists(b.Paths.AppStaging) || Directory.Exists(b.Paths.AppPrevious) || File.Exists(b.Paths.UnfinishedMarker) ||
                Directory.Exists(b.Paths.BackupStaging) || Directory.Exists(b.Paths.BackupSwap) || File.Exists(b.Paths.GoingBackMarker))
            .Select(b => (Ok: Guid.TryParse(Path.GetFileNameWithoutExtension(b.Dir), out var id), Id: id, b.Paths))
            .Where(b => b.Ok)
            .Select(b => (b.Id, b.Paths))];

    /// <summary>
    /// Swaps the box with its backup (<see cref="BoxBackup"/>): the backup's app, data, saved state and installer come
    /// back, and the box's go into the backup, so going back once more returns to where the box was. box.json takes
    /// what describes the app from the backup's and keeps what describes the box (<see cref="BoxUpdate.Apply"/>). It
    /// waits in .going-back while the parts move one by one through Backup.swap, so <see cref="FinishInterruptedUpdate"/>
    /// can finish a swap a crash cut short. Only renames: nothing is copied. Callers hold the box's lock and make sure
    /// it does not run.
    /// </summary>
    public LoadedBox GoBack(LoadedBox box, DateTimeOffset now)
    {
        var paths = box.Paths;
        FinishInterruptedUpdate(paths);
        var backup = ReadBackup(paths) ?? throw new InvalidOperationException($"Box {box.Config.Id:N} has no backup to go back to.");
        // Every part is in two of the three places throughout, which is how a swap cut short is told apart.
        foreach (var item in BoxPaths.BackedUpItems)
            Directory.CreateDirectory(Path.Combine(paths.Root, item));
        var config = BoxUpdate.Apply(box.Config, backup.Config, now);
        var kept = new BoxBackup { Config = box.Config, MadeAt = now, Bytes = BackupFiles.Measure(paths.Data) + BackupFiles.Measure(paths.App) };
        Directory.CreateDirectory(paths.BackupSwap);
        JsonFile.Write(BackupFile(paths.BackupSwap), kept, WinBoxJson.Files.BoxBackup);
        JsonFile.Write(paths.GoingBackMarker, config, WinBoxJson.Files.BoxConfig);
        FinishGoingBack(paths);
        return new LoadedBox(paths, config);
    }

    /// <summary>
    /// Moves each part of the box into Backup.swap and the backup's in its place, picking up where a crash left off,
    /// then writes box.json and makes Backup.swap the backup. Without Backup.swap, that last step was done already.
    /// </summary>
    private static void FinishGoingBack(BoxPaths paths)
    {
        if (Directory.Exists(paths.BackupSwap))
        {
            foreach (var item in BoxPaths.BackedUpItems)
            {
                var (box, backup, swap) = (Path.Combine(paths.Root, item), Path.Combine(paths.Backup, item), Path.Combine(paths.BackupSwap, item));
                if (Directory.Exists(box) && Directory.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(swap)!);
                    Retry(() => Directory.Move(box, swap));
                }
                if (!Directory.Exists(box) && Directory.Exists(backup))
                    Retry(() => Directory.Move(backup, box));
                if (!Directory.Exists(box) || !Directory.Exists(swap))
                    throw new IOException($"Going back left {item} of {paths.Root} neither in the box nor in its backup.");
            }
        }
        JsonFile.Write(paths.ConfigFile, JsonFile.Read(paths.GoingBackMarker, WinBoxJson.Files.BoxConfig), WinBoxJson.Files.BoxConfig);
        if (Directory.Exists(paths.BackupSwap))
        {
            // All that is left of the backup is its old backup.json and empty folders.
            DeleteBackup(paths);
            Retry(() => Directory.Move(paths.BackupSwap, paths.Backup));
        }
        File.Delete(paths.GoingBackMarker);
    }

    /// <summary>The box's backup, or null when it has none that is whole.</summary>
    public static BoxBackup? ReadBackup(BoxPaths paths)
    {
        var file = BackupFile(paths.Backup);
        if (!File.Exists(file) || BoxPaths.BackedUpItems.Any(item => !Directory.Exists(Path.Combine(paths.Backup, item))))
            return null;
        try
        {
            return JsonFile.Read(file, WinBoxJson.Files.BoxBackup);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the box's backup. Its backup.json goes first, so a delete cut short (a file held) never leaves something
    /// that looks like a whole backup. Callers hold the box's lock.
    /// </summary>
    public static void DeleteBackup(BoxPaths paths)
    {
        if (!Directory.Exists(paths.Backup))
            return;
        File.Delete(BackupFile(paths.Backup));
        DeleteTree(paths.Backup);
    }

    private static bool TryDeleteBackup(BoxPaths paths)
    {
        try
        {
            DeleteBackup(paths);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BackupFile(string backup) => Path.Combine(backup, "backup.json");

    /// <summary>The installer the box was last made or updated from, in place of the one before.</summary>
    private static void ReplaceSource(string source, string updated)
    {
        Directory.CreateDirectory(source);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(updated))
        {
            File.Move(file, Path.Combine(source, Path.GetFileName(file)), overwrite: true);
            kept.Add(Path.GetFileName(file));
        }
        foreach (var file in Directory.EnumerateFiles(source).Where(f => !kept.Contains(Path.GetFileName(f))))
            File.Delete(file);
    }

    private static void Retry(Action move)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            Retry(() => Directory.Move(from, to));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held; tried again the next time an update is settled.
        }
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                DeleteTree(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held; the next update or the next start of WinBox deletes it.
        }
    }

    /// <summary>
    /// Starts an import in Boxes\&lt;id&gt;.box.partial. Nothing becomes visible in the library until
    /// <see cref="ImportStaging.Commit"/> renames it into place; disposing without committing removes it.
    /// </summary>
    public ImportStaging BeginImport(Guid id)
    {
        var partial = BoxPaths.PartialRoot(_paths.Boxes, id);
        if (Directory.Exists(partial))
            Directory.Delete(partial, recursive: true);
        var staging = new BoxPaths(partial);
        staging.CreateSkeleton();
        return new ImportStaging(staging, BoxPaths.ForBox(_paths.Boxes, id));
    }

    /// <summary>Removes imports that were interrupted by a crash. Run once at startup.</summary>
    public int CleanUpInterruptedImports()
    {
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(_paths.Boxes, "*" + BoxPaths.PartialExtension))
        {
            Directory.Delete(dir, recursive: true);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Deletes a box. The directory is first renamed so a crash midway never leaves something that
    /// looks like a valid but half-empty box. Callers must make sure nothing in the box is running.
    /// </summary>
    /// <returns>False when the box is out of the library but some of its files could not be deleted yet
    /// (a file still open); <see cref="CleanUpInterruptedDeletes"/> finishes the job later.</returns>
    public bool Delete(BoxEntry box)
    {
        var doomed = box.Paths.Root + DeletingInfix + Guid.NewGuid().ToString("N");
        Directory.Move(box.Paths.Root, doomed);
        try
        {
            DeleteTree(doomed);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const string DeletingInfix = ".deleting-";

    /// <summary>
    /// Deletes a box's folder and everything in it. Directory.Delete refuses read-only folders, and apps leave those:
    /// Windows marks shell folders such as Start Menu\Programs read-only, and the box's Roaming AppData has them as
    /// soon as an app makes a Start menu shortcut. So a refused delete clears the read-only marks and tries again.
    /// Links (the box profile's links to the user's folders) are deleted themselves, never followed.
    /// </summary>
    internal static void DeleteTree(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            ClearReadOnly(new DirectoryInfo(dir));
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        // A link is deleted as it is; its attributes, and whatever it leads to, are left alone.
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;
        dir.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            if (entry is DirectoryInfo sub)
                ClearReadOnly(sub);
            else if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    /// <summary>Finishes deletes that a crash or a file still in use interrupted. Run at startup; what is still in use waits for the next time.</summary>
    public int CleanUpInterruptedDeletes()
    {
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(_paths.Boxes, "*" + BoxPaths.Extension + DeletingInfix + "*"))
        {
            try
            {
                DeleteTree(dir);
                count++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use.
            }
        }
        return count;
    }
}

public sealed class ImportStaging : IDisposable
{
    private readonly BoxPaths _final;
    private bool _committed;

    internal ImportStaging(BoxPaths staging, BoxPaths final)
    {
        Paths = staging;
        _final = final;
    }

    /// <summary>The partial box. Fill App\ and Source\ here before committing.</summary>
    public BoxPaths Paths { get; }

    public LoadedBox Commit(BoxConfig config)
    {
        JsonFile.Write(Paths.ConfigFile, config, WinBoxJson.Files.BoxConfig);
        Directory.Move(Paths.Root, _final.Root);
        _committed = true;
        return new LoadedBox(_final, config);
    }

    public void Dispose()
    {
        if (!_committed && Directory.Exists(Paths.Root))
            Directory.Delete(Paths.Root, recursive: true);
    }
}
