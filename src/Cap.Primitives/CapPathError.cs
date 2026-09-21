namespace Cap.Primitives;

/// <summary>
/// Why a caller-supplied path was refused.
/// </summary>
/// <remarks>
/// <para>
/// Every value other than <see cref="None"/> means the path was rejected outright: no handle
/// is opened, and nothing was touched on disk. The distinctions exist so a caller can tell
/// "you passed an absolute path" from "that name is reserved" — they describe the shape of
/// the input, never anything about the filesystem, so reporting them to an untrusted caller
/// leaks nothing about the host.
/// </para>
/// <para>
/// The values are deliberately fine-grained rather than a single <c>Invalid</c>. A sandbox
/// that answers every malformed input identically is harder to use correctly, and the
/// pressure that creates — callers guessing, or pre-"cleaning" paths with string edits
/// before handing them over — produces exactly the lexical rewriting this library exists to
/// avoid.
/// </para>
/// </remarks>
public enum CapPathError
{
    /// <summary>The path parsed successfully.</summary>
    None = 0,

    /// <summary>
    /// The path was empty, or consisted only of separators and <c>.</c> components. A path
    /// naming nothing cannot be resolved against a directory handle; the caller most likely
    /// meant the directory itself, which it already holds.
    /// </summary>
    Empty,

    /// <summary>
    /// The path is rooted: <c>/etc/passwd</c>, or <c>C:\Windows</c> on Windows. Absolute
    /// paths name a location by starting from a filesystem root the handle does not confer
    /// authority over, so they are never interpreted relative to it.
    /// </summary>
    Absolute,

    /// <summary>
    /// A Windows path beginning with a separator but no drive (<c>\file</c>). It is relative
    /// to the current drive — process-wide ambient state — and so is not a relative path in
    /// any sense this library can honour.
    /// </summary>
    RootRelative,

    /// <summary>
    /// A Windows drive-relative path (<c>C:file</c>), which resolves against the per-drive
    /// working directory the process happens to be carrying. That is ambient authority
    /// wearing the syntax of a relative path, which makes it more dangerous than a plainly
    /// absolute one, not less.
    /// </summary>
    DriveRelative,

    /// <summary>
    /// A UNC path (<c>\\server\share</c>). It names a host, not a location beneath this
    /// handle, and resolving one would reach the network.
    /// </summary>
    Unc,

    /// <summary>
    /// A Windows device-namespace prefix: <c>\\?\</c>, <c>\\.\</c>, or their forward-slash
    /// spellings. These bypass the Win32 path normalisation layer entirely and address the
    /// object manager directly.
    /// </summary>
    DeviceNamespace,

    /// <summary>
    /// A component names a Windows character device — <c>CON</c>, <c>NUL</c>, <c>COM1</c>
    /// and friends — in any of its manglings. Such a name does not refer to a file beneath
    /// this handle at all: the object manager routes it to the device wherever it appears.
    /// </summary>
    ReservedName,

    /// <summary>
    /// A component contains a character that cannot appear in a filename on this platform,
    /// or that would change meaning on the way to the kernel. Always includes <c>U+0000</c>,
    /// which truncates the path at the syscall boundary on every platform.
    /// </summary>
    InvalidCharacter,

    /// <summary>
    /// A component ends in a dot or a space, which Windows silently strips. <c>foo.</c> and
    /// <c>foo</c> are the same file there, so any check that treats them as distinct names
    /// can be walked straight past.
    /// </summary>
    TrailingDotOrSpace,

    /// <summary>
    /// A <c>..</c> component, under the default policy of refusing them at the boundary.
    /// It is never collapsed lexically, because <c>a/../b</c> is <c>/b</c> when <c>a</c> is
    /// a symlink to <c>/</c> — the rewrite would have to be undone by the kernel, and the
    /// kernel does not know a rewrite happened.
    /// </summary>
    ParentLink,

    /// <summary>
    /// The path, or one of its components, exceeds the length this parser will consider.
    /// The kernel imposes its own, stricter limits in bytes; this bound only guarantees that
    /// parsing cannot be made to do unbounded work.
    /// </summary>
    TooLong,
}
