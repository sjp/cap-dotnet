using System.IO.Abstractions.TestingHelpers;
using Cap.IO.Abstractions;
using Cap.Std;
using Cap.Std.Testing;

namespace TestableComponent.Tests;

/// <summary>
/// The component written against <c>IFileSystem</c>, tested as it always was with
/// <see cref="MockFileSystem"/>, and against a <see cref="DirFileSystem"/> over an in-memory
/// <see cref="Dir"/>, which shows that the names it is given cannot lead outside.
/// </summary>
public sealed class UploadStoreTests
{
    [Fact]
    public void An_upload_is_stored_under_its_name_in_a_mock_file_system()
    {
        // <mock-file-system>
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory("uploads");
        var store = new UploadStore(fileSystem, "uploads");

        store.Save("avatar.png", [1, 2, 3]);

        Assert.Equal([1, 2, 3], fileSystem.File.ReadAllBytes(fileSystem.Path.Combine("uploads", "avatar.png")));
        // </mock-file-system>
    }

    [Fact]
    public void An_upload_is_stored_under_its_name_in_a_confined_file_system()
    {
        // <dir-file-system>
        var fs = new InMemoryFileSystem();
        using Dir uploads = fs.OpenRoot();
        var store = new UploadStore(new DirFileSystem(uploads), "/");

        store.Save("avatar.png", [1, 2, 3]);

        Assert.Equal([1, 2, 3], fs.ReadAllBytes("avatar.png"));
        // </dir-file-system>
    }

    [Fact]
    public void A_name_that_leads_outside_the_uploads_directory_is_refused()
    {
        // <dir-file-system-escape>
        var fs = new InMemoryFileSystem();
        fs.AddFile("private/secret.txt", "not for uploaders");
        fs.AddSymbolicLink("uploads/shared", "../private", targetIsDirectory: true);
        using Dir uploads = fs.OpenRoot("uploads");
        var store = new UploadStore(new DirFileSystem(uploads), "/");

        Assert.Throws<SandboxEscapeException>(() => store.Load("shared/secret.txt"));
        Assert.Throws<SandboxEscapeException>(() => store.Save("../private/planted.txt", [1]));
        Assert.Equal(["secret.txt"], fs.GetEntries("private"));
        // </dir-file-system-escape>
    }

    [Fact]
    public void The_migrated_component_refuses_the_same_names()
    {
        // <migrated>
        var fs = new InMemoryFileSystem();
        fs.AddFile("private/secret.txt", "not for uploaders");
        fs.AddSymbolicLink("uploads/shared", "../private", targetIsDirectory: true);
        using Dir uploads = fs.OpenRoot("uploads");
        var store = new DirUploadStore(uploads);

        store.Save("avatar.png", [1, 2, 3]);

        Assert.Equal([1, 2, 3], fs.ReadAllBytes("uploads/avatar.png"));
        Assert.Throws<SandboxEscapeException>(() => store.Load("shared/secret.txt"));
        // </migrated>
    }
}
