using System.Runtime.InteropServices;
using System.Text;

namespace Cap.Testing;

/// <summary>
/// The filesystem a test arranges its scenario in and inspects afterwards: whatever handles
/// opened by path reach during this run.
/// </summary>
/// <remarks>
/// <para>
/// Ordinarily the disk, reached through <c>System.IO</c>. A run told to put a filesystem held
/// in memory in place of the host swaps it here, before the first test, so that a test that
/// builds its tree through <see cref="HostFile"/>, <see cref="HostDirectory"/> and
/// <see cref="HostEntry"/> builds it wherever the code under test will look, without saying
/// which.
/// </para>
/// <para>
/// Either way the set-up stays outside the code under test. On disk it is the framework's own
/// path-based API; in memory it is the filesystem's own scaffolding, which reaches the tree
/// directly rather than through a handle. A test that arranged its attack through the
/// capability API would be arranging an attack that API had already agreed to.
/// </para>
/// <para>
/// Paths are ordinary paths in the host's syntax, as <see cref="ScratchTree.HostPath"/> and
/// <see cref="Path.Combine(string, string)"/> produce them. Each member means what the
/// <c>System.IO</c> member of the same name means, including which of them follow a symbolic
/// link.
/// </para>
/// </remarks>
internal interface IHostTree
{
    /// <summary>Whether this is the filesystem held in memory rather than the disk.</summary>
    bool InMemory { get; }

    /// <summary>Where a scratch directory is made when the test does not say.</summary>
    string TemporaryLocation { get; }

    /// <summary>What is at a path, without following a link there.</summary>
    HostEntryKind KindOf(string path);

    /// <summary>What a path leads to, following links, or <see cref="HostEntryKind.None"/>.</summary>
    HostEntryKind ResolvedKindOf(string path);

    /// <summary>What a symbolic link stores, or null when the path is not one.</summary>
    string? LinkTarget(string path);

    void CreateDirectory(string path);

    void WriteAllBytes(string path, byte[] contents);

    byte[] ReadAllBytes(string path);

    void CreateSymbolicLink(string path, string target, bool directory);

    void CreateHardLink(string existing, string link);

    /// <summary>The names in a directory, in no particular order.</summary>
    IReadOnlyList<string> Names(string path);

    /// <summary>Removes a file or a link of either kind, without following it.</summary>
    void DeleteFile(string path);

    void DeleteDirectory(string path, bool recursive);

    void Move(string source, string destination);

    DateTime GetLastWriteTimeUtc(string path);

    void SetLastWriteTimeUtc(string path, DateTime time);

    void SetLastAccessTimeUtc(string path, DateTime time);

    UnixFileMode GetUnixFileMode(string path);

    void SetUnixFileMode(string path, UnixFileMode mode);

    /// <summary>
    /// Creates an empty file in a directory under a name given as bytes, which need not be
    /// valid text. Unix only.
    /// </summary>
    void CreateRawName(string directory, byte[] name);

    /// <summary>Removes a name given as bytes, if it is there. Unix only.</summary>
    void DeleteRawName(string directory, byte[] name);
}

/// <summary>What a name is, as far as arranging and inspecting a tree cares.</summary>
internal enum HostEntryKind
{
    None,
    File,
    Directory,
    SymbolicLink,
}

/// <summary>The filesystem in force for this run.</summary>
internal static class HostTree
{
    /// <summary>
    /// The filesystem tests arrange their trees in. The disk unless a run replaced it before the
    /// first test.
    /// </summary>
    public static IHostTree Current { get; set; } = new DiskTree();

    /// <summary>Whether this run stands a filesystem held in memory in for the host.</summary>
    public static bool InMemory => Current.InMemory;
}

/// <summary>The <c>System.IO.File</c> members the tests arrange with, on <see cref="HostTree.Current"/>.</summary>
internal static class HostFile
{
    private static IHostTree Tree => HostTree.Current;

    public static bool Exists(string path) => Tree.ResolvedKindOf(path) == HostEntryKind.File;

    public static void WriteAllText(string path, string text) =>
        WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static void WriteAllText(string path, string text, Encoding encoding) =>
        Tree.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);

    public static void WriteAllBytes(string path, byte[] contents) => Tree.WriteAllBytes(path, contents);

    public static string ReadAllText(string path)
    {
        ReadOnlySpan<byte> bytes = Tree.ReadAllBytes(path);
        ReadOnlySpan<byte> bom = Encoding.UTF8.Preamble;
        return Encoding.UTF8.GetString(bytes.StartsWith(bom) ? bytes[bom.Length..] : bytes);
    }

    public static byte[] ReadAllBytes(string path) => Tree.ReadAllBytes(path);

    public static void CreateSymbolicLink(string path, string target) =>
        Tree.CreateSymbolicLink(path, target, directory: false);

    public static void CreateHardLink(string existing, string link) => Tree.CreateHardLink(existing, link);

    public static void Delete(string path) => Tree.DeleteFile(path);

    public static void Move(string source, string destination) => Tree.Move(source, destination);

    public static DateTime GetLastWriteTimeUtc(string path) => Tree.GetLastWriteTimeUtc(path);

    public static void SetLastWriteTimeUtc(string path, DateTime time) => Tree.SetLastWriteTimeUtc(path, time);

    public static void SetLastAccessTimeUtc(string path, DateTime time) => Tree.SetLastAccessTimeUtc(path, time);

    public static UnixFileMode GetUnixFileMode(string path) => Tree.GetUnixFileMode(path);

    public static void SetUnixFileMode(string path, UnixFileMode mode) => Tree.SetUnixFileMode(path, mode);

    /// <summary>
    /// Creates an empty file whose name is the given bytes, whatever they decode to. The
    /// framework takes names as strings, and no string encodes to bytes that are not text by
    /// its rules, so the name is planted the way a program that predates Unicode would plant it.
    /// </summary>
    public static void CreateRawName(string directory, byte[] name) => Tree.CreateRawName(directory, name);

    /// <summary>Removes a name planted by <see cref="CreateRawName"/>, if it is still there.</summary>
    public static void DeleteRawName(string directory, byte[] name) => Tree.DeleteRawName(directory, name);
}

/// <summary>The <c>System.IO.Directory</c> members the tests arrange with, on <see cref="HostTree.Current"/>.</summary>
internal static class HostDirectory
{
    private static IHostTree Tree => HostTree.Current;

    public static bool Exists(string path) => Tree.ResolvedKindOf(path) == HostEntryKind.Directory;

    public static void CreateDirectory(string path) => Tree.CreateDirectory(path);

    public static void CreateSymbolicLink(string path, string target) =>
        Tree.CreateSymbolicLink(path, target, directory: true);

    public static void Delete(string path, bool recursive = false) => Tree.DeleteDirectory(path, recursive);

    public static void Move(string source, string destination) => Tree.Move(source, destination);

    /// <summary>
    /// Makes a new, empty directory with a unique name beginning <paramref name="prefix"/> in
    /// the temporary location, and returns its path.
    /// </summary>
    public static string CreateTempSubdirectory(string prefix)
    {
        string path = Path.Join(Tree.TemporaryLocation, prefix + Path.GetRandomFileName().Replace(".", string.Empty, StringComparison.Ordinal));
        Tree.CreateDirectory(path);
        return path;
    }

    /// <summary>The entries of a directory as paths beneath it, as the framework returns them.</summary>
    public static string[] GetFileSystemEntries(string path) =>
        [.. Tree.Names(path).Select(name => Path.Join(path, name))];
}

/// <summary>Questions about a name that <c>System.IO</c> asks through <c>Path</c> and <c>FileSystemInfo</c>.</summary>
internal static class HostEntry
{
    private static IHostTree Tree => HostTree.Current;

    /// <summary>Whether a path leads to anything, as <c>Path.Exists</c> answers.</summary>
    public static bool Exists(string path) => Tree.ResolvedKindOf(path) != HostEntryKind.None;

    /// <summary>Whether the name is taken at all, a dangling link included.</summary>
    public static bool IsTaken(string path) => Tree.KindOf(path) != HostEntryKind.None;

    /// <summary>What is at the name itself, without following a link.</summary>
    public static HostEntryKind KindOf(string path) => Tree.KindOf(path);

    /// <summary>What a symbolic link stores, or null, as <c>FileSystemInfo.LinkTarget</c> answers.</summary>
    public static string? LinkTarget(string path) => Tree.LinkTarget(path);
}

/// <summary>The disk, through the framework's path-based API.</summary>
internal sealed partial class DiskTree : IHostTree
{
    public bool InMemory => false;

    public string TemporaryLocation => Path.GetTempPath();

    public HostEntryKind KindOf(string path)
    {
        FileInfo info = new(path);
        if (info.LinkTarget is not null)
        {
            return HostEntryKind.SymbolicLink;
        }

        return info.Exists ? HostEntryKind.File
            : Directory.Exists(path) ? HostEntryKind.Directory
            : HostEntryKind.None;
    }

    public HostEntryKind ResolvedKindOf(string path) =>
        File.Exists(path) ? HostEntryKind.File
        : Directory.Exists(path) ? HostEntryKind.Directory
        : HostEntryKind.None;

    public string? LinkTarget(string path) => new FileInfo(path).LinkTarget;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAllBytes(string path, byte[] contents) => File.WriteAllBytes(path, contents);

    public byte[] ReadAllBytes(string path)
    {
        // Shares every kind of access, because a test reads back what a handle it still holds
        // has written. The framework's own read denies other writers, and on Windows a file
        // already open for writing then cannot be opened by it at all.
        using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using MemoryStream contents = new();
        stream.CopyTo(contents);
        return contents.ToArray();
    }

    public void CreateSymbolicLink(string path, string target, bool directory)
    {
        if (directory)
        {
            Directory.CreateSymbolicLink(path, target);
        }
        else
        {
            File.CreateSymbolicLink(path, target);
        }
    }

    public void CreateHardLink(string existing, string link) => HardLinks.Create(existing, link);

    public IReadOnlyList<string> Names(string path) =>
        [.. Directory.GetFileSystemEntries(path).Select(entry => Path.GetFileName(entry))];

    public void DeleteFile(string path)
    {
        // Windows keeps a link to a directory as a directory entry, and removes it as one.
        if (OperatingSystem.IsWindows() && new DirectoryInfo(path) is { LinkTarget: not null, Attributes: var attributes } &&
            (attributes & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public void Move(string source, string destination)
    {
        if (Directory.Exists(source) && new FileInfo(source).LinkTarget is null)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public void SetLastWriteTimeUtc(string path, DateTime time) => File.SetLastWriteTimeUtc(path, time);

    public void SetLastAccessTimeUtc(string path, DateTime time) => File.SetLastAccessTimeUtc(path, time);

    public UnixFileMode GetUnixFileMode(string path) =>
        OperatingSystem.IsWindows()
            ? throw new PlatformNotSupportedException("Windows records no Unix mode bits.")
            : File.GetUnixFileMode(path);

    public void SetUnixFileMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows records no Unix mode bits.");
        }

        File.SetUnixFileMode(path, mode);
    }

    public void CreateRawName(string directory, byte[] name)
    {
        byte[] path = RawPath(directory, name);
        int fd = OpenRaw(path, 0x40 | 0x1, 0b110_100_100);
        if (fd < 0)
        {
            throw new IOException(
                $"A file with a name that is not text could not be created here: {Marshal.GetLastPInvokeError()}.");
        }

        _ = CloseRaw(fd);
    }

    public void DeleteRawName(string directory, byte[] name) => _ = UnlinkRaw(RawPath(directory, name));

    private static byte[] RawPath(string directory, byte[] name) =>
        OperatingSystem.IsWindows()
            ? throw new PlatformNotSupportedException("Names are UTF-16 on Windows and cannot be ill-formed bytes.")
            : [.. Encoding.UTF8.GetBytes(directory), (byte)'/', .. name, 0];

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int OpenRaw([In] byte[] path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseRaw(int fd);

    [LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static partial int UnlinkRaw([In] byte[] path);
}
