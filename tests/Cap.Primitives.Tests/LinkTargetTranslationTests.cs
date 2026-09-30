using Cap.Primitives.Interop;

namespace Cap.Primitives.Tests;

/// <summary>
/// The one mapping from "a link's stored text is unusable" to a resolution failure, shared by
/// the walk, by <c>Dir</c>'s follow-on-request and by the in-memory confined resolver.
/// </summary>
/// <remarks>
/// The table names every <see cref="CapPathError"/>, and
/// <see cref="Every_parse_refusal_has_a_resolution_reading"/> fails when a member is missing
/// from it, so adding a refusal reason without deciding how a link holding one is reported
/// fails a test rather than quietly falling to the default arm. What each mapping looks like
/// through the walk is asserted in <see cref="PortableWalkTests"/>.
/// </remarks>
public sealed class LinkTargetTranslationTests
{
    /// <summary>
    /// Every parse refusal is read as the resolution failure a caller's own path of that shape
    /// would get.
    /// </summary>
    /// <remarks>
    /// <see cref="CapPathError.None"/> and <see cref="CapPathError.ParentLink"/> never reach the
    /// method — it is only called on a refusal, and link targets are parsed with
    /// <see cref="ParentLinkPolicy.Preserve"/> — so both are pinned to the default arm to show
    /// they were considered, not to say that reading is meaningful.
    /// </remarks>
    [Fact]
    public void Every_parse_refusal_has_a_resolution_reading()
    {
        CapPathError[] missing = [.. Enum.GetValues<CapPathError>().Except(Expected.Keys)];
        Assert.True(missing.Length == 0, $"No expected reading for: {string.Join(", ", missing)}");

        foreach ((CapPathError error, CapErrorCategory expected) in Expected)
        {
            Assert.True(
                expected == PortableResolver.TranslateLinkTarget(error).Category,
                $"{error}: expected {expected}, got {PortableResolver.TranslateLinkTarget(error).Category}.");
        }
    }

    private static readonly Dictionary<CapPathError, CapErrorCategory> Expected = new()
    {
        [CapPathError.None] = CapErrorCategory.InvalidArgument,
        [CapPathError.Empty] = CapErrorCategory.NotFound,
        [CapPathError.Absolute] = CapErrorCategory.Escaped,
        [CapPathError.RootRelative] = CapErrorCategory.Escaped,
        [CapPathError.DriveRelative] = CapErrorCategory.Escaped,
        [CapPathError.Unc] = CapErrorCategory.Escaped,
        [CapPathError.DeviceNamespace] = CapErrorCategory.Escaped,
        [CapPathError.ReservedName] = CapErrorCategory.Escaped,
        [CapPathError.InvalidCharacter] = CapErrorCategory.InvalidArgument,
        [CapPathError.TrailingDotOrSpace] = CapErrorCategory.InvalidArgument,
        [CapPathError.ParentLink] = CapErrorCategory.InvalidArgument,
        [CapPathError.TooLong] = CapErrorCategory.NameTooLong,
    };
}
