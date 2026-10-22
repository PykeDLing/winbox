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
        Store = new BoxStore(Paths);
        Sandbox = new MxcSandboxRunner();
        Planner = new LaunchPlanner(
            LaunchPlanner.DefaultPersonalFolders(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Packages = new MsixBoxPackageManager(MsixBoxPackageManager.DefaultPayloadDirectory);
        Shim = new BoxShimDeployer(BoxShimDeployer.DefaultPayloadDirectory);
        PackagedRunner = new PackagedBoxRunner(Packages, Shim);
        Guard = new WindowsHostGuard();
        InnoextractPath = InnoExtractor.Locate();

        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinBox/0.1");

        var extractors = new List<IPackageExtractor>();
        if (InnoextractPath is not null)
            extractors.Add(new InnoExtractor(InnoextractPath));

        Import = new ImportPipeline(Paths, Store, new WindowsFileInspector(), extractors, new HttpDownloader(_http), Recipes);
    }

    public static IReadOnlyList<IRecipe> Recipes { get; } = [new QuarkCloudDriveRecipe()];

    public AppPaths Paths { get; }
    public BoxStore Store { get; }
    public ISandboxRunner Sandbox { get; }
    public LaunchPlanner Planner { get; }
    public IBoxPackageManager Packages { get; }
    public BoxShimDeployer Shim { get; }
    public PackagedBoxRunner PackagedRunner { get; }
    public IHostGuard Guard { get; }
    public ImportPipeline Import { get; }
    public string? InnoextractPath { get; }

    public void Dispose() => _http.Dispose();
}
