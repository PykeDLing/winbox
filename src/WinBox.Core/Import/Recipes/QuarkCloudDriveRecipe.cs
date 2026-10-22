using WinBox.Core.Boxes;
using WinBox.Core.Sandbox;

namespace WinBox.Core.Import.Recipes;

/// <summary>
/// 夸克网盘 PC (QuarkCloudDrivePC). The download page serves a ~4 MB "releasemini" stub whose own
/// download-failure fallback link points at the full Inno Setup package; WinBox follows that link
/// instead of running the stub. The app is Chromium-based, so its data goes to --user-data-dir.
/// </summary>
public sealed class QuarkCloudDriveRecipe : IRecipe
{
    public static readonly Uri FullPackageUrl =
        new("https://download.quark.cn/download/quarkclouddrivepc?platform=pc&ch=pckk@app_downloader_fail");

    public string Id => "quark-cloud-drive";
    public string DisplayName => "夸克网盘";
    public string ExpectedSigner => "ALIBABA (CHINA) NETWORK TECHNOLOGY CO.,LTD.";
    public string EntryFileName => "quark_cloud_drive.exe";

    public IReadOnlyList<string> LaunchArguments =>
    [
        $"--user-data-dir={LaunchPlanner.DataPlaceholder}\\User Data",
        // Chromium's own sandbox cannot be created inside an AppContainer; MXC is the outer sandbox.
        "--no-sandbox",
    ];

    public IReadOnlyList<CompatibilityIssue> KnownIssues =>
    [
        new(IssueCode.InstallerScriptsSkipped, IssueSeverity.Info),
        new(IssueCode.SelfUpdaterDisabled, IssueSeverity.Info, "QuarkCloudDriveUpdater"),
        new(IssueCode.ShellExtensionUnavailable, IssueSeverity.Limited),
    ];

    /// <summary>The 夸克网盘Lite desktop widget; its version info has no OriginalFilename. Name taken from the app's own launch attempt.</summary>
    public IReadOnlyDictionary<string, string> PlaceholderFileNames { get; } = new Dictionary<string, string>
    {
        ["code$GetAppWidgetExeName"] = "clouddrive_desktop_widget_independent.exe",
    };

    public bool Matches(InstallerFacts facts)
    {
        if (facts.Signature is not { IsValid: true } sig || sig.CommonName != ExpectedSigner)
            return false;
        var product = facts.Executable?.ProductName ?? "";
        var description = facts.Executable?.FileDescription ?? "";
        return product.StartsWith("QuarkCloudDrivePC", StringComparison.OrdinalIgnoreCase)
            || description.Contains("夸克网盘", StringComparison.Ordinal)
            || facts.FileName.StartsWith("QuarkCloudDrivePC_", StringComparison.OrdinalIgnoreCase);
    }

    public PayloadSource? ResolvePayload(InstallerFacts facts) =>
        facts.Kind == InstallerKind.InnoSetup ? null : new PayloadSource(FullPackageUrl, InstallerKind.InnoSetup);
}
