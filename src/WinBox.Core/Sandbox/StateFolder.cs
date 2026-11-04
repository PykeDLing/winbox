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
/// <param name="link">
/// Makes its first argument a hard link to the existing file named by its second, false if it cannot (another
/// volume, a file system without hard links). A save links each file that did not change since the last save
/// to that save's copy instead of writing it again, so unchanged state costs neither the writes nor the flushes.
/// The saved copies are never written to in place, which is what makes sharing their data safe.
/// </param>
public sealed class StateFolder(string root, Func<string, string, bool>? link = null)
{
    /// <summary>
    /// Top-level folders of a sandbox's LocalAppData that are never carried over: temporary files and
    /// caches Windows or the app rebuild on their own.
    /// </summary>
    public static IReadOnlyList<string> DefaultExclusions { get; } = ["Temp", "INetCache", "INetHistory", "CrashDumps", "D3DSCache"];

    /// <summary>
    /// Folders a save leaves out wherever they are: the caches Chromium and the apps built on it (Electron, CEF)
    /// rebuild on their own, which can run to gigabytes. A folder named just "Cache" is only taken for Chromium's
    /// when it holds Chromium's Cache_Data.
    /// </summary>
    public static IReadOnlyList<string> RebuiltCaches { get; } =
        ["Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GraphiteDawnCache", "GrShaderCache", "ShaderCache"];

    internal static bool IsRebuiltCache(DirectoryInfo directory) =>
        RebuiltCaches.Contains(directory.Name, StringComparer.OrdinalIgnoreCase) ||
        (directory.Name.Equals("Cache", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(directory.FullName, "Cache_Data")));

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
    /// <remarks>
    /// Nothing is flushed to disk: the sandbox's LocalAppData is thrown away after the run anyway, and a restore
    /// that a power cut interrupts is simply done again next time.
    /// </remarks>
    public CopyStats? RestoreTo(string target)
    {
        if (Recover() is not { } current)
            return null;
        return CopyTree(current, target, excluded: [], fallback: null, save: false);
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
        var stats = CopyTree(source, Next, excluded, fallback: previous, save: true);
        if (previous is not null)
        {
            foreach (var relative in notRestored ?? [])
            {
                var target = Path.Combine(Next, relative);
                if (File.Exists(target) || File.Exists(Path.Combine(source, relative)))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (TryCopyFile(Path.Combine(previous, relative), target, flush: true))
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

    /// <param name="fallback">The last saved copy: where unreadable files are taken from, and what unchanged files are linked to.</param>
    /// <param name="save">Writing a save (flushed, caches left out) rather than a restore into the sandbox.</param>
    private CopyStats CopyTree(string source, string destination, IReadOnlyCollection<string> excluded, string? fallback, bool save)
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
                if (entry is DirectoryInfo directory)
                {
                    if (save && IsRebuiltCache(directory))
                        continue;
                    pending.Push(entry.FullName);
                    continue;
                }

                var target = Path.Combine(destination, relative);
                if (save && fallback is not null && TryLinkUnchanged((FileInfo)entry, Path.Combine(fallback, relative), target))
                {
                    stats.Files++;
                    stats.Linked++;
                    stats.Bytes += ((FileInfo)entry).Length;
                }
                else if (TryCopyFile(entry.FullName, target, flush: save))
                {
                    stats.Files++;
                    stats.Bytes += ((FileInfo)entry).Length;
                }
                else if (fallback is not null && TryCopyFile(Path.Combine(fallback, relative), target, flush: save))
                    stats.KeptPrevious.Add(relative);
                else
                    stats.Skipped.Add(relative);
            }
        }
        return stats;
    }

    /// <summary>
    /// Links <paramref name="to"/> to the last saved copy of a file when the file still holds exactly those bytes.
    /// The bytes are compared rather than the time stamps: a file changed through a memory-mapped view may keep its
    /// old modification time for a while, and linking it would keep stale data. Reading both is still far cheaper
    /// than writing and flushing the copy. False whenever anything differs or cannot be read: the file is copied then.
    /// </summary>
    private bool TryLinkUnchanged(FileInfo current, string saved, string to)
    {
        if (link is null || !_linking)
            return false;
        try
        {
            var previous = new FileInfo(saved);
            if (!previous.Exists || previous.Length != current.Length || !SameBytes(current.FullName, saved))
                return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        // Where links cannot be made at all (a sandboxed process may not link files: measured error 5 inside MXC),
        // comparing would only read every file twice more; the rest of the save copies.
        if (link(to, saved))
            return true;
        _linking = false;
        return false;
    }

    /// <summary>Cleared once a link fails; for the rest of this folder's saves, files are copied without comparing.</summary>
    private bool _linking = true;

    private static bool SameBytes(string a, string b)
    {
        using var first = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var left = new byte[1 << 16];
        var right = new byte[1 << 16];
        while (true)
        {
            var read = first.ReadAtLeast(left, left.Length, throwOnEndOfStream: false);
            if (second.ReadAtLeast(right, right.Length, throwOnEndOfStream: false) != read)
                return false;
            if (read == 0)
                return true;
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read)))
                return false;
        }
    }

    /// <summary>
    /// Copies one file. Returns false only when the source cannot be opened or read (still locked by a
    /// process that outlived the app); a copy that fails halfway is removed rather than kept truncated.
    /// Anything failing on the destination side throws, so the copy is never marked complete.
    /// </summary>
    /// <param name="flush">Make the copy durable before returning (saves; a restore into the sandbox need not be).</param>
    private bool TryCopyFile(string from, string to, bool flush)
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
                if (flush && output is FileStream file)
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

    /// <summary>Of <see cref="Files"/>, those unchanged since the last save and linked to it rather than copied.</summary>
    public int Linked { get; set; }

    /// <summary>Files that could not be read and kept the version from the previous save.</summary>
    public List<string> KeptPrevious { get; } = [];

    /// <summary>Files that could not be read and had no previous version.</summary>
    public List<string> Skipped { get; } = [];

    public override string ToString() =>
        $"{Files} files, {Bytes / 1024} KB" +
        (Linked > 0 ? $", {Linked} unchanged" : "") +
        (KeptPrevious.Count > 0 ? $", {KeptPrevious.Count} kept from the previous save" : "") +
        (Skipped.Count > 0 ? $", {Skipped.Count} skipped" : "");
}
