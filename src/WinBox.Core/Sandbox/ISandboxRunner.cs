namespace WinBox.Core.Sandbox;

/// <summary>Everything needed to start one sandboxed process. Built by Core from a box; executed by Platform.</summary>
public sealed record LaunchSpec
{
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> ReadOnlyPaths { get; init; } = [];
    public IReadOnlyList<string> ReadWritePaths { get; init; } = [];

    /// <summary>Paths the app may not touch even when they sit inside a granted folder.</summary>
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];

    /// <summary>Stable sandbox name; null lets MXC pick a new one per run.</summary>
    public string? ContainerName { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public bool AllowNetwork { get; init; } = true;

    /// <summary>Extra AppContainer capabilities (for example "smbios"). Network capabilities follow <see cref="AllowNetwork"/>.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Lets host processes connect to ports the sandboxed app listens on via 127.0.0.1 (diagnostics only).</summary>
    public bool AllowHostLoopbackIngress { get; init; }

    /// <summary>
    /// Diagnostics only: accesses that would be denied are allowed and recorded instead. The app can then
    /// write to the real system, so never use this for normal runs.
    /// </summary>
    public bool PermissiveCapture { get; init; }

    /// <summary>Where the sandbox writes the record of denied accesses; null disables capture.</summary>
    public string? DenialsOutputPath { get; init; }
}

/// <summary>The single entry point for starting sandboxed processes.</summary>
public interface ISandboxRunner
{
    /// <summary>Reports whether this machine can run the sandbox at all, with a reason when it cannot.</summary>
    SandboxSupport GetSupport();

    Task<ISandboxProcess> LaunchAsync(LaunchSpec spec, CancellationToken cancellationToken = default);
}

public sealed record SandboxSupport(bool IsSupported, string? Reason, IReadOnlyList<string> Capabilities);

public interface ISandboxProcess : IDisposable
{
    uint ProcessId { get; }
    IReadOnlyList<string> Warnings { get; }

    Task<SandboxExit> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>Forcibly ends the process. Only call after the user confirmed a force quit.</summary>
    void Kill();
}

public sealed record SandboxExit(int ExitCode, bool TimedOut, DenialSummary? Denials);

public sealed record DenialSummary(ulong Total, bool Truncated, string? OutputPath);
