using System.Runtime.InteropServices;

namespace WinBox.BoxShim;

internal static unsafe partial class Native
{
    public const uint CreateSuspended = 0x00000004;
    private const int JobObjectBasicAccountingInformation = 1;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public uint cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial int CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint creationFlags, nint environment, string? currentDirectory, in STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll")]
    public static partial int AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    private static partial int QueryInformationJobObject(nint job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, uint length, out uint returned);

    public static uint ActiveProcesses(nint job) =>
        QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), out _) != 0
            ? info.ActiveProcesses
            : 0;

    private const int JobObjectBasicProcessIdList = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject")]
    private static partial int QueryJobProcessIds(nint job, int infoClass, void* info, uint length, out uint returned);

    /// <summary>The ids of the processes in <paramref name="job"/> (JOBOBJECT_BASIC_PROCESS_ID_LIST: two counts, then the ids).</summary>
    public static HashSet<uint> ProcessIds(nint job)
    {
        const int capacity = 1024;
        var size = 8 + capacity * sizeof(nuint);
        var buffer = stackalloc byte[size];
        if (QueryJobProcessIds(job, JobObjectBasicProcessIdList, buffer, (uint)size, out _) == 0)
            return [];
        var count = *(uint*)(buffer + 4);
        var ids = (nuint*)(buffer + 8);
        var result = new HashSet<uint>();
        for (var i = 0; i < count; i++)
            result.Add((uint)ids[i]);
        return result;
    }

    [LibraryImport("kernel32.dll")]
    public static partial int TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll")]
    public static partial int SetProcessShutdownParameters(uint level, uint flags);

    [LibraryImport("kernel32.dll")]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial int GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial int QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    public static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, 0, (uint)processId);
        if (handle == 0)
            return null;
        try
        {
            var buffer = stackalloc char[1024];
            var length = 1024u;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref length) != 0 ? new string(buffer, 0, (int)length) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(in WNDCLASSEXW windowClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(in MSG message);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(in MSG message);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    /// <summary>Every top-level window on the desktop, hidden ones included.</summary>
    public static List<nint> TopLevelWindows()
    {
        var windows = new List<nint>();
        var handle = GCHandle.Alloc(windows);
        try
        {
            EnumWindows(&CollectWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return windows;
    }

    [UnmanagedCallersOnly]
    private static int CollectWindow(nint window, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(window);
        return 1;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ShutdownBlockReasonCreate(nint window, string reason);

    [LibraryImport("user32.dll")]
    public static partial int ShutdownBlockReasonDestroy(nint window);
}
