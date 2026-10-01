using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Tests;

/// <summary>
/// How a Windows object's identity is read, including on a filesystem that declines the
/// 128-bit identity query.
/// </summary>
/// <remarks>
/// This runs on every platform because the decision takes the filesystem's replies as
/// arguments. A filesystem that declines the 128-bit query is a third-party or older network
/// one, and no test agent mounts either.
/// </remarks>
public sealed class FileIdentityTests
{
    private static readonly FileIdInformation Wide = new()
    {
        VolumeSerialNumber = 0x1122_3344_5566_7788,
        FileIdLow = 0x0102_0304_0506_0708,
        FileIdHigh = 0x0A0B_0C0D_0E0F_1011,
    };

    private static readonly CapError AccessDenied = NtFailure(
        CapErrorCategory.PermissionDenied, NtStatusCodes.STATUS_ACCESS_DENIED);

    /// <summary>
    /// Where the 128-bit query is answered, its reply is the identity and nothing else is
    /// asked.
    /// </summary>
    [Fact]
    public void The_wide_reply_is_the_identity_where_it_is_answered()
    {
        CapError error = FileIdentity.Query(0, AnswerWide(CapError.Success), UnaskedNarrow, UnaskedVolume, out FileIdInformation id);

        Assert.True(error.IsSuccess, error.FailureDescription);
        Assert.Equal(Wide.VolumeSerialNumber, id.VolumeSerialNumber);
        Assert.Equal(Wide.FileIdLow, id.FileIdLow);
        Assert.Equal(Wide.FileIdHigh, id.FileIdHigh);
    }

    /// <summary>
    /// A filesystem that declines the 128-bit query, whichever declining status it uses, is
    /// identified by the 64-bit identifier and the 32-bit volume serial, with the high half
    /// zero as the 64-bit directory read reports it.
    /// </summary>
    [Theory]
    [InlineData(NtStatusCodes.STATUS_INVALID_INFO_CLASS)]
    [InlineData(NtStatusCodes.STATUS_NOT_SUPPORTED)]
    [InlineData(NtStatusCodes.STATUS_INVALID_PARAMETER)]
    [InlineData(NtStatusCodes.STATUS_NOT_IMPLEMENTED)]
    public void A_declined_wide_query_falls_back_to_the_narrow_identifier_and_the_volume_serial(int status)
    {
        CapError error = FileIdentity.Query(
            0,
            AnswerWide(NtFailure(NtStatusCodes.Classify(status), status)),
            AnswerNarrow(CapError.Success, unchecked((long)0xF000_0000_0000_002A)),
            AnswerVolume(CapError.Success, 0xDEAD_BEEF),
            out FileIdInformation id);

        Assert.True(error.IsSuccess, error.FailureDescription);
        Assert.Equal(0xDEAD_BEEFUL, id.VolumeSerialNumber);
        Assert.Equal(0xF000_0000_0000_002AUL, id.FileIdLow);
        Assert.Equal(0UL, id.FileIdHigh);
    }

    /// <summary>
    /// Any other failure of the 128-bit query fails the description rather than being
    /// answered some other way.
    /// </summary>
    [Fact]
    public void A_failed_wide_query_is_not_a_declined_one()
    {
        CapError error = FileIdentity.Query(0, AnswerWide(AccessDenied), UnaskedNarrow, UnaskedVolume, out FileIdInformation id);

        Assert.Equal(CapErrorCategory.PermissionDenied, error.Category);
        Assert.Equal(default, id);
    }

    /// <summary>
    /// A declining status this layer classified as unknown is a malformed reply, not a
    /// refusal, and is not fallen back from.
    /// </summary>
    [Fact]
    public void A_declining_status_classified_as_unknown_is_not_fallen_back_from()
    {
        CapError malformed = NtFailure(CapErrorCategory.Unknown, NtStatusCodes.STATUS_INVALID_PARAMETER);

        CapError error = FileIdentity.Query(0, AnswerWide(malformed), UnaskedNarrow, UnaskedVolume, out _);

        Assert.Equal(CapErrorCategory.Unknown, error.Category);
    }

    /// <summary>
    /// On the fallback, a failure of either older question fails the description, and no
    /// identity is reported with half of it invented.
    /// </summary>
    [Fact]
    public void A_failed_fallback_question_fails_the_description()
    {
        CapError declined = NtFailure(CapErrorCategory.NotSupported, NtStatusCodes.STATUS_INVALID_INFO_CLASS);

        CapError narrowFailed = FileIdentity.Query(
            0, AnswerWide(declined), AnswerNarrow(AccessDenied), UnaskedVolume, out FileIdInformation first);
        CapError volumeFailed = FileIdentity.Query(
            0, AnswerWide(declined), AnswerNarrow(CapError.Success, 42), AnswerVolume(AccessDenied), out FileIdInformation second);

        Assert.Equal(CapErrorCategory.PermissionDenied, narrowFailed.Category);
        Assert.Equal(default, first);
        Assert.Equal(CapErrorCategory.PermissionDenied, volumeFailed.Category);
        Assert.Equal(default, second);
    }

    private static CapError NtFailure(CapErrorCategory category, int status) =>
        CapError.Create(category, CapErrorSource.NtStatus, status);

    private static FileIdentity.WideQuery AnswerWide(CapError error) =>
        (nint _, out FileIdInformation reply) =>
        {
            reply = error.IsSuccess ? Wide : default;
            return error;
        };

    private static FileIdentity.NarrowQuery AnswerNarrow(CapError error, long index = 0) =>
        (nint _, out long reply) =>
        {
            reply = error.IsSuccess ? index : 0;
            return error;
        };

    private static FileIdentity.VolumeQuery AnswerVolume(CapError error, uint serial = 0) =>
        (nint _, out uint reply) =>
        {
            reply = error.IsSuccess ? serial : 0;
            return error;
        };

    private static CapError UnaskedNarrow(nint handle, out long index) =>
        throw new InvalidOperationException("The fallback was asked where the wide query had answered.");

    private static CapError UnaskedVolume(nint handle, out uint serial) =>
        throw new InvalidOperationException("The fallback was asked where the wide query had answered.");
}
