using System.Diagnostics.CodeAnalysis;
using Cap.Rand;

namespace WasiHost.Preview1;

/// <summary>
/// A <c>wasi_snapshot_preview1</c> implementation whose filesystem is a set of
/// <see cref="Cap.Std.Dir"/> handles.
/// </summary>
/// <remarks>
/// <para>
/// One instance serves one guest. It holds the guest's descriptor table: numbers 0 to 2 are
/// the standard streams, the preopened directories follow from 3, and every descriptor the
/// guest opens after that is derived from one of them. A guest therefore reaches exactly what
/// the preopens reach, because a <see cref="Cap.Std.Dir"/> can only ever hand out what lies
/// beneath it: the adapter never builds a host path, and never resolves a guest path itself.
/// </para>
/// <para>
/// A preopen is a <see cref="Cap.Std.Dir"/> with nothing added. Every <c>path_*</c> call
/// takes a directory descriptor and a relative path, and so does every method on
/// <see cref="Cap.Std.Dir"/>; the call is passed straight through.
/// </para>
/// <para>
/// Not thread-safe. A preview 1 guest is single-threaded, and so is its host.
/// </para>
/// </remarks>
public sealed partial class WasiPreview1 : IDisposable
{
    /// <summary>The module name preview 1 imports are declared under.</summary>
    public const string ModuleName = "wasi_snapshot_preview1";

    private readonly Dictionary<uint, Descriptor> _descriptors = [];
    private readonly WasiOptions _options;
    private readonly TimeProvider _clock;
    private readonly IRandomSource _random;

    public WasiPreview1(WasiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _clock = options.Clock;
        _random = options.Random;

        _descriptors[0] = new StreamDescriptor(options.StandardInput, Rights.FdRead | Rights.PollFdReadwrite);
        _descriptors[1] = new StreamDescriptor(options.StandardOutput, Rights.FdWrite | Rights.PollFdReadwrite);
        _descriptors[2] = new StreamDescriptor(options.StandardError, Rights.FdWrite | Rights.PollFdReadwrite);

        uint next = 3;
        foreach ((string guestName, Cap.Std.Dir dir) in options.Preopens)
        {
            _descriptors[next++] = new DirectoryDescriptor(dir, Rights.Directory, Rights.Directory | Rights.File, guestName);
        }
    }

    /// <summary>
    /// The code the guest passed to <c>proc_exit</c>, once it has; null while it has not.
    /// </summary>
    public int? ExitCode { get; private set; }

    public void Dispose()
    {
        foreach (Descriptor descriptor in _descriptors.Values)
        {
            if (descriptor is not StreamDescriptor)
            {
                descriptor.Dispose();
            }
        }

        _descriptors.Clear();
    }

    private bool TryGet<T>(uint fd, [NotNullWhen(true)] out T? descriptor)
        where T : Descriptor
    {
        if (_descriptors.TryGetValue(fd, out Descriptor? found) && found is T typed)
        {
            descriptor = typed;
            return true;
        }

        descriptor = null;
        return false;
    }

    /// <summary>Looks up a descriptor that must be a directory, as every <c>path_*</c> call needs.</summary>
    private Errno GetDirectory(uint fd, Rights needed, out DirectoryDescriptor directory)
    {
        directory = null!;
        if (!_descriptors.TryGetValue(fd, out Descriptor? found))
        {
            return Errno.BadF;
        }

        if (found is not DirectoryDescriptor typed)
        {
            return Errno.NotDir;
        }

        if ((typed.RightsBase & needed) != needed)
        {
            return Errno.NotCapable;
        }

        directory = typed;
        return Errno.Success;
    }

    /// <summary>Gives a new descriptor the lowest number not in use.</summary>
    private uint Insert(Descriptor descriptor)
    {
        uint fd = 3;
        while (_descriptors.ContainsKey(fd))
        {
            fd++;
        }

        _descriptors[fd] = descriptor;
        return fd;
    }
}

/// <summary>
/// Thrown through the engine by <c>proc_exit</c>, to unwind the guest without running any
/// more of it.
/// </summary>
public sealed class WasiExitException : Exception
{
    public WasiExitException()
    {
    }

    public WasiExitException(string? message)
        : base(message)
    {
    }

    public WasiExitException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public WasiExitException(int code)
        : base($"The guest exited with code {code}.") => Code = code;

    public int Code { get; }
}
