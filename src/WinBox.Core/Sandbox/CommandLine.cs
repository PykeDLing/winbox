using System.Text;

namespace WinBox.Core.Sandbox;

/// <summary>Builds Windows command lines that CommandLineToArgvW (and .NET's argv parsing) split back exactly.</summary>
public static class CommandLine
{
    public static string Build(string executable, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        AppendQuoted(sb, executable);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendQuoted(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}
