using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Tests;

/// <summary>
/// The value semantics of a snapshot, its identity and its permissions, asserted without a
/// filesystem.
/// </summary>
/// <remarks>
/// These are the cases no host can produce on demand. A filesystem that issues identifiers
/// needing more than 64 bits is the one where a truncating comparison goes wrong, and it is
/// not a filesystem any build agent has mounted; a platform that reports neither kind of
/// permission is the default value of a struct, which no call returns. Both are built here
/// directly, because the alternative is shipping the handling for them untested.
/// </remarks>
public sealed class CapMetadataValueTests
{
    /// <summary>
    /// Two files whose identifiers differ only above the low 64 bits are different files.
    /// </summary>
    /// <remarks>
    /// The whole reason the identifier is carried at 128 bits. Windows issues them that wide
    /// because the narrower ones it used to issue are not unique on every filesystem it
    /// supports, so an implementation that kept the low half would report two distinct files
    /// as one — and would do it silently, on exactly the filesystems this suite never runs
    /// against.
    /// </remarks>
    [Fact]
    public void Identifiers_differing_only_in_their_high_half_are_different_files()
    {
        CapMetadata low = Describe(volumeId: 7, nodeId: new UInt128(0, 42));
        CapMetadata high = Describe(volumeId: 7, nodeId: new UInt128(1, 42));

        Assert.False(low.IsSameFileAs(high));
        Assert.NotEqual(low.FileId, high.FileId);
        Assert.NotEqual(low.FileId.GetHashCode(), high.FileId.GetHashCode());
    }

    /// <summary>
    /// Two objects with the same identity on different filesystems are different objects.
    /// </summary>
    /// <remarks>
    /// An inode number is unique within its filesystem and nowhere else, so two filesystems
    /// routinely hold objects numbered alike. A comparison that dropped the volume would
    /// report them as one file, which for a caller copying a tree means silently skipping
    /// one of them.
    /// </remarks>
    [Fact]
    public void The_same_identity_on_different_volumes_is_a_different_file()
    {
        CapMetadata here = Describe(volumeId: 1, nodeId: new UInt128(0, 99));
        CapMetadata there = Describe(volumeId: 2, nodeId: new UInt128(0, 99));

        Assert.False(here.IsSameFileAs(there));
        Assert.NotEqual(here.FileId, there.FileId);
    }

    /// <summary>The same identity twice is the same file, by every route.</summary>
    [Fact]
    public void The_same_identity_is_the_same_file()
    {
        CapMetadata first = Describe(volumeId: 3, nodeId: new UInt128(ulong.MaxValue, 5));
        CapMetadata second = Describe(volumeId: 3, nodeId: new UInt128(ulong.MaxValue, 5));

        Assert.True(first.IsSameFileAs(second));
        Assert.True(first.FileId == second.FileId);
        Assert.False(first.FileId != second.FileId);
        Assert.Equal(first.FileId, second.FileId);
        Assert.Equal(first.FileId.GetHashCode(), second.FileId.GetHashCode());
        Assert.True(first.FileId.Equals((object)second.FileId));
    }

    /// <summary>
    /// An identifier is usable as a key, which is what a walk detecting hard links needs.
    /// </summary>
    [Fact]
    public void Identifiers_work_as_set_members()
    {
        HashSet<CapFileId> seen =
        [
            Describe(volumeId: 1, nodeId: new UInt128(0, 1)).FileId,
            Describe(volumeId: 1, nodeId: new UInt128(0, 2)).FileId,
            Describe(volumeId: 1, nodeId: new UInt128(0, 1)).FileId,
        ];

        Assert.Equal(2, seen.Count);
        Assert.Contains(Describe(volumeId: 1, nodeId: new UInt128(0, 2)).FileId, seen);
        Assert.DoesNotContain(Describe(volumeId: 2, nodeId: new UInt128(0, 2)).FileId, seen);
    }

    /// <summary>A Unix snapshot reports a mode and refuses to report attributes.</summary>
    [Fact]
    public void A_unix_snapshot_reports_a_mode_and_no_attributes()
    {
        CapPermissions permissions = Describe(unixMode: UnixFileMode.UserRead | UnixFileMode.GroupWrite)
            .Permissions;

        Assert.True(permissions.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupWrite, mode);

        Assert.False(permissions.TryGetWindowsAttributes(out FileAttributes attributes));
        Assert.Equal(default, attributes);
    }

    /// <summary>A Windows snapshot reports attributes and refuses to report a mode.</summary>
    [Fact]
    public void A_windows_snapshot_reports_attributes_and_no_mode()
    {
        CapPermissions permissions = Describe(
            windowsAttributes: FileAttributes.ReadOnly | FileAttributes.Hidden).Permissions;

        Assert.True(permissions.TryGetWindowsAttributes(out FileAttributes attributes));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden, attributes);

        Assert.False(permissions.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(default, mode);
    }

    /// <summary>
    /// A value that came from no filesystem claims nothing, rather than claiming zero.
    /// </summary>
    /// <remarks>
    /// The difference matters: a mode of zero is a real and meaningful mode — a file nobody
    /// may touch — so a default that reported one would be reporting a fact rather than an
    /// absence.
    /// </remarks>
    [Fact]
    public void A_default_value_claims_no_permissions_at_all()
    {
        CapPermissions permissions = default;

        Assert.False(permissions.TryGetUnixMode(out _));
        Assert.False(permissions.TryGetWindowsAttributes(out _));
        Assert.Equal("none", permissions.ToString());

        CapMetadata metadata = default;

        Assert.Equal(CapFileType.Unknown, metadata.Type);
        Assert.Null(metadata.CreationTime);
        Assert.Null(metadata.ChangeTime);
        Assert.False(metadata.Permissions.TryGetUnixMode(out _));
        Assert.False(metadata.Permissions.TryGetWindowsAttributes(out _));
    }

    /// <summary>The snapshot hands back exactly what the platform layer put in it.</summary>
    [Fact]
    public void The_fields_are_carried_through_unchanged()
    {
        DateTimeOffset accessed = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        DateTimeOffset written = new(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);
        DateTimeOffset created = new(2019, 11, 12, 13, 14, 15, TimeSpan.Zero);
        DateTimeOffset changed = new(2022, 3, 4, 5, 6, 7, TimeSpan.Zero);

        CapMetadata metadata = new(new CapNodeStat(
            CapFileType.Fifo,
            volumeId: 11,
            nodeId: new UInt128(2, 3),
            length: 4096,
            accessed,
            written,
            created,
            changed,
            linkCount: 3,
            UnixFileMode.OtherExecute,
            windowsAttributes: null));

        Assert.Equal(CapFileType.Fifo, metadata.Type);
        Assert.Equal(4096, metadata.Length);
        Assert.Equal(accessed, metadata.LastAccessTime);
        Assert.Equal(written, metadata.LastWriteTime);
        Assert.Equal(created, metadata.CreationTime);
        Assert.Equal(changed, metadata.ChangeTime);
        Assert.Equal(3, metadata.LinkCount);
        Assert.Equal(11ul, metadata.FileId.VolumeId);
        Assert.Equal(new UInt128(2, 3), metadata.FileId.NodeId);
    }

    /// <summary>An identifier renders both halves, separately, for a log line.</summary>
    [Fact]
    public void An_identifier_renders_both_halves()
    {
        CapFileId identity = Describe(volumeId: 0xAB, nodeId: new UInt128(0, 0xCD)).FileId;

        Assert.Equal("ab:cd", identity.ToString());
    }

    private static CapMetadata Describe(
        ulong volumeId = 1,
        UInt128 nodeId = default,
        UnixFileMode? unixMode = null,
        FileAttributes? windowsAttributes = null) =>
        new(new CapNodeStat(
            CapFileType.File,
            volumeId,
            nodeId,
            length: 0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            creationTime: null,
            changeTime: null,
            linkCount: 1,
            unixMode,
            windowsAttributes));
}
