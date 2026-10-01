using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Tests;

/// <summary>
/// How a Windows object's times, length, attributes, reparse tag and link count are read,
/// including on a filesystem that declines the query carrying them all together.
/// </summary>
/// <remarks>
/// This runs on every platform because the decision takes the filesystem's replies as
/// arguments. The combined query is declined before Windows 10 1709 and by filesystems that
/// do not implement it, and no test agent runs the one or mounts the other.
/// </remarks>
public sealed class FileStatTests
{
    private const uint Plain = (uint)FileAttributes.Archive;

    private const uint Reparse = Plain | (uint)FileAttributes.ReparsePoint;

    private static readonly FileStatInformation Combined = new()
    {
        FileId = 7,
        CreationTime = 1,
        LastAccessTime = 2,
        LastWriteTime = 3,
        ChangeTime = 4,
        AllocationSize = 4096,
        EndOfFile = 5,
        FileAttributes = Reparse,
        ReparseTag = ReparseTags.SymbolicLink,
        NumberOfLinks = 3,
        EffectiveAccess = 0x1F01FF,
    };

    private static readonly CapError AccessDenied = NtFailure(
        CapErrorCategory.PermissionDenied, NtStatusCodes.STATUS_ACCESS_DENIED);

    private static readonly CapError Declined = NtFailure(
        CapErrorCategory.NotSupported, NtStatusCodes.STATUS_INVALID_INFO_CLASS);

    /// <summary>
    /// Where the combined query is answered, its reply is the description and nothing else is
    /// asked.
    /// </summary>
    [Fact]
    public void The_combined_reply_is_the_description_where_it_is_answered()
    {
        CapError error = FileStat.Query(
            0, AnswerStat(CapError.Success), UnaskedNetworkOpen, UnaskedStandard, UnaskedTag, out FileStatInformation stat);

        Assert.True(error.IsSuccess, error.FailureDescription);
        Assert.Equal(Combined, stat);
    }

    /// <summary>
    /// A filesystem that declines the combined query, whichever declining status it uses, is
    /// described from the times-and-length reply and the link-count reply, and is not asked for
    /// a tag it has no reparse point to carry.
    /// </summary>
    [Theory]
    [InlineData(NtStatusCodes.STATUS_INVALID_INFO_CLASS)]
    [InlineData(NtStatusCodes.STATUS_NOT_SUPPORTED)]
    [InlineData(NtStatusCodes.STATUS_INVALID_PARAMETER)]
    [InlineData(NtStatusCodes.STATUS_NOT_IMPLEMENTED)]
    public void A_declined_combined_query_falls_back_to_the_older_questions(int status)
    {
        CapError error = FileStat.Query(
            0,
            AnswerStat(NtFailure(NtStatusCodes.Classify(status), status)),
            AnswerNetworkOpen(CapError.Success, Plain),
            AnswerStandard(CapError.Success, links: 2),
            UnaskedTag,
            out FileStatInformation stat);

        Assert.True(error.IsSuccess, error.FailureDescription);
        Assert.Equal(10, stat.CreationTime);
        Assert.Equal(20, stat.LastAccessTime);
        Assert.Equal(30, stat.LastWriteTime);
        Assert.Equal(40, stat.ChangeTime);
        Assert.Equal(8192, stat.AllocationSize);
        Assert.Equal(50, stat.EndOfFile);
        Assert.Equal(Plain, stat.FileAttributes);
        Assert.Equal(0u, stat.ReparseTag);
        Assert.Equal(2u, stat.NumberOfLinks);
        Assert.Equal(0, stat.FileId);
        Assert.Equal(0u, stat.EffectiveAccess);
    }

    /// <summary>
    /// On the fallback, an object whose attributes say it has a reparse point is asked for its
    /// tag, and the tag is carried.
    /// </summary>
    [Fact]
    public void The_fallback_asks_for_the_tag_of_a_reparse_point()
    {
        CapError error = FileStat.Query(
            0,
            AnswerStat(Declined),
            AnswerNetworkOpen(CapError.Success, Reparse),
            AnswerStandard(CapError.Success, links: 1),
            AnswerTag(CapError.Success, Reparse, ReparseTags.MountPoint),
            out FileStatInformation stat);

        Assert.True(error.IsSuccess, error.FailureDescription);
        Assert.Equal(Reparse, stat.FileAttributes);
        Assert.Equal(ReparseTags.MountPoint, stat.ReparseTag);
    }

    /// <summary>
    /// A reparse point removed between the attributes and the tag leaves no tag to report,
    /// rather than whatever the tag reply carried.
    /// </summary>
    [Fact]
    public void A_reparse_point_gone_by_the_tag_reply_leaves_no_tag()
    {
        CapError error = FileStat.Query(
            0,
            AnswerStat(Declined),
            AnswerNetworkOpen(CapError.Success, Reparse),
            AnswerStandard(CapError.Success, links: 1),
            AnswerTag(CapError.Success, Plain, ReparseTags.SymbolicLink),
            out FileStatInformation stat);

        Assert.True(error.IsSuccess, error.FailureDescription);
        Assert.Equal(0u, stat.ReparseTag);
    }

    /// <summary>
    /// Any other failure of the combined query fails the description rather than being
    /// answered some other way.
    /// </summary>
    [Fact]
    public void A_failed_combined_query_is_not_a_declined_one()
    {
        CapError error = FileStat.Query(
            0, AnswerStat(AccessDenied), UnaskedNetworkOpen, UnaskedStandard, UnaskedTag, out FileStatInformation stat);

        Assert.Equal(CapErrorCategory.PermissionDenied, error.Category);
        Assert.Equal(default, stat);
    }

    /// <summary>
    /// A declining status this layer classified as unknown is a malformed reply, not a
    /// refusal, and is not fallen back from.
    /// </summary>
    [Fact]
    public void A_declining_status_classified_as_unknown_is_not_fallen_back_from()
    {
        CapError malformed = NtFailure(CapErrorCategory.Unknown, NtStatusCodes.STATUS_INVALID_PARAMETER);

        CapError error = FileStat.Query(
            0, AnswerStat(malformed), UnaskedNetworkOpen, UnaskedStandard, UnaskedTag, out _);

        Assert.Equal(CapErrorCategory.Unknown, error.Category);
    }

    /// <summary>
    /// On the fallback, a failure of any older question fails the description, and nothing is
    /// reported with part of it missing.
    /// </summary>
    [Fact]
    public void A_failed_fallback_question_fails_the_description()
    {
        CapError basicFailed = FileStat.Query(
            0, AnswerStat(Declined), AnswerNetworkOpen(AccessDenied), UnaskedStandard, UnaskedTag, out FileStatInformation first);
        CapError linksFailed = FileStat.Query(
            0, AnswerStat(Declined), AnswerNetworkOpen(CapError.Success, Plain), AnswerStandard(AccessDenied), UnaskedTag, out FileStatInformation second);
        CapError tagFailed = FileStat.Query(
            0,
            AnswerStat(Declined),
            AnswerNetworkOpen(CapError.Success, Reparse),
            AnswerStandard(CapError.Success, links: 1),
            AnswerTag(AccessDenied),
            out FileStatInformation third);

        Assert.Equal(CapErrorCategory.PermissionDenied, basicFailed.Category);
        Assert.Equal(default, first);
        Assert.Equal(CapErrorCategory.PermissionDenied, linksFailed.Category);
        Assert.Equal(default, second);
        Assert.Equal(CapErrorCategory.PermissionDenied, tagFailed.Category);
        Assert.Equal(default, third);
    }

    private static CapError NtFailure(CapErrorCategory category, int status) =>
        CapError.Create(category, CapErrorSource.NtStatus, status);

    private static FileStat.StatQuery AnswerStat(CapError error) =>
        (nint _, out FileStatInformation reply) =>
        {
            reply = error.IsSuccess ? Combined : default;
            return error;
        };

    private static FileStat.NetworkOpenQuery AnswerNetworkOpen(CapError error, uint attributes = 0) =>
        (nint _, out FileNetworkOpenInformation reply) =>
        {
            reply = error.IsSuccess
                ? new FileNetworkOpenInformation
                {
                    CreationTime = 10,
                    LastAccessTime = 20,
                    LastWriteTime = 30,
                    ChangeTime = 40,
                    AllocationSize = 8192,
                    EndOfFile = 50,
                    FileAttributes = attributes,
                }
                : default;
            return error;
        };

    private static FileStat.StandardQuery AnswerStandard(CapError error, uint links = 0) =>
        (nint _, out FileStandardInformation reply) =>
        {
            reply = error.IsSuccess ? new FileStandardInformation { NumberOfLinks = links } : default;
            return error;
        };

    private static FileStat.TagQuery AnswerTag(CapError error, uint attributes = 0, uint tag = 0) =>
        (nint _, out FileAttributeTagInformation reply) =>
        {
            reply = error.IsSuccess
                ? new FileAttributeTagInformation { FileAttributes = attributes, ReparseTag = tag }
                : default;
            return error;
        };

    private static CapError UnaskedNetworkOpen(nint handle, out FileNetworkOpenInformation info) =>
        throw new InvalidOperationException("The fallback was asked where the combined query had answered.");

    private static CapError UnaskedStandard(nint handle, out FileStandardInformation info) =>
        throw new InvalidOperationException("The fallback was asked where the combined query had answered.");

    private static CapError UnaskedTag(nint handle, out FileAttributeTagInformation info) =>
        throw new InvalidOperationException("The tag was asked of an object with no reparse point.");
}
