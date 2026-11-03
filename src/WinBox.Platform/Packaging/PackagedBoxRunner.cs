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
public sealed class PackagedBoxRunner(IBoxPackageManager packages, BoxShimDeployer shim) : IBoxRunner
{
    /// <summary>How long a freshly activated BoxHost may take to start listening before the start is handed over anyway.</summary>
    private static readonly TimeSpan ListenWait = TimeSpan.FromSeconds(30);

    public async Task<IBoxRun> StartAsync(LoadedBox box, LaunchSpec spec, CancellationToken cancellationToken = default)
    {
        var package = box.Config.Package ?? throw new InvalidOperationException($"{box.Config.Name} has no registered package.");
        if (IsRunning(box))
            throw new BoxBusyException(box.Config.Id, BoxBusyReason.Running);

        packages.RefreshHost(box);
        shim.Deploy(box.Paths);
        BoxProfile.Prepare(box.Paths, package.FamilyName, WindowsHostAreas.PersonalFolders());
        JsonFile.Write(BoxHostContract.LaunchPath(box.Paths), new BoxLaunchRequest(box.Config.Id, spec), WinBoxJson.Files.BoxLaunchRequest);
        File.Delete(BoxHostContract.RunPath(box.Paths));
        // A request or result left by an earlier run must not act on, or speak for, this one.
        ShimControl.Reset(box.Paths.Control);

        var hostPid = packages.Activate(package);
        Process? host;
        try
        {
            host = Process.GetProcessById((int)hostPid);
        }
        catch (ArgumentException)
        {
            host = null; // Gone already: it failed at once, and run.json (if any) says why.
        }

        var deadline = DateTimeOffset.UtcNow + ListenWait;
        while (host is { HasExited: false } && !HostIsAlive(box.Config.Id) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        return new PackagedRun(this, box, host, hostPid);
    }

    private sealed class PackagedRun(PackagedBoxRunner runner, LoadedBox box, Process? host, uint hostPid) : IBoxRun
    {
        public async Task<BoxRunRecord> WaitAsync(TimeSpan? limit = null, Action<BoxRunRecord>? onUpdate = null, CancellationToken cancellationToken = default)
        {
            var exited = host?.WaitForExitAsync(cancellationToken) ?? Task.CompletedTask;
            var deadline = limit is { } l ? DateTimeOffset.Now + l : (DateTimeOffset?)null;
            var stopRequested = false;
            BoxRunRecord? last = null;

            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(500, cancellationToken)).ConfigureAwait(false);
                if (runner.ReadRun(box) is { } record && record != last)
                {
                    last = record;
                    onUpdate?.Invoke(record);
                }
                if (!stopRequested && deadline is { } d && DateTimeOffset.Now >= d)
                    stopRequested = runner.RequestStop(box.Config.Id);
            }

            return runner.ReadRun(box) ?? new BoxRunRecord
            {
                State = BoxRunState.Failed,
                HostProcessId = hostPid,
                StartedAt = DateTimeOffset.Now,
                Error = $"BoxHost exited{(host is null ? "" : $" with {host.ExitCode}")} without writing {BoxHostContract.RunFile}; see {BoxHostContract.LogFile}.",
            };
        }

        public void Dispose() => host?.Dispose();
    }

    public bool RequestStop(Guid boxId)
    {
        if (!EventWaitHandle.TryOpenExisting(BoxHostContract.StopEventName(boxId), out var stop))
            return false;
        using (stop)
            return stop.Set();
    }

    /// <summary>
    /// Asks BoxShim to start the app once more inside the sandbox, which brings back the window of an app
    /// that hid in the notification area (where the sandbox does not let it show).
    /// </summary>
    public bool RequestShow(LoadedBox box)
    {
        if (!IsRunning(box))
            return false;
        ShimControl.Request(box.Paths.Control, ShimControl.ShowRequest);
        return true;
    }

    /// <remarks>
    /// Liveness comes from BoxHost's stop event, not from the process id in run.json: a BoxHost killed at
    /// shutdown never writes Exited, and after a reboot its old id may belong to any process, including
    /// ones this user cannot even query. run.json only says when that BoxHost is done; a run.json that
    /// cannot be read right now (being replaced) does not make a live box look stopped.
    /// </remarks>
    public bool IsRunning(LoadedBox box) =>
        HostIsAlive(box.Config.Id) && ReadRun(box) is not { State: BoxRunState.Exited or BoxRunState.Failed };

    public BoxRunRecord? ReadRun(LoadedBox box)
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
