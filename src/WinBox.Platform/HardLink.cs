using System.Runtime.InteropServices;

namespace WinBox.Platform;

/// <summary>Hard links, for copies of files that are never written to in place (see BoxStore's backups).</summary>
internal static partial class HardLink
{
    /// <summary>Makes <paramref name="link"/> a hard link to <paramref name="existing"/>; false where it cannot (another volume, FAT).</summary>
    public static bool TryCreate(string link, string existing) => CreateHardLinkW(link, existing, 0) != 0;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);
}
