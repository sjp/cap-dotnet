namespace Cap.Primitives;

/// <summary>
/// What a filesystem entry is.
/// </summary>
/// <remarks>
/// <para>
/// Wider than the handful of kinds path resolution cares about, because this is what a
/// caller is told rather than what a walk decides from. A caller listing a directory has
/// reasons to distinguish a socket from a pipe that resolution never has: it is deciding
/// what to do with the entry, not whether a path can continue through it.
/// </para>
/// <para>
/// <strong>It describes the name, never what the name points at.</strong> An entry holding a
/// symbolic link reports <see cref="Symlink"/> whatever the link leads to, including when it
/// leads to a directory. Reporting the target's kind would mean following the link in order
/// to answer, which is the decision the handle's own policy exists to make, and it would
/// give the same answer for a link that leaves the subtree as for one that does not.
/// </para>
/// </remarks>
public enum CapFileType
{
    /// <summary>
    /// The kind could not be determined.
    /// </summary>
    /// <remarks>
    /// Reported rather than guessed. A directory read that does not carry the kind — which
    /// some filesystems do not — is followed by a lookup of the name, and this is what
    /// survives when that lookup finds the entry gone, which is an ordinary outcome for a
    /// directory something else is writing to.
    /// </remarks>
    Unknown = 0,

    /// <summary>An ordinary file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>
    /// A symbolic link. On Windows this also covers a junction, whose stored target is
    /// always absolute.
    /// </summary>
    Symlink,

    /// <summary>
    /// Windows only: something that redirects, by a mechanism that is not a filesystem link
    /// — an application execution alias, a container image link, a tag introduced after this
    /// was written.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Symlink"/> because the difference is the whole of the safety
    /// here. What such an entry stores is a structure of unknown shape, and reading it as if
    /// it held a path would mean taking whichever bytes happened to land at the offset a
    /// link keeps its target at and using them as a destination. Nothing follows one, and
    /// the kind is reported so that a caller does not either.
    /// </remarks>
    ReparsePoint,

    /// <summary>A Unix domain socket.</summary>
    Socket,

    /// <summary>A named pipe.</summary>
    Fifo,

    /// <summary>A character device.</summary>
    CharDevice,

    /// <summary>A block device.</summary>
    BlockDevice,
}
