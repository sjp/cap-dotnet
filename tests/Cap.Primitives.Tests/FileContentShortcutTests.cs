using Cap.Primitives.Interop;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The host backends' shortcuts for moving a file's contents, called directly on real files.
/// </summary>
/// <remarks>
/// <para>
/// Each shortcut may be missing on the filesystem under the temporary directory, and its
/// caller reads and writes instead, so what is checked is the contract either way: a shortcut
/// that works moves exactly the right bytes, and one that does not leaves the destination as it
/// was handed over and says so with an error rather than a short or wrong result.
/// </para>
/// <para>
/// The copy that chooses between them is tested against a simulation in the convenience
/// layer's suite, where every shortcut can be switched on and off.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class FileContentShortcutTests : IDisposable
{
    private const int Mebibyte = 1024 * 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("cap-shortcut-").FullName;
    private readonly SafeDirHandle _dir;

    public FileContentShortcutTests()
    {
        CapResult<SafeDirHandle> root = PlatformOps.Host.OpenAmbientDirectory(_root, CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);
        _dir = root.Value;
    }

    public void Dispose()
    {
        _dir.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The data in a file with holes is found where it was written, and nothing is found past
    /// the end.
    /// </summary>
    [Fact]
    public void Data_is_found_where_it_was_written_and_nowhere_past_the_end()
    {
        using SafeFileHandle file = Open("sparse", FileMode.CreateNew, FileAccess.ReadWrite);
        _ = PlatformOps.Host.MarkFileSparse(file);
        RandomAccess.SetLength(file, 8 * Mebibyte);
        RandomAccess.Write(file, Contents(4096), 4 * Mebibyte);

        CapError found = PlatformOps.Host.FindFileData(file, 0, out long start, out long end);
        if (found.Category == CapErrorCategory.NotSupported)
        {
            Assert.Skip("The filesystem under the temporary directory cannot say where data is.");
        }

        Assert.True(found.IsSuccess, found.FailureDescription);
        Assert.InRange(start, 0, 4 * Mebibyte);
        Assert.True(end >= (4 * Mebibyte) + 4096, $"The stretch found ends at {end}.");

        Assert.True(PlatformOps.Host.FindFileData(file, 8 * Mebibyte, out start, out end).IsSuccess);
        Assert.Equal((-1, -1), (start, end));
    }

    /// <summary>A range copied inside the kernel arrives as it was, at the same offset.</summary>
    [Fact]
    public void A_range_copy_moves_the_bytes_or_is_refused()
    {
        byte[] contents = Contents(Mebibyte + 17);
        File.WriteAllBytes(Path.Combine(_root, "source"), contents);
        using SafeFileHandle source = Open("source", FileMode.Open, FileAccess.Read);
        using SafeFileHandle destination = Open("destination", FileMode.CreateNew, FileAccess.Write);

        CapError copied = PlatformOps.Host.CopyFileRange(source, destination, 4096, contents.Length, out long count);

        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(CapErrorCategory.NotSupported, copied.Category);
            return;
        }

        if (copied.IsFailure)
        {
            Assert.Equal(0, count);
            Assert.Equal(0, RandomAccess.GetLength(destination));
            return;
        }

        Assert.InRange(count, 1, contents.Length - 4096);
        byte[] written = new byte[count];
        using SafeFileHandle check = Open("destination", FileMode.Open, FileAccess.Read);
        Assert.Equal(count, RandomAccess.Read(check, written, 4096));
        Assert.Equal(contents.AsSpan(4096, (int)count).ToArray(), written);
    }

    /// <summary>A range copy from the end of the source copies nothing and is not an error.</summary>
    [Fact]
    public void A_range_copy_from_the_end_copies_nothing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Only Linux copies ranges inside the kernel.");
        }

        File.WriteAllBytes(Path.Combine(_root, "source"), Contents(100));
        using SafeFileHandle source = Open("source", FileMode.Open, FileAccess.Read);
        using SafeFileHandle destination = Open("destination", FileMode.CreateNew, FileAccess.Write);

        CapError copied = PlatformOps.Host.CopyFileRange(source, destination, 100, 4096, out long count);

        if (copied.IsSuccess)
        {
            Assert.Equal(0, count);
        }
    }

    /// <summary>
    /// A clone gives the destination the source's contents, or leaves it empty and says why.
    /// </summary>
    [Fact]
    public void A_clone_shares_the_contents_or_leaves_the_destination_empty()
    {
        byte[] contents = Contents(Mebibyte + 17);
        File.WriteAllBytes(Path.Combine(_root, "source"), contents);
        using SafeFileHandle source = Open("source", FileMode.Open, FileAccess.Read);

        CapError cloned;
        using (SafeFileHandle destination = Open("destination", FileMode.CreateNew, FileAccess.Write))
        {
            cloned = PlatformOps.Host.CloneFileContents(source, destination);
        }

        byte[] written = File.ReadAllBytes(Path.Combine(_root, "destination"));
        if (cloned.IsSuccess)
        {
            Assert.Equal(contents, written);
        }
        else
        {
            Assert.Empty(written);
            if (OperatingSystem.IsMacOS())
            {
                Assert.Equal(CapErrorCategory.NotSupported, cloned.Category);
            }
        }
    }

    /// <summary>
    /// A clone made by name is macOS's alone. Where it works, the new name holds the source's
    /// contents, and a name already taken is refused rather than replaced.
    /// </summary>
    [Fact]
    public void A_clone_by_name_claims_a_free_name_on_macos_alone()
    {
        byte[] contents = Contents(Mebibyte + 17);
        File.WriteAllBytes(Path.Combine(_root, "source"), contents);
        File.WriteAllBytes(Path.Combine(_root, "taken"), [1, 2, 3]);
        using SafeFileHandle source = Open("source", FileMode.Open, FileAccess.Read);

        CapError cloned = PlatformOps.Host.CloneFileToChild(source, _dir, "clone");

        if (!OperatingSystem.IsMacOS() || cloned.Category == CapErrorCategory.NotSupported)
        {
            Assert.Equal(CapErrorCategory.NotSupported, cloned.Category);
            Assert.False(File.Exists(Path.Combine(_root, "clone")));
            return;
        }

        Assert.True(cloned.IsSuccess, cloned.FailureDescription);
        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(_root, "clone")));
        Assert.Equal(
            CapErrorCategory.AlreadyExists,
            PlatformOps.Host.CloneFileToChild(source, _dir, "taken").Category);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_root, "taken")));
    }

    /// <summary>
    /// A reservation leaves the file's length alone, whether it asks for more room than the
    /// file holds or less.
    /// </summary>
    [Theory]
    [InlineData(4 * Mebibyte)]
    [InlineData(4096)]
    public void A_reservation_leaves_the_length_alone(long reserved)
    {
        using SafeFileHandle file = Open("reserved", FileMode.CreateNew, FileAccess.ReadWrite);
        RandomAccess.Write(file, Contents(Mebibyte), 0);

        _ = PlatformOps.Host.ReserveFileSpace(file, reserved);

        Assert.Equal(Mebibyte, RandomAccess.GetLength(file));
        byte[] read = new byte[Mebibyte];
        Assert.Equal(Mebibyte, RandomAccess.Read(file, read, 0));
        Assert.Equal(Contents(Mebibyte), read);
    }

    /// <summary>Marking a file sparse is nothing to do on Unix, where every file already is.</summary>
    [Fact]
    public void Marking_a_file_sparse_succeeds_on_unix_without_doing_anything()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows files are marked for real.");
        }

        using SafeFileHandle file = Open("marked", FileMode.CreateNew, FileAccess.Write);

        Assert.True(PlatformOps.Host.MarkFileSparse(file).IsSuccess);
    }

    private SafeFileHandle Open(string name, FileMode mode, FileAccess access)
    {
        FileOpenRequest request = new(mode, access, FileShare.ReadWrite, FileOptions.None, preallocationSize: 0);
        CapResult<SafeFileHandle> result = PlatformOps.Host.OpenChildFile(_dir, name, request);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value;
    }

    private static byte[] Contents(int length)
    {
        byte[] contents = new byte[length];
        for (int i = 0; i < length; i++)
        {
            contents[i] = (byte)((i * 31 % 251) + 1);
        }

        return contents;
    }
}
