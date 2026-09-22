using Cap.Testing;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// That nothing in the convenience layer builds a path either.
/// </summary>
/// <remarks>
/// The same instrument the core layer is held to, pointed at this assembly. It matters more
/// here, not less: a walk, a copy and a pattern search all work level by level, and level by
/// level is exactly the shape in which a path gets accumulated by somebody who means well.
/// See <see cref="PathConstructionAudit"/>.
/// </remarks>
public sealed class PathConstructionAuditTests : PathConstructionAudit
{
    /// <summary>The prefix the build gives each carried source file.</summary>
    private const string Prefix = "CapFsExtSource.";

    /// <inheritdoc/>
    protected override string ResourcePrefix => Prefix;

    /// <summary>The source of this assembly, as the build carried it in.</summary>
    public static TheoryData<string> SourceFiles =>
        Sources(typeof(PathConstructionAuditTests).Assembly, Prefix);

    /// <summary>The audit found the source it is supposed to be auditing.</summary>
    [Fact]
    public void The_source_being_audited_is_present() =>
        AssertSourceIsPresent("DirExtensions.Copy.cs");

    /// <summary>No literal anywhere in the assembly contains a path separator.</summary>
    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void No_literal_in_the_assembly_contains_a_path_separator(string resource) =>
        AssertNoSeparatorLiterals(resource);
}
