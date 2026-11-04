using WinBox.Core;
using WinBox.Core.Boxes;

namespace WinBox.Core.Tests;

/// <summary>A throwaway data root with helpers for building boxes.</summary>
internal sealed class TestBox : IDisposable
{
    /// <param name="root">The data root; a new folder under the temp directory by default.</param>
    public TestBox(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Store = new BoxStore(Paths, freeSpace: dir => FreeSpace(dir));
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public BoxStore Store { get; }

    /// <summary>What the store takes for the free space on a folder's drive: plenty, unless a test says otherwise.</summary>
    public Func<string, long> FreeSpace { get; set; } = _ => 1L << 50;

    public static BoxConfig Config(Guid? id = null, string entry = "app.exe", BoxPackage? package = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "Test App",
        Entry = entry,
        Source = new BoxSource { FileName = "setup.exe", Kind = InstallerKind.InnoSetup },
        CreatedAt = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero),
        Package = package,
    };

    public LoadedBox Create(BoxConfig config)
    {
        using var staging = Store.BeginImport(config.Id);
        File.WriteAllText(Path.Combine(staging.Paths.App, config.Entry), "");
        return staging.Commit(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
