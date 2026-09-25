using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;
using Cap.Primitives;
using Cap.Std;
using Microsoft.Win32.SafeHandles;

namespace Cap.IO.Abstractions;

/// <summary><see cref="IFile"/> over a <see cref="DirFileSystem"/>'s directory.</summary>
/// <remarks>
/// Each member resolves its path beneath the directory and does what the matching
/// <c>System.IO.File</c> member does, with the defaults that member uses: the same access,
/// the same sharing, UTF-8 without a byte order mark for text written without an encoding, and
/// the byte order mark honoured when text is read.
/// </remarks>
internal sealed class FileAdapter(DirFileSystem fs) : IFile
{
    private const int DefaultBufferSize = 4096;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public IFileSystem FileSystem => fs;

    // --- Appending ------------------------------------------------------------------------

    public void AppendAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        AppendAllBytes(path, bytes.AsSpan());
    }

    public void AppendAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using FileSystemStream stream = fs.OpenStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    public Task AppendAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return AppendAllBytesAsync(path, bytes.AsMemory(), cancellationToken);
    }

    public async Task AppendAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await using FileSystemStream stream = fs.OpenStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read, options: FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public void AppendAllLines(string path, IEnumerable<string> contents) => AppendAllLines(path, contents, Utf8NoBom);

    public void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);
        using StreamWriter writer = Writer(path, FileMode.Append, encoding);
        foreach (string line in contents)
        {
            writer.WriteLine(line);
        }
    }

    public Task AppendAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default) =>
        AppendAllLinesAsync(path, contents, Utf8NoBom, cancellationToken);

    public Task AppendAllLinesAsync(string path, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        WriteLinesAsync(path, FileMode.Append, contents, encoding, cancellationToken);

    public void AppendAllText(string path, string? contents) => AppendAllText(path, contents, Utf8NoBom);

    public void AppendAllText(string path, string? contents, Encoding encoding) =>
        AppendAllText(path, contents.AsSpan(), encoding);

    public void AppendAllText(string path, ReadOnlySpan<char> contents) => AppendAllText(path, contents, Utf8NoBom);

    public void AppendAllText(string path, ReadOnlySpan<char> contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        using StreamWriter writer = Writer(path, FileMode.Append, encoding);
        writer.Write(contents);
    }

    public Task AppendAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default) =>
        AppendAllTextAsync(path, contents.AsMemory(), Utf8NoBom, cancellationToken);

    public Task AppendAllTextAsync(string path, string? contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        AppendAllTextAsync(path, contents.AsMemory(), encoding, cancellationToken);

    public Task AppendAllTextAsync(string path, ReadOnlyMemory<char> contents, CancellationToken cancellationToken = default) =>
        AppendAllTextAsync(path, contents, Utf8NoBom, cancellationToken);

    public Task AppendAllTextAsync(string path, ReadOnlyMemory<char> contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        WriteTextAsync(path, FileMode.Append, contents, encoding, cancellationToken);

    public StreamWriter AppendText(string path) => Writer(path, FileMode.Append, Utf8NoBom);

    // --- Copying, moving and replacing ------------------------------------------------------

    public void Copy(string sourceFileName, string destFileName) => Copy(sourceFileName, destFileName, overwrite: false);

    public void Copy(string sourceFileName, string destFileName, bool overwrite)
    {
        Request source = fs.Resolve(sourceFileName, nameof(sourceFileName));
        Request destination = fs.Resolve(destFileName, nameof(destFileName));
        try
        {
            if (source.IsRoot)
            {
                throw Failures.Denied(source.Virtual);
            }

            if (destination.IsRoot)
            {
                throw Failures.Denied(destination.Virtual);
            }

            using CapFile from = fs.Dir.OpenFile(source.Relative, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Creating the destination empties it, so a destination that is the source would
            // be emptied before it was read. System.IO refuses that, and so does this.
            if (overwrite
                && fs.Dir.TryGetMetadata(destination.Relative, followLink: true, out CapMetadata existing)
                && existing.IsSameFileAs(from.GetMetadata()))
            {
                throw new CapIOException(
                    CapErrorKind.InvalidArgument,
                    $"'{source.Virtual}' and '{destination.Virtual}' are the same file.");
            }

            using CapFile to = fs.Dir.OpenFile(
                destination.Relative,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using Stream reading = from.AsStream();
            using Stream writing = to.AsStream();
            reading.CopyTo(writing);
        }
        catch (Exception e) when (fs.Translate(e, source, Expected.File) is { } translated)
        {
            throw translated;
        }
    }

    public void Move(string sourceFileName, string destFileName) => Move(sourceFileName, destFileName, overwrite: false);

    public void Move(string sourceFileName, string destFileName, bool overwrite)
    {
        Request source = fs.Resolve(sourceFileName, nameof(sourceFileName));
        Request destination = fs.Resolve(destFileName, nameof(destFileName));
        try
        {
            if (source.IsRoot || fs.Dir.GetMetadata(source.Relative).Type == CapFileType.Directory)
            {
                throw Failures.FileNotFound(source.Virtual);
            }

            if (destination.IsRoot)
            {
                throw Failures.RootIsFixed(destination.Virtual);
            }

            fs.Dir.Rename(source.Relative, fs.Dir, destination.Relative, overwrite);
        }
        catch (Exception e) when (fs.Translate(e, source, Expected.File) is { } translated)
        {
            throw translated;
        }
    }

    public void Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName) =>
        Replace(sourceFileName, destinationFileName, destinationBackupFileName, ignoreMetadataErrors: false);

    public void Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName, bool ignoreMetadataErrors)
    {
        Request source = fs.Resolve(sourceFileName, nameof(sourceFileName));
        Request destination = fs.Resolve(destinationFileName, nameof(destinationFileName));
        Request? backup = destinationBackupFileName is null
            ? null
            : fs.Resolve(destinationBackupFileName, nameof(destinationBackupFileName));

        RequireFile(source);
        RequireFile(destination);

        try
        {
            if (backup is { } kept)
            {
                if (kept.IsRoot)
                {
                    throw Failures.RootIsFixed(kept.Virtual);
                }

                fs.Dir.Rename(destination.Relative, fs.Dir, kept.Relative, replaceExisting: true);
            }

            fs.Dir.Rename(source.Relative, fs.Dir, destination.Relative, replaceExisting: true);
        }
        catch (Exception e) when (fs.Translate(e, source, Expected.File) is { } translated)
        {
            throw translated;
        }
    }

    // --- Creating and opening ---------------------------------------------------------------

    public FileSystemStream Create(string path) => Create(path, DefaultBufferSize);

    public FileSystemStream Create(string path, int bufferSize) => Create(path, bufferSize, FileOptions.None);

    public FileSystemStream Create(string path, int bufferSize, FileOptions options) =>
        fs.OpenStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize, options);

    public StreamWriter CreateText(string path) => Writer(path, FileMode.Create, Utf8NoBom);

    public FileSystemStream Open(string path, FileMode mode) =>
        Open(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite);

    public FileSystemStream Open(string path, FileMode mode, FileAccess access) =>
        Open(path, mode, access, FileShare.None);

    public FileSystemStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
        fs.OpenStream(path, mode, access, share);

    public FileSystemStream Open(string path, FileStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return fs.OpenStream(
            path,
            options.Mode,
            options.Access,
            options.Share,
            options.BufferSize,
            options.Options,
            options.PreallocationSize,
            OperatingSystem.IsWindows() ? null : options.UnixCreateMode);
    }

    public FileSystemStream OpenRead(string path) => fs.OpenStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public StreamReader OpenText(string path) => Reader(path, Encoding.UTF8);

    public FileSystemStream OpenWrite(string path) => fs.OpenStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

    // --- Reading ----------------------------------------------------------------------------

    public byte[] ReadAllBytes(string path) =>
        fs.Run(path, Expected.File, request =>
            request.IsRoot ? throw Failures.Denied(request.Virtual) : fs.Dir.ReadAllBytes(request.Relative));

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) =>
        fs.RunAsync(path, Expected.File, request =>
            request.IsRoot
                ? throw Failures.Denied(request.Virtual)
                : fs.Dir.ReadAllBytesAsync(request.Relative, cancellationToken));

    public string[] ReadAllLines(string path) => ReadAllLines(path, Encoding.UTF8);

    public string[] ReadAllLines(string path, Encoding encoding) => [.. ReadLines(path, encoding)];

    public Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken);

    public async Task<string[]> ReadAllLinesAsync(string path, Encoding encoding, CancellationToken cancellationToken = default)
    {
        List<string> lines = [];
        await foreach (string line in ReadLinesAsync(path, encoding, cancellationToken).ConfigureAwait(false))
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    public string ReadAllText(string path) => ReadAllText(path, Encoding.UTF8);

    public string ReadAllText(string path, Encoding encoding)
    {
        using StreamReader reader = Reader(path, encoding);
        return reader.ReadToEnd();
    }

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);

    public async Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken = default)
    {
        using StreamReader reader = Reader(path, encoding, FileOptions.Asynchronous);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<string> ReadLines(string path) => ReadLines(path, Encoding.UTF8);

    public IEnumerable<string> ReadLines(string path, Encoding encoding)
    {
        // Opened now, as System.IO opens it, so a missing file is reported by this call
        // rather than by the first step of the enumeration.
        StreamReader reader = Reader(path, encoding);
        return Lines(reader);

        static IEnumerable<string> Lines(StreamReader reader)
        {
            using (reader)
            {
                while (reader.ReadLine() is { } line)
                {
                    yield return line;
                }
            }
        }
    }

    public IAsyncEnumerable<string> ReadLinesAsync(string path, CancellationToken cancellationToken = default) =>
        ReadLinesAsync(path, Encoding.UTF8, cancellationToken);

    public IAsyncEnumerable<string> ReadLinesAsync(string path, Encoding encoding, CancellationToken cancellationToken = default)
    {
        StreamReader reader = Reader(path, encoding, FileOptions.Asynchronous);
        return Lines(reader, cancellationToken);

        static async IAsyncEnumerable<string> Lines(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using (reader)
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    yield return line;
                }
            }
        }
    }

    // --- Writing ----------------------------------------------------------------------------

    public void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        WriteAllBytes(path, bytes.AsSpan());
    }

    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using FileSystemStream stream = fs.OpenStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return WriteAllBytesAsync(path, bytes.AsMemory(), cancellationToken);
    }

    public Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        fs.RunAsync(path, Expected.File, request =>
            request.IsRoot
                ? throw Failures.Denied(request.Virtual)
                : fs.Dir.WriteAllBytesAsync(request.Relative, bytes, cancellationToken));

    public void WriteAllLines(string path, string[] contents) => WriteAllLines(path, (IEnumerable<string>)contents, Utf8NoBom);

    public void WriteAllLines(string path, IEnumerable<string> contents) => WriteAllLines(path, contents, Utf8NoBom);

    public void WriteAllLines(string path, string[] contents, Encoding encoding) =>
        WriteAllLines(path, (IEnumerable<string>)contents, encoding);

    public void WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(encoding);
        using StreamWriter writer = Writer(path, FileMode.Create, encoding);
        foreach (string line in contents)
        {
            writer.WriteLine(line);
        }
    }

    public Task WriteAllLinesAsync(string path, IEnumerable<string> contents, CancellationToken cancellationToken = default) =>
        WriteAllLinesAsync(path, contents, Utf8NoBom, cancellationToken);

    public Task WriteAllLinesAsync(string path, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        WriteLinesAsync(path, FileMode.Create, contents, encoding, cancellationToken);

    public void WriteAllText(string path, string? contents) => WriteAllText(path, contents, Utf8NoBom);

    public void WriteAllText(string path, string? contents, Encoding encoding) =>
        WriteAllText(path, contents.AsSpan(), encoding);

    public void WriteAllText(string path, ReadOnlySpan<char> contents) => WriteAllText(path, contents, Utf8NoBom);

    public void WriteAllText(string path, ReadOnlySpan<char> contents, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        using StreamWriter writer = Writer(path, FileMode.Create, encoding);
        writer.Write(contents);
    }

    public Task WriteAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, contents.AsMemory(), Utf8NoBom, cancellationToken);

    public Task WriteAllTextAsync(string path, string? contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, contents.AsMemory(), encoding, cancellationToken);

    public Task WriteAllTextAsync(string path, ReadOnlyMemory<char> contents, CancellationToken cancellationToken = default) =>
        WriteAllTextAsync(path, contents, Utf8NoBom, cancellationToken);

    public Task WriteAllTextAsync(string path, ReadOnlyMemory<char> contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        WriteTextAsync(path, FileMode.Create, contents, encoding, cancellationToken);

    // --- Existence, removal and links -------------------------------------------------------

    public bool Exists(string? path) =>
        fs.TryDescribeForExistence(path, out CapMetadata metadata) && metadata.Type != CapFileType.Directory;

    public void Delete(string path) =>
        fs.Run(path, Expected.File, request =>
        {
            if (request.IsRoot)
            {
                throw Failures.Denied(request.Virtual);
            }

            try
            {
                fs.Dir.DeleteFile(request.Relative);
            }
            catch (FileNotFoundException) when (fs.HasParentDirectory(request))
            {
                // A missing file is not an error for System.IO. A missing directory above it is.
            }
        });

    public IFileSystemInfo CreateSymbolicLink(string path, string pathToTarget)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathToTarget);
        fs.Run(path, Expected.Any, request =>
        {
            if (request.IsRoot)
            {
                throw Failures.RootIsFixed(request.Virtual);
            }

            fs.Dir.CreateSymlink(request.Relative, pathToTarget);
        });

        return new FileInfoAdapter(fs, path);
    }

    public IFileSystemInfo? ResolveLinkTarget(string linkPath, bool returnFinalTarget) =>
        Descriptions.ResolveLinkTarget(fs, linkPath, returnFinalTarget, directory: false);

    // --- Attributes, modes and times --------------------------------------------------------

    public FileAttributes GetAttributes(string path) =>
        fs.Run(path, Expected.Any, request => Descriptions.AttributesOf(fs, request));

    public FileAttributes GetAttributes(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public void SetAttributes(string path, FileAttributes fileAttributes) => throw Unsupported.Permissions();

    public void SetAttributes(SafeFileHandle fileHandle, FileAttributes fileAttributes) => throw Unsupported.Handle();

    public UnixFileMode GetUnixFileMode(string path) => Descriptions.GetUnixFileMode(fs, path);

    public UnixFileMode GetUnixFileMode(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public void SetUnixFileMode(string path, UnixFileMode mode) => throw Unsupported.Permissions();

    public void SetUnixFileMode(SafeFileHandle fileHandle, UnixFileMode mode) => throw Unsupported.Handle();

    public void Decrypt(string path) => throw Unsupported.Encryption();

    public void Encrypt(string path) => throw Unsupported.Encryption();

    public DateTime GetCreationTime(string path) => Descriptions.GetTime(fs, path, TimeKind.Creation, utc: false);

    public DateTime GetCreationTime(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public DateTime GetCreationTimeUtc(string path) => Descriptions.GetTime(fs, path, TimeKind.Creation, utc: true);

    public DateTime GetCreationTimeUtc(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public DateTime GetLastAccessTime(string path) => Descriptions.GetTime(fs, path, TimeKind.Access, utc: false);

    public DateTime GetLastAccessTime(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public DateTime GetLastAccessTimeUtc(string path) => Descriptions.GetTime(fs, path, TimeKind.Access, utc: true);

    public DateTime GetLastAccessTimeUtc(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public DateTime GetLastWriteTime(string path) => Descriptions.GetTime(fs, path, TimeKind.Write, utc: false);

    public DateTime GetLastWriteTime(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public DateTime GetLastWriteTimeUtc(string path) => Descriptions.GetTime(fs, path, TimeKind.Write, utc: true);

    public DateTime GetLastWriteTimeUtc(SafeFileHandle fileHandle) => throw Unsupported.Handle();

    public void SetCreationTime(string path, DateTime creationTime) => throw Unsupported.CreationTime();

    public void SetCreationTime(SafeFileHandle fileHandle, DateTime creationTime) => throw Unsupported.Handle();

    public void SetCreationTimeUtc(string path, DateTime creationTimeUtc) => throw Unsupported.CreationTime();

    public void SetCreationTimeUtc(SafeFileHandle fileHandle, DateTime creationTimeUtc) => throw Unsupported.Handle();

    public void SetLastAccessTime(string path, DateTime lastAccessTime) =>
        Descriptions.SetTime(fs, path, TimeKind.Access, lastAccessTime, utc: false);

    public void SetLastAccessTime(SafeFileHandle fileHandle, DateTime lastAccessTime) => throw Unsupported.Handle();

    public void SetLastAccessTimeUtc(string path, DateTime lastAccessTimeUtc) =>
        Descriptions.SetTime(fs, path, TimeKind.Access, lastAccessTimeUtc, utc: true);

    public void SetLastAccessTimeUtc(SafeFileHandle fileHandle, DateTime lastAccessTimeUtc) => throw Unsupported.Handle();

    public void SetLastWriteTime(string path, DateTime lastWriteTime) =>
        Descriptions.SetTime(fs, path, TimeKind.Write, lastWriteTime, utc: false);

    public void SetLastWriteTime(SafeFileHandle fileHandle, DateTime lastWriteTime) => throw Unsupported.Handle();

    public void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) =>
        Descriptions.SetTime(fs, path, TimeKind.Write, lastWriteTimeUtc, utc: true);

    public void SetLastWriteTimeUtc(SafeFileHandle fileHandle, DateTime lastWriteTimeUtc) => throw Unsupported.Handle();

    // --- Helpers ----------------------------------------------------------------------------

    private StreamReader Reader(string path, Encoding encoding, FileOptions options = FileOptions.None)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        FileSystemStream stream = fs.OpenStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, options);
        return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
    }

    private StreamWriter Writer(string path, FileMode mode, Encoding encoding, FileOptions options = FileOptions.None)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        FileSystemStream stream = fs.OpenStream(
            path, mode, FileAccess.Write, FileShare.Read, DefaultBufferSize, options);
        return new StreamWriter(stream, encoding);
    }

    private async Task WriteLinesAsync(
        string path, FileMode mode, IEnumerable<string> contents, Encoding encoding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        await using StreamWriter writer = Writer(path, mode, encoding, FileOptions.Asynchronous);
        foreach (string line in contents)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteTextAsync(
        string path, FileMode mode, ReadOnlyMemory<char> contents, Encoding encoding, CancellationToken cancellationToken)
    {
        await using StreamWriter writer = Writer(path, mode, encoding, FileOptions.Asynchronous);
        await writer.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Throws what <c>System.IO</c> throws when a name that must hold a file does not.</summary>
    private void RequireFile(in Request request)
    {
        if (request.IsRoot)
        {
            throw Failures.Denied(request.Virtual);
        }

        fs.Run(request.Virtual, Expected.File, r => fs.Describe(r, followLink: false));
    }
}
