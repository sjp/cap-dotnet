using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// The token that marks where authority enters the process.
/// </summary>
/// <remarks>
/// <para>
/// None of this is enforcement and none of it is meant to be: any code that can call
/// anything can take a token. What is being protected is the property that makes the token
/// worth having at all — that searching for the places it is acquired finds every place the
/// process reaches past what it was handed.
/// </para>
/// <para>
/// A default-constructed value would defeat exactly that. It is the one way to satisfy a
/// parameter of this type without writing the name that the search looks for, so it has to
/// be refused wherever the parameter is required, and refused loudly enough that nobody
/// discovers by accident that it works.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class AmbientAuthorityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-ambient-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A token that was taken opens the directory it is presented for.</summary>
    [Fact]
    public void An_acquired_token_is_accepted()
    {
        using Dir dir = Dir.Open(_root, AmbientAuthority.Acquire());
        Assert.False(dir.UnsafeGetHandle().IsInvalid);
    }

    /// <summary>A value nobody acquired is not a token.</summary>
    [Fact]
    public void A_default_value_is_refused()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => Dir.Open(_root, default));

        Assert.Equal("authority", thrown.ParamName);
    }

    /// <summary>
    /// And it is refused by the form that reports failure rather than throwing, too.
    /// </summary>
    /// <remarks>
    /// The distinction the reporting form draws is between a filesystem that said no and a
    /// caller that wrote something wrong; a missing token is the second. Folding it into the
    /// first would turn the most important precondition in the library into a false return
    /// value that a caller is quite likely to treat as "the directory was not there".
    /// </remarks>
    [Fact]
    public void A_default_value_is_refused_by_the_reporting_form_as_well()
    {
        Assert.Throws<ArgumentException>(() => Dir.TryOpen(_root, default, out _));
    }

    /// <summary>The same applies to asking a handle where it is.</summary>
    [Fact]
    public void A_default_value_is_refused_when_asking_for_a_path()
    {
        using Dir dir = Dir.Open(_root, AmbientAuthority.Acquire());
        Assert.Throws<ArgumentException>(() => dir.TryGetPath(default, out _));
    }

    /// <summary>
    /// A token says where it was taken, which is what makes a log of them worth keeping.
    /// </summary>
    [Fact]
    public void A_token_records_the_site_it_was_acquired_at()
    {
        AmbientAuthority authority = AmbientAuthority.Acquire();

        Assert.Contains(nameof(AmbientAuthorityTests), authority.ToString(), StringComparison.Ordinal);
    }

    /// <summary>And one that was never taken says so rather than describing a site.</summary>
    [Fact]
    public void An_unacquired_value_describes_itself_as_such()
    {
        Assert.Contains("never acquired", default(AmbientAuthority).ToString(), StringComparison.Ordinal);
    }
}
