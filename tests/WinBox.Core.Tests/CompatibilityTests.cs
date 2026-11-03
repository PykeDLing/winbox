using WinBox.Core.Boxes;
using WinBox.Core.Import;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class PeFileTests
{
    private string _dir = "";

    [TestInitialize]
    public void Create() => _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Delete() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] image) => TestPe.Write(Path.Combine(_dir, name), image);

    [TestMethod]
    public void ImportsAreReadFromBothTables()
    {
        var exe = Write("app.exe", TestPe.Build(
            imports: [("KERNEL32.dll", ["CreateFileW", "ExitProcess"]), ("ADVAPI32.dll", ["RegOpenKeyExW"])],
            delayed: [("SHELL32.dll", ["ShellExecuteW"])]));

        CollectionAssert.AreEquivalent(
            new[] { "CreateFileW", "ExitProcess", "RegOpenKeyExW", "ShellExecuteW" },
            PeFile.ReadImportedFunctions(exe).ToArray());
    }

    [TestMethod]
    public void TheEmbeddedManifestIsRead()
    {
        var manifest = TestPe.ManifestFor("requireAdministrator");
        Assert.AreEqual(manifest, PeFile.ReadManifest(Write("app.exe", TestPe.Build(manifest: manifest))));
        Assert.IsNull(PeFile.ReadManifest(Write("bare.exe", TestPe.Build())));
    }

    [TestMethod]
    public void WhatIsNotAProgramHasNothingToRead()
    {
        var text = Path.Combine(_dir, "notes.sys");
        File.WriteAllText(text, "MZ but nothing after it");

        Assert.AreEqual(0, PeFile.ReadImportedFunctions(text).Count);
        Assert.IsNull(PeFile.ReadManifest(text));
        Assert.IsFalse(PeFile.IsNative(text));
    }

    [TestMethod]
    public void ADriverRunsInTheNativeSubsystem()
    {
        Assert.IsTrue(PeFile.IsNative(Write("driver.sys", TestPe.Build(TestPe.Native))));
        Assert.IsFalse(PeFile.IsNative(Write("app.exe", TestPe.Build(TestPe.Gui))));
    }
}

[TestClass]
public sealed class CompatibilityCheckTests
{
    private const string Tencent = "Tencent Technology (Shenzhen) Company Limited";

    private string _dir = "";

    [TestInitialize]
    public void Create() => _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Delete() => Directory.Delete(_dir, recursive: true);

    private static InstallerFacts Installer(string fileName, string? product, string? signer) => new()
    {
        Path = Path.Combine(@"C:\Downloads", fileName),
        Size = 1,
        Kind = InstallerKind.Nsis,
        Executable = new ExecutableInfo(null, product + "安装程序", product, null, null),
        Signature = new SignatureInfo(true, signer is null ? null : $"CN={signer}, O={signer}, C=CN"),
    };

    [TestMethod]
    public void WeChatsInstallerIsKnownBeforeItIsUnpacked()
    {
        var issue = CompatibilityCheck.Known(Installer("WeChatWin_4.2.0.exe", "微信电脑版", Tencent));

        Assert.AreEqual(new CompatibilityIssue(IssueCode.KnownIncompatible, IssueSeverity.NeedsVirtualMachine, "wechat"), issue);
    }

    [TestMethod]
    [DataRow("WXWork_4.1.exe", "企业微信", Tencent)]
    [DataRow("QQ_9.9.exe", "QQ", Tencent)]
    [DataRow("WeChatWin_4.2.0.exe", "微信电脑版", "Someone Else Ltd")]
    [DataRow("WeChatWin_4.2.0.exe", "微信电脑版", null)]
    public void OnlyTheMeasuredAppFromItsOwnPublisherIsKnown(string fileName, string product, string? signer) =>
        Assert.IsNull(CompatibilityCheck.Known(Installer(fileName, product, signer)));

    [TestMethod]
    public void TheProgramItselfIsKnownByItsVersionInformation()
    {
        var info = new ExecutableInfo(null, "Weixin", "Weixin", null, null);

        Assert.AreEqual("wechat", CompatibilityCheck.Known("Weixin.exe", info, () => Tencent)?.Detail);
    }

    [TestMethod]
    public void TheSignerIsOnlyReadWhenANameMatches()
    {
        var read = false;
        var issue = CompatibilityCheck.Known("Obsidian.exe", new ExecutableInfo(null, "Obsidian", "Obsidian", null, null), () =>
        {
            read = true;
            return "Dynalist Inc";
        });

        Assert.IsNull(issue);
        Assert.IsFalse(read);
    }

    [TestMethod]
    [DataRow("requireAdministrator", false, true)]
    [DataRow("requireAdministrator", true, true)]
    [DataRow("highestAvailable", true, true)]
    [DataRow("HighestAvailable", true, true)]
    [DataRow("highestAvailable", false, false)]
    [DataRow("asInvoker", true, false)]
    public void AnEntryThatWantsAdministratorRightsCannotRun(string level, bool administratorAccount, bool blocked)
    {
        var entry = TestPe.Write(Path.Combine(_dir, "app.exe"), TestPe.Build(manifest: TestPe.ManifestFor(level)));

        var issue = CompatibilityCheck.CheckEntry(entry, administratorAccount);

        // The detail is the level as Windows spells it, whatever the manifest's case.
        var expected = level.Equals("highestAvailable", StringComparison.OrdinalIgnoreCase) ? CompatibilityCheck.HighestAvailable : level;
        Assert.AreEqual(blocked ? new CompatibilityIssue(IssueCode.NeedsAdministrator, IssueSeverity.NeedsVirtualMachine, expected) : null, issue);
    }

    [TestMethod]
    public void AManifestBesideTheProgramCountsWhenNoneIsBuiltIn()
    {
        var entry = TestPe.Write(Path.Combine(_dir, "app.exe"), TestPe.Build());
        File.WriteAllText(entry + ".manifest", TestPe.ManifestFor("requireAdministrator"));

        Assert.AreEqual(IssueCode.NeedsAdministrator, CompatibilityCheck.CheckEntry(entry, administratorAccount: false)?.Code);
    }

    [TestMethod]
    public void LevelsInCommentsDoNotCount()
    {
        // Visual Studio's app.manifest template lists every level in a comment.
        const string manifest = """
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
                <security>
                  <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                    <!-- <requestedExecutionLevel level="requireAdministrator" uiAccess="false" /> -->
                    <requestedExecutionLevel level="asInvoker" uiAccess="false" />
                  </requestedPrivileges>
                </security>
              </trustInfo>
            </assembly>
            """;

        Assert.AreEqual("asInvoker", CompatibilityCheck.ExecutionLevel(manifest));
        Assert.IsNull(CompatibilityCheck.ExecutionLevel("<assembly><notclosed>"));
    }

    [TestMethod]
    public void DriversAndServicesAnywhereInThePayloadAreFound()
    {
        TestPe.Write(Path.Combine(_dir, "app", "app.exe"), TestPe.Build(imports: [("KERNEL32.dll", ["ExitProcess"])]));
        TestPe.Write(Path.Combine(_dir, "app", "drivers", "WinRing0x64.sys"), TestPe.Build(TestPe.Native));
        TestPe.Write(Path.Combine(_dir, "{sys}", "drivers", "vpn.sys"), TestPe.Build(TestPe.Native));
        TestPe.Write(Path.Combine(_dir, "app", "SyncService.exe"), TestPe.Build(TestPe.Console, imports: [("ADVAPI32.dll", ["StartServiceCtrlDispatcherW"])]));
        TestPe.Write(Path.Combine(_dir, "app", "Agent.exe"), TestPe.Build(delayed: [("api-ms-win-service-core-l1-1-0.dll", ["StartServiceCtrlDispatcherW"])]));
        // A .sys that is no driver (a data file, or a program misnamed) and a program that only manages services.
        File.WriteAllText(Path.Combine(_dir, "app", "config.sys"), "not a driver");
        TestPe.Write(Path.Combine(_dir, "app", "panel.sys"), TestPe.Build(TestPe.Gui));
        TestPe.Write(Path.Combine(_dir, "app", "control.exe"), TestPe.Build(imports: [("ADVAPI32.dll", ["OpenSCManagerW", "StartServiceW"])]));

        var issues = CompatibilityCheck.ScanFiles(_dir);

        CollectionAssert.AreEqual(new[]
        {
            new CompatibilityIssue(IssueCode.DriverUnavailable, IssueSeverity.Limited, "vpn.sys;WinRing0x64.sys"),
            new CompatibilityIssue(IssueCode.ServiceUnavailable, IssueSeverity.Limited, "Agent.exe;SyncService.exe"),
        }, issues.ToArray());
    }

    [TestMethod]
    public void APlainAppCarriesNothing()
    {
        TestPe.Write(Path.Combine(_dir, "app", "app.exe"), TestPe.Build(imports: [("KERNEL32.dll", ["ExitProcess"])]));

        Assert.AreEqual(0, CompatibilityCheck.ScanFiles(_dir).Count);
    }
}

[TestClass]
public sealed class ImportCompatibilityTests
{
    private const string Tencent = "Tencent Technology (Shenzhen) Company Limited";

    private TestBox _box = null!;
    private string _downloads = "";

    [TestInitialize]
    public void Create()
    {
        _box = new TestBox();
        _downloads = Directory.CreateDirectory(Path.Combine(_box.Root, "dropped")).FullName;
    }

    [TestCleanup]
    public void Delete() => _box.Dispose();

    /// <summary>Version information and signers by file name; everything else is unsigned and says nothing.</summary>
    private sealed class Inspector(Dictionary<string, (ExecutableInfo? Info, string? Signer)> files) : IFileInspector
    {
        public ExecutableInfo? ReadExecutableInfo(string path) => files.GetValueOrDefault(Path.GetFileName(path)).Info;

        public SignatureInfo ReadSignature(string path) =>
            files.GetValueOrDefault(Path.GetFileName(path)).Signer is { } signer ? new(true, $"CN={signer}") : new(false, null);
    }

    /// <summary>Takes any installer and "unpacks" it by writing the given files.</summary>
    private sealed class Extractor(Action<string> unpack) : IPackageExtractor
    {
        public int Extracted { get; private set; }

        public InstallerKind? Recognize(InstallerFacts facts) => InstallerKind.Nsis;

        public Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default)
        {
            Extracted++;
            var app = Path.Combine(destination, "app");
            Directory.CreateDirectory(app);
            unpack(destination);
            return Task.FromResult(new ExtractionResult(app, []));
        }
    }

    /// <summary>Records what it is asked and answers <paramref name="answer"/>.</summary>
    private sealed class Asker(bool answer)
    {
        public List<IReadOnlyList<CompatibilityIssue>> Asked { get; } = [];

        public Task<bool> Confirm(IReadOnlyList<CompatibilityIssue> blockers, CancellationToken cancellationToken)
        {
            Asked.Add(blockers);
            return Task.FromResult(answer);
        }
    }

    private ImportPipeline Pipeline(IPackageExtractor extractor, Dictionary<string, (ExecutableInfo?, string?)>? files = null, bool administratorAccount = false) =>
        new(_box.Paths, _box.Store, new Inspector(files ?? []), [extractor], new BoxManagerTests.NoDownloader(), [], administratorAccount);

    private string Dropped(string fileName) => TestPe.Write(Path.Combine(_downloads, fileName), TestPe.Build());

    private static Action<string> App(params (string Path, byte[] Image)[] files) => destination =>
    {
        foreach (var (path, image) in files)
            TestPe.Write(Path.Combine(destination, "app", path), image);
    };

    private static readonly Dictionary<string, (ExecutableInfo?, string?)> WeChat = new()
    {
        ["WeChatWin_4.2.0.exe"] = (new ExecutableInfo(null, "微信电脑版安装程序", "微信电脑版", null, null), Tencent),
        ["Weixin.exe"] = (new ExecutableInfo(null, "Weixin", "Weixin", "Tencent", "4.2.0.12"), Tencent),
    };

    [TestMethod]
    public async Task AKnownAppIsAskedAboutBeforeAnythingIsUnpacked()
    {
        var extractor = new Extractor(App(("Weixin.exe", TestPe.Build())));
        var asker = new Asker(answer: false);

        var declined = await Assert.ThrowsExactlyAsync<ImportException>(() =>
            Pipeline(extractor, WeChat).ImportAsync(Dropped("WeChatWin_4.2.0.exe"), confirm: asker.Confirm));

        Assert.AreEqual(ImportFailure.Declined, declined.Failure);
        Assert.AreEqual(0, extractor.Extracted);
        Assert.AreEqual("wechat", asker.Asked.Single().Single().Detail);
        Assert.AreEqual(0, _box.Store.Load().Count);
    }

    [TestMethod]
    public async Task AddedAllTheSameItIsAskedOnceAndTheBoxKeepsTheReason()
    {
        var asker = new Asker(answer: true);

        // The entry, Weixin.exe, is known too: that is what was asked already.
        var box = await Pipeline(new Extractor(App(("Weixin.exe", TestPe.Build()))), WeChat)
            .ImportAsync(Dropped("WeChatWin_4.2.0.exe"), confirm: asker.Confirm);

        Assert.AreEqual(1, asker.Asked.Count);
        Assert.AreEqual("Weixin.exe", box.Config.Entry);
        Assert.AreEqual(1, box.Config.Issues.Count(i => i.Code == IssueCode.KnownIncompatible));
        Assert.AreEqual(IssueSeverity.NeedsVirtualMachine, box.Config.Issues[0].Severity);
    }

    [TestMethod]
    public async Task AnEntryThatWantsAdministratorRightsIsAskedAboutAfterUnpacking()
    {
        var extractor = new Extractor(App(("tool.exe", TestPe.Build(manifest: TestPe.ManifestFor("requireAdministrator")))));
        var asker = new Asker(answer: false);

        var declined = await Assert.ThrowsExactlyAsync<ImportException>(() =>
            Pipeline(extractor).ImportAsync(Dropped("tool_1.0.exe"), confirm: asker.Confirm));

        Assert.AreEqual(ImportFailure.Declined, declined.Failure);
        Assert.AreEqual(IssueCode.NeedsAdministrator, asker.Asked.Single().Single().Code);
        // The half-made box is gone.
        Assert.AreEqual(0, _box.Store.Load().Count);
        Assert.AreEqual(0, Directory.GetDirectories(_box.Paths.Boxes).Length);
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 0)]
    public async Task AnEntryThatWantsTheHighestRightsIsAskedAboutOnAnAdministratorAccount(bool administratorAccount, int asked)
    {
        var extractor = new Extractor(App(("tool.exe", TestPe.Build(manifest: TestPe.ManifestFor("highestAvailable")))));
        var asker = new Asker(answer: true);

        var box = await Pipeline(extractor, administratorAccount: administratorAccount).ImportAsync(Dropped("tool_1.0.exe"), confirm: asker.Confirm);

        Assert.AreEqual(asked, asker.Asked.Count);
        Assert.AreEqual(asked, box.Config.Issues.Count(i => i is { Code: IssueCode.NeedsAdministrator, Detail: CompatibilityCheck.HighestAvailable }));
    }

    [TestMethod]
    public async Task DriversAndServicesAreNotedWithoutAsking()
    {
        var extractor = new Extractor(App(
            ("tool.exe", TestPe.Build()),
            ("ToolService.exe", TestPe.Build(TestPe.Console, imports: [("ADVAPI32.dll", ["StartServiceCtrlDispatcherW"])])),
            (@"driver\tool.sys", TestPe.Build(TestPe.Native))));
        var asker = new Asker(answer: false);

        var box = await Pipeline(extractor).ImportAsync(Dropped("tool_1.0.exe"), confirm: asker.Confirm);

        Assert.AreEqual(0, asker.Asked.Count);
        Assert.AreEqual("tool.exe", box.Config.Entry);
        CollectionAssert.IsSubsetOf(new[]
        {
            new CompatibilityIssue(IssueCode.DriverUnavailable, IssueSeverity.Limited, "tool.sys"),
            new CompatibilityIssue(IssueCode.ServiceUnavailable, IssueSeverity.Limited, "ToolService.exe"),
        }, box.Config.Issues.ToArray());
    }

    [TestMethod]
    public async Task WithNobodyToAskTheImportGoesOnAndKeepsTheReason()
    {
        var box = await Pipeline(new Extractor(App(("Weixin.exe", TestPe.Build()))), WeChat)
            .ImportAsync(Dropped("WeChatWin_4.2.0.exe"));

        Assert.AreEqual(IssueCode.KnownIncompatible, box.Config.Issues[0].Code);
    }
}
