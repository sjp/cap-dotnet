using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Removing a name that holds a file or a symbolic link, whichever kind of link it is.
/// </summary>
/// <remarks>
/// <para>
/// Win32 removes a link made to name a directory with <c>RemoveDirectory</c> and one made to
/// name a file with <c>DeleteFile</c>, so code that removes "whatever link this is" has to know
/// which kind it was given — and the obvious way to find out, asking what the link points at,
/// follows it. A <see cref="Dir"/> removes either kind as a file on every platform, and these
/// also cover an <see cref="IDir"/> that keeps the Win32 split. They remove the name itself,
/// never what a link leads to, and never a real directory.
/// </para>
/// </remarks>
public static partial class DirExtensions
{
    /// <summary>
    /// Removes a file or a symbolic link beneath this handle, whichever kind of link it is.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file or link to remove.</param>
    /// <remarks>
    /// <para>
    /// cap-fs-ext's <c>remove_file_or_symlink</c>. A file, a symbolic link, and anything else
    /// that is not a directory — a named pipe, a socket, a device node — is removed by its
    /// single name against the directory holding it. A directory is refused and left as it is,
    /// empty or not; removing one is <see cref="IDir.DeleteDir"/>'s business, or
    /// <see cref="DeleteTree(IDir, string, CancellationToken)"/>'s.
    /// </para>
    /// <para>
    /// The name is described before it is removed, so that a directory is refused rather than
    /// removed, and that description is a moment that has passed by the time the removal runs.
    /// The removal is one that cannot remove a directory, so a name swapped for one in between
    /// is refused too. The one exception is an <see cref="IDir"/> that is not a
    /// <see cref="Dir"/> and refuses to remove a link as a file: a link is then removed as a
    /// directory, and an empty directory swapped in for it in between would be removed in its
    /// place. Nothing inside a directory is ever removed.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with other work on the same handle.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Components ahead of the last are resolved under this
    /// handle's own policy, so a link among them is followed while it stays inside the subtree
    /// or refused as for any other operation. A link as the last component is removed as the
    /// link, whatever it points at — a file, a directory, nothing, or somewhere outside this
    /// handle — and its target is never reached, looked at or changed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable name, or is spelled so that it has to name a
    /// directory.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is nothing at <paramref name="path"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the name is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the removal.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, or could not be removed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void RemoveFileOrSymlink(this IDir dir, string path)
    {
        using ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: false);
        IDir directory = location.Directory;

        if (!directory.TryGetMetadata(location.Name, out CapMetadata metadata))
        {
            // Asked again in the form that throws, so a refusal to look is reported as the
            // refusal it was and not as a name holding nothing.
            metadata = directory.GetMetadata(location.Name);
        }

        if (metadata.Type == CapFileType.Directory)
        {
            throw FailureTranslation.ToException(CapError.FromCategory(CapErrorCategory.IsADirectory), path, ExpectedTarget.Name);
        }

        if (RemovedAsLink(directory, location.Name, metadata.Type))
        {
            return;
        }

        directory.DeleteFile(location.Name);
    }

    /// <summary>
    /// Removes a file or a symbolic link beneath this handle, whichever kind of link it is,
    /// reporting failure rather than throwing.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">
    /// A relative path to the file or link to remove. See <see cref="RemoveFileOrSymlink"/>.
    /// </param>
    /// <returns>
    /// True when the name was removed; false when it held nothing, held a directory, could not
    /// be reached for a missing or refused directory above it, or could not be removed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The same removal as <see cref="RemoveFileOrSymlink"/>, for clearing up, where a name
    /// already gone is an ordinary outcome.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with other work on the same handle.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> As for <see cref="RemoveFileOrSymlink"/>: a link ahead of
    /// the last component is resolved under the handle's policy, and a refusal answers false; a
    /// link as the last component is removed as the link and its target is never reached.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable name, or is spelled so that it has to name a
    /// directory.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool TryRemoveFileOrSymlink(this IDir dir, string path)
    {
        ParentLocation location;
        try
        {
            location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: false);
        }
        catch (Exception exception) when (
            exception is (IOException or UnauthorizedAccessException) and not SandboxEscapeException)
        {
            // The path ahead of the name is missing or was refused: there is no name to
            // remove. A path that leads out of the handle, and a mistake in how the path is
            // spelled, are still thrown, as they are by the other forms that answer false.
            return false;
        }

        using (location)
        {
            IDir directory = location.Directory;
            if (!directory.TryGetMetadata(location.Name, out CapMetadata metadata) ||
                metadata.Type == CapFileType.Directory)
            {
                return false;
            }

            return RemovedAsLink(directory, location.Name, metadata.Type) || directory.TryDeleteFile(location.Name);
        }
    }

    /// <summary>
    /// Removes a file or a symbolic link beneath this handle, whichever kind of link it is,
    /// without holding the calling thread.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">
    /// A relative path to the file or link to remove. See <see cref="RemoveFileOrSymlink"/>.
    /// </param>
    /// <param name="cancellationToken">Stops the removal if it is signalled before the removal starts.</param>
    /// <returns>A task that completes when the name is gone.</returns>
    /// <remarks>
    /// <para>
    /// The same removal as <see cref="RemoveFileOrSymlink"/>, done on a thread-pool thread: no
    /// platform this runs on removes a name asynchronously. The arguments are checked before
    /// the task is made; the path is resolved inside it. A removal that has started is not
    /// stopped part way, since it is a single name.
    /// </para>
    /// <para>
    /// Safe to call from any thread. The handle must stay open until the task completes.
    /// </para>
    /// <para>
    /// <strong>Symbolic links</strong> are treated as <see cref="RemoveFileOrSymlink"/> treats
    /// them: a link as the last component is removed as the link.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable name, or is spelled so that it has to name a
    /// directory.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is nothing at <paramref name="path"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the name is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the removal.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="CapIOException">
    /// The name holds a directory, or could not be removed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static Task RemoveFileOrSymlinkAsync(this IDir dir, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(path);

        return Task.Run(() => RemoveFileOrSymlink(dir, path), cancellationToken);
    }

    /// <summary>
    /// Removes a file or a symbolic link beneath this handle, whichever kind of link it is,
    /// without holding the calling thread, reporting failure rather than throwing.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">
    /// A relative path to the file or link to remove. See <see cref="RemoveFileOrSymlink"/>.
    /// </param>
    /// <param name="cancellationToken">Stops the removal if it is signalled before the removal starts.</param>
    /// <returns>A task whose result is true when the name was removed.</returns>
    /// <remarks>
    /// The same removal as <see cref="TryRemoveFileOrSymlink"/>, done on a thread-pool thread.
    /// A signal on <paramref name="cancellationToken"/> before it starts cancels the task
    /// rather than completing it with false: the name is neither gone nor refused.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable name, or is spelled so that it has to name a
    /// directory.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static Task<bool> TryRemoveFileOrSymlinkAsync(this IDir dir, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(path);

        return Task.Run(() => TryRemoveFileOrSymlink(dir, path), cancellationToken);
    }

    /// <summary>
    /// Removes a link through a handle that may remove a link to a directory only as a
    /// directory.
    /// </summary>
    /// <returns>
    /// True when it was removed; false when the name is not a link, the handle is a
    /// <see cref="Dir"/>, or neither removal took and the ordinary one is the caller's to make.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every backend of a <see cref="Dir"/> removes a link of either kind as a file is removed,
    /// Windows directory links included, and refuses a link as a directory. Another
    /// <see cref="IDir"/>, such as a wrapper over the Win32 calls, may keep the split those
    /// calls make, so for one of those a link the file removal refuses is removed as a
    /// directory.
    /// </para>
    /// <para>
    /// Only for a name described as a link, so a real directory is never handed to the
    /// directory removal, and never through a <see cref="Dir"/>, so the race described on
    /// <see cref="RemoveFileOrSymlink"/> cannot remove a directory there.
    /// </para>
    /// </remarks>
    private static bool RemovedAsLink(IDir directory, string name, CapFileType type) =>
        type == CapFileType.Symlink &&
        directory is not Dir &&
        (directory.TryDeleteFile(name) || directory.TryDeleteDir(name));
}
