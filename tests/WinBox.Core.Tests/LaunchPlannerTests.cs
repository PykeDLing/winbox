using WinBox.Core.Boxes;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class LaunchPlannerTests
{
    private const string LocalAppData = @"C:\Users\test\AppData\Local";
    private static readonly string[] Personal = [@"C:\Users\test\Documents"];

    [TestMethod]
    public void PackagedBoxMayWriteItsPackageStorage()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config(package: new BoxPackage("WinBox.Box.x", "WinBox.Box.x_pub")));

        var spec = new LaunchPlanner(Personal, LocalAppData).Plan(box, DateTimeOffset.Now);

        CollectionAssert.Contains(spec.ReadWritePaths.ToList(), @"C:\Users\test\AppData\Local\Packages\WinBox.Box.x_pub");
    }

    [TestMethod]
    public void UnpackagedBoxGetsNoPackageStorage()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());

        var spec = new LaunchPlanner(Personal, LocalAppData).Plan(box, DateTimeOffset.Now);

        Assert.IsFalse(spec.ReadWritePaths.Any(p => p.Contains(@"\Packages\", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PrivateItemsAreDeniedEvenInsideGrantedFolders()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());

        var spec = new LaunchPlanner([t.Root], LocalAppData).Plan(box, DateTimeOffset.Now);

        CollectionAssert.Contains(spec.ReadWritePaths.ToList(), t.Root);
        foreach (var item in new[] { box.Paths.ConfigFile, box.Paths.Host, box.Paths.Reports, box.Paths.Source })
            CollectionAssert.Contains(spec.DeniedPaths.ToList(), item);
        CollectionAssert.DoesNotContain(spec.DeniedPaths.ToList(), box.Paths.Data);
    }

    [TestMethod]
    public void RestOfWinBoxIsDeniedWhenAGrantedFolderHoldsIt()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        var other = t.Create(TestBox.Config());
        t.Paths.EnsureCreated();
        var install = Directory.CreateDirectory(Path.Combine(t.Root, "..", Path.GetFileName(t.Root) + "-install")).FullName;
        try
        {
            // The data root and the install directory both sit in a granted personal folder, as in development.
            var personal = Path.GetDirectoryName(t.Root)!;
            var spec = new LaunchPlanner([personal], LocalAppData, [install, t.Root]).Plan(box, DateTimeOffset.Now);
            var denied = spec.DeniedPaths.ToList();

            foreach (var item in new[] { other.Paths.Root, t.Paths.Downloads, t.Paths.Logs, install, box.Paths.ConfigFile })
                CollectionAssert.Contains(denied, item);
            // Nothing that holds the box's own grants may be denied: that would take the grants back.
            foreach (var item in new[] { t.Root, t.Paths.Boxes, box.Paths.Root, box.Paths.App, box.Paths.Data, box.Paths.State })
                CollectionAssert.DoesNotContain(denied, item);
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    [TestMethod]
    public void WinBoxOutsideGrantedFoldersNeedsNoExtraDenials()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        t.Create(TestBox.Config());

        var spec = new LaunchPlanner(Personal, LocalAppData, [t.Root]).Plan(box, DateTimeOffset.Now);

        CollectionAssert.AreEquivalent(box.Paths.PrivateItems.ToList(), spec.DeniedPaths.ToList());
    }

    [TestMethod]
    public void DataPlaceholderIsExpanded()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config() with { LaunchArguments = ["--user-data-dir={data}\\User Data"] });

        var spec = new LaunchPlanner(Personal, LocalAppData).Plan(box, DateTimeOffset.Now);

        Assert.AreEqual($"--user-data-dir={box.Paths.Data}\\User Data", ShimArguments.Parse(spec.Arguments).Arguments.Single());
    }

    [TestMethod]
    public void AppStartsThroughTheShimWhichCarriesLocalAppData()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config() with { LaunchArguments = ["--flag"] });

        var spec = new LaunchPlanner(Personal, LocalAppData).Plan(box, DateTimeOffset.Now);
        var shim = ShimArguments.Parse(spec.Arguments);

        Assert.AreEqual(box.Paths.ShimExecutable, spec.Executable);
        Assert.AreEqual(Path.Combine(box.Paths.App, "app.exe"), shim.Executable);
        CollectionAssert.AreEqual(new[] { "--flag" }, shim.Arguments.ToList());
        Assert.AreEqual(box.Paths.LocalAppDataState, shim.StateDirectory);
        CollectionAssert.Contains(shim.Exclusions.ToList(), "Temp");
        // The shim must be executable and its state writable from inside the sandbox.
        CollectionAssert.Contains(spec.ReadWritePaths.ToList(), box.Paths.Shim);
        CollectionAssert.Contains(spec.ReadWritePaths.ToList(), box.Paths.State);
        Assert.AreEqual(box.Paths.App, spec.WorkingDirectory);
    }

    [TestMethod]
    public void DataDrivesAreGrantedFolderByFolderAndTheProgramsOnThemAreDenied()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        // A folder standing in for a drive root.
        var drive = Directory.CreateDirectory(Path.Combine(t.Root, "drive")).FullName;
        var games = Directory.CreateDirectory(Path.Combine(drive, "Games")).FullName;
        var quark = Directory.CreateDirectory(Path.Combine(drive, "Quark")).FullName;
        var system = Directory.CreateDirectory(Path.Combine(drive, "System Volume Information"));
        system.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        var steam = Path.Combine(games, "Steam");
        var areas = new HostAreas(Personal, [drive], [quark + @"\", steam, @"C:\Program Files\App"]);

        var spec = new LaunchPlanner(() => areas, LocalAppData).Plan(box, DateTimeOffset.Now);

        // MXC grants a drive root to its own files only, so the top-level folders are granted one by one.
        CollectionAssert.IsSubsetOf(new[] { drive, games }, spec.ReadWritePaths.ToList());
        CollectionAssert.DoesNotContain(spec.ReadWritePaths.ToList(), quark);
        CollectionAssert.DoesNotContain(spec.ReadWritePaths.ToList(), system.FullName);
        CollectionAssert.IsSubsetOf(new[] { quark, steam }, spec.DeniedPaths.ToList());
        // Not on a granted drive: never granted in the first place, so nothing to deny.
        CollectionAssert.DoesNotContain(spec.DeniedPaths.ToList(), @"C:\Program Files\App");
    }

    [TestMethod]
    public void ProgramEntriesThatWouldTakeAGrantBackAreSkipped()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        var drive = Path.GetPathRoot(t.Root)!;
        // Bad uninstall data naming a whole drive, and an install folder that holds the box itself.
        var areas = new HostAreas([], [drive], [drive, Path.GetDirectoryName(t.Root)!]);

        var spec = new LaunchPlanner(() => areas, LocalAppData).Plan(box, DateTimeOffset.Now);

        CollectionAssert.DoesNotContain(spec.DeniedPaths.ToList(), drive);
        CollectionAssert.DoesNotContain(spec.DeniedPaths.ToList(), Path.GetDirectoryName(t.Root)!);
    }

    [TestMethod]
    public void RestOfWinBoxIsDeniedWhenAGrantedDriveHoldsIt()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        var other = t.Create(TestBox.Config());
        t.Paths.EnsureCreated();
        var areas = new HostAreas([], [Path.GetPathRoot(t.Root)!], []);

        var spec = new LaunchPlanner(() => areas, LocalAppData, [t.Root]).Plan(box, DateTimeOffset.Now);

        CollectionAssert.Contains(spec.DeniedPaths.ToList(), other.Paths.Root);
        CollectionAssert.DoesNotContain(spec.DeniedPaths.ToList(), box.Paths.Data);
    }

    [TestMethod]
    public void ControlFolderIsPassedToTheShim()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());

        var spec = new LaunchPlanner(Personal, LocalAppData).Plan(box, DateTimeOffset.Now);

        Assert.AreEqual(box.Paths.Control, ShimArguments.Parse(spec.Arguments).ControlDirectory);
    }

    [TestMethod]
    [DataRow(@"E:\Quark\", true, @"E:\Quark")]
    [DataRow(@"""E:\Quark""", true, @"E:\Quark")]
    [DataRow(@"E:\Quark\quark.exe,0", false, @"E:\Quark")]
    [DataRow(@"""E:\Program Files\App\unins000.exe"" /SILENT", false, @"E:\Program Files\App")]
    [DataRow(@"E:\Program Files\App\uninstall.exe /S", false, @"E:\Program Files\App")]
    [DataRow(@"E:\Tools\icon.ico,3", false, @"E:\Tools")]
    [DataRow(@"MsiExec.exe /X{1234}", false, null)]
    [DataRow(@"\\server\share\app", true, null)]
    [DataRow("", true, null)]
    public void InstallLocationsAreReadFromWhatEntriesSay(string value, bool isFolder, string? expected) =>
        Assert.AreEqual(expected, InstallLocation.FolderOf(value, isFolder));

    [TestMethod]
    public void EntryOutsideAppIsRejected()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        var escaping = box with { Config = box.Config with { Entry = @"..\Data\evil.exe" } };

        Assert.ThrowsExactly<InvalidOperationException>(() => new LaunchPlanner(Personal, LocalAppData).Plan(escaping, DateTimeOffset.Now));
    }
}
