namespace WinBox.Core.Sandbox;

/// <summary>
/// Command line of WinBox.BoxShim, the first process WinBox starts inside a box's sandbox:
/// <c>--state &lt;dir&gt; [--exclude &lt;name&gt;]... -- &lt;app exe&gt; [app args]...</c>
/// </summary>
public sealed record ShimArguments(string StateDirectory, IReadOnlyList<string> Exclusions, string Executable, IReadOnlyList<string> Arguments)
{
    public const string ExecutableName = "WinBox.BoxShim.exe";

    public IReadOnlyList<string> ToArgs()
    {
        List<string> args = ["--state", StateDirectory];
        foreach (var name in Exclusions)
            args.AddRange(["--exclude", name]);
        args.Add("--");
        args.Add(Executable);
        args.AddRange(Arguments);
        return args;
    }

    public static ShimArguments Parse(IReadOnlyList<string> args)
    {
        string? state = null;
        var exclusions = new List<string>();
        var i = 0;
        for (; i < args.Count && args[i] != "--"; i++)
        {
            switch (args[i])
            {
                case "--state" when i + 1 < args.Count: state = args[++i]; break;
                case "--exclude" when i + 1 < args.Count: exclusions.Add(args[++i]); break;
                default: throw new ArgumentException($"unexpected shim argument '{args[i]}'");
            }
        }
        if (state is null)
            throw new ArgumentException("--state is required");
        if (i + 1 >= args.Count)
            throw new ArgumentException("the app to start is missing after --");
        return new ShimArguments(state, exclusions, args[i + 1], args.Skip(i + 2).ToList());
    }
}
