using System.Reflection;
using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// What the handle type deliberately does not offer.
/// </summary>
/// <remarks>
/// <para>
/// A property answering "where is this?" would be the single most requested addition to this
/// type and the one most corrosive to it. Once a handle can produce a path, callers compare
/// them, join them, and check that one starts with another — which is the technique the
/// handle exists to replace, reintroduced through a convenience. The absence is therefore
/// part of the design rather than a gap, and is worth a test because an innocuous-looking
/// addition would undo it.
/// </para>
/// <para>
/// The same goes for the string form. A type whose text representation is its path invites
/// exactly the same reasoning, in log lines, in string interpolation and in anything that
/// builds a key out of an object, without anybody having asked for a path at all.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirSurfaceTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>There is no public member that hands back a path.</summary>
    /// <remarks>
    /// Named members rather than return types, because the one member that does produce a
    /// path demands a token for it and announces in its name that the answer may be wrong by
    /// the time it is read.
    /// </remarks>
    [Theory]
    [InlineData("Path")]
    [InlineData("FullName")]
    [InlineData("FullPath")]
    [InlineData("Name")]
    [InlineData("DirectoryName")]
    [InlineData("Location")]
    public void The_handle_exposes_no_member_naming_its_own_path(string member)
    {
        MemberInfo[] found = typeof(Dir).GetMember(
            member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        Assert.Empty(found);
    }

    /// <summary>The string form is not the path either.</summary>
    [Fact]
    public void The_string_form_is_not_a_path()
    {
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        string text = root.ToString()!;

        Assert.DoesNotContain(_tree.HostPath, text, StringComparison.Ordinal);
        Assert.Equal(typeof(Dir).ToString(), text);
    }

    /// <summary>
    /// Every public way of producing a handle from nothing demands the ambient token.
    /// </summary>
    /// <remarks>
    /// The property that makes this a capability system: a factory that reached the
    /// filesystem without one would let a component conjure authority nobody gave it, and
    /// would do so without appearing in a search for the places that take it. Checked by
    /// reflection rather than by reading the file, because the thing being protected is a
    /// rule about every future member as much as about the ones written so far.
    /// </remarks>
    [Fact]
    public void Every_way_of_creating_a_handle_from_nothing_demands_the_token()
    {
        Assert.Empty(typeof(Dir).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        MethodInfo[] factories = [.. typeof(Dir)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(Dir) ||
                             method.GetParameters().Any(p => p.ParameterType == typeof(Dir).MakeByRefType()))];

        Assert.NotEmpty(factories);
        foreach (MethodInfo factory in factories)
        {
            Assert.Contains(
                factory.GetParameters(),
                parameter => parameter.ParameterType == typeof(AmbientAuthority));
        }
    }
}
