using System.Buffers;
using System.Diagnostics.CodeAnalysis;
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
/// middle of a path is reported as missing rather than created, because creating a chain
/// means deciding what to do with the ones already made when a later one fails, and that is
/// a policy a caller should choose rather than inherit.
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
    /// A relative path of one or more components. Absolute paths, paths naming a drive or a
    /// network location, and paths containing <c>..</c> are refused: none of them names
    /// something this handle covers. A path spelled so that its target must be a directory —
    /// one ending in a separator — is refused too, because no file can satisfy it.
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
    /// <returns>An open file, owning its handle.</returns>
    /// <remarks>
    /// <para>
    /// Resolution is confined to the subtree this handle was opened on, by whichever backend
    /// the platform provides, and a symbolic link met on the way is followed only if this
    /// handle's policy allows it and only while it stays inside. That applies to the last
    /// component as well: opening a link is opening its target, so a file created through a
    /// link appears where the link points — and only if the link points inside.
    /// </para>
    /// <para>
    /// Nothing about the file's permissions is decided here. A created file is asked for with
    /// the permissions every other program asks for, which the system then narrows as it is
    /// configured to; a capability bounds what can be reached and is not a substitute for the
    /// filesystem's own access control.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable name, or the requested combination of mode,
    /// access and sharing is not one that means anything.
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
    /// The name is taken and the mode refuses to take it, the name holds a directory, or the
    /// platform cannot honour part of the request.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapFile OpenFile(
        string path,
        FileMode mode = FileMode.Open,
        FileAccess access = FileAccess.Read,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0)
    {
        FileOpenRequest request = Demand(mode, access, share, options, preallocationSize);

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
        // cannot create has no such certainty, and the file is by far the likelier answer.
        // This is the whole of the distinction the framework draws between its two missing-
        // thing exceptions, and drawing it the same way keeps a ported catch clause matching.
        ExpectedTarget expected = request.Creates ? ExpectedTarget.Directory : ExpectedTarget.Name;
        throw FailureTranslation.ToException(error, path, expected);
    }

    /// <summary>
    /// Opens a file beneath this handle, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenFile"/>.</param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the file was opened.</returns>
    /// <remarks>
    /// False covers every reason it was not opened, a missing file and a containment refusal
    /// alike. An application that audits escape attempts calls <see cref="OpenFile"/> and
    /// catches <see cref="SandboxEscapeException"/>; this form deliberately reports no
    /// reason, so that the failure path builds no message and no exception.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenFile(string path, [NotNullWhen(true)] out CapFile? file) =>
        TryOpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0, out file);

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
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the file was opened.</returns>
    /// <remarks>
    /// A request that cannot mean anything still throws, as it does from <see cref="OpenFile"/>.
    /// This form is about a filesystem that said no, which is an outcome; a mode combined with
    /// an access it contradicts is a mistake in the calling code, and reporting it as an
    /// ordinary failure would hide it behind whichever branch the caller wrote for a missing
    /// file.
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
        [NotNullWhen(true)] out CapFile? file)
    {
        FileOpenRequest request = Demand(mode, access, share, options, preallocationSize);
        return Succeeded(OpenFileCore(path, in request, out file, out CapError error), error);
    }

    /// <summary>
    /// Creates a file beneath this handle, emptying it if the name is already taken.
    /// </summary>
    /// <param name="path">A relative path. Every component but the last must already exist.</param>
    /// <returns>An open file, ready to be written from the beginning.</returns>
    /// <remarks>
    /// The shorthand for the most common write: the caller has something to store and does
    /// not care whether a file of that name was there before. A caller who does care, and
    /// wants the attempt to fail rather than overwrite, wants <see cref="CreateNewFile"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">The name is held by a directory, or the open failed otherwise.</exception>
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
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">The name is already taken.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapFile CreateNewFile(string path) => OpenFile(path, FileMode.CreateNew, FileAccess.Write);

    /// <summary>
    /// Creates a file beneath this handle, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="CreateFile"/>.</param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the file was created or emptied and opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateFile(string path, [NotNullWhen(true)] out CapFile? file) =>
        TryOpenFile(path, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.None, 0, out file);

    /// <summary>
    /// Claims a name for a new file beneath this handle, reporting failure rather than
    /// throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="CreateNewFile"/>.</param>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when the name was free and is now this caller's.</returns>
    /// <remarks>
    /// The form to use when the claim is expected to fail sometimes, which is most of the
    /// times it is worth making: losing a race for a name is an ordinary outcome and building
    /// an exception to describe it is waste on the path that runs most often.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateNewFile(string path, [NotNullWhen(true)] out CapFile? file) =>
        TryOpenFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.None, 0, out file);

    /// <summary>Reads a whole file beneath this handle.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <returns>Its contents.</returns>
    /// <remarks>
    /// For files small enough to want in one piece. The length is asked for once and the read
    /// carries on to the end regardless, because a file can grow between the two and a length
    /// is a fact about an instant rather than a promise.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
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
    /// Decoded as UTF-8 unless the file opens with a byte-order mark naming something else,
    /// which is the framework's own rule for the same operation and is therefore what a
    /// caller moving code onto this API already expects. A caller who knows the encoding, or
    /// who does not want it guessed, reads the bytes and decodes them.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public string ReadAllText(string path)
    {
        using CapFile file = OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead);
        using StreamReader reader = OpenReader(file);
        return reader.ReadToEnd();
    }

    /// <summary>Writes a whole file beneath this handle, replacing whatever was there.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using CapFile file = OpenFile(path, FileMode.Create, FileAccess.Write);
        file.Write(bytes, 0);
    }

    /// <summary>Reads a whole file beneath this handle without holding the calling thread.</summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="cancellationToken">Asks for the read to be abandoned.</param>
    /// <returns>Its contents.</returns>
    /// <remarks>
    /// Opening still happens on the calling thread. Resolution is a short sequence of calls
    /// that no platform offers asynchronously, so an implementation that promised otherwise
    /// would only be moving them to a pool thread and waiting on that.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
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
    /// <returns>Its contents, decoded as <see cref="ReadAllText"/> describes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        using CapFile file = OpenFile(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, SequentialRead | FileOptions.Asynchronous);

        using StreamReader reader = OpenReader(file);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a whole file beneath this handle without holding the calling thread, replacing
    /// whatever was there.
    /// </summary>
    /// <param name="path">A relative path to the file.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>
    /// Abandoning it does not undo it. The file has already been emptied by the time any of
    /// the contents are written, so a cancelled call leaves a file that is shorter than it
    /// was — cancellation releases the caller and says nothing about what is on disk.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
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
        long preallocationSize)
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
                "A file opened to append can only be written. Every write on such a handle " +
                "goes to the end of the file whatever offset it is given, so a handle that " +
                "could also read would be one whose reads and writes disagreed about where " +
                "they were.",
                nameof(access));
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

        return new FileOpenRequest(mode, access, share, options, preallocationSize);
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
    internal CapResult<SafeFileHandle> OpenAnonymousFile()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return PlatformOps.Current.OpenAnonymousChildFile(_handle, FileAccess.ReadWrite);
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

        if (!CapPath.TryParse(path, out CapPath parsed, out CapPathError pathError))
        {
            return pathError;
        }

        // A trailing separator asks for a directory, and no file open can satisfy that. The
        // parser is the only thing that still knows: splitting a path into components is what
        // loses the distinction, so it has to be applied before resolution or not at all.
        if (parsed.RequiresDirectory)
        {
            error = CapError.FromCategory(CapErrorCategory.IsADirectory);
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
            request.Access,
            request.IsAsynchronous && PlatformOps.Current.Capabilities.SupportsOverlappedFileHandles);

        return CapPathError.None;
    }

    /// <summary>
    /// Presents a file as a text reader, giving the stream the handle so that disposing the
    /// reader closes everything.
    /// </summary>
    private static StreamReader OpenReader(CapFile file) =>
        new(file.AsStream(leaveOpen: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

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
        Array.Resize(ref buffer, buffer.Length == 0 ? GrowthStep : buffer.Length * 2);

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
            throw new CapIOException(
                $"The file holds {length} bytes, which is more than can be returned as one " +
                $"array. Open it and read it in parts instead.");
        }

        return length > 0 ? (int)length : 0;
    }

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
