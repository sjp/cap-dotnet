using System.Text;
using Cap.Std;

namespace WasiHost.Preview1;

/// <summary>
/// The <c>path_*</c> calls. Each takes a directory descriptor and a path relative to it,
/// which is what every method on <see cref="Dir"/> takes, so each is one call on the
/// descriptor's <see cref="Dir"/> with the guest's path passed through as it arrived.
/// </summary>
/// <remarks>
/// The adapter does not look inside a path, with one exception: a path that names the
/// directory itself. The library has no spelling for "this directory" — a handle already is
/// that — and it refuses <c>.</c>, so a path made only of <c>.</c> components is answered
/// from the descriptor's own handle. No component is ever removed or collapsed, and
/// <c>..</c> is passed to the library like everything else.
/// </remarks>
public sealed partial class WasiPreview1
{
    /// <summary>What a file descriptor may hold, and what forces it to be opened for writing.</summary>
    private const Rights WritingRights = Rights.FdWrite | Rights.FdAllocate | Rights.FdFilestatSetSize;

    /// <summary>What forces a file descriptor to be opened for reading.</summary>
    private const Rights ReadingRights = Rights.FdRead | Rights.FdReaddir;

    private Errno PathOpen(
        GuestMemory memory,
        uint fd,
        LookupFlags lookup,
        uint pathAddress,
        uint pathLength,
        OFlags oflags,
        Rights rightsBase,
        Rights rightsInheriting,
        FdFlags fdflags,
        uint resultAddress)
    {
        Errno error = GetDirectory(fd, Rights.PathOpen, out DirectoryDescriptor parent);
        if (error != Errno.Success)
        {
            return error;
        }

        error = memory.ReadPath(pathAddress, pathLength, out string path);
        if (error != Errno.Success)
        {
            return error;
        }

        bool wantsDirectory = (oflags & OFlags.Directory) != 0;
        bool creates = (oflags & OFlags.Create) != 0;
        bool truncates = (oflags & OFlags.Truncate) != 0;
        if (wantsDirectory && (creates || truncates))
        {
            return Errno.Inval;
        }

        // A directory cannot be opened for writing, as open(2) refuses O_RDWR on one.
        if (wantsDirectory && (rightsBase & WritingRights) != 0)
        {
            return Errno.IsDir;
        }

        // What the guest may do beneath this directory is the rights' business, and is
        // checked here; what it can reach is the capability's, and is checked by the library.
        if ((creates && (parent.RightsBase & Rights.PathCreateFile) == 0) ||
            (truncates && (parent.RightsBase & Rights.PathFilestatSetSize) == 0))
        {
            return Errno.NotCapable;
        }

        bool follow = (lookup & LookupFlags.SymlinkFollow) != 0;
        Descriptor? opened = null;
        if (NamesItself(path))
        {
            if (creates && (oflags & OFlags.Exclusive) != 0)
            {
                return Errno.Exist;
            }

            if (truncates)
            {
                return Errno.IsDir;
            }

            error = ErrorMapping.Run(() => opened = DirectoryFor(parent.Dir.Clone(), rightsBase, rightsInheriting, fdflags));
        }
        else if (wantsDirectory)
        {
            error = OpenDirectory(parent, path, follow, rightsBase, rightsInheriting, fdflags, out opened);
        }
        else
        {
            error = OpenFile(parent, path, follow, oflags, rightsBase, rightsInheriting, fdflags, out opened);
        }

        if (error != Errno.Success)
        {
            return error;
        }

        uint result = Insert(opened!);
        return memory.WriteU32(resultAddress, result);
    }

    private static DirectoryDescriptor DirectoryFor(Dir dir, Rights rightsBase, Rights rightsInheriting, FdFlags fdflags) =>
        new(dir, rightsBase & Rights.Directory, rightsInheriting) { Flags = fdflags & FdFlags.NonBlock };

    /// <summary>
    /// Opens a directory, following a final symbolic link only when the guest asked for that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library decides whether links are followed per handle rather than per call, and a
    /// handle passes its policy on to every directory opened from it, with no way to loosen
    /// it again. So a directory opened through the descriptor's no-links view would refuse
    /// links for the rest of its life, and so would everything the guest opened beneath it —
    /// which is not what a single no-follow lookup asks for.
    /// </para>
    /// <para>
    /// Instead the name is opened twice: once through the no-links view, which refuses a link
    /// at the last component as WASI requires, and once through the descriptor's own handle,
    /// which is the one kept. The second is kept only if it is the same directory as the
    /// first; if the name changed hands in between, the open fails rather than return
    /// whatever was put there. Like the no-links view itself, this also refuses a link before
    /// the last component, which WASI would have followed.
    /// </para>
    /// </remarks>
    private static Errno OpenDirectory(
        DirectoryDescriptor parent,
        string path,
        bool follow,
        Rights rightsBase,
        Rights rightsInheriting,
        FdFlags fdflags,
        out Descriptor? opened)
    {
        Descriptor? result = null;
        Errno error = ErrorMapping.Run(() =>
        {
            if (follow)
            {
                result = DirectoryFor(parent.Dir.OpenDir(path), rightsBase, rightsInheriting, fdflags);
                return;
            }

            using Dir unfollowed = parent.NoFollow.OpenDir(path);
            Dir kept = parent.Dir.OpenDir(path);
            if (!kept.GetMetadata().IsSameFileAs(unfollowed.GetMetadata()))
            {
                kept.Dispose();
                throw new IOException($"'{path}' changed between the two opens that check it is not a link.");
            }

            result = DirectoryFor(kept, rightsBase, rightsInheriting, fdflags);
        });

        opened = result;
        return error;
    }

    /// <summary>
    /// Opens a file, choosing the <see cref="FileMode"/> and <see cref="FileAccess"/> that
    /// say what the WASI flags and rights say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file open that is not to follow a final link goes through the descriptor's no-links
    /// view. An open file resolves nothing further, so the stricter policy ends with it. An
    /// open that creates or truncates refuses a final link even when the guest asked to follow
    /// one, because <see cref="Dir"/> never writes a new or emptied file through a link at the
    /// name; that refusal is passed on to the guest.
    /// </para>
    /// <para>
    /// A WASI open that neither requires a directory nor creates anything may name a directory,
    /// and then opens it. <see cref="Dir"/> has no open that takes whatever the name holds, so
    /// a file open that fails is followed by a directory open of the same name. Both are
    /// confined, so the pair can reach nothing either could not; what it costs is a second
    /// resolution, and a window in which a rename can make the answer describe the second
    /// object rather than the first.
    /// </para>
    /// <para>
    /// .NET will not create or empty a file through a handle that cannot write it, while
    /// POSIX will. A creating open is therefore made with write access whatever the guest
    /// asked for; the descriptor is still given only the rights the guest asked for, so the
    /// guest cannot use the access it did not request.
    /// </para>
    /// <para>
    /// Appending is a <see cref="FileMode"/> in .NET, one that also creates the file and
    /// allows only writing. It is used for the one WASI combination that means the same
    /// thing — append, create, write only — and any other open that asks to append is
    /// reported as unsupported rather than approximated.
    /// </para>
    /// </remarks>
    private static Errno OpenFile(
        DirectoryDescriptor parent,
        string path,
        bool follow,
        OFlags oflags,
        Rights rightsBase,
        Rights rightsInheriting,
        FdFlags fdflags,
        out Descriptor? opened)
    {
        opened = null;

        bool truncates = (oflags & OFlags.Truncate) != 0;
        bool appends = (fdflags & FdFlags.Append) != 0;
        bool writeRequested = (rightsBase & WritingRights) != 0 || truncates || appends;
        bool read = (rightsBase & ReadingRights) != 0 || !writeRequested;

        FileMode mode = oflags switch
        {
            _ when (oflags & OFlags.Create) != 0 && (oflags & OFlags.Exclusive) != 0 => FileMode.CreateNew,
            _ when (oflags & OFlags.Create) != 0 && truncates => FileMode.Create,
            _ when (oflags & OFlags.Create) != 0 => FileMode.OpenOrCreate,
            _ when truncates => FileMode.Truncate,
            _ => FileMode.Open,
        };

        if (appends)
        {
            if (mode != FileMode.OpenOrCreate || read)
            {
                return Errno.NotSup;
            }

            mode = FileMode.Append;
        }

        bool write = writeRequested || mode is FileMode.CreateNew or FileMode.Create;
        FileAccess access = (read, write) switch
        {
            (true, true) => FileAccess.ReadWrite,
            (false, true) => FileAccess.Write,
            _ => FileAccess.Read,
        };

        // POSIX lets any process read, write or remove a file another has open, and a guest
        // written against WASI expects the same. Sharing only restricts anything on Windows.
        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        FileOptions options = (fdflags & (FdFlags.Sync | FdFlags.DSync | FdFlags.RSync)) != 0
            ? FileOptions.WriteThrough
            : FileOptions.None;

        Rights granted = rightsBase & Rights.File;
        if (!read)
        {
            granted &= ~Rights.FdRead;
        }

        if (!writeRequested)
        {
            granted &= ~(Rights.FdWrite | Rights.FdAllocate | Rights.FdFilestatSetSize);
        }

        FdFlags kept = fdflags & (FdFlags.Append | FdFlags.NonBlock | FdFlags.Sync | FdFlags.DSync | FdFlags.RSync);
        Dir from = follow ? parent.Dir : parent.NoFollow;

        CapFile? file = null;
        try
        {
            file = from.OpenFile(path, mode, access, share, options);
            FileType type = ToFileType(file.GetMetadata().Type);
            opened = new FileDescriptor(file, type, granted, rightsInheriting) { Flags = kept };
            return Errno.Success;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            file?.Dispose();

            if (mode == FileMode.Open && !write &&
                OpenDirectory(parent, path, follow, rightsBase, rightsInheriting, fdflags, out opened) == Errno.Success)
            {
                return Errno.Success;
            }

            return ErrorMapping.ToErrno(e);
        }
    }

    private Errno PathCreateDirectory(GuestMemory memory, uint fd, uint pathAddress, uint pathLength) =>
        WithPath(memory, fd, Rights.PathCreateDirectory, pathAddress, pathLength,
            static (dir, path) => dir.CreateDir(path).Dispose());

    private Errno PathRemoveDirectory(GuestMemory memory, uint fd, uint pathAddress, uint pathLength) =>
        WithPath(memory, fd, Rights.PathRemoveDirectory, pathAddress, pathLength,
            static (dir, path) => dir.DeleteDir(path));

    private Errno PathUnlinkFile(GuestMemory memory, uint fd, uint pathAddress, uint pathLength) =>
        WithPath(memory, fd, Rights.PathUnlinkFile, pathAddress, pathLength,
            static (dir, path) => dir.DeleteFile(path));

    /// <summary>
    /// Describes what a path names.
    /// </summary>
    /// <remarks>
    /// Without <see cref="LookupFlags.SymlinkFollow"/>, this is <see cref="Dir.GetMetadata(string)"/>,
    /// which describes a link as a link. With it, the guest wants what a final link leads to,
    /// and the library describes a target only through a handle on it — so the target is
    /// opened, as a file and failing that as a directory, and the open handle is described.
    /// That needs permission to open the target, which a plain description would not.
    /// </remarks>
    private Errno PathFilestatGet(
        GuestMemory memory, uint fd, LookupFlags lookup, uint pathAddress, uint pathLength, uint resultAddress)
    {
        Errno error = GetDirectory(fd, Rights.PathFilestatGet, out DirectoryDescriptor directory);
        if (error != Errno.Success)
        {
            return error;
        }

        error = memory.ReadPath(pathAddress, pathLength, out string path);
        if (error != Errno.Success)
        {
            return error;
        }

        CapMetadata metadata = default;
        if (NamesItself(path))
        {
            error = ErrorMapping.Run(() => metadata = directory.Dir.GetMetadata());
        }
        else if ((lookup & LookupFlags.SymlinkFollow) == 0)
        {
            error = ErrorMapping.Run(() => metadata = directory.Dir.GetMetadata(path));
        }
        else
        {
            error = ErrorMapping.Run(() =>
            {
                try
                {
                    using CapFile file = directory.Dir.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    metadata = file.GetMetadata();
                }
                catch (IOException) when (directory.Dir.TryOpenDir(path, out Dir? target))
                {
                    using (target)
                    {
                        metadata = target.GetMetadata();
                    }
                }
            });
        }

        return error != Errno.Success
            ? error
            : WriteFilestat(memory, resultAddress, ToFileType(metadata.Type), metadata);
    }

    /// <remarks>
    /// The library has no way to set a timestamp, so after the arguments are checked this is
    /// reported as unsupported.
    /// </remarks>
    private Errno PathFilestatSetTimes(GuestMemory memory, uint fd, uint pathAddress, uint pathLength, FstFlags flags)
    {
        Errno error = GetDirectory(fd, Rights.PathFilestatSetTimes, out _);
        if (error != Errno.Success)
        {
            return error;
        }

        error = memory.ReadPath(pathAddress, pathLength, out _);
        return error != Errno.Success ? error : ValidateTimes(flags) ?? Errno.NotSup;
    }

    /// <remarks>
    /// <para>
    /// A hard link to what a final symbolic link points at is not something the library can
    /// make: <see cref="Dir.CreateHardLink"/> links the name it is given. Asking to follow is
    /// therefore refused as an invalid argument, which is also how Wasmtime's own host answers
    /// it.
    /// </para>
    /// <para>
    /// The library reports a directory given a second name as
    /// <see cref="CapErrorKind.IsADirectory"/>, which says what was wrong with the request.
    /// <c>link(2)</c> reports it as <c>EPERM</c>, and that is the code a guest checks for.
    /// </para>
    /// </remarks>
    private Errno PathLink(
        GuestMemory memory,
        uint oldFd,
        LookupFlags lookup,
        uint oldAddress,
        uint oldLength,
        uint newFd,
        uint newAddress,
        uint newLength)
    {
        Errno error = GetDirectory(oldFd, Rights.PathLinkSource, out DirectoryDescriptor source);
        if (error == Errno.Success)
        {
            error = GetDirectory(newFd, Rights.PathLinkTarget, out DirectoryDescriptor target);
            if (error == Errno.Success)
            {
                error = memory.ReadPath(oldAddress, oldLength, out string oldPath);
                if (error == Errno.Success)
                {
                    error = memory.ReadPath(newAddress, newLength, out string newPath);
                    if (error == Errno.Success)
                    {
                        return (lookup & LookupFlags.SymlinkFollow) != 0
                            ? Errno.Inval
                            : ErrorMapping.Run(() => source.Dir.CreateHardLink(oldPath, target.Dir, newPath)) switch
                            {
                                Errno.IsDir => Errno.Perm,
                                Errno other => other,
                            };
                    }
                }
            }
        }

        return error;
    }

    private Errno PathReadlink(
        GuestMemory memory, uint fd, uint pathAddress, uint pathLength, uint buffer, uint bufferLength, uint resultAddress)
    {
        Errno error = GetDirectory(fd, Rights.PathReadlink, out DirectoryDescriptor directory);
        if (error != Errno.Success)
        {
            return error;
        }

        error = memory.ReadPath(pathAddress, pathLength, out string path);
        if (error != Errno.Success)
        {
            return error;
        }

        string target = string.Empty;
        error = ErrorMapping.Run(() => target = directory.Dir.ReadLink(path));
        if (error != Errno.Success)
        {
            return error;
        }

        // As readlink(2) does, a buffer too small for the whole target gets as much as fits.
        error = memory.WriteBytes(buffer, bufferLength, Encoding.UTF8.GetBytes(target), out uint written);
        return error != Errno.Success ? error : memory.WriteU32(resultAddress, written);
    }

    /// <remarks>
    /// Replaces whatever holds the destination name, as <c>rename(2)</c> does.
    /// </remarks>
    private Errno PathRename(
        GuestMemory memory, uint oldFd, uint oldAddress, uint oldLength, uint newFd, uint newAddress, uint newLength)
    {
        Errno error = GetDirectory(oldFd, Rights.PathRenameSource, out DirectoryDescriptor source);
        if (error == Errno.Success)
        {
            error = GetDirectory(newFd, Rights.PathRenameTarget, out DirectoryDescriptor target);
            if (error == Errno.Success)
            {
                error = memory.ReadPath(oldAddress, oldLength, out string oldPath);
                if (error == Errno.Success)
                {
                    error = memory.ReadPath(newAddress, newLength, out string newPath);
                    if (error == Errno.Success)
                    {
                        return ErrorMapping.Run(() => source.Dir.Rename(oldPath, target.Dir, newPath, replaceExisting: true));
                    }
                }
            }
        }

        return error;
    }

    /// <remarks>
    /// <para>
    /// The target is stored as the guest wrote it. What it names is decided whenever the link
    /// is followed, and it is followed only by resolution that is confined in the same way. A
    /// rooted target is refused by the library, which answers <c>ENOTCAPABLE</c>, as WASI
    /// hosts refuse one.
    /// </para>
    /// <para>
    /// WASI does not say what kind of object a link names, and Windows needs to know: a link
    /// made as the wrong kind there cannot be traversed. So the target is looked up from where
    /// the link will sit, beneath the same descriptor, and a directory link is made when it
    /// names a directory now; anything else, including nothing yet, gets a file link. This is
    /// a guess about the present, not a promise about the future: a target created or
    /// replaced later may be of the other kind. Everywhere but Windows the two kinds are the
    /// same link.
    /// </para>
    /// </remarks>
    private Errno PathSymlink(
        GuestMemory memory, uint targetAddress, uint targetLength, uint fd, uint pathAddress, uint pathLength)
    {
        Errno error = memory.ReadPath(targetAddress, targetLength, out string target);
        return error != Errno.Success
            ? error
            : WithPath(memory, fd, Rights.PathSymlink, pathAddress, pathLength, (dir, path) =>
            {
                if (NamesDirectory(dir, path, target))
                {
                    dir.CreateDirSymlink(path, target);
                }
                else
                {
                    dir.CreateSymlink(path, target);
                }
            });
    }

    /// <summary>
    /// Whether a link at <paramref name="linkPath"/> storing <paramref name="target"/> would
    /// currently lead to a directory beneath <paramref name="dir"/>.
    /// </summary>
    /// <remarks>
    /// A relative target is read from the directory the link sits in, so it is looked up
    /// after that directory's part of the link's path. Anything that cannot be opened as a
    /// directory beneath the handle, a target that leaves it included, answers false.
    /// </remarks>
    private static bool NamesDirectory(Dir dir, string linkPath, string target)
    {
        int slash = linkPath.TrimEnd('/').LastIndexOf('/');
        string fromLink = slash < 0 ? target : $"{linkPath[..(slash + 1)]}{target}";

        if (!dir.TryOpenDir(fromLink, out Dir? reached))
        {
            return false;
        }

        reached.Dispose();
        return true;
    }

    /// <summary>Runs a call that needs one directory descriptor and one path, and nothing back.</summary>
    private Errno WithPath(
        GuestMemory memory, uint fd, Rights needed, uint pathAddress, uint pathLength, Action<Dir, string> action)
    {
        Errno error = GetDirectory(fd, needed, out DirectoryDescriptor directory);
        if (error != Errno.Success)
        {
            return error;
        }

        error = memory.ReadPath(pathAddress, pathLength, out string path);
        return error != Errno.Success ? error : ErrorMapping.Run(() => action(directory.Dir, path));
    }

    /// <summary>
    /// True for a relative path made only of <c>.</c> components, which names the directory it
    /// is resolved against.
    /// </summary>
    private static bool NamesItself(string path) =>
        path.Length > 0 && path[0] != '/' && path.Split('/').All(component => component is "" or ".");
}
