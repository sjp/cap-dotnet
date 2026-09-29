using Cap.Primitives;

namespace Cap.Std.Testing;

/// <summary>
/// How an <see cref="InMemoryFileSystem"/> behaves: which platform's path rules it applies,
/// whether it tells names apart by case, where its timestamps come from, and which of the
/// library's two resolution strategies runs beneath its handles.
/// </summary>
/// <remarks>
/// Read once, when the filesystem is created. Changing an instance afterwards changes nothing
/// about a filesystem already made from it.
/// </remarks>
public sealed class InMemoryFileSystemOptions
{
    /// <summary>
    /// The rules a path handed to one of the filesystem's handles is read under. Defaults to
    /// the running platform's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CapPathSyntax.Windows"/> makes the handles read paths as they would on
    /// Windows, on any machine: <c>\</c> separates components as well as <c>/</c>, and names
    /// Windows reserves or would silently change are refused. That is how code bound for
    /// Windows can be tested on a Linux build agent.
    /// </para>
    /// <para>
    /// The syntax also decides which permissions the filesystem records, because a real
    /// platform records one kind or the other: Unix mode bits under
    /// <see cref="CapPathSyntax.Unix"/>, Windows attributes under
    /// <see cref="CapPathSyntax.Windows"/>, where the read-only attribute also refuses the
    /// removal of a name and the opening of a file for writing.
    /// </para>
    /// <para>
    /// Under <see cref="CapPathSyntax.Windows"/> an open also honours the
    /// <see cref="FileShare"/> it was made with, as Windows does: a later open the files
    /// already open do not share, and the removal or renaming of a file held open without
    /// <see cref="FileShare.Delete"/>, are refused as sharing violations, reported as
    /// <see cref="UnauthorizedAccessException"/> as the Windows backend reports them. Under
    /// <see cref="CapPathSyntax.Unix"/> sharing is ignored, as Linux ignores it, and a file
    /// removed while open stays usable through its handles.
    /// </para>
    /// <para>
    /// The syntax also selects the few other answers that differ between the platforms:
    /// whether a rename may replace a directory, whether a directory's entries can be flushed,
    /// whether a time before 1601 can be stored, whether a hard link may be given to a link
    /// made as a link to a directory, and the code a write that fills the disk reports.
    /// Everything else answers as Linux does; see the remarks on
    /// <see cref="InMemoryFileSystem"/>.
    /// </para>
    /// </remarks>
    public CapPathSyntax PathSyntax { get; init; } = CapPath.HostSyntax;

    /// <summary>
    /// Whether two names that differ only in case are different names, or null to decide
    /// from <see cref="PathSyntax"/>: case-sensitive under Unix rules and not under Windows
    /// rules.
    /// </summary>
    /// <remarks>
    /// A filesystem that ignores case keeps the spelling a name was created with, finds it
    /// under any spelling, refuses to create a second name that differs from it only in case,
    /// and lets a rename change only its case, as NTFS and the default macOS filesystem do.
    /// Case is compared by ordinal, ignoring case, which agrees with those filesystems for
    /// every name a test is likely to use.
    /// </remarks>
    public bool? CaseSensitive { get; init; }

    /// <summary>
    /// Where the filesystem takes the time it stamps on a creation, a write or a change, or
    /// null for a clock stopped at midnight UTC on 1 January 2000.
    /// </summary>
    /// <remarks>
    /// Stopped by default, rather than the system clock, so that a test's timestamps are the
    /// same on every run unless it asks otherwise. A test that needs time to pass gives a
    /// provider it controls.
    /// </remarks>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// How paths beneath the filesystem's handles are resolved:
    /// <see cref="ResolutionBackend.PortableWalk"/>, one name at a time, or
    /// <see cref="ResolutionBackend.ConfinedOpen"/>, a whole path in one call as the Linux
    /// kernel resolves one. Defaults to the walk.
    /// </summary>
    /// <remarks>
    /// The two reach the same answers, and are both offered so that the same test can run
    /// down either path. The handles report <see cref="ResolutionBackend.InMemory"/> as their
    /// backend whichever is chosen. Any other value is refused when the filesystem is created.
    /// </remarks>
    public ResolutionBackend Resolution { get; init; } = ResolutionBackend.PortableWalk;
}
