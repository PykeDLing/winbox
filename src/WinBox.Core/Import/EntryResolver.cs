namespace WinBox.Core.Import;

/// <summary>An executable found in the extracted app directory.</summary>
/// <param name="RelativePath">Path relative to the app directory.</param>
public sealed record ExecutableCandidate(string RelativePath, long Size, ExecutableInfo? Info);

/// <summary>Chooses which executable is the app.</summary>
public static class EntryResolver
{
    private static readonly string[] ExcludedWords =
    [
        "unins", "uninstall", "卸载", "卸載", "update", "updater", "crashpad", "crash_handler",
        "setup", "installer", "helper", "proxy", "launcher_helper",
    ];

    /// <summary>
    /// Picks the recipe's entry when one is given; otherwise prefers top-level executables that are not
    /// uninstallers, updaters or helpers, and among those the largest. Returns null when nothing qualifies.
    /// </summary>
    public static ExecutableCandidate? Resolve(IReadOnlyList<ExecutableCandidate> candidates, string? preferredFileName = null)
    {
        if (preferredFileName is not null)
        {
            var preferred = candidates
                .Where(c => Path.GetFileName(c.RelativePath).Equals(preferredFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => Depth(c.RelativePath))
                .FirstOrDefault();
            if (preferred is not null)
                return preferred;
        }

        return candidates
            .Where(c => !IsExcluded(c))
            .OrderBy(c => Depth(c.RelativePath))
            .ThenByDescending(c => c.Size)
            .FirstOrDefault();
    }

    internal static bool IsExcluded(ExecutableCandidate candidate)
    {
        var name = Path.GetFileNameWithoutExtension(candidate.RelativePath);
        return ExcludedWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static int Depth(string relativePath) =>
        relativePath.Count(c => c is '\\' or '/');
}
