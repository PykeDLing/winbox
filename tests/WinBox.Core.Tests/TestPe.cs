using System.Buffers.Binary;
using System.Text;

namespace WinBox.Core.Tests;

/// <summary>
/// Builds small 64-bit Windows executables with only what WinBox reads from them: the subsystem, imports (loaded
/// with the program or delay-loaded) and an embedded manifest, all in one section.
/// </summary>
internal static class TestPe
{
    public const ushort Native = 1;
    public const ushort Gui = 2;
    public const ushort Console = 3;

    private const int HeadersSize = 0x200;
    private const uint SectionRva = 0x1000;

    public static byte[] Build(
        ushort subsystem = Gui,
        IReadOnlyList<(string Dll, string[] Functions)>? imports = null,
        IReadOnlyList<(string Dll, string[] Functions)>? delayed = null,
        string? manifest = null)
    {
        var section = new Section();
        var directories = new (uint Rva, uint Size)[16];
        if (imports is { Count: > 0 })
            directories[1] = section.Imports(imports, delayLoaded: false);
        if (manifest is not null)
            directories[2] = section.Manifest(manifest);
        if (delayed is { Count: > 0 })
            directories[13] = section.Imports(delayed, delayLoaded: true);
        var data = section.ToArray();

        var file = new byte[HeadersSize + Align(data.Length)];
        var span = file.AsSpan();
        "MZ"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[0x3C..], 0x40);
        "PE\0\0"u8.CopyTo(span[0x40..]);
        var coff = span[0x44..];
        BinaryPrimitives.WriteUInt16LittleEndian(coff, 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[16..], 240);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[18..], 0x22);
        var optional = span[0x58..];
        BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x20B);
        BinaryPrimitives.WriteUInt16LittleEndian(optional[68..], subsystem);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[108..], 16);
        for (var i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(optional[(112 + 8 * i)..], directories[i].Rva);
            BinaryPrimitives.WriteUInt32LittleEndian(optional[(116 + 8 * i)..], directories[i].Size);
        }
        var header = span[(0x58 + 240)..];
        ".rdata"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], SectionRva);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)Align(data.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], HeadersSize);
        data.CopyTo(span[HeadersSize..]);
        return file;
    }

    public static string Write(string path, byte[] image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>A manifest asking for <paramref name="level"/>.</summary>
    public static string ManifestFor(string level) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
          <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
            <security>
              <requestedPrivileges>
                <requestedExecutionLevel level="{level}" uiAccess="false"/>
              </requestedPrivileges>
            </security>
          </trustInfo>
        </assembly>
        """;

    private static int Align(int size) => (size + 0x1FF) & ~0x1FF;

    private sealed class Section
    {
        private byte[] _bytes = new byte[0x1000];
        private int _length;

        public byte[] ToArray() => _bytes[.._length];

        private static uint Rva(int offset) => SectionRva + (uint)offset;

        private int Reserve(int size)
        {
            if (_length + size > _bytes.Length)
                Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _length + size));
            var at = _length;
            _length += (size + 7) & ~7;
            return at;
        }

        private int Ascii(string text, int prefix = 0)
        {
            var at = Reserve(prefix + text.Length + 1);
            Encoding.ASCII.GetBytes(text).CopyTo(_bytes, at + prefix);
            return at;
        }

        private void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(at), value);

        public (uint, uint) Imports(IReadOnlyList<(string Dll, string[] Functions)> dlls, bool delayLoaded)
        {
            var size = delayLoaded ? 32 : 20;
            var table = Reserve(size * (dlls.Count + 1));
            for (var i = 0; i < dlls.Count; i++)
            {
                var (dll, functions) = dlls[i];
                var names = Reserve(8 * (functions.Length + 1));
                for (var j = 0; j < functions.Length; j++)
                    BinaryPrimitives.WriteUInt64LittleEndian(_bytes.AsSpan(names + 8 * j), Rva(Ascii(functions[j], prefix: 2)));
                var name = Ascii(dll);
                var descriptor = table + size * i;
                if (delayLoaded)
                {
                    Put(descriptor, 1);
                    Put(descriptor + 4, Rva(name));
                    Put(descriptor + 12, Rva(names));
                    Put(descriptor + 16, Rva(names));
                }
                else
                {
                    Put(descriptor, Rva(names));
                    Put(descriptor + 12, Rva(name));
                    Put(descriptor + 16, Rva(names));
                }
            }
            return (Rva(table), (uint)(size * (dlls.Count + 1)));
        }

        /// <summary>A resource tree holding just the manifest: type 24, name 1, language 0x409.</summary>
        public (uint, uint) Manifest(string manifest)
        {
            var root = Reserve(24);
            var type = Reserve(24);
            var name = Reserve(24);
            var entry = Reserve(16);
            var bytes = Encoding.UTF8.GetBytes(manifest);
            var text = Reserve(bytes.Length);
            bytes.CopyTo(_bytes, text);
            void Directory(int at, uint id, uint target)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(at + 14), 1);
                Put(at + 16, id);
                Put(at + 20, target);
            }
            Directory(root, 24, 0x8000_0000 | (uint)(type - root));
            Directory(type, 1, 0x8000_0000 | (uint)(name - root));
            Directory(name, 0x409, (uint)(entry - root));
            Put(entry, Rva(text));
            Put(entry + 4, (uint)bytes.Length);
            return (Rva(root), (uint)(_length - root));
        }
    }
}
