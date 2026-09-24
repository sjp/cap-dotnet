namespace Cap.Std;

/// <summary>
/// What <see cref="Dir.OpenAny"/> opened: a directory or a file, whichever the name held.
/// </summary>
/// <remarks>
/// <para>
/// Holds exactly one handle, and says which kind through <see cref="IsDirectory"/>. The kind
/// was read from the object the open reached, not from the name, so it stays true of the
/// handle however the name has been reassigned since.
/// </para>
/// <para>
/// The handle is taken out with <see cref="TakeDir"/> or <see cref="TakeFile"/>, and from
/// then on belongs to the caller, who disposes it; disposing this afterwards leaves it open.
/// A handle never taken is closed when this is disposed, so a caller that decides it did not
/// want what it found only has to dispose this.
/// </para>
/// <para>
/// The two kinds are kept as the two types they always are, rather than joined under a common
/// one. A directory handle resolves names beneath it and a file handle reads data, and
/// anything that let one stand in for the other would blur authority this library keeps
/// separate.
/// </para>
/// <para>
/// Taking and disposing are safe to call from any thread; the handle is handed out once at
/// most, to whichever call gets there first.
/// </para>
/// </remarks>
public sealed class CapOpened : IDisposable
{
    private readonly bool _isDirectory;
    private IDisposable? _held;
    private volatile bool _disposed;

    internal CapOpened(Dir directory)
    {
        _isDirectory = true;
        _held = directory;
    }

    internal CapOpened(CapFile file)
    {
        _isDirectory = false;
        _held = file;
    }

    /// <summary>
    /// Whether the name held a directory. When false it held something else — a regular file,
    /// or a device or other special file opened as one.
    /// </summary>
    /// <remarks>
    /// Still answers after the handle has been taken or this has been disposed: it describes
    /// what was opened, not what is held now.
    /// </remarks>
    public bool IsDirectory => _isDirectory;

    /// <summary>
    /// Takes the directory out, leaving the caller responsible for disposing it.
    /// </summary>
    /// <returns>The directory, carrying the symbolic-link policy of the handle it was opened beneath.</returns>
    /// <exception cref="InvalidOperationException">
    /// What was opened is not a directory, or the handle has already been taken.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public Dir TakeDir()
    {
        if (!_isDirectory)
        {
            throw new InvalidOperationException(
                "The name held a file, not a directory. Check IsDirectory and take the file instead.");
        }

        return (Dir)Take();
    }

    /// <summary>
    /// Takes the file out, leaving the caller responsible for disposing it.
    /// </summary>
    /// <returns>The file, open to read.</returns>
    /// <exception cref="InvalidOperationException">
    /// What was opened is a directory, or the handle has already been taken.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public CapFile TakeFile()
    {
        if (_isDirectory)
        {
            throw new InvalidOperationException(
                "The name held a directory, not a file. Check IsDirectory and take the directory instead.");
        }

        return (CapFile)Take();
    }

    /// <summary>
    /// Closes the handle, unless it has been taken.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _held, null)?.Dispose();
    }

    private IDisposable Take()
    {
        IDisposable? held = Interlocked.Exchange(ref _held, null);
        if (held is not null)
        {
            return held;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new InvalidOperationException("The handle has already been taken.");
    }
}
