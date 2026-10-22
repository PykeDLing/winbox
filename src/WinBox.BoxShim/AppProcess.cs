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
        var commandLine = (CommandLine.Build(executable, arguments) + "\0").ToCharArray();
        var startup = new Native.STARTUPINFOW { cb = (uint)sizeof(Native.STARTUPINFOW) };
        Native.PROCESS_INFORMATION info;
        fixed (char* line = commandLine)
        {
            // Suspended until it is in the job, so not even its first child can slip past.
            if (Native.CreateProcessW(null, line, 0, 0, 0, Native.CreateSuspended, 0, Path.GetDirectoryName(executable), in startup, out info) == 0)
                throw new Win32Exception();
        }
        if (job != 0 && Native.AssignProcessToJobObject(job, info.hProcess) == 0)
        {
            Native.CloseHandle(job);
            job = 0;
        }
        Native.ResumeThread(info.hThread);
        Native.CloseHandle(info.hThread);
        return new AppProcess(info.hProcess, info.dwProcessId, job, Path.GetDirectoryName(Path.GetFullPath(executable))!);
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
            || !AnyProcessUnder(_appDirectory);
    }

    private static bool AnyProcessUnder(string directory)
    {
        var prefix = directory.TrimEnd('\\') + @"\";
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (Native.ImagePath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        Native.CloseHandle(_process);
        if (_job != 0)
            Native.CloseHandle(_job);
    }
}
