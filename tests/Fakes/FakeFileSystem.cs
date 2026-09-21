using Cap.Primitives.Interop;

namespace Cap.Tests.Fakes;

/// <summary>
/// A filesystem in memory, built to be attacked.
/// </summary>
/// <remarks>
/// <para>
/// This is not a convenience for avoiding temporary directories. It exists so that the
/// resolution logic can be tested against a filesystem that changes at a chosen moment
/// rather than at a lucky one. <see cref="BeforeLookup"/> runs immediately before each name
/// is looked up, which lets a test replace a directory with a symbolic link between two
/// steps of a walk — deterministically, every run, on every platform, including the ones
/// where the real kernel makes that race impossible.
/// </para>
/// <para>
/// It models the three things resolution has to reason about beyond plain names: links,
/// which redirect; volume boundaries, which mark where an unrelated filesystem has been
/// grafted in; and reparse tags that are not links at all, which must never be followed.
/// </para>
/// <para>
/// Paths in the building methods use <c>/</c> as a separator on every platform. They are
/// test scaffolding, not input to anything under test.
/// </para>
/// </remarks>
internal sealed class FakeFileSystem
{
    private ulong _nextNodeId = 1;

    /// <summary>The root of the simulated filesystem.</summary>
    public FakeNode Root { get; }

    /// <summary>Creates a simulated filesystem with an empty root directory.</summary>
    public FakeFileSystem()
    {
        Root = new FakeNode { Type = CapNodeType.Directory, VolumeId = 1, NodeId = _nextNodeId++ };
    }

    /// <summary>
    /// Runs immediately before a name is resolved, with the directory being searched and the
    /// name being sought.
    /// </summary>
    /// <remarks>
    /// The hook is what makes a race a test rather than a coin toss. A handler that renames
    /// or replaces an entry here is doing exactly what a concurrent attacker does, at the one
    /// instant where doing it matters.
    /// </remarks>
    public Action<FakeNode, string>? BeforeLookup { get; set; }

    /// <summary>Whether the simulated platform offers a confined, atomic open.</summary>
    /// <remarks>
    /// Settable so that the same resolution logic can be driven down both paths without
    /// needing two machines. A test that only ever ran against one of them would leave the
    /// other's behaviour unasserted on every build agent that lacks it.
    /// </remarks>
    public bool SupportsConfinedOpen { get; set; }

    /// <summary>Creates a directory, and any missing directories above it.</summary>
    public FakeNode AddDirectory(string path) => Create(path, CapNodeType.Directory, null, 0);

    /// <summary>Creates a file, and any missing directories above it.</summary>
    public FakeNode AddFile(string path) => Create(path, CapNodeType.File, null, 0);

    /// <summary>Creates a symbolic link with the given stored target.</summary>
    public FakeNode AddSymbolicLink(string path, string target) =>
        Create(path, CapNodeType.SymbolicLink, target, 0);

    /// <summary>
    /// Creates a reparse point whose tag is not a filesystem link, which resolution must
    /// refuse rather than interpret.
    /// </summary>
    public FakeNode AddOpaqueReparsePoint(string path, uint tag) =>
        Create(path, CapNodeType.UnknownReparsePoint, null, tag);

    /// <summary>
    /// Creates a directory on a different simulated volume, as a mount point would be.
    /// </summary>
    public FakeNode AddMountPoint(string path, ulong volumeId)
    {
        FakeNode node = AddDirectory(path);
        node.VolumeId = volumeId;
        return node;
    }

    /// <summary>Finds an existing node, or returns null.</summary>
    public FakeNode? Find(string path)
    {
        FakeNode current = Root;
        foreach (string component in Split(path))
        {
            if (!current.Entries.TryGetValue(component, out FakeNode? next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Removes an entry from its parent directory.</summary>
    public void Remove(string path)
    {
        string[] components = Split(path);
        FakeNode parent = Walk(components[..^1], create: false)
            ?? throw new InvalidOperationException($"No directory above '{path}'.");
        _ = parent.Entries.Remove(components[^1]);
    }

    /// <summary>Replaces an entry, keeping its name. Used to spring a swap from a lookup hook.</summary>
    public void Replace(string path, FakeNode node)
    {
        string[] components = Split(path);
        FakeNode parent = Walk(components[..^1], create: false)
            ?? throw new InvalidOperationException($"No directory above '{path}'.");
        parent.Entries[components[^1]] = node;
    }

    /// <summary>Allocates an identity for a node created by a test directly.</summary>
    public ulong NextNodeId() => _nextNodeId++;

    /// <summary>Looks one name up in one directory, announcing it first.</summary>
    internal FakeNode? Lookup(FakeNode directory, string name)
    {
        BeforeLookup?.Invoke(directory, name);
        return directory.Entries.TryGetValue(name, out FakeNode? node) ? node : null;
    }

    private FakeNode Create(string path, CapNodeType type, string? target, uint tag)
    {
        string[] components = Split(path);
        if (components.Length == 0)
        {
            throw new ArgumentException("A path must name something.", nameof(path));
        }

        FakeNode parent = Walk(components[..^1], create: true)!;
        FakeNode node = new()
        {
            Type = type,
            VolumeId = parent.VolumeId,
            NodeId = _nextNodeId++,
            LinkTarget = target,
            ReparseTag = tag,
        };

        parent.Entries[components[^1]] = node;
        return node;
    }

    private FakeNode? Walk(string[] components, bool create)
    {
        FakeNode current = Root;
        foreach (string component in components)
        {
            if (!current.Entries.TryGetValue(component, out FakeNode? next))
            {
                if (!create)
                {
                    return null;
                }

                next = new FakeNode
                {
                    Type = CapNodeType.Directory,
                    VolumeId = current.VolumeId,
                    NodeId = _nextNodeId++,
                };
                current.Entries[component] = next;
            }

            current = next;
        }

        return current;
    }

    private static string[] Split(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
