using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>What one operation came to, and everything it let the caller see.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Detail">The exception, when it ended in one, for the failure message.</param>
internal sealed record Observation(Outcome Outcome, Exception? Detail)
{
    /// <summary>Names the operation listed.</summary>
    public List<string> Names { get; } = [];

    /// <summary>Contents the operation read.</summary>
    public List<string> Contents { get; } = [];

    /// <summary>Identities of the objects the operation reached.</summary>
    public List<CapFileId> Objects { get; } = [];

    /// <summary>
    /// For the operation that creates a link and then follows it, whether the creation
    /// succeeded before the follow was refused.
    /// </summary>
    public bool LinkCreated { get; set; }
}

/// <summary>
/// Drives one operation through the public API and classifies how it ended.
/// </summary>
/// <remarks>
/// Whatever an operation that succeeds hands back is read in full and kept — the names a
/// directory lists, the bytes a file holds, the identity of what was opened — so that the
/// test can check none of it came from outside. An operation that succeeded is not evidence
/// of containment by itself; what it reached is.
/// </remarks>
internal static class OperationRunner
{
    /// <summary>The largest amount of a file read back.</summary>
    private const int ReadLimit = 4096;

    public static Observation Run(Dir root, Operation operation, string path)
    {
        Observation observation = new(Outcome.Success, null);

        try
        {
            return Perform(root, operation, path, observation)
                ? observation
                : new Observation(Outcome.NotFound, null);
        }
        catch (Exception e) when (Classify(e) is Outcome outcome)
        {
            Observation failed = new(outcome, e) { LinkCreated = observation.LinkCreated };
            return failed;
        }
    }

    /// <summary>
    /// The outcome an exception stands for, or null for one that is not an answer at all and
    /// should fail the test as it is.
    /// </summary>
    public static Outcome? Classify(Exception e) => e switch
    {
        SandboxEscapeException => Outcome.Escape,
        FileNotFoundException or DirectoryNotFoundException => Outcome.NotFound,
        UnauthorizedAccessException => Outcome.Denied,
        ArgumentNullException => null,
        ArgumentException => Outcome.Malformed,
        IOException => Outcome.Refused,
        _ => null,
    };

    /// <returns>False only for the one operation that answers rather than acts, answering no.</returns>
    private static bool Perform(Dir root, Operation operation, string path, Observation observation)
    {
        switch (operation)
        {
            case Operation.OpenFile:
                Read(root, path, observation);
                break;

            case Operation.OpenDir:
                List(root, path, noFollow: false, observation);
                break;

            case Operation.CreateFile:
                using (CapFile file = root.CreateFile(path))
                {
                    observation.Objects.Add(file.GetMetadata().FileId);
                    file.Write("written through the sandbox"u8, 0);
                }

                break;

            case Operation.CreateDir:
                using (Dir created = root.CreateDir(path))
                {
                    observation.Objects.Add(created.GetMetadata().FileId);
                }

                break;

            case Operation.GetMetadata:
                observation.Objects.Add(root.GetMetadata(path).FileId);
                break;

            case Operation.SetTimes:
                root.SetTimes(path, lastWrite: CapFileTime.At(EscapeCorpus.PlantedTime));
                observation.Objects.Add(root.GetMetadata(path).FileId);
                break;

            case Operation.Exists:
                return root.Exists(path);

            case Operation.ReadLink:
                // What a link stores is data inside the sandbox, whatever it spells; reading it
                // reveals nothing about what it names.
                _ = root.ReadLink(path);
                break;

            case Operation.DeleteFile:
                root.DeleteFile(path);
                break;

            case Operation.DeleteDir:
                root.DeleteDir(path);
                break;

            case Operation.DeleteTree:
                root.DeleteTree(path);
                break;

            case Operation.RenameFrom:
                root.Rename(path, root, EscapeCorpus.LandingName);
                break;

            case Operation.RenameTo:
                root.Rename(EscapeCorpus.SourceFile, root, path);
                break;

            case Operation.CreateSymlinkAt:
                root.CreateSymlink(path, EscapeCorpus.CreatedLinkTarget);
                break;

            case Operation.CreateSymlinkTo:
                root.CreateSymlink(EscapeCorpus.CreatedLinkName, path);
                observation.LinkCreated = true;
                Read(root, EscapeCorpus.CreatedLinkName, observation);
                break;

            case Operation.HardLinkFrom:
                root.CreateHardLink(path, root, EscapeCorpus.LandingName);
                observation.Objects.Add(root.GetMetadata(EscapeCorpus.LandingName).FileId);
                break;

            case Operation.HardLinkTo:
                root.CreateHardLink(EscapeCorpus.SourceFile, root, path);
                break;

            case Operation.OpenFileNoFollow:
                Read(root, path, observation, noFollow: true);
                break;

            case Operation.OpenDirNoFollow:
                List(root, path, noFollow: true, observation);
                break;

            case Operation.GetMetadataFollowing:
                observation.Objects.Add(root.GetMetadata(path, followLink: true).FileId);
                break;

            case Operation.SetTimesFollowing:
                root.SetTimes(path, lastWrite: CapFileTime.At(EscapeCorpus.PlantedTime), followLink: true);
                observation.Objects.Add(root.GetMetadata(path, followLink: true).FileId);
                break;

            case Operation.HardLinkFromFollowing:
                root.CreateHardLink(path, root, EscapeCorpus.LandingName, followLink: true);
                observation.Objects.Add(root.GetMetadata(EscapeCorpus.LandingName).FileId);
                break;

            case Operation.OpenAny:
                OpenAny(root, path, noFollow: false, observation);
                break;

            case Operation.OpenAnyNoFollow:
                OpenAny(root, path, noFollow: true, observation);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.");
        }

        return true;
    }

    private static void List(Dir root, string path, bool noFollow, Observation observation)
    {
        using Dir opened = root.OpenDir(path, noFollow);
        observation.Objects.Add(opened.GetMetadata().FileId);
        foreach (DirEntry entry in opened.EnumerateEntries())
        {
            observation.Names.Add(entry.Name);
        }
    }

    private static void OpenAny(Dir root, string path, bool noFollow, Observation observation)
    {
        using CapOpened opened = root.OpenAny(path, noFollow: noFollow);
        if (opened.IsDirectory)
        {
            using Dir directory = opened.TakeDir();
            observation.Objects.Add(directory.GetMetadata().FileId);
            foreach (DirEntry entry in directory.EnumerateEntries())
            {
                observation.Names.Add(entry.Name);
            }
        }
        else
        {
            using CapFile file = opened.TakeFile();
            ReadFrom(file, observation);
        }
    }

    private static void Read(Dir root, string path, Observation observation, bool noFollow = false)
    {
        using CapFile file = root.OpenFile(path, noFollow: noFollow);
        ReadFrom(file, observation);
    }

    private static void ReadFrom(CapFile file, Observation observation)
    {
        observation.Objects.Add(file.GetMetadata().FileId);

        byte[] buffer = new byte[ReadLimit];
        int read = file.Read(buffer, 0);
        observation.Contents.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
    }
}
