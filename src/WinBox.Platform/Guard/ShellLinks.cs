using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WinBox.Platform.Guard;

/// <summary>Reads .lnk files with the shell's ShellLink object (shobjidl_core.h), resolved as Explorer resolves them.</summary>
internal static unsafe class ShellLinks
{
    /// <summary>A shortcut's target, arguments and working directory, separated by NULs.</summary>
    public static string Read(string file)
    {
        var link = ComObjects.Create<IShellLinkW>(new Guid("00021401-0000-0000-C000-000000000046"));
        ((IPersistFile)link).Load(file, StgmRead);
        var buffer = new char[32768];
        fixed (char* text = buffer)
        {
            text[0] = '\0';
            var target = Text(link.GetPath(text, buffer.Length, 0, 0), text);
            text[0] = '\0';
            var arguments = Text(link.GetArguments(text, buffer.Length), text);
            text[0] = '\0';
            var directory = Text(link.GetWorkingDirectory(text, buffer.Length), text);
            return $"{target}\0{arguments}\0{directory}";
        }
    }

    private static string Text(int hresult, char* text) => hresult >= 0 ? new string(text) : "";

    private const uint StgmRead = 0;
}

[GeneratedComInterface]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    [PreserveSig] int GetPath(char* file, int length, nint findData, uint flags);
    [PreserveSig] int GetIDList();
    [PreserveSig] int SetIDList();
    [PreserveSig] int GetDescription();
    [PreserveSig] int SetDescription();
    [PreserveSig] int GetWorkingDirectory(char* directory, int length);
    [PreserveSig] int SetWorkingDirectory();
    [PreserveSig] int GetArguments(char* arguments, int length);
}

/// <summary>IPersistFile, after IPersist's one slot.</summary>
[GeneratedComInterface]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    [PreserveSig] int GetClassID();
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
}
