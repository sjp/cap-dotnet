namespace Cap.Fs.Ext;

/// <summary>
/// What a copy does when it meets something that is not an ordinary file or directory.
/// </summary>
/// <remarks>
/// A tree can hold objects a copy has no honest equivalent for. A symbolic link is a stored
/// piece of text that means something different depending on where it sits; a named pipe, a
/// socket and a device node are not storage at all but connections to something else on the
/// machine, and reading one either blocks forever or produces bytes that have nothing to do
/// with the tree. The one thing a copy must not do with any of them is produce a different
/// kind of object under the same name and say nothing, which is what a copy that follows
/// links or reads devices does.
/// </remarks>
public enum CopyAction
{
    /// <summary>
    /// The copy stops and reports what it found.
    /// </summary>
    /// <remarks>
    /// The default for everything. A caller who has not thought about what their tree
    /// contains is told rather than handed a result that differs from the source in a way
    /// they did not ask for. Whatever had been copied before the refusal stays copied.
    /// </remarks>
    Fail,

    /// <summary>
    /// The entry is left out, and counted in the report.
    /// </summary>
    /// <remarks>
    /// For a caller who wants the files and knows the rest is not theirs to reproduce. The
    /// count is in the result rather than in a log, so code that cares whether anything was
    /// left behind can ask.
    /// </remarks>
    Skip,

    /// <summary>
    /// The object is made again in the destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Meaningful for symbolic links alone, where it means the link's stored target text is
    /// copied and a new link is created with it. The text is copied exactly: it is not
    /// resolved, not rewritten and not checked, because what it means is decided where it
    /// ends up, and a relative target that pointed inside the source may point somewhere else
    /// from the destination. Nothing the link pointed at is read or followed.
    /// </para>
    /// <para>
    /// Refused for the other kinds. There is no way here to create a named pipe, a socket or
    /// a device node, and there should not be: each of them is a connection to something on
    /// the machine rather than a piece of a tree, and a library that granted the authority to
    /// make one would be granting far more than the authority to copy files.
    /// </para>
    /// </remarks>
    Recreate,
}
