using System.Runtime.InteropServices;
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
    /// How many times a confined open has been retried after the kernel reported that
    /// resolution lost a race with a concurrent rename.
    /// </summary>
    /// <remarks>Zero wherever there is no confined open to retry.</remarks>
    long ConfinedOpenRaceRetries { get; }

    /// <summary>
    /// How many single-name opens have been issued beneath an existing handle in this
    /// process.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="ConfinedOpenAttempts"/>: a walk spends one of these per
    /// name, and a confined resolution spends none, so between them the two show which
    /// strategy resolution actually took rather than which one was selected.
    /// </remarks>
    long ComponentOpens { get; }

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
    /// The place this system puts scratch files, as an ordinary path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ambient, like <see cref="OpenAmbientDirectory"/> and for the same reason: the answer
    /// comes from the process's own environment, so a caller that acts on it is reaching for
    /// something nobody handed it. It is a member here rather than a framework call because
    /// each platform answers from a different place — an environment variable the platform
    /// names, and a fallback the platform decides — and because the answer has to be
    /// substitutable for a test that has no real filesystem.
    /// </para>
    /// <para>
    /// The path is not checked, resolved or opened. What comes back is a string to open, and
    /// whether it names a directory at all is answered by opening it.
    /// </para>
    /// </remarks>
    CapResult<string> GetSystemTemporaryDirectory();

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
    /// <paramref name="parent"/>, as <paramref name="request"/> describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one member here that may bring something into existence, and it may only do so
    /// for the single name it is given. That is the whole of the concession: a walk still
    /// cannot create the directories it passes through, because every step above this one
    /// opens what is already there. Creation is a property of the last component, so it is
    /// expressed on the call that touches the last component and nowhere else.
    /// </para>
    /// <para>
    /// Still refuses to follow a link in the name, like every other member. A request that
    /// creates and finds a link in the way is reported as
    /// <see cref="CapErrorCategory.SymbolicLink"/> rather than resolved, so the decision
    /// about whether to follow it is made by the caller, under the caller's policy, with the
    /// target re-resolved from the sandbox root.
    /// </para>
    /// <para>
    /// Reports <see cref="CapErrorCategory.NotSupported"/> for anything in the request this
    /// platform cannot honour, rather than opening a handle that behaves differently from
    /// the one that was asked for.
    /// </para>
    /// </remarks>
    CapResult<SafeFileHandle> OpenChildFile(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        in FileOpenRequest request);

    /// <summary>
    /// Creates a file directly beneath <paramref name="parent"/> that has no name.
    /// </summary>
    /// <param name="parent">The directory the file's storage comes from.</param>
    /// <param name="access">The data access the handle carries.</param>
    /// <remarks>
    /// <para>
    /// Not a file with a name nobody knows: a file with no entry in any directory at all.
    /// Nothing can open it, rename it, replace it or plant a link where it sits, because
    /// there is nowhere for any of that to be aimed — the handle returned is the only
    /// reference to it, and when the last such handle closes the storage goes back. That
    /// makes it the only kind of scratch file no other account on the machine can interfere
    /// with, whatever the permissions on the directory it came from.
    /// </para>
    /// <para>
    /// <strong>Reports <see cref="CapErrorCategory.NotSupported"/> wherever a nameless file
    /// is not what the platform actually produces.</strong> That includes platforms with no
    /// such facility and filesystems that do not implement it, and it is reported rather than
    /// approximated: a named file created and immediately unlinked is a different object with
    /// a different exposure, and quietly substituting one would hand a caller who asked for
    /// the unattackable thing something attackable. Falling back is the caller's decision to
    /// make, in the open.
    /// </para>
    /// </remarks>
    CapResult<SafeFileHandle> OpenAnonymousChildFile(SafeDirHandle parent, FileAccess access);

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
    /// <remarks>
    /// <para>
    /// Carries the whole of <paramref name="request"/>, creation included. Doing the creation
    /// in the same operation as the resolution is the point: dividing the path so that the
    /// last component could be created separately would resolve the prefix in one backend and
    /// the name in another, and the instant between them is exactly the window this backend
    /// exists to close.
    /// </para>
    /// <para>
    /// A link at the last component is followed only when the request says so
    /// (<see cref="FileOpenRequest.FollowsFinalLink"/>); otherwise it is refused as
    /// <see cref="CapErrorCategory.SymbolicLinkLoop"/>, which is what the walk reports for the
    /// same link.
    /// </para>
    /// </remarks>
    CapResult<SafeFileHandle> OpenConfinedFile(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        in FileOpenRequest request,
        ConfinedResolveOptions options);

    /// <summary>
    /// Begins a read of what <paramref name="directory"/> holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader gets an object of its own rather than a copy of this handle. The position
    /// a directory read advances belongs to the open object on every platform here, so a
    /// copy would share it — two enumerations through one handle would consume each other's
    /// entries, and a second enumeration would start wherever the first stopped.
    /// </para>
    /// <para>
    /// <strong>It confers no authority the handle did not already carry.</strong> The object
    /// is reached through the handle rather than by naming the directory again, and the
    /// access is restated rather than asked for afresh: a handle held only in order to
    /// resolve names beneath it cannot be turned into one that lists them, whatever the
    /// directory's own permissions would allow. Such a handle is refused here with
    /// <see cref="CapErrorCategory.PermissionDenied"/>.
    /// </para>
    /// <para>
    /// Reports <see cref="CapErrorCategory.NotFound"/> where the directory has been removed
    /// since the handle was opened, which is an ordinary outcome rather than a failure of
    /// the handle: what the handle refers to still exists, but a directory with no name left
    /// cannot be read.
    /// </para>
    /// </remarks>
    CapResult<DirectoryReader> OpenDirectoryReader(SafeDirHandle directory);

    /// <summary>
    /// Reads the target of the symbolic link named by <paramref name="name"/> directly
    /// beneath <paramref name="parent"/>, without following it.
    /// </summary>
    /// <remarks>
    /// The target is returned exactly as stored. It is attacker-controlled data — anything
    /// that can write inside the sandbox can plant one — so it is parsed by the same path
    /// parser as a caller-supplied string, and is not assumed to be relative, to be short,
    /// or to be well-formed text.
    /// <para>
    /// Reports <see cref="CapErrorCategory.NotALink"/>, on every platform, when the name
    /// holds something other than a symbolic link.
    /// </para>
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
    /// Reports everything a caller is told about the entry named by <paramref name="name"/>
    /// beneath <paramref name="parent"/>, without following it if it is a link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The richer sibling of <see cref="StatChild"/>, and separate from it because the two
    /// are asked in different circumstances. <see cref="StatChild"/> is on the path of every
    /// component a walk takes, asks for the least the walk can decide from, and accepts
    /// whatever a network filesystem has cached — a type and an identity do not go stale in
    /// a way that matters, and a walk that forced a revalidation per component would make a
    /// deep path expensive in round trips rather than in syscalls. This one is asked once,
    /// on purpose, by a caller who wants the length and the times to be current, so it
    /// accepts the revalidation.
    /// </para>
    /// <para>
    /// It also distinguishes more kinds. A walk collapses sockets, pipes and device nodes
    /// into a single "cannot be stepped through" case; a caller has reasons to tell them
    /// apart, and this is where the distinction survives.
    /// </para>
    /// </remarks>
    CapError DescribeChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeStat stat);

    /// <summary>
    /// Reports everything a caller is told about what an already-open handle refers to.
    /// </summary>
    /// <param name="handle">
    /// An open directory or file handle. Both are accepted because the question is the same
    /// one and the answer comes from the same call; what distinguishes them is a field of
    /// the answer rather than a different way of asking.
    /// </param>
    /// <param name="stat">The snapshot, when this succeeds.</param>
    /// <remarks>
    /// Asked of the object rather than of a name, so nothing here re-resolves anything and
    /// the answer describes what was opened even if the name it was opened by now belongs to
    /// something else — or to nothing at all, a handle to an unlinked file being perfectly
    /// answerable.
    /// </remarks>
    CapError DescribeHandle(SafeHandle handle, out CapNodeStat stat);

    /// <summary>
    /// Asks the system what path an open directory handle is currently reachable by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For diagnostics alone, and the one member here whose answer must never be acted on.
    /// Nothing in this library resolves anything against it: a path is a name for an object
    /// rather than the object itself, and by the time the answer is read the name may belong
    /// to something else, may have been unlinked, or may be one of several the object
    /// answers to. Every guarantee this layer makes rests on handles for exactly that
    /// reason, and a call that turned a handle back into a string and then reopened it would
    /// undo all of them.
    /// </para>
    /// <para>
    /// Answering also requires reaching outside the subtree — the reply names the object
    /// from the root of a filesystem the handle confers no authority over — so this is
    /// authority the caller must have obtained separately. It is reported as
    /// <see cref="CapErrorCategory.NotSupported"/> where the platform has no way to ask.
    /// </para>
    /// </remarks>
    CapResult<string> GetHandlePath(SafeDirHandle handle);

    /// <summary>
    /// Asks the filesystem to make a directory's own record of what it holds durable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step that finishes a publish. Writing a file's contents durably says nothing
    /// about the name that reaches them: the entry that binds the name to the object lives
    /// in the directory, and until the directory itself has been committed a crash can leave
    /// a fully written object that nothing refers to, or a name that refers to nothing. Every
    /// operation that promises a file will be there after the power goes out therefore ends
    /// here rather than at the file.
    /// </para>
    /// <para>
    /// Reports <see cref="CapErrorCategory.NotSupported"/> where the platform offers no such
    /// request, rather than succeeding at nothing. Windows is that platform for any process
    /// without volume-level privilege, so a caller asking for the strongest durability there
    /// has to be told it did not get it and decide what that means.
    /// </para>
    /// </remarks>
    CapError SyncDirectory(SafeDirHandle directory);

    /// <summary>
    /// Writes to an already-open object the permissions this platform records.
    /// </summary>
    /// <param name="handle">An open directory or file handle, as for
    /// <see cref="DescribeHandle"/>.</param>
    /// <param name="unixMode">The mode bits to set, or null when the value came from
    /// elsewhere.</param>
    /// <param name="windowsAttributes">The attribute bits to set, or null when the value
    /// came from elsewhere.</param>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="DescribeHandle"/>, and nullable in the same way and for the
    /// same reason: the two platforms record different things, and a value carrying the
    /// other one's is not a value this can apply. A platform handed nothing it understands
    /// reports <see cref="CapErrorCategory.NotSupported"/> rather than inventing a mapping —
    /// a Unix mode guessed from a read-only flag would be a permission nobody chose.
    /// </para>
    /// <para>
    /// Acts on the object rather than on a name, which is what makes it usable during a
    /// copy: the destination is already open, so there is no second lookup for anything to
    /// be substituted in.
    /// </para>
    /// </remarks>
    CapError SetHandlePermissions(
        SafeHandle handle,
        UnixFileMode? unixMode,
        FileAttributes? windowsAttributes);

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

    /// <summary>
    /// Produces a second, independent handle to the same open file.
    /// </summary>
    /// <remarks>
    /// A copy of the handle rather than a second open of the name: it refers to the object
    /// this one refers to, with the access this one was granted, and nothing that happens to
    /// the name in the meantime can change which object that is. Re-opening would ask the
    /// filesystem to resolve a name again, which is the one thing a capability handle exists
    /// to avoid having to do.
    /// </remarks>
    CapResult<SafeFileHandle> DuplicateFile(SafeFileHandle handle);

    /// <summary>
    /// Creates a directory named <paramref name="name"/> directly beneath
    /// <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// Fails with <see cref="CapErrorCategory.AlreadyExists"/> when the name is taken, by
    /// anything at all — an existing directory, a file, a symbolic link whose target happens
    /// to be a directory. The name is what is being claimed, so what currently holds it does
    /// not change the answer, and the refusal comes from the one call that makes the
    /// directory rather than from a lookup before it.
    /// </remarks>
    /// <param name="parent">The directory the new one is created in.</param>
    /// <param name="name">The single component to claim.</param>
    /// <param name="visibility">
    /// How much of the rest of the machine may see into the new directory. Asked for in the
    /// same call that creates it, so there is no instant in which it exists more widely
    /// readable than it was meant to be.
    /// </param>
    CapError CreateChildDirectory(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        CreationVisibility visibility);

    /// <summary>
    /// Clears the flag that makes the entry named by <paramref name="name"/> beneath
    /// <paramref name="parent"/> refuse to be removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For Windows, where the read-only attribute is a property of the file itself and stops
    /// the file being deleted by an account that is otherwise entitled to delete it. Clearing
    /// it is what every tool that empties a directory has to do, and it is a separate call
    /// because it is worth paying for only on the removal that has already failed.
    /// </para>
    /// <para>
    /// Reports <see cref="CapErrorCategory.NotSupported"/> where no such flag exists. On Unix
    /// there is none to clear: whether a name can be removed is decided by the permissions on
    /// the directory holding it and not by the mode of the object it names, so a removal that
    /// failed there did so for a reason this would not fix.
    /// </para>
    /// <para>
    /// Acts on the name and never on what the name points at, like every other member here.
    /// </para>
    /// </remarks>
    CapError ClearChildRemovalBlock(SafeDirHandle parent, ReadOnlySpan<char> name);

    /// <summary>
    /// Removes the non-directory entry named by <paramref name="name"/> directly beneath
    /// <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// Removes the name and never what a name points at: a symbolic link here is unlinked
    /// itself, and its target is not touched or even looked at. A handle whose policy
    /// refuses to follow links can therefore still delete one, which is the case that
    /// matters — a caller that distrusts the links in a subtree needs above all to be able
    /// to clear them out.
    /// </remarks>
    CapError RemoveChildFile(SafeDirHandle parent, ReadOnlySpan<char> name);

    /// <summary>
    /// Removes the directory named by <paramref name="name"/> directly beneath
    /// <paramref name="parent"/>, which must be empty.
    /// </summary>
    /// <remarks>
    /// Reports <see cref="CapErrorCategory.NotEmpty"/> rather than removing what is inside.
    /// Recursive removal is a walk, and a walk is something built on top of this from
    /// handles, never something a single call is quietly allowed to do.
    /// </remarks>
    CapError RemoveChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name);

    /// <summary>
    /// Moves the entry named by <paramref name="fromName"/> beneath
    /// <paramref name="fromParent"/> to <paramref name="toName"/> beneath
    /// <paramref name="toParent"/>.
    /// </summary>
    /// <param name="fromParent">The directory the entry is in now.</param>
    /// <param name="fromName">The name it has now.</param>
    /// <param name="toParent">The directory it is to end up in.</param>
    /// <param name="toName">The name it is to have there.</param>
    /// <param name="replaceExisting">
    /// Whether an entry already holding the destination name is replaced. When false the
    /// operation fails with <see cref="CapErrorCategory.AlreadyExists"/> instead, and the
    /// refusal is made by the same call that does the move rather than by a lookup before
    /// it — a check followed by a rename would have a window in which the destination could
    /// appear.
    /// </param>
    /// <remarks>
    /// <para>
    /// Both ends are named relative to a directory handle, so the operation needs both
    /// handles and is exactly as confined as the two capabilities that were combined to
    /// perform it. The handles may be the same one.
    /// </para>
    /// <para>
    /// A move between filesystems is reported as <see cref="CapErrorCategory.CrossDevice"/>
    /// and never emulated by copying. A copy is a different operation with different
    /// failure modes, different timing and a different result for a hard link, and
    /// performing one under the name of a rename would make an operation callers rely on to
    /// be atomic silently stop being so.
    /// </para>
    /// <para>
    /// A platform that cannot refuse an existing destination atomically reports
    /// <see cref="CapErrorCategory.NotSupported"/> rather than falling back to a check.
    /// </para>
    /// </remarks>
    CapError RenameChild(
        SafeDirHandle fromParent,
        ReadOnlySpan<char> fromName,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName,
        bool replaceExisting);

    /// <summary>
    /// Creates a symbolic link named <paramref name="name"/> beneath
    /// <paramref name="parent"/>, storing <paramref name="target"/> as its target.
    /// </summary>
    /// <param name="parent">The directory the link is created in.</param>
    /// <param name="name">The name the link is given.</param>
    /// <param name="target">The text stored as the link's target.</param>
    /// <param name="targetIsDirectory">
    /// Whether the link is to be created as a link to a directory. Ignored where links are
    /// untyped, which is every platform but Windows; there, a link records which kind it is
    /// and one created as the wrong kind cannot be traversed at all.
    /// </param>
    /// <remarks>
    /// <para>
    /// The target is stored exactly as given and is not resolved, validated against the
    /// subtree, or required to exist. Containment is enforced where the link is followed:
    /// resolution reads the stored text and refuses it there if it leaves, whoever wrote it
    /// and whatever wrote it. Refusing a target here would add nothing to that and would
    /// refuse links that are legitimate — the same relative target escapes or does not
    /// depending on where the link ends up, which is not knowable when it is created.
    /// </para>
    /// <para>
    /// Reports <see cref="CapErrorCategory.NotSupported"/> where the filesystem has no
    /// symbolic links, and <see cref="CapErrorCategory.PermissionDenied"/> where creating
    /// one needs a privilege the process does not hold.
    /// </para>
    /// </remarks>
    CapError CreateChildSymbolicLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> target,
        bool targetIsDirectory);

    /// <summary>
    /// Creates a second name, <paramref name="toName"/> beneath <paramref name="toParent"/>,
    /// for the object already named by <paramref name="name"/> beneath
    /// <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// Acts on the name it is given rather than on what that name points at, so a hard link
    /// made to a symbolic link is a second name for the link and not for its target. Both
    /// ends are relative to a directory handle for the same reason a rename's are: joining
    /// two places together requires authority over both.
    /// </remarks>
    CapError CreateChildHardLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName);
}
