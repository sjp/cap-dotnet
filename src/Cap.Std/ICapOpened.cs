namespace Cap.Std;

/// <summary>
/// The members of <see cref="CapOpened"/>, as an interface a test can substitute.
/// </summary>
/// <remarks>
/// <para>
/// What <see cref="IDir.OpenAny"/> returns. <see cref="CapOpened"/> is the one type in this
/// library that implements it, and hands out its handle as the interface it implements when
/// taken through this.
/// </para>
/// <para>
/// <strong>Versioning.</strong> As for <see cref="IDir"/>: a member added to
/// <see cref="CapOpened"/> is added here in the same release, without a default
/// implementation.
/// </para>
/// </remarks>
public interface ICapOpened : IDisposable
{
    /// <inheritdoc cref="CapOpened.IsDirectory"/>
    bool IsDirectory { get; }

    /// <inheritdoc cref="CapOpened.TakeDir()"/>
    IDir TakeDir();

    /// <inheritdoc cref="CapOpened.TakeFile()"/>
    ICapFile TakeFile();
}
