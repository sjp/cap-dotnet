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
    /// created with. Times are not carried; <see cref="CopyTo"/> with
    /// <see cref="CopyOptions.PreserveTimes"/> is the helper that does.
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

        using ParentLocation location = ParentLocation.Resolve(toDir, to, nameof(to), mayNameDirectory: false);
        IDir directory = location.Directory;

        if (!overwrite)
        {
            using ICapFile created = directory.CreateNewFile(location.Name);
            return FillCopy(source, created, metadata, directory);
        }

        if (directory.TryGetMetadata(location.Name, out CapMetadata existing) &&
            existing.Type == CapFileType.Directory)
        {
            throw new CapIOException(
                CapErrorKind.IsADirectory,
                $"'{to}' is a directory, and a file copy replaces files, not directories; " +
                $"remove the directory first if it is meant to go.");
        }

        string? scratch = Claim(directory, asynchronous: false, ownerOnly: false, out ICapFile target);
        try
        {
            long copied;
            using (target)
            {
                copied = FillCopy(source, target, metadata, directory);
            }

            // Windows refuses to replace a file open without FileShare.Delete, and the
            // source is the file being replaced when a file is copied onto itself.
            source.Dispose();
            MoveOnto(directory, scratch, location.Name, quoted: to);
            scratch = null;
            return copied;
        }
        finally
        {
            Abandon(directory, scratch);
        }
    }

    /// <summary>
    /// Writes a source file's contents into a new file, and its permissions where the
    /// destination records the same kind.
    /// </summary>
    private static long FillCopy(ICapFile source, ICapFile target, in CapMetadata metadata, IDir destination)
    {
        long copied = Transfer(source, target);

        CapPermissions permissions = metadata.Permissions;
        bool recordsWindows = Handles.SyntaxOf(destination) == CapPathSyntax.Windows;
        if (recordsWindows ? permissions.TryGetWindowsAttributes(out _) : permissions.TryGetUnixMode(out _))
        {
            target.SetPermissions(permissions);
        }

        return copied;
    }
}
