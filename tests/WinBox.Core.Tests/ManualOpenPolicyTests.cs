using WinBox.Core.Boxes;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class ManualOpenPolicyTests
{
    private static readonly BoxPaths Box = new(@"C:\Users\test\Desktop\winbox\.data\Boxes\b.box");
    private const string PackageStorage = @"C:\Users\test\AppData\Local\Packages\WinBox.Box.b_pub";

    private static readonly LaunchSpec Spec = new()
    {
        Executable = Box.ShimExecutable,
        WorkingDirectory = Box.App,
        ReadWritePaths =
        [
            Box.App, Box.Shim, Box.Data, Box.Tmp, Box.State, PackageStorage,
            @"C:\Users\test\Desktop", @"C:\Users\test\Downloads",
            @"E:\", @"E:\galgame", @"E:\steam",
        ],
        DeniedPaths = [Box.ConfigFile, Box.Host, @"E:\steam", @"C:\Users\test\Desktop\winbox\.data\Logs"],
    };

    [TestMethod]
    public void DocumentInAGrantedFolderMayBeOpened()
    {
        Assert.IsNull(ManualOpenPolicy.CheckFile(Spec, @"E:\galgame\25年合集.rar"));
        Assert.IsNull(ManualOpenPolicy.CheckFile(Spec, @"C:\Users\test\Downloads\sub\report.pdf"));
    }

    [TestMethod]
    public void ProgramsAndShortcutsAreRefusedEvenInsideGrants()
    {
        foreach (var name in new[] { "setup.exe", "run.BAT", "invoice.pdf.lnk", "page.url", "tool.ps1", "disk.iso", "fix.reg" })
            Assert.AreEqual(ManualOpenRefusal.DangerousType, ManualOpenPolicy.CheckFile(Spec, $@"C:\Users\test\Downloads\{name}"), name);
    }

    [TestMethod]
    public void PlacesOutsideTheGrantsAreRefused()
    {
        Assert.AreEqual(ManualOpenRefusal.OutsideGrants, ManualOpenPolicy.CheckFile(Spec, @"C:\Windows\win.ini"));
        Assert.AreEqual(ManualOpenRefusal.OutsideGrants, ManualOpenPolicy.CheckFolder(Spec, @"C:\Users\test\Documents"));
        // A prefix of a granted folder's name is not inside it.
        Assert.AreEqual(ManualOpenRefusal.OutsideGrants, ManualOpenPolicy.CheckFile(Spec, @"C:\Users\test\DesktopOld\a.txt"));
    }

    [TestMethod]
    public void DriveRootGrantCoversOnlyTheFilesAtTheRoot()
    {
        Assert.IsNull(ManualOpenPolicy.CheckFile(Spec, @"E:\notes.txt"));
        Assert.IsNull(ManualOpenPolicy.CheckFolder(Spec, @"E:\"));
        // Not granted folder by folder, as MXC applies a drive grant: hidden system folders, folders made later.
        Assert.AreEqual(ManualOpenRefusal.OutsideGrants, ManualOpenPolicy.CheckFile(Spec, @"E:\$RECYCLE.BIN\x.txt"));
    }

    [TestMethod]
    public void DeniedPartsWinOverTheGrantsAroundThem()
    {
        Assert.AreEqual(ManualOpenRefusal.Denied, ManualOpenPolicy.CheckFile(Spec, @"E:\steam\readme.txt"));
        Assert.AreEqual(ManualOpenRefusal.Denied, ManualOpenPolicy.CheckFile(Spec, @"C:\Users\test\Desktop\winbox\.data\Logs\app.log"));
        Assert.AreEqual(ManualOpenRefusal.Denied, ManualOpenPolicy.CheckFile(Spec, Box.ConfigFile));
    }

    [TestMethod]
    public void OnlyFullLocalPathsAreAccepted()
    {
        foreach (var path in new[]
                 {
                     @"\\server\share\a.txt", @"\\?\E:\galgame\a.txt", @"\\.\E:\galgame\a.txt",
                     @"E:\galgame\a.txt:hidden", @"galgame\a.txt", @"E:galgame\a.txt", "shell:Downloads",
                 })
            Assert.AreEqual(ManualOpenRefusal.NotLocalPath, ManualOpenPolicy.CheckFile(Spec, path), path);
    }

    [TestMethod]
    public void UserFoldersAreThePersonalFoldersAndDriveRoots()
    {
        var folders = ManualOpenPolicy.UserFolders(Spec, Box, PackageStorage);

        CollectionAssert.AreEqual(new[] { @"C:\Users\test\Desktop", @"C:\Users\test\Downloads", @"E:\" }, folders.ToList());
    }
}
