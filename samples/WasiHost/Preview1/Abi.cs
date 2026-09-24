namespace WasiHost.Preview1;

// The flag types keep the names the ABI gives them, which is what a reader will search for.
#pragma warning disable CA1711

/// <summary>
/// The error codes <c>wasi_snapshot_preview1</c> returns, with the numbering the ABI fixes.
/// </summary>
/// <remarks>
/// Only the codes this adapter produces are named. The ABI numbers every POSIX error; a guest
/// compares against those numbers, so the values here must never be renumbered.
/// </remarks>
public enum Errno : ushort
{
    Success = 0,
    Access = 2,
    BadF = 8,
    Exist = 20,
    Fault = 21,
    IlSeq = 25,
    Inval = 28,
    Io = 29,
    IsDir = 31,
    Loop = 32,
    NameTooLong = 37,
    NoEnt = 44,
    NoSys = 52,
    NotDir = 54,
    NotSup = 58,
    NotSock = 57,
    Overflow = 61,
    Perm = 63,
    SPipe = 70,

    /// <summary>
    /// The descriptor does not carry the authority the call needs — including a path that
    /// resolves outside the directory it was given against.
    /// </summary>
    NotCapable = 76,
}

/// <summary>The kinds of object a descriptor or a directory entry can refer to.</summary>
public enum FileType : byte
{
    Unknown = 0,
    BlockDevice = 1,
    CharacterDevice = 2,
    Directory = 3,
    RegularFile = 4,
    SocketDgram = 5,
    SocketStream = 6,
    SymbolicLink = 7,
}

/// <summary>What <c>path_open</c> does with the last component.</summary>
[Flags]
public enum OFlags : ushort
{
    None = 0,
    Create = 1 << 0,
    Directory = 1 << 1,
    Exclusive = 1 << 2,
    Truncate = 1 << 3,
}

/// <summary>Flags a descriptor carries after it is opened.</summary>
[Flags]
public enum FdFlags : ushort
{
    None = 0,
    Append = 1 << 0,
    DSync = 1 << 1,
    NonBlock = 1 << 2,
    RSync = 1 << 3,
    Sync = 1 << 4,
}

/// <summary>How the path argument of a <c>path_*</c> call is resolved.</summary>
[Flags]
public enum LookupFlags : uint
{
    None = 0,

    /// <summary>Follow a symbolic link at the last component.</summary>
    SymlinkFollow = 1 << 0,
}

/// <summary>Which timestamps a <c>*_filestat_set_times</c> call changes.</summary>
[Flags]
public enum FstFlags : ushort
{
    None = 0,
    Atim = 1 << 0,
    AtimNow = 1 << 1,
    Mtim = 1 << 2,
    MtimNow = 1 << 3,
}

#pragma warning restore CA1711

/// <summary>The rights a descriptor may carry.</summary>
[Flags]
public enum Rights : ulong
{
    None = 0,
    FdDatasync = 1UL << 0,
    FdRead = 1UL << 1,
    FdSeek = 1UL << 2,
    FdFdstatSetFlags = 1UL << 3,
    FdSync = 1UL << 4,
    FdTell = 1UL << 5,
    FdWrite = 1UL << 6,
    FdAdvise = 1UL << 7,
    FdAllocate = 1UL << 8,
    PathCreateDirectory = 1UL << 9,
    PathCreateFile = 1UL << 10,
    PathLinkSource = 1UL << 11,
    PathLinkTarget = 1UL << 12,
    PathOpen = 1UL << 13,
    FdReaddir = 1UL << 14,
    PathReadlink = 1UL << 15,
    PathRenameSource = 1UL << 16,
    PathRenameTarget = 1UL << 17,
    PathFilestatGet = 1UL << 18,
    PathFilestatSetSize = 1UL << 19,
    PathFilestatSetTimes = 1UL << 20,
    FdFilestatGet = 1UL << 21,
    FdFilestatSetSize = 1UL << 22,
    FdFilestatSetTimes = 1UL << 23,
    PathSymlink = 1UL << 24,
    PathRemoveDirectory = 1UL << 25,
    PathUnlinkFile = 1UL << 26,
    PollFdReadwrite = 1UL << 27,
    SockShutdown = 1UL << 28,
    SockAccept = 1UL << 29,

    /// <summary>Everything a regular file's descriptor can use.</summary>
    File = FdDatasync | FdRead | FdSeek | FdFdstatSetFlags | FdSync | FdTell | FdWrite |
        FdAdvise | FdAllocate | FdFilestatGet | FdFilestatSetSize | FdFilestatSetTimes |
        PollFdReadwrite,

    /// <summary>Everything a directory's descriptor can use.</summary>
    Directory = FdFdstatSetFlags | FdSync | FdAdvise | PathCreateDirectory | PathCreateFile |
        PathLinkSource | PathLinkTarget | PathOpen | FdReaddir | PathReadlink |
        PathRenameSource | PathRenameTarget | PathFilestatGet | PathFilestatSetSize |
        PathFilestatSetTimes | FdFilestatGet | FdFilestatSetTimes | PathSymlink |
        PathRemoveDirectory | PathUnlinkFile | PollFdReadwrite,
}

/// <summary>Where <c>fd_seek</c> measures its offset from.</summary>
public enum Whence : byte
{
    Set = 0,
    Cur = 1,
    End = 2,
}

/// <summary>The clocks <c>clock_time_get</c> can read.</summary>
public enum ClockId : uint
{
    Realtime = 0,
    Monotonic = 1,
    ProcessCpuTime = 2,
    ThreadCpuTime = 3,
}
