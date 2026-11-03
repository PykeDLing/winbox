using System.Text.Json;
using WinBox.Core.Boxes;
using WinBox.Core.IO;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class BoxStoreTests
{
    [TestMethod]
    public void CommittedImportIsListed()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());

        var loaded = t.Store.Load().OfType<LoadedBox>().Single();
        Assert.AreEqual(box.Config.Id, loaded.Config.Id);
        Assert.IsTrue(Directory.Exists(loaded.Paths.App));
        Assert.IsFalse(Directory.Exists(BoxPaths.PartialRoot(t.Paths.Boxes, box.Config.Id)));
    }

    [TestMethod]
    public void UncommittedImportLeavesNothing()
    {
        using var t = new TestBox();
        var id = Guid.NewGuid();
        using (t.Store.BeginImport(id)) { }

        Assert.AreEqual(0, t.Store.Load().Count);
        Assert.IsFalse(Directory.Exists(BoxPaths.PartialRoot(t.Paths.Boxes, id)));
    }

    [TestMethod]
    public void CrashedImportIsCleanedUpAtStartup()
    {
        using var t = new TestBox();
        Directory.CreateDirectory(BoxPaths.PartialRoot(t.Paths.Boxes, Guid.NewGuid()));

        Assert.AreEqual(1, t.Store.CleanUpInterruptedImports());
        Assert.AreEqual(0, Directory.GetDirectories(t.Paths.Boxes).Length);
    }

    [TestMethod]
    public void UnreadableConfigBecomesBrokenAndIsNotTouched()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        File.WriteAllText(box.Paths.ConfigFile, "{ not json");

        var broken = t.Store.Load().OfType<BrokenBox>().Single();
        Assert.AreEqual(BrokenReason.UnreadableConfig, broken.Reason);
        Assert.AreEqual("{ not json", File.ReadAllText(box.Paths.ConfigFile));
    }

    [TestMethod]
    public void NewerSchemaIsReportedAsBroken()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config() with { SchemaVersion = BoxConfig.CurrentSchemaVersion + 1 });

        Assert.IsInstanceOfType<BrokenBox>(t.Store.Load().Single());
    }

    [TestMethod]
    public void UnknownFieldsSurviveARoundTrip()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        var json = File.ReadAllText(box.Paths.ConfigFile).TrimEnd().TrimEnd('}') + ",\"fromTheFuture\":{\"x\":1}}";
        File.WriteAllText(box.Paths.ConfigFile, json);

        var loaded = t.Store.Load().OfType<LoadedBox>().Single();
        t.Store.Save(loaded with { Config = loaded.Config with { Name = "Renamed" } });

        using var doc = JsonDocument.Parse(File.ReadAllText(box.Paths.ConfigFile));
        Assert.AreEqual("Renamed", doc.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(1, doc.RootElement.GetProperty("fromTheFuture").GetProperty("x").GetInt32());
    }

    [TestMethod]
    public void PackageRoundTripsAndNetworkDefaultIsNotWritten()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config(package: new BoxPackage("WinBox.Box.0123456789ab", "WinBox.Box.0123456789ab_abc")));

        var config = JsonFile.Read(box.Paths.ConfigFile, WinBoxJson.Files.BoxConfig);
        Assert.AreEqual("WinBox.Box.0123456789ab_abc!App", config.Package!.AppUserModelId);
        Assert.IsFalse(File.ReadAllText(box.Paths.ConfigFile).Contains("blockNetwork"));
        Assert.IsTrue(config.AllowsNetwork);
    }

    [TestMethod]
    public void DeleteRemovesTheBox()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());

        t.Store.Delete(box);

        Assert.AreEqual(0, t.Store.Load().Count);
        Assert.AreEqual(0, Directory.GetDirectories(t.Paths.Boxes).Length);
    }

    [TestMethod]
    public void ReadOnlyFoldersAndLinksDoNotStopADelete()
    {
        using var t = new TestBox();
        var box = t.Create(TestBox.Config());
        // What an app leaves in the box profile: Windows marks shell folders read-only.
        var startup = Directory.CreateDirectory(Path.Combine(box.Paths.Roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"));
        File.WriteAllText(Path.Combine(startup.FullName, "app.lnk"), "");
        File.SetAttributes(Path.Combine(startup.FullName, "app.lnk"), FileAttributes.ReadOnly);
        for (var dir = startup; dir.Name != "Microsoft"; dir = dir.Parent!)
            dir.Attributes |= FileAttributes.ReadOnly;
        // And a link to a folder of the user's, which must outlive the box.
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N")));
        File.WriteAllText(Path.Combine(outside.FullName, "keep.txt"), "mine");
        outside.Attributes |= FileAttributes.ReadOnly;
        try
        {
            var linked = TryLink(Path.Combine(box.Paths.Profile, "Documents"), outside.FullName);

            Assert.IsTrue(t.Store.Delete(box));

            Assert.AreEqual(0, Directory.GetDirectories(t.Paths.Boxes).Length);
            Assert.IsTrue(File.Exists(Path.Combine(outside.FullName, "keep.txt")), linked ? "the link's target was deleted" : "no link made");
            Assert.IsTrue(new DirectoryInfo(outside.FullName).Attributes.HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            outside.Attributes &= ~FileAttributes.ReadOnly;
            outside.Delete(recursive: true);
        }
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // Symbolic links need Developer Mode; the read-only part is tested all the same.
        }
    }
}
