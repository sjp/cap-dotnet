using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Cap.Analyzers;

/// <summary>
/// A list of banned symbols in the format of the <c>BannedSymbols.txt</c> files read by
/// <c>Microsoft.CodeAnalysis.BannedApiAnalyzers</c>, so that a list written for one reads
/// the same way to the other.
/// </summary>
/// <remarks>
/// <para>
/// One documentation-comment ID per line — <c>T:System.IO.File</c>,
/// <c>M:System.IO.Path.GetFullPath(System.String)</c>, <c>P:System.DateTime.Now</c> —
/// optionally followed by <c>;</c> and the message to show. Blank lines and lines starting
/// with <c>#</c> are ignored. A method written without a parameter list stands for every
/// overload of it, so that a ban on <c>M:System.Threading.Thread.Sleep</c> does not have to
/// be kept in step with the framework's overloads.
/// </para>
/// <para>
/// An entry that names nothing in the compilation being analysed is ignored rather than
/// reported, so that one list can serve projects that reference different assemblies.
/// </para>
/// </remarks>
internal sealed class SymbolList
{
    private SymbolList(ImmutableArray<(string Id, string Message)> entries) => Entries = entries;

    public ImmutableArray<(string Id, string Message)> Entries { get; }

    public static SymbolList Parse(SourceText text)
    {
        ImmutableArray<(string, string)>.Builder entries = ImmutableArray.CreateBuilder<(string, string)>();
        foreach (TextLine line in text.Lines)
        {
            string content = line.ToString().Trim();
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            int split = content.IndexOf(';');
            string id = (split < 0 ? content : content.Substring(0, split)).Trim();
            string message = split < 0 ? string.Empty : content.Substring(split + 1).Trim();
            if (id.Length > 2 && id[1] == ':')
            {
                entries.Add((id, message));
            }
        }

        return new SymbolList(entries.ToImmutable());
    }

    /// <summary>
    /// Every symbol in <paramref name="compilation"/> that <paramref name="id"/> names.
    /// </summary>
    public static IEnumerable<ISymbol> Resolve(string id, Compilation compilation)
    {
        if (id[0] == 'M' && id.IndexOf('(') < 0)
        {
            int dot = id.LastIndexOf('.');
            if (dot <= 2)
            {
                return [];
            }

            string member = id.Substring(dot + 1);
            string name = member == "#ctor" ? WellKnownMemberNames.InstanceConstructorName : member;
            return DocumentationCommentId
                .GetSymbolsForDeclarationId("T:" + id.Substring(2, dot - 2), compilation)
                .OfType<INamedTypeSymbol>()
                .SelectMany(type => type.GetMembers(name))
                .Where(symbol => symbol.Kind == SymbolKind.Method);
        }

        return DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation);
    }
}
