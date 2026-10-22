namespace WinBox.Core.Boxes;

/// <summary>
/// On-disk layout of one box. Every entry in the box root belongs to exactly one category, and both the
/// sandbox policy and WinBox's own checks are derived from these lists, never from ad-hoc paths.
/// </summary>
/// <remarks>
/// <code>
/// Boxes\&lt;id&gt;.box\
///   box.json        private   (WinBox only)
///   icon.png        private   (the entry executable's icon, for the library)
///   App\            readonly  (the sandboxed app may read and execute, never write; WinBox owns updates)
///   Shim\           readonly  (WinBox.BoxShim, the first process in the sandbox; refreshed before every run)
///   Data\           writable  (app data: --user-data-dir points here)
///   Tmp\            writable  (TEMP / TMP inside the sandbox)
///   State\          writable  (state BoxShim carries between runs, e.g. the sandbox's LocalAppData)
///   Source\         private   (the installer the user dropped in)
///   Reports\        private   (run reports, newest 20 kept)
///   Host\           private   (the box's MSIX package: manifest, BoxHost, launch.json, run.json)
///   App.new\        private   (staging for updates, see FileOps.Replace)
///   .unfinished     private   (marker for interrupted maintenance)
/// </code>
/// </remarks>
public sealed class BoxPaths
{
    public const string Extension = ".box";
    public const string PartialExtension = ".box.partial";

    public BoxPaths(string root) => Root = root;

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, "box.json");
    public string Icon => Path.Combine(Root, "icon.png");
    public string App => Path.Combine(Root, "App");
    public string Data => Path.Combine(Root, "Data");
    public string Tmp => Path.Combine(Root, "Tmp");
    public string Shim => Path.Combine(Root, "Shim");
    public string ShimExecutable => Path.Combine(Shim, Sandbox.ShimArguments.ExecutableName);
    public string State => Path.Combine(Root, "State");
    public string LocalAppDataState => Path.Combine(State, "LocalAppData");
    public string Source => Path.Combine(Root, "Source");
    public string Reports => Path.Combine(Root, "Reports");
    public string Host => Path.Combine(Root, "Host");
    public string AppStaging => Path.Combine(Root, "App.new");
    public string UnfinishedMarker => Path.Combine(Root, ".unfinished");

    /// <summary>Directories the sandboxed app may write to.</summary>
    public IReadOnlyList<string> WritableItems => [Data, Tmp, State];

    /// <summary>Directories the sandboxed app may read and execute but not modify.</summary>
    public IReadOnlyList<string> ReadOnlyItems => [App, Shim];

    /// <summary>Items the sandboxed app gets no access to at all.</summary>
    public IReadOnlyList<string> PrivateItems => [ConfigFile, Icon, Source, Reports, Host, AppStaging, UnfinishedMarker];

    /// <summary>Directories that must exist for the box to be usable.</summary>
    public IReadOnlyList<string> Skeleton => [App, Shim, Data, Tmp, State, Source, Reports];

    public static BoxPaths ForBox(string boxesDirectory, Guid id) =>
        new(Path.Combine(boxesDirectory, id.ToString("D") + Extension));

    public static string PartialRoot(string boxesDirectory, Guid id) =>
        Path.Combine(boxesDirectory, id.ToString("D") + PartialExtension);

    public void CreateSkeleton()
    {
        foreach (var dir in Skeleton)
            Directory.CreateDirectory(dir);
    }
}
