namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// Reads what a caller-facing description of an open object needs, other than its identity:
/// the times, the length, the attributes, the reparse tag and the link count.
/// </summary>
/// <remarks>
/// <para>
/// All of it is asked for in one query where the filesystem offers the class that carries it.
/// That class is recent, so where it is declined the same fields are assembled from the older
/// questions: the times, the length and the attributes from one reply, the link count from a
/// second, and the reparse tag from a third, asked only when the attributes say there is one.
/// On a network filesystem each question is a round trip, which is the reason for preferring
/// the one that asks them together.
/// </para>
/// <para>
/// Nothing is remembered from one handle to the next, since the next may be on another volume
/// whose filesystem answers differently. A filesystem that declines the newer class costs one
/// declined query per description over what the older questions alone would.
/// </para>
/// <para>
/// A failure of any older question fails the description, as it would have had those been the
/// only questions asked.
/// </para>
/// <para>
/// The questions are passed in rather than asked here so the fallback can be exercised on
/// every build agent, with replies no local volume would give.
/// </para>
/// </remarks>
internal static class FileStat
{
    /// <summary>Asks an open object for everything at once.</summary>
    internal delegate CapError StatQuery(nint handle, out FileStatInformation stat);

    /// <summary>Asks an open object for its times, length and attributes.</summary>
    internal delegate CapError NetworkOpenQuery(nint handle, out FileNetworkOpenInformation info);

    /// <summary>Asks an open object for its link count.</summary>
    internal delegate CapError StandardQuery(nint handle, out FileStandardInformation info);

    /// <summary>Asks an open object for its attributes and reparse tag.</summary>
    internal delegate CapError TagQuery(nint handle, out FileAttributeTagInformation info);

    /// <summary>Reads the description of <paramref name="handle"/>, less its identity.</summary>
    /// <param name="handle">The object to describe.</param>
    /// <param name="stat">Answers everything together.</param>
    /// <param name="networkOpen">Answers the times, the length and the attributes, where
    /// <paramref name="stat"/> is declined.</param>
    /// <param name="standard">Answers the link count, where <paramref name="stat"/> is
    /// declined.</param>
    /// <param name="tag">Answers the reparse tag, where <paramref name="stat"/> is declined and
    /// the object has a reparse point.</param>
    /// <param name="result">The description. Its identifier and effective access are zero when
    /// it was assembled from the older questions.</param>
    internal static CapError Query(
        nint handle,
        StatQuery stat,
        NetworkOpenQuery networkOpen,
        StandardQuery standard,
        TagQuery tag,
        out FileStatInformation result)
    {
        CapError error = stat(handle, out result);
        if (error.IsSuccess || !NtStatusCodes.IsDeclined(error))
        {
            return error;
        }

        result = default;

        error = networkOpen(handle, out FileNetworkOpenInformation basic);
        if (error.IsFailure)
        {
            return error;
        }

        error = standard(handle, out FileStandardInformation links);
        if (error.IsFailure)
        {
            return error;
        }

        uint reparseTag = 0;
        if ((basic.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            error = tag(handle, out FileAttributeTagInformation tagInfo);
            if (error.IsFailure)
            {
                return error;
            }

            // A reparse point removed between the two replies leaves no tag to report, and
            // the attributes the first reply carried still say there was one; the tag is then
            // left at zero, which stands for nothing.
            if ((tagInfo.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            {
                reparseTag = tagInfo.ReparseTag;
            }
        }

        result = new FileStatInformation
        {
            CreationTime = basic.CreationTime,
            LastAccessTime = basic.LastAccessTime,
            LastWriteTime = basic.LastWriteTime,
            ChangeTime = basic.ChangeTime,
            AllocationSize = basic.AllocationSize,
            EndOfFile = basic.EndOfFile,
            FileAttributes = basic.FileAttributes,
            ReparseTag = reparseTag,
            NumberOfLinks = links.NumberOfLinks,
        };
        return CapError.Success;
    }
}
