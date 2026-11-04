using System.Runtime.InteropServices;

namespace WinBox.Platform;

/// <summary>Hard links, for copies of files that are never written to in place (see BoxStore's backups).</summary>
internal static partial class HardLink
{
    /// <summary>Makes <paramref name="link"/> a hard link to <paramref name="existing"/>; false where it cannot (another volume, FAT).</summary>
    public static bool TryCreate(string link, string existing) => CreateHardLinkW(Extended(link), Extended(existing), 0) != 0;

    /// <summary>
    /// The path in its \\?\ form. WinBox does not declare itself long-path aware, so CreateHardLinkW refuses paths past
    /// MAX_PATH in their plain form, and a box's saved state goes far deeper than that (measured: a 424-character path
    /// was copied instead of linked).
    /// </summary>
    internal static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);
}
