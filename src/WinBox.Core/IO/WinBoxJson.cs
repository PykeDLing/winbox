using System.Text.Json.Serialization;
using WinBox.Core.Boxes;
using WinBox.Core.Packaging;
using WinBox.Core.Sandbox;

namespace WinBox.Core.IO;

/// <summary>Source-generated JSON metadata for the files WinBox, BoxHost and BoxShim share.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BoxConfig))]
[JsonSerializable(typeof(BoxLaunchRequest))]
[JsonSerializable(typeof(BoxRunRecord))]
[JsonSerializable(typeof(ShimResult))]
public sealed partial class WinBoxJson : JsonSerializerContext
{
    /// <summary>The metadata bound to WinBox's file settings (<see cref="JsonFile.NewOptions"/>).</summary>
    public static WinBoxJson Files { get; } = new(JsonFile.NewOptions());
}
