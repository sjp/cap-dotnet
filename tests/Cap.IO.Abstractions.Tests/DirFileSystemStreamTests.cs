using System.IO.Abstractions;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// What the adapter's stream does beyond forwarding to the stream beneath: store the file on
/// the device when asked, as <see cref="FileStream.Flush(bool)"/> does, and close a file the
/// stream beneath only borrows.
/// </summary>
public sealed class DirFileSystemStreamTests
{
    [Fact]
    public void Flushing_to_disk_stores_the_file()
    {
        Recorder inner = new();
        int syncs = 0;
        using DirFileSystemStream stream = new(inner, "/a.bin", isAsync: false, () => syncs++);

        stream.Flush(flushToDisk: true);

        Assert.Equal(1, inner.Flushes);
        Assert.Equal(1, syncs);
    }

    [Fact]
    public void Flushing_without_disk_only_empties_the_buffer()
    {
        Recorder inner = new();
        int syncs = 0;
        using DirFileSystemStream stream = new(inner, "/a.bin", isAsync: false, () => syncs++);

        stream.Flush();
        stream.Flush(flushToDisk: false);

        Assert.Equal(2, inner.Flushes);
        Assert.Equal(0, syncs);
    }

    [Fact]
    public void Flushing_to_disk_with_no_device_beneath_only_empties_the_buffer()
    {
        Recorder inner = new();
        using DirFileSystemStream stream = new(inner, "/a.bin", isAsync: false, sync: null);

        stream.Flush(flushToDisk: true);

        Assert.Equal(1, inner.Flushes);
    }

    [Theory]
    [InlineData(FileMode.Create, FileOptions.None)]
    [InlineData(FileMode.Append, FileOptions.None)]
    [InlineData(FileMode.Create, FileOptions.Asynchronous)]
    [InlineData(FileMode.Append, FileOptions.Asynchronous)]
    public void Every_stream_on_disk_can_be_flushed_to_disk(FileMode mode, FileOptions options)
    {
        using DiskFixture fixture = new();
        string path = fixture.FileSystem.Path.Combine(fixture.FileSystem.Directory.GetCurrentDirectory(), "a.bin");

        using FileSystemStream stream = fixture.FileSystem.FileStream.New(path, mode, FileAccess.Write, FileShare.None, 4096, options);

        Assert.True(Assert.IsType<DirFileSystemStream>(stream).SyncsToDisk);
    }

    [Fact]
    public void Disposing_closes_the_stream_and_then_the_borrowed_file()
    {
        bool streamClosed = false;
        bool? streamClosedFirst = null;
        Recorder inner = new(() => streamClosed = true);
        DirFileSystemStream stream = new(inner, "/a.bin", isAsync: true, sync: null, new Closer(() => streamClosedFirst = streamClosed));

        stream.Dispose();

        Assert.True(streamClosedFirst);
    }

    [Fact]
    public async Task Disposing_asynchronously_closes_the_borrowed_file()
    {
        bool closed = false;
        DirFileSystemStream stream = new(new Recorder(), "/a.bin", isAsync: true, sync: null, new Closer(() => closed = true));

        await stream.DisposeAsync();

        Assert.True(closed);
    }

    private sealed class Recorder(Action? disposed = null) : MemoryStream
    {
        public int Flushes { get; private set; }

        public override void Flush()
        {
            Flushes++;
            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposed?.Invoke();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class Closer(Action disposed) : IDisposable
    {
        public void Dispose() => disposed();
    }
}
