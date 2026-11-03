using System.Buffers.Binary;
using System.Text;

namespace WinBox.Core.Import;

/// <summary>Reads the little WinBox needs from a Windows executable's headers, without loading it.</summary>
public static class PeFile
{
    private const ushort SubsystemNative = 1;
    private const ushort SubsystemWindowsGui = 2;
    private const ushort SubsystemWindowsConsole = 3;

    private const int ImportDirectory = 1;
    private const int ResourceDirectory = 2;
    private const int DelayImportDirectory = 13;
    private const uint ManifestResourceType = 24;
    private const uint ProcessManifestId = 1;

    // Bounds for damaged or hostile files: real programs stay far below them.
    private const int MaxImportedDlls = 1024;
    private const int MaxImportsPerDll = 16384;
    private const int MaxManifestSize = 1024 * 1024;

    /// <summary>Whether the file is a Windows program or DLL (an MZ header leading to a PE header).</summary>
    public static bool IsExecutable(string path) => ReadSubsystem(path, out _);

    /// <summary>True for a program with windows, false for a console program, null when it is neither or unreadable.</summary>
    public static bool? IsGui(string path) => ReadSubsystem(path, out var subsystem)
        ? subsystem switch { SubsystemWindowsGui => true, SubsystemWindowsConsole => false, _ => null }
        : null;

    /// <summary>Whether the file runs in the kernel's own subsystem: for a .sys file, a driver.</summary>
    public static bool IsNative(string path) => ReadSubsystem(path, out var subsystem) && subsystem == SubsystemNative;

    /// <summary>
    /// The names of the functions the program imports by name, from whichever DLLs, loaded with it or on first use
    /// (delay-loaded); empty when it cannot be read.
    /// </summary>
    public static IReadOnlySet<string> ReadImportedFunctions(string path)
    {
        var functions = new HashSet<string>(StringComparer.Ordinal);
        using var image = Image.Open(path);
        if (image is null)
            return functions;
        // IMAGE_IMPORT_DESCRIPTOR: the lookup table first, or in old linkers' output only the address table (at 16).
        if (image.Directory(ImportDirectory) is { Rva: > 0 } imports)
            ReadNameTables(image, imports.Rva, 20, d => U32(d, 0) is not 0 and var lookup ? lookup : U32(d, 16), functions);
        // IMAGE_DELAYLOAD_DESCRIPTOR: the name table at 16, given as an address by every linker since Visual C++ 7.
        if (image.Directory(DelayImportDirectory) is { Rva: > 0 } delayed)
            ReadNameTables(image, delayed.Rva, 32, d => (U32(d, 0) & 1) != 0 ? U32(d, 16) : 0, functions);
        return functions;
    }

    private static void ReadNameTables(Image image, uint table, int descriptorSize, Func<byte[], uint> nameTable, HashSet<string> functions)
    {
        var thunkSize = image.Is64 ? 8 : 4;
        var descriptor = new byte[descriptorSize];
        var thunk = new byte[8];
        for (var i = 0; i < MaxImportedDlls; i++)
        {
            // The table ends with a descriptor of zeros.
            if (!image.Read(table + (uint)(i * descriptorSize), descriptor) || descriptor.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                break;
            var names = nameTable(descriptor);
            for (var j = 0; names != 0 && j < MaxImportsPerDll; j++)
            {
                if (!image.Read(names + (uint)(j * thunkSize), thunk.AsSpan(0, thunkSize)))
                    break;
                var value = image.Is64 ? BinaryPrimitives.ReadUInt64LittleEndian(thunk) : U32(thunk, 0);
                if (value == 0)
                    break;
                var byOrdinal = image.Is64 ? (value & (1UL << 63)) != 0 : (value & 0x8000_0000) != 0;
                // IMAGE_IMPORT_BY_NAME: a two-byte hint, then the name.
                if (!byOrdinal && image.ReadAsciiZ((uint)(value & 0x7FFF_FFFF) + 2) is { Length: > 0 } name)
                    functions.Add(name);
            }
        }
    }

    /// <summary>
    /// The application manifest built into the program, which Windows reads when the program starts (its
    /// RT_MANIFEST resource 1, else the first there is), or null when it has none or it cannot be read.
    /// </summary>
    public static string? ReadManifest(string path)
    {
        using var image = Image.Open(path);
        if (image is null || image.Directory(ResourceDirectory) is not { Rva: > 0 } resources)
            return null;
        // Type, then name, then language; offsets inside the resource tree count from its start.
        if (image.ResourceEntry(resources.Rva, resources.Rva, ManifestResourceType) is not { Directory: true } type)
            return null;
        if ((image.ResourceEntry(resources.Rva, type.Rva, ProcessManifestId) ?? image.ResourceEntry(resources.Rva, type.Rva, null)) is not { Directory: true } name)
            return null;
        if (image.ResourceEntry(resources.Rva, name.Rva, null) is not { Directory: false } language)
            return null;
        // IMAGE_RESOURCE_DATA_ENTRY: where the data is (an address, unlike the tree's offsets) and how long.
        var entry = new byte[8];
        if (!image.Read(language.Rva, entry))
            return null;
        var size = U32(entry, 4);
        if (size is 0 or > MaxManifestSize)
            return null;
        var data = new byte[size];
        if (!image.Read(U32(entry, 0), data))
            return null;
        var text = data is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
            : data is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(data, 3, data.Length - 3)
            : Encoding.UTF8.GetString(data);
        return text.Trim('\0', ' ', '\r', '\n', '\t');
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

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

    /// <summary>An open executable, read through its section table: addresses in the loaded image map to file offsets.</summary>
    private sealed class Image : IDisposable
    {
        private readonly FileStream _stream;
        private readonly (uint Rva, uint Size)[] _directories;
        private readonly (uint Address, uint Length, uint Offset)[] _sections;

        private Image(FileStream stream, bool is64, (uint, uint)[] directories, (uint, uint, uint)[] sections)
        {
            _stream = stream;
            Is64 = is64;
            _directories = directories;
            _sections = sections;
        }

        public bool Is64 { get; }

        public static Image? Open(string path)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var dos = new byte[64];
                if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z')
                    return Close(stream);
                var offset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
                if (offset <= 0 || offset > stream.Length - 24)
                    return Close(stream);
                var file = new byte[24];
                stream.Position = offset;
                if (stream.ReadAtLeast(file, file.Length, throwOnEndOfStream: false) < file.Length || !file.AsSpan(0, 4).SequenceEqual("PE\0\0"u8))
                    return Close(stream);
                var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
                var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(20));
                var optional = new byte[optionalSize];
                if (optionalSize < 96 || stream.ReadAtLeast(optional, optional.Length, throwOnEndOfStream: false) < optional.Length)
                    return Close(stream);
                // PE32 (0x10B) and PE32+ (0x20B) differ in where the data directories start.
                var is64 = BinaryPrimitives.ReadUInt16LittleEndian(optional) == 0x20B;
                var countAt = is64 ? 108 : 92;
                if (optional.Length < countAt + 4)
                    return Close(stream);
                var count = (int)Math.Min(U32(optional, countAt), (uint)(optional.Length - countAt - 4) / 8);
                var directories = new (uint, uint)[Math.Min(count, 16)];
                for (var i = 0; i < directories.Length; i++)
                    directories[i] = (U32(optional, countAt + 4 + 8 * i), U32(optional, countAt + 8 + 8 * i));

                var table = new byte[40 * sectionCount];
                if (stream.ReadAtLeast(table, table.Length, throwOnEndOfStream: false) < table.Length)
                    return Close(stream);
                var sections = new (uint, uint, uint)[sectionCount];
                for (var i = 0; i < sectionCount; i++)
                {
                    var virtualSize = U32(table, 40 * i + 8);
                    var rawSize = U32(table, 40 * i + 16);
                    // Only what is in the file can be read; the rest of a section is zeros once loaded.
                    sections[i] = (U32(table, 40 * i + 12), virtualSize == 0 ? rawSize : Math.Min(virtualSize, rawSize), U32(table, 40 * i + 20));
                }
                return new Image(stream, is64, directories, sections);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Close(stream);
            }
        }

        private static Image? Close(FileStream? stream)
        {
            stream?.Dispose();
            return null;
        }

        public (uint Rva, uint Size)? Directory(int index) => index < _directories.Length ? _directories[index] : null;

        public bool Read(uint rva, Span<byte> buffer)
        {
            foreach (var (address, length, offset) in _sections)
            {
                if (rva < address || rva - address >= length)
                    continue;
                if (rva - address + (ulong)buffer.Length > length)
                    return false;
                try
                {
                    _stream.Position = offset + (long)(rva - address);
                    return _stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
                }
                catch (IOException)
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>A zero-terminated ASCII string such as an imported function's name.</summary>
        public string? ReadAsciiZ(uint rva, int limit = 512)
        {
            var builder = new StringBuilder();
            var one = new byte[1];
            for (var i = 0; i < limit; i++)
            {
                if (!Read(rva + (uint)i, one))
                    return null;
                if (one[0] == 0)
                    return builder.ToString();
                builder.Append((char)one[0]);
            }
            return null;
        }

        /// <summary>
        /// The entry with <paramref name="id"/> (the first entry when null) in the resource directory at
        /// <paramref name="directory"/>: where it leads, and whether that is another directory or the data's entry.
        /// </summary>
        public (uint Rva, bool Directory)? ResourceEntry(uint root, uint directory, uint? id)
        {
            var header = new byte[16];
            if (!Read(directory, header))
                return null;
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
            var entry = new byte[8];
            for (var i = 0; i < Math.Min(entries, 4096); i++)
            {
                if (!Read(directory + 16 + (uint)(8 * i), entry))
                    return null;
                var name = U32(entry, 0);
                // Entries named by a string (high bit set) never match an id.
                if (id is { } wanted && ((name & 0x8000_0000) != 0 || name != wanted))
                    continue;
                var target = U32(entry, 4);
                return (root + (target & 0x7FFF_FFFF), (target & 0x8000_0000) != 0);
            }
            return null;
        }

        public void Dispose() => _stream.Dispose();
    }
}
