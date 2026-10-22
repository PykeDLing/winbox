using System.Text;
using System.Xml.Linq;
using WinBox.Core.Boxes;
using WinBox.Core.Import;
using WinBox.Core.Packaging;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class InstallerDetectorTests
{
    private static byte[] Bytes(params string[] parts) => parts.SelectMany(Encoding.ASCII.GetBytes).ToArray();

    [TestMethod]
    public void InnoSetupIsRecognisedByItsLoaderTable() =>
        Assert.AreEqual(InstallerKind.InnoSetup, InstallerDetector.Detect(Bytes("MZ", new string('\0', 64), "rDlPtS02"), ".exe"));

    [TestMethod]
    public void NsisIsRecognised()
    {
        byte[] nsis = [0xEF, 0xBE, 0xAD, 0xDE, .. "NullsoftInst"u8];
        Assert.AreEqual(InstallerKind.Nsis, InstallerDetector.Detect([.. Bytes("MZ"), .. nsis], ".exe"));
    }

    [TestMethod]
    public void PlainExecutableIsPortable() =>
        Assert.AreEqual(InstallerKind.PortableExe, InstallerDetector.Detect(Bytes("MZ", "nothing to see"), ".exe"));

    [TestMethod]
    public void ZipAndMsixAreTellApartByExtension()
    {
        var zip = Bytes("PK\x03\x04rest");
        Assert.AreEqual(InstallerKind.Zip, InstallerDetector.Detect(zip, ".zip"));
        Assert.AreEqual(InstallerKind.Msix, InstallerDetector.Detect(zip, ".msix"));
    }

    [TestMethod]
    public void MsiNeedsItsExtension()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0];
        Assert.AreEqual(InstallerKind.Msi, InstallerDetector.Detect(ole, ".msi"));
        Assert.AreEqual(InstallerKind.Unknown, InstallerDetector.Detect(ole, ".doc"));
    }
}

[TestClass]
public sealed class EntryResolverTests
{
    private static ExecutableCandidate C(string path, long size = 100) => new(path, size, null);

    [TestMethod]
    public void RecipeEntryWins() =>
        Assert.AreEqual("quark_cloud_drive.exe", EntryResolver.Resolve([C("big.exe", 999), C("quark_cloud_drive.exe", 1)], "quark_cloud_drive.exe")!.RelativePath);

    [TestMethod]
    public void UninstallersUpdatersAndHelpersAreSkipped() =>
        Assert.AreEqual("app.exe", EntryResolver.Resolve([C("unins000.exe", 999), C("updater.exe", 999), C("crashpad_handler.exe", 999), C("app.exe", 1)])!.RelativePath);

    [TestMethod]
    public void TopLevelBeatsNestedThenLargest() =>
        Assert.AreEqual("main.exe", EntryResolver.Resolve([C(@"bin\huge.exe", 9999), C("small.exe", 10), C("main.exe", 20)])!.RelativePath);

    [TestMethod]
    public void NothingQualifies() =>
        Assert.IsNull(EntryResolver.Resolve([C("uninstall.exe")]));
}

[TestClass]
public sealed class BoxPackageManifestTests
{
    [TestMethod]
    public void NameIsAValidPackageIdentity()
    {
        var name = BoxPackageManifest.NameFor(Guid.Parse("1838711e-d6d7-43d1-9eaa-5a403a6f9679"));
        Assert.AreEqual("WinBox.Box.1838711ed6d7", name);
        Assert.IsTrue(name.Length is >= 3 and <= 50);
        Assert.IsTrue(name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'));
    }

    [TestMethod]
    public void ManifestIsWellFormedAndEscapesDisplayName()
    {
        var xml = BoxPackageManifest.Build("WinBox.Box.abc", "A & B <夸克>");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        Assert.AreEqual("WinBox.Box.abc", doc.Root!.Element(ns + "Identity")!.Attribute("Name")!.Value);
        Assert.AreEqual("WinBox · A & B <夸克>", doc.Root.Element(ns + "Properties")!.Element(ns + "DisplayName")!.Value);
        var app = doc.Root.Element(ns + "Applications")!.Element(ns + "Application")!;
        Assert.AreEqual(BoxPackage.ApplicationId, app.Attribute("Id")!.Value);
        Assert.AreEqual(BoxPackageManifest.HostExecutable, app.Attribute("Executable")!.Value);
        Assert.AreEqual("Windows.FullTrustApplication", app.Attribute("EntryPoint")!.Value);
    }
}

[TestClass]
public sealed class SignatureInfoTests
{
    [TestMethod]
    public void QuotedCommonNameWithCommaIsParsed() =>
        Assert.AreEqual("ALIBABA (CHINA) NETWORK TECHNOLOGY CO.,LTD.",
            new SignatureInfo(true, "CN=\"ALIBABA (CHINA) NETWORK TECHNOLOGY CO.,LTD.\", O=\"ALIBABA\", C=CN").CommonName);

    [TestMethod]
    public void PlainCommonNameIsParsed() =>
        Assert.AreEqual("Contoso", new SignatureInfo(true, "CN=Contoso, O=Contoso, C=US").CommonName);
}
