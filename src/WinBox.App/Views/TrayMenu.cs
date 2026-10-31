using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBox.App.Library;
using static Microsoft.UI.Reactor.Factories;

namespace WinBox.App.Views;

/// <summary>What the items of the notification-area icon's menu do; the page wires them.</summary>
internal sealed record TrayActions(
    Action<BoxEntry> ShowBox,
    Action<BoxEntry> QuitBox,
    Action ShowWinBox,
    Action ChangeCloseAction,
    Action Quit);

/// <summary>
/// The notification-area icon's menu: each running box with Show window and Quit, then WinBox's own items.
/// Any process of the user's can fake a click on a tray icon, so the items that end something (quitting a box,
/// quitting WinBox) only bring up the main window and ask there.
/// </summary>
internal static class TrayMenu
{
    public static Element Build(LibrarySnapshot snapshot, CloseAction closeAction, TrayActions actions)
    {
        var running = snapshot.Running;
        Element boxes = running.Count == 0
            ? Caption("没有正在运行的盒子").Foreground(Theme.SecondaryText).Margin(12, 8, 12, 8)
            : ScrollView(VStack(4, [.. running.Select(entry => BoxItem(entry, actions).WithKey(entry.Id.ToString("N")))]));

        // Reactor hosts the menu in a window of a fixed size: the boxes take up what the items below leave.
        return Border((FlexColumn(
                    Caption("正在运行的盒子").Foreground(Theme.SecondaryText).Margin(12, 4, 12, 0),
                    boxes.Flex(grow: 1, basis: 0),
                    Divider(),
                    Item("显示 WinBox", actions.ShowWinBox),
                    Item(Texts.CloseActionItem(closeAction), actions.ChangeCloseAction),
                    Divider(),
                    Item("退出 WinBox…", actions.Quit))
                with { RowGap = 2 }).FlexPadding(4, 8, 4, 4))
            .Background(Theme.SolidBackground);
    }

    private static Element BoxItem(BoxEntry entry, TrayActions actions)
    {
        var name = entry.Box.Config.Name;
        return VStack(6,
                VStack(0,
                    BodyStrong(name).TextTrimming(TextTrimming.CharacterEllipsis),
                    Caption(Texts.Added(entry.Box.Config.CreatedAt)).Foreground(Theme.SecondaryText)),
                HStack(8,
                    Button("显示窗口", () => actions.ShowBox(entry)).IsEnabled(entry.CanShow).AutomationName($"显示{name}的窗口"),
                    Button("退出…", () => actions.QuitBox(entry)).AutomationName($"退出{name}")))
            .Padding(12, 6, 12, 6);
    }

    private static Element Item(string label, Action onClick) =>
        Button(label, onClick).SubtleButton()
            .HAlign(HorizontalAlignment.Stretch).HorizontalContentAlignment(HorizontalAlignment.Left)
            .AutomationName(label.TrimEnd('…'));

    private static Element Divider() => Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(8, 4, 8, 4);
}
