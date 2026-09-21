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
    /// The open flags that differ between architectures differ in the direction they are
    /// supposed to.
    /// </summary>
    /// <remarks>
    /// AArch64 inherited 32-bit ARM's values here rather than the generic ones. Swapping the
    /// two sets does not produce an error: the x86-64 value for "do not follow the final
    /// link" is the AArch64 value for "bypass the buffer cache", so an open meant to refuse a
    /// symbolic link would instead follow it and ask for unbuffered IO. Nothing downstream
    /// would report anything wrong.
    /// </remarks>
    [Fact]
    public void Linux_open_flags_follow_the_running_architecture()
    {
        bool arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        Assert.Equal(arm64 ? 0x4000 : 0x10000, LinuxConstants.O_DIRECTORY);
        Assert.Equal(arm64 ? 0x8000 : 0x20000, LinuxConstants.O_NOFOLLOW);

        // The two flag sets are disjoint. If they were not, a value borrowed from the wrong
        // architecture could still be a meaningful flag on this one.
        Assert.NotEqual(LinuxConstants.O_DIRECTORY, LinuxConstants.O_NOFOLLOW);

        // Unlike the two above, these are the same everywhere.
        Assert.Equal(0x80000, LinuxConstants.O_CLOEXEC);
        Assert.Equal(-100, LinuxConstants.AT_FDCWD);
    }

    /// <summary>The stat syscall is numbered differently on the two architectures.</summary>
    [Fact]
    public void Linux_syscall_numbers_follow_the_running_architecture()
    {
        bool arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        Assert.Equal(arm64 ? 291 : 332, LinuxConstants.SYS_statx);
        Assert.Equal(437, LinuxConstants.SYS_openat2);
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
