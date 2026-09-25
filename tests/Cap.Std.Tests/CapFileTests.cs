using System.Reflection;
using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// What an open file does once the name that produced it has stopped mattering.
/// </summary>
/// <remarks>
/// <para>
/// Almost nothing here is about containment, and that is the point: by the time a file
/// handle exists, resolution has finished and the guarantee has already been kept or broken.
/// What is left to get right is ownership — who closes the file, and whether two things can
/// come to believe they both do — and the promise that positional reads and writes are
/// exactly the framework's own, since the alternative is a capability layer that quietly
/// costs throughput.
/// </para>
/// <para>
/// The asynchronous cases are written to be honest about the platform rather than about the
/// API. A file handle that the operating system completes work on without a thread waiting
/// is a Windows idea; elsewhere the asynchronous methods still free the caller's thread, but
/// the waiting has not gone anywhere, and a test asserting otherwise would be asserting
/// something untrue on most of the systems it runs on.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class CapFileTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(string name) => Path.Combine(_tree.HostPath, name);

    // --- positional reads and writes -----------------------------------------------------------

    /// <summary>A read takes its position from the call and leaves nothing behind.</summary>
    /// <remarks>
    /// Two reads of the same range return the same bytes, which is what "no position is
    /// carried between calls" means in practice and is what lets several threads share one
    /// handle without agreeing about anything.
    /// </remarks>
    [Fact]
    public void Reads_are_positional_and_carry_nothing_between_calls()
    {
        File.WriteAllText(Host("data"), "0123456789");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Span<byte> buffer = stackalloc byte[4];

        Assert.Equal(4, file.Read(buffer, 2));
        Assert.Equal("2345"u8, buffer);

        Assert.Equal(4, file.Read(buffer, 2));
        Assert.Equal("2345"u8, buffer);
    }

    /// <summary>A read at the end of the file returns nothing, and that is not an error.</summary>
    [Fact]
    public void A_read_past_the_end_returns_nothing()
    {
        File.WriteAllText(Host("data"), "short");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Assert.Equal(0, file.Read(new byte[16], 5));
    }

    /// <summary>A write lands where it was told to and leaves the rest of the file alone.</summary>
    [Fact]
    public void Writes_are_positional()
    {
        File.WriteAllText(Host("data"), "aaaaaaaa");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Write))
        {
            file.Write("bb"u8, 3);
        }

        Assert.Equal("aaabbaaa", File.ReadAllText(Host("data")));
    }

    /// <summary>A handle opened to read refuses to write, because the system refused the right.</summary>
    /// <remarks>
    /// Not a check this library performs. The access was fixed when the file was opened and
    /// nothing since has asked for more, which is the only form of the guarantee that cannot
    /// be forgotten somewhere.
    /// </remarks>
    [Fact]
    public void A_read_only_handle_cannot_write()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Assert.ThrowsAny<Exception>(() => file.Write("x"u8, 0));
    }

    /// <summary>A negative offset is a mistake in the calling code and is named as one.</summary>
    [Fact]
    public void A_negative_offset_is_refused()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Assert.Throws<ArgumentOutOfRangeException>(() => file.Read(new byte[4], -1));
    }

    // --- length and durability -------------------------------------------------------------------

    /// <summary>The length follows the file, and setting it truncates or extends.</summary>
    [Fact]
    public void Setting_the_length_truncates_and_extends()
    {
        File.WriteAllText(Host("data"), "0123456789");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.Equal(10, file.Length);

            file.SetLength(4);
            Assert.Equal(4, file.Length);

            file.SetLength(6);
            Assert.Equal(6, file.Length);

            byte[] read = new byte[6];
            Assert.Equal(6, file.Read(read, 0));
            Assert.Equal<byte[]>([(byte)'0', (byte)'1', (byte)'2', (byte)'3', 0, 0], read);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite);
            file.SetLength(-1);
        });
    }

    /// <summary>Asking for durability is a call that returns; asking for nothing does nothing.</summary>
    /// <remarks>
    /// There is no buffer of this library's own for the weaker form to empty, so the only
    /// thing worth asserting about it is that it is harmless — which is what the
    /// documentation promises and what a caller arriving from a stream will assume.
    /// </remarks>
    [Fact]
    public void Flushing_is_available_in_both_forms()
    {
        using Dir root = OpenRoot();
        using CapFile file = root.CreateFile("data");

        file.Write("contents"u8, 0);
        file.Flush(toDisk: false);
        file.Flush(toDisk: true);

        Assert.Equal("contents", File.ReadAllText(Host("data")));
    }

    // --- streams and ownership --------------------------------------------------------------------

    /// <summary>A borrowed stream leaves the file open, so both can be disposed once.</summary>
    [Fact]
    public void A_borrowed_stream_leaves_the_handle_usable()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        using (Stream stream = file.AsStream())
        {
            using StreamReader reader = new(stream);
            Assert.Equal("contents", reader.ReadToEnd());
        }

        Assert.Equal(8, file.Length);
        Assert.Equal(8, file.Read(new byte[16], 0));
    }

    /// <summary>Two borrowed streams reach the same file without disturbing each other.</summary>
    [Fact]
    public void Borrowed_streams_are_independent_of_one_another()
    {
        File.WriteAllText(Host("data"), "0123456789");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        using Stream first = file.AsStream();
        using Stream second = file.AsStream();

        byte[] buffer = new byte[4];
        Assert.Equal(4, first.ReadAtLeast(buffer, 4, throwOnEndOfStream: false));
        Assert.Equal("0123"u8, buffer);

        Assert.Equal(4, second.ReadAtLeast(buffer, 4, throwOnEndOfStream: false));
        Assert.Equal("0123"u8, buffer);
    }

    /// <summary>A stream given ownership closes the file, and the handle is spent afterwards.</summary>
    [Fact]
    public void A_stream_given_ownership_leaves_the_handle_spent()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        CapFile file = root.OpenFile("data");

        using (Stream stream = file.AsStream(leaveOpen: false))
        {
            using StreamReader reader = new(stream);
            Assert.Equal("contents", reader.ReadToEnd());
        }

        Assert.Throws<ObjectDisposedException>(() => file.Length);

        // Disposing afterwards is safe and closes nothing: the stream owns the file now.
        file.Dispose();
    }

    /// <summary>A stream can write through the handle it borrowed.</summary>
    [Fact]
    public void A_borrowed_stream_can_write()
    {
        using Dir root = OpenRoot();
        using CapFile file = root.CreateFile("data");

        using (Stream stream = file.AsStream())
        {
            stream.Write("streamed"u8);
        }

        Assert.Equal("streamed", File.ReadAllText(Host("data")));
        Assert.Equal(8, file.Length);
    }

    /// <summary>The stream reports the kind of handle it was given.</summary>
    /// <remarks>
    /// Which is not always the kind that was asked for. Only Windows has file handles the
    /// operating system completes work on by itself; everywhere else there is nothing for the
    /// request to be true of, and the handle says so rather than claiming a capability the
    /// platform does not have.
    /// </remarks>
    [Theory]
    [InlineData(FileOptions.None)]
    [InlineData(FileOptions.Asynchronous)]
    public void A_stream_reports_the_same_asynchrony_as_the_handle(FileOptions options)
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Read, FileShare.Read, options);

        Assert.Equal(OperatingSystem.IsWindows() && options == FileOptions.Asynchronous, file.IsAsync);

        using FileStream stream = Assert.IsType<FileStream>(file.AsStream());
        Assert.Equal(file.IsAsync, stream.IsAsync);

        using FileStream owned = Assert.IsType<FileStream>(file.AsStream(leaveOpen: false));
        Assert.Equal(file.IsAsync, owned.IsAsync);
    }

    /// <summary>Disposing closes the file, and everything afterwards says so.</summary>
    [Fact]
    public void A_disposed_handle_refuses_every_operation()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        CapFile file = root.OpenFile("data");
        file.Dispose();
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.Length);
        Assert.Throws<ObjectDisposedException>(() => file.Read(new byte[4], 0));
        Assert.Throws<ObjectDisposedException>(() => file.AsStream());
        Assert.Throws<ObjectDisposedException>(file.UnsafeGetHandle);
    }

    // --- asynchronous operations -----------------------------------------------------------------

    /// <summary>Asynchronous reads and writes agree with the positional ones.</summary>
    [Theory]
    [InlineData(FileOptions.None)]
    [InlineData(FileOptions.Asynchronous)]
    public async Task Asynchronous_operations_work_on_either_kind_of_handle(FileOptions options)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        byte[] contents = [.. Enumerable.Range(0, 2048).Select(value => (byte)value)];

        using Dir root = OpenRoot();

        using (CapFile writer = root.OpenFile(
            "data", FileMode.Create, FileAccess.Write, FileShare.Read, options))
        {
            await writer.WriteAsync(contents, 0, token);
        }

        using CapFile reader = root.OpenFile("data", FileMode.Open, FileAccess.Read, FileShare.Read, options);

        byte[] buffer = new byte[contents.Length];
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(filled), filled, token);
            Assert.NotEqual(0, read);
            filled += read;
        }

        Assert.Equal(contents, buffer);
    }

    /// <summary>A token already signalled stops the operation before it starts.</summary>
    /// <remarks>
    /// The part of cancellation that holds on every platform, and the only part worth
    /// asserting. Once a read is in the hands of the operating system it is usually not
    /// recallable, so what cancelling reliably does is release the caller — and a test that
    /// demanded more would be demanding it of a platform rather than of this library.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_token_stops_the_operation_before_it_starts()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await file.ReadAsync(new byte[8], 0, source.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await root.ReadAllBytesAsync("data", source.Token));
    }

    /// <summary>An open file names no path either, for the reason the directory handle does not.</summary>
    /// <remarks>
    /// The same corrosion by a different door. A file handle that could say where it came
    /// from would let callers compare, join and prefix-check the answer, which is the
    /// technique handles exist to replace — and it would be even less trustworthy here, since
    /// a file's name can be reassigned while the handle stays perfectly valid.
    /// </remarks>
    [Theory]
    [InlineData("Path")]
    [InlineData("FullName")]
    [InlineData("FullPath")]
    [InlineData("Name")]
    [InlineData("DirectoryName")]
    public void An_open_file_exposes_no_member_naming_its_own_path(string member)
    {
        MemberInfo[] found = typeof(CapFile).GetMember(
            member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        Assert.Empty(found);
    }

    /// <summary>Nor can one be made from nothing: every way of getting one comes from a handle.</summary>
    [Fact]
    public void An_open_file_cannot_be_constructed_from_outside()
    {
        Assert.Empty(typeof(CapFile).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>The handle it hands out is the one it holds, and is still its own to close.</summary>
    [Fact]
    public void The_raw_handle_is_lent_rather_than_transferred()
    {
        File.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        CapFile file = root.OpenFile("data");

        Assert.False(file.UnsafeGetHandle().IsInvalid);
        Assert.False(file.UnsafeGetHandle().IsClosed);

        file.Dispose();
    }
}
