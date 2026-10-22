using WinBox.Core.Boxes;

namespace WinBox.Core.Sandbox;

/// <summary>Turns a box into a <see cref="LaunchSpec"/>: what the app may read, write and see.</summary>
public sealed class LaunchPlanner
{
    /// <summary>Placeholder in <see cref="BoxConfig.LaunchArguments"/> replaced by the box's Data directory.</summary>
    public const string DataPlaceholder = "{data}";

    private readonly IReadOnlyList<string> _personalFolders;
    private readonly string _localAppData;

    /// <param name="personalFolders">
    /// The user's own folders the app may read and write (Desktop, Documents, Downloads...). Personal files
    /// are deliberately not restricted; only the app's install files and configuration are isolated.
    /// </param>
    /// <param name="localAppData">%LOCALAPPDATA%; package private storage lives under its Packages folder.</param>
    public LaunchPlanner(IReadOnlyList<string> personalFolders, string localAppData)
    {
        _personalFolders = personalFolders;
        _localAppData = localAppData;
    }

    /// <summary>Where Windows keeps a package's private AppData files and registry hive.</summary>
    public string PackageStorage(BoxPackage package) => Path.Combine(_localAppData, "Packages", package.FamilyName);

    public static IReadOnlyList<string> DefaultPersonalFolders()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic,
        ];
        var paths = folders.Select(Environment.GetFolderPath).ToList();
        // Downloads has no SpecialFolder value; it sits next to Documents under the profile by default.
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        return paths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public LaunchSpec Plan(LoadedBox box, DateTimeOffset now)
    {
        var paths = box.Paths;
        var exe = Path.GetFullPath(Path.Combine(paths.App, box.Config.Entry));
        if (!exe.StartsWith(Path.GetFullPath(paths.App) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Entry '{box.Config.Entry}' points outside the box's App directory.");

        var arguments = box.Config.LaunchArguments
            .Select(a => a.Replace(DataPlaceholder, paths.Data, StringComparison.Ordinal))
            .ToList();

        // Inside the box's package, Windows redirects the app's new AppData files and HKCU writes into the
        // package's private storage, so the sandbox must let it write there. Modifying files that already
        // exist in the real AppData is not redirected; those stay denied by not being granted at all.
        List<string> packageStorage = box.Config.Package is { } package ? [PackageStorage(package)] : [];

        // The sandbox's LocalAppData is thrown away after every run, so BoxShim starts first: it restores
        // the state saved in the box, runs the app, and saves the state again once the app has exited.
        var shim = new ShimArguments(paths.LocalAppDataState, StateFolder.DefaultExclusions, exe, arguments);

        return new LaunchSpec
        {
            Executable = paths.ShimExecutable,
            Arguments = shim.ToArgs(),
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            // MXC's BaseContainer tier (PSEC) grants readonlyPaths without FILE_EXECUTE, so an app could
            // not load its own DLLs from a read-only App directory (STATUS_ACCESS_DENIED at startup).
            // App and Shim are therefore granted read-write until MXC offers read+execute; WinBox still
            // owns updates and refreshes Shim before every run.
            ReadOnlyPaths = [],
            ReadWritePaths = [.. paths.ReadOnlyItems, .. paths.WritableItems, .. packageStorage, .. _personalFolders],
            // The box itself may sit inside a granted personal folder (development data under Desktop);
            // its private items must stay out of reach regardless.
            DeniedPaths = paths.PrivateItems,
            Environment = new Dictionary<string, string>
            {
                ["TEMP"] = paths.Tmp,
                ["TMP"] = paths.Tmp,
            },
            AllowNetwork = box.Config.AllowsNetwork,
            DenialsOutputPath = Path.Combine(paths.Reports, $"denials-{now:yyyyMMdd-HHmmss}"),
        };
    }
}
