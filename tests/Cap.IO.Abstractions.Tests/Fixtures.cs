using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.IO.Abstractions.Tests;

/// <summary>An <see cref="IFileSystem"/> for one test, and what it is.</summary>
public interface IFileSystemFixture : IDisposable
{
    /// <summary>The file system under test.</summary>
    IFileSystem FileSystem { get; }

    /// <summary>Whether this is a <see cref="DirFileSystem"/>, which confines, rather than the mock.</summary>
    bool Confined { get; }

    /// <summary>Whether symbolic links can be made here; creating one can need a privilege on Windows.</summary>
    bool SupportsLinks { get; }
}

/// <summary>A <see cref="DirFileSystem"/> over a scratch directory on disk.</summary>
public sealed class DiskFixture : IFileSystemFixture
{
    private readonly ScratchTree _tree = new();

    public DiskFixture()
    {
        FileSystem = new DirFileSystem(_tree.Directory);
        SupportsLinks = TestLinks.CanCreate(_tree.HostPath);
    }

    public IFileSystem FileSystem { get; }

    public bool Confined => true;

    public bool SupportsLinks { get; }

    public void Dispose() => _tree.Dispose();
}

/// <summary>A <see cref="DirFileSystem"/> over the in-memory filesystem.</summary>
public sealed class MemoryFixture : IFileSystemFixture
{
    private readonly Dir _root;

    public MemoryFixture()
    {
        _root = new InMemoryFileSystem().OpenRoot();
        FileSystem = new DirFileSystem(_root);
    }

    public IFileSystem FileSystem { get; }

    public bool Confined => true;

    public bool SupportsLinks => true;

    public void Dispose() => _root.Dispose();
}

/// <summary>The <see cref="MockFileSystem"/> that <c>IFileSystem</c> code is usually tested with.</summary>
public sealed class MockFixture : IFileSystemFixture
{
    public IFileSystem FileSystem { get; } = new MockFileSystem();

    public bool Confined => false;

    public bool SupportsLinks => true;

    public void Dispose()
    {
    }
}

/// <summary>Whether this process can make symbolic links in a directory.</summary>
internal static class TestLinks
{
    public static bool CanCreate(string directory)
    {
        string probe = Path.Combine(directory, "link-probe");
        try
        {
            File.CreateSymbolicLink(probe, "target");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
