using System.ComponentModel;
using System.Diagnostics;
using WinBox.Core.Sandbox;

namespace WinBox.BoxShim;

/// <summary>The boxed app's first process and, through a job object, every process it starts.</summary>
internal sealed unsafe class AppProcess : IDisposable
{
    // Without a job (nesting refused), processes from the app's folder that outlive the first one are
    // waited for this long before the state is saved anyway.
    private static readonly TimeSpan UntrackedGrace = TimeSpan.FromSeconds(15);

    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const nint EndSessionCloseApp = 0x1;
    private const uint SmtoAbortIfHung = 0x2;
    private const uint TerminatedExitCode = 1;

    private readonly nint _process;
    private readonly nint _job;
    private readonly string _appDirectory;
    private long _rootExitedAt;

    private AppProcess(nint process, uint processId, nint job, string appDirectory)
    {
        _process = process;
        ProcessId = processId;
        _job = job;
        _appDirectory = appDirectory;
    }

    public uint ProcessId { get; }
    public bool Tracked => _job != 0;
    public int ExitCode { get; private set; } = -1;

    public static AppProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        var job = Native.CreateJobObjectW(0, null);
        var info = CreateSuspended(executable, arguments);
        if (job != 0 && Native.AssignProcessToJobObject(job, info.hProcess) == 0)
        {
            Native.CloseHandle(job);
            job = 0;
        }
        Native.ResumeThread(info.hThread);
        Native.CloseHandle(info.hThread);
        return new AppProcess(info.hProcess, info.dwProcessId, job, Path.GetDirectoryName(Path.GetFullPath(executable))!);
    }

    /// <summary>
    /// Starts the app once more, inside the same job. A single-instance app hands such a start over to the
    /// instance already running, which shows its window again: the way back to an app that hid in the
    /// notification area, which the sandbox does not let it show.
    /// </summary>
    public void StartAnother(string executable, IReadOnlyList<string> arguments)
    {
        var info = CreateSuspended(executable, arguments);
        if (_job != 0)
            Native.AssignProcessToJobObject(_job, info.hProcess);
        Native.ResumeThread(info.hThread);
        Native.CloseHandle(info.hThread);
        Native.CloseHandle(info.hProcess);
    }

    private static Native.PROCESS_INFORMATION CreateSuspended(string executable, IReadOnlyList<string> arguments)
    {
        var commandLine = (CommandLine.Build(executable, arguments) + "\0").ToCharArray();
        var startup = new Native.STARTUPINFOW { cb = (uint)sizeof(Native.STARTUPINFOW) };
        Native.PROCESS_INFORMATION info;
        fixed (char* line = commandLine)
        {
            // Suspended until it is in the job, so not even its first child can slip past.
            if (Native.CreateProcessW(null, line, 0, 0, 0, Native.CreateSuspended, 0, Path.GetDirectoryName(executable), in startup, out info) == 0)
                throw new Win32Exception();
        }
        return info;
    }

    /// <summary>
    /// Tells every window of the app what Windows tells it at sign-out, flagged ENDSESSION_CLOSEAPP the way
    /// Restart Manager does it. Apps that only hide when their window is closed (cloud drives, chat apps) save
    /// their data and exit on this, which a plain WM_CLOSE never achieves. Returns how many windows were told.
    /// </summary>
    public int AskToEnd()
    {
        var processes = ProcessIds();
        var windows = Native.TopLevelWindows()
            .Where(w => Native.GetWindowThreadProcessId(w, out var id) != 0 && processes.Contains(id))
            .ToList();
        foreach (var window in windows)
            Native.SendMessageTimeoutW(window, WmQueryEndSession, 0, EndSessionCloseApp, SmtoAbortIfHung, 2000, out _);
        // Sent whatever the answers were: the user asked the app to quit, and what refuses is ended later.
        foreach (var window in windows)
            Native.SendMessageTimeoutW(window, WmEndSession, 1, EndSessionCloseApp, SmtoAbortIfHung, 3000, out _);
        return windows.Count;
    }

    /// <summary>Ends every process of the app at once.</summary>
    public void Terminate()
    {
        if (_job != 0)
        {
            Native.TerminateJobObject(_job, TerminatedExitCode);
            return;
        }
        foreach (var id in ProcessIds())
        {
            try
            {
                using var process = Process.GetProcessById((int)id);
                process.Kill();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }
    }

    private HashSet<uint> ProcessIds()
    {
        if (_job != 0)
            return Native.ProcessIds(_job);
        var prefix = _appDirectory.TrimEnd('\\') + @"\";
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (Native.ImagePath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    ids.Add((uint)process.Id);
            }
        }
        return ids;
    }

    /// <summary>Waits until the app and everything it started have exited; false if <paramref name="timeout"/> passed first.</summary>
    public bool WaitForAll(TimeSpan timeout)
    {
        var deadline = timeout == Timeout.InfiniteTimeSpan ? long.MaxValue : Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var rootExited = Native.WaitForSingleObject(_process, 0) == 0;
            if (rootExited)
            {
                if (Interlocked.CompareExchange(ref _rootExitedAt, Environment.TickCount64, 0) == 0 &&
                    Native.GetExitCodeProcess(_process, out var code) != 0)
                    ExitCode = unchecked((int)code);
                if (AllOthersExited())
                    return true;
            }
            if (Environment.TickCount64 >= deadline)
                return false;
            if (rootExited)
                Thread.Sleep(250);
            else
                Native.WaitForSingleObject(_process, 250);
        }
    }

    private bool AllOthersExited()
    {
        if (_job != 0)
            return Native.ActiveProcesses(_job) == 0;
        return Environment.TickCount64 - Interlocked.Read(ref _rootExitedAt) > UntrackedGrace.TotalMilliseconds
            || ProcessIds().Count == 0;
    }

    public void Dispose()
    {
        Native.CloseHandle(_process);
        if (_job != 0)
            Native.CloseHandle(_job);
    }
}
