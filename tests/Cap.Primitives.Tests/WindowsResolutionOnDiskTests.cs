using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cap.Primitives.Interop;

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
    private readonly string _root = Directory.CreateTempSubdirectory("cap-win-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

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

    private string Sandbox => Path.Join(_root, "sandbox");

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

        // Both streams drained before waiting: a process whose output fills the pipe while
        // nobody is reading it never exits.
        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

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
