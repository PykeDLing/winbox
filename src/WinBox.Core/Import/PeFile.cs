using System.Buffers.Binary;

namespace WinBox.Core.Import;

/// <summary>Reads the little WinBox needs from a Windows executable's headers, without loading it.</summary>
public static class PeFile
{
    private const ushort SubsystemWindowsGui = 2;
    private const ushort SubsystemWindowsConsole = 3;

    /// <summary>Whether the file is a Windows program or DLL (an MZ header leading to a PE header).</summary>
    public static bool IsExecutable(string path) => ReadSubsystem(path, out _);

    /// <summary>True for a program with windows, false for a console program, null when it is neither or unreadable.</summary>
    public static bool? IsGui(string path) => ReadSubsystem(path, out var subsystem)
        ? subsystem switch { SubsystemWindowsGui => true, SubsystemWindowsConsole => false, _ => null }
        : null;

    private static bool ReadSubsystem(string path, out ushort subsystem)
    {
        subsystem = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> dos = stackalloc byte[64];
            if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z')
                return false;
            var offset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
            if (offset <= 0 || offset > stream.Length - 0x60)
                return false;
            // PE signature, the 20-byte file header, then the optional header with the subsystem 68 bytes in,
            // at the same place for 32-bit and 64-bit programs.
            Span<byte> header = stackalloc byte[4 + 20 + 70];
            stream.Position = offset;
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header[..4].SequenceEqual("PE\0\0"u8))
                return false;
            subsystem = BinaryPrimitives.ReadUInt16LittleEndian(header[(4 + 20 + 68)..]);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
