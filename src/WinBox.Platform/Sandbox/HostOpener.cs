using System.ComponentModel;
using System.Runtime.InteropServices;
using WinBox.Core.Sandbox;

namespace WinBox.Platform.Sandbox;

/// <summary>
/// Opens a box's files and folders outside the box, for the user, from WinBox itself (see <see cref="ManualOpenPolicy"/>).
/// WinBox runs unpackaged and outside any sandbox, so what it starts is an ordinary program of the user's: no
/// sandbox, no box package identity. A boxed app cannot open things outside its box by itself; this is the
/// user doing it through WinBox.
/// </summary>
public static unsafe partial class HostOpener
{
    /// <summary>Null once the file was handed to its program; otherwise why it was not.</summary>
    public static ManualOpenRefusal? OpenFile(LaunchSpec spec, string path)
    {
        if (FinalPath(path, out var isFolder) is not { } final || isFolder)
            return ManualOpenRefusal.Missing;
        if (ManualOpenPolicy.CheckFile(spec, final) is { } refusal)
            return refusal;
        var extension = Path.GetExtension(final);
        if (extension.Length > 0 && AssocIsDangerous(extension) != 0)
            return ManualOpenRefusal.DangerousType;
        if (!HasProgram(extension))
            return ManualOpenRefusal.NoAssociation;
        MarkFromInternet(final);
        Shell(final);
        return null;
    }

    /// <summary>Null once Explorer was asked to show the folder; otherwise why it was not.</summary>
    public static ManualOpenRefusal? OpenFolder(LaunchSpec spec, string path)
    {
        if (FinalPath(path, out var isFolder) is not { } final || !isFolder)
            return ManualOpenRefusal.Missing;
        if (ManualOpenPolicy.CheckFolder(spec, final) is { } refusal)
            return refusal;
        Shell(final);
        return null;
    }

    /// <summary>
    /// The path the file system itself names for <paramref name="path"/>: junctions, symbolic links and short
    /// names resolved, so the check sees where it really leads. Null when it does not exist.
    /// </summary>
    private static string? FinalPath(string path, out bool isFolder)
    {
        isFolder = false;
        if (!Path.IsPathFullyQualified(path))
            return null;
        using var handle = CreateFileW(path, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName)
                return null;
            throw new Win32Exception(error);
        }
        const int capacity = 32768;
        var buffer = new char[capacity];
        uint length;
        fixed (char* chars = buffer)
            length = GetFinalPathNameByHandleW(handle, chars, capacity, 0);
        if (length == 0 || length >= capacity)
            throw new Win32Exception();
        var final = new string(buffer, 0, (int)length);
        isFolder = Directory.Exists(final);
        // "\\?\E:\..." for local volumes; "\\?\UNC\..." and volume GUID paths are left as they are, and refused.
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) && final.Length > 6 && final[5] == ':'
            ? final[4..]
            : final;
    }

    /// <summary>Whether some program on this computer opens files of this type.</summary>
    private static bool HasProgram(string extension)
    {
        if (extension.Length == 0)
            return false;
        uint size = 0;
        // A command line or a DelegateExecute handler (Explorer's archive folders have both). Asked for the
        // length only: S_FALSE (or S_OK) means there is something to return.
        return AssocQueryStringW(AssocfNone, AssocstrCommand, extension, "open", null, ref size) is 0 or SFalse
            || AssocQueryStringW(AssocfNone, AssocstrDelegateExecute, extension, "open", null, ref size) is 0 or SFalse;
    }

    /// <summary>
    /// Marks the file as coming from the internet unless it already carries a zone: Office then opens it in
    /// Protected View, and programs unpacked from an archive get a SmartScreen check. Drives without
    /// alternate data streams (FAT, exFAT) simply keep the file unmarked.
    /// </summary>
    private static void MarkFromInternet(string path)
    {
        var zone = path + ":Zone.Identifier";
        try
        {
            if (!File.Exists(zone))
                File.WriteAllText(zone, "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not markable here; it opens all the same.
        }
    }

    private static void Shell(string path)
    {
        fixed (char* verb = "open", file = path)
        {
            var info = new ShellExecuteInfo
            {
                cbSize = sizeof(ShellExecuteInfo),
                fMask = SeeMaskFlagNoUi,
                lpVerb = verb,
                lpFile = file,
                nShow = SwShowNormal,
            };
            if (!ShellExecuteExW(ref info))
                throw new Win32Exception();
        }
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidName = 123;
    private const int AssocfNone = 0;
    private const int AssocstrCommand = 1;
    private const int AssocstrDelegateExecute = 18;
    private const int SFalse = 1;
    private const uint SeeMaskFlagNoUi = 0x400;
    private const int SwShowNormal = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public char* lpVerb;
        public char* lpFile;
        public char* lpParameters;
        public char* lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public char* lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, char* path, uint size, uint flags);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AssocIsDangerous(string type);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AssocQueryStringW(int flags, int str, string assoc, string? extra, char* output, ref uint size);

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ref ShellExecuteInfo info);
}
