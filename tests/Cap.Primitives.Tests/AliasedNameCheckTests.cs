using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Tests;

/// <summary>
/// The decision that refuses a Windows object reached through its generated short name.
/// </summary>
/// <remarks>
/// This runs on every platform because the decision takes the filesystem's replies as
/// arguments. The replies that matter most — a filesystem that declines the normalised-name
/// query, or declines both — are ones no volume on a test agent gives, and the on-disk cases in
/// <see cref="WindowsResolutionOnDiskTests"/> only ever see NTFS answering.
/// </remarks>
public sealed class AliasedNameCheckTests
{
    private static readonly CapError InvalidInfoClass = NtFailure(
        CapErrorCategory.NotSupported, NtStatusCodes.STATUS_INVALID_INFO_CLASS);

    private static readonly CapError NoAlias = NtFailure(
        CapErrorCategory.NotFound, NtStatusCodes.STATUS_OBJECT_NAME_NOT_FOUND);

    private static readonly CapError AccessDenied = NtFailure(
        CapErrorCategory.PermissionDenied, NtStatusCodes.STATUS_ACCESS_DENIED);

    /// <summary>
    /// A name without a tilde asks nothing of the filesystem.
    /// </summary>
    /// <remarks>
    /// Every open goes through the check, so the common case must cost nothing.
    /// </remarks>
    [Fact]
    public void A_name_without_a_tilde_asks_nothing()
    {
        CapError error = AliasedNameCheck.Refuse(0, "report.docx", Unasked, Unasked);

        Assert.True(error.IsSuccess, error.FailureDescription);
    }

    /// <summary>
    /// Where the normalised query answers, its last component decides and the alias is never
    /// asked for.
    /// </summary>
    [Theory]
    [InlineData(@"\sandbox\plain~1", "PLAIN~1", false)]
    [InlineData(@"\sandbox\a long directory name", "ALONGD~1", true)]
    public void The_normalised_name_decides_where_it_is_answered(string normalized, string requested, bool refused)
    {
        CapError error = AliasedNameCheck.Refuse(0, requested, Answer(CapError.Success, normalized), Unasked);

        Assert.Equal(refused ? CapErrorCategory.AliasedName : CapErrorCategory.None, error.Category);
    }

    /// <summary>
    /// A filesystem that declines the normalised query is asked for the entry's alias
    /// instead, whichever of the declining statuses it uses.
    /// </summary>
    /// <remarks>
    /// Declining is not a failure to report: it is what redirectors, older Samba servers and
    /// user-mode filesystems answer, and on those every name with a tilde in it — an Office
    /// lock file, an editor backup — would otherwise be unopenable.
    /// </remarks>
    [Theory]
    [InlineData(unchecked((int)0xC0000003))] // STATUS_INVALID_INFO_CLASS
    [InlineData(unchecked((int)0xC00000BB))] // STATUS_NOT_SUPPORTED
    [InlineData(unchecked((int)0xC000000D))] // STATUS_INVALID_PARAMETER
    [InlineData(unchecked((int)0xC0000002))] // STATUS_NOT_IMPLEMENTED
    public void A_declined_normalised_query_falls_back_to_the_alias(int status)
    {
        Assert.True(NtStatusCodes.IsUnsupportedClass(status));

        // Built the way the query builds it from the kernel's reply.
        CapError declined = NtFailure(NtStatusCodes.Classify(status), status);

        Assert.True(AliasedNameCheck.Refuse(
            0, "~$lock.docx", Answer(declined), Answer(CapError.Success, "~LOCK~1.DOC")).IsSuccess);
        Assert.Equal(
            CapErrorCategory.AliasedName,
            AliasedNameCheck.Refuse(0, "~lock~1.doc", Answer(declined), Answer(CapError.Success, "~LOCK~1.DOC")).Category);
    }

    /// <summary>
    /// With the normalised query declined, the alias decides.
    /// </summary>
    /// <remarks>
    /// The last row is the case the alias cannot settle: an entry whose own name is already
    /// eight-plus-three reports that name as its alias too, and is refused along with a real
    /// alias rather than letting a real alias through with it.
    /// </remarks>
    [Theory]
    [InlineData("~$lock.docx", "~LOCK~1.DOC", false)]
    [InlineData("notes.txt~", "NOTEST~1", false)]
    [InlineData("plain~1", "", false)]
    [InlineData("progra~1", "PROGRA~1", true)]
    [InlineData("PROGRA~1", "progra~1", true)]
    [InlineData("plain~1", "PLAIN~1", true)]
    public void The_alias_decides_where_the_normalised_name_is_declined(string requested, string alias, bool refused)
    {
        CapError error = AliasedNameCheck.Refuse(
            0, requested, Answer(InvalidInfoClass), Answer(CapError.Success, alias));

        Assert.Equal(refused ? CapErrorCategory.AliasedName : CapErrorCategory.None, error.Category);
    }

    /// <summary>
    /// An entry with no alias, or on a filesystem that declines to report aliases too, has no
    /// second name to have been reached by.
    /// </summary>
    [Fact]
    public void A_filesystem_with_no_alias_to_report_lets_the_name_through()
    {
        Assert.True(AliasedNameCheck.Refuse(0, "plain~1", Answer(InvalidInfoClass), Answer(NoAlias)).IsSuccess);
        Assert.True(AliasedNameCheck.Refuse(0, "plain~1", Answer(InvalidInfoClass), Answer(InvalidInfoClass)).IsSuccess);
    }

    /// <summary>
    /// Any other failure of either question fails the open rather than standing the check
    /// aside.
    /// </summary>
    /// <remarks>
    /// The last case is a reply this layer found malformed. It carries the invalid-parameter
    /// status a declining driver also uses, but is a failure to answer, and treating it as a
    /// refusal to be asked would pass a name nothing was learned about.
    /// </remarks>
    [Fact]
    public void Any_other_failure_fails_the_open()
    {
        CapError malformed = NtFailure(CapErrorCategory.Unknown, NtStatusCodes.STATUS_INVALID_PARAMETER);

        Assert.Equal(AccessDenied, AliasedNameCheck.Refuse(0, "plain~1", Answer(AccessDenied), Unasked));
        Assert.Equal(
            AccessDenied,
            AliasedNameCheck.Refuse(0, "plain~1", Answer(InvalidInfoClass), Answer(AccessDenied)));
        Assert.Equal(malformed, AliasedNameCheck.Refuse(0, "plain~1", Answer(malformed), Unasked));
        Assert.Equal(
            malformed,
            AliasedNameCheck.Refuse(0, "plain~1", Answer(InvalidInfoClass), Answer(malformed)));
    }

    private static CapError NtFailure(CapErrorCategory category, int status) =>
        CapError.Create(category, CapErrorSource.NtStatus, status);

    private static AliasedNameCheck.NameQuery Answer(CapError error, string name = "") =>
        (nint _, out string reply) =>
        {
            reply = error.IsSuccess ? name : string.Empty;
            return error;
        };

    private static CapError Unasked(nint handle, out string name) =>
        throw new InvalidOperationException("The check asked a question it had no need of.");
}
