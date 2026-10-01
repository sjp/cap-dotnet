using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Asking what a name holds, without throwing when it holds nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each of these answers about the name and not about what the name points at</strong>,
/// unless it is told to follow a link. A symbolic link is a symbolic link here, whatever it
/// leads to — so a link aimed at a directory answers false to the directory question and true
/// to the link question. That is the same rule the description of a name follows everywhere in
/// this library, and it is the only rule under which the three questions are consistent with
/// each other. The forms of <see cref="IsDir(IDir, string, bool)"/> and
/// <see cref="IsFile(IDir, string, bool)"/> that take <c>followLink</c> ask about the target
/// instead, when the caller says so by name.
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
    /// <remarks>
    /// <para>
    /// Safe to call from any thread: it is a single lookup, and changes nothing.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Components ahead of the last are resolved under the
    /// handle's own policy, so a link among them is followed or refused as it would be for any
    /// other operation, and a refusal answers false. A link as the last component is looked at
    /// rather than followed, so a link to a directory answers false.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsDir(this IDir dir, string path) => Holds(dir, path, followLink: false, CapFileType.Directory);

    /// <summary>Whether a name beneath this handle currently holds an ordinary file.</summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <returns>True when the name holds a file; false when it holds a directory, a link,
    /// something that is neither, or nothing at all.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread: it is a single lookup, and changes nothing.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Components ahead of the last are resolved under the
    /// handle's own policy, so a link among them is followed or refused as it would be for any
    /// other operation, and a refusal answers false. A link as the last component is looked at
    /// rather than followed, so a link to a file answers false.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsFile(this IDir dir, string path) => Holds(dir, path, followLink: false, CapFileType.File);

    /// <summary>
    /// Whether a name beneath this handle currently holds a directory, or a symbolic link that
    /// leads to one when asked to follow it.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <param name="followLink">
    /// Whether a link as the last component is followed and its target asked about. False
    /// gives the same answer as <see cref="IsDir(IDir, string)"/>.
    /// </param>
    /// <returns>True when the name, or what it leads to, holds a directory; false when it holds
    /// anything else, holds nothing, or could not be reached.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread: it is a single lookup, and changes nothing.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Every component is resolved under the handle's own
    /// policy when <paramref name="followLink"/> is set, the last included, so a link is
    /// followed only while its target stays beneath this handle, and a link leading out of it,
    /// a link the policy refuses, a chain that never arrives and a link to nothing all answer
    /// false. Without it a link as the last component is looked at rather than followed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsDir(this IDir dir, string path, bool followLink) =>
        Holds(dir, path, followLink, CapFileType.Directory);

    /// <summary>
    /// Whether a name beneath this handle currently holds an ordinary file, or a symbolic link
    /// that leads to one when asked to follow it.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <param name="followLink">
    /// Whether a link as the last component is followed and its target asked about. False
    /// gives the same answer as <see cref="IsFile(IDir, string)"/>.
    /// </param>
    /// <returns>True when the name, or what it leads to, holds a file; false when it holds a
    /// directory, something that is neither, or nothing at all, or could not be reached.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread: it is a single lookup, and changes nothing.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> As for <see cref="IsDir(IDir, string, bool)"/>: with
    /// <paramref name="followLink"/> set, a link is followed only while its target stays beneath
    /// this handle and under its policy, and one that leads out, is refused or leads nowhere
    /// answers false.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsFile(this IDir dir, string path, bool followLink) =>
        Holds(dir, path, followLink, CapFileType.File);

    /// <summary>Whether a name beneath this handle currently holds a symbolic link.</summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <returns>True when the name holds a symbolic link, whether or not its target exists
    /// and whether or not the target lies outside this handle's authority.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Symbolic links.</strong> Only components ahead of the last one are resolved,
    /// and they are resolved under the handle's own policy — so a link in the middle of the
    /// path is followed or refused as it would be for any other operation, while the last
    /// component is looked at rather than followed.
    /// </para>
    /// <para>
    /// Safe to call from any thread: it is a single lookup, and changes nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool IsSymlink(this IDir dir, string path) => Holds(dir, path, followLink: false, CapFileType.Symlink);

    /// <summary>Whether the name holds the kind asked about.</summary>
    private static bool Holds(IDir dir, string path, bool followLink, CapFileType type)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(path);

        CapMetadata metadata;
        bool described = followLink
            ? dir.TryGetMetadata(path, followLink: true, out metadata)
            : dir.TryGetMetadata(path, out metadata);

        return described && metadata.Type == type;
    }
}
