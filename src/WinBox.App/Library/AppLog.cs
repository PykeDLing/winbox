namespace WinBox.App.Library;

/// <summary>What the UI did and what went wrong, in &lt;data&gt;\Logs\app.log; starts over past 1 MB.</summary>
internal static class AppLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var path = Path.Combine(Services.Host.Paths.Logs, "app.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the UI.
            }
        }
    }
}
