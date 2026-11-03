using System.Text.RegularExpressions;

namespace WinBox.Core.Import;

/// <summary>
/// What unpacking tools leave behind for each kind of installer, and how to get from there to the app's own
/// files. Only the file system is touched here; the tools themselves run in WinBox.Platform.
/// </summary>
public static partial class InstallerLayout
{
    /// <summary>
    /// Arranges what innounp unpacked into <paramref name="destination"/>: {app} holds the install directory, and
    /// folders such as {code_GetDestDir} the files an installer script places by computing the folder (most often
    /// the install directory itself; VS Code puts everything there). {tmp} is the installer's own, and the other
    /// constants ({sys}, {commonappdata}...) are places outside the app that WinBox does not install to.
    /// </summary>
    public static ExtractionResult ArrangeInno(string destination)
    {
        var app = Path.Combine(destination, "{app}");
        Directory.CreateDirectory(app);
        var skipped = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(destination))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("{app}", StringComparison.OrdinalIgnoreCase) || name.Equals("{tmp}", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.StartsWith("{code_", StringComparison.OrdinalIgnoreCase) && HoldsPrograms(dir))
                MoveMerge(dir, app);
            else if (name.StartsWith('{'))
                skipped.Add(name);
        }

        var script = Path.Combine(destination, "install_script.iss");
        var hints = File.Exists(script) ? InnoEntryHints(File.ReadAllText(script)) : [];
        RestoreHintedNames(app, hints);
        return new ExtractionResult(app, skipped, hints);
    }

    /// <summary>
    /// The programs an Inno Setup script makes shortcuts to ([Icons]) or offers to start when it is done ([Run]),
    /// relative to {app}: Start menu and desktop shortcuts first, then the uninstall icon. Targets whose name the
    /// script computes ("{app}\{code:GetExeName}") cannot be told and are left out.
    /// </summary>
    public static IReadOnlyList<string> InnoEntryHints(string script)
    {
        var icons = new List<string>();
        var runs = new List<string>();
        string? uninstallIcon = null;
        string? section = null;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            if (section is "Setup" && line.StartsWith("UninstallDisplayIcon=", StringComparison.OrdinalIgnoreCase))
                uninstallIcon = line["UninstallDisplayIcon=".Length..].Split(',')[0].Trim('"');
            else if (section is "Icons" && Parameter(line, "Filename") is { } icon)
            {
                var name = Parameter(line, "Name") ?? "";
                if (!name.Contains("unins", StringComparison.OrdinalIgnoreCase) && !name.Contains("卸载", StringComparison.Ordinal))
                    icons.Add(icon);
            }
            else if (section is "Run" && Parameter(line, "Filename") is { } run && (Parameter(line, "Flags") ?? "").Contains("postinstall", StringComparison.OrdinalIgnoreCase))
                runs.Add(run);
        }
        return [.. icons.Concat(runs).Append(uninstallIcon)
            .Select(InAppDirectory).OfType<string>()
            .Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? InAppDirectory(string? target)
    {
        const string prefix = @"{app}\";
        if (target is null || !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var relative = target[prefix.Length..];
        return relative.Contains('{') || relative.Contains("..") ? null : relative;
    }

    /// <summary>A parameter of an Inno Setup entry line: Name: "..."; Filename: "{app}\app.exe"; Flags: ...</summary>
    private static string? Parameter(string line, string name)
    {
        var match = Regex.Match(line, $@"(?:^|;)\s*{name}\s*:\s*(""(?<v>[^""]*)""|(?<v>[^;]*))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    /// <summary>
    /// Gives a program whose name the installer script computes the name its shortcuts use: when a shortcut names
    /// a program that is not there and the same folder holds exactly one program of a computed name
    /// ({code_GetExeBasename}), that is the one. The name in a program's version information is no good here:
    /// Electron apps keep "electron.exe" in it.
    /// </summary>
    private static void RestoreHintedNames(string app, IReadOnlyList<string> hints)
    {
        foreach (var hint in hints)
        {
            var target = Path.Combine(app, hint);
            if (File.Exists(target) || !Directory.Exists(Path.GetDirectoryName(target)))
                continue;
            var computed = Directory.EnumerateFiles(Path.GetDirectoryName(target)!)
                .Where(f => PlaceholderName(Path.GetFileName(f)) is not null && PeFile.IsExecutable(f))
                .Take(2)
                .ToList();
            if (computed.Count == 1)
                File.Move(computed[0], target);
        }
    }

    /// <summary>
    /// The script function behind a file name an installer computes at install time: "GetAppExeDestName" for
    /// innoextract's "code$GetAppExeDestName" and innounp's "{code_GetAppExeDestName}", null for ordinary names.
    /// </summary>
    public static string? PlaceholderName(string fileName)
    {
        if (fileName.StartsWith("code$", StringComparison.Ordinal))
            return fileName["code$".Length..];
        if (fileName.StartsWith("{code_", StringComparison.Ordinal) && fileName.EndsWith('}'))
            return fileName["{code_".Length..^1];
        return null;
    }

    private static bool HoldsPrograms(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(PeFile.IsExecutable);

    /// <summary>
    /// NSIS folders named after the installer's variables. $PLUGINSDIR, $TEMP and the numbered registers are the
    /// installer's own; the others ($SYSDIR, $APPDATA, $COMMONFILES...) are places outside the app. Files for the
    /// install directory ($INSTDIR) are at the top.
    /// </summary>
    public static bool IsNsisVariable(string name) => name.StartsWith('$');

    public static bool IsNsisInternal(string name) =>
        name.Equals("$PLUGINSDIR", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("$TEMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("$OUTDIR", StringComparison.OrdinalIgnoreCase) ||
        NsisRegister().IsMatch(name);

    [GeneratedRegex(@"^\$R?\d$")]
    private static partial Regex NsisRegister();

    private static readonly string[] ArchiveExtensions = [".7z", ".zip", ".nupkg"];

    /// <summary>Smaller archives are resources of the installer (skins, plug-ins), not the app.</summary>
    private const long MinimumPayload = 4 * 1024 * 1024;

    /// <summary>
    /// Archives in an unpacked installer that carry the app itself rather than the installer: electron-builder's
    /// $PLUGINSDIR\app-64.7z, WeChat's install.7z. Where an installer carries one per processor, x64 is taken.
    /// </summary>
    /// <returns>The archives to unpack into the app directory, and every candidate (the ones not taken included), which go afterwards.</returns>
    public static (IReadOnlyList<string> Unpack, IReadOnlyList<string> Remove) PayloadArchives(string root)
    {
        IEnumerable<string> Archives(string dir) => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => ArchiveExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && new FileInfo(f).Length >= MinimumPayload)
            : [];
        var candidates = Archives(root).Concat(Archives(Path.Combine(root, "$PLUGINSDIR"))).ToList();
        var byArch = candidates.GroupBy(ArchitectureOf).ToDictionary(g => g.Key, g => g.ToList());
        List<string> unpack = [.. byArch.GetValueOrDefault(Architecture.Neutral) ?? []];
        if (byArch.TryGetValue(Architecture.X64, out var x64))
            unpack.AddRange(x64);
        else if (byArch.TryGetValue(Architecture.X86, out var x86))
            unpack.AddRange(x86);
        return (unpack, candidates);
    }

    private enum Architecture { Neutral, X64, X86, Arm64 }

    private static Architecture ArchitectureOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("arm64") || name.Contains("aarch64"))
            return Architecture.Arm64;
        if (Regex.IsMatch(name, @"(x64|amd64|win64|[-_.]64(bit)?$)"))
            return Architecture.X64;
        if (Regex.IsMatch(name, @"(x86|ia32|win32|i386|[-_.]32(bit)?$)"))
            return Architecture.X86;
        return Architecture.Neutral;
    }

    /// <summary>
    /// The NuGet package a Squirrel installer carries (Discord-1.0.9266-full.nupkg next to RELEASES and
    /// Update.exe), or null when <paramref name="root"/> is not one.
    /// </summary>
    public static string? SquirrelPackage(string root) =>
        File.Exists(Path.Combine(root, "RELEASES"))
            ? Directory.EnumerateFiles(root, "*-full.nupkg").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
            : null;

    /// <summary>The version in a Squirrel package's name: "1.0.9266" for Discord-1.0.9266-full.nupkg.</summary>
    public static string SquirrelVersion(string package)
    {
        var match = Regex.Match(Path.GetFileName(package), @"-(\d+(?:\.\d+)+)-full\.nupkg$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "current";
    }

    /// <summary>Where a Squirrel package keeps the app: lib\net45 by convention, or the one folder under lib.</summary>
    public static string? SquirrelApp(string unpackedPackage)
    {
        var lib = Path.Combine(unpackedPackage, "lib");
        if (!Directory.Exists(lib))
            return null;
        var net45 = Path.Combine(lib, "net45");
        if (Directory.Exists(net45))
            return net45;
        var dirs = Directory.GetDirectories(lib);
        return dirs.Length == 1 ? dirs[0] : null;
    }

    /// <summary>Moves everything in <paramref name="source"/> into <paramref name="destination"/>, keeping what is there already.</summary>
    public static void MoveMerge(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Move(file, target);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(dir));
            if (Directory.Exists(target) || File.Exists(target))
                MoveMerge(dir, target);
            else
                Directory.Move(dir, target);
        }
        Directory.Delete(source, recursive: true);
    }
}
