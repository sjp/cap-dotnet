using Microsoft.CodeAnalysis;

namespace Cap.Analyzers.Tests;

public sealed class UnsafeHandleTests
{
    [Fact]
    public async Task Taking_the_raw_handle_out_of_a_capability_is_reported_for_review()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            using System;
            using System.Runtime.InteropServices;
            using Cap.Std;

            public static class Uses
            {
                public static SafeHandle Directory(Dir dir) => dir.UnsafeGetHandle();
                public static SafeHandle File(CapFile file) => file.UnsafeGetHandle();
                public static Func<SafeHandle> Later(Dir dir) => dir.UnsafeGetHandle;
            }
            """);

        Assert.Equal(
            ["dir.UnsafeGetHandle()", "file.UnsafeGetHandle()", "dir.UnsafeGetHandle"],
            diagnostics.Select(d => d.Flagged()));
        Assert.All(diagnostics, d => Assert.Equal(("CAP0004", DiagnosticSeverity.Info), (d.Id, d.Severity)));
    }

    [Fact]
    public async Task A_method_of_the_same_name_elsewhere_is_not_this_library_s_business()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            public sealed class Mine
            {
                public int UnsafeGetHandle() => 0;
                public int Use() => UnsafeGetHandle();
            }
            """);

        Assert.Empty(diagnostics);
    }
}
