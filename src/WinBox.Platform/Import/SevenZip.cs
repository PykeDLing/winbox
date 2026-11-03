using WinBox.Core.Import;

namespace WinBox.Platform.Import;

/// <summary>
/// 7-Zip (7z.exe with its 7z.dll): reads NSIS installers, 7z, zip, CAB, RAR and ISO archives, and finds the
/// archive inside a self-extracting program. It only ever reads the installer; nothing in it runs.
/// </summary>
public sealed class SevenZip(string executable)
{
    public const string PathVariable = "WINBOX_7ZIP";

    public static string? Locate() => Tools.Locate(PathVariable, @"7zip\7z.exe", "7z.exe");

    /// <summary>What 7-Zip finds in a file: its format and its top-level entries.</summary>
    /// <param name="Type">7-Zip's name for the format ("Nsis", "7z", "zip"...); for a program carrying an archive, the archive's.</param>
    public sealed record Listing(string Type, IReadOnlyList<string> Entries);

    /// <summary>The archive 7-Zip finds in <paramref name="path"/>, or null when it finds nothing but a program.</summary>
    public Listing? List(string path)
    {
        var (code, output) = Tools.RunAsync(executable, ["l", "-slt", "-sccUTF-8", "--", path], CancellationToken.None).GetAwaiter().GetResult();
        if (code != 0)
            return null;
        // The header names one format per level (a program, then the archive inside it) before the "----------"
        // line; the entries follow, one "Path = " each.
        string? type = null;
        var entries = new List<string>();
        var inEntries = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("----------", StringComparison.Ordinal))
                inEntries = true;
            else if (!inEntries && line.StartsWith("Type = ", StringComparison.Ordinal))
                type = line["Type = ".Length..];
            else if (inEntries && line.StartsWith("Path = ", StringComparison.Ordinal))
                entries.Add(line["Path = ".Length..]);
        }
        return type is null || type.Equals("PE", StringComparison.OrdinalIgnoreCase) ? null : new Listing(type, entries);
    }

    /// <summary>Unpacks <paramref name="archive"/> into <paramref name="destination"/>, replacing files that are there already.</summary>
    public async Task ExtractAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var (code, output) = await Tools.RunAsync(executable,
            ["x", "-y", "-aoa", "-bso0", "-bsp0", "-sccUTF-8", "-o" + destination, "--", archive], cancellationToken).ConfigureAwait(false);
        // 1 is a warning (a file it could not write, say); 2 and up mean the archive could not be read.
        if (code >= 2)
            throw new ImportException(ImportFailure.ExtractionFailed, $"7-Zip {code}: {Tools.Tail(output)}");
    }
}
