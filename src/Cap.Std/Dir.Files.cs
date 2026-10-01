using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Microsoft.Win32.SafeHandles;

namespace Cap.Std;

/// <summary>
/// The file-opening half of the directory capability.
/// </summary>
/// <remarks>
/// <para>
/// Opening a file is the same resolution as opening a directory, and everything said about
/// containment on <see cref="Dir"/> applies here unchanged. What is different is that the
/// last component can be brought into existence, which means the open has to say what should
/// happen when the name is free and when it is taken. Those are the questions
/// <c>System.IO</c> already answers with <see cref="FileMode"/>, <see cref="FileAccess"/>,
/// <see cref="FileShare"/> and <see cref="FileOptions"/>, so they are the ones asked here:
/// the signature is the framework's own file open with the path replaced by a name and a
/// capability, which is the whole idea of this library in one line.
/// </para>
/// <para>
/// Creation happens at the last component and nowhere else. A directory missing from the
/// middle of a path is reported as missing rather than created. A caller who wants the chain
/// made asks for it with <see cref="OpenOrCreateDirAll"/> first, and so chooses to have it.
/// </para>
/// </remarks>
public sealed partial class Dir
{
    /// <summary>
    /// The options a whole-file read asks for.
    /// </summary>
    /// <remarks>
    /// The file is read once, from start to finish, and then closed; saying so lets the
    /// system read ahead and lets it drop what it has read rather than keeping it in case
    /// somebody comes back. Where the hint means nothing it is ignored, which costs nothing.
    /// </remarks>
    private const FileOptions SequentialRead = FileOptions.SequentialScan;

    /// <summary>
    /// Opens a file beneath this handle.
    /// </summary>
    /// <param name="path">
    /// A relative path of one or more components. Absolute paths and paths naming a drive or
    /// a network location are refused: none of them names something this handle covers. A
    /// <c>..</c> component is resolved beneath this handle and refused if it would climb above
    /// it, as <see cref="OpenDir"/> describes. A path spelled so that its target must be a
    /// directory — one ending in a separator or in <c>..</c> — is refused too, because no file
    /// can satisfy it: by what the name holds when the mode only opens (missing, not a
    /// directory, or a directory), and as naming a directory when the mode may create, as
    /// <c>open(2)</c> refuses them.
    /// </param>
    /// <param name="mode">Whether the name may be created, and what happens to what is there.</param>
    /// <param name="access">What the handle may do with the contents.</param>
    /// <param name="share">
    /// What other openers may do while the handle is open. Enforced only where the platform
    /// enforces sharing at all, which is Windows; it is not a containment control anywhere,
    /// since a share mode denied to somebody else says nothing about what this handle covers.
    /// </param>
    /// <param name="options">
    /// Flags and hints for the open. Asking for a file to be removed when its last handle
    /// closes, or to be stored encrypted, is refused where the platform cannot do it rather
    /// than quietly dropped. The two access hints are dropped where they mean nothing, which
    /// is what a hint is for.
    /// </param>
    /// <param name="preallocationSize">
    /// How much room to claim in advance, for an open that creates the file or empties it.
    /// Zero claims none. The file's length is not changed by the claim — what comes back is
    /// an empty file with room behind it, not one that already reports that many bytes. A
    /// claim that cannot be met for want of space fails the open, which is the point of
    /// making it: a caller who reserves does so precisely so that a later write will not
    /// fail for that reason.
    /// </param>
    /// <param name="append">
    /// Whether every write through the handle goes to the end of the file, whatever offset it
    /// names. It combines with any <paramref name="mode"/> and with reading, as appending
    /// does in POSIX: a file can be created or emptied and then appended to, or read anywhere
    /// and appended to, through one handle. It needs an <paramref name="access"/> that can
    /// write. <see cref="FileMode.Append"/> keeps the meaning it has in <c>System.IO</c>,
    /// which is <see cref="FileMode.OpenOrCreate"/> with this set and writing only. Appending
    /// can be changed later through <see cref="CapFile.IsAppending"/>.
    /// </param>
    /// <param name="noFollow">
    /// Whether a symbolic link at the last component is refused rather than followed, as
    /// <c>O_NOFOLLOW</c> asks of a POSIX open. It changes only what <see cref="FileMode.Open"/>
    /// does, since every other mode refuses such a link already, and it changes nothing about
    /// links before the last component or about this handle's policy.
    /// </param>
    /// <returns>An open file, owning its handle.</returns>
    /// <remarks>
    /// <para>
    /// Resolution is confined to the subtree this handle was opened on, by whichever backend
    /// the platform provides, and a symbolic link met on the way is followed only if this
    /// handle's policy allows it and only while it stays inside.
    /// </para>
    /// <para>
    /// <strong>Symbolic links, precisely.</strong> Under
    /// <see cref="Cap.Primitives.SymlinkPolicy.FollowWithinSandbox"/> a link before the last
    /// component is followed while its target stays beneath this handle, and one whose target
    /// leaves — an absolute target, or one that climbs above this directory — is refused with
    /// <see cref="SandboxEscapeException"/>. Under <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>
    /// every link is refused with <see cref="CapIOException"/>, wherever it points.
    /// </para>
    /// <para>
    /// A link <em>at</em> the last component depends on the mode. <see cref="FileMode.Open"/>
    /// follows it under the rules above, because opening a link is opening its target, unless
    /// <paramref name="noFollow"/> is set, in which case it is refused with
    /// <see cref="CapIOException"/> under either policy, wherever it points. A path ending in
    /// a separator is the exception: it asks for what a final link leads to, and follows it
    /// even then, as POSIX resolution does, to be refused because a file open cannot name a
    /// directory. Every
    /// other mode — <see cref="FileMode.Create"/>, <see cref="FileMode.CreateNew"/>,
    /// <see cref="FileMode.Truncate"/>, <see cref="FileMode.OpenOrCreate"/> and
    /// <see cref="FileMode.Append"/> — refuses it with <see cref="CapIOException"/> under
    /// either policy, whatever it points at, whether it leads to a file or a directory and
    /// whether or not it dangles, and leaves both the link and its target as they were. This
    /// is where the library parts from <c>System.IO</c>, which follows the link. A write
    /// usually names its file by a name somebody else chose, and in a directory untrusted code
    /// can write into, following a link planted under that name would empty or create a
    /// different file from the one named. Remove the link first to write at its name.
    /// </para>
    /// <para>
    /// Nothing about the file's permissions is decided here. A created file is asked for with
    /// the permissions every other program asks for, which the system then narrows as it is
    /// configured to; a capability bounds what can be reached and is not a substitute for the
    /// filesystem's own access control.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable name, or the requested combination of mode,
    /// access, sharing and appending is not one that means anything.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preallocationSize"/> is negative, or an enumeration argument is not one
    /// of its defined values.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file, and the mode does not create one.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">
    /// The name is taken and the mode refuses to take it, the name holds a directory or is
    /// spelled as one, the path passes through something that is not a directory, the
    /// name holds a symbolic link and the mode may create or empty the file or
    /// <paramref name="noFollow"/> is set, a symbolic link
    /// the policy will not follow is in the way, or the platform cannot honour part of the
    /// request.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapFile OpenFile(
        string path,
        FileMode mode = FileMode.Open,
        FileAccess access = FileAccess.Read,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0,
        bool append = false,
        bool noFollow = false)
    {
        FileOpenRequest request = Demand(mode, access, share, options, preallocationSize, append, noFollow);

        CapPathError pathError = OpenFileCore(path, in request, out CapFile? file, out CapError error);
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, nameof(path));
        }

        if (error.IsSuccess)
        {
            return file!;
        }

        // A mode that creates cannot fail for want of the file it was going to make, so a
        // report of something missing can only be about a directory above it. A mode that
        // cannot create has no such certainty, and asks which it was. This is the whole of the
        // distinction the framework draws between its two missing-thing exceptions, and
        // drawing it the same way keeps a ported catch clause matching.
        ExpectedTarget expected = request.Creates ? ExpectedTarget.Parent : ClassifyMissing(path, error);
        throw FailureTranslation.ToException(error, path, expected);
    }

    /// <summary>
    /// Opens a file beneath this handle, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenFile"/>.</param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the file was opened.</returns>
    /// <remarks>
    /// <para>
    /// False covers every reason it was not opened, a missing file and a containment refusal
    /// alike. An application that audits escape attempts calls <see cref="OpenFile"/> and
    /// catches <see cref="SandboxEscapeException"/>; this form deliberately reports no
    /// reason, so that the failure path builds no message and no exception.
    /// </para>
    /// <para>
    /// Opens an existing file to read, so symbolic links, the last component included, are
    /// followed or refused under this handle's policy exactly as <see cref="OpenFile"/>
    /// describes; a refusal is reported as false. Safe to call concurrently with any other
    /// member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenFile(string path, [NotNullWhen(true)] out CapFile? file) =>
        TryOpenFile(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0,
            append: false, noFollow: false, out file);

    /// <summary>
    /// Opens a file beneath this handle as the arguments describe, reporting failure rather
    /// than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenFile"/>.</param>
    /// <param name="mode">Whether the name may be created, and what happens to what is there.</param>
    /// <param name="access">What the handle may do with the contents.</param>
    /// <param name="share">What other openers may do while the handle is open.</param>
    /// <param name="options">Flags and hints for the open.</param>
    /// <param name="preallocationSize">How much room to claim in advance.</param>
    /// <param name="append">Whether every write goes to the end of the file. See <see cref="OpenFile"/>.</param>
    /// <param name="noFollow">
    /// Whether a symbolic link at the last component is refused. See <see cref="OpenFile"/>.
    /// </param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the file was opened.</returns>
    /// <remarks>
    /// <para>
    /// A request that cannot mean anything still throws, as it does from <see cref="OpenFile"/>.
    /// This form is about a filesystem that said no, which is an outcome; a mode combined with
    /// an access it contradicts is a mistake in the calling code, and reporting it as an
    /// ordinary failure would hide it behind whichever branch the caller wrote for a missing
    /// file.
    /// </para>
    /// <para>
    /// Symbolic links are followed or refused exactly as <see cref="OpenFile"/> describes for
    /// the same <paramref name="mode"/> and <paramref name="noFollow"/>; a refusal is reported
    /// as false. Safe to call
    /// concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">The request is not one that means anything.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is outside its defined values.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        bool noFollow,
        [NotNullWhen(true)] out CapFile? file)
    {
        FileOpenRequest request = Demand(mode, access, share, options, preallocationSize, append, noFollow);
        return Succeeded(OpenFileCore(path, in request, out file, out CapError error), error);
    }

    /// <summary>
    /// Opens whatever a name beneath this handle holds, a directory or a file, to read, and
    /// says which it was.
    /// </summary>
    /// <param name="path">
    /// A relative path of one or more components, refused and resolved as for
    /// <see cref="OpenDir"/>. A path spelled so that its target must be a directory — one
    /// ending in a separator or in <c>..</c> — opens only a directory.
    /// </param>
    /// <param name="share">
    /// What other openers may do while the handle is open, if what is found is a file. See
    /// <see cref="OpenFile"/>. A directory is shared as every directory handle is.
    /// </param>
    /// <param name="options">
    /// Flags and hints for the open, if what is found is a file, with the meanings they have
    /// for <see cref="OpenFile"/>. A directory is opened as <see cref="OpenDir"/> opens one,
    /// whatever is asked here.
    /// </param>
    /// <param name="noFollow">
    /// Whether a symbolic link at the last component is refused rather than followed, as
    /// <c>O_NOFOLLOW</c> asks of a POSIX open, with the meaning it has for
    /// <see cref="OpenDir"/> and <see cref="OpenFile"/>.
    /// </param>
    /// <returns>
    /// What was opened, holding either a directory carrying this handle's policy or a file open
    /// to read. Dispose it, or take the handle out of it and dispose that.
    /// </returns>
    /// <remarks>
    /// <para>
    /// For a caller that does not know which kind a name holds, as <c>open(2)</c> without
    /// <c>O_DIRECTORY</c> does not. Trying <see cref="OpenFile"/> and then
    /// <see cref="OpenDir"/> would resolve the path twice, and a rename between the two could
    /// make the answer describe a different object from the one the first attempt found.
    /// This resolves it once, and reports the kind of the object it opened. On Windows, where
    /// a directory and a file cannot be opened with the same options, the object is opened a
    /// second time through the first handle, which reaches the same object and names nothing.
    /// </para>
    /// <para>
    /// Only reads. A directory cannot be written, created or emptied by an open, so an open
    /// that could do any of those is one of a file, and <see cref="OpenFile"/> is the method
    /// for it.
    /// </para>
    /// <para>
    /// Resolution, <c>..</c> and symbolic links, the last component included, are handled
    /// exactly as <see cref="OpenDir"/> and <see cref="OpenFile"/> with
    /// <see cref="FileMode.Open"/> handle them: a link is followed only under
    /// <see cref="Cap.Primitives.SymlinkPolicy.FollowWithinSandbox"/> and only while its
    /// target stays beneath this handle, one that leaves is refused with
    /// <see cref="SandboxEscapeException"/>, and under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> every link is refused with
    /// <see cref="CapIOException"/>. The kind reported is that of what a followed link leads
    /// to.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable name, or <paramref name="share"/> asks for an
    /// inheritable handle.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="share"/> or <paramref name="options"/> holds a value that is not defined.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is nothing at the name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the name is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">
    /// A symbolic link the policy will not follow is in the way, the name holds a link and
    /// <paramref name="noFollow"/> is set, a component before the last is not a directory, the
    /// path is spelled as a directory and names something else, or the platform cannot honour
    /// part of the request.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapOpened OpenAny(
        string path,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        bool noFollow = false)
    {
        FileOpenRequest request = Demand(FileMode.Open, FileAccess.Read, share, options, 0, append: false, noFollow);

        CapPathError pathError = OpenAnyCore(path, in request, out CapOpened? opened, out CapError error);
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, nameof(path));
        }

        return error.IsSuccess
            ? opened!
            : throw FailureTranslation.ToException(error, path, ClassifyMissing(path, error));
    }

    /// <summary>
    /// Opens whatever a name beneath this handle holds, reporting failure rather than
    /// throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenAny"/>.</param>
    /// <param name="opened">What was opened, when this returns true.</param>
    /// <returns>True when something was opened.</returns>
    /// <remarks>
    /// <para>
    /// False covers every reason nothing was opened, a missing name and a containment refusal
    /// alike. An application that audits escape attempts calls <see cref="OpenAny"/> and
    /// catches <see cref="SandboxEscapeException"/>; this form deliberately reports no reason.
    /// </para>
    /// <para>
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="OpenAny"/> describes; a refusal is reported as false. Safe to call
    /// concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenAny(string path, [NotNullWhen(true)] out CapOpened? opened) =>
        TryOpenAny(path, noFollow: false, out opened);

    /// <summary>
    /// Opens whatever a name beneath this handle holds, choosing whether a final symbolic
    /// link is followed, and reports failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenAny"/>.</param>
    /// <param name="noFollow">
    /// Whether a symbolic link at the last component is refused. See <see cref="OpenAny"/>.
    /// </param>
    /// <param name="opened">What was opened, when this returns true.</param>
    /// <returns>True when something was opened.</returns>
    /// <remarks>
    /// Symbolic links are followed or refused exactly as <see cref="OpenAny"/> describes for
    /// the same <paramref name="noFollow"/>, and every refusal, containment included, is
    /// reported as false. Safe to call concurrently with any other member of this handle, from
    /// any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenAny(string path, bool noFollow, [NotNullWhen(true)] out CapOpened? opened)
    {
        FileOpenRequest request = Demand(
            FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0, append: false, noFollow);
        return Succeeded(OpenAnyCore(path, in request, out opened, out CapError error), error);
    }

    /// <summary>
    /// Creates a file beneath this handle, emptying it if the name is already taken.
    /// </summary>
    /// <param name="path">A relative path. Every component but the last must already exist.</param>
    /// <returns>An open file, ready to be written from the beginning.</returns>
    /// <remarks>
    /// <para>
    /// The shorthand for the most common write: the caller has something to store and does
    /// not care whether a file of that name was there before. A caller who does care, and
    /// wants the attempt to fail rather than overwrite, wants <see cref="CreateNewFile"/>.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The same as <see cref="OpenFile"/> with
    /// <see cref="FileMode.Create"/>: a link at the last component is refused with
    /// <see cref="CapIOException"/> under either policy, wherever it points and whether or not
    /// it dangles, so what it leads to is neither emptied nor created. A link before the last
    /// component is followed while it stays beneath this handle and refused with
    /// <see cref="SandboxEscapeException"/> if it leaves; under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> it is refused with
    /// <see cref="CapIOException"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">
    /// The name is held by a directory or a symbolic link, a link the policy will not follow
    /// is in the way, or the open failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapFile CreateFile(string path) => OpenFile(path, FileMode.Create, FileAccess.Write);

    /// <summary>
    /// Creates a file beneath this handle, failing if the name is already taken.
    /// </summary>
    /// <param name="path">A relative path. Every component but the last must already exist.</param>
    /// <returns>An open file, guaranteed to have been empty a moment ago.</returns>
    /// <remarks>
    /// <para>
    /// The refusal is part of the one operation that makes the file, never a lookup followed
    /// by a create. That is what makes this usable as a claim on a name: two processes racing
    /// for the same name will see one succeed and the other fail, with no window in which
    /// both believe they won.
    /// </para>
    /// <para>
    /// The name is what is being claimed, so anything holding it is a failure — a file, a
    /// directory, or a symbolic link, whatever the link points at and whether or not its
    /// target exists.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The last component is therefore never followed, under
    /// either policy. A link met before it is followed or refused under this handle's policy
    /// as <see cref="OpenFile"/> describes — refused with <see cref="SandboxEscapeException"/>
    /// if its target leaves the subtree, and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">
    /// The name is already taken, a symbolic link the policy will not follow is in the way,
    /// or the open failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapFile CreateNewFile(string path) => OpenFile(path, FileMode.CreateNew, FileAccess.Write);

    /// <summary>
    /// Creates a file beneath this handle, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="CreateFile"/>.</param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the file was created or emptied and opened.</returns>
    /// <remarks>
    /// Symbolic links are followed or refused exactly as <see cref="CreateFile"/> describes,
    /// the last component included; a refusal is reported as false. Safe to call concurrently
    /// with any other member of this handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateFile(string path, [NotNullWhen(true)] out CapFile? file) =>
        TryOpenFile(path, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.None, 0, append: false, noFollow: false, out file);

    /// <summary>
    /// Claims a name for a new file beneath this handle, reporting failure rather than
    /// throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="CreateNewFile"/>.</param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the name was free and is now this caller's.</returns>
    /// <remarks>
    /// <para>
    /// The form to use when the claim is expected to fail sometimes, which is most of the
    /// times it is worth making: losing a race for a name is an ordinary outcome and building
    /// an exception to describe it is waste on the path that runs most often.
    /// </para>
    /// <para>
    /// Symbolic links are treated exactly as <see cref="CreateNewFile"/> describes: a link
    /// holding the last component makes the name taken and is never followed, one on the way
    /// is followed or refused by this handle's policy, and a refusal is reported as false.
    /// Safe to call concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateNewFile(string path, [NotNullWhen(true)] out CapFile? file) =>
        TryOpenFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.None, 0, append: false, noFollow: false, out file);

    /// <summary>Reads a whole file beneath this handle.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <returns>Its contents.</returns>
    /// <remarks>
    /// <para>
    /// For files small enough to want in one piece. The length is asked for once and the read
    /// carries on to the end regardless, because a file can grow between the two and a length
    /// is a fact about an instant rather than a promise.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Opened as <see cref="OpenFile"/> opens an existing
    /// file, so a link anywhere in the path, the last component included, is followed under
    /// the default policy while its target stays beneath this handle and refused with
    /// <see cref="SandboxEscapeException"/> when it leaves; under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> any link is refused with
    /// <see cref="CapIOException"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, a symbolic link the policy will not follow is in the way,
    /// or the read failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public byte[] ReadAllBytes(string path)
    {
        using CapFile file = OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead);
        return ReadToEnd(file);
    }

    /// <summary>Reads a whole file beneath this handle as text.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <returns>Its contents, decoded.</returns>
    /// <remarks>
    /// <para>
    /// Decoded as UTF-8 unless the file opens with a byte-order mark naming something else,
    /// which is the framework's own rule for the same operation and is therefore what a
    /// caller moving code onto this API already expects. A caller who knows the encoding, or
    /// who does not want it guessed, reads the bytes and decodes them.
    /// </para>
    /// <para>
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any other member
    /// of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, a symbolic link the policy will not follow is in the way,
    /// or the read failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public string ReadAllText(string path)
    {
        using CapFile file = OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead);
        return DecodeText(ReadToEnd(file), Encoding.UTF8);
    }

    /// <summary>Reads a whole file beneath this handle as text in a given encoding.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="encoding">
    /// The encoding to decode with, unless the file opens with a byte-order mark naming
    /// another, as the framework's own read of the same name does.
    /// </param>
    /// <returns>Its contents, decoded.</returns>
    /// <remarks>
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any other member
    /// of this handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, a symbolic link the policy will not follow is in the way,
    /// or the read failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public string ReadAllText(string path, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        using CapFile file = OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead);
        return DecodeText(ReadToEnd(file), encoding);
    }

    /// <summary>Reads a whole file beneath this handle as lines of text.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <returns>Its lines, without their line ends.</returns>
    /// <remarks>
    /// Decoded as <see cref="ReadAllText(string)"/> decodes, and split where the framework's
    /// own line reading splits: at a line feed, a carriage return, or the two together. A
    /// line end at the very end of the file does not start another, empty line. Symbolic
    /// links, the last component included, are followed or refused exactly as
    /// <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any other member of
    /// this handle, from any thread.
    /// </remarks>
    /// <inheritdoc cref="ReadAllText(string)" path="/exception"/>
    public string[] ReadAllLines(string path) => ReadAllLinesCore(path, Encoding.UTF8);

    /// <summary>Reads a whole file beneath this handle as lines of text in a given encoding.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="encoding">
    /// The encoding to decode with, unless the file opens with a byte-order mark naming another.
    /// </param>
    /// <returns>Its lines, without their line ends.</returns>
    /// <remarks>
    /// Split as <see cref="ReadAllLines(string)"/> splits, with symbolic links followed or
    /// refused as <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any
    /// other member of this handle, from any thread.
    /// </remarks>
    /// <inheritdoc cref="ReadAllText(string, Encoding)" path="/exception"/>
    public string[] ReadAllLines(string path, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return ReadAllLinesCore(path, encoding);
    }

    /// <summary>Reads a file beneath this handle a line at a time, as the lines are asked for.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <returns>
    /// Its lines, without their line ends, read as the sequence is enumerated. Enumerate it
    /// once; the file is closed when the enumeration finishes or is disposed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The file is opened by this call, before anything is enumerated, so a missing file or a
    /// refused path is reported here rather than at the first line; what is read afterwards is
    /// the object that open reached, whatever the name comes to hold in the meantime. A
    /// sequence that is never enumerated keeps the file open until it is collected, as the
    /// framework's own does, so enumerate what this returns or dispose its enumerator.
    /// </para>
    /// <para>
    /// Lines are split and decoded as <see cref="ReadAllLines(string)"/> describes. Unlike the
    /// framework's, the sequence cannot be enumerated a second time: that would mean opening
    /// the name again, which may by then name something else.
    /// </para>
    /// <para>
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any other member of
    /// this handle, from any thread; the sequence returned belongs to one caller at a time.
    /// </para>
    /// </remarks>
    /// <inheritdoc cref="ReadAllText(string)" path="/exception"/>
    public IEnumerable<string> ReadLines(string path) => ReadLinesCore(path, Encoding.UTF8);

    /// <summary>
    /// Reads a file beneath this handle a line at a time in a given encoding, as the lines are
    /// asked for.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="encoding">
    /// The encoding to decode with, unless the file opens with a byte-order mark naming another.
    /// </param>
    /// <returns>Its lines, read as the sequence is enumerated. Enumerate it once.</returns>
    /// <remarks>
    /// As <see cref="ReadLines(string)"/>, decoding with <paramref name="encoding"/>. Safe to
    /// call concurrently with any other member of this handle, from any thread.
    /// </remarks>
    /// <inheritdoc cref="ReadAllText(string, Encoding)" path="/exception"/>
    public IEnumerable<string> ReadLines(string path, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return ReadLinesCore(path, encoding);
    }

    /// <summary>
    /// Reads a file beneath this handle a line at a time, without holding the calling thread.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="cancellationToken">
    /// Asks for the reading to be abandoned. Combined with any token given to the
    /// enumeration itself.
    /// </param>
    /// <returns>Its lines, read as the sequence is enumerated. Enumerate it once.</returns>
    /// <remarks>
    /// <para>
    /// The file is opened by this call, on the calling thread, as it is for
    /// <see cref="ReadAllBytesAsync"/>, so a missing file or a refused path is reported here.
    /// Everything else is as <see cref="ReadLines(string)"/> describes.
    /// </para>
    /// <para>
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any other member
    /// of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <inheritdoc cref="ReadAllText(string)" path="/exception"/>
    public IAsyncEnumerable<string> ReadLinesAsync(string path, CancellationToken cancellationToken = default) =>
        ReadLinesAsyncCore(path, Encoding.UTF8, cancellationToken);

    /// <summary>
    /// Reads a file beneath this handle a line at a time in a given encoding, without holding
    /// the calling thread.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="encoding">
    /// The encoding to decode with, unless the file opens with a byte-order mark naming another.
    /// </param>
    /// <param name="cancellationToken">Asks for the reading to be abandoned.</param>
    /// <returns>Its lines, read as the sequence is enumerated. Enumerate it once.</returns>
    /// <remarks>
    /// As <see cref="ReadLinesAsync(string, CancellationToken)"/>, decoding with
    /// <paramref name="encoding"/>. Safe to call concurrently with any other member of this
    /// handle, from any thread.
    /// </remarks>
    /// <inheritdoc cref="ReadAllText(string, Encoding)" path="/exception"/>
    public IAsyncEnumerable<string> ReadLinesAsync(
        string path,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return ReadLinesAsyncCore(path, encoding, cancellationToken);
    }

    /// <summary>Writes a whole file beneath this handle, replacing whatever was there.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <remarks>
    /// <para>
    /// <strong>Symbolic links.</strong> Opened as <see cref="CreateFile"/> opens, so a link at
    /// the last component is refused with <see cref="CapIOException"/> under either policy,
    /// and the file it leads to, if any, is left as it was. A link before the last component
    /// is followed while it stays beneath this handle and refused with
    /// <see cref="SandboxEscapeException"/> if it leaves; under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> it is refused with
    /// <see cref="CapIOException"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, a symbolic link the policy will not follow is in the way,
    /// or the write failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using CapFile file = OpenFile(path, FileMode.Create, FileAccess.Write);
        file.Write(bytes, 0);
    }

    /// <summary>Writes a whole file of text beneath this handle, replacing whatever was there.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="contents">The text to store. Null stores an empty file.</param>
    /// <remarks>
    /// <para>
    /// Encoded as UTF-8 with no byte-order mark, which is what the framework's own text write
    /// produces and what <see cref="ReadAllText(string)"/> assumes of a file that begins with
    /// no mark. The text is encoded a piece at a time as it is written, so no second copy of
    /// it the size of the file is made.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Opened as <see cref="CreateFile"/> opens, exactly as
    /// <see cref="WriteAllBytes"/> is: a link at the last component is refused with
    /// <see cref="CapIOException"/> under either policy, and the file it leads to, if any, is
    /// left as it was. A link before the last component is followed while it stays beneath
    /// this handle and refused with <see cref="SandboxEscapeException"/> if it leaves; under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> it is refused with
    /// <see cref="CapIOException"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <inheritdoc cref="WriteAllBytes(string, ReadOnlySpan{byte})" path="/exception"/>
    public void WriteAllText(string path, string? contents) =>
        WriteText(path, FileMode.Create, contents, Utf8NoMark);

    /// <summary>
    /// Writes a whole file of text in a given encoding beneath this handle, replacing whatever
    /// was there.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="contents">The text to store. Null stores only the encoding's byte-order mark.</param>
    /// <param name="encoding">
    /// The encoding to write in. Its byte-order mark, if it has one, is written first, as the
    /// framework's own text write does.
    /// </param>
    /// <remarks>
    /// Symbolic links are refused or followed exactly as <see cref="WriteAllText(string, string)"/>
    /// describes. Safe to call concurrently with any other member of this handle, from any
    /// thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="encoding"/> is null.</exception>
    /// <inheritdoc cref="WriteAllBytes(string, ReadOnlySpan{byte})" path="/exception"/>
    public void WriteAllText(string path, string? contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        WriteText(path, FileMode.Create, contents, encoding);
    }

    /// <summary>
    /// Writes lines of text as a whole file beneath this handle, replacing whatever was there.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="contents">The lines, each of which is followed by the platform's line end.</param>
    /// <remarks>
    /// <para>
    /// Encoded as <see cref="WriteAllText(string, string)"/> encodes, and ended as the
    /// framework's own line write ends them, with <see cref="Environment.NewLine"/>. The lines
    /// are taken from <paramref name="contents"/> one at a time as they are written, so a
    /// sequence that produces them lazily is never held whole.
    /// </para>
    /// <para>
    /// The file is created or emptied before the first line is asked for, so a sequence that
    /// throws part of the way leaves a file holding the lines before it.
    /// </para>
    /// <para>
    /// Symbolic links are refused or followed exactly as
    /// <see cref="WriteAllText(string, string)"/> describes. Safe to call concurrently with any
    /// other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="contents"/> is null.</exception>
    /// <inheritdoc cref="WriteAllBytes(string, ReadOnlySpan{byte})" path="/exception"/>
    public void WriteAllLines(string path, IEnumerable<string> contents) =>
        WriteLines(path, contents, Utf8NoMark);

    /// <summary>
    /// Writes lines of text in a given encoding as a whole file beneath this handle, replacing
    /// whatever was there.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="contents">The lines, each of which is followed by the platform's line end.</param>
    /// <param name="encoding">
    /// The encoding to write in. Its byte-order mark, if it has one, is written first.
    /// </param>
    /// <remarks>
    /// As <see cref="WriteAllLines(string, IEnumerable{string})"/>, in
    /// <paramref name="encoding"/>. Safe to call concurrently with any other member of this
    /// handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <inheritdoc cref="WriteAllBytes(string, ReadOnlySpan{byte})" path="/exception"/>
    public void WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        WriteLines(path, contents, encoding);
    }

    /// <summary>
    /// Adds text to the end of a file beneath this handle, creating the file if it is not
    /// there.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="contents">The text to add. Null adds nothing, and still creates the file.</param>
    /// <remarks>
    /// <para>
    /// Encoded as UTF-8 with no byte-order mark, as <see cref="WriteAllText(string, string)"/>
    /// is. Every write goes to wherever the end of the file is at that moment, as
    /// <see cref="CapFile.IsAppending"/> describes, on every platform, so appenders sharing the
    /// file do not overwrite one another. The text is written a piece at a time, so a long
    /// one from this call can have another writer's bytes land between its pieces.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Opened as <see cref="OpenFile"/> opens with
    /// <see cref="FileMode.Append"/>, which may create the file: a link at the last component
    /// is refused with <see cref="CapIOException"/> under either policy, and what it leads to
    /// is neither appended to nor created. This is where the library parts from
    /// <c>System.IO</c>, which follows the link. A link before the last component is
    /// followed while it stays beneath this handle and refused with
    /// <see cref="SandboxEscapeException"/> if it leaves; under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> it is refused with
    /// <see cref="CapIOException"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <inheritdoc cref="WriteAllBytes(string, ReadOnlySpan{byte})" path="/exception"/>
    public void AppendAllText(string path, string? contents) =>
        WriteText(path, FileMode.Append, contents, Utf8NoMark);

    /// <summary>
    /// Adds text in a given encoding to the end of a file beneath this handle, creating the
    /// file if it is not there.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="contents">The text to add. Null adds nothing, and still creates the file.</param>
    /// <param name="encoding">
    /// The encoding to write in. Its byte-order mark, if it has one, is written only when the
    /// file was empty, as the framework's own append does, so a mark never lands in the middle
    /// of a file.
    /// </param>
    /// <remarks>
    /// As <see cref="AppendAllText(string, string)"/>, in <paramref name="encoding"/>. Safe to
    /// call concurrently with any other member of this handle, from any thread; two calls
    /// appending to an empty file at once can both write a mark.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="encoding"/> is null.</exception>
    /// <inheritdoc cref="WriteAllBytes(string, ReadOnlySpan{byte})" path="/exception"/>
    public void AppendAllText(string path, string? contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        WriteText(path, FileMode.Append, contents, encoding);
    }

    /// <summary>Reads a whole file beneath this handle without holding the calling thread.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="cancellationToken">Asks for the read to be abandoned.</param>
    /// <returns>Its contents.</returns>
    /// <remarks>
    /// <para>
    /// Opening still happens on the calling thread. Resolution is a short sequence of calls
    /// that no platform offers asynchronously, so an implementation that promised otherwise
    /// would only be moving them to a pool thread and waiting on that.
    /// </para>
    /// <para>
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any other member
    /// of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, a symbolic link the policy will not follow is in the way,
    /// or the read failed otherwise.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        using CapFile file = OpenFile(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead | FileOptions.Asynchronous);

        return await ReadToEndAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a whole file beneath this handle as text, without holding the calling thread.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="cancellationToken">Asks for the read to be abandoned.</param>
    /// <returns>Its contents, decoded as <see cref="ReadAllText(string)"/> describes.</returns>
    /// <remarks>
    /// Opening happens on the calling thread, as it does for <see cref="ReadAllBytesAsync"/>.
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="ReadAllBytes"/> describes. Safe to call concurrently with any other member
    /// of this handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, a symbolic link the policy will not follow is in the way,
    /// or the read failed otherwise.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        using CapFile file = OpenFile(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead | FileOptions.Asynchronous);

        return DecodeText(await ReadToEndAsync(file, cancellationToken).ConfigureAwait(false), Encoding.UTF8);
    }

    /// <summary>
    /// Writes a whole file beneath this handle without holding the calling thread, replacing
    /// whatever was there.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>
    /// <para>
    /// A token that is already signalled when the call is made stops it before the file is
    /// opened, so the file is left exactly as it was, as the framework's own whole-file write
    /// leaves it.
    /// </para>
    /// <para>
    /// Abandoning a write that has started does not undo it. The file has already been
    /// emptied by the time any of the contents are written, so a call cancelled part of the
    /// way through leaves a file that is shorter than it was — cancellation releases the
    /// caller and says nothing about what is on disk.
    /// </para>
    /// <para>
    /// Symbolic links are followed or refused exactly as <see cref="WriteAllBytes"/>
    /// describes, so a link at the last component is refused and what it leads to is left
    /// alone. Safe to call concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, a symbolic link the policy will not follow is in the way,
    /// or the write failed otherwise.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using CapFile file = OpenFile(
            path, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);

        await file.WriteAsync(bytes, 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a caller's open request and turns it into the one the platform layer takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal here is about a request that cannot mean anything, rather than about
    /// something the filesystem would have said no to. A mode that can only create combined
    /// with an access that can only read describes no operation at all, and the framework's
    /// own file open refuses the same combinations — so code moving onto this API keeps the
    /// diagnosis it had.
    /// </para>
    /// <para>
    /// An undefined enumeration value is refused rather than ignored, because every one of
    /// these is read by masking or by a switch whose default is the mildest case, and a value
    /// arriving from a bad cast or a stale constant would otherwise quietly select the
    /// mildest behaviour.
    /// </para>
    /// <para>
    /// Inheritable sharing is refused outright and on different grounds: it asks for a handle
    /// a child process would receive, and a child that receives one has been handed authority
    /// nobody granted it. Every handle this library opens is closed across a process launch
    /// for that reason, and an option that undid it would undo the guarantee rather than
    /// widen it.
    /// </para>
    /// </remarks>
    private static FileOpenRequest Demand(
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        bool noFollow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preallocationSize);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The file mode is not one of the defined values.");
        }

        if (access is < FileAccess.Read or > FileAccess.ReadWrite)
        {
            throw new ArgumentOutOfRangeException(
                nameof(access), access, "The file access is not one of the defined values.");
        }

        const FileShare KnownSharing = FileShare.Read | FileShare.Write | FileShare.Delete;
        if ((share & FileShare.Inheritable) != 0)
        {
            throw new ArgumentException(
                "Inheritable sharing asks for a handle a child process would receive, which " +
                "would hand that process authority nobody granted it. Every handle opened " +
                "through a capability is closed across a process launch, and this option " +
                "would undo that rather than widen it.",
                nameof(share));
        }

        if ((share & ~KnownSharing) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(share), share, "The sharing request contains a value that is not defined.");
        }

        const FileOptions KnownOptions = FileOptions.WriteThrough | FileOptions.Asynchronous |
                                         FileOptions.RandomAccess | FileOptions.DeleteOnClose |
                                         FileOptions.SequentialScan | FileOptions.Encrypted;

        if ((options & ~KnownOptions) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options, "The open options contain a value that is not defined.");
        }

        if (mode == FileMode.Append && access != FileAccess.Write)
        {
            throw new ArgumentException(
                "FileMode.Append means what it means to System.IO: open or create, and write " +
                "only. To append through a handle that can also read, ask for appending " +
                "separately, with FileMode.OpenOrCreate or whichever mode is wanted.",
                nameof(access));
        }

        if (append && (access & FileAccess.Write) == 0)
        {
            throw new ArgumentException(
                "Appending is a rule about where writes go, and this handle could not write. " +
                "Ask for an access that can write, or do not ask to append.",
                nameof(append));
        }

        if ((access & FileAccess.Write) == 0 &&
            mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate or FileMode.Append)
        {
            throw new ArgumentException(
                "A mode that creates or empties a file cannot be combined with an access " +
                "that cannot write it. The open would change the file and then refuse to let " +
                "the caller put anything in it.",
                nameof(mode));
        }

        if (preallocationSize > 0 && mode is FileMode.Open or FileMode.Append)
        {
            throw new ArgumentException(
                "Room can only be claimed in advance for an open that starts the file from " +
                "nothing. Claiming it for a file opened as it stands would extend data " +
                "somebody else wrote.",
                nameof(preallocationSize));
        }

        return new FileOpenRequest(mode, access, share, options, preallocationSize, append, noFollow);
    }

    /// <summary>
    /// Resolves a path and opens the file it names, as the request describes.
    /// </summary>
    /// <remarks>
    /// One resolution, not a parent lookup followed by an open. Splitting the path so that
    /// the last component could be created by a separate call would put an instant between
    /// resolving the prefix and taking the name, and on the platform that can resolve a whole
    /// path in one operation that instant is exactly what the operation was chosen to remove.
    /// </remarks>
    /// <summary>
    /// Creates a file with no name in any directory, taking its storage from this one.
    /// </summary>
    /// <remarks>
    /// Internal because the decision about what to do when the platform has no such facility
    /// belongs with the scratch-file helper rather than with every caller. The failure is
    /// handed back as a value for the same reason: an answer of "this system does not do
    /// that" is something to act on, and building an exception for it would make the
    /// ordinary case on those systems the expensive one.
    /// </remarks>
    internal CapResult<CapFile> OpenAnonymousFile()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        CapResult<SafeFileHandle> opened = Ops.OpenAnonymousChildFile(_handle, FileAccess.ReadWrite);
        return opened.IsSuccess
            ? CapResult<CapFile>.Ok(new CapFile(opened.Value, Ops, FileAccess.ReadWrite, isAsync: false, appending: false))
            : CapResult<CapFile>.Fail(opened.Error);
    }

    private CapPathError OpenFileCore(
        string path,
        in FileOpenRequest request,
        out CapFile? file,
        out CapError error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        file = null;
        error = CapError.Success;

        if (!TryParseCallerPath(path, out CapPath parsed, out CapPathError pathError))
        {
            // The handle's own directory, which is there and is a directory, so the refusal
            // is the one a path ending in `..` that climbed back to it is given.
            if (NamesThisDirectory(path, pathError))
            {
                error = CapError.FromCategory(CapErrorCategory.IsADirectory);
                return CapPathError.None;
            }

            return pathError;
        }

        // A trailing separator, or a final `..`, asks for a directory, and no file open can
        // satisfy that. The parser is the only thing that still knows: splitting a path into
        // components is what loses the distinction, so it has to be applied before resolution
        // or not at all.
        if (parsed.RequiresDirectory)
        {
            error = RefuseDirectorySpelling(in parsed, in request);
            return CapPathError.None;
        }

        CapResult<SafeFileHandle> opened = Resolver.OpenFile(_handle, in parsed, in request, _options);
        if (!opened.IsSuccess)
        {
            error = opened.Error;
            return CapPathError.None;
        }

        file = new CapFile(
            opened.Value,
            Ops,
            request.Access,
            request.IsAsynchronous && Ops.Capabilities.SupportsOverlappedFileHandles,
            request.Appends);

        return CapPathError.None;
    }

    /// <summary>
    /// Resolves a path and opens whatever it names, as a directory or as a file.
    /// </summary>
    /// <remarks>
    /// A path spelled as a directory needs no refusal of its own here, unlike a file open:
    /// resolution opens only a directory for it, and says what it found otherwise.
    /// </remarks>
    private CapPathError OpenAnyCore(
        string path,
        in FileOpenRequest request,
        out CapOpened? opened,
        out CapError error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        opened = null;
        error = CapError.Success;

        if (!TryParseCallerPath(path, out CapPath parsed, out CapPathError pathError))
        {
            if (!NamesThisDirectory(path, pathError))
            {
                return pathError;
            }

            // The handle's own directory, opened as resolution would open it: only for a
            // request a directory can satisfy.
            if (!request.OpensAnyKind)
            {
                error = CapError.FromCategory(CapErrorCategory.InvalidArgument);
                return CapPathError.None;
            }

            error = CloneCore(out Dir? self);
            opened = self is null ? null : new CapOpened(self);
            return CapPathError.None;
        }

        CapResult<OpenedNode> node = Resolver.OpenNode(_handle, in parsed, in request, _options);
        if (!node.IsSuccess)
        {
            error = node.Error;
            return CapPathError.None;
        }

        opened = node.Value.Directory is { } directory
            ? new CapOpened(new Dir(directory, _options))
            : new CapOpened(new CapFile(
                node.Value.File!,
                node.Value.Backend,
                request.Access,
                request.IsAsynchronous && node.Value.Backend.Capabilities.SupportsOverlappedFileHandles,
                appending: false));

        return CapPathError.None;
    }

    /// <summary>
    /// Says whether an open that found something missing was missing its last component or
    /// a directory on the way to it, for the exception that reports it.
    /// </summary>
    /// <param name="path">The path the open was given.</param>
    /// <param name="error">The open's failure.</param>
    /// <remarks>
    /// <para>
    /// Resolution reports one category for both, and an open resolves the whole path in one
    /// call, so the only way to tell is to resolve again only as far as the directory that
    /// holds the last component. That is a second lookup, made only on the failure path and
    /// only for a failure that is a missing thing, so an open that succeeds costs nothing more.
    /// It decides the exception's type and nothing else; if the tree changes between the two,
    /// the answer can describe the later state, but nothing is opened or acted on by it.
    /// </para>
    /// <para>
    /// A dangling link as the last component leaves the parent in place, so it is reported
    /// as a missing file, as the framework reports one. A path ending in <c>..</c> names a
    /// directory, so something missing on it can only be a directory it passes through.
    /// </para>
    /// </remarks>
    private ExpectedTarget ClassifyMissing(string path, CapError error)
    {
        if (error.Category != CapErrorCategory.NotFound ||
            !TryParseCallerPath(path, out CapPath parsed, out _) ||
            !parsed.TrySplitLastComponent(out ReadOnlySpan<char> prefix, out ReadOnlySpan<char> name))
        {
            return ExpectedTarget.Name;
        }

        if (name.SequenceEqual(".."))
        {
            return ExpectedTarget.Parent;
        }

        if (prefix.IsEmpty)
        {
            return ExpectedTarget.Name;
        }

        CapResult<ResolvedParent> parent = Resolver.ResolveParent(_handle, in parsed, _options);
        if (parent.IsSuccess)
        {
            parent.Value.Dispose();
            return ExpectedTarget.Name;
        }

        return parent.Error.Category == CapErrorCategory.NotFound ? ExpectedTarget.Parent : ExpectedTarget.Name;
    }

    /// <summary>
    /// The refusal for a file open whose path is spelled so that it names a directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is resolved as a directory, without opening it for reading, and the refusal
    /// says what was found there, as <c>open(2)</c> does: nothing, something that is not a
    /// directory, or a directory, which a file open cannot use. A caller told only "is a
    /// directory" about a name holding a regular file would go looking for the wrong mistake.
    /// </para>
    /// <para>
    /// An open that may create the file is refused as naming a directory whatever is there,
    /// again as <c>open(2)</c> refuses one, since a file cannot be created under a name
    /// spelled as a directory. The lookup is still made, so that a path climbing above the
    /// handle is reported as the escape it is on every open, rather than disguised as the
    /// wrong kind of object; the attempt deserves to reach whatever records escapes.
    /// </para>
    /// <para>
    /// Nothing is opened for use either way. What the name holds may change before the answer
    /// is read, which can alter which refusal is given but never lets the open go ahead.
    /// </para>
    /// </remarks>
    private CapError RefuseDirectorySpelling(scoped in CapPath parsed, scoped in FileOpenRequest request)
    {
        CapError isADirectory = CapError.FromCategory(CapErrorCategory.IsADirectory);

        CapResult<SafeDirHandle> reached = Resolver.OpenDirectory(_handle, in parsed, CapAccess.None, _options);
        if (reached.IsSuccess)
        {
            reached.Value.Dispose();
            return isADirectory;
        }

        return request.Creates && reached.Error.Category != CapErrorCategory.Escaped
            ? isADirectory
            : reached.Error;
    }

    /// <summary>
    /// Presents a file as a text reader, giving the stream the handle so that disposing the
    /// reader closes everything.
    /// </summary>
    private static StreamReader OpenReader(CapFile file, Encoding encoding)
    {
        Stream stream = file.AsStream(leaveOpen: false);
        try
        {
            return new(stream, encoding, detectEncodingFromByteOrderMarks: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes a whole file's bytes exactly as a <see cref="StreamReader"/> that detects
    /// byte-order marks would, without the stream, the reader and their buffers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader looks for <paramref name="encoding"/>'s own preamble first, and when the
    /// file opens with it, drops it and looks no further. A file shorter than that preamble
    /// which matches as far as it goes is decoded whole in <paramref name="encoding"/>, mark
    /// and all, since the reader is still waiting for the rest of the preamble when the file
    /// ends. Otherwise the marks are tried in the reader's order: UTF-16 big-endian, then
    /// little-endian unless two zero bytes follow and make it UTF-32, then UTF-8, then UTF-32
    /// big-endian; a file with none of them is <paramref name="encoding"/> throughout.
    /// </para>
    /// <para>
    /// Decoding the whole array at once gives the same characters the reader's chunked
    /// decoder does, invalid sequences included, because that decoder carries a partial
    /// sequence over from one chunk to the next and flushes it at the end.
    /// </para>
    /// </remarks>
    private static string DecodeText(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        ReadOnlySpan<byte> preamble = encoding.Preamble;
        if (preamble.Length > 0)
        {
            int compared = Math.Min(bytes.Length, preamble.Length);
            if (bytes[..compared].SequenceEqual(preamble[..compared]))
            {
                return encoding.GetString(compared == preamble.Length ? bytes[compared..] : bytes);
            }
        }

        if (bytes.Length >= 2)
        {
            switch (bytes[0], bytes[1])
            {
                case (0xFE, 0xFF):
                    return Encoding.BigEndianUnicode.GetString(bytes[2..]);
                case (0xFF, 0xFE):
                    return bytes.Length >= 4 && bytes[2] == 0 && bytes[3] == 0
                        ? Encoding.UTF32.GetString(bytes[4..])
                        : Encoding.Unicode.GetString(bytes[2..]);
                case (0xEF, 0xBB) when bytes.Length >= 3 && bytes[2] == 0xBF:
                    return Encoding.UTF8.GetString(bytes[3..]);
                case (0x00, 0x00) when bytes.Length >= 4 && bytes[2] == 0xFE && bytes[3] == 0xFF:
                    return Utf32BigEndian.GetString(bytes[4..]);
            }
        }

        return encoding.GetString(bytes);
    }

    /// <summary>The UTF-32 big-endian encoding a reader switches to on seeing its mark.</summary>
    private static readonly UTF32Encoding Utf32BigEndian = new(bigEndian: true, byteOrderMark: true);

    /// <summary>
    /// UTF-8 with no byte-order mark, refusing to encode a string that is not valid UTF-16,
    /// which is what the framework's own text writes use when no encoding is named.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoMark = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// How many characters a text write encodes at a time: enough that a long text is not
    /// written in thousands of pieces, few enough that the bytes they encode to come from the
    /// shared pool.
    /// </summary>
    private const int TextChunk = 4096;

    /// <summary>Reads every line of a file into an array.</summary>
    private string[] ReadAllLinesCore(string path, Encoding encoding)
    {
        using CapFile file = OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead);
        using StreamReader reader = OpenReader(file, encoding);

        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    /// <summary>
    /// Opens a file now and hands back a sequence that reads its lines later.
    /// </summary>
    /// <remarks>
    /// Split from the iterator so that the open, and any failure of it, happens when the
    /// caller asks rather than at the first line. The reader is owned by the sequence from
    /// then on, and is disposed when an enumeration of it ends.
    /// </remarks>
    private IEnumerable<string> ReadLinesCore(string path, Encoding encoding)
    {
        CapFile file = OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead);
        StreamReader reader;
        try
        {
            reader = OpenReader(file, encoding);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        return Lines(reader);

        static IEnumerable<string> Lines(StreamReader reader)
        {
            using (reader)
            {
                while (reader.ReadLine() is { } line)
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>The asynchronous twin of <see cref="ReadLinesCore"/>.</summary>
    private IAsyncEnumerable<string> ReadLinesAsyncCore(string path, Encoding encoding, CancellationToken cancellationToken)
    {
        CapFile file = OpenFile(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead | FileOptions.Asynchronous);
        StreamReader reader;
        try
        {
            reader = OpenReader(file, encoding);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        return Lines(reader, cancellationToken);

        static async IAsyncEnumerable<string> Lines(
            StreamReader reader,
            CancellationToken given,
            [EnumeratorCancellation] CancellationToken enumerating = default)
        {
            using (reader)
            {
                using CancellationTokenSource? linked = given.CanBeCanceled && enumerating.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(given, enumerating)
                    : null;
                CancellationToken token = linked?.Token ?? (given.CanBeCanceled ? given : enumerating);

                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>Opens a file for a text write and writes the text into it.</summary>
    /// <param name="path">The caller's path.</param>
    /// <param name="mode">
    /// <see cref="FileMode.Create"/> to replace the file, or <see cref="FileMode.Append"/> to add
    /// to its end.
    /// </param>
    /// <param name="contents">The text, or null for none.</param>
    /// <param name="encoding">The encoding to write it in.</param>
    private void WriteText(string path, FileMode mode, string? contents, Encoding encoding)
    {
        using CapFile file = OpenFile(path, mode, FileAccess.Write);
        using TextSink sink = new(file, encoding, preamble: mode != FileMode.Append || file.Length == 0);
        sink.Write(contents);
        sink.Finish();
    }

    /// <summary>Creates or empties a file and writes lines into it.</summary>
    private void WriteLines(string path, IEnumerable<string> contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(contents);

        using CapFile file = OpenFile(path, FileMode.Create, FileAccess.Write);
        using TextSink sink = new(file, encoding, preamble: true);
        foreach (string line in contents)
        {
            sink.Write(line);
            sink.Write(Environment.NewLine);
        }

        sink.Finish();
    }

    /// <summary>
    /// Encodes text into a file a piece at a time, through positioned writes, so that no copy
    /// of the whole text in bytes is ever made.
    /// </summary>
    /// <remarks>
    /// Written through the handle rather than through a stream, so that a file opened to
    /// append puts every piece at its end on every platform, as <see cref="CapFile.Write"/>
    /// does, rather than wherever a stream's own position has got to.
    /// </remarks>
    private sealed class TextSink : IDisposable
    {
        private readonly CapFile _file;
        private readonly Encoder _encoder;
        private readonly byte[] _buffer;
        private long _offset;
        private bool _returned;

        public TextSink(CapFile file, Encoding encoding, bool preamble)
        {
            _file = file;
            _encoder = encoding.GetEncoder();
            _buffer = ArrayPool<byte>.Shared.Rent(encoding.GetMaxByteCount(TextChunk));

            if (preamble)
            {
                ReadOnlySpan<byte> mark = encoding.Preamble;
                if (!mark.IsEmpty)
                {
                    Put(mark);
                }
            }
        }

        /// <summary>Encodes and writes some text, keeping any half of a pair of surrogates for later.</summary>
        public void Write(ReadOnlySpan<char> text)
        {
            while (!text.IsEmpty)
            {
                ReadOnlySpan<char> piece = text.Length > TextChunk ? text[..TextChunk] : text;
                _encoder.Convert(piece, _buffer, flush: false, out int used, out int produced, out _);
                Put(_buffer.AsSpan(0, produced));
                text = text[used..];
            }
        }

        /// <summary>Writes whatever the encoder was still holding.</summary>
        public void Finish()
        {
            bool completed;
            do
            {
                _encoder.Convert(ReadOnlySpan<char>.Empty, _buffer, flush: true, out _, out int produced, out completed);
                Put(_buffer.AsSpan(0, produced));
            }
            while (!completed);
        }

        public void Dispose()
        {
            if (!_returned)
            {
                _returned = true;
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }

        private void Put(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return;
            }

            _file.Write(bytes, _offset);
            _offset += bytes.Length;
        }
    }

    /// <summary>
    /// Reads a file from the beginning until it stops giving anything back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The length is a starting estimate and never a stopping condition. A file can grow
    /// while it is being read, and several kinds of file report no length at all while still
    /// having contents, so the loop ends when a read returns nothing rather than when a
    /// counter reaches a number decided beforehand.
    /// </para>
    /// <para>
    /// Finding that end is the one extra read, and it is made into a single spare byte rather
    /// than into a larger array. The usual file is exactly as long as it said, fills the buffer
    /// on the first read, and has nothing more to give; growing the buffer to ask would
    /// allocate twice the file and then a third copy to trim it back, for an answer that is
    /// almost always "nothing". The buffer grows only once that byte has actually arrived.
    /// </para>
    /// </remarks>
    private static byte[] ReadToEnd(CapFile file)
    {
        long length = file.Length;
        byte[] buffer = new byte[Capacity(length)];
        int filled = 0;
        Span<byte> probe = stackalloc byte[1];

        while (true)
        {
            if (filled == buffer.Length)
            {
                if (file.Read(probe, filled) == 0)
                {
                    break;
                }

                Grow(ref buffer);
                buffer[filled++] = probe[0];
            }

            int read = file.Read(buffer.AsSpan(filled), filled);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        if (filled != buffer.Length)
        {
            Array.Resize(ref buffer, filled);
        }

        return buffer;
    }

    /// <summary>The asynchronous twin of <see cref="ReadToEnd"/>, ending the same way.</summary>
    private static async Task<byte[]> ReadToEndAsync(CapFile file, CancellationToken cancellationToken)
    {
        long length = file.Length;
        byte[] buffer = new byte[Capacity(length)];
        int filled = 0;
        byte[] probe = ArrayPool<byte>.Shared.Rent(1);

        try
        {
            while (true)
            {
                if (filled == buffer.Length)
                {
                    int extra = await file
                        .ReadAsync(probe.AsMemory(0, 1), filled, cancellationToken)
                        .ConfigureAwait(false);

                    if (extra == 0)
                    {
                        break;
                    }

                    Grow(ref buffer);
                    buffer[filled++] = probe[0];
                }

                int read = await file
                    .ReadAsync(buffer.AsMemory(filled), filled, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                filled += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe);
        }

        if (filled != buffer.Length)
        {
            Array.Resize(ref buffer, filled);
        }

        return buffer;
    }

    /// <summary>Makes room in a whole-file read's buffer once the file has proved longer than it.</summary>
    private static void Grow(ref byte[] buffer) =>
        Array.Resize(ref buffer, NextCapacity(buffer.Length));

    /// <summary>
    /// The size a whole-file read's buffer grows to from <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Doubling stops at the largest array rather than wrapping past it, and a buffer already
    /// that large is refused the way <see cref="Capacity"/> refuses a file that says it is
    /// too big: the file has proved to hold at least one byte more than one array can return.
    /// </remarks>
    internal static int NextCapacity(int current)
    {
        if (current >= Array.MaxLength)
        {
            throw TooLargeForOneArray((long)Array.MaxLength + 1, exact: false);
        }

        long doubled = current == 0 ? GrowthStep : (long)current * 2;
        return (int)Math.Min(doubled, Array.MaxLength);
    }

    /// <summary>
    /// The first allocation for a whole-file read, from the length the file reports.
    /// </summary>
    /// <remarks>
    /// A file larger than a single array can hold is refused here rather than part-way
    /// through, and refused as what it is. Left to the arithmetic it would surface as an
    /// overflow, which describes this library's buffer rather than the caller's problem: the
    /// file is too big to be wanted in one piece, and the answer is to read it in parts.
    /// </remarks>
    private static int Capacity(long length)
    {
        if (length > Array.MaxLength)
        {
            throw TooLargeForOneArray(length, exact: true);
        }

        return length > 0 ? (int)length : 0;
    }

    /// <summary>
    /// The refusal for a whole-file read of a file too large for one array, whether its
    /// reported length said so or it only proved so while being read.
    /// </summary>
    private static CapIOException TooLargeForOneArray(long length, bool exact) =>
        new(
            $"The file holds {(exact ? "" : "at least ")}{length} bytes, which is more than " +
            $"can be returned as one array. Open it and read it in parts instead.");

    /// <summary>
    /// How much room a whole-file read adds when it has run out and the file is still
    /// giving bytes back.
    /// </summary>
    /// <remarks>
    /// Only ever reached by a file whose reported length was wrong or has since grown, so it
    /// is a recovery step rather than the usual path: the first allocation is the file's own
    /// length and normally holds all of it.
    /// </remarks>
    private const int GrowthStep = 8192;
}
