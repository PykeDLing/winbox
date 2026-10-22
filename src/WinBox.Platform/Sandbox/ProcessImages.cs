using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinBox.Platform.Sandbox;

/// <summary>Finds running processes by where their executable lives.</summary>
public static partial class ProcessImages
{
    /// <summary>Processes whose executable is inside <paramref name="directory"/>. The caller disposes them.</summary>
    public static List<Process> Under(string directory)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd('\\') + @"\";
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            if (ImagePath(process.Id) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                found.Add(process);
            else
                process.Dispose();
        }
        return found;
    }

    public static unsafe string? ImagePath(int processId)
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

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
