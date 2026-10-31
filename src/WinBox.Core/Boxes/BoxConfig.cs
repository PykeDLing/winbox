using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBox.Core.Boxes;

/// <summary>
/// The contents of box.json. Fields are only ever added, never renamed or removed, and every field
/// added after schema version 1 must be nullable so older files still load. Unknown fields written
/// by a newer WinBox are kept in <see cref="Extra"/> and written back unchanged.
/// </summary>
public sealed record BoxConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }

    /// <summary>Entry executable, relative to the box's App directory.</summary>
    public required string Entry { get; init; }

    /// <summary>Extra arguments WinBox adds on every launch (for example --user-data-dir for Chromium apps).</summary>
    public IReadOnlyList<string> LaunchArguments { get; init; } = [];

    public required BoxSource Source { get; init; }

    /// <summary>Null means network is allowed; written only when the user turns it off.</summary>
    public bool? BlockNetwork { get; init; }

    public IReadOnlyList<CompatibilityIssue> Issues { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The box's own MSIX package, registered so Windows redirects the app's new AppData files and HKCU
    /// writes into private package storage. Null until the package is registered.
    /// </summary>
    public BoxPackage? Package { get; init; }

    [JsonIgnore]
    public bool AllowsNetwork => BlockNetwork != true;

    // Settable, not init: source-generated deserialization fills init properties through the constructor,
    // which extension data cannot take part in.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Where the box's app came from, so WinBox can re-fetch or update it later.</summary>
public sealed record BoxSource
{
    public required string FileName { get; init; }
    public required InstallerKind Kind { get; init; }
    public string? Sha256 { get; init; }

    /// <summary>Recipe that resolved the dropped file to the real payload, if any (for example "quark-cloud-drive").</summary>
    public string? RecipeId { get; init; }

    /// <summary>The URL the payload was downloaded from when the dropped file was only a stub.</summary>
    public string? DownloadUrl { get; init; }
}

/// <param name="Name">Package identity name, for example "WinBox.Box.1838711ed6d7".</param>
/// <param name="FamilyName">Package family name; its private storage is %LOCALAPPDATA%\Packages\&lt;FamilyName&gt;.</param>
public sealed record BoxPackage(string Name, string FamilyName)
{
    public const string ApplicationId = "App";

    public string AppUserModelId => $"{FamilyName}!{ApplicationId}";
}

/// <summary>
/// A known limitation found while importing or running. <see cref="Code"/> is stable and is what the UI
/// translates; <see cref="Detail"/> carries data (a path, a registry key) and is never localized prose.
/// </summary>
public sealed record CompatibilityIssue(IssueCode Code, IssueSeverity Severity, string? Detail = null);

[JsonConverter(typeof(JsonStringEnumConverter<IssueSeverity>))]
public enum IssueSeverity
{
    Info,
    Limited,
    NeedsVirtualMachine,
}

[JsonConverter(typeof(JsonStringEnumConverter<IssueCode>))]
public enum IssueCode
{
    /// <summary>The installer's own scripts ([Run], [Code], registry, shortcuts) were not executed.</summary>
    InstallerScriptsSkipped,

    /// <summary>The installer ships a self-updater that WinBox does not run; WinBox manages updates itself.</summary>
    SelfUpdaterDisabled,

    /// <summary>The installer drops files outside the app directory (system or common folders) that were not installed.</summary>
    FilesOutsideAppSkipped,

    /// <summary>The app registers a shell extension (context menu) that is unavailable inside the box.</summary>
    ShellExtensionUnavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<InstallerKind>))]
public enum InstallerKind
{
    Unknown,
    InnoSetup,
    Nsis,
    Msi,
    Msix,
    Zip,
    PortableExe,

    /// <summary>A small online installer that downloads the real package at install time.</summary>
    Stub,
}
