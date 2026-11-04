using WinBox.Core.Boxes;
using WinBox.Core.Import;
using WinBox.Core.IO;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class BoxUpdateRuleTests
{
    private static BoxConfig App(string entry = "Tool.exe", string name = "Tool", string? publisher = "Tool Co", string? recipe = null) =>
        TestBox.Config(entry: entry) with
        {
            Name = name,
            Publisher = publisher,
            Source = new BoxSource { FileName = "setup.exe", Kind = InstallerKind.Nsis, RecipeId = recipe },
        };

    [TestMethod]
    public void TheSameProgramUnderTheSameNameOrPublisherIsTheSameApp()
    {
        Assert.IsTrue(BoxUpdate.IsSameApp(App(), App(entry: @"app-2.0\tool.exe")));
        Assert.IsTrue(BoxUpdate.IsSameApp(App(), App(name: "Tool 2")));
        Assert.IsTrue(BoxUpdate.IsSameApp(App(), App(publisher: "Someone Else")));
        Assert.IsFalse(BoxUpdate.IsSameApp(App(), App(name: "Tool 2", publisher: "Someone Else")));
        Assert.IsFalse(BoxUpdate.IsSameApp(App(), App(entry: "Other.exe")));
        Assert.IsFalse(BoxUpdate.IsSameApp(App(publisher: null), App(name: "Tool 2", publisher: null)));
    }

    [TestMethod]
    public void TheSameRecipeIsTheSameApp() =>
        Assert.IsTrue(BoxUpdate.IsSameApp(App(recipe: "quark-cloud-drive"), App(entry: "other.exe", name: "x", publisher: null, recipe: "quark-cloud-drive")));

    [TestMethod]
    public void AnUpdateTheUserPicksMayRenameTheProgramOrTheApp()
    {
        Assert.IsTrue(BoxUpdate.MayUpdate(App(), App(entry: "Tool2.exe")));
        Assert.IsTrue(BoxUpdate.MayUpdate(App(), App(name: "Tool 2", publisher: null)));
        Assert.IsFalse(BoxUpdate.MayUpdate(App(), App(entry: "Obsidian.exe", name: "Obsidian", publisher: "Tool Co")));
    }

    [TestMethod]
    [DataRow("7.2.0.800", "7.4.0.818", VersionChange.Newer)]
    [DataRow("1.15.0.0", "1.15.0", VersionChange.Same)]
    [DataRow("1.144.1 (user setup)", "1.144.1", VersionChange.Same)]
    [DataRow("8.9.7.101", "8.10.0", VersionChange.Newer)]
    [DataRow("2.0", "1.9.9", VersionChange.Older)]
    [DataRow(null, "1.0", VersionChange.Unknown)]
    [DataRow("beta", "1.0", VersionChange.Unknown)]
    public void VersionsCompareByTheirNumbers(string? current, string incoming, VersionChange change) =>
        Assert.AreEqual(change, BoxUpdate.Compare(current, incoming));

    [TestMethod]
    public void AnUpdateTakesWhatDescribesTheAppAndKeepsWhatDescribesTheBox()
    {
        var package = new BoxPackage("WinBox.Box.1", "WinBox.Box.1_x");
        var box = App() with { Version = "1.0", Package = package, BlockNetwork = true, LaunchArguments = ["--old"] };
        var incoming = App(entry: @"app-2.0\Tool.exe", name: "Tool Two") with
        {
            Id = Guid.NewGuid(),
            Version = "2.0",
            LaunchArguments = ["--no-sandbox"],
            Issues = [new CompatibilityIssue(IssueCode.InnerSandboxDisabled, IssueSeverity.Info)],
            CreatedAt = DateTimeOffset.Now,
        };
        var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

        var updated = BoxUpdate.Apply(box, incoming, now);

        Assert.AreEqual(box.Id, updated.Id);
        Assert.AreEqual("Tool", updated.Name);
        Assert.AreEqual(box.CreatedAt, updated.CreatedAt);
        Assert.AreEqual(package, updated.Package);
        Assert.AreEqual(true, updated.BlockNetwork);
        Assert.AreEqual("2.0", updated.Version);
        Assert.AreEqual(@"app-2.0\Tool.exe", updated.Entry);
        CollectionAssert.AreEqual(new[] { "--no-sandbox" }, updated.LaunchArguments.ToArray());
        Assert.AreEqual(IssueCode.InnerSandboxDisabled, updated.Issues.Single().Code);
        Assert.AreEqual(now, updated.UpdatedAt);
    }
}

[TestClass]
public sealed class BoxUpdateTests
{
    private TestBox _t = null!;
    private BoxManagerTests.FakePackages _packages = null!;
    private BoxManagerTests.FakeRunner _runner = null!;
    private string _downloads = "";

    /// <summary>What the next installer unpacks to: its files, and the version information of its programs.</summary>
    private readonly Dictionary<string, ExecutableInfo?> _info = new(StringComparer.OrdinalIgnoreCase);
    private Action<string> _unpack = _ => { };

    [TestInitialize]
    public void Create()
    {
        _t = new TestBox();
        _packages = new BoxManagerTests.FakePackages();
        _runner = new BoxManagerTests.FakeRunner();
        _downloads = Directory.CreateDirectory(Path.Combine(_t.Root, "dropped")).FullName;
    }

    [TestCleanup]
    public void Delete() => _t.Dispose();

    private sealed class Inspector(Dictionary<string, ExecutableInfo?> info) : IFileInspector
    {
        public ExecutableInfo? ReadExecutableInfo(string path) => info.GetValueOrDefault(Path.GetFileName(path));
        public SignatureInfo ReadSignature(string path) => new(false, null);
    }

    private sealed class Extractor(Func<Action<string>> unpack) : IPackageExtractor
    {
        public InstallerKind? Recognize(InstallerFacts facts) => InstallerKind.Nsis;

        public Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default)
        {
            var app = Directory.CreateDirectory(Path.Combine(destination, "app")).FullName;
            unpack()(app);
            return Task.FromResult(new ExtractionResult(app, []));
        }
    }

    private BoxManager Manager(TimeSpan? stopWait = null) => new(
        _t.Paths, _t.Store,
        new ImportPipeline(_t.Paths, _t.Store, new Inspector(_info), [new Extractor(() => _unpack)], new BoxManagerTests.NoDownloader(), []),
        _packages, _runner, new BoxManagerTests.FakeGuard(),
        new LaunchPlanner([], @"C:\Users\test\AppData\Local"),
        stopWait ?? TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));

    /// <summary>An installer of Tool <paramref name="version"/>: Tool.exe, which says it is that version, and a file naming it.</summary>
    private string Installer(string version, string program = "Tool.exe", string name = "Tool")
    {
        _info[program] = new ExecutableInfo(null, name, name, "Tool Co", version);
        _unpack = app =>
        {
            TestPe.Write(Path.Combine(app, program), TestPe.Build());
            File.WriteAllText(Path.Combine(app, "version.txt"), version);
        };
        return TestPe.Write(Path.Combine(_downloads, $"{name.Replace(' ', '_')}_setup_{version}.exe"), TestPe.Build());
    }

    private async Task<LoadedBox> ImportedTool(string version = "1.0.0")
    {
        var (box, _) = await Manager().ImportAsync(Installer(version));
        // What the app saved in its box: it must survive every update.
        Directory.CreateDirectory(Path.Combine(box.Paths.Roaming, "Tool"));
        File.WriteAllText(Path.Combine(box.Paths.Roaming, "Tool", "settings.json"), "{\"signedIn\":true}");
        Directory.CreateDirectory(box.Paths.LocalAppDataState);
        File.WriteAllText(Path.Combine(box.Paths.LocalAppDataState, "cache.db"), "state");
        return _t.Store.Reload(box.Paths)!;
    }

    private static string VersionIn(LoadedBox box) => File.ReadAllText(Path.Combine(box.Paths.App, "version.txt"));

    private void AssertNothingLeftOver(LoadedBox box)
    {
        Assert.IsFalse(Directory.Exists(box.Paths.AppStaging), "App.new left over");
        Assert.IsFalse(Directory.Exists(box.Paths.AppPrevious), "App.old left over");
        Assert.IsFalse(File.Exists(box.Paths.UnfinishedMarker), ".unfinished left over");
        Assert.IsFalse(Directory.Exists(box.Paths.BackupStaging), "Backup.new left over");
        Assert.IsFalse(Directory.Exists(box.Paths.BackupSwap), "Backup.swap left over");
        Assert.IsFalse(File.Exists(box.Paths.GoingBackMarker), ".going-back left over");
        Assert.AreEqual(0, Directory.GetDirectories(_t.Paths.Boxes, "*" + BoxPaths.PartialExtension).Length, "staging left over");
    }

    private static void AssertDataKept(LoadedBox box)
    {
        Assert.AreEqual("{\"signedIn\":true}", File.ReadAllText(Path.Combine(box.Paths.Roaming, "Tool", "settings.json")));
        Assert.AreEqual("state", File.ReadAllText(Path.Combine(box.Paths.LocalAppDataState, "cache.db")));
    }

    [TestMethod]
    public async Task AnInstallerOfAnAppInTheLibraryIsOfferedAsAnUpdateAndKeepsTheData()
    {
        var box = await ImportedTool("1.0.0");
        var registered = await Manager().RegisterAsync(box);
        BoxConfig? offered = null;

        var imported = await Manager().ImportAsync(Installer("2.0.0"), chooseUpdate: (boxes, incoming, _) =>
        {
            offered = incoming;
            return Task.FromResult<LoadedBox?>(boxes.Single());
        });

        Assert.IsTrue(imported.Updated);
        Assert.AreEqual("2.0.0", offered?.Version);
        var updated = _t.Store.Load().OfType<LoadedBox>().Single();
        Assert.AreEqual(box.Config.Id, updated.Config.Id);
        Assert.AreEqual("2.0.0", updated.Config.Version);
        Assert.AreEqual(registered.Config.Package, updated.Config.Package);
        Assert.IsNotNull(updated.Config.UpdatedAt);
        Assert.AreEqual("2.0.0", VersionIn(updated));
        CollectionAssert.AreEqual(new[] { "Tool_setup_2.0.0.exe" }, Directory.GetFiles(updated.Paths.Source).Select(Path.GetFileName).ToArray());
        AssertDataKept(updated);
        AssertNothingLeftOver(updated);
    }

    [TestMethod]
    public async Task ChoosingANewBoxLeavesTheOldOneAlone()
    {
        var box = await ImportedTool("1.0.0");

        var imported = await Manager().ImportAsync(Installer("2.0.0"), chooseUpdate: (_, _, _) => Task.FromResult<LoadedBox?>(null));

        Assert.IsFalse(imported.Updated);
        Assert.AreEqual(2, _t.Store.Load().Count);
        Assert.AreEqual("1.0.0", VersionIn(_t.Store.Reload(box.Paths)!));
    }

    [TestMethod]
    public async Task AnotherAppIsNotOffered()
    {
        await ImportedTool();
        var asked = false;

        await Manager().ImportAsync(Installer("1.0", program: "Other.exe", name: "Other"), chooseUpdate: (_, _, _) =>
        {
            asked = true;
            return Task.FromResult<LoadedBox?>(null);
        });

        Assert.IsFalse(asked);
        Assert.AreEqual(2, _t.Store.Load().Count);
    }

    [TestMethod]
    public async Task ARunningBoxIsQuitBeforeTheUpdateItWasOffered()
    {
        var box = await ImportedTool();
        _runner.Running.Add(box.Config.Id);
        _runner.StopsWhenAsked = true;

        await Manager(stopWait: TimeSpan.FromSeconds(5)).ImportAsync(Installer("2.0.0"), chooseUpdate: (boxes, _, _) => Task.FromResult<LoadedBox?>(boxes[0]));

        CollectionAssert.Contains(_runner.StopRequests, box.Config.Id);
        Assert.AreEqual("2.0.0", VersionIn(box));
    }

    [TestMethod]
    public async Task AnUpdateThatWasNotOfferedLeavesARunningBoxAlone()
    {
        var box = await ImportedTool("1.0.0");
        _runner.Running.Add(box.Config.Id);

        var busy = await Assert.ThrowsExactlyAsync<BoxBusyException>(() => Manager().UpdateAsync(box, Installer("2.0.0")));

        Assert.AreEqual(BoxBusyReason.Running, busy.Reason);
        Assert.AreEqual(0, _runner.StopRequests.Count);
        Assert.AreEqual("1.0.0", VersionIn(box));
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task AnInstallerOfAnotherAppDoesNotUpdateTheBox()
    {
        var box = await ImportedTool("1.0.0");

        var refused = await Assert.ThrowsExactlyAsync<ImportException>(() =>
            Manager().UpdateAsync(box, Installer("9.0", program: "Obsidian.exe", name: "Obsidian")));

        Assert.AreEqual(ImportFailure.NotTheSameApp, refused.Failure);
        Assert.AreEqual("Obsidian", refused.Detail);
        Assert.AreEqual("1.0.0", VersionIn(box));
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task AnOlderVersionIsRefusedUnlessTheCallerAgrees()
    {
        var box = await ImportedTool("2.0.0");

        var unasked = await Assert.ThrowsExactlyAsync<ImportException>(() => Manager().UpdateAsync(box, Installer("1.0.0")));
        var declined = await Assert.ThrowsExactlyAsync<ImportException>(() =>
            Manager().UpdateAsync(box, Installer("1.0.0"), confirmDowngrade: (_, _, _) => Task.FromResult(false)));

        Assert.AreEqual(ImportFailure.OlderVersion, unasked.Failure);
        Assert.AreEqual(ImportFailure.OlderVersion, declined.Failure);
        Assert.AreEqual("1.0.0", declined.Detail);
        Assert.AreEqual("2.0.0", VersionIn(box));
        Assert.AreEqual("2.0.0", _t.Store.Reload(box.Paths)!.Config.Version);
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task AnOlderVersionTheCallerAgreesToKeepsTheData()
    {
        var box = await ImportedTool("2.0.0");
        (string? Has, string? Brings)? asked = null;

        var updated = (await Manager().UpdateAsync(box, Installer("1.0.0"), confirmDowngrade: (current, incoming, _) =>
        {
            asked = (current.Config.Version, incoming.Version);
            return Task.FromResult(true);
        })).Box;

        Assert.AreEqual(("2.0.0", "1.0.0"), asked);
        Assert.AreEqual("1.0.0", updated.Config.Version);
        Assert.AreEqual("1.0.0", VersionIn(updated));
        AssertDataKept(updated);
        AssertNothingLeftOver(updated);
    }

    [TestMethod]
    [DataRow("1.0.0")]
    [DataRow("2.0.0")]
    public async Task TheSameOrANewerVersionIsNotAskedAbout(string version)
    {
        var box = await ImportedTool("1.0.0");

        var updated = (await Manager().UpdateAsync(box, Installer(version), confirmDowngrade: (_, _, _) => throw new AssertFailedException("asked"))).Box;

        Assert.AreEqual(version, VersionIn(updated));
    }

    [TestMethod]
    public async Task ARunningBoxIsNotAskedAboutAnOlderVersion()
    {
        var box = await ImportedTool("2.0.0");
        _runner.Running.Add(box.Config.Id);
        var asked = false;

        await Assert.ThrowsExactlyAsync<BoxBusyException>(() => Manager().UpdateAsync(box, Installer("1.0.0"), confirmDowngrade: (_, _, _) =>
        {
            asked = true;
            return Task.FromResult(true);
        }));

        Assert.IsFalse(asked);
        Assert.AreEqual("2.0.0", VersionIn(box));
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task AnUpdateMayMoveTheProgram()
    {
        var box = await ImportedTool("1.0.0");
        var installer = Installer("2.0.0");
        var unpack = _unpack;
        _unpack = app => unpack(Directory.CreateDirectory(Path.Combine(app, "app-2.0.0")).FullName);

        var updated = (await Manager().UpdateAsync(box, installer)).Box;

        Assert.AreEqual(@"app-2.0.0\Tool.exe", updated.Config.Entry);
        Assert.IsTrue(File.Exists(Path.Combine(updated.Paths.App, updated.Config.Entry)));
        AssertDataKept(updated);
    }

    // A crash at each step of BoxStore.ReplaceApp, and what the next start makes of it.

    private async Task<(LoadedBox Box, BoxConfig Pending)> Interrupted(bool staged, bool pending, bool swapped)
    {
        var box = await ImportedTool("1.0.0");
        var next = box.Config with { Version = "2.0.0", UpdatedAt = DateTimeOffset.Now };
        if (staged)
        {
            Directory.CreateDirectory(box.Paths.AppStaging);
            File.WriteAllText(Path.Combine(box.Paths.AppStaging, "version.txt"), "2.0.0");
        }
        if (pending)
            JsonFile.Write(box.Paths.UnfinishedMarker, next, WinBoxJson.Files.BoxConfig);
        if (swapped)
        {
            Directory.Move(box.Paths.App, box.Paths.AppPrevious);
            if (staged)
                Directory.Move(box.Paths.AppStaging, box.Paths.App);
        }
        return (box, next);
    }

    [TestMethod]
    public async Task ACrashBeforeTheNewBoxJsonWasWrittenDropsTheNewApp()
    {
        var (box, _) = await Interrupted(staged: true, pending: false, swapped: false);

        Assert.AreEqual(UpdateRecovery.RolledBack, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("1.0.0", VersionIn(box));
        Assert.AreEqual("1.0.0", _t.Store.Reload(box.Paths)!.Config.Version);
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task ACrashBeforeTheSwapPutsNothingInPlace()
    {
        var (box, _) = await Interrupted(staged: true, pending: true, swapped: false);

        Assert.AreEqual(UpdateRecovery.RolledBack, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("1.0.0", VersionIn(box));
        Assert.AreEqual("1.0.0", _t.Store.Reload(box.Paths)!.Config.Version);
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task ACrashHalfwayThroughTheSwapBringsTheOldAppBack()
    {
        var (box, _) = await Interrupted(staged: true, pending: true, swapped: true);
        // Halfway: App went to App.old, App.new did not become App.
        Directory.Move(box.Paths.App, box.Paths.AppStaging);
        Assert.IsFalse(Directory.Exists(box.Paths.App));

        Assert.AreEqual(UpdateRecovery.RolledBack, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("1.0.0", VersionIn(box));
        Assert.AreEqual("1.0.0", _t.Store.Reload(box.Paths)!.Config.Version);
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task ACrashAfterTheSwapFinishesTheUpdate()
    {
        var (box, pending) = await Interrupted(staged: true, pending: true, swapped: true);

        Assert.AreEqual(UpdateRecovery.Finished, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("2.0.0", VersionIn(box));
        Assert.AreEqual(pending.Version, _t.Store.Reload(box.Paths)!.Config.Version);
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task AReplacedAppLeftBehindIsDeleted()
    {
        var box = await ImportedTool("1.0.0");
        Directory.CreateDirectory(box.Paths.AppPrevious);
        File.WriteAllText(Path.Combine(box.Paths.AppPrevious, "version.txt"), "0.9");

        Assert.AreEqual(UpdateRecovery.CleanedUp, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("1.0.0", VersionIn(box));
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task InterruptedUpdatesAreFoundAndSettledAtStartup()
    {
        var (box, _) = await Interrupted(staged: true, pending: true, swapped: true);
        await ImportedTool();

        var settled = await Manager().FinishInterruptedUpdatesAsync();

        Assert.AreEqual((box.Config.Id, UpdateRecovery.Finished), settled.Single());
        Assert.AreEqual(0, _t.Store.InterruptedUpdates().Count);
    }

    [TestMethod]
    public async Task ABoxIsStartedOnlyOnceAnInterruptedUpdateIsSettled()
    {
        var (box, _) = await Interrupted(staged: true, pending: true, swapped: true);
        await Manager().RegisterAsync(box);

        var run = Manager().RunAsync(_t.Store.Reload(box.Paths)!);
        await _runner.Started(box.Config.Id);
        _runner.Finish(box.Config.Id);
        await run;

        Assert.AreEqual("2.0.0", _runner.StartedWith.Single().Config.Version);
        AssertNothingLeftOver(box);
    }

    // Backups: the box as it was before an update, and going back to it.

    private static string Settings(LoadedBox box) => File.ReadAllText(Path.Combine(box.Paths.Roaming, "Tool", "settings.json"));

    private static void WriteSettings(LoadedBox box, string json) => File.WriteAllText(Path.Combine(box.Paths.Roaming, "Tool", "settings.json"), json);

    private static string VersionInBackup(LoadedBox box) => File.ReadAllText(Path.Combine(box.Paths.Backup, "App", "version.txt"));

    private async Task<LoadedBox> UpdatedTool(string from, string to)
    {
        var box = await ImportedTool(from);
        var updated = await Manager().UpdateAsync(box, Installer(to));
        Assert.IsNull(updated.BackupSkipped);
        return updated.Box;
    }

    [TestMethod]
    public async Task AnUpdateKeepsTheBoxAsItWasToGoBackTo()
    {
        var box = await ImportedTool("1.0.0");

        var updated = await Manager().UpdateAsync(box, Installer("2.0.0"));

        Assert.IsNull(updated.BackupSkipped);
        var backup = BoxStore.ReadBackup(box.Paths)!;
        Assert.AreEqual("1.0.0", backup.Config.Version);
        Assert.AreEqual(updated.Box.Config.UpdatedAt, backup.MadeAt);
        Assert.IsTrue(backup.Bytes > 0);
        Assert.AreEqual("1.0.0", VersionInBackup(box));
        Assert.AreEqual("{\"signedIn\":true}", File.ReadAllText(Path.Combine(box.Paths.Backup, "Data", "Profile", "AppData", "Roaming", "Tool", "settings.json")));
        Assert.AreEqual("state", File.ReadAllText(Path.Combine(box.Paths.Backup, "State", "LocalAppData", "cache.db")));
        CollectionAssert.AreEqual(new[] { "Tool_setup_1.0.0.exe" }, Directory.GetFiles(Path.Combine(box.Paths.Backup, "Source")).Select(Path.GetFileName).ToArray());
        Assert.AreEqual("2.0.0", VersionIn(updated.Box));
        AssertDataKept(updated.Box);
        AssertNothingLeftOver(updated.Box);
    }

    [TestMethod]
    public async Task GoingBackSwapsTheBoxWithItsBackup()
    {
        var updated = await UpdatedTool("1.0.0", "2.0.0");
        WriteSettings(updated, "{\"after\":true}");
        _t.Store.Save(updated with { Config = updated.Config with { BlockNetwork = true } });

        var back = await Manager().GoBackAsync(_t.Store.Reload(updated.Paths)!);

        Assert.AreEqual("1.0.0", back.Config.Version);
        Assert.AreEqual("1.0.0", _t.Store.Reload(back.Paths)!.Config.Version);
        Assert.AreEqual("1.0.0", VersionIn(back));
        Assert.AreEqual("{\"signedIn\":true}", Settings(back));
        Assert.AreEqual(true, back.Config.BlockNetwork);
        CollectionAssert.AreEqual(new[] { "Tool_setup_1.0.0.exe" }, Directory.GetFiles(back.Paths.Source).Select(Path.GetFileName).ToArray());
        // What the box had becomes the backup, so nothing is lost: going back once more returns to it.
        Assert.AreEqual("2.0.0", BoxStore.ReadBackup(back.Paths)!.Config.Version);
        Assert.AreEqual("2.0.0", VersionInBackup(back));
        AssertNothingLeftOver(back);

        var again = await Manager().GoBackAsync(back);

        Assert.AreEqual("2.0.0", again.Config.Version);
        Assert.AreEqual("2.0.0", VersionIn(again));
        Assert.AreEqual("{\"after\":true}", Settings(again));
        Assert.AreEqual("1.0.0", BoxStore.ReadBackup(again.Paths)!.Config.Version);
        AssertNothingLeftOver(again);
    }

    [TestMethod]
    public async Task ABackupLeavesOutLinksAndRebuiltCaches()
    {
        var box = await ImportedTool("1.0.0");
        var outside = Directory.CreateDirectory(Path.Combine(_t.Root, "Documents")).FullName;
        File.WriteAllText(Path.Combine(outside, "mine.txt"), "the user's");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(box.Paths.Profile, "Documents"), outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"symbolic links need Developer Mode here: {e.Message}");
        }
        var cache = Directory.CreateDirectory(Path.Combine(box.Paths.Roaming, "Tool", "GPUCache")).FullName;
        File.WriteAllText(Path.Combine(cache, "data_0"), "rebuilt");

        var updated = (await Manager().UpdateAsync(box, Installer("2.0.0"))).Box;

        var data = Path.Combine(box.Paths.Backup, "Data", "Profile");
        Assert.IsFalse(Directory.Exists(Path.Combine(data, "Documents")));
        Assert.IsFalse(Directory.Exists(Path.Combine(data, "AppData", "Roaming", "Tool", "GPUCache")));
        Assert.IsTrue(File.Exists(Path.Combine(data, "AppData", "Roaming", "Tool", "settings.json")));

        // The box's own data, links and all, goes into the backup when the box goes back; deleting the backup then
        // deletes the link, never what it leads to.
        var back = await Manager().GoBackAsync(updated);
        Assert.IsNotNull(new DirectoryInfo(Path.Combine(back.Paths.Backup, "Data", "Profile", "Documents")).LinkTarget);
        await Manager().DeleteBackupAsync(back);

        Assert.IsFalse(Directory.Exists(back.Paths.Backup));
        Assert.IsNull(BoxStore.ReadBackup(back.Paths));
        Assert.AreEqual("the user's", File.ReadAllText(Path.Combine(outside, "mine.txt")));
    }

    [TestMethod]
    public async Task AnUpdateWithoutRoomForTheDataKeepsNoBackup()
    {
        var updated = await UpdatedTool("1.0.0", "2.0.0");
        _t.FreeSpace = _ => BoxStore.BackupHeadroom;

        var result = await Manager().UpdateAsync(updated, Installer("3.0.0"));

        Assert.AreEqual(BackupSkip.NoSpace, result.BackupSkipped);
        Assert.AreEqual("3.0.0", VersionIn(result.Box));
        AssertDataKept(result.Box);
        // The backup before is gone too: it was no longer the box as it was before this update.
        Assert.IsFalse(Directory.Exists(updated.Paths.Backup));
        AssertNothingLeftOver(result.Box);
    }

    [TestMethod]
    public async Task TheBackupBeforeMakesRoomForTheNewOne()
    {
        var updated = await UpdatedTool("1.0.0", "2.0.0");
        _t.FreeSpace = _ => Directory.Exists(updated.Paths.Backup) ? 0 : 1L << 50;

        var result = await Manager().UpdateAsync(updated, Installer("3.0.0"));

        Assert.IsNull(result.BackupSkipped);
        Assert.AreEqual("2.0.0", BoxStore.ReadBackup(updated.Paths)!.Config.Version);
        Assert.AreEqual("2.0.0", VersionInBackup(updated));
        AssertNothingLeftOver(result.Box);
    }

    [TestMethod]
    public async Task GoingBackNeedsABackupAndABoxThatDoesNotRun()
    {
        var box = await ImportedTool("1.0.0");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Manager().GoBackAsync(box));

        var updated = (await Manager().UpdateAsync(box, Installer("2.0.0"))).Box;
        _runner.Running.Add(updated.Config.Id);
        await Assert.ThrowsExactlyAsync<BoxBusyException>(() => Manager().GoBackAsync(updated));

        Assert.AreEqual("2.0.0", VersionIn(updated));
        Assert.AreEqual("1.0.0", VersionInBackup(updated));
    }

    [TestMethod]
    public async Task ARunningBoxIsQuitBeforeGoingBackWhenAsked()
    {
        var updated = await UpdatedTool("1.0.0", "2.0.0");
        _runner.Running.Add(updated.Config.Id);
        _runner.StopsWhenAsked = true;

        var back = await Manager(stopWait: TimeSpan.FromSeconds(5)).GoBackAsync(updated, quitFirst: true);

        CollectionAssert.Contains(_runner.StopRequests, updated.Config.Id);
        Assert.AreEqual("1.0.0", VersionIn(back));
    }

    // A crash while going back, and what the next start makes of it.

    private async Task<LoadedBox> StartedGoingBack()
    {
        var updated = await UpdatedTool("1.0.0", "2.0.0");
        var paths = updated.Paths;
        Directory.CreateDirectory(paths.LocalAppDataState);
        Directory.CreateDirectory(paths.BackupSwap);
        JsonFile.Write(Path.Combine(paths.BackupSwap, "backup.json"), new BoxBackup { Config = updated.Config, MadeAt = DateTimeOffset.Now },
            WinBoxJson.Files.BoxBackup);
        var backup = BoxStore.ReadBackup(paths)!;
        JsonFile.Write(paths.GoingBackMarker, BoxUpdate.Apply(updated.Config, backup.Config, DateTimeOffset.Now), WinBoxJson.Files.BoxConfig);
        return updated;
    }

    private void AssertWentBack(LoadedBox box)
    {
        Assert.AreEqual(UpdateRecovery.WentBack, BoxStore.FinishInterruptedUpdate(box.Paths));
        Assert.AreEqual("1.0.0", VersionIn(box));
        Assert.AreEqual("1.0.0", _t.Store.Reload(box.Paths)!.Config.Version);
        Assert.AreEqual("{\"signedIn\":true}", Settings(box));
        Assert.AreEqual("2.0.0", BoxStore.ReadBackup(box.Paths)!.Config.Version);
        Assert.AreEqual("2.0.0", VersionInBackup(box));
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task ACrashHalfwayThroughAPartFinishesGoingBack()
    {
        var box = await StartedGoingBack();
        WriteSettings(box, "{\"after\":true}");
        Directory.Move(box.Paths.App, Path.Combine(box.Paths.BackupSwap, "App"));

        AssertWentBack(box);
    }

    [TestMethod]
    public async Task ACrashAfterSomePartsFinishesGoingBack()
    {
        var box = await StartedGoingBack();
        foreach (var item in new[] { "App", "Data" })
        {
            Directory.Move(Path.Combine(box.Paths.Root, item), Path.Combine(box.Paths.BackupSwap, item));
            Directory.Move(Path.Combine(box.Paths.Backup, item), Path.Combine(box.Paths.Root, item));
        }

        AssertWentBack(box);
    }

    [TestMethod]
    public async Task ACrashAfterEveryPartMovedFinishesGoingBack()
    {
        var box = await StartedGoingBack();
        foreach (var item in BoxPaths.BackedUpItems)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(box.Paths.BackupSwap, item))!);
            Directory.Move(Path.Combine(box.Paths.Root, item), Path.Combine(box.Paths.BackupSwap, item));
            Directory.Move(Path.Combine(box.Paths.Backup, item), Path.Combine(box.Paths.Root, item));
        }
        BoxStore.DeleteBackup(box.Paths);

        AssertWentBack(box);
    }

    [TestMethod]
    public async Task ACrashBeforeGoingBackBeganChangesNothing()
    {
        var box = await StartedGoingBack();
        File.Delete(box.Paths.GoingBackMarker);

        Assert.AreEqual(UpdateRecovery.None, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("2.0.0", VersionIn(box));
        Assert.AreEqual("1.0.0", VersionInBackup(box));
        AssertNothingLeftOver(box);
    }

    // A crash during an update that was making a backup.

    private static void StageBackup(LoadedBox box)
    {
        var staging = box.Paths.BackupStaging;
        foreach (var item in BoxPaths.BackedUpItems.Where(i => i != "App"))
            Directory.CreateDirectory(Path.Combine(staging, item));
        JsonFile.Write(Path.Combine(staging, "backup.json"), new BoxBackup { Config = box.Config, MadeAt = DateTimeOffset.Now }, WinBoxJson.Files.BoxBackup);
    }

    [TestMethod]
    public async Task ACrashBeforeTheSwapDropsTheBackupItWasMaking()
    {
        var (box, _) = await Interrupted(staged: true, pending: true, swapped: false);
        StageBackup(box);

        Assert.AreEqual(UpdateRecovery.RolledBack, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("1.0.0", VersionIn(box));
        Assert.IsNull(BoxStore.ReadBackup(box.Paths));
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task ACrashAfterTheSwapKeepsTheBackup()
    {
        var (box, pending) = await Interrupted(staged: true, pending: true, swapped: true);
        StageBackup(box);

        Assert.AreEqual(UpdateRecovery.Finished, BoxStore.FinishInterruptedUpdate(box.Paths));

        Assert.AreEqual("2.0.0", VersionIn(box));
        Assert.AreEqual(pending.Version, _t.Store.Reload(box.Paths)!.Config.Version);
        Assert.AreEqual("1.0.0", BoxStore.ReadBackup(box.Paths)!.Config.Version);
        Assert.AreEqual("1.0.0", VersionInBackup(box));
        AssertNothingLeftOver(box);
    }

    [TestMethod]
    public async Task ABackupMadeForAnUpdateThatNeverBeganIsDropped()
    {
        var box = await ImportedTool("1.0.0");
        StageBackup(box);

        BoxStore.FinishInterruptedUpdate(box.Paths);

        Assert.IsNull(BoxStore.ReadBackup(box.Paths));
        AssertNothingLeftOver(box);
    }
}
