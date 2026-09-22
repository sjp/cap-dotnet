namespace Cap.Fs.Ext;

/// <summary>
/// How far a write is pushed before it is treated as done.
/// </summary>
/// <remarks>
/// <para>
/// Three different promises, and the difference between them is invisible until the power
/// fails. A write that has returned has been accepted by the operating system, which is
/// enough for every other program on the machine to see it and not enough for it to survive
/// the machine stopping. Which of those a caller needs is not something this library can
/// work out, because the cost varies by two orders of magnitude between devices and the
/// consequence of losing the write varies by rather more.
/// </para>
/// <para>
/// None of these changes what a reader can observe while the machine is running. Publishing
/// a file by moving it onto its name is atomic under all three: there is no instant in which
/// the name holds a half-written file, whatever is or is not committed to the disk.
/// </para>
/// </remarks>
public enum Durability
{
    /// <summary>
    /// Nothing is committed: the write is handed to the operating system and the name is
    /// moved into place.
    /// </summary>
    /// <remarks>
    /// The right choice for content that can be rebuilt — a cache, a rendered artefact, an
    /// extracted archive. After a crash the name may hold the old contents, the new ones, or,
    /// on a filesystem that does not order its own metadata, nothing useful at all.
    /// </remarks>
    None,

    /// <summary>
    /// The file's contents are committed before the name is moved into place, but the
    /// directory holding the name is not.
    /// </summary>
    /// <remarks>
    /// Rules out the worst outcome — a name that resolves to a file whose contents never
    /// reached the disk — at the cost of one commit per write. What it does not promise is
    /// that the name itself survives: a crash immediately after the move may leave the
    /// directory as it was, with the old file still under the name and the new one under a
    /// scratch name nobody will look for.
    /// </remarks>
    File,

    /// <summary>
    /// The file's contents are committed, the name is moved into place, and the directory
    /// holding the name is committed too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only setting under which "the file is there" survives the power going out, and the
    /// default for that reason. It costs two commits per write, which on a spinning disk or a
    /// conservative flash controller is the dominant cost of writing a small file.
    /// </para>
    /// <para>
    /// <strong>Windows cannot commit a directory</strong> for a process that does not hold
    /// volume-level privilege, and asking here does not fail there — it does what
    /// <see cref="File"/> does. Refusing instead would make the safe default unusable on that
    /// platform and push every caller towards the weaker setting everywhere; the honest
    /// alternative is to say plainly, as this does, that the last step is not taken there.
    /// </para>
    /// </remarks>
    FileAndDirectory,
}
