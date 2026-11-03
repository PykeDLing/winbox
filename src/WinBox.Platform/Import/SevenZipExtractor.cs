using WinBox.Core.Boxes;
using WinBox.Core.Import;

namespace WinBox.Platform.Import;

/// <summary>
/// Unpacks the installers and archives 7-Zip reads: NSIS installers (electron-builder's, most Chinese desktop
/// apps'), programs carrying a 7z or zip archive (self-extracting installers, Squirrel's Setup.exe), and plain
/// archives of portable apps. Where the installer carries the app in an archive of its own (an app-64.7z, an
/// install.7z, a NuGet package), that is unpacked too.
/// </summary>
public sealed class SevenZipExtractor(SevenZip sevenZip) : IPackageExtractor
{
    /// <summary>The formats taken for an app's archive; the others 7-Zip reads (disk images of other systems, documents) are not.</summary>
    private static readonly HashSet<string> ArchiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "7z", "zip", "Rar", "Rar5", "Cab", "Iso", "Udf", "Wim",
    };

    public InstallerKind? Recognize(InstallerFacts facts)
    {
        if (facts.Kind is InstallerKind.InnoSetup or InstallerKind.Msi or InstallerKind.Msix)
            return null;
        if (sevenZip.List(facts.Path) is not { } listing)
            return null;
        if (listing.Type.Equals("Nsis", StringComparison.OrdinalIgnoreCase))
            return InstallerKind.Nsis;
        if (!ArchiveTypes.Contains(listing.Type))
            return null;
        if (listing.Type.Equals("zip", StringComparison.OrdinalIgnoreCase) &&
            listing.Entries.Contains("RELEASES", StringComparer.OrdinalIgnoreCase) &&
            listing.Entries.Any(e => e.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase)))
            return InstallerKind.Squirrel;
        return listing.Type switch
        {
            "7z" => InstallerKind.SevenZip,
            "zip" => InstallerKind.Zip,
            _ => InstallerKind.Archive,
        };
    }

    public async Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default)
    {
        var app = Path.Combine(destination, "app");
        await sevenZip.ExtractAsync(installer, app, cancellationToken).ConfigureAwait(false);

        // NSIS: files for the install directory are at the top; folders named after variables are the installer's
        // own ($PLUGINSDIR) or places outside the app ($SYSDIR, $APPDATA), which are not installed.
        var variables = kind == InstallerKind.Nsis
            ? Directory.EnumerateDirectories(app).Where(d => InstallerLayout.IsNsisVariable(Path.GetFileName(d))).ToList()
            : [];
        var skipped = variables.Select(Path.GetFileName).OfType<string>().Where(n => !InstallerLayout.IsNsisInternal(n)).ToList();

        // Squirrel: laid out as Squirrel installs it, Update.exe next to app-<version>\ holding the package's lib
        // folder. The apps count on that: Discord's updater fetches its core modules into app-<version>\modules.
        if (InstallerLayout.SquirrelPackage(app) is { } package)
        {
            var unpacked = Path.Combine(destination, "package");
            await sevenZip.ExtractAsync(package, unpacked, cancellationToken).ConfigureAwait(false);
            var lib = InstallerLayout.SquirrelApp(unpacked)
                ?? throw new ImportException(ImportFailure.NoEntryFound, "the Squirrel package has no lib folder");
            var squirrel = Path.Combine(destination, "squirrel");
            Directory.CreateDirectory(squirrel);
            Directory.Move(lib, Path.Combine(squirrel, "app-" + InstallerLayout.SquirrelVersion(package)));
            var update = Path.Combine(app, "Update.exe");
            if (File.Exists(update))
                File.Move(update, Path.Combine(squirrel, "Update.exe"));
            var packages = Directory.CreateDirectory(Path.Combine(squirrel, "packages")).FullName;
            File.Move(Path.Combine(app, "RELEASES"), Path.Combine(packages, "RELEASES"));
            return new ExtractionResult(squirrel, skipped);
        }

        var (unpack, remove) = InstallerLayout.PayloadArchives(app);
        foreach (var archive in unpack)
            await sevenZip.ExtractAsync(archive, app, cancellationToken).ConfigureAwait(false);
        foreach (var archive in remove)
            File.Delete(archive);
        foreach (var dir in variables)
            Directory.Delete(dir, recursive: true);
        return new ExtractionResult(app, skipped);
    }
}
