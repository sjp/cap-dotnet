namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// Reads the identity of an open object: the volume it is on and its identifier within it.
/// </summary>
/// <remarks>
/// <para>
/// The identity is asked for in its 128-bit form, which carries the volume serial with it. That
/// query is recent, and a filesystem that does not offer the 128-bit directory read usually
/// does not offer it either, so a third-party or older network filesystem whose entries can be
/// listed could otherwise not have one of them described — and since resolution describes
/// every component, not have a directory opened on it at all.
/// </para>
/// <para>
/// Where the query is declined, the identity is assembled from the two older questions every
/// filesystem answers: the 64-bit identifier, and the 32-bit serial of the volume. The
/// identifier is the one the 64-bit directory read reports for the same entry, so an entry and
/// the object it names still compare equal. The high half is zero, as the directory read's is.
/// Nothing is remembered from one handle to the next, since the next may be on another volume.
/// </para>
/// <para>
/// A failure of either older question fails the description. An identity with an invented
/// volume would let two objects on different volumes compare as one, and would hide a mount
/// crossing from the check that looks for one.
/// </para>
/// <para>
/// The three questions are passed in rather than asked here so the fallback can be exercised
/// on every build agent, with replies no local volume would give.
/// </para>
/// </remarks>
internal static class FileIdentity
{
    /// <summary>Asks an open object for its volume serial and 128-bit identifier.</summary>
    internal delegate CapError WideQuery(nint handle, out FileIdInformation id);

    /// <summary>Asks an open object for its 64-bit identifier.</summary>
    internal delegate CapError NarrowQuery(nint handle, out long index);

    /// <summary>Asks the volume an open object is on for its 32-bit serial.</summary>
    internal delegate CapError VolumeQuery(nint handle, out uint serial);

    /// <summary>Reads the identity of <paramref name="handle"/>.</summary>
    /// <param name="handle">The object to identify.</param>
    /// <param name="wide">Answers the volume serial and the 128-bit identifier together.</param>
    /// <param name="narrow">Answers the 64-bit identifier, where <paramref name="wide"/> is
    /// declined.</param>
    /// <param name="volume">Answers the volume serial, where <paramref name="wide"/> is
    /// declined.</param>
    /// <param name="result">The identity.</param>
    internal static CapError Query(
        nint handle, WideQuery wide, NarrowQuery narrow, VolumeQuery volume, out FileIdInformation result)
    {
        CapError error = wide(handle, out result);
        if (error.IsSuccess || !NtStatusCodes.IsDeclined(error))
        {
            return error;
        }

        result = default;

        error = narrow(handle, out long index);
        if (error.IsFailure)
        {
            return error;
        }

        error = volume(handle, out uint serial);
        if (error.IsFailure)
        {
            return error;
        }

        result = new FileIdInformation
        {
            VolumeSerialNumber = serial,
            FileIdLow = unchecked((ulong)index),
            FileIdHigh = 0,
        };
        return CapError.Success;
    }
}
