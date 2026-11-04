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
///   State\          writable  (state BoxShim carries between runs, e.g. the sandbox's LocalAppData;
///                              State\Control holds WinBox's requests to BoxShim and its last result)
///   Source\         private   (the installer the user dropped in)
///   Reports\        private   (run reports, newest 20 kept)
///   Host\           private   (the box's MSIX package: manifest, BoxHost, launch.json, run.json)
///   App.new\        private   (an update's new app, until it takes App\'s place; see BoxStore.ReplaceApp)
///   App.old\        private   (the app an update replaced, until it moves into the backup or is deleted)
///   .unfinished     private   (an update's new box.json, while App\ is being swapped)
///   Backup\         private   (the box as it was before its last update, to go back to; see BoxBackup)
///   Backup.new\     private   (the backup an update is making, until the update is done)
///   Backup.swap\    private   (the box as it was, while going back swaps it with the backup)
///   .going-back     private   (box.json once the box has gone back, while the swap runs)
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

    /// <summary>
    /// The user profile the app sees (USERPROFILE inside the sandbox): its AppData is the box's own, and its personal
    /// folders (Desktop, Documents...) are links to the user's real ones. See WinBox.Platform's BoxProfile.
    /// </summary>
    public string Profile => Path.Combine(Data, "Profile");

    /// <summary>The app's Roaming AppData (APPDATA inside the sandbox), kept with the rest of its data.</summary>
    public string Roaming => Path.Combine(Profile, "AppData", "Roaming");
    public string Tmp => Path.Combine(Root, "Tmp");
    public string Shim => Path.Combine(Root, "Shim");
    public string ShimExecutable => Path.Combine(Shim, Sandbox.ShimArguments.ExecutableName);
    public string State => Path.Combine(Root, "State");
    public string LocalAppDataState => Path.Combine(State, "LocalAppData");
    public string Control => Path.Combine(State, "Control");
    public string Source => Path.Combine(Root, "Source");
    public string Reports => Path.Combine(Root, "Reports");
    public string Host => Path.Combine(Root, "Host");
    public string AppStaging => Path.Combine(Root, "App.new");
    public string AppPrevious => Path.Combine(Root, "App.old");
    public string UnfinishedMarker => Path.Combine(Root, ".unfinished");
    public string Backup => Path.Combine(Root, "Backup");
    public string BackupStaging => Path.Combine(Root, "Backup.new");
    public string BackupSwap => Path.Combine(Root, "Backup.swap");
    public string GoingBackMarker => Path.Combine(Root, ".going-back");

    /// <summary>
    /// What a backup holds of the box, relative to the box's root and to the backup's: the parts going back swaps.
    /// The saved state is only its LocalAppData; State\Control is WinBox's talk with BoxShim, not the app's.
    /// </summary>
    public static IReadOnlyList<string> BackedUpItems { get; } = ["App", "Data", Path.Combine("State", "LocalAppData"), "Source"];

    /// <summary>Directories the sandboxed app may write to.</summary>
    public IReadOnlyList<string> WritableItems => [Data, Tmp, State];

    /// <summary>Directories the sandboxed app may read and execute but not modify.</summary>
    public IReadOnlyList<string> ReadOnlyItems => [App, Shim];

    /// <summary>Items the sandboxed app gets no access to at all.</summary>
    public IReadOnlyList<string> PrivateItems =>
        [ConfigFile, Icon, Source, Reports, Host, AppStaging, AppPrevious, UnfinishedMarker, Backup, BackupStaging, BackupSwap, GoingBackMarker];

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
