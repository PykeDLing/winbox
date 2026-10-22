namespace WinBox.BoxShim;

/// <summary>Append-only log next to the box's state folder; starts over once it grows past 1 MB.</summary>
internal sealed class ShimLog(string path)
{
    private readonly Lock _lock = new();

    public void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never stop the app from starting or its state from being saved.
            }
        }
    }
}
