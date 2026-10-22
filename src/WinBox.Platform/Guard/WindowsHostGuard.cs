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
    public IReadOnlyList<HostReference> Scan(string root)
    {
        var found = new List<HostReference>();
        ScanRegistry(root, found);
        ScanShortcuts(root, found);
        ScanTasks(root, found);
        return found;
    }

    public GuardCleanup Remove(IReadOnlyCollection<HostReference> references, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var removed = 0;
        var failures = new List<string>();
        var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in references)
        {
            try
            {
                switch (reference.Location)
                {
                    case HostLocationKind.RegistryValue:
                        var key = reference.Path[@"HKCU\".Length..];
                        if (exported.Add(key))
                            ExportKey(reference.Path, Path.Combine(backupDirectory, $"reg-{exported.Count:D3}.reg"));
                        if (HostReferenceRules.RemovesWholeKey(key))
                            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                        else
                        {
                            using var writable = Registry.CurrentUser.OpenSubKey(key, writable: true);
                            writable?.DeleteValue(reference.Name ?? "", throwOnMissingValue: false);
                        }
                        break;
                    case HostLocationKind.File:
                        var copy = Path.Combine(backupDirectory, "files", $"{removed:D3}-{Path.GetFileName(reference.Path)}");
                        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                        File.Move(reference.Path, copy);
                        break;
                    case HostLocationKind.ScheduledTask:
                        DeleteTask(reference.Path, Path.Combine(backupDirectory, $"task-{removed:D3}.xml"));
                        break;
                }
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or COMException or ArgumentException)
            {
                failures.Add($"{reference}: {e.Message}");
            }
        }
        return new GuardCleanup(removed, failures, backupDirectory);
    }

    private static void ScanRegistry(string root, List<HostReference> found)
    {
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
                foreach (var name in key.GetValueNames())
                {
                    var text = ReadText(key, name);
                    if (HostReferenceRules.Mentions(name, root) || (text is not null && HostReferenceRules.Mentions(text, root)))
                        found.Add(new HostReference(kind, HostLocationKind.RegistryValue, $@"HKCU\{path}", name, text ?? ""));
                }
                foreach (var sub in key.GetSubKeyNames())
                    pending.Push($@"{path}\{sub}");
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
    /// Where a .lnk or .url points, resolved by the shell. The file itself may not hold the path in one piece:
    /// a target under a shared folder is stored as \\machine\share plus a suffix, next to a relative path.
    /// </summary>
    private static string? ShortcutTarget(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            s_shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic link = ((dynamic)s_shell!).CreateShortcut(file);
            return file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? $"{link.TargetPath}\0{link.Arguments}\0{link.WorkingDirectory}"
                : (string)link.TargetPath;
        }
        catch (Exception e) when (e is COMException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }

    private static object? s_shell;

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
                string xml = task.Xml;
                if (HostReferenceRules.Mentions(xml, root))
                    found.Add(new HostReference(HostReferenceKind.Leak, HostLocationKind.ScheduledTask, (string)task.Path, null, Excerpt(xml, root)));
            });
        }
        catch (COMException)
        {
            // Task Scheduler unavailable: nothing to report from there.
        }
    }

    private static void DeleteTask(string taskPath, string backupFile)
    {
        var done = false;
        ForEachTask((folder, task) =>
        {
            if (done || !string.Equals((string)task.Path, taskPath, StringComparison.OrdinalIgnoreCase))
                return;
            File.WriteAllText(backupFile, (string)task.Xml);
            folder.DeleteTask((string)task.Name, 0);
            done = true;
        });
        if (!done)
            throw new IOException($"task {taskPath} not found");
    }

    /// <summary>Walks every task folder the user can read through the Task Scheduler scripting API.</summary>
    private static void ForEachTask(Action<dynamic, dynamic> visit)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Schedule.Service is not registered");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        var folders = new Stack<dynamic>();
        folders.Push(service.GetFolder(@"\"));
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            try
            {
                dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
                for (var i = 1; i <= (int)tasks.Count; i++)
                    visit(folder, tasks[i]);
                dynamic subfolders = folder.GetFolders(0);
                for (var i = 1; i <= (int)subfolders.Count; i++)
                    folders.Push(subfolders[i]);
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException)
            {
                // Some system folders are not readable without elevation; the user's own tasks are.
            }
        }
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
