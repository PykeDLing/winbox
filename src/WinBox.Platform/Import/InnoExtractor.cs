using System.Diagnostics;
using WinBox.Core.Boxes;
using WinBox.Core.Import;

namespace WinBox.Platform.Import;

/// <summary>
/// Unpacks Inno Setup installers with innoextract. Nothing from the installer runs: [Run], [Code],
/// [Registry] and [Icons] are all skipped, which the import records as a compatibility issue.
/// </summary>
public sealed class InnoExtractor(string innoextractPath) : IPackageExtractor
{
    public const string PathVariable = "WINBOX_INNOEXTRACT";

    /// <summary>Directories innoextract writes for installer-only temporary files; never part of the app.</summary>
    private static readonly HashSet<string> InstallerOnlyRoots = new(StringComparer.OrdinalIgnoreCase) { "tmp" };

    /// <summary>Finds innoextract: WINBOX_INNOEXTRACT, then a copy next to WinBox, then PATH.</summary>
    public static string? Locate()
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "innoextract.exe");
        if (File.Exists(bundled))
            return bundled;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return pathDirs.Select(d => Path.Combine(d, "innoextract.exe")).FirstOrDefault(File.Exists);
    }

    public bool CanExtract(InstallerKind kind) => kind == InstallerKind.InnoSetup;

    public async Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        var psi = new ProcessStartInfo(innoextractPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "--silent", "--extract", "--output-dir", destination, installer })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("innoextract did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"innoextract exited with {process.ExitCode}: {(await stderr).Trim()} {(await stdout).Trim()}");

        var app = Path.Combine(destination, "app");
        if (!Directory.Exists(app))
            throw new ImportException(ImportFailure.NoEntryFound, "installer has no {app} files");

        var skipped = Directory.EnumerateDirectories(destination)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.Equals("app", StringComparison.OrdinalIgnoreCase) && !InstallerOnlyRoots.Contains(n))
            .Select(n => n!)
            .ToList();
        return new ExtractionResult(app, skipped);
    }
}
