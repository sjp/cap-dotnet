using Cap.Testing;

namespace Cap.Std.Tests;

/// <summary>
/// That nothing in the public layer builds a path.
/// </summary>
/// <remarks>
/// The audit itself is shared; this points it at this assembly's carried source. See
/// <see cref="PathConstructionAudit"/> for why the guarantee is asserted about the text of
/// the library rather than about its behaviour.
/// </remarks>
public sealed class DirPathConstructionAuditTests : PathConstructionAudit
{
    /// <summary>The prefix the build gives each carried source file.</summary>
    private const string Prefix = "CapStdSource.";

    /// <inheritdoc/>
    protected override string ResourcePrefix => Prefix;

    /// <summary>The source of this library, as the build carried it in.</summary>
    public static TheoryData<string> SourceFiles =>
        Sources(typeof(DirPathConstructionAuditTests).Assembly, Prefix);

    /// <summary>The audit found the source it is supposed to be auditing.</summary>
    [Fact]
    public void The_source_being_audited_is_present() => AssertSourceIsPresent("Dir.cs");

    /// <summary>No literal anywhere in the library contains a path separator.</summary>
    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void No_literal_in_the_library_contains_a_path_separator(string resource) =>
        AssertNoSeparatorLiterals(resource);
}
