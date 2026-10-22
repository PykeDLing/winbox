using WinBox.Core.Boxes;
using WinBox.Core.IO;
using WinBox.Core.Packaging;
using WinBox.Platform.Sandbox;

// BoxHost: activated by WinBox inside a box's MSIX package. Reads launch.json next to itself, starts the
// boxed app in the MXC sandbox, keeps run.json current, and force-quits the app when WinBox sets the
// box's stop event. It lives exactly as long as the sandbox.
var hostDir = AppContext.BaseDirectory;
// Host\ sits directly in the box root.
var box = new BoxPaths(Path.GetFullPath(Path.Combine(hostDir, "..")));
// Force quit kills the app's own processes first, so BoxShim (which lives in Shim\, not App\) can still
// save the box's state; only if the sandbox does not close by then is everything in it killed.
var saveGrace = TimeSpan.FromSeconds(20);
var runPath = Path.Combine(hostDir, BoxHostContract.RunFile);
var logPath = Path.Combine(hostDir, BoxHostContract.LogFile);
var hostPid = (uint)Environment.ProcessId;
var startedAt = DateTimeOffset.Now;

void Log(string message) =>
    File.AppendAllText(logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{hostPid}] {message}{Environment.NewLine}");

void Report(BoxRunRecord record)
{
    try { JsonFile.Write(runPath, record); }
    catch (Exception e) { Log($"writing {BoxHostContract.RunFile} failed: {e.Message}"); }
}

try
{
    var request = JsonFile.Read<BoxLaunchRequest>(Path.Combine(hostDir, BoxHostContract.LaunchFile));
    Log($"launching {request.Spec.Executable}");
    Report(new BoxRunRecord { State = BoxRunState.Starting, HostProcessId = hostPid, StartedAt = startedAt });

    using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, BoxHostContract.StopEventName(request.BoxId));
    using var app = await new MxcSandboxRunner().LaunchAsync(request.Spec);
    Log($"app pid {app.ProcessId}; warnings: {string.Join(" | ", app.Warnings)}");
    Report(new BoxRunRecord { State = BoxRunState.Running, HostProcessId = hostPid, AppProcessId = app.ProcessId, StartedAt = startedAt });

    var exit = app.WaitForExitAsync();
    var stopped = Task.Run(() => stop.WaitOne());
    // BoxShim is the sandbox's first process. Once it is gone the box's state is saved, although MXC
    // takes several more seconds to tear the sandbox down; WinBox can already say so.
    var shimGone = WaitForProcessAsync(app.ProcessId);
    var reportLock = new Lock();
    var finished = false;
    _ = shimGone.ContinueWith(_ =>
    {
        lock (reportLock)
        {
            if (!finished)
                Report(new BoxRunRecord { State = BoxRunState.Closing, HostProcessId = hostPid, AppProcessId = app.ProcessId, StartedAt = startedAt });
        }
    }, TaskScheduler.Default);

    var forceStopped = false;
    if (await Task.WhenAny(exit, stopped) == stopped && !exit.IsCompleted)
    {
        forceStopped = true;
        var killed = KillAppProcesses(box.App);
        Log($"stop requested; killed {killed} app processes, waiting for the state to be saved");
        if (await Task.WhenAny(shimGone, Task.Delay(saveGrace)) != shimGone)
        {
            Log($"state not saved after {saveGrace.TotalSeconds:0}s; killing everything in the sandbox");
            app.Kill();
        }
    }
    var result = await exit;
    Log($"app exited with {result.ExitCode}");
    lock (reportLock)
    {
        finished = true;
        Report(new BoxRunRecord
        {
            State = BoxRunState.Exited,
            HostProcessId = hostPid,
            AppProcessId = app.ProcessId,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.Now,
            ExitCode = result.ExitCode,
            ForceStopped = forceStopped,
            DeniedAccesses = result.Denials?.Total,
            DenialsPath = result.Denials?.OutputPath,
        });
    }
    return 0;
}
catch (Exception e)
{
    Log($"failed: {e}");
    Report(new BoxRunRecord
    {
        State = BoxRunState.Failed,
        HostProcessId = hostPid,
        StartedAt = startedAt,
        EndedAt = DateTimeOffset.Now,
        Error = e.Message,
    });
    return 1;
}

static Task WaitForProcessAsync(uint processId)
{
    try
    {
        var process = System.Diagnostics.Process.GetProcessById((int)processId);
        return process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
    }
    catch (ArgumentException)
    {
        return Task.CompletedTask; // Already gone.
    }
}

static int KillAppProcesses(string appDirectory)
{
    var killed = 0;
    foreach (var process in ProcessImages.Under(appDirectory))
    {
        using (process)
        {
            try
            {
                process.Kill();
                killed++;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }
    return killed;
}
