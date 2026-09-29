using System.Runtime.InteropServices;
using Cap.Primitives.Interop.Unix;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Tests;

/// <summary>
/// The sizes and field offsets of every structure that crosses into a kernel.
/// </summary>
/// <remarks>
/// <para>
/// These assertions exist because a layout mistake here does not fail loudly. The confined
/// open on Linux is passed the size of its argument structure and rejects a size it does not
/// recognise, which a capability probe reads as "this kernel cannot do it" — so a structure
/// declared one field too long would silently and permanently demote every caller to the
/// slower backend with the weaker guarantee, and nothing else in the suite would notice.
/// The other structures fail more quietly still: a field read from the wrong offset produces
/// a plausible number, and an identity comparison built on it would agree when it should not.
/// </para>
/// <para>
/// Layouts are checked here without any syscall, so they are asserted on every leg of the
/// build matrix rather than only on the platform that would use them. That matters most for
/// the values that differ by architecture: an x86-64 agent cannot tell you anything about
/// what the AArch64 build would send.
/// </para>
/// </remarks>
public sealed class InteropStructLayoutTests
{
    /// <summary>
    /// The confined open's request structure is three 64-bit words, and the kernel is told so.
    /// </summary>
    [Fact]
    public void Confined_open_request_is_twenty_four_bytes()
    {
        Assert.Equal(24, Marshal.SizeOf<OpenHow>());
        Assert.Equal((nuint)24, OpenHow.Size);

        Assert.Equal(0, Marshal.OffsetOf<OpenHow>(nameof(OpenHow.Flags)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<OpenHow>(nameof(OpenHow.Mode)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<OpenHow>(nameof(OpenHow.Resolve)).ToInt32());
    }

    /// <summary>
    /// The Linux stat reply is a fixed 256 bytes with the same shape on every architecture,
    /// which is the reason this call is used in preference to the older one.
    /// </summary>
    [Fact]
    public void Linux_stat_reply_matches_the_kernel_layout()
    {
        Assert.Equal(256, StatxBuffer.StructSize);

        Assert.Equal(0, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.Mask)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.Mode)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.Inode)).ToInt32());
        Assert.Equal(136, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.DeviceMajor)).ToInt32());
        Assert.Equal(140, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.DeviceMinor)).ToInt32());

        // The fields a caller's metadata is read from. The four timestamps are the same
        // shape and sit next to each other, so a declaration one field out reports the
        // access time as the creation time -- a wrong answer that looks entirely reasonable.
        Assert.Equal(40, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.Size)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.AccessTime)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.BirthTime)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.ChangeTime)).ToInt32());
        Assert.Equal(112, Marshal.OffsetOf<StatxBuffer>(nameof(StatxBuffer.ModifyTime)).ToInt32());
    }

    /// <summary>
    /// The macOS stat reply is the 64-bit-inode layout, 144 bytes, with the inode sitting
    /// after the mode rather than before it.
    /// </summary>
    [Fact]
    public void Macos_stat_reply_matches_the_sixty_four_bit_inode_layout()
    {
        Assert.Equal(144, DarwinStat.StructSize);

        Assert.Equal(0, Marshal.OffsetOf<DarwinStat>(nameof(DarwinStat.Device)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<DarwinStat>(nameof(DarwinStat.Mode)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DarwinStat>(nameof(DarwinStat.Inode)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<DarwinStat>(nameof(DarwinStat.Size)).ToInt32());

        // The timestamps, for the reason the Linux ones are asserted: four identically
        // shaped fields in a row, where being one out is a wrong answer that reads as a
        // right one.
        Assert.Equal(32, Marshal.OffsetOf<DarwinStat>(nameof(DarwinStat.AccessTime)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<DarwinStat>(nameof(DarwinStat.ModifyTime)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<DarwinStat>(nameof(DarwinStat.BirthTime)).ToInt32());
    }

    /// <summary>
    /// The pair of times handed to the calls that set them is two sixteen-byte records on both
    /// Unix platforms, seconds first.
    /// </summary>
    /// <remarks>
    /// The call reads two of them from the pointer it is given, so a record declared a word
    /// short would give it the access time's nanoseconds as the write time's seconds.
    /// </remarks>
    [Fact]
    public void Unix_time_pair_entries_are_sixteen_bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<UnixTimespec>());
        Assert.Equal(0, Marshal.OffsetOf<UnixTimespec>(nameof(UnixTimespec.Seconds)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<UnixTimespec>(nameof(UnixTimespec.Nanoseconds)).ToInt32());
    }

    /// <summary>
    /// The macOS directory record is the 64-bit-inode layout, with the name starting at the
    /// twenty-first byte.
    /// </summary>
    /// <remarks>
    /// The record is filled in by the C library into a buffer this declaration sizes, so
    /// getting it wrong is not a misread field but a write past the end of one. The name's
    /// stated length sits immediately before the name itself; reading that from the wrong
    /// offset would take the low half of the record length and report a name of some other
    /// size entirely.
    /// </remarks>
    [Fact]
    public void Macos_directory_record_matches_the_sixty_four_bit_inode_layout()
    {
        Assert.Equal(1048, DarwinDirectoryEntry.StructSize);

        Assert.Equal(0, Marshal.OffsetOf<DarwinDirectoryEntry>(nameof(DarwinDirectoryEntry.Inode)).ToInt32());
        Assert.Equal(
            16, Marshal.OffsetOf<DarwinDirectoryEntry>(nameof(DarwinDirectoryEntry.RecordLength)).ToInt32());
        Assert.Equal(
            18, Marshal.OffsetOf<DarwinDirectoryEntry>(nameof(DarwinDirectoryEntry.NameLength)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<DarwinDirectoryEntry>(nameof(DarwinDirectoryEntry.Kind)).ToInt32());
        Assert.Equal(21, Marshal.OffsetOf<DarwinDirectoryEntry>(nameof(DarwinDirectoryEntry.Name)).ToInt32());
    }

    /// <summary>
    /// The Windows structures are laid out at natural alignment, which on a 64-bit process
    /// puts every pointer on an eight-byte boundary.
    /// </summary>
    [Fact]
    public void Windows_structures_match_the_native_layout()
    {
        Assert.Equal(nint.Size == 8 ? 16 : 8, UnicodeString.StructSize);
        Assert.Equal(nint.Size == 8 ? 48 : 24, ObjectAttributes.StructSize);
        Assert.Equal(nint.Size == 8 ? 16 : 8, IoStatusBlock.StructSize);

        Assert.Equal(0, Marshal.OffsetOf<UnicodeString>(nameof(UnicodeString.Length)).ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<UnicodeString>(nameof(UnicodeString.MaximumLength)).ToInt32());
        Assert.Equal(nint.Size, Marshal.OffsetOf<UnicodeString>(nameof(UnicodeString.Buffer)).ToInt32());

        Assert.Equal(0, Marshal.OffsetOf<ObjectAttributes>(nameof(ObjectAttributes.Length)).ToInt32());
        Assert.Equal(
            nint.Size,
            Marshal.OffsetOf<ObjectAttributes>(nameof(ObjectAttributes.RootDirectory)).ToInt32());
    }

    /// <summary>
    /// The running process reads the table for its own architecture, and is one of the three
    /// that have one.
    /// </summary>
    [Fact]
    public void Linux_constants_follow_the_running_architecture()
    {
        LinuxAbi expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => LinuxAbi.X64,
            Architecture.Arm64 => LinuxAbi.Arm64,
            Architecture.Arm => LinuxAbi.Arm,
            _ => LinuxAbi.Unsupported,
        };

        Assert.Equal(expected, LinuxConstants.Abi);
        Assert.Equal(LinuxConstants.DirectoryFlag(expected), LinuxConstants.O_DIRECTORY);
        Assert.Equal(LinuxConstants.NoFollowFlag(expected), LinuxConstants.O_NOFOLLOW);
        Assert.Equal(LinuxConstants.LargeFileFlag(expected), LinuxConstants.O_LARGEFILE);
        Assert.Equal(0x400000 | LinuxConstants.O_DIRECTORY, LinuxConstants.O_TMPFILE);
        Assert.Equal(LinuxConstants.StatxNumber(expected), LinuxConstants.SYS_statx);
        Assert.Equal(LinuxConstants.Renameat2Number(expected), LinuxConstants.SYS_renameat2);
        Assert.Equal(LinuxConstants.Getdents64Number(expected), LinuxConstants.SYS_getdents64);
        Assert.Equal(expected == LinuxAbi.Arm, LinuxConstants.HasNarrowCTypes);

        // Unlike the ones above, these are the same everywhere.
        Assert.Equal(0x80000, LinuxConstants.O_CLOEXEC);
        Assert.Equal(0x200000, LinuxConstants.O_PATH);
        Assert.Equal(-100, LinuxConstants.AT_FDCWD);
        Assert.Equal(437, LinuxConstants.SYS_openat2);
    }

    /// <summary>
    /// The open flags that differ between architectures differ in the direction they are
    /// supposed to, in every table, whichever architecture runs the test.
    /// </summary>
    /// <remarks>
    /// The ARM targets have their own values here rather than the generic ones. Swapping the
    /// two sets does not produce an error: the x86-64 value for "do not follow the final link"
    /// is the ARM value for "large file", and the x86-64 value for "must be a directory" is
    /// the ARM value for "bypass the buffer cache", so an open meant to refuse a symbolic link
    /// would instead follow it. Nothing downstream would report anything wrong. Asserted as
    /// literals from the kernel's headers, per table, because an x86-64 agent is the only
    /// place most changes are ever run.
    /// </remarks>
    [Theory]
    [InlineData(nameof(LinuxAbi.X64), 0x10000, 0x20000, 0x4000, 0x8000)]
    [InlineData(nameof(LinuxAbi.Arm64), 0x4000, 0x8000, 0x10000, 0x20000)]
    [InlineData(nameof(LinuxAbi.Arm), 0x4000, 0x8000, 0x10000, 0x20000)]
    public void Linux_open_flags_match_each_architecture(
        string abiName, int directory, int noFollow, int direct, int largeFile)
    {
        LinuxAbi abi = Enum.Parse<LinuxAbi>(abiName);

        Assert.Equal(directory, LinuxConstants.DirectoryFlag(abi));
        Assert.Equal(noFollow, LinuxConstants.NoFollowFlag(abi));
        Assert.Equal(direct, LinuxConstants.DirectFlag(abi));
        Assert.Equal(largeFile, LinuxConstants.LargeFileFlag(abi));

        // Within a table the four are distinct single bits, and none of the flags every table
        // shares lands on one of them.
        int[] bits = [directory, noFollow, direct, largeFile];
        Assert.All(bits, bit => Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)bit)));
        Assert.Equal(bits.Length, bits.Distinct().Count());

        int shared = LinuxConstants.O_PATH | LinuxConstants.O_CLOEXEC | LinuxConstants.O_CREAT |
                     LinuxConstants.O_EXCL | LinuxConstants.O_TRUNC | LinuxConstants.O_APPEND |
                     LinuxConstants.O_SYNC | LinuxConstants.O_NONBLOCK | LinuxConstants.O_RDWR |
                     LinuxConstants.O_WRONLY | 0x400000;
        Assert.All(bits, bit => Assert.Equal(0, bit & shared));
    }

    /// <summary>
    /// The syscalls called by number are numbered as each architecture's table has them.
    /// </summary>
    /// <remarks>
    /// Three tables, not two: x86-64 has its own, AArch64 has the generic one, and 32-bit ARM
    /// has an older one of its own. A number from the wrong table is either unassigned, which
    /// fails every call, or a different call entirely.
    /// </remarks>
    [Theory]
    [InlineData(nameof(LinuxAbi.X64), 332, 316, 217)]
    [InlineData(nameof(LinuxAbi.Arm64), 291, 276, 61)]
    [InlineData(nameof(LinuxAbi.Arm), 397, 382, 217)]
    public void Linux_syscall_numbers_match_each_architecture(
        string abiName, int statx, int renameat2, int getdents64)
    {
        LinuxAbi abi = Enum.Parse<LinuxAbi>(abiName);

        Assert.Equal(statx, LinuxConstants.StatxNumber(abi));
        Assert.Equal(renameat2, LinuxConstants.Renameat2Number(abi));
        Assert.Equal(getdents64, LinuxConstants.Getdents64Number(abi));
    }

    /// <summary>
    /// The calls 32-bit ARM makes by number, where the C library's default ones take a
    /// 32-bit offset or time.
    /// </summary>
    [Fact]
    public void Linux_arm_only_syscall_numbers_match_the_arm_table()
    {
        Assert.Equal(181, LinuxConstants.SYS_arm_pwrite64);
        Assert.Equal(352, LinuxConstants.SYS_arm_fallocate);
        Assert.Equal(267, LinuxConstants.SYS_arm_fstatfs64);
        Assert.Equal(412, LinuxConstants.SYS_arm_utimensat_time64);
        Assert.Equal(84u, LinuxConstants.ArmStatfs64Bytes);
        Assert.True(LinuxConstants.ArmStatfs64Bytes <= (nuint)LinuxConstants.StatfsBufferBytes);
    }

    /// <summary>
    /// A 64-bit argument passed as two 32-bit registers is split low word first, and the
    /// split loses nothing.
    /// </summary>
    [Theory]
    [InlineData(0L, 0, 0)]
    [InlineData(1L, 1, 0)]
    [InlineData(0x1_0000_0000L, 0, 1)]
    [InlineData(0x1234_5678_9ABC_DEF0L, unchecked((int)0x9ABC_DEF0), 0x1234_5678)]
    [InlineData(long.MaxValue, -1, int.MaxValue)]
    public void A_64_bit_argument_splits_into_two_registers(long value, int low, int high)
    {
        Assert.Equal((nint)low, LinuxNative.Low(value));
        Assert.Equal((nint)high, LinuxNative.High(value));
        Assert.Equal(value, ((long)(uint)LinuxNative.High(value) << 32) | (uint)LinuxNative.Low(value));
    }

    /// <summary>
    /// An architecture without a table is refused rather than handed another's values.
    /// </summary>
    /// <remarks>
    /// Several of these run .NET through community ports and would load the library without
    /// complaint. On riscv64 and loongarch64 the open flags happen to match x86-64's but every
    /// syscall number differs; ppc64le has ARM's flags and a table of its own; s390x is
    /// big-endian. The pointer size is part of the key, because the right numbers read at the
    /// wrong width are still wrong.
    /// </remarks>
    [Theory]
    [InlineData(Architecture.X64, 8, true)]
    [InlineData(Architecture.Arm64, 8, true)]
    [InlineData(Architecture.Arm, 4, true)]
    [InlineData(Architecture.X64, 4, false)]
    [InlineData(Architecture.Arm64, 4, false)]
    [InlineData(Architecture.Arm, 8, false)]
    [InlineData(Architecture.X86, 4, false)]
    [InlineData(Architecture.RiscV64, 8, false)]
    [InlineData(Architecture.LoongArch64, 8, false)]
    [InlineData(Architecture.Ppc64le, 8, false)]
    [InlineData(Architecture.S390x, 8, false)]
    [InlineData(Architecture.Armv6, 4, false)]
    [InlineData(Architecture.Wasm, 4, false)]
    public void An_architecture_without_a_table_is_refused(Architecture architecture, int pointerSize, bool supported)
    {
        Assert.Equal(supported, LinuxConstants.IsSupportedArchitecture(architecture, pointerSize));
        Assert.Equal(supported, LinuxConstants.AbiFor(architecture, pointerSize) != LinuxAbi.Unsupported);
    }

    /// <summary>
    /// Asking the unsupported table for a value throws, naming the file to extend, rather
    /// than answering with some other architecture's.
    /// </summary>
    [Fact]
    public void The_unsupported_table_has_no_values()
    {
        PlatformNotSupportedException thrown =
            Assert.Throws<PlatformNotSupportedException>(() => LinuxConstants.NoFollowFlag(LinuxAbi.Unsupported));
        Assert.Contains("LinuxConstants.cs", thrown.Message, StringComparison.Ordinal);

        Assert.Throws<PlatformNotSupportedException>(() => LinuxConstants.DirectoryFlag(LinuxAbi.Unsupported));
        Assert.Throws<PlatformNotSupportedException>(() => LinuxConstants.LargeFileFlag(LinuxAbi.Unsupported));
        Assert.Throws<PlatformNotSupportedException>(() => LinuxConstants.DirectFlag(LinuxAbi.Unsupported));
        Assert.Throws<PlatformNotSupportedException>(() => LinuxConstants.StatxNumber(LinuxAbi.Unsupported));
        Assert.Throws<PlatformNotSupportedException>(() => LinuxConstants.Renameat2Number(LinuxAbi.Unsupported));
        Assert.Throws<PlatformNotSupportedException>(() => LinuxConstants.Getdents64Number(LinuxAbi.Unsupported));
    }

    /// <summary>
    /// The Windows reply carrying the link count is 24 bytes, with the count after the two
    /// sizes.
    /// </summary>
    /// <remarks>
    /// The two flags after the count are single bytes, and the native declaration leaves the
    /// padding after them implicit; a declaration that stopped at the flags would be two bytes
    /// short of the buffer the system fills.
    /// </remarks>
    [Fact]
    public void Windows_link_count_reply_matches_the_native_layout()
    {
        Assert.Equal(24, FileStandardInformation.StructSize);
        Assert.Equal(
            16,
            Marshal.OffsetOf<FileStandardInformation>(
                nameof(FileStandardInformation.NumberOfLinks)).ToInt32());
        Assert.Equal(
            20,
            Marshal.OffsetOf<FileStandardInformation>(
                nameof(FileStandardInformation.DeletePending)).ToInt32());
    }

    /// <summary>
    /// The Windows reply carrying the times, the length and the attributes is 56 bytes, with
    /// the creation time first.
    /// </summary>
    /// <remarks>
    /// The four timestamps are four identical 64-bit fields in a row, so a declaration that
    /// omitted one or reordered them would report one time under another's name and never
    /// fail. The trailing padding matters separately: the system is told the size of the
    /// buffer, and one declared four bytes short is one it refuses to fill.
    /// </remarks>
    [Fact]
    public void Windows_times_and_size_reply_matches_the_native_layout()
    {
        Assert.Equal(56, FileNetworkOpenInformation.StructSize);

        Assert.Equal(
            0,
            Marshal.OffsetOf<FileNetworkOpenInformation>(
                nameof(FileNetworkOpenInformation.CreationTime)).ToInt32());
        Assert.Equal(
            8,
            Marshal.OffsetOf<FileNetworkOpenInformation>(
                nameof(FileNetworkOpenInformation.LastAccessTime)).ToInt32());
        Assert.Equal(
            16,
            Marshal.OffsetOf<FileNetworkOpenInformation>(
                nameof(FileNetworkOpenInformation.LastWriteTime)).ToInt32());
        Assert.Equal(
            24,
            Marshal.OffsetOf<FileNetworkOpenInformation>(
                nameof(FileNetworkOpenInformation.ChangeTime)).ToInt32());
        Assert.Equal(
            40,
            Marshal.OffsetOf<FileNetworkOpenInformation>(
                nameof(FileNetworkOpenInformation.EndOfFile)).ToInt32());
        Assert.Equal(
            48,
            Marshal.OffsetOf<FileNetworkOpenInformation>(
                nameof(FileNetworkOpenInformation.FileAttributes)).ToInt32());
    }

    /// <summary>
    /// The identity reply carries both halves of the 128-bit identifier, the high one after
    /// the low one.
    /// </summary>
    /// <remarks>
    /// Asserted because the identifier is 128 bits precisely so that it is unique on the
    /// filesystems where 64 are not enough, and a reader that kept only the low half would
    /// report two distinct files on such a filesystem as one file with two names. Silently,
    /// and on exactly the filesystems nobody runs the test suite against.
    /// </remarks>
    [Fact]
    public void Windows_identity_reply_carries_both_halves_of_the_identifier()
    {
        Assert.Equal(24, Marshal.SizeOf<FileIdInformation>());

        Assert.Equal(
            0,
            Marshal.OffsetOf<FileIdInformation>(nameof(FileIdInformation.VolumeSerialNumber)).ToInt32());
        Assert.Equal(
            8,
            Marshal.OffsetOf<FileIdInformation>(nameof(FileIdInformation.FileIdLow)).ToInt32());
        Assert.Equal(
            16,
            Marshal.OffsetOf<FileIdInformation>(nameof(FileIdInformation.FileIdHigh)).ToInt32());
    }

    /// <summary>
    /// The framework's Unix mode enumeration is numbered exactly as the mode bits are, so
    /// masking is the whole of the conversion.
    /// </summary>
    /// <remarks>
    /// The conversion is a cast, which cannot fail and cannot be wrong in a way anything
    /// notices: every file would simply be reported with somebody else's permissions. So the
    /// assumption behind the cast is asserted directly, bit by bit.
    /// </remarks>
    [Fact]
    public void Unix_mode_bits_line_up_with_the_framework_enumeration()
    {
        Assert.Equal(UnixFileMode.OtherExecute, UnixFileTypes.PermissionsFromMode(0b000_000_001));
        Assert.Equal(UnixFileMode.OtherWrite, UnixFileTypes.PermissionsFromMode(0b000_000_010));
        Assert.Equal(UnixFileMode.OtherRead, UnixFileTypes.PermissionsFromMode(0b000_000_100));
        Assert.Equal(UnixFileMode.GroupExecute, UnixFileTypes.PermissionsFromMode(0b000_001_000));
        Assert.Equal(UnixFileMode.GroupWrite, UnixFileTypes.PermissionsFromMode(0b000_010_000));
        Assert.Equal(UnixFileMode.GroupRead, UnixFileTypes.PermissionsFromMode(0b000_100_000));
        Assert.Equal(UnixFileMode.UserExecute, UnixFileTypes.PermissionsFromMode(0b001_000_000));
        Assert.Equal(UnixFileMode.UserWrite, UnixFileTypes.PermissionsFromMode(0b010_000_000));
        Assert.Equal(UnixFileMode.UserRead, UnixFileTypes.PermissionsFromMode(0b100_000_000));

        Assert.Equal(UnixFileMode.StickyBit, UnixFileTypes.PermissionsFromMode(0b001_000_000_000));
        Assert.Equal(UnixFileMode.SetGroup, UnixFileTypes.PermissionsFromMode(0b010_000_000_000));
        Assert.Equal(UnixFileMode.SetUser, UnixFileTypes.PermissionsFromMode(0b100_000_000_000));

        // The type bits sit above all of these and are not permissions. A mode carrying both
        // must report only the lower half, or a directory would be reported as having
        // permissions no caller could ever have asked for.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            UnixFileTypes.PermissionsFromMode(UnixFileTypes.S_IFDIR | 0b111_000_000));
    }

    /// <summary>
    /// macOS values are not Linux values, and the ones most likely to be confused are
    /// asserted as being different.
    /// </summary>
    [Fact]
    public void Macos_flags_do_not_share_linux_values()
    {
        Assert.NotEqual(LinuxConstants.AT_FDCWD, DarwinConstants.AT_FDCWD);
        Assert.NotEqual(LinuxConstants.O_CLOEXEC, DarwinConstants.O_CLOEXEC);
        Assert.NotEqual(LinuxConstants.AT_SYMLINK_NOFOLLOW, DarwinConstants.AT_SYMLINK_NOFOLLOW);
        Assert.Equal(-2, DarwinConstants.AT_FDCWD);
        Assert.Equal(0x0100, DarwinConstants.O_NOFOLLOW);
    }
}
