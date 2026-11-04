using WinBox.Core.Sandbox;

namespace WinBox.Core.Boxes;

/// <summary>
/// The box as it was before its last update, kept so the user can go back to it: its app, a copy of its data, its
/// saved state, its installer, and its box.json then (<see cref="Config"/>). Going back swaps the backup with the box
/// (<see cref="BoxStore.GoBack"/>), so the box as it was becomes the backup and nothing is lost either way.
/// </summary>
/// <remarks>
/// Not kept: what Windows holds for the box outside its folder, the package's registry hive among it (it stays as the
/// newer version left it); the links in the box profile, which WinBox makes again before every run; and the caches
/// Chromium-based apps rebuild by themselves (<see cref="StateFolder.RebuiltCaches"/>).
/// </remarks>
public sealed record BoxBackup
{
    public required BoxConfig Config { get; init; }

    /// <summary>When the box was as the backup has it: the time of the update, or of going back, that changed it.</summary>
    public required DateTimeOffset MadeAt { get; init; }

    /// <summary>The disk space deleting the backup gives back: its app and its copy of the data.</summary>
    public long Bytes { get; init; }
}

/// <summary>Why an update kept no backup of the box as it was.</summary>
public enum BackupSkip
{
    /// <summary>The copy of the box's data would not leave <see cref="BoxStore.BackupHeadroom"/> free on its drive.</summary>
    NoSpace,

    /// <summary>Copying the box's data failed (a file held, say); the update went on without a backup.</summary>
    Failed,
}

/// <summary>Measures and copies what a backup holds.</summary>
internal static class BackupFiles
{
    private static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, RecurseSubdirectories = false };

    /// <summary>The bytes <see cref="Copy"/> takes from <paramref name="dir"/>; 0 when it does not exist.</summary>
    public static long Measure(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
            return 0;
        long bytes = 0;
        foreach (var entry in info.EnumerateFileSystemInfos("*", Everything))
        {
            if (Skipped(entry))
                continue;
            bytes += entry is DirectoryInfo sub ? Measure(sub.FullName) : ((FileInfo)entry).Length;
        }
        return bytes;
    }

    /// <summary>
    /// Copies <paramref name="from"/> into <paramref name="to"/>, which it makes even when there is nothing to copy.
    /// Links are left out: the box profile's lead to the user's own folders and to the package's storage, and WinBox
    /// makes them again before every run. So are the caches Chromium-based apps rebuild. With <paramref name="link"/>,
    /// files are hard-linked rather than copied wherever that works: only for folders whose files are never written in
    /// place, or the backup would change with them.
    /// </summary>
    public static void Copy(string from, string to, Func<string, string, bool>? link, CancellationToken cancellationToken)
    {
        var source = new DirectoryInfo(from);
        Directory.CreateDirectory(to);
        if (!source.Exists)
            return;
        foreach (var entry in source.EnumerateFileSystemInfos("*", Everything))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Skipped(entry))
                continue;
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo sub)
            {
                Copy(sub.FullName, target, link, cancellationToken);
                // Shell folders keep their read-only mark, which is what makes Explorer read their desktop.ini.
                new DirectoryInfo(target).Attributes = sub.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            }
            else if (link is null || !link(target, entry.FullName))
                File.Copy(entry.FullName, target);
        }
    }

    private static bool Skipped(FileSystemInfo entry) =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || (entry is DirectoryInfo dir && StateFolder.IsRebuiltCache(dir));
}
