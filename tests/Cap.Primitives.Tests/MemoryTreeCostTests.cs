using System.Runtime.CompilerServices;
using Cap.Primitives.Interop;
using Cap.Std.Testing;

namespace Cap.Primitives.Tests;

/// <summary>
/// What the in-memory tree costs to resolve through and to describe.
/// </summary>
/// <remarks>
/// The in-memory filesystem stands in for the disk in whole test suites, so a walk runs for
/// every path they open and a directory is described for every stat they make. Both are
/// asserted here rather than left to a benchmark, as <see cref="CapPathAllocationTests"/>
/// asserts parsing, so that a regression fails on the commit that caused it.
/// </remarks>
public sealed class MemoryTreeCostTests
{
    private static readonly Func<MemoryNode, ReadOnlySpan<char>, MemoryNode?> Lookup =
        static (directory, name) => directory.Entries.GetValueOrDefault(name);

    [Theory]
    [InlineData(CapPathSyntax.Unix)]
    [InlineData(CapPathSyntax.Windows)]
    public void A_walk_through_directories_links_and_dot_dot_allocates_nothing(CapPathSyntax syntax)
    {
        MemoryNode root = Tree(syntax, out string path);

        // Warm up first, so the jitting and the pool's first array are not what is measured.
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(CapError.Success, Walk(root, path, syntax));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = Walk(root, path, syntax);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void The_subdirectory_count_follows_every_change_to_the_entries()
    {
        MemoryNode directory = new();
        MemoryNode file = new() { Type = CapNodeType.File };

        directory.Entries["a"] = new MemoryNode();
        directory.Entries["b"] = new MemoryNode();
        directory.Entries["f"] = file;
        Assert.Equal(2, directory.Entries.SubdirectoryCount);

        // A name pointed at something else gives up what it held before.
        directory.Entries["a"] = file;
        Assert.Equal(1, directory.Entries.SubdirectoryCount);
        directory.Entries["f"] = new MemoryNode();
        Assert.Equal(2, directory.Entries.SubdirectoryCount);

        Assert.True(directory.Entries.Remove("b"));
        Assert.False(directory.Entries.Remove("b"));
        Assert.Equal(1, directory.Entries.SubdirectoryCount);
        Assert.True(directory.Entries.Remove("f"));
        Assert.Equal(0, directory.Entries.SubdirectoryCount);
        Assert.Equal(["a"], directory.Entries.Keys);
    }

    [Fact]
    public void A_span_lookup_compares_names_as_the_directory_does()
    {
        MemoryNode exact = new();
        MemoryNode ignoringCase = new(StringComparer.OrdinalIgnoreCase);
        MemoryNode child = new();
        exact.Entries["Name"] = child;
        ignoringCase.Entries["Name"] = child;

        Assert.Same(child, exact.Entries.GetValueOrDefault("xName".AsSpan(1)));
        Assert.Null(exact.Entries.GetValueOrDefault("xNAME".AsSpan(1)));
        Assert.Same(child, ignoringCase.Entries.GetValueOrDefault("xNAME".AsSpan(1)));
    }

    /// <summary>
    /// <c>a/b/c/d/e/f/g/h</c>, with a link in <c>h</c> that leads back to <c>h</c> through
    /// its parent, and a path through all of it that ends in <c>.</c> so that no final name is
    /// reported. Under Windows rules the directories ignore case and the path is spelled in
    /// the other case and with the other separator.
    /// </summary>
    private static MemoryNode Tree(CapPathSyntax syntax, out string path)
    {
        StringComparer names = syntax == CapPathSyntax.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        MemoryNode root = new(names);
        MemoryNode current = root;
        foreach (string name in new[] { "a", "b", "c", "d", "e", "f", "g", "h" })
        {
            MemoryNode next = new(names);
            current.Entries[name] = next;
            current = next;
        }

        current.Entries["back"] = new MemoryNode { Type = CapNodeType.SymbolicLink, LinkTarget = "../h" };
        path = syntax == CapPathSyntax.Windows
            ? @"A\B\C\D\E\F\G\H\BACK\..\H\."
            : "a/b/c/d/e/f/g/h/back/../h/.";
        return root;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CapError Walk(MemoryNode root, string path, CapPathSyntax syntax) =>
        MemoryPathWalk.Resolve(root, path, syntax, ConfinedResolveOptions.None, followFinalLink: true, Lookup, out _);
}
