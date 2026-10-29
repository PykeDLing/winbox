using WinBox.Core.Boxes;
using WinBox.Core.Guard;
using WinBox.Core.Import;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;

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

    public const string DownloadStalled = "下载中断了：30 秒没有收到任何数据。请检查网络后重新添加";
    public const string NetworkTimeout = "连接下载服务器超时。请检查网络后重新添加";

    public static string NetworkFailed(string detail) => $"下载失败，请检查网络后重新添加（{detail}）";

    public const string DropFolder ="这是一个文件夹。请把安装包文件本身拖进来";
    public const string DropShortcut = "这是一个快捷方式。请把它指向的安装包拖进来";
    public const string DropNotLocal = "只能添加这台电脑本地磁盘上的文件";
    public const string DropMissing = "找不到这个文件";

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
        if (record.StateSaved == false)
            return $"这次运行的数据没能保存回盒子{Detail(record.SaveError)}。上一次保存的数据还在";
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

    public const string FolderLeftOver = "盒子文件夹里还有文件在使用，暂时没删掉，WinBox 下次启动时会再删";

    public const string StillRunning = "它在 75 秒内没有退出，盒子没有删除。请等它退出后再删除。";

    /// <summary>Tells same-named boxes apart (two imports of one installer look identical otherwise).</summary>
    public static string Added(DateTimeOffset createdAt) => $"添加于 {createdAt.ToLocalTime():M月d日 HH:mm}";

    public static string Unexpected(Exception e) => $"出错了：{e.Message}";

    /// <summary>Why WinBox did not open a box's file or folder outside the box.</summary>
    public static string ManualOpenRefused(ManualOpenRefusal refusal, string path) => refusal switch
    {
        ManualOpenRefusal.NotLocalPath => $"没有打开 {path}：只能打开这台电脑本地磁盘上的文件",
        ManualOpenRefusal.OutsideGrants => $"没有打开 {path}：它不在这个盒子能访问的位置里",
        ManualOpenRefusal.Denied => $"没有打开 {path}：这里是已安装的程序或 WinBox 自己的文件，盒子不能访问",
        ManualOpenRefusal.DangerousType =>
            $"没有打开 {Path.GetFileName(path)}：程序、脚本、快捷方式和安装包不能从这里在盒子外打开。确实要运行的话，请自己在资源管理器里打开",
        ManualOpenRefusal.NoAssociation => $"没有打开 {Path.GetFileName(path)}：这台电脑上没有能打开这种文件的程序",
        ManualOpenRefusal.Missing => $"没有打开 {path}：找不到它，可能已被移动或删除",
        _ => refusal.ToString(),
    };

    /// <summary>How the card's menu names one of the user's places: 桌面, 下载, E 盘…</summary>
    public static string Place(string path)
    {
        if (path.Length == 3 && path[1] == ':')
            return $"{char.ToUpperInvariant(path[0])} 盘";
        (Environment.SpecialFolder Folder, string Name)[] known =
        [
            (Environment.SpecialFolder.Desktop, "桌面"),
            (Environment.SpecialFolder.MyDocuments, "文档"),
            (Environment.SpecialFolder.MyPictures, "图片"),
            (Environment.SpecialFolder.MyVideos, "视频"),
            (Environment.SpecialFolder.MyMusic, "音乐"),
        ];
        foreach (var (folder, name) in known)
        {
            if (string.Equals(Environment.GetFolderPath(folder), path, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        var leaf = Path.GetFileName(path);
        return leaf.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ? "下载" : leaf;
    }

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
