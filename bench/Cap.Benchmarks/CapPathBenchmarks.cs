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
/// <para>
/// Without a <c>System.IO</c> row there is no ratio to hold time to, so the regression gate
/// watches this class for allocation and for each row's time relative to the single-component
/// parse. The allocation is the part that matters: any byte at all fails it.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.HotPath)]
public class CapPathBenchmarks
{
    private const string SingleComponent = "config.json";
    private const string ShallowPath = "config/app.json";
    private const string DeepPath = "a/b/c/d/e/f/g/h/config.json";

    /// <summary>
    /// A path whose every component is a near-miss for a Windows device name, so that each
    /// one runs the device comparisons to the end before being allowed through.
    /// </summary>
    /// <remarks>
    /// The stem lengths are chosen to land on the arms that actually compare: three and four
    /// characters for the console and port names, six and seven for the console input and
    /// output streams. A component whose stem is any other length is dismissed on its length
    /// alone and would measure nothing.
    /// </remarks>
    private const string DeviceLookalikePath = "cat/cold/config/content/lpto/console.log";

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

    /// <summary>
    /// The worst case for the Windows name rules, run under both syntaxes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows needs more of a component than POSIX does: every character is checked against
    /// the set the platform forbids, the stem is cut out and matched against the device
    /// table, and the last character is checked for the dot or space Windows would strip.
    /// POSIX needs one thing, that the component holds no <c>U+0000</c>. This case is built
    /// so that none of the Windows work can exit early, which makes the gap between the two
    /// syntax rows the full price of those rules.
    /// </para>
    /// <para>
    /// The row that matters is the POSIX one, and what it has to show is that it does not
    /// move: the rules are selected by syntax rather than by the running OS, so a Unix caller
    /// must not be paying for a defence against a platform it is not on. Comparing it against
    /// <see cref="ParseDeep"/> under the same syntax is the comparison to make — two paths of
    /// similar length, one of them adversarial only to Windows, and no difference between
    /// them.
    /// </para>
    /// </remarks>
    [Benchmark]
    public bool ParseDeviceLookalikes() =>
        CapPath.TryParse(DeviceLookalikePath, Syntax, ParentLinkPolicy.Reject, out _, out _);

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
