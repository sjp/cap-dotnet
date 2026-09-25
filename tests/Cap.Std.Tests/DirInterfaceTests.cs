using Cap.Primitives;
using Moq;

namespace Cap.Std.Tests;

/// <summary>
/// A real handle used through the interfaces, and what it does when the other end of an
/// operation is not a real handle.
/// </summary>
/// <remarks>
/// The members that forward from an interface to the handle's own are one line each, so
/// these tests are about the places where forwarding has a shape of its own: a result widened
/// to an interface, an entry boxed on its way out of an enumeration, and a destination that
/// has to be refused because it is not a handle at all.
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirInterfaceTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    /// <summary>
    /// Everything handed out through the interface is the handle type itself, so a component
    /// given an <see cref="IDir"/> that is a <see cref="Dir"/> stays on real handles all the way down.
    /// </summary>
    [Fact]
    public void What_a_handle_hands_out_through_the_interface_is_a_real_handle()
    {
        HostDirectory.CreateDirectory(Host("sub"));
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        IDir dir = root;

        using (IDir sub = dir.OpenDir("sub"))
        {
            Assert.IsType<Dir>(sub);
        }

        Assert.True(dir.TryOpenDir("sub", out IDir? opened));
        using (opened)
        {
            Assert.IsType<Dir>(opened);
        }

        Assert.False(dir.TryOpenDir("missing", out IDir? missing));
        Assert.Null(missing);

        using (ICapFile file = dir.OpenFile("data"))
        {
            Assert.IsType<CapFile>(file);
            Assert.Equal(8, file.Length);
        }

        Assert.True(dir.TryOpenFile("data", out ICapFile? tried));
        using (tried)
        {
            Assert.IsType<CapFile>(tried);
        }

        using (IDir restricted = dir.Restrict(SymlinkPolicy.Deny))
        {
            Assert.Equal(SymlinkPolicy.Deny, Assert.IsType<Dir>(restricted).SymlinkPolicy);
        }

        using (ICapOpened any = dir.OpenAny("sub"))
        {
            Assert.True(any.IsDirectory);
            using IDir taken = any.TakeDir();
            Assert.IsType<Dir>(taken);
        }

        Assert.Equal("contents", dir.ReadAllText("data"));
    }

    /// <summary>
    /// Entries enumerated through the interface are the handle's own entries, and open what
    /// they name through it.
    /// </summary>
    [Fact]
    public async Task Entries_enumerated_through_the_interface_open_what_they_name()
    {
        HostDirectory.CreateDirectory(Host("sub"));
        HostFile.WriteAllText(Host("sub", "inner"), "");
        HostFile.WriteAllText(Host("data"), "contents");

        using Dir root = OpenRoot();
        IDir dir = root;

        IDirEntry[] entries = [.. dir.EnumerateEntries().OrderBy(entry => entry.Name, StringComparer.Ordinal)];
        Assert.Equal(["data", "sub"], entries.Select(entry => entry.Name));
        Assert.All(entries, entry => Assert.IsType<DirEntry>(entry));
        Assert.Equal(CapFileType.File, entries[0].Type);
        Assert.Equal(CapFileType.Directory, entries[1].Type);

        using (ICapFile file = entries[0].OpenFile())
        {
            Assert.Equal(8, file.Length);
        }

        using (IDir sub = entries[1].OpenDir())
        {
            Assert.Equal(["inner"], sub.EnumerateEntries().Select(entry => entry.Name));
        }

        Assert.True(entries[1].TryOpenDir(out IDir? tried));
        tried.Dispose();
        Assert.False(entries[0].TryOpenDir(out IDir? notADirectory));
        Assert.Null(notADirectory);

        List<string> names = [];
        await foreach (IDirEntry entry in dir.EnumerateEntriesAsync(TestContext.Current.CancellationToken))
        {
            names.Add(entry.Name);
        }

        Assert.Equal(["data", "sub"], names.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A disposed handle says so when the enumeration is asked for, through the interface as
    /// through the handle, rather than when the first entry is read.
    /// </summary>
    [Fact]
    public void Enumerating_a_disposed_handle_through_the_interface_fails_at_once()
    {
        Dir root = OpenRoot();
        IDir dir = root;
        root.Dispose();

        Assert.Throws<ObjectDisposedException>(() => dir.EnumerateEntries());
        Assert.Throws<ObjectDisposedException>(() => dir.EnumerateEntriesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A cancellation passed to the interface's asynchronous enumeration reaches the
    /// handle's, whether it is passed to the call or to the sequence it returns.
    /// </summary>
    [Fact]
    public async Task Cancelling_an_enumeration_through_the_interface_stops_it()
    {
        HostFile.WriteAllText(Host("data"), "");

        using Dir root = OpenRoot();
        IDir dir = root;
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (IDirEntry _ in dir.EnumerateEntriesAsync(cancelled.Token))
            {
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (IDirEntry _ in dir.EnumerateEntriesAsync(TestContext.Current.CancellationToken).WithCancellation(cancelled.Token))
            {
            }
        });
    }

    /// <summary>
    /// Moving or linking onto a destination that is not a handle is refused as a move across
    /// devices, before either name is looked at, and nothing is asked of the stand-in.
    /// </summary>
    /// <remarks>
    /// A real handle cannot hand a kernel call anything but another real handle as the other
    /// end. Whatever a stand-in would do with the name, it would do it somewhere this handle
    /// has no authority over, so the operation stops before either side is touched.
    /// </remarks>
    [Fact]
    public void A_stand_in_for_a_handle_is_refused_as_a_destination()
    {
        HostFile.WriteAllText(Host("entry"), "contents");

        // Strict, so that any call made on the stand-in would fail the test where it is made.
        Mock<IDir> standInMock = new(MockBehavior.Strict);
        IDir standIn = standInMock.Object;

        using Dir root = OpenRoot();

        AssertCrossDevice(() => root.Rename("entry", standIn, "moved"));
        AssertCrossDevice(() => root.Rename("entry", standIn, "moved", replaceExisting: true));
        AssertCrossDevice(() => root.CreateHardLink("entry", standIn, "linked"));
        AssertCrossDevice(() => root.CreateHardLink("entry", standIn, "linked", followLink: true));
        Assert.False(root.TryRename("entry", standIn, "moved"));
        Assert.False(root.TryCreateHardLink("entry", standIn, "linked"));

        AssertCrossDevice(() => ((IDir)root).Rename("entry", standIn, "moved"));
        AssertCrossDevice(() => ((IDir)root).CreateHardLink("entry", standIn, "linked"));

        standInMock.VerifyNoOtherCalls();
        Assert.Equal(["entry"], HostDirectory.GetFileSystemEntries(_tree.HostPath).Select(Path.GetFileName));
        Assert.Equal("contents", HostFile.ReadAllText(Host("entry")));
    }

    /// <summary>
    /// The refusal of a stand-in is decided before the source is resolved, so it is the
    /// answer even for a source that does not exist or could not be reached.
    /// </summary>
    [Fact]
    public void A_stand_in_is_refused_before_the_source_is_resolved()
    {
        IDir standIn = new Mock<IDir>(MockBehavior.Strict).Object;

        using Dir root = OpenRoot();

        AssertCrossDevice(() => root.Rename("missing", standIn, "moved"));
        AssertCrossDevice(() => root.CreateHardLink("../outside", standIn, "linked"));
    }

    /// <summary>A null destination is still a null argument, whatever its declared type.</summary>
    [Fact]
    public void A_null_destination_is_an_argument_error()
    {
        using Dir root = OpenRoot();

        Assert.Throws<ArgumentNullException>(() => root.Rename("entry", null!, "moved"));
        Assert.Throws<ArgumentNullException>(() => root.CreateHardLink("entry", null!, "linked"));
    }

    private static void AssertCrossDevice(Action operation)
    {
        CapIOException refused = Assert.Throws<CapIOException>(operation);
        Assert.Equal(CapErrorKind.CrossDevice, refused.Kind);
    }
}
