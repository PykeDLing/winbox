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
        // innoextract stops at MAX_PATH unless its output directory carries the \\?\ prefix, and an app's files
        // can sit 140+ characters deep below the box's staging folder (Quark's do).
        foreach (var arg in new[] { "--silent", "--extract", "--output-dir", LongPath(destination), installer })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("innoextract did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Left running, innoextract would go on writing into the staging folder that is about to be deleted.
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // It finished in the meantime.
            }
            throw;
        }
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

    /// <summary>A drive path in the \\?\ form, which Win32 does not limit to MAX_PATH; anything else as it is.</summary>
    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 2 && full[1] == ':' ? @"\\?\" + full : full;
    }
}
