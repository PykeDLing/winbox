namespace WinBox.Platform.IO;

/// <summary>Build outputs WinBox copies into each box (BoxHost into Host, BoxShim into Shim).</summary>
internal static class Payload
{
    /// <summary>Copies <paramref name="source"/> into <paramref name="destination"/>, skipping files that are already up to date.</summary>
    public static void Sync(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            var from = new FileInfo(file);
            var to = new FileInfo(target);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            from.CopyTo(target, overwrite: true);
        }
    }
}
