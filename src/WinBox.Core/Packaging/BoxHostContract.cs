using System.Text.Json.Serialization;
using WinBox.Core.Boxes;

namespace WinBox.Core.Packaging;

/// <summary>
/// How WinBox and BoxHost talk. WinBox writes launch.json into the box's Host directory and activates the
/// package; BoxHost (running inside the package) starts the sandbox and keeps run.json up to date.
/// </summary>
public static class BoxHostContract
{
    public const string LaunchFile = "launch.json";
    public const string RunFile = "run.json";
    public const string LogFile = "boxhost.log";

    /// <summary>
    /// Named event WinBox sets to make BoxHost force-kill the sandboxed app. BoxHost holds it from before it
    /// reports Starting until it exits, so it also tells whether BoxHost is alive.
    /// </summary>
    public static string StopEventName(Guid boxId) => $@"Local\WinBox.Box.{boxId:N}.Stop";

    public static string LaunchPath(BoxPaths box) => Path.Combine(box.Host, LaunchFile);
    public static string RunPath(BoxPaths box) => Path.Combine(box.Host, RunFile);
}

/// <summary>Contents of launch.json: which box, and exactly how to sandbox it.</summary>
public sealed record BoxLaunchRequest(Guid BoxId, Sandbox.LaunchSpec Spec);

[JsonConverter(typeof(JsonStringEnumConverter<BoxRunState>))]
public enum BoxRunState
{
    Starting,
    Running,

    /// <summary>The app and BoxShim have exited, so the box's state is saved; MXC is still tearing down the sandbox.</summary>
    Closing,
    Exited,
    Failed,
}

/// <summary>Contents of run.json, written by BoxHost.</summary>
public sealed record BoxRunRecord
{
    public required BoxRunState State { get; init; }
    public required uint HostProcessId { get; init; }
    public uint? AppProcessId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int? ExitCode { get; init; }
    public bool? ForceStopped { get; init; }
    public ulong? DeniedAccesses { get; init; }
    public string? DenialsPath { get; init; }
    public string? Error { get; init; }
}
