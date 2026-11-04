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
        BoxActivity.Updating => "正在更新…",
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
        ImportStep.Updating => "换上新的程序，登录信息和数据保留",
        _ => "创建盒子的应用包",
    };

    /// <summary>The import card's title: what the installer is for.</summary>
    public static string Importing(ImportJob job) => job.Target is { } box
        ? job.Failed ? $"没能用 {job.FileName} 更新“{box}”" : $"正在用 {job.FileName} 更新“{box}”"
        : job.Failed ? $"没能添加 {job.FileName}" : $"正在添加 {job.FileName}";

    public static string UpdateOfferTitle(UpdateOffer offer) => offer.Picked
        ? $"要把“{offer.Box.Config.Name}”换成旧版本吗？"
        : $"“{offer.Box.Config.Name}”已经在应用库里了";

    /// <summary>What updating the box would do, for the card that asks.</summary>
    public static string UpdateOffer(UpdateOffer offer)
    {
        var (current, incoming) = (offer.Box.Config.Version, offer.Incoming.Version);
        var versions = BoxUpdate.Compare(current, incoming) switch
        {
            VersionChange.Newer => $"安装包里是新版本 {incoming}，盒子里是 {current}。",
            VersionChange.Same => $"安装包里的版本和盒子里的一样（{current}）。",
            VersionChange.Older => $"安装包里是旧版本 {incoming}，盒子里是 {current}。",
            _ => "",
        };
        // Going back keeps the data as the newer version left it, which the older one may not read; a box of its own
        // is the way to try an older version.
        var what = offer.Older
            ? "换成旧版本会保留登录信息、设置和数据，但新版本可能已经把它们改成旧版本读不了的样子，有的软件也不让旧版本登录。"
                + (offer.Picked ? "只想试试旧版本，可以把安装包拖进窗口，另建一个盒子。" : "只想试试旧版本，可以另建一个盒子。")
            : "更新盒子会换上安装包里的程序，登录信息、设置和数据都保留。";
        return versions + what
            + (offer.Running ? offer.Older ? "它正在运行，换之前会先请它保存并退出。" : "它正在运行，更新前会先请它保存并退出。" : "")
            + (offer.Count > 1 ? $"这样的盒子有 {offer.Count} 个，{(offer.Older ? "换" : "更新")}的是{Added(offer.Box.Config.CreatedAt)}的那个。" : "");
    }

    /// <summary>What an update did; <paramref name="previous"/> is the version the box had.</summary>
    public static string Updated(BoxConfig config, string? previous) =>
        string.IsNullOrWhiteSpace(config.Version) ? $"已更新“{config.Name}”，登录信息和数据都在"
        : BoxUpdate.Compare(previous, config.Version) == VersionChange.Older ? $"已把“{config.Name}”换成旧版本 {config.Version}，登录信息和数据都在"
        : $"已把“{config.Name}”更新到 {config.Version}，登录信息和数据都在";

    public const string UpdateWhileBusy = "它正在运行。请先退出它，再更新";

    public static string Issue(CompatibilityIssue issue) => issue.Code switch
    {
        IssueCode.InstallerScriptsSkipped => "没有运行安装程序：右键菜单、开机自启等安装步骤都已跳过",
        IssueCode.SelfUpdaterDisabled => "自带的自动更新不可用：把新版安装包拖进来就能更新这个盒子",
        IssueCode.FilesOutsideAppSkipped => "安装包里要装到程序目录以外的文件没有放进盒子",
        IssueCode.ShellExtensionUnavailable => "右键菜单等资源管理器扩展不可用",
        IssueCode.InnerSandboxDisabled => "基于 Chromium 的软件：它自带的沙盒已关闭，由 WinBox 的沙盒负责隔离",
        IssueCode.KnownIncompatible => issue.Detail switch
        {
            "wechat" => "实测微信不能在盒子里运行：它要读取本机的文件夹设置和磁盘设备，沙盒不允许，启动后会提示“文件保存位置无法使用”并退出",
            "discord" => "实测 Discord 不能在盒子里运行：启动后约 4 秒就自己退出",
            _ => "实测这个软件不能在盒子里运行",
        },
        IssueCode.NeedsAdministrator => issue.Detail == CompatibilityCheck.HighestAvailable
            ? "你的账户是管理员账户，它每次启动都会要管理员权限；盒子里的软件拿不到管理员权限，所以打不开"
            : "它每次启动都要管理员权限，盒子里的软件拿不到管理员权限，所以打不开",
        IssueCode.DriverUnavailable => $"带有驱动程序（{Files(issue.Detail)}）：盒子里不能安装驱动，靠它的功能用不了",
        IssueCode.ServiceUnavailable => $"带有 Windows 服务（{Files(issue.Detail)}）：盒子里不能安装服务，靠它的功能用不了",
        _ => issue.Code.ToString(),
    };

    /// <summary>The card's one-line summary of a box's issues, or null when it has none.</summary>
    public static string? IssueSummary(IReadOnlyList<CompatibilityIssue> issues) =>
        issues.Count == 0 ? null
        : issues.Any(i => i.Severity == IssueSeverity.NeedsVirtualMachine) ? "⚠ 可能无法在盒子里运行"
        : issues.Any(i => i.Severity == IssueSeverity.Limited) ? "⚠ 有些功能在盒子里用不了"
        : $"ⓘ {issues.Count} 条兼容性说明";

    public static string Blocked(ImportJob job) => $"{job.FileName} 可能无法在盒子里运行";

    public const string BlockedNote = "仍然添加的话，盒子会照常建好。打不开时删除盒子即可，电脑上不会留下痕迹。";

    /// <summary>File names stored as "a.sys;b.sys": the first three, then how many more.</summary>
    private static string Files(string? detail)
    {
        var names = (detail ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        return names.Length <= 3 ? string.Join("、", names) : $"{string.Join("、", names.Take(3))} 等 {names.Length} 个";
    }

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
        ImportFailure.StubWithoutRecipe => "这是一个在线安装器，或 WinBox 还不认识的安装程序。请下载软件的完整安装包（离线安装包）再添加",
        ImportFailure.UntrustedPayload => $"下载到的安装包签名对不上，已停止{Detail(detail)}",
        ImportFailure.NoEntryFound => "安装包里没有找到可以启动的程序",
        ImportFailure.PackagedApp => "这是 MSIX 应用包，Windows 会自己隔离安装它，不需要放进盒子",
        ImportFailure.ExtractionFailed => $"没能解开这个安装包（可能已损坏或加了密）{Detail(detail)}",
        ImportFailure.Declined => "没有添加：它可能无法在盒子里运行",
        ImportFailure.NotTheSameApp => $"这个安装包里是“{detail}”，不是这个盒子里的软件。盒子没有改动",
        ImportFailure.OlderVersion => $"这个安装包里是旧版本{Detail(detail)}。盒子没有改动",
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

    /// <summary>Why something could not be done to a box right now.</summary>
    public static string Busy(BoxBusyReason reason) => reason switch
    {
        BoxBusyReason.Running => "它已经在运行（可能是从另一个 WinBox 或命令行打开的）",
        _ => "另一个 WinBox 或命令行正在打开、注册或删除这个盒子，请稍后再试",
    };

    public static string RegisterFailed(Exception e) => $"盒子已添加，但它的应用包没能注册：{e.Message}。打开时会再试一次";

    /// <summary>Tells same-named boxes apart (two imports of one installer look identical otherwise).</summary>
    public static string Added(DateTimeOffset createdAt) => $"添加于 {createdAt.ToLocalTime():M月d日 HH:mm}";

    public static string Unexpected(Exception e) => $"出错了：{e.Message}";

    /// <summary>A box as dialogs and the tray menu name it: 夸克网盘（添加于 10月20日 23:02）.</summary>
    public static string BoxName(BoxEntry entry) => $"{entry.Box.Config.Name}（{Added(entry.Box.Config.CreatedAt)}）";

    /// <summary>The notification-area icon's tooltip.</summary>
    public static string TrayTooltip(LibrarySnapshot snapshot)
    {
        var parts = new List<string> { "WinBox" };
        if (snapshot.Running.Count > 0)
            parts.Add($"{snapshot.Running.Count} 个盒子在运行");
        if (snapshot.Importing > 0)
            parts.Add($"正在添加 {snapshot.Importing} 个安装包");
        return string.Join(" · ", parts);
    }

    public static string CloseActionLabel(CloseAction action) => action switch
    {
        CloseAction.Tray => "最小化到托盘，盒子继续运行",
        CloseAction.Quit => "退出 WinBox 并关闭所有盒子",
        _ => "每次询问",
    };

    /// <summary>The tray menu's item for the setting, which says what it is now.</summary>
    public static string CloseActionItem(CloseAction action) => action switch
    {
        CloseAction.Tray => "关闭窗口时：最小化到托盘…",
        CloseAction.Quit => "关闭窗口时：退出 WinBox…",
        _ => "关闭窗口时：每次询问…",
    };

    public const string CloseActionNote =
        "没有盒子在运行时，关闭窗口会直接退出 WinBox。选“退出”时，每次仍会先列出要关闭的盒子，请你确认。";

    /// <summary>What closing the window would cut short, for the question of what closing it should do.</summary>
    public static string? CloseChoiceNote(LibrarySnapshot snapshot)
    {
        var parts = new List<string>();
        if (snapshot.Running.Count > 0)
            parts.Add($"正在运行：{string.Join("、", snapshot.Running.Select(BoxName))}。退出会先请{Them(snapshot.Running)}保存并退出，正在进行的上传、下载会中断。");
        if (snapshot.Importing > 0)
            parts.Add($"正在添加 {snapshot.Importing} 个安装包，退出会取消。");
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    /// <summary>What quitting WinBox ends, for its confirmation.</summary>
    public static string QuitAllEffects(LibrarySnapshot snapshot)
    {
        var parts = new List<string>();
        if (snapshot.Running.Count > 0)
        {
            parts.Add((snapshot.Running.Count == 1 ? "这个盒子会退出：\n" : "这些盒子会退出：\n")
                + string.Join("\n", snapshot.Running.Select(b => "· " + BoxName(b))));
            parts.Add($"会先请{Them(snapshot.Running)}自己保存并退出，10 秒后还没退出的部分会被结束。数据都会保存回盒子，但正在进行的上传、下载会中断。");
        }
        if (snapshot.Importing > 0)
            parts.Add($"正在添加的 {snapshot.Importing} 个安装包会取消。");
        if (parts.Count == 0)
            parts.Add("现在没有正在运行的盒子。");
        return string.Join("\n\n", parts);
    }

    private static string Them(IReadOnlyList<BoxEntry> boxes) => boxes.Count == 1 ? "它" : "它们";

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
