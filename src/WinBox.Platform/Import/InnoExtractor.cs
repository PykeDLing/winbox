using WinBox.Core.Boxes;
using WinBox.Core.Import;

namespace WinBox.Platform.Import;

/// <summary>
/// Unpacks Inno Setup installers (1.2 up to 7.1) with innounp. Nothing from the installer runs: [Run], [Code],
/// [Registry] and [Icons] are all skipped, which the import records as a compatibility issue. innounp also writes
/// out the installer's script, whose shortcuts tell which program is the app.
/// </summary>
public sealed class InnoExtractor(string innounpPath) : IPackageExtractor
{
    public const string PathVariable = "WINBOX_INNOUNP";

    public static string? Locate() => Tools.Locate(PathVariable, @"innounp\innounp.exe", "innounp.exe");

    public InstallerKind? Recognize(InstallerFacts facts) => facts.Kind == InstallerKind.InnoSetup ? InstallerKind.InnoSetup : null;

    public async Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        // -x extract with paths, -b never ask (a password, the next disk), -q -h -o quiet, -y overwrite. innounp
        // writes past MAX_PATH on its own, which Quark's 140-character-deep files need.
        var (code, output) = await Tools.RunAsync(innounpPath,
            ["-x", "-b", "-q", "-y", "-h", "-o", "-u", "-d" + destination, installer], cancellationToken).ConfigureAwait(false);
        if (code != 0 || !Directory.EnumerateFileSystemEntries(destination).Any())
            throw new ImportException(ImportFailure.ExtractionFailed, $"innounp {code}: {Tools.Tail(output)}");

        var result = InstallerLayout.ArrangeInno(destination);
        if (!Directory.EnumerateFileSystemEntries(result.AppDirectory).Any())
            throw new ImportException(ImportFailure.NoEntryFound, "installer has no {app} files");
        return result;
    }
}
