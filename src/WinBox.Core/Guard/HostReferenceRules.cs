namespace WinBox.Core.Guard;

/// <summary>Which registry locations the host guard looks at, and what a box path found there means.</summary>
public static class HostReferenceRules
{
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
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",
        @"Software\Microsoft\Windows\CurrentVersion\Search\JumplistData",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant",
        @"Control Panel\NotifyIconSettings",
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

    private static bool IsUnder(string key, string prefix) =>
        key.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase);
}
