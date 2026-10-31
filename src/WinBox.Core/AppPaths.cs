namespace WinBox.Core;

/// <summary>
/// Root of everything WinBox stores: the Data folder inside WinBox's install directory, so a box lives
/// and dies with the installation. The WINBOX_DATA_ROOT environment variable moves all of it elsewhere,
/// which development builds and tests use.
/// </summary>
/// <remarks>
/// This requires WinBox to be installed somewhere the user can write (a per-user install, not Program
/// Files) and to run unpackaged: inside an MSIX package context, files are virtualized for the launching
/// process and MXC cannot create the sandboxed process from them (CreateProcessW: ERROR_PATH_NOT_FOUND).
/// </remarks>
public sealed class AppPaths
{
    public const string DataRootVariable = "WINBOX_DATA_ROOT";

    public AppPaths(string root) => Root = root;

    public string Root { get; }
    public string Boxes => Path.Combine(Root, "Boxes");
    public string Downloads => Path.Combine(Root, "Downloads");
    public string Logs => Path.Combine(Root, "Logs");

    /// <summary>The user's choices in the app (what closing the window does).</summary>
    public string Settings => Path.Combine(Root, "settings.json");

    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataRootVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return new AppPaths(Path.GetFullPath(overridden));

        return new AppPaths(Path.Combine(AppContext.BaseDirectory, "Data"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Boxes);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Logs);
    }
}
