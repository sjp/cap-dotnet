using System.Text;
using static Cap.Benchmarks.Gate;

namespace Cap.Benchmarks.Tests;

public sealed class GateTests
{
    private const string A = "FileBenchmarks.OpenRead/walk";
    private const string B = "FileBenchmarks.StatFile/walk";
    private const string C = "FileBenchmarks.OpenRead/openat2";

    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    [Fact]
    public void A_committed_row_that_was_not_measured_fails()
    {
        var report = new StringBuilder();
        Comparison comparison = Compare(Baseline(A, B), [Measured(A)], None, [], report);

        Assert.Equal([$"{B}: in the baseline but not measured"], comparison.Failures);
        Assert.Contains($"| {B} | – | 1.000 | – | 0 B | **missing** |", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_committed_row_whose_job_is_allowed_to_be_missing_is_reported_but_passes()
    {
        var report = new StringBuilder();
        Comparison comparison = Compare(Baseline(A, C), [Measured(A)], None, ["openat2"], report);

        Assert.Empty(comparison.Failures);
        Assert.Contains($"| {C} | – | 1.000 | – | 0 B | missing (allowed) |", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_allowance_covers_only_the_job_it_names()
    {
        Comparison comparison = Compare(Baseline(A, B, C), [Measured(A)], None, ["openat2"], new StringBuilder());

        Assert.Equal([$"{B}: in the baseline but not measured"], comparison.Failures);
    }

    [Fact]
    public void A_row_that_produced_no_result_is_not_reported_missing_as_well()
    {
        // Collect has already failed it as "did not produce a result".
        var report = new StringBuilder();
        Comparison comparison = Compare(Baseline(A, B), [Measured(A)], Set(B), [], report);

        Assert.Empty(comparison.Failures);
        Assert.DoesNotContain(B, report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_committed_row_measured_and_within_tolerance_passes()
    {
        Comparison comparison = Compare(Baseline(A, B), [Measured(A), Measured(B)], None, [], new StringBuilder());

        Assert.Empty(comparison.Failures);
        Assert.Equal(0, comparison.Ungated);
    }

    [Fact]
    public void A_measured_row_with_no_committed_figures_is_ungated_not_failed()
    {
        Comparison comparison = Compare(Baseline(A), [Measured(A), Measured(B)], None, [], new StringBuilder());

        Assert.Empty(comparison.Failures);
        Assert.Equal(1, comparison.Ungated);
    }

    [Fact]
    public void Update_keeps_the_rows_of_a_job_that_did_not_run()
    {
        Dictionary<string, Figures> merged = Merge(Baseline(A, C), [Measured(A)], Set("walk"), None);

        Assert.Equal(Set(A, C), merged.Keys.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Update_drops_a_row_its_job_ran_without_producing()
    {
        Dictionary<string, Figures> merged = Merge(Baseline(A, B), [Measured(A)], Set("walk"), None);

        Assert.Equal([A], merged.Keys);
    }

    [Fact]
    public void Update_keeps_a_row_that_was_attempted_and_failed()
    {
        Dictionary<string, Figures> merged = Merge(Baseline(A, B), [Measured(A)], Set("walk"), Set(B));

        Assert.Equal(Set(A, B), merged.Keys.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Update_replaces_a_committed_row_with_the_measured_one()
    {
        Dictionary<string, Figures> merged = Merge(Baseline(A), [new Row(A, new Figures(2.0, 8), true)], Set("walk"), None);

        Assert.Equal(new Figures(2.0, 8), merged[A]);
    }

    [Theory]
    [InlineData("CapPathBenchmarks.ParseDeep[Syntax=Unix]/openat2", "openat2")]
    [InlineData("FileBenchmarks.OpenRead/walk", "walk")]
    public void The_job_is_the_part_after_the_last_slash(string key, string job) =>
        Assert.Equal(job, JobOf(key));

    private static BaselineFile Baseline(params string[] keys) => new()
    {
        Benchmarks = keys.ToDictionary(k => k, _ => new Figures(1.0, 0), StringComparer.Ordinal),
    };

    private static Row Measured(string key) => new(key, new Figures(1.0, 0), TimeGated: true);

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
