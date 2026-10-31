using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;

namespace WinBox.App;

/// <summary>
/// One WinBox per data root. Starting WinBox again (the desktop shortcut while it sits in the notification area)
/// brings back the window of the WinBox already running, rather than a second one following, starting and
/// stopping the same boxes.
/// </summary>
internal static class SingleInstance
{
    /// <summary>Another start was handed over to this WinBox. Raised on a background thread.</summary>
    public static event Action? ShowRequested;

    /// <summary>
    /// Registers this process as the WinBox of <paramref name="dataRoot"/>, or hands this start over to the one
    /// registered already. True when it was handed over, and this process should end.
    /// </summary>
    public static bool HandOver(string dataRoot)
    {
        var running = AppInstance.FindOrRegisterForKey(Key(dataRoot));
        if (running.IsCurrent)
        {
            AppInstance.GetCurrent().Activated += (_, _) => ShowRequested?.Invoke();
            return false;
        }
        // The user started this process, so it may let the other one take the foreground.
        AllowSetForegroundWindow(running.ProcessId);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // A cross-process call: made from the thread pool, so it never waits on this thread's apartment.
        Task.Run(async () => await running.RedirectActivationToAsync(activation)).Wait();
        return true;
    }

    /// <summary>Scoped by the data root: an installed WinBox and a development build never stand in for each other.</summary>
    private static string Key(string dataRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        return "WinBox-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16];
    }

    // DllImport, not LibraryImport: that would need unsafe code in the App for one blittable call.
    [DllImport("user32.dll")]
    private static extern int AllowSetForegroundWindow(uint processId);
}
