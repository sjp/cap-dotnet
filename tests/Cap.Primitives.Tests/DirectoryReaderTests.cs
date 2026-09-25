using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Tests.Fakes;

namespace Cap.Primitives.Tests;

/// <summary>
/// The platform layer's directory reader.
/// </summary>
/// <remarks>
/// <para>
/// What belongs here rather than at the public level is everything that is about the
/// backends agreeing with each other: that a name the filesystem declined to describe gets
/// looked up, that a handle carrying no authority to list a directory cannot list it, and
/// that the two names every directory keeps for itself are dropped whether or not the
/// platform reported them.
/// </para>
/// <para>
/// The case that cannot be built on a real filesystem is the interesting one. Several
/// filesystems answer a directory read without saying what each entry is, and whether one is
/// mounted on the machine running the tests is not something a test can arrange — so the
/// simulation can be told to behave that way, and the lookup that covers for it is exercised
/// on every platform instead of on whichever agent happened to have the right filesystem.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class DirectoryReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-read-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IPlatformOps Ops => PlatformOps.Host;

    /// <summary>
    /// The tag of an application execution alias: something that redirects, by a mechanism
    /// that has nothing to do with paths.
    /// </summary>
    private const uint AppExecutionAliasTag = 0x8000001B;

    // --- against the host ---------------------------------------------------------------------

    /// <summary>A handle opened only to resolve names beneath a directory cannot list it.</summary>
    /// <remarks>
    /// <para>
    /// The property that makes a traversal-only handle worth having. Its whole point is that
    /// it is a position in the tree and not a list of names, and the reader is the one place
    /// that distinction could be given away — every platform here reaches the entries by
    /// opening the directory a second time, and an implementation that asked for read access
    /// on that open would promote the handle it was derived from.
    /// </para>
    /// <para>
    /// The refusal is this layer's own and not the filesystem's. The directory in this test
    /// is perfectly readable; what is being refused is reading it <em>through this handle</em>.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_traversal_only_handle_cannot_read_the_directory()
    {
        File.WriteAllText(Path.Combine(_root, "entry"), "x");

        using SafeDirHandle anchor = OpenRoot(CapAccess.None);

        CapResult<DirectoryReader> refused = Ops.OpenDirectoryReader(anchor);

        Assert.False(refused.IsSuccess);
        Assert.Equal(CapErrorCategory.PermissionDenied, refused.Error.Category);

        // And the same directory through a handle that was granted the authority.
        using SafeDirHandle readable = OpenRoot(CapAccess.Read);
        Assert.Equal(["entry"], Names(readable));
    }

    /// <summary>The two names a directory keeps for itself are never reported.</summary>
    /// <remarks>
    /// Asserted against the host as well as against the simulation because this is exactly
    /// where the platforms differ: some include them in what they hand back and some do not,
    /// and the skipping is done once above all of them so that the answer cannot depend on
    /// which one is running.
    /// </remarks>
    [Fact]
    public void The_names_a_directory_keeps_for_itself_are_dropped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));

        using SafeDirHandle root = OpenRoot(CapAccess.Read);

        Assert.Equal(["child"], Names(root));
    }

    /// <summary>A directory larger than one bufferful is read to its end.</summary>
    /// <remarks>
    /// The refill is the part of every backend most likely to be subtly wrong — a record
    /// straddling the end of a buffer, an offset not reset, a scan restarted instead of
    /// continued — and none of those show up until there is more than one bufferful to read.
    /// </remarks>
    [Fact]
    public void A_directory_that_does_not_fit_in_one_buffer_is_read_to_its_end()
    {
        HashSet<string> expected = [];
        for (int i = 0; i < 3000; i++)
        {
            string name = $"entry-with-a-name-of-some-length-{i}";
            File.WriteAllText(Path.Combine(_root, name), "x");
            expected.Add(name);
        }

        using SafeDirHandle root = OpenRoot(CapAccess.Read);

        Assert.Equal(expected, Names(root).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Asked to disregard what the filesystem said, the reader looks every entry up and gets
    /// the same answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lookup exists for filesystems that answer a directory read without saying what
    /// each entry is. Whether one of those is mounted on the machine running the tests is not
    /// something a test can arrange, so the switch that makes the reader distrust the answer
    /// is what puts the lookup on the path here — the same switch a caller reaches for on a
    /// filesystem whose answers have turned out to be wrong.
    /// </para>
    /// <para>
    /// Proving the switch was honoured needs more than matching answers, which is what an
    /// ignored switch would also produce. So the entry is removed after the directory has
    /// been read and before its kind is settled: looked up, it is of no known kind, and taken
    /// from the read it is still a file. The two runs therefore disagree, and only if the
    /// lookup really happened.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_entry_can_be_made_to_have_its_kind_looked_up()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The directory read on this platform always reports the kind; there is no lookup to force.");
        }

        if (Environment.GetEnvironmentVariable(UnixFileTypes.AlwaysLookUpKindVariableName) is not null)
        {
            Assert.Skip("The lookup is already forced for this whole run, so the two halves cannot differ.");
        }

        Assert.Equal(CapFileType.File, KindOfAnEntryRemovedAfterTheRead(lookUp: false));
        Assert.Equal(CapFileType.Unknown, KindOfAnEntryRemovedAfterTheRead(lookUp: true));
    }

    /// <summary>
    /// Each entry's identity is read from the directory record and agrees with a description
    /// of the name.
    /// </summary>
    /// <remarks>
    /// Checked at this layer because each backend reads the identifier out of a record it
    /// lays out by hand, at an offset of its own; a wrong offset reads some other field, which
    /// is a number like any other and fails nothing until it is compared.
    /// </remarks>
    [Fact]
    public void An_entry_carries_the_identity_a_description_reports()
    {
        File.WriteAllText(Path.Combine(_root, "file"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "directory"));

        using SafeDirHandle root = OpenRoot(CapAccess.Read);
        CapResult<DirectoryReader> opened = Ops.OpenDirectoryReader(root);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using DirectoryReader reader = opened.Value;
        int seen = 0;
        while (true)
        {
            Assert.True(reader.Read(out bool advanced).IsSuccess);
            if (!advanced)
            {
                break;
            }

            Assert.True(Ops.DescribeChild(root, reader.CurrentName, out CapNodeStat stat).IsSuccess);
            Assert.Equal(stat.NodeId, reader.CurrentNodeId);
            Assert.NotEqual(UInt128.Zero, reader.CurrentNodeId);
            seen++;
        }

        Assert.Equal(2, seen);
    }

    /// <summary>
    /// Reads a directory of two files, removes the one not yet reported, and answers what the
    /// second entry's kind came back as.
    /// </summary>
    private CapFileType KindOfAnEntryRemovedAfterTheRead(bool lookUp)
    {
        foreach (string name in (string[])["first", "second"])
        {
            File.WriteAllText(Path.Combine(_root, name), "x");
        }

        bool previous = AppContext.TryGetSwitch(UnixFileTypes.AlwaysLookUpKindSwitchName, out bool wasSet) && wasSet;
        AppContext.SetSwitch(UnixFileTypes.AlwaysLookUpKindSwitchName, lookUp);

        try
        {
            using SafeDirHandle root = OpenRoot(CapAccess.Read);
            CapResult<DirectoryReader> opened = Ops.OpenDirectoryReader(root);
            Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

            using DirectoryReader reader = opened.Value;

            // The first read fills the buffer, so both names are already in hand and the
            // second one will be reported from it whatever happens to the directory now.
            Assert.True(reader.Read(out bool advanced).IsSuccess);
            Assert.True(advanced);
            string reported = reader.CurrentName.ToString();

            // Still there, so both runs must call it a file -- which in the forced run is
            // the lookup's own answer and is what proves the lookup reads the kind correctly
            // and not merely that it ran.
            Assert.Equal(CapFileType.File, reader.CurrentType);

            string remaining = reported == "first" ? "second" : "first";
            File.Delete(Path.Combine(_root, remaining));

            Assert.True(reader.Read(out advanced).IsSuccess);
            Assert.True(advanced);
            Assert.Equal(remaining, reader.CurrentName.ToString());

            return reader.CurrentType;
        }
        finally
        {
            AppContext.SetSwitch(UnixFileTypes.AlwaysLookUpKindSwitchName, previous);
            File.Delete(Path.Combine(_root, "first"));
            File.Delete(Path.Combine(_root, "second"));
        }
    }

    // --- against the simulation -----------------------------------------------------------------

    /// <summary>
    /// A filesystem that does not say what an entry is has the entry looked up instead.
    /// </summary>
    [Fact]
    public void An_entry_whose_kind_the_filesystem_withholds_is_looked_up()
    {
        FakeFileSystem fs = new();
        fs.AddFile("told");
        fs.AddFile("withheld").HidesKindFromDirectoryRead = true;

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenSimulatedRoot(ops);

        Dictionary<string, CapFileType> kinds = Kinds(ops, root);

        Assert.Equal(CapFileType.File, kinds["told"]);
        Assert.Equal(CapFileType.File, kinds["withheld"]);
    }

    /// <summary>
    /// An entry that has gone by the time it is looked up is of no known kind rather than a
    /// failure.
    /// </summary>
    /// <remarks>
    /// A directory something else is writing to is the ordinary case, not the exotic one. An
    /// entry read and then removed before its kind could be established must not make the
    /// rest of the directory unreadable, and reporting the kind as unknown is the only honest
    /// answer left — the alternative is to say what it used to be.
    /// </remarks>
    [Fact]
    public void An_entry_that_vanishes_before_it_is_looked_up_is_of_no_known_kind()
    {
        FakeFileSystem fs = new();
        fs.AddFile("fleeting").HidesKindFromDirectoryRead = true;

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenSimulatedRoot(ops);

        CapResult<DirectoryReader> opened = ops.OpenDirectoryReader(root);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using DirectoryReader reader = opened.Value;
        fs.Root.Entries.Remove("fleeting");

        Assert.True(reader.Read(out bool advanced).IsSuccess);
        Assert.True(advanced);
        Assert.Equal("fleeting", reader.CurrentName.ToString());
        Assert.Equal(CapFileType.Unknown, reader.CurrentType);
    }

    /// <summary>
    /// The kinds a walk has no use for are still told apart for a caller reading a directory.
    /// </summary>
    /// <remarks>
    /// Resolution collapses sockets, pipes and device nodes into a single case because the
    /// only question it asks is whether a path can continue through them, and the answer is
    /// no for all of them. A caller listing a directory is deciding what to do with the entry
    /// instead, and the difference between a pipe and a socket decides it.
    /// </remarks>
    [Fact]
    public void The_kinds_resolution_collapses_are_reported_separately()
    {
        FakeFileSystem fs = new();

        foreach ((string name, CapFileType kind) in new[]
        {
            ("socket", CapFileType.Socket),
            ("pipe", CapFileType.Fifo),
            ("character-device", CapFileType.CharDevice),
            ("block-device", CapFileType.BlockDevice),
        })
        {
            FakeNode node = fs.AddFile(name);
            node.Type = CapNodeType.Other;
            node.EntryType = kind;
        }

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenSimulatedRoot(ops);

        Dictionary<string, CapFileType> kinds = Kinds(ops, root);

        Assert.Equal(CapFileType.Socket, kinds["socket"]);
        Assert.Equal(CapFileType.Fifo, kinds["pipe"]);
        Assert.Equal(CapFileType.CharDevice, kinds["character-device"]);
        Assert.Equal(CapFileType.BlockDevice, kinds["block-device"]);
    }

    /// <summary>
    /// Something that redirects by a mechanism this library does not read is not reported as
    /// a link.
    /// </summary>
    /// <remarks>
    /// The distinction exists because only one of the two holds something that can be read as
    /// a path. Collapsing them at the point a caller is told what an entry is would invite
    /// exactly the mistake the resolution layer takes care never to make: reading a structure
    /// of unknown shape as if it named a destination.
    /// </remarks>
    [Fact]
    public void A_redirection_that_is_not_a_link_is_not_reported_as_one()
    {
        FakeFileSystem fs = new();
        fs.AddSymbolicLink("link", "elsewhere");
        fs.AddOpaqueReparsePoint("alias", AppExecutionAliasTag);

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenSimulatedRoot(ops);

        Dictionary<string, CapFileType> kinds = Kinds(ops, root);

        Assert.Equal(CapFileType.Symlink, kinds["link"]);
        Assert.Equal(CapFileType.ReparsePoint, kinds["alias"]);
    }

    // --- helpers -------------------------------------------------------------------------------

    private static List<string> Names(SafeDirHandle directory) => Names(Ops, directory);

    private static List<string> Names(IPlatformOps ops, SafeDirHandle directory)
    {
        List<string> names = [];
        CapResult<DirectoryReader> opened = ops.OpenDirectoryReader(directory);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using DirectoryReader reader = opened.Value;
        while (true)
        {
            CapError error = reader.Read(out bool advanced);
            Assert.True(error.IsSuccess, error.FailureDescription);
            if (!advanced)
            {
                return names;
            }

            names.Add(reader.CurrentName.ToString());
        }
    }

    /// <summary>
    /// Reads a simulated directory, answering what each entry was reported as.
    /// </summary>
    /// <remarks>
    /// Takes the simulation rather than the platform slot because every caller is a
    /// simulation case: what an entry is reported as on a real filesystem is settled at the
    /// public level, where the tree can be built with the framework's own calls.
    /// </remarks>
    private static Dictionary<string, CapFileType> Kinds(FakePlatformOps ops, SafeDirHandle directory)
    {
        Dictionary<string, CapFileType> kinds = new(StringComparer.Ordinal);
        CapResult<DirectoryReader> opened = ops.OpenDirectoryReader(directory);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using DirectoryReader reader = opened.Value;
        while (true)
        {
            CapError error = reader.Read(out bool advanced);
            Assert.True(error.IsSuccess, error.FailureDescription);
            if (!advanced)
            {
                return kinds;
            }

            kinds.Add(reader.CurrentName.ToString(), reader.CurrentType);
        }
    }

    /// <summary>The root of a simulated filesystem, which has no path to be named by.</summary>
    private static SafeDirHandle OpenSimulatedRoot(FakePlatformOps ops)
    {
        CapResult<SafeDirHandle> result = ops.OpenAmbientDirectory(string.Empty, CapAccess.Read);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value;
    }

    private SafeDirHandle OpenRoot(CapAccess access)
    {
        CapResult<SafeDirHandle> result = Ops.OpenAmbientDirectory(_root, access);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value;
    }
}
