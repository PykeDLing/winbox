using System.Diagnostics;
using WinBox.BoxShim;
using WinBox.Core.Sandbox;

// BoxShim: started by MXC as the sandbox's first process (see LaunchPlanner). In MXC's BaseContainer tier
// the sandbox's LocalAppData is a fresh folder every run and is deleted afterwards, so BoxShim:
//   1. restores the state saved in the box into it,
//   2. starts the app in a job object and waits until every process of the app has exited,
//   3. saves the state back into the box.
// A force quit from WinBox kills only the app's processes, so step 3 still runs. When Windows ends the
// session the save happens from the WM_ENDSESSION handler, before the session goes away.
var options = ShimArguments.Parse(args);
var log = new ShimLog(options.StateDirectory + ".log");
var state = new StateFolder(options.StateDirectory);
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var saveLock = new Lock();
var saved = false;
CopyStats? restored = null;

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
        // Windows ends the session: the app gets the same notification, give it a moment to close. Kept
        // short so the save fits in the few seconds before Windows lists BoxShim as blocking shutdown.
        app.WaitForAll(TimeSpan.FromSeconds(2.5));
        Save("session ending");
    });
    log.Write(session.Window != 0
        ? $"watching for session end (window 0x{session.Window:X})"
        : $"cannot watch for session end: window creation failed with error {session.Error}");

    app.WaitForAll(Timeout.InfiniteTimeSpan);
    log.Write($"app exited with {app.ExitCode}");
    Save("app exited");
    return app.ExitCode;
}
catch (Exception e)
{
    log.Write($"failed: {e}");
    return 1;
}

void Save(string reason)
{
    lock (saveLock)
    {
        if (saved)
            return;
        var clock = Stopwatch.StartNew();
        try
        {
            var stats = state.Save(localAppData, options.Exclusions, restored?.Skipped);
            log.Write($"saved ({reason}): {stats} ({clock.ElapsedMilliseconds} ms)");
            foreach (var file in stats.KeptPrevious.Concat(stats.Skipped).Take(20))
                log.Write($"  unreadable: {file}");
            saved = true;
        }
        catch (Exception e)
        {
            // Not marked saved: a later trigger (app exit after a session-end attempt) tries again.
            log.Write($"save failed ({reason}): {e}");
        }
    }
}
