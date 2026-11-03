using System.Diagnostics;
using WinBox.Core.Boxes;

namespace WinBox.Platform.Sandbox;

/// <summary>
/// The user profile a boxed app sees (its USERPROFILE, see LaunchPlanner): Windows works the user's folders out
/// from it, so AppData falls inside the box while the personal folders still lead to the user's own.
/// </summary>
/// <remarks>
/// <code>
/// Data\Profile\
///   AppData\Roaming\                 the app's settings, kept in the box
///   AppData\Local\Packages\&lt;family&gt;  link to the package's storage, where Windows puts the sandbox's LocalAppData
///   Desktop, Documents, Downloads... links to the user's real folders
/// </code>
/// The links are made by WinBox outside the sandbox; inside it they reach no further than the sandbox's grants. A
/// folder the app made in place of a link is left as it is. Deleting the box deletes the links, not what they point at.
/// </remarks>
public static class BoxProfile
{
    public static void Prepare(BoxPaths box, string packageFamily, IReadOnlyList<string> personalFolders)
    {
        Directory.CreateDirectory(box.Roaming);
        var packages = Path.Combine(box.Profile, "AppData", "Local", "Packages");
        Directory.CreateDirectory(packages);
        var storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", packageFamily);
        // WinBox's alone. Should a run ever have found a folder here instead of the link, it held nothing but that
        // run's copy of LocalAppData, which BoxShim has saved into State, and it would grow with every run.
        Link(Path.Combine(packages, packageFamily), storage, replaceFolder: true);

        var realProfile = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var folder in personalFolders)
        {
            // Folders moved out of the profile (D:\Documents) are looked up as they are; only the ones Windows
            // finds through USERPROFILE need a way back.
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!full.StartsWith(realProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Link(Path.Combine(box.Profile, full[(realProfile.Length + 1)..]), full);
        }
    }

    private static void Link(string link, string target, bool replaceFolder = false)
    {
        if (!Directory.Exists(target))
            return;
        var existing = new DirectoryInfo(link);
        if (existing.Exists)
        {
            if (string.Equals(existing.LinkTarget, target, StringComparison.OrdinalIgnoreCase))
                return; // The right link already.
            if (existing.LinkTarget is null && !replaceFolder)
                return; // A folder of the app's own.
            // A link to where the folder used to be, or a folder in WinBox's place. Deleting a link leaves its target alone.
            existing.Delete(recursive: existing.LinkTarget is null);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need Developer Mode or elevation; a junction needs neither.
            using var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            mklink?.WaitForExit(10_000);
        }
    }
}
