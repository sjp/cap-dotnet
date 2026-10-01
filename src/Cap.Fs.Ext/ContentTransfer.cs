using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Moves a file's contents into another file, by the quickest means the two have in common.
/// </summary>
/// <remarks>
/// <para>
/// In order of preference: the destination is made to share the source's storage, which takes
/// the same time whatever the length; failing that, the kernel copies the contents a piece at
/// a time without bringing them into the process; failing that, they are read and written
/// through a buffer, position by position. Which of the first two is available depends on the
/// platform and on the filesystems at both ends, and the only way to find out is to try: a
/// failure of either is never reported, because the reads and writes after it will meet, and
/// report, whatever real fault there is.
/// </para>
/// <para>
/// One is made per copy and kept for the whole of it. A shortcut the platform says it does not
/// have at all is not tried again for later files, so a tree of small files on a filesystem
/// without one costs one refused call rather than one per file. A refusal for any other reason
/// is about that file, and the next one tries again.
/// </para>
/// <para>
/// Not safe for concurrent use: a copy moves one file at a time.
/// </para>
/// </remarks>
internal sealed class ContentTransfer
{
    /// <summary>
    /// The size of the buffer contents are read and written through when no shortcut is taken.
    /// </summary>
    /// <remarks>
    /// Large enough that a big file is not copied in thousands of round trips, small enough
    /// that the buffer comes from the shared pool rather than from the heap segment reserved
    /// for large objects — which it would if it were any larger, making every copy a source of
    /// collections that cannot be compacted.
    /// </remarks>
    private const int BufferSize = 64 * 1024;

    /// <summary>The most one kernel copy is asked to move.</summary>
    /// <remarks>
    /// The cancellation token is looked at between pieces, so this bounds how long a copy that
    /// has been asked to stop carries on, and how long a piece holds a thread in an
    /// asynchronous copy. Large enough that the calls cost nothing against the bytes moved.
    /// </remarks>
    private const long KernelPieceBytes = 8L * 1024 * 1024;

    private readonly bool _keepHoles;
    private bool _noClone;
    private bool _noCloneByName;
    private bool _noRangeCopy;

    /// <summary>Prepares a transfer for one copy.</summary>
    /// <param name="keepHoles">
    /// Whether ranges the source stores nothing for are left as holes in the destination, as
    /// <see cref="CopyOptions.PreserveSparseness"/> describes.
    /// </param>
    public ContentTransfer(bool keepHoles) => _keepHoles = keepHoles;

    /// <summary>Copies a file's contents into another, from the start to the source's end.</summary>
    /// <param name="source">The file read from.</param>
    /// <param name="target">The file written to, which the copy created and which is empty.</param>
    /// <param name="expectedLength">
    /// How long the source was when it was described, used to reserve room in the
    /// destination. The copy runs to the source's end whatever it is by then.
    /// </param>
    /// <param name="cancellationToken">Looked at before each piece.</param>
    /// <returns>The length the destination ends up with: the bytes it reads as.</returns>
    public long Transfer(ICapFile source, ICapFile target, long expectedLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryClone(source, target, out long cloned))
        {
            return cloned;
        }

        Pass pass = new(this, source, target, expectedLength);
        byte[]? buffer = null;
        try
        {
            long position = 0;
            while (pass.Next(position, out long start, out long stop))
            {
                position = start;
                while (position < stop)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pass.TryKernel(position, stop, out long copied))
                    {
                        position += copied;
                        continue;
                    }

                    pass.Buffering(position);
                    buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
                    int read = source.Read(buffer.AsSpan(0, Piece(buffer, position, stop)), position);
                    if (read == 0)
                    {
                        return pass.Finish(position);
                    }

                    target.Write(buffer.AsSpan(0, read), position);
                    position += read;
                }
            }

            return pass.Finish(position);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Copies a file's contents into another without holding the calling thread while they
    /// are read and written.
    /// </summary>
    /// <remarks>
    /// As <see cref="Transfer"/>. A shortcut is a call the system finishes before returning,
    /// so one holds the thread for as long as it takes: an instant for shared storage, and at
    /// most one piece's worth for a kernel copy. The reads and writes are asynchronous.
    /// </remarks>
    public async ValueTask<long> TransferAsync(
        ICapFile source, ICapFile target, long expectedLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryClone(source, target, out long cloned))
        {
            return cloned;
        }

        Pass pass = new(this, source, target, expectedLength);
        byte[]? buffer = null;
        try
        {
            long position = 0;
            while (pass.Next(position, out long start, out long stop))
            {
                position = start;
                while (position < stop)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pass.TryKernel(position, stop, out long copied))
                    {
                        position += copied;
                        continue;
                    }

                    pass.Buffering(position);
                    buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
                    int read = await source
                        .ReadAsync(buffer.AsMemory(0, Piece(buffer, position, stop)), position, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return pass.Finish(position);
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), position, cancellationToken).ConfigureAwait(false);
                    position += read;
                }
            }

            return pass.Finish(position);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Creates <paramref name="name"/> beneath <paramref name="directory"/> as a clone of
    /// <paramref name="source"/>, where this platform clones by making a new name.
    /// </summary>
    /// <returns>
    /// The new file, open for writing and already holding the source's contents; or null when
    /// no clone was made, and the file is to be created and filled as any other.
    /// </returns>
    /// <remarks>
    /// macOS alone clones this way, and the clone carries the source's mode, so it is for a
    /// copy that gives the destination the source's permissions anyway. A name already taken
    /// is not reported here: the creation that follows is exclusive too, and says so in its
    /// own words.
    /// </remarks>
    public ICapFile? CloneNew(IDir directory, string name, ICapFile source, bool asynchronous)
    {
        if (_noCloneByName || source is not CapFile from || directory is not Dir dir)
        {
            return null;
        }

        CapError cloned = dir.CloneFile(from, name, Options(asynchronous), out CapFile? created);
        if (cloned.IsSuccess)
        {
            return created;
        }

        Declined(cloned, ref _noCloneByName);
        return null;
    }

    /// <summary>
    /// Creates a file under a fresh scratch name beneath <paramref name="directory"/> as a
    /// clone of <paramref name="source"/>, as <see cref="CloneNew"/> does under a given one.
    /// </summary>
    /// <returns>False when no clone was made.</returns>
    public bool TryCloneScratch(
        IDir directory,
        ICapFile source,
        bool asynchronous,
        [NotNullWhen(true)] out string? scratch,
        [NotNullWhen(true)] out ICapFile? file)
    {
        scratch = null;
        file = null;
        if (_noCloneByName || source is not CapFile from || directory is not Dir dir)
        {
            return false;
        }

        for (int attempt = 0; attempt < TemporaryNames.Attempts; attempt++)
        {
            string candidate = TemporaryNames.Next();
            CapError cloned = dir.CloneFile(from, candidate, Options(asynchronous), out CapFile? created);
            if (cloned.IsSuccess && created is not null)
            {
                scratch = candidate;
                file = created;
                return true;
            }

            if (cloned.Category != CapErrorCategory.AlreadyExists)
            {
                Declined(cloned, ref _noCloneByName);
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Finishes a file made by <see cref="CloneNew"/> or <see cref="TryCloneScratch"/>, which
    /// already holds the source's contents.
    /// </summary>
    /// <param name="clone">The clone.</param>
    /// <param name="keepTimes">
    /// Whether the copy carries the source's times, which it sets afterwards. Otherwise the
    /// clone is given the time it was made, as a file written now would have, rather than
    /// whatever the clone took from its source.
    /// </param>
    /// <returns>Its length, which is what the copy counts as written.</returns>
    public static long Cloned(ICapFile clone, bool keepTimes)
    {
        if (!keepTimes)
        {
            clone.SetTimes(CapFileTime.Now, CapFileTime.Now);
        }

        return ((CapFile)clone).Length;
    }

    /// <summary>Makes the destination share the source's storage, where both can.</summary>
    private bool TryClone(ICapFile source, ICapFile target, out long length)
    {
        length = 0;
        if (_noClone || source is not CapFile from || target is not CapFile to)
        {
            return false;
        }

        CapError cloned = from.CloneContentsTo(to);
        if (cloned.IsSuccess)
        {
            length = to.Length;
            return true;
        }

        Declined(cloned, ref _noClone);
        return false;
    }

    /// <summary>Stops trying a shortcut for the rest of the copy when the platform has none.</summary>
    private static void Declined(CapError error, ref bool never)
    {
        if (error.Category == CapErrorCategory.NotSupported)
        {
            never = true;
        }
    }

    private static FileOptions Options(bool asynchronous) =>
        asynchronous ? FileOptions.Asynchronous : FileOptions.None;

    /// <summary>How much of the buffer a read at <paramref name="position"/> may fill.</summary>
    private static int Piece(byte[] buffer, long position, long stop) =>
        (int)Math.Min(buffer.Length, stop - position);

    /// <summary>The state of one file's transfer, shared by the two loops that drive it.</summary>
    private sealed class Pass
    {
        private readonly ContentTransfer _owner;
        private readonly ICapFile _target;
        private readonly CapFile? _from;
        private readonly CapFile? _to;
        private readonly long _expectedLength;
        private bool _holes;
        private bool _kernel;
        private bool _reserved;
        private bool _markedSparse;

        public Pass(ContentTransfer owner, ICapFile source, ICapFile target, long expectedLength)
        {
            _owner = owner;
            _target = target;
            _from = source as CapFile;
            _to = target as CapFile;
            _expectedLength = expectedLength;
            _holes = owner._keepHoles && _from is not null;
            _kernel = !owner._noRangeCopy && _from is not null && _to is not null;
        }

        /// <summary>The next stretch of the source to copy, from <paramref name="position"/>.</summary>
        /// <returns>False when nothing from <paramref name="position"/> on holds data.</returns>
        /// <remarks>
        /// The rest of the file, unless holes are being kept, in which case it is the next
        /// stretch the source stores data for. A source that cannot say where its data is,
        /// at any point, has the rest copied whole.
        /// </remarks>
        public bool Next(long position, out long start, out long stop)
        {
            start = position;
            stop = long.MaxValue;
            if (!_holes)
            {
                return true;
            }

            CapError found = _from!.FindData(position, out long data, out long end);

            // A stretch that ends where it starts, or before where the copy has reached, is an
            // answer no filesystem should give; followed, it would be asked about again for ever.
            if (found.IsFailure || (data >= 0 && (data < position || end <= data)))
            {
                _holes = false;
                return true;
            }

            if (data < 0)
            {
                return false;
            }

            if (data > position)
            {
                Skipping();
            }

            start = data;
            stop = end;
            return true;
        }

        /// <summary>Copies a piece inside the kernel.</summary>
        /// <returns>False when the piece is to be read and written instead.</returns>
        /// <remarks>
        /// A refusal, or a piece that copied nothing, ends kernel copies for this file. A
        /// piece that copied nothing is usually the end of the source, but some filesystems —
        /// <c>/proc</c>, <c>/sys</c>, some FUSE ones — report nothing copied from a file they
        /// do not know the length of, so the read that follows is what decides.
        /// </remarks>
        public bool TryKernel(long position, long stop, out long copied)
        {
            copied = 0;
            if (!_kernel)
            {
                return false;
            }

            CapError result = _from!.CopyRangeTo(_to!, position, Math.Min(stop - position, KernelPieceBytes), out copied);
            if (result.IsSuccess && copied > 0)
            {
                return true;
            }

            _kernel = false;
            Declined(result, ref _owner._noRangeCopy);
            return false;
        }

        /// <summary>Called before each piece that is read and written through the buffer.</summary>
        /// <remarks>
        /// The first time, reserves room for the rest of the file in one piece, so that it is
        /// not claimed a write at a time. Not while keeping holes, which a reservation would
        /// fill, and not where a kernel copy already reached the expected length. A refusal
        /// is ignored: the reservation is only advice, and a write that then finds no room
        /// says so itself.
        /// </remarks>
        public void Buffering(long position)
        {
            if (_reserved)
            {
                return;
            }

            _reserved = true;
            if (!_holes && _to is not null && _expectedLength > position)
            {
                _ = _to.Reserve(_expectedLength);
            }
        }

        /// <summary>Completes the destination once the source's end has been reached.</summary>
        /// <returns>The destination's length.</returns>
        /// <remarks>
        /// A source that ends in a hole has nothing written for it, so the destination is
        /// given the source's length, which leaves the same hole at its end.
        /// </remarks>
        public long Finish(long position)
        {
            if (!_holes)
            {
                return position;
            }

            long length = _from!.Length;
            if (length <= position)
            {
                return position;
            }

            Skipping();
            _target.SetLength(length);
            return length;
        }

        /// <summary>
        /// Called before a hole is left in the destination, so that a destination that needs
        /// to be told keeps it as one.
        /// </summary>
        private void Skipping()
        {
            if (!_markedSparse)
            {
                _markedSparse = true;
                _ = _to?.MarkSparse();
            }
        }
    }
}
