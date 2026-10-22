using Microsoft.Mxc.Sdk.V1;
using WinBox.Core.Sandbox;

namespace WinBox.Platform.Sandbox;

/// <summary>Runs boxes in an MXC ProcessContainer (AppContainer-based process isolation).</summary>
public sealed class MxcSandboxRunner : ISandboxRunner
{
    public SandboxSupport GetSupport()
    {
        var platform = MxcPlatform.GetPlatformSupport();
        var processContainer = MxcPlatform.GetAvailableBackends()
            .FirstOrDefault(b => b.Backend.ToString() == "ProcessContainer");
        if (!platform.IsSupported || processContainer is null)
            return new SandboxSupport(false, platform.Reason ?? "ProcessContainer backend is not available on this Windows build.", []);
        return new SandboxSupport(true, null, processContainer.Capabilities.Select(c => c.ToString()).ToList());
    }

    public async Task<ISandboxProcess> LaunchAsync(LaunchSpec spec, CancellationToken cancellationToken = default)
    {
        var containment = new Containment.ProcessContainer
        {
            Ui = new ProcessContainerUiPolicy
            {
                // Desktop isolation keeps the global atom table, which clipboard formats and
                // drag-and-drop between the app and Explorer depend on.
                Isolation = ProcessContainerUiIsolation.Desktop,
                Ime = true,
            },
        };
        containment.Capabilities.AddRange(spec.Capabilities);
        if (spec.DenialsOutputPath is not null)
            containment.CaptureDenials = new CaptureDenialsPolicy
            {
                Mode = spec.PermissiveCapture ? CaptureDenialsMode.Allow : CaptureDenialsMode.Block,
                OutputPath = spec.DenialsOutputPath,
            };

        var request = new ContainerRequest(CommandLine.Build(spec.Executable, spec.Arguments))
        {
            Containment = containment,
            Ui = new UiPolicy { Disable = false, Clipboard = ClipboardPolicy.All },
            Filesystem = new FilesystemPolicy(),
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = spec.AllowNetwork ? NetworkAction.Allow : NetworkAction.Deny },
            },
            WorkingDirectory = spec.WorkingDirectory,
            ContainerName = spec.ContainerName!,
            InheritDefaultEnvironment = true,
            Environment = new Dictionary<string, string>(spec.Environment),
        };
        if (spec.AllowHostLoopbackIngress)
            request.Network.Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Allow };
        request.Filesystem.ReadonlyPaths.AddRange(spec.ReadOnlyPaths);
        request.Filesystem.ReadwritePaths.AddRange(spec.ReadWritePaths);
        request.Filesystem.DeniedPaths.AddRange(spec.DeniedPaths.Where(Path.Exists));

        var process = await MxcContainer.SpawnAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new MxcSandboxProcess(process);
    }

    private sealed class MxcSandboxProcess(MxcProcess process) : ISandboxProcess
    {
        public uint ProcessId => process.Id;
        public IReadOnlyList<string> Warnings => process.Warnings;

        public async Task<SandboxExit> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            var result = await process.WaitAsync(cancellationToken).ConfigureAwait(false);
            var capture = process.OutputMetadata?.CaptureDenials;
            var denials = capture is null ? null : new DenialSummary(capture.TotalDenials, capture.DeniedResourcesTruncated, capture.OutputPath);
            return new SandboxExit(result.ExitCode, result.TimedOut, denials);
        }

        public void Kill() => process.Kill();

        public void Dispose() => process.Dispose();
    }
}
