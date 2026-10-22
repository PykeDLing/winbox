using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

/// <summary>What WinBox can learn about a file without running it.</summary>
public sealed record InstallerFacts
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required InstallerKind Kind { get; init; }
    public ExecutableInfo? Executable { get; init; }
    public SignatureInfo? Signature { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>PE version resource fields.</summary>
public sealed record ExecutableInfo(
    string? OriginalFilename,
    string? FileDescription,
    string? ProductName,
    string? CompanyName,
    string? ProductVersion);

/// <summary>Authenticode result. <see cref="Subject"/> is the signer certificate subject.</summary>
public sealed record SignatureInfo(bool IsValid, string? Subject)
{
    /// <summary>The CN= part of the subject, without quotes.</summary>
    public string? CommonName
    {
        get
        {
            if (Subject is null) return null;
            const string prefix = "CN=";
            var start = Subject.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += prefix.Length;
            if (start < Subject.Length && Subject[start] == '"')
            {
                var close = Subject.IndexOf('"', start + 1);
                return close < 0 ? Subject[(start + 1)..] : Subject[(start + 1)..close];
            }
            var comma = Subject.IndexOf(',', start);
            return (comma < 0 ? Subject[start..] : Subject[start..comma]).Trim();
        }
    }
}

/// <summary>Reads version resources and signatures. Implemented on Windows by WinBox.Platform.</summary>
public interface IFileInspector
{
    ExecutableInfo? ReadExecutableInfo(string path);
    SignatureInfo ReadSignature(string path);
}
