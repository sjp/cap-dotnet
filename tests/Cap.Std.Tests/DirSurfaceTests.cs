using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
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

    /// <summary>
    /// Each handle type's interface has exactly the handle's public members, apart from the
    /// one that hands out the operating-system handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interfaces are a description of the handles for code that wants to substitute them
    /// in a test, and a description that lags behind is worse than none: a member added to
    /// <see cref="Dir"/> and not to <see cref="IDir"/> would be out of reach of every
    /// component written against the interface, and nothing would say so until someone went
    /// looking for it. So the comparison is by reflection over whatever the handles have now,
    /// names, parameter names, defaults and all, with each handle type standing for its
    /// interface wherever it appears in a signature.
    /// </para>
    /// <para>
    /// <c>UnsafeGetHandle</c> is the one exception, and its absence is asserted rather than
    /// merely tolerated. A stub has no handle to give, and leaving the member off the
    /// interfaces keeps every way of reaching the raw handle on the concrete types, where the
    /// analyzer rule that flags it for review looks.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(typeof(Dir), typeof(IDir))]
    [InlineData(typeof(CapFile), typeof(ICapFile))]
    [InlineData(typeof(DirEntry), typeof(IDirEntry))]
    [InlineData(typeof(CapOpened), typeof(ICapOpened))]
    public void Every_public_member_of_a_handle_is_on_its_interface(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties)]
        Type handle,
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.Interfaces)]
        Type contract)
    {
        Assert.True(contract.IsAssignableFrom(handle));

        // The only interface any of them inherits. Named rather than discovered, so that the
        // members of whatever is inherited can be read without reflection the trimmer
        // cannot follow; a new base interface fails here and has to be added.
        Type[] inherited = contract.GetInterfaces();
        Assert.All(inherited, type => Assert.Equal(typeof(IDisposable), type));

        string[] expected = [.. Members(handle)
            .Where(member => member.DeclaringType != typeof(object) && member.DeclaringType != typeof(ValueType))
            .Where(member => member.Name != "UnsafeGetHandle")
            .Select(Describe)
            .Order(StringComparer.Ordinal)];

        string[] actual = [.. Members(contract)
            .Concat(inherited.Length == 0 ? [] : Members(typeof(IDisposable)))
            .Select(Describe)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(actual, member => member.Contains("UnsafeGetHandle", StringComparison.Ordinal));
    }

    /// <summary>A type's public instance properties, and its public instance methods other than accessors.</summary>
    private static IEnumerable<MemberInfo> Members(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties)]
        Type type) =>
        type.GetProperties().Where(property => !(property.GetMethod ?? property.SetMethod)!.IsStatic).Cast<MemberInfo>()
            .Concat(type.GetMethods().Where(method => !method.IsStatic && !method.IsSpecialName));

    private static string Describe(MemberInfo member)
    {
        switch (member)
        {
            case PropertyInfo property:
                return $"{property.Name} : {Interface(property.PropertyType)} " +
                       $"{{{(property.GetMethod?.IsPublic == true ? " get;" : "")}" +
                       $"{(property.SetMethod?.IsPublic == true ? " set;" : "")} }}";

            case MethodInfo method:
                var text = new StringBuilder();
                text.Append(method.Name).Append('(');
                text.AppendJoin(", ", method.GetParameters().Select(parameter =>
                    $"{(parameter.IsOut ? "out " : "")}{Interface(parameter.ParameterType)} {parameter.Name}" +
                    (parameter.HasDefaultValue ? $" = {parameter.DefaultValue ?? "default"}" : "")));
                text.Append(") : ").Append(Interface(method.ReturnType));
                return text.ToString();

            default:
                throw new ArgumentOutOfRangeException(nameof(member));
        }
    }

    /// <summary>The name a type has in an interface's signature, with each handle type replaced by its interface.</summary>
    private static string Interface(Type type)
    {
        if (type.IsByRef)
        {
            return Interface(type.GetElementType()!);
        }

        if (type.IsGenericType)
        {
            string name = type.GetGenericTypeDefinition().Name;
            return $"{name[..name.IndexOf('`', StringComparison.Ordinal)]}<" +
                   string.Join(", ", type.GetGenericArguments().Select(Interface)) + ">";
        }

        return type == typeof(Dir) ? nameof(IDir)
            : type == typeof(CapFile) ? nameof(ICapFile)
            : type == typeof(DirEntry) ? nameof(IDirEntry)
            : type == typeof(CapOpened) ? nameof(ICapOpened)
            : type.Name;
    }
}
