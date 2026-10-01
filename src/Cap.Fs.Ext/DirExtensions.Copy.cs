using System.Buffers;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Copying a tree from one handle to another.
/// </summary>
/// <remarks>
/// <para>
/// Handle to handle throughout: the source is descended by opening each directory through the
/// handle that listed it, and the destination is built by creating each directory through the
/// handle above it. Neither side ever holds a path, so a copy is confined at both ends by the
/// two capabilities that were combined to perform it — and combining them is exactly what a
/// copy is, which is why the destination is a handle rather than a string.
/// </para>
/// <para>
/// <strong>What a copy cannot honestly reproduce, it refuses to reproduce.</strong> A
/// symbolic link is not followed, so a tree containing one aimed outside the sandbox cannot
/// be used to drag something in; a named pipe or a device node is not read, so a tree
/// containing one cannot be used to make the copy block forever or to fill a disk. By default
/// each of those stops the copy and says what it found. The caller decides otherwise
/// deliberately, per kind, in <see cref="CopyOptions"/>.
/// </para>
/// <para>
/// <strong>A hard link is not preserved.</strong> Two names for one object in the source
/// become two independent files in the destination, holding the same bytes and sharing
/// nothing. Recognising them would mean remembering the identity of every file copied and
/// making the second name a link to the first, which is a different operation with different
/// consequences for whoever writes to the result afterwards — and doing it silently would
/// make a copy of a tree of hard links into a tree where writing one file changes another.
/// </para>
/// </remarks>
public static partial class DirExtensions
{
    /// <summary>
    /// How much is read from a file before it is written to the destination.
    /// </summary>
    /// <remarks>
    /// Large enough that a big file is not copied in thousands of round trips, small enough
    /// that the buffer comes from the shared pool rather than from the heap segment reserved
    /// for large objects — which it would if it were any larger, making every copy a source of
    /// collections that cannot be compacted.
    /// </remarks>
    private const int TransferBufferSize = 64 * 1024;

    /// <summary>
    /// Copies everything beneath this handle into another directory.
    /// </summary>
    /// <param name="dir">The directory whose contents are copied.</param>
    /// <param name="destination">The directory they are copied into.</param>
    /// <param name="options">What the copy does with what it finds, or null for the defaults.</param>
    /// <param name="progress">
    /// Told what has been copied so far each time an entry is done, or null for no reports.
    /// </param>
    /// <param name="cancellationToken">Stops the copy before the next entry or the next piece of a file.</param>
    /// <returns>What was copied, and what was left out.</returns>
    /// <remarks>
    /// <para>
    /// The contents are copied, not the directory itself: entries directly inside
    /// <paramref name="dir"/> become entries directly inside <paramref name="destination"/>.
    /// A caller who wants the source to appear as a named directory inside the destination
    /// creates that directory and passes a handle on it.
    /// </para>
    /// <para>
    /// <strong>The destination must not be the source or inside it.</strong> A destination
    /// that is the source directory itself is refused before anything is read or written. A
    /// copy into its own subtree would copy what it had just written, without end, so it is
    /// refused as soon as the copy reaches the directory in question rather than being allowed
    /// to run; whatever the copy had reached before it — the directories above it, and
    /// anything listed ahead of them — has by then been copied into the destination and is
    /// left there. The reverse — a source inside the destination — is an ordinary copy and is
    /// allowed.
    /// </para>
    /// <para>
    /// <strong>It is not atomic and it is not a snapshot.</strong> A source that is being
    /// changed while the copy runs is copied partly as it was and partly as it became, and a
    /// failure part of the way through leaves the destination holding what had been copied
    /// until then.
    /// </para>
    /// <para>
    /// <strong>Cancellation and failure part of the way through a file.</strong> The token is
    /// looked at before each entry and between the pieces a file is copied in, and a signal
    /// stops the copy with an <see cref="OperationCanceledException"/>. What stopping leaves is
    /// what a failure leaves: every entry finished before it is in the destination, and the file
    /// being written when it came is not. That file is removed rather than left holding part of
    /// its contents under its real name — or, when <see cref="CopyOptions.Overwrite"/> is on, its
    /// scratch copy is removed and whatever held the name keeps it — so no name in the
    /// destination holds a partly written file and no scratch name is left behind. Directories
    /// already made stay, and, when permissions are being carried, keep the owner-only
    /// permissions they are given while they are filled.
    /// </para>
    /// <para>
    /// <strong>Progress.</strong> <paramref name="progress"/> is given a report after each
    /// directory, file and link is made and each entry is skipped, counting everything up to
    /// and including it, so the counts never go down and the last report equals the one
    /// returned. It is called on the thread doing the copy, before the copy moves on; a
    /// <see cref="Progress{T}"/> passes each report on to the context it was made on instead of
    /// running there. Anything it throws stops the copy, as a failure there would.
    /// </para>
    /// <para>
    /// Safe to call from any thread, and concurrently with other work on either handle; the
    /// copy's own state belongs to the call. Two copies writing into the same destination at
    /// once do not coordinate: without <see cref="CopyOptions.Overwrite"/> each stops at a name
    /// the other took first, and with it a file both write ends up as one copy's file or the
    /// other's, whole. Whatever the interleaving, every read stays inside the source's subtree and every
    /// write inside the destination's.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> There is no path, so nothing is resolved on the way in:
    /// each handle is the directory used, however it was reached. A link inside the source is
    /// recognised by describing its name without following it, and is then refused, skipped or
    /// made again with the same target text, as <see cref="CopyOptions.Symlinks"/> says — never
    /// followed, read through or descended into. Each directory and file is then opened refusing
    /// a link at its name, so one swapped for a link between being described and being opened
    /// is not followed either: it is described again and dealt with as the same setting says,
    /// exactly as it would have been had it been a link all along. A file whose open finds a
    /// named pipe, a socket or a device in its place is not read, and is dealt with as
    /// <see cref="CopyOptions.OtherKinds"/> says. What a
    /// link already sitting at a name in the destination does is set out under
    /// <see cref="CopyOptions.Overwrite"/>.
    /// </para>
    /// <para>
    /// <strong>The two handles may be on different backends.</strong> Everything is read
    /// through the source handle and written through the destination handle, and nothing
    /// from one is ever handed to the other. So a tree held in memory can be filled from one
    /// on disk, or the reverse. A destination on another backend cannot be inside the source,
    /// so that check is not made.
    /// </para>
    /// <para>
    /// <strong>Handles that are not a <see cref="Dir"/>.</strong> Either side may be any
    /// <see cref="IDir"/>, and the copy uses only the interface's members on it. Whether the
    /// destination lies inside the source is decided by comparing identities, which is done
    /// only when both handles can be shown to number their objects the same way: two
    /// <see cref="Dir"/> handles on one filesystem, or two handles of any kind that both report
    /// a backend on the host's own filesystem. Otherwise the check is not made, and a copy into
    /// its own subtree ends when it reaches <see cref="CopyOptions.MaxDepth"/>.
    /// <see cref="CopyOptions.PreservePermissions"/> writes permissions through
    /// <see cref="IDir.SetPermissions"/> and <see cref="ICapFile.SetPermissions"/> on whatever
    /// the destination hands back, and a failure there fails the copy rather than let it
    /// finish without the permissions it was asked to carry.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="CopyOptions.OtherKinds"/> asks for objects to be recreated, which is not
    /// something this can do.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="CopyOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused part of the copy.</exception>
    /// <exception cref="FileNotFoundException">An entry went away while it was being copied.</exception>
    /// <exception cref="CapIOException">
    /// The source holds something the options say to refuse, a destination name is already
    /// taken, the destination is the source or lies inside it, permissions were to be preserved and the
    /// destination would not take them, or the copy failed otherwise.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public static CopyReport CopyTo(
        this IDir dir,
        IDir destination,
        CopyOptions? options = null,
        IProgress<CopyReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CopyOptions settings = Demand(dir, destination, options);

        Copier copier = new(dir, destination, settings, progress, asynchronous: false, cancellationToken);
        return copier.Run(dir);
    }

    /// <summary>
    /// Copies everything beneath this handle into another directory, without holding the
    /// calling thread.
    /// </summary>
    /// <param name="dir">The directory whose contents are copied.</param>
    /// <param name="destination">The directory they are copied into.</param>
    /// <param name="options">What the copy does with what it finds, or null for the defaults.</param>
    /// <param name="progress">
    /// Told what has been copied so far each time an entry is done, or null for no reports.
    /// </param>
    /// <param name="cancellationToken">Stops the copy before the next entry or the next piece of a file.</param>
    /// <returns>A task whose result is what was copied, and what was left out.</returns>
    /// <remarks>
    /// <para>
    /// The same copy as <see cref="CopyTo"/>, with the same treatment of links, of other kinds,
    /// of a destination inside the source, of permissions and times, and the same state left
    /// behind by a failure or a cancellation. What differs is the waiting. Each directory is
    /// read as <see cref="WalkAsync(IDir, WalkOptions?, CancellationToken)"/> reads it, and
    /// file contents are moved with reads and writes the operating system can complete by
    /// itself where it offers that. Opening, creating, describing, renaming and carrying
    /// permissions and times are short calls no platform here performs asynchronously, and
    /// happen on whichever thread the copy resumes on.
    /// </para>
    /// <para>
    /// The arguments and options are checked before the task is made; everything else,
    /// refusing a destination that is the source included, is reported through the task.
    /// </para>
    /// <para>
    /// <strong>Progress</strong> is reported as <see cref="CopyTo"/> reports it, on whichever
    /// thread the copy is running on at the time. Both handles must stay open until the task
    /// completes.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="CopyOptions.OtherKinds"/> asks for objects to be recreated, which is not
    /// something this can do.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="CopyOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused part of the copy.</exception>
    /// <exception cref="FileNotFoundException">An entry went away while it was being copied.</exception>
    /// <exception cref="CapIOException">
    /// The source holds something the options say to refuse, a destination name is already
    /// taken, the destination is the source or lies inside it, permissions were to be preserved and the
    /// destination would not take them, or the copy failed otherwise.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public static Task<CopyReport> CopyToAsync(
        this IDir dir,
        IDir destination,
        CopyOptions? options = null,
        IProgress<CopyReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CopyOptions settings = Demand(dir, destination, options);

        return CopyingAsync(dir, destination, settings, progress, cancellationToken);
    }

    /// <summary>Checks what a copy was given, or supplies the default options.</summary>
    private static CopyOptions Demand(IDir dir, IDir destination, CopyOptions? options)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(destination);

        CopyOptions settings = options ?? CopyOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxDepth, 1, nameof(options));

        if (settings.OtherKinds == CopyAction.Recreate)
        {
            throw new ArgumentException(
                "A named pipe, a socket or a device node cannot be created by this library, " +
                "so they cannot be recreated in a copy. Ask for them to be refused or to be " +
                "skipped.",
                nameof(options));
        }

        return settings;
    }

    /// <summary>The asynchronous copy, once its arguments have been checked.</summary>
    private static async Task<CopyReport> CopyingAsync(
        IDir dir,
        IDir destination,
        CopyOptions settings,
        IProgress<CopyReport>? progress,
        CancellationToken cancellationToken)
    {
        Copier copier = new(dir, destination, settings, progress, asynchronous: true, cancellationToken);
        return await copier.RunAsync(dir).ConfigureAwait(false);
    }

    /// <summary>Reads a file to its end, writing everything read.</summary>
    /// <remarks>
    /// Position by position rather than through a stream, so the two handles keep no
    /// shared state and a short read is handled as what it is: the amount available now,
    /// and not a statement about what follows. The token is looked at before each piece, so a
    /// large file does not hold up a copy that has been asked to stop.
    /// </remarks>
    private static long Transfer(ICapFile source, ICapFile target, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = source.Read(buffer, offset);
                if (read == 0)
                {
                    return offset;
                }

                target.Write(buffer.AsSpan(0, read), offset);
                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads a file to its end without holding the calling thread, writing everything read.</summary>
    /// <remarks>As <see cref="Transfer"/>, a piece at a time and position by position.</remarks>
    private static async ValueTask<long> TransferAsync(
        ICapFile source, ICapFile target, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = await source.ReadAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return offset;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), offset, cancellationToken).ConfigureAwait(false);
                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The state one copy carries while it runs.</summary>
    /// <remarks>
    /// A class rather than a set of parameters threaded through a recursion, because the copy
    /// keeps its own stack of levels: the depth of a tree is decided by whoever built it, and
    /// a copy that called itself per level would meet a tree built to be deep as a stack
    /// overflow, which cannot be caught.
    /// </remarks>
    private sealed class Copier
    {
        /// <summary>What a copied directory is given while it is filled. See <see cref="Guard"/>.</summary>
        private static readonly CapPermissions OwnerOnly = CapPermissions.FromUnixMode(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        private readonly CopyOptions _options;
        private readonly IProgress<CopyReport>? _progress;
        private readonly bool _asynchronous;
        private readonly CancellationToken _cancellationToken;
        private readonly CapFileId? _destinationRoot;
        private readonly List<CopyLevel> _levels = [];

        private int _directories;
        private int _files;
        private int _symlinks;
        private int _skipped;
        private long _bytes;

        public Copier(
            IDir source,
            IDir destination,
            CopyOptions options,
            IProgress<CopyReport>? progress,
            bool asynchronous,
            CancellationToken cancellationToken)
        {
            _options = options;
            _progress = progress;
            _asynchronous = asynchronous;
            _cancellationToken = cancellationToken;

            // A destination on another backend, such as a tree in memory being filled from
            // one on disk, cannot be inside the source, and the two backends number their
            // objects independently, so an identity from one can equal an identity from the
            // other by coincidence. Comparing them would refuse a copy for no reason.
            if (Handles.ShareIdentities(source, destination))
            {
                CapFileId destinationRoot = destination.GetMetadata().FileId;

                // Two handles on one directory. Without replacement the copy would stop at the
                // first directory, finding its own name taken; with it, every file would be
                // rewritten onto itself while the directory holding it was being read.
                if (source.GetMetadata().FileId == destinationRoot)
                {
                    throw new CapIOException(
                        CapErrorKind.InvalidArgument,
                        "The destination is the source directory itself; a copy needs two " +
                        "different directories.");
                }

                _destinationRoot = destinationRoot;
            }

            Destination = destination;
        }

        private IDir Destination { get; }

        public CopyReport Run(IDir source)
        {
            try
            {
                Push(source, Destination, ownsSource: false, ownsDestination: false, name: null, metadata: null);

                while (_levels.Count > 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    CopyLevel level = _levels[^1];
                    if (!level.Reader.MoveNext())
                    {
                        _levels.RemoveAt(_levels.Count - 1);
                        try
                        {
                            Finish(level.Destination, level.SourceMetadata, level.Name);
                        }
                        finally
                        {
                            level.Dispose();
                        }

                        continue;
                    }

                    Copy(level, level.Reader.Current);
                }
            }
            finally
            {
                for (int i = _levels.Count - 1; i >= 0; i--)
                {
                    _levels[i].Dispose();
                }

                _levels.Clear();
            }

            return Report;
        }

        /// <summary>Runs the copy in the form that does not hold the calling thread.</summary>
        /// <remarks>
        /// <see cref="Run"/> step for step, with each directory read and each file's contents
        /// moved asynchronously. Everything else an entry needs is the same code both forms
        /// call, so the two cannot come to disagree about what a copy does.
        /// </remarks>
        public async Task<CopyReport> RunAsync(IDir source)
        {
            try
            {
                Push(source, Destination, ownsSource: false, ownsDestination: false, name: null, metadata: null);

                while (_levels.Count > 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    CopyLevel level = _levels[^1];
                    if (!await level.Reader.MoveNextAsync().ConfigureAwait(false))
                    {
                        _levels.RemoveAt(_levels.Count - 1);
                        try
                        {
                            Finish(level.Destination, level.SourceMetadata, level.Name);
                        }
                        finally
                        {
                            await level.DisposeAsync().ConfigureAwait(false);
                        }

                        continue;
                    }

                    ListedEntry entry = level.Reader.Current;
                    CapMetadata metadata = entry.GetMetadata();

                    if (metadata.Type == CapFileType.File)
                    {
                        await CopyFileAsync(level, entry, metadata).ConfigureAwait(false);
                    }
                    else
                    {
                        CopyOther(level, entry, metadata);
                    }
                }
            }
            finally
            {
                for (int i = _levels.Count - 1; i >= 0; i--)
                {
                    await _levels[i].DisposeAsync().ConfigureAwait(false);
                }

                _levels.Clear();
            }

            return Report;
        }

        /// <summary>What has been copied so far.</summary>
        private CopyReport Report => new(_directories, _files, _symlinks, _skipped, _bytes);

        /// <summary>Tells the caller what has been copied so far, if they asked to be told.</summary>
        private void Reported() => _progress?.Report(Report);

        /// <summary>Copies one entry, by what a fresh description says it is.</summary>
        /// <remarks>
        /// The kind is taken from a description of the name rather than from the directory
        /// read, for two reasons. Several filesystems decline to say what their entries are at
        /// all, so the read's answer is often no answer; and where there is one it describes
        /// an earlier instant, while the permissions the copy may be about to carry across
        /// have to be read now anyway. One lookup answers both.
        /// </remarks>
        private void Copy(CopyLevel level, ListedEntry entry)
        {
            CapMetadata metadata = entry.GetMetadata();

            if (metadata.Type == CapFileType.File)
            {
                CopyFile(level, entry, metadata);
            }
            else
            {
                CopyOther(level, entry, metadata);
            }
        }

        /// <summary>Copies an entry that is not a file: a directory, a link, or anything else.</summary>
        /// <remarks>
        /// Shared by both forms of the copy. Only a file's contents are worth moving
        /// asynchronously; everything done here is a handful of short calls.
        /// </remarks>
        private void CopyOther(CopyLevel level, ListedEntry entry, in CapMetadata metadata)
        {
            switch (metadata.Type)
            {
                case CapFileType.Directory:
                    Descend(level, entry, metadata);
                    break;

                case CapFileType.Symlink:
                    Irregular(level, entry, metadata, _options.Symlinks, recreate: true);
                    break;

                default:
                    Irregular(level, entry, metadata, _options.OtherKinds, recreate: false);
                    break;
            }
        }

        /// <summary>Creates the matching directory in the destination and goes into both.</summary>
        private void Descend(CopyLevel level, ListedEntry entry, in CapMetadata metadata)
        {
            IDir source;
            try
            {
                source = level.Source.OpenDir(entry.Name, noFollow: true);
            }
            catch (CapIOException refusal) when (IsLinkRefusal(refusal))
            {
                if (!CopiedAsLink(level, entry))
                {
                    throw;
                }

                return;
            }

            bool kept = false;
            IDir? target = null;
            try
            {
                if (_destinationRoot is { } destinationRoot && source.GetMetadata().FileId == destinationRoot)
                {
                    throw new CapIOException(
                        CapErrorKind.InvalidArgument,
                        $"'{entry.Name}' is the directory this copy is writing into, so " +
                        $"copying it would copy what the copy had just written. A copy's " +
                        $"destination cannot be inside its source.");
                }

                // A directory at the limit is copied only if it is empty, and is looked into
                // before its copy is made, so a source too deep to copy leaves nothing behind
                // for the directory that was refused.
                bool atLimit = _levels.Count >= _options.MaxDepth;
                if (atLimit && HasEntries(source))
                {
                    throw FailureTranslation.ToException(
                        CapError.FromCategory(CapErrorCategory.PathTooDeep),
                        entry.Name,
                        ExpectedTarget.Directory);
                }

                target = _options.Overwrite
                    ? level.Destination.OpenOrCreateDir(entry.Name)
                    : level.Destination.CreateDir(entry.Name);

                Guard(target, metadata, entry.Name);
                _directories++;
                Reported();

                if (atLimit)
                {
                    // Not entered: it was empty when looked at, and reading it again from a
                    // level past the limit would copy whatever arrived since, at a depth the
                    // caller did not allow.
                    Finish(target, metadata, entry.Name);
                    return;
                }

                Push(source, target, ownsSource: true, ownsDestination: true, entry.Name, metadata);
                kept = true;
            }
            finally
            {
                if (!kept)
                {
                    source.Dispose();
                    target?.Dispose();
                }
            }
        }

        /// <summary>Copies a file's contents into a new file of the same name.</summary>
        /// <remarks>
        /// <para>
        /// The source is opened through the handle that listed it, refusing a name that has
        /// since become a link, and what was opened is asked what it is before anything is read
        /// from it: a refusal to follow a link says nothing about a named pipe or a device
        /// swapped in at the name, and reading one of those is what
        /// <see cref="CopyOptions.OtherKinds"/> exists to prevent.
        /// </para>
        /// <para>
        /// The destination is never opened through its name: without replacement it is created
        /// exclusively, so a taken name stops the copy, and with replacement the copy is made
        /// under a scratch name and moved onto the real one. Either way nothing already at the
        /// name is written through, so a link there cannot steer the contents into whatever it
        /// points at.
        /// </para>
        /// <para>
        /// A file that is not finished — the copy failed or was cancelled while writing it or
        /// carrying its permissions and times — is removed, under whichever name it was being
        /// written, so what a stopped copy leaves never includes a partly written file.
        /// </para>
        /// </remarks>
        private void CopyFile(CopyLevel level, ListedEntry entry, in CapMetadata metadata)
        {
            using ICapFile? source = OpenSource(level, entry);
            if (source is null)
            {
                return;
            }

            ICapFile target = Begin(level.Destination, entry.Name, out string? scratch);
            bool placed = false;
            try
            {
                using (target)
                {
                    _bytes += Transfer(source, target, _cancellationToken);
                    Fill(target, metadata, entry.Name);
                }

                Place(level.Destination, scratch, entry.Name);
                placed = true;
            }
            finally
            {
                if (!placed)
                {
                    Discard(level.Destination, scratch, entry.Name);
                }
            }

            _files++;
            Reported();
        }

        /// <summary>Copies a file's contents into a new file of the same name, without holding the thread.</summary>
        /// <remarks><see cref="CopyFile"/>, with the contents moved asynchronously.</remarks>
        private async ValueTask CopyFileAsync(CopyLevel level, ListedEntry entry, CapMetadata metadata)
        {
            using ICapFile? source = OpenSource(level, entry);
            if (source is null)
            {
                return;
            }

            ICapFile target = Begin(level.Destination, entry.Name, out string? scratch);
            bool placed = false;
            try
            {
                using (target)
                {
                    _bytes += await TransferAsync(source, target, _cancellationToken).ConfigureAwait(false);
                    Fill(target, metadata, entry.Name);
                }

                Place(level.Destination, scratch, entry.Name);
                placed = true;
            }
            finally
            {
                if (!placed)
                {
                    Discard(level.Destination, scratch, entry.Name);
                }
            }

            _files++;
            Reported();
        }

        /// <summary>
        /// Opens a file in the source for copying, or deals with the name as what it turned out
        /// to be instead.
        /// </summary>
        /// <returns>
        /// The open file, or null when the name was a link or another kind by the time it was
        /// opened and has been dealt with as the options say.
        /// </returns>
        private ICapFile? OpenSource(CopyLevel level, ListedEntry entry)
        {
            FileOptions options = _asynchronous
                ? FileOptions.SequentialScan | FileOptions.Asynchronous
                : FileOptions.SequentialScan;

            ICapFile opened;
            try
            {
                opened = level.Source.OpenFile(
                    entry.Name,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    options,
                    0,
                    append: false,
                    noFollow: true);
            }
            catch (CapIOException refusal) when (IsLinkRefusal(refusal))
            {
                if (!CopiedAsLink(level, entry))
                {
                    throw;
                }

                return null;
            }

            bool kept = false;
            try
            {
                CapMetadata held = opened.GetMetadata();
                if (held.Type != CapFileType.File)
                {
                    Irregular(level, entry, held, _options.OtherKinds, recreate: false);
                    return null;
                }

                kept = true;
                return opened;
            }
            finally
            {
                if (!kept)
                {
                    opened.Dispose();
                }
            }
        }

        /// <summary>Creates the file a copied file's contents are written into.</summary>
        /// <param name="directory">The directory the file is copied into.</param>
        /// <param name="name">The name it is to have there.</param>
        /// <param name="scratch">
        /// The scratch name it was created under, to be moved onto <paramref name="name"/> once
        /// it is written; null when it was created under <paramref name="name"/> itself.
        /// </param>
        /// <remarks>
        /// <para>
        /// Without replacement the file is created exclusively under its real name. With it, a
        /// directory at the name is refused before anything is written — the move would refuse
        /// it too, but each platform reports that in its own way, and a copy that has been told
        /// to replace files still has no business deciding to replace a directory — and the
        /// file is created under a scratch name beside it.
        /// </para>
        /// <para>
        /// A move acts on the name, not on what the name refers to, so whatever was there — a
        /// file, or a link to anything at all — is replaced and never written through. A link
        /// is gone afterwards and its target is left exactly as it was.
        /// </para>
        /// </remarks>
        private ICapFile Begin(IDir directory, string name, out string? scratch)
        {
            if (!_options.Overwrite)
            {
                scratch = null;
                return _asynchronous
                    ? directory.OpenFile(name, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous)
                    : directory.CreateNewFile(name);
            }

            if (directory.TryGetMetadata(name, out CapMetadata existing) &&
                existing.Type == CapFileType.Directory)
            {
                throw new CapIOException(
                    CapErrorKind.IsADirectory,
                    $"'{name}' is a file in the source and a directory in the destination. A " +
                    $"copy replaces files, not directories with files; remove the directory " +
                    $"first if it is meant to go.");
            }

            scratch = Claim(directory, _asynchronous, ownerOnly: false, out ICapFile target);
            return target;
        }

        /// <summary>Moves a file written under a scratch name onto its real name.</summary>
        private static void Place(IDir directory, string? scratch, string name)
        {
            if (scratch is not null)
            {
                MoveOnto(directory, scratch, name);
            }
        }

        /// <summary>Removes a file the copy did not finish, under whichever name it was written.</summary>
        /// <remarks>
        /// Failures are dropped, as <see cref="Abandon"/> drops them: this runs from a
        /// <c>finally</c>, where the exception worth reporting is the one already on its way out.
        /// A file created under its real name was created exclusively by this copy, so the name
        /// removed is one the copy made.
        /// </remarks>
        private static void Discard(IDir directory, string? scratch, string name) =>
            _ = directory.TryDeleteFile(scratch ?? name);

        /// <summary>
        /// Gives a file whose contents have been written its permissions and times, if asked.
        /// </summary>
        /// <remarks>
        /// The times are set last, after every write, so that nothing written afterwards moves
        /// them on again.
        /// </remarks>
        private void Fill(ICapFile target, in CapMetadata metadata, string name)
        {
            if (_options.PreservePermissions)
            {
                if (target is CapFile file)
                {
                    Demand(file.SetPermissionsCore(metadata.Permissions), name);
                }
                else
                {
                    target.SetPermissions(metadata.Permissions);
                }
            }

            if (_options.PreserveTimes)
            {
                target.SetTimes(
                    Accessed(metadata), CapFileTime.At(metadata.LastWriteTime));
            }
        }

        /// <summary>Deals with an entry that is neither a file nor a directory.</summary>
        private void Irregular(
            CopyLevel level,
            ListedEntry entry,
            in CapMetadata metadata,
            CopyAction action,
            bool recreate)
        {
            switch (action)
            {
                case CopyAction.Skip:
                    _skipped++;
                    Reported();
                    return;

                case CopyAction.Recreate when recreate:
                    Relink(level, entry, metadata);
                    return;

                default:
                    throw new CapIOException(
                        CapErrorKind.NotSupported,
                        $"'{entry.Name}' is a {metadata.Type} and a copy has no faithful " +
                        $"equivalent for one. It is not followed and not read: doing either " +
                        $"would reach outside the tree being copied, or would block on " +
                        $"something that is not storage. Ask for entries of this kind to be " +
                        $"skipped, or remove it from the source.");
            }
        }

        /// <summary>
        /// Deals with a name that was described as a directory or a file and had become a link
        /// by the time it was opened, as <see cref="CopyOptions.Symlinks"/> says to deal with any
        /// link.
        /// </summary>
        /// <returns>
        /// False when a fresh description does not say the name is a link either — it changed
        /// again, or went away — so the refusal that led here is the caller's to report.
        /// </returns>
        /// <remarks>
        /// Asked again rather than assumed, because the refusal is only the open's account of
        /// the name, and a link is made again from what the name holds now. A copy's answer to
        /// a link then does not depend on whether the link was there before the copy looked or
        /// arrived while it was looking.
        /// </remarks>
        private bool CopiedAsLink(CopyLevel level, ListedEntry entry)
        {
            if (!entry.TryGetMetadata(out CapMetadata now) || now.Type != CapFileType.Symlink)
            {
                return false;
            }

            Irregular(level, entry, now, _options.Symlinks, recreate: true);
            return true;
        }

        /// <summary>Whether an open that refuses a final link failed because there was one.</summary>
        /// <remarks>
        /// Each backend names the refusal in its own way: the kernel's answer to an open that
        /// will not follow a link is the one it gives for a link it will not follow, and a
        /// backend that looks at the name first reports it as the link it found.
        /// </remarks>
        private static bool IsLinkRefusal(CapIOException refusal) =>
            refusal.Kind is CapErrorKind.LinkNotFollowed or CapErrorKind.SymbolicLink;

        /// <summary>Creates a link in the destination holding the same target text.</summary>
        /// <remarks>
        /// <para>
        /// The text is read from the link and written to the new one unchanged. Nothing
        /// resolves it, at either end: it is data that whatever wrote the link chose, it means
        /// whatever it means from wherever the new link ends up, and containment is enforced
        /// when something follows it rather than when it is created.
        /// </para>
        /// <para>
        /// The one exception is a rooted target, which no link beneath a handle may store, so
        /// the copy fails there rather than leaving the link out. It is refused before a name
        /// taken in the destination is cleared, so that a copy that cannot make the link does
        /// not first remove what was there.
        /// </para>
        /// <para>
        /// Windows records which kind of object a link expects to find, and a link created as
        /// the wrong kind cannot be traversed at all — so the source link's own directory flag
        /// decides which kind is made. Everywhere else links are untyped and the flag is
        /// absent, which is the same answer arrived at by asking.
        /// </para>
        /// </remarks>
        private void Relink(CopyLevel level, ListedEntry entry, in CapMetadata metadata)
        {
            string target = level.Source.ReadLink(entry.Name);

            if (CapPath.IsRooted(target, Handles.SyntaxOf(level.Destination)))
            {
                throw new SandboxEscapeException(
                    $"'{entry.Name}' is a symbolic link to '{target}', which is rooted, and a link " +
                    $"beneath a handle cannot store a rooted target, so it cannot be made again in " +
                    $"the destination.");
            }

            if (_options.Overwrite)
            {
                // Removed as a name rather than replaced through one: what is there may be a
                // link of the other kind, which no creation would write over.
                _ = level.Destination.TryDeleteFile(entry.Name);
            }

            bool namesDirectory =
                metadata.Permissions.TryGetWindowsAttributes(out FileAttributes attributes) &&
                (attributes & FileAttributes.Directory) != 0;

            if (namesDirectory)
            {
                level.Destination.CreateDirSymlink(entry.Name, target);
            }
            else
            {
                level.Destination.CreateSymlink(entry.Name, target);
            }

            if (_options.PreserveTimes)
            {
                level.Destination.SetTimes(
                    entry.Name, Accessed(metadata), CapFileTime.At(metadata.LastWriteTime));
            }

            _symlinks++;
            Reported();
        }

        /// <summary>
        /// Closes a copied directory to everyone but its owner while it is filled, if
        /// permissions are to be carried across.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The source's own permissions are not given to the directory until everything inside
        /// it has been copied: a source directory its owner cannot write to would otherwise
        /// give a copy the copy itself could not write into. Until then a directory whose
        /// source records Unix mode bits is left readable, writable and searchable by its owner
        /// alone, as <c>cp -p</c> leaves one, so a private source directory is never open to
        /// anyone else while its contents are arriving.
        /// </para>
        /// <para>
        /// Attribute flags are left alone until the end. None of them stops entries being
        /// created in a directory, and none of them keeps anyone out.
        /// </para>
        /// </remarks>
        private void Guard(IDir target, in CapMetadata metadata, string name)
        {
            if (_options.PreservePermissions && metadata.Permissions.TryGetUnixMode(out _))
            {
                Apply(target, OwnerOnly, name);
            }
        }

        /// <summary>
        /// Gives a copied directory the source's permissions and times, if asked, once
        /// everything inside it has been copied.
        /// </summary>
        /// <remarks>
        /// Left until the directory is finished because every entry created in it moves its
        /// last-write time on, and because the source's permissions may not let the copy
        /// create those entries. Permissions go first, so that the times are the last thing
        /// written. The directory the copy writes into has neither to carry, since the copy
        /// does not reproduce it.
        /// </remarks>
        private void Finish(IDir destination, CapMetadata? metadata, string? name)
        {
            if (metadata is not { } source || name is null)
            {
                return;
            }

            if (_options.PreservePermissions)
            {
                Apply(destination, source.Permissions, name);
            }

            if (_options.PreserveTimes)
            {
                destination.SetTimes(
                    Accessed(source), CapFileTime.At(source.LastWriteTime));
            }
        }

        /// <summary>Writes permissions onto a copied directory.</summary>
        /// <remarks>
        /// A <see cref="Dir"/> is asked for the platform's own answer, so that a refusal names
        /// the entry being copied. Any other handle is asked through the interface, and a
        /// refusal is whatever that implementation throws.
        /// </remarks>
        private static void Apply(IDir target, in CapPermissions permissions, string name)
        {
            if (target is Dir directory)
            {
                Demand(directory.SetPermissionsCore(permissions), name);
            }
            else
            {
                target.SetPermissions(permissions);
            }
        }

        /// <summary>The access time to give a copy: the source's, or none where it has none.</summary>
        /// <remarks>
        /// A source on a filesystem that keeps no access times has nothing to carry over, and
        /// leaving the copy's own in place is closer to that than stamping it with a date.
        /// </remarks>
        private static CapFileTime Accessed(in CapMetadata metadata) =>
            metadata.LastAccessTime is { } accessed ? CapFileTime.At(accessed) : CapFileTime.Unchanged;

        /// <summary>Whether a directory has anything in it.</summary>
        private static bool HasEntries(IDir directory)
        {
            EntryReader reader = EntryReader.Open(directory);
            try
            {
                return reader.MoveNext();
            }
            finally
            {
                reader.Dispose();
            }
        }

        /// <summary>Reports a refusal to carry permissions across.</summary>
        /// <remarks>
        /// Reported rather than ignored. A caller who asked for permissions to be preserved
        /// asked because the answer matters — a private file that arrives readable by everyone
        /// is the failure this option exists to prevent — so a copy that could not do it says
        /// so instead of finishing and looking successful.
        /// </remarks>
        private static void Demand(CapError error, string name)
        {
            if (error.IsFailure)
            {
                throw FailureTranslation.ToException(error, name, ExpectedTarget.Name);
            }
        }

        private void Push(
            IDir source, IDir destination, bool ownsSource, bool ownsDestination, string? name, CapMetadata? metadata) =>
            _levels.Add(new CopyLevel(
                source,
                destination,
                ownsSource,
                ownsDestination,
                name,
                metadata,
                _asynchronous ? EntryReader.OpenAsync(source, _cancellationToken) : EntryReader.Open(source)));
    }

    /// <summary>One pair of open directories the copy is working between.</summary>
    private sealed class CopyLevel : IDisposable
    {
        private readonly bool _ownsSource;
        private readonly bool _ownsDestination;

        public CopyLevel(
            IDir source,
            IDir destination,
            bool ownsSource,
            bool ownsDestination,
            string? name,
            CapMetadata? sourceMetadata,
            EntryReader reader)
        {
            Source = source;
            Destination = destination;
            Name = name;
            SourceMetadata = sourceMetadata;
            _ownsSource = ownsSource;
            _ownsDestination = ownsDestination;
            Reader = reader;
        }

        /// <summary>The directory being read.</summary>
        public IDir Source { get; }

        /// <summary>The directory being written.</summary>
        public IDir Destination { get; }

        /// <summary>
        /// The directory's name in its parent, for reporting a failure to finish it. Null for
        /// the directory the copy writes into.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// The source directory's description, whose permissions and times the destination is
        /// given when it is finished. Null for the directory the copy writes into.
        /// </summary>
        public CapMetadata? SourceMetadata { get; }

        /// <summary>The reading in progress.</summary>
        public EntryReader Reader { get; }

        /// <summary>Stops a reading made on the calling thread and closes what this level opened.</summary>
        public void Dispose()
        {
            Reader.Dispose();
            Close();
        }

        /// <summary>Stops a reading of either form, waiting for it to stop, and closes what this level opened.</summary>
        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync().ConfigureAwait(false);
            Close();
        }

        private void Close()
        {
            if (_ownsSource)
            {
                Source.Dispose();
            }

            if (_ownsDestination)
            {
                Destination.Dispose();
            }
        }
    }
}
