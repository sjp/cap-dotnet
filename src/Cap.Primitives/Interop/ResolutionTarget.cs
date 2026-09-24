namespace Cap.Primitives.Interop;

/// <summary>
/// What a resolution is for, which is the only thing that differs between operations.
/// </summary>
/// <remarks>
/// <para>
/// Every operation that names something beneath a directory handle walks the same
/// components in the same way; they disagree only about the last one. Opening wants it
/// opened, removing wants it left alone so that it can be unlinked by name, and creating
/// wants it not to exist at all. Writing a walk per operation is how those three grow
/// different opinions about what <c>..</c> means, which is the class of divergence that has
/// produced escapes in comparable libraries — so the walk is written once and this says
/// which tail to take.
/// </para>
/// <para>
/// There is deliberately no value for "follow the last component or not". Following is a
/// property of the operation, not a separate axis: <see cref="Parent"/> never follows,
/// because an operation that acts on a name must act on the name it was given, and the
/// other two follow within whatever the resolution options permit, because an open of a
/// link is an open of its target. The exceptions belong to the open's request rather than
/// to the walk: an open that may create or empty a file refuses a link at the last
/// component, and so does one whose caller asked it not to follow one (see
/// <see cref="FileOpenRequest.FollowsFinalLink"/>). An operation on a name asked to follow a
/// final link reads the link and resolves its target as a new <see cref="Parent"/>
/// resolution, so the one call that touches the name still never follows.
/// </para>
/// </remarks>
internal enum ResolutionTarget
{
    /// <summary>
    /// Open the last component as a directory. A link there is followed, subject to the
    /// resolution options.
    /// </summary>
    Directory,

    /// <summary>
    /// Open the last component as a file. A link there is followed, subject to the
    /// resolution options, unless the open may create or empty the file, in which case it
    /// is refused.
    /// </summary>
    File,

    /// <summary>
    /// Stop one component short and hand back the directory the last component would have
    /// been looked up in, together with that name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For everything that acts on a name rather than on an object: creating exclusively,
    /// removing, renaming, reading a link, and asking what a name refers to without
    /// following it. Those cannot be expressed as an open — an exclusive create must fail
    /// if the name exists, and a link must be read rather than traversed — so they perform
    /// their own single call against the directory this produces.
    /// </para>
    /// <para>
    /// The last component is not looked at at all. It need not exist, and if it is a link it
    /// stays one. Whether it is allowed to be a file, a directory or absent is the
    /// operation's own rule to apply, including the requirement a trailing separator
    /// carries.
    /// </para>
    /// </remarks>
    Parent,
}
