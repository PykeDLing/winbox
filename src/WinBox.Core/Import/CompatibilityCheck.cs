using System.Xml;
using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

/// <summary>An app measured not to run in a box, known by who signs it and the names it goes by.</summary>
/// <param name="Id">Stable: stored as the issue's detail, and what the UI words the reason by.</param>
/// <param name="Signer">The common name in the certificate its installer and its program are signed with.</param>
/// <param name="Names">Its product names, compared by letters and digits only, case ignored.</param>
public sealed record KnownApp(string Id, string Signer, IReadOnlyList<string> Names);

/// <summary>
/// Finds out, before a box is made, what keeps an app from running in it or limits it there. Nothing is run: it goes
/// by what the installer says about itself and by the files unpacked from it.
/// </summary>
public static class CompatibilityCheck
{
    /// <summary>Apps measured not to run in a box, with what was measured. An app comes off once it runs.</summary>
    public static IReadOnlyList<KnownApp> KnownIncompatible { get; } =
    [
        // WeChat 4.2 (2026-11-03): reads HKCU\...\Explorer\User Shell Folders and the host's Tencent keys, which the
        // sandbox denies whatever their permissions say, and opens volume devices (\??\STORAGE#Volume#...); it then
        // says "文件保存位置无法使用" and quits.
        new("wechat", "Tencent Technology (Shenzhen) Company Limited", ["微信", "微信电脑版", "Weixin", "WeChat"]),
        // Discord 1.0 (2026-11-03): exits with 0xC0000409 about four seconds after it starts.
        new("discord", "Discord Inc.", ["Discord"]),
    ];

    /// <summary>What only a Windows service program calls: it connects the program to the service control manager.</summary>
    private static readonly string[] ServiceEntryPoints = ["StartServiceCtrlDispatcherW", "StartServiceCtrlDispatcherA"];

    /// <summary>The measured app whose installer (or program) <paramref name="facts"/> describe, if any.</summary>
    public static CompatibilityIssue? Known(InstallerFacts facts) =>
        Known(EntryResolver.NamesOf(facts), () => facts.Signature?.CommonName);

    /// <summary>
    /// The measured app the program at <paramref name="program"/> is, judged by its file name and version
    /// information. Its signer is only read when a name matches: checking a signature reads the whole file.
    /// </summary>
    public static CompatibilityIssue? Known(string program, ExecutableInfo? info, Func<string?> signer) =>
        Known([Path.GetFileNameWithoutExtension(program), info?.ProductName ?? "", info?.FileDescription ?? ""], signer);

    private static CompatibilityIssue? Known(IReadOnlyList<string> names, Func<string?> signer)
    {
        var given = names.Select(EntryResolver.Normalize).Where(n => n.Length > 0).ToHashSet();
        var candidates = KnownIncompatible.Where(a => a.Names.Any(n => given.Contains(EntryResolver.Normalize(n)))).ToList();
        if (candidates.Count == 0)
            return null;
        var signedBy = signer();
        var app = candidates.FirstOrDefault(a => string.Equals(signedBy, a.Signer, StringComparison.OrdinalIgnoreCase));
        return app is null ? null : new CompatibilityIssue(IssueCode.KnownIncompatible, IssueSeverity.NeedsVirtualMachine, app.Id);
    }

    /// <summary>
    /// What the files unpacked under <paramref name="root"/> carry that a box cannot install: kernel drivers and
    /// Windows services. The installer's own folders and the places outside the app count too, since the installer
    /// would install from there.
    /// </summary>
    public static IReadOnlyList<CompatibilityIssue> ScanFiles(string root)
    {
        var drivers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var services = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".sys", StringComparison.OrdinalIgnoreCase) && PeFile.IsNative(file))
                drivers.Add(Path.GetFileName(file));
            else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && PeFile.ReadImportedFunctions(file).Overlaps(ServiceEntryPoints))
                services.Add(Path.GetFileName(file));
        }
        var issues = new List<CompatibilityIssue>();
        if (drivers.Count > 0)
            issues.Add(new CompatibilityIssue(IssueCode.DriverUnavailable, IssueSeverity.Limited, string.Join(";", drivers)));
        if (services.Count > 0)
            issues.Add(new CompatibilityIssue(IssueCode.ServiceUnavailable, IssueSeverity.Limited, string.Join(";", services)));
        return issues;
    }

    public const string RequireAdministrator = "requireAdministrator";
    public const string HighestAvailable = "highestAvailable";

    /// <summary>
    /// What the entry program asks for on start that a box cannot give: administrator rights. <see cref="RequireAdministrator"/>
    /// asks for them always, <see cref="HighestAvailable"/> on an <paramref name="administratorAccount"/>; the
    /// detail is the level. Measured (2026-11-03): starting either fails with ERROR_ELEVATION_REQUIRED in a box.
    /// Windows reads the manifest built into the program, or else the one next to it (<c>app.exe.manifest</c>).
    /// </summary>
    public static CompatibilityIssue? CheckEntry(string entry, bool administratorAccount)
    {
        var manifest = PeFile.ReadManifest(entry);
        if (manifest is null && File.Exists(entry + ".manifest"))
            manifest = File.ReadAllText(entry + ".manifest");
        var level = manifest is null ? null : ExecutionLevel(manifest);
        var elevates = string.Equals(level, RequireAdministrator, StringComparison.OrdinalIgnoreCase) ? RequireAdministrator
            : administratorAccount && string.Equals(level, HighestAvailable, StringComparison.OrdinalIgnoreCase) ? HighestAvailable
            : null;
        return elevates is null ? null : new CompatibilityIssue(IssueCode.NeedsAdministrator, IssueSeverity.NeedsVirtualMachine, elevates);
    }

    /// <summary>
    /// The level an application manifest's requestedExecutionLevel asks for ("asInvoker", <see cref="HighestAvailable"/>,
    /// <see cref="RequireAdministrator"/>), or null when it names none or is not well-formed. Comments are skipped: Visual
    /// Studio's template lists every level in one.
    /// </summary>
    public static string? ExecutionLevel(string manifest)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true };
            using var reader = XmlReader.Create(new StringReader(manifest), settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "requestedExecutionLevel")
                    return reader.GetAttribute("level")?.Trim();
            }
        }
        catch (XmlException)
        {
        }
        return null;
    }
}
