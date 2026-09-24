using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std;

/// <summary>
/// What a filesystem object was, at the moment it was asked about.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A value, taken once.</strong> The framework's own <c>FileInfo</c> and
/// <c>DirectoryInfo</c> are objects that hold a path and look things up again as they
/// are read, which makes them convenient and makes two properties of one instance able to
/// describe two different moments — and, worse here, makes every read another resolution of
/// a path that something else may have reassigned in between. Everything in this type comes
/// from a single call against a single handle, so the fields agree with each other and
/// nothing is looked up a second time.
/// </para>
/// <para>
/// It is still a description of the past. Nothing keeps it true, and a caller that acts on
/// what it says — opening a file because this reported it as a file — is acting on a fact
/// that may have expired. The safe shape is to attempt the operation and handle its refusal;
/// this type is for reporting, for choosing between things that are all safe, and for
/// telling two names for one object apart.
/// </para>
/// <para>
/// <strong>It describes a name and never what a name points at.</strong> Asked about an
/// entry holding a symbolic link, it reports the link: <see cref="CapFileType.Symlink"/>, the
/// link's own length, the link's own times. That is the same rule every other operation
/// taking a name follows, and it is what makes the answer independent of the symbolic-link
/// policy the handle carries — a name would otherwise describe one thing through a permissive
/// handle and another through a strict one. A caller that wants the target described opens
/// the target and asks the handle.
/// </para>
/// <para>
/// A snapshot taken from an open handle, rather than of a name, describes the object the
/// handle refers to. A handle never refers to a link — any link was followed or refused when
/// it was opened — so such a snapshot never reports <see cref="CapFileType.Symlink"/>.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are immutable, so they are safe to share
/// between threads and to read from any number of them at once. Nothing here goes back to
/// the filesystem.
/// </para>
/// </remarks>
public readonly struct CapMetadata
{
    private readonly CapNodeStat _stat;

    internal CapMetadata(in CapNodeStat stat) => _stat = stat;

    /// <summary>
    /// What the object is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Told apart further than resolution tells things apart. A walk has one question about
    /// a socket, a pipe and a device node — can a path continue through it, and it cannot —
    /// so it groups them; a caller looking at what is in a directory is deciding what to do
    /// with each one, and the difference is the answer.
    /// </para>
    /// <para>
    /// <see cref="CapFileType.Symlink"/> when the name described holds a link, whatever the
    /// link points at and whether or not its target exists.
    /// </para>
    /// </remarks>
    public CapFileType Type => _stat.Type;

    /// <summary>
    /// The length the filesystem reports, in bytes.
    /// </summary>
    /// <remarks>
    /// Meaningful as a size for an ordinary file, and something else for everything else: a
    /// directory's length is whatever the filesystem spends on its entries, a symbolic link's
    /// is usually the length of its stored target, and a device node's means nothing at all.
    /// Reported as the platform gives it rather than blanked out for the other kinds, because
    /// a zero would be indistinguishable from a genuinely empty file.
    /// </remarks>
    public long Length => _stat.Length;

    /// <summary>When the object's contents were last changed.</summary>
    /// <remarks>
    /// Resolution differs by filesystem and is often coarser than the type it is reported in
    /// — a second on some, a nanosecond on others — so two writes close together can share a
    /// timestamp, and a value written and read back may not be the value written.
    /// </remarks>
    public DateTimeOffset LastWriteTime => _stat.LastWriteTime;

    /// <summary>
    /// When the object's contents were last read.
    /// </summary>
    /// <remarks>
    /// The least dependable field here. Filesystems are routinely mounted so that reads do
    /// not update it, because updating it turns every read into a write, so a value far in
    /// the past may mean the object has not been read or may mean nobody is recording.
    /// </remarks>
    public DateTimeOffset LastAccessTime => _stat.LastAccessTime;

    /// <summary>
    /// When the object was created, or null where nothing recorded it.
    /// </summary>
    /// <remarks>
    /// Null rather than a stand-in date, because a filesystem that does not keep a creation
    /// time is common rather than exotic and the alternative — reporting the start of some
    /// epoch — is a date that looks like an answer. A caller that needs one either way has
    /// to decide what to do about its absence, and this makes it decide.
    /// </remarks>
    public DateTimeOffset? CreationTime => _stat.CreationTime;

    /// <summary>
    /// When anything about the object last changed — its contents, its permissions, its
    /// names, its link count — or null where nothing recorded it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unix calls this the status-change time and Windows the change time, and on both it is
    /// kept by the filesystem rather than by whoever writes to the object: there is no way to
    /// set it, which is what makes it worth having. A file whose last-write time has been put
    /// back to an earlier date still shows here that something happened to it.
    /// </para>
    /// <para>
    /// Null for the reason <see cref="CreationTime"/> can be, and more rarely: every Unix
    /// filesystem keeps one, and on Windows the FAT family does not. It is not the creation
    /// time, which the Unix field's abbreviation is often taken to mean.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ChangeTime => _stat.ChangeTime;

    /// <summary>
    /// How many directory entries refer to the object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One for an ordinary file with a single name, more for a file that has hard links, and
    /// zero for one whose last name has been removed while a handle still holds it open. What
    /// it means for a directory differs by filesystem: on many Unix filesystems it counts the
    /// directory's own entry, its <c>.</c> and each subdirectory's <c>..</c>, while others
    /// and Windows report one.
    /// </para>
    /// <para>
    /// A count, not a list. Finding the other names means walking the directories that might
    /// hold them and comparing <see cref="FileId"/>; nothing here or on any platform this runs
    /// on maps an object back to its names.
    /// </para>
    /// </remarks>
    public long LinkCount => _stat.LinkCount;

    /// <summary>
    /// What the filesystem records about who may do what with the object.
    /// </summary>
    /// <remarks>
    /// Reached through a type that refuses to flatten the platforms together; see
    /// <see cref="CapPermissions"/> for why there is no portable accessor on it.
    /// </remarks>
    public CapPermissions Permissions => new(_stat.UnixMode, _stat.WindowsAttributes);

    /// <summary>
    /// What the filesystem calls the object, as distinct from what a directory calls it.
    /// </summary>
    /// <remarks>
    /// Equatable and hashable, so a walk can keep a set of what it has already met and
    /// notice a file reached under a second name. <see cref="IsSameFileAs"/> answers the same
    /// question for two snapshots in hand.
    /// </remarks>
    public CapFileId FileId => new(_stat.VolumeId, _stat.NodeId);

    /// <summary>
    /// The user id of the account that owns the object, or null on a platform that records
    /// ownership some other way.
    /// </summary>
    /// <remarks>
    /// Internal because the only thing that asks is this library's own check on a location
    /// it did not create and is about to trust. A public owner would need a portable shape
    /// for it, and a Windows owner is a security identifier, not a number.
    /// </remarks>
    internal uint? UnixOwnerId => _stat.UnixOwnerId;

    /// <summary>
    /// Whether this and <paramref name="other"/> describe the same filesystem object.
    /// </summary>
    /// <param name="other">The other snapshot.</param>
    /// <returns>True when both describe one object.</returns>
    /// <remarks>
    /// <para>
    /// This is how hard links are detected: two names in one subtree can lead to one object,
    /// and there is nothing about either name that says so. It is also how a caller checks
    /// that two handles it was given separately are not aimed at the same file.
    /// </para>
    /// <para>
    /// <strong>Nothing about the paths is compared.</strong> Two paths that spell the same
    /// thing may name different objects and two that spell different things may name one, so
    /// a comparison of strings answers a question nobody asked. What is compared is the
    /// filesystem's own identity for the object, which is stable across a rename and is the
    /// same value the kernel would compare.
    /// </para>
    /// <para>
    /// A filesystem may reuse an identifier once the object holding it is deleted, so two
    /// snapshots taken far enough apart can agree about objects that were never the same
    /// one. That is a limit of the mechanism rather than of this implementation, and it is
    /// why the answer is worth trusting for two snapshots taken in the course of one
    /// operation and not for two stored and compared later.
    /// </para>
    /// </remarks>
    public bool IsSameFileAs(in CapMetadata other) => FileId == other.FileId;
}
