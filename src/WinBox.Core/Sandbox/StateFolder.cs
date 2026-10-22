namespace WinBox.Core.Sandbox;

/// <summary>
/// App state that WinBox carries from one sandboxed run to the next, such as the sandbox's LocalAppData,
/// which Windows discards after every run in MXC's BaseContainer tier.
/// </summary>
/// <remarks>
/// A save never overwrites the last good copy in place. It copies into next\, marks it complete, and swaps
/// it in with two renames, so an interruption at any point (power loss, the process being killed) leaves
/// either the old or the new copy, never a mix. <see cref="Recover"/> finishes or rolls back such a save.
/// <code>
/// &lt;root&gt;\current\        the copy restored on the next run
/// &lt;root&gt;\next\           a save in progress
/// &lt;root&gt;\next.complete   next\ was copied completely and may be swapped in
/// &lt;root&gt;\previous\       the copy being replaced, during the swap
/// </code>
/// </remarks>
public sealed class StateFolder(string root)
{
    /// <summary>
    /// Top-level folders of a sandbox's LocalAppData that are never carried over: temporary files and
    /// caches Windows or the app rebuild on their own.
    /// </summary>
    public static IReadOnlyList<string> DefaultExclusions { get; } = ["Temp", "INetCache", "INetHistory", "CrashDumps", "D3DSCache"];

    public string Root => root;
    public string Current => Path.Combine(root, "current");
    private string Next => Path.Combine(root, "next");
    private string NextComplete => Path.Combine(root, "next.complete");
    private string Previous => Path.Combine(root, "previous");

    /// <summary>Finishes or rolls back an interrupted save; returns the copy to restore, or null if there is none.</summary>
    public string? Recover()
    {
        if (!Directory.Exists(root))
            return null;
        if (Directory.Exists(Next))
        {
            if (File.Exists(NextComplete))
                SwapInNext();
            else
                Directory.Delete(Next, recursive: true);
        }
        if (!Directory.Exists(Current) && Directory.Exists(Previous))
            Directory.Move(Previous, Current);
        File.Delete(NextComplete);
        if (Directory.Exists(Previous))
            Directory.Delete(Previous, recursive: true);
        return Directory.Exists(Current) ? Current : null;
    }

    /// <summary>Copies the saved state into <paramref name="target"/>. Returns null when nothing was saved yet.</summary>
    public CopyStats? RestoreTo(string target)
    {
        if (Recover() is not { } current)
            return null;
        return CopyTree(current, target, excluded: [], fallback: null);
    }

    /// <summary>
    /// Saves <paramref name="source"/> as the new state. Files that cannot be read (still locked by a
    /// process that outlived the app) keep their previously saved version, and so do the files
    /// <paramref name="notRestored"/> lists: the last restore could not bring them in, so the app never saw
    /// them. Failing to write the new copy (a full disk) throws and leaves the last good copy in place.
    /// </summary>
    public CopyStats Save(string source, IReadOnlyCollection<string> excluded, IReadOnlyCollection<string>? notRestored = null)
    {
        var previous = Recover();
        Directory.CreateDirectory(root);
        var stats = CopyTree(source, Next, excluded, fallback: previous);
        if (previous is not null)
        {
            foreach (var relative in notRestored ?? [])
            {
                var target = Path.Combine(Next, relative);
                if (File.Exists(target) || File.Exists(Path.Combine(source, relative)))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (TryCopyFile(Path.Combine(previous, relative), target))
                    stats.KeptPrevious.Add(relative);
                else
                    stats.Skipped.Add(relative);
            }
        }
        using (File.Create(NextComplete, 0, FileOptions.WriteThrough)) { }
        SwapInNext();
        File.Delete(NextComplete);
        if (Directory.Exists(Previous))
            Directory.Delete(Previous, recursive: true);
        return stats;
    }

    private void SwapInNext()
    {
        if (Directory.Exists(Current))
        {
            if (Directory.Exists(Previous))
                Directory.Delete(Previous, recursive: true);
            Directory.Move(Current, Previous);
        }
        Directory.Move(Next, Current);
    }

    /// <summary>Opens a file of the copy being written. Tests replace it to simulate a full disk.</summary>
    internal Func<string, Stream> OpenDestination { get; init; } =
        path => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    private CopyStats CopyTree(string source, string destination, IReadOnlyCollection<string> excluded, string? fallback)
    {
        var stats = new CopyStats();
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source))
            return stats;

        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var relativeDir = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relativeDir));
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(source, entry.FullName);
                if (relativeDir == "." && excluded.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                // Junctions and symlinks are not followed: they could point anywhere on the host.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                    continue;
                }

                var target = Path.Combine(destination, relative);
                if (TryCopyFile(entry.FullName, target))
                {
                    stats.Files++;
                    stats.Bytes += ((FileInfo)entry).Length;
                }
                else if (fallback is not null && TryCopyFile(Path.Combine(fallback, relative), target))
                    stats.KeptPrevious.Add(relative);
                else
                    stats.Skipped.Add(relative);
            }
        }
        return stats;
    }

    /// <summary>
    /// Copies one file. Returns false only when the source cannot be opened or read (still locked by a
    /// process that outlived the app); a copy that fails halfway is removed rather than kept truncated.
    /// Anything failing on the destination side throws, so the copy is never marked complete.
    /// </summary>
    private bool TryCopyFile(string from, string to)
    {
        FileStream input;
        try
        {
            if (!File.Exists(from))
                return false;
            input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using (input)
        {
            var modified = File.GetLastWriteTimeUtc(input.SafeFileHandle);
            using (var output = OpenDestination(to))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    int read;
                    try
                    {
                        read = input.Read(buffer);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        output.Dispose();
                        File.Delete(to);
                        return false;
                    }
                    if (read == 0)
                        break;
                    output.Write(buffer, 0, read);
                }
                // The swap is a rename; without this a power cut right after it could keep the rename but
                // lose the data still sitting in the cache.
                if (output is FileStream file)
                    file.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(to, modified);
            return true;
        }
    }
}

public sealed class CopyStats
{
    public int Files { get; set; }
    public long Bytes { get; set; }

    /// <summary>Files that could not be read and kept the version from the previous save.</summary>
    public List<string> KeptPrevious { get; } = [];

    /// <summary>Files that could not be read and had no previous version.</summary>
    public List<string> Skipped { get; } = [];

    public override string ToString() =>
        $"{Files} files, {Bytes / 1024} KB" +
        (KeptPrevious.Count > 0 ? $", {KeptPrevious.Count} kept from the previous save" : "") +
        (Skipped.Count > 0 ? $", {Skipped.Count} skipped" : "");
}
