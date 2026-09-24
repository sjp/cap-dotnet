using System.Text;
using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Appending as a flag of its own, apart from the mode an open is made with.
/// </summary>
/// <remarks>
/// <para>
/// The framework makes appending a mode, which creates, writes only and never truncates.
/// POSIX makes it a flag that combines with reading, with creating a file new, with emptying
/// it, and that can be turned on and off on an open file. These cases hold the library to
/// the second, on every platform, while the framework's own mode keeps meaning what it
/// meant.
/// </para>
/// <para>
/// Every case checks where the bytes ended up by reading the file afterwards through the
/// ambient filesystem, not through the handle under test, so that a handle that was wrong
/// about its own writes cannot also be the one vouching for them.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class CapFileAppendingTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(string name) => Path.Combine(_tree.HostPath, name);

    // --- opening to append -------------------------------------------------------------------

    /// <summary>One handle can read anywhere and append, as POSIX allows.</summary>
    [Fact]
    public void A_handle_can_read_and_append()
    {
        File.WriteAllText(Host("log"), "first");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("log", FileMode.Open, FileAccess.ReadWrite, append: true))
        {
            Assert.True(file.IsAppending);

            byte[] start = new byte[3];
            Assert.Equal(3, file.Read(start, 0));
            Assert.Equal("fir"u8, start);

            file.Write(" second"u8, 0);

            byte[] whole = new byte[12];
            Assert.Equal(12, file.Read(whole, 0));
            Assert.Equal("first second"u8, whole);
        }

        Assert.Equal("first second", File.ReadAllText(Host("log")));
    }

    /// <summary>An open can empty the file and then append to it.</summary>
    /// <remarks>
    /// The combination a C program asks for with <c>O_CREAT | O_TRUNC | O_APPEND</c>, which
    /// the framework's modes cannot express: its append mode keeps what is there.
    /// </remarks>
    [Theory]
    [InlineData(FileMode.Create)]
    [InlineData(FileMode.Truncate)]
    public void An_open_can_truncate_and_append(FileMode mode)
    {
        File.WriteAllText(Host("log"), "discarded");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("log", mode, FileAccess.Write, append: true))
        {
            file.Write("ab"u8, 0);
            file.Write("cd"u8, 0);
        }

        Assert.Equal("abcd", File.ReadAllText(Host("log")));
    }

    /// <summary>An open that must create the file can append to it.</summary>
    [Fact]
    public void An_exclusive_create_can_append()
    {
        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("log", FileMode.CreateNew, FileAccess.ReadWrite, append: true))
        {
            file.Write("ab"u8, 0);
            file.Write("cd"u8, 1);
        }

        Assert.Equal("abcd", File.ReadAllText(Host("log")));
    }

    /// <summary>The framework's append mode reports that it appends.</summary>
    [Fact]
    public void The_append_mode_opens_a_handle_that_appends()
    {
        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("log", FileMode.Append, FileAccess.Write);

        Assert.True(file.IsAppending);
    }

    /// <summary>A handle opened without asking to append does not.</summary>
    [Fact]
    public void An_ordinary_open_does_not_append()
    {
        File.WriteAllText(Host("data"), "aaaa");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.False(file.IsAppending);
            file.Write("b"u8, 1);
        }

        Assert.Equal("abaa", File.ReadAllText(Host("data")));
    }

    /// <summary>Asking to append through a handle that cannot write is a mistake in the request.</summary>
    [Fact]
    public void Appending_without_write_access_is_refused()
    {
        File.WriteAllText(Host("log"), "first");

        using Dir root = OpenRoot();

        Assert.Throws<ArgumentException>(() => root.OpenFile("log", FileMode.Open, FileAccess.Read, append: true));
        Assert.Throws<ArgumentException>(() => root.TryOpenFile(
            "log", FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0, append: true, noFollow: false, out _));
    }

    /// <summary>The reporting form opens to append as the throwing form does.</summary>
    [Fact]
    public void The_reporting_form_opens_to_append()
    {
        File.WriteAllText(Host("log"), "first");

        using Dir root = OpenRoot();
        Assert.True(root.TryOpenFile(
            "log", FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.None, 0, append: true, noFollow: false,
            out CapFile? file));

        using (file)
        {
            Assert.True(file!.IsAppending);
            file.Write("!"u8, 0);
        }

        Assert.Equal("first!", File.ReadAllText(Host("log")));
    }

    // --- changing it on an open file -----------------------------------------------------------

    /// <summary>Appending can be turned on and off, and each write follows the setting it met.</summary>
    [Fact]
    public void Appending_can_be_turned_on_and_off()
    {
        File.WriteAllText(Host("data"), "aaaa");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("data", FileMode.Open, FileAccess.ReadWrite))
        {
            file.IsAppending = true;
            Assert.True(file.IsAppending);
            file.Write("b"u8, 0);

            file.IsAppending = false;
            Assert.False(file.IsAppending);
            file.Write("c"u8, 0);

            file.IsAppending = true;
            file.Write("d"u8, 0);
        }

        Assert.Equal("caaabd", File.ReadAllText(Host("data")));
    }

    /// <summary>A handle opened to append can stop, and then writes where it is told.</summary>
    [Fact]
    public void A_handle_opened_to_append_can_stop()
    {
        File.WriteAllText(Host("data"), "aaaa");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("data", FileMode.Append, FileAccess.Write))
        {
            file.IsAppending = false;
            file.Write("b"u8, 0);
        }

        Assert.Equal("baaa", File.ReadAllText(Host("data")));
    }

    /// <summary>A handle that cannot write has nothing for appending to place.</summary>
    [Fact]
    public void Appending_cannot_be_turned_on_for_a_handle_that_cannot_write()
    {
        File.WriteAllText(Host("data"), "aaaa");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("data");

        Assert.Throws<UnauthorizedAccessException>(() => file.IsAppending = true);
        Assert.False(file.IsAppending);
    }

    /// <summary>Changing the setting on a closed handle reports that it is closed.</summary>
    [Fact]
    public void Appending_cannot_be_changed_on_a_closed_handle()
    {
        using Dir root = OpenRoot();
        CapFile file = root.CreateFile("data");
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.IsAppending = true);
    }

    // --- how writes behave -----------------------------------------------------------------------

    /// <summary>The asynchronous write appends too.</summary>
    [Theory]
    [InlineData(FileOptions.None)]
    [InlineData(FileOptions.Asynchronous)]
    public async Task The_asynchronous_write_appends(FileOptions options)
    {
        File.WriteAllText(Host("log"), "first");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile(
                   "log", FileMode.Open, FileAccess.ReadWrite, FileShare.Read, options, append: true))
        {
            await file.WriteAsync(" second"u8.ToArray(), 0, TestContext.Current.CancellationToken);
        }

        Assert.Equal("first second", File.ReadAllText(Host("log")));
    }

    /// <summary>A write asked to stop before it starts does not happen.</summary>
    [Fact]
    public async Task A_cancelled_appending_write_does_not_happen()
    {
        File.WriteAllText(Host("log"), "first");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("log", FileMode.Open, FileAccess.Write, append: true))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => file.WriteAsync(" second"u8.ToArray(), 0, new CancellationToken(canceled: true)).AsTask());
        }

        Assert.Equal("first", File.ReadAllText(Host("log")));
    }

    /// <summary>Two handles appending to one file never overwrite each other.</summary>
    /// <remarks>
    /// What appending is for. Each write finds the end and writes there in one step, so no
    /// write can land on bytes another handle has just put there, however the two interleave.
    /// </remarks>
    [Fact]
    public void Two_appending_handles_never_overwrite_each_other()
    {
        const int Writes = 200;
        File.WriteAllText(Host("log"), string.Empty);

        using Dir root = OpenRoot();
        using CapFile first = root.OpenFile(
            "log", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, append: true);
        using CapFile second = root.OpenFile(
            "log", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, append: true);

        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < Writes; i++)
                {
                    first.Write("a\n"u8, 0);
                }
            },
            () =>
            {
                for (int i = 0; i < Writes; i++)
                {
                    second.Write("b\n"u8, 0);
                }
            });

        string[] lines = File.ReadAllText(Host("log")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Writes, lines.Count(line => line == "a"));
        Assert.Equal(Writes, lines.Count(line => line == "b"));
        Assert.Equal(2 * Writes, lines.Length);
    }

    /// <summary>Resizing is not a write and is not moved to the end.</summary>
    [Fact]
    public void Resizing_an_appending_file_still_works()
    {
        File.WriteAllText(Host("log"), "first second");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("log", FileMode.Open, FileAccess.Write, append: true))
        {
            file.SetLength(5);
            file.Write("!"u8, 0);
        }

        Assert.Equal("first!", File.ReadAllText(Host("log")));
    }

    // --- streams -------------------------------------------------------------------------------

    /// <summary>A stream taken while appending is on appends, wherever it thinks it is.</summary>
    /// <remarks>
    /// Promised on Linux and Windows. A stream writes at its own position, and macOS does not
    /// document where such a write goes on a file that appends.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_stream_taken_while_appending_appends(bool leaveOpen)
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS does not document where a positioned write to an appending file goes.");

        File.WriteAllText(Host("log"), "first");

        using Dir root = OpenRoot();
        CapFile file = root.OpenFile("log", FileMode.Open, FileAccess.ReadWrite, append: true);
        using (file)
        {
            using (FileStream stream = file.AsStream(leaveOpen, bufferSize: 0))
            {
                stream.Position = 0;
                stream.Write(Encoding.ASCII.GetBytes(" second"));
            }

            if (leaveOpen)
            {
                file.Write(" third"u8, 0);
            }
            else
            {
                Assert.Throws<ObjectDisposedException>(() => file.Length);
            }
        }

        string expected = leaveOpen ? "first second third" : "first second";
        Assert.Equal(expected, File.ReadAllText(Host("log")));
    }

    /// <summary>
    /// On the systems where appending is a flag on the open file, turning it on reaches a
    /// stream already taken.
    /// </summary>
    /// <remarks>
    /// Documented as platform behaviour, since Windows keeps no such flag, and tested where
    /// it is promised so that the documentation stays true.
    /// </remarks>
    [Fact]
    public void Turning_appending_on_reaches_a_borrowed_stream_where_the_system_keeps_the_flag()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux both keeps the flag and documents where a stream's write then goes.");

        File.WriteAllText(Host("log"), "first");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("log", FileMode.Open, FileAccess.ReadWrite))
        using (FileStream stream = file.AsStream(bufferSize: 0))
        {
            file.IsAppending = true;
            stream.Position = 0;
            stream.Write(Encoding.ASCII.GetBytes(" second"));
        }

        Assert.Equal("first second", File.ReadAllText(Host("log")));
    }
}
