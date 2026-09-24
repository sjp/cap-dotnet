using Cap.Primitives.Interop;
using Cap.Tests.Fakes;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The component-at-a-time walk, driven against a filesystem that can be changed between
/// any two of its steps.
/// </summary>
/// <remarks>
/// <para>
/// The attacks that matter to this code are races: a name that is a directory when it is
/// looked at and a symbolic link when it is opened, or a directory that is moved elsewhere
/// while the walk is standing inside it. Against a real kernel those can only be provoked by
/// running the attack in a loop and hoping to land in a window that is a few instructions
/// wide, which makes for a test that fails once a month on one agent and is then disabled.
/// </para>
/// <para>
/// Against the simulation the same attacks are exact. A hook runs immediately before each
/// name is looked up, so the swap happens at the instant that matters, on every run, on
/// every platform — including the ones whose kernel makes the race impossible and which
/// would therefore never exercise the defence at all.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class PortableWalkTests
{
    /// <summary>Several names in a row reach the directory the path spells out.</summary>
    [Fact]
    public void A_path_of_several_components_reaches_what_it_names()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode target = fs.AddDirectory("sandbox/a/b/c");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "a/b/c");
            AssertIs(ops, target, opened);
        });
    }

    /// <summary>
    /// A step up goes back to the directory the walk came from, and the walk carries on.
    /// </summary>
    [Fact]
    public void A_step_up_returns_to_where_the_walk_came_from()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/a/b");
        FakeNode target = fs.AddDirectory("sandbox/a/c");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "a/b/../c");
            AssertIs(ops, target, opened);
        });
    }

    /// <summary>
    /// A step up from the root is refused rather than clamped to it.
    /// </summary>
    /// <remarks>
    /// Clamping would be the friendlier-looking choice and is the wrong one: a caller that
    /// asked to go above the root has been handed a path that tries to escape, and resolving
    /// it to something else would hide that from them while leaving the same path working
    /// for whoever supplied it.
    /// </remarks>
    [Theory]
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("a/../..")]
    [InlineData("a/../../secret")]
    public void A_step_up_from_the_root_is_refused(string path)
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/a");
        _ = fs.AddDirectory("secret");

        Run(fs, (ops, root) => AssertFails(CapErrorCategory.Escaped, ops, root, path));
    }

    /// <summary>
    /// Stepping up works the same at every depth, and one step more than the walk has
    /// descended is refused at every depth too.
    /// </summary>
    /// <remarks>
    /// Written over a range rather than at one depth because the bug this guards against is
    /// an off-by-one in the test for "am I at the root": a walk that compares the wrong way
    /// round is correct at exactly one depth and wrong at every other, and a test that picked
    /// that one depth would agree with it.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void Stepping_up_behaves_the_same_at_every_depth(int depth)
    {
        FakeFileSystem fs = Sandbox();
        string[] levels = [.. Enumerable.Range(1, depth).Select(level => $"d{level}")];
        _ = fs.AddDirectory("sandbox/" + string.Join('/', levels));
        FakeNode sandbox = fs.Find("sandbox")!;

        Run(fs, (ops, root) =>
        {
            string descent = string.Join('/', levels);

            // Climbing back out one level at a time lands on each directory the walk passed
            // through, in reverse, and finally on the root itself.
            for (int up = 1; up <= depth; up++)
            {
                string path = descent + string.Concat(Enumerable.Repeat("/..", up));
                FakeNode expected = up == depth
                    ? sandbox
                    : fs.Find("sandbox/" + string.Join('/', levels[..(depth - up)]))!;

                using SafeDirHandle opened = OpenDirectory(ops, root, path);
                AssertIs(ops, expected, opened);
            }

            // One more than the walk descended leaves the sandbox, whatever the depth.
            AssertFails(
                CapErrorCategory.Escaped,
                ops,
                root,
                descent + string.Concat(Enumerable.Repeat("/..", depth + 1)));
        });
    }

    /// <summary>
    /// Nothing is collapsed as text. <c>link/..</c> is the parent of the link's
    /// <em>target</em>, which is where the kernel would land and is not where string
    /// arithmetic would.
    /// </summary>
    [Fact]
    public void A_step_up_is_taken_from_where_the_walk_actually_is()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode x = fs.AddDirectory("sandbox/x");
        _ = fs.AddDirectory("sandbox/x/y");
        _ = fs.AddSymbolicLink("sandbox/link", "x/y");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "link/..");

            // Removing `link/..` as text would have answered with the sandbox root. The
            // walk answers with `x`, because that is what it is standing in once the link
            // has been followed.
            AssertIs(ops, x, opened);
        });
    }

    /// <summary>A link whose target stays inside is followed.</summary>
    [Fact]
    public void A_link_inside_the_sandbox_is_followed()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode target = fs.AddDirectory("sandbox/real/inner");
        _ = fs.AddSymbolicLink("sandbox/link", "real");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "link/inner");
            AssertIs(ops, target, opened);
        });
    }

    /// <summary>
    /// A link's target is resolved from the directory holding the link, not from the root.
    /// </summary>
    [Fact]
    public void A_links_target_is_resolved_from_where_the_link_lives()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode target = fs.AddDirectory("sandbox/a/sibling");
        _ = fs.AddDirectory("sandbox/a/b");
        _ = fs.AddSymbolicLink("sandbox/a/b/up", "../sibling");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "a/b/up");
            AssertIs(ops, target, opened);
        });
    }

    /// <summary>A link that climbs out through <c>..</c> is refused.</summary>
    [Fact]
    public void A_link_that_climbs_out_is_refused()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("secret");
        _ = fs.AddDirectory("sandbox/a");
        _ = fs.AddSymbolicLink("sandbox/a/out", "../../secret");

        Run(fs, (ops, root) => AssertFails(CapErrorCategory.Escaped, ops, root, "a/out"));
    }

    /// <summary>
    /// An absolute link target is refused rather than re-read as if the sandbox root were
    /// the filesystem root.
    /// </summary>
    /// <remarks>
    /// The re-reading is defensible and is not the default, because it silently changes
    /// which file a link means and there is nothing in the result that tells the caller
    /// which reading they got.
    /// </remarks>
    [Fact]
    public void An_absolute_link_is_refused()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddSymbolicLink("sandbox/out", "/etc/passwd");

        Run(fs, (ops, root) => AssertFails(CapErrorCategory.Escaped, ops, root, "out"));
    }

    /// <summary>A link pointing at itself terminates rather than spinning.</summary>
    [Fact]
    public void A_link_cycle_is_stopped()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddSymbolicLink("sandbox/a", "b");
        _ = fs.AddSymbolicLink("sandbox/b", "a");

        Run(fs, (ops, root) => AssertFails(CapErrorCategory.SymbolicLinkLoop, ops, root, "a"));
    }

    /// <summary>
    /// A chain is followed as far as the platform itself would follow one, and no further.
    /// </summary>
    /// <remarks>
    /// The number matters, not just the existence of a limit. A tree that resolves on one
    /// backend and not on another is a difference nobody finds until it is in production, so
    /// the budget is the one Linux applies to its own resolution.
    /// </remarks>
    [Fact]
    public void A_chain_of_links_is_followed_exactly_as_far_as_the_platform_would()
    {
        AssertChain(PortableResolver.MaxSymbolicLinks, expectSuccess: true);
        AssertChain(PortableResolver.MaxSymbolicLinks + 1, expectSuccess: false);

        static void AssertChain(int length, bool expectSuccess)
        {
            FakeFileSystem fs = Sandbox();
            FakeNode target = fs.AddDirectory("sandbox/end");
            for (int i = 0; i < length; i++)
            {
                _ = fs.AddSymbolicLink($"sandbox/link{i}", i == length - 1 ? "end" : $"link{i + 1}");
            }

            Run(fs, (ops, root) =>
            {
                if (expectSuccess)
                {
                    using SafeDirHandle opened = OpenDirectory(ops, root, "link0");
                    AssertIs(ops, target, opened);
                }
                else
                {
                    AssertFails(CapErrorCategory.SymbolicLinkLoop, ops, root, "link0");
                }
            });
        }
    }

    /// <summary>A caller may refuse links outright, even ones that stay inside.</summary>
    [Fact]
    public void Links_can_be_refused_outright()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/real");
        _ = fs.AddSymbolicLink("sandbox/link", "real");

        Run(fs, (ops, root) =>
        {
            AssertFails(
                CapErrorCategory.SymbolicLinkLoop, ops, root, "link", ConfinedResolveOptions.RefuseSymlinks);

            using SafeDirHandle opened = OpenDirectory(ops, root, "real", ConfinedResolveOptions.RefuseSymlinks);
            Assert.False(opened.IsInvalid);
        });
    }

    /// <summary>
    /// A path deeper than the walk will descend is refused, at exactly the stated depth.
    /// </summary>
    /// <remarks>
    /// The limit is on open handles rather than on nesting for its own sake: a walk holds one
    /// per level, and a path of a few thousand components — cheap to send, and well within
    /// the length a path may have — would otherwise consume the process's whole descriptor
    /// budget and start breaking opens in code that has nothing to do with this.
    /// </remarks>
    [Fact]
    public void A_path_deeper_than_the_walk_will_descend_is_refused()
    {
        int limit = PortableResolver.MaxDepth;

        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/" + string.Join('/', Enumerable.Repeat("d", limit + 2)));

        Run(fs, (ops, root) =>
        {
            // The last component is opened but never descended into, so a path of one more
            // component than the depth limit is still resolvable.
            using SafeDirHandle deepest = OpenDirectory(ops, root, string.Join('/', Enumerable.Repeat("d", limit + 1)));
            Assert.False(deepest.IsInvalid);

            AssertFails(
                CapErrorCategory.PathTooDeep, ops, root, string.Join('/', Enumerable.Repeat("d", limit + 2)));
        });
    }

    /// <summary>
    /// A directory replaced by a link to somewhere outside, in the window between two steps,
    /// does not carry the walk out.
    /// </summary>
    /// <remarks>
    /// This is the race the walk cannot prevent and must survive. The swap happens at the
    /// worst possible instant — after the walk has decided to look up the name and before it
    /// has — and the defence is not that the swap is noticed but that the link it plants is
    /// resolved under the same root test as anything else.
    /// </remarks>
    [Fact]
    public void A_directory_swapped_for_an_escaping_link_mid_walk_does_not_escape()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("secret");
        _ = fs.AddDirectory("sandbox/a/b/c");
        FakeNode a = fs.Find("sandbox/a")!;

        fs.BeforeLookup = (directory, name) =>
        {
            if (ReferenceEquals(directory, a) && name == "b")
            {
                fs.Replace("sandbox/a/b", LinkNode(fs, "../../secret"));
            }
        };

        Run(fs, (ops, root) => AssertFails(CapErrorCategory.Escaped, ops, root, "a/b/c"));
    }

    /// <summary>
    /// A name that was a link when the walk opened it and is a directory again by the time the
    /// walk reads the link is looked at afresh, and resolution carries on through it.
    /// </summary>
    /// <remarks>
    /// Opening the name and reading the link are two calls, and whatever can write in the
    /// directory can swap the name between them. The read then fails because there is no link
    /// to read, which describes the tree at that instant rather than anything wrong with the
    /// caller's path. Reporting it would hand the caller an error for a lost race.
    /// </remarks>
    [Fact]
    public void A_link_swapped_back_for_a_directory_before_it_is_read_is_looked_at_again()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/elsewhere");
        FakeNode a = fs.AddDirectory("sandbox/a");
        FakeNode directory = fs.AddDirectory("sandbox/a/b");
        FakeNode c = fs.AddDirectory("sandbox/a/b/c");
        fs.Replace("sandbox/a/b", LinkNode(fs, "../elsewhere"));

        int lookups = 0;
        fs.BeforeLookup = (parent, name) =>
        {
            // The first look finds the link; the second, which is the read, finds the
            // directory put back.
            if (ReferenceEquals(parent, a) && name == "b" && ++lookups == 2)
            {
                fs.Replace("sandbox/a/b", directory);
            }
        };

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "a/b/c");
            AssertIs(ops, c, opened);
        });
    }

    /// <summary>
    /// A name that changes kind every time it is looked at is given up on once the link budget
    /// is spent, rather than looked at forever.
    /// </summary>
    /// <remarks>
    /// Anything that can swap the name once can swap it in a loop, so looking again has to be
    /// bounded. The bound is the one that already applies to following links, and the refusal
    /// is the same one a chain of links too long to follow gets.
    /// </remarks>
    [Fact]
    public void A_name_that_keeps_changing_is_given_up_on_after_the_link_budget()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode a = fs.AddDirectory("sandbox/a");
        FakeNode directory = fs.AddDirectory("sandbox/a/b");
        FakeNode link = LinkNode(fs, "../a");

        int lookups = 0;
        fs.BeforeLookup = (parent, name) =>
        {
            if (ReferenceEquals(parent, a) && name == "b")
            {
                // Always a link when opened, never one when read.
                fs.Replace("sandbox/a/b", lookups++ % 2 == 0 ? link : directory);
            }
        };

        Run(fs, (ops, root) => AssertFails(CapErrorCategory.SymbolicLinkLoop, ops, root, "a/b"));

        Assert.InRange(lookups, 2, 2 * (PortableResolver.MaxSymbolicLinks + 1));
    }

    /// <summary>
    /// A directory moved out of the sandbox while the walk is standing in it keeps serving
    /// the walk, because the walk holds it open rather than naming it again.
    /// </summary>
    /// <remarks>
    /// The point is not that the rename is defeated — it is not, and the walk carries on
    /// inside what is now an unrelated directory. The point is that it carries on inside the
    /// object it had already opened and proved was beneath the root, rather than following
    /// the name to wherever the name now leads.
    /// </remarks>
    [Fact]
    public void A_rename_under_the_walk_does_not_redirect_it()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/a");
        FakeNode a = fs.Find("sandbox/a")!;
        FakeNode b = fs.AddDirectory("sandbox/a/b");
        FakeNode decoy = fs.AddDirectory("decoy");
        _ = fs.AddDirectory("decoy/b");

        fs.BeforeLookup = (directory, name) =>
        {
            if (ReferenceEquals(directory, a) && name == "b")
            {
                fs.Replace("sandbox/a", decoy);
            }
        };

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "a/b");
            AssertIs(ops, b, opened);
        });
    }

    /// <summary>
    /// Every way the walk can fail closes the handles it opened on the way in.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed, because the error paths are the ones a hostile input is
    /// trying to take: a leak reachable by a crafted path is a way to exhaust the process's
    /// descriptors from outside, and nothing about the resulting failures elsewhere in the
    /// program would point back here.
    /// </remarks>
    [Fact]
    public void Every_failure_closes_the_handles_the_walk_opened()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("secret");
        _ = fs.AddDirectory("sandbox/a/b/c");
        _ = fs.AddFile("sandbox/a/file");
        _ = fs.AddSymbolicLink("sandbox/a/out", "../../secret");
        _ = fs.AddSymbolicLink("sandbox/a/absolute", "/etc/passwd");
        _ = fs.AddSymbolicLink("sandbox/a/loop", "loop");
        _ = fs.AddOpaqueReparsePoint("sandbox/a/opaque", 0xA000_0123);
        _ = fs.AddDirectory("sandbox/" + string.Join('/', Enumerable.Repeat("d", PortableResolver.MaxDepth + 2)));

        string[] failures =
        [
            "a/b/../../..",
            "a/b/missing",
            "a/file/beyond",
            "a/out",
            "a/absolute",
            "a/loop",
            "a/opaque",
            "a/b/c/../../../../secret",
            string.Join('/', Enumerable.Repeat("d", PortableResolver.MaxDepth + 2)),
        ];

        Run(fs, (ops, root) =>
        {
            int baseline = ops.OpenHandleCount;

            foreach (string path in failures)
            {
                CapResult<SafeDirHandle> result =
                    PortableResolver.OpenDirectory(root, Parse(path), CapAccess.Read, ConfinedResolveOptions.None);

                Assert.False(result.IsSuccess, $"'{path}' was expected to fail.");
                Assert.Equal(baseline, ops.OpenHandleCount);
            }

            // And a success leaks nothing either once its one result is closed.
            CapResult<SafeDirHandle> success =
                PortableResolver.OpenDirectory(root, Parse("a/b/c"), CapAccess.Read, ConfinedResolveOptions.None);
            Assert.True(success.IsSuccess);
            Assert.Equal(baseline + 1, ops.OpenHandleCount);
            success.Value!.Dispose();
            Assert.Equal(baseline, ops.OpenHandleCount);
        });
    }

    /// <summary>
    /// Resolving to the parent hands back the directory and the name without looking the
    /// name up, so a name that does not exist yet is an ordinary answer.
    /// </summary>
    [Fact]
    public void Resolving_to_the_parent_does_not_look_at_the_final_name()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode parent = fs.AddDirectory("sandbox/a/b");

        Run(fs, (ops, root) =>
        {
            CapResult<ResolvedParent> result =
                PortableResolver.ResolveParent(root, Parse("a/b/not-created-yet"), ConfinedResolveOptions.None);

            Assert.True(result.IsSuccess, result.Error.FailureDescription);
            using ResolvedParent resolved = result.Value!;
            Assert.Equal("not-created-yet", resolved.Name);
            AssertIs(ops, parent, resolved.Directory);
        });
    }

    /// <summary>
    /// Resolving to the parent leaves a final link alone, which is what removing one and
    /// reading one both need.
    /// </summary>
    [Fact]
    public void Resolving_to_the_parent_does_not_follow_a_final_link()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode sandbox = fs.Find("sandbox")!;
        _ = fs.AddDirectory("sandbox/real");
        _ = fs.AddSymbolicLink("sandbox/link", "real");

        Run(fs, (ops, root) =>
        {
            CapResult<ResolvedParent> result =
                PortableResolver.ResolveParent(root, Parse("link"), ConfinedResolveOptions.None);

            Assert.True(result.IsSuccess, result.Error.FailureDescription);
            using ResolvedParent resolved = result.Value!;
            Assert.Equal("link", resolved.Name);
            AssertIs(ops, sandbox, resolved.Directory);

            CapResult<string> target = ops.ReadChildLink(resolved.Directory, resolved.Name);
            Assert.True(target.IsSuccess);
            Assert.Equal("real", target.Value);
        });
    }

    /// <summary>A file is opened by the same walk that opens a directory.</summary>
    [Fact]
    public void A_file_is_reached_by_the_same_walk()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddFile("sandbox/a/b/data");

        Run(fs, (ops, root) =>
        {
            CapResult<SafeFileHandle> result =
                PortableResolver.OpenFile(root, Parse("a/b/data"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);

            Assert.True(result.IsSuccess, result.Error.FailureDescription);
            result.Value!.Dispose();
        });
    }

    /// <summary>
    /// <c>foo/</c> and <c>foo</c> are different requests, and the difference survives the
    /// path being split into components.
    /// </summary>
    [Fact]
    public void A_trailing_separator_insists_on_a_directory()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddFile("sandbox/data");
        _ = fs.AddDirectory("sandbox/dir");

        Run(fs, (ops, root) =>
        {
            CapResult<SafeFileHandle> plain =
                PortableResolver.OpenFile(root, Parse("data"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
            Assert.True(plain.IsSuccess, plain.Error.FailureDescription);
            plain.Value!.Dispose();

            CapResult<SafeFileHandle> slashed =
                PortableResolver.OpenFile(root, Parse("data/"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
            Assert.False(slashed.IsSuccess);
            Assert.Equal(CapErrorCategory.NotADirectory, slashed.Error.Category);

            CapResult<SafeFileHandle> directory =
                PortableResolver.OpenFile(root, Parse("dir"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
            Assert.False(directory.IsSuccess);
            Assert.Equal(CapErrorCategory.IsADirectory, directory.Error.Category);
        });
    }

    /// <summary>
    /// A trailing separator in a link's stored target insists on a directory exactly as one
    /// in the caller's path does, and survives the target being resolved in place of the link.
    /// </summary>
    [Fact]
    public void A_trailing_separator_stored_in_a_link_insists_on_a_directory()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddFile("sandbox/data");
        FakeNode dir = fs.AddDirectory("sandbox/dir");
        _ = fs.AddSymbolicLink("sandbox/plain", "data");
        _ = fs.AddSymbolicLink("sandbox/slashed", "data/");
        _ = fs.AddSymbolicLink("sandbox/through", "slashed");
        _ = fs.AddSymbolicLink("sandbox/slashed-link", "plain/");
        _ = fs.AddSymbolicLink("sandbox/dir-slashed", "dir/");

        Run(fs, (ops, root) =>
        {
            CapResult<SafeFileHandle> plain =
                PortableResolver.OpenFile(root, Parse("plain"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
            Assert.True(plain.IsSuccess, plain.Error.FailureDescription);
            plain.Value!.Dispose();

            // In the link, in a link the link leads to, on a link that leads on to the file,
            // and in the caller's path with the link being what the separator follows.
            foreach (string path in new[] { "slashed", "through", "slashed-link", "plain/" })
            {
                CapResult<SafeFileHandle> file =
                    PortableResolver.OpenFile(root, Parse(path), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
                Assert.False(file.IsSuccess, $"'{path}' opened a file through a target spelled as a directory.");
                Assert.Equal(CapErrorCategory.NotADirectory, file.Error.Category);
            }

            using SafeDirHandle opened = OpenDirectory(ops, root, "dir-slashed");
            AssertIs(ops, dir, opened);
        });
    }

    /// <summary>
    /// A link storing <c>.</c> names the directory holding it: a directory to open, a place
    /// to resolve on from, and not a file.
    /// </summary>
    [Fact]
    public void A_link_to_dot_names_the_directory_holding_it()
    {
        FakeFileSystem fs = Sandbox();
        FakeNode holder = fs.AddDirectory("sandbox/a");
        FakeNode inner = fs.AddDirectory("sandbox/a/inner");
        _ = fs.AddSymbolicLink("sandbox/a/self", ".");
        _ = fs.AddSymbolicLink("sandbox/a/self-again", "./.");

        Run(fs, (ops, root) =>
        {
            foreach (string link in new[] { "self", "self-again" })
            {
                using (SafeDirHandle opened = OpenDirectory(ops, root, $"a/{link}"))
                {
                    AssertIs(ops, holder, opened);
                }

                using (SafeDirHandle opened = OpenDirectory(ops, root, $"a/{link}/inner"))
                {
                    AssertIs(ops, inner, opened);
                }

                CapResult<SafeFileHandle> file =
                    PortableResolver.OpenFile(root, Parse($"a/{link}"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
                Assert.False(file.IsSuccess);
                Assert.Equal(CapErrorCategory.IsADirectory, file.Error.Category);
            }
        });
    }

    /// <summary>A mount appearing inside the sandbox can be refused.</summary>
    /// <remarks>
    /// Whoever writes the mount table is outside the trust boundary, so a filesystem grafted
    /// in beneath the root is not something the root's authority was ever meant to cover. It
    /// is a choice rather than a rule because a great many legitimate sandboxes contain one.
    /// </remarks>
    [Fact]
    public void A_mount_point_can_be_refused()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddMountPoint("sandbox/mnt", volumeId: 2);
        _ = fs.AddDirectory("sandbox/mnt/inner");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle crossed = OpenDirectory(ops, root, "mnt/inner");
            Assert.False(crossed.IsInvalid);

            AssertFails(CapErrorCategory.CrossDevice, ops, root, "mnt", ConfinedResolveOptions.RefuseMountCrossing);
            AssertFails(
                CapErrorCategory.CrossDevice, ops, root, "mnt/inner", ConfinedResolveOptions.RefuseMountCrossing);
        });
    }

    /// <summary>
    /// A reparse point whose tag is not a filesystem link is refused, never read as one.
    /// </summary>
    [Fact]
    public void A_reparse_point_that_is_not_a_link_is_never_followed()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddOpaqueReparsePoint("sandbox/weird", 0xA000_0123);

        Run(fs, (ops, root) => AssertFails(CapErrorCategory.Reparse, ops, root, "weird"));
    }

    /// <summary>
    /// A path naming nothing is refused rather than read as naming the directory the caller
    /// already holds.
    /// </summary>
    [Fact]
    public void A_path_that_names_nothing_is_refused()
    {
        FakeFileSystem fs = Sandbox();

        Run(fs, (ops, root) =>
        {
            CapResult<SafeDirHandle> result =
                PortableResolver.OpenDirectory(root, default, CapAccess.Read, ConfinedResolveOptions.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(CapErrorCategory.InvalidArgument, result.Error.Category);
        });
    }

    private static FakeFileSystem Sandbox()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        return fs;
    }

    private static FakeNode LinkNode(FakeFileSystem fs, string target) => new()
    {
        Type = CapNodeType.SymbolicLink,
        VolumeId = 1,
        NodeId = fs.NextNodeId(),
        LinkTarget = target,
    };

    /// <summary>
    /// Parses under POSIX rules on every platform, because the simulated filesystem is not
    /// any platform's, and preserving <c>..</c> because moving up is what the walk is being
    /// asked about.
    /// </summary>
    private static CapPath Parse(string raw)
    {
        Assert.True(
            CapPath.TryParse(raw, CapPathSyntax.Unix, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError error),
            $"'{raw}' did not parse: {error}");
        return path;
    }

    private static void Run(FakeFileSystem fs, Action<FakePlatformOps, SafeDirHandle> body)
    {
        FakePlatformOps ops = new(fs);
        using (PlatformOps.Substitute(ops))
        {
            CapResult<SafeDirHandle> root = ops.OpenAmbientDirectory("sandbox", CapAccess.Read);
            Assert.True(root.IsSuccess, root.Error.FailureDescription);

            using SafeDirHandle handle = root.Value!;
            body(ops, handle);
        }
    }

    private static SafeDirHandle OpenDirectory(
        FakePlatformOps ops,
        SafeDirHandle root,
        string path,
        ConfinedResolveOptions options = ConfinedResolveOptions.None)
    {
        _ = ops;
        CapResult<SafeDirHandle> result =
            PortableResolver.OpenDirectory(root, Parse(path), CapAccess.Read, options);
        Assert.True(result.IsSuccess, $"'{path}': {result.Error.FailureDescription}");
        return result.Value!;
    }

    private static void AssertFails(
        CapErrorCategory expected,
        FakePlatformOps ops,
        SafeDirHandle root,
        string path,
        ConfinedResolveOptions options = ConfinedResolveOptions.None)
    {
        _ = ops;
        CapResult<SafeDirHandle> result =
            PortableResolver.OpenDirectory(root, Parse(path), CapAccess.Read, options);

        Assert.False(result.IsSuccess, $"'{path}' resolved when it should not have.");
        Assert.Equal(expected, result.Error.Category);
    }

    /// <summary>
    /// Asserts a handle refers to a given object by identity rather than by the name it was
    /// opened under, since the name is the one thing an attacker can reassign.
    /// </summary>
    private static void AssertIs(FakePlatformOps ops, FakeNode expected, SafeDirHandle handle)
    {
        Assert.True(ops.StatHandle(handle, out CapNodeInfo info).IsSuccess);
        Assert.True(info.IsSameNodeAs(expected.Info), "The walk reached a different object.");
    }
}
