using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Reading a directory through a handle.
/// </summary>
/// <remarks>
/// <para>
/// Two properties matter more than the rest and most of what is here is one of them.
/// </para>
/// <para>
/// The first is that nothing path-shaped comes back. An entry carries one component and the
/// handle it was read through, and that is the whole of what a caller can do with it — so
/// the tests insist on what is <em>absent</em> as much as on what is there, which is the only
/// way to test an API for a temptation it declines to offer.
/// </para>
/// <para>
/// The second is that an entry describes the name and not what the name leads to. A link is
/// reported as a link whatever it points at, the kind that comes back is a snapshot of when
/// the directory was read, and opening from an entry resolves the name again from scratch.
/// A caller who cleans up an untrusted subtree depends on all three.
/// </para>
/// <para>
/// Set-up uses the ambient filesystem deliberately: the tree being read has to be built by
/// something that is not the code under test.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirEnumerationTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    /// <summary>A name planted as raw bytes, which the framework's own cleanup cannot remove.</summary>
    private byte[]? _rawName;

    public void Dispose()
    {
        // Removed the way it was planted, because a name that is not valid text cannot be
        // named to the framework at all -- which is the very thing the test that plants one
        // is about, and it applies as much to tidying up afterwards.
        if (_rawName is not null)
        {
            HostFile.DeleteRawName(_tree.HostPath, _rawName);
        }

        _tree.Dispose();
    }

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    // --- what comes back --------------------------------------------------------------------

    /// <summary>Every name in the directory is reported, once each.</summary>
    [Fact]
    public void Every_entry_is_reported()
    {
        HostFile.WriteAllText(Host("one"), "1");
        HostFile.WriteAllText(Host("two"), "2");
        HostDirectory.CreateDirectory(Host("three"));

        using Dir root = OpenRoot();

        string[] names = [.. root.EnumerateEntries().Select(entry => entry.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["one", "three", "two"], names);
    }

    /// <summary>An empty directory reports nothing at all.</summary>
    /// <remarks>
    /// Worth its own case because it is not the same as a directory that is empty of
    /// interesting things: every platform stores the two names a directory has for itself
    /// and for the one above it, and those are skipped everywhere. A backend that forgot
    /// would report two entries here and would go unnoticed in every other test, which would
    /// simply see two extra names among many.
    /// </remarks>
    [Fact]
    public void An_empty_directory_reports_nothing()
    {
        using Dir root = OpenRoot();

        Assert.Empty(root.EnumerateEntries());
    }

    /// <summary>The names a directory holds for itself and for its parent are never reported.</summary>
    /// <remarks>
    /// The second of them is the one that matters: it names the directory above, which is
    /// outside what the handle covers, and an enumeration that handed it back would be
    /// offering a caller the one name the whole design refuses to resolve.
    /// </remarks>
    [Fact]
    public void The_directory_does_not_report_itself_or_its_parent()
    {
        HostFile.WriteAllText(Host("real"), "x");

        using Dir root = OpenRoot();

        Assert.DoesNotContain(root.EnumerateEntries(), entry => entry.Name is "." or "..");
        Assert.Single(root.EnumerateEntries());
    }

    /// <summary>A file is reported as a file and a directory as a directory.</summary>
    [Fact]
    public void An_entry_reports_what_it_is()
    {
        HostFile.WriteAllText(Host("plain"), "x");
        HostDirectory.CreateDirectory(Host("folder"));

        using Dir root = OpenRoot();
        Dictionary<string, CapFileType> kinds = Kinds(root);

        Assert.Equal(CapFileType.File, kinds["plain"]);
        Assert.Equal(CapFileType.Directory, kinds["folder"]);
    }

    /// <summary>Each entry carries the identity a description of its name reports.</summary>
    [Fact]
    public void An_entry_carries_the_identity_of_what_it_names()
    {
        HostFile.WriteAllText(Host("plain"), "x");
        HostDirectory.CreateDirectory(Host("folder"));

        using Dir root = OpenRoot();
        Dictionary<string, CapFileId> identities = Identities(root);

        Assert.Equal(root.GetMetadata("plain").FileId, identities["plain"]);
        Assert.Equal(root.GetMetadata("folder").FileId, identities["folder"]);
        Assert.NotEqual(identities["plain"], identities["folder"]);
    }

    /// <summary>An entry holding a symbolic link identifies the link, not its target.</summary>
    /// <remarks>
    /// The case that tells the directory's own record apart from a lookup that followed the
    /// link: the link and its target are different objects.
    /// </remarks>
    [Fact]
    public void An_entry_for_a_link_identifies_the_link()
    {
        LinkSupport.RequireSymbolicLinks(_tree.HostPath);

        HostFile.WriteAllText(Host("plain"), "x");
        HostFile.CreateSymbolicLink(Host("to-plain"), "plain");

        using Dir root = OpenRoot();
        Dictionary<string, CapFileId> identities = Identities(root);

        Assert.Equal(root.GetMetadata("to-plain").FileId, identities["to-plain"]);
        Assert.NotEqual(identities["plain"], identities["to-plain"]);
    }

    /// <summary>Two names for one file are two entries with one identity.</summary>
    [Fact]
    public void Hard_links_are_two_entries_with_one_identity()
    {
        HostFile.WriteAllText(Host("first"), "shared");
        HostFile.WriteAllText(Host("other"), "shared");

        using Dir root = OpenRoot();

        if (!root.TryCreateHardLink("first", root, "second"))
        {
            Assert.Skip("A second name for one file could not be created here.");
        }

        Dictionary<string, CapFileId> identities = Identities(root);

        Assert.Equal(identities["first"], identities["second"]);
        Assert.NotEqual(identities["first"], identities["other"]);
    }

    /// <summary>A link to a directory is reported as a link, not as a directory.</summary>
    /// <remarks>
    /// The single most important answer this API gives. A caller walking a tree decides
    /// whether to descend from what an entry says it is, and a link reported as a directory
    /// is an invitation to descend through it — which for a link pointing out of the subtree
    /// is the walk leaving the sandbox by its own choice. Reporting the target's kind would
    /// also mean following the link in order to answer, before any policy had been consulted.
    /// </remarks>
    [Fact]
    public void A_link_to_a_directory_is_reported_as_a_link()
    {
        LinkSupport.RequireSymbolicLinks(_tree.HostPath);

        HostDirectory.CreateDirectory(Host("folder"));
        HostDirectory.CreateSymbolicLink(Host("to-folder"), "folder");
        HostFile.CreateSymbolicLink(Host("to-file"), "missing");

        using Dir root = OpenRoot();
        Dictionary<string, CapFileType> kinds = Kinds(root);

        Assert.Equal(CapFileType.Symlink, kinds["to-folder"]);
        Assert.Equal(CapFileType.Symlink, kinds["to-file"]);
    }

    /// <summary>A named pipe is reported as one rather than as a file.</summary>
    /// <remarks>
    /// The kinds that are neither a file nor a directory are worth one case between them.
    /// They are reachable inside a sandbox and nameable, and a caller that treated one as an
    /// ordinary file would block a thread for ever the first time it opened one.
    /// </remarks>
    [Fact]
    public void A_pipe_is_reported_as_a_pipe()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A named pipe is not an entry of a directory on this platform.");
        }

        HostFile.CreateFifo(Host("pipe"));

        using Dir root = OpenRoot();

        Assert.Equal(CapFileType.Fifo, Kinds(root)["pipe"]);
    }

    // --- names that are not text ---------------------------------------------------------------

    /// <summary>
    /// A name that is not valid UTF-8 is reported, and the rest of the directory with it.
    /// </summary>
    /// <remarks>
    /// A filename on these systems is a sequence of bytes, and on a filesystem that has been
    /// around long enough some of them are not text. The failure this guards against is not
    /// that such a name comes back wrong — it is that one such name makes its whole directory
    /// unusable, which is what happens when a decoder throws or when it substitutes a
    /// replacement character and the name can no longer be opened.
    /// </remarks>
    [Fact]
    public void A_name_that_is_not_valid_text_does_not_stop_the_enumeration()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Names are UTF-16 on this platform and cannot be ill-formed bytes.");
        }

        using Dir root = OpenRoot();

        if (OperatingSystem.IsMacOS())
        {
            // APFS and HFS+ store names as UTF-8 and refuse any that is not, so there is no
            // such entry to enumerate. What is pinned instead is that the refusal reads as the
            // caller's mistake rather than an unknown failure. "\udcff" is the escaped spelling
            // of the byte 0xFF.
            CapIOException refused = Assert.ThrowsAny<CapIOException>(
                () => root.OpenFile("b\udcffb", FileMode.CreateNew, FileAccess.Write).Dispose());
            Assert.Equal(CapErrorKind.InvalidArgument, refused.Kind);
            Assert.Empty(root.EnumerateEntries());
            return;
        }

        HostFile.WriteAllText(Host("ordinary"), "x");
        CreateRawName([0x62, 0xFF, 0x62]);

        DirEntry[] entries = [.. root.EnumerateEntries()];

        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, entry => entry.Name == "ordinary");

        DirEntry odd = entries.Single(entry => entry.Name != "ordinary");
        Assert.Equal(CapFileType.File, odd.Type);

        // The point of the escaping scheme: the name that came back opens the file it came
        // from. A decoder that replaced what it could not read would produce a string that
        // looks plausible and names nothing.
        Assert.True(odd.TryOpenFile(out CapFile? file));
        file!.Dispose();

        // And it travels back through the rest of the surface unchanged, which is the whole
        // of what the escaping scheme promises: a name this library hands out is a name this
        // library accepts.
        root.DeleteFile(odd.Name);
        Assert.Single(root.EnumerateEntries());
    }

    /// <summary>
    /// A name ending in a dot or a space, which the volume can hold but the parser refuses,
    /// is listed as stored and refused when it is handed back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The documented behaviour, asserted so that it does not change unnoticed in either
    /// direction. Hiding the name from the listing would leave a caller emptying the directory
    /// unable to see why it is not empty; accepting it back would open whatever the system's
    /// stripping made of it, which is the divergence the refusal exists to prevent.
    /// </para>
    /// <para>
    /// Planted through the extended-length prefix, which is the only way to make one: every
    /// other route strips the dot or the space before the name reaches the volume.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("dotted.")]
    [InlineData("spaced ")]
    [NotInMemory("Needs a name the host's own API would strip.")]
    public void A_name_ending_in_a_dot_or_space_is_listed_but_not_reopened(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows strips trailing dots and spaces.");
            return;
        }

        // Only the directory goes through normalisation: it would strip the name's own dot or space.
        string planted = @"\\?\" + Path.Join(Path.GetFullPath(Host()), name);
        File.WriteAllText(planted, "x");

        try
        {
            using Dir root = OpenRoot();

            DirEntry entry = Assert.Single(root.EnumerateEntries());
            Assert.Equal(name, entry.Name);
            Assert.Equal(CapFileType.File, entry.Type);

            Assert.False(entry.TryOpenFile(out CapFile? file));
            Assert.Null(file);
            Assert.Throws<ArgumentException>(() => entry.OpenFile().Dispose());
            Assert.Throws<ArgumentException>(() => root.OpenFile(name).Dispose());
            Assert.Throws<ArgumentException>(() => root.DeleteFile(name));
        }
        finally
        {
            File.Delete(planted);
        }
    }

    // --- acting on an entry ----------------------------------------------------------------------

    /// <summary>An entry opens the file it names.</summary>
    [Fact]
    public void An_entry_opens_the_file_it_names()
    {
        HostFile.WriteAllText(Host("content"), "the contents");

        using Dir root = OpenRoot();
        DirEntry entry = root.EnumerateEntries().Single();

        using CapFile file = entry.OpenFile();
        byte[] buffer = new byte[file.Length];
        file.Read(buffer, 0);

        Assert.Equal("the contents", System.Text.Encoding.UTF8.GetString(buffer));
    }

    /// <summary>An entry opens the directory it names, and that handle can be read in turn.</summary>
    /// <remarks>
    /// The shape every tree walk is built from: a handle, its entries, a handle derived from
    /// one of them. Nothing in it needs a path, which is the property that makes the walk
    /// confined without the walk having to do anything about it.
    /// </remarks>
    [Fact]
    public void An_entry_opens_the_directory_it_names()
    {
        HostDirectory.CreateDirectory(Host("folder"));
        HostFile.WriteAllText(Host("folder", "inner"), "x");

        using Dir root = OpenRoot();
        DirEntry entry = root.EnumerateEntries().Single();

        using Dir folder = entry.OpenDir();

        Assert.Equal("inner", folder.EnumerateEntries().Single().Name);
    }

    /// <summary>An entry removed after it was read fails to open rather than opening something else.</summary>
    /// <remarks>
    /// The reason opening from an entry resolves the name again instead of keeping something
    /// from the read. An entry is a description of the past; by the time it is acted on the
    /// name may hold nothing, or may hold something a writer put there in between, and
    /// either way the answer has to come from looking now.
    /// </remarks>
    [Fact]
    public void An_entry_that_has_gone_does_not_open()
    {
        HostFile.WriteAllText(Host("fleeting"), "x");

        using Dir root = OpenRoot();
        DirEntry entry = root.EnumerateEntries().Single();

        HostFile.Delete(Host("fleeting"));

        Assert.False(entry.TryOpenFile(out CapFile? file));
        Assert.Null(file);
        Assert.Throws<FileNotFoundException>(() => entry.OpenFile());
    }

    /// <summary>An entry whose kind has changed since the read fails at the open.</summary>
    /// <remarks>
    /// The remembered kind is not consulted. Checking it would answer from the state of the
    /// world when the directory was read, which is the state this whole type is careful not
    /// to act on; the open is attempted and the filesystem gives the current answer.
    /// </remarks>
    [Fact]
    public void An_entry_whose_kind_changed_is_refused_by_the_filesystem()
    {
        HostDirectory.CreateDirectory(Host("swapped"));

        using Dir root = OpenRoot();
        DirEntry entry = root.EnumerateEntries().Single();
        Assert.Equal(CapFileType.Directory, entry.Type);

        HostDirectory.Delete(Host("swapped"));
        HostFile.WriteAllText(Host("swapped"), "x");

        Assert.False(entry.TryOpenDir(out Dir? opened));
        Assert.Null(opened);
    }

    /// <summary>An entry that came from nowhere has nothing to open.</summary>
    [Fact]
    public void A_default_entry_has_no_authority()
    {
        DirEntry entry = default;

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(CapFileType.Unknown, entry.Type);
        Assert.Throws<InvalidOperationException>(() => entry.OpenDir());
        Assert.Throws<InvalidOperationException>(() => entry.OpenFile());
    }

    // --- the enumeration itself ---------------------------------------------------------------------

    /// <summary>Reading the same handle twice reads the whole directory twice.</summary>
    /// <remarks>
    /// Not a formality. The position a directory read advances belongs to the open object on
    /// every platform here, so an implementation that read through the caller's own handle,
    /// or through a duplicate of it, would hand the second enumeration whatever the first
    /// left behind — nothing, in this case, and no error.
    /// </remarks>
    [Fact]
    public void The_same_handle_can_be_read_twice()
    {
        Seed(20);

        using Dir root = OpenRoot();

        Assert.Equal(20, root.EnumerateEntries().Count());
        Assert.Equal(20, root.EnumerateEntries().Count());
    }

    /// <summary>Two enumerations of one handle running at once do not consume each other.</summary>
    [Fact]
    public void Two_enumerations_of_one_handle_are_independent()
    {
        Seed(20);

        using Dir root = OpenRoot();
        using IEnumerator<DirEntry> first = root.EnumerateEntries().GetEnumerator();
        using IEnumerator<DirEntry> second = root.EnumerateEntries().GetEnumerator();

        List<string> left = [];
        List<string> right = [];

        while (first.MoveNext() && second.MoveNext())
        {
            left.Add(first.Current.Name);
            right.Add(second.Current.Name);
        }

        Assert.Equal(20, left.Count);
        Assert.Equal(left, right);
    }

    /// <summary>A disposed handle cannot be read.</summary>
    [Fact]
    public void A_disposed_handle_refuses_to_be_read()
    {
        Dir root = OpenRoot();
        root.Dispose();

        Assert.Throws<ObjectDisposedException>(() => root.EnumerateEntries());
        Assert.Throws<ObjectDisposedException>(
            () => root.EnumerateEntriesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The directory is opened when enumeration begins, not when it is asked for.</summary>
    /// <remarks>
    /// What makes a second pass possible at all, and what keeps a caller that stops early
    /// from paying for the rest. Observed through the one difference a caller can see: a
    /// directory removed between the call and the first step fails at the step.
    /// </remarks>
    [Fact]
    public void Reading_does_not_begin_until_the_enumeration_does()
    {
        HostDirectory.CreateDirectory(Host("folder"));

        using Dir root = OpenRoot();
        using Dir folder = root.OpenDir("folder");

        IEnumerable<DirEntry> entries = folder.EnumerateEntries();
        HostDirectory.Delete(Host("folder"));

        // On the platforms that unlink at once the directory is gone and the read fails; on
        // the one that keeps a removed name until the last handle closes, the directory is
        // still readable and empty. Both are the filesystem's own behaviour showing through,
        // and both prove the read had not already happened.
        try
        {
            Assert.Empty(entries);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // --- asynchronous reading ---------------------------------------------------------------------

    /// <summary>The asynchronous form reports the same entries as the synchronous one.</summary>
    [Fact]
    public async Task Reading_asynchronously_reports_the_same_entries()
    {
        Seed(200);

        using Dir root = OpenRoot();

        List<(string Name, CapFileId Id)> asynchronous = [];
        await foreach (DirEntry entry in root.EnumerateEntriesAsync(TestContext.Current.CancellationToken))
        {
            asynchronous.Add((entry.Name, entry.FileId));
        }

        (string Name, CapFileId Id)[] synchronous = [.. root.EnumerateEntries().Select(entry => (entry.Name, entry.FileId))];

        Assert.Equal(200, asynchronous.Count);
        Assert.Equal(
            synchronous.OrderBy(entry => entry.Name, StringComparer.Ordinal),
            asynchronous.OrderBy(entry => entry.Name, StringComparer.Ordinal));
    }

    /// <summary>A token that is already signalled stops the enumeration before it starts.</summary>
    [Fact]
    public async Task Reading_asynchronously_observes_a_token_that_is_already_signalled()
    {
        Seed(10);

        using Dir root = OpenRoot();
        using CancellationTokenSource source = new();
        await source.CancelAsync().WaitAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DirEntry entry in root.EnumerateEntriesAsync(source.Token))
            {
                Assert.Fail($"The enumeration produced '{entry.Name}' after it was cancelled.");
            }
        });
    }

    /// <summary>Cancelling part of the way through stops the enumeration.</summary>
    [Fact]
    public async Task Reading_asynchronously_stops_when_the_token_is_signalled()
    {
        Seed(1000);

        using Dir root = OpenRoot();
        using CancellationTokenSource source = new();

        int seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DirEntry entry in root.EnumerateEntriesAsync(source.Token))
            {
                seen++;
                await source.CancelAsync().WaitAsync(TestContext.Current.CancellationToken);
            }
        });

        Assert.InRange(seen, 1, 999);
    }

    // --- cost ---------------------------------------------------------------------------------------

    /// <summary>
    /// What an entry costs does not grow with how many there are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property worth asserting is the shape of the cost rather than its size: an
    /// implementation that kept every record it read, or that grew a buffer per entry, would
    /// pass any fixed threshold on a small directory and fall over on a large one. So the
    /// same measurement is taken at two sizes and the per-entry figure is required not to
    /// grow.
    /// </para>
    /// <para>
    /// One allocation per entry remains, and is the name. There is no version of this API
    /// that hands back a borrowed name and still lets a caller keep one, so the cost of
    /// copying it is inherent rather than incidental.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_entry_costs_no_more_in_a_large_directory_than_in_a_small_one()
    {
        using Dir root = OpenRoot();

        HostDirectory.CreateDirectory(Host("small"));
        HostDirectory.CreateDirectory(Host("large"));
        Seed(250, "small");
        Seed(4000, "large");

        using Dir small = root.OpenDir("small");
        using Dir large = root.OpenDir("large");

        // Warm every path the measurement runs through, so that what is measured is the
        // enumeration rather than the once-per-process work of getting to it.
        Drain(small);

        double perEntrySmall = Drain(small) / 250.0;
        double perEntryLarge = Drain(large) / 4000.0;

        Assert.True(
            perEntryLarge <= perEntrySmall * 1.5,
            $"An entry cost {perEntryLarge:F0} bytes in a directory of 4000 and " +
            $"{perEntrySmall:F0} bytes in one of 250. The per-entry cost is supposed not to " +
            $"grow with the size of the directory.");
    }

    /// <summary>Enumerates a directory, answering how many bytes it allocated doing so.</summary>
    private static long Drain(Dir dir)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        int count = 0;
        foreach (DirEntry entry in dir.EnumerateEntries())
        {
            count += entry.Name.Length;
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(count > 0);
        return after - before;
    }

    // --- helpers ------------------------------------------------------------------------------------

    private static Dictionary<string, CapFileType> Kinds(Dir dir) =>
        dir.EnumerateEntries().ToDictionary(entry => entry.Name, entry => entry.Type, StringComparer.Ordinal);

    private static Dictionary<string, CapFileId> Identities(Dir dir) =>
        dir.EnumerateEntries().ToDictionary(entry => entry.Name, entry => entry.FileId, StringComparer.Ordinal);

    private void Seed(int count, string? within = null)
    {
        string directory = within is null ? _tree.HostPath : Host(within);
        for (int i = 0; i < count; i++)
        {
            HostFile.WriteAllText(
                Path.Combine(directory, i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "x");
        }
    }

    /// <summary>Creates a file whose name is the given bytes, whatever they decode to.</summary>
    private void CreateRawName(byte[] name)
    {
        _rawName = name;

        try
        {
            HostFile.CreateRawName(_tree.HostPath, name);
        }
        catch (IOException thrown)
        {
            Assert.Skip(thrown.Message);
        }
    }

    /// <summary>
    /// An entry opens with a creating mode, which does what the same mode does to the name
    /// through the handle.
    /// </summary>
    /// <remarks>
    /// The name is resolved afresh when the entry is opened, so what an entry was listed as
    /// says nothing about what the name holds by then — which is why the creating modes are
    /// accepted at all, and why each has to behave exactly as it would on a path.
    /// </remarks>
    [Fact]
    public void An_entry_opens_with_a_creating_mode()
    {
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        DirEntry entry = root.EnumerateEntries().Single(listed => listed.Name == "data");

        using (CapFile file = entry.OpenFile(FileMode.Create, FileAccess.Write))
        {
            Assert.Equal(0, file.Length);
        }

        Assert.Equal(string.Empty, HostFile.ReadAllText(Host("data")));

        CapIOException taken = Assert.Throws<CapIOException>(
            () => entry.OpenFile(FileMode.CreateNew, FileAccess.Write));
        Assert.Equal(CapErrorKind.AlreadyExists, taken.Kind);

        Assert.False(entry.TryOpenFile(
            FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.None, 0, append: false, noFollow: false, out CapFile? refused));
        Assert.Null(refused);

        Assert.True(entry.TryOpenFile(
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.None, 0, append: false, noFollow: false, out CapFile? opened));
        using (opened)
        {
            opened.Write("kept"u8, 0);
        }

        Assert.Equal("kept", HostFile.ReadAllText(Host("data")));

        // The name is resolved again, so a creating mode puts back a file removed since the
        // listing was taken.
        HostFile.Delete(Host("data"));
        using (entry.OpenFile(FileMode.CreateNew, FileAccess.Write))
        {
        }

        Assert.True(HostFile.Exists(Host("data")));
    }

    /// <summary>
    /// An entry opened without following refuses a link at its name, as the handle's own open
    /// does, where the default follows it.
    /// </summary>
    [Fact]
    public void An_entry_opened_without_following_refuses_a_link()
    {
        LinkSupport.RequireSymbolicLinks(_tree.HostPath);
        HostFile.WriteAllText(Host("plain"), "x");
        HostFile.CreateSymbolicLink(Host("to-plain"), "plain");

        using Dir root = OpenRoot();
        DirEntry link = root.EnumerateEntries().Single(entry => entry.Name == "to-plain");
        DirEntry plain = root.EnumerateEntries().Single(entry => entry.Name == "plain");

        using (link.OpenFile())
        {
        }

        _ = Assert.Throws<CapIOException>(() => link.OpenFile(noFollow: true));
        Assert.False(link.TryOpenFile(FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0, append: false, noFollow: true, out CapFile? refused));
        Assert.Null(refused);

        using (plain.OpenFile(noFollow: true))
        {
        }

        IDirEntry boxed = link;
        _ = Assert.Throws<CapIOException>(() => boxed.OpenFile(noFollow: true));
        Assert.False(boxed.TryOpenFile(FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0, append: false, noFollow: true, out _));
    }
}
