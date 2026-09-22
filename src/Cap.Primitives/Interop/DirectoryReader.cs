namespace Cap.Primitives.Interop;

/// <summary>
/// A one-pass read of what a directory holds, positioned on one entry at a time.
/// </summary>
/// <remarks>
/// <para>
/// Shaped as a cursor rather than as a sequence because of what a directory read costs. The
/// platforms all answer in batches — one call fills a buffer with as many entries as fit —
/// and a type that handed out a sequence of objects would allocate one per entry for a
/// caller that is about to look at a name and discard it. Here the buffer belongs to the
/// reader, the name is a span into storage the reader reuses, and nothing at all is
/// allocated per entry until something decides to keep one.
/// </para>
/// <para>
/// <strong>It reads names, it does not resolve them.</strong> A name it reports is a
/// component of the directory it was opened on, and acting on it means looking it up again
/// through the ordinary confined path. That is not caution for its own sake: an entry is a
/// snapshot of something a concurrent writer may already have replaced, and a reader that
/// handed back a handle would be handing back the thing that was there when the buffer was
/// filled rather than the thing the name holds now.
/// </para>
/// <para>
/// <strong><c>.</c> and <c>..</c> are never reported.</strong> Some platforms include them
/// and some do not; skipping them here rather than in each backend is what makes the same
/// directory produce the same entries everywhere. They also carry the one authority an
/// enumeration must not confer — a name that climbs out of the subtree — so a backend that
/// grew a way of leaking them would be leaking exactly the wrong one.
/// </para>
/// <para>
/// <strong>An enumeration is not a snapshot</strong> on any platform this runs on, and
/// nothing here pretends otherwise. An entry present for the whole read is reported; an
/// entry created or removed while the read is in progress may or may not be.
/// </para>
/// </remarks>
internal abstract class DirectoryReader : IDisposable
{
    /// <summary>
    /// Room for a name before the buffer has to grow. Above the longest name every
    /// filesystem this runs on will store, so it never does.
    /// </summary>
    private const int InitialNameCapacity = 256;

    private char[] _name = new char[InitialNameCapacity];
    private int _length;

    /// <summary>
    /// The name of the entry the reader is positioned on: one component, never a path.
    /// </summary>
    /// <remarks>
    /// Valid until the next read. It is a view of storage the reader reuses, so anything
    /// that outlives the call has to copy it.
    /// </remarks>
    public ReadOnlySpan<char> CurrentName => _name.AsSpan(0, _length);

    /// <summary>What the entry is.</summary>
    public CapFileType CurrentType { get; private set; }

    /// <summary>
    /// Moves to the next entry.
    /// </summary>
    /// <param name="advanced">
    /// True when the reader is positioned on an entry; false when the directory has been
    /// read to its end.
    /// </param>
    /// <remarks>
    /// A failure leaves the reader where it was and is not retried here. Reading a directory
    /// can fail part of the way through — the filesystem may go away, a network mount may
    /// stop answering — and a caller that has already been handed entries needs to be told
    /// that rather than shown a clean end.
    /// </remarks>
    public CapError Read(out bool advanced)
    {
        while (true)
        {
            CapError error = ReadCore(out advanced);
            if (error.IsFailure || !advanced)
            {
                return error;
            }

            if (IsSelfOrParent(CurrentName))
            {
                continue;
            }

            // Asked for only when the directory read did not carry the kind. Most
            // filesystems supply it and this never runs; the ones that do not would
            // otherwise make every entry's kind unknowable without the caller looking each
            // name up by hand, which is the same lookup made less carefully.
            if (CurrentType == CapFileType.Unknown)
            {
                CurrentType = Classify();
            }

            return CapError.Success;
        }
    }

    /// <summary>Closes whatever the reader holds open.</summary>
    public abstract void Dispose();

    /// <summary>
    /// Moves to the next raw entry, dots and all, refilling the backend's buffer when it
    /// runs out.
    /// </summary>
    protected abstract CapError ReadCore(out bool advanced);

    /// <summary>
    /// Looks up the entry the reader is positioned on, for a platform whose directory read
    /// did not say what kind it is.
    /// </summary>
    /// <remarks>
    /// Never follows the entry if it is a link, and answers <see cref="CapFileType.Unknown"/>
    /// rather than throwing if the lookup fails — an entry that has been removed since the
    /// buffer was filled is an ordinary thing to find, and it must not make the rest of the
    /// directory unreadable.
    /// </remarks>
    protected virtual CapFileType Classify() => CapFileType.Unknown;

    /// <summary>
    /// Records the entry the reader has moved to, decoding a Unix name from the bytes the
    /// kernel stores.
    /// </summary>
    /// <remarks>
    /// Decoded by the scheme that round-trips rather than by a strict UTF-8 decoder. A Unix
    /// filename is a byte string under no obligation to be text, and a decoder that replaced
    /// what it could not read would hand back a name that no longer encodes to the bytes it
    /// came from — so the entry could be listed and never opened, and one such name would
    /// make its whole directory useless.
    /// </remarks>
    protected void SetCurrent(ReadOnlySpan<byte> name, CapFileType type)
    {
        int count = PathEncoding.GetCharCount(name);
        EnsureCapacity(count);

        PathEncoding.TryGetChars(name, _name, out _length);
        CurrentType = type;
    }

    /// <summary>Records the entry the reader has moved to, from a name already in characters.</summary>
    protected void SetCurrent(ReadOnlySpan<char> name, CapFileType type)
    {
        EnsureCapacity(name.Length);

        name.CopyTo(_name);
        _length = name.Length;
        CurrentType = type;
    }

    /// <summary>The names every directory holds for itself and for the one above it.</summary>
    /// <remarks>
    /// Compared as characters rather than as encoded bytes because a name that failed to
    /// decode cannot be either of these: both are plain ASCII, and the escape the decoder
    /// uses cannot produce an ASCII character.
    /// </remarks>
    private static bool IsSelfOrParent(ReadOnlySpan<char> name) =>
        name.Length <= 2 && (name.SequenceEqual(".") || name.SequenceEqual(".."));

    private void EnsureCapacity(int count)
    {
        if (_name.Length < count)
        {
            _name = new char[count];
        }
    }
}
