using WinBox.Core.Boxes;
using WinBox.Core.Import;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class InstallerLayoutTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private string Put(string relative, long size = 1, bool program = false)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        if (program)
            file.Write(MinimalPe(gui: true));
        file.SetLength(Math.Max(size, file.Length));
        return path;
    }

    /// <summary>The smallest header <see cref="PeFile"/> reads: MZ, the PE offset, PE\0\0, then the subsystem.</summary>
    private static byte[] MinimalPe(bool gui)
    {
        var bytes = new byte[0x200];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BitConverter.GetBytes((ushort)(gui ? 2 : 3)).CopyTo(bytes, 0x80 + 4 + 20 + 68);
        return bytes;
    }

    private const string VsCodeScript = """
        [Setup]
        AppName=Visual Studio Code
        UninstallDisplayIcon={app}\Code.exe

        [Icons]
        Name: "{group}\Visual Studio Code"; Filename: "{app}\Code.exe"; Check: "ShouldUpdateShortcut(ExpandConstant('{group}\Visual Studio Code.lnk'))";
        Name: "{group}\Uninstall"; Filename: "{uninstallexe}"
        Name: "{autodesktop}\Widget"; Filename: "{app}\{code:GetWidgetName}"

        [Run]
        Filename: "{app}\tools\first-run.exe"; Flags: runhidden
        Filename: "{app}\Code.exe"; Description: "Launch"; Flags: postinstall nowait
        """;

    [TestMethod]
    public void InnoScriptShortcutsNameTheEntry() =>
        CollectionAssert.AreEqual(new[] { "Code.exe" }, InstallerLayout.InnoEntryHints(VsCodeScript).ToList());

    [TestMethod]
    public void InnoComputedFoldersAreMergedIntoTheAppAndComputedNamesFollowTheShortcuts()
    {
        Put(@"{app}\tools\inno_updater.exe", program: true);
        Put(@"{code_GetDestDir}\{code_GetExeBasename}", program: true);
        Put(@"{code_GetDestDir}\261c84cb1e\resources.pak");
        Put(@"{tmp}\setup-helper.dll", program: true);
        Put(@"{sys}\driver.sys", program: true);
        Put(@"{code_GetDataDir}\notes.txt");
        File.WriteAllText(Path.Combine(_root, "install_script.iss"), VsCodeScript);

        var result = InstallerLayout.ArrangeInno(_root);

        Assert.IsTrue(File.Exists(Path.Combine(result.AppDirectory, "Code.exe")), "the computed program takes the shortcut's name");
        Assert.IsTrue(File.Exists(Path.Combine(result.AppDirectory, "261c84cb1e", "resources.pak")));
        Assert.IsTrue(File.Exists(Path.Combine(result.AppDirectory, "tools", "inno_updater.exe")));
        CollectionAssert.AreEquivalent(new[] { "{sys}", "{code_GetDataDir}" }, result.SkippedRoots.ToList());
        CollectionAssert.AreEqual(new[] { "Code.exe" }, result.EntryHints!.ToList());
    }

    [TestMethod]
    [DataRow("code$GetAppExeDestName", "GetAppExeDestName")]
    [DataRow("{code_GetAppExeDestName}", "GetAppExeDestName")]
    [DataRow("quark.exe", null)]
    [DataRow("{app}", null)]
    public void PlaceholderNamesOfBothUnpackersAreRecognized(string name, string? function) =>
        Assert.AreEqual(function, InstallerLayout.PlaceholderName(name));

    [TestMethod]
    public void ThePayloadForX64IsTakenAndTheOthersGo()
    {
        const long big = 5 * 1024 * 1024;
        var x64 = Put(@"$PLUGINSDIR\app-64.7z", big);
        var x86 = Put(@"$PLUGINSDIR\app-32.7z", big);
        var arm = Put(@"$PLUGINSDIR\app-arm64.7z", big);
        Put(@"$PLUGINSDIR\skin.zip", 1000);

        var (unpack, remove) = InstallerLayout.PayloadArchives(_root);

        CollectionAssert.AreEqual(new[] { x64 }, unpack.ToList());
        CollectionAssert.AreEquivalent(new[] { x64, x86, arm }, remove.ToList());
    }

    [TestMethod]
    public void APayloadAtTheTopIsTaken()
    {
        var install = Put("install.7z", 5 * 1024 * 1024);

        CollectionAssert.AreEqual(new[] { install }, InstallerLayout.PayloadArchives(_root).Unpack.ToList());
    }

    [TestMethod]
    [DataRow("$PLUGINSDIR", true)]
    [DataRow("$R0", true)]
    [DataRow("$0", true)]
    [DataRow("$TEMP", true)]
    [DataRow("$SYSDIR", false)]
    [DataRow("$APPDATA", false)]
    public void NsisInternalFoldersAreTold(string name, bool internalOnly) =>
        Assert.AreEqual(internalOnly, InstallerLayout.IsNsisInternal(name));

    [TestMethod]
    public void SquirrelPackagesKeepTheAppInLibNet45()
    {
        Put("RELEASES");
        var package = Put("Discord-1.0.9266-full.nupkg");
        Put(@"unpacked\lib\net45\Discord.exe", program: true);

        Assert.AreEqual(package, InstallerLayout.SquirrelPackage(_root));
        Assert.AreEqual(Path.Combine(_root, "unpacked", "lib", "net45"), InstallerLayout.SquirrelApp(Path.Combine(_root, "unpacked")));
    }

    [TestMethod]
    public void ProgramHeadersAreRead()
    {
        var gui = Put("gui.exe", program: true);
        var console = Path.Combine(_root, "console.exe");
        File.WriteAllBytes(console, MinimalPe(gui: false));
        var text = Put("readme.exe", 10);

        Assert.IsTrue(PeFile.IsGui(gui));
        Assert.IsFalse(PeFile.IsGui(console));
        Assert.IsNull(PeFile.IsGui(text));
        Assert.IsFalse(PeFile.IsExecutable(text));
    }

    [TestMethod]
    public void ElectronAndCefProgramsAreRecognized()
    {
        var electron = Put(@"electron\App.exe", program: true);
        Put(@"electron\resources\app.asar");
        var versioned = Put(@"vscode\Code.exe", program: true);
        Put(@"vscode\261c84cb1e\icudtl.dat");
        Put(@"vscode\261c84cb1e\resources.pak");
        var cef = Put(@"cef\Netdisk.exe", program: true);
        Put(@"cef\libcef.dll");
        var plain = Put(@"plain\Tool.exe", program: true);

        Assert.IsTrue(ChromiumApp.IsMainProgram(electron));
        Assert.IsTrue(ChromiumApp.IsMainProgram(versioned));
        Assert.IsTrue(ChromiumApp.IsMainProgram(cef));
        Assert.IsFalse(ChromiumApp.IsMainProgram(plain));
    }
}

[TestClass]
public sealed class EntryScoringTests
{
    private static ExecutableCandidate C(string path, long size = 100, bool? gui = true, bool chromium = false) => new(path, size, null, gui, chromium);

    private static InstallerFacts Facts(string fileName, string? product = null) => new()
    {
        Path = Path.Combine(@"C:\Downloads", fileName),
        Size = 1,
        Kind = InstallerKind.Nsis,
        Executable = new ExecutableInfo(null, null, product, null, null),
    };

    [TestMethod]
    [DataRow("BaiduNetdisk_8.9.7.101_x64.exe", "Baidu Netdisk")]
    [DataRow("DingTalk_v8.2.0.260119001.exe", "Ding Talk")]
    [DataRow("DiscordSetup.exe", "Discord")]
    [DataRow("Obsidian-1.15.0.exe", "Obsidian")]
    [DataRow("VSCodeUserSetup-x64-1.144.1.exe", "VSCode")]
    public void InstallerFileNamesLoseTheirVersionAndInstallerWords(string fileName, string name) =>
        CollectionAssert.Contains(EntryResolver.NamesOf(Facts(fileName)).ToList(), name);

    [TestMethod]
    public void TheProgramCarryingTheAppsNameWinsOverBiggerOnes()
    {
        List<ExecutableCandidate> candidates =
        [
            C(@"build\dist\AlibabaProtectCon.exe", 9_000_000),
            C(@"build\dist\dingpan_sync.exe", 8_000_000),
            C(@"build\dist\DingTalk.exe", 1_000_000),
            C("uninst.exe", 5_000_000),
        ];

        var entry = EntryResolver.Resolve(candidates, names: EntryResolver.NamesOf(Facts("DingTalk_v8.2.0.260119001.exe")));

        Assert.AreEqual(@"build\dist\DingTalk.exe", entry!.RelativePath);
    }

    [TestMethod]
    public void TheMainProgramAtTheTopBeatsAHelperBelowItThatSharesTheProductName()
    {
        // WeChat 4: the helper's version information says "WeChat", which the installer's file name also gives.
        List<ExecutableCandidate> candidates =
        [
            new("Weixin.exe", 3_127_848, new ExecutableInfo(null, "Weixin", "Weixin", null, null), Gui: true),
            new(@"4.2.0.12\WeixinExt.exe", 251_432, new ExecutableInfo(null, null, "WeChat", null, null), Gui: true, Chromium: true),
            C("Uninstall.exe", 999_999),
        ];

        var entry = EntryResolver.Resolve(candidates, names: EntryResolver.NamesOf(Facts("WeChatWin_4.2.0.exe", "微信电脑版")));

        Assert.AreEqual("Weixin.exe", entry!.RelativePath);
    }

    [TestMethod]
    public void TheInstallersOwnShortcutWins() =>
        Assert.AreEqual(@"bin\app.exe", EntryResolver.Resolve([C("launcher.exe", 999), C(@"bin\app.exe")], hints: [@"bin\app.exe"])!.RelativePath);

    [TestMethod]
    public void ProgramsWithWindowsBeatConsoleOnes() =>
        Assert.AreEqual("viewer.exe", EntryResolver.Resolve([C("cli.exe", 999, gui: false), C("viewer.exe", 10)])!.RelativePath);

    [TestMethod]
    public void ChromiumsMainProgramBeatsItsNeighbours() =>
        Assert.AreEqual("Slack.exe", EntryResolver.Resolve([C("tool.exe", 999), C("Slack.exe", 10, chromium: true)])!.RelativePath);

    [TestMethod]
    [DataRow("WeChatSetup.exe", null, true)]
    [DataRow("QQ_9.9.36_x64.exe", "QQ 安装程序", true)]
    [DataRow("putty.exe", "PuTTY suite", false)]
    [DataRow("Everything.exe", "Everything", false)]
    public void InstallersAreToldFromPortablePrograms(string fileName, string? product, bool installer) =>
        Assert.AreEqual(installer, PortableExtractor.LooksLikeInstaller(Facts(fileName, product) with { Kind = InstallerKind.PortableExe }));
}
