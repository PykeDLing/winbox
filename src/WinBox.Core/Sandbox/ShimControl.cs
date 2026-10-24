using System.Text.Json;
using WinBox.Core.IO;

namespace WinBox.Core.Sandbox;

/// <summary>
/// How WinBox and BoxHost reach BoxShim inside the sandbox: request files in the box's State\Control folder,
/// which both sides may write. Named events would not do, because the sandbox has a namespace of its own for
/// them. BoxShim leaves the outcome of the run there too.
/// </summary>
public static class ShimControl
{
    /// <summary>Start the app once more inside the sandbox, so that a single-instance app shows its window again.</summary>
    public const string ShowRequest = "show.request";

    /// <summary>Ask the app to end as at sign-out, end whatever is left of it, save the state and exit.</summary>
    public const string QuitRequest = "quit.request";

    public const string ResultFile = "result.json";

    public static void Request(string controlDirectory, string request)
    {
        Directory.CreateDirectory(controlDirectory);
        File.WriteAllText(Path.Combine(controlDirectory, request), DateTimeOffset.Now.ToString("O"));
    }

    /// <summary>Takes a pending request: true, with the request file gone, if there was one.</summary>
    public static bool Take(string controlDirectory, string request)
    {
        var path = Path.Combine(controlDirectory, request);
        if (!File.Exists(path))
            return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false; // Still being written; the next look takes it.
        }
    }

    /// <summary>Clears the requests and the result an earlier run left behind. Called before a box starts.</summary>
    public static void Reset(string controlDirectory)
    {
        if (!Directory.Exists(controlDirectory))
            return;
        foreach (var name in new[] { ShowRequest, QuitRequest, ResultFile })
            File.Delete(Path.Combine(controlDirectory, name));
    }

    public static void WriteResult(string controlDirectory, ShimResult result)
    {
        Directory.CreateDirectory(controlDirectory);
        JsonFile.Write(Path.Combine(controlDirectory, ResultFile), result);
    }

    public static ShimResult? ReadResult(string controlDirectory)
    {
        var path = Path.Combine(controlDirectory, ResultFile);
        try
        {
            return File.Exists(path) ? JsonFile.Read<ShimResult>(path) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>What BoxShim reports once it is done.</summary>
/// <param name="Saved">Whether the box's state was saved back into the box.</param>
/// <param name="Reason">What ended the run: the app exited, WinBox asked it to quit, or Windows ended the session.</param>
/// <param name="Error">Why the save failed, when it did.</param>
/// <param name="Terminated">The app did not end when asked and its processes were ended.</param>
public sealed record ShimResult(bool Saved, string Reason, string? Error = null, bool Terminated = false);
