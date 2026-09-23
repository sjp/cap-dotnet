namespace Cap.Std;

/// <summary>
/// What a filesystem records about who may do what with an object.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The platforms do not record the same thing, and this type does not pretend they
/// do.</strong> A Unix mode says which of three classes of user may read, write or execute,
/// and it is the whole of the answer. A Windows attribute set says nothing of the sort: it
/// carries a read-only flag, a hidden flag and a dozen others, while the actual permission
/// decision lives in a security descriptor that is not this. Flattening the two into one
/// portable shape would mean inventing a common denominator that is true of neither, and
/// code written against it would be code that believes a file is writable because a flag it
/// misread was clear.
/// </para>
/// <para>
/// So there is no portable accessor here, deliberately. Each platform's answer is reached by
/// a member that names that platform, and the member reports false rather than an invented
/// value when asked on the other one. Code that reads permissions has therefore said in its
/// own text which system it is assuming, and code that must run on both has been made to
/// write the branch rather than to discover at runtime that it needed one.
/// </para>
/// <para>
/// A value that came from no filesystem — the default of the type — answers false to both.
/// </para>
/// <para>
/// <strong>Symbolic links.</strong> A value read from a description of a link holds the
/// link's own mode or attributes, not its target's; on Windows those attributes include the
/// bit that marks the entry as redirecting.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are immutable, so they are safe to share
/// between threads and to read from any number of them at once.
/// </para>
/// </remarks>
public readonly struct CapPermissions
{
    private readonly UnixFileMode _unixMode;
    private readonly FileAttributes _windowsAttributes;
    private readonly Origin _origin;

    internal CapPermissions(UnixFileMode? unixMode, FileAttributes? windowsAttributes)
    {
        if (unixMode is { } mode)
        {
            _unixMode = mode;
            _origin = Origin.Unix;
        }
        else if (windowsAttributes is { } attributes)
        {
            _windowsAttributes = attributes;
            _origin = Origin.Windows;
        }
    }

    /// <summary>Which system's answer this holds, if either.</summary>
    private enum Origin : byte
    {
        /// <summary>Neither: the value describes nothing.</summary>
        None = 0,

        /// <summary>A Unix mode.</summary>
        Unix,

        /// <summary>A Windows attribute set.</summary>
        Windows,
    }

    /// <summary>
    /// Reads the Unix mode bits, when there are any.
    /// </summary>
    /// <param name="mode">
    /// The permission, set-user, set-group and sticky bits, when this returns true.
    /// </param>
    /// <returns>True on a system that records a mode; false on one that does not.</returns>
    /// <remarks>
    /// <para>
    /// What comes back is what the filesystem stores, not what the calling process may do.
    /// The two differ whenever the process is not the file's owner, whenever a mandatory
    /// access control system has an opinion, and whenever the filesystem is mounted
    /// read-only — so this is the right thing to copy onto a new file and the wrong thing to
    /// check before opening one. The way to find out whether an open will succeed is to
    /// attempt it.
    /// </para>
    /// <para>
    /// The type bits are not included. What the object is has its own property, and leaving
    /// the bits in would make a mode compare unequal to the one a caller assembled to create
    /// the same permissions.
    /// </para>
    /// </remarks>
    public bool TryGetUnixMode(out UnixFileMode mode)
    {
        mode = _unixMode;
        return _origin == Origin.Unix;
    }

    /// <summary>
    /// Reads the Windows file attributes, when there are any.
    /// </summary>
    /// <param name="attributes">The attribute bits, when this returns true.</param>
    /// <returns>True on Windows; false on a system that records no such thing.</returns>
    /// <remarks>
    /// These are flags about the file — read-only, hidden, archived, compressed, and the
    /// bit that says the entry redirects — and not a statement about who may reach it. The
    /// read-only flag in particular is a property of the file rather than a permission, and
    /// an account with the right to change it can clear it and then write. A caller deciding
    /// whether it is allowed to do something should attempt the thing.
    /// </remarks>
    public bool TryGetWindowsAttributes(out FileAttributes attributes)
    {
        attributes = _windowsAttributes;
        return _origin == Origin.Windows;
    }

    /// <summary>
    /// The Unix mode this value carries, or null when it came from the other kind of system.
    /// </summary>
    /// <remarks>
    /// The same answer <see cref="TryGetUnixMode"/> gives, in the shape the layer beneath
    /// takes. Internal because a nullable pair is the right thing to hand a platform and the
    /// wrong thing to hand a caller: a caller asking about permissions has to be made to say
    /// which system's they mean, and a null they could ignore would let them not.
    /// </remarks>
    internal UnixFileMode? UnixMode => _origin == Origin.Unix ? _unixMode : null;

    /// <summary>
    /// The Windows attributes this value carries, or null when it came from the other kind of
    /// system.
    /// </summary>
    internal FileAttributes? WindowsAttributes => _origin == Origin.Windows ? _windowsAttributes : null;

    /// <summary>True when this value describes permissions some filesystem actually records.</summary>
    internal bool IsPresent => _origin != Origin.None;

    /// <summary>The permissions as text, for a log line.</summary>
    public override string ToString() => _origin switch
    {
        Origin.Unix => _unixMode.ToString(),
        Origin.Windows => _windowsAttributes.ToString(),
        _ => "none",
    };
}
