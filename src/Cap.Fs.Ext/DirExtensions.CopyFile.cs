using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Copying one file from beneath one handle to beneath another.
/// </summary>
public static partial class DirExtensions
{
    /// <summary>
    /// Copies a file beneath this handle to a name beneath another handle, or beneath this one.
    /// </summary>
    /// <param name="dir">The handle <paramref name="from"/> is relative to.</param>
    /// <param name="from">A relative path to the file to copy.</param>
    /// <param name="toDir">
    /// The handle <paramref name="to"/> is relative to. May be <paramref name="dir"/>, and may
    /// be on a different filesystem from it.
    /// </param>
    /// <param name="to">A relative path, beneath <paramref name="toDir"/>, for the copy.</param>
    /// <param name="overwrite">
    /// Whether a file already holding <paramref name="to"/> is replaced. False by default, so
    /// the destructive reading is never the one a caller gets by not thinking about it.
    /// </param>
    /// <returns>How many bytes were copied.</returns>
    /// <remarks>
    /// <para>
    /// What <c>File.Copy</c> does, and cap-std's <c>Dir::copy</c>, with both ends named by a
    /// handle and a path beneath it: joining two places together takes authority over both.
    /// The contents are read through the source handle and written through the destination
    /// handle, and nothing from one is handed to the other, so the two may be on different
    /// backends, such as a tree in memory and the disk.
    /// </para>
    /// <para>
    /// <strong>Permissions travel with the contents</strong>, as they do for <c>File.Copy</c>:
    /// the copy is given the source's mode on Linux and macOS and its attribute flags on
    /// Windows, as <see cref="IDir.SetPermissions"/> writes them. Between two filesystems that
    /// record different kinds, such as a tree in memory following Windows rules and the disk
    /// on Linux, there is nothing faithful to carry and the copy keeps the permissions it was
    /// created with. Times are not carried; the form that takes <see cref="CopyOptions"/>, with
    /// <see cref="CopyOptions.PreserveTimes"/>, carries them.
    /// </para>
    /// <para>
    /// <strong>Without <paramref name="overwrite"/></strong> the destination is created
    /// exclusively, so a name already taken, by anything, is refused before anything is
    /// written. A failure part of the way through the contents leaves the partial copy at
    /// <paramref name="to"/>. <strong>With it</strong>, the copy is written under a scratch name
    /// beside <paramref name="to"/> and moved onto it, so a reader of <paramref name="to"/>
    /// sees the old file or the whole new one, and a failure leaves the old file as it was. A
    /// directory at <paramref name="to"/> is refused either way. Copying a file onto itself
    /// with <paramref name="overwrite"/> reads it whole before replacing it, and leaves it
    /// holding what it held.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The source is opened as <see cref="IDir.OpenFile"/>
    /// opens an existing file, so a link anywhere in <paramref name="from"/>, the last
    /// component included, is followed under
    /// <see cref="Cap.Primitives.SymlinkPolicy.FollowWithinSandbox"/> while its target stays
    /// beneath <paramref name="dir"/>, refused with <see cref="SandboxEscapeException"/> once it
    /// leaves, and refused with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>; what is copied is what the link led to.
    /// The last component of <paramref name="to"/> is never followed: without
    /// <paramref name="overwrite"/> a link there makes the name taken, and with it the link is
    /// replaced by the copy and what it pointed at is neither written nor removed. A link
    /// before the last component of <paramref name="to"/> is followed or refused under
    /// <paramref name="toDir"/>'s policy as for any other path.
    /// </para>
    /// <para>
    /// <strong>The contents are moved by the quickest means both ends share</strong>, as
    /// <see cref="CopyTo"/> describes: on the host's filesystem the copy may share the
    /// source's storage or be made inside the kernel, and is otherwise read and written in
    /// pieces. On macOS a copy that shares storage carries the source's extended attributes
    /// as well.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with other work on either handle.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A path is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// A path named something outside the authority of the handle it was used against.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="from"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above either name is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused part of the copy.</exception>
    /// <exception cref="CapIOException">
    /// <paramref name="from"/> is not a file, <paramref name="to"/> is taken and
    /// <paramref name="overwrite"/> is not set or holds a directory, a symbolic link a policy
    /// will not follow is in the way, or the copy failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public static long CopyFile(this IDir dir, string from, IDir toDir, string to, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(toDir);
        ArgumentNullException.ThrowIfNull(to);

        using ICapFile source = dir.OpenFile(
            from, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        CapMetadata metadata = source.GetMetadata();

        // A FIFO, a socket or a device opens as a file does, and has no contents to copy: a
        // read of one waits on whatever is at the other end. Refused before anything is made
        // at the destination, as the tree copy refuses one it is not told to skip.
        if (metadata.Type != CapFileType.File)
        {
            throw new CapIOException(
                CapErrorKind.NotSupported,
                $"'{from}' is a {metadata.Type}, not a file with contents, and a copy has no " +
                $"faithful equivalent for one.");
        }

        return CopyContents(source, metadata, toDir, to, SingleCopy.LikeFileCopy(overwrite), CancellationToken.None);
    }

    /// <summary>
    /// Copies a file beneath this handle to a name beneath another handle, or beneath this one,
    /// as <see cref="CopyTo"/> would copy that one entry.
    /// </summary>
    /// <param name="dir">The handle <paramref name="from"/> is relative to.</param>
    /// <param name="from">A relative path to the file to copy.</param>
    /// <param name="toDir">
    /// The handle <paramref name="to"/> is relative to. May be <paramref name="dir"/>, and may
    /// be on a different filesystem from it.
    /// </param>
    /// <param name="to">A relative path, beneath <paramref name="toDir"/>, for the copy.</param>
    /// <param name="options">
    /// What the copy does with what it finds: the same settings, with the same meanings, that
    /// <see cref="CopyTo"/> takes.
    /// </param>
    /// <param name="cancellationToken">Stops the copy before it starts or between the pieces of the file.</param>
    /// <returns>
    /// What was copied: one file and its bytes, one link, or one entry skipped.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The single-file form of <see cref="CopyTo"/>, for a caller who wants that copy's choices
    /// for one file under a name of its own. Where it differs from
    /// <see cref="CopyFile(IDir, string, IDir, string, bool)"/> it differs in the direction of
    /// <see cref="CopyTo"/>: nothing is carried that the options do not ask for, so with the
    /// defaults the copy gets the permissions a new file gets; a link at
    /// <paramref name="from"/> is dealt with as <see cref="CopyOptions.Symlinks"/> says rather
    /// than followed; and a copy that fails or is cancelled part way never leaves a partly
    /// written file at <paramref name="to"/>.
    /// </para>
    /// <para>
    /// <see cref="CopyOptions.Overwrite"/> replaces a file or a link at <paramref name="to"/>
    /// by moving a finished copy onto the name, as
    /// <see cref="CopyFile(IDir, string, IDir, string, bool)"/> does, and a directory there is
    /// refused either way. <see cref="CopyOptions.PreservePermissions"/>,
    /// <see cref="CopyOptions.PreserveTimes"/> and <see cref="CopyOptions.PreserveSparseness"/>
    /// carry what they carry for each file of a tree, and a destination that will not take the
    /// permissions fails the copy. <see cref="CopyOptions.OtherKinds"/> decides what a named
    /// pipe, a socket or a device at <paramref name="from"/> gets, and
    /// <see cref="CopyOptions.MaxDepth"/> has nothing to limit. A directory at
    /// <paramref name="from"/> is refused: <see cref="CopyTo"/> copies a tree.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with other work on either handle.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Components ahead of the last, at either end, are
    /// resolved under the policy of the handle the path is used against, as for any other path.
    /// A link as the last component of <paramref name="from"/> is never followed: the source is
    /// opened refusing one, and a link found there is refused, skipped or made again at
    /// <paramref name="to"/> with the same target text, as <see cref="CopyOptions.Symlinks"/>
    /// says. The last component of <paramref name="to"/> is never followed either, as
    /// <see cref="CopyOptions.Overwrite"/> describes.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// A path is not a usable name for a file, or <see cref="CopyOptions.OtherKinds"/> asks for
    /// objects to be recreated, which is not something this can do.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="CopyOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// A path named something outside the authority of the handle it was used against, or a
    /// link to be made again holds a rooted target.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is nothing at <paramref name="from"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above either name is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused part of the copy.</exception>
    /// <exception cref="CapIOException">
    /// <paramref name="from"/> is a directory or something the options say to refuse,
    /// <paramref name="to"/> is taken and <see cref="CopyOptions.Overwrite"/> is not set or
    /// holds a directory, permissions were to be preserved and the destination would not take
    /// them, or the copy failed otherwise.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public static CopyReport CopyFile(
        this IDir dir,
        string from,
        IDir toDir,
        string to,
        CopyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(options);
        CopyOptions settings = Demand(dir, toDir, options);
        cancellationToken.ThrowIfCancellationRequested();

        ICapFile source;
        try
        {
            source = dir.OpenFile(
                from,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan,
                0,
                append: false,
                noFollow: true);
        }
        catch (CapIOException refusal) when (IsLinkRefusal(refusal))
        {
            // Described again rather than assumed, as the tree copy does, so that a link is
            // made again from what the name holds now.
            if (!dir.TryGetMetadata(from, out CapMetadata link) || link.Type != CapFileType.Symlink)
            {
                throw;
            }

            return CopyIrregular(dir, from, toDir, to, link, settings.Symlinks, isLink: true, settings);
        }

        using (source)
        {
            CapMetadata metadata = source.GetMetadata();
            if (metadata.Type == CapFileType.Directory)
            {
                throw new CapIOException(
                    CapErrorKind.IsADirectory,
                    $"'{from}' is a directory, and a file copy copies files; CopyTo copies a tree.");
            }

            if (metadata.Type != CapFileType.File)
            {
                return CopyIrregular(dir, from, toDir, to, metadata, settings.OtherKinds, isLink: false, settings);
            }

            long copied = CopyContents(
                source, metadata, toDir, to, SingleCopy.LikeTreeCopy(settings), cancellationToken);
            return new CopyReport(0, 1, 0, 0, copied);
        }
    }

    /// <summary>
    /// The choices that tell the two forms of a single-file copy apart.
    /// </summary>
    /// <param name="Overwrite">Whether a file at the destination name is replaced.</param>
    /// <param name="CarryPermissions">Whether the source's permissions are given to the copy.</param>
    /// <param name="DemandPermissions">
    /// Whether a destination that records the other platform's kind of permissions fails the
    /// copy, rather than the permissions being left behind.
    /// </param>
    /// <param name="KeepTimes">Whether the source's times are given to the copy.</param>
    /// <param name="KeepHoles">Whether a sparse source's holes stay holes.</param>
    /// <param name="DiscardPartial">
    /// Whether a copy made under its real name and not finished is removed.
    /// </param>
    private readonly record struct SingleCopy(
        bool Overwrite,
        bool CarryPermissions,
        bool DemandPermissions,
        bool KeepTimes,
        bool KeepHoles,
        bool DiscardPartial)
    {
        /// <summary>What <c>File.Copy</c> does.</summary>
        public static SingleCopy LikeFileCopy(bool overwrite) =>
            new(overwrite, CarryPermissions: true, DemandPermissions: false, KeepTimes: false, KeepHoles: false, DiscardPartial: false);

        /// <summary>What the tree copy does for one of its files.</summary>
        public static SingleCopy LikeTreeCopy(CopyOptions options) =>
            new(
                options.Overwrite,
                options.PreservePermissions,
                DemandPermissions: true,
                options.PreserveTimes,
                options.PreserveSparseness,
                DiscardPartial: true);

        /// <summary>
        /// Whether the copy may be made as a clone, which takes the source's mode with it and
        /// so only when the permissions are carried anyway.
        /// </summary>
        public bool MayClone => CarryPermissions;
    }

    /// <summary>Copies an open source file's contents to a name beneath a handle.</summary>
    private static long CopyContents(
        ICapFile source,
        in CapMetadata metadata,
        IDir toDir,
        string to,
        SingleCopy how,
        CancellationToken cancellationToken)
    {
        using ParentLocation location = ParentLocation.Resolve(toDir, to, nameof(to), mayNameDirectory: false);
        IDir directory = location.Directory;
        ContentTransfer transfer = new(how.KeepHoles);

        if (!how.Overwrite)
        {
            ICapFile? clone = how.MayClone ? transfer.CloneNew(directory, location.Name, source, asynchronous: false) : null;
            ICapFile created = clone ?? directory.CreateNewFile(location.Name);
            bool finished = false;
            try
            {
                long written;
                using (created)
                {
                    written = FillCopy(transfer, source, created, metadata, directory, clone is not null, how, cancellationToken);
                }

                finished = true;
                return written;
            }
            finally
            {
                // Created exclusively by this copy a moment ago, so the name removed is one
                // the copy made.
                if (!finished && how.DiscardPartial)
                {
                    _ = directory.TryDeleteFile(location.Name);
                }
            }
        }

        if (directory.TryGetMetadata(location.Name, out CapMetadata existing) &&
            existing.Type == CapFileType.Directory)
        {
            throw new CapIOException(
                CapErrorKind.IsADirectory,
                $"'{to}' is a directory, and a file copy replaces files, not directories; " +
                $"remove the directory first if it is meant to go.");
        }

        string? scratch = null;
        ICapFile? target = null;
        bool cloned = how.MayClone && transfer.TryCloneScratch(directory, source, asynchronous: false, out scratch, out target);
        if (!cloned)
        {
            scratch = Claim(directory, asynchronous: false, ownerOnly: false, out ICapFile claimed);
            target = claimed;
        }

        try
        {
            long copied;
            using (target)
            {
                copied = FillCopy(transfer, source, target!, metadata, directory, cloned, how, cancellationToken);
            }

            // Windows refuses to replace a file open without FileShare.Delete, and the
            // source is the file being replaced when a file is copied onto itself.
            source.Dispose();
            MoveOnto(directory, scratch!, location.Name, quoted: to);
            scratch = null;
            return copied;
        }
        finally
        {
            Abandon(directory, scratch);
        }
    }

    /// <summary>
    /// Writes a source file's contents into a new file, and its permissions and times as asked.
    /// </summary>
    /// <remarks>
    /// A file made as a clone already holds the contents, and is only given the time it was
    /// made, as a written file would carry, unless the source's times are being kept. The
    /// times are set last, after every write, so that nothing written afterwards moves them on.
    /// </remarks>
    private static long FillCopy(
        ContentTransfer transfer,
        ICapFile source,
        ICapFile target,
        in CapMetadata metadata,
        IDir destination,
        bool cloned,
        SingleCopy how,
        CancellationToken cancellationToken)
    {
        long copied = cloned
            ? ContentTransfer.Cloned(target, how.KeepTimes)
            : transfer.Transfer(source, target, metadata.Length, cancellationToken);

        if (how.CarryPermissions)
        {
            CapPermissions permissions = metadata.Permissions;
            bool recordsWindows = Handles.SyntaxOf(destination) == CapPathSyntax.Windows;
            if (how.DemandPermissions ||
                (recordsWindows ? permissions.TryGetWindowsAttributes(out _) : permissions.TryGetUnixMode(out _)))
            {
                target.SetPermissions(permissions);
            }
        }

        if (how.KeepTimes)
        {
            target.SetTimes(Accessed(metadata), CapFileTime.At(metadata.LastWriteTime));
        }

        return copied;
    }

    /// <summary>
    /// Deals with a source that is a link or another kind that is not a file, as the options
    /// say to deal with that kind.
    /// </summary>
    private static CopyReport CopyIrregular(
        IDir dir,
        string from,
        IDir toDir,
        string to,
        in CapMetadata metadata,
        CopyAction action,
        bool isLink,
        CopyOptions options)
    {
        switch (action)
        {
            case CopyAction.Skip:
                return new CopyReport(0, 0, 0, 1, 0);

            case CopyAction.Recreate when isLink:
                RelinkFile(dir, from, toDir, to, metadata, options);
                return new CopyReport(0, 0, 1, 0, 0);

            default:
                throw new CapIOException(
                    CapErrorKind.NotSupported,
                    $"'{from}' is a {metadata.Type} and a copy has no faithful equivalent for " +
                    $"one. It is not followed and not read: doing either could reach outside " +
                    $"the handle, or block on something that is not storage. Ask for entries " +
                    $"of this kind to be skipped or, for a link, made again.");
        }
    }

    /// <summary>
    /// Makes a link at the destination holding the same target text as the link at the source,
    /// as the tree copy makes one again.
    /// </summary>
    /// <remarks>
    /// A rooted target, which no link beneath a handle may store, is refused before anything
    /// at the destination is touched. With replacement, what holds the name is removed as a
    /// name first, never written through.
    /// </remarks>
    private static void RelinkFile(IDir dir, string from, IDir toDir, string to, in CapMetadata metadata, CopyOptions options)
    {
        string target = dir.ReadLink(from);

        using ParentLocation location = ParentLocation.Resolve(toDir, to, nameof(to), mayNameDirectory: false);
        IDir directory = location.Directory;

        if (CapPath.IsRooted(target, Handles.SyntaxOf(directory)))
        {
            throw new SandboxEscapeException(
                $"'{from}' is a symbolic link to '{target}', which is rooted, and a link beneath " +
                $"a handle cannot store a rooted target, so it cannot be made again at '{to}'.");
        }

        if (options.Overwrite)
        {
            _ = directory.TryDeleteFile(location.Name);
        }

        bool namesDirectory =
            metadata.Permissions.TryGetWindowsAttributes(out FileAttributes attributes) &&
            (attributes & FileAttributes.Directory) != 0;

        if (namesDirectory)
        {
            directory.CreateDirSymlink(location.Name, target);
        }
        else
        {
            directory.CreateSymlink(location.Name, target);
        }

        if (options.PreserveTimes)
        {
            directory.SetTimes(location.Name, Accessed(metadata), CapFileTime.At(metadata.LastWriteTime));
        }
    }

    /// <summary>Whether an open that refuses a final link failed because there was one.</summary>
    /// <remarks>
    /// Each backend names the refusal in its own way: the kernel's answer to an open that
    /// will not follow a link is the one it gives for a link it will not follow, and a
    /// backend that looks at the name first reports it as the link it found.
    /// </remarks>
    private static bool IsLinkRefusal(CapIOException refusal) =>
        refusal.Kind is CapErrorKind.LinkNotFollowed or CapErrorKind.SymbolicLink;

    /// <summary>The access time to give a copy: the source's, or none where it has none.</summary>
    /// <remarks>
    /// A source on a filesystem that keeps no access times has nothing to carry over, and
    /// leaving the copy's own in place is closer to that than stamping it with a date.
    /// </remarks>
    private static CapFileTime Accessed(in CapMetadata metadata) =>
        metadata.LastAccessTime is { } accessed ? CapFileTime.At(accessed) : CapFileTime.Unchanged;
}
