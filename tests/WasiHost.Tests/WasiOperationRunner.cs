using System.Buffers.Binary;
using System.Text;
using Cap.Escape.Tests;
using WasiHost.Preview1;

namespace WasiHost.Tests;

/// <summary>What a WASI operation came to, and everything it let the guest see.</summary>
/// <param name="Observation">
/// The outcome and what was read or listed, in the form the corpus's containment checks take.
/// </param>
/// <param name="Reached">The device and inode of every object the operation reached.</param>
/// <param name="Errno">The code the call that decided the outcome returned.</param>
internal sealed record WasiObservation(Observation Observation, List<(ulong Device, ulong Inode)> Reached, Errno Errno);

/// <summary>
/// Drives one corpus operation through the WASI calls a guest would make for it.
/// </summary>
/// <remarks>
/// <para>
/// Each operation is the WASI counterpart of the library call the corpus makes directly:
/// opening and reading a file is <c>path_open</c> then <c>fd_read</c>, listing a directory is
/// <c>path_open</c> with <c>O_DIRECTORY</c> then <c>fd_readdir</c>, and so on. Removing a
/// whole tree has no WASI call, so that operation is not driven here.
/// </para>
/// <para>
/// An open follows a final link, as a guest's <c>open()</c> asks by default, and a
/// description does not, as <c>lstat()</c>. That is the same choice the library's own calls
/// make, so the corpus's expectations apply unchanged.
/// </para>
/// </remarks>
internal static class WasiOperationRunner
{
    public static WasiObservation Run(TrampolineGuest guest, Operation operation, string path)
    {
        List<(ulong, ulong)> reached = [];
        Observation succeeded = new(Outcome.Success, null);
        Errno errno = Perform(guest, operation, path, succeeded, reached);

        if (errno == Errno.Success)
        {
            return new(succeeded, reached, errno);
        }

        // Asking whether a name is taken never fails. It answers no.
        Outcome outcome = operation == Operation.Exists ? Outcome.NotFound : Classify(errno);
        return new(new Observation(outcome, null) { LinkCreated = succeeded.LinkCreated }, reached, errno);
    }

    /// <summary>
    /// The outcome an error code stands for: the same classes the corpus sorts the library's
    /// exceptions into.
    /// </summary>
    public static Outcome Classify(Errno errno) => errno switch
    {
        Errno.Success => Outcome.Success,
        Errno.NotCapable => Outcome.Escape,
        Errno.NoEnt => Outcome.NotFound,
        Errno.Access or Errno.Perm => Outcome.Denied,
        Errno.Inval or Errno.IlSeq => Outcome.Malformed,
        _ => Outcome.Refused,
    };

    private static Errno Perform(
        TrampolineGuest guest, Operation operation, string path, Observation observation, List<(ulong, ulong)> reached) =>
        operation switch
        {
            Operation.OpenFile => Read(guest, path, observation, reached),
            Operation.OpenDir => ListDirectory(guest, path, observation, reached),
            Operation.CreateFile => CreateAndWrite(guest, path, reached),
            Operation.CreateDir => WithPath(guest, "path_create_directory", path),
            Operation.GetMetadata or Operation.Exists => DescribePath(guest, path, reached),
            Operation.SetTimes => SetTimes(guest, path, reached),
            Operation.ReadLink => ReadLink(guest, path),
            Operation.DeleteFile => WithPath(guest, "path_unlink_file", path),
            Operation.DeleteDir => WithPath(guest, "path_remove_directory", path),
            Operation.RenameFrom => Rename(guest, path, EscapeCorpus.LandingName),
            Operation.RenameTo => Rename(guest, EscapeCorpus.SourceFile, path),
            Operation.CreateSymlinkAt => Symlink(guest, EscapeCorpus.CreatedLinkTarget, path),
            Operation.CreateSymlinkTo => SymlinkAndFollow(guest, path, observation, reached),
            Operation.HardLinkFrom => LinkAndDescribe(guest, path, reached),
            Operation.HardLinkTo => Link(guest, EscapeCorpus.SourceFile, path),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "No WASI call does this."),
        };

    private static Errno ListDirectory(TrampolineGuest guest, string path, Observation observation, List<(ulong, ulong)> reached)
    {
        Errno errno = Open(guest, path, OFlags.Directory, Rights.FdReaddir | Rights.FdFilestatGet, out uint fd);
        if (errno != Errno.Success)
        {
            return errno;
        }

        errno = Describe(guest, fd, reached);
        if (errno == Errno.Success)
        {
            errno = List(guest, fd, observation);
        }

        _ = guest.Call("fd_close", (int)fd);
        return errno;
    }

    private static Errno CreateAndWrite(TrampolineGuest guest, string path, List<(ulong, ulong)> reached)
    {
        Errno errno = Open(guest, path, OFlags.Create | OFlags.Truncate, Rights.FdWrite | Rights.FdFilestatGet, out uint fd);
        if (errno != Errno.Success)
        {
            return errno;
        }

        errno = Describe(guest, fd, reached);
        if (errno == Errno.Success)
        {
            int length = Encoding.UTF8.GetBytes(
                "written through the sandbox", guest.Bytes(TrampolineGuest.DataSlot, TrampolineGuest.DataLength));
            guest.SetIoVec(length);
            errno = guest.Call("fd_write", (int)fd, TrampolineGuest.IoVecSlot, 1, TrampolineGuest.ResultSlot);
        }

        _ = guest.Call("fd_close", (int)fd);
        return errno;
    }

    /// <summary>Describes a name without following a final link, as <c>lstat()</c> does.</summary>
    private static Errno DescribePath(TrampolineGuest guest, string path, List<(ulong, ulong)> reached)
    {
        int length = guest.WritePath(TrampolineGuest.PathSlot, path);
        Errno errno = guest.Call(
            "path_filestat_get", TrampolineGuest.Root, 0, TrampolineGuest.PathSlot, length, TrampolineGuest.ResultSlot);
        if (errno == Errno.Success)
        {
            reached.Add(Identity(guest));
        }

        return errno;
    }

    /// <summary>
    /// Sets a name's last-write time without following a final link, then describes it the
    /// same way to learn what was reached.
    /// </summary>
    private static Errno SetTimes(TrampolineGuest guest, string path, List<(ulong, ulong)> reached)
    {
        int length = guest.WritePath(TrampolineGuest.PathSlot, path);
        long written = (EscapeCorpus.PlantedTime - DateTimeOffset.UnixEpoch).Ticks * 100;
        Errno errno = guest.Call(
            "path_filestat_set_times", TrampolineGuest.Root, 0, TrampolineGuest.PathSlot, length,
            0L, written, (int)FstFlags.Mtim);

        return errno == Errno.Success ? DescribePath(guest, path, reached) : errno;
    }

    private static Errno ReadLink(TrampolineGuest guest, string path)
    {
        int length = guest.WritePath(TrampolineGuest.PathSlot, path);
        return guest.Call(
            "path_readlink", TrampolineGuest.Root, TrampolineGuest.PathSlot, length,
            TrampolineGuest.DataSlot, TrampolineGuest.DataLength, TrampolineGuest.ResultSlot);
    }

    private static Errno SymlinkAndFollow(TrampolineGuest guest, string path, Observation observation, List<(ulong, ulong)> reached)
    {
        Errno errno = Symlink(guest, path, EscapeCorpus.CreatedLinkName);
        if (errno != Errno.Success)
        {
            return errno;
        }

        observation.LinkCreated = true;
        return Read(guest, EscapeCorpus.CreatedLinkName, observation, reached);
    }

    private static Errno LinkAndDescribe(TrampolineGuest guest, string path, List<(ulong, ulong)> reached)
    {
        Errno errno = Link(guest, path, EscapeCorpus.LandingName);
        return errno != Errno.Success ? errno : DescribePath(guest, EscapeCorpus.LandingName, reached);
    }

    /// <summary>Opens a name and reads from it, as a guest's <c>open()</c> and <c>read()</c> would.</summary>
    private static Errno Read(TrampolineGuest guest, string path, Observation observation, List<(ulong, ulong)> reached)
    {
        Errno errno = Open(guest, path, OFlags.None, Rights.FdRead | Rights.FdFilestatGet, out uint fd);
        if (errno != Errno.Success)
        {
            return errno;
        }

        errno = Describe(guest, fd, reached);
        if (errno == Errno.Success)
        {
            guest.SetIoVec(TrampolineGuest.DataLength);
            errno = guest.Call("fd_read", (int)fd, TrampolineGuest.IoVecSlot, 1, TrampolineGuest.ResultSlot);
            if (errno == Errno.Success)
            {
                int read = (int)guest.ReadU32(TrampolineGuest.ResultSlot);
                observation.Contents.Add(Encoding.UTF8.GetString(guest.Bytes(TrampolineGuest.DataSlot, read)));
            }
        }

        _ = guest.Call("fd_close", (int)fd);
        return errno;
    }

    private static Errno Open(TrampolineGuest guest, string path, OFlags oflags, Rights rights, out uint fd)
    {
        fd = 0;
        int length = guest.WritePath(TrampolineGuest.PathSlot, path);
        Errno errno = guest.Call(
            "path_open",
            TrampolineGuest.Root,
            (int)LookupFlags.SymlinkFollow,
            TrampolineGuest.PathSlot,
            length,
            (int)oflags,
            (long)rights,
            0L,
            0,
            TrampolineGuest.ResultSlot);

        if (errno == Errno.Success)
        {
            fd = guest.ReadU32(TrampolineGuest.ResultSlot);
        }

        return errno;
    }

    private static Errno Describe(TrampolineGuest guest, uint fd, List<(ulong, ulong)> reached)
    {
        Errno errno = guest.Call("fd_filestat_get", (int)fd, TrampolineGuest.ResultSlot);
        if (errno == Errno.Success)
        {
            reached.Add(Identity(guest));
        }

        return errno;
    }

    /// <summary>The device and inode of the <c>filestat</c> record a call just wrote.</summary>
    private static (ulong, ulong) Identity(TrampolineGuest guest) =>
        (guest.ReadU64(TrampolineGuest.ResultSlot), guest.ReadU64(TrampolineGuest.ResultSlot + 8));

    /// <summary>Reads every entry of a directory, a buffer at a time.</summary>
    private static Errno List(TrampolineGuest guest, uint fd, Observation observation)
    {
        ulong cookie = 0;
        while (true)
        {
            Errno errno = guest.Call(
                "fd_readdir", (int)fd, TrampolineGuest.DataSlot, TrampolineGuest.DataLength, (long)cookie,
                TrampolineGuest.ResultSlot);
            if (errno != Errno.Success)
            {
                return errno;
            }

            int used = (int)guest.ReadU32(TrampolineGuest.ResultSlot);
            ReadOnlySpan<byte> buffer = guest.Bytes(TrampolineGuest.DataSlot, used);
            int offset = 0;
            bool whole = true;
            while (offset + 24 <= used)
            {
                ulong next = BinaryPrimitives.ReadUInt64LittleEndian(buffer[offset..]);
                int nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 16)..]);
                if (offset + 24 + nameLength > used)
                {
                    whole = false;
                    break;
                }

                string name = Encoding.UTF8.GetString(buffer.Slice(offset + 24, nameLength));
                if (name is not "." and not "..")
                {
                    observation.Names.Add(name);
                }

                cookie = next;
                offset += 24 + nameLength;
            }

            // A buffer the listing did not fill means the listing is over.
            if (whole && used < TrampolineGuest.DataLength)
            {
                return Errno.Success;
            }
        }
    }

    private static Errno WithPath(TrampolineGuest guest, string call, string path)
    {
        int length = guest.WritePath(TrampolineGuest.PathSlot, path);
        return guest.Call(call, TrampolineGuest.Root, TrampolineGuest.PathSlot, length);
    }

    private static Errno Rename(TrampolineGuest guest, string from, string to)
    {
        int fromLength = guest.WritePath(TrampolineGuest.PathSlot, from);
        int toLength = guest.WritePath(TrampolineGuest.SecondPathSlot, to);
        return guest.Call(
            "path_rename", TrampolineGuest.Root, TrampolineGuest.PathSlot, fromLength,
            TrampolineGuest.Root, TrampolineGuest.SecondPathSlot, toLength);
    }

    private static Errno Symlink(TrampolineGuest guest, string target, string at)
    {
        int targetLength = guest.WritePath(TrampolineGuest.PathSlot, target);
        int atLength = guest.WritePath(TrampolineGuest.SecondPathSlot, at);
        return guest.Call(
            "path_symlink", TrampolineGuest.PathSlot, targetLength, TrampolineGuest.Root,
            TrampolineGuest.SecondPathSlot, atLength);
    }

    private static Errno Link(TrampolineGuest guest, string from, string to)
    {
        int fromLength = guest.WritePath(TrampolineGuest.PathSlot, from);
        int toLength = guest.WritePath(TrampolineGuest.SecondPathSlot, to);
        return guest.Call(
            "path_link", TrampolineGuest.Root, 0, TrampolineGuest.PathSlot, fromLength,
            TrampolineGuest.Root, TrampolineGuest.SecondPathSlot, toLength);
    }
}
