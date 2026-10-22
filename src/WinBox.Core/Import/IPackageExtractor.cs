using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

/// <summary>Unpacks an installer's payload without running the installer.</summary>
public interface IPackageExtractor
{
    bool CanExtract(InstallerKind kind);

    /// <summary>
    /// Extracts into <paramref name="destination"/>. The returned result names the directory holding the
    /// app's own files and lists everything else the installer would have put elsewhere.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default);
}

/// <param name="AppDirectory">Files destined for the install directory ({app} for Inno Setup).</param>
/// <param name="SkippedRoots">Other install targets (system or common folders) that WinBox does not install.</param>
public sealed record ExtractionResult(string AppDirectory, IReadOnlyList<string> SkippedRoots);
