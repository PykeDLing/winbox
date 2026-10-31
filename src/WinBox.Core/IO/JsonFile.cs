using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WinBox.Core.IO;

/// <summary>
/// Reads and writes WinBox's JSON files. Writes are atomic: a crash leaves either the old or the new file.
/// Every type comes with source-generated metadata (<see cref="WinBoxJson"/>), so nothing is reflected over
/// at run time and WinBox can be compiled ahead of time.
/// </summary>
public static class JsonFile
{
    /// <summary>The settings every file is written with. A context binds the options it gets, so each takes a new copy.</summary>
    public static JsonSerializerOptions NewOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep Chinese names and paths readable in box.json instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T Read<T>(string path, JsonTypeInfo<T> type)
    {
        // Shared for delete as well: another process replacing the file (BoxHost updating run.json while
        // WinBox polls it) must not fail because it is being read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize(stream, type)
            ?? throw new JsonException($"{path} contains null.");
    }

    public static void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, type);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
