using System.Security.AccessControl;
using System.Security.Principal;

namespace WinBox.Platform.Sandbox;

/// <summary>
/// Lets every AppContainer read and run a box's App folder, as Windows lets them read and run Program Files.
/// The sandbox's own grant is an entry for the box's package alone; Chromium-based apps check that their install
/// folder is open to ALL APPLICATION PACKAGES as well, since their own helper processes run in AppContainers of
/// their own, and stop at once when it is not (Electron: "FATAL: install_dir_access.cc ... Sandboxed processes
/// cannot read"). Only reading and running: the folder holds the app's program files, nothing of the user's.
/// </summary>
public static class AppFolderAccess
{
    private static readonly SecurityIdentifier AllApplicationPackages = new("S-1-15-2-1");
    private const FileSystemRights ReadAndRun = FileSystemRights.ReadAndExecute;
    private const InheritanceFlags ToEverythingInside = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Adds the entry when the folder lacks it; Windows passes it on to everything already inside.</summary>
    public static void Ensure(string appDirectory)
    {
        var dir = new DirectoryInfo(appDirectory);
        if (!dir.Exists)
            return;
        var acl = dir.GetAccessControl(AccessControlSections.Access);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference.Equals(AllApplicationPackages) && rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & ReadAndRun) == ReadAndRun && (rule.InheritanceFlags & ToEverythingInside) == ToEverythingInside)
                return;
        }
        acl.AddAccessRule(new FileSystemAccessRule(AllApplicationPackages, ReadAndRun, ToEverythingInside, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(acl);
    }
}
