using System.Reflection;
using System.Text;

namespace Cap.Std.Tests;

/// <summary>
/// That nothing in the public layer builds a path.
/// </summary>
/// <remarks>
/// <para>
/// Every operation here reaches the filesystem the same way: resolution confines a
/// directory, and one further call is made against that directory with one component. The
/// alternative — assembling a longer path and handing it to something that resolves it — is
/// the technique this library exists to replace, and it is dangerous precisely because it
/// usually works. A rebuilt path opens the right file every time, until a component in the
/// middle turns out to be a symbolic link, and no test written about results would ever
/// catch it.
/// </para>
/// <para>
/// So the guarantee is asserted about the source. A path cannot be assembled without a
/// separator, so the check is that no literal in the assembly's source contains one. That is
/// a blunt instrument and deliberately so: it is easy to explain, impossible to satisfy
/// accidentally while still building a path, and it fails loudly on the first line of code
/// that reaches for the old habit.
/// </para>
/// <para>
/// Comments are exempt, and have to be — the documentation is full of <c>a/b</c> and
/// <c>..</c> because that is what it is describing.
/// </para>
/// </remarks>
public sealed class DirPathConstructionAuditTests
{
    /// <summary>The prefix the build gives each carried source file.</summary>
    private const string ResourcePrefix = "CapStdSource.";

    /// <summary>The characters that divide one path component from the next, on any platform.</summary>
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The source of this library, as the build carried it in.</summary>
    public static TheoryData<string> SourceFiles
    {
        get
        {
            TheoryData<string> files = [];
            foreach (string name in typeof(DirPathConstructionAuditTests).Assembly.GetManifestResourceNames())
            {
                if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                {
                    files.Add(name);
                }
            }

            return files;
        }
    }

    /// <summary>The audit found the source it is supposed to be auditing.</summary>
    /// <remarks>
    /// Without this the whole audit passes by finding nothing, which is the failure mode of
    /// every test that scans something: a build change that stops carrying the source turns a
    /// guarantee into an empty loop that reports success.
    /// </remarks>
    [Fact]
    public void The_source_being_audited_is_present()
    {
        string[] names = [.. typeof(DirPathConstructionAuditTests).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))];

        Assert.NotEmpty(names);
        Assert.Contains(names, name => name.EndsWith("Dir.cs", StringComparison.Ordinal));
    }

    /// <summary>No literal anywhere in the library contains a path separator.</summary>
    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void No_literal_in_the_library_contains_a_path_separator(string resource)
    {
        string source = Read(resource);

        // Raw string literals would need their own rules in the scanner below, and there are
        // none. Insisting on that is cheaper than writing rules for a construct nobody used,
        // and it fails at the moment somebody does rather than silently skipping the literal.
        Assert.DoesNotContain("\"\"\"", source, StringComparison.Ordinal);

        foreach (string literal in Literals(source))
        {
            Assert.True(
                literal.IndexOfAny(Separators) < 0,
                $"{resource} contains the literal \"{literal}\", which holds a path separator. " +
                "Nothing here may assemble a path: a name is resolved against a directory " +
                "handle one component at a time, and a joined path hands the containment " +
                "decision to whatever resolves it.");
        }
    }

    private static string Read(string resource)
    {
        using Stream stream = typeof(DirPathConstructionAuditTests).Assembly.GetManifestResourceStream(resource)!;
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Yields the contents of every string and character literal in a compilation unit.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than a regular expression, because the thing that has to be got
    /// right is telling a literal from a comment and a comment from a literal — a <c>//</c>
    /// inside a string and a quotation mark inside a comment both defeat any pattern simple
    /// enough to read. A small state machine gets both right and is no longer.
    /// </remarks>
    private static IEnumerable<string> Literals(string source)
    {
        int index = 0;
        while (index < source.Length)
        {
            char current = source[index];

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] is not ('\n' or '\r'))
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    index++;
                }

                index += 2;
                continue;
            }

            if (current == '@' && index + 1 < source.Length && source[index + 1] == '"')
            {
                index += 2;
                StringBuilder verbatim = new();
                while (index < source.Length)
                {
                    if (source[index] == '"')
                    {
                        if (index + 1 < source.Length && source[index + 1] == '"')
                        {
                            verbatim.Append('"');
                            index += 2;
                            continue;
                        }

                        index++;
                        break;
                    }

                    verbatim.Append(source[index]);
                    index++;
                }

                yield return verbatim.ToString();
                continue;
            }

            if (current is '"' or '\'')
            {
                char quote = current;
                index++;
                StringBuilder value = new();
                while (index < source.Length && source[index] != quote)
                {
                    if (source[index] == '\\' && index + 1 < source.Length)
                    {
                        // An escape sequence is copied as written, so that a separator spelled
                        // as one is still found.
                        value.Append(source[index]).Append(source[index + 1]);
                        index += 2;
                        continue;
                    }

                    value.Append(source[index]);
                    index++;
                }

                index++;
                yield return value.ToString();
                continue;
            }

            index++;
        }
    }
}
