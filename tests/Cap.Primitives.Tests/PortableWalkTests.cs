using System.Runtime.CompilerServices;
using Cap.Primitives.Interop;
using Cap.Std.Testing;
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
public sealed class PortableWalkTests
{
    /// <summary>Several names in a row reach the directory the path spells out.</summary>
    [Fact]
    public void A_path_of_several_components_reaches_what_it_names()
    {
        FakeFileSystem fs = Sandbox();
        MemoryNode target = fs.AddDirectory("sandbox/a/b/c");

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
        MemoryNode target = fs.AddDirectory("sandbox/a/c");

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
        MemoryNode sandbox = fs.Find("sandbox")!;

        Run(fs, (ops, root) =>
        {
            string descent = string.Join('/', levels);

            // Climbing back out one level at a time lands on each directory the walk passed
            // through, in reverse, and finally on the root itself.
            for (int up = 1; up <= depth; up++)
            {
                string path = descent + string.Concat(Enumerable.Repeat("/..", up));
                MemoryNode expected = up == depth
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
        MemoryNode x = fs.AddDirectory("sandbox/x");
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

    /// <summary>
    /// A path ending back on a directory the walk already holds with the access asked for,
    /// or more, hands that directory back without reopening it.
    /// </summary>
    /// <remarks>
    /// The root is held for reading, so <c>a/..</c> asked for with no access or for reading
    /// already has everything it needs. Reopening it to narrow it would cost syscalls on
    /// every <c>..</c>-spelled name the higher layers check, to grant nothing.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ending_on_a_held_directory_does_not_reopen_it_when_it_already_has_the_access_asked_for(
        bool forReading)
    {
        CapAccess access = forReading ? CapAccess.Read : CapAccess.None;
        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/a");
        MemoryNode sandbox = fs.Find("sandbox")!;

        Run(fs, (ops, root) =>
        {
            int before = ops.DirectoryReopens;

            CapResult<SafeDirHandle> result =
                PortableResolver.OpenDirectory(root, Parse("a/.."), access, ConfinedResolveOptions.None);
            Assert.True(result.IsSuccess, result.Error.FailureDescription);
            using SafeDirHandle opened = result.Value!;

            Assert.Equal(before, ops.DirectoryReopens);
            Assert.Equal(CapAccess.Read, opened.Access);
            AssertIs(ops, sandbox, opened);
        });
    }

    /// <summary>
    /// A path ending back on a directory the walk only passed through is reopened with the
    /// access asked for, so the caller can read what it was handed.
    /// </summary>
    [Fact]
    public void Ending_on_a_traversal_only_directory_reopens_it_for_reading()
    {
        FakeFileSystem fs = Sandbox();
        MemoryNode a = fs.AddDirectory("sandbox/a");
        _ = fs.AddDirectory("sandbox/a/b");

        Run(fs, (ops, root) =>
        {
            int before = ops.DirectoryReopens;

            using SafeDirHandle opened = OpenDirectory(ops, root, "a/b/..");

            Assert.Equal(before + 1, ops.DirectoryReopens);
            Assert.Equal(CapAccess.Read, opened.Access);
            AssertIs(ops, a, opened);
        });
    }

    /// <summary>A link whose target stays inside is followed.</summary>
    [Fact]
    public void A_link_inside_the_sandbox_is_followed()
    {
        FakeFileSystem fs = Sandbox();
        MemoryNode target = fs.AddDirectory("sandbox/real/inner");
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
        MemoryNode target = fs.AddDirectory("sandbox/a/sibling");
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
            MemoryNode target = fs.AddDirectory("sandbox/end");
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

    /// <summary>
    /// Chains longer than the frames the walk first makes room for still reach their end,
    /// across every point at which the room has to grow.
    /// </summary>
    /// <remarks>
    /// Each link in a chain is the last component of the target before it, so every frame
    /// stays held until the end is reached: a chain of <c>n</c> is <c>n</c> frames deep.
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    public void A_chain_deeper_than_the_initial_frame_capacity_still_resolves(int length)
    {
        FakeFileSystem fs = Sandbox();
        MemoryNode target = fs.AddDirectory("sandbox/end/inner");
        for (int i = 0; i < length; i++)
        {
            _ = fs.AddSymbolicLink($"sandbox/link{i}", i == length - 1 ? "end" : $"link{i + 1}");
        }

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "link0/inner");
            AssertIs(ops, target, opened);
        });
    }

    /// <summary>
    /// Following one link costs nothing beyond what reading the link itself costs: the frame
    /// it is resolved through comes from a pool and goes back to it.
    /// </summary>
    /// <remarks>
    /// Measured as the difference from the same walk through a real directory, so that what
    /// the simulation allocates for each step is not counted against the walk. What is left
    /// is the stored target the platform hands back and what parsing it takes, which is tens
    /// of bytes; a frame array sized for the worst case would be about a kilobyte.
    /// </remarks>
    [Fact]
    public void Following_one_link_allocates_only_the_target_string()
    {
        const int Iterations = 1000;

        FakeFileSystem fs = Sandbox();
        _ = fs.AddDirectory("sandbox/real/inner");
        _ = fs.AddSymbolicLink("sandbox/link", "real");

        Run(fs, (ops, root) =>
        {
            CapPath direct = Parse("real/inner");
            CapPath throughLink = Parse("link/inner");

            long directBytes = Measure(root, direct);
            long linkBytes = Measure(root, throughLink);

            long perResolution = (linkBytes - directBytes) / Iterations;
            Assert.True(
                perResolution < 128,
                $"Following one link cost {perResolution} bytes more per resolution than not following one.");
        });

        static long Measure(SafeDirHandle root, CapPath path)
        {
            // Warm up first: the first calls pay for jitting, and the first rent fills the pool.
            for (int i = 0; i < 64; i++)
            {
                OpenAndDispose(root, path);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                OpenAndDispose(root, path);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void OpenAndDispose(SafeDirHandle root, CapPath path)
        {
            CapResult<SafeDirHandle> result =
                PortableResolver.OpenDirectory(root, path, CapAccess.Read, ConfinedResolveOptions.None);
            result.Value!.Dispose();
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
        MemoryNode a = fs.Find("sandbox/a")!;

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
        MemoryNode a = fs.AddDirectory("sandbox/a");
        MemoryNode directory = fs.AddDirectory("sandbox/a/b");
        MemoryNode c = fs.AddDirectory("sandbox/a/b/c");
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
        MemoryNode a = fs.AddDirectory("sandbox/a");
        MemoryNode directory = fs.AddDirectory("sandbox/a/b");
        MemoryNode link = LinkNode(fs, "../a");

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
        MemoryNode a = fs.Find("sandbox/a")!;
        MemoryNode b = fs.AddDirectory("sandbox/a/b");
        MemoryNode decoy = fs.AddDirectory("decoy");
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
        MemoryNode parent = fs.AddDirectory("sandbox/a/b");

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
        MemoryNode sandbox = fs.Find("sandbox")!;
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
        MemoryNode dir = fs.AddDirectory("sandbox/dir");
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
        MemoryNode holder = fs.AddDirectory("sandbox/a");
        MemoryNode inner = fs.AddDirectory("sandbox/a/inner");
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

    /// <summary>Every row of the unusable-link-target table, on each resolution strategy.</summary>
    public static TheoryData<string, bool> LinkTargetRows
    {
        get
        {
            TheoryData<string, bool> rows = [];
            foreach (string name in LinkTargets.Keys)
            {
                rows.Add(name, false);
                rows.Add(name, true);
            }

            return rows;
        }
    }

    /// <summary>
    /// A link whose stored text the parser refuses is answered as a caller's own path of that
    /// shape would be, on the walk and on the confined open alike, from the text alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller's path is read under Windows rules, which is what gives the target the
    /// rooted, device and rewritten-name shapes to be refused for; the simulation itself
    /// belongs to no platform, so this runs on every agent. Both strategies are read against
    /// the same expected value rather than compared with each other, which would pass when
    /// both were wrong alike.
    /// </para>
    /// <para>
    /// The only name looked up is the link's own: a target is refused before anything it
    /// names is looked for, so a planted link cannot be used to probe what lies outside.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(LinkTargetRows))]
    public void A_link_target_the_parser_refuses_is_reported_as_the_walk_would_report_a_callers_path(
        string row, bool confined)
    {
        (string target, CapErrorCategory expected) = LinkTargets[row];
        FakeFileSystem fs = Sandbox();
        fs.PathSyntax = CapPathSyntax.Windows;
        fs.SupportsConfinedOpen = confined;
        MemoryNode sandbox = fs.Find("sandbox")!;
        _ = fs.AddDirectory("sandbox/dir");
        _ = fs.AddSymbolicLink("sandbox/l", target);

        List<string> lookups = [];
        fs.BeforeLookup = (_, name) => lookups.Add(name);

        Assert.True(
            CapPath.TryParse("l", CapPathSyntax.Windows, ParentLinkPolicy.Preserve, out CapPath path, out _));
        FileOpenRequest read = FileOpenRequest.Existing(FileAccess.Read);

        Run(fs, (ops, root) =>
        {
            int baseline = ops.OpenHandleCount;
            lookups.Clear();

            CapResult<SafeDirHandle> directory = Resolver.OpenDirectory(root, in path, CapAccess.Read, ConfinedResolveOptions.None);
            using (directory.Value)
            {
                if (expected == CapErrorCategory.None)
                {
                    Assert.True(directory.IsSuccess, directory.Error.FailureDescription);
                    AssertIs(ops, sandbox, directory.Value!);
                }
                else
                {
                    Assert.False(directory.IsSuccess, $"'{target}' resolved when it should not have.");
                    Assert.Equal(expected, directory.Error.Category);
                    Assert.All(lookups, name => Assert.Equal("l", name));
                }
            }

            Assert.Equal(baseline, ops.OpenHandleCount);
            lookups.Clear();

            CapResult<SafeFileHandle> file = Resolver.OpenFile(root, in path, in read, ConfinedResolveOptions.None);
            file.Value?.Dispose();
            Assert.False(file.IsSuccess, $"'{target}' opened as a file.");
            if (expected == CapErrorCategory.None)
            {
                Assert.Equal(CapErrorCategory.IsADirectory, file.Error.Category);
            }
            else
            {
                Assert.Equal(expected, file.Error.Category);
                Assert.All(lookups, name => Assert.Equal("l", name));
            }

            Assert.Equal(baseline, ops.OpenHandleCount);
        });
    }

    /// <summary>
    /// Link targets the parser refuses under Windows rules, and what resolving a link holding
    /// each must report; <see cref="CapErrorCategory.None"/> is the one target that is not a
    /// refusal, <c>.</c>, which names the directory holding the link.
    /// </summary>
    private static readonly Dictionary<string, (string Target, CapErrorCategory Expected)> LinkTargets = new()
    {
        ["reserved-name"] = ("CON", CapErrorCategory.Escaped),
        ["reserved-name-in-a-later-component"] = (@"dir\NUL.txt", CapErrorCategory.Escaped),
        ["absolute"] = (@"C:\x", CapErrorCategory.Escaped),
        ["drive-relative"] = ("C:x", CapErrorCategory.Escaped),
        ["root-relative"] = (@"\x", CapErrorCategory.Escaped),
        ["unc"] = (@"\\srv\share", CapErrorCategory.Escaped),
        ["device-namespace"] = (@"\\?\C:\x", CapErrorCategory.Escaped),
        ["trailing-dot"] = ("foo.", CapErrorCategory.InvalidArgument),
        ["invalid-character"] = ("a|b", CapErrorCategory.InvalidArgument),
        ["component-too-long"] = (@"dir\" + new string('a', CapPath.MaxComponentLength + 1), CapErrorCategory.NameTooLong),
        ["empty"] = ("", CapErrorCategory.NotFound),
        ["dot"] = (".", CapErrorCategory.None),
    };

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
        _ = fs.AddFile("sandbox/mnt/f");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle crossed = OpenDirectory(ops, root, "mnt/inner");
            Assert.False(crossed.IsInvalid);

            AssertFails(CapErrorCategory.CrossDevice, ops, root, "mnt", ConfinedResolveOptions.RefuseMountCrossing);
            AssertFails(
                CapErrorCategory.CrossDevice, ops, root, "mnt/inner", ConfinedResolveOptions.RefuseMountCrossing);

            int baseline = ops.OpenHandleCount;
            FileOpenRequest read = FileOpenRequest.Existing(FileAccess.Read);

            CapResult<SafeFileHandle> file =
                PortableResolver.OpenFile(root, Parse("mnt/f"), in read, ConfinedResolveOptions.RefuseMountCrossing);
            Assert.False(file.IsSuccess);
            Assert.Equal(CapErrorCategory.CrossDevice, file.Error.Category);

            foreach (string path in (string[])["mnt", "mnt/f"])
            {
                CapResult<OpenedNode> node =
                    PortableResolver.OpenNode(root, Parse(path), in read, ConfinedResolveOptions.RefuseMountCrossing);
                Assert.False(node.IsSuccess, $"'{path}' opened when it should not have.");
                Assert.Equal(CapErrorCategory.CrossDevice, node.Error.Category);
            }

            CapResult<ResolvedParent> parent =
                PortableResolver.ResolveParent(root, Parse("mnt/x"), ConfinedResolveOptions.RefuseMountCrossing);
            Assert.False(parent.IsSuccess);
            Assert.Equal(CapErrorCategory.CrossDevice, parent.Error.Category);

            Assert.Equal(baseline, ops.OpenHandleCount);
        });
    }

    /// <summary>
    /// An open that may create its file still creates it when mount crossings are refused,
    /// as the kernel's own resolution does: a name not there yet has no volume to be asked
    /// about, and whatever is created lands in a directory whose volume was already checked.
    /// </summary>
    [Theory]
    [InlineData(FileMode.CreateNew)]
    [InlineData(FileMode.Create)]
    [InlineData(FileMode.OpenOrCreate)]
    [InlineData(FileMode.Append)]
    public void A_creating_open_under_a_mount_refusing_resolution_creates_the_file(FileMode mode)
    {
        FakeFileSystem fs = Sandbox();
        FileAccess access = mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite;
        FileOpenRequest request = new(mode, access, FileShare.None, FileOptions.None, 0);

        Run(fs, (ops, root) =>
        {
            int baseline = ops.OpenHandleCount;

            CapResult<SafeFileHandle> result =
                PortableResolver.OpenFile(root, Parse("new"), in request, ConfinedResolveOptions.RefuseMountCrossing);

            Assert.True(result.IsSuccess, result.Error.FailureDescription);
            result.Value!.Dispose();
            Assert.Equal(baseline, ops.OpenHandleCount);

            Assert.True(ops.StatChild(root, "new", out CapNodeInfo created).IsSuccess);
            Assert.Equal(CapNodeType.File, created.Type);
        });
    }

    /// <summary>
    /// Letting a missing name through does not let a creating open past a mount point on the
    /// way to it, nor onto an existing name that lives on another volume.
    /// </summary>
    [Fact]
    public void A_creating_open_under_a_mount_refusing_resolution_still_refuses_another_volume()
    {
        FakeFileSystem fs = Sandbox();
        _ = fs.AddMountPoint("sandbox/mnt", volumeId: 2);
        fs.AddFile("sandbox/foreign").VolumeId = 2;
        FileOpenRequest createNew = new(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0);
        FileOpenRequest openOrCreate =
            new(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0);

        Run(fs, (ops, root) =>
        {
            int baseline = ops.OpenHandleCount;

            CapResult<SafeFileHandle> beyond =
                PortableResolver.OpenFile(root, Parse("mnt/new"), in createNew, ConfinedResolveOptions.RefuseMountCrossing);
            Assert.False(beyond.IsSuccess);
            Assert.Equal(CapErrorCategory.CrossDevice, beyond.Error.Category);
            Assert.Null(fs.Find("sandbox/mnt/new"));

            CapResult<SafeFileHandle> onto =
                PortableResolver.OpenFile(root, Parse("foreign"), in openOrCreate, ConfinedResolveOptions.RefuseMountCrossing);
            Assert.False(onto.IsSuccess);
            Assert.Equal(CapErrorCategory.CrossDevice, onto.Error.Category);

            Assert.Equal(baseline, ops.OpenHandleCount);
        });
    }

    /// <summary>Every row of the mount-crossing table, on each resolution strategy.</summary>
    public static TheoryData<string, bool> MountCrossingRows
    {
        get
        {
            TheoryData<string, bool> rows = [];
            foreach (string name in MountCrossings.Keys)
            {
                rows.Add(name, false);
                rows.Add(name, true);
            }

            return rows;
        }
    }

    /// <summary>
    /// Refusing mount crossings gives the same answer on the walk and on the confined open,
    /// whichever kind of open meets the mount, and costs nothing when no mount is met.
    /// </summary>
    /// <remarks>
    /// Each row is asserted twice: under the option, against its expected answer, and without
    /// it, as a pass-through, so a row cannot pass by the path simply not resolving. Both
    /// strategies are read against the same expected value rather than compared with each
    /// other, which would pass when both were wrong alike. Opens that may create are not
    /// here, because the simulated confined open cannot create; the walk's answer for them is
    /// asserted above.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MountCrossingRows))]
    public void A_mount_crossing_is_answered_alike_by_the_walk_and_the_confined_open(string row, bool confined)
    {
        (MountOpen open, string path, CapErrorCategory refused) = MountCrossings[row];
        FakeFileSystem fs = Sandbox();
        fs.SupportsConfinedOpen = confined;
        _ = fs.AddMountPoint("sandbox/mnt", volumeId: 2);
        _ = fs.AddDirectory("sandbox/mnt/inner");
        _ = fs.AddFile("sandbox/mnt/f");
        _ = fs.AddFile("sandbox/plain/f");
        fs.AddFile("sandbox/foreign").VolumeId = 2;
        _ = fs.AddSymbolicLink("sandbox/via-link", "mnt/inner");
        _ = fs.AddSymbolicLink("sandbox/file-link", "mnt/f");

        Run(fs, (ops, root) =>
        {
            int baseline = ops.OpenHandleCount;

            Assert.Equal(CapErrorCategory.None, OpenAndClose(root, open, path, ConfinedResolveOptions.None));
            Assert.Equal(baseline, ops.OpenHandleCount);

            Assert.Equal(refused, OpenAndClose(root, open, path, ConfinedResolveOptions.RefuseMountCrossing));
            Assert.Equal(baseline, ops.OpenHandleCount);
        });
    }

    /// <summary>
    /// A mount swapped in between the walk asking what a name is and opening it is refused
    /// by an open of whatever the name holds, which asks the directory it opened again.
    /// </summary>
    [Fact]
    public void A_mount_swapped_in_before_an_open_of_any_kind_is_still_refused()
    {
        FakeFileSystem fs = Sandbox();
        MemoryNode plain = fs.AddDirectory("sandbox/swapped");
        MemoryNode sandbox = fs.Find("sandbox")!;
        int lookups = 0;

        fs.BeforeLookup = (directory, name) =>
        {
            if (ReferenceEquals(directory, sandbox) && name == "swapped" && ++lookups == 2)
            {
                fs.Replace("sandbox/swapped", new MemoryNode
                {
                    Type = CapNodeType.Directory,
                    VolumeId = 2,
                    NodeId = fs.NextNodeId(),
                });
            }
        };

        Run(fs, (ops, root) =>
        {
            int baseline = ops.OpenHandleCount;

            CapResult<OpenedNode> node = PortableResolver.OpenNode(
                root, Parse("swapped"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.RefuseMountCrossing);

            Assert.Equal(2, lookups);
            Assert.False(node.IsSuccess, "A directory on another volume opened.");
            Assert.Equal(CapErrorCategory.CrossDevice, node.Error.Category);
            Assert.Equal(baseline, ops.OpenHandleCount);
        });

        Assert.NotSame(plain, fs.Find("sandbox/swapped"));
    }

    /// <summary>
    /// A root that cannot say which volume it is on fails a resolution that refuses mount
    /// crossings, before anything beneath it is opened, and resolves as usual otherwise.
    /// </summary>
    /// <remarks>
    /// Every later volume check compares against the root's, so going on without it would
    /// either refuse everything or refuse nothing.
    /// </remarks>
    [Fact]
    public void A_root_that_cannot_describe_itself_fails_a_mount_refusing_resolution()
    {
        FakeFileSystem fs = Sandbox();
        MemoryNode sandbox = fs.Find("sandbox")!;
        _ = fs.AddDirectory("sandbox/a");
        FakePlatformOps ops = new(fs)
        {
            HandleStatFault = node => ReferenceEquals(node, sandbox) ? CapErrorCategory.Unknown : CapErrorCategory.None,
        };
        CapResult<SafeDirHandle> opened = ops.OpenAmbientDirectory("sandbox", CapAccess.Read);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        using SafeDirHandle root = opened.Value!;
        int baseline = ops.OpenHandleCount;
        fs.BeforeLookup = (_, name) => Assert.Fail($"'{name}' was looked up beneath a root whose volume is unknown.");

        foreach (MountOpen open in Enum.GetValues<MountOpen>())
        {
            Assert.Equal(CapErrorCategory.Unknown, OpenAndClose(root, open, "a/x", ConfinedResolveOptions.RefuseMountCrossing));
            Assert.Equal(baseline, ops.OpenHandleCount);
        }

        fs.BeforeLookup = null;
        Assert.Equal(CapErrorCategory.None, OpenAndClose(root, MountOpen.Directory, "a", ConfinedResolveOptions.None));
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
    /// A directory whose reparse point only says which filter serves it — a cloud
    /// placeholder, a projected directory — is a directory like any other: the walk steps
    /// through it and it can be listed.
    /// </summary>
    [Fact]
    public void A_directory_a_filter_serves_is_walked_through_and_listed()
    {
        FakeFileSystem fs = Sandbox();
        MemoryNode placeholder = fs.AddOpaqueReparsePoint("sandbox/placeholder", 0x9000_001A, directory: true);
        _ = fs.AddFile("sandbox/placeholder/report");

        Run(fs, (ops, root) =>
        {
            using SafeDirHandle opened = OpenDirectory(ops, root, "placeholder");
            AssertIs(ops, placeholder, opened);

            CapResult<DirectoryReader> reader = ops.OpenDirectoryReader(opened);
            Assert.True(reader.IsSuccess, reader.Error.FailureDescription);
            using (DirectoryReader entries = reader.Value)
            {
                Assert.True(entries.Read(out bool advanced).IsSuccess);
                Assert.True(advanced);
                Assert.Equal("report", entries.CurrentName.ToString());
            }

            CapResult<SafeFileHandle> file = PortableResolver.OpenFile(
                root, Parse("placeholder/report"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
            Assert.True(file.IsSuccess, file.Error.FailureDescription);
            file.Value!.Dispose();
        });
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

    /// <summary>
    /// The mount-crossing table: which open meets the path, and what it reports when mount
    /// crossings are refused. <c>mnt</c> is on another volume, as is the file <c>foreign</c>
    /// standing directly in the root; <c>plain</c> is not.
    /// </summary>
    private static readonly Dictionary<string, (MountOpen Open, string Path, CapErrorCategory Refused)> MountCrossings = new()
    {
        ["directory-at-mount"] = (MountOpen.Directory, "mnt", CapErrorCategory.CrossDevice),
        ["directory-beyond-mount"] = (MountOpen.Directory, "mnt/inner", CapErrorCategory.CrossDevice),
        ["directory-through-link"] = (MountOpen.Directory, "via-link", CapErrorCategory.CrossDevice),
        ["directory-plain"] = (MountOpen.Directory, "plain", CapErrorCategory.None),
        ["file-beyond-mount"] = (MountOpen.File, "mnt/f", CapErrorCategory.CrossDevice),
        ["file-on-another-volume"] = (MountOpen.File, "foreign", CapErrorCategory.CrossDevice),
        ["file-through-link"] = (MountOpen.File, "file-link", CapErrorCategory.CrossDevice),
        ["file-plain"] = (MountOpen.File, "plain/f", CapErrorCategory.None),
        ["node-at-mount"] = (MountOpen.Node, "mnt", CapErrorCategory.CrossDevice),
        ["node-beyond-mount"] = (MountOpen.Node, "mnt/f", CapErrorCategory.CrossDevice),
        ["node-on-another-volume"] = (MountOpen.Node, "foreign", CapErrorCategory.CrossDevice),
        ["node-through-link"] = (MountOpen.Node, "via-link", CapErrorCategory.CrossDevice),
        ["node-plain-directory"] = (MountOpen.Node, "plain", CapErrorCategory.None),
        ["node-plain-file"] = (MountOpen.Node, "plain/f", CapErrorCategory.None),
        ["parent-beyond-mount"] = (MountOpen.Parent, "mnt/x", CapErrorCategory.CrossDevice),
        ["parent-of-mount"] = (MountOpen.Parent, "mnt", CapErrorCategory.None),
        ["parent-through-link"] = (MountOpen.Parent, "via-link/x", CapErrorCategory.CrossDevice),
        ["parent-plain"] = (MountOpen.Parent, "plain/x", CapErrorCategory.None),
    };

    /// <summary>The kinds of open the mount-crossing table drives.</summary>
    private enum MountOpen
    {
        Directory,
        File,
        Node,
        Parent,
    }

    /// <summary>
    /// Opens a path by whichever strategy the backend provides, closes what it opened, and
    /// reports the category, <see cref="CapErrorCategory.None"/> for success.
    /// </summary>
    /// <remarks>
    /// A resolved parent must hand back the caller's last name untouched, since that name is
    /// not looked at; that is asserted here so every parent row checks it.
    /// </remarks>
    private static CapErrorCategory OpenAndClose(SafeDirHandle root, MountOpen open, string raw, ConfinedResolveOptions options)
    {
        CapPath path = Parse(raw);
        FileOpenRequest read = FileOpenRequest.Existing(FileAccess.Read);

        switch (open)
        {
            case MountOpen.Directory:
                CapResult<SafeDirHandle> directory = Resolver.OpenDirectory(root, in path, CapAccess.Read, options);
                directory.Value?.Dispose();
                return directory.IsSuccess ? CapErrorCategory.None : directory.Error.Category;

            case MountOpen.File:
                CapResult<SafeFileHandle> file = Resolver.OpenFile(root, in path, in read, options);
                file.Value?.Dispose();
                return file.IsSuccess ? CapErrorCategory.None : file.Error.Category;

            case MountOpen.Node:
                CapResult<OpenedNode> node = Resolver.OpenNode(root, in path, in read, options);
                node.Value?.Dispose();
                return node.IsSuccess ? CapErrorCategory.None : node.Error.Category;

            default:
                CapResult<ResolvedParent> parent = Resolver.ResolveParent(root, in path, options);
                if (!parent.IsSuccess)
                {
                    return parent.Error.Category;
                }

                using (parent.Value!)
                {
                    Assert.Equal(raw[(raw.LastIndexOf('/') + 1)..], parent.Value!.Name);
                }

                return CapErrorCategory.None;
        }
    }

    private static FakeFileSystem Sandbox()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        return fs;
    }

    private static MemoryNode LinkNode(FakeFileSystem fs, string target) => new()
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
        CapResult<SafeDirHandle> root = ops.OpenAmbientDirectory("sandbox", CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);

        using SafeDirHandle handle = root.Value!;
        body(ops, handle);
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
    private static void AssertIs(FakePlatformOps ops, MemoryNode expected, SafeDirHandle handle)
    {
        Assert.True(ops.StatHandle(handle, out CapNodeInfo info).IsSuccess);
        Assert.True(info.IsSameNodeAs(expected.Info), "The walk reached a different object.");
    }
}
