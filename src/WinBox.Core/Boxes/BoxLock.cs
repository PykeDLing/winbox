namespace WinBox.Core.Boxes;

/// <summary>Why WinBox cannot do what was asked to a box right now.</summary>
public enum BoxBusyReason
{
    /// <summary>The box is running (started here, by another WinBox or by the CLI).</summary>
    Running,

    /// <summary>Another WinBox process (the app or the CLI) is starting, registering or removing the box.</summary>
    Locked,
}

public sealed class BoxBusyException(Guid boxId, BoxBusyReason reason)
    : Exception($"Box {boxId:N} is busy: {reason}.")
{
    public Guid BoxId { get; } = boxId;
    public BoxBusyReason Reason { get; } = reason;
}

/// <summary>
/// Makes what WinBox does to one box happen one at a time across processes (the app, the CLI): starting it,
/// registering its package, removing it. Checking that a box is idle and then starting it is otherwise two
/// steps another process can slip between, and two sandboxes would then share the box's saved state.
/// </summary>
/// <remarks>
/// The lock is a file in the data root's Locks folder, opened for this process alone and deleted when closed.
/// Windows closes it when the process ends however it ends, so a crash never leaves a box locked. A second
/// acquire from the same process waits like any other: callers never nest them.
/// </remarks>
public static class BoxLock
{
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(100);

    public static async Task<IDisposable> AcquireAsync(AppPaths paths, Guid boxId, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.Locks);
        var path = Path.Combine(paths.Locks, boxId.ToString("N") + ".lock");
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(Retry, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new BoxBusyException(boxId, BoxBusyReason.Locked);
            }
            catch (UnauthorizedAccessException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Being deleted by the process that just let it go.
                await Task.Delay(Retry, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
