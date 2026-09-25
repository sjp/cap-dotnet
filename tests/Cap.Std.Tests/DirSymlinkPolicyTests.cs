using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Tests;

/// <summary>
/// The symbolic-link policy as a caller meets it: chosen when a root is opened, carried by
/// every handle derived from it, and tightenable but never loosenable.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour of the policy against every shape of link is settled against the resolution
/// backends directly, where all three can be driven from one table on one machine. What is
/// left for this level, and what is only true here, is that the policy is part of the
/// capability: that a handle knows which policy it carries, that handing a handle on cannot
/// lose it, and that the one operation which changes it can only make it stricter.
/// </para>
/// <para>
/// That last property is the whole reason the word "policy" is usable. A restriction a
/// derived handle could lift is a suggestion: whoever was given a handle in order to work
/// inside a subtree would be able to undo it in a single call, and the restriction would
/// last exactly as long as it took somebody to try.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirSymlinkPolicyTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A root opened without saying follows links that stay inside the subtree.</summary>
    /// <remarks>
    /// The default is the useful one rather than the strict one, because refusing every link
    /// breaks ordinary directory layouts: a link inside a tree pointing elsewhere inside the
    /// same tree is a normal thing for a package manager or a build system to have left
    /// behind, and a sandbox that cannot read such a tree is not used.
    /// </remarks>
    [Fact]
    public void A_root_opened_without_a_policy_follows_a_link_that_stays_inside()
    {
        RequireSymbolicLinks();
        Build();

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, root.SymlinkPolicy);
        using Dir reached = root.OpenDir("inside-link");
        Assert.True(SameDirectory(reached, root.OpenDir("plain")));
    }

    /// <summary>A root opened refusing links refuses one, and says so as what it is.</summary>
    /// <remarks>
    /// Not as an escape. The link named something inside the subtree, so nothing tried to
    /// leave; reporting it alongside the refusals that <em>were</em> attempts to leave would
    /// put noise into the one log an application has reason to read closely.
    /// </remarks>
    [Fact]
    public void A_root_that_denies_links_refuses_one_that_stays_inside()
    {
        RequireSymbolicLinks();
        Build();

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), SymlinkPolicy.Deny);

        Assert.Equal(SymlinkPolicy.Deny, root.SymlinkPolicy);

        Exception thrown = Assert.ThrowsAny<IOException>(() => root.OpenDir("inside-link"));
        Assert.IsNotType<SandboxEscapeException>(thrown);

        // And the directory the link pointed at is still reachable by its own name, which is
        // what makes this a policy about links rather than a broken handle.
        using Dir plain = root.OpenDir("plain");
    }

    /// <summary>A link out of the subtree is refused under either policy.</summary>
    /// <remarks>
    /// Containment is not what this knob controls. Under the default the refusal names the
    /// containment failure, because the link was read and its target was seen to leave. Under
    /// the stricter policy the link is never read at all, so the refusal can only say that a
    /// link was in the way — which also means a caller cannot use the stricter policy to ask
    /// which links point outside.
    /// </remarks>
    [Fact]
    public void A_link_out_of_the_subtree_is_refused_under_either_policy()
    {
        RequireSymbolicLinks();
        Build();

        using Dir following = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        Assert.Throws<SandboxEscapeException>(() => following.OpenDir("escape-link"));

        using Dir denying = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), SymlinkPolicy.Deny);
        Exception thrown = Assert.ThrowsAny<IOException>(() => denying.OpenDir("escape-link"));
        Assert.IsNotType<SandboxEscapeException>(thrown);
    }

    /// <summary>Every way of deriving a handle carries the policy across unchanged.</summary>
    [Fact]
    public void Deriving_a_handle_carries_the_policy_across()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "plain", "deeper"));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), SymlinkPolicy.Deny);
        using Dir child = root.OpenDir("plain");
        using Dir grandchild = child.OpenDir("deeper");
        using Dir copy = child.Clone();

        Assert.Equal(SymlinkPolicy.Deny, child.SymlinkPolicy);
        Assert.Equal(SymlinkPolicy.Deny, grandchild.SymlinkPolicy);
        Assert.Equal(SymlinkPolicy.Deny, copy.SymlinkPolicy);
    }

    /// <summary>Tightening produces a handle on the same directory under the stricter rule.</summary>
    [Fact]
    public void Restricting_produces_a_stricter_handle_on_the_same_directory()
    {
        RequireSymbolicLinks();
        Build();

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir strict = root.Restrict(SymlinkPolicy.Deny);

        Assert.Equal(SymlinkPolicy.Deny, strict.SymlinkPolicy);
        Assert.True(SameDirectory(root, strict));

        // Same directory, same authority over what is beneath it, different answer about a
        // link — which is the only thing that was meant to change.
        using Dir viaOriginal = root.OpenDir("inside-link");
        Assert.ThrowsAny<IOException>(() => strict.OpenDir("inside-link"));
        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, root.SymlinkPolicy);
    }

    /// <summary>The stricter policy travels on to whatever is derived from the stricter handle.</summary>
    [Fact]
    public void A_handle_derived_from_a_restricted_one_is_restricted_too()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "plain", "deeper"));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir strict = root.Restrict(SymlinkPolicy.Deny);
        using Dir child = strict.OpenDir("plain");
        using Dir grandchild = child.OpenDir("deeper");

        Assert.Equal(SymlinkPolicy.Deny, child.SymlinkPolicy);
        Assert.Equal(SymlinkPolicy.Deny, grandchild.SymlinkPolicy);
    }

    /// <summary>Asking for a looser policy than the handle carries is refused.</summary>
    /// <remarks>
    /// The property the whole mechanism rests on. It is enforced by this refusal and by there
    /// being no other way for the value to change: derivation copies it, and there is no
    /// setter.
    /// </remarks>
    [Fact]
    public void Restricting_cannot_loosen_the_policy()
    {
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), SymlinkPolicy.Deny);

        ArgumentException thrown =
            Assert.Throws<ArgumentException>(() => root.Restrict(SymlinkPolicy.FollowWithinSandbox));
        Assert.Equal("policy", thrown.ParamName);

        Assert.Throws<ArgumentException>(
            () => root.TryRestrict(SymlinkPolicy.FollowWithinSandbox, out _));

        Assert.Equal(SymlinkPolicy.Deny, root.SymlinkPolicy);
    }

    /// <summary>Asking for the policy the handle already has is an ordinary copy.</summary>
    /// <remarks>
    /// Allowed rather than refused, so that code which tightens a handle before passing it on
    /// does not have to know whether the tightening is redundant. "At least as strict"
    /// includes "as strict".
    /// </remarks>
    [Fact]
    public void Restricting_to_the_policy_already_in_force_is_a_copy()
    {
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), SymlinkPolicy.Deny);

        Assert.True(root.TryRestrict(SymlinkPolicy.Deny, out Dir? same));
        using Dir copy = same!;

        Assert.Equal(SymlinkPolicy.Deny, copy.SymlinkPolicy);
        Assert.True(SameDirectory(root, copy));
    }

    /// <summary>A restricted handle has a lifetime of its own.</summary>
    /// <remarks>
    /// It owns a separate open directory, so handing one over is a transfer of authority
    /// rather than a loan: closing the handle it came from does not revoke it, and closing it
    /// does not disturb the original.
    /// </remarks>
    [Fact]
    public void A_restricted_handle_outlives_the_handle_it_came_from()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "plain"));

        Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        using Dir strict = root.Restrict(SymlinkPolicy.Deny);
        root.Dispose();

        using Dir child = strict.OpenDir("plain");
        Assert.Equal(SymlinkPolicy.Deny, child.SymlinkPolicy);
    }

    /// <summary>A policy value the enumeration does not define is refused, not defaulted.</summary>
    /// <remarks>
    /// The default is the least restrictive value, so a value that arrived by a bad cast or a
    /// stale constant must not be quietly read as it. Silently selecting the weakest
    /// behaviour is the one wrong answer here.
    /// </remarks>
    [Fact]
    public void An_undefined_policy_is_refused_rather_than_treated_as_the_default()
    {
        const SymlinkPolicy Undefined = (SymlinkPolicy)7;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), Undefined));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dir.TryOpen(_tree.HostPath, AmbientAuthority.Acquire(), out _, Undefined));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        Assert.Throws<ArgumentOutOfRangeException>(() => root.Restrict(Undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.TryRestrict(Undefined, out _));
    }

    /// <summary>The non-throwing open takes the policy too.</summary>
    [Fact]
    public void The_non_throwing_open_takes_the_policy()
    {
        Assert.True(Dir.TryOpen(_tree.HostPath, AmbientAuthority.Acquire(), out Dir? opened, SymlinkPolicy.Deny));
        using Dir root = opened!;

        Assert.Equal(SymlinkPolicy.Deny, root.SymlinkPolicy);
    }

    /// <summary>Restricting a handle that has been closed is a use-after-dispose.</summary>
    [Fact]
    public void Restricting_a_disposed_handle_is_refused()
    {
        Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());
        root.Dispose();

        Assert.Throws<ObjectDisposedException>(() => root.Restrict(SymlinkPolicy.Deny));
        Assert.Throws<ObjectDisposedException>(() => root.TryRestrict(SymlinkPolicy.Deny, out _));
    }

    /// <summary>
    /// Builds a tree with a link that stays inside and one that leaves.
    /// </summary>
    /// <remarks>
    /// The sandbox root is the temporary directory itself here and the escaping link points
    /// above it, which is enough: what is being tested is which refusal arrives, not what
    /// lies outside.
    /// </remarks>
    private void Build()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "plain"));
        Directory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "inside-link"), "plain");
        Directory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "escape-link"), "..");
    }

    /// <summary>
    /// Skips when this host will not let the test process create a symbolic link.
    /// </summary>
    /// <remarks>
    /// Established by creating one rather than inferred from the platform. Windows needs
    /// either developer mode or an elevated token, and which of those a machine has is not
    /// something a platform check can answer — one that assumed would keep skipping on a
    /// machine perfectly able to run these.
    /// </remarks>
    private void RequireSymbolicLinks()
    {
        string probe = Path.Combine(_tree.HostPath, "link-probe");

        try
        {
            File.CreateSymbolicLink(probe, "target");
            File.Delete(probe);
        }
        catch (Exception thrown) when (
            thrown is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic links cannot be created here, so these cases cannot be built: {thrown.Message}");
        }
    }

    /// <summary>Whether two handles refer to the same directory, by identity rather than by name.</summary>
    private static bool SameDirectory(Dir left, Dir right)
    {
        IPlatformOps ops = PlatformOps.Host;
        Assert.True(ops.StatHandle((SafeDirHandle)left.UnsafeGetHandle(), out CapNodeInfo first).IsSuccess);
        Assert.True(ops.StatHandle((SafeDirHandle)right.UnsafeGetHandle(), out CapNodeInfo second).IsSuccess);
        return first.IsSameNodeAs(second);
    }
}
