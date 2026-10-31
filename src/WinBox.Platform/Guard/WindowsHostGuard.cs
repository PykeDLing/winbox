using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using WinBox.Core.Guard;

namespace WinBox.Platform.Guard;

/// <summary>
/// Scans the current user's registry, shortcuts and scheduled tasks for anything that points into the
/// boxes. Boxed apps cannot write there; a hit means a box's program ran or was registered outside its
/// sandbox, and it would run unsandboxed again if left in place.
/// </summary>
public sealed class WindowsHostGuard : IHostGuard
{
    public IReadOnlyList<HostReference> Scan(string root, Func<string, bool>? includePackage = null)
    {
        var found = new List<HostReference>();
        ScanRegistry(root, includePackage, found);
        ScanShortcuts(root, found);
        ScanTasks(root, found);
        return found;
    }

    public GuardCleanup Remove(IReadOnlyCollection<HostReference> references, string root, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var backup = new Backup(backupDirectory);
        var removed = 0;
        var failures = new List<string>();

        // Each reference is checked again right before it goes: the scan may be old, and the program may have
        // been installed natively since, reusing the same value names, shortcuts or tasks.
        foreach (var reference in references)
        {
            try
            {
                var done = reference.Location switch
                {
                    HostLocationKind.RegistryValue => RemoveValue(reference, root, backup),
                    HostLocationKind.RegistryKey => RemovePackageKey(reference, backup),
                    HostLocationKind.File => RemoveFile(reference.Path, root,
                        Path.Combine(backupDirectory, "files", $"{removed:D3}-{Path.GetFileName(reference.Path)}")),
                    HostLocationKind.ScheduledTask => DeleteTask(reference.Path, root, Path.Combine(backupDirectory, $"task-{removed:D3}.xml")),
                    _ => false,
                };
                if (done)
                    removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or COMException or ArgumentException)
            {
                failures.Add($"{reference}: {e.Message}");
            }
        }
        return new GuardCleanup(removed, failures, backupDirectory);
    }

    /// <summary>
    /// Removes a registry reference that still points inside <paramref name="root"/>, or is still named after its
    /// package. List values (REG_MULTI_SZ, environment variables) lose only their matching entries and keep
    /// their kind.
    /// </summary>
    private static bool RemoveValue(HostReference reference, string root, Backup backup)
    {
        var key = reference.Path[@"HKCU\".Length..];
        var name = reference.Name ?? "";
        using var writable = Registry.CurrentUser.OpenSubKey(key, writable: true);
        if (writable is null)
            return false;
        var wholeKey = HostReferenceRules.RemovesWholeKey(key);
        if (wholeKey ? !writable.GetValueNames().Any(n => PointsInto(writable, key, n, root)) : !StillMatches(writable, key, name, reference, root))
            return false;

        // Nothing goes without a backup, and a failed backup throws before anything changed. Only what goes is
        // saved: the keys these values sit in (MuiCache, UserAssist) are the user's whole program history.
        if (wholeKey)
        {
            backup.SaveKey(reference.Path);
            writable.Dispose(); // The key itself goes.
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            return true;
        }
        backup.SaveValue(key, writable, name);
        var kind = writable.GetValueKind(name);
        // A value named after a box (by path or by package) goes whole; only a list that merely contains a box
        // path keeps its other entries.
        var isList = reference.Package is null && !HostReferenceRules.Mentions(ReadableName(key, name), root) &&
            (kind == RegistryValueKind.MultiString ||
             (HostReferenceRules.HoldsLists(key) && kind is RegistryValueKind.String or RegistryValueKind.ExpandString));
        var kept = !isList ? null : HostReferenceRules.WithoutMentions(
            kind == RegistryValueKind.MultiString
                ? (string[])writable.GetValue(name)!
                : ((string)writable.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)!).Split(';'),
            root);
        if (kept is null)
            writable.DeleteValue(name, throwOnMissingValue: false);
        else
            writable.SetValue(name, kind == RegistryValueKind.MultiString ? kept.ToArray() : string.Join(';', kept), kind);
        return true;
    }

    private static bool StillMatches(RegistryKey key, string keyPath, string name, HostReference reference, string root) =>
        reference.Package is { } family
            ? string.Equals(HostReferenceRules.BoxPackageFamilyIn(name), family, StringComparison.OrdinalIgnoreCase)
            : PointsInto(key, keyPath, name, root);

    private static bool PointsInto(RegistryKey key, string keyPath, string name, string root) =>
        HostReferenceRules.Mentions(ReadableName(keyPath, name), root) || (ReadText(key, name) is { } text && HostReferenceRules.Mentions(text, root));

    private static string ReadableName(string keyPath, string name) =>
        HostReferenceRules.HasEncodedNames(keyPath) ? HostReferenceRules.Rot13(name) : name;

    /// <summary>Removes a key Windows keeps under a box package's name, if it is still there and still named so.</summary>
    private static bool RemovePackageKey(HostReference reference, Backup backup)
    {
        var key = reference.Path[@"HKCU\".Length..];
        var name = key[(key.LastIndexOf('\\') + 1)..];
        if (reference.Package is not { } family || !string.Equals(HostReferenceRules.BoxPackageFamilyIn(name), family, StringComparison.OrdinalIgnoreCase))
            return false;
        using (var existing = Registry.CurrentUser.OpenSubKey(key))
        {
            if (existing is null)
                return false;
        }
        backup.SaveKey(reference.Path);
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        return true;
    }

    private static bool RemoveFile(string file, string root, string backup)
    {
        if (!File.Exists(file) || !HostReferenceRules.Mentions(string.Join('\0', ShortcutTarget(file), ReadForSearch(file)), root))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Move(file, backup);
        return true;
    }

    private static void ScanRegistry(string root, Func<string, bool>? includePackage, List<HostReference> found)
    {
        string? Package(string name) =>
            includePackage is not null && HostReferenceRules.BoxPackageFamilyIn(name) is { } family && includePackage(family) ? family : null;

        var pending = new Stack<string>(HostReferenceRules.RegistryRoots);
        while (pending.TryPop(out var path))
        {
            if (HostReferenceRules.IsIgnoredKey(path))
                continue;
            RegistryKey? key;
            try { key = Registry.CurrentUser.OpenSubKey(path); }
            catch (SecurityException) { continue; }
            if (key is null)
                continue;
            using (key)
            {
                var kind = HostReferenceRules.ClassifyKey(path);
                var encoded = HostReferenceRules.HasEncodedNames(path);
                foreach (var name in key.GetValueNames())
                {
                    var readable = encoded ? HostReferenceRules.Rot13(name) : name;
                    var text = ReadText(key, name);
                    if (HostReferenceRules.Mentions(readable, root) || (text is not null && HostReferenceRules.Mentions(text, root)))
                        found.Add(new HostReference(kind, HostLocationKind.RegistryValue, $@"HKCU\{path}", name, encoded ? readable : text ?? ""));
                    else if (Package(name) is { } family)
                        found.Add(new HostReference(HostReferenceKind.Trace, HostLocationKind.RegistryValue, $@"HKCU\{path}", name, family, family));
                }
                foreach (var sub in key.GetSubKeyNames())
                {
                    // A key named after the package is a record of it as a whole and goes as a whole.
                    if (Package(sub) is { } family)
                        found.Add(new HostReference(HostReferenceKind.Trace, HostLocationKind.RegistryKey, $@"HKCU\{path}\{sub}", null, family, family));
                    else
                        pending.Push($@"{path}\{sub}");
                }
            }
        }
    }

    private static string? ReadText(RegistryKey key, string name)
    {
        try
        {
            return key.GetValueKind(name) switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString =>
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                RegistryValueKind.MultiString => key.GetValue(name) is string[] lines ? string.Join(" ", lines) : null,
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Places a link to a box's program would start it unsandboxed: autostart, Start, Desktop, taskbar pins.</summary>
    private static void ScanShortcuts(string root, List<HostReference> found)
    {
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        (string Dir, bool Recursive)[] places =
        [
            (Folder(Environment.SpecialFolder.Startup), false),
            (Folder(Environment.SpecialFolder.CommonStartup), false),
            (Folder(Environment.SpecialFolder.Programs), true),
            (Folder(Environment.SpecialFolder.CommonPrograms), true),
            (Folder(Environment.SpecialFolder.DesktopDirectory), false),
            (Folder(Environment.SpecialFolder.CommonDesktopDirectory), false),
            (Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch"), true),
        ];
        string[] patterns = ["*.lnk", "*.url", "*.bat", "*.cmd", "*.vbs", "*.ps1"];
        // Startup lies inside Programs, which is walked recursively.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, recursive) in places)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in patterns.SelectMany(p => SafeEnumerate(dir, p, option)).Where(seen.Add))
            {
                var text = string.Join('\0', ShortcutTarget(file), ReadForSearch(file));
                if (HostReferenceRules.Mentions(text, root))
                    found.Add(new HostReference(HostReferenceKind.Leak, HostLocationKind.File, file, null, Excerpt(text, root)));
            }
        }
    }

    /// <summary>
    /// Where a .lnk points, resolved by the shell. The file itself may not hold the path in one piece: a target
    /// under a shared folder is stored as \\machine\share plus a suffix, next to a relative path. A .url file
    /// holds its URL as plain text, which <see cref="ReadForSearch"/> covers.
    /// </summary>
    private static string? ShortcutTarget(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return ShellLinks.Read(file);
        }
        catch (Exception e) when (e is COMException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern, SearchOption option)
    {
        try { return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = option == SearchOption.AllDirectories, IgnoreInaccessible = true }).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Shortcuts keep their full target as UTF-16, at whatever byte offset it lands (often odd), so both
    /// alignments are decoded; scripts and .url files are usually UTF-8.
    /// </summary>
    private static string? ReadForSearch(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 1024 * 1024)
                return null;
            var bytes = File.ReadAllBytes(file);
            return string.Join('\0',
                Encoding.Unicode.GetString(bytes),
                bytes.Length > 1 ? Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1) : "",
                Encoding.UTF8.GetString(bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Excerpt(string text, string root)
    {
        var at = text.IndexOf(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var end = at;
        while (end < text.Length && text[end] is not ('\0' or '"' or '<' or '\r' or '\n'))
            end++;
        return text[at..end];
    }

    private static void ScanTasks(string root, List<HostReference> found)
    {
        try
        {
            ForEachTask((folder, task) =>
            {
                var xml = task.GetXml();
                if (HostReferenceRules.Mentions(xml, root))
                    found.Add(new HostReference(HostReferenceKind.Leak, HostLocationKind.ScheduledTask, task.GetPath(), null, Excerpt(xml, root)));
            });
        }
        catch (COMException)
        {
            // Task Scheduler unavailable: nothing to report from there.
        }
    }

    private static bool DeleteTask(string taskPath, string root, string backupFile)
    {
        var deleted = false;
        ForEachTask((folder, task) =>
        {
            if (deleted || !string.Equals(task.GetPath(), taskPath, StringComparison.OrdinalIgnoreCase))
                return;
            var xml = task.GetXml();
            if (!HostReferenceRules.Mentions(xml, root))
                return;
            File.WriteAllText(backupFile, xml);
            folder.DeleteTask(task.GetName(), 0);
            deleted = true;
        });
        return deleted;
    }

    /// <summary>Walks every task folder the user can read through the Task Scheduler API.</summary>
    private static void ForEachTask(Action<ITaskFolder, IRegisteredTask> visit)
    {
        var folders = new Stack<ITaskFolder>();
        folders.Push(ScheduledTasks.Connect().GetFolder(@"\"));
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            try
            {
                var tasks = folder.GetTasks(ScheduledTasks.EnumHidden);
                for (var i = 1; i <= tasks.GetCount(); i++)
                    visit(folder, tasks.GetItem(Variant.Int32(i)));
                var subfolders = folder.GetFolders(0);
                for (var i = 1; i <= subfolders.GetCount(); i++)
                    folders.Push(subfolders.GetItem(Variant.Int32(i)));
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException)
            {
                // Some system folders are not readable without elevation; the user's own tasks are.
            }
        }
    }

    /// <summary>
    /// The backup of one cleanup: removed values in values.reg, removed keys exported whole (they belong to one
    /// program or package). Each entry is written through before the original goes.
    /// </summary>
    private sealed class Backup(string directory)
    {
        private readonly string _values = Path.Combine(directory, "values.reg");
        private int _keys;

        public void SaveValue(string keyPath, RegistryKey key, string name)
        {
            var kind = key.GetValueKind(name);
            var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var text = $"\r\n[HKEY_CURRENT_USER\\{keyPath}]\r\n{RegFile.ValueLine(name, kind == RegistryValueKind.None ? 0 : (int)kind, data)}\r\n";
            var isNew = !File.Exists(_values);
            using var file = new FileStream(_values, FileMode.Append, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            if (isNew)
            {
                file.Write(Encoding.Unicode.GetPreamble());
                text = RegFile.Header + "\r\n" + text;
            }
            file.Write(Encoding.Unicode.GetBytes(text));
        }

        public void SaveKey(string key) => ExportKey(key, Path.Combine(directory, $"key-{++_keys:D3}.reg"));
    }

    private static void ExportKey(string key, string file)
    {
        using var reg = Process.Start(new ProcessStartInfo("reg.exe", ["export", key, file, "/y"])
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        reg.WaitForExit();
        if (reg.ExitCode != 0)
            throw new IOException($"reg export {key} failed: {reg.StandardError.ReadToEnd().Trim()}");
    }
}
