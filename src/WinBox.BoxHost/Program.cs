using WinBox.Core.Boxes;
using WinBox.Core.IO;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;
using WinBox.Platform.Sandbox;

// BoxHost: activated by WinBox inside a box's MSIX package. Reads launch.json next to itself, starts the
// boxed app in the MXC sandbox, keeps run.json current, and stops the app when WinBox sets the box's stop
// event. It lives exactly as long as the sandbox.
var hostDir = AppContext.BaseDirectory;
// Host\ sits directly in the box root.
var box = new BoxPaths(Path.GetFullPath(Path.Combine(hostDir, "..")));
// A stop first asks BoxShim to end the app the way Windows does at sign-out and to save the box's state
// (BoxShim ends the app's processes itself after 10 seconds). Only if BoxShim is not done in time are the
// app's own processes killed, so BoxShim can still save; after that, everything in the sandbox.
var quitGrace = TimeSpan.FromSeconds(25);
var saveGrace = TimeSpan.FromSeconds(20);
var runPath = Path.Combine(hostDir, BoxHostContract.RunFile);
var logPath = Path.Combine(hostDir, BoxHostContract.LogFile);
var hostPid = (uint)Environment.ProcessId;
var startedAt = DateTimeOffset.Now;

var logLock = new Lock();

void Log(string message)
{
    lock (logLock)
        File.AppendAllText(logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{hostPid}] {message}{Environment.NewLine}");
}

void Report(BoxRunRecord record)
{
    // WinBox reads run.json every half second; a write landing in the middle of a read is retried.
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            JsonFile.Write(runPath, record, WinBoxJson.Files.BoxRunRecord);
            return;
        }
        catch (IOException) when (attempt < 5)
        {
            Thread.Sleep(100);
        }
        catch (Exception e)
        {
            Log($"writing {BoxHostContract.RunFile} failed: {e.Message}");
            return;
        }
    }
}

try
{
    var request = JsonFile.Read(Path.Combine(hostDir, BoxHostContract.LaunchFile), WinBoxJson.Files.BoxLaunchRequest);
    Log($"launching {request.Spec.Executable}");
    // Created before anything is reported: WinBox treats the box as running only while this event exists,
    // and it goes away with this process however it ends.
    using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, BoxHostContract.StopEventName(request.BoxId));
    Report(new BoxRunRecord { State = BoxRunState.Starting, HostProcessId = hostPid, StartedAt = startedAt });

    using var app = await new MxcSandboxRunner().LaunchAsync(request.Spec);
    Log($"app pid {app.ProcessId}; warnings: {string.Join(" | ", app.Warnings)}");
    Report(new BoxRunRecord { State = BoxRunState.Running, HostProcessId = hostPid, AppProcessId = app.ProcessId, StartedAt = startedAt });

    var exit = app.WaitForExitAsync();
    var stopped = Task.Run(() => stop.WaitOne());
    // BoxShim is the sandbox's first process. Once it is gone it has saved the box's state, or reported why
    // it could not, although MXC takes several more seconds to tear the sandbox down; WinBox can already say so.
    var shimGone = WaitForProcessAsync(app.ProcessId);

    // Windows ends the session. BoxHost hears of it first of all applications (shutdown level 0x3FF), asks for
    // the same quit as a stop, and holds the shutdown until BoxShim has saved. Left to itself, BoxHost has no
    // window and is simply terminated, which takes the sandbox down with it before the app or BoxShim hear of
    // anything: measured on a real restart, nothing was saved and the app showed its crash bubble afterwards.
    if (!SessionEnd.Watch(0x3FF, "正在保存盒子里的应用数据", () =>
        {
            Log("session ending: asking BoxShim to end the app and save the state");
            stop.Set();
            Log(shimGone.Wait(TimeSpan.FromSeconds(20)) ? "session ending: BoxShim is done" : "session ending: BoxShim not done after 20s");
        }))
        Log("cannot watch for the session ending; a shutdown will cut the sandbox off");

    var reportLock = new Lock();
    var finished = false;
    _ = shimGone.ContinueWith(_ =>
    {
        var shim = ShimControl.ReadResult(box.Control);
        lock (reportLock)
        {
            if (!finished)
                Report(new BoxRunRecord
                {
                    State = BoxRunState.Closing,
                    HostProcessId = hostPid,
                    AppProcessId = app.ProcessId,
                    StartedAt = startedAt,
                    StateSaved = shim?.Saved,
                    SaveError = shim?.Error,
                });
        }
    }, TaskScheduler.Default);

    var forceStopped = false;
    if (await Task.WhenAny(exit, stopped) == stopped && !exit.IsCompleted)
    {
        forceStopped = true;
        try
        {
            ShimControl.Request(box.Control, ShimControl.QuitRequest);
            Log("stop requested; asked BoxShim to end the app and save the state");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"stop requested; asking BoxShim failed: {e.Message}");
        }
        if (await Task.WhenAny(shimGone, Task.Delay(quitGrace)) != shimGone)
        {
            var killed = KillAppProcesses(box.App);
            Log($"BoxShim not done after {quitGrace.TotalSeconds:0}s; killed {killed} app processes, waiting for the state to be saved");
            if (await Task.WhenAny(shimGone, Task.Delay(saveGrace)) != shimGone)
            {
                Log($"state not saved after another {saveGrace.TotalSeconds:0}s; killing everything in the sandbox");
                app.Kill();
            }
        }
    }
    var result = await exit;
    var outcome = ShimControl.ReadResult(box.Control);
    Log($"app exited with {result.ExitCode}; state {(outcome is null ? "not reported" : outcome.Saved ? $"saved ({outcome.Reason})" : $"NOT saved: {outcome.Error}")}");
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
            StateSaved = outcome?.Saved,
            SaveError = outcome?.Error,
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
