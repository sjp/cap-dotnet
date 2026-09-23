using System.IO.Compression;
using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;

namespace ArchiveExtractor;

/// <summary>
/// Extracts a zip archive into a directory, where a name inside the archive cannot put a file
/// anywhere else.
/// </summary>
/// <remarks>
/// <para>
/// Zip slip is the vulnerability: an archive entry named <c>../../home/user/.bashrc</c>, or
/// <c>/etc/cron.d/job</c>, is joined onto the destination path and written wherever the
/// result points. The usual fix is a check before the write — normalise the joined path,
/// compare its prefix with the destination's — and every such check has been got wrong
/// somewhere: a prefix that matches <c>/srv/out-evil</c> as well as <c>/srv/out</c>, a
/// symbolic link planted by an earlier entry, a separator one platform honours and the check
/// did not.
/// </para>
/// <para>
/// Here there is no check. The destination is a <see cref="Dir"/>, the entry's name is handed
/// to it as it came out of the archive, and a name that resolves to somewhere outside is
/// refused by the resolution itself. <see cref="Extract"/> never builds a path, so there is no
/// path for it to get wrong.
/// </para>
/// <para>
/// Run with no arguments it makes a hostile archive of its own, extracts it into a scratch
/// directory and shows what was refused. Run as <c>ArchiveExtractor archive.zip destination</c>
/// it extracts a real one.
/// </para>
/// </remarks>
internal static class Program
{
    internal static int Main(string[] args)
    {
        switch (args)
        {
            case []:
                return Demonstrate();

            case [string archivePath, string destinationPath]:
                // The composition root: the two paths the user named are the only authority
                // this program takes, and everything after this reaches the filesystem through
                // the handle opened here.
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                using (Dir destination = Dir.Open(destinationPath, AmbientAuthority.Acquire()))
                {
                    Summary summary = Extract(archive, destination);
                    PrintTree(destination);
                    return summary.Failed == 0 ? 0 : 1;
                }

            default:
                Console.Error.WriteLine("usage: ArchiveExtractor [archive.zip destination]");
                return 2;
        }
    }

    /// <summary>
    /// Writes every entry of <paramref name="archive"/> beneath <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// An entry that cannot be written is reported and skipped rather than ending the
    /// extraction, so that one hostile name in an archive does not hide what the others were.
    /// </remarks>
    internal static Summary Extract(ZipArchive archive, Dir destination)
    {
        Summary summary = default;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            try
            {
                if (entry.FullName.EndsWith('/'))
                {
                    EnsureDirectory(destination, entry.FullName.TrimEnd('/'));
                    continue;
                }

                EnsureParent(destination, entry.FullName);

                using CapFile file = destination.CreateNewFile(entry.FullName);
                using FileStream output = file.AsStream();
                using Stream input = entry.Open();
                input.CopyTo(output);

                Console.WriteLine($"  wrote    {entry.FullName}");
                summary.Written++;
            }
            catch (SandboxEscapeException)
            {
                Console.WriteLine($"  refused  {entry.FullName}  (outside the destination)");
                summary.Failed++;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"  refused  {entry.FullName}  (not a usable name)");
                summary.Failed++;
            }
            catch (IOException exception)
            {
                Console.WriteLine($"  failed   {entry.FullName}  ({exception.Message})");
                summary.Failed++;
            }
        }

        return summary;
    }

    /// <summary>
    /// Creates the directories above an entry's name that are not there yet.
    /// </summary>
    /// <remarks>
    /// Each directory is named by the leading part of the entry's own name, cut short rather
    /// than put together, so every name the handle is asked to resolve is one the archive
    /// supplied. <c>docs/../../x</c> is refused when its parent <c>docs/../..</c> is resolved,
    /// and a leading <c>/</c> when its first component is.
    /// </remarks>
    private static void EnsureParent(Dir destination, string name)
    {
        int separator = name.LastIndexOf('/');
        if (separator > 0)
        {
            EnsureDirectory(destination, name[..separator]);
        }
    }

    private static void EnsureDirectory(Dir destination, string name)
    {
        try
        {
            using Dir created = destination.OpenOrCreateDir(name);
        }
        catch (DirectoryNotFoundException)
        {
            // Only the last component is ever created; make the ones above it, then try again.
            EnsureParent(destination, name);
            using Dir created = destination.OpenOrCreateDir(name);
        }
    }

    /// <summary>Lists what ended up in the destination.</summary>
    private static void PrintTree(Dir destination)
    {
        Console.WriteLine();
        Console.WriteLine("The destination now holds:");
        foreach (WalkEntry entry in destination.Walk())
        {
            string suffix = entry.Type == CapFileType.Directory ? "/" : string.Empty;
            Console.WriteLine($"  {new string(' ', 2 * (entry.Depth - 1))}{entry.Name}{suffix}");
        }
    }

    /// <summary>
    /// Extracts an archive built to escape, and checks that nothing did.
    /// </summary>
    private static int Demonstrate()
    {
        // Ordinary ambient .NET, outside the capability graph: somewhere for the demonstration
        // to happen, made and removed by the sample itself. The extraction goes into "out";
        // everything else in the workspace is what a hostile entry would be trying to reach.
        string workspace = Directory.CreateTempSubdirectory("cap-archive-extractor-").FullName;

        try
        {
            string output = Directory.CreateDirectory(System.IO.Path.Join(workspace, "out")).FullName;

            using MemoryStream bytes = HostileArchive();
            using ZipArchive archive = new(bytes, ZipArchiveMode.Read);

            Console.WriteLine($"Extracting a hostile archive into {output}:");
            Summary summary;
            using (Dir destination = Dir.Open(output, AmbientAuthority.Acquire()))
            {
                summary = Extract(archive, destination);
                PrintTree(destination);
            }

            string[] escaped = [.. Directory
                .EnumerateFileSystemEntries(workspace, "*", SearchOption.AllDirectories)
                .Where(entry => !entry.StartsWith(output, StringComparison.Ordinal))];

            Console.WriteLine();
            if (escaped.Length > 0)
            {
                Console.WriteLine($"Written outside the destination: {string.Join(", ", escaped)}");
                return 1;
            }

            Console.WriteLine(
                $"{summary.Written} entries written, {summary.Failed} refused, " +
                "and nothing written outside the destination.");
            return summary.Written == 3 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>
    /// An archive with three honest entries and the classic ways out of an extraction
    /// directory.
    /// </summary>
    private static MemoryStream HostileArchive()
    {
        MemoryStream bytes = new();
        using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in (string[])[
                "readme.txt",
                "docs/guide/intro.txt",
                "docs/guide/usage.txt",
                "../escaped.txt",
                "docs/../../escaped.txt",
                "docs/guide/../../../escaped.txt",
                "/escaped-absolute.txt",
            ])
            {
                using StreamWriter writer = new(archive.CreateEntry(name).Open());
                writer.Write($"This is {name}.");
            }
        }

        bytes.Position = 0;
        return bytes;
    }

    /// <summary>How an extraction went.</summary>
    internal struct Summary
    {
        public int Written;
        public int Failed;
    }
}
