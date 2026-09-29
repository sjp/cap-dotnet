namespace Cap.Tests;

/// <summary>
/// The directory with a bind mount inside it, prepared before the run by
/// <c>build/ci/prepare-bind-mount.sh</c> and named in <see cref="Variable"/>.
/// </summary>
/// <remarks>
/// Mounting needs a privilege the suite must not hold, so the mount is made by whoever starts
/// the run. The directory holds <c>mnt</c>, onto which another directory has been
/// bind-mounted; that directory holds a file, <c>file</c>, a link <c>climb</c> to
/// <c>../..</c> (out of the directory, from inside the mount) and a link <c>up</c> to
/// <c>..</c> (back to the directory).
/// </remarks>
internal static class BindMountFixture
{
    /// <summary>The environment variable naming the prepared directory.</summary>
    public const string Variable = "CAPDOTNET_TEST_BIND_MOUNT";

    /// <summary>The name, inside the prepared directory, of the mount point.</summary>
    public const string MountName = "mnt";

    /// <summary>
    /// Returns the prepared directory, skipping where none was prepared and failing where the
    /// one named has no mount inside it.
    /// </summary>
    /// <remarks>
    /// A directory whose <c>mnt</c> is not a mount point would have the case test an ordinary
    /// subdirectory and pass for the wrong reason, so it fails rather than skips.
    /// </remarks>
    public static string Require()
    {
        string? prepared = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(prepared))
        {
            Assert.Skip(
                $"No bind mount was prepared for this run. Mounting needs a privilege the suite " +
                $"must not hold, so it is made beforehand and named in {Variable}.");
        }

        string mountPoint = Path.Join(prepared, MountName);
        Assert.True(
            IsMountPoint(mountPoint),
            $"{Variable} names '{prepared}', but '{mountPoint}' is not a mount point. The run " +
            "was set up to test a mount and would otherwise test an ordinary directory.");

        return prepared;
    }

    private static bool IsMountPoint(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        foreach (string line in File.ReadLines("/proc/self/mountinfo"))
        {
            // The fifth field is where the mount sits. Spaces in it are written as octal escapes.
            string[] fields = line.Split(' ');
            if (fields.Length > 4 && fields[4].Replace("\\040", " ", StringComparison.Ordinal) == full)
            {
                return true;
            }
        }

        return false;
    }
}
