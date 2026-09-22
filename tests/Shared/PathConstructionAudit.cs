using System.Reflection;
using System.Text;

namespace Cap.Testing;

/// <summary>
/// That nothing in a shipping assembly builds a path.
/// </summary>
/// <remarks>
/// <para>
/// Every operation in this library reaches the filesystem the same way: resolution confines a
/// directory, and one further call is made against that directory with one component. The
/// alternative — assembling a longer path and handing it to something that resolves it — is
/// the technique this library exists to replace, and it is dangerous precisely because it
/// usually works. A rebuilt path opens the right file every time, until a component in the
/// middle turns out to be a symbolic link, and no test written about results would ever catch
/// it.
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
/// <para>
/// Written once and pointed at each assembly by a class that derives from it. The convenience
/// layer re-enters the old habit more easily than the core does, because a walk and a copy
/// both have a natural place to put a path that is being built up level by level, so it is
/// the assembly that most needs the same instrument rather than a second one written later.
/// </para>
/// </remarks>
public abstract class PathConstructionAudit
{
    /// <summary>The characters that divide one path component from the next, on any platform.</summary>
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The prefix the build gives each carried source file of the audited assembly.</summary>
    protected abstract string ResourcePrefix { get; }

    /// <summary>The carried source files of an assembly, as a theory's worth of cases.</summary>
    protected static TheoryData<string> Sources(Assembly assembly, string prefix)
    {
        TheoryData<string> files = [];
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                files.Add(name);
            }
        }

        return files;
    }

    /// <summary>
    /// Asserts that the audit found the source it is supposed to be auditing.
    /// </summary>
    /// <param name="expected">
    /// The name of a file that must be among them, so that a build carrying some unrelated
    /// resource cannot satisfy this.
    /// </param>
    /// <remarks>
    /// Without this the whole audit passes by finding nothing, which is the failure mode of
    /// every test that scans something: a build change that stops carrying the source turns a
    /// guarantee into an empty loop that reports success.
    /// </remarks>
    protected void AssertSourceIsPresent(string expected)
    {
        string[] names = [.. GetType().Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))];

        Assert.NotEmpty(names);
        Assert.Contains(names, name => name.EndsWith(expected, StringComparison.Ordinal));
    }

    /// <summary>Asserts that no literal in one carried source file contains a path separator.</summary>
    protected void AssertNoSeparatorLiterals(string resource)
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

    private string Read(string resource)
    {
        using Stream stream = GetType().Assembly.GetManifestResourceStream(resource)!;
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
