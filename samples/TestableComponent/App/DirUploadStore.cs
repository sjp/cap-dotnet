using Cap.Std;

namespace TestableComponent;

/// <summary>
/// <see cref="UploadStore"/> rewritten against <see cref="Dir"/>, for code that is being moved
/// off <c>IFileSystem</c> rather than wrapped.
/// </summary>
/// <remarks>
/// It takes <see cref="Dir"/> and not <see cref="IDir"/> because it resolves names chosen by
/// whoever sent the upload, and only the concrete type carries the guarantee that such a name
/// cannot lead outside the directory. There is no directory string left to combine with: the
/// handle is the directory.
/// </remarks>
// <dir-upload-store>
public sealed class DirUploadStore(Dir uploads)
{
    public void Save(string fileName, byte[] contents) => uploads.WriteAllBytes(fileName, contents);

    public byte[] Load(string fileName) => uploads.ReadAllBytes(fileName);
}
// </dir-upload-store>
