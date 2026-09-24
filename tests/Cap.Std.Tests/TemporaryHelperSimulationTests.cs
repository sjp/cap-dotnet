using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Tests.Fakes;

namespace Cap.Std.Tests;

/// <summary>
/// Scratch directories and files against a simulated platform.
/// </summary>
/// <remarks>
/// <para>
/// Four things the real filesystem cannot be made to show on demand. What permissions a
/// creation asks the system for, rather than what they end up as once a umask has had its
/// say. What happens when an object refuses its own removal, which is a Windows behaviour and
/// so unobservable everywhere else. Both answers to "can this system make a file with no
/// name", when the machine running the tests has only one. And a tree deeper than the walk
/// is willing to follow, which is expensive to build for real and trivial to simulate.
/// </para>
/// <para>
/// The simulation stands in for the platform, not for the code under test: everything above
/// the platform contract is the shipping code, driven the way a caller drives it.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class TemporaryHelperSimulationTests
{
    /// <summary>Where the simulated platform says scratch files go.</summary>
    private const string TemporaryLocation = "/tmp";

    private static FakeFileSystem Simulated()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory(TemporaryLocation);
        return fs;
    }

    /// <summary>The path the simulation holds a scratch object under.</summary>
    private static string Inside(string name) => $"{TemporaryLocation}/{name}";

    /// <summary>The creation asks for a directory only its owner can enter.</summary>
    /// <remarks>
    /// Asserted about the request rather than the result, which is the only place it can be
    /// asserted: a umask narrows what is asked for, so a machine configured to clear those
    /// bits anyway would let a library that asked for anything at all pass.
    /// </remarks>
    [Fact]
    public void A_scratch_directory_is_asked_for_closed_to_everybody_else()
    {
        FakeFileSystem fs = Simulated();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());

            FakeNode? created = fs.Find(Inside(temp.Name));

            Assert.NotNull(created);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                created!.UnixMode);
        }
    }

    /// <summary>An ordinary directory creation is not narrowed the same way.</summary>
    /// <remarks>
    /// The other half of the same decision, and the one that keeps it honest. Asking for
    /// less than the system's default is right only where this library chose the location;
    /// a directory the caller named should be no different from one made by any other
    /// program.
    /// </remarks>
    [Fact]
    public void A_directory_the_caller_named_is_asked_for_with_the_usual_permissions()
    {
        FakeFileSystem fs = Simulated();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            using Dir root = Dir.Open(TemporaryLocation, AmbientAuthority.Acquire());
            using Dir made = root.CreateDir("ordinary");

            FakeNode? created = fs.Find(Inside("ordinary"));

            Assert.NotNull(created);
            Assert.True(created!.UnixMode!.Value.HasFlag(UnixFileMode.OtherRead));
        }
    }

    /// <summary>A system with no temporary location says so rather than guessing at one.</summary>
    [Fact]
    public void A_system_with_no_temporary_location_reports_it()
    {
        FakeFileSystem fs = Simulated();
        fs.TemporaryDirectory = null;

        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            _ = Assert.Throws<DirectoryNotFoundException>(() => CapTempDir.New(AmbientAuthority.Acquire()));
        }
    }

    /// <summary>A file that refuses its own removal is cleared, and then removed.</summary>
    /// <remarks>
    /// The Windows read-only attribute, which is a property of the file rather than a
    /// permission and stops a deletion the account is entitled to make. A cleanup that did
    /// not deal with it would leave the directory behind for a reason having nothing to do
    /// with rights.
    /// </remarks>
    [Fact]
    public void A_file_that_refuses_removal_is_cleared_and_removed()
    {
        FakeFileSystem fs = Simulated();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            string name = temp.Name;

            temp.Directory.CreateFile("stubborn").Dispose();
            fs.Find(Inside($"{name}/stubborn"))!.RefusesRemoval = true;

            temp.Dispose();

            Assert.Null(fs.Find(Inside(name)));
        }
    }

    /// <summary>A file that goes on refusing is left behind, and disposal still returns.</summary>
    /// <remarks>
    /// Cleanup reports nothing, so the assertion is about what it does not do: it does not
    /// throw out of a disposal, and it does not retry for ever against something that keeps
    /// saying no.
    /// </remarks>
    [Fact]
    public void A_file_that_cannot_be_removed_is_left_behind_without_a_failure()
    {
        FakeFileSystem fs = Simulated();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            string name = temp.Name;

            temp.Directory.CreateFile("immovable").Dispose();
            FakeNode file = fs.Find(Inside($"{name}/immovable"))!;
            file.RefusesRemoval = true;
            file.Unreadable = true;

            temp.Dispose();

            Assert.NotNull(fs.Find(Inside(name)));
        }
    }

    /// <summary>A file with no name is used where the system offers one.</summary>
    [Fact]
    public void A_nameless_scratch_file_is_used_where_the_system_offers_one()
    {
        FakeFileSystem fs = Simulated();
        fs.SupportsAnonymousFiles = true;

        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            using CapTempFile file = CapTempFile.NewAnonymous(temp.Directory);

            Assert.False(file.HasName);
            Assert.Null(file.Name);
            Assert.Empty(temp.Directory.EnumerateEntries());
        }
    }

    /// <summary>Where it does not, a named file is used and says so.</summary>
    [Fact]
    public void A_named_scratch_file_is_used_where_the_system_offers_nothing_better()
    {
        FakeFileSystem fs = Simulated();
        fs.SupportsAnonymousFiles = false;

        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            using CapTempFile file = CapTempFile.NewAnonymous(temp.Directory);

            Assert.True(file.HasName);
            Assert.True(temp.Directory.Exists(file.Name!));
        }
    }

    /// <summary>A tree deeper than the walk will follow is left behind rather than followed.</summary>
    /// <remarks>
    /// The descent holds an open directory and a stack frame per level, so something that
    /// nests without end is a way to exhaust both. Refusing to go further leaves the tree on
    /// the disk, which is the lesser of the two outcomes and the one that can be recovered
    /// from.
    /// </remarks>
    [Fact]
    public void A_tree_too_deep_to_walk_is_left_behind()
    {
        FakeFileSystem fs = Simulated();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            string name = temp.Name;

            Dir current = temp.Directory.Clone();
            for (int depth = 0; depth < 300; depth++)
            {
                Dir next = current.CreateDir("down");
                current.Dispose();
                current = next;
            }

            current.Dispose();
            temp.Dispose();

            Assert.NotNull(fs.Find(Inside(name)));
        }
    }

    /// <summary>A tree within the limit is removed entirely.</summary>
    [Fact]
    public void A_deep_but_walkable_tree_is_removed()
    {
        FakeFileSystem fs = Simulated();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            string name = temp.Name;

            Dir current = temp.Directory.Clone();
            for (int depth = 0; depth < 200; depth++)
            {
                current.CreateFile("leaf").Dispose();
                Dir next = current.CreateDir("down");
                current.Dispose();
                current = next;
            }

            current.Dispose();
            temp.Dispose();

            Assert.Null(fs.Find(Inside(name)));
        }
    }

    /// <summary>
    /// A subdirectory swapped for a link to another directory in the tree, between being
    /// listed and being opened, is not emptied through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scratch directory carries <see cref="SymlinkPolicy.FollowWithinSandbox"/>, under
    /// which an open of a link that stays inside is followed. Disposal must not inherit that:
    /// what it opens has to be what it listed, or a link swapped in would redirect part of the
    /// removal onto a directory it was never asked to reach that way.
    /// </para>
    /// <para>
    /// Disposal removes the link's target in the end anyway, when it comes to it by its own
    /// name, so the final state cannot tell the two apart. What can is the target's state each
    /// time disposal looks at the swapped name, the last of which is its removal: by then the
    /// link has been dealt with, and the target must still hold what it held.
    /// </para>
    /// </remarks>
    [Fact]
    public void Disposal_does_not_empty_a_directory_through_a_link_swapped_in_beneath_it()
    {
        FakeFileSystem fs = Simulated();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            Assert.Equal(SymlinkPolicy.FollowWithinSandbox, temp.Directory.SymlinkPolicy);
            string name = temp.Name;

            // Created first so that disposal reaches it before the directory it will lead to.
            temp.Directory.CreateDir("swapped").Dispose();
            temp.Directory.CreateDir("target").Dispose();
            temp.Directory.CreateFile("target/survivor").Dispose();

            FakeNode tree = fs.Find(Inside(name))!;
            FakeNode target = fs.Find(Inside($"{name}/target"))!;
            bool swapped = false;
            bool intact = true;
            fs.BeforeLookup = (directory, entry) =>
            {
                if (directory != tree || entry != "swapped")
                {
                    return;
                }

                if (!swapped)
                {
                    swapped = true;
                    fs.Replace(Inside($"{name}/swapped"), new FakeNode
                    {
                        Type = CapNodeType.SymbolicLink,
                        LinkTarget = "target",
                        VolumeId = 1,
                        NodeId = fs.NextNodeId(),
                    });
                }
                else
                {
                    intact &= target.Entries.ContainsKey("survivor");
                }
            };

            temp.Dispose();
            fs.BeforeLookup = null;

            Assert.True(swapped);
            Assert.True(intact, "The target was emptied through the link before disposal reached it.");
            Assert.Null(fs.Find(Inside(name)));
        }
    }
}
