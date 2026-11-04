using WinBox.Core;
using WinBox.Core.Boxes;
using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Import.Recipes;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;
using WinBox.Platform.Guard;
using WinBox.Platform.Import;
using WinBox.Platform.Packaging;
using WinBox.Platform.Sandbox;

namespace WinBox.Platform;

/// <summary>Composition root shared by the App and the CLI: wires Core to the Windows implementations.</summary>
public sealed class WinBoxHost : IDisposable
{
    private readonly HttpClient _http;

    public WinBoxHost(AppPaths? paths = null)
    {
        Paths = paths ?? AppPaths.Default();
        Store = new BoxStore(Paths, link: HardLink.TryCreate);
        Sandbox = new MxcSandboxRunner();
        Planner = new LaunchPlanner(
            WindowsHostAreas.Current,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            [AppContext.BaseDirectory, Paths.Root]);
        Packages = new MsixBoxPackageManager(MsixBoxPackageManager.DefaultPayloadDirectory);
        Shim = new BoxShimDeployer(BoxShimDeployer.DefaultPayloadDirectory);
        Runner = new PackagedBoxRunner(Packages, Shim);
        Guard = new WindowsHostGuard();
        InnounpPath = InnoExtractor.Locate();
        SevenZipPath = SevenZip.Locate();

        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinBox/0.1");

        // Asked in this order; the first that recognizes the installer unpacks it.
        var extractors = new List<IPackageExtractor>();
        if (InnounpPath is not null)
            extractors.Add(new InnoExtractor(InnounpPath));
        if (SevenZipPath is not null)
        {
            var sevenZip = new SevenZip(SevenZipPath);
            extractors.Add(new MsiExtractor(sevenZip));
            extractors.Add(new SevenZipExtractor(sevenZip));
        }
        extractors.Add(new PortableExtractor());

        Import = new ImportPipeline(Paths, Store, new WindowsFileInspector(), extractors, new HttpDownloader(_http), Recipes,
            WindowsAccount.IsFilteredAdministrator());
        Manager = new BoxManager(Paths, Store, Import, Packages, Runner, Guard, Planner);
    }

    public static IReadOnlyList<IRecipe> Recipes { get; } = [new QuarkCloudDriveRecipe()];

    public AppPaths Paths { get; }
    public BoxStore Store { get; }
    public ISandboxRunner Sandbox { get; }
    public LaunchPlanner Planner { get; }
    public IBoxPackageManager Packages { get; }
    public BoxShimDeployer Shim { get; }
    public IBoxRunner Runner { get; }
    public IHostGuard Guard { get; }
    public ImportPipeline Import { get; }

    /// <summary>What the app and the CLI do to boxes: import, register, run, remove, clear the host.</summary>
    public BoxManager Manager { get; }
    /// <summary>innounp, which unpacks Inno Setup installers; null when it is not there.</summary>
    public string? InnounpPath { get; }

    /// <summary>7-Zip, which unpacks NSIS installers, MSI cabinets and archives; null when it is not there.</summary>
    public string? SevenZipPath { get; }

    public void Dispose() => _http.Dispose();
}
