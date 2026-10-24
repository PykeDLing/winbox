using System.Runtime.InteropServices;

namespace WinBox.Platform.Sandbox;

/// <summary>
/// Hears when Windows ends the session (shutdown, restart, sign-out) and holds it up while a handler runs.
/// Only top-level windows receive WM_QUERYENDSESSION / WM_ENDSESSION, so a hidden one is pumped on a thread
/// of its own. The shutdown level decides when this process hears of it relative to the others.
/// </summary>
public static unsafe partial class SessionEnd
{
    private const string ClassName = "WinBox.SessionEnd";
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;

    private static Action? s_onEnding;
    private static string s_reason = "";

    /// <summary>Starts watching; false if the window could not be created (the process then just ends with the session).</summary>
    /// <param name="shutdownLevel">0x100–0x3FF; higher hears of the session ending earlier (the default is 0x280).</param>
    /// <param name="reason">What Windows shows while <paramref name="onEnding"/> holds up the shutdown.</param>
    public static bool Watch(uint shutdownLevel, string reason, Action onEnding)
    {
        s_onEnding = onEnding;
        s_reason = reason;
        SetProcessShutdownParameters(shutdownLevel, 0);
        var window = (nint)0;
        using var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            var instance = GetModuleHandleW(null);
            fixed (char* className = ClassName)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                    hInstance = instance,
                    lpszClassName = (nint)className,
                };
                RegisterClassExW(in windowClass);
            }
            window = CreateWindowExW(0, ClassName, ClassName, 0, 0, 0, 0, 0, 0, 0, instance, 0);
            ready.Set();
            while (window != 0 && GetMessageW(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(in message);
                DispatchMessageW(in message);
            }
        }) { IsBackground = true, Name = "session end" }.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return window != 0;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmQueryEndSession)
        {
            // Shown on the "apps are preventing shutdown" screen should the handler below take a while.
            ShutdownBlockReasonCreate(window, s_reason);
            return 1;
        }
        if (message == WmEndSession)
        {
            try
            {
                if (wParam != 0)
                    s_onEnding?.Invoke();
            }
            catch
            {
                // Nothing may escape into the window procedure.
            }
            finally
            {
                ShutdownBlockReasonDestroy(window);
            }
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int SetProcessShutdownParameters(uint level, uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(in WNDCLASSEXW windowClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(in MSG message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in MSG message);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ShutdownBlockReasonCreate(nint window, string reason);

    [LibraryImport("user32.dll")]
    private static partial int ShutdownBlockReasonDestroy(nint window);
}
