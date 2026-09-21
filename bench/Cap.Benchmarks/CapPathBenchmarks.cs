using BenchmarkDotNet.Attributes;
using Cap.Primitives;

namespace Cap.Benchmarks;

/// <summary>
/// Path parsing, measured for allocation as much as for time.
/// </summary>
/// <remarks>
/// <para>
/// Parsing sits in front of every single operation, so its cost is paid on the most common
/// path through the library and is the one place where an allocation would be felt. The
/// interesting column here is therefore Allocated, which must read zero: a parsed path
/// borrows the caller's string rather than copying it, and walking its components yields
/// spans into that same string.
/// </para>
/// <para>
/// There is no <c>System.IO</c> baseline in this file on purpose. The obvious candidate
/// would be combining and fully-qualifying a path, but that is a different operation with a
/// different answer -- it consults the process working directory and rewrites the string --
/// and putting the two side by side would invite reading the gap as a cost of safety rather
/// than as two functions doing different things.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CapPathBenchmarks
{
    private const string SingleComponent = "config.json";
    private const string ShallowPath = "config/app.json";
    private const string DeepPath = "a/b/c/d/e/f/g/h/config.json";

    [Params(CapPathSyntax.Unix, CapPathSyntax.Windows)]
    public CapPathSyntax Syntax { get; set; }

    /// <summary>The fast path: one name, one lookup, no walk.</summary>
    [Benchmark(Baseline = true)]
    public bool ParseSingleComponent() =>
        CapPath.TryParse(SingleComponent, Syntax, ParentLinkPolicy.Reject, out _, out _);

    [Benchmark]
    public bool ParseShallow() =>
        CapPath.TryParse(ShallowPath, Syntax, ParentLinkPolicy.Reject, out _, out _);

    [Benchmark]
    public bool ParseDeep() =>
        CapPath.TryParse(DeepPath, Syntax, ParentLinkPolicy.Reject, out _, out _);

    /// <summary>
    /// Parsing plus the walk a resolver actually performs, which is what the cost really is
    /// at the call site.
    /// </summary>
    [Benchmark]
    public int ParseAndWalkDeep()
    {
        if (!CapPath.TryParse(DeepPath, Syntax, ParentLinkPolicy.Reject, out CapPath path, out _))
        {
            return -1;
        }

        int characters = 0;
        foreach (ReadOnlySpan<char> component in path.EnumerateComponents())
        {
            characters += component.Length;
        }

        return characters;
    }

    /// <summary>Validation straight off a span, for a caller that has no string to hand.</summary>
    [Benchmark]
    public CapPathError ValidateSpan() =>
        CapPath.Validate(DeepPath.AsSpan(), Syntax, ParentLinkPolicy.Reject);

    /// <summary>
    /// A rejection, to confirm the refusing path is not quietly more expensive than the
    /// accepting one. A sandbox that takes noticeably longer to say no leaks which inputs
    /// it dislikes.
    /// </summary>
    [Benchmark]
    public bool ParseRejected() =>
        CapPath.TryParse("../../etc/passwd", Syntax, ParentLinkPolicy.Reject, out _, out _);
}
