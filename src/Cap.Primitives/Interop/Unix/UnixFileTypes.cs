namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The two ways a Unix system says what an entry is, and the one answer they map to.
/// </summary>
/// <remarks>
/// <para>
/// Shared by both Unix backends, which is unusual here and is justified by these values in
/// particular: the type bits of a mode and the kind byte a directory read carries are fixed
/// by history rather than by either kernel. They have held the same values across every
/// system descended from the same design, and the two backends disagreeing about them would
/// mean one of them was simply wrong.
/// </para>
/// <para>
/// Kept apart from <see cref="CapNodeType"/>, which resolution uses. That one answers
/// whether a walk can continue through a node and collapses everything else into a single
/// case; this one is what a caller reading a directory is told, and a caller has reasons to
/// tell a socket from a pipe that a walk never has.
/// </para>
/// </remarks>
internal static class UnixFileTypes
{
    /// <summary>The bits of a mode that say what the node is.</summary>
    public const ushort S_IFMT = 0xF000;

    /// <summary>
    /// The bits of a mode that say who may do what: the nine permission bits, plus the
    /// set-user, set-group and sticky bits above them.
    /// </summary>
    /// <remarks>
    /// The framework's own <see cref="UnixFileMode"/> is numbered to match these exactly, so
    /// masking is the whole of the conversion. That is not a coincidence worth relying on
    /// silently — it is checked by a test, because a mismatch would silently report every
    /// file's permissions as some other file's.
    /// </remarks>
    public const ushort S_IPERM = 0x0FFF;

    public const ushort S_IFIFO = 0x1000;
    public const ushort S_IFCHR = 0x2000;
    public const ushort S_IFDIR = 0x4000;
    public const ushort S_IFBLK = 0x6000;
    public const ushort S_IFREG = 0x8000;
    public const ushort S_IFLNK = 0xA000;
    public const ushort S_IFSOCK = 0xC000;

    // --- The kind a directory read reports, where the filesystem supplies it ---------------

    /// <summary>
    /// The filesystem did not say. Not an error and not rare: a filesystem is entitled to
    /// answer this for every entry, and some do.
    /// </summary>
    public const byte DT_UNKNOWN = 0;

    public const byte DT_FIFO = 1;
    public const byte DT_CHR = 2;
    public const byte DT_DIR = 4;
    public const byte DT_BLK = 6;
    public const byte DT_REG = 8;
    public const byte DT_LNK = 10;
    public const byte DT_SOCK = 12;

    /// <summary>
    /// The application context switch that makes every entry's kind be looked up rather than
    /// taken from the directory read.
    /// </summary>
    /// <remarks>
    /// Two reasons it exists, and the second is the one that matters day to day.
    /// <para>
    /// A filesystem is allowed to answer a directory read without saying what each entry is,
    /// and several do — but one that answers <em>wrongly</em> is also possible, and has
    /// happened, in filesystems implemented outside the kernel. A caller that has met one
    /// needs a way to stop trusting the answer without giving up the API, and this is it.
    /// </para>
    /// <para>
    /// It is also the only way to exercise the lookup on a machine whose filesystems all
    /// report the kind. Without it the code that covers for the filesystems that do not
    /// would ship having never run, because whether such a filesystem is mounted is not
    /// something a test can arrange.
    /// </para>
    /// </remarks>
    public const string AlwaysLookUpKindSwitchName = "Cap.Primitives.AlwaysLookUpEntryKind";

    /// <summary>The environment variable that does the same.</summary>
    public const string AlwaysLookUpKindVariableName = "CAPDOTNET_ALWAYS_LOOK_UP_ENTRY_KIND";

    /// <summary>
    /// Whether the kind a directory read reports should be disregarded.
    /// </summary>
    /// <remarks>
    /// Read once when an enumeration begins rather than per entry: the answer cannot
    /// usefully change in the middle of reading one directory, and asking per entry would
    /// put a lookup of this on the path of every name.
    /// </remarks>
    public static bool AlwaysLookUpKind =>
        (AppContext.TryGetSwitch(AlwaysLookUpKindSwitchName, out bool enabled) && enabled) ||
        Environment.GetEnvironmentVariable(AlwaysLookUpKindVariableName) is "1" or "true" or "TRUE";

    /// <summary>Reads the permission, set-id and sticky bits of a mode.</summary>
    public static UnixFileMode PermissionsFromMode(ushort mode) => (UnixFileMode)(mode & S_IPERM);

    /// <summary>Reads the type bits of a mode.</summary>
    public static CapFileType FromMode(ushort mode) => (mode & S_IFMT) switch
    {
        S_IFREG => CapFileType.File,
        S_IFDIR => CapFileType.Directory,
        S_IFLNK => CapFileType.Symlink,
        S_IFSOCK => CapFileType.Socket,
        S_IFIFO => CapFileType.Fifo,
        S_IFCHR => CapFileType.CharDevice,
        S_IFBLK => CapFileType.BlockDevice,
        _ => CapFileType.Unknown,
    };

    /// <summary>
    /// Reads the kind byte a directory read carries.
    /// </summary>
    /// <remarks>
    /// Anything unrecognised becomes <see cref="CapFileType.Unknown"/>, which is also what
    /// the filesystem's own "I did not say" value becomes — and both then cost a lookup of
    /// the name. That is the right way round: a kind byte this code does not know is a kind
    /// it cannot describe, and answering it with a guess would describe the entry wrongly
    /// rather than not at all.
    /// </remarks>
    public static CapFileType FromDirectoryEntry(byte kind) => kind switch
    {
        DT_REG => CapFileType.File,
        DT_DIR => CapFileType.Directory,
        DT_LNK => CapFileType.Symlink,
        DT_SOCK => CapFileType.Socket,
        DT_FIFO => CapFileType.Fifo,
        DT_CHR => CapFileType.CharDevice,
        DT_BLK => CapFileType.BlockDevice,
        _ => CapFileType.Unknown,
    };
}
