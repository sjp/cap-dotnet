using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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

    /// <summary>
    /// Nothing in the public surface produces a handle out of nothing without the token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule the whole arrangement rests on, stated over the assembly rather than over
    /// the one type that happens to break it today. A member that can hand back authority
    /// either derives it from authority it was given — a handle in its parameters — or takes
    /// it from outside, in which case it says so and appears in the list of places that do.
    /// </para>
    /// <para>
    /// Instance members are not the subject: a method on a handle is already speaking for
    /// something the caller holds, and everything it produces is bounded by that. It is the
    /// static members and the constructors, the ones reachable by code holding nothing at
    /// all, that would be a way in.
    /// </para>
    /// </remarks>
    [Fact]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject of the test is the whole exported surface, including members " +
                        "nobody has written yet, which is exactly what cannot be named statically. " +
                        "The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "As above: the types being reflected over are whatever the assembly exports.")]
    public void No_exported_member_produces_a_handle_from_nothing()
    {
        Type[] authorities = [typeof(Dir), typeof(CapFile)];
        List<string> examined = [];

        foreach (Type exported in typeof(Dir).Assembly.GetExportedTypes())
        {
            IEnumerable<MethodBase> reachableWithoutAHandle =
            [
                .. exported.GetConstructors(BindingFlags.Public | BindingFlags.Instance),
                .. exported.GetMethods(BindingFlags.Public | BindingFlags.Static),
            ];

            foreach (MethodBase member in reachableWithoutAHandle)
            {
                if (!Produces(member, authorities))
                {
                    continue;
                }

                examined.Add($"{exported.Name}.{member.Name}");

                ParameterInfo[] parameters = member.GetParameters();
                Assert.True(
                    parameters.Any(parameter => parameter.ParameterType == typeof(AmbientAuthority)) ||
                    parameters.Any(parameter => authorities.Contains(parameter.ParameterType)),
                    $"{exported.Name}.{member.Name} hands back authority without either taking a token " +
                    "or being given authority to derive it from.");
            }
        }

        // A scan that matched nothing would pass while saying nothing, and would go on
        // passing after the members it is about were renamed out from under it.
        Assert.Contains($"{nameof(Dir)}.{nameof(Dir.Open)}", examined);
        Assert.Contains($"{nameof(Dir)}.{nameof(Dir.TryOpen)}", examined);

        static bool Produces(MethodBase member, Type[] authorities) => member switch
        {
            ConstructorInfo constructor => authorities.Contains(constructor.DeclaringType),
            MethodInfo method =>
                authorities.Contains(method.ReturnType) ||
                method.GetParameters().Any(parameter =>
                    parameter.ParameterType.IsByRef &&
                    authorities.Contains(parameter.ParameterType.GetElementType()!)),
            _ => false,
        };
    }

    /// <summary>The site a token names includes the member the call was written in.</summary>
    /// <remarks>
    /// A line number is exact until the file is edited. The member is the half of the
    /// location that a reader of a log six months later can still act on.
    /// </remarks>
    [Fact]
    public void A_token_names_the_member_it_was_acquired_in()
    {
        AmbientAuthority authority = AmbientAuthority.Acquire();

        Assert.Contains(
            nameof(A_token_names_the_member_it_was_acquired_in),
            authority.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A process that does not ask for the record of acquisitions does not get one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This suite takes authority in every test that opens a directory, which is what makes
    /// the assertion worth making here: with the recording off, which is the default, all of
    /// it accumulates nothing at all.
    /// </para>
    /// <para>
    /// An empty report is also the one case where saying nothing would be actively
    /// misleading — it reads like a process that reached for nothing — so it says instead
    /// that nothing was being recorded, and how to change that.
    /// </para>
    /// </remarks>
    [Fact]
    public void Acquisitions_go_unrecorded_until_the_recording_is_asked_for()
    {
        if (AmbientAuthority.IsRecording)
        {
            Assert.Skip("The recording is forced on for this whole run, so the default cannot be observed.");
        }

        using Dir dir = Dir.Open(_root, AmbientAuthority.Acquire());

        Assert.Empty(AmbientAuthority.RecordedSites);

        string report = AmbientAuthority.DescribeRecordedSites();
        Assert.Contains("recording is off", report, StringComparison.Ordinal);
        Assert.Contains(AmbientAuthority.RecordingSwitchName, report, StringComparison.Ordinal);
        Assert.Contains(AmbientAuthority.RecordingVariableName, report, StringComparison.Ordinal);
    }
}
