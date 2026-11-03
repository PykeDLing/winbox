using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

/// <summary>Unpacks an installer's payload without running the installer.</summary>
public interface IPackageExtractor
{
    /// <summary>
    /// The kind of installer this extractor takes <paramref name="facts"/> for, or null when it cannot unpack it.
    /// <see cref="InstallerFacts.Kind"/> is what the file's bytes say (see <see cref="InstallerDetector"/>); an
    /// extractor may look closer, for example to find the archive behind a self-extracting program.
    /// </summary>
    InstallerKind? Recognize(InstallerFacts facts);

    /// <summary>
    /// Extracts into <paramref name="destination"/>. The returned result names the directory holding the
    /// app's own files and lists everything else the installer would have put elsewhere.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default);
}

/// <param name="AppDirectory">Files destined for the install directory ({app} for Inno Setup, $INSTDIR for NSIS).</param>
/// <param name="SkippedRoots">Other install targets (system or common folders) that WinBox does not install.</param>
/// <param name="EntryHints">
/// Programs the installer itself makes shortcuts to or starts when it is done, relative to
/// <paramref name="AppDirectory"/>, the most telling first. The app's entry is the first of them that exists.
/// </param>
public sealed record ExtractionResult(string AppDirectory, IReadOnlyList<string> SkippedRoots, IReadOnlyList<string>? EntryHints = null);
