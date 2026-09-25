using Cap.Primitives;
using Moq;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// Descriptions, identities and entries made for stubs, through the public surface alone.
/// </summary>
public sealed class TestValueTests
{
    private static readonly DateTimeOffset Noon = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_default_description_is_an_empty_file_with_one_name()
    {
        CapMetadata metadata = new CapMetadataBuilder().Build();

        Assert.Equal(CapFileType.File, metadata.Type);
        Assert.Equal(0, metadata.Length);
        Assert.Equal(1, metadata.LinkCount);
        Assert.NotNull(metadata.CreationTime);
        Assert.NotNull(metadata.ChangeTime);
        Assert.Equal(TestFileIds.DefaultVolumeId, metadata.FileId.VolumeId);
    }

    [Fact]
    public void Default_permissions_are_the_running_platforms_kind()
    {
        CapPermissions file = new CapMetadataBuilder().Build().Permissions;
        CapPermissions directory = new CapMetadataBuilder().WithType(CapFileType.Directory).Build().Permissions;

        if (OperatingSystem.IsWindows())
        {
            Assert.False(file.TryGetUnixMode(out _));
            Assert.True(file.TryGetWindowsAttributes(out FileAttributes fileAttributes));
            Assert.Equal(FileAttributes.Archive, fileAttributes);
            Assert.True(directory.TryGetWindowsAttributes(out FileAttributes directoryAttributes));
            Assert.Equal(FileAttributes.Directory, directoryAttributes);
        }
        else
        {
            Assert.False(file.TryGetWindowsAttributes(out _));
            Assert.True(file.TryGetUnixMode(out UnixFileMode fileMode));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                fileMode);
            Assert.True(directory.TryGetUnixMode(out UnixFileMode directoryMode));
            Assert.True(directoryMode.HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void Every_field_set_is_reported()
    {
        CapFileId id = TestFileIds.Create(7, 42);
        CapMetadata metadata = new CapMetadataBuilder()
            .WithType(CapFileType.Directory)
            .WithLength(4096)
            .WithLastWriteTime(Noon)
            .WithLastAccessTime(Noon.AddHours(1))
            .WithCreationTime(null)
            .WithChangeTime(Noon.AddHours(2))
            .WithLinkCount(3)
            .WithFileId(id)
            .Build();

        Assert.Equal(CapFileType.Directory, metadata.Type);
        Assert.Equal(4096, metadata.Length);
        Assert.Equal(Noon, metadata.LastWriteTime);
        Assert.Equal(Noon.AddHours(1), metadata.LastAccessTime);
        Assert.Null(metadata.CreationTime);
        Assert.Equal(Noon.AddHours(2), metadata.ChangeTime);
        Assert.Equal(3, metadata.LinkCount);
        Assert.Equal(id, metadata.FileId);
        Assert.Equal(7UL, metadata.FileId.VolumeId);
        Assert.Equal((UInt128)42, metadata.FileId.NodeId);
    }

    [Fact]
    public void Setting_one_kind_of_permission_clears_the_other()
    {
        CapMetadataBuilder builder = new CapMetadataBuilder()
            .WithWindowsAttributes(FileAttributes.ReadOnly)
            .WithUnixMode(UnixFileMode.UserRead);

        CapPermissions unix = builder.Build().Permissions;
        Assert.True(unix.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(UnixFileMode.UserRead, mode);
        Assert.False(unix.TryGetWindowsAttributes(out _));

        CapPermissions windows = builder.WithWindowsAttributes(FileAttributes.Hidden).Build().Permissions;
        Assert.True(windows.TryGetWindowsAttributes(out FileAttributes attributes));
        Assert.Equal(FileAttributes.Hidden, attributes);
        Assert.False(windows.TryGetUnixMode(out _));
    }

    [Fact]
    public void Separate_builders_describe_separate_objects_unless_given_one_identity()
    {
        CapMetadata first = new CapMetadataBuilder().Build();
        CapMetadata second = new CapMetadataBuilder().Build();
        CapMetadata secondName = new CapMetadataBuilder().WithFileId(first.FileId).WithLinkCount(2).Build();

        Assert.False(first.IsSameFileAs(second));
        Assert.True(first.IsSameFileAs(secondName));
    }

    [Fact]
    public void Building_takes_a_snapshot()
    {
        CapMetadataBuilder builder = new CapMetadataBuilder().WithLength(1);
        CapMetadata before = builder.Build();

        builder.WithLength(2);

        Assert.Equal(1, before.Length);
        Assert.Equal(2, builder.Build().Length);
    }

    [Fact]
    public void Out_of_range_values_are_refused()
    {
        CapMetadataBuilder builder = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithLinkCount(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithType((CapFileType)999));
    }

    [Fact]
    public void Identities_made_from_the_same_numbers_are_equal()
    {
        Assert.Equal(TestFileIds.Create(1, 2), TestFileIds.Create(1, 2));
        Assert.NotEqual(TestFileIds.Create(1, 2), TestFileIds.Create(2, 2));
        Assert.NotEqual(TestFileIds.Next(), TestFileIds.Next());
    }

    [Fact]
    public void A_made_identity_equals_the_one_a_handle_reports_with_the_same_numbers()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("a.txt", "a");
        using Dir root = fs.OpenRoot();
        CapFileId real = root.GetMetadata("a.txt").FileId;

        Assert.Equal(real, TestFileIds.Create(real.VolumeId, real.NodeId));
    }

    [Fact]
    public void An_entry_reports_what_it_was_made_with()
    {
        CapFileId id = TestFileIds.Next();
        IDirEntry entry = TestEntries.Create("a.json", CapFileType.File, id, Mock.Of<IDir>());

        Assert.Equal("a.json", entry.Name);
        Assert.Equal(CapFileType.File, entry.Type);
        Assert.Equal(id, entry.FileId);
    }

    [Fact]
    public void An_entry_opens_and_describes_through_its_owner_by_name()
    {
        Mock<IDir> owner = new();
        ICapFile file = Mock.Of<ICapFile>();
        IDir subdirectory = Mock.Of<IDir>();
        CapMetadata metadata = new CapMetadataBuilder().WithLength(12).Build();
        owner.Setup(dir => dir.OpenFile("a.json", FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0, false, false))
            .Returns(file);
        owner.Setup(dir => dir.OpenDir("a.json", false)).Returns(subdirectory);
        owner.Setup(dir => dir.GetMetadata("a.json", false)).Returns(metadata);
        owner.Setup(dir => dir.TryGetMetadata("a.json", out metadata)).Returns(true);

        IDirEntry entry = TestEntries.Create("a.json", CapFileType.File, metadata.FileId, owner.Object);

        Assert.Same(file, entry.OpenFile());
        Assert.Same(subdirectory, entry.OpenDir());
        Assert.Equal(12, entry.GetMetadata().Length);
        Assert.True(entry.TryGetMetadata(out CapMetadata described));
        Assert.True(described.IsSameFileAs(metadata));
    }

    [Fact]
    public void Entry_arguments_are_checked()
    {
        IDir owner = Mock.Of<IDir>();

        Assert.Throws<ArgumentNullException>(() => TestEntries.Create(null!, CapFileType.File, default, owner));
        Assert.Throws<ArgumentException>(() => TestEntries.Create("", CapFileType.File, default, owner));
        Assert.Throws<ArgumentNullException>(() => TestEntries.Create("a", CapFileType.File, default, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestEntries.Create("a", (CapFileType)999, default, owner));
    }

    /// <summary>A component that finds the largest file in a directory, stubbed end to end.</summary>
    [Fact]
    public void A_stubbed_listing_and_its_descriptions_drive_a_component()
    {
        Mock<IDir> reports = new();
        reports.Setup(dir => dir.EnumerateEntries()).Returns(
        [
            TestEntries.Create("small.log", CapFileType.File, TestFileIds.Next(), reports.Object),
            TestEntries.Create("large.log", CapFileType.File, TestFileIds.Next(), reports.Object),
            TestEntries.Create("archive", CapFileType.Directory, TestFileIds.Next(), reports.Object),
        ]);
        reports.Setup(dir => dir.GetMetadata("small.log", false)).Returns(new CapMetadataBuilder().WithLength(10).Build());
        reports.Setup(dir => dir.GetMetadata("large.log", false)).Returns(new CapMetadataBuilder().WithLength(900).Build());

        string? largest = reports.Object.EnumerateEntries()
            .Where(entry => entry.Type == CapFileType.File)
            .MaxBy(entry => entry.GetMetadata().Length)?
            .Name;

        Assert.Equal("large.log", largest);
        reports.Verify(dir => dir.GetMetadata("archive", It.IsAny<bool>()), Times.Never);
    }
}
