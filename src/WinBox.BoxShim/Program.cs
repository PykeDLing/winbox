using System.Diagnostics;
using WinBox.BoxShim;
using WinBox.Core.Sandbox;

// BoxShim: started by MXC as the sandbox's first process (see LaunchPlanner). In MXC's BaseContainer tier
// the sandbox's LocalAppData is a fresh folder every run and is deleted afterwards, so BoxShim:
//   1. restores the state saved in the box into it,
//   2. starts the app in a job object and waits until every process of the app has exited,
//   3. saves the state back into the box and reports in the box's Control folder whether that worked.
// While the app runs, BoxShim takes WinBox's requests from that folder: show the app's window again, or quit
// (ask the app to end as at sign-out, end whatever is left, save). When Windows ends the session the save
// happens from the WM_ENDSESSION handler instead.
var options = ShimArguments.Parse(args);
var log = new ShimLog(options.StateDirectory + ".log");
var state = new StateFolder(options.StateDirectory);
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var quitGrace = TimeSpan.FromSeconds(10);
var saveLock = new Lock();
ShimResult? result = null;
CopyStats? restored = null;
var quitting = 0;
var terminated = 0;

// Windows notifies processes that the session ends from the highest shutdown level down. Below the default
// (0x280), BoxShim hears of it after the app, which has saved its own files by then, so the copy is whole.
if (Native.SetProcessShutdownParameters(0x180, 0) == 0)
    log.Write($"could not set the shutdown level (error {System.Runtime.InteropServices.Marshal.GetLastSystemError()}); the save at sign-out may run alongside the app");

try
{
    var clock = Stopwatch.StartNew();
    // Failing to write into the sandbox (a full disk) throws here: the app does not start on partial state.
    restored = state.RestoreTo(localAppData);
    log.Write($"restored into {localAppData}: {(restored is null ? "nothing saved yet" : restored)} ({clock.ElapsedMilliseconds} ms)");
    foreach (var file in restored?.Skipped.Take(20) ?? [])
        log.Write($"  not restored (kept for the next save): {file}");

    using var app = AppProcess.Start(options.Executable, options.Arguments);
    log.Write($"started {options.Executable} pid {app.ProcessId}; {(app.Tracked ? "tracking its process tree in a job" : "job unavailable, tracking by image path")}");

    using var session = SessionWatcher.Start(() =>
    {
        // The app was told before BoxShim (see the shutdown level above) and has usually exited by now.
        // Whatever still runs is ended, so the copy is not taken while it writes.
        if (!app.WaitForAll(TimeSpan.FromSeconds(3)))
        {
            Interlocked.Exchange(ref terminated, 1);
            app.Terminate();
            app.WaitForAll(TimeSpan.FromSeconds(2));
        }
        Save("session ending");
    });
    log.Write(session.Window != 0
        ? $"watching for session end (window 0x{session.Window:X})"
        : $"cannot watch for session end: window creation failed with error {session.Error}");

    using var requests = options.ControlDirectory is { } control
        ? ControlRequests.Start(control, request =>
        {
            if (request == ShimControl.ShowRequest)
            {
                log.Write("show requested: starting the app once more so that it shows its window");
                try { app.StartAnother(options.Executable, options.Arguments); }
                catch (Exception e) { log.Write($"show failed: {e.Message}"); }
                return;
            }
            Interlocked.Exchange(ref quitting, 1);
            log.Write($"quit requested: told {app.AskToEnd()} windows that the session ends");
            if (!app.WaitForAll(quitGrace))
            {
                log.Write($"still running after {quitGrace.TotalSeconds:0}s; ending its processes");
                Interlocked.Exchange(ref terminated, 1);
                app.Terminate();
            }
        })
        : null;

    app.WaitForAll(Timeout.InfiniteTimeSpan);
    log.Write($"app exited with {app.ExitCode}");
    Save(Volatile.Read(ref quitting) == 1 ? "quit" : "app exited");
    return app.ExitCode;
}
catch (Exception e)
{
    // The app never ran (restore or start failed), so this run has nothing to lose and nothing is reported;
    // the copy saved by the previous run is untouched.
    log.Write($"failed: {e}");
    return 1;
}

void Save(string reason)
{
    lock (saveLock)
    {
        if (result is { Saved: true })
            return;
        var clock = Stopwatch.StartNew();
        try
        {
            var stats = state.Save(localAppData, options.Exclusions, restored?.Skipped);
            log.Write($"saved ({reason}): {stats} ({clock.ElapsedMilliseconds} ms)");
            foreach (var file in stats.KeptPrevious.Concat(stats.Skipped).Take(20))
                log.Write($"  unreadable: {file}");
            result = new ShimResult(true, reason, Terminated: Volatile.Read(ref terminated) == 1);
        }
        catch (Exception e)
        {
            // Not saved: a later trigger (app exit after a session-end attempt) tries again.
            log.Write($"save failed ({reason}): {e}");
            result = new ShimResult(false, reason, e.Message, Volatile.Read(ref terminated) == 1);
        }
        Report(result);
    }
}

// WinBox learns from this whether to say the data was saved; the sandbox has no other way to tell it.
void Report(ShimResult outcome)
{
    if (options.ControlDirectory is null)
        return;
    try { ShimControl.WriteResult(options.ControlDirectory, outcome); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write($"reporting the result failed: {e.Message}"); }
}
