using System.Diagnostics;
using System.Text;

namespace WinBox.Platform.Import;

/// <summary>The command-line tools WinBox unpacks installers with, and running them.</summary>
internal static class Tools
{
    /// <summary>
    /// Finds a tool: the environment variable naming it, then the copy shipped next to WinBox in tools\
    /// (<paramref name="bundled"/>), then PATH.
    /// </summary>
    public static string? Locate(string variable, string bundled, string executable)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var shipped = Path.Combine(AppContext.BaseDirectory, "tools", bundled);
        if (File.Exists(shipped))
            return shipped;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return pathDirs.Select(d => Path.Combine(d, executable)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Runs a tool to the end and returns its exit code and output. Cancelling ends the tool (and anything it
    /// started), which would otherwise go on writing into a staging folder that is about to be deleted.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(executable)} did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // It finished in the meantime.
            }
            throw;
        }
        return (process.ExitCode, (await stdout) + (await stderr));
    }

    /// <summary>The last lines of a tool's output, for an error message.</summary>
    public static string Tail(string output, int lines = 4) =>
        string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));
}
