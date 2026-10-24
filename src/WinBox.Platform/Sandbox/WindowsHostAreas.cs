using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using WinBox.Core.Sandbox;

namespace WinBox.Platform.Sandbox;

/// <summary>Looks up this machine's <see cref="HostAreas"/>: the user's folders, the data drives, and where programs are installed.</summary>
public static partial class WindowsHostAreas
{
    public static HostAreas Current() => new(PersonalFolders(), DataDrives(), InstalledPrograms());

    public static IReadOnlyList<string> PersonalFolders()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic,
        ];
        var paths = folders.Select(Environment.GetFolderPath).ToList();
        // Downloads has no SpecialFolder value. The known folder follows it when the user moved it elsewhere.
        paths.Add(KnownFolder(DownloadsFolderId)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        return paths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Fixed drives other than the system drive. A drive made with subst is left out: it is a folder of
    /// another drive, which granting the drive would expose.
    /// </summary>
    public static IReadOnlyList<string> DataDrives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        var drives = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;
                var root = drive.RootDirectory.FullName;
                if (!root.Equals(system, StringComparison.OrdinalIgnoreCase) && !IsSubst(root))
                    drives.Add(root);
            }
            catch (IOException)
            {
                // The drive went away while being looked at.
            }
        }
        return drives;
    }

    /// <summary>
    /// Folders programs are installed in, from the uninstall entries (both registry views of HKLM, and
    /// HKCU) and App Paths, plus the Program Files folders a data drive may have.
    /// </summary>
    public static IReadOnlyList<string> InstalledPrograms()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (RegistryHive Hive, RegistryView View)[] places =
        [
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        ];
        foreach (var (hive, view) in places)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            ForEachSubKey(root, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", entry =>
            {
                // InstallLocation names the folder. Without it, the folder of the program's icon or of its
                // uninstaller is the best guess.
                if ((InstallLocation.FolderOf(entry.GetValue("InstallLocation") as string, isFolder: true)
                        ?? InstallLocation.FolderOf(entry.GetValue("DisplayIcon") as string, isFolder: false)
                        ?? InstallLocation.FolderOf(entry.GetValue("UninstallString") as string, isFolder: false)) is { } folder)
                    found.Add(folder);
            });
            ForEachSubKey(root, @"Software\Microsoft\Windows\CurrentVersion\App Paths", entry =>
            {
                if (InstallLocation.FolderOf(entry.GetValue(null) as string, isFolder: false) is { } folder)
                    found.Add(folder);
            });
        }
        foreach (var drive in DataDrives())
        {
            foreach (var name in new[] { "Program Files", "Program Files (x86)", "WindowsApps" })
            {
                var path = Path.Combine(drive, name);
                if (Directory.Exists(path))
                    found.Add(path);
            }
        }
        return [.. found];
    }

    private static void ForEachSubKey(RegistryKey root, string path, Action<RegistryKey> visit)
    {
        using var parent = root.OpenSubKey(path);
        if (parent is null)
            return;
        foreach (var name in parent.GetSubKeyNames())
        {
            try
            {
                using var entry = parent.OpenSubKey(name);
                if (entry is not null)
                    visit(entry);
            }
            catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
            {
                // An entry this user cannot read says nothing either way.
            }
        }
    }

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, 0, out var path) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    private static unsafe bool IsSubst(string root)
    {
        var buffer = stackalloc char[1024];
        // subst drives map to "\??\C:\some\folder"; real volumes to "\Device\HarddiskVolumeN".
        return QueryDosDeviceW(root.TrimEnd('\\'), buffer, 1024) > 0 && new string(buffer).StartsWith(@"\??\", StringComparison.Ordinal);
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDeviceW(string deviceName, char* targetPath, uint max);
}
