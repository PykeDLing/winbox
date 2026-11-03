using WinBox.Core.Boxes;
using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;
using WinBox.Platform;
using WinBox.Platform.Packaging;
using WinBox.Platform.Sandbox;

// Developer harness for WinBox. Drives the same Core/Platform code as the App, without UI.
Console.OutputEncoding = System.Text.Encoding.UTF8;
using var host = new WinBoxHost();

try
{
    return args switch
    {
        ["info"] => Info(),
        ["list"] => List(),
        ["import", var file] => await Import(file, register: true),
        ["import", var file, "--no-package"] => await Import(file, register: false),
        ["package", var box] => await Package(box),
        ["run", var box, ..] => await Run(box, args.Skip(2).ToArray()),
        ["stop", var box] => Stop(box),
        ["show", var box] => Show(box),
        ["remove", var box] => await Remove(box),
        ["guard"] => Guard(fix: false),
        ["guard", "--fix"] => Guard(fix: true),
        _ => Usage(),
    };
}
catch (ImportException e)
{
    Console.Error.WriteLine($"import failed: {e.Failure} {e.Detail}");
    return 2;
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
catch (BoxBusyException e)
{
    Console.Error.WriteLine($"box is busy: {e.Reason}");
    return 2;
}

int Usage()
{
    Console.WriteLine("""
        usage: winbox <command>
          info                     sandbox and package support, data root, tools
          list                     boxes in the library
          import <installer> [--no-package]
                                   create a box from an installer (stubs are resolved through recipes)
                                   and register its package
          package <id|name>        (re)register the box's MSIX package
          run <id|name> [options]  launch a box inside its package. Options ({data} = the box's Data dir):
                --seconds N   stop it after N seconds
                --cap NAME    grant an extra AppContainer capability (e.g. smbios)
                --env K=V     set an environment variable inside the sandbox
                --arg A       append a launch argument
                --rw PATH     also grant read-write on PATH (environment variables expanded)
                --loopback on let host tools reach the app's 127.0.0.1 ports (e.g. DevTools)
                --capture allow  DIAGNOSTICS ONLY: allow and record would-be-denied accesses
                --direct on   DIAGNOSTICS ONLY: skip the package; AppData/HKCU writes are then denied
                --exe PATH    DIAGNOSTICS ONLY: run PATH (with only the --arg values) instead of the app
                --container N ask MXC for a fixed sandbox name (ignored by the BaseContainer tier)
          stop <id|name>           quit a running box: ask its app to save and exit, end whatever is left
                                   after 10 seconds, then save the box's state
          show <id|name>           bring back the window of a running box's app
          remove <id|name>         unregister the box's package, delete the box and the host's traces of it
          guard [--fix]            look for host settings that point into the boxes (autostart, file
                                   associations, shortcuts, tasks...) and for Windows' records of boxes
                                   deleted earlier; --fix removes both after a backup
        """);
    return 1;
}

int Info()
{
    var support = host.Sandbox.GetSupport();
    Console.WriteLine($"data root     : {host.Paths.Root}");
    Console.WriteLine($"sandbox       : {(support.IsSupported ? "ProcessContainer available" : "unavailable: " + support.Reason)}");
    Console.WriteLine($"capabilities  : {string.Join(", ", support.Capabilities)}");
    Console.WriteLine($"packages      : {host.Packages.GetUnavailableReason() ?? "available (Developer Mode, unsigned loose layout)"}");
    Console.WriteLine($"innounp       : {host.InnounpPath ?? $"not found (set {WinBox.Platform.Import.InnoExtractor.PathVariable})"}");
    Console.WriteLine($"7-Zip         : {host.SevenZipPath ?? $"not found (set {WinBox.Platform.Import.SevenZip.PathVariable})"}");
    var areas = WindowsHostAreas.Current();
    var protectedPrograms = areas.InstalledPrograms.Count(p => areas.DataDrives.Any(d => p.StartsWith(d, StringComparison.OrdinalIgnoreCase) && p.Length > d.Length));
    Console.WriteLine($"personal      : {string.Join(", ", areas.PersonalFolders)}");
    Console.WriteLine($"data drives   : {(areas.DataDrives.Count == 0 ? "none" : string.Join(", ", areas.DataDrives))} ({protectedPrograms} installed programs on them stay denied)");
    var references = host.Guard.Scan(host.Paths.Boxes);
    var leaks = references.Count(r => r.Kind == HostReferenceKind.Leak);
    Console.WriteLine($"host guard    : {(leaks == 0 ? "clean" : $"{leaks} LEAKS - run `winbox guard`")} ({references.Count - leaks} traces)");
    return support.IsSupported ? 0 : 3;
}

int List()
{
    foreach (var entry in host.Store.Load())
    {
        switch (entry)
        {
            case LoadedBox b:
                var package = b.Config.Package is { } p
                    ? $"{p.FamilyName}{(host.Packages.IsRegistered(p) ? "" : " (NOT registered)")}"
                    : "none";
                Console.WriteLine($"{b.Config.Id}  {b.Config.Name}  {b.Config.Version}  entry={b.Config.Entry}");
                Console.WriteLine($"    package: {package}");
                if (host.Runner.ReadRun(b) is { } run)
                    Console.WriteLine($"    last run: {run.State} host={run.HostProcessId} exit={run.ExitCode} {run.Error}");
                foreach (var issue in b.Config.Issues)
                    Console.WriteLine($"    issue: {issue.Code} ({issue.Severity}) {issue.Detail}");
                break;
            case BrokenBox broken:
                Console.WriteLine($"[broken] {broken.Paths.Root}: {broken.Reason} {broken.Message}");
                break;
        }
    }
    return 0;
}

async Task<int> Import(string file, bool register)
{
    var lastStage = (ImportStage?)null;
    var lastPercent = -1;
    var progress = new Progress<ImportProgress>(p =>
    {
        if (p.Stage != lastStage)
        {
            Console.WriteLine($"[{p.Stage}]");
            lastStage = p.Stage;
        }
        if (p.Download is { Total: > 0 } d)
        {
            var percent = (int)(d.Received * 100 / d.Total.Value);
            if (percent / 10 != lastPercent / 10)
                Console.WriteLine($"  downloaded {percent}% of {d.Total.Value / (1024 * 1024)} MB");
            lastPercent = percent;
        }
    });
    var (box, registrationError) = await host.Manager.ImportAsync(file, progress, register: register);
    Console.WriteLine($"imported {box.Config.Name} {box.Config.Version} -> {box.Paths.Root}");
    Console.WriteLine($"entry: {box.Config.Entry}  args: {string.Join(' ', box.Config.LaunchArguments)}");
    foreach (var issue in box.Config.Issues)
        Console.WriteLine($"issue: {issue.Code} ({issue.Severity}) {issue.Detail}");
    Console.WriteLine(registrationError is null
        ? register ? $"package registered: {box.Config.Package?.FamilyName}" : "package not registered (--no-package); `run` registers it"
        : $"package not registered ({registrationError.Message}); `run` tries again");
    return 0;
}

async Task<int> Package(string key)
{
    var box = await host.Manager.RegisterAsync(Find(key));
    Console.WriteLine($"package registered: {box.Config.Package?.FamilyName}");
    return 0;
}

async Task<int> Run(string key, string[] options)
{
    var box = Find(key);
    int? seconds = null;
    var capabilities = new List<string>();
    var environment = new Dictionary<string, string>();
    var extraArgs = new List<string>();
    var loopback = false;
    var permissive = false;
    var direct = false;
    string? containerName = null;
    string? executable = null;
    var extraWritable = new List<string>();
    for (var i = 0; i + 1 < options.Length; i += 2)
    {
        var value = options[i + 1].Replace(LaunchPlanner.DataPlaceholder, box.Paths.Data, StringComparison.Ordinal);
        switch (options[i])
        {
            case "--seconds": seconds = int.Parse(value); break;
            case "--cap": capabilities.Add(value); break;
            case "--arg": extraArgs.Add(value); break;
            case "--loopback": loopback = value is "on" or "true"; break;
            case "--capture": permissive = value == "allow"; break;
            case "--direct": direct = value is "on" or "true"; break;
            case "--container": containerName = value; break;
            case "--exe": executable = value; break;
            case "--rw": extraWritable.Add(Environment.ExpandEnvironmentVariables(value)); break;
            case "--env" when value.IndexOf('=') is > 0 and var eq: environment[value[..eq]] = value[(eq + 1)..]; break;
            default: throw new ArgumentException($"unknown run option {options[i]}");
        }
    }

    LaunchSpec Customize(LaunchSpec planned) => planned with
    {
        // --exe replaces the app (diagnostics): its arguments are only the --arg values.
        Executable = executable ?? planned.Executable,
        Arguments = executable is null ? [.. planned.Arguments, .. extraArgs] : extraArgs,
        Capabilities = [.. planned.Capabilities, .. capabilities],
        // --env wins over what WinBox sets (TEMP, APPDATA...).
        Environment = new Dictionary<string, string>(planned.Environment.Where(e => !environment.ContainsKey(e.Key)).Concat(environment)),
        AllowHostLoopbackIngress = loopback,
        PermissiveCapture = permissive,
        ReadWritePaths = [.. planned.ReadWritePaths, .. extraWritable],
        ContainerName = containerName ?? planned.ContainerName,
    };
    if (permissive)
        Console.WriteLine("WARNING: permissive capture - denied accesses are allowed and only recorded");
    foreach (var (_, value) in environment)
        if (value.StartsWith(box.Paths.Data, StringComparison.OrdinalIgnoreCase))
            Directory.CreateDirectory(value);

    if (!direct)
    {
        var record = await host.Manager.RunAsync(box, new RunOptions
        {
            Customize = spec =>
            {
                spec = Customize(spec);
                Console.WriteLine($"launching {spec.Executable} {string.Join(' ', spec.Arguments)}");
                return spec;
            },
            Registered = b => Console.WriteLine($"package registered: {b.Config.Package?.FamilyName}"),
            Limit = seconds is int s ? TimeSpan.FromSeconds(s) : null,
            Update = r => Console.WriteLine($"[{r.State}] host={r.HostProcessId} app={r.AppProcessId}"),
        });
        if (record is null)
            return 5;
        Console.WriteLine($"{record.State}: exit {record.ExitCode} (0x{record.ExitCode ?? 0:X8}) forceStopped={record.ForceStopped} {record.Error}");
        if (record.DeniedAccesses is { } denied)
            Console.WriteLine($"denied accesses: {denied} -> {record.DenialsPath}");
        WarnAboutLeaks();
        return record.State == BoxRunState.Exited ? 0 : 5;
    }

    Console.WriteLine("WARNING: direct run - no package, the app's AppData and HKCU writes will be denied");
    var directSpec = Customize(host.Planner.Plan(box, DateTimeOffset.Now));
    Console.WriteLine($"launching {directSpec.Executable} {string.Join(' ', directSpec.Arguments)}");
    host.Shim.Deploy(box.Paths);
    using var process = await host.Sandbox.LaunchAsync(directSpec);
    Console.WriteLine($"pid {process.ProcessId}");
    foreach (var w in process.Warnings)
        Console.WriteLine($"warning: {w}");
    var exit = process.WaitForExitAsync();
    if (seconds is int limit && await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(limit))) != exit)
    {
        Console.WriteLine($"still running after {limit}s, killing");
        process.Kill();
    }
    var result = await exit;
    Console.WriteLine($"exit code {result.ExitCode} (0x{result.ExitCode:X8}) timedOut={result.TimedOut}");
    if (result.Denials is { } d)
        Console.WriteLine($"denied accesses: {d.Total}{(d.Truncated ? "+" : "")} -> {d.OutputPath}");
    return 0;
}

int Show(string key)
{
    var box = Find(key);
    var sent = host.Runner.RequestShow(box);
    Console.WriteLine(sent ? $"asked {box.Config.Name} to show its window" : $"{box.Config.Name} is not running");
    return sent ? 0 : 6;
}

int Stop(string key)
{
    var box = Find(key);
    var sent = host.Runner.RequestStop(box.Config.Id);
    Console.WriteLine(sent ? $"stop requested for {box.Config.Name}" : $"{box.Config.Name} is not running");
    return sent ? 0 : 6;
}

async Task<int> Remove(string key)
{
    var box = Find(key);
    if (host.Runner.IsRunning(box))
        Console.WriteLine("box is running; asking it to quit");
    RemoveOutcome outcome;
    try
    {
        outcome = await host.Manager.RemoveAsync(box);
    }
    catch (BoxBusyException e)
    {
        Console.Error.WriteLine($"{box.Config.Name} not removed: {e.Reason}");
        return 8;
    }
    if (outcome.Cleanup is { } cleanup)
        Console.WriteLine($"host references removed: {cleanup.Removed} (backup: {cleanup.BackupDirectory})");
    foreach (var failure in outcome.Failures)
        Console.Error.WriteLine($"  could not remove {failure}");
    Console.WriteLine(outcome.FolderLeftOver
        ? $"removed {box.Config.Name}, but some files are still in use; they go the next time WinBox starts"
        : $"removed {box.Config.Name}");
    // Windows files some records under the package's name only after it is gone; `guard --fix` clears them later.
    return 0;
}

int Guard(bool fix)
{
    var references = host.Guard.Scan(host.Paths.Boxes);
    var leaks = references.Where(r => r.Kind == HostReferenceKind.Leak).ToList();
    var orphans = host.Manager.FindLeftovers();
    Console.WriteLine($"leaks: {leaks.Count}");
    foreach (var leak in leaks)
        Console.WriteLine($"  {leak}");
    Console.WriteLine($"traces: {references.Count - leaks.Count} (Windows' own records of boxed apps; removed with their box)");
    Console.WriteLine($"left over from deleted boxes: {orphans.Count}");
    foreach (var orphan in orphans)
        Console.WriteLine($"  {orphan}");
    if (!fix)
        return leaks.Count == 0 ? 0 : 7;

    List<HostReference> doomed = [.. leaks, .. orphans];
    if (doomed.Count == 0)
        return 0;
    var cleanup = host.Manager.Clear(doomed, "fix");
    Console.WriteLine($"removed {cleanup.Removed} (backup: {cleanup.BackupDirectory})");
    foreach (var failure in cleanup.Failures)
        Console.Error.WriteLine($"  could not remove {failure}");
    return cleanup.Failures.Count == 0 ? 0 : 7;
}

void WarnAboutLeaks()
{
    var leaks = host.Guard.Scan(host.Paths.Boxes).Where(r => r.Kind == HostReferenceKind.Leak).ToList();
    if (leaks.Count == 0)
        return;
    Console.Error.WriteLine($"WARNING: {leaks.Count} host settings point into the boxes and would start a boxed program outside its sandbox:");
    foreach (var leak in leaks.Take(10))
        Console.Error.WriteLine($"  {leak}");
    Console.Error.WriteLine("run `winbox guard --fix` to remove them");
}

// Exactly one box or nothing: commands like remove must never pick "the first" of several matches.
LoadedBox Find(string key)
{
    var boxes = host.Store.Load().OfType<LoadedBox>().ToList();
    var byId = key.Length >= 8
        ? boxes.Where(b => b.Config.Id.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList()
        : [];
    var matches = byId.Count > 0 ? byId : boxes.Where(b => b.Config.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
    return matches.Count switch
    {
        1 => matches[0],
        0 => throw new InvalidOperationException($"no box matches '{key}' (give its name, or at least 8 characters of its id)"),
        _ => throw new InvalidOperationException($"'{key}' matches {matches.Count} boxes ({string.Join(", ", matches.Select(b => b.Config.Id))}); give the id"),
    };
}
