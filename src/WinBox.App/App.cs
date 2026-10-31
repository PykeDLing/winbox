using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using WinBox.App;
using WinBox.App.Library;
using WinBox.App.Views;
using WinBox.Core;
using WinBox.Platform;

// WinBox: drop an installer in, the app runs in a box of its own (an MSIX package plus an MXC sandbox) and
// leaves nothing behind when the box is deleted. The window is the library; all work goes through
// LibraryService, which the CLI's commands mirror.
if (SingleInstance.HandOver(AppPaths.Default().Root))
    return;

ReactorApp.Run<LibraryPage>(new WindowSpec
{
    Title = "WinBox",
    Width = 1080,
    Height = 720,
    MinWidth = 720,
    MinHeight = 520,
    Backdrop = BackdropChoice.Of(BackdropKind.Mica),
    StartPosition = WindowStartPosition.CenterOnPrimary,
});

namespace WinBox.App
{
    /// <summary>One WinBoxHost and one library per process, shared by every component.</summary>
    internal static class Services
    {
        public static WinBoxHost Host { get; } = new();
        public static LibraryService Library { get; } = new(Host);
        public static SettingsStore Settings { get; } = new(Host.Paths.Settings);
    }
}
