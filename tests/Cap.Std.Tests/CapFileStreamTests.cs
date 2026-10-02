using System.Text;
using Cap.Std.Testing;

namespace Cap.Std.Tests;

/// <summary>
/// The stream a file hands out when its handle is one the operating system completes work on
/// by itself, held to the <see cref="Stream"/> contract on every platform.
/// </summary>
/// <remarks>
/// Only Windows hands out such handles, so through the public API this stream would be
/// reached on the Windows leg alone. It reads and writes through the file's own positioned
/// members and nothing else, so it is built here directly over a file in memory, where the
/// bookkeeping it keeps (a position, and the checks on what the file allows) is the whole of
/// what is under test.
/// </remarks>
public sealed class CapFileStreamTests
{
    private static Dir Root(string contents = "0123456789")
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("data", contents);
        return fs.OpenRoot();
    }

    /// <summary>
    /// Reads the whole file back through the handle under test. The file system follows the
    /// host's rules, so on Windows a second open for reading would be refused while the
    /// handle writes.
    /// </summary>
    private static string Contents(CapFile file)
    {
        byte[] buffer = new byte[file.Length];
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = file.Read(buffer.AsSpan(filled), filled);
            Assert.NotEqual(0, read);
            filled += read;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    [Fact]
    public void Reading_advances_the_position_and_stops_at_the_end()
    {
        using Dir root = Root();
        using CapFile file = root.OpenFile("data");
        using CapFileStream stream = new(file, ownsFile: false);

        byte[] buffer = new byte[8];
        Assert.Equal(3, stream.Read(buffer, 2, 3));
        Assert.Equal("012"u8, buffer.AsSpan(2, 3));
        Assert.Equal(3, stream.Position);

        Assert.Equal(7, stream.Read(buffer));
        Assert.Equal("3456789"u8, buffer.AsSpan(0, 7));
        Assert.Equal(10, stream.Position);

        Assert.Equal(0, stream.Read(buffer));
        Assert.Equal(10, stream.Position);

        stream.Position = 8;
        Assert.Equal(2, stream.Read(buffer));
        Assert.Equal("89"u8, buffer.AsSpan(0, 2));

        stream.Position = 50;
        Assert.Equal(0, stream.Read(buffer));
        Assert.Equal(50, stream.Position);
    }

    [Fact]
    public void Seeking_from_each_origin_lands_where_FileStream_would()
    {
        byte[] contents = "0123456789"u8.ToArray();
        (long Offset, SeekOrigin Origin)[] moves =
        [
            (4, SeekOrigin.Begin),
            (3, SeekOrigin.Current),
            (-5, SeekOrigin.Current),
            (-1, SeekOrigin.End),
            (0, SeekOrigin.End),
            (6, SeekOrigin.End),
            (0, SeekOrigin.Begin),
            (12, SeekOrigin.Current),
        ];

        using Dir root = Root();
        using CapFile file = root.OpenFile("data");
        using CapFileStream stream = new(file, ownsFile: false);
        using MemoryStream oracle = new(contents, writable: false);

        byte[] actual = new byte[2];
        byte[] expected = new byte[2];
        foreach ((long offset, SeekOrigin origin) in moves)
        {
            Assert.Equal(oracle.Seek(offset, origin), stream.Seek(offset, origin));
            Assert.Equal(oracle.Position, stream.Position);

            int read = stream.Read(actual);
            Assert.Equal(oracle.Read(expected), read);
            Assert.Equal(expected.AsSpan(0, read), actual.AsSpan(0, read));
        }

        long before = stream.Position;
        Assert.Throws<IOException>(() => oracle.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-11, SeekOrigin.End));
        Assert.Equal(before, stream.Position);

        Assert.Throws<ArgumentException>(() => stream.Seek(0, (SeekOrigin)3));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Equal(before, stream.Position);
    }

    [Fact]
    public void Setting_the_length_shorter_clamps_the_position()
    {
        using Dir root = Root();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite);
        using CapFileStream stream = new(file, ownsFile: false);

        stream.Position = 8;
        stream.SetLength(4);
        Assert.Equal(4, stream.Length);
        Assert.Equal(4, file.Length);
        Assert.Equal(4, stream.Position);

        stream.Position = 2;
        stream.SetLength(6);
        Assert.Equal(6, stream.Length);
        Assert.Equal(2, stream.Position);

        stream.Write("ab"u8);
        Assert.Equal("01ab\0\0", Contents(file));
    }

    [Fact]
    public void Writes_land_at_the_position_and_extend_the_file()
    {
        using Dir root = Root();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite);
        using CapFileStream stream = new(file, ownsFile: false);

        stream.Position = 8;
        stream.Write("xyz"u8.ToArray(), 1, 2);
        Assert.Equal(10, stream.Position);

        stream.Write("!!"u8);
        Assert.Equal(12, stream.Position);
        Assert.Equal(12, stream.Length);

        stream.Seek(0, SeekOrigin.Begin);
        stream.Write("A"u8);
        stream.Flush();

        Assert.Equal("A1234567yz!!", Contents(file));
    }

    [Fact]
    public void Writes_to_an_appending_file_go_to_the_end()
    {
        using Dir root = Root();
        using (CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Write, append: true))
        using (CapFileStream stream = new(file, ownsFile: false))
        {
            stream.Write("ab"u8);
            stream.Write("cd"u8);
        }

        // The handle cannot read, and on Windows a reader is refused while it is open.

        Assert.Equal("0123456789abcd", Encoding.UTF8.GetString(root.ReadAllBytes("data")));
    }

    [Fact]
    public void Writing_through_a_read_only_file_is_refused()
    {
        using Dir root = Root();
        using CapFile file = root.OpenFile("data");
        using CapFileStream stream = new(file, ownsFile: false);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.True(stream.CanSeek);

        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Write("x"u8));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
        Assert.Equal(0, stream.Position);
        Assert.Equal("0123456789", Encoding.UTF8.GetString(root.ReadAllBytes("data")));
    }

    [Fact]
    public async Task Asynchronous_writing_through_a_read_only_file_is_refused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using Dir root = Root();
        using CapFile file = root.OpenFile("data");
        await using CapFileStream stream = new(file, ownsFile: false);

        await Assert.ThrowsAsync<NotSupportedException>(() => stream.WriteAsync(new byte[1], 0, 1, token));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await stream.WriteAsync(new byte[1].AsMemory(), token));
    }

    [Fact]
    public async Task Reading_through_a_write_only_file_is_refused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using Dir root = Root();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Write);
        await using CapFileStream stream = new(file, ownsFile: false);

        Assert.False(stream.CanRead);
        Assert.True(stream.CanWrite);

        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1].AsSpan()));
        await Assert.ThrowsAsync<NotSupportedException>(() => stream.ReadAsync(new byte[1], 0, 1, token));
        await Assert.ThrowsAsync<NotSupportedException>(() => stream.ReadAsync(new byte[1].AsMemory(), token).AsTask());
    }

    [Fact]
    public async Task Asynchronous_reads_and_writes_agree_with_the_synchronous_ones()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using Dir root = Root();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite);
        await using CapFileStream stream = new(file, ownsFile: false);

        byte[] buffer = new byte[8];
#pragma warning disable CA1835 // The array overloads are what is under test here.
        Assert.Equal(3, await stream.ReadAsync(buffer, 2, 3, token));
#pragma warning restore CA1835
        Assert.Equal("012"u8, buffer.AsSpan(2, 3));
        Assert.Equal(3, stream.Position);

        Assert.Equal(7, await stream.ReadAsync(buffer.AsMemory(), token));
        Assert.Equal("3456789"u8, buffer.AsSpan(0, 7));
        Assert.Equal(10, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(buffer.AsMemory(), token));

        stream.Position = 4;
#pragma warning disable CA1835 // As above.
        await stream.WriteAsync("xyz"u8.ToArray(), 1, 2, token);
#pragma warning restore CA1835
        Assert.Equal(6, stream.Position);
        await stream.WriteAsync("!!!!!!"u8.ToArray().AsMemory(), token);
        Assert.Equal(12, stream.Position);
        await stream.FlushAsync(token);

        Assert.Equal("0123yz!!!!!!", Contents(file));

        stream.Position = 4;
        byte[] sync = new byte[8];
        byte[] async = new byte[8];
        Assert.Equal(8, stream.Read(sync));
        stream.Position = 4;
        Assert.Equal(8, await stream.ReadAsync(async.AsMemory(), token));
        Assert.Equal(sync, async);
    }

    [Fact]
    public async Task Buffer_arguments_are_validated_before_the_file_is_touched()
    {
        using Dir root = Root();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite);
        using CapFileStream stream = new(file, ownsFile: false);

        Assert.Throws<ArgumentNullException>(() => stream.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], 3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(new byte[4], 3, 2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.ReadAsync(new byte[4], 3, 2, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.WriteAsync(new byte[4], 3, 2, TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
        Assert.Equal("0123456789", Contents(file));
    }

    [Fact]
    public async Task A_cancelled_flush_is_reported_as_cancelled()
    {
        using Dir root = Root();
        using CapFile file = root.OpenFile("data");
        await using CapFileStream stream = new(file, ownsFile: false);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Task flush = stream.FlushAsync(cancelled.Token);
        Assert.True(flush.IsCanceled);
        await stream.FlushAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Disposing_an_owning_stream_disposes_the_file_and_a_lending_one_does_not()
    {
        using Dir root = Root();

        using (CapFile lent = root.OpenFile("data"))
        {
            CapFileStream stream = new(lent, ownsFile: false);
            stream.Dispose();
            stream.Dispose();

            Assert.Equal(10, lent.Length);
            Assert.Equal(4, lent.Read(new byte[4], 0));
        }

        using CapFile owned = root.OpenFile("data");
        using (CapFileStream stream = new(owned, ownsFile: true))
        {
            Assert.Equal(10, stream.Length);
        }

        Assert.Throws<ObjectDisposedException>(() => owned.Length);
    }

    [Fact]
    public void A_lending_stream_fails_once_its_file_is_disposed()
    {
        using Dir root = Root();
        CapFile file = root.OpenFile("data");
        using CapFileStream stream = new(file, ownsFile: false);

        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1]));
    }

    [Fact]
    public async Task Every_member_throws_after_disposal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using Dir root = Root();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite);
        CapFileStream stream = new(file, ownsFile: false);
        await stream.DisposeAsync();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);

        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.Position);
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => stream.SetLength(0));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1].AsSpan()));
        Assert.Throws<ObjectDisposedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<ObjectDisposedException>(() => stream.Write("x"u8));
        Assert.Throws<ObjectDisposedException>(stream.Flush);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[1], 0, 1, token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[1].AsMemory(), token).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync(new byte[1], 0, 1, token));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.WriteAsync(new byte[1].AsMemory(), token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.FlushAsync(token));

        Assert.Equal(10, file.Length);
    }
}
