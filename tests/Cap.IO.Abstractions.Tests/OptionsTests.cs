using Cap.Std;
using Cap.Std.Testing;

namespace Cap.IO.Abstractions.Tests;

/// <summary>How the namespace is presented.</summary>
public sealed class OptionsTests : IDisposable
{
    private readonly Dir _root = new InMemoryFileSystem().OpenRoot();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_virtual_drive_spells_the_root_as_that_drive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A virtual drive is offered only on Windows.");
        DirFileSystem fs = new(_root, new DirFileSystemOptions { VirtualDrive = 'c' });

        fs.File.WriteAllText(@"C:\a.txt", "one");

        Assert.Equal(@"C:\", fs.Directory.GetCurrentDirectory());
        Assert.Equal("one", fs.File.ReadAllText(@"\a.txt"));
        Assert.Equal(@"C:\a.txt", fs.FileInfo.New("a.txt").FullName);
        Assert.Throws<SandboxEscapeException>(() => fs.File.ReadAllText(@"D:\a.txt"));
    }

    [Fact]
    public void A_virtual_drive_is_refused_elsewhere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows offers a virtual drive.");

        Assert.Throws<PlatformNotSupportedException>(() => new DirFileSystem(_root, new DirFileSystemOptions { VirtualDrive = 'C' }));
    }

    [Fact]
    public void A_virtual_drive_must_be_a_letter()
    {
        Assert.Throws<ArgumentException>(() => new DirFileSystem(_root, new DirFileSystemOptions { VirtualDrive = '1' }));
    }

    [Fact]
    public void The_default_root_is_a_slash()
    {
        DirFileSystem fs = new(_root);

        Assert.Equal("/", fs.Directory.GetCurrentDirectory());
        Assert.Equal('/', fs.Path.DirectorySeparatorChar);
    }
}
