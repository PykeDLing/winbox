using System.Text;
using System.Text.RegularExpressions;

namespace WinBox.Core.Import;

/// <summary>An executable found in the extracted app directory.</summary>
/// <param name="RelativePath">Path relative to the app directory.</param>
/// <param name="Gui">A program with windows (true), a console program (false), or unknown.</param>
/// <param name="Chromium">It sits among Chromium's files: the main program of an Electron or CEF app.</param>
public sealed record ExecutableCandidate(string RelativePath, long Size, ExecutableInfo? Info, bool? Gui = null, bool Chromium = false);

/// <summary>Chooses which executable is the app.</summary>
public static partial class EntryResolver
{
    private static readonly string[] ExcludedWords =
    [
        "unins", "uninst", "卸载", "卸載", "update", "upgrade", "crashpad", "crash", "bugreport", "report", "dump",
        "setup", "installer", "helper", "proxy", "launcher_helper", "elevate", "service", "notification", "squirrel",
        "repair", "cleaner", "regsvr", "7z", "vc_redist", "vcredist", "dotnet", "webview2",
    ];

    /// <summary>
    /// Picks the app's entry: the recipe's when it names one, else the first program the installer itself points
    /// at (<paramref name="hints"/>), else the best-scoring program. A program scores for carrying the app's name
    /// (<paramref name="names"/>: the installer's product name, its file name), for having windows, for being
    /// Chromium's main program, and for sitting near the top; uninstallers, updaters and helpers drop out.
    /// Returns null when nothing qualifies.
    /// </summary>
    public static ExecutableCandidate? Resolve(
        IReadOnlyList<ExecutableCandidate> candidates,
        string? preferredFileName = null,
        IReadOnlyList<string>? hints = null,
        IReadOnlyList<string>? names = null)
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

        foreach (var hint in hints ?? [])
        {
            var hinted = candidates.FirstOrDefault(c => c.RelativePath.Equals(hint, StringComparison.OrdinalIgnoreCase));
            if (hinted is not null)
                return hinted;
        }

        var wanted = (names ?? []).Select(Normalize).Where(n => n.Length >= 2).Distinct().ToList();
        return candidates
            .Where(c => !IsExcluded(c) || NameScore(c, wanted) >= 100)
            .OrderByDescending(c => Score(c, wanted))
            .ThenByDescending(c => c.Size)
            .FirstOrDefault();
    }

    // A folder down costs more than any hint but the program's own name: apps keep their main program at the top
    // and helpers below it (WeChat's Weixin.exe over 4.2.0.12\WeixinExt.exe, whose version information says
    // "WeChat"), and go deeper only when nothing at the top qualifies (DingTalk's build\dist\DingTalk.exe).
    private static int Score(ExecutableCandidate candidate, IReadOnlyList<string> names) =>
        NameScore(candidate, names)
        + (candidate.Gui switch { true => 30, false => -30, null => 0 })
        + (candidate.Chromium ? 15 : 0)
        - 50 * Depth(candidate.RelativePath);

    /// <summary>How well the program's file name, or the product named in its version information, matches the app's names.</summary>
    private static int NameScore(ExecutableCandidate candidate, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return 0;
        var stem = Normalize(Path.GetFileNameWithoutExtension(candidate.RelativePath));
        var score = 0;
        foreach (var name in names)
        {
            if (stem == name)
                return 100;
            if (stem.Length >= 4 && name.Length >= 4 && (name.Contains(stem) || stem.Contains(name)))
                score = Math.Max(score, 60);
        }
        var product = Normalize(candidate.Info?.ProductName ?? "");
        var description = Normalize(candidate.Info?.FileDescription ?? "");
        if (names.Any(n => n == product || n == description))
            score = Math.Max(score, 25);
        return score;
    }

    internal static bool IsExcluded(ExecutableCandidate candidate)
    {
        var name = Path.GetFileNameWithoutExtension(candidate.RelativePath);
        return ExcludedWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static int Depth(string relativePath) =>
        relativePath.Count(c => c is '\\' or '/');

    /// <summary>
    /// Names an installer gives its app: its product name and its file name, without the version, processor,
    /// language and installer words around them ("BaiduNetdisk_8.9.7.101_x64.exe" is "BaiduNetdisk",
    /// "DiscordSetup.exe" is "Discord").
    /// </summary>
    public static IReadOnlyList<string> NamesOf(InstallerFacts facts)
    {
        var names = new List<string>();
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            // "VSCodeUserSetup" is "VS Code User Setup": installer words glued to the name come apart.
            var cleaned = InstallerWords().Replace(CamelCase().Replace(text, " "), " ");
            cleaned = string.Join(" ", cleaned.Split([' ', '_', '-', '.', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !NoiseToken().IsMatch(t)));
            if (cleaned.Length > 0)
                names.Add(cleaned);
        }
        Add(Path.GetFileNameWithoutExtension(facts.FileName));
        Add(facts.Executable?.ProductName);
        Add(facts.Executable?.FileDescription);
        return names;
    }

    [GeneratedRegex(@"\b(web)?(setup|installer|install|user|system|full|offline|online|release|stable|latest|portable)\b|安装程序|安装包|安装", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerWords();

    [GeneratedRegex(@"(?<=\p{Ll})(?=\p{Lu})")]
    private static partial Regex CamelCase();

    // Versions (8.9.7.101, v4.2.0, Build3181715), processors, and short language or channel codes.
    [GeneratedRegex(@"^(v?\d[\d.]*[a-z]?|build\d+|x64|x86|amd64|arm64|win64|win32|win|windows|64bit|32bit|ia32|pc|zh|cn|en|us|chs|cht)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseToken();

    /// <summary>Lower case, letters and digits only (any script), so "Visual Studio Code" and "visualstudiocode" compare equal.</summary>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}

/// <summary>Recognizes apps built on Chromium, whose own sandbox cannot start inside the box's.</summary>
public static class ChromiumApp
{
    /// <summary>
    /// Whether the program at <paramref name="exe"/> is the main program of a Chromium-based app: Electron (an
    /// app.asar or app folder under resources), CEF (libcef.dll), or Chromium itself (.pak resources next to
    /// icudtl.dat). Newer Electron apps keep those files in a versioned folder beside the program.
    /// </summary>
    public static bool IsMainProgram(string exe)
    {
        var dir = Path.GetDirectoryName(exe);
        if (dir is null || !Directory.Exists(dir))
            return false;
        return Marks(dir) || Directory.EnumerateDirectories(dir).Any(Marks);
    }

    private static bool Marks(string dir) =>
        File.Exists(Path.Combine(dir, "resources", "app.asar")) ||
        File.Exists(Path.Combine(dir, "resources", "app", "package.json")) ||
        File.Exists(Path.Combine(dir, "libcef.dll")) ||
        (File.Exists(Path.Combine(dir, "icudtl.dat")) && Directory.EnumerateFiles(dir, "*.pak").Any());

    /// <summary>What a Chromium-based app gets on every launch: its own sandbox off, since the box's sandbox isolates it.</summary>
    public static IReadOnlyList<string> LaunchArguments { get; } = ["--no-sandbox"];
}
