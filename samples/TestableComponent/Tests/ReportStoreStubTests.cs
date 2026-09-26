using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;
using Moq;

namespace TestableComponent.Tests;

/// <summary>
/// A stubbed <see cref="IDir"/>, for tests about the calls the component makes: which names
/// it reads or removes, how often, and what it does when one of them fails.
/// </summary>
/// <remarks>
/// A stub resolves no names, so nothing here shows that the component stays inside its
/// directory. The in-memory tests do that.
/// </remarks>
public sealed class ReportStoreStubTests
{
    [Fact]
    public void Only_files_named_for_a_day_are_listed()
    {
        // <stub>
        Mock<IDir> reports = new();
        reports.Setup(dir => dir.EnumerateEntries()).Returns(
        [
            TestEntries.Create("2026-09-01.json", CapFileType.File, TestFileIds.Next(), reports.Object),
            TestEntries.Create("2026-09-02.json", CapFileType.Directory, TestFileIds.Next(), reports.Object),
            TestEntries.Create("notes.json", CapFileType.File, TestFileIds.Next(), reports.Object),
        ]);

        IReadOnlyList<DateOnly> days = new ReportStore(reports.Object).ListDays();

        Assert.Equal([new DateOnly(2026, 9, 1)], days);
        reports.Verify(dir => dir.ReadAllText(It.IsAny<string>()), Times.Never);
        // </stub>
    }

    [Fact]
    public void Pruning_asks_to_remove_each_older_report_once()
    {
        Mock<IDir> reports = new();
        reports.Setup(dir => dir.EnumerateEntries()).Returns(
        [
            TestEntries.Create("2026-08-31.json", CapFileType.File, TestFileIds.Next(), reports.Object),
            TestEntries.Create("2026-09-01.json", CapFileType.File, TestFileIds.Next(), reports.Object),
        ]);
        reports.Setup(dir => dir.TryDeleteFile(It.IsAny<string>())).Returns(true);

        new ReportStore(reports.Object).Prune(new DateOnly(2026, 9, 1));

        reports.Verify(dir => dir.TryDeleteFile("2026-08-31.json"), Times.Once);
        reports.Verify(dir => dir.TryDeleteFile("2026-09-01.json"), Times.Never);
    }

    [Fact]
    public void A_report_removed_by_someone_else_is_not_counted()
    {
        Mock<IDir> reports = new();
        reports.Setup(dir => dir.EnumerateEntries()).Returns(
        [
            TestEntries.Create("2026-08-31.json", CapFileType.File, TestFileIds.Next(), reports.Object),
        ]);
        reports.Setup(dir => dir.TryDeleteFile("2026-08-31.json")).Returns(false);

        Assert.Equal(0, new ReportStore(reports.Object).Prune(new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void A_refused_read_reaches_the_caller()
    {
        Mock<IDir> reports = new();
        reports.Setup(dir => dir.Exists("2026-09-01.json")).Returns(true);
        reports.Setup(dir => dir.ReadAllText("2026-09-01.json")).Throws(new UnauthorizedAccessException("refused"));

        Assert.Throws<UnauthorizedAccessException>(() => new ReportStore(reports.Object).Load(new DateOnly(2026, 9, 1)));
    }
}
