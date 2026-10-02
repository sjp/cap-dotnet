using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Std;

namespace Cap.Net;

/// <summary>
/// Turns a directory handle and a path into an address, refusing anything the handle does
/// not cover.
/// </summary>
/// <remarks>
/// <para>
/// A socket in the Unix domain is a filesystem object, so the authority to reach one is a
/// directory handle and not a pool — which makes this the place the two capability systems
/// meet. The whole of the containment decision is the directory handle's: the path is
/// resolved beneath it exactly as any other path would be, under the symbolic-link policy
/// that handle carries, and what comes back is a directory the resolution confined and a
/// single name that has never been looked up.
/// </para>
/// <para>
/// The remaining step is the one a socket call would otherwise do for itself, and doing it
/// here is the point: a socket call resolves the address it is given with the whole process's
/// authority and follows links on the way. The platform layer turns the confined pair into an
/// address that makes the kernel look up nothing that has not already been decided.
/// </para>
/// </remarks>
internal static class UnixSocketReach
{
    /// <summary>Whether this platform can reach a socket through a directory handle at all.</summary>
    public static bool IsSupported => UnixSocketNaming.IsSupported;

    /// <summary>Names an existing socket beneath <paramref name="dir"/>.</summary>
    public static UnixSocketName ToConnect(Dir dir, string path)
    {
        UnixSocketName named = Reach(dir, path, creating: false);
        if (named.Kind == CapFileType.Socket)
        {
            return named;
        }

        named.Dispose();

        // A link is called out separately because it is the interesting case: the name was
        // not followed, so nothing escaped, and saying "that is not a socket" about it would
        // hide why the caller did not get what they expected.
        throw named.Kind == CapFileType.Symlink
            ? new CapIOException(
                CapErrorKind.SymbolicLink,
                $"'{path}' is a symbolic link, and a connection is made to what a name holds " +
                "rather than to what it points at. Following it would resolve the target with " +
                "the whole process's authority, which the directory handle exists to prevent.")
            : new CapIOException($"'{path}' does not name a socket.");
    }

    /// <summary>Names a socket that is about to be created beneath <paramref name="dir"/>.</summary>
    public static UnixSocketName ToBind(Dir dir, string path) => Reach(dir, path, creating: true);

    private static UnixSocketName Reach(Dir dir, string path, bool creating)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "This platform has no way to reach a socket in the Unix domain relative to a " +
                "directory handle. The alternative would be to resolve the path and hand the " +
                "result to a socket call, which resolves it again with the whole process's " +
                "authority and follows links on the way — so the operation is refused rather " +
                "than performed without the guarantee the handle is supposed to carry.");
        }

        CapPathError pathError = dir.OpenNameHolder(
            path, out SafeDirHandle? holder, out string? name, out CapError error);

        // The two answers become different exceptions, and the difference is the one worth
        // keeping: a path that names somewhere outside the handle is a containment refusal,
        // and reporting it as an ordinary failure would lose the only event in this library
        // worth alerting on.
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, nameof(path));
        }

        if (error.IsFailure)
        {
            throw FailureTranslation.ToException(error, path, ExpectedTarget.Name);
        }

        SafeDirHandle directory = holder!;
        string component = name!;

        using (directory)
        {
            int nameBudget = 0;
            CapResult<UnixSocketName> named = creating
                ? UnixSocketNaming.ForBind(directory, component, out nameBudget)
                : UnixSocketNaming.ForConnect(directory, component);

            if (named.IsSuccess)
            {
                return named.Value;
            }

            if (creating && named.Error.Category == CapErrorCategory.NameTooLong)
            {
                throw AddressTooLong(path, component, nameBudget, named.Error);
            }

            throw FailureTranslation.ToException(named.Error, path, ExpectedTarget.Name);
        }
    }

    /// <summary>Explains a name that does not fit the address a bind has to write.</summary>
    /// <remarks>
    /// The same type every other name that is too long is reported as, so a caller sorting
    /// failures by reason does not need to know this one came from a socket. What differs is
    /// the message: the limit is the kernel's socket address rather than the filesystem's,
    /// and it moves with the number of descriptors the process holds, so the figure that
    /// applied is quoted rather than left for the caller to work out.
    /// </remarks>
    private static PathTooLongException AddressTooLong(
        string path, string component, int nameBudget, CapError error) =>
        new(
            $"'{path}' cannot be bound: its last component is {PathEncoding.GetByteCount(component)} " +
            $"bytes and the socket address had room for {nameBudget}. The address is written " +
            "as /proc/self/fd/N/<name> and the kernel holds 107 bytes of it, so the room left " +
            "for the name shrinks as the process holds more descriptors. Only the last component " +
            $"counts against it; the directories above it do not. ({error})");
}
