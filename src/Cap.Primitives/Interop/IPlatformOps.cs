using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop;

/// <summary>
/// The whole of the filesystem surface that confined resolution is built on.
/// </summary>
/// <remarks>
/// <para>
/// Two reasons this is an interface rather than a static class of imports.
/// </para>
/// <para>
/// The first is that the security-relevant logic — the walk that decides which steps are
/// allowed — should be testable without a filesystem. The interesting cases for that logic
/// are races: a component that is a directory when it is looked at and a symlink when it is
/// opened. Provoking those against a real kernel means running the attack in a loop and
/// hoping to hit the window. Against a simulated filesystem that can be mutated between any
/// two calls, the same case is a deterministic test.
/// </para>
/// <para>
/// The second is that the platforms disagree about enough that a single set of imports
/// would be a nest of runtime branches. Keeping them behind one narrow contract means the
/// resolver is written once and the disagreements stay in the implementations.
/// </para>
/// <para>
/// <strong>Every name here is a single path component unless the member says otherwise,</strong>
/// and every open refuses to follow a symbolic link in the name it is given. That is not a
/// convenience default: a walk that lets the kernel follow a link has delegated the
/// containment decision to something that does not know where the sandbox root is. Links
/// are read explicitly, by <see cref="ReadChildLink"/>, and re-resolved by the caller.
/// </para>
/// </remarks>
internal interface IPlatformOps
{
    /// <summary>What this platform can do. Probed once and cached.</summary>
    PlatformCapabilities Capabilities { get; }

    /// <summary>
    /// How many times a confined, kernel-atomic open has been attempted in this process.
    /// </summary>
    /// <remarks>
    /// Exists so that a build configured to exercise the fallback can prove it did. A
    /// forced-fallback test run that quietly keeps taking the fast path tests nothing at
    /// all, and the only way to tell from the outside is to count.
    /// </remarks>
    long ConfinedOpenAttempts { get; }

    /// <summary>
    /// Opens a directory by an ordinary path, with the process's ambient authority.
    /// </summary>
    /// <remarks>
    /// The one member here that is not confined to anything, and the only way a first
    /// directory handle can come into existence. Everything the containment guarantee
    /// promises begins after this call returns; the path is resolved with the same rules,
    /// and the same exposure, as any other program opening any other path.
    /// </remarks>
    CapResult<SafeDirHandle> OpenAmbientDirectory(string path, CapAccess access);

    /// <summary>
    /// Opens the directory named by <paramref name="name"/> directly beneath
    /// <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// Fails with <see cref="CapErrorCategory.SymbolicLink"/> when the name is a link,
    /// rather than following it, and with <see cref="CapErrorCategory.NotADirectory"/> when
    /// it is not a directory. Both are ordinary outcomes during a walk.
    /// </remarks>
    CapResult<SafeDirHandle> OpenChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name, CapAccess access);

    /// <summary>
    /// Opens the file named by <paramref name="name"/> directly beneath
    /// <paramref name="parent"/>. The file must already exist.
    /// </summary>
    /// <remarks>
    /// Creation, truncation and append are absent on purpose: they belong to the file API,
    /// which applies them to a name resolution has already confined. Mixing them in here
    /// would mean the walk could create things while it was still deciding whether it was
    /// allowed to look at them.
    /// </remarks>
    CapResult<SafeFileHandle> OpenChildFile(SafeDirHandle parent, ReadOnlySpan<char> name, CapAccess access);

    /// <summary>
    /// Resolves a whole relative path beneath <paramref name="root"/> in one operation that
    /// the kernel guarantees cannot leave that subtree, and opens the directory it names.
    /// </summary>
    /// <param name="root">The subtree resolution is pinned to.</param>
    /// <param name="path">
    /// A relative path of one or more components. Unlike every other member, this one is not
    /// limited to a single component — resolving the whole path at once is the entire point.
    /// </param>
    /// <param name="access">The authority the resulting handle carries.</param>
    /// <param name="options">Policy applied on top of confinement.</param>
    /// <remarks>
    /// <para>
    /// Callable only when <see cref="PlatformCapabilities.SupportsConfinedOpen"/> is true;
    /// otherwise it reports <see cref="CapErrorCategory.NotSupported"/>.
    /// </para>
    /// <para>
    /// An attempt to leave the subtree is reported as <see cref="CapErrorCategory.Escaped"/>,
    /// and a symbolic link the caller's policy forbids as
    /// <see cref="CapErrorCategory.SymbolicLinkLoop"/>. Neither reaches the caller as a
    /// successful open, which is what makes this the backend with no race window.
    /// </para>
    /// <para>
    /// <strong>Implementations retry internally, a bounded number of times, when the kernel
    /// reports that resolution lost a race with a concurrent rename.</strong> That is a
    /// normal outcome of confined resolution under mutation and not a failure; surfacing it
    /// would make every caller write the same retry loop, and a caller that forgot would
    /// turn a busy directory into spurious errors. It is never returned as
    /// <see cref="CapErrorCategory.Raced"/> unless the attempt budget is exhausted.
    /// </para>
    /// </remarks>
    CapResult<SafeDirHandle> OpenConfinedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options);

    /// <summary>
    /// The file counterpart of <see cref="OpenConfinedDirectory"/>, with the same confinement,
    /// the same reporting and the same internal retry.
    /// </summary>
    CapResult<SafeFileHandle> OpenConfinedFile(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options);

    /// <summary>
    /// Reads the target of the symbolic link named by <paramref name="name"/> directly
    /// beneath <paramref name="parent"/>, without following it.
    /// </summary>
    /// <remarks>
    /// The target is returned exactly as stored. It is attacker-controlled data — anything
    /// that can write inside the sandbox can plant one — so it is parsed by the same path
    /// parser as a caller-supplied string, and is not assumed to be relative, to be short,
    /// or to be well-formed text.
    /// </remarks>
    CapResult<string> ReadChildLink(SafeDirHandle parent, ReadOnlySpan<char> name);

    /// <summary>
    /// Reports what the entry named by <paramref name="name"/> beneath
    /// <paramref name="parent"/> is, without following it if it is a link.
    /// </summary>
    CapError StatChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeInfo info);

    /// <summary>
    /// Reports what an already-open handle refers to.
    /// </summary>
    /// <remarks>
    /// Asked after an open, not before: comparing the identity of what was opened against
    /// the identity of what was looked at is how a walk notices that the two were not the
    /// same object. Comparing names instead would compare the one thing an attacker can
    /// reassign.
    /// </remarks>
    CapError StatHandle(SafeDirHandle handle, out CapNodeInfo info);

    /// <summary>
    /// Produces a second, independent handle to the same directory.
    /// </summary>
    /// <remarks>
    /// The copy refers to the same object even if the name it was opened by is reassigned,
    /// which is what lets a walk hold on to where it has been. Upward movement is
    /// implemented by keeping handles like these, never by asking the kernel to resolve a
    /// parent, because the parent of an open directory is whatever a concurrent rename last
    /// made it.
    /// </remarks>
    CapResult<SafeDirHandle> DuplicateDirectory(SafeDirHandle handle);
}
