using System.Text.RegularExpressions;

namespace WinBox.Core.Guard;

/// <summary>Which registry locations the host guard looks at, and what a box path found there means.</summary>
public static partial class HostReferenceRules
{
    private const string UserAssist = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    // Key paths are relative to HKCU and compared case-insensitively by prefix.

    /// <summary>
    /// Not scanned. Package registrations point at each box's Host folder by design, and Explorer's
    /// history records what the user browsed or typed, not what an app did.
    /// </summary>
    private static readonly string[] Ignored =
    [
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel",
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer",
        @"Software\Classes\Local Settings\MrtCache",
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU",
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags",
        @"Software\Classes\ActivatableClasses",
        @"Software\Classes\PackagedCom",
        @"Software\Classes\Extensions\ContractId",
        @"Software\Microsoft\Windows\CurrentVersion\AppModel",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StreamMRU",
    ];

    /// <summary>Windows' records of programs that ran. Explorer writes some of these for sandboxed apps too.</summary>
    private static readonly string[] Traces =
    [
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage",
        UserAssist,
        @"Software\Microsoft\Windows\CurrentVersion\Search\JumplistData",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant",
        @"Control Panel\NotifyIconSettings",
        // Per-program settings the user made (GPU choice, compatibility mode); they cannot start anything.
        @"Software\Microsoft\DirectX\UserGpuPreferences",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
    ];

    /// <summary>Top-level keys the guard walks.</summary>
    public static IReadOnlyList<string> RegistryRoots { get; } = ["Software", "Control Panel", "Environment"];

    public static bool IsIgnoredKey(string key) => Ignored.Any(p => IsUnder(key, p));

    public static HostReferenceKind ClassifyKey(string key) =>
        Traces.Any(p => IsUnder(key, p)) ? HostReferenceKind.Trace : HostReferenceKind.Leak;

    /// <summary>
    /// Whether removing a reference in <paramref name="key"/> takes the whole key rather than one value:
    /// a tray icon's settings key describes one program and is useless without its path.
    /// </summary>
    public static bool RemovesWholeKey(string key) =>
        IsUnder(key, @"Control Panel\NotifyIconSettings") && key.Count(c => c == '\\') == 2;

    /// <summary>
    /// Whether the values in <paramref name="key"/> are ';'-separated lists (environment variables such as
    /// PATH): one entry pointing into a box must not cost the user the whole list.
    /// </summary>
    public static bool HoldsLists(string key) => IsUnder(key, "Environment");

    /// <summary>
    /// The entries of a list value that do not mention <paramref name="root"/>, or null when none are left
    /// and the value should go entirely.
    /// </summary>
    public static IReadOnlyList<string>? WithoutMentions(IReadOnlyList<string> entries, string root)
    {
        var kept = entries.Where(e => !Mentions(e, root)).ToList();
        return kept.Any(e => e.Trim().Length > 0) ? kept : null;
    }

    /// <summary>Whether <paramref name="text"/> names <paramref name="root"/> or anything inside it.</summary>
    public static bool Mentions(string text, string root)
    {
        var trimmed = root.TrimEnd('\\', '/');
        var at = text.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var end = at + trimmed.Length;
            // "...\Boxes" must not match "...\BoxesOld".
            if (end == text.Length || text[end] is '\\' or '/' or '"' or ' ' or ',' or ';' or '\0')
                return true;
            at = text.IndexOf(trimmed, at + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Whether the value names in <paramref name="key"/> are ROT13-encoded program paths. Explorer's UserAssist
    /// records every program started that way, so a box path never appears there as written.
    /// </summary>
    public static bool HasEncodedNames(string key) =>
        IsUnder(key, UserAssist) && key.EndsWith(@"\Count", StringComparison.OrdinalIgnoreCase);

    /// <summary>ROT13 of the ASCII letters, the encoding UserAssist applies to its value names (it is its own inverse).</summary>
    public static string Rot13(string text) => string.Create(text.Length, text, static (chars, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            chars[i] = c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            };
        }
    });

    /// <summary>
    /// The box package family a registry key or value name stands for, if any. Windows files some records of a
    /// package under its family name or app id rather than under a path, and keeps several of them after the
    /// package is removed: "WinBox.Box.1838711ed6d7_skdvrgrnjqq2t", "…!App", or a CloudStore key ending in
    /// "$winbox.box.1838711ed6d7_skdvrgrnjqq2t".
    /// </summary>
    public static string? BoxPackageFamilyIn(string name)
    {
        var match = BoxPackageName().Match(name);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?:^|\$)(winbox\.box\.[0-9a-f]{12}_[0-9a-z]{13})(?:!app)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoxPackageName();

    /// <summary>The entry directly under <paramref name="boxesRoot"/> that <paramref name="text"/> names ("{id}.box"), if any.</summary>
    public static string? BoxFolderIn(string text, string boxesRoot)
    {
        var prefix = boxesRoot.TrimEnd('\\', '/') + @"\";
        var at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        var start = at + prefix.Length;
        var end = text.IndexOfAny(['\\', '/', '"', '\0', ',', ';'], start);
        var folder = end < 0 ? text[start..] : text[start..end];
        return folder.Length == 0 ? null : folder;
    }

    private static bool IsUnder(string key, string prefix) =>
        key.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase);
}
