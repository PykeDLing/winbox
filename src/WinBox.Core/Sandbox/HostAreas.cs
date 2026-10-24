namespace WinBox.Core.Sandbox;

/// <summary>The parts of the host a boxed app may use, looked up again for every launch.</summary>
/// <param name="PersonalFolders">The user's own folders the app may read and write: Desktop, Documents, Downloads...</param>
/// <param name="DataDrives">
/// Whole non-system drives (D:\, E:\...) the app may read and write. Users keep their files there, and often
/// their programs too, which is what <paramref name="InstalledPrograms"/> is for.
/// </param>
/// <param name="InstalledPrograms">
/// Folders programs are installed in, anywhere on the host. The ones on a granted drive are denied: a boxed
/// app that could change a program there would get its code run outside the sandbox the next time the
/// user starts that program.
/// </param>
public sealed record HostAreas(IReadOnlyList<string> PersonalFolders, IReadOnlyList<string> DataDrives, IReadOnlyList<string> InstalledPrograms);

/// <summary>Reads where a program is installed from what its uninstall entry or App Paths entry says.</summary>
public static class InstallLocation
{
    /// <summary>
    /// The folder <paramref name="value"/> points at: the value itself when it names a folder, else the folder
    /// of the file it names, which may be quoted, carry an icon index ("app.exe,0") or be followed by arguments.
    /// Null for anything that is not a full local path.
    /// </summary>
    public static string? FolderOf(string? value, bool isFolder)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            text = end > 1 ? text[1..end] : text.Trim('"');
        }
        else if (!isFolder)
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0)
                text = text[..(exe + 4)];
            else if (text.LastIndexOf(',') is var comma and > 0 && int.TryParse(text[(comma + 1)..], out _))
                text = text[..comma];
        }
        if (!Path.IsPathFullyQualified(text) || text.StartsWith(@"\\", StringComparison.Ordinal))
            return null;
        try
        {
            var full = Path.GetFullPath(text);
            return isFolder ? Path.TrimEndingDirectorySeparator(full) : Path.GetDirectoryName(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
