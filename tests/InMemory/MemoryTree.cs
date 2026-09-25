using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std.Testing;

namespace Cap.Testing;

/// <summary>
/// A filesystem held in memory, reached by path the way <c>System.IO</c> reaches the disk.
/// </summary>
/// <remarks>
/// <para>
/// The arranging half of a run that stands the filesystem in for the host. It works on the
/// node tree directly, under the filesystem's lock, rather than through a handle, so that
/// arranging a scenario never asks the code under test to agree to it.
/// </para>
/// <para>
/// Paths are read as the host reads them: separated by <c>/</c>, and under Windows rules by
/// <c>\</c> too, with any drive letter ignored, since there is only the one volume. Links are
/// followed where the <c>System.IO</c> member of the same name follows them: in every
/// component but the last, and in the last for reading and writing contents. A relative target
/// is resolved from the directory holding the link, and a rooted one from the top of the tree.
/// </para>
/// </remarks>
internal sealed class MemoryTree(InMemoryFileSystem fs) : IHostTree
{
    /// <summary>The directory scratch trees are made in, at the top of the tree.</summary>
    public const string TemporaryDirectory = "tmp";

    /// <summary>As many links as one lookup follows before giving up, as Linux allows.</summary>
    private const int MaxLinks = 40;

    public bool InMemory => true;

    public string TemporaryLocation =>
        fs.PathSyntax == CapPathSyntax.Windows ? $@"\{TemporaryDirectory}" : $"/{TemporaryDirectory}";

    public HostEntryKind KindOf(string path)
    {
        lock (fs.Gate)
        {
            return Kind(Resolve(path, followLast: false).Node);
        }
    }

    public HostEntryKind ResolvedKindOf(string path)
    {
        lock (fs.Gate)
        {
            try
            {
                return Kind(Resolve(path, followLast: true).Node);
            }
            catch (IOException)
            {
                // A loop of links, which System.IO reports as nothing being there.
                return HostEntryKind.None;
            }
        }
    }

    public string? LinkTarget(string path)
    {
        lock (fs.Gate)
        {
            MemoryNode? node = Resolve(path, followLast: false).Node;
            return node?.Type == CapNodeType.SymbolicLink ? node.LinkTarget : null;
        }
    }

    public void CreateDirectory(string path)
    {
        lock (fs.Gate)
        {
            string[] components = Components(path);
            for (int count = 1; count <= components.Length; count++)
            {
                Found found = Resolve(components.AsSpan(0, count), followLast: true);
                if (found.Node is { } existing)
                {
                    if (existing.Type != CapNodeType.Directory)
                    {
                        throw new IOException($"'{path}' has something that is not a directory on the way.");
                    }

                    continue;
                }

                fs.Attach(RequireParent(found, path), found.Name, fs.NewNode(CapNodeType.Directory));
            }
        }
    }

    public void WriteAllBytes(string path, byte[] contents)
    {
        lock (fs.Gate)
        {
            Found found = Resolve(path, followLast: true);
            if (found.Node is { } existing)
            {
                if (existing.Type != CapNodeType.File)
                {
                    throw new UnauthorizedAccessException($"'{path}' is not a file.");
                }

                long before = existing.Length;
                existing.Contents = contents;
                fs.Account(existing, before);
                DateTimeOffset now = fs.Now();
                existing.LastWriteTime = now;
                existing.ChangeTime = now;
                return;
            }

            MemoryNode file = fs.NewNode(CapNodeType.File);
            file.Contents = contents;
            fs.Attach(RequireParent(found, path), found.Name, file);
            fs.Account(file, 0);
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (fs.Gate)
        {
            MemoryNode node = Require(Resolve(path, followLast: true), path);
            return node.Type == CapNodeType.File
                ? node.Contents
                : throw new UnauthorizedAccessException($"'{path}' is not a file.");
        }
    }

    public void CreateSymbolicLink(string path, string target, bool directory)
    {
        lock (fs.Gate)
        {
            Found found = RequireAbsent(path);
            MemoryNode link = fs.NewNode(CapNodeType.SymbolicLink);
            link.LinkTarget = target;
            link.LinkIsDirectory = directory;
            if (directory && link.WindowsAttributes is { } attributes)
            {
                link.WindowsAttributes = attributes | FileAttributes.Directory;
            }

            fs.Attach(found.Parent!, found.Name, link);
        }
    }

    public void CreateHardLink(string existing, string link)
    {
        lock (fs.Gate)
        {
            MemoryNode node = Require(Resolve(existing, followLast: false), existing);
            if (node.Type == CapNodeType.Directory)
            {
                throw new UnauthorizedAccessException($"'{existing}' is a directory, which cannot have a second name.");
            }

            Found found = RequireAbsent(link);
            fs.Attach(found.Parent!, found.Name, node);
            node.LinkCount++;
            node.ChangeTime = fs.Now();
        }
    }

    public IReadOnlyList<string> Names(string path)
    {
        lock (fs.Gate)
        {
            MemoryNode node = Require(Resolve(path, followLast: true), path);
            return node.Type == CapNodeType.Directory
                ? [.. node.Entries.Keys]
                : throw new IOException($"'{path}' is not a directory.");
        }
    }

    public void DeleteFile(string path)
    {
        lock (fs.Gate)
        {
            Found found = Resolve(path, followLast: false);
            if (found.Node is not { } node)
            {
                // As File.Delete: a missing file is not an error, a missing directory is.
                _ = RequireParent(found, path);
                return;
            }

            if (node.Type == CapNodeType.Directory)
            {
                throw new UnauthorizedAccessException($"'{path}' is a directory.");
            }

            Detach(found.Parent!, found.Name, node);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        lock (fs.Gate)
        {
            Found found = Resolve(path, followLast: false);
            MemoryNode node = found.Node ?? throw new DirectoryNotFoundException($"There is no directory at '{path}'.");
            if (node.Type == CapNodeType.SymbolicLink)
            {
                Detach(found.Parent!, found.Name, node);
                return;
            }

            if (node.Type != CapNodeType.Directory)
            {
                throw new IOException($"'{path}' is not a directory.");
            }

            if (node.Entries.Count != 0 && !recursive)
            {
                throw new IOException($"The directory '{path}' is not empty.");
            }

            Empty(node);
            Detach(found.Parent!, found.Name, node);
        }
    }

    public void Move(string source, string destination)
    {
        lock (fs.Gate)
        {
            Found from = Resolve(source, followLast: false);
            MemoryNode node = Require(from, source);
            Found to = RequireAbsent(destination);

            _ = from.Parent!.Entries.Remove(from.Name);
            DateTimeOffset now = fs.Now();
            from.Parent.LastWriteTime = now;
            from.Parent.ChangeTime = now;
            fs.Attach(to.Parent!, to.Name, node);
            node.ChangeTime = now;
        }
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        lock (fs.Gate)
        {
            return Require(Resolve(path, followLast: false), path).LastWriteTime.UtcDateTime;
        }
    }

    public void SetLastWriteTimeUtc(string path, DateTime time)
    {
        lock (fs.Gate)
        {
            MemoryNode node = Require(Resolve(path, followLast: true), path);
            node.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
            node.ChangeTime = fs.Now();
        }
    }

    public void SetLastAccessTimeUtc(string path, DateTime time)
    {
        lock (fs.Gate)
        {
            MemoryNode node = Require(Resolve(path, followLast: true), path);
            node.LastAccessTime = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
            node.ChangeTime = fs.Now();
        }
    }

    public UnixFileMode GetUnixFileMode(string path)
    {
        lock (fs.Gate)
        {
            return Require(Resolve(path, followLast: true), path).UnixMode
                ?? throw new PlatformNotSupportedException("Windows rules record no Unix mode bits.");
        }
    }

    public void SetUnixFileMode(string path, UnixFileMode mode)
    {
        lock (fs.Gate)
        {
            MemoryNode node = Require(Resolve(path, followLast: true), path);
            if (node.UnixMode is null)
            {
                throw new PlatformNotSupportedException("Windows rules record no Unix mode bits.");
            }

            node.UnixMode = mode;
            node.ChangeTime = fs.Now();
        }
    }

    /// <remarks>
    /// Held as the library holds such a name, each byte that is not text escaped to a lone
    /// surrogate, since that is the string a handle on this filesystem is given for it.
    /// </remarks>
    public void CreateRawName(string directory, byte[] name) =>
        WriteAllBytes(Path.Join(directory, RawName(name)), []);

    public void DeleteRawName(string directory, byte[] name)
    {
        lock (fs.Gate)
        {
            Found found = Resolve(Path.Join(directory, RawName(name)), followLast: false);
            if (found.Node is { } node)
            {
                Detach(found.Parent!, found.Name, node);
            }
        }
    }

    private string RawName(byte[] name) =>
        fs.PathSyntax == CapPathSyntax.Windows
            ? throw new PlatformNotSupportedException("Names are UTF-16 under Windows rules and cannot be ill-formed bytes.")
            : PathEncoding.GetString(name);

    private static HostEntryKind Kind(MemoryNode? node) => node?.Type switch
    {
        null => HostEntryKind.None,
        CapNodeType.File => HostEntryKind.File,
        CapNodeType.Directory => HostEntryKind.Directory,
        CapNodeType.SymbolicLink => HostEntryKind.SymbolicLink,
        _ => HostEntryKind.File,
    };

    private static MemoryNode Require(Found found, string path) =>
        found.Node ?? throw (found.Parent is null
            ? new DirectoryNotFoundException($"A directory on the way to '{path}' does not exist.")
            : new FileNotFoundException($"Nothing is at '{path}'.", path));

    private static MemoryNode RequireParent(Found found, string path) =>
        found.Parent ?? throw new DirectoryNotFoundException($"A directory on the way to '{path}' does not exist.");

    private Found RequireAbsent(string path)
    {
        Found found = Resolve(path, followLast: false);
        _ = RequireParent(found, path);
        return found.Node is null ? found : throw new IOException($"'{path}' already exists.");
    }

    private void Empty(MemoryNode directory)
    {
        foreach ((string name, MemoryNode child) in directory.Entries.ToArray())
        {
            if (child.Type == CapNodeType.Directory)
            {
                Empty(child);
            }

            Detach(directory, name, child);
        }
    }

    private void Detach(MemoryNode directory, string name, MemoryNode node)
    {
        _ = directory.Entries.Remove(name);
        DateTimeOffset now = fs.Now();
        directory.LastWriteTime = now;
        directory.ChangeTime = now;
        fs.Unlinked(node);
    }

    private string[] Components(string path)
    {
        ReadOnlySpan<char> text = path;
        if (fs.PathSyntax == CapPathSyntax.Windows)
        {
            if (text.Length >= 2 && text[1] == ':' && char.IsAsciiLetter(text[0]))
            {
                text = text[2..];
            }

            return text.ToString().Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        }

        return text.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private bool IsRooted(string target) =>
        target.StartsWith('/') ||
        (fs.PathSyntax == CapPathSyntax.Windows &&
            (target.StartsWith('\\') || (target.Length >= 2 && target[1] == ':')));

    private Found Resolve(string path, bool followLast) => Resolve(Components(path), followLast);

    /// <summary>
    /// Walks a path from the top, following links as <c>System.IO</c> would. The parent is
    /// null when a directory on the way is missing, and the node null when only the last name is.
    /// </summary>
    private Found Resolve(ReadOnlySpan<string> components, bool followLast)
    {
        Stack<string> pending = new();
        for (int i = components.Length - 1; i >= 0; i--)
        {
            pending.Push(components[i]);
        }

        List<MemoryNode> trail = [fs.Root];
        MemoryNode? parent = fs.Root;
        string name = string.Empty;
        int links = 0;

        while (pending.TryPop(out string? component))
        {
            MemoryNode current = trail[^1];
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (trail.Count > 1)
                {
                    trail.RemoveAt(trail.Count - 1);
                }

                continue;
            }

            if (current.Type != CapNodeType.Directory)
            {
                return new Found(null, null, component);
            }

            parent = current;
            name = component;
            if (!current.Entries.TryGetValue(component, out MemoryNode? next))
            {
                return pending.Count == 0 ? new Found(null, parent, name) : new Found(null, null, name);
            }

            bool last = pending.Count == 0;
            if (next.Type == CapNodeType.SymbolicLink && (!last || followLast))
            {
                if (++links > MaxLinks)
                {
                    throw new IOException("Too many levels of symbolic links.");
                }

                string target = next.LinkTarget!;
                if (IsRooted(target))
                {
                    trail.RemoveRange(1, trail.Count - 1);
                }

                string[] parts = Components(target);
                for (int i = parts.Length - 1; i >= 0; i--)
                {
                    pending.Push(parts[i]);
                }

                continue;
            }

            trail.Add(next);
        }

        return new Found(trail[^1], parent, name);
    }

    /// <summary>What a lookup reached, and where the last name sits.</summary>
    private readonly record struct Found(MemoryNode? Node, MemoryNode? Parent, string Name);
}
