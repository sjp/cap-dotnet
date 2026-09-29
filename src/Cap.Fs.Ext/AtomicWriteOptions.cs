namespace Cap.Fs.Ext;

/// <summary>
/// How an atomic write publishes a file.
/// </summary>
/// <remarks>
/// <strong>Immutable once constructed.</strong> Every property is set only by an object
/// initializer, so one instance — <see cref="Default"/> included — can be shared by any number
/// of writes on any number of threads.
/// </remarks>
public sealed class AtomicWriteOptions
{
    /// <summary>
    /// The ordinary settings: everything committed, and a replaced file's permissions kept.
    /// </summary>
    public static AtomicWriteOptions Default { get; } = new();

    /// <summary>How far the write is pushed before it is treated as done.</summary>
    /// <remarks>
    /// <see cref="Cap.Fs.Ext.Durability.FileAndDirectory"/> by default, the only setting under
    /// which the published name survives the power going out.
    /// </remarks>
    public Durability Durability { get; init; } = Durability.FileAndDirectory;

    /// <summary>
    /// Whether the published file is given the permissions of the file it replaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On, because the published file is a new object, and a new object gets whatever the
    /// directory and the process's own settings give one — so without this a file kept
    /// private, a secrets or state file created owner-only, would be readable by everyone the
    /// first time it was republished, and nothing would say so. With it on, when a file
    /// already holds the name, the new one is given that file's permissions as the platform
    /// records them: the mode bits on Unix, the attribute flags on Windows. When nothing holds
    /// the name, or something other than a file does, the new file keeps what it was created
    /// with.
    /// </para>
    /// <para>
    /// On a <see cref="Cap.Std.Dir"/> on Linux or macOS the new contents are written into a
    /// file only its owner can read, and the replaced file's mode is given to it once they are
    /// written, so a private file's next contents are never readable by anyone else, even
    /// under the scratch name. On any other handle the file is created as it would be
    /// otherwise and given the mode afterwards.
    /// </para>
    /// <para>
    /// <strong>What is not carried.</strong> Ownership: the new file belongs to the account
    /// that wrote it, because only a privileged process may give a file away. Access control
    /// lists, extended attributes and times. And other hard links: they name the old file,
    /// which keeps the old contents, so after the write they no longer share contents with
    /// the name that was published.
    /// </para>
    /// <para>
    /// A symbolic link holding the name is replaced as a link, so the mode of what it points
    /// at is not carried either. A refusal to set the permissions fails the write and leaves
    /// the name as it was.
    /// </para>
    /// <para>
    /// <strong>A read-only file on Windows</strong> cannot be replaced by moving another file
    /// onto it, with or without this setting, so a publish over one fails with
    /// <see cref="System.UnauthorizedAccessException"/> or <see cref="Cap.Std.CapIOException"/>
    /// and leaves it as it was. Clear the flag first to replace it. For that reason the
    /// read-only flag is the one attribute not carried: it could only stop the scratch file
    /// being removed after the refusal. Unix has no such rule: a file whose mode forbids
    /// writing is replaced, and with this setting the new one forbids writing too.
    /// </para>
    /// </remarks>
    public bool PreservePermissions { get; init; } = true;
}
