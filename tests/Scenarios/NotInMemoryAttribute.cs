#if !CAP_XUNIT_AOT
using System.Reflection;
using Xunit.v3;
#endif

namespace Cap.Testing;

/// <summary>
/// Marks a test that is about the host itself and has no meaning when a filesystem held in
/// memory stands in for it, and says why.
/// </summary>
/// <remarks>
/// <para>
/// On that run the test is skipped with the reason; everywhere else it runs as usual. The
/// reason is also carried as a trait named <see cref="TraitName"/>, so the tests that stand
/// aside can be listed and counted without running anything, and a growing number is visible
/// rather than quietly accepted.
/// </para>
/// <para>
/// For a test that exercises something only the operating system has: a raw handle, which
/// backend the process chose, a child process, the host's own paths. It is not for a test that
/// fails in memory because the in-memory backend answers differently from the disk. That is a
/// fault in the backend, and the fix belongs there.
/// </para>
/// <para>
/// Inert where the test framework is the ahead-of-time build, which supports neither kind of
/// attribute this is built from. Nothing built that way takes the in-memory leg.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
#if CAP_XUNIT_AOT
internal sealed class NotInMemoryAttribute(string reason) : Attribute
{
    /// <summary>Why the test has no meaning in memory.</summary>
    public string Reason { get; } = reason;
}
#else
internal sealed class NotInMemoryAttribute(string reason) : BeforeAfterTestAttribute, ITraitAttribute
{
    /// <summary>The trait the reason is recorded under.</summary>
    public const string TraitName = "NotInMemory";

    /// <summary>Why the test has no meaning in memory.</summary>
    public string Reason { get; } = reason;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(TraitName, Reason)];

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (HostTree.InMemory)
        {
            Assert.Skip($"Not run in memory: {Reason}");
        }
    }
}
#endif
