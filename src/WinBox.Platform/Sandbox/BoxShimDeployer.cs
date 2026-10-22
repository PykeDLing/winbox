using WinBox.Core.Boxes;
using WinBox.Core.Sandbox;
using WinBox.Platform.IO;

namespace WinBox.Platform.Sandbox;

/// <summary>
/// Puts WinBox.BoxShim into a box before every run. It has to live inside the box: the sandbox can only
/// execute files from folders it was granted, and the box's private folders are denied to it.
/// </summary>
public sealed class BoxShimDeployer(string payloadDirectory)
{
    /// <summary>Where the BoxShim build output is expected next to WinBox: &lt;base&gt;\boxshim.</summary>
    public static string DefaultPayloadDirectory => Path.Combine(AppContext.BaseDirectory, "boxshim");

    public void Deploy(BoxPaths box)
    {
        if (!File.Exists(Path.Combine(payloadDirectory, ShimArguments.ExecutableName)))
            throw new InvalidOperationException($"BoxShim payload not found in {payloadDirectory}.");
        Payload.Sync(payloadDirectory, box.Shim);
        // Boxes imported before BoxShim existed have no State folder; the sandbox needs it to exist.
        Directory.CreateDirectory(box.LocalAppDataState);
    }
}
