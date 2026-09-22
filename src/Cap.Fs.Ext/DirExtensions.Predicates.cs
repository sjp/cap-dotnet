using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Asking what a name holds, without throwing when it holds nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each of these answers about the name and not about what the name points at.</strong>
/// A symbolic link is a symbolic link here, whatever it leads to — so a link aimed at a
/// directory answers false to the directory question and true to the link question, and a
/// caller who wants to know about the target opens the target and asks the handle they get
/// back. That is the same rule the description of a name follows everywhere in this library,
/// and it is the only rule under which the three questions are consistent with each other.
/// </para>
/// <para>
/// <strong>An answer is about an instant that has already passed.</strong> By the time one of
/// these returns, the name may hold something else; nothing here can be relied on to decide
/// whether a later operation will succeed, and code that asks one of these in order to choose
/// which operation to attempt has written the check-then-act race this library exists to
/// remove. The way to find out whether an open will work is to attempt the open. These are
/// for reporting, for deciding how to display something, and for the cases where being wrong
/// costs nothing.
/// </para>
/// </remarks>
public static partial class DirExtensions
{
    /// <summary>Whether a name beneath this handle currently holds a directory.</summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <returns>True when the name holds a directory; false when it holds anything else,
    /// holds nothing, or could not be reached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsDir(this Dir dir, string path) => Holds(dir, path, CapFileType.Directory);

    /// <summary>Whether a name beneath this handle currently holds an ordinary file.</summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <returns>True when the name holds a file; false when it holds a directory, a link,
    /// something that is neither, or nothing at all.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsFile(this Dir dir, string path) => Holds(dir, path, CapFileType.File);

    /// <summary>Whether a name beneath this handle currently holds a symbolic link.</summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <returns>True when the name holds a symbolic link, whether or not its target exists
    /// and whether or not the target lies outside this handle's authority.</returns>
    /// <remarks>
    /// Only components ahead of the last one are resolved, and they are resolved under the
    /// handle's own policy — so a link in the middle of the path is followed or refused as it
    /// would be for any other operation, while the last component is looked at rather than
    /// followed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsSymlink(this Dir dir, string path) => Holds(dir, path, CapFileType.Symlink);

    /// <summary>Whether the name holds the kind asked about.</summary>
    private static bool Holds(Dir dir, string path, CapFileType type)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(path);

        return dir.TryGetMetadata(path, out CapMetadata metadata) && metadata.Type == type;
    }
}
