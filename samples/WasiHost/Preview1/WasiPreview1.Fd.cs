using System.Buffers.Binary;
using System.Text;
using Cap.Primitives;
using Cap.Std;

namespace WasiHost.Preview1;

public sealed partial class WasiPreview1
{
    /// <summary>The size of a <c>filestat</c> record in guest memory.</summary>
    private const int FilestatSize = 64;

    /// <summary>The size of a <c>dirent</c> header, which the entry's name follows.</summary>
    private const int DirentHeaderSize = 24;

    private byte[] _scratch = [];

    private Errno FdClose(uint fd)
    {
        if (!_descriptors.Remove(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        // The host's own streams are the host's to close.
        if (descriptor is not StreamDescriptor)
        {
            descriptor.Dispose();
        }

        return Errno.Success;
    }

    private Errno FdRenumber(uint from, uint to)
    {
        if (!_descriptors.TryGetValue(from, out Descriptor? moving) || !_descriptors.ContainsKey(to))
        {
            return Errno.BadF;
        }

        if (from != to)
        {
            _ = FdClose(to);
            _descriptors.Remove(from);
            _descriptors[to] = moving;
        }

        return Errno.Success;
    }

    private Errno FdAdvise(uint fd, uint advice)
    {
        if (!TryGet(fd, out FileDescriptor? _))
        {
            return Errno.BadF;
        }

        // Advice is a hint, and a hint the platform is not given costs nothing. The values are
        // still checked, so that a guest passing nonsense hears about it.
        return advice <= 5 ? Errno.Success : Errno.Inval;
    }

    /// <remarks>
    /// <see cref="CapFile"/> has no way to reserve space without also changing the length,
    /// and allocating by growing the file would change what the guest sees, so this is
    /// reported as unsupported.
    /// </remarks>
    private Errno FdAllocate(uint fd) => TryGet(fd, out FileDescriptor? _) ? Errno.NotSup : Errno.BadF;

    private Errno FdSync(uint fd, Rights needed)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        if ((descriptor.RightsBase & needed) == 0)
        {
            return Errno.NotCapable;
        }

        return descriptor switch
        {
            FileDescriptor file => ErrorMapping.Run(() => file.File.Flush(toDisk: true)),

            // Committing a directory's own entries is something the library does inside its
            // durable writes, and does not offer on its own.
            DirectoryDescriptor => Errno.NotSup,
            _ => Errno.Inval,
        };
    }

    private Errno FdFdstatGet(GuestMemory memory, uint fd, uint address)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        if (!memory.TrySlice(address, 24, out Span<byte> record))
        {
            return Errno.Fault;
        }

        record.Clear();
        record[0] = (byte)descriptor.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(record[2..], (ushort)descriptor.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], (ulong)descriptor.RightsBase);
        BinaryPrimitives.WriteUInt64LittleEndian(record[16..], (ulong)descriptor.RightsInheriting);
        return Errno.Success;
    }

    /// <remarks>
    /// The non-blocking flag and appending can change. Appending on a file is changed on the
    /// <see cref="CapFile"/>, and a file whose handle cannot write keeps the flag only as the
    /// guest's record, since it has no writes for appending to place. The synchronous-write
    /// flags are fixed when a <see cref="CapFile"/> is opened and cannot be changed on the
    /// open file.
    /// </remarks>
    private Errno FdFdstatSetFlags(uint fd, FdFlags flags)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        const FdFlags Changeable = FdFlags.NonBlock | FdFlags.Append;
        if ((flags & ~Changeable) != (descriptor.Flags & ~Changeable))
        {
            return Errno.NotSup;
        }

        bool appends = (flags & FdFlags.Append) != 0;
        if (descriptor is FileDescriptor file &&
            (file.File.Access & FileAccess.Write) != 0 &&
            file.File.IsAppending != appends)
        {
            Errno error = ErrorMapping.Run(() => file.File.IsAppending = appends);
            if (error != Errno.Success)
            {
                return error;
            }
        }

        descriptor.Flags = flags;
        return Errno.Success;
    }

    private Errno FdFdstatSetRights(uint fd, Rights rightsBase, Rights rightsInheriting)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        // Rights only narrow. Widening them would not reach anything new — the capability
        // underneath decides that — but it would make the descriptor claim what it cannot do.
        if ((rightsBase & ~descriptor.RightsBase) != 0 || (rightsInheriting & ~descriptor.RightsInheriting) != 0)
        {
            return Errno.NotCapable;
        }

        descriptor.RightsBase = rightsBase;
        descriptor.RightsInheriting = rightsInheriting;
        return Errno.Success;
    }

    private Errno FdFilestatGet(GuestMemory memory, uint fd, uint address)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        CapMetadata metadata = default;
        Errno error = descriptor switch
        {
            FileDescriptor file => ErrorMapping.Run(() => metadata = file.File.GetMetadata()),
            DirectoryDescriptor directory => ErrorMapping.Run(() => metadata = directory.Dir.GetMetadata()),
            _ => Errno.Success,
        };

        if (error != Errno.Success)
        {
            return error;
        }

        return descriptor is StreamDescriptor
            ? WriteFilestat(memory, address, FileType.CharacterDevice, null)
            : WriteFilestat(memory, address, ToFileType(metadata.Type), metadata);
    }

    private Errno FdFilestatSetSize(uint fd, long size)
    {
        if (!TryGet(fd, out FileDescriptor? file))
        {
            return Errno.BadF;
        }

        if ((file.RightsBase & Rights.FdFilestatSetSize) == 0)
        {
            return Errno.NotCapable;
        }

        return size < 0 ? Errno.Inval : ErrorMapping.Run(() => file.File.SetLength(size));
    }

    private Errno FdFilestatSetTimes(uint fd, ulong atim, ulong mtim, FstFlags flags)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        if (ValidateTimes(flags) is { } invalid)
        {
            return invalid;
        }

        if ((descriptor.RightsBase & Rights.FdFilestatSetTimes) == 0)
        {
            return Errno.NotCapable;
        }

        CapFileTime lastAccess = ToFileTime(atim, flags, FstFlags.Atim, FstFlags.AtimNow);
        CapFileTime lastWrite = ToFileTime(mtim, flags, FstFlags.Mtim, FstFlags.MtimNow);
        return descriptor switch
        {
            FileDescriptor file => ErrorMapping.Run(() => file.File.SetTimes(lastAccess, lastWrite)),
            DirectoryDescriptor directory => ErrorMapping.Run(() => directory.Dir.SetTimes(lastAccess, lastWrite)),
            _ => Errno.NotSup,
        };
    }

    /// <summary>
    /// Turns one of a <c>*_filestat_set_times</c> call's times into what the library takes:
    /// the value given, the time of the change, or no change.
    /// </summary>
    private static CapFileTime ToFileTime(ulong nanoseconds, FstFlags flags, FstFlags given, FstFlags now)
    {
        if ((flags & now) != 0)
        {
            return CapFileTime.Now;
        }

        return (flags & given) != 0
            ? CapFileTime.At(DateTimeOffset.UnixEpoch.AddTicks((long)(nanoseconds / 100)))
            : CapFileTime.Unchanged;
    }

    private static Errno? ValidateTimes(FstFlags flags)
    {
        bool both = ((flags & FstFlags.Atim) != 0 && (flags & FstFlags.AtimNow) != 0) ||
                    ((flags & FstFlags.Mtim) != 0 && (flags & FstFlags.MtimNow) != 0);
        bool unknown = (flags & ~(FstFlags.Atim | FstFlags.AtimNow | FstFlags.Mtim | FstFlags.MtimNow)) != 0;
        return both || unknown ? Errno.Inval : null;
    }

    /// <summary>
    /// <c>fd_read</c> and <c>fd_pread</c>: an unpositioned read starts at, and moves, the
    /// descriptor's position, while a positioned one leaves it alone.
    /// </summary>
    private Errno FdRead(GuestMemory memory, uint fd, uint vectors, uint count, long? offset, uint resultAddress)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        Errno error = memory.ReadIoVecs(vectors, count, out (uint Address, uint Length)[] buffers);
        if (error != Errno.Success)
        {
            return error;
        }

        if (descriptor is StreamDescriptor stream)
        {
            if (offset is not null)
            {
                return Errno.SPipe;
            }

            uint streamTotal = 0;
            foreach ((uint address, uint length) in buffers)
            {
                if (!memory.TrySlice(address, length, out Span<byte> destination))
                {
                    return Errno.Fault;
                }

                int read = stream.Stream.Read(destination);
                streamTotal += (uint)read;
                if (read < destination.Length)
                {
                    break;
                }
            }

            return memory.WriteU32(resultAddress, streamTotal);
        }

        if (descriptor is not FileDescriptor file || (file.RightsBase & Rights.FdRead) == 0)
        {
            return Errno.BadF;
        }

        if (offset < 0)
        {
            return Errno.Inval;
        }

        long position = offset ?? file.Position;
        uint total = 0;
        foreach ((uint address, uint length) in buffers)
        {
            if (!memory.TrySlice(address, length, out Span<byte> destination))
            {
                return Errno.Fault;
            }

            int read = 0;
            int wanted = destination.Length;
            error = ErrorMapping.Run(() => read = file.File.Read(Scratch(wanted), position));
            if (error != Errno.Success)
            {
                return error;
            }

            _scratch.AsSpan(0, read).CopyTo(destination);
            position += read;
            total += (uint)read;
            if (read < destination.Length)
            {
                break;
            }
        }

        if (offset is null)
        {
            file.Position = position;
        }

        return memory.WriteU32(resultAddress, total);
    }

    /// <summary>
    /// <c>fd_write</c> and <c>fd_pwrite</c>, positioned the same way as the reads.
    /// </summary>
    private Errno FdWrite(GuestMemory memory, uint fd, uint vectors, uint count, long? offset, uint resultAddress)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        Errno error = memory.ReadIoVecs(vectors, count, out (uint Address, uint Length)[] buffers);
        if (error != Errno.Success)
        {
            return error;
        }

        if (descriptor is StreamDescriptor stream)
        {
            if (offset is not null)
            {
                return Errno.SPipe;
            }

            uint streamTotal = 0;
            foreach ((uint address, uint length) in buffers)
            {
                if (!memory.TrySlice(address, length, out Span<byte> source))
                {
                    return Errno.Fault;
                }

                stream.Stream.Write(source);
                streamTotal += length;
            }

            stream.Stream.Flush();
            return memory.WriteU32(resultAddress, streamTotal);
        }

        if (descriptor is not FileDescriptor file || (file.RightsBase & Rights.FdWrite) == 0)
        {
            return Errno.BadF;
        }

        if (offset < 0)
        {
            return Errno.Inval;
        }

        long position = offset ?? file.Position;
        uint total = 0;
        foreach ((uint address, uint length) in buffers)
        {
            if (!memory.TrySlice(address, length, out Span<byte> source))
            {
                return Errno.Fault;
            }

            byte[] copy = source.ToArray();
            error = ErrorMapping.Run(() => file.File.Write(copy, position));
            if (error != Errno.Success)
            {
                return error;
            }

            position += length;
            total += length;
        }

        if (offset is null)
        {
            // A file opened to append writes at its end whatever position it is given, so the
            // position that follows is wherever the end now is.
            if ((file.Flags & FdFlags.Append) != 0)
            {
                error = ErrorMapping.Run(() => position = file.File.Length);
                if (error != Errno.Success)
                {
                    return error;
                }
            }

            file.Position = position;
        }

        return memory.WriteU32(resultAddress, total);
    }

    /// <summary>
    /// A host buffer for one read. A read goes into host memory and is then copied into the
    /// guest's, because the guest's span cannot be carried into the delegate that performs it.
    /// </summary>
    private Span<byte> Scratch(int length)
    {
        if (_scratch.Length < length)
        {
            _scratch = new byte[length];
        }

        return _scratch.AsSpan(0, length);
    }

    private Errno FdSeek(GuestMemory memory, uint fd, long offset, uint whence, uint resultAddress)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        if (descriptor is StreamDescriptor)
        {
            return Errno.SPipe;
        }

        if (descriptor is not FileDescriptor file)
        {
            return Errno.BadF;
        }

        Rights needed = whence == (uint)Whence.Cur && offset == 0 ? Rights.FdTell : Rights.FdSeek;
        if ((file.RightsBase & needed) == 0)
        {
            return Errno.NotCapable;
        }

        long origin = 0;
        switch ((Whence)whence)
        {
            case Whence.Set:
                break;
            case Whence.Cur:
                origin = file.Position;
                break;
            case Whence.End:
                Errno error = ErrorMapping.Run(() => origin = file.File.Length);
                if (error != Errno.Success)
                {
                    return error;
                }

                break;
            default:
                return Errno.Inval;
        }

        long target = origin + offset;
        if (target < 0)
        {
            return Errno.Inval;
        }

        file.Position = target;
        return memory.WriteU64(resultAddress, (ulong)target);
    }

    private Errno FdTell(GuestMemory memory, uint fd, uint resultAddress)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        if (descriptor is StreamDescriptor)
        {
            return Errno.SPipe;
        }

        if (descriptor is not FileDescriptor file)
        {
            return Errno.BadF;
        }

        return (file.RightsBase & Rights.FdTell) == 0
            ? Errno.NotCapable
            : memory.WriteU64(resultAddress, (ulong)file.Position);
    }

    private Errno FdPrestatGet(GuestMemory memory, uint fd, uint address)
    {
        if (!TryGet(fd, out DirectoryDescriptor? directory) || directory.PreopenName is null)
        {
            return Errno.BadF;
        }

        Errno error = memory.WriteU32(address, 0);
        return error != Errno.Success
            ? error
            : memory.WriteU32(address + 4, (uint)Encoding.UTF8.GetByteCount(directory.PreopenName));
    }

    private Errno FdPrestatDirName(GuestMemory memory, uint fd, uint address, uint length)
    {
        if (!TryGet(fd, out DirectoryDescriptor? directory) || directory.PreopenName is null)
        {
            return Errno.BadF;
        }

        byte[] name = Encoding.UTF8.GetBytes(directory.PreopenName);
        return length < name.Length ? Errno.NameTooLong : memory.WriteBytes(address, length, name, out _);
    }

    /// <summary>
    /// Lists a directory from a cookie, filling the guest's buffer with as many entries as fit
    /// and cutting the last one short when it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cookie is an entry's position in the listing, which is read afresh on each call:
    /// <see cref="Dir.EnumerateEntries"/> has no resumable position to hand out. A directory
    /// that changes between calls can therefore repeat or skip an entry across the boundary,
    /// which POSIX also permits of a directory changed during a listing.
    /// </para>
    /// <para>
    /// Each entry's inode comes from the listing itself, which records it alongside the name,
    /// so an entry costs no lookup and its inode describes the same entry the name and type
    /// do.
    /// </para>
    /// <para>
    /// WASI lists <c>.</c> and <c>..</c> first. The library lists neither, because neither is a
    /// name beneath the handle, so they are added here; <c>..</c> is given inode 0, since
    /// asking what lies above a handle is exactly what a handle cannot do.
    /// </para>
    /// </remarks>
    private Errno FdReaddir(GuestMemory memory, uint fd, uint buffer, uint length, ulong cookie, uint resultAddress)
    {
        if (!_descriptors.TryGetValue(fd, out Descriptor? descriptor))
        {
            return Errno.BadF;
        }

        if (descriptor is not DirectoryDescriptor directory)
        {
            return Errno.NotDir;
        }

        if ((directory.RightsBase & Rights.FdReaddir) == 0)
        {
            return Errno.NotCapable;
        }

        List<(string Name, FileType Type, ulong Inode)> entries = [];
        Errno error = ErrorMapping.Run(() =>
        {
            entries.Add((".", FileType.Directory, Inode(directory.Dir.GetMetadata())));
            entries.Add(("..", FileType.Directory, 0));
            foreach (DirEntry entry in directory.Dir.EnumerateEntries())
            {
                entries.Add((entry.Name, ToFileType(entry.Type), (ulong)entry.FileId.NodeId));
            }
        });

        if (error != Errno.Success)
        {
            return error;
        }

        if (!memory.TrySlice(buffer, length, out Span<byte> output))
        {
            return Errno.Fault;
        }

        int used = 0;
        for (ulong i = cookie; i < (ulong)entries.Count && used < output.Length; i++)
        {
            (string name, FileType type, ulong inode) = entries[(int)i];
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);

            Span<byte> record = new byte[DirentHeaderSize + nameBytes.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(record, i + 1);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], inode);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], (uint)nameBytes.Length);
            record[20] = (byte)type;
            nameBytes.CopyTo(record[DirentHeaderSize..]);

            int fits = Math.Min(record.Length, output.Length - used);
            record[..fits].CopyTo(output[used..]);
            used += fits;
        }

        return memory.WriteU32(resultAddress, (uint)used);
    }

    private static ulong Inode(in CapMetadata metadata) => (ulong)metadata.FileId.NodeId;

    /// <summary>
    /// Writes a <c>filestat</c> record. A status-change time the filesystem does not record is
    /// written as zero, which is the only way the record has of saying nothing.
    /// </summary>
    private static Errno WriteFilestat(GuestMemory memory, uint address, FileType type, CapMetadata? metadata)
    {
        if (!memory.TrySlice(address, FilestatSize, out Span<byte> record))
        {
            return Errno.Fault;
        }

        record.Clear();
        record[16] = (byte)type;
        if (metadata is CapMetadata known)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record, known.FileId.VolumeId);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], Inode(known));
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..], (ulong)known.LinkCount);
            BinaryPrimitives.WriteUInt64LittleEndian(record[32..], (ulong)known.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(record[40..], Nanoseconds(known.LastAccessTime));
            BinaryPrimitives.WriteUInt64LittleEndian(record[48..], Nanoseconds(known.LastWriteTime));
            if (known.ChangeTime is { } changed)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(record[56..], Nanoseconds(changed));
            }
        }

        return Errno.Success;
    }

    private static ulong Nanoseconds(DateTimeOffset time) =>
        time <= DateTimeOffset.UnixEpoch ? 0 : (ulong)(time - DateTimeOffset.UnixEpoch).Ticks * 100;

    private static FileType ToFileType(CapFileType type) => type switch
    {
        CapFileType.File => FileType.RegularFile,
        CapFileType.Directory => FileType.Directory,
        CapFileType.Symlink => FileType.SymbolicLink,
        CapFileType.Socket => FileType.SocketStream,
        CapFileType.CharDevice => FileType.CharacterDevice,
        CapFileType.BlockDevice => FileType.BlockDevice,
        _ => FileType.Unknown,
    };
}
