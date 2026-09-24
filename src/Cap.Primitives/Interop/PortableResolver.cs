using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop;

/// <summary>
/// Resolves a path beneath a directory handle one component at a time, on platforms where
/// the kernel will not do it in a single confined operation.
/// </summary>
/// <remarks>
/// <para>
/// This is the backend for everything but Linux with a working <c>openat2</c>: older
/// kernels, containers whose seccomp filter removes that syscall, and macOS. It is also the
/// model the Windows backend follows, with its own per-step call. It is therefore not a
/// legacy path but the one a large share of real deployments take, and the one that has to
/// be got right.
/// </para>
/// <para>
/// The shape is small and the reasons it is that shape are not:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <strong>Every step refuses to follow links.</strong> A single intermediate open that
/// followed one would have handed the containment decision to the kernel, which does not
/// know where the sandbox root is. Links are read here instead, and their targets walked as
/// part of the same resolution, so that the same root test applies to a component a link
/// introduced as to one the caller wrote.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Upward movement pops a handle.</strong> It never asks the kernel to resolve
/// <c>..</c>, which would answer with the parent of whatever the open directory is at that
/// instant — a thing a concurrent rename can change, and change to somewhere outside.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>Nothing lexical happens.</strong> <c>a/../b</c> is not rewritten to <c>b</c>; the
/// walk enters <c>a</c>, steps back out of it, and then enters <c>b</c>, which is the only
/// reading that stays correct when <c>a</c> is a link.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>What it does not give you.</strong> Each step is safe and each step is anchored
/// to a handle already held, so an attacker cannot redirect resolution to a different
/// subtree — but the sequence is not atomic. Between two steps a directory inside the
/// sandbox can be replaced by another directory inside the sandbox, and the walk will
/// continue into the replacement. The guarantee is therefore "the result is beneath the
/// root", not "the result is the object the path named when the call started". The confined
/// open on Linux gives both; this gives the first. Callers that need to know which they have
/// can ask which backend is in use.
/// </para>
/// </remarks>
internal static class PortableResolver
{
    /// <summary>
    /// How many symbolic links one resolution may follow before giving up.
    /// </summary>
    /// <remarks>
    /// Matched to the limit Linux applies to its own resolution, so that a chain the kernel
    /// would have refused is refused here too and a chain it would have followed still works.
    /// A number of our own would make the same tree resolvable on one backend and not on
    /// another, which is the kind of difference that is only ever discovered in production.
    /// </remarks>
    public const int MaxSymbolicLinks = 40;

    /// <summary>
    /// How many directories below the root the walk will descend into.
    /// </summary>
    /// <remarks>
    /// A bound on descriptors rather than on nesting as such. Every level is a handle that
    /// stays open until the walk finishes, so without a limit a single path of a few
    /// thousand components — cheap to send, and well inside the length a path may have —
    /// would consume the process's entire descriptor budget and make unrelated opens fail
    /// elsewhere in the program. The limit is far deeper than any real directory tree and
    /// well under the descriptor allowance a process is normally given.
    /// </remarks>
    public const int MaxDepth = 256;

    /// <summary>
    /// Opens the directory that <paramref name="path"/> names beneath
    /// <paramref name="root"/>.
    /// </summary>
    public static CapResult<SafeDirHandle> OpenDirectory(
        SafeDirHandle root,
        scoped in CapPath path,
        CapAccess access,
        ConfinedResolveOptions options)
    {
        CapError error = Walk(
            root, path, ResolutionTarget.Directory, access, FileOpenRequest.Existing(FileAccess.Read),
            options, out Outcome outcome);
        return error.IsFailure
            ? CapResult<SafeDirHandle>.Fail(error)
            : CapResult<SafeDirHandle>.Ok(outcome.Directory!);
    }

    /// <summary>
    /// Opens the file that <paramref name="path"/> names beneath <paramref name="root"/>,
    /// creating it if <paramref name="request"/> says it may be created.
    /// </summary>
    /// <remarks>
    /// The creation belongs to the last step and to nothing before it. Every component ahead
    /// of the last is opened as something that already exists, so a path whose middle is
    /// missing fails as missing rather than being brought into being a directory at a time —
    /// and the one call that can create is the one call that can also refuse a name already
    /// taken, which is what makes an exclusive create exclusive.
    /// </remarks>
    public static CapResult<SafeFileHandle> OpenFile(
        SafeDirHandle root,
        scoped in CapPath path,
        scoped in FileOpenRequest request,
        ConfinedResolveOptions options)
    {
        CapError error = Walk(
            root, path, ResolutionTarget.File, CapAccess.None, in request, options, out Outcome outcome);

        return error.IsFailure
            ? CapResult<SafeFileHandle>.Fail(error)
            : CapResult<SafeFileHandle>.Ok(outcome.File!);
    }

    /// <summary>
    /// Resolves everything but the last component, and hands back the directory that
    /// component would be looked up in together with the component itself.
    /// </summary>
    /// <remarks>
    /// The last component is not looked at, so it may be absent, and if it is a symbolic
    /// link it stays one. That is what the operations this serves need: an exclusive create
    /// has to see the name is taken, a removal has to unlink the link rather than its
    /// target, and reading a link has to read it.
    /// </remarks>
    public static CapResult<ResolvedParent> ResolveParent(
        SafeDirHandle root,
        scoped in CapPath path,
        ConfinedResolveOptions options)
    {
        CapError error = Walk(
            root, path, ResolutionTarget.Parent, CapAccess.None, FileOpenRequest.Existing(FileAccess.Read),
            options, out Outcome outcome);
        return error.IsFailure
            ? CapResult<ResolvedParent>.Fail(error)
            : CapResult<ResolvedParent>.Ok(new ResolvedParent(outcome.Directory!, outcome.Name!));
    }

    /// <summary>
    /// The walk. Every operation reaches the filesystem through this and no other loop.
    /// </summary>
    /// <remarks>
    /// One loop rather than one per operation, because the interesting part — what
    /// <c>..</c> means, when a link may be followed, where the root is — is identical for
    /// all of them, and two copies of it would eventually disagree. Only the last step
    /// differs, and that is the three-way switch at the end of the body.
    /// </remarks>
    private static CapError Walk(
        SafeDirHandle root,
        scoped in CapPath path,
        ResolutionTarget target,
        CapAccess access,
        scoped in FileOpenRequest request,
        ConfinedResolveOptions options,
        out Outcome outcome)
    {
        outcome = default;

        if (path.ComponentCount == 0)
        {
            // Nothing to resolve. The caller has named the directory it already holds, which
            // no step can express and which the three targets read differently enough that
            // guessing would be worse than refusing.
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        IPlatformOps ops = PlatformOps.Current;
        DirectoryStack stack = new(root);
        PendingComponents pending = new(path);
        int linkBudget = MaxSymbolicLinks;

        try
        {
            if ((options & ConfinedResolveOptions.RefuseMountCrossing) != 0)
            {
                CapError rootInfo = ops.StatHandle(root, out CapNodeInfo info);
                if (rootInfo.IsFailure)
                {
                    return rootInfo;
                }

                stack.SetRootVolumeId(info.VolumeId);
            }

            while (pending.TryNext(out ReadOnlySpan<char> component))
            {
                // Taken after the component, not before: following a link puts its target in
                // front of what was pending, so which component is last is only known once
                // everything ahead of it has been seen.
                bool isLast = !pending.HasMore;

                if (component.SequenceEqual(".."))
                {
                    if (stack.AtRoot)
                    {
                        // The one thing the sandbox is for. Refused rather than clamped to
                        // the root: a caller that asked to go above it has been given a path
                        // that tries to escape, and silently resolving it to something else
                        // would hide that from them.
                        return CapError.FromCategory(CapErrorCategory.Escaped);
                    }

                    stack.Pop();
                    continue;
                }

                if (!isLast)
                {
                    CapError step = Descend(ops, ref stack, component, options);
                    if (step.Category == CapErrorCategory.SymbolicLink)
                    {
                        CapError followed = Follow(
                            ops, in stack, ref pending, component, path.Syntax, options, ref linkBudget);
                        if (followed.IsFailure)
                        {
                            return followed;
                        }

                        continue;
                    }

                    if (step.IsFailure)
                    {
                        return step;
                    }

                    continue;
                }

                CapError last = Finish(
                    ops, ref stack, ref pending, component, path, target, access, in request, options,
                    ref linkBudget, out bool followedLink, out outcome);

                if (followedLink)
                {
                    continue;
                }

                return last;
            }

            // Only reachable when the path ended on `..`, or on a link whose target names the
            // directory holding it, since every other component either returns or leaves
            // something pending. The walk is standing on the directory the path named, and
            // there is no final name for an operation to act on.
            return FinishAtStack(ops, ref stack, target, out outcome);
        }
        finally
        {
            stack.Dispose();
        }
    }

    /// <summary>
    /// Takes one step down, into a component that is not the last.
    /// </summary>
    /// <remarks>
    /// Always traversal-only: an intermediate directory is a place to resolve from and never
    /// a thing the caller asked to read. On Linux that is a descriptor with no data access at
    /// all, which is also the only kind that opens a directory granting execute permission
    /// without read permission — the usual way a shared parent is kept from disclosing what
    /// is beneath it.
    /// </remarks>
    private static CapError Descend(
        IPlatformOps ops,
        scoped ref DirectoryStack stack,
        scoped ReadOnlySpan<char> name,
        ConfinedResolveOptions options)
    {
        CapResult<SafeDirHandle> opened = ops.OpenChildDirectory(stack.Top, name, CapAccess.None);
        if (!opened.IsSuccess)
        {
            return opened.Error;
        }

        ulong volumeId = 0;
        if ((options & ConfinedResolveOptions.RefuseMountCrossing) != 0)
        {
            CapError crossing = CheckVolume(ops, in stack, opened.Value, out volumeId);
            if (crossing.IsFailure)
            {
                opened.Value.Dispose();
                return crossing;
            }
        }

        if (!stack.TryPush(opened.Value, volumeId))
        {
            opened.Value.Dispose();
            return CapError.FromCategory(CapErrorCategory.PathTooDeep);
        }

        return CapError.Success;
    }

    /// <summary>
    /// The last step, which is the only part of resolution that depends on what the caller
    /// is doing.
    /// </summary>
    /// <remarks>
    /// Reports back through <c>followedLink</c> when the component turned out to be a link
    /// that was followed. Nothing has been produced in that case: the walk carries on with
    /// the target's components, and the last of those becomes the new last step, which may
    /// itself be a link.
    /// </remarks>
    private static CapError Finish(
        IPlatformOps ops,
        scoped ref DirectoryStack stack,
        scoped ref PendingComponents pending,
        scoped ReadOnlySpan<char> name,
        scoped in CapPath path,
        ResolutionTarget target,
        CapAccess access,
        scoped in FileOpenRequest request,
        ConfinedResolveOptions options,
        ref int linkBudget,
        out bool followedLink,
        out Outcome outcome)
    {
        followedLink = false;
        outcome = default;

        if (target == ResolutionTarget.Parent)
        {
            // Deliberately without looking the name up. Whether it exists, and what it is if
            // it does, is the operation's business; a walk that checked would both slow every
            // create down and answer a question that is stale by the time it is read.
            CapResult<SafeDirHandle> parent = stack.DetachTop(ops);
            if (!parent.IsSuccess)
            {
                return parent.Error;
            }

            outcome = new Outcome(parent.Value, null, name.ToString());
            return CapError.Success;
        }

        // A trailing separator, or a final `.` or `..`, is a request that the target be a
        // directory: `foo/` must fail where `foo` would have succeeded on a regular file.
        // So the last step opens a directory in that case whatever was asked for, and a
        // caller who wanted a file is told it found one of the other kind. The request can
        // come from a followed link's stored target as well as from the caller's path.
        bool asDirectory = target == ResolutionTarget.Directory || pending.RequiresDirectory;

        // A file open that may create or empty the file refuses a link here, wherever it
        // points: the write must land on the name the caller gave, not on whatever a link
        // planted under that name leads to.
        bool followFinal = target != ResolutionTarget.File || request.FollowsFinalLink;

        if (asDirectory)
        {
            CapAccess directoryAccess = target == ResolutionTarget.Directory ? access : CapAccess.None;
            CapResult<SafeDirHandle> opened = ops.OpenChildDirectory(stack.Top, name, directoryAccess);
            if (!opened.IsSuccess)
            {
                return opened.Error.Category == CapErrorCategory.SymbolicLink
                    ? FollowLast(
                        ops, in stack, ref pending, name, path.Syntax, options, followFinal, ref linkBudget,
                        out followedLink)
                    : opened.Error;
            }

            if ((options & ConfinedResolveOptions.RefuseMountCrossing) != 0)
            {
                CapError crossing = CheckVolume(ops, in stack, opened.Value, out _);
                if (crossing.IsFailure)
                {
                    opened.Value.Dispose();
                    return crossing;
                }
            }

            if (target == ResolutionTarget.File)
            {
                opened.Value.Dispose();
                return CapError.FromCategory(CapErrorCategory.IsADirectory);
            }

            outcome = new Outcome(opened.Value, null, null);
            return CapError.Success;
        }

        if ((options & ConfinedResolveOptions.RefuseMountCrossing) != 0)
        {
            // Asked of the name rather than of the opened file, because a handle to a file
            // is not something this layer can interrogate. The mount table is written by
            // whoever administers the host and not by anything inside the sandbox, so a name
            // changing between this question and the open cannot change the answer to it.
            CapError info = ops.StatChild(stack.Top, name, out CapNodeInfo child);
            if (info.IsFailure)
            {
                return info;
            }

            if (child.VolumeId != stack.TopVolumeId)
            {
                return CapError.FromCategory(CapErrorCategory.CrossDevice);
            }
        }

        CapResult<SafeFileHandle> file = ops.OpenChildFile(stack.Top, name, in request);
        if (!file.IsSuccess)
        {
            return file.Error.Category == CapErrorCategory.SymbolicLink
                ? FollowLast(
                    ops, in stack, ref pending, name, path.Syntax, options, followFinal, ref linkBudget,
                    out followedLink)
                : file.Error;
        }

        outcome = new Outcome(null, file.Value, null);
        return CapError.Success;
    }

    /// <summary>
    /// Produces the result for a path that ended on a directory already held, such as
    /// <c>a/..</c>, or a link storing <c>.</c>.
    /// </summary>
    private static CapError FinishAtStack(
        IPlatformOps ops,
        scoped ref DirectoryStack stack,
        ResolutionTarget target,
        out Outcome outcome)
    {
        outcome = default;

        switch (target)
        {
            case ResolutionTarget.Directory:
                CapResult<SafeDirHandle> directory = stack.DetachTop(ops);
                if (!directory.IsSuccess)
                {
                    return directory.Error;
                }

                outcome = new Outcome(directory.Value, null, null);
                return CapError.Success;

            case ResolutionTarget.File:
                return CapError.FromCategory(CapErrorCategory.IsADirectory);

            default:
                // `..` is not a name an operation can act on: there is nothing to create,
                // remove or rename, only a directory the caller could have asked for
                // directly.
                return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }
    }

    /// <summary>Follows the last component, which turned out to be a link.</summary>
    /// <remarks>
    /// Refused without being read when the operation does not follow a final link, and
    /// reported as the same refusal the stricter policy gives: the link is not looked at, so
    /// where it points is never learned, and nothing was attempted that could be called an
    /// escape. The confined open refuses the same link in the same terms.
    /// </remarks>
    private static CapError FollowLast(
        IPlatformOps ops,
        in DirectoryStack stack,
        scoped ref PendingComponents pending,
        scoped ReadOnlySpan<char> name,
        CapPathSyntax syntax,
        ConfinedResolveOptions options,
        bool followFinal,
        ref int linkBudget,
        out bool followedLink)
    {
        if (!followFinal)
        {
            followedLink = false;
            return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
        }

        CapError error = Follow(ops, in stack, ref pending, name, syntax, options, ref linkBudget);
        followedLink = error.IsSuccess;
        return error;
    }

    /// <summary>
    /// Reads a link and puts its target in front of everything still to be resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target is data written by whoever could write in the directory holding the link,
    /// which inside a sandbox is frequently the attacker. It is therefore parsed by the same
    /// parser as a caller's own path, and its components are walked by the same loop under
    /// the same root test — a link cannot reach anywhere a written-out path could not.
    /// </para>
    /// <para>
    /// An absolute target is refused rather than resolved from the sandbox root. The
    /// alternative reading, treating the root as if it were the filesystem root, would quietly
    /// make <c>/etc/passwd</c> inside the sandbox mean a different file from the one whoever
    /// created the link meant, and there is no way for a caller to tell which reading they
    /// are getting from the result. Refusing says so.
    /// </para>
    /// <para>
    /// This is also what keeps the walk away from the kernel's own synthetic links — the
    /// entries under <c>/proc</c> that jump straight to an open file, a process root or a
    /// namespace. Those cannot be traversed by an open that refuses to follow links, and
    /// reading one yields either an absolute path or a token that is not a path at all, so
    /// neither can carry resolution out of the subtree.
    /// </para>
    /// </remarks>
    private static CapError Follow(
        IPlatformOps ops,
        in DirectoryStack stack,
        scoped ref PendingComponents pending,
        scoped ReadOnlySpan<char> name,
        CapPathSyntax syntax,
        ConfinedResolveOptions options,
        ref int linkBudget)
    {
        if ((options & ConfinedResolveOptions.RefuseSymlinks) != 0)
        {
            return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
        }

        if (--linkBudget < 0)
        {
            // Both a cycle and an honestly long chain arrive here, and they are not
            // distinguishable without walking the cycle — which is the thing the budget
            // exists to avoid doing.
            return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
        }

        CapResult<string> target = ops.ReadChildLink(stack.Top, name);
        if (!target.IsSuccess)
        {
            // The name was a link when it was opened and is something else now: swapped in
            // between the two calls by whatever can write in the directory. A lost race rather
            // than an answer, so the name is looked at again.
            return target.Error.Category == CapErrorCategory.NotALink
                ? Revisit(ref pending, name, syntax)
                : target.Error;
        }

        if (!CapPath.TryParse(
                target.Value, syntax, ParentLinkPolicy.Preserve, out CapPath parsed, out CapPathError error))
        {
            // A target such as `.` or `./.` is spelled out and has no components, so the parser
            // refuses it as naming nothing -- which is right for a caller's path and wrong for a
            // link. From where the link lives it names the directory holding the link, and the
            // walk is already standing there: there is nothing to push, and resolution carries
            // on from that directory, as the kernel's own resolution does.
            if (error == CapPathError.Empty && target.Value.Length > 0)
            {
                return CapError.Success;
            }

            return TranslateLinkTarget(error);
        }

        return pending.TryFollow(parsed)
            ? CapError.Success
            : CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
    }

    /// <summary>
    /// Puts a name back in front of what is still to be resolved, so that the next step looks
    /// at it afresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The open that found the link and the read of its target are two calls, and whatever
    /// can write in the directory can put a directory or a file back under the name between
    /// them. Passing on the read's failure would hand the caller an error about their request
    /// for what was a lost race; the second look goes through the same no-follow open as the
    /// first, so it decides nothing about what may be reached.
    /// </para>
    /// <para>
    /// The kernel's confined open does the same thing with a lost race: it abandons the
    /// attempt and asks to be called again, and the call is made again. Here the unit retried
    /// is one component rather than the whole path, since the handles above it are already
    /// held and were never in question.
    /// </para>
    /// <para>
    /// Bounded, because anything able to swap the name can swap it forever. Each look spends
    /// one unit of the link budget — the read that failed was charged before it was made — so
    /// a name that keeps changing ends the resolution with the same refusal as a chain of
    /// links too long to follow, after at most as many attempts.
    /// </para>
    /// </remarks>
    private static CapError Revisit(
        scoped ref PendingComponents pending,
        scoped ReadOnlySpan<char> name,
        CapPathSyntax syntax)
    {
        // A name that was a single component when the caller's path was split is still one.
        // Parsing it again rather than pushing the characters is what lets the frame carry
        // the syntax the rest of the walk reads it under.
        return CapPath.TryParse(name.ToString(), syntax, ParentLinkPolicy.Preserve, out CapPath again, out _) &&
               pending.TryFollow(again)
            ? CapError.Success
            : CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
    }

    /// <summary>
    /// Reads a refusal of a link's target as a resolution failure.
    /// </summary>
    /// <remarks>
    /// Every rooted shape means the same thing here: the link names somewhere by starting
    /// from a root this handle confers no authority over, so following it would leave the
    /// sandbox. That is the containment refusal, reported as one, whichever spelling of
    /// "rooted" the target used.
    /// </remarks>
    private static CapError TranslateLinkTarget(CapPathError error) => error switch
    {
        CapPathError.Absolute or
        CapPathError.RootRelative or
        CapPathError.DriveRelative or
        CapPathError.Unc or
        CapPathError.DeviceNamespace => CapError.FromCategory(CapErrorCategory.Escaped),

        // A link whose target names nothing points nowhere, which is the same situation as
        // a link to a name that does not exist.
        CapPathError.Empty => CapError.FromCategory(CapErrorCategory.NotFound),

        CapPathError.TooLong => CapError.FromCategory(CapErrorCategory.NameTooLong),

        // A reserved device name, a character the platform will reinterpret, or a trailing
        // dot the filesystem would strip. Refused for the same reasons a caller's path
        // containing one is refused; that it arrived through a link makes it more suspect,
        // not less.
        _ => CapError.FromCategory(CapErrorCategory.InvalidArgument),
    };

    /// <summary>
    /// Confirms that a newly opened directory is on the same filesystem as the one it was
    /// found in.
    /// </summary>
    /// <remarks>
    /// Asked of the open handle rather than of the name, so that the answer is about the
    /// object resolution actually reached. A mount inside the sandbox is a piece of an
    /// unrelated filesystem grafted in by whoever controls the mount table, who is outside
    /// the trust boundary, which is why a caller may ask not to cross one.
    /// </remarks>
    private static CapError CheckVolume(
        IPlatformOps ops,
        in DirectoryStack stack,
        SafeDirHandle opened,
        out ulong volumeId)
    {
        CapError error = ops.StatHandle(opened, out CapNodeInfo info);
        if (error.IsFailure)
        {
            volumeId = 0;
            return error;
        }

        volumeId = info.VolumeId;
        return info.VolumeId == stack.TopVolumeId
            ? CapError.Success
            : CapError.FromCategory(CapErrorCategory.CrossDevice);
    }

    /// <summary>
    /// What a completed walk produced: exactly one of the three, according to the target.
    /// </summary>
    private readonly struct Outcome
    {
        public Outcome(SafeDirHandle? directory, SafeFileHandle? file, string? name)
        {
            Directory = directory;
            File = file;
            Name = name;
        }

        public SafeDirHandle? Directory { get; }

        public SafeFileHandle? File { get; }

        public string? Name { get; }
    }
}
