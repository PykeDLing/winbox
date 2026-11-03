using System.Runtime.InteropServices;

namespace WinBox.Platform;

/// <summary>What User Account Control makes of the account WinBox runs as.</summary>
public static partial class WindowsAccount
{
    /// <summary>
    /// True when the account is an administrator that User Account Control runs with a filtered token, as it does by
    /// default: a program that asks for the highest rights available then asks to be elevated too.
    /// </summary>
    public static bool IsFilteredAdministrator()
    {
        if (OpenProcessToken(CurrentProcess, TokenQuery, out var token) == 0)
            return false;
        try
        {
            return GetTokenInformation(token, TokenElevationTypeClass, out var type, sizeof(int), out _) != 0
                && type == TokenElevationTypeLimited;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const nint CurrentProcess = -1;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationTypeLimited = 3;

    [LibraryImport("advapi32.dll")]
    private static partial int OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll")]
    private static partial int GetTokenInformation(nint token, int informationClass, out int information, uint length, out uint returned);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
