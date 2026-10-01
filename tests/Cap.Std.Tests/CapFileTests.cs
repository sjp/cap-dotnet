using System.Reflection;
using System.Text;
using Cap.Primitives;
using Cap.Primitives.Interop;

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
        HostFile.WriteAllText(Host("data"), "0123456789");

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
        HostFile.WriteAllText(Host("data"), "short");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Assert.Equal(0, file.Read(new byte[16], 5));
    }

    /// <summary>A write lands where it was told to and leaves the rest of the file alone.</summary>
    [Fact]
    public void Writes_are_positional()
    {
        HostFile.WriteAllText(Host("data"), "aaaaaaaa");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Write))
        {
            file.Write("bb"u8, 3);
        }

        Assert.Equal("aaabbaaa", HostFile.ReadAllText(Host("data")));
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
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Assert.ThrowsAny<Exception>(() => file.Write("x"u8, 0));
    }

    /// <summary>A negative offset is a mistake in the calling code and is named as one.</summary>
    [Fact]
    public void A_negative_offset_is_refused()
    {
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Assert.Throws<ArgumentOutOfRangeException>(() => file.Read(new byte[4], -1));
    }

    // --- length and durability -------------------------------------------------------------------

    /// <summary>The length follows the file, and setting it truncates or extends.</summary>
    [Fact]
    public void Setting_the_length_truncates_and_extends()
    {
        HostFile.WriteAllText(Host("data"), "0123456789");

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

    /// <summary>
    /// A handle opened only for reading cannot resize the file, and says so as a permission
    /// problem rather than leaving the operating system's own answer to describe it.
    /// </summary>
    [Fact]
    public void A_read_only_handle_cannot_change_the_length()
    {
        HostFile.WriteAllText(Host("data"), "0123456789");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("data"))
        {
            UnauthorizedAccessException refused =
                Assert.Throws<UnauthorizedAccessException>(() => file.SetLength(0));
            Assert.Equal(CapErrorKind.PermissionDenied, CapIOException.KindOf(refused));
            Assert.Equal(10, file.Length);
        }

        Assert.Equal("0123456789", HostFile.ReadAllText(Host("data")));
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

        Assert.Equal("contents", HostFile.ReadAllText(Host("data")));
    }

    /// <summary>
    /// A handle opened only for reading can still be flushed to disk.
    /// </summary>
    /// <remarks>
    /// Whether a descriptor opened for reading may be synced is a matter of folklore rather
    /// than of any one standard, and a platform that refused it would turn a defensive flush
    /// in a caller's cleanup path into a failure. Every platform this runs on accepts it, and
    /// this pins that.
    /// </remarks>
    [Fact]
    public void A_read_only_handle_can_still_flush()
    {
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        file.Flush(toDisk: false);
        file.Flush(toDisk: true);

        Assert.Equal("contents", HostFile.ReadAllText(Host("data")));
    }

    /// <summary>
    /// Positional reads and writes from many threads on one handle each land where they were
    /// told to.
    /// </summary>
    /// <remarks>
    /// The promise that makes a positional handle worth having: no position is shared, so
    /// threads working on disjoint ranges need no agreement between them. A handle that kept
    /// a cursor underneath and moved it for each call would put some of these writes in the
    /// wrong range, and the read-back would find another thread's byte.
    /// </remarks>
    [Fact]
    public void Concurrent_positional_reads_and_writes_do_not_interfere()
    {
        const int Ranges = 64;
        const int RangeBytes = 1024;

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data", FileMode.Create, FileAccess.ReadWrite);
        file.SetLength(Ranges * RangeBytes);

        Parallel.For(0, Ranges, range =>
        {
            byte[] fill = new byte[RangeBytes];
            Array.Fill(fill, (byte)(range + 1));
            file.Write(fill, (long)range * RangeBytes);
        });

        Parallel.For(0, Ranges, range =>
        {
            byte[] read = new byte[RangeBytes];
            Assert.Equal(RangeBytes, file.Read(read, (long)range * RangeBytes));
            Assert.All(read, b => Assert.Equal((byte)(range + 1), b));
        });

        Assert.Equal(Ranges * RangeBytes, file.Length);
    }

    // --- streams and ownership --------------------------------------------------------------------

    /// <summary>A borrowed stream leaves the file open, so both can be disposed once.</summary>
    [Fact]
    public void A_borrowed_stream_leaves_the_handle_usable()
    {
        HostFile.WriteAllText(Host("data"), "contents");

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
        HostFile.WriteAllText(Host("data"), "0123456789");

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
        HostFile.WriteAllText(Host("data"), "contents");

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

        Assert.Equal("streamed", HostFile.ReadAllText(Host("data")));
        Assert.Equal(8, file.Length);
    }

    /// <summary>The stream reports the kind of handle it was given.</summary>
    /// <remarks>
    /// Which is not always the kind that was asked for. Only Windows has file handles the
    /// operating system completes work on by itself; everywhere else there is nothing for the
    /// request to be true of, and the handle says so rather than claiming a capability the
    /// platform does not have.
    /// </remarks>
    [Fact]
    [NotInMemory("About the FileStream the framework builds over an operating-system handle. A file held in memory has a stream of its own.")]
    public void A_synchronous_handle_gives_a_synchronous_file_stream()
    {
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);

        Assert.False(file.IsAsync);

        using FileStream stream = Assert.IsType<FileStream>(file.AsStream());
        Assert.False(stream.IsAsync);

        using FileStream owned = Assert.IsType<FileStream>(file.AsStream(leaveOpen: false));
        Assert.False(owned.IsAsync);
    }

    /// <summary>
    /// A handle opened for asynchronous work can back any number of streams, borrowed and
    /// owned, each with its own position.
    /// </summary>
    /// <remarks>
    /// On Windows such a file is attached to the thread pool once, and a second framework
    /// stream over it, or over a copy of its handle, cannot be attached again. Taking several
    /// streams and reading through each asynchronously is what would fail if any of them were
    /// built that way.
    /// </remarks>
    [Fact]
    [NotInMemory("About the stream built over an operating-system handle. A file held in memory has a stream of its own.")]
    public async Task An_asynchronous_handle_backs_any_number_of_streams()
    {
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);

        Assert.Equal(OperatingSystem.IsWindows(), file.IsAsync);

        byte[] buffer = new byte[8];
        Assert.Equal(8, await file.ReadAsync(buffer, 0, TestContext.Current.CancellationToken));

        await using Stream first = file.AsStream();
        await using Stream second = file.AsStream();
        Assert.Equal(8, await first.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal(4, await second.ReadAsync(buffer.AsMemory(0, 4), TestContext.Current.CancellationToken));
        Assert.Equal(4, second.Position);
        Assert.Equal(8, first.Position);

        await using Stream owned = file.AsStream(leaveOpen: false);
        Assert.Equal(8, await owned.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal("contents", Encoding.UTF8.GetString(buffer));
        Assert.Throws<ObjectDisposedException>(() => file.Length);
    }

    /// <summary>
    /// Every stream a file can give truncates the way a <see cref="FileStream"/> does: a
    /// position past the new end moves back to it, so the next write lands there.
    /// </summary>
    /// <remarks>
    /// Left where it was, the position would put the next write past the end and fill the gap
    /// with zeros. Each kind is built directly over a file in the scratch tree, so all three run
    /// on every host leg, whichever kind <see cref="CapFile.AsStream"/> would have chosen there.
    /// </remarks>
    [Theory]
    [InlineData(nameof(FileStream))]
    [InlineData(nameof(CapFileStream))]
    [InlineData(nameof(PositionedFileStream))]
    public void A_stream_moves_its_position_back_when_truncated(string kind)
    {
        HostFile.WriteAllText(Host("data"), "0123456789");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite);

        using (Stream stream = kind switch
        {
            nameof(FileStream) => file.AsStream(),
            nameof(CapFileStream) => new CapFileStream(file, ownsFile: false),

            // A file held in memory has no handle to build one over, and is given one anyway.
            _ when InMemoryLeg.FileSystem is not null => Assert.IsType<PositionedFileStream>(file.AsStream()),
            _ => new PositionedFileStream(PlatformOps.Host, file.UnsafeGetHandle(), FileAccess.ReadWrite),
        })
        {
            Assert.Equal(10, stream.Seek(0, SeekOrigin.End));

            stream.SetLength(4);
            Assert.Equal(4, stream.Position);

            // Shorter than the position only moves it when it was past the new end.
            stream.Position = 2;
            stream.SetLength(3);
            Assert.Equal(2, stream.Position);

            stream.SetLength(0);
            Assert.Equal(0, stream.Position);
            stream.Write("new"u8);
            Assert.Equal(3, stream.Position);
            stream.Flush();
        }

        Assert.Equal("new", HostFile.ReadAllText(Host("data")));
    }

    /// <summary>
    /// The stream over a handle the operating system completes work on truncates as a
    /// <see cref="FileStream"/> does.
    /// </summary>
    /// <remarks>
    /// The same check as above, through <see cref="CapFile.AsStream"/> itself. Only Windows has
    /// such handles, so only there is the stream it chooses the library's own.
    /// </remarks>
    [Fact]
    [NotInMemory("About the stream built over an operating-system handle. A file held in memory has a stream of its own.")]
    public void A_stream_over_an_asynchronous_handle_moves_its_position_back_when_truncated()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows has file handles the operating system completes work on by itself.");
        }

        HostFile.WriteAllText(Host("data"), "0123456789");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

        using (Stream stream = file.AsStream())
        {
            Assert.IsType<CapFileStream>(stream);
            stream.Seek(0, SeekOrigin.End);
            stream.SetLength(0);
            Assert.Equal(0, stream.Position);
            stream.Write("new"u8);
        }

        Assert.Equal("new", HostFile.ReadAllText(Host("data")));
    }

    /// <summary>Disposing closes the file, and everything afterwards says so.</summary>
    [Fact]
    public void A_disposed_handle_refuses_every_operation()
    {
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        CapFile file = root.OpenFile("data");
        file.Dispose();
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.Length);
        Assert.Throws<ObjectDisposedException>(() => file.Read(new byte[4], 0));
        Assert.Throws<ObjectDisposedException>(() => file.AsStream());
        Assert.Throws<ObjectDisposedException>(file.UnsafeGetHandle);
    }

    // --- FIFOs -------------------------------------------------------------------------------------

    /// <summary>
    /// A FIFO opens like any file, has no positions to read or write at, and says so in words
    /// that point at the stream, which reads and writes one in order.
    /// </summary>
    /// <remarks>
    /// The framework's own positioned calls refuse such a handle in terms of streams and
    /// seeking, which describe neither what the caller asked for nor what to do instead.
    /// </remarks>
    [Fact]
    [NotInMemory("Needs a named pipe, which only the host's filesystem can hold.")]
    public async Task A_pipe_is_refused_positional_access_and_carried_by_a_stream()
    {
        SkipWithoutPipes();
        HostFile.CreateFifo(Host("pipe"));

        using Dir root = OpenRoot();
        using CapFile reader = root.OpenFile("pipe");
        using CapFile writer = root.OpenFile("pipe", FileMode.Open, FileAccess.Write);

        NotSupportedException read = Assert.Throws<NotSupportedException>(() => reader.Read(new byte[4], 0));
        Assert.Contains(nameof(CapFile.AsStream), read.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => writer.Write("x"u8, 0));
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await reader.ReadAsync(new byte[4], 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await writer.WriteAsync("x"u8.ToArray(), 0, TestContext.Current.CancellationToken));

        using (Stream output = writer.AsStream())
        {
            output.Write("in order"u8);
            output.Flush();
        }

        using Stream input = reader.AsStream(bufferSize: 0);
        byte[] buffer = new byte[16];
        int count = input.Read(buffer);
        Assert.Equal("in order", Encoding.UTF8.GetString(buffer, 0, count));
    }

    /// <summary>
    /// A FIFO nobody is reading is refused for writing as a kind of entry the open cannot act
    /// on, rather than reported missing.
    /// </summary>
    /// <remarks>
    /// The open is issued so that it cannot wait for a reader, and the system then refuses it
    /// with a code that also means a device with no driver behind it. Reported as missing, the
    /// refusal would send a caller looking for a directory that is there.
    /// </remarks>
    [Fact]
    [NotInMemory("Needs a named pipe, which only the host's filesystem can hold.")]
    public void A_pipe_nobody_is_reading_is_refused_for_writing_rather_than_reported_missing()
    {
        SkipWithoutPipes();
        HostFile.CreateFifo(Host("pipe"));

        using Dir root = OpenRoot();

        CapIOException refused = Assert.Throws<CapIOException>(
            () => root.OpenFile("pipe", FileMode.Open, FileAccess.Write));
        Assert.Equal(CapErrorKind.NotSupported, refused.Kind);
    }

    private static void SkipWithoutPipes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A named pipe is not an entry of a directory on this platform.");
        }
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
        HostFile.WriteAllText(Host("data"), "contents");

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
    [NotInMemory("About the operating-system handle, which a file held in memory does not have.")]
    public void The_raw_handle_is_lent_rather_than_transferred()
    {
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        CapFile file = root.OpenFile("data");

        Assert.False(file.UnsafeGetHandle().IsInvalid);
        Assert.False(file.UnsafeGetHandle().IsClosed);

        file.Dispose();
    }

    // --- copying the handle ------------------------------------------------------------------------

    /// <summary>A copy of an open file keeps working once the original is closed.</summary>
    [Fact]
    public void A_copy_of_an_open_file_outlives_it()
    {
        using Dir root = OpenRoot();
        CapFile original = root.OpenFile("data", FileMode.CreateNew, FileAccess.ReadWrite);
        using CapFile copy = original.Clone();
        original.Dispose();

        copy.Write("written through the copy"u8, 0);

        Assert.Equal(FileAccess.ReadWrite, copy.Access);
        Assert.Equal("written through the copy", HostFile.ReadAllText(Host("data")));
    }

    /// <summary>
    /// A copy reaches the object that was opened, not whatever the name holds by the time it
    /// is made.
    /// </summary>
    [Fact]
    public void A_copy_reaches_the_object_and_not_the_name()
    {
        HostFile.WriteAllText(Host("data"), "first");

        using Dir root = OpenRoot();
        using CapFile original = root.OpenFile("data", share: FileShare.Read | FileShare.Delete);
        root.Rename("data", root, "moved");
        HostFile.WriteAllText(Host("data"), "second");

        Assert.True(original.TryClone(out CapFile? copy));
        using (copy)
        {
            byte[] buffer = new byte[16];
            int read = copy.Read(buffer, 0);
            Assert.Equal("first", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    /// <summary>A copy of a handle that can only read can only read.</summary>
    [Fact]
    public void A_copy_carries_the_access_of_its_original()
    {
        HostFile.WriteAllText(Host("data"), "x");

        using Dir root = OpenRoot();
        using CapFile original = root.OpenFile("data");
        using CapFile copy = original.Clone();

        Assert.Equal(FileAccess.Read, copy.Access);
        Assert.Throws<UnauthorizedAccessException>(() => copy.Write("y"u8, 0));
    }

    /// <summary>A closed handle has nothing to copy.</summary>
    [Fact]
    public void A_closed_file_cannot_be_copied()
    {
        using Dir root = OpenRoot();
        CapFile file = root.CreateNewFile("data");
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.Clone());
        Assert.Throws<ObjectDisposedException>(() => file.TryClone(out _));
    }
}
