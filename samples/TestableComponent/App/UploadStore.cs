using System.IO.Abstractions;

namespace TestableComponent;

/// <summary>
/// Stores uploaded files under the names their senders gave them. Written against
/// <see cref="IFileSystem"/>, as a lot of existing code is.
/// </summary>
/// <remarks>
/// Nothing here checks the name, and nothing needs to change for it to be confined: the
/// composition root hands it a <c>DirFileSystem</c> over the uploads directory rather than
/// the ambient <c>FileSystem</c>, and every path it builds is then resolved beneath that
/// directory. A name that climbs out, or leads through a link that does, is refused.
/// </remarks>
// <upload-store>
public sealed class UploadStore(IFileSystem fileSystem, string directory)
{
    public void Save(string fileName, byte[] contents) =>
        fileSystem.File.WriteAllBytes(fileSystem.Path.Combine(directory, fileName), contents);

    public byte[] Load(string fileName) =>
        fileSystem.File.ReadAllBytes(fileSystem.Path.Combine(directory, fileName));
}
// </upload-store>
