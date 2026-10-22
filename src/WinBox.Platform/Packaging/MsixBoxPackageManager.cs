using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.Win32;
using Windows.Management.Deployment;
using WinBox.Core.Boxes;
using WinBox.Core.Packaging;
using WinBox.Platform.IO;

namespace WinBox.Platform.Packaging;

/// <summary>
/// Gives each box its own MSIX package. Development builds register the package unsigned from a loose
/// layout in the box's Host directory, which Windows only allows with Developer Mode on.
/// </summary>
public sealed partial class MsixBoxPackageManager(string boxHostPayload) : IBoxPackageManager
{
    private readonly PackageManager _packages = new();

    /// <summary>Where the BoxHost build output is expected next to WinBox: &lt;base&gt;\boxhost.</summary>
    public static string DefaultPayloadDirectory => Path.Combine(AppContext.BaseDirectory, "boxhost");

    public string? GetUnavailableReason()
    {
        if (!File.Exists(Path.Combine(boxHostPayload, BoxPackageManifest.HostExecutable)))
            return $"BoxHost payload not found in {boxHostPayload}.";
        if (!IsDeveloperModeOn())
            return "Developer Mode is off; unsigned box packages cannot be registered (Settings > System > For developers).";
        return null;
    }

    public async Task<BoxPackage> RegisterAsync(LoadedBox box, CancellationToken cancellationToken = default)
    {
        if (GetUnavailableReason() is { } reason)
            throw new InvalidOperationException(reason);

        var host = box.Paths.Host;
        Directory.CreateDirectory(host);
        Payload.Sync(boxHostPayload, host);

        var name = box.Config.Package?.Name ?? BoxPackageManifest.NameFor(box.Config.Id);
        var manifestPath = Path.Combine(host, "AppxManifest.xml");
        File.WriteAllText(manifestPath, BoxPackageManifest.Build(name, box.Config.Name), new UTF8Encoding(false));

        var result = await _packages
            .RegisterPackageAsync(new Uri(manifestPath), null, DeploymentOptions.DevelopmentMode | DeploymentOptions.ForceTargetApplicationShutdown)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (result.ExtendedErrorCode is { } error && error.HResult != 0)
            throw new InvalidOperationException($"Registering {name} failed (0x{error.HResult:X8}): {result.ErrorText}", error);

        var registered = _packages.FindPackagesForUser(string.Empty)
            .FirstOrDefault(p => p.Id.Name == name && p.Id.Publisher == BoxPackageManifest.DevelopmentPublisher)
            ?? throw new InvalidOperationException($"{name} was registered but cannot be found.");
        return new BoxPackage(name, registered.Id.FamilyName);
    }

    public async Task UnregisterAsync(BoxPackage package, CancellationToken cancellationToken = default)
    {
        foreach (var installed in _packages.FindPackagesForUser(string.Empty, package.FamilyName).ToList())
        {
            var result = await _packages.RemovePackageAsync(installed.Id.FullName).AsTask(cancellationToken).ConfigureAwait(false);
            if (result.ExtendedErrorCode is { } error && error.HResult != 0)
                throw new InvalidOperationException($"Removing {installed.Id.FullName} failed (0x{error.HResult:X8}): {result.ErrorText}", error);
        }
        // Windows leaves the package's (empty) policy cache key behind after removing it.
        Registry.CurrentUser.DeleteSubKeyTree($@"{PolicyCacheKey}\{package.FamilyName}", throwOnMissingSubKey: false);
    }

    private const string PolicyCacheKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PolicyCache";

    public bool IsRegistered(BoxPackage package) =>
        _packages.FindPackagesForUser(string.Empty, package.FamilyName).Any();

    public void RefreshHost(LoadedBox box) => Payload.Sync(boxHostPayload, box.Paths.Host);

    public uint Activate(BoxPackage package)
    {
        Guid clsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        var iid = typeof(IApplicationActivationManager).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, ClsctxAll, iid, out var pointer));
        try
        {
            var manager = (IApplicationActivationManager)new StrategyBasedComWrappers()
                .GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(package.AppUserModelId, null, 0, out var processId));
            return processId;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static bool IsDeveloperModeOn()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
        return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int value && value != 0;
    }

    private const uint ClsctxAll = 0x17;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);
}

/// <summary>Shell activation of packaged apps by AppUserModelID (shobjidl_core.h). Only the first method is used.</summary>
[GeneratedComInterface]
[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
internal partial interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        int options,
        out uint processId);
}
