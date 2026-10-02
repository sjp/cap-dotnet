using System.IO.Abstractions;
using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary>
/// An <see cref="IFileSystem"/> whose every path is resolved beneath a <see cref="Dir"/>.
/// </summary>
/// <remarks>
/// <para>
/// Code written against <see cref="IFileSystem"/> takes whatever implementation it is given.
/// Handing it one of these instead of the ambient <c>FileSystem</c> confines it to one
/// directory without changing the code: the change is made where the program is assembled,
/// not in the component.
/// </para>
/// <code>
/// using Dir uploads = Dir.Open("/srv/app/uploads", AmbientAuthority.Acquire());
/// services.AddSingleton&lt;IFileSystem&gt;(new DirFileSystem(uploads));
/// </code>
/// <para>
/// <strong>A virtual namespace.</strong> <see cref="IFileSystem"/> callers pass absolute
/// paths and expect a current directory and full names back, so this presents the
/// <see cref="Dir"/> as the whole of a namespace whose root is spelled <c>/</c> (or, on
/// Windows, as a drive; see <see cref="DirFileSystemOptions.VirtualDrive"/>). An absolute path
/// has the root removed and nothing else, a relative one is taken against the current
/// directory, and what remains is handed to the <see cref="Dir"/> as the caller wrote it.
/// <c>..</c>, symbolic links and every other component are resolved beneath the handle,
/// which refuses what would leave it with a <see cref="SandboxEscapeException"/>. No check on
/// the text stands between the caller and the disk.
/// </para>
/// <para>
/// <strong>Full names are virtual.</strong> <c>FullName</c>, <c>GetFullPath</c> and the
/// strings enumeration returns name paths in this namespace, folded lexically as
/// <c>System.IO</c> folds them. They mean something only to this adapter: given back to it,
/// they are harmless, but given to <c>System.IO</c> they name a different file, on the host,
/// outside the directory. A folded name is also a request rather than a location, because
/// when a component before a <c>..</c> is a symbolic link the folded string and the original
/// lead to different places.
/// </para>
/// <para>
/// <strong>What does not carry over.</strong> Members with no meaning beneath a directory
/// handle, such as drives, watchers, version information, access control lists and anything
/// that takes a <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>, throw
/// <see cref="NotSupportedException"/> saying why. Behaviour the library chooses on purpose
/// carries through too: an open that creates or truncates refuses a symbolic link at the
/// final name rather than following it. The package documentation lists every difference
/// from <c>System.IO</c>.
/// </para>
/// <para>
/// <strong>Ownership.</strong> The <see cref="Dir"/> is never disposed by this object, which
/// is not <see cref="IDisposable"/>. Whoever opened it, usually the composition root, closes
/// it, after the last use of this file system.
/// </para>
/// <para>
/// Safe to use from any thread. The current directory is this instance's own state, shared by
/// every caller of the instance and never the process's.
/// </para>
/// </remarks>
public sealed class DirFileSystem : IFileSystem
{
    private string _currentDirectory;

    /// <summary>Creates a file system confined to a directory, with its root spelled <c>/</c>.</summary>
    /// <param name="root">The directory that is the whole of this file system. It is not disposed by it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    public DirFileSystem(Dir root)
        : this(root, null)
    {
    }

    /// <summary>Creates a file system confined to a directory.</summary>
    /// <param name="root">The directory that is the whole of this file system. It is not disposed by it.</param>
    /// <param name="options">How the namespace is presented, or null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="DirFileSystemOptions.VirtualDrive"/> is not an ASCII letter.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// <see cref="DirFileSystemOptions.VirtualDrive"/> is set on a system other than Windows.
    /// </exception>
    public DirFileSystem(Dir root, DirFileSystemOptions? options)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= DirFileSystemOptions.Default;

        char? drive = null;
        if (options.VirtualDrive is char letter)
        {
            if (!char.IsAsciiLetter(letter))
            {
                throw new ArgumentException("The virtual drive must be an ASCII letter.", nameof(options));
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "A virtual drive is only offered on Windows, where code that expects one runs.");
            }

            drive = char.ToUpperInvariant(letter);
        }

        Dir = root;
        Paths = new VirtualPath(drive, OperatingSystem.IsWindows());
        _currentDirectory = Paths.Root;

        File = new FileAdapter(this);
        Directory = new DirectoryAdapter(this);
        FileInfo = new FileInfoFactory(this);
        DirectoryInfo = new DirectoryInfoFactory(this);
        FileStream = new FileStreamFactory(this);
        Path = new PathAdapter(this);
        DriveInfo = new DriveInfoFactory(this);
        FileSystemWatcher = new FileSystemWatcherFactory(this);
        FileVersionInfo = new FileVersionInfoFactory(this);
        RandomAccess = new RandomAccessAdapter(this);
    }

    /// <inheritdoc/>
    public IDirectory Directory { get; }

    /// <inheritdoc/>
    public IDirectoryInfoFactory DirectoryInfo { get; }

    /// <inheritdoc/>
    /// <remarks>Every member throws <see cref="NotSupportedException"/>.</remarks>
    public IDriveInfoFactory DriveInfo { get; }

    /// <inheritdoc/>
    public IFile File { get; }

    /// <inheritdoc/>
    public IFileInfoFactory FileInfo { get; }

    /// <inheritdoc/>
    public IFileStreamFactory FileStream { get; }

    /// <inheritdoc/>
    /// <remarks>Every member throws <see cref="NotSupportedException"/>.</remarks>
    public IFileSystemWatcherFactory FileSystemWatcher { get; }

    /// <inheritdoc/>
    /// <remarks>Every member throws <see cref="NotSupportedException"/>.</remarks>
    public IFileVersionInfoFactory FileVersionInfo { get; }

    /// <inheritdoc/>
    public IPath Path { get; }

    /// <inheritdoc/>
    /// <remarks>Every member throws <see cref="NotSupportedException"/>.</remarks>
    public IRandomAccess RandomAccess { get; }

    /// <summary>The directory this file system is confined to.</summary>
    internal Dir Dir { get; }

    /// <summary>The syntax of the virtual namespace.</summary>
    internal VirtualPath Paths { get; }

    /// <summary>
    /// The current directory, as the virtual absolute path it was set with. Unfolded, so that
    /// a relative path taken against it reaches the <see cref="Dir"/> with the caller's own
    /// components.
    /// </summary>
    internal string CurrentDirectory
    {
        get => Volatile.Read(ref _currentDirectory);
        set => Volatile.Write(ref _currentDirectory, value);
    }

    /// <summary>Whether names compare without regard to case where this runs.</summary>
    internal static bool IgnoresCase => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Validates a caller's path and spells out what it asks for.</summary>
    internal Request Resolve(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        ArgumentException.ThrowIfNullOrEmpty(path, parameterName);
        return Paths.Resolve(path, CurrentDirectory);
    }

    /// <summary>Runs an operation on a path, translating its failures as <c>System.IO</c> reports them.</summary>
    internal T Run<T>(string path, Expected expected, Func<Request, T> operation, string parameterName = "path")
    {
        Request request = Resolve(path, parameterName);
        try
        {
            return operation(request);
        }
        catch (Exception e) when (Translate(e, request, expected) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>Runs an operation on a path, translating its failures as <c>System.IO</c> reports them.</summary>
    internal void Run(string path, Expected expected, Action<Request> operation, string parameterName = "path") =>
        Run(path, expected, request =>
        {
            operation(request);
            return true;
        }, parameterName);

    /// <summary>Runs an asynchronous operation on a path, translating its failures as <c>System.IO</c> reports them.</summary>
    internal async Task<T> RunAsync<T>(string path, Expected expected, Func<Request, Task<T>> operation)
    {
        Request request = Resolve(path, nameof(path));
        try
        {
            return await operation(request).ConfigureAwait(false);
        }
        catch (Exception e) when (Translate(e, request, expected) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// The exception <c>System.IO</c> would throw in place of <paramref name="exception"/>, or
    /// null to let it propagate unchanged.
    /// </summary>
    /// <remarks>
    /// Adds to <see cref="Failures.Translate"/> the one distinction that needs a look at the
    /// tree: a file reported missing because the directory above it is missing is a missing
    /// directory to <c>System.IO</c>.
    /// </remarks>
    internal Exception? Translate(Exception exception, in Request request, Expected expected)
    {
        Exception? translated = Failures.Translate(exception, request.Virtual, expected);
        if (translated is FileNotFoundException && !HasParentDirectory(request))
        {
            return Failures.PartNotFound(request.Virtual, exception);
        }

        return translated;
    }

    /// <summary>
    /// The exception <c>System.IO</c> would throw in place of a failure at the destination of
    /// a copy, move or replace, naming the destination; or null to let it propagate unchanged.
    /// </summary>
    /// <remarks>
    /// Adds to <see cref="Translate"/> the name that is already taken, which a one-path
    /// operation leaves as the <see cref="Dir"/> reports it.
    /// </remarks>
    internal Exception? TranslateDestination(Exception exception, in Request destination, Expected expected)
    {
        if (exception is not SandboxEscapeException && CapIOException.KindOf(exception) == CapErrorKind.AlreadyExists)
        {
            return expected == Expected.Directory
                ? Failures.DirectoryExists(destination.Virtual, exception)
                : Failures.FileExists(destination.Virtual, exception);
        }

        return Translate(exception, destination, expected);
    }

    /// <summary>
    /// The exception <c>System.IO</c> would throw in place of a failed rename, naming the
    /// end of it that failed; or null to let it propagate unchanged.
    /// </summary>
    /// <remarks>
    /// A rename reports one failure for two names. The source is to blame only when it is not
    /// there; otherwise what went wrong is at the destination: the directory that would hold
    /// it is missing, its name is taken, or it is a directory.
    /// </remarks>
    internal Exception? TranslateRename(Exception exception, in Request source, in Request destination, Expected expected)
    {
        if (exception is SandboxEscapeException)
        {
            return null;
        }

        return IsThere(source)
            ? TranslateDestination(exception, destination, expected)
            : Translate(exception, source, expected);
    }

    /// <summary>Whether a request names something, a link that leads nowhere included.</summary>
    private bool IsThere(in Request request)
    {
        try
        {
            return TryDescribe(request, followLink: false, out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether the directory that would hold a request's last name is there.</summary>
    internal bool HasParentDirectory(in Request request)
    {
        if (Paths.ParentRequest(request.Virtual) is not { } parent)
        {
            return true;
        }

        try
        {
            return TryDescribe(Paths.Resolve(parent, CurrentDirectory), followLink: true, out CapMetadata holder)
                && holder.Type == CapFileType.Directory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Runs an asynchronous operation on a path, translating its failures as <c>System.IO</c> reports them.</summary>
    internal Task RunAsync(string path, Expected expected, Func<Request, Task> operation) =>
        RunAsync(path, expected, async request =>
        {
            await operation(request).ConfigureAwait(false);
            return true;
        });

    /// <summary>Opens the directory a request names; the root is a copy of the handle this holds.</summary>
    internal Dir OpenDirectory(in Request request) =>
        request.IsRoot ? Dir.Clone() : Dir.OpenDir(request.Relative);

    /// <summary>Describes what a request names, following a final link if asked.</summary>
    internal CapMetadata Describe(in Request request, bool followLink) =>
        request.IsRoot ? Dir.GetMetadata() : Dir.GetMetadata(request.Relative, followLink);

    /// <summary>Describes what a request names, or reports that it could not.</summary>
    internal bool TryDescribe(in Request request, bool followLink, out CapMetadata metadata)
    {
        if (request.IsRoot)
        {
            metadata = Dir.GetMetadata();
            return true;
        }

        return Dir.TryGetMetadata(request.Relative, followLink, out metadata);
    }

    /// <summary>
    /// Describes what a path names as <c>System.IO</c>'s existence checks see it: through a
    /// final link, or as the link itself when it leads nowhere. Never throws.
    /// </summary>
    internal bool TryDescribeForExistence(string? path, out CapMetadata metadata)
    {
        metadata = default;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return TryDescribeForExistence(Paths.Resolve(path, CurrentDirectory), out metadata, out _);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Describes what a request names as <c>System.IO</c>'s existence checks see it, and the
    /// entry itself, which differs only for a link. Never throws.
    /// </summary>
    /// <remarks>
    /// The entry is described first, so a missing name or one that is not a link costs one
    /// description; only a link is described again, through itself.
    /// </remarks>
    internal bool TryDescribeForExistence(in Request request, out CapMetadata metadata, out CapMetadata own)
    {
        metadata = default;
        own = default;
        try
        {
            if (!TryDescribe(request, followLink: false, out own))
            {
                return false;
            }

            metadata = own.Type == CapFileType.Symlink && TryDescribe(request, followLink: true, out CapMetadata target)
                ? target
                : own;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            metadata = default;
            own = default;
            return false;
        }
    }

    /// <summary>Opens a file as a stream that carries its virtual path.</summary>
    internal FileSystemStream OpenStream(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize = 4096,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0,
        UnixFileMode? unixCreateMode = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferSize);
        if (unixCreateMode is not null)
        {
            throw Unsupported.CreateMode();
        }

        return Run(path, Expected.File, request =>
        {
            if (request.IsRoot)
            {
                throw Failures.Denied(request.Virtual);
            }

            CapFile file = Dir.OpenFile(
                request.Relative,
                mode,
                access,
                share,
                options,
                preallocationSize,
                append: mode == FileMode.Append);
            try
            {
                // A stream over a file the system completes work on only borrows the file,
                // which costs nothing, so the file stays here to be stored through: the stream
                // beneath cannot be asked to. Any other stream takes the handle, and on the
                // host is a FileStream, which can.
                bool isAsync = (options & FileOptions.Asynchronous) != 0;
                Stream stream = file.AsStream(leaveOpen: isAsync, bufferSize: bufferSize);
                if (mode == FileMode.Append && stream.CanSeek)
                {
                    stream.Seek(0, SeekOrigin.End);
                }

                Action? sync = isAsync
                    ? () => file.Flush(toDisk: true)
                    : stream is FileStream host ? () => host.Flush(flushToDisk: true) : null;
                return new DirFileSystemStream(
                    stream,
                    Paths,
                    request.Virtual,
                    isAsync,
                    sync,
                    isAsync ? file : null);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        });
    }

    /// <summary>
    /// Creates a directory and every missing directory above it, each one resolved from the
    /// root by the <see cref="Dir"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each leading portion of the caller's path is opened if it is already a directory,
    /// following a link there as <c>System.IO</c> does, and created only if nothing is there.
    /// Any other reason it cannot be opened, a link that leads out of the tree among them, is
    /// reported as it is. The portions are
    /// slices of the caller's string, so a <c>..</c> in it is walked by the
    /// <see cref="Dir"/>, not folded here, and one that would climb out is refused as an escape.
    /// </para>
    /// <para>
    /// A portion that opens as a directory was reached through every portion before it, so
    /// those need not be opened again: the portions are tried from the longest down, and only
    /// the ones beyond the longest that is there are created. A path that climbs is taken a
    /// portion at a time from the first, since a portion before its <c>..</c> has to be there
    /// even when the one after it already is.
    /// </para>
    /// </remarks>
    internal void CreateDirectoryChain(in Request request)
    {
        if (request.IsRoot)
        {
            return;
        }

        string relative = request.Relative;
        List<string> prefixes = [.. Paths.CreatablePrefixes(relative)];
        int start = 0;
        if (!Paths.Climbs(relative))
        {
            for (start = prefixes.Count; start > 0; start--)
            {
                bool? there = TryOpenExisting(prefixes[start - 1]);
                if (there == true)
                {
                    break;
                }

                if (there is null)
                {
                    // Something other than a missing name is in the way; it is reported by
                    // the portion it belongs to, as the walk from the first finds it.
                    start = 0;
                    break;
                }
            }
        }

        for (int i = start; i < prefixes.Count; i++)
        {
            string prefix = prefixes[i];
            Dir directory;
            try
            {
                try
                {
                    directory = Dir.OpenDir(prefix);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    directory = Dir.OpenOrCreateDir(prefix);
                }
            }
            catch (CapIOException e) when (
                e.Kind is CapErrorKind.NotADirectory or CapErrorKind.SymbolicLink
                && Paths.EndsInLastName(relative, prefix))
            {
                // The name asked for holds something other than a directory: a file, or a link
                // to one or to nothing. System.IO says it is taken; a missing part of the path
                // is what it says only of a name further up.
                throw Failures.FileExists(request.Virtual, e);
            }

            directory.Dispose();
        }

        // A path ending in `..` or `.` creates nothing of its own; it still has to name a
        // directory, as it does for System.IO. One ending in a name has just been opened.
        if (prefixes.Count == 0 || !Paths.EndsInLastName(relative, prefixes[^1]))
        {
            OpenDirectory(request).Dispose();
        }
    }

    /// <summary>
    /// Whether a leading portion of a path is a directory: true when it opens, false when
    /// nothing is there, null for any other answer.
    /// </summary>
    private bool? TryOpenExisting(string prefix)
    {
        try
        {
            Dir.OpenDir(prefix).Dispose();
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The virtual scratch directory, created beneath the root the first time it is asked for.
    /// </summary>
    internal Dir OpenTempDirectory() => Dir.OpenOrCreateDir(VirtualPath.TempDirectoryName);

    /// <summary>Removes a directory, and with <paramref name="recursive"/> everything in it.</summary>
    internal void DeleteDirectory(string path, bool recursive) =>
        Run(path, Expected.Directory, request =>
        {
            if (request.IsRoot)
            {
                throw Failures.RootIsFixed(request.Virtual);
            }

            if (!recursive)
            {
                try
                {
                    Dir.DeleteDir(request.Relative);
                }
                catch (CapIOException e) when (
                    e.Kind is CapErrorKind.NotADirectory or CapErrorKind.SymbolicLink
                    && Dir.GetMetadata(request.Relative).Type == CapFileType.Symlink)
                {
                    // Only a name that is not a directory is described again, so removing an
                    // ordinary directory costs nothing more.
                    DeleteLink(request);
                }

                return;
            }

            CapMetadata metadata = Dir.GetMetadata(request.Relative);
            switch (metadata.Type)
            {
                case CapFileType.Directory:
                    Dir.DeleteTree(request.Relative);
                    break;
                case CapFileType.Symlink:
                    DeleteLink(request);
                    break;
                default:
                    throw new CapIOException(CapErrorKind.NotADirectory, $"'{request.Virtual}' is not a directory.");
            }
        });

    /// <summary>
    /// Removes the link a directory path names. What it points at is not reached, as with
    /// <c>System.IO</c>. On Windows a link to a directory is removed as a directory.
    /// </summary>
    private void DeleteLink(in Request request)
    {
        if (!Dir.TryDeleteFile(request.Relative))
        {
            Dir.DeleteDir(request.Relative);
        }
    }
}
