using System.Text;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Microsoft.Win32.SafeHandles;

namespace Cap.Std.Testing;

/// <summary>
/// A filesystem held in memory that hands out real <see cref="Dir"/> handles, for testing
/// code that takes one.
/// </summary>
/// <remarks>
/// <para>
/// Only the storage is simulated. A <see cref="Dir"/> from <see cref="OpenRoot()"/> is the
/// same type a program gets from <see cref="Dir.Open"/>, and every call through it runs the
/// library's own path parsing, resolution, symbolic-link policy and translation of failures
/// into exceptions. What is replaced is the bottom layer, the one that turns "open this one
/// name beneath this directory" into a system call. So code under test that climbs out of its
/// directory, or follows a link that leaves it, is refused with the same exception, for the
/// same reason, as it would be on disk.
/// </para>
/// <para>
/// <strong>Building and inspecting.</strong> The <c>Add</c>, <c>Set</c>, <c>Write</c> and
/// <c>Remove</c> members build a tree before a test runs, or change it between two steps of
/// one, and <see cref="Exists"/>, <see cref="ReadAllBytes"/>, <see cref="GetMetadata"/>,
/// <see cref="Snapshot"/> and their siblings inspect it afterwards without going through a
/// handle, so an assertion does not depend on the code it is checking. None of them is
/// refused by a fault or by <see cref="ReadOnly"/>, and none of them counts towards
/// <see cref="MutationCount"/>. Their paths are scaffolding rather than input to
/// anything under test: <c>/</c> separates components under either path syntax, a leading
/// <c>/</c> is allowed and means the same as none, and they are relative to the top of this
/// filesystem. They are looked up as written, so a symbolic link on the way is not followed,
/// and <c>.</c> and <c>..</c> are refused. Each name is checked against
/// <see cref="PathSyntax"/>, so a tree cannot hold a name its own handles would refuse.
/// </para>
/// <para>
/// <strong>What is modelled.</strong> Files, directories and symbolic links, whose targets
/// are stored as given and resolved only when followed; hard links, and the link count they
/// change; a stable identity per object, reported as a <see cref="CapFileId"/>; access,
/// write, change and creation times; Unix mode bits or Windows attributes, as
/// <see cref="PathSyntax"/> decides; renames with and without replacing the destination;
/// the refusal to remove a directory that is not empty; and files and directories that stay
/// usable through a handle after their name is removed. Permissions are recorded and
/// reported but not enforced, except that under Windows rules the read-only attribute refuses
/// the removal of a name and the opening of a file for writing, as it does on Windows.
/// Appending, once turned on for an open file, puts every write through that file and its
/// copies at the end, a stream's included, as Linux's append flag does; under Windows rules
/// it applies only to the file's own writes, as on Windows. Under Windows rules, too,
/// flushing a directory's entries is reported unsupported, as Windows reports it; a file open
/// with a <see cref="FileShare"/> that does not admit a later open, or does not include
/// <see cref="FileShare.Delete"/>, refuses that open, or the removal or renaming of its name,
/// as a sharing violation, as Windows refuses them; a link made as a file is refused over a
/// directory as a directory rather than as a name taken; a time before 1601 cannot be stored; a hard link cannot be given to a link made as a link to
/// a directory; and a full disk carries Windows' code rather than Linux's.
/// </para>
/// <para>
/// <strong>Times.</strong> Creating something stamps all four of its times; a write or a
/// change of length stamps its write and change times; a change to anything else about it
/// stamps its change time; and adding, removing or renaming an entry stamps the write and
/// change times of the directory holding it. Reading never changes the access time, as on a
/// filesystem mounted with <c>noatime</c>, so a read cannot make an assertion about times
/// depend on whether it happened. The time comes from
/// <see cref="InMemoryFileSystemOptions.TimeProvider"/>.
/// </para>
/// <para>
/// <strong>Faults.</strong> <see cref="SetUnreadable"/>, <see cref="SetUndeletable"/>,
/// <see cref="FailNextWrites"/>, <see cref="Capacity"/> and <see cref="ReadOnly"/> produce, on
/// demand, the failures a real filesystem produces only in awkward circumstances.
/// </para>
/// <para>
/// <strong>Authority.</strong> Opening a root needs no <see cref="AmbientAuthority"/> token.
/// The token marks where authority over the host enters a program, and this filesystem is not
/// the host's: a handle on it grants nothing outside it. Its roots therefore never appear in
/// the ambient authority log.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Every member, and every operation through a handle, may be
/// called from any number of threads at once. One lock guards the whole tree, so operations
/// are atomic with respect to each other and do not run in parallel. Separate instances share
/// nothing and do not contend.
/// </para>
/// </remarks>
public sealed class InMemoryFileSystem
{
    /// <summary>The stopped clock used when no time provider is given.</summary>
    private static readonly DateTimeOffset StoppedAt = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The name the backend gives its scratch directory when asked where temporary files go.
    /// </summary>
    /// <remarks>
    /// A NUL cannot appear in a build path, so this cannot be mistaken for a directory in the
    /// tree, and the scratch directory is kept out of the tree a test builds and inspects.
    /// </remarks>
    internal const string ScratchPath = "\0scratch";

    private static long s_nextVolumeId = 0x4d454d00;

    private readonly TimeProvider? _clock;
    private ulong _nextNodeId = 1;
    private int _failingWrites;
    private CapErrorKind _failingWriteKind;
    private long _usedBytes;
    private long? _capacity;
    private List<MemoryNode>? _held;
    private bool _readOnly;
    private long _mutations;

    /// <summary>Creates an empty filesystem that follows the running platform's rules.</summary>
    public InMemoryFileSystem()
        : this(new InMemoryFileSystemOptions())
    {
    }

    /// <summary>Creates an empty filesystem that behaves as <paramref name="options"/> says.</summary>
    /// <param name="options">How the filesystem behaves.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The path syntax is not one the enumeration defines, or the resolution is neither the
    /// walk nor the confined open.
    /// </exception>
    public InMemoryFileSystem(InMemoryFileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.PathSyntax))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.PathSyntax, "The path syntax is not one the enumeration defines.");
        }

        if (options.Resolution is not (ResolutionBackend.PortableWalk or ResolutionBackend.ConfinedOpen))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Resolution,
                "An in-memory filesystem resolves either one name at a time or a whole path at once.");
        }

        PathSyntax = options.PathSyntax;
        CaseSensitive = options.CaseSensitive ?? PathSyntax == CapPathSyntax.Unix;
        Resolution = options.Resolution;
        Names = CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _clock = options.TimeProvider;
        VolumeId = (ulong)Interlocked.Increment(ref s_nextVolumeId);

        Root = NewNode(CapNodeType.Directory);
        Scratch = NewNode(CapNodeType.Directory);
        Backend = new InMemoryPlatformOps(this);
    }

    /// <summary>The rules this filesystem's handles read paths under.</summary>
    public CapPathSyntax PathSyntax { get; }

    /// <summary>Whether this filesystem tells apart names that differ only in case.</summary>
    public bool CaseSensitive { get; }

    /// <summary>
    /// How paths beneath this filesystem's handles are resolved: one name at a time, or a
    /// whole path at once.
    /// </summary>
    public ResolutionBackend Resolution { get; }

    /// <summary>
    /// The most bytes the files in this filesystem may hold between them, or null for no
    /// limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A write, append, length change or space reservation through a handle that would take
    /// the total past this fails as a full disk does, with an <see cref="IOException"/> whose
    /// <see cref="CapIOException.KindOf"/> is <see cref="CapErrorKind.Other"/>: the kind the
    /// framework's own file APIs report for it. Lowering the limit below what is already held
    /// removes nothing, and only stops further growth.
    /// </para>
    /// <para>
    /// Counts the contents of every file, as a disk counts its blocks: a file created with no
    /// name at all counts while a handle keeps it open, and so does a file whose last name has
    /// been removed. Such a file stops counting once its last handle is disposed, or, for a
    /// handle nobody disposed, once the garbage collector has reclaimed it. Directories and
    /// links take no space.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? Capacity
    {
        get
        {
            lock (Gate)
            {
                return _capacity;
            }
        }

        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A capacity cannot be negative.");
            }

            lock (Gate)
            {
                _capacity = value;
            }
        }
    }

    /// <summary>
    /// How many bytes the files hold between them, those with no name that are still open
    /// included, as <see cref="Capacity"/> counts them.
    /// </summary>
    public long UsedBytes
    {
        get
        {
            lock (Gate)
            {
                _ = Reclaim();
                return _usedBytes;
            }
        }
    }

    /// <summary>
    /// How many handles on this filesystem, files and directories together, are open now.
    /// </summary>
    /// <remarks>
    /// Counts every handle issued through any <see cref="Dir"/>, <see cref="CapFile"/> or
    /// stream on this filesystem, including the ones they open for themselves, until it is
    /// disposed. A test can dispose everything the code under test gave it and assert zero, to
    /// show the code left nothing open behind it. A handle nobody disposed stops counting once
    /// the garbage collector has reclaimed it, as a real one is closed by its finalizer.
    /// </remarks>
    public int OpenHandleCount => Backend.OpenHandleCount;

    /// <summary>
    /// Whether every change through a handle is refused, as on a filesystem mounted read-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While set, anything through a handle that would create, change or remove something
    /// fails with a <see cref="CapIOException"/> whose <see cref="CapIOException.KindOf"/> is
    /// <see cref="CapErrorKind.ReadOnlyFilesystem"/>: opening a file for writing or to empty it,
    /// creating a file, directory or link, writing, appending, changing a length, permissions
    /// or times, removing, and renaming. A file already open for writing is refused its writes
    /// too, as on a filesystem the system has turned read-only after an error. Reading,
    /// listing, describing and opening for reading still work.
    /// </para>
    /// <para>
    /// A refusal that the names alone decide comes first: a name that is missing, or already
    /// taken where a new one is to go, is reported as such. The building members of this type
    /// are scaffolding and are not refused.
    /// </para>
    /// </remarks>
    public bool ReadOnly
    {
        get
        {
            lock (Gate)
            {
                return _readOnly;
            }
        }

        set
        {
            lock (Gate)
            {
                _readOnly = value;
            }
        }
    }

    /// <summary>
    /// How many changes the handles on this filesystem have made since it was created.
    /// </summary>
    /// <remarks>
    /// Counts each call through a handle that created, changed or removed something, whatever
    /// it changed: an open that created or emptied a file, a write, an append, a change of
    /// length, permissions or times, a new directory or link, a removal and a rename. A call
    /// that was refused, or changed nothing, is not counted, and neither is anything the
    /// building members of this type do. A test reads it before and after the code under test
    /// to show that code changed nothing, or how many times it did.
    /// </remarks>
    public long MutationCount
    {
        get
        {
            lock (Gate)
            {
                return _mutations;
            }
        }
    }

    /// <summary>The lock around the whole tree.</summary>
    internal object Gate { get; } = new();

    /// <summary>The directory at the top of the tree.</summary>
    internal MemoryNode Root { get; }

    /// <summary>The directory the backend reports as where temporary files go.</summary>
    internal MemoryNode Scratch { get; }

    /// <summary>The backend every handle on this filesystem resolves through.</summary>
    internal InMemoryPlatformOps Backend { get; }

    /// <summary>How two names are decided to be the same one.</summary>
    internal StringComparer Names { get; }

    /// <summary>The volume every object in this filesystem is on.</summary>
    internal ulong VolumeId { get; }

    /// <summary>Whether this filesystem imitates Windows' permissions and removal rules.</summary>
    internal bool WindowsRules => PathSyntax == CapPathSyntax.Windows;

    /// <summary>Whether changes through a handle are refused. Read under <see cref="Gate"/>.</summary>
    internal bool IsReadOnly => _readOnly;

    /// <summary>Records a change made through a handle. Called under <see cref="Gate"/>.</summary>
    internal void Mutated() => _mutations++;

    /// <summary>Creates a directory, and any missing directories above it.</summary>
    /// <param name="path">Where, as a build path; see the remarks on this type.</param>
    /// <remarks>A directory that already exists is left as it is.</remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Something other than a directory holds the name, or one above it.</exception>
    public void AddDirectory(string path)
    {
        lock (Gate)
        {
            (MemoryNode parent, string name) = Place(path);
            if (parent.Entries.TryGetValue(name, out MemoryNode? existing))
            {
                if (existing.Type != CapNodeType.Directory)
                {
                    throw new IOException($"'{path}' already exists and is not a directory.");
                }

                return;
            }

            Attach(parent, name, NewNode(CapNodeType.Directory));
        }
    }

    /// <summary>Creates an empty file, and any missing directories above it.</summary>
    /// <param name="path">Where, as a build path; see the remarks on this type.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">The name is taken, or something other than a directory is above it.</exception>
    public void AddFile(string path) => AddFile(path, ReadOnlySpan<byte>.Empty);

    /// <summary>Creates a file holding <paramref name="contents"/>, and any missing directories above it.</summary>
    /// <param name="path">Where, as a build path; see the remarks on this type.</param>
    /// <param name="contents">The bytes the file holds.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">The name is taken, or something other than a directory is above it.</exception>
    public void AddFile(string path, ReadOnlySpan<byte> contents)
    {
        lock (Gate)
        {
            (MemoryNode parent, string name) = PlaceNew(path);
            MemoryNode file = NewNode(CapNodeType.File);
            file.WriteAt(contents, 0);
            Attach(parent, name, file);
            _usedBytes += contents.Length;
        }
    }

    /// <summary>
    /// Creates a file holding <paramref name="text"/> as UTF-8 without a byte order mark, and
    /// any missing directories above it.
    /// </summary>
    /// <param name="path">Where, as a build path; see the remarks on this type.</param>
    /// <param name="text">The text the file holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">The name is taken, or something other than a directory is above it.</exception>
    public void AddFile(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        AddFile(path, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Creates a symbolic link storing <paramref name="target"/>, and any missing directories
    /// above it.
    /// </summary>
    /// <param name="path">Where, as a build path; see the remarks on this type.</param>
    /// <param name="target">
    /// The target, stored exactly as given and resolved only when the link is followed. It is
    /// read under <see cref="PathSyntax"/>, like a target a program stores through a handle,
    /// and unlike one it may be rooted, so that a test can plant the link an attacker would.
    /// </param>
    /// <param name="targetIsDirectory">
    /// Whether the link is recorded as a link to a directory, as Windows records it. It is
    /// reported and changes nothing about how the link is followed.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable build path, or <paramref name="target"/> is
    /// empty or holds a NUL.
    /// </exception>
    /// <exception cref="IOException">The name is taken, or something other than a directory is above it.</exception>
    public void AddSymbolicLink(string path, string target, bool targetIsDirectory = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (target.Contains('\0'))
        {
            throw new ArgumentException("A symbolic link's target cannot hold a NUL.", nameof(target));
        }

        lock (Gate)
        {
            (MemoryNode parent, string name) = PlaceNew(path);
            MemoryNode link = NewNode(CapNodeType.SymbolicLink);
            link.LinkTarget = target;
            link.LinkIsDirectory = targetIsDirectory;
            if (link.WindowsAttributes is { } attributes && targetIsDirectory)
            {
                link.WindowsAttributes = attributes | FileAttributes.Directory;
            }

            Attach(parent, name, link);
        }
    }

    /// <summary>
    /// Gives the object at <paramref name="existingPath"/> a second name, and creates any
    /// missing directories above it.
    /// </summary>
    /// <param name="path">The new name, as a build path.</param>
    /// <param name="existingPath">The object's existing name. A link there gets a second name itself.</param>
    /// <exception cref="ArgumentException">A path is not a usable build path.</exception>
    /// <exception cref="IOException">
    /// Nothing is at <paramref name="existingPath"/>, it is a directory, or the new name is taken.
    /// </exception>
    public void AddHardLink(string path, string existingPath)
    {
        lock (Gate)
        {
            MemoryNode existing = Find(existingPath);
            if (existing.Type == CapNodeType.Directory)
            {
                throw new IOException($"'{existingPath}' is a directory, and a directory cannot have a second name.");
            }

            (MemoryNode parent, string name) = PlaceNew(path);
            Attach(parent, name, existing);
            existing.LinkCount++;
            existing.ChangeTime = Now();
        }
    }

    /// <summary>
    /// Makes the file at <paramref name="path"/> hold <paramref name="contents"/>, creating it
    /// and any missing directories above it if it is not there.
    /// </summary>
    /// <param name="path">The file, as a build path.</param>
    /// <param name="contents">The bytes the file holds afterwards.</param>
    /// <remarks>
    /// A file that exists keeps its identity and is rewritten in place, as a program writing
    /// to it would rewrite it, so every other name it has sees the new contents; its write and
    /// change times are stamped. A handle open on it sees the new contents too.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Something other than a file holds the name, or something other than a directory is above it.</exception>
    public void WriteAllBytes(string path, ReadOnlySpan<byte> contents)
    {
        lock (Gate)
        {
            (MemoryNode parent, string name) = Place(path);
            if (!parent.Entries.TryGetValue(name, out MemoryNode? file))
            {
                file = NewNode(CapNodeType.File);
                Attach(parent, name, file);
            }
            else if (file.Type != CapNodeType.File)
            {
                throw new IOException($"'{path}' already exists and is not a file.");
            }
            else
            {
                DateTimeOffset now = Now();
                file.LastWriteTime = now;
                file.ChangeTime = now;
            }

            long before = file.Length;
            file.Contents = contents.ToArray();
            Account(file, before);
        }
    }

    /// <summary>
    /// Makes the file at <paramref name="path"/> hold <paramref name="text"/> as UTF-8 without
    /// a byte order mark, creating it and any missing directories above it if it is not there.
    /// </summary>
    /// <param name="path">The file, as a build path.</param>
    /// <param name="text">The text the file holds afterwards.</param>
    /// <remarks>As <see cref="WriteAllBytes"/>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Something other than a file holds the name, or something other than a directory is above it.</exception>
    public void WriteAllText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Removes the name at <paramref name="path"/>, which is not a directory's.</summary>
    /// <param name="path">A file or a link, as a build path. A link is removed, not followed.</param>
    /// <remarks>
    /// Scaffolding, so no fault, attribute or open handle refuses it. A file with other names
    /// keeps them, and one held open stays usable through the handle, as when a program
    /// removes the name.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is at <paramref name="path"/>, or it is a directory.</exception>
    public void RemoveFile(string path)
    {
        lock (Gate)
        {
            (MemoryNode parent, string name, MemoryNode node) = FindEntry(path);
            if (node.Type == CapNodeType.Directory)
            {
                throw new IOException($"'{path}' is a directory.");
            }

            Detach(parent, name, node);
        }
    }

    /// <summary>Removes the directory at <paramref name="path"/>.</summary>
    /// <param name="path">The directory, as a build path.</param>
    /// <param name="recursive">
    /// True to remove everything in it as well; false to refuse a directory that is not empty.
    /// </param>
    /// <remarks>
    /// Scaffolding, so no fault, attribute or open handle refuses it. A handle open on the
    /// directory, or on anything removed with it, stays usable for what needs no name, as when
    /// a program removes them.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a usable build path, or names the top of the tree.
    /// </exception>
    /// <exception cref="IOException">
    /// Nothing is at <paramref name="path"/>, it is not a directory, or it is not empty and
    /// <paramref name="recursive"/> is false.
    /// </exception>
    public void RemoveDirectory(string path, bool recursive = false)
    {
        lock (Gate)
        {
            (MemoryNode parent, string name, MemoryNode node) = FindEntry(path);
            if (node.Type != CapNodeType.Directory)
            {
                throw new IOException($"'{path}' is not a directory.");
            }

            if (node.Entries.Count > 0 && !recursive)
            {
                throw new IOException($"'{path}' is not empty.");
            }

            Empty(node);
            Detach(parent, name, node);
        }

        void Empty(MemoryNode directory)
        {
            foreach ((string childName, MemoryNode child) in directory.Entries.ToArray())
            {
                if (child.Type == CapNodeType.Directory)
                {
                    Empty(child);
                }

                Detach(directory, childName, child);
            }
        }
    }

    /// <summary>Sets the times recorded for the object at <paramref name="path"/>.</summary>
    /// <param name="path">The object, as a build path. A link there has its own times set.</param>
    /// <param name="lastAccess">The last-access time, or null to leave it.</param>
    /// <param name="lastWrite">The last-write time, or null to leave it.</param>
    /// <param name="creation">The creation time, or null to leave it.</param>
    /// <param name="change">The change time, or null to leave it.</param>
    /// <remarks>
    /// Scaffolding, so it stamps nothing of its own: the change time is set only when it is
    /// given, unlike a change made through a handle.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is at <paramref name="path"/>.</exception>
    public void SetTimes(
        string path,
        DateTimeOffset? lastAccess = null,
        DateTimeOffset? lastWrite = null,
        DateTimeOffset? creation = null,
        DateTimeOffset? change = null)
    {
        lock (Gate)
        {
            MemoryNode node = Find(path);
            node.LastAccessTime = lastAccess ?? node.LastAccessTime;
            node.LastWriteTime = lastWrite ?? node.LastWriteTime;
            node.CreationTime = creation ?? node.CreationTime;
            node.ChangeTime = change ?? node.ChangeTime;
        }
    }

    /// <summary>Sets the Unix mode bits of the object at <paramref name="path"/>.</summary>
    /// <param name="path">The object, as a build path.</param>
    /// <param name="mode">The mode bits.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="InvalidOperationException">This filesystem follows Windows rules and records attributes instead.</exception>
    /// <exception cref="IOException">Nothing is at <paramref name="path"/>.</exception>
    public void SetUnixMode(string path, UnixFileMode mode)
    {
        if (WindowsRules)
        {
            throw new InvalidOperationException(
                "This filesystem follows Windows rules, which record attributes rather than Unix mode bits.");
        }

        lock (Gate)
        {
            Find(path).UnixMode = mode;
        }
    }

    /// <summary>Sets the Windows attributes of the object at <paramref name="path"/>.</summary>
    /// <param name="path">The object, as a build path.</param>
    /// <param name="attributes">
    /// The attributes. <see cref="FileAttributes.ReadOnly"/> refuses the removal of the name
    /// and the opening of a file for writing until it is cleared. The bits that say what kind
    /// of object this is are kept as they were, whatever is given.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="InvalidOperationException">This filesystem follows Unix rules and records mode bits instead.</exception>
    /// <exception cref="IOException">Nothing is at <paramref name="path"/>.</exception>
    public void SetAttributes(string path, FileAttributes attributes)
    {
        if (!WindowsRules)
        {
            throw new InvalidOperationException(
                "This filesystem follows Unix rules, which record mode bits rather than Windows attributes.");
        }

        lock (Gate)
        {
            ApplyAttributes(Find(path), attributes);
        }
    }

    /// <summary>
    /// Sets or clears a fault that refuses every operation on the object at
    /// <paramref name="path"/>, and every lookup inside it if it is a directory, as
    /// permission denied.
    /// </summary>
    /// <param name="path">The object, as a build path.</param>
    /// <param name="unreadable">True to refuse, false to stop refusing.</param>
    /// <remarks>
    /// Opening, reading, writing and listing the object fail, and so does changing its times or
    /// its permissions, whether by name or through a handle opened before the fault was set.
    /// Describing, renaming and removing the object's name still work, as they do on a real
    /// system, where they depend on the directory holding the name rather than on the object.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is at <paramref name="path"/>.</exception>
    public void SetUnreadable(string path, bool unreadable = true)
    {
        lock (Gate)
        {
            Find(path).Unreadable = unreadable;
        }
    }

    /// <summary>
    /// Sets or clears a fault that refuses removing or replacing the name at
    /// <paramref name="path"/>, as permission denied.
    /// </summary>
    /// <param name="path">The object, as a build path.</param>
    /// <param name="undeletable">True to refuse, false to stop refusing.</param>
    /// <remarks>
    /// Unlike the Windows read-only attribute, this cannot be cleared through a handle, so it
    /// stands for a name that no retry removes.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is at <paramref name="path"/>.</exception>
    public void SetUndeletable(string path, bool undeletable = true)
    {
        lock (Gate)
        {
            Find(path).Undeletable = undeletable;
        }
    }

    /// <summary>
    /// Makes the next <paramref name="count"/> writes through any handle fail as
    /// <paramref name="kind"/>.
    /// </summary>
    /// <param name="count">How many writes fail. Zero stops any failures still to come.</param>
    /// <param name="kind">
    /// What the failure is reported as: the <see cref="CapIOException.KindOf"/> of the
    /// exception thrown. <see cref="CapErrorKind.Other"/> is what a full disk is reported as.
    /// </param>
    /// <remarks>
    /// A write is anything that changes a file's contents or length: a write, an append, or a
    /// length change. Each fails before changing anything, with the exception type the
    /// framework's file APIs throw for that kind where there is one:
    /// <see cref="FileNotFoundException"/> for <see cref="CapErrorKind.NotFound"/>,
    /// <see cref="UnauthorizedAccessException"/> for
    /// <see cref="CapErrorKind.PermissionDenied"/>, <see cref="PathTooLongException"/> for
    /// <see cref="CapErrorKind.NameTooLong"/>, and a plain <see cref="IOException"/> for
    /// <see cref="CapErrorKind.Other"/>. Any other kind is thrown as a
    /// <see cref="CapIOException"/> carrying it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is negative, or <paramref name="kind"/> is not a value the
    /// enumeration defines.
    /// </exception>
    public void FailNextWrites(int count, CapErrorKind kind = CapErrorKind.Other)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind is not one the enumeration defines.");
        }

        lock (Gate)
        {
            _failingWrites = count;
            _failingWriteKind = kind;
        }
    }

    /// <summary>Whether anything is at <paramref name="path"/>.</summary>
    /// <param name="path">A build path. A link at the end is reported, not followed.</param>
    /// <returns>True when a name is there.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    public bool Exists(string path)
    {
        lock (Gate)
        {
            return TryFind(path, out _);
        }
    }

    /// <summary>What the file at <paramref name="path"/> holds.</summary>
    /// <param name="path">A build path.</param>
    /// <returns>A copy of its contents.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is there, or it is not a file.</exception>
    public byte[] ReadAllBytes(string path)
    {
        lock (Gate)
        {
            MemoryNode node = Find(path);
            return node.Type == CapNodeType.File
                ? node.Contents
                : throw new IOException($"'{path}' is not a file.");
        }
    }

    /// <summary>What the file at <paramref name="path"/> holds, read as UTF-8.</summary>
    /// <param name="path">A build path.</param>
    /// <returns>The text. A leading byte order mark is dropped.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is there, or it is not a file.</exception>
    public string ReadAllText(string path)
    {
        ReadOnlySpan<byte> bytes = ReadAllBytes(path);
        ReadOnlySpan<byte> bom = Encoding.UTF8.Preamble;
        return Encoding.UTF8.GetString(bytes.StartsWith(bom) ? bytes[bom.Length..] : bytes);
    }

    /// <summary>The names in the directory at <paramref name="path"/>, in ordinal order.</summary>
    /// <param name="path">A build path. Empty or <c>/</c> is the top of the tree.</param>
    /// <returns>The names, spelled as they were created.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is there, or it is not a directory.</exception>
    public IReadOnlyList<string> GetEntries(string path = "")
    {
        lock (Gate)
        {
            MemoryNode node = Find(path);
            if (node.Type != CapNodeType.Directory)
            {
                throw new IOException($"'{path}' is not a directory.");
            }

            string[] names = [.. node.Entries.Keys];
            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>The target stored in the symbolic link at <paramref name="path"/>.</summary>
    /// <param name="path">A build path.</param>
    /// <returns>The target, exactly as it was stored.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is there, or it is not a symbolic link.</exception>
    public string GetSymbolicLinkTarget(string path)
    {
        lock (Gate)
        {
            MemoryNode node = Find(path);
            return node.Type == CapNodeType.SymbolicLink
                ? node.LinkTarget!
                : throw new IOException($"'{path}' is not a symbolic link.");
        }
    }

    /// <summary>The description of the object at <paramref name="path"/>, as a handle would report it.</summary>
    /// <param name="path">
    /// A build path. Empty or <c>/</c> is the top of the tree. A link at the end is described
    /// itself, not followed.
    /// </param>
    /// <returns>
    /// What <see cref="Dir.GetMetadata(string, bool)"/> reports for it, a directory's link count
    /// included.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="IOException">Nothing is at <paramref name="path"/>.</exception>
    public CapMetadata GetMetadata(string path)
    {
        lock (Gate)
        {
            return new CapMetadata(Backend.Describe(Find(path)));
        }
    }

    /// <summary>A copy of everything in the tree as it is now.</summary>
    /// <returns>
    /// Every name beneath the top of the tree, the top itself excepted, with what it names:
    /// its description, and a file's contents or a link's target. Compare two with
    /// <see cref="InMemorySnapshot.Diff"/>.
    /// </returns>
    /// <remarks>
    /// Taken at one instant: nothing changes the tree while the copy is made. The copy is the
    /// caller's and does not change with the tree afterwards.
    /// </remarks>
    public InMemorySnapshot Snapshot()
    {
        List<KeyValuePair<string, InMemoryEntry>> entries = [];
        lock (Gate)
        {
            Collect(Root, prefix: null);
        }

        return new InMemorySnapshot(entries, Names);

        void Collect(MemoryNode directory, string? prefix)
        {
            foreach ((string name, MemoryNode node) in directory.Entries)
            {
                string path = prefix is null ? name : prefix + "/" + name;
                entries.Add(new(path, new InMemoryEntry(
                    Backend.Describe(node),
                    node.Type == CapNodeType.File ? node.Contents : [],
                    node.Type == CapNodeType.SymbolicLink ? node.LinkTarget : null)));

                if (node.Type == CapNodeType.Directory)
                {
                    Collect(node, path);
                }
            }
        }
    }

    /// <summary>
    /// Opens the top of the tree as a directory handle that follows links within it.
    /// </summary>
    /// <returns>A handle, which the caller disposes.</returns>
    public Dir OpenRoot() => OpenRoot(string.Empty, SymlinkPolicy.FollowWithinSandbox);

    /// <summary>Opens the top of the tree as a directory handle under <paramref name="policy"/>.</summary>
    /// <param name="policy">What resolution beneath the handle does with a symbolic link.</param>
    /// <returns>A handle, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    public Dir OpenRoot(SymlinkPolicy policy) => OpenRoot(string.Empty, policy);

    /// <summary>
    /// Opens a directory in the tree as a handle, under <paramref name="policy"/>.
    /// </summary>
    /// <param name="path">
    /// The directory, as a build path. The handle reaches that directory and what is beneath
    /// it, and nothing above it, as a handle from <see cref="Dir.Open"/> does.
    /// </param>
    /// <param name="policy">What resolution beneath the handle does with a symbolic link.</param>
    /// <returns>A handle, which the caller disposes.</returns>
    /// <remarks>
    /// Any number of roots may be open at once, on the same directory or different ones. They
    /// all see the same tree.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable build path.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">Nothing is at <paramref name="path"/>, or it is not a directory.</exception>
    public Dir OpenRoot(string path, SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "The policy is not one the enumeration defines.");
        }

        SafeDirHandle handle;
        lock (Gate)
        {
            if (!TryFind(path, out MemoryNode? node) || node.Type != CapNodeType.Directory)
            {
                throw new DirectoryNotFoundException($"There is no directory at '{path}' in the in-memory filesystem.");
            }

            handle = Backend.OpenDirectoryHandle(node, CapAccess.Read);
        }

        return Dir.FromInMemoryRoot(handle, policy);
    }

    /// <summary>The time to stamp on a change made now.</summary>
    internal DateTimeOffset Now() => _clock?.GetUtcNow() ?? StoppedAt;

    /// <summary>Makes an object with this filesystem's identity, times and default permissions.</summary>
    /// <remarks>
    /// The permissions are what a creation asks for with the usual umask of 022: read and
    /// write for everybody but the owner's write alone for a file, and search too for a
    /// directory. A link's mode means nothing on Unix and is reported as every bit.
    /// </remarks>
    internal MemoryNode NewNode(CapNodeType type)
    {
        DateTimeOffset now = Now();
        MemoryNode node = new(Names)
        {
            Type = type,
            VolumeId = VolumeId,
            NodeId = _nextNodeId++,
            LastAccessTime = now,
            LastWriteTime = now,
            CreationTime = now,
            ChangeTime = now,
        };

        if (WindowsRules)
        {
            node.UnixMode = null;
            node.WindowsAttributes = type switch
            {
                CapNodeType.Directory => FileAttributes.Directory,
                CapNodeType.SymbolicLink => FileAttributes.ReparsePoint,
                _ => FileAttributes.Archive,
            };
        }
        else
        {
            node.UnixMode = type switch
            {
                CapNodeType.Directory => SharedDirectoryMode,
                CapNodeType.SymbolicLink => (UnixFileMode)0x1FF,
                _ => UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            };
        }

        return node;
    }

    /// <summary>What a directory created with the usual permissions records.</summary>
    internal const UnixFileMode SharedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Records Windows attributes, keeping the bits that say what the object is, and makes
    /// the read-only attribute refuse removal.
    /// </summary>
    internal static void ApplyAttributes(MemoryNode node, FileAttributes attributes)
    {
        const FileAttributes Kind = FileAttributes.Directory | FileAttributes.ReparsePoint;
        FileAttributes kept = (node.WindowsAttributes ?? 0) & Kind;
        node.WindowsAttributes = (attributes & ~Kind) | kept;
        node.RefusesRemoval = (attributes & FileAttributes.ReadOnly) != 0;
    }

    /// <summary>Puts <paramref name="node"/> in <paramref name="directory"/> under <paramref name="name"/>.</summary>
    internal void Attach(MemoryNode directory, string name, MemoryNode node)
    {
        directory.Entries[name] = node;
        node.Detached = false;
        DateTimeOffset now = Now();
        directory.LastWriteTime = now;
        directory.ChangeTime = now;
    }

    /// <summary>
    /// Takes the next write fault, if one is due, as the kind to report.
    /// </summary>
    internal bool TakeWriteFault(out CapErrorKind kind)
    {
        kind = _failingWriteKind;
        if (_failingWrites == 0)
        {
            return false;
        }

        _failingWrites--;
        return true;
    }

    /// <summary>Whether <paramref name="growth"/> more bytes fit.</summary>
    /// <remarks>
    /// Gives back what closed files with no name held only when the answer would otherwise be
    /// no, so that a write that fits costs no look through them.
    /// </remarks>
    internal bool HasRoomFor(long growth) =>
        growth <= 0 || _capacity is not { } capacity || _usedBytes + growth <= capacity ||
        (Reclaim() && _usedBytes + growth <= capacity);

    /// <summary>Records a change in a file's length against the capacity.</summary>
    internal void Account(MemoryNode file, long before) => _usedBytes += file.Length - before;

    /// <summary>
    /// Records that <paramref name="file"/>, which has no name, still holds its bytes until
    /// its last handle closes.
    /// </summary>
    internal void Held(MemoryNode file) => (_held ??= []).Add(file);

    /// <summary>
    /// Gives back the bytes of files with no name whose last handle has closed, and says
    /// whether there were any.
    /// </summary>
    /// <remarks>
    /// There is no hook on a file handle's close (the runtime's handle type is sealed), so
    /// closure is observed here instead, as <see cref="OpenHandleCount"/> observes it.
    /// </remarks>
    private bool Reclaim()
    {
        if (_held is not { } held)
        {
            return false;
        }

        bool reclaimed = false;
        for (int i = held.Count - 1; i >= 0; i--)
        {
            MemoryNode file = held[i];
            if (!IsOpen(file))
            {
                _usedBytes -= file.Length;
                held.RemoveAt(i);
                reclaimed = true;
            }
        }

        return reclaimed;
    }

    /// <summary>
    /// Whether some handle on <paramref name="file"/> is still open, dropping those found
    /// closed or collected on the way.
    /// </summary>
    private static bool IsOpen(MemoryNode file)
    {
        if (file.Opens is not { } opens)
        {
            return false;
        }

        for (int i = opens.Count - 1; i >= 0; i--)
        {
            if (opens[i].TryGetTarget(out SafeFileHandle? handle) && !handle.IsClosed)
            {
                return true;
            }

            opens.RemoveAt(i);
        }

        return false;
    }

    /// <summary>Removes a name, and records that the object it named has one fewer.</summary>
    internal void Detach(MemoryNode directory, string name, MemoryNode node)
    {
        _ = directory.Entries.Remove(name);
        DateTimeOffset now = Now();
        directory.LastWriteTime = now;
        directory.ChangeTime = now;
        Unlinked(node);
    }

    /// <summary>
    /// Records that a name for <paramref name="node"/> has gone, and detaches it once it has
    /// none left.
    /// </summary>
    internal void Unlinked(MemoryNode node)
    {
        node.ChangeTime = Now();
        if (node.Type == CapNodeType.Directory)
        {
            node.Detached = true;
            return;
        }

        node.LinkCount--;
        if (node.LinkCount <= 0 && !node.Detached)
        {
            node.LinkCount = 0;
            node.Detached = true;
            if (node.Type == CapNodeType.File)
            {
                if (IsOpen(node))
                {
                    Held(node);
                }
                else
                {
                    _usedBytes -= node.Length;
                }
            }
        }
    }

    /// <summary>
    /// Finds the directory a new name at <paramref name="path"/> goes in, refusing a name
    /// that is taken.
    /// </summary>
    private (MemoryNode Parent, string Name) PlaceNew(string path)
    {
        (MemoryNode parent, string name) = Place(path);
        if (parent.Entries.ContainsKey(name))
        {
            throw new IOException($"'{path}' already exists.");
        }

        return (parent, name);
    }

    /// <summary>
    /// Finds the directory <paramref name="path"/>'s last name goes in, creating the
    /// directories above it that are missing.
    /// </summary>
    private (MemoryNode Parent, string Name) Place(string path)
    {
        string[] components = Split(path);
        if (components.Length == 0)
        {
            throw new ArgumentException("A build path must name something beneath the top of the tree.", nameof(path));
        }

        MemoryNode current = Root;
        foreach (string component in components.AsSpan(0, components.Length - 1))
        {
            if (!current.Entries.TryGetValue(component, out MemoryNode? next))
            {
                next = NewNode(CapNodeType.Directory);
                Attach(current, component, next);
            }
            else if (next.Type != CapNodeType.Directory)
            {
                throw new IOException($"'{component}' in '{path}' is not a directory.");
            }

            current = next;
        }

        return (current, components[^1]);
    }

    /// <summary>
    /// Finds the entry a build path names: the directory holding it, its name as stored there,
    /// and what it names.
    /// </summary>
    private (MemoryNode Parent, string Name, MemoryNode Node) FindEntry(string path)
    {
        string[] components = Split(path);
        if (components.Length == 0)
        {
            throw new ArgumentException("A build path must name something beneath the top of the tree.", nameof(path));
        }

        MemoryNode parent = Root;
        MemoryNode? node = Root;
        foreach (string component in components)
        {
            parent = node;
            if (parent.Type != CapNodeType.Directory || !parent.Entries.TryGetValue(component, out node))
            {
                throw new IOException($"Nothing is at '{path}' in the in-memory filesystem.");
            }
        }

        return (parent, components[^1], node);
    }

    /// <summary>Finds what is at a build path, or throws.</summary>
    private MemoryNode Find(string path) =>
        TryFind(path, out MemoryNode? node)
            ? node
            : throw new IOException($"Nothing is at '{path}' in the in-memory filesystem.");

    /// <summary>Finds what is at a build path, looking every name up as written.</summary>
    private bool TryFind(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MemoryNode? node)
    {
        node = Root;
        foreach (string component in Split(path))
        {
            if (node.Type != CapNodeType.Directory || !node.Entries.TryGetValue(component, out node))
            {
                node = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a build path at <c>/</c>, refusing any name the filesystem's own handles would
    /// refuse.
    /// </summary>
    private string[] Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string[] components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string component in components)
        {
            bool separator = PathSyntax == CapPathSyntax.Windows && component.Contains('\\');
            if (separator ||
                component is "." or ".." ||
                CapPath.Validate(component, PathSyntax, ParentLinkPolicy.Reject) != CapPathError.None ||
                MemoryPathWalk.NameTooLong(component, PathSyntax))
            {
                throw new ArgumentException(
                    $"'{component}' in '{path}' is not a name this filesystem's handles could reach under {PathSyntax} rules " +
                    $"(a name is at most {MemoryPathWalk.MaxNameLength} {(WindowsRules ? "UTF-16 units" : "UTF-8 bytes")}).",
                    nameof(path));
            }
        }

        return components;
    }
}
