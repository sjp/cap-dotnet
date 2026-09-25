using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using Cap.Std;
using Cap.Std.Testing;

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
/// That no path-taking entry point of <see cref="IFile"/>, <see cref="IDirectory"/> or
/// <see cref="IFileStreamFactory"/> reaches outside the root.
/// </summary>
/// <remarks>
/// The table of entry points is checked against the interfaces by reflection, so a member
/// added in a later version of System.IO.Abstractions, or a path parameter missed here, fails
/// <see cref="Every_path_parameter_is_covered"/> rather than going untested.
/// </remarks>
public abstract class EscapeTests : IDisposable
{
    private static readonly string[] PathParameterNames =
    [
        "path", "fileName", "linkPath", "sourceFileName", "destFileName", "destinationFileName",
        "destinationBackupFileName", "sourceDirName", "destDirName",
    ];

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
            foreach (string spelling in new[] { "above-root", "relative", "through-link" })
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
    public void Every_entry_point_stays_inside(string key, string spelling)
    {
        EntryPoint entry = Find(key);
        IFileSystem fs = _fixture.FileSystem;
        if (spelling == "through-link")
        {
            Assert.SkipUnless(_fixture.SupportsLinks, "This process cannot create symbolic links here.");
        }

        string outside = spelling switch
        {
            "above-root" => fs.Path.Combine(fs.Path.GetPathRoot(fs.Directory.GetCurrentDirectory())!, "..", "outside"),
            "relative" => fs.Path.Combine("..", "outside"),
            _ => fs.Path.Combine("escape"),
        };
        string path = fs.Path.Combine(outside, entry.Names == Names.File ? "secret.txt" : "sub");

        AssertOutcome(entry, () => entry.Call(fs, path));
        Assert.Equal(_before, _fixture.Snapshot());
    }

    [Fact]
    public void Every_path_parameter_is_covered()
    {
        HashSet<string> expected = [];
        Collect(typeof(IFile), expected);
        Collect(typeof(IDirectory), expected);
        Collect(typeof(IFileStreamFactory), expected);

        HashSet<string> covered = [.. EntryPoints().Select(entry => entry.Key)];

        Assert.Empty(expected.Except(covered).Order(StringComparer.Ordinal));
        Assert.Empty(covered.Except(expected).Order(StringComparer.Ordinal));
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

    private static EntryPoint Find(string key) => EntryPoints().Single(entry => entry.Key == key);

    private static string Key(Type type, string method, ParameterInfo[] parameters, string parameter) =>
        $"{type.Name}.{method}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))}) {parameter}";

    private static void AssertOutcome(EntryPoint entry, Func<object?> call)
    {
        switch (entry.Outcome)
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
        }
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
    }

#pragma warning restore CA1416

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

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(inner, "escape"), Path.Combine("..", "outside"));
            SupportsLinks = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SupportsLinks = false;
        }

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
