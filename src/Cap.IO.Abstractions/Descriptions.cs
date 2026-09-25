using System.IO.Abstractions;
using Cap.Primitives;
using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary>Which of an entry's times a member reads or sets.</summary>
internal enum TimeKind
{
    Creation,
    Access,
    Write,
}

/// <summary>
/// Times, attributes and link targets, read through a <see cref="Dir"/> and reported as
/// <c>System.IO</c> reports them. Shared by <see cref="FileAdapter"/>,
/// <see cref="DirectoryAdapter"/> and the info wrappers, which answer the same questions.
/// </summary>
internal static class Descriptions
{
    /// <summary>How many links <c>ResolveLinkTarget</c> follows before giving up, as the kernel does.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>What <c>System.IO</c> reports as the time of something that is not there.</summary>
    public static DateTime Missing(bool utc) => utc ? DateTime.FromFileTimeUtc(0) : DateTime.FromFileTime(0);

    /// <summary>One of the times in a description, in UTC or local time.</summary>
    public static DateTime TimeOf(in CapMetadata metadata, TimeKind kind, bool utc)
    {
        DateTimeOffset value = kind switch
        {
            TimeKind.Access => metadata.LastAccessTime,
            TimeKind.Write => metadata.LastWriteTime,

            // Where the filesystem records no birth time, the older of the write and change
            // times is the best answer there is, and the one .NET gives on Unix.
            _ => metadata.CreationTime
                ?? (metadata.ChangeTime is { } changed && changed < metadata.LastWriteTime ? changed : metadata.LastWriteTime),
        };

        return utc ? value.UtcDateTime : value.LocalDateTime;
    }

    /// <summary>
    /// Reads a time of what a path names, following a final link; the missing-entry time
    /// when there is nothing there, as <c>System.IO</c> answers.
    /// </summary>
    public static DateTime GetTime(DirFileSystem fs, string path, TimeKind kind, bool utc) =>
        fs.Run(path, Expected.Any, request =>
        {
            try
            {
                return TimeOf(fs.Describe(request, followLink: true), kind, utc);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return Missing(utc);
            }
        });

    /// <summary>Sets a time of what a path names, following a final link as <c>System.IO</c> does.</summary>
    public static void SetTime(DirFileSystem fs, string path, TimeKind kind, DateTime value, bool utc)
    {
        if (kind == TimeKind.Creation)
        {
            throw Unsupported.CreationTime();
        }

        CapFileTime time = At(value, utc);
        CapFileTime access = kind == TimeKind.Access ? time : CapFileTime.Unchanged;
        CapFileTime write = kind == TimeKind.Write ? time : CapFileTime.Unchanged;

        fs.Run(path, Expected.Any, request =>
        {
            if (request.IsRoot)
            {
                fs.Dir.SetTimes(access, write);
            }
            else
            {
                fs.Dir.SetTimes(request.Relative, access, write, followLink: true);
            }
        });
    }

    /// <summary>
    /// A caller's <see cref="DateTime"/> as a moment: one of unspecified kind is taken as UTC
    /// by the <c>Utc</c> members and as local time by the others, as <c>System.IO</c> takes it.
    /// </summary>
    public static CapFileTime At(DateTime value, bool utc)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, utc ? DateTimeKind.Utc : DateTimeKind.Local);
        }

        return CapFileTime.At(new DateTimeOffset(value));
    }

    /// <summary>The attributes of what a request names, as <c>System.IO</c> reports them.</summary>
    public static FileAttributes AttributesOf(DirFileSystem fs, in Request request) =>
        AttributesOf(fs, request, fs.Describe(request, followLink: false));

    /// <summary>The attributes of an entry described without following it.</summary>
    /// <remarks>
    /// Windows records attributes, and they are reported as recorded. Elsewhere they are
    /// derived as .NET derives them: a link is a reparse point, and a directory as well when
    /// it leads to one inside the tree; a name beginning with a dot is hidden; and a mode
    /// without the owner's write bit is read-only.
    /// </remarks>
    public static FileAttributes AttributesOf(DirFileSystem fs, in Request request, in CapMetadata own)
    {
        if (own.Permissions.TryGetWindowsAttributes(out FileAttributes recorded))
        {
            return recorded == 0 ? FileAttributes.Normal : recorded;
        }

        FileAttributes attributes = 0;
        switch (own.Type)
        {
            case CapFileType.Directory:
                attributes |= FileAttributes.Directory;
                break;
            case CapFileType.Symlink:
                attributes |= FileAttributes.ReparsePoint;
                if (fs.TryDescribe(request, followLink: true, out CapMetadata target) && target.Type == CapFileType.Directory)
                {
                    attributes |= FileAttributes.Directory;
                }

                break;
        }

        string name = fs.Paths.LastName(request.Virtual);
        if (name.Length > 1 && name[0] == '.' && name != "..")
        {
            attributes |= FileAttributes.Hidden;
        }

        if (own.Permissions.TryGetUnixMode(out UnixFileMode mode) && (mode & UnixFileMode.UserWrite) == 0)
        {
            attributes |= FileAttributes.ReadOnly;
        }

        return attributes == 0 ? FileAttributes.Normal : attributes;
    }

    /// <summary>The Unix mode of what a path names, following a final link.</summary>
    public static UnixFileMode GetUnixFileMode(DirFileSystem fs, string path) =>
        fs.Run(path, Expected.Any, request => UnixModeOf(fs.Describe(request, followLink: true)));

    /// <summary>The Unix mode in a description; on Windows there is none, as for <c>System.IO</c>.</summary>
    public static UnixFileMode UnixModeOf(in CapMetadata metadata) =>
        metadata.Permissions.TryGetUnixMode(out UnixFileMode mode)
            ? mode
            : throw new PlatformNotSupportedException("Unix file modes are not recorded on this platform.");

    /// <summary>
    /// What a link leads to, as an info over the virtual path its target names, or null when
    /// the name is not a link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relative target is taken against the directory holding the link, by joining it onto
    /// the link's own request with the link's name sliced off, so a <c>..</c> in the target is
    /// walked by the <see cref="Dir"/> when the info is used. The info names a request, not a
    /// verdict: whether the target stays inside is decided when something resolves it.
    /// </para>
    /// <para>
    /// A rooted target is refused with <see cref="SandboxEscapeException"/>. It names a place
    /// on the host, and presenting it as a virtual path would name something else beneath the
    /// root that merely shares its spelling.
    /// </para>
    /// </remarks>
    public static IFileSystemInfo? ResolveLinkTarget(DirFileSystem fs, string path, bool returnFinalTarget, bool directory) =>
        fs.Run(path, Expected.Any, request =>
        {
            if (request.IsRoot || fs.Describe(request, followLink: false).Type != CapFileType.Symlink)
            {
                return null;
            }

            Request link = request;
            for (int hops = 0; ; hops++)
            {
                string target = fs.Dir.ReadLink(link.Relative);
                if (fs.Paths.IsRooted(target))
                {
                    throw new SandboxEscapeException(
                        $"The symbolic link '{link.Virtual}' leads to '{target}', which is outside this file system.");
                }

                string next = fs.Paths.Join(fs.Paths.ParentRequest(link.Virtual) ?? fs.Paths.Root, target);
                Request resolved = fs.Paths.Resolve(next, fs.CurrentDirectory);
                if (!returnFinalTarget
                    || resolved.IsRoot
                    || !fs.TryDescribe(resolved, followLink: false, out CapMetadata metadata)
                    || metadata.Type != CapFileType.Symlink)
                {
                    return directory
                        ? new DirectoryInfoAdapter(fs, next)
                        : (IFileSystemInfo)new FileInfoAdapter(fs, next);
                }

                if (hops >= MaxLinkHops)
                {
                    throw new CapIOException(
                        CapErrorKind.LinkNotFollowed,
                        $"'{request.Virtual}' leads through more than {MaxLinkHops} symbolic links.");
                }

                link = resolved;
            }
        });
}
