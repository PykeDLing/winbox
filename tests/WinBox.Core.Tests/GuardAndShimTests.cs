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
    public void TrayIconSettingsGoAsAWholeKey()
    {
        Assert.IsTrue(HostReferenceRules.RemovesWholeKey(@"Control Panel\NotifyIconSettings\14056188812941994953"));
        Assert.IsFalse(HostReferenceRules.RemovesWholeKey(@"Control Panel\NotifyIconSettings"));
        Assert.IsFalse(HostReferenceRules.RemovesWholeKey(@"Software\Microsoft\Windows\CurrentVersion\Run"));
    }
}
