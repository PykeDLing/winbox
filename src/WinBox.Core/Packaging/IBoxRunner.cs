using WinBox.Core.Boxes;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Packaging;

/// <summary>
/// Starts boxes through their package and talks to them while they run. Implemented by WinBox.Platform; every
/// method works whoever started the box (this process, another WinBox, the CLI).
/// </summary>
public interface IBoxRunner
{
    /// <summary>
    /// Hands the launch to the box's BoxHost and returns once BoxHost is listening (or has already exited). The
    /// box must have a registered package and must not be running; <see cref="BoxManager"/> makes sure of both
    /// under the box's lock.
    /// </summary>
    Task<IBoxRun> StartAsync(LoadedBox box, LaunchSpec spec, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the box's BoxHost to quit the app: it is asked to end as at sign-out, what is left is ended, and the
    /// box's state is saved before the sandbox closes. False when no BoxHost is listening (yet).
    /// </summary>
    bool RequestStop(Guid boxId);

    /// <summary>Asks the running app to show its window again. False when the box is not running.</summary>
    bool RequestShow(LoadedBox box);

    /// <summary>Whether the box's sandbox is up right now, including while it closes.</summary>
    bool IsRunning(LoadedBox box);

    /// <summary>The box's run.json: how its current or last run went. Null when there is none, or it is being replaced.</summary>
    BoxRunRecord? ReadRun(LoadedBox box);
}

/// <summary>A run of a box that has been handed to its BoxHost.</summary>
public interface IBoxRun : IDisposable
{
    /// <summary>
    /// Follows the run until BoxHost exits and returns how it went. With <paramref name="limit"/>, the app is asked
    /// to quit once that much time has passed.
    /// </summary>
    Task<BoxRunRecord> WaitAsync(TimeSpan? limit = null, Action<BoxRunRecord>? onUpdate = null, CancellationToken cancellationToken = default);
}
