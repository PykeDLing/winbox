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
    public void EntryOutsideAppIsRejected()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        var escaping = box with { Config = box.Config with { Entry = @"..\Data\evil.exe" } };

        Assert.ThrowsExactly<InvalidOperationException>(() => new LaunchPlanner(Personal, LocalAppData).Plan(escaping, DateTimeOffset.Now));
    }
}
