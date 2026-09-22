using System.Globalization;

namespace Cap.Std;

/// <summary>
/// What the filesystem calls an object, as opposed to what a directory calls it.
/// </summary>
/// <remarks>
/// <para>
/// A name is a thing a directory holds; this is a thing the filesystem holds. The difference
/// is the whole reason the type exists. Two names can lead to one object — that is what a
/// hard link is — and one name can lead to different objects at different moments, which is
/// what every attack this library defends against relies on. Comparing names answers neither
/// question. Comparing these answers the first one exactly.
/// </para>
/// <para>
/// <strong>It identifies; it does not authenticate.</strong> A filesystem is free to reuse
/// an identifier once the object it belonged to is gone, so a value kept across a deletion
/// may come to describe an unrelated object. That makes it right for questions asked about
/// one moment — "have I already copied this file under another name?" — and wrong as
/// something to store, to publish, or to treat as a token that grants anything. Authority
/// here is carried by handles, and nothing in this library accepts one of these in place of
/// one.
/// </para>
/// <para>
/// Comparable and hashable on purpose, so that a walk over a subtree can keep a set of the
/// objects it has already met. Detecting that a tree contains the same file under several
/// names needs a set rather than a chain of pairwise comparisons, and a type that could only
/// be compared in pairs would force every caller doing that to invent this one.
/// </para>
/// </remarks>
public readonly struct CapFileId : IEquatable<CapFileId>
{
    internal CapFileId(ulong volumeId, UInt128 nodeId)
    {
        VolumeId = volumeId;
        NodeId = nodeId;
    }

    /// <summary>
    /// The filesystem the object lives on: a Unix device number, or a Windows volume serial
    /// number.
    /// </summary>
    /// <remarks>
    /// Carried because an object's identity is only unique within its filesystem. Two
    /// objects on different filesystems routinely share a node identifier, and comparing
    /// those alone would report them as the same file — which, for a caller deciding whether
    /// it has already copied something, means silently dropping a file.
    /// </remarks>
    public ulong VolumeId { get; }

    /// <summary>
    /// The object's identity within its filesystem: a Unix inode number, or a Windows file
    /// identifier.
    /// </summary>
    /// <remarks>
    /// Wide enough for the widest platform rather than for the commonest. Windows issues
    /// 128-bit identifiers because the 64-bit ones it used to issue are not unique on every
    /// filesystem it supports, and a narrower value here would make two distinct files on
    /// such a filesystem compare equal.
    /// </remarks>
    public UInt128 NodeId { get; }

    /// <summary>Whether two identifiers name the same object.</summary>
    public static bool operator ==(CapFileId left, CapFileId right) => left.Equals(right);

    /// <summary>Whether two identifiers name different objects.</summary>
    public static bool operator !=(CapFileId left, CapFileId right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(CapFileId other) => VolumeId == other.VolumeId && NodeId == other.NodeId;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CapFileId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(VolumeId, NodeId);

    /// <summary>
    /// The identifier as text, for a log line.
    /// </summary>
    /// <remarks>
    /// Both halves in hexadecimal, separated, because the two are different numbers from
    /// different sources and a single run of digits would invite reading them as one. Not a
    /// format anything here parses back.
    /// </remarks>
    public override string ToString() =>
        VolumeId.ToString("x", CultureInfo.InvariantCulture) + ":" +
        NodeId.ToString("x", CultureInfo.InvariantCulture);
}
