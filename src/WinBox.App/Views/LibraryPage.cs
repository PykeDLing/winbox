using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinBox.App.Library;
using WinBox.Core.Sandbox;
using WinBox.Platform.Sandbox;
using static Microsoft.UI.Reactor.Factories;

namespace WinBox.App.Views;

/// <summary>What the dialog is asking about. Only the questions about one box name it.</summary>
internal sealed record Confirmation(ConfirmKind Kind, Guid BoxId = default, string BoxName = "", string Added = "");

internal enum ConfirmKind
{
    Quit,
    Remove,

    /// <summary>Quit every box and exit WinBox.</summary>
    QuitAll,

    /// <summary>The window was closed while boxes run, and no choice has been remembered: tray or quit?</summary>
    CloseChoice,

    /// <summary>Change what closing the window does (from the tray menu).</summary>
    CloseSetting,
}

/// <summary>
/// The window's only page: the boxes, imports in flight, and anything that needs the user's attention. It also
/// owns WinBox's life: closing the window while boxes run hides WinBox in the notification area (or quits
/// everything, as the user chose), since a boxed app cannot show a tray icon of its own and WinBox is then the
/// only way back to its window.
/// </summary>
internal sealed class LibraryPage : Component
{
    private static readonly string[] PickerTypes = [".exe", ".msi", ".msix", ".zip"];

    private static readonly WindowIcon TrayImage = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

    /// <summary>
    /// How long quitting waits for stops to reach boxes still starting, imports to clean up and removals to finish.
    /// A removal alone may wait 75 s for its box to stop.
    /// </summary>
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(90);

    // Segoe Fluent Icons code points.
    private const string AddGlyph = "\uE710";
    private const string PlayGlyph = "\uE768";
    private const string StopGlyph = "\uE711";
    private const string WindowGlyph = "\uE8A7";
    private const string DeleteGlyph = "\uE74D";
    private const string MoreGlyph = "\uE712";
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
        var (closeAction, setCloseAction) = UseState(Services.Settings.Current.CloseAction);
        var (closeChoice, setCloseChoice) = UseState(CloseAction.Tray);
        var (remember, setRemember) = UseState(false);
        // Hidden in the notification area, the window closed but WinBox still following its boxes.
        var (trayed, setTrayed) = UseState(false);
        var (quitting, setQuitting) = UseState(false);
        var allowClose = UseRef(false);
        // What needed the user's attention when WinBox went to the tray: they had seen it already.
        var seen = UseRef<IReadOnlySet<string>>(new HashSet<string>());
        // The tray icon and another start of WinBox call back into the latest render.
        var showLatest = UseRef<Action?>(null);
        var menuLatest = UseRef<Action?>(null);
        var window = UseWindow();

        UseEffect(() =>
        {
            void OnChanged() => setSnapshot(library.Snapshot());
            library.Changed += OnChanged;
            library.Start();
            OnChanged();
            return () => library.Changed -= OnChanged;
        });

        UseEffect(() =>
        {
            // Sign-out and shutdown must never meet a dialog (see the closing guard below).
            if (window is not null)
                SessionEnd.Follow(Win32Interop.GetWindowFromWindowId(window.AppWindow.Id));
            void OnShowRequested() => Later(() => showLatest.Current?.Invoke());
            SingleInstance.ShowRequested += OnShowRequested;
            return () => SingleInstance.ShowRequested -= OnShowRequested;
        });

        void Ask(ConfirmKind kind, BoxEntry entry) =>
            AskAbout(new Confirmation(kind, entry.Id, entry.Box.Config.Name, Texts.Added(entry.Box.Config.CreatedAt)));

        void AskAbout(Confirmation question)
        {
            setLastConfirm(question);
            setConfirm(question);
        }

        void Answer(ContentDialogResult result)
        {
            var question = confirm;
            setConfirm(null);
            if (question is null || result != ContentDialogResult.Primary)
                return;
            switch (question.Kind)
            {
                case ConfirmKind.Quit:
                    library.Stop(question.BoxId);
                    break;
                case ConfirmKind.Remove:
                    _ = Guarded(() => RemoveAsync(question));
                    break;
                case ConfirmKind.QuitAll:
                    _ = Guarded(QuitAllAsync);
                    break;
                case ConfirmKind.CloseChoice:
                    if (remember)
                        SaveCloseAction(closeChoice);
                    // The question named what quitting cuts short: answering it was the confirmation.
                    if (closeChoice == CloseAction.Quit)
                        _ = Guarded(QuitAllAsync);
                    else
                        HideToTray();
                    break;
                case ConfirmKind.CloseSetting:
                    SaveCloseAction(closeChoice);
                    break;
            }
        }

        void SaveCloseAction(CloseAction action)
        {
            Services.Settings.Save(Services.Settings.Current with { CloseAction = action });
            setCloseAction(action);
            AppLog.Write($"closing the window now does: {action}");
        }

        // Closing the window with nothing going on exits as ever. With boxes running it may not simply exit: the
        // boxes would run on with nothing to bring their windows back, report on their runs or quit them. The
        // guard is synchronous, so it cancels the close and asks, or hides, instead.
        UseClosingGuard(() =>
        {
            // Sign-out and shutdown: BoxHost saves every box by itself, and nothing may hold Windows up.
            if (allowClose.Current || SessionEnd.IsEnding)
                return true;
            if (!library.Snapshot().Unfinished)
                return true;
            // A dialog is open already: it is answered first.
            if (confirm is not null)
                return false;
            switch (closeAction)
            {
                case CloseAction.Tray:
                    // After the close has been called off, not from within it.
                    Later(HideToTray);
                    break;
                case CloseAction.Quit:
                    AskAbout(new Confirmation(ConfirmKind.QuitAll));
                    break;
                default:
                    setCloseChoice(CloseAction.Tray);
                    setRemember(false);
                    AskAbout(new Confirmation(ConfirmKind.CloseChoice));
                    break;
            }
            return false;
        });

        void HideToTray()
        {
            seen.Current = Reportable(library.Snapshot(), error);
            setTrayed(true);
            window!.Hide();
            AppLog.Write("hidden in the notification area");
        }

        void ShowWindow()
        {
            if (quitting)
                return;
            setTrayed(false);
            if (window!.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            window.Show();
            window.Activate();
        }

        void Exit()
        {
            allowClose.Current = true;
            // Never from within a render or a close.
            Later(() => ReactorApp.Exit());
        }

        // Quitting asks every box to quit, then waits only until each stop has reached its box: BoxHost then
        // quits the app and saves on its own. Imports are cancelled and clean up before WinBox goes.
        async Task QuitAllAsync()
        {
            setQuitting(true);
            window!.Hide();
            try
            {
                library.QuitAll();
                if (!await library.WaitForQuitAsync(QuitWait))
                    AppLog.Write("quitting: gave up waiting for boxes still starting, imports or removals");
            }
            finally
            {
                // The window is gone already: WinBox must not linger out of sight, whatever went wrong.
                AppLog.Write("exiting");
                Exit();
            }
        }

        // In the tray, once the last box has ended and its run been followed up, WinBox has nothing left to do:
        // it exits, unless something came up that the user has not seen, and then it shows the window instead.
        UseEffect(() =>
        {
            if (!trayed || quitting || !snapshot.Settled)
                return;
            if (Reportable(snapshot, error).Except(seen.Current).Any())
            {
                AppLog.Write("nothing left running, but something to report: showing the window");
                ShowWindow();
            }
            else
            {
                AppLog.Write("nothing left running and nothing to report: exiting");
                Exit();
            }
        }, snapshot, trayed, error);

        var tray = UseTrayIcon(new TrayIconSpec(TrayImage, Texts.TrayTooltip(snapshot), IsVisible: !quitting));
        UseEffect(() =>
        {
            if (tray is null)
                return () => { };
            void OnShow(object? sender, EventArgs e) => showLatest.Current?.Invoke();
            void OnMenu(object? sender, EventArgs e) => menuLatest.Current?.Invoke();
            tray.Click += OnShow;
            tray.DoubleClick += OnShow;
            tray.RightClick += OnMenu;
            return () =>
            {
                tray.Click -= OnShow;
                tray.DoubleClick -= OnShow;
                tray.RightClick -= OnMenu;
            };
        }, tray);

        // A tray click proves nothing about who clicked (any process of the user's can fake one): items that
        // end something only bring up the window and ask there.
        void FromTray(Action act)
        {
            tray?.HideFlyout();
            act();
        }

        // A question already open stays: it is answered first.
        void AskFromTray(Confirmation question, Action? prepare = null)
        {
            ShowWindow();
            if (confirm is not null)
                return;
            prepare?.Invoke();
            AskAbout(question);
        }

        showLatest.Current = ShowWindow;
        menuLatest.Current = () => tray?.ShowFlyout(TrayMenu.Build(library.Snapshot(), closeAction, new TrayActions(
            ShowBox: entry => FromTray(() => library.Show(entry.Id)),
            QuitBox: entry => FromTray(() => AskFromTray(
                new Confirmation(ConfirmKind.Quit, entry.Id, entry.Box.Config.Name, Texts.Added(entry.Box.Config.CreatedAt)))),
            ShowWinBox: () => FromTray(ShowWindow),
            ChangeCloseAction: () => FromTray(() =>
                AskFromTray(new Confirmation(ConfirmKind.CloseSetting), prepare: () => setCloseChoice(closeAction))),
            // With nothing going on, quitting ends nothing: no need to ask.
            Quit: () => FromTray(() =>
            {
                if (library.Snapshot().Unfinished)
                    AskFromTray(new Confirmation(ConfirmKind.QuitAll));
                else
                    Exit();
            }))));

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

        async Task OpenOutsideAsync(Task<ManualOpenRefusal?> opening, string path)
        {
            if (await opening is { } refusal)
                setError(Texts.ManualOpenRefused(refusal, path));
        }

        async Task PickOutsideAsync(BoxEntry entry)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window!.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Downloads,
                CommitButtonText = "打开",
            };
            picker.FileTypeFilter.Add("*");
            if (await picker.PickSingleFileAsync() is { } file)
                await OpenOutsideAsync(library.OpenFileOutsideAsync(entry.Id, file.Path), file.Path);
        }

        // The box's files and folders, opened by WinBox outside the box: a boxed app's own "open" cannot reach
        // Explorer or the programs of this computer.
        // An icon button rather than a DropDownButton: its text label and chevron left no room for Show window
        // and Quit on a running box's card. A button's flyout opens above it by default; below, as a
        // DropDownButton's does, it keeps the card's status in view.
        Element More(BoxEntry entry) => Button(Glyph(MoreGlyph))
            .WithFlyout(MenuItems(FlyoutPlacementMode.BottomEdgeAlignedLeft,
                MenuSubItem("打开授权文件夹", [.. library.HostPlaces.Select(place => MenuItem(place.Label,
                    () => _ = Guarded(() => OpenOutsideAsync(library.OpenFolderOutsideAsync(entry.Id, place.Path), place.Path))))]),
                MenuItem("在盒外打开文件…", () => _ = Guarded(() => PickOutsideAsync(entry)))))
            .AutomationName($"{entry.Box.Config.Name}的更多操作").ToolTip("在盒子外打开文件或文件夹");

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
                        remove: () => Ask(ConfirmKind.Remove, entry),
                        more: More(entry)).WithKey(entry.Id.ToString("N")))
                    .ToArray<Element>())
                with { Wrap = FlexWrap.Wrap, ColumnGap = 16, RowGap = 16 });

        var content = (FlexColumn([header, .. banners.Where(b => b is not null).Cast<Element>(), boxes])
            with { RowGap = 16 }).FlexPadding(32, 8, 32, 32);

        var asked = confirm ?? lastConfirm;
        var dialog = (asked?.Kind switch
        {
            ConfirmKind.Remove => ConfirmDialog(
                $"删除“{asked.BoxName}”（{asked.Added}）？",
                "将删除这个软件的程序、登录状态和全部数据，以及它的应用包，并清除 Windows 记下的使用痕迹。\n\n你自己的文件（例如下载到“下载”文件夹的文件）不受影响。删除后无法恢复。",
                "删除"),
            ConfirmKind.QuitAll => ConfirmDialog("退出 WinBox？", Texts.QuitAllEffects(snapshot), "退出"),
            ConfirmKind.CloseChoice => ChoiceDialog("关闭 WinBox 窗口时",
                VStack(12,
                    RadioButtons([Texts.CloseActionLabel(CloseAction.Tray), Texts.CloseActionLabel(CloseAction.Quit)],
                        closeChoice == CloseAction.Quit ? 1 : 0, i => setCloseChoice(i == 1 ? CloseAction.Quit : CloseAction.Tray)),
                    Caption(Texts.CloseChoiceNote(snapshot) ?? "").Foreground(Theme.SecondaryText).TextWrapping(),
                    CheckBox(remember, on => setRemember(on), "记住我的选择（可以在托盘图标的菜单里更改）")),
                "确定"),
            ConfirmKind.CloseSetting => ChoiceDialog("关闭 WinBox 窗口时",
                VStack(12,
                    RadioButtons([.. Enum.GetValues<CloseAction>().Select(Texts.CloseActionLabel)],
                        (int)closeChoice, i => setCloseChoice((CloseAction)i)),
                    Caption(Texts.CloseActionNote).Foreground(Theme.SecondaryText).TextWrapping()),
                "保存"),
            _ => ConfirmDialog(
                $"退出“{asked?.BoxName}”（{asked?.Added}）？",
                "会先请它自己保存并退出，10 秒后还没退出的部分会被结束。它的数据都会保存回盒子，但还没完成的操作（例如正在进行的传输）会中断。",
                "退出"),
        } with { IsOpen = confirm is not null }).Closed(Answer);

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

    private static Element BoxCard(BoxEntry entry, Action open, Action show, Action quit, Action remove, Element more)
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
                more.WithKey("more"),
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
            // Wide enough for a running box's four buttons (Show window, Quit, More, Delete) at full width.
            .Width(340);
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

    /// <summary>Runs on the UI thread once the current render, close or other callback is done.</summary>
    private static void Later(Action act) => ReactorApp.UIDispatcher!.TryEnqueue(() => act());

    /// <summary>Asks before something that cannot be undone. Enter cancels; the action is the button on the left.</summary>
    private static ContentDialogElement ConfirmDialog(string title, string body, string action) =>
        ContentDialog(title, Body(body).TextWrapping(), action) with
        {
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

    /// <summary>Asks for a choice; its content holds the options, already set to the safe one.</summary>
    private static ContentDialogElement ChoiceDialog(string title, Element content, string action) =>
        ContentDialog(title, content, action) with
        {
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

    /// <summary>
    /// What would need the user's attention: problems on cards, host settings pointing into the boxes, failed
    /// imports, an error banner. Compared before and after a stay in the tray, to tell what came up meanwhile.
    /// </summary>
    private static IReadOnlySet<string> Reportable(LibrarySnapshot snapshot, string? error) =>
        new HashSet<string>([
            .. snapshot.Boxes.Where(b => b.Problem is not null).Select(b => $"box {b.Id:N} {b.Problem}"),
            .. snapshot.Leaks.Select(l => $"leak {l.Location} {l.Path} {l.Name}"),
            .. snapshot.Imports.Where(j => j.Failed).Select(j => $"import {j.Key:N}"),
            .. error is null ? [] : new[] { $"error {error}" },
        ]);
}
