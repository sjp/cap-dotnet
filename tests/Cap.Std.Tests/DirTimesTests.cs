using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Setting when something was last read and last written, through an open handle or by name.
/// </summary>
/// <remarks>
/// <para>
/// The property that matters most is the one setting by name shares with describing by name:
/// a symbolic link at the last component has its own times changed, and what it points at is
/// never reached. Changing a time is a write, however small, and a write that followed a
/// planted link would land on whatever the link chose.
/// </para>
/// <para>
/// The other is that a handle opened only to read cannot change anything about the file, its
/// times included, on any platform, even where the system itself would let the file's owner
/// do so.
/// </para>
/// <para>
/// The instants used are chosen to be unmistakable: whole years away from the moment the test
/// runs, and with a fraction of a second that exercises the full precision the library
/// carries.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirTimesTests : IDisposable
{
    private static readonly DateTimeOffset Written = new DateTimeOffset(2003, 4, 5, 6, 7, 8, TimeSpan.Zero).AddTicks(1234567);
    private static readonly DateTimeOffset Accessed = new DateTimeOffset(2004, 5, 6, 7, 8, 9, TimeSpan.Zero).AddTicks(7654321);

    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    // --- through an open file ------------------------------------------------------------------

    /// <summary>Both times given are stored, to the precision the library carries.</summary>
    [Fact]
    public void An_open_file_is_given_both_times()
    {
        File.WriteAllText(Host("file"), "content");

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("file", FileMode.Open, FileAccess.ReadWrite))
        {
            file.SetTimes(CapFileTime.At(Accessed), CapFileTime.At(Written));

            CapMetadata metadata = file.GetMetadata();
            Assert.Equal(Written, metadata.LastWriteTime);
            Assert.Equal(Accessed, metadata.LastAccessTime);
        }

        Assert.Equal(Written, root.GetMetadata("file").LastWriteTime);
    }

    /// <summary>A time left out is left as it was.</summary>
    [Fact]
    public void A_time_left_out_is_left_alone()
    {
        File.WriteAllText(Host("file"), "content");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("file", FileMode.Open, FileAccess.Write);
        file.SetTimes(CapFileTime.At(Accessed), CapFileTime.At(Written));

        file.SetTimes(lastWrite: CapFileTime.At(Written.AddDays(1)));

        CapMetadata metadata = file.GetMetadata();
        Assert.Equal(Written.AddDays(1), metadata.LastWriteTime);
        Assert.Equal(Accessed, metadata.LastAccessTime);
    }

    /// <summary>
    /// Asking for the time of the change gives a time the system took, not one the caller
    /// passed in.
    /// </summary>
    [Fact]
    public void Now_is_the_time_the_change_is_recorded()
    {
        File.WriteAllText(Host("file"), "content");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("file", FileMode.Open, FileAccess.Write);
        file.SetTimes(CapFileTime.At(Accessed), CapFileTime.At(Written));

        DateTimeOffset before = DateTimeOffset.UtcNow.AddMinutes(-5);
        file.SetTimes(lastWrite: CapFileTime.Now);
        DateTimeOffset after = DateTimeOffset.UtcNow.AddMinutes(5);

        CapMetadata metadata = file.GetMetadata();
        Assert.InRange(metadata.LastWriteTime, before, after);
        Assert.Equal(Accessed, metadata.LastAccessTime);
    }

    /// <summary>
    /// An instant before 1970 survives the trip, which is the case where the seconds are
    /// negative and the fraction is not.
    /// </summary>
    [Fact]
    public void An_instant_before_the_Unix_epoch_is_stored_exactly()
    {
        DateTimeOffset early = new DateTimeOffset(1969, 7, 20, 20, 17, 40, TimeSpan.Zero).AddTicks(1234567);
        File.WriteAllText(Host("file"), "content");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("file", FileMode.Open, FileAccess.Write);
        file.SetTimes(lastWrite: CapFileTime.At(early));

        Assert.Equal(early, file.GetMetadata().LastWriteTime);
    }

    /// <summary>
    /// The offset an instant is written with does not matter: the filesystem records the
    /// instant.
    /// </summary>
    [Fact]
    public void An_instant_is_stored_whatever_offset_it_is_written_with()
    {
        File.WriteAllText(Host("file"), "content");

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("file", FileMode.Open, FileAccess.Write);
        file.SetTimes(lastWrite: CapFileTime.At(Written.ToOffset(TimeSpan.FromHours(-7))));

        Assert.Equal(Written, file.GetMetadata().LastWriteTime);
    }

    /// <summary>
    /// A handle opened only to read cannot change the file's times, and the refusal changes
    /// nothing.
    /// </summary>
    [Fact]
    public void A_handle_that_can_only_read_cannot_set_times()
    {
        File.WriteAllText(Host("file"), "content");
        File.SetLastWriteTimeUtc(Host("file"), Written.UtcDateTime);

        using Dir root = OpenRoot();
        using CapFile file = root.OpenFile("file");

        Assert.Throws<UnauthorizedAccessException>(() => file.SetTimes(lastWrite: CapFileTime.Now));
        Assert.Equal(Written, file.GetMetadata().LastWriteTime);
    }

    /// <summary>A closed handle refuses, rather than acting on whatever took its number.</summary>
    [Fact]
    public void A_closed_file_refuses()
    {
        File.WriteAllText(Host("file"), "content");

        using Dir root = OpenRoot();
        CapFile file = root.OpenFile("file", FileMode.Open, FileAccess.Write);
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.SetTimes(lastWrite: CapFileTime.Now));
    }

    /// <summary>
    /// A time the platform cannot store at all is refused as an argument, before anything
    /// is changed. Windows counts from 1601, and cannot express an instant before that.
    /// </summary>
    [Fact]
    public void An_instant_the_platform_cannot_store_is_refused_as_an_argument()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Every instant a DateTimeOffset holds can be expressed here.");
            return;
        }

        File.WriteAllText(Host("file"), "content");
        File.SetLastWriteTimeUtc(Host("file"), Written.UtcDateTime);
        DateTimeOffset tooEarly = new(1500, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using Dir root = OpenRoot();
        using (CapFile file = root.OpenFile("file", FileMode.Open, FileAccess.Write))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => file.SetTimes(lastWrite: CapFileTime.At(tooEarly)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => root.SetTimes("file", lastWrite: CapFileTime.At(tooEarly)));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.TrySetTimes("file", lastWrite: CapFileTime.At(tooEarly)));
        Assert.Equal(Written, root.GetMetadata("file").LastWriteTime);
    }

    // --- through an open directory -------------------------------------------------------------

    /// <summary>
    /// A directory handle sets the directory it refers to, including one opened only to
    /// resolve names beneath it.
    /// </summary>
    [Fact]
    public void A_directory_handle_sets_the_directory_it_refers_to()
    {
        Directory.CreateDirectory(Host("branch"));

        using Dir root = OpenRoot();
        using Dir branch = root.OpenDir("branch");
        branch.SetTimes(CapFileTime.At(Accessed), CapFileTime.At(Written));

        CapMetadata metadata = root.GetMetadata("branch");
        Assert.Equal(Written, metadata.LastWriteTime);
        Assert.Equal(Accessed, metadata.LastAccessTime);
    }

    /// <summary>The handle a caller opened with the ambient token can be set too.</summary>
    [Fact]
    public void A_root_handle_sets_its_own_directory()
    {
        using Dir root = OpenRoot();
        root.SetTimes(lastWrite: CapFileTime.At(Written));

        Assert.Equal(Written, root.GetMetadata().LastWriteTime);
    }

    // --- by name -------------------------------------------------------------------------------

    /// <summary>A name holding a file has that file's times set.</summary>
    [Fact]
    public void A_name_holding_a_file_is_set()
    {
        Directory.CreateDirectory(Host("a", "b"));
        File.WriteAllText(Host("a", "b", "leaf"), "content");

        using Dir root = OpenRoot();
        root.SetTimes("a/b/leaf", CapFileTime.At(Accessed), CapFileTime.At(Written));

        CapMetadata metadata = root.GetMetadata("a/b/leaf");
        Assert.Equal(Written, metadata.LastWriteTime);
        Assert.Equal(Accessed, metadata.LastAccessTime);
    }

    /// <summary>
    /// A name holding a directory is set, including when it is spelled with a trailing
    /// separator.
    /// </summary>
    [Fact]
    public void A_name_holding_a_directory_is_set()
    {
        Directory.CreateDirectory(Host("branch"));

        using Dir root = OpenRoot();
        root.SetTimes("branch/", lastWrite: CapFileTime.At(Written));

        Assert.Equal(Written, root.GetMetadata("branch").LastWriteTime);
    }

    /// <summary>A path spelled as a directory is refused when a file holds the name.</summary>
    [Fact]
    public void A_file_spelled_as_a_directory_is_refused_and_left_alone()
    {
        File.WriteAllText(Host("file"), "content");
        File.SetLastWriteTimeUtc(Host("file"), Written.UtcDateTime);

        using Dir root = OpenRoot();

        CapIOException refused = Assert.Throws<CapIOException>(
            () => root.SetTimes("file/", lastWrite: CapFileTime.Now));
        Assert.Equal(CapErrorKind.NotADirectory, refused.Kind);
        Assert.Equal(Written, root.GetMetadata("file").LastWriteTime);
    }

    /// <summary>A path ending in <c>..</c> sets the directory it climbs back to.</summary>
    [Fact]
    public void A_path_ending_in_a_parent_step_sets_the_directory_it_names()
    {
        Directory.CreateDirectory(Host("outer", "inner"));

        using Dir root = OpenRoot();
        root.SetTimes("outer/inner/..", lastWrite: CapFileTime.At(Written));

        Assert.Equal(Written, root.GetMetadata("outer").LastWriteTime);
    }

    /// <summary>A name that is not there is reported as missing, or as false.</summary>
    [Fact]
    public void A_missing_name_is_reported()
    {
        using Dir root = OpenRoot();

        Assert.Throws<FileNotFoundException>(() => root.SetTimes("absent", lastWrite: CapFileTime.Now));
        Assert.False(root.TrySetTimes("absent", lastWrite: CapFileTime.Now));
    }

    /// <summary>The reporting form answers true when it worked.</summary>
    [Fact]
    public void The_reporting_form_answers_true_when_it_worked()
    {
        File.WriteAllText(Host("file"), "content");

        using Dir root = OpenRoot();

        Assert.True(root.TrySetTimes("file", lastWrite: CapFileTime.At(Written)));
        Assert.Equal(Written, root.GetMetadata("file").LastWriteTime);
    }

    /// <summary>A path that climbs out is refused as an escape, and reaches nothing.</summary>
    [Fact]
    public void A_path_that_climbs_out_is_an_escape()
    {
        Directory.CreateDirectory(Host("inside"));

        using Dir root = OpenRoot();
        using Dir inside = root.OpenDir("inside");

        Assert.Throws<SandboxEscapeException>(() => inside.SetTimes("../inside", lastWrite: CapFileTime.Now));
        Assert.False(inside.TrySetTimes("../inside", lastWrite: CapFileTime.Now));
    }

    /// <summary>A null path is a mistake in the calling code, reported as one by both forms.</summary>
    [Fact]
    public void A_null_path_is_refused()
    {
        using Dir root = OpenRoot();

        Assert.Throws<ArgumentNullException>(() => root.SetTimes(null!, lastWrite: CapFileTime.Now));
        Assert.Throws<ArgumentNullException>(() => root.TrySetTimes(null!, lastWrite: CapFileTime.Now));
    }

    // --- the name, never what the name leads to ------------------------------------------------

    /// <summary>
    /// A link at the last component has its own times set, and its target's are not touched,
    /// under either policy.
    /// </summary>
    [Theory]
    [InlineData(SymlinkPolicy.FollowWithinSandbox)]
    [InlineData(SymlinkPolicy.Deny)]
    public void A_link_has_its_own_times_set_and_its_target_is_not_reached(SymlinkPolicy policy)
    {
        RequireSymbolicLinks();

        File.WriteAllText(Host("target"), "content");
        File.SetLastWriteTimeUtc(Host("target"), Accessed.UtcDateTime);
        File.CreateSymbolicLink(Host("link"), "target");

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire(), policy);
        root.SetTimes("link", lastWrite: CapFileTime.At(Written));

        Assert.Equal(Written, root.GetMetadata("link").LastWriteTime);
        Assert.Equal(Accessed, root.GetMetadata("target").LastWriteTime);
    }

    /// <summary>
    /// A link whose target lies outside the handle is set as a link, and what it names
    /// outside is not reached.
    /// </summary>
    [Fact]
    public void A_link_to_something_outside_is_set_as_a_link()
    {
        RequireSymbolicLinks();

        using ScratchTree outside = new();
        string victim = Path.Combine(outside.HostPath, "victim");
        File.WriteAllText(victim, "outside");
        File.SetLastWriteTimeUtc(victim, Accessed.UtcDateTime);
        File.CreateSymbolicLink(Host("link"), victim);

        using Dir root = OpenRoot();
        root.SetTimes("link", lastWrite: CapFileTime.At(Written));

        Assert.Equal(Written, root.GetMetadata("link").LastWriteTime);
        Assert.Equal(Accessed.UtcDateTime, File.GetLastWriteTimeUtc(victim));
    }

    /// <summary>A link on the way to the name is still subject to the handle's policy.</summary>
    [Fact]
    public void A_link_in_the_middle_of_the_path_obeys_the_policy()
    {
        RequireSymbolicLinks();

        Directory.CreateDirectory(Host("actual"));
        File.WriteAllText(Host("actual", "leaf"), "x");
        Directory.CreateSymbolicLink(Host("hop"), "actual");

        using Dir permissive = OpenRoot();
        using Dir strict = permissive.Restrict(SymlinkPolicy.Deny);

        permissive.SetTimes("hop/leaf", lastWrite: CapFileTime.At(Written));
        Assert.Equal(Written, permissive.GetMetadata("actual/leaf").LastWriteTime);
        Assert.ThrowsAny<IOException>(() => strict.SetTimes("hop/leaf", lastWrite: CapFileTime.Now));
    }

    // --- helpers -------------------------------------------------------------------------------

    private void RequireSymbolicLinks()
    {
        string probe = Host("link-probe");

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
}
