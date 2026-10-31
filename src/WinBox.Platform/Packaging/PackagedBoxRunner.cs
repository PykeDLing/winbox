using System.Diagnostics;
using System.Text.Json;
using WinBox.Core.Boxes;
using WinBox.Core.IO;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;
using WinBox.Platform.Sandbox;

namespace WinBox.Platform.Packaging;

/// <summary>
/// Runs a box through its package: writes launch.json, activates BoxHost inside the package, and follows
/// run.json until BoxHost exits. Activation starts BoxHost from the system, never as a child of WinBox.
/// </summary>
public sealed class PackagedBoxRunner(IBoxPackageManager packages, BoxShimDeployer shim)
{
    public async Task<BoxRunRecord> RunAsync(
        LoadedBox box,
        LaunchSpec spec,
        TimeSpan? limit = null,
        Action<BoxRunRecord>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        var package = box.Config.Package ?? throw new InvalidOperationException($"{box.Config.Name} has no registered package.");
        if (IsRunning(box))
            throw new InvalidOperationException($"{box.Config.Name} is already running (BoxHost {ReadRun(box)?.HostProcessId}).");

        packages.RefreshHost(box);
        shim.Deploy(box.Paths);
        JsonFile.Write(BoxHostContract.LaunchPath(box.Paths), new BoxLaunchRequest(box.Config.Id, spec), WinBoxJson.Files.BoxLaunchRequest);
        File.Delete(BoxHostContract.RunPath(box.Paths));
        // A request or result left by an earlier run must not act on, or speak for, this one.
        ShimControl.Reset(box.Paths.Control);

        var hostPid = packages.Activate(package);
        using var host = Process.GetProcessById((int)hostPid);
        var exited = host.WaitForExitAsync(cancellationToken);
        var deadline = limit is { } l ? DateTimeOffset.Now + l : (DateTimeOffset?)null;
        var stopRequested = false;
        BoxRunRecord? last = null;

        while (!exited.IsCompleted)
        {
            await Task.WhenAny(exited, Task.Delay(500, cancellationToken)).ConfigureAwait(false);
            if (ReadRun(box) is { } record && record != last)
            {
                last = record;
                onUpdate?.Invoke(record);
            }
            if (!stopRequested && deadline is { } d && DateTimeOffset.Now >= d)
                stopRequested = RequestStop(box.Config.Id);
        }

        return ReadRun(box) ?? new BoxRunRecord
        {
            State = BoxRunState.Failed,
            HostProcessId = hostPid,
            StartedAt = DateTimeOffset.Now,
            Error = $"BoxHost exited with {host.ExitCode} without writing {BoxHostContract.RunFile}; see {BoxHostContract.LogFile}.",
        };
    }

    /// <summary>
    /// Asks BoxHost to stop the app: BoxShim asks it to end as at sign-out, ends what is left, and saves the
    /// box's state before the sandbox closes. Returns false when no BoxHost is listening (yet).
    /// </summary>
    public static bool RequestStop(Guid boxId)
    {
        if (!EventWaitHandle.TryOpenExisting(BoxHostContract.StopEventName(boxId), out var stop))
            return false;
        using (stop)
            return stop.Set();
    }

    /// <summary>
    /// Asks BoxShim to start the app once more inside the sandbox, which brings back the window of an app
    /// that hid in the notification area (where the sandbox does not let it show). False if the box is not running.
    /// </summary>
    public static bool RequestShow(LoadedBox box)
    {
        if (!IsRunning(box))
            return false;
        ShimControl.Request(box.Paths.Control, ShimControl.ShowRequest);
        return true;
    }

    /// <summary>
    /// Whether the box's sandbox is up right now (including while it closes), whoever started it: this
    /// WinBox, another one, or the CLI.
    /// </summary>
    /// <remarks>
    /// Liveness comes from BoxHost's stop event, not from the process id in run.json: a BoxHost killed at
    /// shutdown never writes Exited, and after a reboot its old id may belong to any process, including
    /// ones this user cannot even query. run.json only says when that BoxHost is done; a run.json that
    /// cannot be read right now (being replaced) does not make a live box look stopped.
    /// </remarks>
    public static bool IsRunning(LoadedBox box) =>
        HostIsAlive(box.Config.Id) && ReadRun(box) is not { State: BoxRunState.Exited or BoxRunState.Failed };

    public static BoxRunRecord? ReadRun(LoadedBox box)
    {
        var path = BoxHostContract.RunPath(box.Paths);
        try
        {
            return File.Exists(path) ? JsonFile.Read(path, WinBoxJson.Files.BoxRunRecord) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null; // Mid-write; the next poll picks it up.
        }
    }

    private static bool HostIsAlive(Guid boxId)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(BoxHostContract.StopEventName(boxId), out var stop))
                return false;
            stop.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // It exists; only BoxHost creates it.
        }
    }
}
