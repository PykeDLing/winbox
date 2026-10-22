using System.Text;
using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

/// <summary>Identifies an installer's format from its bytes. Never executes anything.</summary>
public static class InstallerDetector
{
    // Inno Setup: the loader's offset table ("rDlPtS") and the setup data header both live in the
    // first few megabytes, ahead of the (large) compressed file data.
    private static readonly byte[] InnoLoaderMagic = "rDlPtS"u8.ToArray();
    private static readonly byte[] InnoDataMagic = "Inno Setup Setup Data"u8.ToArray();
    private static readonly byte[] NsisMagic = [0xEF, 0xBE, 0xAD, 0xDE, (byte)'N', (byte)'u', (byte)'l', (byte)'l', (byte)'s', (byte)'o', (byte)'f', (byte)'t'];
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] ZipMagic = "PK\x03\x04"u8.ToArray();

    /// <summary>How much of the file is scanned for embedded markers.</summary>
    public const int ScanLength = 8 * 1024 * 1024;

    public static InstallerKind Detect(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(ScanLength, stream.Length)];
        stream.ReadExactly(buffer);
        return Detect(buffer, Path.GetExtension(path));
    }

    public static InstallerKind Detect(ReadOnlySpan<byte> head, string extension)
    {
        if (head.StartsWith(OleMagic))
            return extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ? InstallerKind.Msi : InstallerKind.Unknown;

        if (head.StartsWith(ZipMagic))
            return extension.ToLowerInvariant() switch
            {
                ".msix" or ".appx" or ".msixbundle" or ".appxbundle" => InstallerKind.Msix,
                _ => InstallerKind.Zip,
            };

        if (!head.StartsWith("MZ"u8))
            return InstallerKind.Unknown;

        if (head.IndexOf(InnoLoaderMagic) >= 0 || head.IndexOf(InnoDataMagic) >= 0)
            return InstallerKind.InnoSetup;
        if (head.IndexOf(NsisMagic) >= 0)
            return InstallerKind.Nsis;

        // An executable with no recognised installer payload: either a portable app or an online
        // stub. Telling those apart needs a recipe (see IRecipe), so report it as portable for now.
        return InstallerKind.PortableExe;
    }

    internal static string Describe(InstallerKind kind) => kind switch
    {
        InstallerKind.InnoSetup => "Inno Setup",
        InstallerKind.Nsis => "NSIS",
        InstallerKind.Msi => "Windows Installer (MSI)",
        InstallerKind.Msix => "MSIX",
        InstallerKind.Zip => "ZIP",
        InstallerKind.PortableExe => "executable",
        InstallerKind.Stub => "online installer",
        _ => "unknown",
    };

    internal static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);
}
