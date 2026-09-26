using Cap.Primitives;
using Cap.Std;

namespace TestableComponent.Tests;

/// <summary>
/// A scratch directory on disk, for what only a real filesystem has: the host's resolution
/// backend, flushing to storage, and files other programs put there.
/// </summary>
/// <remarks>
/// Slower than memory and different on each platform, so these are kept to the few things the
/// in-memory tests cannot show.
/// </remarks>
public sealed class ReportStoreOnDiskTests
{
    [Fact]
    public void A_report_saved_on_disk_is_read_back()
    {
        // <on-disk>
        using CapTempDir scratch = CapTempDir.New(AmbientAuthority.Acquire());
        var store = new ReportStore(scratch.Directory);

        store.Save(new DateOnly(2026, 9, 1), """{ "total": 3 }""");   // flushed to storage by default

        Assert.Equal("""{ "total": 3 }""", store.Load(new DateOnly(2026, 9, 1)));
        Assert.Equal(Dir.ResolutionBackend, scratch.Directory.Backend);
        // </on-disk>
    }

    [Fact]
    public void A_report_another_program_wrote_is_listed()
    {
        using CapTempDir scratch = CapTempDir.New(AmbientAuthority.Acquire());
        Assert.True(scratch.Directory.TryGetPath(AmbientAuthority.Acquire(), out string? path));
        File.WriteAllText(Path.Combine(path, "2026-09-01.json"), "{}");

        Assert.Equal([new DateOnly(2026, 9, 1)], new ReportStore(scratch.Directory).ListDays());
    }
}
