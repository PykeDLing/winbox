using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

/// <summary>
/// Per-app knowledge WinBox cannot infer generically: how to turn an online stub into the real package,
/// which executable is the app, which arguments it needs, and what the skipped installer would have done.
/// </summary>
public interface IRecipe
{
    string Id { get; }

    /// <summary>True when this recipe knows the dropped file (a stub, or the full package itself).</summary>
    bool Matches(InstallerFacts facts);

    /// <summary>When the dropped file is a stub, where the full package is; null when it already is the full package.</summary>
    PayloadSource? ResolvePayload(InstallerFacts facts);

    /// <summary>Signer common name the payload must carry; downloads signed by anyone else are rejected.</summary>
    string ExpectedSigner { get; }

    /// <summary>Entry executable file name (after placeholder names have been restored).</summary>
    string EntryFileName { get; }

    /// <summary>Arguments added on every launch. May contain <see cref="Sandbox.LaunchPlanner.DataPlaceholder"/>.</summary>
    IReadOnlyList<string> LaunchArguments { get; }

    string DisplayName { get; }

    IReadOnlyList<CompatibilityIssue> KnownIssues { get; }

    /// <summary>
    /// Real names for files whose names the installer script computes, keyed by the script function
    /// ("GetAppWidgetExeName" for a file unpacked as "{code_GetAppWidgetExeName}"). They win over the
    /// name in the file's version information.
    /// </summary>
    IReadOnlyDictionary<string, string> PlaceholderFileNames => new Dictionary<string, string>();
}

public sealed record PayloadSource(Uri Url, InstallerKind ExpectedKind);
