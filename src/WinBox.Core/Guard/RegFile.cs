using System.Text;

namespace WinBox.Core.Guard;

/// <summary>
/// Registry values in .reg syntax ("Windows Registry Editor Version 5.00"), so a backup made of single values
/// goes back with <c>reg import</c>.
/// </summary>
public static class RegFile
{
    public const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>One value line: <c>"name"=data</c>.</summary>
    /// <param name="kind">The REG_* type number: REG_SZ = 1, REG_EXPAND_SZ = 2, REG_BINARY = 3, REG_DWORD = 4, REG_MULTI_SZ = 7, REG_QWORD = 11.</param>
    /// <param name="data">The value as the registry API returns it, environment variables not expanded.</param>
    public static string ValueLine(string name, int kind, object? data)
    {
        var left = name.Length == 0 ? "@" : Quote(name);
        return (kind, data) switch
        {
            // A quoted string cannot hold line breaks or NULs; such text is written as hex like any other kind.
            (1, string s) when s.IndexOfAny(['\r', '\n', '\0']) < 0 => $"{left}={Quote(s)}",
            (1, string s) => $"{left}=hex(1):{Hex(Encoding.Unicode.GetBytes(s + "\0"))}",
            (2, string s) => $"{left}=hex(2):{Hex(Encoding.Unicode.GetBytes(s + "\0"))}",
            (4, int d) => $"{left}=dword:{unchecked((uint)d):x8}",
            (7, string[] lines) => $"{left}=hex(7):{Hex(Encoding.Unicode.GetBytes(string.Concat(lines.Select(l => l + "\0")) + "\0"))}",
            (11, long q) => $"{left}=hex(b):{Hex(BitConverter.GetBytes(q))}",
            (3, byte[] bytes) => $"{left}=hex:{Hex(bytes)}",
            _ => $"{left}=hex({kind:x}):{Hex(data as byte[] ?? [])}",
        };
    }

    private static string Quote(string text) => "\"" + text.Replace(@"\", @"\\").Replace("\"", "\\\"") + "\"";

    private static string Hex(byte[] bytes) => string.Join(',', bytes.Select(b => b.ToString("x2")));
}
