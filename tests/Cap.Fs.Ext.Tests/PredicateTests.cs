namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Asking what a name holds, without throwing when it holds nothing.
/// </summary>
/// <remarks>
/// The case worth a test of its own is the link, because it is where the obvious
/// implementation and the correct one differ: a question answered by following the link says a
/// link to a directory is a directory, and code that then treats the name as a directory has
/// been told something about somewhere else.
/// </remarks>
public sealed class PredicateTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public PredicateTests()
    {
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "report.txt"), "contents");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "folder"));
        HostDirectory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "to-folder"), "folder");
        HostDirectory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "to-file"), "report.txt");
        HostDirectory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "to-nothing"), "absent");
    }

    public void Dispose() => _tree.Dispose();

    /// <summary>Each kind answers true to its own question and false to the others.</summary>
    [Theory]
    [InlineData("report.txt", false, true, false)]
    [InlineData("folder", true, false, false)]
    [InlineData("to-folder", false, false, true)]
    [InlineData("to-file", false, false, true)]
    [InlineData("to-nothing", false, false, true)]
    [InlineData("absent", false, false, false)]
    public void Each_question_is_about_the_name_and_not_its_target(
        string name,
        bool isDir,
        bool isFile,
        bool isSymlink)
    {
        Assert.Equal(isDir, _tree.Directory.IsDir(name));
        Assert.Equal(isFile, _tree.Directory.IsFile(name));
        Assert.Equal(isSymlink, _tree.Directory.IsSymlink(name));
    }

    /// <summary>A name that cannot be reached at all answers false rather than throwing.</summary>
    /// <remarks>
    /// Including a name that leaves the handle's authority. Answering false rather than
    /// refusing is right here and only here: the question is whether this handle's subtree
    /// holds such a thing, and a name outside it does not, whatever exists there.
    /// </remarks>
    [Fact]
    public void A_name_outside_the_handle_answers_false()
    {
        Assert.False(_tree.Directory.IsDir(Path.Combine("..", "..")));
        Assert.False(_tree.Directory.IsFile(Path.Combine("to-nothing", "beyond")));
    }

    /// <summary>A path of several components is resolved the way every other path is.</summary>
    [Fact]
    public void A_longer_path_is_resolved_the_usual_way()
    {
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "folder", "inner.txt"), "contents");

        Assert.True(_tree.Directory.IsFile(Path.Combine("folder", "inner.txt")));
        Assert.True(_tree.Directory.IsFile(Path.Combine("to-folder", "inner.txt")));
    }
}
