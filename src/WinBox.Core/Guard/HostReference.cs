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
    File,
    ScheduledTask,
}

/// <summary>A place on the host, outside the boxes, that points at a box's files.</summary>
/// <param name="Path">Registry key (HKCU\...), file path, or task path.</param>
/// <param name="Name">Registry value name ("" for the default value); null for files and tasks.</param>
/// <param name="Detail">What it points at: the value data, or an excerpt of the file or task.</param>
public sealed record HostReference(HostReferenceKind Kind, HostLocationKind Location, string Path, string? Name, string Detail)
{
    public override string ToString() => Location switch
    {
        HostLocationKind.RegistryValue => $"{Path} :: {(Name is "" ? "(default)" : Name)} = {Detail}",
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
    /// <summary>Everything in the user's registry, shortcuts and scheduled tasks that points inside <paramref name="root"/>.</summary>
    IReadOnlyList<HostReference> Scan(string root);

    /// <summary>Removes the references, first saving what each one was into <paramref name="backupDirectory"/>.</summary>
    GuardCleanup Remove(IReadOnlyCollection<HostReference> references, string backupDirectory);
}
