using System.Runtime.InteropServices;
using WinBox.Core.Boxes;
using WinBox.Core.Import;

namespace WinBox.Platform.Import;

/// <summary>
/// Unpacks Windows Installer packages without installing them: the package's database (opened read-only through
/// msi.dll, which runs nothing) says which file goes to which folder, its cabinets hold the files under their
/// database keys, and 7-Zip unpacks those. Custom actions, registry entries and services are not carried out.
/// </summary>
public sealed partial class MsiExtractor(SevenZip sevenZip) : IPackageExtractor
{
    /// <summary>Folders Windows Installer resolves itself. Files for the app sit under one of the program folders.</summary>
    private static readonly HashSet<string> SystemFolders = new(StringComparer.Ordinal)
    {
        "ProgramFilesFolder", "ProgramFiles64Folder", "ProgramFiles6432Folder", "CommonFilesFolder", "CommonFiles64Folder",
        "CommonFiles6432Folder", "LocalAppDataFolder", "AppDataFolder", "CommonAppDataFolder", "SystemFolder",
        "System64Folder", "System16Folder", "WindowsFolder", "WindowsVolume", "TempFolder", "DesktopFolder", "ProgramMenuFolder",
        "StartMenuFolder", "StartupFolder", "PersonalFolder", "FontsFolder", "SendToFolder", "TemplateFolder",
        "FavoritesFolder", "NetHoodFolder", "PrintHoodFolder", "RecentFolder", "AdminToolsFolder", "MyPicturesFolder",
    };

    private static readonly string[] ProgramFolders =
        ["ProgramFiles64Folder", "ProgramFilesFolder", "ProgramFiles6432Folder", "LocalAppDataFolder", "AppDataFolder"];

    public InstallerKind? Recognize(InstallerFacts facts) => facts.Kind == InstallerKind.Msi ? InstallerKind.Msi : null;

    public async Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default)
    {
        var layout = ReadLayout(installer);
        var cabinets = Path.Combine(destination, "cab");
        var unpacked = Path.Combine(destination, "files");
        Directory.CreateDirectory(cabinets);
        foreach (var cabinet in layout.Cabinets)
        {
            string path;
            if (cabinet.StartsWith('#'))
            {
                path = Path.Combine(cabinets, cabinet[1..]);
                WriteStream(installer, cabinet[1..], path);
            }
            else
            {
                path = Path.Combine(Path.GetDirectoryName(installer)!, cabinet);
                if (!File.Exists(path))
                    throw new ImportException(ImportFailure.ExtractionFailed, $"cabinet {cabinet} is not next to the package");
            }
            await sevenZip.ExtractAsync(path, unpacked, cancellationToken).ConfigureAwait(false);
        }

        // Every file to where the package would install it, under the name of the Windows folder it starts in.
        var tree = Path.Combine(destination, "tree");
        foreach (var (key, relative) in layout.Files)
        {
            var from = Path.Combine(unpacked, key);
            if (!File.Exists(from))
                continue; // Not compressed into a cabinet (an uncompressed source layout): not supported.
            var to = Path.Combine(tree, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
        }
        if (!Directory.Exists(tree))
            throw new ImportException(ImportFailure.ExtractionFailed, "the package's files are not in cabinets");

        var appRelative = AppFolder(layout);
        var app = Path.Combine(tree, appRelative);
        var skipped = Directory.EnumerateDirectories(tree).Select(Path.GetFileName).OfType<string>()
            .Where(n => !appRelative.StartsWith(n + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && n != appRelative)
            .ToList();
        var hints = layout.Shortcuts
            .Where(s => s.StartsWith(appRelative + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(s => s[(appRelative.Length + 1)..])
            .ToList();
        return new ExtractionResult(app, skipped, hints);
    }

    /// <summary>
    /// The app's folder: the deepest folder holding every file of the program folder with the most files (so
    /// "ProgramFiles64Folder\PowerShell\7", not the vendor folder above it).
    /// </summary>
    private static string AppFolder(MsiLayout layout)
    {
        var byRoot = layout.Files.Select(f => f.Relative)
            .Where(r => ProgramFolders.Any(p => r.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .GroupBy(r => r[..r.IndexOf(Path.DirectorySeparatorChar)])
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()
            ?? layout.Files.Select(f => f.Relative).GroupBy(r => r.Split(Path.DirectorySeparatorChar)[0]).OrderByDescending(g => g.Count()).First();
        var common = byRoot.Select(r => Path.GetDirectoryName(r)!).Aggregate(CommonFolder);
        // Directly in the program folder there is no app folder of its own; take the busiest folder below it.
        if (!common.Contains(Path.DirectorySeparatorChar))
        {
            var busiest = byRoot.Where(r => r.Count(c => c == Path.DirectorySeparatorChar) >= 2)
                .GroupBy(r => string.Join(Path.DirectorySeparatorChar, r.Split(Path.DirectorySeparatorChar)[..2]))
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            if (busiest is not null)
                common = busiest.Key;
        }
        return common;
    }

    private static string CommonFolder(string a, string b)
    {
        var x = a.Split(Path.DirectorySeparatorChar);
        var y = b.Split(Path.DirectorySeparatorChar);
        var n = 0;
        while (n < x.Length && n < y.Length && string.Equals(x[n], y[n], StringComparison.OrdinalIgnoreCase))
            n++;
        return string.Join(Path.DirectorySeparatorChar, x[..n]);
    }

    /// <param name="Files">Each file's database key and where it goes, relative to a Windows folder ("ProgramFiles64Folder\PowerShell\7\pwsh.exe").</param>
    /// <param name="Cabinets">The cabinets holding the files: "#name" for one inside the package.</param>
    /// <param name="Shortcuts">Where the package's shortcuts point, relative like the files.</param>
    private sealed record MsiLayout(IReadOnlyList<(string Key, string Relative)> Files, IReadOnlyList<string> Cabinets, IReadOnlyList<string> Shortcuts);

    private static MsiLayout ReadLayout(string package)
    {
        using var db = Database.Open(package);
        var directories = db.Query("SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`")
            .ToDictionary(r => r[0]!, r => (Parent: r[1], Name: TargetName(r[2] ?? ".")));
        var resolved = new Dictionary<string, string>();
        string Resolve(string dir, int depth = 0)
        {
            if (resolved.TryGetValue(dir, out var known))
                return known;
            if (depth > 64 || !directories.TryGetValue(dir, out var entry))
                return dir;
            string path;
            if (SystemFolders.Contains(dir))
                path = dir;
            else if (entry.Parent is null || entry.Parent == dir)
                path = ""; // TARGETDIR
            else
            {
                var parent = Resolve(entry.Parent, depth + 1);
                path = entry.Name == "." ? parent : parent.Length == 0 ? entry.Name : Path.Combine(parent, entry.Name);
            }
            return resolved[dir] = path;
        }

        var components = db.Query("SELECT `Component`, `Directory_`, `KeyPath` FROM `Component`")
            .ToDictionary(r => r[0]!, r => (Directory: r[1] ?? "", KeyPath: r[2]));
        var files = new List<(string Key, string Relative)>();
        var fileDirs = new Dictionary<string, string>();
        foreach (var row in db.Query("SELECT `File`, `Component_`, `FileName` FROM `File`"))
        {
            if (!components.TryGetValue(row[1] ?? "", out var component))
                continue;
            var relative = Path.Combine(Resolve(component.Directory), LongName(row[2] ?? row[0]!));
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains(".."))
                continue;
            files.Add((row[0]!, relative));
            fileDirs[row[0]!] = relative;
        }

        var cabinets = db.Query("SELECT `Cabinet` FROM `Media`").Select(r => r[0]).OfType<string>().Where(c => c.Length > 0).ToList();

        var shortcuts = new List<string>();
        if (db.HasTable("Shortcut"))
        {
            foreach (var row in db.Query("SELECT `Target`, `Component_` FROM `Shortcut`"))
            {
                var target = row[0] ?? "";
                // "[#FileKey]" names a file; anything else is a feature (an advertised shortcut), whose
                // component's key file is what it starts.
                var key = target.StartsWith("[#") && target.EndsWith(']') ? target[2..^1]
                    : components.GetValueOrDefault(row[1] ?? "").KeyPath;
                if (key is not null && fileDirs.TryGetValue(key, out var path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    shortcuts.Add(path);
            }
        }
        return new MsiLayout(files, cabinets, shortcuts);
    }

    /// <summary>The target part of a DefaultDir ("target:source"), long name of "short|long".</summary>
    private static string TargetName(string defaultDir) => LongName(defaultDir.Split(':')[0]);

    private static string LongName(string name) => name.Split('|') is [_, var full] ? full : name;

    private static unsafe void WriteStream(string package, string name, string path)
    {
        using var db = Database.Open(package);
        using var view = db.View($"SELECT `Data` FROM `_Streams` WHERE `Name` = '{name.Replace("'", "''")}'");
        var record = view.Fetch() ?? throw new ImportException(ImportFailure.ExtractionFailed, $"stream {name} is missing");
        using (record)
        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[1 << 16];
            while (true)
            {
                var size = (uint)buffer.Length;
                fixed (byte* data = buffer)
                    Check(MsiRecordReadStream(record.Handle, 1, data, ref size));
                if (size == 0)
                    break;
                output.Write(buffer, 0, (int)size);
            }
        }
    }

    private sealed class Database : IDisposable
    {
        private Database(nint handle) => Handle = handle;

        public nint Handle { get; }

        public static Database Open(string path)
        {
            // MSIDBOPEN_READONLY is the null pointer.
            Check(MsiOpenDatabaseW(path, 0, out var handle));
            return new Database(handle);
        }

        public View View(string query)
        {
            Check(MsiDatabaseOpenViewW(Handle, query, out var view));
            Check(MsiViewExecute(view, 0));
            return new View(view);
        }

        public bool HasTable(string table) => MsiDatabaseIsTablePersistentW(Handle, table) == 1; // MSICONDITION_TRUE

        public List<string?[]> Query(string query)
        {
            using var view = View(query);
            var rows = new List<string?[]>();
            while (view.Fetch() is { } record)
            {
                using (record)
                {
                    var count = MsiRecordGetFieldCount(record.Handle);
                    var row = new string?[count];
                    for (uint i = 1; i <= count; i++)
                        row[i - 1] = record.GetString(i);
                    rows.Add(row);
                }
            }
            return rows;
        }

        public void Dispose() => MsiCloseHandle(Handle);
    }

    private sealed class View(nint handle) : IDisposable
    {
        public Record? Fetch()
        {
            var result = MsiViewFetch(handle, out var record);
            if (result == ErrorNoMoreItems)
                return null;
            Check(result);
            return new Record(record);
        }

        public void Dispose() => MsiCloseHandle(handle);
    }

    private sealed unsafe class Record(nint handle) : IDisposable
    {
        public nint Handle => handle;

        public string? GetString(uint field)
        {
            if (MsiRecordIsNull(handle, field) != 0)
                return null;
            uint size = 0;
            char empty = '\0';
            var result = MsiRecordGetStringW(handle, field, &empty, ref size);
            if (result != ErrorMoreData && result != 0)
                Check(result);
            var buffer = new char[size + 1];
            size++;
            fixed (char* text = buffer)
                Check(MsiRecordGetStringW(handle, field, text, ref size));
            return new string(buffer, 0, (int)size);
        }

        public void Dispose() => MsiCloseHandle(handle);
    }

    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;

    private static void Check(uint result)
    {
        if (result != 0)
            throw new ImportException(ImportFailure.ExtractionFailed, $"Windows Installer error {result}");
    }

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiOpenDatabaseW(string path, nint persist, out nint database);

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MsiDatabaseOpenViewW(nint database, string query, out nint view);

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MsiDatabaseIsTablePersistentW(nint database, string table);

    [LibraryImport("msi.dll")]
    private static partial uint MsiViewExecute(nint view, nint record);

    [LibraryImport("msi.dll")]
    private static partial uint MsiViewFetch(nint view, out nint record);

    [LibraryImport("msi.dll")]
    private static partial uint MsiRecordGetFieldCount(nint record);

    [LibraryImport("msi.dll")]
    private static partial int MsiRecordIsNull(nint record, uint field);

    [LibraryImport("msi.dll")]
    private static unsafe partial uint MsiRecordGetStringW(nint record, uint field, char* value, ref uint size);

    [LibraryImport("msi.dll")]
    private static unsafe partial uint MsiRecordReadStream(nint record, uint field, byte* data, ref uint size);

    [LibraryImport("msi.dll")]
    private static partial uint MsiCloseHandle(nint handle);
}
