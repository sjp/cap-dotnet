namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Where this system puts scratch files, answered the way every Unix program answers it.
/// </summary>
/// <remarks>
/// <para>
/// Shared by both Unix backends because the convention is a Unix one rather than a kernel
/// one: an environment variable naming a directory, and a well-known directory when it says
/// nothing. Answering differently from the rest of the system would put our scratch files
/// somewhere the machine's own cleanup, quotas and disk sizing do not expect them.
/// </para>
/// <para>
/// The variable is read at each call rather than cached. It is part of the process
/// environment, a caller may change it deliberately between one scratch directory and the
/// next, and reading it costs nothing next to the open that follows.
/// </para>
/// </remarks>
internal static class UnixTemporaryDirectory
{
    /// <summary>The variable that overrides where scratch files go.</summary>
    private const string Variable = "TMPDIR";

    /// <summary>
    /// The directory to create scratch files in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whatever the environment names, or the shared directory every Unix has otherwise.
    /// That shared directory is world-writable, which is exactly why the things this library
    /// puts there are created exclusively, opened once, and thereafter reached only through
    /// the handle: an attacker who can create names alongside ours can never make one of
    /// ours refer to something else once it is open.
    /// </para>
    /// <para>
    /// A variable set to an empty value is treated as unset, which is what the shell's own
    /// convention makes it mean, and is the reading that avoids handing an empty path to an
    /// open.
    /// </para>
    /// </remarks>
    public static string Location
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(Variable);
            return string.IsNullOrEmpty(configured) ? Fallback : configured;
        }
    }

    /// <summary>
    /// The directory scratch files go in when the environment names none, which every Unix
    /// has and which is world-writable on all of them.
    /// </summary>
    private const string Fallback = "/tmp";
}
