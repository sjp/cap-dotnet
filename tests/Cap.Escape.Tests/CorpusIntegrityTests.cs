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
    /// The containment rows of the threat model — lexical, symbolic link, Windows naming,
    /// case and normalisation, and topology — with the text of each one's test column.
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

        Assert.NotEmpty(rows);
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

    [GeneratedRegex(@"^\| ([LSWMT]\d+) \|")]
    private static partial Regex RowPattern();
}
