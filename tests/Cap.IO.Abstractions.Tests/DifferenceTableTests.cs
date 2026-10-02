using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// The rows of the "Differences from <c>System.IO</c>" table in the package documentation,
/// each spelled as its first cell is, without the code marks. A test that proves a row carries
/// it as a trait named <see cref="Name"/>.
/// </summary>
internal static class Difference
{
    public const string Name = "Difference";

    public const string Reach = "Reach";
    public const string WriteThroughLink = "A symbolic link at the name an open creates, truncates or appends to";
    public const string LinkTargets = "Symbolic link targets";
    public const string ResolveRootedLink = "ResolveLinkTarget on a link with a rooted target";
    public const string DoubleSeparator = "//x";
    public const string JoinSeam = "Path.Join with a separator on both sides of a seam";
    public const string FullNames = "Full names";
    public const string CurrentDirectory = "Current directory";
    public const string LogicalDrives = "Directory.GetLogicalDrives";
    public const string TempPath = "Path.GetTempPath";
    public const string TempFileName = "Path.GetTempFileName";
    public const string TempSubdirectory = "Directory.CreateTempSubdirectory(prefix)";
    public const string AppendStream = "A stream opened with FileMode.Append";
    public const string SearchPatterns = "Search patterns";
    public const string RecursiveEnumeration = "Recursive enumeration";
    public const string Copy = "File.Copy";
    public const string CreateSubdirectoryAbove = "DirectoryInfo.CreateSubdirectory(\"../x\")";
    public const string MoveIntoMissing = "Directory.Move into a directory that is missing";
    public const string RootFixed = "Removing or moving the root";
    public const string Permissions = "Changing permissions or attributes: SetAttributes, SetUnixFileMode, the Attributes, IsReadOnly and UnixFileMode setters";
    public const string CreationMode = "Creating with a Unix mode: Directory.CreateDirectory(path, mode), FileStreamOptions.UnixCreateMode";
    public const string CreationTime = "Setting a creation time";
    public const string AbsentAccessTime = "Reading an access time the filesystem does not keep";
    public const string Handles = "Members that take a SafeFileHandle, RandomAccess, and Wrap on the factories";
    public const string OpenHandle = "File.OpenHandle";
    public const string HostServices = "DriveInfo, FileSystemWatcher, FileVersionInfo, access control lists, Encrypt, Decrypt";
}

/// <summary>
/// That the documented differences and the tests that prove them cannot drift apart: every
/// row of the table has a test, and every test names a row that is there.
/// </summary>
public sealed class DifferenceTableTests
{
    private const string Heading = "## Differences from `System.IO`";

    [Fact]
    public void Every_documented_difference_has_a_test()
    {
        HashSet<string> tested = TestedRows();

        Assert.DoesNotContain(DocumentedRows(), row => !tested.Contains(row));
    }

    [Fact]
    public void Every_difference_test_names_a_documented_row()
    {
        HashSet<string> documented = [.. DocumentedRows()];

        Assert.DoesNotContain(TestedRows(), row => !documented.Contains(row));
    }

    /// <summary>The first cell of each row of the table, without code marks.</summary>
    private static List<string> DocumentedRows()
    {
        using Stream stream = typeof(DifferenceTableTests).Assembly.GetManifestResourceStream("io-abstractions.md")
            ?? throw new InvalidOperationException("The package documentation is not embedded in the test assembly.");
        using StreamReader reader = new(stream);

        List<string> rows = [];
        string? line;
        while ((line = reader.ReadLine()) is not null && line != Heading)
        {
        }

        Assert.NotNull(line);
        bool inTable = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.StartsWith('|'))
            {
                if (inTable)
                {
                    break;
                }

                continue;
            }

            inTable = true;
            string cell = line.Split('|')[1].Trim().Replace("`", string.Empty, StringComparison.Ordinal);
            if (cell != "Area" && !cell.StartsWith("---", StringComparison.Ordinal))
            {
                rows.Add(cell);
            }
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    /// <summary>The rows named by a <see cref="Difference"/> trait on any test in this assembly.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject is every test in this assembly, which cannot be named statically, " +
                        "and the suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "The types reflected over are whatever the assembly defines. The suite is not trimmed.")]
    private static HashSet<string> TestedRows()
    {
        HashSet<string> rows = [];
        foreach (Type type in typeof(DifferenceTableTests).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (TraitAttribute trait in method.GetCustomAttributes<TraitAttribute>())
                {
                    if (trait.Name == Difference.Name)
                    {
                        rows.Add(trait.Value);
                    }
                }
            }
        }

        return rows;
    }
}
