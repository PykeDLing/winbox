using WinBox.Core.Boxes;

namespace WinBox.Core.Packaging;

/// <summary>Registers, removes and activates a box's MSIX package. Implemented by WinBox.Platform.</summary>
public interface IBoxPackageManager
{
    /// <summary>Why packages cannot be registered on this machine, or null when they can.</summary>
    string? GetUnavailableReason();

    /// <summary>
    /// Lays out the package in the box's Host directory (manifest, BoxHost) and registers it for the
    /// current user. Idempotent: an already registered package is re-registered from the same layout.
    /// </summary>
    Task<BoxPackage> RegisterAsync(LoadedBox box, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the package. Windows deletes its private storage (redirected AppData files and HKCU
    /// writes) with it. Succeeds when the package is already gone.
    /// </summary>
    Task UnregisterAsync(BoxPackage package, CancellationToken cancellationToken = default);

    bool IsRegistered(BoxPackage package);

    /// <summary>
    /// Family names of every box package registered for the current user, whichever WinBox (or data root)
    /// registered it. Records filed under any other box package name are leftovers of removed packages.
    /// </summary>
    IReadOnlySet<string> RegisteredBoxFamilies();

    /// <summary>Brings the BoxHost files in the box's Host directory up to date with this WinBox build.</summary>
    void RefreshHost(LoadedBox box);

    /// <summary>Starts BoxHost inside the package and returns its process id.</summary>
    uint Activate(BoxPackage package);
}
