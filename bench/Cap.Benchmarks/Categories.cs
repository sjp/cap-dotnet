namespace Cap.Benchmarks;

/// <summary>Benchmark categories and naming conventions the runner and the gate rely on.</summary>
internal static class Categories
{
    /// <summary>
    /// The operations paid on every request, which the regression gate runs on every change.
    /// Select them by hand with <c>--anyCategories HotPath</c>.
    /// </summary>
    public const string HotPath = "HotPath";

    /// <summary>
    /// The name every <c>System.IO</c> baseline method has. The gate holds a baseline's
    /// time as the yardstick and does not gate it: what <c>System.IO</c> allocates, or how its
    /// speed moves between runtimes, is not a regression in this library.
    /// </summary>
    public const string SystemIOMethod = "SystemIO";
}
