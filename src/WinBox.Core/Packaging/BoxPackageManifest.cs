using System.Security;
using WinBox.Core.Boxes;

namespace WinBox.Core.Packaging;

/// <summary>
/// The AppxManifest.xml of a box's package: a full-trust package whose only content is BoxHost. Being a
/// full (not sparse) package is what makes Windows virtualize AppData and HKCU writes for everything
/// BoxHost starts.
/// </summary>
public static class BoxPackageManifest
{
    /// <summary>Publisher of development packages, registered unsigned from a loose layout in Developer Mode.</summary>
    public const string DevelopmentPublisher = "CN=WinBoxDev";

    public const string HostExecutable = "WinBox.BoxHost.exe";
    public const string LogoFile = "logo.png";

    /// <summary>Package identity name for a box: "WinBox.Box." plus the first 12 hex digits of its id.</summary>
    public static string NameFor(Guid boxId) => "WinBox.Box." + boxId.ToString("N")[..12];

    public static string Build(string identityName, string displayName, string publisher = DevelopmentPublisher, string version = "1.0.0.0")
    {
        var name = SecurityElement.Escape(identityName);
        var display = SecurityElement.Escape($"WinBox · {displayName}");
        var pub = SecurityElement.Escape(publisher);
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package
              xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              IgnorableNamespaces="uap rescap">
              <Identity Name="{name}" Publisher="{pub}" Version="{version}" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>{display}</DisplayName>
                <PublisherDisplayName>WinBox</PublisherDisplayName>
                <Logo>{LogoFile}</Logo>
              </Properties>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.26100.0" MaxVersionTested="10.0.26200.0" />
              </Dependencies>
              <Resources>
                <Resource Language="zh-cn" />
              </Resources>
              <Applications>
                <Application Id="{BoxPackage.ApplicationId}" Executable="{HostExecutable}" EntryPoint="Windows.FullTrustApplication">
                  <uap:VisualElements DisplayName="{display}" Description="{display}" BackgroundColor="transparent"
                    Square150x150Logo="{LogoFile}" Square44x44Logo="{LogoFile}" AppListEntry="none" />
                </Application>
              </Applications>
              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """;
    }
}
