using System.Text.Json;
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

/// <summary>Owns the Boxes directory: listing, importing through a staging directory, and deleting.</summary>
public sealed class BoxStore
{
    private readonly AppPaths _paths;

    public BoxStore(AppPaths paths)
    {
        _paths = paths;
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
