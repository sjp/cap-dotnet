using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;
using Cap.Tests;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// The Windows walk against a real volume.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is behaviour of the operating system, not of a loop, which is why none of
/// it can be asserted anywhere but on the platform itself. Whether two names that differ in
/// case reach one file, whether a mangled alias reaches the object it was derived from,
/// whether a junction is followed before this code sees it, and what a user-facing path
/// string is allowed to open — these are the platform's answers, and the only way to know
/// them is to ask.
/// </para>
/// <para>
/// A junction carries most of the weight in the link cases rather than a symbolic link,
/// because creating one needs no privilege. Symbolic links need an elevated token or
/// developer mode, so the cases that need one say so and stand aside when the host will not
/// oblige. A junction is also the more interesting of the two here: its target is recorded as
/// a path from a volume root, so it can never stay beneath a directory handle, and it is the
/// shape a caller is most likely to meet by accident.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed partial class WindowsResolutionOnDiskTests : IDisposable
{
    /// <summary>How long one of the system's own tools is given before it is killed.</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("cap-win-").FullName;

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            PrepareForRemoval(_root);
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Clears the read-only flag of every entry beneath a directory, and removes every
    /// directory link and junction there as the links themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Done before the recursive removal rather than left to it. The framework's recursive
    /// removal takes a junction for a volume mount point and asks the system to unmount it
    /// first, which fails for a junction to an ordinary directory, and the whole removal
    /// fails with it. Removed on its own, a link is just a name.
    /// </para>
    /// <para>
    /// The flags are cleared because the removal-block cases leave read-only entries behind
    /// when they fail part way, and a removal that fails over them would report itself in
    /// place of whatever failed the case. A link's own flag is cleared, not its target's.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void PrepareForRemoval(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                SetOwnAttributes(entry, FileAttributes.Normal);
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(entry);
            }
            else
            {
                PrepareForRemoval(entry);
            }
        }
    }

    /// <summary>
    /// Two spellings that differ only in case are one file, and resolution agrees with the
    /// rest of the system about that.
    /// </summary>
    /// <remarks>
    /// Asserted by identity rather than by both opens succeeding. Succeeding twice would be
    /// consistent with two different objects, which is the reading under which a containment
    /// check written as a string comparison would look correct.
    /// </remarks>
    [Fact]
    public void Names_differing_only_in_case_reach_the_same_object()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        Directory.CreateDirectory(Path.Join(Sandbox, "Mixed", "inner"));

        using SafeDirHandle root = OpenSandbox();

        CapNodeInfo first = Identify(root, "Mixed");
        CapNodeInfo lowered = Identify(root, "mixed");
        CapNodeInfo raised = Identify(root, "MIXED");

        Assert.Equal(first.VolumeId, lowered.VolumeId);
        Assert.Equal(first.NodeId, lowered.NodeId);
        Assert.Equal(first.VolumeId, raised.VolumeId);
        Assert.Equal(first.NodeId, raised.NodeId);
    }

    /// <summary>
    /// The other half of the same fact: a refusal cannot be got past by changing the case of
    /// the name that earned it.
    /// </summary>
    /// <remarks>
    /// This is the case that makes case-insensitivity a security property rather than an
    /// ergonomic one. A sandbox that decided from the spelling of a name would have to decide
    /// the same way for every spelling that reaches the same file, and there are more of those
    /// than a comparison tends to account for.
    /// </remarks>
    [Fact]
    public void Changing_the_case_of_a_refused_name_does_not_get_past_the_refusal()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        BuildOutsideTarget();
        CreateJunction(Path.Join(Sandbox, "Jn"), Path.Join(_root, "outside"));

        using SafeDirHandle root = OpenSandbox();

        AssertFails(CapErrorCategory.Escaped, root, "Jn/inner");
        AssertFails(CapErrorCategory.Escaped, root, "jn/inner");
        AssertFails(CapErrorCategory.Escaped, root, "JN/inner");
        AssertFails(CapErrorCategory.Escaped, root, "jN/INNER");
    }

    /// <summary>
    /// A junction is refused, and refused as an attempt to leave rather than as a missing
    /// directory.
    /// </summary>
    /// <remarks>
    /// Two things are being checked at once. That the object manager did not follow it before
    /// this code saw it — every open here asks for the reparse point itself, and without that
    /// the junction's target would have been resolved by the system and the walk would have
    /// continued outside without ever learning a link existed. And that what the junction
    /// stores is what makes it unfollowable: its target is recorded as a path from a volume
    /// root, so it comes back spelled as one, and the path parser refuses every such spelling
    /// wherever a link is followed. Reading it is not following it, so the text itself is
    /// handed back — a caller auditing the links in a subtree has to be able to see the ones
    /// that lead out of it.
    /// </remarks>
    [Fact]
    public void A_junction_is_refused_rather_than_followed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        BuildOutsideTarget();
        CreateJunction(Path.Join(Sandbox, "Jn"), Path.Join(_root, "outside"));

        using SafeDirHandle root = OpenSandbox();

        AssertFails(CapErrorCategory.Escaped, root, "Jn");

        CapResult<string> read = PlatformOps.Host.ReadChildLink(root, "Jn");
        Assert.True(read.IsSuccess, $"a junction's target could not be read: {read.Error}.");
        Assert.True(
            CapPath.IsRooted(read.Value, CapPathSyntax.Windows),
            $"a junction's target came back as '{read.Value}', which does not name a location " +
            "from a root -- so nothing in resolution would refuse following it.");
    }

    /// <summary>
    /// A generated short name does not reach the object it was derived from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A volume that generates short names records two names for one entry, and an open by
    /// either reaches the same object. That is inside the directory and so not an escape; what
    /// it defeats is any rule a caller states about names, because such a rule is stated about
    /// one spelling and there are two.
    /// </para>
    /// <para>
    /// Stands aside where the volume does not generate short names, because then there is no
    /// alias to try and nothing the assertion would mean. That is a real configuration — the
    /// generation can be turned off per volume — so the case has to cope with it rather than
    /// assume it.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_short_name_alias_does_not_reach_the_object_it_aliases()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        const string LongName = "a long directory name";
        Directory.CreateDirectory(Path.Join(Sandbox, LongName, "inner"));

        string? alias = ShortNameOf(Path.Join(Sandbox, LongName));
        if (alias is null || alias.Equals(LongName, StringComparison.OrdinalIgnoreCase))
        {
            // Generation can be turned off per volume, and where it is there is no alias to
            // try and nothing an assertion would mean. A host that is supposed to have it on
            // says so, and there the absence of an alias is a broken harness rather than a
            // configuration to accommodate -- a case that always stood aside would leave the
            // defence untested everywhere.
            Assert.False(
                Environment.GetEnvironmentVariable("CAPDOTNET_EXPECT_SHORT_NAMES") == "1",
                $"no short name was generated for '{LongName}', but this host was set up to " +
                "generate them. The alias defence cannot be exercised as configured.");

            Assert.Skip("This volume does not generate short names, so there is no alias to refuse.");
        }

        using SafeDirHandle root = OpenSandbox();

        // The object's own name works, which is what makes the refusal below a refusal of the
        // alias and not of the directory.
        CapResult<SafeDirHandle> byOwnName = PortableResolver.OpenDirectory(
            root, Parse(LongName), CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(byOwnName.IsSuccess, byOwnName.Error.FailureDescription);
        byOwnName.Value!.Dispose();

        AssertFails(CapErrorCategory.AliasedName, root, alias);
        AssertFails(CapErrorCategory.AliasedName, root, alias + "/inner");
    }

    /// <summary>
    /// A name that merely contains a tilde is its own name, and is opened.
    /// </summary>
    /// <remarks>
    /// The tilde is what marks a name as possibly an alias, and refusing the character rather
    /// than the aliasing would make a legitimate filename permanently unreachable. So the
    /// check is a comparison against what the object says it is called, and a file that says
    /// this is its name is opened.
    /// </remarks>
    [Fact]
    public void A_name_that_merely_contains_a_tilde_is_its_own_name()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        Directory.CreateDirectory(Path.Join(Sandbox, "plain~1"));
        File.WriteAllText(Path.Join(Sandbox, "plain~1", "marker"), "x");

        using SafeDirHandle root = OpenSandbox();

        CapResult<SafeDirHandle> opened = PortableResolver.OpenDirectory(
            root, Parse("plain~1"), CapAccess.Read, ConfinedResolveOptions.None);

        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        opened.Value!.Dispose();
    }

    /// <summary>
    /// Where the normalised-name query is declined, the alias NTFS reports for an entry still
    /// refuses that alias and still lets a genuine tilde name through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback exists for filesystems that decline the normalised query, and NTFS never
    /// does, so the decline is supplied here and only the alias query is asked of the volume.
    /// What that proves is the part no build agent without Windows can: that the alias query
    /// is issued correctly and its reply read as the alias the volume generated.
    /// </para>
    /// <para>
    /// The genuine name is long and not eight-plus-three, like the Office lock files and
    /// editor backups that motivated the fallback, so its alias is some other spelling.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_alias_fallback_refuses_an_alias_and_passes_a_tilde_name_on_disk()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Short names exist only on Windows.");
            return;
        }

        const string LongName = "a long directory name";
        const string TildeName = "~$a long lock name.docx";
        Directory.CreateDirectory(Path.Join(Sandbox, LongName));
        Directory.CreateDirectory(Path.Join(Sandbox, TildeName));

        string? alias = ShortNameOf(Path.Join(Sandbox, LongName));
        if (alias is null || alias.Equals(LongName, StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(
                Environment.GetEnvironmentVariable("CAPDOTNET_EXPECT_SHORT_NAMES") == "1",
                $"no short name was generated for '{LongName}', but this host was set up to generate them.");

            Assert.Skip("This volume does not generate short names, so there is no alias to refuse.");
            return;
        }

        AliasedNameCheck.NameQuery declined = (nint _, out string name) =>
        {
            name = string.Empty;
            return CapError.Create(
                CapErrorCategory.NotSupported, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_INFO_CLASS);
        };

        using SafeDirHandle root = OpenSandbox();

        using (SafeDirHandle aliased = OpenDirectory(root, LongName))
        using (HandleLease lease = aliased.Lease())
        {
            CapError error = WindowsPlatformOps.QueryAlternateName(lease.Raw, out string reported);
            Assert.True(error.IsSuccess, error.FailureDescription);
            Assert.Equal(alias, reported, ignoreCase: true);

            Assert.Equal(
                CapErrorCategory.AliasedName,
                AliasedNameCheck.Refuse(lease.Raw, alias, declined, WindowsPlatformOps.QueryAlternateName).Category);
        }

        using (SafeDirHandle genuine = OpenDirectory(root, TildeName))
        using (HandleLease lease = genuine.Lease())
        {
            CapError error = AliasedNameCheck.Refuse(lease.Raw, TildeName, declined, WindowsPlatformOps.QueryAlternateName);
            Assert.True(error.IsSuccess, error.FailureDescription);
        }
    }

    private static SafeDirHandle OpenDirectory(SafeDirHandle root, string name)
    {
        CapResult<SafeDirHandle> opened = PortableResolver.OpenDirectory(
            root, Parse(name), CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        return opened.Value!;
    }

    /// <summary>
    /// The one open that takes a user-facing path refuses what is not a directory on a
    /// filesystem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place a path string is resolved by the system rather than a name by
    /// this library, so it is the only place a name can reach something that is not a file at
    /// all. Confining names beneath the handle afterwards would be beside the point if the
    /// handle were a serial port.
    /// </para>
    /// <para>
    /// The device names are the reason the check exists rather than being left to the caller.
    /// They look like ordinary relative filenames and they resolve wherever a filename is
    /// accepted, so a program that builds a root path from configuration can reach one without
    /// anything in the path looking unusual.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_first_directory_handle_refuses_what_is_not_a_filesystem_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string file = Path.Join(Sandbox, "ordinary.txt");
        File.WriteAllText(file, "x");

        AssertAmbientFails(file);
        AssertAmbientFails("NUL");
        AssertAmbientFails(Path.Join(Sandbox, "CON"));
    }

    /// <summary>
    /// A relative link that stays inside is followed, and an absolute one is refused, on this
    /// platform as on the others.
    /// </summary>
    /// <remarks>
    /// The link policy is meant to be the same wherever the walk runs, and a policy asserted
    /// only where links are cheap to create is a policy asserted on two platforms out of
    /// three. Needs a privilege this host may not grant, so it stands aside rather than
    /// failing when it cannot build what it needs.
    /// </remarks>
    [Fact]
    public void The_link_policy_is_the_same_here_as_elsewhere()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        BuildOutsideTarget();
        Directory.CreateDirectory(Path.Join(Sandbox, "inside", "deeper"));
        File.WriteAllText(Path.Join(Sandbox, "inside", "deeper", "marker"), "x");

        if (!TryCreateDirectoryLink(Path.Join(Sandbox, "shortcut"), "inside\\deeper") ||
            !TryCreateDirectoryLink(Path.Join(Sandbox, "escape"), "..\\outside") ||
            !TryCreateDirectoryLink(Path.Join(Sandbox, "rooted"), Path.Join(_root, "outside")))
        {
            Assert.Skip("This host will not create symbolic links for this process.");
        }

        using SafeDirHandle root = OpenSandbox();

        CapResult<SafeDirHandle> inside = PortableResolver.OpenDirectory(
            root, Parse("shortcut"), CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(inside.IsSuccess, inside.Error.FailureDescription);
        inside.Value!.Dispose();

        AssertFails(CapErrorCategory.Escaped, root, "escape");
        AssertFails(CapErrorCategory.Escaped, root, "rooted");
    }

    /// <summary>
    /// Symbolic links this library writes are followed by the system itself, to the target
    /// they were written with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other way round from the case above, and the half the library's own tests cannot
    /// reach: the writer and the reader were built together, so a wrong offset, a missing
    /// terminator or a wrong flag is one they agree about, and every round trip through both
    /// passes. The links are read here by the framework and the filesystem instead, which is
    /// what Explorer, the shell and every other tool on the machine will read them with.
    /// </para>
    /// <para>
    /// The targets cover both kinds of link and relative targets from the directory holding the
    /// link, including one that climbs out of it. Where a link lands relative to the sandbox is
    /// of no interest to the system, so none of those is refused. A rooted target is refused and
    /// leaves nothing behind: storing one would mean respelling it in the object manager's
    /// syntax, and the backend respells nothing but separators.
    /// </para>
    /// <para>
    /// Targets written with <c>/</c> are the ones that would otherwise pass every test here
    /// and fail everywhere else. Resolution through a directory handle reads <c>/</c> as a
    /// separator, but the filesystem does not when it resolves a relative link, so a link stored
    /// as written is followed by this library and dangles for every other program.
    /// </para>
    /// </remarks>
    [Fact]
    public void Links_written_here_are_followed_by_the_platform()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        const string Contents = "read through a link";
        BuildOutsideTarget();
        Directory.CreateDirectory(Path.Join(Sandbox, "inside", "deeper"));
        File.WriteAllText(Path.Join(Sandbox, "inside", "deeper", "marker"), "x");
        File.WriteAllText(Path.Join(Sandbox, "inside", "file.txt"), Contents);
        string rootedTarget = Path.Join(_root, "outside");

        CapResult<SafeDirHandle> opened = PlatformOps.Host.OpenAmbientDirectory(Sandbox, CapAccess.Read);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        using SafeDirHandle root = opened.Value!;

        CapError first = PlatformOps.Host.CreateChildSymbolicLink(root, "flink", @"inside\file.txt", false);
        ExpectedHostFeatures.Require(
            HostFeature.Symlinks,
            first.Category != CapErrorCategory.PermissionDenied,
            "The links this library writes cannot be checked against the system's reader without one.");
        Assert.True(first.IsSuccess, first.FailureDescription);

        CreateLink(root, "dlink", @"inside\deeper", targetIsDirectory: true);
        CreateLink(root, "sibling", @"..\outside", targetIsDirectory: true);
        CreateLink(root, "slashed-flink", "inside/file.txt", targetIsDirectory: false);
        CreateLink(root, "slashed-dlink", "inside/deeper", targetIsDirectory: true);
        foreach (string rooted in new[]
        {
            rootedTarget,
            @"\\?\" + rootedTarget,
            @"\\localhost\share\x",
            @"C:x",
            @"\x",
        })
        {
            CapError refused = PlatformOps.Host.CreateChildSymbolicLink(root, "rooted", rooted, targetIsDirectory: true);
            Assert.Equal(CapErrorCategory.InvalidArgument, refused.Category);
            Assert.False(Path.Exists(Path.Join(Sandbox, "rooted")), rooted);
        }

        string fileLink = Path.Join(Sandbox, "flink");
        Assert.Equal(Contents, File.ReadAllText(fileLink));
        FileInfo fileInfo = new(fileLink);
        Assert.Equal(FileAttributes.ReparsePoint, fileInfo.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory));
        Assert.Equal(@"inside\file.txt", fileInfo.LinkTarget);

        AssertDirectoryLink("dlink", @"inside\deeper", "marker");
        AssertDirectoryLink("sibling", @"..\outside", "secret");

        Assert.Equal(Contents, File.ReadAllText(Path.Join(Sandbox, "slashed-flink")));
        Assert.Contains("marker", Directory.EnumerateFiles(Path.Join(Sandbox, "slashed-dlink")).Select(Path.GetFileName));

        static void CreateLink(SafeDirHandle root, string name, string target, bool targetIsDirectory)
        {
            CapError error = PlatformOps.Host.CreateChildSymbolicLink(root, name, target, targetIsDirectory);
            Assert.True(error.IsSuccess, $"'{name}' -> '{target}': {error.FailureDescription}");
        }

        void AssertDirectoryLink(string name, string target, string entry)
        {
            string link = Path.Join(Sandbox, name);
            Assert.Contains(entry, Directory.EnumerateFiles(link).Select(Path.GetFileName));
            DirectoryInfo info = new(link);
            Assert.Equal(
                FileAttributes.ReparsePoint | FileAttributes.Directory,
                info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory));
            Assert.Equal(target, info.LinkTarget);
        }
    }

    /// <summary>
    /// A file the Windows Overlay Filter compressed is opened, read and described as the file
    /// it is, and is never read as a link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>compact /exe</c> gives the file a reparse point whose tag says the overlay filter
    /// serves its contents, and moves those contents out of the file's own stream. The tag
    /// does not redirect anywhere, so refusing the file as a way out would be refusing an
    /// ordinary file of the sandbox — and reading it without the filter would read the empty
    /// stream the volume stores rather than the text.
    /// </para>
    /// <para>
    /// Needs no privilege, but not every volume or edition will compress a file this way, so
    /// the case stands aside where the file comes back without the reparse point.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_file_the_overlay_filter_compressed_is_read_through_the_filter()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "packed.txt");
        string content = string.Concat(Enumerable.Repeat("a line the overlay filter will compress\n", 512));
        File.WriteAllText(path, content);

        RunCompact(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
        {
            Assert.Skip("This volume would not compress a file with the overlay filter.");
        }

        using SafeDirHandle root = OpenSandbox();

        CapResult<SafeFileHandle> opened = PortableResolver.OpenFile(
            root, Parse("packed.txt"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        using (SafeFileHandle file = opened.Value!)
        {
            Assert.Equal(content, System.Text.Encoding.UTF8.GetString(ReadAll(file)));
        }

        Assert.True(PlatformOps.Host.StatChild(root, "packed.txt", out CapNodeInfo info).IsSuccess);
        Assert.Equal(CapNodeType.File, info.Type);
        Assert.Equal(OverlayFilterTag, info.ReparseTag);

        CapError described = PlatformOps.Host.DescribeChild(root, "packed.txt", out CapNodeStat stat);
        Assert.True(described.IsSuccess, described.FailureDescription);
        Assert.Equal(CapFileType.File, stat.Type);

        CapResult<string> link = PlatformOps.Host.ReadChildLink(root, "packed.txt");
        Assert.False(link.IsSuccess, $"a compressed file was read as a link to '{link.Value}'.");
        Assert.Equal(CapErrorCategory.NotALink, link.Error.Category);
    }

    /// <summary>
    /// A reparse point whose tag stands for another object, by a means that is not a
    /// filesystem link, is refused and never read as a link.
    /// </summary>
    /// <remarks>
    /// The tag is a made-up one with the bit the system reserves for redirections, standing
    /// for every such tag this library does not know. A tag outside Microsoft's range needs no
    /// privilege to set; the case stands aside where the volume will not take one.
    /// </remarks>
    [Fact]
    public void A_redirecting_reparse_point_of_an_unknown_kind_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "elsewhere");
        File.WriteAllText(path, "x");
        if (!TrySetReparsePoint(path, 0x2000_0123))
        {
            Assert.Skip("This volume would not take a reparse point from this process.");
        }

        using SafeDirHandle root = OpenSandbox();

        CapResult<SafeFileHandle> opened = PortableResolver.OpenFile(
            root, Parse("elsewhere"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        if (opened.IsSuccess)
        {
            opened.Value!.Dispose();
            Assert.Fail("a redirecting reparse point was opened.");
        }

        Assert.Equal(CapErrorCategory.Reparse, opened.Error.Category);
        AssertFails(CapErrorCategory.Reparse, root, "elsewhere");

        Assert.True(PlatformOps.Host.StatChild(root, "elsewhere", out CapNodeInfo info).IsSuccess);
        Assert.Equal(CapNodeType.UnknownReparsePoint, info.Type);

        CapResult<string> link = PlatformOps.Host.ReadChildLink(root, "elsewhere");
        Assert.False(link.IsSuccess, $"a redirecting reparse point was read as a link to '{link.Value}'.");
        Assert.Equal(CapErrorCategory.Reparse, link.Error.Category);
    }

    /// <summary>
    /// A reparse point whose tag redirects nothing, but which no filter on this system
    /// serves, cannot be opened — and is reported as unsupported, not as an escape.
    /// </summary>
    /// <remarks>
    /// The same situation as a cloud placeholder whose provider has been uninstalled, or an
    /// application execution alias, which only the process launcher reads. Nothing about the
    /// entry points anywhere, so calling the failure an attempt to leave would be false.
    /// </remarks>
    [Fact]
    public void A_reparse_point_no_filter_serves_is_unsupported_rather_than_an_escape()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        string path = Path.Join(Sandbox, "orphan");
        File.WriteAllText(path, "x");
        if (!TrySetReparsePoint(path, 0x0000_0123))
        {
            Assert.Skip("This volume would not take a reparse point from this process.");
        }

        using SafeDirHandle root = OpenSandbox();

        CapResult<SafeFileHandle> opened = PortableResolver.OpenFile(
            root, Parse("orphan"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        if (opened.IsSuccess)
        {
            opened.Value!.Dispose();
            Assert.Fail("a reparse point no filter serves was opened.");
        }

        Assert.Equal(CapErrorCategory.NotSupported, opened.Error.Category);

        Assert.True(PlatformOps.Host.StatChild(root, "orphan", out CapNodeInfo info).IsSuccess);
        Assert.Equal(CapNodeType.File, info.Type);

        CapResult<string> link = PlatformOps.Host.ReadChildLink(root, "orphan");
        Assert.False(link.IsSuccess, $"a reparse point no filter serves was read as a link to '{link.Value}'.");
        Assert.Equal(CapErrorCategory.NotALink, link.Error.Category);
    }

    /// <summary>
    /// A symbolic link refused because the process lacks the privilege is reported as a
    /// permission failure, and the empty object made to hold it is gone.
    /// </summary>
    /// <remarks>
    /// The build agents run elevated, so a host that happens to lack the privilege cannot be
    /// relied on. The privilege is taken away instead: the test thread impersonates a copy of
    /// the process token with the privilege removed, which refuses the link on this thread
    /// alone. A host in Developer Mode may still allow the link without it, and then there is
    /// no refusal to observe.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_link_refused_for_want_of_the_privilege_is_a_permission_failure(bool targetIsDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("These are Windows's own resolution rules.");
            return;
        }

        Directory.CreateDirectory(Sandbox);
        CapResult<SafeDirHandle> opened = PlatformOps.Host.OpenAmbientDirectory(Sandbox, CapAccess.Read);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);
        using SafeDirHandle root = opened.Value!;

        CapError error;
        using (WithoutSymbolicLinkPrivilege())
        {
            error = PlatformOps.Host.CreateChildSymbolicLink(root, "link", "target", targetIsDirectory);
        }

        if (error.IsSuccess)
        {
            Assert.Skip("This host lets the process create symbolic links without the privilege.");
        }

        Assert.Equal(CapErrorCategory.PermissionDenied, error.Category);
        Assert.Equal(CapErrorSource.Win32, error.Source);
        Assert.Equal(Win32Errors.ERROR_PRIVILEGE_NOT_HELD, error.RawCode);
        Assert.False(Path.Exists(Path.Join(Sandbox, "link")), "the stub made to hold the link was left behind.");
    }

    /// <summary>
    /// Makes the calling thread act under a copy of the process token that does not hold the
    /// privilege to create symbolic links, until the result is disposed.
    /// </summary>
    /// <remarks>
    /// Removed rather than disabled, so that nothing the system does on the thread's behalf
    /// can enable it again. The rest of the process keeps its own token throughout.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static Impersonation WithoutSymbolicLinkPrivilege()
    {
        const uint TokenDuplicate = 0x0002;
        const uint TokenImpersonate = 0x0004;
        const uint TokenQuery = 0x0008;
        const uint TokenAdjustPrivileges = 0x0020;
        const int SecurityImpersonation = 2;
        const int TokenImpersonation = 2;
        const uint PrivilegeRemoved = 0x00000004;
        const int NotAllAssigned = 1300;

        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate, out nint process))
        {
            throw new System.ComponentModel.Win32Exception();
        }

        nint copy = 0;
        try
        {
            if (!DuplicateTokenEx(
                    process,
                    TokenImpersonate | TokenQuery | TokenAdjustPrivileges,
                    0,
                    SecurityImpersonation,
                    TokenImpersonation,
                    out copy) ||
                !LookupPrivilegeValue(null, "SeCreateSymbolicLinkPrivilege", out Luid privilege))
            {
                throw new System.ComponentModel.Win32Exception();
            }

            TokenPrivilege removal = new() { Count = 1, Privilege = privilege, Attributes = PrivilegeRemoved };
            if (!AdjustTokenPrivileges(copy, false, removal, 0, 0, 0))
            {
                throw new System.ComponentModel.Win32Exception();
            }

            // Reported when the token never held the privilege, which is the state wanted.
            int adjusted = Marshal.GetLastPInvokeError();
            if (adjusted is not 0 and not NotAllAssigned)
            {
                throw new System.ComponentModel.Win32Exception(adjusted);
            }

            if (!SetThreadToken(0, copy))
            {
                throw new System.ComponentModel.Win32Exception();
            }

            return new Impersonation();
        }
        finally
        {
            if (copy != 0)
            {
                _ = CloseHandle(copy);
            }

            _ = CloseHandle(process);
        }
    }

    /// <summary>Returns the thread to the process token when disposed.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class Impersonation : IDisposable
    {
        public void Dispose()
        {
            if (!RevertToSelf())
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary><c>TOKEN_PRIVILEGES</c> with room for the one entry it is used with.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege
    {
        public uint Count;
        public Luid Privilege;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(
        nint existing,
        uint access,
        nint attributes,
        int impersonationLevel,
        int tokenType,
        out nint duplicate);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        in TokenPrivilege newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadToken(nint thread, nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    /// <summary>The tag the Windows Overlay Filter puts on a file it compressed.</summary>
    private const uint OverlayFilterTag = 0x80000017;

    private string Sandbox => Path.Join(_root, "sandbox");

    private static byte[] ReadAll(SafeFileHandle file)
    {
        byte[] buffer = new byte[RandomAccess.GetLength(file)];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(file, buffer.AsSpan(total), total);
            Assert.NotEqual(0, read);
            total += read;
        }

        return buffer;
    }

    /// <summary>
    /// Compresses a file with the Windows Overlay Filter, leaving it uncompressed where the
    /// volume or the edition will not.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void RunCompact(string path)
    {
        using Process? process = Process.Start(new ProcessStartInfo("compact.exe")
        {
            Arguments = $"/c /exe:xpress4k \"{path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(process);

        _ = ChildProcessWait.Finish(process, "compact", ToolTimeout);
    }

    /// <summary>
    /// Gives a file a reparse point with a tag outside Microsoft's range, reporting whether
    /// the volume allowed it rather than throwing.
    /// </summary>
    /// <remarks>
    /// A tag outside Microsoft's range carries a GUID naming its owner ahead of its data. The
    /// data is four bytes of nothing: no filter will ever read it.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static bool TrySetReparsePoint(string path, uint tag)
    {
        const uint SetReparsePoint = 0x000900A4;
        const int DataLength = 4;

        byte[] buffer = new byte[24 + DataLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, tag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), DataLength);
        new Guid("6f1c8f1e-3c1e-4b8e-9d55-5a0f8a3e2b71").TryWriteBytes(buffer.AsSpan(8));

        using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        return DeviceIoControl(file, SetReparsePoint, buffer, (uint)buffer.Length, 0, 0, out _, 0);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint code,
        byte[] input,
        uint inputLength,
        nint output,
        uint outputLength,
        out uint returned,
        nint overlapped);

    /// <summary>A directory outside the sandbox, for a refusal to have somewhere to aim at.</summary>
    private void BuildOutsideTarget()
    {
        Directory.CreateDirectory(Path.Join(Sandbox, "inside"));
        Directory.CreateDirectory(Path.Join(_root, "outside", "inner"));
        File.WriteAllText(Path.Join(_root, "outside", "secret"), "x");
    }

    private SafeDirHandle OpenSandbox()
    {
        Directory.CreateDirectory(Sandbox);
        CapResult<SafeDirHandle> root = PlatformOps.Host.OpenAmbientDirectory(Sandbox, CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);
        return root.Value!;
    }

    private static CapNodeInfo Identify(SafeDirHandle root, string path)
    {
        CapResult<SafeDirHandle> opened = PortableResolver.OpenDirectory(
            root, Parse(path), CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(opened.IsSuccess, opened.Error.FailureDescription);

        using SafeDirHandle handle = opened.Value!;
        CapError error = PlatformOps.Host.StatHandle(handle, out CapNodeInfo info);
        Assert.True(error.IsSuccess, error.FailureDescription);
        return info;
    }

    private static void AssertFails(CapErrorCategory expected, SafeDirHandle root, string path)
    {
        CapResult<SafeDirHandle> result =
            PortableResolver.OpenDirectory(root, Parse(path), CapAccess.Read, ConfinedResolveOptions.None);

        Assert.False(result.IsSuccess, $"'{path}' resolved when it should not have.");
        Assert.Equal(expected, result.Error.Category);
    }

    private static void AssertAmbientFails(string path)
    {
        CapResult<SafeDirHandle> result = PlatformOps.Host.OpenAmbientDirectory(path, CapAccess.Read);
        if (result.IsSuccess)
        {
            result.Value!.Dispose();
            Assert.Fail($"'{path}' was accepted as a sandbox root.");
        }
    }

    private static CapPath Parse(string raw)
    {
        Assert.True(
            CapPath.TryParse(raw, CapPath.HostSyntax, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError error),
            $"'{raw}' did not parse: {error}");
        return path;
    }

    /// <summary>
    /// Creates a junction, which unlike a symbolic link needs no privilege.
    /// </summary>
    /// <remarks>
    /// Through the shell because the framework has no API for one. The paths are quoted rather
    /// than passed as separate arguments: the shell re-parses its own command line, and a
    /// temporary directory can contain a space.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void CreateJunction(string link, string target)
    {
        using Process? process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(process);

        (string output, string errors) = ChildProcessWait.Finish(process, "mklink", ToolTimeout);

        Assert.True(Directory.Exists(link), $"could not create a junction at '{link}': {errors}{output}");
    }

    /// <summary>
    /// Creates a directory symbolic link, reporting whether the host allowed it rather than
    /// throwing.
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a file symbolic link, reporting whether the host allowed it rather than
    /// throwing.
    /// </summary>
    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The short name the volume generated for a path's last component, or
    /// <see langword="null"/> when it has none.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ShortNameOf(string path)
    {
        char[] buffer = new char[1024];
        uint written = GetShortPathName(path, buffer, (uint)buffer.Length);
        if (written == 0 || written >= buffer.Length)
        {
            return null;
        }

        string shortPath = new(buffer, 0, (int)written);
        int separator = shortPath.LastIndexOf('\\');
        return separator < 0 ? shortPath : shortPath[(separator + 1)..];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string longPath, [Out] char[] shortPath, uint bufferLength);
}
