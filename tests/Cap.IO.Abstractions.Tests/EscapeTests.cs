using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using Cap.Std;
using Cap.Std.Testing;
using Cap.Tests;

namespace Cap.IO.Abstractions.Tests;

/// <summary>What an entry point does with a path that would leave the root.</summary>
public enum Outcome
{
    /// <summary>Refuses it with <see cref="SandboxEscapeException"/>.</summary>
    Refused,

    /// <summary>Answers false, as an existence check does for anything it cannot reach.</summary>
    False,

    /// <summary>Throws <see cref="NotSupportedException"/> before looking at the path.</summary>
    Unsupported,

    /// <summary>Only works on the text, and reaches nothing.</summary>
    Lexical,

    /// <summary>
    /// Stores the path as a link target, beneath the root at <c>link</c>, or refuses it with
    /// <see cref="SandboxEscapeException"/> when it is rooted; either way, following the link
    /// is refused.
    /// </summary>
    Stored,

    /// <summary>
    /// Answers what <c>System.IO</c> answers for a path that names nothing; the call returns
    /// whether it did.
    /// </summary>
    Absent,
}

/// <summary>Whether an entry point's path names a file or a directory beyond the way out.</summary>
public enum Names
{
    File,
    Directory,
}

/// <summary>One path parameter of one member, and how to call the member with a path there.</summary>
public sealed record EntryPoint(string Key, Names Names, Outcome Outcome, Func<IFileSystem, string, object?> Call)
{
    public override string ToString() => Key;
}

/// <summary>One member of an info, and how to call it on an info made over a path.</summary>
public sealed record ReceiverEntry(string Key, Outcome Outcome, Func<IFileSystemInfo, object?> Call)
{
    public override string ToString() => Key;
}

/// <summary>
/// A tree with a directory outside the root, reached three ways: by <c>..</c> above the root,
/// by the same spelled relative to the current directory, and through a symbolic link inside
/// the root whose target climbs out.
/// </summary>
public interface IEscapeFixture : IDisposable
{
    IFileSystem FileSystem { get; }

    bool SupportsLinks { get; }

    /// <summary>An absolute host path to the outside directory, where there is a host.</summary>
    string? HostOutside { get; }

    /// <summary>What is outside, for checking that nothing there changed.</summary>
    string Snapshot();
}

/// <summary>
/// That no path-taking entry point of the file system, its infos or its factories reaches
/// outside the root, that no member of an info made over a path outside does either, and that
/// scratch space is made beneath the root.
/// </summary>
/// <remarks>
/// The tables of entry points and of info members are checked against the interfaces by
/// reflection, so a member added in a later version of System.IO.Abstractions, or a path
/// parameter missed here, fails <see cref="Every_path_parameter_is_covered"/> or
/// <see cref="Every_info_member_is_covered"/> rather than going untested.
/// </remarks>
public abstract class EscapeTests : IDisposable
{
    private static readonly string[] PathParameterNames =
    [
        "path", "fileName", "linkPath", "sourceFileName", "destFileName", "destinationFileName",
        "destinationBackupFileName", "sourceDirName", "destDirName", "pathToTarget",
    ];

    private static readonly string[] Spellings = ["above-root", "relative", "through-link"];

    private readonly IEscapeFixture _fixture;
    private readonly string _before;

    protected EscapeTests(IEscapeFixture fixture)
    {
        _fixture = fixture;
        IFileSystem fs = fixture.FileSystem;
        fs.File.WriteAllText("a.txt", "inside");
        fs.File.WriteAllText("b.txt", "inside");
        fs.Directory.CreateDirectory("d");
        _before = fixture.Snapshot();
    }

    /// <summary>Each entry point, with each of the three ways out.</summary>
    public static TheoryData<string, string> Cases()
    {
        TheoryData<string, string> cases = [];
        foreach (EntryPoint entry in EntryPoints())
        {
            foreach (string spelling in Spellings)
            {
                cases.Add(entry.Key, spelling);
            }
        }

        return cases;
    }

    /// <summary>Each info member, with each of the three ways out for the info's own path.</summary>
    public static TheoryData<string, string> ReceiverCases()
    {
        TheoryData<string, string> cases = [];
        foreach (ReceiverEntry entry in ReceiverEntries())
        {
            foreach (string spelling in Spellings)
            {
                cases.Add(entry.Key, spelling);
            }
        }

        return cases;
    }

    /// <summary>Each entry point.</summary>
    public static TheoryData<string> Keys() => [.. EntryPoints().Select(entry => entry.Key)];

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait(Difference.Name, Difference.Reach)]
    public void Every_entry_point_stays_inside(string key, string spelling)
    {
        EntryPoint entry = EntryPoints().Single(entry => entry.Key == key);
        IFileSystem fs = _fixture.FileSystem;
        string path = Outside(spelling, entry.Names);

        AssertOutcome(fs, entry.Names, entry.Outcome, () => entry.Call(fs, path));
        Assert.Equal(_before, _fixture.Snapshot());
    }

    /// <summary>
    /// That an info made over a path outside reaches nothing there, whichever member is used:
    /// a member of <see cref="IFileSystemInfo"/> is called on both a file and a directory info.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReceiverCases))]
    public void Every_info_member_stays_inside(string key, string spelling)
    {
        ReceiverEntry entry = ReceiverEntries().Single(entry => entry.Key == key);
        IFileSystem fs = _fixture.FileSystem;
        foreach (Names names in new[] { Names.File, Names.Directory })
        {
            if ((names == Names.File && key.StartsWith(nameof(IDirectoryInfo), StringComparison.Ordinal))
                || (names == Names.Directory && key.StartsWith(nameof(IFileInfo), StringComparison.Ordinal)))
            {
                continue;
            }

            string path = Outside(spelling, names);
            IFileSystemInfo info = names == Names.File ? fs.FileInfo.New(path) : fs.DirectoryInfo.New(path);

            AssertOutcome(fs, names, entry.Outcome, () => entry.Call(info));
            Assert.Equal(_before, _fixture.Snapshot());
        }
    }

    [Fact]
    public void Every_path_parameter_is_covered()
    {
        HashSet<string> expected = [];
        Collect(typeof(IFile), expected);
        Collect(typeof(IDirectory), expected);
        Collect(typeof(IFileStreamFactory), expected);
        Collect(typeof(IFileSystemInfo), expected);
        Collect(typeof(IFileInfo), expected);
        Collect(typeof(IDirectoryInfo), expected);
        Collect(typeof(IFileInfoFactory), expected);
        Collect(typeof(IDirectoryInfoFactory), expected);
        Collect(typeof(IFileSystemWatcherFactory), expected);
        Collect(typeof(IFileVersionInfoFactory), expected);

        HashSet<string> covered = [.. EntryPoints().Select(entry => entry.Key)];

        Assert.Empty(expected.Except(covered).Order(StringComparer.Ordinal));
        Assert.Empty(covered.Except(expected).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_info_member_is_covered()
    {
        HashSet<string> expected = [];
        CollectMembers(typeof(IFileSystemInfo), expected);
        CollectMembers(typeof(IFileInfo), expected);
        CollectMembers(typeof(IDirectoryInfo), expected);

        HashSet<string> covered = [.. ReceiverEntries().Select(entry => entry.Key)];

        Assert.Empty(expected.Except(covered).Order(StringComparer.Ordinal));
        Assert.Empty(covered.Except(expected).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// That the scratch space <see cref="IPath.GetTempPath"/>, <see cref="IPath.GetTempFileName"/>
    /// and <see cref="IDirectory.CreateTempSubdirectory"/> make, which no path of the caller's
    /// names, is made beneath the root and nowhere else.
    /// </summary>
    [Fact]
    public void Scratch_space_is_made_beneath_the_root_only()
    {
        IFileSystem fs = _fixture.FileSystem;
        string root = fs.Path.GetPathRoot(fs.Directory.GetCurrentDirectory())!;

        string temp = fs.Path.GetTempPath();
#pragma warning disable CS0618 // The adapter's own GetTempFileName is what is under test.
        string file = fs.Path.GetTempFileName();
#pragma warning restore CS0618
        IDirectoryInfo directory = fs.Directory.CreateTempSubdirectory();

        Assert.Equal(_before, _fixture.Snapshot());
        Assert.StartsWith(root, temp, StringComparison.Ordinal);
        Assert.True(fs.Directory.Exists(temp));
        Assert.True(fs.File.Exists(file));
        Assert.StartsWith(temp, file, StringComparison.Ordinal);
        Assert.True(directory.Exists);
        Assert.StartsWith(temp, directory.FullName, StringComparison.Ordinal);
        Assert.Contains(fs.Path.GetFileName(fs.Path.TrimEndingDirectorySeparator(temp)), fs.Directory.GetDirectories(root).Select(fs.Path.GetFileName));
    }

    /// <summary>
    /// That the host's own absolute path to the outside directory reaches nothing there
    /// either: on Windows it is rooted on a drive and refused, and elsewhere it is a path in the
    /// virtual namespace, which names something beneath the root.
    /// </summary>
    protected void AssertHostPathNamesNothingOutside(string key)
    {
        EntryPoint entry = Find(key);
        IFileSystem fs = _fixture.FileSystem;
        string path = Path.Combine(_fixture.HostOutside!, entry.Names == Names.File ? "secret.txt" : "sub");

        try
        {
            object? result = entry.Call(fs, path);
            Drain(result);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // On Windows a host path is rooted on a drive and refused as an escape; elsewhere it
            // is a path in the virtual namespace and names something beneath the root, which
            // usually is not there. Either way what matters is below.
        }

        Assert.Equal(_before, _fixture.Snapshot());
        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsAny<SandboxEscapeException>(() => fs.File.ReadAllText(Path.Combine(_fixture.HostOutside!, "secret.txt")));
        }
    }

    private static void Collect([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type, HashSet<string> keys)
    {
        foreach (MethodInfo method in type.GetMethods())
        {
            ParameterInfo[] parameters = method.GetParameters();
            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.ParameterType == typeof(string) && PathParameterNames.Contains(parameter.Name))
                {
                    keys.Add(Key(type, method.Name, parameters, parameter.Name!));
                }
            }
        }
    }

    private static void CollectMembers([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type, HashSet<string> keys)
    {
        foreach (MethodInfo method in type.GetMethods())
        {
            keys.Add(Key(type, method.Name, method.GetParameters(), "receiver"));
        }
    }

    private static EntryPoint Find(string key) => EntryPoints().Single(entry => entry.Key == key);

    private static string Key(Type type, string method, ParameterInfo[] parameters, string parameter) =>
        $"{type.Name}.{method}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))}) {parameter}";

    private static void AssertOutcome(IFileSystem fs, Names names, Outcome outcome, Func<object?> call)
    {
        switch (outcome)
        {
            case Outcome.Refused:
                Assert.ThrowsAny<SandboxEscapeException>(() => Drain(call()));
                break;
            case Outcome.False:
                Assert.Equal(false, call());
                break;
            case Outcome.Unsupported:
                Assert.Throws<NotSupportedException>(() => Drain(call()));
                break;
            case Outcome.Lexical:
                Drain(call());
                break;
            case Outcome.Stored:
                try
                {
                    Drain(call());
                }
                catch (SandboxEscapeException)
                {
                    Assert.False(fs.File.Exists("link") || fs.Directory.Exists("link"));
                    break;
                }

                Assert.ThrowsAny<SandboxEscapeException>(() => Drain(names == Names.File
                    ? fs.File.ReadAllText("link")
                    : fs.Directory.GetFileSystemEntries("link")));
                break;
            case Outcome.Absent:
                Assert.Equal(true, call());
                break;
        }
    }

    /// <summary>The path to the file or directory outside, spelled the given way.</summary>
    private string Outside(string spelling, Names names)
    {
        IFileSystem fs = _fixture.FileSystem;
        if (spelling == "through-link")
        {
            TestLinks.Require(_fixture.SupportsLinks);
        }

        string outside = spelling switch
        {
            "above-root" => fs.Path.Combine(fs.Path.GetPathRoot(fs.Directory.GetCurrentDirectory())!, "..", "outside"),
            "relative" => fs.Path.Combine("..", "outside"),
            _ => fs.Path.Combine("escape"),
        };
        return fs.Path.Combine(outside, names == Names.File ? "secret.txt" : "sub");
    }

    /// <summary>Waits for a task, runs an enumeration and disposes a stream, so that a lazy result does its work.</summary>
    private static void Drain(object? result)
    {
        switch (result)
        {
            case Task task:
                task.GetAwaiter().GetResult();
                break;
            case IAsyncEnumerable<string> lines:
                IAsyncEnumerator<string> enumerator = lines.GetAsyncEnumerator();
                try
                {
                    while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                    }
                }
                finally
                {
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                break;
            case string:
                break;
            case IEnumerable sequence:
                foreach (object? _ in sequence)
                {
                }

                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>Every path parameter of every path-taking member, keyed as <see cref="Key"/> spells it.</summary>
    /// <remarks>
    /// Members the platform does not offer are called anyway: containment is decided before the
    /// platform has a say, and a member that is never called is a member nobody checked.
    /// </remarks>
#pragma warning disable CA1416
    public static IEnumerable<EntryPoint> EntryPoints()
    {
        const Names F = Names.File;
        const Names D = Names.Directory;
        const Outcome R = Outcome.Refused;
        CancellationToken none = CancellationToken.None;
        byte[] bytes = [1];
        string[] lines = ["x"];
        Encoding utf8 = Encoding.UTF8;
        DateTime when = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        EnumerationOptions options = new() { RecurseSubdirectories = true };
        FileStreamOptions createOptions = new() { Mode = FileMode.Create, Access = FileAccess.Write };

        static EntryPoint E(string key, Names names, Outcome outcome, Func<IFileSystem, string, object?> call) =>
            new(key, names, outcome, call);

        static Func<IFileSystem, string, object?> Do(Action<IFileSystem, string> action) =>
            (fs, p) =>
            {
                action(fs, p);
                return null;
            };

        // IFile
        yield return E("IFile.AppendAllBytes(String, Byte[]) path", F, R, Do((fs, p) => fs.File.AppendAllBytes(p, bytes)));
        yield return E("IFile.AppendAllBytes(String, ReadOnlySpan`1) path", F, R, Do((fs, p) => fs.File.AppendAllBytes(p, new ReadOnlySpan<byte>(bytes))));
        yield return E("IFile.AppendAllBytesAsync(String, Byte[], CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllBytesAsync(p, bytes, none));
        yield return E("IFile.AppendAllBytesAsync(String, ReadOnlyMemory`1, CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllBytesAsync(p, new ReadOnlyMemory<byte>(bytes), none));
        yield return E("IFile.AppendAllLines(String, IEnumerable`1) path", F, R, Do((fs, p) => fs.File.AppendAllLines(p, lines)));
        yield return E("IFile.AppendAllLines(String, IEnumerable`1, Encoding) path", F, R, Do((fs, p) => fs.File.AppendAllLines(p, lines, utf8)));
        yield return E("IFile.AppendAllLinesAsync(String, IEnumerable`1, CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllLinesAsync(p, lines, none));
        yield return E("IFile.AppendAllLinesAsync(String, IEnumerable`1, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllLinesAsync(p, lines, utf8, none));
        yield return E("IFile.AppendAllText(String, String) path", F, R, Do((fs, p) => fs.File.AppendAllText(p, "x")));
        yield return E("IFile.AppendAllText(String, String, Encoding) path", F, R, Do((fs, p) => fs.File.AppendAllText(p, "x", utf8)));
        yield return E("IFile.AppendAllText(String, ReadOnlySpan`1) path", F, R, Do((fs, p) => fs.File.AppendAllText(p, "x".AsSpan())));
        yield return E("IFile.AppendAllText(String, ReadOnlySpan`1, Encoding) path", F, R, Do((fs, p) => fs.File.AppendAllText(p, "x".AsSpan(), utf8)));
        yield return E("IFile.AppendAllTextAsync(String, String, CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllTextAsync(p, "x", none));
        yield return E("IFile.AppendAllTextAsync(String, String, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllTextAsync(p, "x", utf8, none));
        yield return E("IFile.AppendAllTextAsync(String, ReadOnlyMemory`1, CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllTextAsync(p, "x".AsMemory(), none));
        yield return E("IFile.AppendAllTextAsync(String, ReadOnlyMemory`1, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.AppendAllTextAsync(p, "x".AsMemory(), utf8, none));
        yield return E("IFile.AppendText(String) path", F, R, (fs, p) => fs.File.AppendText(p));
        yield return E("IFile.Copy(String, String) sourceFileName", F, R, Do((fs, p) => fs.File.Copy(p, "copy.txt")));
        yield return E("IFile.Copy(String, String) destFileName", F, R, Do((fs, p) => fs.File.Copy("a.txt", p)));
        yield return E("IFile.Copy(String, String, Boolean) sourceFileName", F, R, Do((fs, p) => fs.File.Copy(p, "copy.txt", true)));
        yield return E("IFile.Copy(String, String, Boolean) destFileName", F, R, Do((fs, p) => fs.File.Copy("a.txt", p, true)));
        yield return E("IFile.Create(String) path", F, R, (fs, p) => fs.File.Create(p));
        yield return E("IFile.Create(String, Int32) path", F, R, (fs, p) => fs.File.Create(p, 4096));
        yield return E("IFile.Create(String, Int32, FileOptions) path", F, R, (fs, p) => fs.File.Create(p, 4096, FileOptions.None));
        yield return E("IFile.CreateSymbolicLink(String, String) path", F, R, (fs, p) => fs.File.CreateSymbolicLink(p, "a.txt"));
        yield return E("IFile.CreateSymbolicLink(String, String) pathToTarget", F, Outcome.Stored, (fs, p) => fs.File.CreateSymbolicLink("link", p));
        yield return E("IFile.CreateText(String) path", F, R, (fs, p) => fs.File.CreateText(p));
        yield return E("IFile.Decrypt(String) path", F, Outcome.Unsupported, Do((fs, p) => fs.File.Decrypt(p)));
        yield return E("IFile.Delete(String) path", F, R, Do((fs, p) => fs.File.Delete(p)));
        yield return E("IFile.Encrypt(String) path", F, Outcome.Unsupported, Do((fs, p) => fs.File.Encrypt(p)));
        yield return E("IFile.Exists(String) path", F, Outcome.False, (fs, p) => fs.File.Exists(p));
        yield return E("IFile.GetAttributes(String) path", F, R, (fs, p) => fs.File.GetAttributes(p));
        yield return E("IFile.GetCreationTime(String) path", F, R, (fs, p) => fs.File.GetCreationTime(p));
        yield return E("IFile.GetCreationTimeUtc(String) path", F, R, (fs, p) => fs.File.GetCreationTimeUtc(p));
        yield return E("IFile.GetLastAccessTime(String) path", F, R, (fs, p) => fs.File.GetLastAccessTime(p));
        yield return E("IFile.GetLastAccessTimeUtc(String) path", F, R, (fs, p) => fs.File.GetLastAccessTimeUtc(p));
        yield return E("IFile.GetLastWriteTime(String) path", F, R, (fs, p) => fs.File.GetLastWriteTime(p));
        yield return E("IFile.GetLastWriteTimeUtc(String) path", F, R, (fs, p) => fs.File.GetLastWriteTimeUtc(p));
        yield return E("IFile.GetUnixFileMode(String) path", F, R, (fs, p) => fs.File.GetUnixFileMode(p));
        yield return E("IFile.Move(String, String) sourceFileName", F, R, Do((fs, p) => fs.File.Move(p, "moved.txt")));
        yield return E("IFile.Move(String, String) destFileName", F, R, Do((fs, p) => fs.File.Move("a.txt", p)));
        yield return E("IFile.Move(String, String, Boolean) sourceFileName", F, R, Do((fs, p) => fs.File.Move(p, "moved.txt", true)));
        yield return E("IFile.Move(String, String, Boolean) destFileName", F, R, Do((fs, p) => fs.File.Move("a.txt", p, true)));
        yield return E("IFile.Open(String, FileMode) path", F, R, (fs, p) => fs.File.Open(p, FileMode.OpenOrCreate));
        yield return E("IFile.Open(String, FileMode, FileAccess) path", F, R, (fs, p) => fs.File.Open(p, FileMode.OpenOrCreate, FileAccess.ReadWrite));
        yield return E("IFile.Open(String, FileMode, FileAccess, FileShare) path", F, R, (fs, p) => fs.File.Open(p, FileMode.Open, FileAccess.Read, FileShare.Read));
        yield return E("IFile.Open(String, FileStreamOptions) path", F, R, (fs, p) => fs.File.Open(p, createOptions));
        yield return E("IFile.OpenHandle(String, FileMode, FileAccess, FileShare, FileOptions, Int64) path", F, Outcome.Unsupported, (fs, p) => fs.File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0));
        yield return E("IFile.OpenRead(String) path", F, R, (fs, p) => fs.File.OpenRead(p));
        yield return E("IFile.OpenText(String) path", F, R, (fs, p) => fs.File.OpenText(p));
        yield return E("IFile.OpenWrite(String) path", F, R, (fs, p) => fs.File.OpenWrite(p));
        yield return E("IFile.ReadAllBytes(String) path", F, R, (fs, p) => fs.File.ReadAllBytes(p));
        yield return E("IFile.ReadAllBytesAsync(String, CancellationToken) path", F, R, (fs, p) => fs.File.ReadAllBytesAsync(p, none));
        yield return E("IFile.ReadAllLines(String) path", F, R, (fs, p) => fs.File.ReadAllLines(p));
        yield return E("IFile.ReadAllLines(String, Encoding) path", F, R, (fs, p) => fs.File.ReadAllLines(p, utf8));
        yield return E("IFile.ReadAllLinesAsync(String, CancellationToken) path", F, R, (fs, p) => fs.File.ReadAllLinesAsync(p, none));
        yield return E("IFile.ReadAllLinesAsync(String, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.ReadAllLinesAsync(p, utf8, none));
        yield return E("IFile.ReadAllText(String) path", F, R, (fs, p) => fs.File.ReadAllText(p));
        yield return E("IFile.ReadAllText(String, Encoding) path", F, R, (fs, p) => fs.File.ReadAllText(p, utf8));
        yield return E("IFile.ReadAllTextAsync(String, CancellationToken) path", F, R, (fs, p) => fs.File.ReadAllTextAsync(p, none));
        yield return E("IFile.ReadAllTextAsync(String, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.ReadAllTextAsync(p, utf8, none));
        yield return E("IFile.ReadLines(String) path", F, R, (fs, p) => fs.File.ReadLines(p));
        yield return E("IFile.ReadLines(String, Encoding) path", F, R, (fs, p) => fs.File.ReadLines(p, utf8));
        yield return E("IFile.ReadLinesAsync(String, CancellationToken) path", F, R, (fs, p) => fs.File.ReadLinesAsync(p, none));
        yield return E("IFile.ReadLinesAsync(String, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.ReadLinesAsync(p, utf8, none));
        yield return E("IFile.Replace(String, String, String) sourceFileName", F, R, Do((fs, p) => fs.File.Replace(p, "b.txt", "backup.txt")));
        yield return E("IFile.Replace(String, String, String) destinationFileName", F, R, Do((fs, p) => fs.File.Replace("a.txt", p, "backup.txt")));
        yield return E("IFile.Replace(String, String, String) destinationBackupFileName", F, R, Do((fs, p) => fs.File.Replace("a.txt", "b.txt", p)));
        yield return E("IFile.Replace(String, String, String, Boolean) sourceFileName", F, R, Do((fs, p) => fs.File.Replace(p, "b.txt", "backup.txt", false)));
        yield return E("IFile.Replace(String, String, String, Boolean) destinationFileName", F, R, Do((fs, p) => fs.File.Replace("a.txt", p, "backup.txt", false)));
        yield return E("IFile.Replace(String, String, String, Boolean) destinationBackupFileName", F, R, Do((fs, p) => fs.File.Replace("a.txt", "b.txt", p, false)));
        yield return E("IFile.ResolveLinkTarget(String, Boolean) linkPath", F, R, (fs, p) => fs.File.ResolveLinkTarget(p, true));
        yield return E("IFile.SetAttributes(String, FileAttributes) path", F, Outcome.Unsupported, Do((fs, p) => fs.File.SetAttributes(p, FileAttributes.Normal)));
        yield return E("IFile.SetCreationTime(String, DateTime) path", F, Outcome.Unsupported, Do((fs, p) => fs.File.SetCreationTime(p, when)));
        yield return E("IFile.SetCreationTimeUtc(String, DateTime) path", F, Outcome.Unsupported, Do((fs, p) => fs.File.SetCreationTimeUtc(p, when)));
        yield return E("IFile.SetLastAccessTime(String, DateTime) path", F, R, Do((fs, p) => fs.File.SetLastAccessTime(p, when)));
        yield return E("IFile.SetLastAccessTimeUtc(String, DateTime) path", F, R, Do((fs, p) => fs.File.SetLastAccessTimeUtc(p, when)));
        yield return E("IFile.SetLastWriteTime(String, DateTime) path", F, R, Do((fs, p) => fs.File.SetLastWriteTime(p, when)));
        yield return E("IFile.SetLastWriteTimeUtc(String, DateTime) path", F, R, Do((fs, p) => fs.File.SetLastWriteTimeUtc(p, when)));
        yield return E("IFile.SetUnixFileMode(String, UnixFileMode) path", F, Outcome.Unsupported, Do((fs, p) => fs.File.SetUnixFileMode(p, UnixFileMode.UserRead)));
        yield return E("IFile.WriteAllBytes(String, Byte[]) path", F, R, Do((fs, p) => fs.File.WriteAllBytes(p, bytes)));
        yield return E("IFile.WriteAllBytes(String, ReadOnlySpan`1) path", F, R, Do((fs, p) => fs.File.WriteAllBytes(p, new ReadOnlySpan<byte>(bytes))));
        yield return E("IFile.WriteAllBytesAsync(String, Byte[], CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllBytesAsync(p, bytes, none));
        yield return E("IFile.WriteAllBytesAsync(String, ReadOnlyMemory`1, CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllBytesAsync(p, new ReadOnlyMemory<byte>(bytes), none));
        yield return E("IFile.WriteAllLines(String, String[]) path", F, R, Do((fs, p) => fs.File.WriteAllLines(p, lines)));
        yield return E("IFile.WriteAllLines(String, IEnumerable`1) path", F, R, Do((fs, p) => fs.File.WriteAllLines(p, lines.AsEnumerable())));
        yield return E("IFile.WriteAllLines(String, String[], Encoding) path", F, R, Do((fs, p) => fs.File.WriteAllLines(p, lines, utf8)));
        yield return E("IFile.WriteAllLines(String, IEnumerable`1, Encoding) path", F, R, Do((fs, p) => fs.File.WriteAllLines(p, lines.AsEnumerable(), utf8)));
        yield return E("IFile.WriteAllLinesAsync(String, IEnumerable`1, CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllLinesAsync(p, lines, none));
        yield return E("IFile.WriteAllLinesAsync(String, IEnumerable`1, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllLinesAsync(p, lines, utf8, none));
        yield return E("IFile.WriteAllText(String, String) path", F, R, Do((fs, p) => fs.File.WriteAllText(p, "x")));
        yield return E("IFile.WriteAllText(String, String, Encoding) path", F, R, Do((fs, p) => fs.File.WriteAllText(p, "x", utf8)));
        yield return E("IFile.WriteAllText(String, ReadOnlySpan`1) path", F, R, Do((fs, p) => fs.File.WriteAllText(p, "x".AsSpan())));
        yield return E("IFile.WriteAllText(String, ReadOnlySpan`1, Encoding) path", F, R, Do((fs, p) => fs.File.WriteAllText(p, "x".AsSpan(), utf8)));
        yield return E("IFile.WriteAllTextAsync(String, String, CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllTextAsync(p, "x", none));
        yield return E("IFile.WriteAllTextAsync(String, String, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllTextAsync(p, "x", utf8, none));
        yield return E("IFile.WriteAllTextAsync(String, ReadOnlyMemory`1, CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllTextAsync(p, "x".AsMemory(), none));
        yield return E("IFile.WriteAllTextAsync(String, ReadOnlyMemory`1, Encoding, CancellationToken) path", F, R, (fs, p) => fs.File.WriteAllTextAsync(p, "x".AsMemory(), utf8, none));

        // IDirectory
        yield return E("IDirectory.CreateDirectory(String) path", D, R, (fs, p) => fs.Directory.CreateDirectory(fs.Path.Combine(p, "new")));
        yield return E("IDirectory.CreateDirectory(String, UnixFileMode) path", D, Outcome.Unsupported, (fs, p) => fs.Directory.CreateDirectory(p, UnixFileMode.UserRead));
        yield return E("IDirectory.CreateSymbolicLink(String, String) path", D, R, (fs, p) => fs.Directory.CreateSymbolicLink(fs.Path.Combine(p, "new"), "d"));
        yield return E("IDirectory.CreateSymbolicLink(String, String) pathToTarget", D, Outcome.Stored, (fs, p) => fs.Directory.CreateSymbolicLink("link", p));
        yield return E("IDirectory.Delete(String) path", D, R, Do((fs, p) => fs.Directory.Delete(p)));
        yield return E("IDirectory.Delete(String, Boolean) path", D, R, Do((fs, p) => fs.Directory.Delete(p, true)));
        foreach (string kind in new[] { "Directories", "Files", "FileSystemEntries" })
        {
            foreach (string verb in new[] { "Enumerate", "Get" })
            {
                string name = verb + kind;
                yield return E($"IDirectory.{name}(String) path", D, R, (fs, p) => Enumerate(fs, name, p, null, null));
                yield return E($"IDirectory.{name}(String, String) path", D, R, (fs, p) => Enumerate(fs, name, p, "*", null));
                yield return E($"IDirectory.{name}(String, String, SearchOption) path", D, R, (fs, p) => Enumerate(fs, name, p, "*", SearchOption.AllDirectories));
                yield return E($"IDirectory.{name}(String, String, EnumerationOptions) path", D, R, (fs, p) => Enumerate(fs, name, p, "*", options));
            }
        }

        yield return E("IDirectory.Exists(String) path", D, Outcome.False, (fs, p) => fs.Directory.Exists(p));
        yield return E("IDirectory.GetCreationTime(String) path", D, R, (fs, p) => fs.Directory.GetCreationTime(p));
        yield return E("IDirectory.GetCreationTimeUtc(String) path", D, R, (fs, p) => fs.Directory.GetCreationTimeUtc(p));
        yield return E("IDirectory.GetDirectoryRoot(String) path", D, Outcome.Lexical, (fs, p) => fs.Directory.GetDirectoryRoot(p));
        yield return E("IDirectory.GetLastAccessTime(String) path", D, R, (fs, p) => fs.Directory.GetLastAccessTime(p));
        yield return E("IDirectory.GetLastAccessTimeUtc(String) path", D, R, (fs, p) => fs.Directory.GetLastAccessTimeUtc(p));
        yield return E("IDirectory.GetLastWriteTime(String) path", D, R, (fs, p) => fs.Directory.GetLastWriteTime(p));
        yield return E("IDirectory.GetLastWriteTimeUtc(String) path", D, R, (fs, p) => fs.Directory.GetLastWriteTimeUtc(p));
        yield return E("IDirectory.GetParent(String) path", D, Outcome.Lexical, (fs, p) => fs.Directory.GetParent(p));
        yield return E("IDirectory.Move(String, String) sourceDirName", D, R, Do((fs, p) => fs.Directory.Move(p, "moved")));
        yield return E("IDirectory.Move(String, String) destDirName", D, R, Do((fs, p) => fs.Directory.Move("d", fs.Path.Combine(p, "moved"))));
        yield return E("IDirectory.ResolveLinkTarget(String, Boolean) linkPath", D, R, (fs, p) => fs.Directory.ResolveLinkTarget(p, true));
        yield return E("IDirectory.SetCreationTime(String, DateTime) path", D, Outcome.Unsupported, Do((fs, p) => fs.Directory.SetCreationTime(p, when)));
        yield return E("IDirectory.SetCreationTimeUtc(String, DateTime) path", D, Outcome.Unsupported, Do((fs, p) => fs.Directory.SetCreationTimeUtc(p, when)));
        yield return E("IDirectory.SetCurrentDirectory(String) path", D, R, Do((fs, p) => fs.Directory.SetCurrentDirectory(p)));
        yield return E("IDirectory.SetLastAccessTime(String, DateTime) path", D, R, Do((fs, p) => fs.Directory.SetLastAccessTime(p, when)));
        yield return E("IDirectory.SetLastAccessTimeUtc(String, DateTime) path", D, R, Do((fs, p) => fs.Directory.SetLastAccessTimeUtc(p, when)));
        yield return E("IDirectory.SetLastWriteTime(String, DateTime) path", D, R, Do((fs, p) => fs.Directory.SetLastWriteTime(p, when)));
        yield return E("IDirectory.SetLastWriteTimeUtc(String, DateTime) path", D, R, Do((fs, p) => fs.Directory.SetLastWriteTimeUtc(p, when)));

        // IFileStreamFactory
        yield return E("IFileStreamFactory.New(String, FileMode) path", F, R, (fs, p) => fs.FileStream.New(p, FileMode.OpenOrCreate));
        yield return E("IFileStreamFactory.New(String, FileMode, FileAccess) path", F, R, (fs, p) => fs.FileStream.New(p, FileMode.Open, FileAccess.Read));
        yield return E("IFileStreamFactory.New(String, FileMode, FileAccess, FileShare) path", F, R, (fs, p) => fs.FileStream.New(p, FileMode.Create, FileAccess.Write, FileShare.None));
        yield return E("IFileStreamFactory.New(String, FileMode, FileAccess, FileShare, Int32) path", F, R, (fs, p) => fs.FileStream.New(p, FileMode.Append, FileAccess.Write, FileShare.None, 4096));
        yield return E("IFileStreamFactory.New(String, FileMode, FileAccess, FileShare, Int32, Boolean) path", F, R, (fs, p) => fs.FileStream.New(p, FileMode.Truncate, FileAccess.Write, FileShare.None, 4096, true));
        yield return E("IFileStreamFactory.New(String, FileMode, FileAccess, FileShare, Int32, FileOptions) path", F, R, (fs, p) => fs.FileStream.New(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None));
        yield return E("IFileStreamFactory.New(String, FileStreamOptions) path", F, R, (fs, p) => fs.FileStream.New(p, createOptions));

        // IFileSystemInfo, IFileInfo and IDirectoryInfo, over a path inside and given one outside
        yield return E("IFileSystemInfo.CreateAsSymbolicLink(String) pathToTarget", F, Outcome.Stored, Do((fs, p) => fs.FileInfo.New("link").CreateAsSymbolicLink(p)));
        yield return E("IFileInfo.CopyTo(String) destFileName", F, R, (fs, p) => fs.FileInfo.New("a.txt").CopyTo(p));
        yield return E("IFileInfo.CopyTo(String, Boolean) destFileName", F, R, (fs, p) => fs.FileInfo.New("a.txt").CopyTo(p, true));
        yield return E("IFileInfo.MoveTo(String) destFileName", F, R, Do((fs, p) => fs.FileInfo.New("a.txt").MoveTo(p)));
        yield return E("IFileInfo.MoveTo(String, Boolean) destFileName", F, R, Do((fs, p) => fs.FileInfo.New("a.txt").MoveTo(p, true)));
        yield return E("IFileInfo.Replace(String, String) destinationFileName", F, R, (fs, p) => fs.FileInfo.New("a.txt").Replace(p, "backup.txt"));
        yield return E("IFileInfo.Replace(String, String) destinationBackupFileName", F, R, (fs, p) => fs.FileInfo.New("a.txt").Replace("b.txt", p));
        yield return E("IFileInfo.Replace(String, String, Boolean) destinationFileName", F, R, (fs, p) => fs.FileInfo.New("a.txt").Replace(p, "backup.txt", false));
        yield return E("IFileInfo.Replace(String, String, Boolean) destinationBackupFileName", F, R, (fs, p) => fs.FileInfo.New("a.txt").Replace("b.txt", p, false));

        // CreateSubdirectory refuses a rooted path before looking at it, so the way out is taken
        // from the directory's parent: every spelling then climbs above the root or through the link.
        yield return E("IDirectoryInfo.CreateSubdirectory(String) path", D, R, (fs, p) => fs.DirectoryInfo.New("d").CreateSubdirectory(fs.Path.Join("..", p, "new")));
        yield return E("IDirectoryInfo.MoveTo(String) destDirName", D, R, Do((fs, p) => fs.DirectoryInfo.New("d").MoveTo(fs.Path.Combine(p, "moved"))));

        // The factories: an info over a path outside finds nothing there. What each of its
        // members does is the business of ReceiverEntries.
        yield return E("IFileInfoFactory.New(String) fileName", F, Outcome.False, (fs, p) => fs.FileInfo.New(p).Exists);
        yield return E("IDirectoryInfoFactory.New(String) path", D, Outcome.False, (fs, p) => fs.DirectoryInfo.New(p).Exists);
        yield return E("IFileSystemWatcherFactory.New(String) path", D, Outcome.Unsupported, (fs, p) => fs.FileSystemWatcher.New(p));
        yield return E("IFileSystemWatcherFactory.New(String, String) path", D, Outcome.Unsupported, (fs, p) => fs.FileSystemWatcher.New(p, "*"));
        yield return E("IFileVersionInfoFactory.GetVersionInfo(String) fileName", F, Outcome.Unsupported, (fs, p) => fs.FileVersionInfo.GetVersionInfo(p));
    }

    /// <summary>
    /// Every member of <see cref="IFileSystemInfo"/>, <see cref="IFileInfo"/> and
    /// <see cref="IDirectoryInfo"/>, property accessors included, keyed as <see cref="Key"/>
    /// spells it with <c>receiver</c> for the parameter, for calling on an info made over a
    /// path outside the root.
    /// </summary>
    /// <remarks>
    /// An info's getters describe what the path names as <c>System.IO</c>'s do, never throwing:
    /// what is outside is described as nothing, so they are <see cref="Outcome.Absent"/>.
    /// A getter that hands back another info is checked through that info's <c>Exists</c>.
    /// </remarks>
    public static IEnumerable<ReceiverEntry> ReceiverEntries()
    {
        const Outcome R = Outcome.Refused;
        const Outcome A = Outcome.Absent;
        const Outcome L = Outcome.Lexical;
        const Outcome U = Outcome.Unsupported;
        DateTime when = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        FileStreamOptions createOptions = new() { Mode = FileMode.Create, Access = FileAccess.Write };

        static ReceiverEntry E(string key, Outcome outcome, Func<IFileSystemInfo, object?> call) =>
            new($"{key} receiver", outcome, call);

        static Func<IFileSystemInfo, object?> Do(Action<IFileSystemInfo> action) =>
            info =>
            {
                action(info);
                return null;
            };

        static IFileInfo File(IFileSystemInfo info) => (IFileInfo)info;

        static IDirectoryInfo Directory(IFileSystemInfo info) => (IDirectoryInfo)info;

        static IFileSystemInfo Missing(IFileSystemInfo info) =>
            info is IFileInfo ? info.FileSystem.FileInfo.New("missing") : info.FileSystem.DirectoryInfo.New("missing");

        // IFileSystemInfo
        yield return E("IFileSystemInfo.CreateAsSymbolicLink(String)", R, Do(info => info.CreateAsSymbolicLink(info is IFileInfo ? "a.txt" : "d")));
        yield return E("IFileSystemInfo.Delete()", R, Do(info => info.Delete()));
        yield return E("IFileSystemInfo.Refresh()", L, Do(info => info.Refresh()));
        yield return E("IFileSystemInfo.ResolveLinkTarget(Boolean)", R, info => info.ResolveLinkTarget(true));
        yield return E("IFileSystemInfo.get_Attributes()", A, info => info.Attributes == Missing(info).Attributes);
        yield return E("IFileSystemInfo.set_Attributes(FileAttributes)", U, Do(info => info.Attributes = FileAttributes.Normal));
        yield return E("IFileSystemInfo.get_CreationTime()", A, info => info.CreationTime == Missing(info).CreationTime);
        yield return E("IFileSystemInfo.set_CreationTime(DateTime)", U, Do(info => info.CreationTime = when));
        yield return E("IFileSystemInfo.get_CreationTimeUtc()", A, info => info.CreationTimeUtc == Missing(info).CreationTimeUtc);
        yield return E("IFileSystemInfo.set_CreationTimeUtc(DateTime)", U, Do(info => info.CreationTimeUtc = when));
        yield return E("IFileSystemInfo.get_Exists()", Outcome.False, info => info.Exists);
        yield return E("IFileSystemInfo.get_Extension()", L, info => info.Extension);
        yield return E("IFileSystemInfo.get_FileSystem()", L, info => info.FileSystem);
        yield return E("IFileSystemInfo.get_FullName()", L, info => info.FullName);
        yield return E("IFileSystemInfo.get_LastAccessTime()", A, info => info.LastAccessTime == Missing(info).LastAccessTime);
        yield return E("IFileSystemInfo.set_LastAccessTime(DateTime)", R, Do(info => info.LastAccessTime = when));
        yield return E("IFileSystemInfo.get_LastAccessTimeUtc()", A, info => info.LastAccessTimeUtc == Missing(info).LastAccessTimeUtc);
        yield return E("IFileSystemInfo.set_LastAccessTimeUtc(DateTime)", R, Do(info => info.LastAccessTimeUtc = when));
        yield return E("IFileSystemInfo.get_LastWriteTime()", A, info => info.LastWriteTime == Missing(info).LastWriteTime);
        yield return E("IFileSystemInfo.set_LastWriteTime(DateTime)", R, Do(info => info.LastWriteTime = when));
        yield return E("IFileSystemInfo.get_LastWriteTimeUtc()", A, info => info.LastWriteTimeUtc == Missing(info).LastWriteTimeUtc);
        yield return E("IFileSystemInfo.set_LastWriteTimeUtc(DateTime)", R, Do(info => info.LastWriteTimeUtc = when));
        yield return E("IFileSystemInfo.get_LinkTarget()", A, info => info.LinkTarget is null);
        yield return E("IFileSystemInfo.get_Name()", L, info => info.Name);
        yield return E("IFileSystemInfo.get_UnixFileMode()", A, info => info.UnixFileMode == Missing(info).UnixFileMode);
        yield return E("IFileSystemInfo.set_UnixFileMode(UnixFileMode)", U, Do(info => info.UnixFileMode = UnixFileMode.UserRead));

        // IFileInfo
        yield return E("IFileInfo.AppendText()", R, info => File(info).AppendText());
        yield return E("IFileInfo.CopyTo(String)", R, info => File(info).CopyTo("copy.txt"));
        yield return E("IFileInfo.CopyTo(String, Boolean)", R, info => File(info).CopyTo("copy.txt", true));
        yield return E("IFileInfo.Create()", R, info => File(info).Create());
        yield return E("IFileInfo.CreateText()", R, info => File(info).CreateText());
        yield return E("IFileInfo.Decrypt()", U, Do(info => File(info).Decrypt()));
        yield return E("IFileInfo.Encrypt()", U, Do(info => File(info).Encrypt()));
        yield return E("IFileInfo.MoveTo(String)", R, Do(info => File(info).MoveTo("moved.txt")));
        yield return E("IFileInfo.MoveTo(String, Boolean)", R, Do(info => File(info).MoveTo("moved.txt", true)));
        yield return E("IFileInfo.Open(FileMode)", R, info => File(info).Open(FileMode.OpenOrCreate));
        yield return E("IFileInfo.Open(FileMode, FileAccess)", R, info => File(info).Open(FileMode.OpenOrCreate, FileAccess.ReadWrite));
        yield return E("IFileInfo.Open(FileMode, FileAccess, FileShare)", R, info => File(info).Open(FileMode.Open, FileAccess.Read, FileShare.Read));
        yield return E("IFileInfo.Open(FileStreamOptions)", R, info => File(info).Open(createOptions));
        yield return E("IFileInfo.OpenRead()", R, info => File(info).OpenRead());
        yield return E("IFileInfo.OpenText()", R, info => File(info).OpenText());
        yield return E("IFileInfo.OpenWrite()", R, info => File(info).OpenWrite());
        yield return E("IFileInfo.Replace(String, String)", R, info => File(info).Replace("b.txt", null));
        yield return E("IFileInfo.Replace(String, String, Boolean)", R, info => File(info).Replace("b.txt", "backup.txt", false));
        yield return E("IFileInfo.get_Directory()", Outcome.False, info => File(info).Directory!.Exists);
        yield return E("IFileInfo.get_DirectoryName()", L, info => File(info).DirectoryName);
        yield return E("IFileInfo.get_IsReadOnly()", A, info => File(info).IsReadOnly == ((IFileInfo)Missing(info)).IsReadOnly);
        yield return E("IFileInfo.set_IsReadOnly(Boolean)", U, Do(info => File(info).IsReadOnly = false));
        yield return E("IFileInfo.get_Length()", A, info =>
        {
            try
            {
                _ = File(info).Length;
                return false;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
        });

        // IDirectoryInfo
        yield return E("IDirectoryInfo.Create()", R, Do(info => Directory(info).Create()));
        yield return E("IDirectoryInfo.CreateSubdirectory(String)", R, info => Directory(info).CreateSubdirectory("new"));
        yield return E("IDirectoryInfo.Delete(Boolean)", R, Do(info => Directory(info).Delete(true)));
        yield return E("IDirectoryInfo.MoveTo(String)", R, Do(info => Directory(info).MoveTo("moved")));
        yield return E("IDirectoryInfo.get_Parent()", Outcome.False, info => Directory(info).Parent!.Exists);
        yield return E("IDirectoryInfo.get_Root()", L, info => Directory(info).Root);
        foreach (string kind in new[] { "Directories", "Files", "FileSystemInfos" })
        {
            foreach (string verb in new[] { "Enumerate", "Get" })
            {
                string name = verb + kind;
                yield return E($"IDirectoryInfo.{name}()", R, info => Infos(Directory(info), name));
                yield return E($"IDirectoryInfo.{name}(String)", R, info => Infos(Directory(info), name, "*"));
                yield return E($"IDirectoryInfo.{name}(String, SearchOption)", R, info => Infos(Directory(info), name, "*", SearchOption.AllDirectories));
                yield return E($"IDirectoryInfo.{name}(String, EnumerationOptions)", R, info => Infos(Directory(info), name, "*", new EnumerationOptions { RecurseSubdirectories = true }));
            }
        }
    }

#pragma warning restore CA1416

    /// <summary>Calls one of the enumerating members of a directory info by name.</summary>
    private static object? Infos(IDirectoryInfo directory, string member, params object[] arguments) =>
        typeof(IDirectoryInfo)
            .GetMethod(member, [.. arguments.Select(argument => argument.GetType())])!
            .Invoke(directory, BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null);

    private static object Enumerate(IFileSystem fs, string member, string path, string? pattern, object? option) =>
        (member, pattern, option) switch
        {
            ("EnumerateDirectories", null, _) => fs.Directory.EnumerateDirectories(path),
            ("EnumerateDirectories", _, null) => fs.Directory.EnumerateDirectories(path, pattern),
            ("EnumerateDirectories", _, SearchOption o) => fs.Directory.EnumerateDirectories(path, pattern, o),
            ("EnumerateDirectories", _, EnumerationOptions o) => fs.Directory.EnumerateDirectories(path, pattern, o),
            ("GetDirectories", null, _) => fs.Directory.GetDirectories(path),
            ("GetDirectories", _, null) => fs.Directory.GetDirectories(path, pattern),
            ("GetDirectories", _, SearchOption o) => fs.Directory.GetDirectories(path, pattern, o),
            ("GetDirectories", _, EnumerationOptions o) => fs.Directory.GetDirectories(path, pattern, o),
            ("EnumerateFiles", null, _) => fs.Directory.EnumerateFiles(path),
            ("EnumerateFiles", _, null) => fs.Directory.EnumerateFiles(path, pattern),
            ("EnumerateFiles", _, SearchOption o) => fs.Directory.EnumerateFiles(path, pattern, o),
            ("EnumerateFiles", _, EnumerationOptions o) => fs.Directory.EnumerateFiles(path, pattern, o),
            ("GetFiles", null, _) => fs.Directory.GetFiles(path),
            ("GetFiles", _, null) => fs.Directory.GetFiles(path, pattern),
            ("GetFiles", _, SearchOption o) => fs.Directory.GetFiles(path, pattern, o),
            ("GetFiles", _, EnumerationOptions o) => fs.Directory.GetFiles(path, pattern, o),
            ("EnumerateFileSystemEntries", null, _) => fs.Directory.EnumerateFileSystemEntries(path),
            ("EnumerateFileSystemEntries", _, null) => fs.Directory.EnumerateFileSystemEntries(path, pattern),
            ("EnumerateFileSystemEntries", _, SearchOption o) => fs.Directory.EnumerateFileSystemEntries(path, pattern, o),
            ("EnumerateFileSystemEntries", _, EnumerationOptions o) => fs.Directory.EnumerateFileSystemEntries(path, pattern, o),
            ("GetFileSystemEntries", null, _) => fs.Directory.GetFileSystemEntries(path),
            ("GetFileSystemEntries", _, null) => fs.Directory.GetFileSystemEntries(path, pattern),
            ("GetFileSystemEntries", _, SearchOption o) => fs.Directory.GetFileSystemEntries(path, pattern, o),
            ("GetFileSystemEntries", _, EnumerationOptions o) => fs.Directory.GetFileSystemEntries(path, pattern, o),
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };
}

/// <summary>The escape tree on disk, arranged through <c>System.IO</c> rather than the code under test.</summary>
public sealed class DiskEscapeFixture : IEscapeFixture
{
    private readonly ScratchTree _tree = new();
    private readonly Dir _inner;

    public DiskEscapeFixture()
    {
        string inner = Path.Combine(_tree.HostPath, "inner");
        HostOutside = Path.Combine(_tree.HostPath, "outside");
        Directory.CreateDirectory(inner);
        Directory.CreateDirectory(HostOutside);
        Directory.CreateDirectory(Path.Combine(HostOutside, "sub"));
        File.WriteAllText(Path.Combine(HostOutside, "secret.txt"), "secret");

        SupportsLinks = HostLinks.TryCreate(
            () => Directory.CreateSymbolicLink(Path.Combine(inner, "escape"), Path.Combine("..", "outside")),
            out _);

        _inner = _tree.Directory.OpenDir("inner");
        FileSystem = new DirFileSystem(_inner);
    }

    public IFileSystem FileSystem { get; }

    public bool SupportsLinks { get; }

    public string? HostOutside { get; }

    public string Snapshot() =>
        string.Join(
            "|",
            Directory.GetFileSystemEntries(HostOutside!, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(entry => File.Exists(entry)
                    ? $"{entry}={File.ReadAllText(entry)}@{File.GetLastWriteTimeUtc(entry).Ticks}"
                    : $"{entry}@{Directory.GetLastWriteTimeUtc(entry).Ticks}"))
        + $"@{Directory.GetLastWriteTimeUtc(HostOutside!).Ticks}";

    public void Dispose()
    {
        _inner.Dispose();
        _tree.Dispose();
    }
}

/// <summary>The escape tree in memory, arranged through the filesystem's own scaffolding.</summary>
public sealed class MemoryEscapeFixture : IEscapeFixture
{
    private readonly InMemoryFileSystem _memory = new();
    private readonly Dir _inner;

    public MemoryEscapeFixture()
    {
        _memory.AddDirectory("inner");
        _memory.AddDirectory("outside");
        _memory.AddDirectory("outside/sub");
        _memory.AddFile("outside/secret.txt", "secret");
        _memory.AddSymbolicLink("inner/escape", "../outside", targetIsDirectory: true);
        _inner = _memory.OpenRoot("inner");
        FileSystem = new DirFileSystem(_inner);
    }

    public IFileSystem FileSystem { get; }

    public bool SupportsLinks => true;

    public string? HostOutside => null;

    public string Snapshot() =>
        string.Join("|", _memory.GetEntries("outside").Order(StringComparer.Ordinal))
        + "=" + _memory.ReadAllText("outside/secret.txt");

    public void Dispose() => _inner.Dispose();
}

public sealed class OnDiskEscapeTests() : EscapeTests(new DiskEscapeFixture())
{
    [Theory]
    [MemberData(nameof(Keys))]
    public void A_host_path_names_nothing_outside(string key) => AssertHostPathNamesNothingOutside(key);
}

public sealed class InMemoryEscapeTests() : EscapeTests(new MemoryEscapeFixture());
