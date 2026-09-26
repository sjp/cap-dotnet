using Cap.Std;
using Cap.Std.Testing;

namespace TestableComponent.Tests;

/// <summary>
/// The default way to test a component that takes a directory: a real <see cref="Dir"/> on a
/// filesystem held in memory, so that the library's own resolution runs and the tree can be
/// inspected afterwards without going through the component.
/// </summary>
public sealed class ReportStoreInMemoryTests
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    [Fact]
    public void A_saved_report_is_a_file_named_for_its_day()
    {
        // <in-memory>
        var fs = new InMemoryFileSystem();
        fs.AddFile("reports/notes.txt", "not a report");

        using Dir reports = fs.OpenRoot("reports");
        var store = new ReportStore(reports);

        store.Save(new DateOnly(2026, 9, 1), """{ "total": 3 }""");

        Assert.Equal("""{ "total": 3 }""", fs.ReadAllText("reports/2026-09-01.json"));
        Assert.Equal([new DateOnly(2026, 9, 1)], store.ListDays());
        // </in-memory>
    }

    [Fact]
    public void Pruning_removes_only_older_reports()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("2026-08-30.json", "{}");
        fs.AddFile("2026-08-31.json", "{}");
        fs.AddFile("2026-09-01.json", "{}");
        using Dir root = fs.OpenRoot();

        int removed = new ReportStore(root).Prune(Day);

        Assert.Equal(2, removed);
        Assert.Equal(["2026-09-01.json"], fs.GetEntries());
    }

    [Fact]
    public void A_failed_save_leaves_the_previous_report_in_place()
    {
        // <fault>
        var fs = new InMemoryFileSystem();
        fs.AddFile("2026-09-01.json", """{ "total": 3 }""");
        using Dir root = fs.OpenRoot();
        var store = new ReportStore(root);

        fs.FailNextWrites(1, CapErrorKind.Other);   // what a full disk reports

        IOException e = Assert.ThrowsAny<IOException>(() => store.Save(new DateOnly(2026, 9, 1), """{ "total": 4 }"""));
        Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(e));
        Assert.Equal("""{ "total": 3 }""", fs.ReadAllText("2026-09-01.json"));
        Assert.Equal(["2026-09-01.json"], fs.GetEntries());   // no half-written scratch file left behind
        // </fault>
    }

    [Fact]
    public void An_unreadable_report_directory_is_reported_to_the_caller()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("reports");
        using Dir reports = fs.OpenRoot("reports");
        fs.SetUnreadable("reports");

        Assert.Throws<UnauthorizedAccessException>(() => new ReportStore(reports).ListDays());
    }
}
