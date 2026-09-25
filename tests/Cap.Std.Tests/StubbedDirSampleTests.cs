using Cap.Primitives;
using Moq;

namespace Cap.Std.Tests;

/// <summary>
/// A component written against <see cref="IDir"/>, tested with a stub from a mocking library
/// and no filesystem at all.
/// </summary>
/// <remarks>
/// <para>
/// Written as a consumer would write it, to show the shape rather than to test this library:
/// the component below is the kind of code that takes the interface, and the tests are the
/// kind a team with a mocking library as its house style would write for it. Nothing here
/// reaches the disk or the in-memory filesystem.
/// </para>
/// <para>
/// A stub checks what the component asked for and nothing about where it would have
/// landed; it resolves no names and confines nothing. For a test of what a component does
/// to a tree, a real <see cref="Dir"/> on the in-memory filesystem is the better double.
/// </para>
/// </remarks>
public sealed class StubbedDirSampleTests
{
    /// <summary>A component that reads every <c>.json</c> file in the directory it is given.</summary>
    private sealed class ReportIndex(IDir reports)
    {
        public Dictionary<string, string> Load()
        {
            Dictionary<string, string> loaded = new(StringComparer.Ordinal);
            foreach (IDirEntry entry in reports.EnumerateEntries())
            {
                if (entry.Type == CapFileType.File && entry.Name.EndsWith(".json", StringComparison.Ordinal))
                {
                    loaded[entry.Name] = reports.ReadAllText(entry.Name);
                }
            }

            return loaded;
        }
    }

    private static IDirEntry Entry(string name, CapFileType type) =>
        Mock.Of<IDirEntry>(entry => entry.Name == name && entry.Type == type);

    [Fact]
    public void The_component_reads_each_report_the_listing_names()
    {
        Mock<IDir> reports = new();
        reports.Setup(dir => dir.EnumerateEntries()).Returns(
        [
            Entry("a.json", CapFileType.File),
            Entry("notes.txt", CapFileType.File),
            Entry("archive.json", CapFileType.Directory),
        ]);
        reports.Setup(dir => dir.ReadAllText("a.json")).Returns("""{ "total": 3 }""");

        Dictionary<string, string> loaded = new ReportIndex(reports.Object).Load();

        Assert.Equal("""{ "total": 3 }""", Assert.Single(loaded).Value);
        reports.Verify(dir => dir.ReadAllText("a.json"), Times.Once);
        reports.Verify(dir => dir.ReadAllText("notes.txt"), Times.Never);
        reports.Verify(dir => dir.ReadAllText("archive.json"), Times.Never);
    }

    [Fact]
    public void A_refusal_from_the_directory_reaches_the_caller()
    {
        Mock<IDir> reports = new();
        reports.Setup(dir => dir.EnumerateEntries()).Returns([Entry("a.json", CapFileType.File)]);
        reports.Setup(dir => dir.ReadAllText("a.json")).Throws(new UnauthorizedAccessException("refused"));

        Assert.Throws<UnauthorizedAccessException>(() => new ReportIndex(reports.Object).Load());
    }
}
