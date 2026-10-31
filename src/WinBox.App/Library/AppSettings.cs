using System.Text.Json;
using System.Text.Json.Serialization;
using WinBox.Core.IO;

namespace WinBox.App.Library;

/// <summary>What closing the main window does while boxes run (with nothing going on, it always exits).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CloseAction>))]
public enum CloseAction
{
    Ask,

    /// <summary>WinBox hides in the notification area and the boxes keep running.</summary>
    Tray,

    /// <summary>WinBox quits every box and exits, once the user has confirmed which.</summary>
    Quit,
}

public sealed record AppSettings(CloseAction CloseAction = CloseAction.Ask);

/// <summary>The user's choices, in &lt;data&gt;\settings.json. A file that cannot be read counts as no choices made.</summary>
public sealed class SettingsStore(string path)
{
    private AppSettings? _current;

    public AppSettings Current => _current ??= Load();

    public void Save(AppSettings settings)
    {
        _current = settings;
        try
        {
            JsonFile.Write(path, settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session all the same; asked again next time.
            AppLog.Write($"saving the settings failed: {e.Message}");
        }
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonFile.Read<AppSettings>(path) : new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"reading the settings failed: {e.Message}");
            return new AppSettings();
        }
    }
}
