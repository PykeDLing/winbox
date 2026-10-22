using WinBox.Core.Boxes;
using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Packaging;

namespace WinBox.App.Library;

/// <summary>Everything the library says to the user. Core stores codes; the wording lives only here.</summary>
public static class Texts
{
    public static string Activity(BoxActivity activity) => activity switch
    {
        BoxActivity.Starting => "正在启动…",
        BoxActivity.Running => "运行中",
        BoxActivity.Stopping => "正在退出并保存数据…",
        BoxActivity.Closing => "数据已保存，正在关闭沙盒…",
        BoxActivity.Removing => "正在删除…",
        _ => "未运行",
    };

    public static string Step(ImportJob job) => job.Step switch
    {
        ImportStep.Inspecting => "读取安装包",
        ImportStep.Downloading when job.Download is { Total: > 0 } d =>
            $"下载完整安装包 {d.Received * 100 / d.Total.Value}%（{d.Total.Value / (1024 * 1024)} MB）",
        ImportStep.Downloading => "下载完整安装包",
        ImportStep.Extracting => "取出程序（不运行安装程序）",
        ImportStep.CheckingCompatibility => "检查兼容性",
        ImportStep.Finishing => "收尾",
        _ => "创建盒子的应用包",
    };

    public static string Issue(CompatibilityIssue issue) => issue.Code switch
    {
        IssueCode.InstallerScriptsSkipped => "没有运行安装程序：右键菜单、开机自启等安装步骤都已跳过",
        IssueCode.SelfUpdaterDisabled => "自带的自动更新不可用，由 WinBox 负责更新",
        IssueCode.FilesOutsideAppSkipped => "安装包里要装到程序目录以外的文件没有放进盒子",
        IssueCode.ShellExtensionUnavailable => "右键菜单等资源管理器扩展不可用",
        _ => issue.Code.ToString(),
    };

    public static string ImportFailed(ImportFailure failure, string? detail) => failure switch
    {
        ImportFailure.UnsupportedFormat => $"暂不支持这种安装包{Detail(detail)}",
        ImportFailure.StubWithoutRecipe => "这是一个在线安装器，WinBox 还不知道去哪里拿它的完整安装包",
        ImportFailure.UntrustedPayload => $"下载到的安装包签名对不上，已停止{Detail(detail)}",
        ImportFailure.NoEntryFound => "安装包里没有找到可以启动的程序",
        _ => failure.ToString(),
    };

    /// <summary>What went wrong with a finished run, or null if nothing did.</summary>
    public static string? RunProblem(BoxRunRecord record)
    {
        if (record.State == BoxRunState.Failed)
            return $"没能启动：{record.Error}";
        if (record.ForceStopped == true || record.ExitCode is null or 0)
            return null;
        var quick = record.EndedAt is { } ended && ended - record.StartedAt < TimeSpan.FromSeconds(5);
        return quick ? $"启动后很快就退出了（{ExitCode(record.ExitCode.Value)}）" : null;
    }

    public static string ExitCode(int code)
    {
        var hex = $"0x{code:X8}";
        return (uint)code switch
        {
            0xC0000135 => $"{hex}，缺少 DLL",
            0xC0000022 => $"{hex}，沙盒拒绝了访问",
            0xC0000142 => $"{hex}，DLL 初始化失败",
            0x80000003 => $"{hex}，程序自带的沙盒和 WinBox 冲突",
            _ => $"退出码 {hex}",
        };
    }

    public static string RemoveFailed(Exception e) => $"删除没有完成：{e.Message}";

    public const string StillRunning = "它在 30 秒内没有退出，请先强制退出再删除。";

    /// <summary>Tells same-named boxes apart (two imports of one installer look identical otherwise).</summary>
    public static string Added(DateTimeOffset createdAt) => $"添加于 {createdAt.ToLocalTime():M月d日 HH:mm}";

    public static string Unexpected(Exception e) => $"出错了：{e.Message}";

    public static string SandboxUnavailable(string? reason) =>
        $"这台电脑用不了 WinBox 的沙盒（需要 Windows 11 25H2）。{reason}";

    public static string PackagesUnavailable(string reason) =>
        $"不能为盒子创建应用包，软件无法隔离运行。请在 设置 > 系统 > 开发者选项 里打开开发者模式。（{reason}）";

    public static string Leak(HostReference leak) => leak.Location switch
    {
        HostLocationKind.File => $"快捷方式 {Path.GetFileName(leak.Path)}",
        HostLocationKind.ScheduledTask => $"计划任务 {leak.Path}",
        _ when leak.Path.EndsWith(@"\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase) => $"开机启动项 {leak.Name}",
        _ => $"注册表 {leak.Path}",
    };

    private static string Detail(string? detail) => detail is null ? "" : $"（{detail}）";
}
