using WinBox.Core;
using WinBox.Core.Boxes;

namespace WinBox.Core.Tests;

/// <summary>A throwaway data root with helpers for building boxes.</summary>
internal sealed class TestBox : IDisposable
{
    public TestBox()
    {
        Root = Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Store = new BoxStore(Paths);
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public BoxStore Store { get; }

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
