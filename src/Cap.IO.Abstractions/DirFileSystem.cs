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
            Request request = Paths.Resolve(path, CurrentDirectory);
            return TryDescribe(request, followLink: true, out metadata)
                || (TryDescribe(request, followLink: false, out metadata) && metadata.Type == CapFileType.Symlink);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
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
                Stream stream = file.AsStream(leaveOpen: false, bufferSize: bufferSize);
                if (mode == FileMode.Append && stream.CanSeek)
                {
                    stream.Seek(0, SeekOrigin.End);
                }

                return new DirFileSystemStream(
                    stream,
                    Paths.GetFullPath(request.Virtual, CurrentDirectory),
                    (options & FileOptions.Asynchronous) != 0);
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
    /// Each leading portion of the caller's path is opened if it is already a directory,
    /// following a link there as <c>System.IO</c> does, and created only if nothing is there.
    /// Any other reason it cannot be opened, a link that leads out of the tree among them, is
    /// reported as it is. The portions are
    /// slices of the caller's string, so a <c>..</c> in it is walked by the
    /// <see cref="Dir"/>, not folded here, and one that would climb out is refused as an escape.
    /// </remarks>
    internal void CreateDirectoryChain(in Request request)
    {
        if (request.IsRoot)
        {
            return;
        }

        foreach (string prefix in Paths.CreatablePrefixes(request.Relative))
        {
            Dir directory;
            try
            {
                directory = Dir.OpenDir(prefix);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                directory = Dir.OpenOrCreateDir(prefix);
            }

            directory.Dispose();
        }

        // A path ending in `..` or `.` creates nothing of its own; it still has to name a
        // directory, as it does for System.IO.
        OpenDirectory(request).Dispose();
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
                Dir.DeleteDir(request.Relative);
                return;
            }

            CapMetadata metadata = Dir.GetMetadata(request.Relative);
            switch (metadata.Type)
            {
                case CapFileType.Directory:
                    Dir.DeleteTree(request.Relative);
                    break;
                case CapFileType.Symlink:
                    // The link is removed, and what it points at is not reached, as with
                    // System.IO. On Windows a link to a directory is removed as a directory.
                    if (!Dir.TryDeleteFile(request.Relative))
                    {
                        Dir.DeleteDir(request.Relative);
                    }

                    break;
                default:
                    throw new CapIOException(CapErrorKind.NotADirectory, $"'{request.Virtual}' is not a directory.");
            }
        });
}
