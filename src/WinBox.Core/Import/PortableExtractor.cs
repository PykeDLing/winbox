using System.Text.RegularExpressions;
using WinBox.Core.Boxes;

namespace WinBox.Core.Import;

/// <summary>
/// A program that needs no installing: a single executable with no installer inside it. It becomes the box's
/// whole app. Executables that call themselves installers are left alone: they are online installers WinBox has
/// no recipe for, or installers of a kind it cannot unpack, and running one in the box would install nothing.
/// </summary>
public sealed partial class PortableExtractor : IPackageExtractor
{
    public InstallerKind? Recognize(InstallerFacts facts) =>
        facts.Kind == InstallerKind.PortableExe && !LooksLikeInstaller(facts) ? InstallerKind.PortableExe : null;

    public static bool LooksLikeInstaller(InstallerFacts facts) =>
        InstallerName().IsMatch(facts.FileName) ||
        InstallerName().IsMatch(facts.Executable?.ProductName ?? "") ||
        InstallerName().IsMatch(facts.Executable?.FileDescription ?? "");

    [GeneratedRegex(@"setup|install|bootstrap|downloader|安装|下载器", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerName();

    public Task<ExtractionResult> ExtractAsync(string installer, InstallerKind kind, string destination, CancellationToken cancellationToken = default)
    {
        var app = Path.Combine(destination, "app");
        Directory.CreateDirectory(app);
        File.Copy(installer, Path.Combine(app, Path.GetFileName(installer)));
        return Task.FromResult(new ExtractionResult(app, [], [Path.GetFileName(installer)]));
    }
}
