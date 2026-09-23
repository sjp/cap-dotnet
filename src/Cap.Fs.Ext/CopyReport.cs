namespace Cap.Fs.Ext;

/// <summary>
/// What a recursive copy did.
/// </summary>
/// <remarks>
/// <para>
/// Returned rather than logged, because the number that matters most —
/// <see cref="Skipped"/> — is one a caller has to be able to act on. A copy configured to
/// leave things out and a copy that found nothing to leave out are the same operation with
/// different results, and code that cares about the difference should not have to read a log
/// to find it.
/// </para>
/// <para>
/// <strong>Immutable.</strong> The counts are fixed when the copy finishes, so a report can be
/// read from any thread.
/// </para>
/// </remarks>
public readonly struct CopyReport
{
    internal CopyReport(int directories, int files, int symlinks, int skipped, long bytes)
    {
        Directories = directories;
        Files = files;
        Symlinks = symlinks;
        Skipped = skipped;
        Bytes = bytes;
    }

    /// <summary>How many directories were created in the destination.</summary>
    /// <remarks>
    /// With <see cref="CopyOptions.Overwrite"/> on, a directory that was already there and was
    /// copied into is counted as well, since the copy cannot tell it from one it made.
    /// </remarks>
    public int Directories { get; }

    /// <summary>How many files were copied.</summary>
    public int Files { get; }

    /// <summary>How many symbolic links were made again in the destination.</summary>
    /// <remarks>
    /// Non-zero only under <see cref="CopyAction.Recreate"/>. A link that was skipped is counted
    /// in <see cref="Skipped"/> instead, and nothing a link pointed at is counted anywhere,
    /// because nothing it pointed at was copied.
    /// </remarks>
    public int Symlinks { get; }

    /// <summary>
    /// How many entries were left out because their kind was one the copy was told to skip.
    /// </summary>
    public int Skipped { get; }

    /// <summary>How many bytes of file contents were written.</summary>
    /// <remarks>
    /// What was written, not what the source occupied. A file with holes in it is read as the
    /// zeroes it reports and written as those zeroes, so a sparse source becomes a destination
    /// that is larger on disk than the source was.
    /// </remarks>
    public long Bytes { get; }
}
