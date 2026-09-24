using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;

namespace Cap.Std;

/// <summary>
/// One entry of a directory, and the authority to open what it names.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It carries a name and never a path.</strong> There is no property that answers
/// "where is this?", and that is the whole design of the type rather than an omission.
/// Enumeration is the operation most likely to leak authority by accident, because the
/// obvious thing for it to return is a path — and a path is a string, which a caller will
/// sooner or later hand to something that resolves it with the process's own privileges.
/// Handing back a name together with the handle it belongs to removes the temptation: there
/// is nothing here to concatenate, and the only way to act on the entry is through the
/// capability it came from.
/// </para>
/// <para>
/// <strong>Opening re-resolves the name.</strong> An entry is what was there when the
/// directory was read, which may be some time ago and may already be something else — so the
/// members here look the name up again, through the same confined path as any other open,
/// rather than holding on to anything the read produced. That is slower than keeping a
/// handle from the enumeration and it is the only version that is correct: a handle kept
/// from the read would refer to whatever held the name then, whatever holds it now.
/// </para>
/// <para>
/// <strong><see cref="Type"/> is a snapshot and not a promise.</strong> Nothing here checks
/// it before opening. An entry that said it was a directory and has since been replaced by a
/// file fails at the open, with the same refusal as any other attempt to open a file as a
/// directory, which is the honest answer; a check against the remembered kind would report
/// the state of the world as it was.
/// </para>
/// <para>
/// A name read from a directory is not guaranteed to be one this library will open. Windows
/// stores names below the layer that strips trailing dots and spaces and reserves device
/// names, so an entry can be listed and still be refused as a path — which is the right way
/// round, because such a name cannot be opened safely by any spelling.
/// </para>
/// </remarks>
public readonly struct DirEntry
{
    private readonly Dir? _directory;
    private readonly string? _name;
    private readonly CapFileType _type;

    internal DirEntry(Dir directory, string name, CapFileType type)
    {
        _directory = directory;
        _name = name;
        _type = type;
    }

    /// <summary>
    /// The entry's name: a single component, as the filesystem stores it.
    /// </summary>
    /// <remarks>
    /// On Unix this may not be well-formed text. A filename there is a sequence of bytes
    /// under no obligation to be UTF-8, and one that is not is carried here by an escape that
    /// reverses exactly when the name is used again — so the name opens the file it came
    /// from, and a directory holding one undecodable name is still fully enumerable. The
    /// cost is that such a string must not be written anywhere that assumes well-formed
    /// text: it will not survive a round trip through most encoders.
    /// </remarks>
    public string Name => _name ?? string.Empty;

    /// <summary>
    /// What the entry is, as the directory read reported it.
    /// </summary>
    /// <remarks>
    /// An entry holding a symbolic link reports <see cref="CapFileType.Symlink"/> whatever
    /// the link leads to, a directory included. The alternative would mean following the
    /// link in order to answer, which is the decision the handle's own policy exists to
    /// make.
    /// </remarks>
    public CapFileType Type => _type;

    /// <summary>
    /// Opens the entry as a directory.
    /// </summary>
    /// <returns>A handle on it, carrying this entry's directory's own resolution policy.</returns>
    /// <remarks>
    /// <para>
    /// The name is resolved again rather than reused from the enumeration; see the notes on
    /// this type. Whether a symbolic link here is followed is decided by the policy the
    /// handle the entry came from carries, exactly as for a name a caller supplied.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The name is a single component, so the only link that
    /// can be met is the one holding it, and it is followed as <see cref="Dir.OpenDir"/>
    /// follows a last component: under
    /// <see cref="Cap.Primitives.SymlinkPolicy.FollowWithinSandbox"/> it opens the directory
    /// the link leads to while that stays beneath the handle, and is refused with
    /// <see cref="SandboxEscapeException"/> when it leaves; under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> it is refused with
    /// <see cref="CapIOException"/>. An entry whose <see cref="Type"/> says
    /// <see cref="CapFileType.Symlink"/> may therefore still open as a directory.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with anything else done through the handle
    /// the entry came from; the entry itself is an immutable value.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no enumeration.</exception>
    /// <exception cref="ArgumentException">The name is not one this platform will open.</exception>
    /// <exception cref="SandboxEscapeException">
    /// The name holds a symbolic link whose target leaves the handle's subtree.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">The entry is gone.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">
    /// The name holds something that is not a directory, a symbolic link the policy will not
    /// follow, or the open failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The directory it came from has been disposed.</exception>
    public Dir OpenDir() => Owner.OpenDir(Name);

    /// <summary>
    /// Opens the entry as a directory, reporting failure rather than throwing.
    /// </summary>
    /// <param name="dir">The open directory, when this returns true.</param>
    /// <returns>True when it was opened.</returns>
    /// <remarks>
    /// <para>
    /// The form to prefer here. An entry that has been removed between the read and the open
    /// is an ordinary outcome of listing a directory something else is writing to, and not a
    /// reason to build an exception.
    /// </para>
    /// <para>
    /// A symbolic link holding the name is followed or refused exactly as
    /// <see cref="OpenDir"/> describes, and a refusal, containment included, is reported as
    /// false.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with anything else done through the handle
    /// the entry came from; the entry itself is an immutable value.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no enumeration.</exception>
    /// <exception cref="ObjectDisposedException">The directory it came from has been disposed.</exception>
    public bool TryOpenDir([NotNullWhen(true)] out Dir? dir) => Owner.TryOpenDir(Name, out dir);

    /// <summary>
    /// Opens the entry as a file.
    /// </summary>
    /// <param name="mode">Whether the name may be created, and what happens to what is there.</param>
    /// <param name="access">What the handle may do with the contents.</param>
    /// <param name="share">What other openers may do while the handle is open.</param>
    /// <param name="options">Flags and hints for the open.</param>
    /// <param name="preallocationSize">How much room to claim in advance.</param>
    /// <param name="append">Whether every write goes to the end of the file.</param>
    /// <returns>The open file.</returns>
    /// <remarks>
    /// <para>
    /// The same arguments as opening a file by name through the handle this entry came from,
    /// and with the same defaults: an existing file, opened to read. The creating modes are
    /// accepted because the name is resolved afresh and may by now hold nothing.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The name is a single component, and a link holding it
    /// is treated as <see cref="Dir.OpenFile"/> treats a last component: under
    /// <see cref="Cap.Primitives.SymlinkPolicy.FollowWithinSandbox"/> it is followed while
    /// its target stays beneath the handle and refused with
    /// <see cref="SandboxEscapeException"/> when it leaves; under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> it is refused with
    /// <see cref="CapIOException"/>. <see cref="FileMode.CreateNew"/> never follows it and
    /// reports the name taken.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with anything else done through the handle
    /// the entry came from; the entry itself is an immutable value.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no enumeration.</exception>
    /// <exception cref="ArgumentException">The name is not one this platform will open.</exception>
    /// <exception cref="SandboxEscapeException">
    /// The name holds a symbolic link whose target leaves the handle's subtree.
    /// </exception>
    /// <exception cref="FileNotFoundException">The entry is gone, and the mode does not create.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">The name holds a directory, or the open failed otherwise.</exception>
    /// <exception cref="ObjectDisposedException">The directory it came from has been disposed.</exception>
    public CapFile OpenFile(
        FileMode mode = FileMode.Open,
        FileAccess access = FileAccess.Read,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0,
        bool append = false) =>
        Owner.OpenFile(Name, mode, access, share, options, preallocationSize, append);

    /// <summary>
    /// Opens the entry as a file to read, reporting failure rather than throwing.
    /// </summary>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when it was opened.</returns>
    /// <remarks>
    /// <para>
    /// A symbolic link holding the name is followed or refused exactly as
    /// <see cref="OpenFile"/> describes for an existing file, and a refusal, containment
    /// included, is reported as false.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with anything else done through the handle
    /// the entry came from; the entry itself is an immutable value.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no enumeration.</exception>
    /// <exception cref="ObjectDisposedException">The directory it came from has been disposed.</exception>
    public bool TryOpenFile([NotNullWhen(true)] out CapFile? file) => Owner.TryOpenFile(Name, out file);

    /// <summary>
    /// Opens the entry as a file as the arguments describe, reporting failure rather than
    /// throwing.
    /// </summary>
    /// <param name="mode">Whether the name may be created, and what happens to what is there.</param>
    /// <param name="access">What the handle may do with the contents.</param>
    /// <param name="share">What other openers may do while the handle is open.</param>
    /// <param name="options">Flags and hints for the open.</param>
    /// <param name="preallocationSize">How much room to claim in advance.</param>
    /// <param name="append">Whether every write goes to the end of the file.</param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when it was opened.</returns>
    /// <remarks>
    /// <para>
    /// A request that cannot mean anything still throws. This form is about a filesystem
    /// that said no; a mode combined with an access it contradicts is a mistake in the
    /// calling code, and reporting it as an ordinary failure would hide it behind whichever
    /// branch the caller wrote for a missing file.
    /// </para>
    /// <para>
    /// A symbolic link holding the name is followed or refused exactly as
    /// <see cref="OpenFile"/> describes for the same <paramref name="mode"/>, and a refusal,
    /// containment included, is reported as false.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with anything else done through the handle
    /// the entry came from; the entry itself is an immutable value.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no enumeration.</exception>
    /// <exception cref="ArgumentException">The request is not one that means anything.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is outside its defined values.</exception>
    /// <exception cref="ObjectDisposedException">The directory it came from has been disposed.</exception>
    public bool TryOpenFile(
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        [NotNullWhen(true)] out CapFile? file) =>
        Owner.TryOpenFile(Name, mode, access, share, options, preallocationSize, append, noFollow: false, out file);

    /// <summary>
    /// Describes what the entry's name holds now.
    /// </summary>
    /// <returns>A snapshot of the entry, taken at the moment of the call.</returns>
    /// <remarks>
    /// <para>
    /// Not free, and not part of the enumeration. A directory read answers what each entry is
    /// and nothing else, so everything beyond <see cref="Type"/> — the length, the times, the
    /// permissions — costs a separate lookup of the name. That is why it is a method rather
    /// than a property, and why an enumeration that does not call it pays nothing for it.
    /// </para>
    /// <para>
    /// The name is looked up again rather than answered from the read, so what comes back
    /// describes the entry as it is now and not as it was when the directory was listed. The
    /// two can disagree, including about what kind of thing the name holds; the snapshot is
    /// the more recent answer and <see cref="Type"/> is left as the enumeration reported it.
    /// </para>
    /// <para>
    /// A link is described as a link, not as what it leads to, exactly as it is when the same
    /// name is described through the handle this entry came from. That holds under either
    /// policy, and since the name is a single component no other link is ever met.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with anything else done through the handle
    /// the entry came from; the entry itself is an immutable value.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no enumeration.</exception>
    /// <exception cref="ArgumentException">The name is not one this platform will open.</exception>
    /// <exception cref="FileNotFoundException">The entry is gone.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the question.</exception>
    /// <exception cref="CapIOException">The question could not be answered.</exception>
    /// <exception cref="ObjectDisposedException">The directory it came from has been disposed.</exception>
    public CapMetadata GetMetadata() => Owner.GetMetadata(Name);

    /// <summary>
    /// Describes what the entry's name holds now, reporting failure rather than throwing.
    /// </summary>
    /// <param name="metadata">The snapshot, when this returns true.</param>
    /// <returns>True when the entry was described.</returns>
    /// <remarks>
    /// <para>
    /// The form to prefer here, for the reason the opening members give: an entry that has
    /// gone between the read and the lookup is an ordinary outcome of listing a directory
    /// something else is writing to.
    /// </para>
    /// <para>
    /// A symbolic link holding the name is described as itself and never followed, as
    /// <see cref="GetMetadata"/> describes.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with anything else done through the handle
    /// the entry came from; the entry itself is an immutable value.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no enumeration.</exception>
    /// <exception cref="ObjectDisposedException">The directory it came from has been disposed.</exception>
    public bool TryGetMetadata(out CapMetadata metadata) => Owner.TryGetMetadata(Name, out metadata);

    /// <summary>
    /// The handle this entry was read through, which is where its authority comes from.
    /// </summary>
    /// <remarks>
    /// A default-constructed entry has none, and there is nothing sensible for it to open.
    /// Refused as a misuse rather than reported as a missing file, because it is one: the
    /// value never came from a directory.
    /// </remarks>
    private Dir Owner =>
        _directory ??
        throw new InvalidOperationException(
            "This entry did not come from reading a directory, so there is no handle to " +
            "open it through. A default value of this type carries no authority and names " +
            "nothing.");
}
