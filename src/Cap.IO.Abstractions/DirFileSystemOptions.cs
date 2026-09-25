namespace Cap.IO.Abstractions;

/// <summary>How a <see cref="DirFileSystem"/> presents its namespace.</summary>
public sealed class DirFileSystemOptions
{
    /// <summary>The defaults: the root is spelled <c>/</c> on every platform.</summary>
    public static DirFileSystemOptions Default { get; } = new();

    /// <summary>
    /// Windows only: a drive letter to spell the root as, such as <c>'C'</c> for <c>C:\</c>;
    /// or null, the default, for <c>/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For code that looks for a drive letter in a full name and misbehaves without one. The
    /// root is still the directory handle, and no drive is reached: <c>C:\data</c> names
    /// <c>data</c> beneath the handle, and a path on any other drive, or a UNC path, is
    /// refused as the absolute path it is.
    /// </para>
    /// <para>
    /// The cost is that full names then look like real Windows paths. One passed on to
    /// <c>System.IO</c> by mistake opens a file on the host rather than failing, which is why
    /// <c>/</c> is the default.
    /// </para>
    /// </remarks>
    public char? VirtualDrive { get; init; }
}
