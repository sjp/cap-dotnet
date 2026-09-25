using Cap.Testing;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// That nothing in the adapter builds a path, apart from the one file whose job is the
/// virtual namespace's syntax.
/// </summary>
/// <remarks>
/// <para>
/// The same instrument the core layer is held to; see <see cref="PathConstructionAudit"/>.
/// This assembly cannot be held to it whole: code written against <c>IFileSystem</c> passes
/// absolute paths and a current directory, and gets full names back, so removing the virtual
/// root, joining a relative path onto the current directory and folding a full name for
/// display are all text work that has to be done somewhere.
/// </para>
/// <para>
/// All of it is kept in <c>VirtualPath.cs</c>, and that file alone is exempt. The exemption
/// is itself asserted, so a second file cannot quietly join it, and the behaviour that makes
/// it safe (the path handed to the <c>Dir</c> is the caller's own components, less the root)
/// is asserted in <see cref="VirtualPathTests"/>.
/// </para>
/// </remarks>
public sealed class PathConstructionAuditTests : PathConstructionAudit
{
    /// <summary>The prefix the build gives each carried source file.</summary>
    private const string Prefix = "CapIOAbstractionsSource.";

    /// <summary>The one file that may handle separators.</summary>
    private const string Exempt = Prefix + "VirtualPath.cs";

    /// <inheritdoc/>
    protected override string ResourcePrefix => Prefix;

    /// <summary>The source of this assembly, less the exempt file, as the build carried it in.</summary>
    public static TheoryData<string> SourceFiles =>
        [.. Sources(typeof(PathConstructionAuditTests).Assembly, Prefix).Select(row => row.Data).Where(name => name != Exempt)];

    /// <summary>The audit found the source it is supposed to be auditing.</summary>
    [Fact]
    public void The_source_being_audited_is_present() =>
        AssertSourceIsPresent("FileAdapter.cs");

    /// <summary>The exemption covers exactly the file that holds the virtual namespace's syntax.</summary>
    [Fact]
    public void Only_the_virtual_path_syntax_is_exempt()
    {
        string[] all = [.. Sources(typeof(PathConstructionAuditTests).Assembly, Prefix).Select(row => row.Data)];

        Assert.Contains(Exempt, all);
        Assert.Equal([Exempt], all.Except(SourceFiles.Select(row => row.Data)));
    }

    /// <summary>No literal anywhere else in the assembly contains a path separator.</summary>
    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void No_literal_in_the_assembly_contains_a_path_separator(string resource) =>
        AssertNoSeparatorLiterals(resource);
}
