using System.Text;
using Cap.Primitives;
using Cap.Tests.Fakes;

namespace Cap.Std.Tests;

/// <summary>
/// A file's contents, read and written through a simulated platform.
/// </summary>
/// <remarks>
/// <para>
/// Every read, write, resize and stream on a file goes through the backend that opened it,
/// so a file on a simulated filesystem holds real bytes and never reaches the disk. These
/// write through the public surface and read back through it, then look at the simulated
/// node directly to show the bytes landed there and nowhere else.
/// </para>
/// <para>
/// The simulation's handle values are indices into its own table, so a test that passed if
/// the bytes went to the operating system instead would be reading or writing some unrelated
/// descriptor. Each case therefore checks the simulated node, not only the round trip.
/// </para>
/// </remarks>
public sealed class FileContentSimulationTests
{
    private const string RootPath = "/root";

    private static (FakeFileSystem Fs, FakePlatformOps Ops, Dir Root) Simulated()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory(RootPath);
        FakePlatformOps ops = new(fs);
        return (fs, ops, Dir.OpenThrough(ops, RootPath, AmbientAuthority.Acquire()));
    }

    [Fact]
    public void Bytes_written_through_a_handle_are_read_back_through_another()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            using (CapFile file = root.CreateFile("data"))
            {
                file.Write("hello, world"u8, 0);
                file.Write("W"u8, 7);
                Assert.Equal(12, file.Length);
            }

            using CapFile reader = root.OpenFile("data");
            byte[] buffer = new byte[32];
            int read = reader.Read(buffer, 0);

            Assert.Equal("hello, World", Encoding.ASCII.GetString(buffer, 0, read));
            Assert.Equal("hello, World"u8.ToArray(), fs.Find($"{RootPath}/data")!.Contents);
        }
    }

    [Fact]
    public async Task Bytes_written_asynchronously_are_read_back_asynchronously()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            using (CapFile file = root.CreateFile("data"))
            {
                Assert.False(file.IsAsync);
                await file.WriteAsync("asynchronous"u8.ToArray(), 0, TestContext.Current.CancellationToken);
            }

            using CapFile reader = root.OpenFile("data");
            byte[] buffer = new byte[32];
            int read = await reader.ReadAsync(buffer, 2, TestContext.Current.CancellationToken);

            Assert.Equal("ynchronous", Encoding.ASCII.GetString(buffer, 0, read));
            Assert.Equal("asynchronous"u8.ToArray(), fs.Find($"{RootPath}/data")!.Contents);
        }
    }

    [Fact]
    public void The_whole_file_helpers_round_trip_through_the_simulation()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            root.WriteAllBytes("data", [1, 2, 3, 4]);

            Assert.Equal([1, 2, 3, 4], root.ReadAllBytes("data"));
            Assert.Equal([1, 2, 3, 4], fs.Find($"{RootPath}/data")!.Contents);
        }
    }

    [Fact]
    public async Task The_asynchronous_whole_file_helpers_round_trip_through_the_simulation()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            await root.WriteAllBytesAsync("data", new byte[] { 5, 6, 7 }, TestContext.Current.CancellationToken);

            Assert.Equal([5, 6, 7], await root.ReadAllBytesAsync("data", TestContext.Current.CancellationToken));
            Assert.Equal([5, 6, 7], fs.Find($"{RootPath}/data")!.Contents);
        }
    }

    [Fact]
    public void A_stream_reads_what_was_written_through_a_stream()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            using (CapFile file = root.CreateFile("data"))
            using (Stream stream = file.AsStream())
            {
                Assert.IsNotType<FileStream>(stream);
                stream.Write("first line\n"u8);
                using StreamWriter writer = new(stream, leaveOpen: true);
                writer.Write("second line");
            }

            using (CapFile file = root.OpenFile("data"))
            {
                using StreamReader reader = new(file.AsStream(leaveOpen: false));
                Assert.Equal("first line", reader.ReadLine());
                Assert.Equal("second line", reader.ReadLine());
            }

            Assert.Equal("first line\nsecond line", Encoding.UTF8.GetString(fs.Find($"{RootPath}/data")!.Contents));
        }
    }

    [Fact]
    public void A_stream_seeks_and_resizes_like_a_file_stream()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            using CapFile file = root.OpenFile("data", FileMode.Create, FileAccess.ReadWrite);
            using Stream stream = file.AsStream();

            Assert.True(stream.CanSeek);
            stream.Write("0123456789"u8);
            Assert.Equal(10, stream.Position);

            Assert.Equal(3, stream.Seek(-7, SeekOrigin.End));
            byte[] buffer = new byte[2];
            stream.ReadExactly(buffer);
            Assert.Equal("34"u8.ToArray(), buffer);

            stream.SetLength(4);
            Assert.Equal(4, stream.Length);
            stream.Position = 6;
            stream.Write("x"u8);
            stream.Flush();

            Assert.Equal("0123\0\0x"u8.ToArray(), fs.Find($"{RootPath}/data")!.Contents);
        }
    }

    [Fact]
    public async Task A_stream_reads_and_writes_asynchronously()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            using CapFile file = root.OpenFile("data", FileMode.Create, FileAccess.ReadWrite);
            await using (Stream stream = file.AsStream())
            {
                await stream.WriteAsync("async stream"u8.ToArray(), TestContext.Current.CancellationToken);
                await stream.FlushAsync(TestContext.Current.CancellationToken);
                stream.Position = 6;
                byte[] buffer = new byte[6];
                await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
                Assert.Equal("stream"u8.ToArray(), buffer);
            }

            Assert.Equal("async stream"u8.ToArray(), fs.Find($"{RootPath}/data")!.Contents);
        }
    }

    [Fact]
    public void A_stream_from_a_handle_that_cannot_write_refuses_writes()
    {
        (_, _, Dir root) = Simulated();
        using (root)
        {
            root.WriteAllBytes("data", [1]);

            using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Read);
            using Stream stream = file.AsStream();

            Assert.True(stream.CanRead);
            Assert.False(stream.CanWrite);
            Assert.Throws<NotSupportedException>(() => stream.Write([2]));
        }
    }

    [Fact]
    public void A_handle_refuses_what_it_was_not_opened_for()
    {
        (_, _, Dir root) = Simulated();
        using (root)
        {
            root.WriteAllBytes("data", [1]);

            using CapFile reader = root.OpenFile("data", FileMode.Open, FileAccess.Read);
            Assert.Throws<UnauthorizedAccessException>(() => reader.Write([2], 0));
            Assert.Throws<UnauthorizedAccessException>(() => reader.SetLength(0));

            using CapFile writer = root.OpenFile("data", FileMode.Open, FileAccess.Write);
            Assert.Throws<UnauthorizedAccessException>(() => writer.Read(new byte[1], 0));
        }
    }

    [Fact]
    public void Opening_to_replace_an_existing_file_empties_it()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            root.WriteAllBytes("data", [1, 2, 3, 4]);
            root.WriteAllBytes("data", [9]);

            Assert.Equal([9], fs.Find($"{RootPath}/data")!.Contents);
        }
    }

    [Fact]
    public void An_appending_handle_and_its_stream_both_write_at_the_end()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            root.WriteAllBytes("log", "one"u8);

            using CapFile file = root.OpenFile("log", FileMode.Append, FileAccess.Write);
            file.Write(" two"u8, 0);

            using (Stream stream = file.AsStream(bufferSize: 0))
            {
                stream.Position = 0;
                stream.Write(" three"u8);
            }

            Assert.Equal("one two three"u8.ToArray(), fs.Find($"{RootPath}/log")!.Contents);
        }
    }

    [Fact]
    public void A_flush_to_disk_reaches_the_simulation_and_a_plain_flush_does_not()
    {
        (_, FakePlatformOps ops, Dir root) = Simulated();
        using (root)
        {
            using CapFile file = root.CreateFile("data");

            file.Flush(toDisk: false);
            Assert.Equal(0, ops.FileFlushes);

            file.Flush(toDisk: true);
            Assert.Equal(1, ops.FileFlushes);
        }
    }

    [Fact]
    public void A_file_seeded_by_the_test_is_read_through_the_handle()
    {
        (FakeFileSystem fs, _, Dir root) = Simulated();
        using (root)
        {
            fs.AddFile($"{RootPath}/seeded").Contents = "from the test"u8.ToArray();

            Assert.Equal("from the test"u8.ToArray(), root.ReadAllBytes("seeded"));
        }
    }

    [Fact]
    public void A_file_on_a_simulated_filesystem_has_no_raw_handle_to_give_out()
    {
        (_, _, Dir root) = Simulated();
        using (root)
        {
            using CapFile file = root.CreateFile("data");

            NotSupportedException refused = Assert.Throws<NotSupportedException>(file.UnsafeGetHandle);
            Assert.Contains("host's filesystem", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_directory_on_a_simulated_filesystem_has_no_raw_handle_to_give_out()
    {
        (_, _, Dir root) = Simulated();
        using (root)
        {
            NotSupportedException refused = Assert.Throws<NotSupportedException>(root.UnsafeGetHandle);
            Assert.Contains("host's filesystem", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_anonymous_scratch_file_holds_its_contents_in_the_simulation()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("/tmp");
        fs.SupportsAnonymousFiles = true;
        FakePlatformOps ops = new(fs);

        using Dir root = Dir.OpenThrough(ops, "/tmp", AmbientAuthority.Acquire());
        using CapTempFile temp = CapTempFile.NewAnonymous(root);

        temp.File.Write("scratch"u8, 0);
        byte[] buffer = new byte[16];
        int read = temp.File.Read(buffer, 0);

        Assert.Equal("scratch", Encoding.ASCII.GetString(buffer, 0, read));
    }
}
