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
        Assert.Contains($"| {B} | – | 1.000 | – (–) | – | 0 B | **missing** |", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_committed_row_whose_job_is_allowed_to_be_missing_is_reported_but_passes()
    {
        var report = new StringBuilder();
        Comparison comparison = Compare(Baseline(A, C), [Measured(A)], None, ["openat2"], report);

        Assert.Empty(comparison.Failures);
        Assert.Contains($"| {C} | – | 1.000 | – (–) | – | 0 B | missing (allowed) |", report.ToString(), StringComparison.Ordinal);
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

    [Fact]
    public void A_ratio_within_the_tolerance_is_ok_however_wide_its_interval() =>
        Assert.Equal(TimeVerdict.Ok, JudgeTime(1.09, new RatioInterval(0.5, 2.0), 1.0));

    [Fact]
    public void A_ratio_whose_whole_interval_is_over_the_line_regresses() =>
        Assert.Equal(TimeVerdict.Regressed, JudgeTime(1.15, new RatioInterval(1.12, 1.18), 1.0));

    [Fact]
    public void A_ratio_over_the_line_whose_interval_reaches_below_it_is_suspect() =>
        Assert.Equal(TimeVerdict.Suspect, JudgeTime(1.15, new RatioInterval(1.05, 1.25), 1.0));

    [Fact]
    public void A_ratio_over_the_line_with_no_interval_regresses() =>
        Assert.Equal(TimeVerdict.Regressed, JudgeTime(1.15, null, 1.0));

    [Fact]
    public void The_line_scales_with_the_committed_ratio()
    {
        Assert.Equal(TimeVerdict.Ok, JudgeTime(2.30, new RatioInterval(2.29, 2.31), 2.10));
        Assert.Equal(TimeVerdict.Regressed, JudgeTime(2.35, new RatioInterval(2.33, 2.37), 2.10));
    }

    [Fact]
    public void Spread_combines_the_two_margins_at_their_worst()
    {
        (RatioInterval interval, double rse) = Spread(2.0, new Noise(100, 3, 10), new Noise(50, 2, 2.5));

        // 2 x (1 - 0.1) / (1 + 0.05) and 2 x (1 + 0.1) / (1 - 0.05).
        Assert.Equal(1.7143, interval.Lower);
        Assert.Equal(2.3158, interval.Upper);

        // sqrt(0.03^2 + 0.04^2).
        Assert.Equal(0.05, rse);
    }

    [Fact]
    public void Spread_leaves_a_bound_open_when_a_margin_is_as_large_as_its_mean()
    {
        (RatioInterval interval, _) = Spread(1.0, new Noise(10, 5, 10), new Noise(10, 5, 10));

        Assert.Equal(0, interval.Lower);
        Assert.Equal(double.PositiveInfinity, interval.Upper);
    }

    [Fact]
    public void A_clear_time_regression_fails_the_gate()
    {
        var report = new StringBuilder();
        Row slow = new(A, new Figures(1.15, 0, 0.002), TimeGated: true, new RatioInterval(1.13, 1.17));
        Comparison comparison = Compare(Baseline(A), [slow], None, [], report);

        Assert.Equal([$"{A}: time ratio 1.150 (1.130–1.170) against 1.000"], comparison.Failures);
        Assert.Empty(comparison.Suspects);
        Assert.Contains("**regressed**", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_noisy_ratio_over_the_line_is_reported_suspect_and_passes()
    {
        var report = new StringBuilder();
        Row noisy = new(A, new Figures(1.15, 0, 0.04), TimeGated: true, new RatioInterval(1.02, 1.30));
        Comparison comparison = Compare(Baseline(A), [noisy], None, [], report);

        Assert.Empty(comparison.Failures);
        Assert.Equal([$"{A}: time ratio 1.150 (1.020–1.300) against 1.000"], comparison.Suspects);
        Assert.Contains("suspect: interval crosses the line", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_ratio_that_is_not_time_gated_is_never_suspect()
    {
        Row noisy = new(A, new Figures(1.5, 0, 0.04), TimeGated: false, new RatioInterval(1.0, 2.0));
        Comparison comparison = Compare(Baseline(A), [noisy], None, [], new StringBuilder());

        Assert.Empty(comparison.Failures);
        Assert.Empty(comparison.Suspects);
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
