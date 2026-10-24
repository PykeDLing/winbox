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
            WindowsHostAreas.Current,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            [AppContext.BaseDirectory, Paths.Root]);
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

    /// <summary>
    /// Windows' records of boxes that are gone: those filed under the name of a box package that is no longer
    /// registered (Windows writes some of them only after the package is removed, so a removal cannot catch
    /// them all), and those naming a box folder that no longer exists. Packages still registered count as in
    /// use whoever registered them, so another WinBox's boxes are never taken for leftovers.
    /// </summary>
    public IReadOnlyList<HostReference> FindLeftovers()
    {
        var inUse = Store.Load().OfType<LoadedBox>()
            .Select(b => b.Config.Package?.FamilyName).OfType<string>()
            .Concat(Packages.RegisteredBoxFamilies())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Guard.Scan(Paths.Boxes, family => !inUse.Contains(family))
            .Where(r => r.Kind == HostReferenceKind.Trace &&
                (r.Package is not null ||
                 (HostReferenceRules.BoxFolderIn($"{r.Name}\0{r.Detail}", Paths.Boxes) is { } folder &&
                  !Directory.Exists(Path.Combine(Paths.Boxes, folder)))))
            .ToList();
    }

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
