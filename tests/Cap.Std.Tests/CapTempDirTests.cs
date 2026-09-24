using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Scratch directories against a real filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Two properties carry the weight here, and both are about what happens around the
/// directory rather than inside it.
/// </para>
/// <para>
/// The first is that the directory is claimed and never merely found: the name is drawn from
/// a space nobody can search, and the creation either takes it or fails. The tests can only
/// show the name's shape and that two draws never agree, which is the observable half of it;
/// the other half is that no code path here looks a name up before creating it, and that is
/// asserted by the audit over the library's own source.
/// </para>
/// <para>
/// The second is that the cleanup stays inside the directory. A removal that rebuilt paths
/// would follow a symbolic link planted in the tree and delete whatever it pointed at, using
/// the caller's own privileges, so the case of a link aimed at a file outside is the one that
/// matters most and is tested directly.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class CapTempDirTests
{
    /// <summary>The path of a scratch directory, as the host sees it.</summary>
    /// <remarks>
    /// The tests look at the tree from outside, the way anything auditing it would, so they
    /// need the path the handle deliberately does not volunteer. Asking for it is itself an
    /// ambient act, which is why the token is required and why this is a helper rather than
    /// something the type offers.
    /// </remarks>
    private static string HostPath(Dir dir)
    {
        Assert.True(dir.TryGetPath(AmbientAuthority.Acquire(), out string? path));
        return path!;
    }

    /// <summary>A new scratch directory is there, and it is empty.</summary>
    [Fact]
    public void A_new_scratch_directory_is_created_empty()
    {
        using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());

        string path = HostPath(temp.Directory);

        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(path));
        Assert.Empty(temp.Directory.EnumerateEntries());
    }

    /// <summary>The directory is under the system's temporary location, by the name given.</summary>
    [Fact]
    public void The_directory_sits_beneath_the_system_temporary_location_under_its_own_name()
    {
        using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());

        string path = HostPath(temp.Directory);

        Assert.Equal(temp.Name, Path.GetFileName(path));
    }

    /// <summary>Names are long, lower-case and drawn from a restricted alphabet.</summary>
    /// <remarks>
    /// The shape is asserted rather than the entropy, which no test can observe. Its point is
    /// the width: a name this long in this alphabet cannot be arrived at by guessing, and
    /// one case throughout means two names cannot collide on a filesystem that ignores case.
    /// </remarks>
    [Fact]
    public void A_name_is_wide_and_spelled_in_one_case()
    {
        using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());

        Assert.StartsWith("cap-", temp.Name, StringComparison.Ordinal);
        Assert.Equal(28, temp.Name.Length);
        Assert.All(temp.Name["cap-".Length..], character =>
            Assert.True(character is >= 'a' and <= 'z' or >= '2' and <= '9', $"unexpected character {character}"));
    }

    /// <summary>Two scratch directories never share a name.</summary>
    [Fact]
    public void Names_are_not_repeated()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < 32; i++)
        {
            using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            Assert.True(seen.Add(temp.Name));
        }
    }

    /// <summary>Nobody else on the machine can look inside.</summary>
    /// <remarks>
    /// The directory usually lands somewhere every account can write to, so what keeps its
    /// contents private is its own permissions. Unix records those per object; Windows
    /// decides access from a security descriptor inherited from a temporary location that is
    /// already per-account, so there is nothing of this kind to assert there.
    /// </remarks>
    [Fact]
    public void A_scratch_directory_is_closed_to_other_accounts()
    {
        using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());

        if (!temp.Directory.GetMetadata().Permissions.TryGetUnixMode(out UnixFileMode mode))
        {
            Assert.Skip("This platform records no mode bits, and decides access another way.");
            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    /// <summary>Disposal removes the directory.</summary>
    [Fact]
    public void Disposal_removes_the_directory()
    {
        CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
        string path = HostPath(temp.Directory);

        temp.Dispose();

        Assert.False(Directory.Exists(path));
    }

    /// <summary>Disposal removes what is inside it, at every depth.</summary>
    [Fact]
    public void Disposal_removes_a_populated_tree()
    {
        CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
        string path = HostPath(temp.Directory);

        temp.Directory.WriteAllBytes("top", "contents"u8);
        using (Dir nested = temp.Directory.CreateDir("nested"))
        {
            nested.WriteAllBytes("inner", "contents"u8);
            using Dir deeper = nested.CreateDir("deeper");
            deeper.WriteAllBytes("deepest", "contents"u8);
        }

        temp.Dispose();

        Assert.False(Directory.Exists(path));
    }

    /// <summary>A link in the tree is unlinked; what it points at is untouched.</summary>
    /// <remarks>
    /// The case a path-rebuilding cleanup gets wrong, and the reason this type's disposal
    /// descends by handle. The file outside is not writable through anything the scratch
    /// directory holds, so its survival is the whole assertion.
    /// </remarks>
    [Fact]
    public void Disposal_removes_a_link_without_reaching_what_it_points_at()
    {
        string outside = Directory.CreateTempSubdirectory("cap-tempdir-victim-").FullName;
        try
        {
            string victim = Path.Combine(outside, "victim");
            File.WriteAllText(victim, "must survive");

            CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            string path = HostPath(temp.Directory);
            File.CreateSymbolicLink(Path.Join(path, "escape"), victim);
            Directory.CreateSymbolicLink(Path.Join(path, "escape-dir"), outside);

            temp.Dispose();

            Assert.False(Directory.Exists(path));
            Assert.True(File.Exists(victim));
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>Keeping a directory leaves it, and its contents, on the disk.</summary>
    [Fact]
    public void Keeping_a_directory_leaves_it_behind()
    {
        CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
        string path = HostPath(temp.Directory);
        temp.Directory.WriteAllBytes("evidence", "contents"u8);

        temp.Keep();
        temp.Dispose();

        try
        {
            Assert.True(File.Exists(Path.Combine(path, "evidence")));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>The switch keeps every directory in the process, without anybody asking per directory.</summary>
    [Fact]
    public void The_persistence_switch_keeps_directories()
    {
        AppContext.SetSwitch(CapTempDir.PersistSwitchName, true);
        string path;

        try
        {
            CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
            path = HostPath(temp.Directory);
            temp.Dispose();

            Assert.True(Directory.Exists(path));
        }
        finally
        {
            AppContext.SetSwitch(CapTempDir.PersistSwitchName, false);
        }

        Directory.Delete(path, recursive: true);

        // With the switch off again the next directory is removed as usual, which is what
        // makes the switch a switch rather than a change of behaviour for the rest of the run.
        CapTempDir after = CapTempDir.New(AmbientAuthority.Acquire());
        string second = HostPath(after.Directory);
        after.Dispose();

        Assert.False(Directory.Exists(second));
    }

    /// <summary>A scratch directory can be made inside a handle, with no ambient authority.</summary>
    [Fact]
    public void A_scratch_directory_can_be_made_inside_a_handle()
    {
        using CapTempDir enclosing = CapTempDir.New(AmbientAuthority.Acquire());
        enclosing.Directory.WriteAllBytes("sibling", "contents"u8);

        string path;
        using (CapTempDir inner = CapTempDir.NewIn(enclosing.Directory))
        {
            path = HostPath(inner.Directory);
            inner.Directory.WriteAllBytes("inner", "contents"u8);
            Assert.True(enclosing.Directory.Exists(inner.Name));
        }

        Assert.False(Directory.Exists(path));
        Assert.True(enclosing.Directory.Exists("sibling"));
    }

    /// <summary>Closing the handle it was made in does not stop it cleaning up.</summary>
    /// <remarks>
    /// The scratch directory holds a handle of its own on the directory it lives in, so the
    /// caller's handle is theirs to close whenever they like. Without that, a scratch
    /// directory would silently depend on the lifetime of something it was only handed once.
    /// </remarks>
    [Fact]
    public void Closing_the_enclosing_handle_does_not_stop_the_cleanup()
    {
        using CapTempDir enclosing = CapTempDir.New(AmbientAuthority.Acquire());

        Dir handed = enclosing.Directory.Clone();
        CapTempDir inner = CapTempDir.NewIn(handed);
        string path = HostPath(inner.Directory);
        handed.Dispose();

        inner.Dispose();

        Assert.False(Directory.Exists(path));
        Assert.False(enclosing.Directory.Exists(inner.Name));
    }

    /// <summary>Disposing twice does nothing the second time.</summary>
    [Fact]
    public void Disposing_twice_is_harmless()
    {
        CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
        string path = HostPath(temp.Directory);

        temp.Dispose();
        temp.Dispose();

        Assert.False(Directory.Exists(path));
    }

    /// <summary>The handle it hands out is confined to the directory, like any other.</summary>
    [Fact]
    public void The_handle_cannot_reach_above_the_scratch_directory()
    {
        using CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());

        _ = Assert.Throws<SandboxEscapeException>(() => temp.Directory.OpenDir(".."));
    }

    /// <summary>Disposal of a directory something else already removed is quiet.</summary>
    /// <remarks>
    /// Cleanup runs from disposal, including the disposal that happens while an exception is
    /// on its way out. Something thrown from here would replace that exception with one about
    /// tidying up, so a directory that has gone — removed by the code being tested, by
    /// another program, or by whatever sweeps the temporary location — has to be an ordinary
    /// outcome and not a fault.
    /// </remarks>
    [Fact]
    public void Disposal_is_quiet_when_the_directory_has_already_gone()
    {
        CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire());
        string path = HostPath(temp.Directory);
        temp.Directory.WriteAllBytes("contents", "contents"u8);

        Directory.Delete(path, recursive: true);

        temp.Dispose();

        Assert.False(Directory.Exists(path));
    }

    /// <summary>A default token is refused, as everywhere else that demands one.</summary>
    [Fact]
    public void A_token_that_was_never_acquired_is_refused()
    {
        _ = Assert.Throws<ArgumentException>(() => CapTempDir.New(default));
    }
}
