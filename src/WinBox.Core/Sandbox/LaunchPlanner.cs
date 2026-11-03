using WinBox.Core.Boxes;

namespace WinBox.Core.Sandbox;

/// <summary>Turns a box into a <see cref="LaunchSpec"/>: what the app may read, write and see.</summary>
public sealed class LaunchPlanner
{
    /// <summary>Placeholder in <see cref="BoxConfig.LaunchArguments"/> replaced by the box's Data directory.</summary>
    public const string DataPlaceholder = "{data}";

    private readonly Func<HostAreas> _areas;
    private readonly string _localAppData;
    private readonly IReadOnlyList<string> _winBoxRoots;

    /// <summary>A planner that grants only <paramref name="personalFolders"/> of the host.</summary>
    public LaunchPlanner(IReadOnlyList<string> personalFolders, string localAppData, IReadOnlyList<string>? winBoxRoots = null)
        : this(() => new HostAreas(personalFolders, [], []), localAppData, winBoxRoots)
    {
    }

    /// <param name="areas">
    /// The parts of the host the app may read and write (personal folders, data drives) and the installed
    /// programs inside them it may not touch. Personal files are deliberately not restricted; only the app's
    /// install files and configuration are isolated.
    /// </param>
    /// <param name="localAppData">%LOCALAPPDATA%; package private storage lives under its Packages folder.</param>
    /// <param name="winBoxRoots">
    /// WinBox's own directories (install directory, data root). No granted area may cover any part of them,
    /// apart from the box's own items: WinBox runs files from there outside the sandbox.
    /// </param>
    public LaunchPlanner(Func<HostAreas> areas, string localAppData, IReadOnlyList<string>? winBoxRoots = null)
    {
        _areas = areas;
        _localAppData = localAppData;
        _winBoxRoots = winBoxRoots ?? [];
    }

    /// <summary>Where Windows keeps a package's private AppData files and registry hive.</summary>
    public string PackageStorage(BoxPackage package) => Path.Combine(_localAppData, "Packages", package.FamilyName);

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
        // WinBox asks it to show the app's window again or to quit through the box's Control folder.
        var shim = new ShimArguments(paths.LocalAppDataState, StateFolder.DefaultExclusions, exe, arguments, paths.Control);

        // MXC's BaseContainer tier (PSEC) grants readonlyPaths without FILE_EXECUTE, so an app could not
        // load its own DLLs from a read-only App directory (STATUS_ACCESS_DENIED at startup). App and Shim
        // are therefore granted read-write until MXC offers read+execute; WinBox still owns updates and
        // refreshes Shim before every run.
        List<string> boxGrants = [.. paths.ReadOnlyItems, .. paths.WritableItems, .. packageStorage];

        var areas = _areas();
        var userAreas = AroundWinBox([.. areas.PersonalFolders, .. areas.DataDrives.SelectMany(d => DriveGrants(d, areas.InstalledPrograms))]);
        List<string> granted = [.. boxGrants, .. userAreas];

        return new LaunchSpec
        {
            Executable = paths.ShimExecutable,
            Arguments = shim.ToArgs(),
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            ReadOnlyPaths = [],
            ReadWritePaths = granted,
            // The box's private items are never granted; denying them as well keeps them out of reach should a
            // grant ever cover them. The programs installed on a granted drive are denied.
            DeniedPaths = paths.PrivateItems
                .Concat(ProtectedPrograms(areas, [.. boxGrants, .. userAreas]))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Environment = new Dictionary<string, string>
            {
                ["TEMP"] = paths.Tmp,
                ["TMP"] = paths.Tmp,
                // The real Roaming AppData is out of the sandbox's reach, and Windows does not move it for a
                // sandboxed package (measured: a folder made there with Roaming granted landed in the real one), so
                // apps that keep their settings there (Electron and Qt apps, most others) could not start. Windows
                // works the user's folders out from USERPROFILE as the process sees it, so the app gets a profile
                // of its own in the box: Roaming AppData is the box's, the personal folders link to the real ones.
                ["USERPROFILE"] = paths.Profile,
                ["HOMEDRIVE"] = Path.GetPathRoot(paths.Profile)!.TrimEnd(Path.DirectorySeparatorChar),
                ["HOMEPATH"] = paths.Profile[Path.GetPathRoot(paths.Profile)!.TrimEnd(Path.DirectorySeparatorChar).Length..],
                ["APPDATA"] = paths.Roaming,
            },
            AllowNetwork = box.Config.AllowsNetwork,
            DenialsOutputPath = Path.Combine(paths.Reports, $"denials-{now:yyyyMMdd-HHmmss}"),
        };
    }

    /// <summary>
    /// The user's areas with WinBox's own directories cut out. WinBox runs files from its install directory and
    /// data root outside the sandbox, so the app may not write anywhere in them: not to what is there (other
    /// boxes, WinBox's binaries), and not new files either, such as a DLL next to WinBox.exe that WinBox would
    /// load on its next start. Denying the entries of a granted folder leaves the folder open to new ones, so no
    /// grant may cover WinBox at all: an area holding a WinBox directory is replaced by its entries, the ones
    /// leading to WinBox split again in turn, and WinBox's directories themselves left out. A drive root stays,
    /// since MXC grants it to the files at the root alone (see <see cref="DriveGrants"/>). What the user adds to a
    /// split folder while the box runs is covered from its next run.
    /// </summary>
    private List<string> AroundWinBox(IReadOnlyList<string> areas)
    {
        var roots = _winBoxRoots.Where(Directory.Exists).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0)
            return [.. areas];

        bool InWinBox(string path) => roots.Any(r => IsSameOrUnder(path, r));
        bool HoldsWinBox(string path) => roots.Any(r => IsSameOrUnder(r, path));

        var grants = new List<string>();
        void Split(string directory)
        {
            List<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var entry in entries)
            {
                var path = Normalize(entry);
                if (InWinBox(path))
                    continue;
                if (HoldsWinBox(path))
                    Split(path);
                else
                    grants.Add(entry);
            }
        }

        foreach (var area in areas)
        {
            var path = Normalize(area);
            if (InWinBox(path))
                continue;
            if (HoldsWinBox(path) && !IsDriveRoot(path))
                Split(path);
            else
                grants.Add(area);
        }
        return grants;
    }

    private static bool IsDriveRoot(string path) => string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What granting a whole drive takes: MXC applies a grant of a drive root ("D:\") to the files at the root
    /// alone, never to the folders below it (measured: writing into any subfolder of a granted D:\ was denied,
    /// and "D:" or "D:\." make MXC refuse the sandbox). So the drive is granted as its root plus each of its
    /// top-level folders, leaving out installed programs and Windows' hidden system folders ($RECYCLE.BIN,
    /// System Volume Information). A top-level folder made while the box runs is covered from its next run.
    /// </summary>
    private static List<string> DriveGrants(string drive, IReadOnlyList<string> installedPrograms)
    {
        var programs = installedPrograms.Where(Path.IsPathFullyQualified).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> grants = [drive];
        try
        {
            foreach (var folder in new DirectoryInfo(drive).EnumerateDirectories())
            {
                const FileAttributes hiddenSystem = FileAttributes.Hidden | FileAttributes.System;
                if ((folder.Attributes & hiddenSystem) == hiddenSystem || programs.Contains(Normalize(folder.FullName)))
                    continue;
                grants.Add(folder.FullName);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable drive keeps just its root.
        }
        return grants;
    }

    /// <summary>
    /// The installed programs that sit on a granted drive. A program folder holding a grant (WinBox itself,
    /// a personal folder moved onto that drive) is left alone, since denying it would take the grant back,
    /// and so is a drive root listed as an install location, which would deny the whole drive.
    /// </summary>
    private static IEnumerable<string> ProtectedPrograms(HostAreas areas, IReadOnlyList<string> grants)
    {
        var drives = areas.DataDrives.Select(Normalize).ToList();
        var granted = grants.Select(Normalize).ToList();
        return areas.InstalledPrograms
            .Where(Path.IsPathFullyQualified)
            .Select(Normalize)
            .Where(p => drives.Any(d => IsSameOrUnder(p, d) && !p.Equals(d, StringComparison.OrdinalIgnoreCase)))
            .Where(p => !granted.Any(g => IsSameOrUnder(g, p)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrUnder(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        // A drive root ("D:\") already ends in the separator.
        path.StartsWith(Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
