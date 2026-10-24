using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBox.App.Library;
using static Microsoft.UI.Reactor.Factories;

namespace WinBox.App.Views;

/// <summary>What the confirmation dialog is asking about.</summary>
internal sealed record Confirmation(ConfirmKind Kind, Guid BoxId, string BoxName, string Added);

internal enum ConfirmKind
{
    Quit,
    Remove,
}

/// <summary>The window's only page: the boxes, imports in flight, and anything that needs the user's attention.</summary>
internal sealed class LibraryPage : Component
{
    private static readonly string[] PickerTypes = [".exe", ".msi", ".msix", ".zip"];

    // Segoe Fluent Icons code points.
    private const string AddGlyph = "\uE710";
    private const string PlayGlyph = "\uE768";
    private const string StopGlyph = "\uE711";
    private const string WindowGlyph = "\uE8A7";
    private const string DeleteGlyph = "\uE74D";
    private const string PackageGlyph = "\uE7B8";
    private const string DropGlyph = "\uE896";
    private const string ErrorGlyph = "\uEA39";

    public override Element Render()
    {
        var library = Services.Library;
        var (snapshot, setSnapshot) = UseState(library.Snapshot());
        var (confirm, setConfirm) = UseState<Confirmation?>(null);
        var (lastConfirm, setLastConfirm) = UseState<Confirmation?>(null);
        var (notice, setNotice) = UseState<string?>(null);
        var (fixing, setFixing) = UseState(false);
        var (error, setError) = UseState<string?>(null);
        var window = UseWindow();

        UseEffect(() =>
        {
            void OnChanged() => setSnapshot(library.Snapshot());
            library.Changed += OnChanged;
            library.Start();
            OnChanged();
            return () => library.Changed -= OnChanged;
        });

        void Ask(ConfirmKind kind, BoxEntry entry)
        {
            var question = new Confirmation(kind, entry.Id, entry.Box.Config.Name, Texts.Added(entry.Box.Config.CreatedAt));
            setLastConfirm(question);
            setConfirm(question);
        }

        void Answer(ContentDialogResult result)
        {
            var question = confirm;
            setConfirm(null);
            if (question is null || result != ContentDialogResult.Primary)
                return;
            if (question.Kind == ConfirmKind.Quit)
                library.Stop(question.BoxId);
            else
                _ = Guarded(() => RemoveAsync(question));
        }

        async Task RemoveAsync(Confirmation question)
        {
            var outcome = await library.RemoveAsync(question.BoxId);
            // Not removed: the card stays and says why.
            if (!outcome.Removed)
                return;
            if (outcome.Leftovers.Count == 0)
                setNotice($"已删除“{question.BoxName}”：程序、数据、应用包和 Windows 记下的使用痕迹都已清除。");
            else
                setError($"已删除“{question.BoxName}”，但有 {outcome.Leftovers.Count} 处没能清理：{string.Join("；", outcome.Leftovers)}");
        }

        // Fire-and-forget work from a click: whatever goes wrong must show up, never vanish.
        async Task Guarded(Func<Task> work)
        {
            try { await work(); }
            catch (Exception e)
            {
                AppLog.Write($"failed: {e}");
                setError(Texts.Unexpected(e));
            }
        }

        async Task PickAsync()
        {
            AppLog.Write("opening the installer picker");
            // The Windows App SDK picker takes the window id directly; it works unpackaged and elevated.
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window!.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
                CommitButtonText = "添加",
            };
            foreach (var type in PickerTypes)
                picker.FileTypeFilter.Add(type);
            var files = await picker.PickMultipleFilesAsync();
            AppLog.Write($"picker returned {files?.Count ?? 0} files");
            if (files is { Count: > 0 })
                library.Import(files.Select(f => f.Path));
        }

        async Task FixLeaksAsync()
        {
            setFixing(true);
            var cleanup = await library.FixLeaksAsync();
            setFixing(false);
            setNotice(cleanup.Failures.Count == 0
                ? $"已清除 {cleanup.Removed} 处。原来的设置备份在 {cleanup.BackupDirectory}"
                : $"清除了 {cleanup.Removed} 处，{cleanup.Failures.Count} 处没能清除：{string.Join("；", cleanup.Failures)}");
        }

        var header = (FlexRow(
            VStack(4,
                Title("应用库"),
                Body("把安装包拖进这个窗口，软件会装进独立的盒子里运行；删除盒子时，不在电脑上留下痕迹。")
                    .Foreground(Theme.SecondaryText).TextWrapping()
            ).Flex(grow: 1, basis: 0),
            Button(HStack(8, Glyph(AddGlyph), TextBlock("添加安装包…")), () => _ = Guarded(PickAsync))
                .AccentButton().AutomationName("添加安装包")
        ) with { ColumnGap = 16, AlignItems = FlexAlign.Center });

        var banners = new List<Element?>
        {
            snapshot.Problem is { } problem
                ? InfoBar("WinBox 现在不能正常工作", problem).Error().IsClosable(false)
                : null,
            snapshot.Leaks.Count > 0
                ? InfoBar(
                        $"发现 {snapshot.Leaks.Count} 处本机设置指向盒子里的程序",
                        "它们会让盒子里的软件绕过沙盒运行，例如开机自启。正常使用不会产生这些设置。")
                    .Warning().IsClosable(false)
                    .Content(VStack(8,
                        Caption(string.Join("\n", snapshot.Leaks.Take(5).Select(Texts.Leak))
                            + (snapshot.Leaks.Count > 5 ? $"\n…还有 {snapshot.Leaks.Count - 5} 处" : ""))
                            .Foreground(Theme.SecondaryText).TextWrapping(),
                        Button(fixing ? "正在清除…" : "清除（先备份）", () => _ = Guarded(FixLeaksAsync))
                            .IsEnabled(!fixing).AutomationName("清除这些设置")
                    ).Margin(0, 0, 0, 12))
                : null,
            notice is not null
                ? InfoBar(null, notice).Success().IsClosable(true).Closed(() => setNotice(null))
                : null,
            error is not null
                ? InfoBar(null, error).Error().IsClosable(true).Closed(() => setError(null))
                : null,
        };
        banners.AddRange(snapshot.Imports.Select(job => ImportCard(job,
            dismiss: () => library.DismissImport(job.Key),
            cancel: () => library.CancelImport(job.Key)).WithKey(job.Key.ToString("N"))));
        banners.AddRange(snapshot.Broken.Select(b =>
            InfoBar("有一个盒子读不出来", $"{b.Paths.Root}（{b.Reason}）。WinBox 不会动它。").Warning().IsClosable(false)
                .WithKey(b.Paths.Root)));

        Element boxes = snapshot.Boxes.Count == 0 && snapshot.Imports.Count == 0
            ? EmptyState(() => _ = Guarded(PickAsync))
            : (FlexRow(snapshot.Boxes
                    .Select(entry => BoxCard(entry,
                        open: () => library.Open(entry.Id),
                        show: () => library.Show(entry.Id),
                        quit: () => Ask(ConfirmKind.Quit, entry),
                        remove: () => Ask(ConfirmKind.Remove, entry)).WithKey(entry.Id.ToString("N")))
                    .ToArray<Element>())
                with { Wrap = FlexWrap.Wrap, ColumnGap = 16, RowGap = 16 });

        var content = (FlexColumn([header, .. banners.Where(b => b is not null).Cast<Element>(), boxes])
            with { RowGap = 16 }).FlexPadding(32, 8, 32, 32);

        var dialog = ConfirmDialog(confirm ?? lastConfirm, isOpen: confirm is not null).Closed(Answer);

        return (FlexColumn(
                TitleBar("WinBox").Subtitle("预览版").Flex(shrink: 0),
                ScrollView(content).Flex(grow: 1, basis: 0),
                dialog)
            .Backdrop(BackdropKind.Mica)
            // Transparent is still hit-testable, so the whole window takes drops and Mica stays visible.
            .Background(new ThemeRef("SubtleFillColorTransparentBrush"))
            .OnDragOver(a =>
            {
                a.AcceptedOperation = DragOperations.Copy;
                a.UIOverride.Caption = "添加到 WinBox";
                a.UIOverride.IsCaptionVisible = true;
            })
            // Files dragged in from another process (Explorer) only arrive asynchronously: Reactor's synchronous
            // TryGetFiles and TryGetSafeLocalFiles never see them, so the drop used to vanish without a word.
            .OnDrop(a => _ = Guarded(async () =>
                library.Drop((await a.Data.GetFilesAsync(CancellationToken.None) ?? []).Select(i => i.Path))),
                DragOperations.Copy));
    }

    private static Element ImportCard(ImportJob job, Action dismiss, Action cancel)
    {
        var status = job.Failed
            ? VStack(2,
                BodyStrong($"没能添加 {job.FileName}"),
                Body(job.Error!).Foreground(Theme.SecondaryText).TextWrapping())
            : VStack(2,
                BodyStrong($"正在添加 {job.FileName}"),
                Caption(Texts.Step(job)).Foreground(Theme.SecondaryText));
        var leading = job.Failed
            ? Glyph(ErrorGlyph, 24, Theme.SystemCritical)
            : (Element)ProgressRing().Size(24, 24);
        return Card((FlexRow(
                leading,
                status.Flex(grow: 1, basis: 0),
                job.Failed
                    ? Button("关闭", dismiss).AutomationName($"关闭{job.FileName}的提示")
                    // Registering the package cannot be called back halfway; by then the box is all but done.
                    : Button("取消", cancel).AutomationName($"取消添加{job.FileName}").IsEnabled(job.Step != ImportStep.Registering))
            with { ColumnGap = 16, AlignItems = FlexAlign.Center }));
    }

    private static Element BoxCard(BoxEntry entry, Action open, Action show, Action quit, Action remove)
    {
        var config = entry.Box.Config;
        Element icon = entry.IconPath is { } path
            ? Image(new Uri(path).AbsoluteUri).Size(48, 48).AccessibilityHidden()
            : Glyph(PackageGlyph, 32, Theme.SecondaryText).Size(48, 48);

        var busy = entry.Activity is BoxActivity.Starting or BoxActivity.Stopping or BoxActivity.Closing or BoxActivity.Removing;
        Element light = busy
            ? ProgressRing().Size(16, 16)
            : Border(Empty()).Size(8, 8).CornerRadius(4)
                .Background(entry.Activity == BoxActivity.Running ? Theme.SystemSuccess : Theme.ControlStrokeSecondary);
        var status = (FlexRow(light, Caption(Texts.Activity(entry.Activity)).Foreground(Theme.SecondaryText))
            with { ColumnGap = 6, AlignItems = FlexAlign.Center });

        var details = string.Join(" · ", new[] { config.Version, config.Publisher }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var notes = config.Issues.Count == 0
            ? null
            : Caption($"ⓘ {config.Issues.Count} 条兼容性说明").Foreground(Theme.SecondaryText)
                .ToolTip(string.Join("\n", config.Issues.Select(i => "· " + Texts.Issue(i))));
        var problem = entry.Problem is null
            ? null
            : Caption(entry.Problem).Foreground(Theme.SystemCritical).TextWrapping();

        // The primary button keeps its place and its accent from Open to Show window, and Delete sits at the far
        // end: a quick second click on Open lands on Show window, never on Quit or Delete. Every child is keyed,
        // so a button is never reused for another one along with its styling.
        Element primary = entry.Activity == BoxActivity.Idle
            ? Button(HStack(8, Glyph(PlayGlyph), TextBlock("打开")), open).AccentButton().AutomationName($"打开{config.Name}").IsEnabled(entry.CanOpen)
            : Button(HStack(8, Glyph(WindowGlyph), TextBlock("显示窗口")), show).AccentButton().AutomationName($"显示{config.Name}的窗口").IsEnabled(entry.CanShow);
        var actions = (FlexRow(
                (FlexRow(
                        primary.WithKey("primary"),
                        entry.CanStop ? Button(HStack(8, Glyph(StopGlyph), TextBlock("退出")), quit).AutomationName($"退出{config.Name}").WithKey("quit") : null)
                    with { ColumnGap = 8 }).Flex(grow: 1, basis: 0).WithKey("main"),
                Button(Glyph(DeleteGlyph), remove).AutomationName($"删除{config.Name}").ToolTip("删除这个盒子").IsEnabled(entry.CanRemove).WithKey("delete"))
            with { ColumnGap = 8, AlignItems = FlexAlign.Center });

        return Card((FlexColumn(
                (FlexRow(
                    icon,
                    VStack(2,
                        BodyStrong(config.Name).TextTrimming(TextTrimming.CharacterEllipsis),
                        Caption(details).Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis),
                        Caption(Texts.Added(config.CreatedAt)).Foreground(Theme.SecondaryText)
                    ).Flex(grow: 1, basis: 0))
                    with { ColumnGap = 12, AlignItems = FlexAlign.Center }),
                status,
                notes,
                problem,
                actions)
            with { RowGap = 12 }))
            .Width(300);
    }

    private static Element EmptyState(Action pick) =>
        Border(VStack(12,
                Glyph(DropGlyph, 48, Theme.SecondaryText).HAlign(HorizontalAlignment.Center),
                Subtitle("把安装包拖到这里").HAlign(HorizontalAlignment.Center),
                Body("例如夸克网盘的安装包。WinBox 只取出程序，不运行安装程序。").Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center).TextWrapping(),
                Button("选择安装包…", pick).AutomationName("选择安装包").HAlign(HorizontalAlignment.Center))
            .Padding(48))
        .WithBorder(Theme.CardStroke, 1).CornerRadius(8).Background(Theme.CardBackground).Margin(0, 24, 0, 0);

    /// <summary>A Segoe Fluent Icons glyph. Reactor ignores Foreground on IconElement, but not on a TextBlock.</summary>
    private static TextBlockElement Glyph(string glyph, double size = 16, ThemeRef? color = null)
    {
        var text = TextBlock(glyph).FontFamily("Segoe Fluent Icons").FontSize(size).AccessibilityHidden();
        return color is { } c ? text.Foreground(c) : text;
    }

    private static ContentDialogElement ConfirmDialog(Confirmation? question, bool isOpen)
    {
        var (title, body, action) = question?.Kind switch
        {
            ConfirmKind.Remove => (
                $"删除“{question.BoxName}”（{question.Added}）？",
                "将删除这个软件的程序、登录状态和全部数据，以及它的应用包，并清除 Windows 记下的使用痕迹。\n\n你自己的文件（例如下载到“下载”文件夹的文件）不受影响。删除后无法恢复。",
                "删除"),
            _ => (
                $"退出“{question?.BoxName}”（{question?.Added}）？",
                "会先请它自己保存并退出，10 秒后还没退出的部分会被结束。它的数据都会保存回盒子，但还没完成的操作（例如正在进行的传输）会中断。",
                "退出"),
        };
        return ContentDialog(title, Body(body).TextWrapping(), action) with
        {
            IsOpen = isOpen,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
    }
}
