using WinBox.Core.Boxes;

namespace WinBox.Core.Sandbox;

/// <summary>Turns a box into a <see cref="LaunchSpec"/>: what the app may read, write and see.</summary>
public sealed class LaunchPlanner
{
    /// <summary>Placeholder in <see cref="BoxConfig.LaunchArguments"/> replaced by the box's Data directory.</summary>
    public const string DataPlaceholder = "{data}";

    private readonly IReadOnlyList<string> _personalFolders;
    private readonly string _localAppData;
    private readonly IReadOnlyList<string> _winBoxRoots;

    /// <param name="personalFolders">
    /// The user's own folders the app may read and write (Desktop, Documents, Downloads...). Personal files
    /// are deliberately not restricted; only the app's install files and configuration are isolated.
    /// </param>
    /// <param name="localAppData">%LOCALAPPDATA%; package private storage lives under its Packages folder.</param>
    /// <param name="winBoxRoots">
    /// WinBox's own directories (install directory, data root). Whatever of them a personal folder would
    /// expose is denied, apart from the box's own items: WinBox runs files from there outside the sandbox.
    /// </param>
    public LaunchPlanner(IReadOnlyList<string> personalFolders, string localAppData, IReadOnlyList<string>? winBoxRoots = null)
    {
        _personalFolders = personalFolders;
        _localAppData = localAppData;
        _winBoxRoots = winBoxRoots ?? [];
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

        // MXC's BaseContainer tier (PSEC) grants readonlyPaths without FILE_EXECUTE, so an app could not
        // load its own DLLs from a read-only App directory (STATUS_ACCESS_DENIED at startup). App and Shim
        // are therefore granted read-write until MXC offers read+execute; WinBox still owns updates and
        // refreshes Shim before every run.
        List<string> boxGrants = [.. paths.ReadOnlyItems, .. paths.WritableItems, .. packageStorage];

        return new LaunchSpec
        {
            Executable = paths.ShimExecutable,
            Arguments = shim.ToArgs(),
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            ReadOnlyPaths = [],
            ReadWritePaths = [.. boxGrants, .. _personalFolders],
            // The box itself may sit inside a granted personal folder (development data under Desktop);
            // its private items must stay out of reach regardless, and so must the rest of WinBox.
            DeniedPaths = paths.PrivateItems
                .Concat(ExposedWinBoxEntries(boxGrants))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Environment = new Dictionary<string, string>
            {
                ["TEMP"] = paths.Tmp,
                ["TMP"] = paths.Tmp,
            },
            AllowNetwork = box.Config.AllowsNetwork,
            DenialsOutputPath = Path.Combine(paths.Reports, $"denials-{now:yyyyMMdd-HHmmss}"),
        };
    }

    /// <summary>
    /// Everything under WinBox's roots that a granted personal folder exposes, except the box's own grants:
    /// other boxes, downloads, logs, WinBox's binaries. A directory holding a grant is split into its entries
    /// instead of being denied whole, because a denial would take the grant back. Entries created after the
    /// box starts (another box imported meanwhile) are only covered from its next run.
    /// </summary>
    private List<string> ExposedWinBoxEntries(IReadOnlyList<string> boxGrants)
    {
        var grants = boxGrants.Concat(_personalFolders).Select(Normalize).ToList();
        var own = boxGrants.Select(Normalize).ToList();
        var denied = new List<string>();

        void Visit(string path)
        {
            if (own.Any(g => IsSameOrUnder(path, g)))
                return;
            if (!grants.Any(g => IsSameOrUnder(g, path)))
            {
                denied.Add(path);
                return;
            }
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(path).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var entry in entries)
                Visit(entry);
        }

        foreach (var root in _winBoxRoots.Select(Normalize).Where(Directory.Exists))
        {
            if (_personalFolders.Any(p => IsSameOrUnder(root, Normalize(p))))
                Visit(root);
        }
        return denied;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrUnder(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
