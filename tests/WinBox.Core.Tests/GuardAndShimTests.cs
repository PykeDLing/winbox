using WinBox.Core.Guard;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class ShimArgumentsTests
{
    [TestMethod]
    public void RoundTripsAppArgumentsThatLookLikeShimOptions()
    {
        var shim = new ShimArguments(@"C:\b\State\LocalAppData", ["Temp", "INetCache"], @"C:\b\App\app.exe", ["--state", "--", "x y"]);

        var parsed = ShimArguments.Parse(shim.ToArgs());

        Assert.AreEqual(shim.StateDirectory, parsed.StateDirectory);
        CollectionAssert.AreEqual(shim.Exclusions.ToList(), parsed.Exclusions.ToList());
        Assert.AreEqual(shim.Executable, parsed.Executable);
        CollectionAssert.AreEqual(shim.Arguments.ToList(), parsed.Arguments.ToList());
    }

    [TestMethod]
    public void MissingAppIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => ShimArguments.Parse(["--state", @"C:\s", "--"]));

    [TestMethod]
    public void ControlFolderRoundTrips()
    {
        var shim = new ShimArguments(@"C:\b\State\LocalAppData", [], @"C:\b\App\app.exe", ["--control"], @"C:\b\State\Control");

        var parsed = ShimArguments.Parse(shim.ToArgs());

        Assert.AreEqual(@"C:\b\State\Control", parsed.ControlDirectory);
        CollectionAssert.AreEqual(new[] { "--control" }, parsed.Arguments.ToList());
        Assert.IsNull(ShimArguments.Parse(["--state", @"C:\s", "--", "app.exe"]).ControlDirectory);
    }
}

[TestClass]
public sealed class RegFileTests
{
    [TestMethod]
    public void ValuesAreWrittenAsRegImportReadsThem()
    {
        Assert.AreEqual(@"""C:\\a\\app.exe.FriendlyAppName""=""Quark \""Drive\""""", RegFile.ValueLine(@"C:\a\app.exe.FriendlyAppName", 1, "Quark \"Drive\""));
        Assert.AreEqual(@"@=dword:000000ff", RegFile.ValueLine("", 4, 255));
        Assert.AreEqual(@"""t""=hex:01,ab", RegFile.ValueLine("t", 3, new byte[] { 0x01, 0xAB }));
        Assert.AreEqual(@"""p""=hex(2):25,00,41,00,25,00,00,00", RegFile.ValueLine("p", 2, "%A%"));
        Assert.AreEqual(@"""m""=hex(7):61,00,00,00,62,00,00,00,00,00", RegFile.ValueLine("m", 7, new[] { "a", "b" }));
        Assert.AreEqual(@"""q""=hex(b):01,00,00,00,00,00,00,00", RegFile.ValueLine("q", 11, 1L));
        Assert.AreEqual(@"""n""=hex(1):61,00,0a,00,00,00", RegFile.ValueLine("n", 1, "a\n"));
    }
}

[TestClass]
public sealed class ShimControlTests
{
    [TestMethod]
    public void RequestIsTakenOnceAndResetClearsTheRest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"), "Control");
        try
        {
            Assert.IsFalse(ShimControl.Take(dir, ShimControl.ShowRequest));
            ShimControl.Request(dir, ShimControl.ShowRequest);
            Assert.IsTrue(ShimControl.Take(dir, ShimControl.ShowRequest));
            Assert.IsFalse(ShimControl.Take(dir, ShimControl.ShowRequest));

            ShimControl.Request(dir, ShimControl.QuitRequest);
            ShimControl.WriteResult(dir, new ShimResult(false, "quit", "disk full", Terminated: true));
            Assert.AreEqual(new ShimResult(false, "quit", "disk full", true), ShimControl.ReadResult(dir));

            ShimControl.Reset(dir);
            Assert.IsFalse(ShimControl.Take(dir, ShimControl.QuitRequest));
            Assert.IsNull(ShimControl.ReadResult(dir));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [TestMethod]
    public void ResetWithoutAFolderDoesNothing() =>
        ShimControl.Reset(Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N")));
}

[TestClass]
public sealed class CommandLineTests
{
    [TestMethod]
    public void QuotesOnlyWhatNeedsQuoting() =>
        Assert.AreEqual(
            @"C:\a\app.exe plain ""with space"" ""dir with space\\"" ""quote\""inside"" """"",
            CommandLine.Build(@"C:\a\app.exe", ["plain", "with space", @"dir with space\", "quote\"inside", ""]));
}

[TestClass]
public sealed class HostReferenceRulesTests
{
    private const string Boxes = @"C:\WinBox\Data\Boxes";

    [TestMethod]
    [DataRow(@"""C:\WinBox\Data\Boxes\1.box\App\app.exe"" --launch-from=loginitem")]
    [DataRow(@"c:\winbox\data\boxes\1.box\app\app.exe")]
    [DataRow(@"C:\WinBox\Data\Boxes")]
    public void MentionsBoxPaths(string text) => Assert.IsTrue(HostReferenceRules.Mentions(text, Boxes));

    [TestMethod]
    [DataRow(@"C:\WinBox\Data\BoxesOld\app.exe")]
    [DataRow(@"E:\Quark\quark.exe ""%1""")]
    public void IgnoresOtherPaths(string text) => Assert.IsFalse(HostReferenceRules.Mentions(text, Boxes));

    [TestMethod]
    [DataRow(@"Software\Microsoft\Windows\CurrentVersion\Run")]
    [DataRow(@"Software\Classes\QuarkCloudDriveHTM.mp4\shell\open\command")]
    [DataRow(@"Software\Google\Chrome\NativeMessagingHosts\com.quark.hostclient")]
    [DataRow(@"Software\Classes\CLSID\{6FCBEFA0-DD11-4EB0-A87D-46DAF26BF2B7}\DefaultIcon")]
    public void AutostartAssociationsAndComAreLeaks(string key) =>
        Assert.AreEqual(HostReferenceKind.Leak, HostReferenceRules.ClassifyKey(key));

    [TestMethod]
    [DataRow(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache")]
    [DataRow(@"Control Panel\NotifyIconSettings\14056188812941994953")]
    [DataRow(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched")]
    public void WindowsUsageRecordsAreTraces(string key) =>
        Assert.AreEqual(HostReferenceKind.Trace, HostReferenceRules.ClassifyKey(key));

    [TestMethod]
    [DataRow(@"Software\Microsoft\DirectX\UserGpuPreferences")]
    [DataRow(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers")]
    public void SettingsThatCannotStartAnythingAreTraces(string key) =>
        Assert.AreEqual(HostReferenceKind.Trace, HostReferenceRules.ClassifyKey(key));

    [TestMethod]
    public void ListsLoseOnlyTheEntriesInTheBox()
    {
        Assert.IsTrue(HostReferenceRules.HoldsLists("Environment"));
        Assert.IsFalse(HostReferenceRules.HoldsLists(@"Software\Microsoft\Windows\CurrentVersion\Run"));

        var path = @"%USERPROFILE%\bin;C:\WinBox\Data\Boxes\1.box\App\bin;C:\Python312".Split(';');
        CollectionAssert.AreEqual(new[] { @"%USERPROFILE%\bin", @"C:\Python312" }, HostReferenceRules.WithoutMentions(path, Boxes)!.ToList());
    }

    [TestMethod]
    public void ListOfOnlyBoxEntriesGoesEntirely() =>
        Assert.IsNull(HostReferenceRules.WithoutMentions(@"C:\WinBox\Data\Boxes\1.box\App;".Split(';'), Boxes));

    [TestMethod]
    public void PackageRegistrationsAreNotScanned()
    {
        Assert.IsTrue(HostReferenceRules.IsIgnoredKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\WinBox.Box.1_1.0.0.0_x64__x"));
        Assert.IsTrue(HostReferenceRules.IsIgnoredKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings\S-1-15-2-1"));
        Assert.IsFalse(HostReferenceRules.IsIgnoredKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"));
    }

    [TestMethod]
    public void UserAssistNamesAreDecodedBeforeMatching()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count";
        Assert.IsTrue(HostReferenceRules.HasEncodedNames(key));
        Assert.IsFalse(HostReferenceRules.HasEncodedNames(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"));

        // A value as Windows stores it for a deleted box: the path is only recognisable once decoded.
        const string stored = @"P:\Hfref\nyvpr\JvaObk\Qngn\Obkrf\q6n353q3-299n-43r5-8s5n-oqn401q27n16.obk\Ncc\dhnex_pybhq_qevir.rkr";
        var decoded = HostReferenceRules.Rot13(stored);
        Assert.AreEqual(@"C:\Users\alice\WinBox\Data\Boxes\d6a353d3-299a-43e5-8f5a-bda401d27a16.box\App\quark_cloud_drive.exe", decoded);
        Assert.AreEqual(stored, HostReferenceRules.Rot13(decoded));
        Assert.IsTrue(HostReferenceRules.Mentions(decoded, @"C:\Users\alice\WinBox\Data\Boxes"));
    }

    [TestMethod]
    [DataRow("WinBox.Box.d6a353d3299a_skdvrgrnjqq2t", "WinBox.Box.d6a353d3299a_skdvrgrnjqq2t")]
    [DataRow("WinBox.Box.d6a353d3299a_skdvrgrnjqq2t!App", "WinBox.Box.d6a353d3299a_skdvrgrnjqq2t")]
    [DataRow("windows.data.apps.appmetadata$winbox.box.d6a353d3299a_skdvrgrnjqq2t", "winbox.box.d6a353d3299a_skdvrgrnjqq2t")]
    [DataRow("WinBox.Box.d6a353d3299a_1.0.0.0_x64__skdvrgrnjqq2t", null)]
    [DataRow("Microsoft.WindowsCalculator_8wekyb3d8bbwe", null)]
    [DataRow("prefix WinBox.Box.d6a353d3299a_skdvrgrnjqq2t", null)]
    public void PackageRecordsAreRecognisedByName(string name, string? family) =>
        Assert.AreEqual(family, HostReferenceRules.BoxPackageFamilyIn(name));

    [TestMethod]
    public void BoxFolderIsReadFromAPath()
    {
        Assert.AreEqual("1.box", HostReferenceRules.BoxFolderIn(@"""C:\WinBox\Data\Boxes\1.box\App\app.exe"" --x", Boxes));
        Assert.AreEqual("1.box", HostReferenceRules.BoxFolderIn(@"c:\winbox\data\boxes\1.box", Boxes));
        Assert.IsNull(HostReferenceRules.BoxFolderIn(@"C:\WinBox\Data\BoxesOld\1.box\app.exe", Boxes));
    }

    [TestMethod]
    public void TrayIconSettingsGoAsAWholeKey()
    {
        Assert.IsTrue(HostReferenceRules.RemovesWholeKey(@"Control Panel\NotifyIconSettings\14056188812941994953"));
        Assert.IsFalse(HostReferenceRules.RemovesWholeKey(@"Control Panel\NotifyIconSettings"));
        Assert.IsFalse(HostReferenceRules.RemovesWholeKey(@"Software\Microsoft\Windows\CurrentVersion\Run"));
    }
}
