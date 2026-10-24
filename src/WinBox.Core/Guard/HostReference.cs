namespace WinBox.Core.Guard;

public enum HostReferenceKind
{
    /// <summary>
    /// A setting that can start a boxed program outside its sandbox (autostart, file association, protocol,
    /// COM server, shortcut, scheduled task...). Normal sandboxed runs never create these.
    /// </summary>
    Leak,

    /// <summary>Windows' own record that a program ran (display-name cache, tray icon settings...). Harmless, but removed with the box.</summary>
    Trace,
}

public enum HostLocationKind
{
    RegistryValue,

    /// <summary>A whole registry key, named after a box's package.</summary>
    RegistryKey,
    File,
    ScheduledTask,
}

/// <summary>A place on the host, outside the boxes, that points at a box's files or stands for a box's package.</summary>
/// <param name="Path">Registry key (HKCU\...), file path, or task path.</param>
/// <param name="Name">Registry value name ("" for the default value); null for keys, files and tasks.</param>
/// <param name="Detail">What it points at: the value data (or the decoded name), or an excerpt of the file or task.</param>
/// <param name="Package">The box package family the record is named after, when it is found by name rather than by path.</param>
public sealed record HostReference(HostReferenceKind Kind, HostLocationKind Location, string Path, string? Name, string Detail, string? Package = null)
{
    public override string ToString() => Location switch
    {
        HostLocationKind.RegistryValue => $"{Path} :: {(Name is "" ? "(default)" : Name)} = {Detail}",
        HostLocationKind.RegistryKey => $"{Path} (key of {Package})",
        _ => $"{Path} -> {Detail}",
    };
}

public sealed record GuardCleanup(int Removed, IReadOnlyList<string> Failures, string BackupDirectory);

/// <summary>
/// Finds and removes host settings that point into the boxes. A boxed app never gets to write them; if
/// one shows up anyway, something ran outside the sandbox and the user must hear about it.
/// </summary>
public interface IHostGuard
{
    /// <summary>
    /// Everything in the user's registry, shortcuts and scheduled tasks that points inside <paramref name="root"/>,
    /// plus, when <paramref name="includePackage"/> is given, the registry records Windows keeps under the name
    /// of a box package it accepts (they are traces, and outlive the package).
    /// </summary>
    IReadOnlyList<HostReference> Scan(string root, Func<string, bool>? includePackage = null);

    /// <summary>
    /// Removes the references that still point inside <paramref name="root"/> (or are still named after their
    /// package), first saving what each one was into <paramref name="backupDirectory"/>. A reference that
    /// changed since it was scanned is left alone.
    /// </summary>
    GuardCleanup Remove(IReadOnlyCollection<HostReference> references, string root, string backupDirectory);
}
