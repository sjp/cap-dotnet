using System.Runtime.CompilerServices;

namespace Cap.Primitives.Tests;

/// <summary>
/// Parsing must not allocate.
/// </summary>
/// <remarks>
/// <para>
/// This is measured by a benchmark as well, but a benchmark is run when someone remembers
/// to run it. Asserting it here makes a regression fail the build on the commit that caused
/// it, which is the difference between a property the library has and one it used to have.
/// </para>
/// <para>
/// The claim is real rather than nominal: a parsed path borrows the caller's string instead
/// of copying it, and walking its components yields spans into that same string, so a parse
/// of any length allocates nothing at all.
/// </para>
/// </remarks>
public sealed class CapPathAllocationTests
{
    [Theory]
    [InlineData(CapPathSyntax.Unix)]
    [InlineData(CapPathSyntax.Windows)]
    public void Parsing_and_walking_allocate_nothing(CapPathSyntax syntax)
    {
        // Warm up first. The very first call through a method also pays for its jitting,
        // and that allocation is not the one under test.
        for (int i = 0; i < 64; i++)
        {
            ParseAndWalk(syntax);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            ParseAndWalk(syntax);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// One parse and one full walk, over a path long enough that a per-component allocation
    /// would show up clearly. Kept out of the measuring method so nothing the assertion
    /// machinery does is counted.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ParseAndWalk(CapPathSyntax syntax)
    {
        const string Raw = "a/b/c/d/e/f/g/h/config.json";

        if (!CapPath.TryParse(Raw, syntax, ParentLinkPolicy.Reject, out CapPath path, out _))
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
}
