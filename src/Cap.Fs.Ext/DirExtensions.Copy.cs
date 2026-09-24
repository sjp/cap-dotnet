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
    /// <returns>What was copied, and what was left out.</returns>
    /// <remarks>
    /// <para>
    /// The contents are copied, not the directory itself: entries directly inside
    /// <paramref name="dir"/> become entries directly inside <paramref name="destination"/>.
    /// A caller who wants the source to appear as a named directory inside the destination
    /// creates that directory and passes a handle on it.
    /// </para>
    /// <para>
    /// <strong>The destination must not be inside the source.</strong> A copy into its own
    /// subtree would copy what it had just written, without end, so it is refused as soon as
    /// the copy reaches the directory in question rather than being allowed to run. The
    /// reverse — a source inside the destination — is an ordinary copy and is allowed.
    /// </para>
    /// <para>
    /// <strong>It is not atomic and it is not a snapshot.</strong> A source that is being
    /// changed while the copy runs is copied partly as it was and partly as it became, and a
    /// failure part of the way through leaves the destination holding what had been copied
    /// until then.
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
    /// followed, read through or descended into. A directory or file in the source that is
    /// swapped for a link between being described and being opened is opened as the source
    /// handle's policy would follow that link, which never leaves the source's subtree. What a
    /// link already sitting at a name in the destination does is set out under
    /// <see cref="CopyOptions.Overwrite"/>.
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
    /// taken, the destination lies inside the source, or the copy failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public static CopyReport CopyTo(this Dir dir, Dir destination, CopyOptions? options = null)
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

        Copier copier = new(destination, settings);
        return copier.Run(dir);
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
        private readonly CopyOptions _options;
        private readonly CapFileId _destinationRoot;
        private readonly List<CopyLevel> _levels = [];

        private int _directories;
        private int _files;
        private int _symlinks;
        private int _skipped;
        private long _bytes;

        public Copier(Dir destination, CopyOptions options)
        {
            _options = options;
            _destinationRoot = destination.GetMetadata().FileId;
            Destination = destination;
        }

        private Dir Destination { get; }

        public CopyReport Run(Dir source)
        {
            try
            {
                Push(source, Destination, ownsSource: false, ownsDestination: false);

                while (_levels.Count > 0)
                {
                    CopyLevel level = _levels[^1];
                    if (!level.Entries.MoveNext())
                    {
                        _levels.RemoveAt(_levels.Count - 1);
                        level.Dispose();
                        continue;
                    }

                    Copy(level, level.Entries.Current);
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

            return new CopyReport(_directories, _files, _symlinks, _skipped, _bytes);
        }

        /// <summary>Copies one entry, by what a fresh description says it is.</summary>
        /// <remarks>
        /// The kind is taken from a description of the name rather than from the directory
        /// read, for two reasons. Several filesystems decline to say what their entries are at
        /// all, so the read's answer is often no answer; and where there is one it describes
        /// an earlier instant, while the permissions the copy may be about to carry across
        /// have to be read now anyway. One lookup answers both.
        /// </remarks>
        private void Copy(CopyLevel level, DirEntry entry)
        {
            CapMetadata metadata = entry.GetMetadata();

            switch (metadata.Type)
            {
                case CapFileType.Directory:
                    Descend(level, entry, metadata);
                    break;

                case CapFileType.File:
                    CopyFile(level, entry, metadata);
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
        private void Descend(CopyLevel level, DirEntry entry, in CapMetadata metadata)
        {
            Dir source = entry.OpenDir();

            bool kept = false;
            Dir? target = null;
            try
            {
                if (source.GetMetadata().FileId == _destinationRoot)
                {
                    throw new CapIOException(
                        CapErrorKind.InvalidArgument,
                        $"'{entry.Name}' is the directory this copy is writing into, so " +
                        $"copying it would copy what the copy had just written. A copy's " +
                        $"destination cannot be inside its source.");
                }

                if (_levels.Count >= _options.MaxDepth)
                {
                    throw FailureTranslation.ToException(
                        CapError.FromCategory(CapErrorCategory.PathTooDeep),
                        entry.Name,
                        ExpectedTarget.Directory);
                }

                target = _options.Overwrite
                    ? level.Destination.OpenOrCreateDir(entry.Name)
                    : level.Destination.CreateDir(entry.Name);

                Apply(target, metadata, entry.Name);
                _directories++;

                Push(source, target, ownsSource: true, ownsDestination: true);
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
        /// The source is opened from the entry, so it is opened through the handle that listed
        /// it and the open refuses a name that has since become a link. The destination is
        /// never opened through its name: without replacement it is created exclusively, so a
        /// taken name stops the copy, and with replacement the copy is made under a scratch
        /// name and moved onto the real one. Either way nothing already at the name is written
        /// through, so a link there cannot steer the contents into whatever it points at.
        /// </remarks>
        private void CopyFile(CopyLevel level, DirEntry entry, in CapMetadata metadata)
        {
            using CapFile source = entry.OpenFile(
                FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan, 0);

            if (_options.Overwrite)
            {
                Replace(level.Destination, entry.Name, source, metadata);
            }
            else
            {
                using CapFile target = level.Destination.CreateNewFile(entry.Name);
                Fill(source, target, metadata, entry.Name);
            }

            _files++;
        }

        /// <summary>
        /// Writes a file under a scratch name beside the destination name, then moves it onto
        /// that name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A move acts on the name, not on what the name refers to, so whatever was there — a
        /// file, or a link to anything at all — is replaced and never written through. A link
        /// is gone afterwards and its target is left exactly as it was.
        /// </para>
        /// <para>
        /// A directory at the name is refused before anything is written. The move would
        /// refuse it too, but each platform reports that in its own way, and a copy that has
        /// been told to replace files still has no business deciding to replace a directory.
        /// </para>
        /// </remarks>
        private void Replace(Dir directory, string name, CapFile source, in CapMetadata metadata)
        {
            if (directory.TryGetMetadata(name, out CapMetadata existing) &&
                existing.Type == CapFileType.Directory)
            {
                throw new CapIOException(
                    CapErrorKind.IsADirectory,
                    $"'{name}' is a file in the source and a directory in the destination. A " +
                    $"copy replaces files, not directories with files; remove the directory " +
                    $"first if it is meant to go.");
            }

            string? scratch = Claim(directory, asynchronous: false, out CapFile target);
            try
            {
                using (target)
                {
                    Fill(source, target, metadata, name);
                }

                directory.Rename(scratch, directory, name, replaceExisting: true);
                scratch = null;
            }
            finally
            {
                Abandon(directory, scratch);
            }
        }

        /// <summary>Writes the source's contents, and its permissions if asked, into a file.</summary>
        private void Fill(CapFile source, CapFile target, in CapMetadata metadata, string name)
        {
            _bytes += Transfer(source, target);

            if (_options.PreservePermissions)
            {
                Demand(target.SetPermissions(metadata.Permissions), name);
            }
        }

        /// <summary>Deals with an entry that is neither a file nor a directory.</summary>
        private void Irregular(
            CopyLevel level,
            DirEntry entry,
            in CapMetadata metadata,
            CopyAction action,
            bool recreate)
        {
            switch (action)
            {
                case CopyAction.Skip:
                    _skipped++;
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
        private void Relink(CopyLevel level, DirEntry entry, in CapMetadata metadata)
        {
            string target = level.Source.ReadLink(entry.Name);

            if (CapPath.IsRooted(target, CapPath.HostSyntax))
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

            _symlinks++;
        }

        /// <summary>Carries the source's permissions onto a copied directory, if asked.</summary>
        private void Apply(Dir target, in CapMetadata metadata, string name)
        {
            if (_options.PreservePermissions)
            {
                Demand(target.SetPermissions(metadata.Permissions), name);
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

        /// <summary>Reads a file to its end, writing everything read.</summary>
        /// <remarks>
        /// Position by position rather than through a stream, so the two handles keep no
        /// shared state and a short read is handled as what it is: the amount available now,
        /// and not a statement about what follows.
        /// </remarks>
        private static long Transfer(CapFile source, CapFile target)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
            try
            {
                long offset = 0;
                while (true)
                {
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

        private void Push(Dir source, Dir destination, bool ownsSource, bool ownsDestination) =>
            _levels.Add(new CopyLevel(source, destination, ownsSource, ownsDestination));
    }

    /// <summary>One pair of open directories the copy is working between.</summary>
    private sealed class CopyLevel : IDisposable
    {
        private readonly bool _ownsSource;
        private readonly bool _ownsDestination;

        public CopyLevel(Dir source, Dir destination, bool ownsSource, bool ownsDestination)
        {
            Source = source;
            Destination = destination;
            _ownsSource = ownsSource;
            _ownsDestination = ownsDestination;
            Entries = source.EnumerateEntries().GetEnumerator();
        }

        /// <summary>The directory being read.</summary>
        public Dir Source { get; }

        /// <summary>The directory being written.</summary>
        public Dir Destination { get; }

        /// <summary>The reading in progress.</summary>
        public IEnumerator<DirEntry> Entries { get; }

        public void Dispose()
        {
            Entries.Dispose();

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
