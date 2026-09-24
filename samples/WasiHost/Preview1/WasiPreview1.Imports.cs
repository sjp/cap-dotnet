using Wasmtime;

namespace WasiHost.Preview1;

public sealed partial class WasiPreview1
{
    /// <summary>One import's body: the arguments as the engine passed them, and the guest's memory.</summary>
    private delegate Errno Body(WasiPreview1 host, GuestMemory memory, ReadOnlySpan<ValueBox> args);

    /// <summary>
    /// Defines every <c>wasi_snapshot_preview1</c> function on a linker, bound to this
    /// instance.
    /// </summary>
    /// <remarks>
    /// The filesystem functions are implemented over the preopened directories. The
    /// functions for sockets, signals and polling, which the filesystem does not need, are
    /// defined so that a module importing them links, and report that they are not supported.
    /// </remarks>
    public void DefineImports(Linker linker)
    {
        ArgumentNullException.ThrowIfNull(linker);

        Define(linker, "args_get", "ii", static (h, m, a) => h.ArgsGet(m, U(a[0]), U(a[1])));
        Define(linker, "args_sizes_get", "ii", static (h, m, a) => h.ArgsSizesGet(m, U(a[0]), U(a[1])));
        Define(linker, "environ_get", "ii", static (h, m, a) => h.EnvironGet(m, U(a[0]), U(a[1])));
        Define(linker, "environ_sizes_get", "ii", static (h, m, a) => h.EnvironSizesGet(m, U(a[0]), U(a[1])));
        Define(linker, "clock_res_get", "ii", static (h, m, a) => h.ClockResGet(m, U(a[0]), U(a[1])));
        Define(linker, "clock_time_get", "iIi", static (h, m, a) => h.ClockTimeGet(m, U(a[0]), U(a[2])));
        Define(linker, "random_get", "ii", static (h, m, a) => h.RandomGet(m, U(a[0]), U(a[1])));
        Define(linker, "sched_yield", string.Empty, static (_, _, _) => Errno.Success);

        Define(linker, "fd_advise", "iIIi", static (h, _, a) => h.FdAdvise(U(a[0]), U(a[3])));
        Define(linker, "fd_allocate", "iII", static (h, _, a) => h.FdAllocate(U(a[0])));
        Define(linker, "fd_close", "i", static (h, _, a) => h.FdClose(U(a[0])));
        Define(linker, "fd_datasync", "i", static (h, _, a) => h.FdSync(U(a[0]), Rights.FdDatasync));
        Define(linker, "fd_sync", "i", static (h, _, a) => h.FdSync(U(a[0]), Rights.FdSync));
        Define(linker, "fd_fdstat_get", "ii", static (h, m, a) => h.FdFdstatGet(m, U(a[0]), U(a[1])));
        Define(linker, "fd_fdstat_set_flags", "ii", static (h, _, a) => h.FdFdstatSetFlags(U(a[0]), (FdFlags)U(a[1])));
        Define(linker, "fd_fdstat_set_rights", "iII", static (h, _, a) =>
            h.FdFdstatSetRights(U(a[0]), (Rights)a[1].AsInt64(), (Rights)a[2].AsInt64()));
        Define(linker, "fd_filestat_get", "ii", static (h, m, a) => h.FdFilestatGet(m, U(a[0]), U(a[1])));
        Define(linker, "fd_filestat_set_size", "iI", static (h, _, a) => h.FdFilestatSetSize(U(a[0]), a[1].AsInt64()));
        Define(linker, "fd_filestat_set_times", "iIIi", static (h, _, a) =>
            h.FdFilestatSetTimes(U(a[0]), (ulong)a[1].AsInt64(), (ulong)a[2].AsInt64(), (FstFlags)U(a[3])));
        Define(linker, "fd_pread", "iiiIi", static (h, m, a) =>
            h.FdRead(m, U(a[0]), U(a[1]), U(a[2]), a[3].AsInt64(), U(a[4])));
        Define(linker, "fd_read", "iiii", static (h, m, a) => h.FdRead(m, U(a[0]), U(a[1]), U(a[2]), null, U(a[3])));
        Define(linker, "fd_pwrite", "iiiIi", static (h, m, a) =>
            h.FdWrite(m, U(a[0]), U(a[1]), U(a[2]), a[3].AsInt64(), U(a[4])));
        Define(linker, "fd_write", "iiii", static (h, m, a) => h.FdWrite(m, U(a[0]), U(a[1]), U(a[2]), null, U(a[3])));
        Define(linker, "fd_prestat_get", "ii", static (h, m, a) => h.FdPrestatGet(m, U(a[0]), U(a[1])));
        Define(linker, "fd_prestat_dir_name", "iii", static (h, m, a) => h.FdPrestatDirName(m, U(a[0]), U(a[1]), U(a[2])));
        Define(linker, "fd_readdir", "iiiIi", static (h, m, a) =>
            h.FdReaddir(m, U(a[0]), U(a[1]), U(a[2]), (ulong)a[3].AsInt64(), U(a[4])));
        Define(linker, "fd_renumber", "ii", static (h, _, a) => h.FdRenumber(U(a[0]), U(a[1])));
        Define(linker, "fd_seek", "iIii", static (h, m, a) => h.FdSeek(m, U(a[0]), a[1].AsInt64(), U(a[2]), U(a[3])));
        Define(linker, "fd_tell", "ii", static (h, m, a) => h.FdTell(m, U(a[0]), U(a[1])));

        Define(linker, "path_create_directory", "iii", static (h, m, a) => h.PathCreateDirectory(m, U(a[0]), U(a[1]), U(a[2])));
        Define(linker, "path_filestat_get", "iiiii", static (h, m, a) =>
            h.PathFilestatGet(m, U(a[0]), (LookupFlags)U(a[1]), U(a[2]), U(a[3]), U(a[4])));
        Define(linker, "path_filestat_set_times", "iiiiIIi", static (h, m, a) =>
            h.PathFilestatSetTimes(
                m, U(a[0]), (LookupFlags)U(a[1]), U(a[2]), U(a[3]),
                (ulong)a[4].AsInt64(), (ulong)a[5].AsInt64(), (FstFlags)U(a[6])));
        Define(linker, "path_link", "iiiiiii", static (h, m, a) =>
            h.PathLink(m, U(a[0]), (LookupFlags)U(a[1]), U(a[2]), U(a[3]), U(a[4]), U(a[5]), U(a[6])));
        Define(linker, "path_open", "iiiiiIIii", static (h, m, a) =>
            h.PathOpen(
                m, U(a[0]), (LookupFlags)U(a[1]), U(a[2]), U(a[3]), (OFlags)U(a[4]),
                (Rights)a[5].AsInt64(), (Rights)a[6].AsInt64(), (FdFlags)U(a[7]), U(a[8])));
        Define(linker, "path_readlink", "iiiiii", static (h, m, a) =>
            h.PathReadlink(m, U(a[0]), U(a[1]), U(a[2]), U(a[3]), U(a[4]), U(a[5])));
        Define(linker, "path_remove_directory", "iii", static (h, m, a) => h.PathRemoveDirectory(m, U(a[0]), U(a[1]), U(a[2])));
        Define(linker, "path_rename", "iiiiii", static (h, m, a) =>
            h.PathRename(m, U(a[0]), U(a[1]), U(a[2]), U(a[3]), U(a[4]), U(a[5])));
        Define(linker, "path_symlink", "iiiii", static (h, m, a) =>
            h.PathSymlink(m, U(a[0]), U(a[1]), U(a[2]), U(a[3]), U(a[4])));
        Define(linker, "path_unlink_file", "iii", static (h, m, a) => h.PathUnlinkFile(m, U(a[0]), U(a[1]), U(a[2])));

        Define(linker, "poll_oneoff", "iiii", static (_, _, _) => Errno.NotSup);
        Define(linker, "proc_raise", "i", static (_, _, _) => Errno.NotSup);
        Define(linker, "sock_accept", "iii", static (h, _, a) => h.NotASocket(U(a[0])));
        Define(linker, "sock_recv", "iiiiii", static (h, _, a) => h.NotASocket(U(a[0])));
        Define(linker, "sock_send", "iiiii", static (h, _, a) => h.NotASocket(U(a[0])));
        Define(linker, "sock_shutdown", "ii", static (h, _, a) => h.NotASocket(U(a[0])));

        linker.DefineFunction(
            ModuleName,
            "proc_exit",
            (Caller _, ReadOnlySpan<ValueBox> args, Span<ValueBox> _) =>
            {
                ExitCode = args[0].AsInt32();
                throw new WasiExitException(ExitCode.Value);
            },
            [ValueKind.Int32],
            []);
    }

    private static uint U(ValueBox value) => (uint)value.AsInt32();

    /// <summary>
    /// Defines one import returning an error code. The signature is one letter per parameter:
    /// <c>i</c> for a 32-bit integer and <c>I</c> for a 64-bit one.
    /// </summary>
    private void Define(Linker linker, string name, string signature, Body body)
    {
        ValueKind[] parameters = [.. signature.Select(c => c == 'I' ? ValueKind.Int64 : ValueKind.Int32)];

        linker.DefineFunction(
            ModuleName,
            name,
            (Caller caller, ReadOnlySpan<ValueBox> args, Span<ValueBox> results) =>
            {
                Memory memory = caller.GetMemory("memory")
                    ?? throw new InvalidOperationException("The guest exports no memory named 'memory'.");
                results[0] = (int)body(this, new GuestMemory(memory), args);
            },
            parameters,
            [ValueKind.Int32]);
    }

    private Errno NotASocket(uint fd) => _descriptors.ContainsKey(fd) ? Errno.NotSock : Errno.BadF;
}
