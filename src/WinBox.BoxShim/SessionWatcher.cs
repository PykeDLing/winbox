namespace WinBox.BoxShim;

/// <summary>
/// Gets BoxShim told when Windows ends the session (shutdown, restart, sign-out), which is how a tray app
/// like a cloud drive usually stops. Only top-level windows receive WM_QUERYENDSESSION / WM_ENDSESSION,
/// so this keeps a hidden one on its own thread.
/// </summary>
internal sealed unsafe class SessionWatcher : IDisposable
{
    private const string ClassName = "WinBox.BoxShim.Session";
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;

    private static Action? s_onEnding;
    private static nint s_window;
    private static int s_error;

    /// <summary>The hidden window; 0 if it could not be created (see <see cref="Error"/>).</summary>
    public nint Window => s_window;

    /// <summary>The Win32 error from creating the window, when <see cref="Window"/> is 0.</summary>
    public int Error => s_error;

    public static SessionWatcher Start(Action onEnding)
    {
        s_onEnding = onEnding;
        using var ready = new ManualResetEventSlim();
        new Thread(() => Pump(ready)) { IsBackground = true, Name = "session watcher" }.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return new SessionWatcher();
    }

    private static void Pump(ManualResetEventSlim ready)
    {
        var instance = Native.GetModuleHandleW(null);
        fixed (char* className = ClassName)
        {
            var windowClass = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                hInstance = instance,
                lpszClassName = (nint)className,
            };
            if (Native.RegisterClassExW(in windowClass) == 0)
                s_error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        }
        s_window = Native.CreateWindowExW(0, ClassName, "WinBox.BoxShim", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (s_window == 0 && s_error == 0)
            s_error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        ready.Set();
        while (Native.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(in message);
            Native.DispatchMessageW(in message);
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmQueryEndSession)
            return 1;
        if (message == WmEndSession && wParam != 0)
        {
            // Lists BoxShim on the "apps are preventing shutdown" screen for the second or so the save takes.
            Native.ShutdownBlockReasonCreate(window, "正在保存盒子里的应用数据");
            try { s_onEnding?.Invoke(); }
            catch { /* Nothing may escape into the window procedure. */ }
            finally { Native.ShutdownBlockReasonDestroy(window); }
            return 0;
        }
        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    public void Dispose() => s_onEnding = null;
}
