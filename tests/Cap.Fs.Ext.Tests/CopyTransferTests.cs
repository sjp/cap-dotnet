using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;
using Cap.Tests.Fakes;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Which way a copy moves a file's contents: shared storage, a copy inside the kernel, or reads
/// and writes, and what it does when one of those is not there.
/// </summary>
/// <remarks>
/// <para>
/// Against a simulated filesystem whose shortcuts are each switched on by the test and counted,
/// so that the choice is shown on every platform and not only on whichever filesystem the
/// machine running the suite happens to have. The real shortcuts are exercised against the
/// disk by <see cref="CopyTests"/>.
/// </para>
/// <para>
/// Whichever way the contents went, they have to arrive whole: every case checks the bytes.
/// </para>
/// </remarks>
public sealed class CopyTransferTests
{
    private const int Length = 10_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_is_cloned_where_the_filesystem_shares_storage(bool asynchronous)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.SharesStorage = true;
        ops.CopiesRanges = true;

        CopyReport report = await Copy(ops, asynchronous);

        Assert.Equal(1, ops.Clones);
        Assert.Equal(0, ops.RangeCopyAttempts);
        Assert.Empty(ops.Reservations);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_is_copied_inside_the_kernel_a_piece_at_a_time(bool asynchronous)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.CopiesRanges = true;
        ops.RangeCopyLimit = 3000;

        CopyReport report = await Copy(ops, asynchronous);

        // Four pieces of at most 3000 bytes, and one more asked for at the end that copies
        // nothing; the read that confirms the end finds nothing either, so nothing is reserved.
        Assert.Equal(4, ops.RangeCopies);
        Assert.Empty(ops.Reservations);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>
    /// A kernel copy that reports nothing copied before the end, as one from <c>/proc</c> does,
    /// is not taken for the end: the rest is read and written.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kernel_copy_that_stops_early_is_finished_by_reading(bool asynchronous)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.CopiesRanges = true;
        ops.RangeCopyLimit = 3000;
        ops.RangeCopyEndsEarlyAt = 3000;

        CopyReport report = await Copy(ops, asynchronous);

        Assert.Equal(1, ops.RangeCopies);
        Assert.Equal(2, ops.RangeCopyAttempts);
        Assert.Equal([Length], ops.Reservations);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>
    /// Read and written, the destination has its room reserved first, up to the length the
    /// source was described as having.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_read_and_written_has_its_room_reserved_first(bool asynchronous)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));

        CopyReport report = await Copy(ops, asynchronous);

        Assert.Equal([Length], ops.Reservations);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>An empty file has nothing to reserve room for.</summary>
    [Fact]
    public async Task An_empty_file_reserves_nothing()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree([]);

        CopyReport report = await Copy(ops, asynchronous: false);

        Assert.Empty(ops.Reservations);
        Assert.Equal(0, report.Bytes);
        Assert.Empty(Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>
    /// A shortcut the platform says it does not have is asked for once in a copy, not once per
    /// file.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_shortcut_the_platform_lacks_is_asked_for_once_per_copy(bool asynchronous)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        fs.AddFile("/tree/source/second.bin").WriteAt(Contents(Length), 0);
        fs.AddFile("/tree/source/third.bin").WriteAt(Contents(Length), 0);

        CopyReport report = await Copy(ops, asynchronous);

        Assert.Equal(3, report.Files);
        Assert.Equal(1, ops.CloneAttempts);
        Assert.Equal(1, ops.RangeCopyAttempts);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/third.bin"));
    }

    /// <summary>
    /// Asked to keep holes, the copy writes only the stretches the source stores data for,
    /// marks the destination sparse before leaving the first hole, and gives it the source's
    /// length although the source ends in a hole.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Holes_are_left_unwritten_when_asked(bool asynchronous, bool kernel)
    {
        byte[] contents = new byte[Length];
        Contents(10).CopyTo(contents, 100);
        Contents(20).CopyTo(contents, 5000);
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(contents);
        ops.DataStretches = node => node.Length == Length ? [(100, 110), (5000, 5020)] : [];
        ops.CopiesRanges = kernel;

        CopyReport report = await Copy(ops, asynchronous, new CopyOptions { PreserveSparseness = true });

        Assert.Equal(1, ops.SparseMarks);
        Assert.Empty(ops.Reservations);
        Assert.Equal(kernel ? 2 : 0, ops.RangeCopies);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(contents, Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>A source made of nothing but a hole becomes a destination of the same length.</summary>
    [Fact]
    public async Task A_file_that_is_all_hole_keeps_its_length()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(new byte[Length]);
        ops.DataStretches = _ => [];

        CopyReport report = await Copy(ops, asynchronous: false, new CopyOptions { PreserveSparseness = true });

        Assert.Equal(1, ops.SparseMarks);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(new byte[Length], Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>Holes are written as zeroes unless the copy is asked to keep them.</summary>
    [Fact]
    public async Task Holes_are_written_by_default()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(new byte[Length]);
        ops.DataStretches = _ => [];

        CopyReport report = await Copy(ops, asynchronous: false);

        Assert.Equal(0, ops.SparseMarks);
        Assert.Equal([Length], ops.Reservations);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(new byte[Length], Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>
    /// Where a filesystem cannot say where a file's data is, keeping holes copies the file whole,
    /// and reserves its room as a copy that keeps none would.
    /// </summary>
    [Fact]
    public async Task Keeping_holes_where_none_can_be_found_copies_the_file_whole()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));

        CopyReport report = await Copy(ops, asynchronous: false, new CopyOptions { PreserveSparseness = true });

        Assert.Equal(0, ops.SparseMarks);
        Assert.Equal([Length], ops.Reservations);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>
    /// A filesystem that reports a stretch of data with no length is not asked again and again:
    /// the rest of the file is copied whole.
    /// </summary>
    [Fact]
    public async Task An_empty_stretch_of_data_ends_the_search_for_holes()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.DataStretches = _ => [(100, 100)];

        CopyReport report = await Copy(ops, asynchronous: false, new CopyOptions { PreserveSparseness = true });

        Assert.Equal(Length, report.Bytes);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>
    /// Where a clone is made by name, a copy that carries permissions makes the file that way,
    /// and the clone is given the time it was made, as a written file would have.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_file_is_cloned_by_name_when_permissions_are_carried(bool asynchronous, bool overwrite)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.ClonesByName = true;
        fs.Find("/tree/source/file.bin")!.LastWriteTime = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (overwrite)
        {
            fs.AddFile("/tree/copy/file.bin").WriteAt([9, 9, 9], 0);
        }

        CopyReport report = await Copy(
            ops, asynchronous, new CopyOptions { PreservePermissions = true, Overwrite = overwrite });

        Assert.Equal(1, ops.Clones);
        Assert.Equal(0, ops.RangeCopyAttempts);
        Assert.Equal(Length, report.Bytes);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
        Assert.Equal(fs.Now, fs.Find("/tree/copy/file.bin")!.LastWriteTime);
        Assert.Equal(["file.bin"], fs.Find("/tree/copy")!.Entries.Keys);
    }

    /// <summary>
    /// A clone by name carries the source's mode, so a copy that leaves permissions to the
    /// destination does not make one.
    /// </summary>
    [Fact]
    public async Task A_file_is_not_cloned_by_name_when_permissions_are_left_alone()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.ClonesByName = true;

        _ = await Copy(ops, asynchronous: false);

        Assert.Equal(0, ops.Clones);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>A clone by name keeps the source's times when the copy is asked to.</summary>
    [Fact]
    public async Task A_file_cloned_by_name_keeps_the_source_times_when_asked()
    {
        DateTimeOffset written = new(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.ClonesByName = true;
        fs.Find("/tree/source/file.bin")!.LastWriteTime = written;

        _ = await Copy(ops, asynchronous: false, new CopyOptions { PreservePermissions = true, PreserveTimes = true });

        Assert.Equal(1, ops.Clones);
        Assert.Equal(written, fs.Find("/tree/copy/file.bin")!.LastWriteTime);
    }

    /// <summary>Copying a single file takes the same shortcuts as copying a tree.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_single_file_copy_shares_storage_where_it_can(bool overwrite)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.SharesStorage = true;
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        long copied = source.CopyFile("file.bin", destination, "file.bin", overwrite);

        Assert.Equal(1, ops.Clones);
        Assert.Equal(Length, copied);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
    }

    /// <summary>
    /// A single file copy clones by name where that is how the platform clones, since it always
    /// carries permissions, and gives the clone the time it was made.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_single_file_copy_clones_by_name_where_it_can(bool overwrite)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = Tree(Contents(Length));
        ops.ClonesByName = true;
        fs.Find("/tree/source/file.bin")!.LastWriteTime = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        long copied = source.CopyFile("file.bin", destination, "file.bin", overwrite);

        Assert.Equal(1, ops.Clones);
        Assert.Equal(Length, copied);
        Assert.Equal(Contents(Length), Read(fs, "/tree/copy/file.bin"));
        Assert.Equal(fs.Now, fs.Find("/tree/copy/file.bin")!.LastWriteTime);
        Assert.Equal(["file.bin"], fs.Find("/tree/copy")!.Entries.Keys);
    }

    /// <summary>A simulated tree holding one file of the given contents, and an empty destination.</summary>
    private static (FakeFileSystem FileSystem, FakePlatformOps Ops) Tree(byte[] contents)
    {
        FakeFileSystem fs = new();
        fs.AddFile("/tree/source/file.bin").WriteAt(contents, 0);
        _ = fs.AddDirectory("/tree/copy");
        return (fs, new FakePlatformOps(fs));
    }

    private static async Task<CopyReport> Copy(FakePlatformOps ops, bool asynchronous, CopyOptions? options = null)
    {
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        return asynchronous
            ? await source.CopyToAsync(destination, options, cancellationToken: cancellationToken)
            : source.CopyTo(destination, options, cancellationToken: cancellationToken);
    }

    private static byte[] Read(FakeFileSystem fs, string path)
    {
        MemoryNode node = fs.Find(path)!;
        byte[] contents = new byte[node.Length];
        Assert.Equal(contents.Length, node.ReadAt(contents, 0));
        return contents;
    }

    /// <summary>Contents that differ from one position to the next, so a misplaced piece shows.</summary>
    private static byte[] Contents(int length)
    {
        byte[] contents = new byte[length];
        for (int i = 0; i < length; i++)
        {
            contents[i] = (byte)((i * 31 % 251) + 1);
        }

        return contents;
    }
}
