using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Tests;

/// <summary>
/// What closing a handle does, and — more to the point — what it does not do.
/// </summary>
/// <remarks>
/// <para>
/// The naive model of a capability tree is that a handle owns the handles derived from it,
/// so that closing a parent revokes its children. That model is wrong here, and quietly
/// adopting it would change what passing a handle to a component means. Each handle owns a
/// separate open object; the one it was derived from was a place to resolve a name from, not
/// a container it lives inside. Handing one over is therefore a transfer that the giver
/// cannot take back, which is the property that lets a component be given exactly the
/// authority it needs and then be left alone.
/// </para>
/// <para>
/// Concurrency is part of the same story. The handle is safe to use from several threads,
/// and a disposal racing an operation must end as a failed operation rather than as a call
/// that lands on whatever object has since taken the handle's number — the failure mode that
/// makes descriptor reuse a containment problem and not merely a bug.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirLifetimeTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>Closing a handle leaves everything derived from it working.</summary>
    [Fact]
    public void Disposing_a_handle_does_not_invalidate_handles_derived_from_it()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "child", "grandchild"));

        Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        Dir child = root.OpenDir("child");

        root.Dispose();

        // The child was not reached through the parent; it is its own open directory, and
        // resolution from it has nothing to do with the handle it was derived from.
        using Dir grandchild = child.OpenDir("grandchild");
        Assert.False(grandchild.UnsafeGetHandle().IsInvalid);

        child.Dispose();
        grandchild.Dispose();
    }

    /// <summary>A copy outlives the handle it was copied from.</summary>
    [Fact]
    public void Disposing_a_handle_does_not_invalidate_a_copy_of_it()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "child"));

        Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir copy = root.Clone();

        root.Dispose();

        using Dir child = copy.OpenDir("child");
        Assert.False(child.UnsafeGetHandle().IsInvalid);
    }

    /// <summary>And the original outlives the copy, which is the same claim from the other side.</summary>
    [Fact]
    public void Disposing_a_copy_does_not_invalidate_the_handle_it_came_from()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "child"));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        root.Clone().Dispose();

        using Dir child = root.OpenDir("child");
        Assert.False(child.UnsafeGetHandle().IsInvalid);
    }

    /// <summary>Closing twice is not an error.</summary>
    /// <remarks>
    /// Handles are passed around and wrapped in <c>using</c> at more than one level; a
    /// second close being fatal would make ownership something every caller had to reason
    /// about rather than something it can be careless with.
    /// </remarks>
    [Fact]
    public void Disposing_twice_is_harmless()
    {
        Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        root.Dispose();
        root.Dispose();
    }

    /// <summary>Using a closed handle says so, rather than failing as an IO error.</summary>
    /// <remarks>
    /// A closed handle is a mistake in the calling code, and the report should name that
    /// rather than describe a filesystem that had nothing to do with it.
    /// </remarks>
    [Fact]
    public void Operations_on_a_closed_handle_report_it()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "child"));

        Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        root.Dispose();

        Assert.Throws<ObjectDisposedException>(() => root.OpenDir("child"));
        Assert.Throws<ObjectDisposedException>(() => root.TryOpenDir("child", out _));
        Assert.Throws<ObjectDisposedException>(() => root.Clone());
        Assert.Throws<ObjectDisposedException>(() => root.TryClone(out _));
        Assert.Throws<ObjectDisposedException>(() => root.UnsafeGetHandle());
        Assert.Throws<ObjectDisposedException>(() => root.TryGetPath(AmbientAuthority.Acquire(), out _));
    }

    /// <summary>
    /// A handle closed on another thread after an operation checked it, and before the
    /// operation reached the platform, is reported as disposed as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation's own check that the handle is open and the moment the platform call pins
    /// it are not the same instant, and a disposal can land between them. The call is then
    /// refused without being made — its number may already belong to something else — and
    /// what the caller hears has to be the same thing a call on an already-closed handle
    /// hears. Reporting it as a filesystem failure instead would describe a filesystem that
    /// had nothing to do with it.
    /// </para>
    /// <para>
    /// The instant cannot be scheduled from a test, so the two halves are checked where they
    /// meet: the platform layer's refusal for a closed handle, and what the caller-facing
    /// layer makes of it. The stress suite provokes the real race.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_handle_closed_part_way_through_a_call_is_reported_as_disposed()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "child"));

        CapResult<SafeDirHandle> opened = PlatformOps.Current.OpenAmbientDirectory(_tree.HostPath, CapAccess.Read);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        SafeDirHandle handle = opened.Value;
        handle.Dispose();

        CapResult<SafeDirHandle> child = PlatformOps.Current.OpenChildDirectory(handle, "child", CapAccess.Read);
        Assert.False(child.IsSuccess);
        Assert.Equal(CapErrorCategory.Closed, child.Error.Category);

        Assert.IsType<ObjectDisposedException>(FailureTranslation.ToException(child.Error, "child"));
        Assert.IsType<ObjectDisposedException>(FailureTranslation.ToEnumerationException(child.Error));
        Assert.IsType<ObjectDisposedException>(FailureTranslation.ToHandleException(child.Error));
        Assert.Throws<ObjectDisposedException>(() => FailureTranslation.ThrowIfClosed(child.Error));
    }

    /// <summary>
    /// A copy carries the authority of its original rather than whatever the directory
    /// would have granted.
    /// </summary>
    /// <remarks>
    /// The copy is made by code that was handed a handle, not by the code that decided how
    /// much authority to take. Re-deriving the answer from the directory's own permissions
    /// would mean a handle deliberately opened with less became wider the first time anyone
    /// duplicated it.
    /// </remarks>
    [Fact]
    public void A_copy_carries_the_authority_of_the_handle_it_came_from()
    {
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir copy = root.Clone();

        SafeDirHandle original = (SafeDirHandle)root.UnsafeGetHandle();
        SafeDirHandle duplicate = (SafeDirHandle)copy.UnsafeGetHandle();

        Assert.Equal(original.Access, duplicate.Access);
        Assert.NotEqual(original.DangerousGetHandle(), duplicate.DangerousGetHandle());
    }

    /// <summary>
    /// Many threads resolving through one handle, while others copy it and close their
    /// copies, do not interfere.
    /// </summary>
    /// <remarks>
    /// Deliberately modest — a few threads for a moment, not a soak. It holds the documented
    /// thread-safety claim to something rather than hunting for rare interleavings, which
    /// needs a harness built for it and far more time than a unit test has. The case it does
    /// cover is the one that matters most: a copy being closed on one thread frees a
    /// descriptor number that is immediately reusable, so an operation on another thread
    /// that read a raw value and then lost the race would land somewhere else entirely.
    /// </remarks>
    [Fact]
    public void Concurrent_use_of_one_handle_is_safe()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "child", "grandchild"));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        Parallel.For(0, 128, iteration =>
        {
            using Dir child = root.OpenDir("child/grandchild");
            Assert.False(child.UnsafeGetHandle().IsInvalid);

            using Dir copy = root.Clone();
            Assert.True(copy.TryOpenDir("child", out Dir? viaCopy));
            viaCopy!.Dispose();
        });
    }
}
