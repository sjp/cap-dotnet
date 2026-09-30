using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Cap.Primitives;

namespace Cap.Escape.Tests;

/// <summary>
/// Checks on the corpus itself: that it says what it means, and that it covers what the
/// threat model says is defended.
/// </summary>
public sealed partial class CorpusIntegrityTests
{
    /// <summary>The operations whose two ends are the two sides of a hard link.</summary>
    private static readonly Operation[] HardLinkOperations = [Operation.HardLinkFrom, Operation.HardLinkTo];

    /// <summary>The operations whose two ends are the two sides of a move.</summary>
    private static readonly Operation[] RenameOperations = [Operation.RenameFrom, Operation.RenameTo];

    /// <summary>Case names are unique, since a test selects its case by name.</summary>
    [Fact]
    public void Every_case_has_its_own_name()
    {
        string[] duplicated = [.. EscapeCorpus.Cases.GroupBy(entry => entry.Name).Where(group => group.Count() > 1).Select(group => group.Key)];
        Assert.Empty(duplicated);
    }

    /// <summary>
    /// Where a case says a path is refused before anything is looked up, the parser for that
    /// syntax refuses it, for the reason the case says; and where a case says a path reaches
    /// the filesystem, the parser lets it through. The parser is run as a directory handle
    /// runs it, carrying a parent step through to resolution.
    /// </summary>
    /// <remarks>
    /// Checked under both syntaxes on every host, because the parser takes the syntax as an
    /// argument. That makes the Windows half of the table checkable on a Linux machine, which
    /// is where most of it will be edited, rather than only on the Windows leg of CI.
    /// </remarks>
    [Theory]
    [InlineData(CapPathSyntax.Unix)]
    [InlineData(CapPathSyntax.Windows)]
    public void What_a_case_says_the_parser_decides_is_what_the_parser_decides(CapPathSyntax syntax)
    {
        List<string> wrong = [];
        foreach (EscapeCase entry in EscapeCorpus.Cases)
        {
            Expectation? expected = syntax == CapPathSyntax.Windows ? entry.Windows : entry.Unix;
            if (expected is null || entry.Setup.Length != 0)
            {
                continue;
            }

            string path = entry.Path.Replace(
                "{outside}", syntax == CapPathSyntax.Windows ? @"C:\outside" : "/outside", StringComparison.Ordinal);
            bool parsed = CapPath.TryParse(
                path, syntax, ParentLinkPolicy.Preserve, out CapPath parsedPath, out CapPathError error);

            Outcome opened = expected[Operation.OpenFile];

            // A parent step is carried through the parser and refused by resolution, at the
            // step that would climb above the root, so an escape spelled with one is not the
            // parser's to decide.
            if (opened == Outcome.Escape && parsed && parsedPath.ContainsParentLink)
            {
                continue;
            }

            if (opened is Outcome.Escape or Outcome.Malformed)
            {
                if (parsed || opened != AsOutcome(error))
                {
                    wrong.Add($"'{entry.Name}' says {opened}; the parser says {(parsed ? "accepted" : error)}.");
                }
            }
            else if (!parsed)
            {
                wrong.Add($"'{entry.Name}' says its path reaches the filesystem; the parser says {error}.");
            }
        }

        Assert.True(wrong.Count == 0, $"Under {syntax} rules:\n{string.Join('\n', wrong)}");
    }

    /// <summary>
    /// Every attack the threat model lists as in scope has something defending it, and every
    /// row the corpus claims to defend exists.
    /// </summary>
    /// <remarks>
    /// A row is defended here when a case, or a test outside the table, names it. A row the
    /// corpus cannot arrange — a crafted reparse point, a device that only a parser bug could
    /// reach, a concurrent attack — has to name the test that covers it elsewhere instead, and a
    /// row with nothing in its test column is a claim nothing checks. Rows defended here must
    /// also say so in the document, so that a reader of it can find them.
    /// </remarks>
    [Fact]
    public void Every_row_of_the_threat_model_is_defended()
    {
        Dictionary<string, string> rows = ThreatModelRows();
        HashSet<string> defended = DefendedHere();

        foreach (string claimed in defended)
        {
            Assert.True(rows.ContainsKey(claimed), $"The corpus claims to defend {claimed}, which the threat model does not list.");
        }

        foreach ((string row, string tests) in rows)
        {
            if (defended.Contains(row))
            {
                Assert.True(
                    tests.Contains("escape corpus", StringComparison.Ordinal),
                    $"{row} is defended by the escape corpus, and its row in the threat model does not say so.");
            }
            else
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(tests),
                    $"{row} is in scope, the escape corpus does not defend it, and the threat model names no test that does.");
            }
        }
    }

    /// <summary>
    /// Every test the threat model names in a test column is a test that exists, in whichever
    /// test project it lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test column that names a test nobody can find is the same claim nothing checks as an
    /// empty one, and it is the one a rename produces. The document writes a test as
    /// <c>`Class.Method`</c> or <c>`Class`</c>, and a further method of the class just named as
    /// <c>`.Method`</c>; an elision (<c>…</c>) is the document saying "and so on", and is skipped.
    /// </para>
    /// <para>
    /// The other test projects are not referenced from here, so their sources are carried as
    /// text and a test is found by its declaration. A method counts as found in a class when a
    /// file that declares the class declares the method, which is loose where a file holds more
    /// than one class, and exact enough to catch a rename.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_test_the_threat_model_names_exists()
    {
        Dictionary<string, HashSet<string>> declared = DeclaredTests();
        List<string> missing = [];
        int named = 0;

        foreach ((string row, string tests) in ThreatModelRows())
        {
            string? current = null;
            foreach (Match quoted in QuotedPattern().Matches(tests))
            {
                string token = quoted.Groups[1].Value;
                string? type;
                string? method;

                if (TestPattern().Match(token) is { Success: true } test)
                {
                    type = current = test.Groups[1].Value;
                    method = test.Groups[2].Success ? test.Groups[2].Value : null;
                }
                else if (token.Contains('…', StringComparison.Ordinal))
                {
                    continue;
                }
                else if (SameClassPattern().Match(token) is { Success: true } sameClass)
                {
                    if (current is null)
                    {
                        missing.Add($"{row} names `{token}` before naming the class it belongs to.");
                        continue;
                    }

                    type = current;
                    method = sameClass.Groups[1].Value;
                }
                else
                {
                    continue;
                }

                named++;
                if (!declared.TryGetValue(type, out HashSet<string>? methods))
                {
                    missing.Add($"{row} names {type}, and no test source declares it.");
                }
                else if (method is not null && !methods.Contains(method))
                {
                    missing.Add($"{row} names {type}.{method}, and {type} declares no such member.");
                }
            }
        }

        Assert.True(named > 0, "No test named in the threat model was found; the parsing has stopped matching the document.");
        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    /// <summary>
    /// Every corpus case the threat model names is a case that exists: after
    /// <c>escape corpus:</c>, a case of <see cref="EscapeCorpus"/>, and after <c>corpus case</c>,
    /// a case of the symbolic link policy corpus in Cap.Primitives.Tests.
    /// </summary>
    /// <remarks>
    /// A <c>*</c> in a name stands for anything, so <c>`absolute-*`</c> must match at least one
    /// case and <c>`reserved-name *`</c> the family written with a space. A name starting with
    /// <c>-</c> is the document abbreviating the name before it, and is skipped.
    /// </remarks>
    [Fact]
    public void Every_corpus_case_the_threat_model_names_exists()
    {
        string[] escapeCases = [.. EscapeCorpus.Cases.Select(entry => entry.Name)];
        string[] symlinkCases = SymlinkPolicyCaseNames();
        List<string> missing = [];
        int named = 0;

        foreach ((string row, string tests) in ThreatModelRows())
        {
            foreach (Match quoted in QuotedPattern().Matches(tests))
            {
                string token = quoted.Groups[1].Value;
                if (!CaseNamePattern().IsMatch(token))
                {
                    continue;
                }

                // Which corpus the name belongs to is said by the nearest marker before it.
                Match? marker = null;
                foreach (Match candidate in CorpusMarkerPattern().Matches(tests[..quoted.Index]))
                {
                    marker = candidate;
                }

                if (marker is null)
                {
                    missing.Add($"{row} names `{token}` without saying which corpus it is from.");
                    continue;
                }

                bool escape = marker.Value == "escape corpus";
                string[] cases = escape ? escapeCases : symlinkCases;
                named++;
                if (!cases.Any(name => Matches(token, name)))
                {
                    missing.Add($"{row} names `{token}`, and the {(escape ? "escape" : "symbolic link policy")} corpus has no such case.");
                }
            }
        }

        Assert.True(named > 0, "No corpus case named in the threat model was found; the parsing has stopped matching the document.");
        Assert.True(missing.Count == 0, string.Join('\n', missing));

        static bool Matches(string pattern, string name)
        {
            string[] parts = pattern.Split('*');
            if (parts.Length == 1)
            {
                return pattern == name;
            }

            if (!name.StartsWith(parts[0], StringComparison.Ordinal) || !name.EndsWith(parts[^1], StringComparison.Ordinal))
            {
                return false;
            }

            int at = parts[0].Length;
            for (int i = 1; i < parts.Length - 1; i++)
            {
                int found = name.IndexOf(parts[i], at, StringComparison.Ordinal);
                if (found < 0)
                {
                    return false;
                }

                at = found + parts[i].Length;
            }

            return at <= name.Length - parts[^1].Length;
        }
    }

    /// <summary>
    /// The types every carried test source declares, each with the members declared in the
    /// files that declare it.
    /// </summary>
    private static Dictionary<string, HashSet<string>> DeclaredTests()
    {
        Dictionary<string, HashSet<string>> declared = [];
        foreach ((_, string source) in TestSources())
        {
            HashSet<string> members = [.. MethodDeclarationPattern().Matches(source).Select(match => match.Groups[1].Value)];
            foreach (Match type in TypeDeclarationPattern().Matches(source))
            {
                string name = type.Groups[1].Value;
                if (!declared.TryGetValue(name, out HashSet<string>? existing))
                {
                    declared[name] = existing = [];
                }

                existing.UnionWith(members);
            }
        }

        Assert.Contains(nameof(CorpusIntegrityTests), declared.Keys);
        return declared;
    }

    /// <summary>The names of the cases of the symbolic link policy corpus, read from its source.</summary>
    private static string[] SymlinkPolicyCaseNames()
    {
        string source = TestSources().Single(file => file.Name.EndsWith("SymlinkPolicyCorpus.cs", StringComparison.Ordinal)).Source;
        string[] names = [.. SymlinkCaseNamePattern().Matches(source).Select(match => match.Groups[1].Value)];
        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>Every test source carried in this assembly, by resource name.</summary>
    private static IEnumerable<(string Name, string Source)> TestSources()
    {
        Assembly assembly = typeof(CorpusIntegrityTests).Assembly;
        bool any = false;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("TestSource.", StringComparison.Ordinal))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream);
            any = true;
            yield return (name, reader.ReadToEnd());
        }

        Assert.True(any, "The test sources are not embedded in the test assembly.");
    }

    /// <summary>The rows the corpus defends, from the table and from the tests beside it.</summary>
    private static HashSet<string> DefendedHere()
    {
        HashSet<string> defended = [];
        foreach (EscapeCase entry in EscapeCorpus.Cases)
        {
            defended.UnionWith(entry.Threats);
        }

        // Every case runs through both ends of a hard link and of a move. Those are the two
        // operations that join a place to another, so they defend the boundary-crossing rows
        // when some case expects an escaping path to be refused at each end.
        if (HardLinkOperations.All(RefusedAsEscapeSomewhere))
        {
            defended.Add("T2");
        }

        if (RenameOperations.All(RefusedAsEscapeSomewhere))
        {
            defended.Add("T3");
        }

        // The tests beside the table. Listed rather than discovered, so that the analysis that
        // keeps the libraries trimmable can see every type this reflects over.
        Declared(typeof(TopologyTests), defended);
        Declared(typeof(WindowsTopologyTests), defended);
        Declared(typeof(TreeOperationTests), defended);
        Declared(typeof(FinalLinkWriteTests), defended);
        Declared(typeof(PerCallFinalLinkTests), defended);

        return defended;
    }

    private static void Declared(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type tests,
        HashSet<string> defended)
    {
        foreach (MethodInfo method in tests.GetMethods())
        {
            if (method.GetCustomAttribute<DefendsAttribute>() is { } defends)
            {
                defended.UnionWith(defends.Rows);
            }
        }
    }

    /// <summary>
    /// The numbered rows of the threat model — lexical, symbolic link, Windows naming, case and
    /// normalisation, topology, scratch space and project directories — with the text of each
    /// one's test column.
    /// </summary>
    private static Dictionary<string, string> ThreatModelRows()
    {
        using Stream stream = typeof(CorpusIntegrityTests).Assembly.GetManifestResourceStream("threat-model.md")
            ?? throw new InvalidOperationException("The threat model is not embedded in the test assembly.");
        using StreamReader reader = new(stream);

        Dictionary<string, string> rows = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Match match = RowPattern().Match(line);
            if (match.Success)
            {
                string[] cells = line.Split('|');
                rows[match.Groups[1].Value] = cells[^2].Trim();
            }
        }

        // Counted, so that a row whose id stops matching the pattern above is noticed rather than
        // silently dropping out of every check. Change it when a row is added or removed.
        Assert.Equal(49, rows.Count);
        return rows;
    }

    private static bool RefusedAsEscapeSomewhere(Operation operation) =>
        EscapeCorpus.Cases.Any(entry =>
            entry.Unix?[operation] == Outcome.Escape && entry.Windows?[operation] == Outcome.Escape);

    private static Outcome AsOutcome(CapPathError error) => error switch
    {
        CapPathError.Absolute or CapPathError.RootRelative or CapPathError.DriveRelative or
        CapPathError.Unc or CapPathError.DeviceNamespace or CapPathError.ParentLink or
        CapPathError.ReservedName => Outcome.Escape,
        _ => Outcome.Malformed,
    };

    [GeneratedRegex(@"^\| ([A-Z]{1,2}\d+) \|")]
    private static partial Regex RowPattern();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex QuotedPattern();

    [GeneratedRegex(@"^([A-Z][A-Za-z0-9_]*Tests)(?:\.([A-Za-z0-9_]+))?$")]
    private static partial Regex TestPattern();

    [GeneratedRegex(@"^\.([A-Za-z0-9_]+)$")]
    private static partial Regex SameClassPattern();

    [GeneratedRegex(@"^[a-z0-9*][a-z0-9*' \-]*$")]
    private static partial Regex CaseNamePattern();

    [GeneratedRegex("escape corpus|corpus case")]
    private static partial Regex CorpusMarkerPattern();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclarationPattern();

    [GeneratedRegex(@"(?:\bvoid|\bstring|\bbool|\bint|\blong|>|\]|\b[A-Z][A-Za-z0-9_]*)\??\s+([A-Za-z_][A-Za-z0-9_]*)\s*[<(]")]
    private static partial Regex MethodDeclarationPattern();

    [GeneratedRegex("new\\(\"([^\"]+)\"")]
    private static partial Regex SymlinkCaseNamePattern();
}
