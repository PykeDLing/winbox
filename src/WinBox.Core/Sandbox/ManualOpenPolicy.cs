using WinBox.Core.Boxes;

namespace WinBox.Core.Sandbox;

/// <summary>Why WinBox refuses to open something outside the box for the user. Stable; the UI translates it.</summary>
public enum ManualOpenRefusal
{
    /// <summary>Not a full path on a local drive: a network path, a device path, an alternate data stream.</summary>
    NotLocalPath,

    /// <summary>Outside everything the box may read and write.</summary>
    OutsideGrants,

    /// <summary>Inside a granted area, but in a part the box is denied (an installed program, WinBox itself).</summary>
    Denied,

    /// <summary>A program, script, shortcut, installer or other type that runs code when opened.</summary>
    DangerousType,

    /// <summary>No program on this computer opens this type.</summary>
    NoAssociation,

    /// <summary>The file or folder does not exist (any more).</summary>
    Missing,
}

/// <summary>
/// What WinBox lets the user open outside a box from the box's card: files and folders the box itself may reach,
/// never anything that runs code. A boxed app can write into the granted areas (a shortcut posing as a document
/// in Downloads, say), so a file the user picks is checked like any other; WinBox must not become a way around
/// the sandbox. The paths given here are final paths, with links and short names already resolved.
/// </summary>
public static class ManualOpenPolicy
{
    /// <summary>
    /// Types that run code or change the system when opened, on top of what Windows itself calls dangerous
    /// (AssocIsDangerous, checked by the caller).
    /// </summary>
    public static readonly IReadOnlySet<string> DangerousExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".ws", ".hta", ".jar", ".msc", ".cpl", ".inf", ".reg", ".dll", ".ocx", ".sys", ".drv",
        ".lnk", ".url", ".scf", ".msi", ".msp", ".mst", ".msix", ".msixbundle", ".appx", ".appxbundle",
        ".appinstaller", ".application", ".appref-ms", ".gadget", ".chm", ".xll", ".iso", ".img", ".vhd",
        ".vhdx", ".library-ms", ".search-ms", ".searchconnector-ms", ".settingcontent-ms", ".theme",
        ".themepack", ".diagcab",
    };

    /// <summary>Null when <paramref name="path"/> is a file the box may reach and its type does not run code.</summary>
    public static ManualOpenRefusal? CheckFile(LaunchSpec spec, string path) =>
        CheckLocation(spec, path) ?? (DangerousExtensions.Contains(Path.GetExtension(path)) ? ManualOpenRefusal.DangerousType : null);

    /// <summary>Null when <paramref name="path"/> is a folder the box may reach.</summary>
    public static ManualOpenRefusal? CheckFolder(LaunchSpec spec, string path) => CheckLocation(spec, path);

    /// <summary>
    /// The user's own places among the box's grants, for the card's menu: the personal folders and the roots of
    /// the granted drives (whose top-level folders are granted one by one). The box's own folders and its package
    /// storage are left out.
    /// </summary>
    public static IReadOnlyList<string> UserFolders(LaunchSpec spec, BoxPaths box, string? packageStorage)
    {
        var grants = spec.ReadWritePaths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roots = grants.Where(IsDriveRoot).ToList();
        var own = new List<string> { Normalize(box.Root) };
        if (packageStorage is not null)
            own.Add(Normalize(packageStorage));
        return grants
            .Where(g => !own.Any(o => IsSameOrUnder(g, o)))
            .Where(g => IsDriveRoot(g) || !roots.Any(r => IsUnder(g, r)))
            .ToList();
    }

    private static ManualOpenRefusal? CheckLocation(LaunchSpec spec, string path)
    {
        if (!IsLocalPath(path))
            return ManualOpenRefusal.NotLocalPath;
        var full = Normalize(path);
        if (spec.DeniedPaths.Select(Normalize).Any(d => IsSameOrUnder(full, d)))
            return ManualOpenRefusal.Denied;
        return spec.ReadWritePaths.Select(Normalize).Any(g => Covers(g, full)) ? null : ManualOpenRefusal.OutsideGrants;
    }

    /// <summary>
    /// Whether a grant reaches <paramref name="path"/> the way MXC applies it: a drive root covers only the
    /// files at the root itself (its folders are granted one by one, see LaunchPlanner.DriveGrants); any
    /// other grant covers everything below it.
    /// </summary>
    private static bool Covers(string grant, string path) =>
        IsDriveRoot(grant)
            ? path.Equals(grant, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(Path.GetDirectoryName(path), grant, StringComparison.OrdinalIgnoreCase)
            : IsSameOrUnder(path, grant);

    /// <summary>"X:\..." and nothing else: no UNC or device path, no \\?\ prefix, no alternate data stream.</summary>
    private static bool IsLocalPath(string path) =>
        path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        path.IndexOf(':', 2) < 0 &&
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
        Path.IsPathFullyQualified(path);

    private static bool IsDriveRoot(string path) => path.Length == 3 && path[1] == ':' && path[2] == '\\';

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrUnder(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) || IsUnder(path, ancestor);

    private static bool IsUnder(string path, string ancestor) =>
        path.StartsWith(Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
