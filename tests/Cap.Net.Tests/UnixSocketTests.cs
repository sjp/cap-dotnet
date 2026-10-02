using System.Net.Sockets;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Net.Tests;

/// <summary>
/// Sockets in the Unix domain, reached through a directory handle rather than through a pool.
/// </summary>
/// <remarks>
/// <para>
/// This is where the two capability systems meet, and the reason it is worth building at all:
/// a socket in this domain is a filesystem object, so the authority to reach one is authority
/// over a path, and a second allowlist for it would be a second answer to a question that
/// already has one.
/// </para>
/// <para>
/// The attacks are the filesystem's attacks, because the object is a filesystem object. A
/// path that climbs out, a path that was absolute all along, and a name that holds a link
/// aimed at a socket elsewhere on the machine are the three, and the third is the one that
/// would otherwise work: the calls that use these addresses resolve them with the whole
/// process's authority and follow links while they do it, so a link planted inside the
/// directory would reach anything the process can.
/// </para>
/// <para>
/// The suite needs the sockets it attacks through to exist, which means creating them with
/// the ambient API rather than through the library — a socket this library agreed to create
/// would be a socket it had already agreed to reach.
/// </para>
/// </remarks>
public sealed class UnixSocketTests : IDisposable
{
    private readonly ScratchTree _tree = new();
    private readonly List<Socket> _ambient = [];

    public void Dispose()
    {
        foreach (Socket socket in _ambient)
        {
            socket.Dispose();
        }

        _tree.Dispose();
    }

    /// <summary>A socket created through a handle can be connected to through the same handle.</summary>
    [Fact]
    public async Task A_socket_beneath_a_handle_can_be_reached_through_it()
    {
        RequireSupport();

        Dir dir = _tree.Directory;
        using CapUnixListener listener = CapUnixListener.Bind(dir, "service.sock");

        ValueTask<CapUnixStream> accepting = listener.AcceptAsync(TestContext.Current.CancellationToken);

        using CapUnixStream client = await CapUnixStream.ConnectAsync(
            dir, "service.sock", TestContext.Current.CancellationToken);
        using CapUnixStream server = await accepting;

        await client.WriteAsync("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        byte[] received = new byte[5];
        await server.ReadExactlyAsync(received, TestContext.Current.CancellationToken);

        Assert.Equal("hello"u8.ToArray(), received);
    }

    /// <summary>A socket several levels down is reached by resolving the levels above it.</summary>
    [Fact]
    public void A_socket_below_a_subdirectory_is_reached_through_the_walk()
    {
        RequireSupport();

        Dir root = _tree.Directory;
        using Dir nested = root.CreateDir("one");
        using Dir deeper = nested.CreateDir("two");

        using CapUnixListener listener = CapUnixListener.Bind(root, Join("one", "two", "service.sock"));
        using CapUnixStream client = CapUnixStream.Connect(deeper, "service.sock");

        Assert.True(client.CanRead);
    }

    /// <summary>A path that climbs above the handle is refused.</summary>
    [Fact]
    public void A_path_that_climbs_out_is_refused()
    {
        RequireSupport();

        BindAmbient(Join(_tree.HostPath, "outside.sock"));

        Dir root = _tree.Directory;
        using Dir inner = root.CreateDir("inner");

        Assert.Throws<SandboxEscapeException>(() =>
            CapUnixStream.Connect(inner, Join("..", "outside.sock")));
        Assert.Throws<SandboxEscapeException>(() =>
            CapUnixListener.Bind(inner, Join("..", "created.sock")));
    }

    /// <summary>A path that was absolute all along is refused.</summary>
    [Fact]
    public void An_absolute_path_is_refused()
    {
        RequireSupport();

        string outside = Join(_tree.HostPath, "outside.sock");
        BindAmbient(outside);

        Dir root = _tree.Directory;

        Assert.Throws<SandboxEscapeException>(() => CapUnixStream.Connect(root, outside));
    }

    /// <summary>
    /// A name that holds a link aimed outside the handle is refused rather than followed.
    /// </summary>
    /// <remarks>
    /// The attack this whole mechanism exists for. The link is inside the directory, so
    /// resolution reaches it legitimately; what must not happen is that the target is handed
    /// to a socket call, which would resolve it with the process's own authority and arrive at
    /// a socket the handle confers no authority over. The assertion is that the connection
    /// does not happen — a listener is running at the far end, so a refusal that failed to
    /// happen would be a successful connection rather than a different error.
    /// </remarks>
    [Fact]
    public void A_link_aimed_outside_the_handle_is_refused()
    {
        RequireSupport();
        LinkSupport.RequireSymbolicLinks(_tree.HostPath);

        BindAmbient(Join(_tree.HostPath, "outside.sock"));

        Dir root = _tree.Directory;
        using Dir inner = root.CreateDir("inner");
        inner.CreateSymlink("service.sock", Join("..", "outside.sock"));

        Exception thrown = Assert.ThrowsAny<IOException>(() =>
            CapUnixStream.Connect(inner, "service.sock"));

        Assert.IsNotType<SocketException>(thrown);
    }

    /// <summary>
    /// A name that holds a link is refused even when the link points somewhere the handle
    /// does cover.
    /// </summary>
    /// <remarks>
    /// Because following it would mean handing a path to a socket call, and once that call is
    /// doing the resolving, where the link happens to point is not something this library
    /// decided. The refusal is about who resolves, not about where this particular link went.
    /// </remarks>
    [Fact]
    public void A_link_is_refused_even_when_it_stays_inside()
    {
        RequireSupport();
        LinkSupport.RequireSymbolicLinks(_tree.HostPath);

        Dir root = _tree.Directory;
        using CapUnixListener listener = CapUnixListener.Bind(root, "real.sock");
        root.CreateSymlink("alias.sock", "real.sock");

        Exception thrown = Assert.ThrowsAny<IOException>(() => CapUnixStream.Connect(root, "alias.sock"));

        Assert.IsNotType<SocketException>(thrown);
    }

    /// <summary>A name holding something that is not a socket is refused as such.</summary>
    [Fact]
    public void A_name_that_is_not_a_socket_is_refused()
    {
        RequireSupport();

        Dir root = _tree.Directory;
        root.WriteAllBytes("notes.txt", "text"u8);

        Assert.ThrowsAny<IOException>(() => CapUnixStream.Connect(root, "notes.txt"));
    }

    /// <summary>A name nothing holds is reported as missing.</summary>
    [Fact]
    public void A_name_that_holds_nothing_is_reported_as_missing()
    {
        RequireSupport();

        Dir root = _tree.Directory;

        Assert.Throws<FileNotFoundException>(() => CapUnixStream.Connect(root, "absent.sock"));
    }

    /// <summary>A name something already holds cannot be bound over.</summary>
    /// <remarks>
    /// Including a name holding a link, which is the case that matters: a bind that replaced
    /// what it found, or followed it, would create the socket wherever the link pointed.
    /// </remarks>
    [Fact]
    public void A_name_already_taken_cannot_be_bound()
    {
        RequireSupport();
        LinkSupport.RequireSymbolicLinks(_tree.HostPath);

        Dir root = _tree.Directory;
        using CapUnixListener first = CapUnixListener.Bind(root, "service.sock");

        Assert.ThrowsAny<Exception>(() => CapUnixListener.Bind(root, "service.sock"));

        root.CreateSymlink("aimed.sock", Join("..", "elsewhere.sock"));
        Assert.ThrowsAny<Exception>(() => CapUnixListener.Bind(root, "aimed.sock"));

        // Read with the ambient API, because the point is that nothing appeared where the
        // library could not have looked.
        Assert.False(File.Exists(Join(_tree.HostPath, "..", "elsewhere.sock")));
    }

    /// <summary>A name the socket address cannot hold is refused, and nothing is created.</summary>
    /// <remarks>
    /// The address is <c>/proc/self/fd/N/</c> and the name, in 107 bytes, so a name of 100
    /// leaves no room for the prefix whatever <c>N</c> is. Truncating it would bind a
    /// different name, so the refusal is the only answer, and it is reported as the name
    /// being too long rather than as whatever the socket call would have said.
    /// </remarks>
    [Fact]
    public void A_name_that_does_not_fit_the_address_is_refused()
    {
        RequireSupport();

        Dir root = _tree.Directory;
        string name = new('a', 100);

        PathTooLongException refused = Assert.Throws<PathTooLongException>(() => CapUnixListener.Bind(root, name));

        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(refused));
        Assert.Contains("100 bytes", refused.Message, StringComparison.Ordinal);
        Assert.Matches(@"had room for \d+\.", refused.Message);
        Assert.False(root.Exists(name));
    }

    /// <summary>A long name that fits binds, however long the directories above it are.</summary>
    /// <remarks>
    /// Only the last component is written into the address; the directories ahead of it are
    /// opened by the walk and stand in it as a descriptor number. So a path far longer than
    /// the address binds as long as its last component fits.
    /// </remarks>
    [Fact]
    public void A_long_name_that_fits_binds_beneath_a_long_path()
    {
        RequireSupport();

        Dir root = _tree.Directory;
        string directory = new('d', 200);
        string name = new('s', 60);
        using Dir nested = root.CreateDir(directory);

        using CapUnixListener listener = CapUnixListener.Bind(root, Join(directory, name));
        using CapUnixStream client = CapUnixStream.Connect(nested, name);

        Assert.True(client.CanRead);
    }

    /// <summary>Closing a listener leaves the name where it was.</summary>
    /// <remarks>
    /// Removing it acts on the name rather than on the object, and by the time a process is
    /// finished the name may hold something somebody else put there. So it is the caller's
    /// call, made through the handle like any other deletion.
    /// </remarks>
    [Fact]
    public void Closing_a_listener_leaves_the_name_behind()
    {
        RequireSupport();

        Dir root = _tree.Directory;

        CapUnixListener.Bind(root, "service.sock").Dispose();

        Assert.True(root.Exists("service.sock"));
        root.DeleteFile("service.sock");
        Assert.False(root.Exists("service.sock"));
    }

    /// <summary>Where the platform cannot express it, it is refused rather than approximated.</summary>
    /// <remarks>
    /// The alternative would be to resolve the path and hand the result to a socket call,
    /// which resolves it again with the process's own authority. That would make the type's
    /// stated guarantee untrue rather than merely weaker, which is the one outcome worth
    /// refusing over.
    /// </remarks>
    [Fact]
    public void Where_the_platform_cannot_express_it_the_operation_is_refused()
    {
        if (CapUnixStream.IsSupported)
        {
            Assert.True(CapUnixListener.IsSupported);
            return;
        }

        Dir root = _tree.Directory;

        Assert.Throws<PlatformNotSupportedException>(() => CapUnixStream.Connect(root, "service.sock"));
        Assert.Throws<PlatformNotSupportedException>(() => CapUnixListener.Bind(root, "service.sock"));
    }

    /// <summary>Skips the body where sockets in this domain cannot be reached by handle.</summary>
    private static void RequireSupport()
    {
        if (!CapUnixStream.IsSupported)
        {
            Assert.Skip("This platform reaches no socket through a directory handle.");
        }
    }

    /// <summary>Builds an ordinary path, for the set-up that must not go through the library.</summary>
    private static string Join(params string[] parts) => Path.Combine(parts);

    /// <summary>Creates a socket by path, the way anything that is not this library would.</summary>
    private void BindAmbient(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _ambient.Add(socket);

        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(1);
    }
}
