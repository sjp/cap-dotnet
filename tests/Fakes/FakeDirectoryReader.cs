using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Tests.Fakes;

/// <summary>
/// Reads the entries of a directory in the simulated filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Takes a copy of the entry list when it is created. A real enumeration promises less than
/// that — an entry added or removed while it runs may or may not appear — and a copy is one
/// of the outcomes that promise allows, so a test written against the contract passes here
/// and a test that assumed a stronger one fails against the real thing instead of here.
/// </para>
/// <para>
/// The entry whose kind the simulation declines to report is the interesting case. Several
/// real filesystems answer a directory read without saying what each entry is, and the only
/// way to exercise the lookup that covers for them is to have something that can be told to
/// behave that way on demand.
/// </para>
/// </remarks>
internal sealed class FakeDirectoryReader : DirectoryReader
{
    private readonly FakeNode _directory;
    private readonly KeyValuePair<string, FakeNode>[] _entries;
    private int _index;
    private string _name = string.Empty;

    public FakeDirectoryReader(FakeNode directory)
    {
        _directory = directory;
        _entries = [.. directory.Entries];
    }

    /// <summary>Nothing to close: no handle was opened to read a dictionary.</summary>
    public override void Dispose()
    {
    }

    /// <inheritdoc/>
    protected override CapError ReadCore(out bool advanced)
    {
        advanced = false;
        if (_index >= _entries.Length)
        {
            return CapError.Success;
        }

        (string name, FakeNode node) = _entries[_index++];
        _name = name;

        SetCurrent(name, node.HidesKindFromDirectoryRead ? CapFileType.Unknown : node.FileType, node.NodeId);
        advanced = true;
        return CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Looked up now rather than read from the copy, which is what the real backends do: the
    /// lookup happens after the buffer was filled, so an entry that has since been removed
    /// is reported as being of no known kind rather than as whatever it used to be.
    /// </remarks>
    protected override CapFileType Classify() =>
        _directory.Entries.TryGetValue(_name, out FakeNode? node) ? node.FileType : CapFileType.Unknown;
}
