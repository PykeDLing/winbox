using WinBox.Core.Sandbox;

namespace WinBox.BoxShim;

/// <summary>Takes WinBox's requests (see <see cref="ShimControl"/>) from the box's Control folder while the app runs.</summary>
internal sealed class ControlRequests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(300);
    private readonly CancellationTokenSource _stop = new();

    private ControlRequests()
    {
    }

    /// <summary>Calls <paramref name="handle"/> with each request as it arrives, one at a time, on a thread of its own.</summary>
    public static ControlRequests Start(string directory, Action<string> handle)
    {
        var requests = new ControlRequests();
        var token = requests._stop.Token;
        new Thread(() =>
        {
            while (!token.WaitHandle.WaitOne(Interval))
            {
                // A quit wins over a show that arrived with it.
                foreach (var request in new[] { ShimControl.QuitRequest, ShimControl.ShowRequest })
                {
                    if (ShimControl.Take(directory, request))
                        handle(request);
                }
            }
        }) { IsBackground = true, Name = "control requests" }.Start();
        return requests;
    }

    public void Dispose() => _stop.Cancel();
}
