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

/// <summary>Source-generated JSON metadata for settings.json, with WinBox's file settings.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJson : JsonSerializerContext
{
    public static AppJson Files { get; } = new(JsonFile.NewOptions());
}

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
            JsonFile.Write(path, settings, AppJson.Files.AppSettings);
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
            return File.Exists(path) ? JsonFile.Read(path, AppJson.Files.AppSettings) : new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"reading the settings failed: {e.Message}");
            return new AppSettings();
        }
    }
}
