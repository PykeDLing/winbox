using System.Text.RegularExpressions;
using WinBox.Core.Import;

namespace WinBox.Core.Boxes;

/// <summary>How a version an installer brings compares with the one in the box.</summary>
public enum VersionChange
{
    /// <summary>One of the two versions is not known, or not a number WinBox can compare.</summary>
    Unknown,
    Newer,
    Same,
    Older,
}

/// <summary>
/// Updating a box: an installer's app takes the place of the box's app, and everything else stays: the app's data
/// and settings, its saved state, its package (and so the package's private storage), its name and its id.
/// </summary>
public static partial class BoxUpdate
{
    /// <summary>
    /// The installer brings the app <paramref name="box"/> holds: the same recipe, or a program of the same file name
    /// under the same name or publisher. What WinBox offers to update when an installer is dropped.
    /// </summary>
    public static bool IsSameApp(BoxConfig box, BoxConfig incoming)
    {
        if (box.Source.RecipeId is { } recipe && string.Equals(recipe, incoming.Source.RecipeId, StringComparison.OrdinalIgnoreCase))
            return true;
        return SameProgram(box, incoming) && (SameText(box.Name, incoming.Name) || SameText(box.Publisher, incoming.Publisher));
    }

    /// <summary>
    /// The installer may update <paramref name="box"/> when the user picks it for that box: the same app, or a program
    /// of the same file name, or an app of the same name. Anything else is another app, whose program would run on
    /// this one's data.
    /// </summary>
    public static bool MayUpdate(BoxConfig box, BoxConfig incoming) =>
        IsSameApp(box, incoming) || SameProgram(box, incoming) || SameText(box.Name, incoming.Name);

    /// <summary>
    /// The box's box.json once <paramref name="incoming"/>'s app is in it: what describes the app comes from the
    /// installer, what describes the box stays.
    /// </summary>
    public static BoxConfig Apply(BoxConfig box, BoxConfig incoming, DateTimeOffset now) => box with
    {
        Publisher = incoming.Publisher,
        Version = incoming.Version,
        Entry = incoming.Entry,
        LaunchArguments = incoming.LaunchArguments,
        Source = incoming.Source,
        Issues = incoming.Issues,
        UpdatedAt = now,
    };

    /// <summary>How <paramref name="incoming"/> compares with <paramref name="current"/>, by their leading numbers ("7.2.0.800").</summary>
    public static VersionChange Compare(string? current, string? incoming)
    {
        if (Numbers(current) is not { } from || Numbers(incoming) is not { } to)
            return VersionChange.Unknown;
        var order = from.CompareTo(to);
        return order < 0 ? VersionChange.Newer : order > 0 ? VersionChange.Older : VersionChange.Same;
    }

    private static Version? Numbers(string? version)
    {
        var match = version is null ? null : LeadingNumbers().Match(version);
        if (match is not { Success: true })
            return null;
        // Version needs two parts at least, and counts a missing part as lower than 0: 1.2 and 1.2.0 are the same here.
        var parts = match.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : -1).ToList();
        if (parts.Contains(-1))
            return null;
        while (parts.Count < 4)
            parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    [GeneratedRegex(@"^\s*\d+(\.\d+){0,3}")]
    private static partial Regex LeadingNumbers();

    private static bool SameProgram(BoxConfig box, BoxConfig incoming) =>
        string.Equals(Path.GetFileName(box.Entry), Path.GetFileName(incoming.Entry), StringComparison.OrdinalIgnoreCase);

    private static bool SameText(string? a, string? b) =>
        a is not null && b is not null && EntryResolver.Normalize(a) is { Length: > 0 } normalized && normalized == EntryResolver.Normalize(b);
}
