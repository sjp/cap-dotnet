using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Cap.Analyzers;

/// <summary>
/// Reports the places a program reaches authority it was not handed, and the uses of this
/// library that undo what it confines.
/// </summary>
/// <remarks>
/// <para>
/// One analyzer rather than one per rule, and deliberately so. The rules that forbid the
/// ambient framework APIs are off by default, and the compiler does not run an analyzer
/// whose every rule is off; an assembly marked <c>[assembly: CapabilityStrict]</c> turns
/// those rules on from inside the compilation, where no configuration can see it, and that
/// only works if the analyzer carrying them is already running on the strength of the rules
/// that are on.
/// </para>
/// <para>
/// None of this is a security boundary. A compiler diagnostic stops only code that is
/// compiled with the analyzer and does not suppress it. What it does is make each place a
/// program reaches past its capabilities visible at the line where it happens.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapabilityAnalyzer : DiagnosticAnalyzer
{
    private const string AmbientAuthorityName = "Cap.Primitives.AmbientAuthority";
    private const string CapabilityStrictName = "Cap.Primitives.CapabilityStrictAttribute";
    private const string CompositionRootName = "Cap.Primitives.CompositionRootAttribute";

    private static readonly (string Resource, DiagnosticDescriptor Rule)[] BuiltInLists =
    [
        ("Cap.Analyzers.Lists.Filesystem.txt", Rules.AmbientFilesystem),
        ("Cap.Analyzers.Lists.Network.txt", Rules.AmbientNetwork),
        ("Cap.Analyzers.Lists.Clock.txt", Rules.AmbientClock),
        ("Cap.Analyzers.Lists.Entropy.txt", Rules.AmbientEntropy),
    ];

    private static readonly Lazy<ImmutableArray<(SymbolList List, DiagnosticDescriptor Rule)>> BuiltIn =
        new(LoadBuiltInLists);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [.. Rules.All];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(Start);
    }

    /// <summary>
    /// The lists compiled into the analyzer, one per rule.
    /// </summary>
    /// <remarks>Exposed so that a test can check that every entry names something that exists.</remarks>
    internal static ImmutableArray<(SymbolList List, DiagnosticDescriptor Rule)> BuiltInSymbolLists => BuiltIn.Value;

    /// <summary>
    /// Whether an AdditionalFile is a project's own list of banned symbols.
    /// </summary>
    /// <remarks>
    /// <c>CapBannedSymbols.txt</c>, or <c>CapBannedSymbols.</c><em>anything</em><c>.txt</c> so
    /// that a repository can keep one list per concern. Named apart from the
    /// <c>BannedSymbols.txt</c> that BannedApiAnalyzers reads, so that a project using both
    /// does not have every entry reported twice under two IDs.
    /// </remarks>
    internal static bool IsProjectList(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("CapBannedSymbols", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && (name.Length == "CapBannedSymbols.txt".Length || name["CapBannedSymbols".Length] == '.');
    }

    private static ImmutableArray<(SymbolList, DiagnosticDescriptor)> LoadBuiltInLists()
    {
        ImmutableArray<(SymbolList, DiagnosticDescriptor)>.Builder lists =
            ImmutableArray.CreateBuilder<(SymbolList, DiagnosticDescriptor)>(BuiltInLists.Length);
        foreach ((string resource, DiagnosticDescriptor rule) in BuiltInLists)
        {
            using Stream stream = typeof(CapabilityAnalyzer).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The analyzer was built without its list {resource}.");
            lists.Add((SymbolList.Parse(SourceText.From(stream)), rule));
        }

        return lists.MoveToImmutable();
    }

    private static void Start(CompilationStartAnalysisContext context)
    {
        Compilation compilation = context.Compilation;
        INamedTypeSymbol? strictAttribute = compilation.GetTypeByMetadataName(CapabilityStrictName);
        bool strict = strictAttribute is not null && compilation.Assembly.GetAttributes()
            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, strictAttribute));

        // The project's own lists first. The first list to name a symbol decides how it is
        // reported, and a project that bans File by name wants that reported whether or not
        // it has also turned on the rule that bans the whole filesystem.
        var bans = new Dictionary<ISymbol, (DiagnosticDescriptor Rule, string Message)>(SymbolEqualityComparer.Default);
        foreach (AdditionalText file in context.Options.AdditionalFiles)
        {
            if (IsProjectList(file.Path) && file.GetText(context.CancellationToken) is { } text)
            {
                AddBans(bans, SymbolList.Parse(text), Rules.ProjectBannedSymbol, compilation);
            }
        }

        foreach ((SymbolList list, DiagnosticDescriptor rule) in BuiltIn.Value)
        {
            AddBans(bans, list, rule, compilation);
        }

        var state = new CompilationState(
            compilation,
            strict,
            bans,
            compilation.GetTypeByMetadataName(AmbientAuthorityName),
            compilation.GetTypeByMetadataName(CompositionRootName),
            compilation.GetTypeByMetadataName("System.IO.Path"),
            compilation.GetTypeByMetadataName("System.String"),
            context.Options.AnalyzerConfigOptionsProvider);

        context.RegisterOperationAction(state.AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(state.AnalyzeObjectCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(
            state.AnalyzeMemberReference,
            OperationKind.PropertyReference,
            OperationKind.FieldReference,
            OperationKind.EventReference,
            OperationKind.MethodReference);
    }

    private static void AddBans(
        Dictionary<ISymbol, (DiagnosticDescriptor, string)> bans,
        SymbolList list,
        DiagnosticDescriptor rule,
        Compilation compilation)
    {
        foreach ((string id, string message) in list.Entries)
        {
            foreach (ISymbol symbol in SymbolList.Resolve(id, compilation))
            {
                if (!bans.ContainsKey(symbol.OriginalDefinition))
                {
                    bans.Add(symbol.OriginalDefinition, (rule, message));
                }
            }
        }
    }

    private sealed class CompilationState(
        Compilation compilation,
        bool strict,
        Dictionary<ISymbol, (DiagnosticDescriptor Rule, string Message)> bans,
        INamedTypeSymbol? ambientAuthority,
        INamedTypeSymbol? compositionRoot,
        INamedTypeSymbol? path,
        INamedTypeSymbol? stringType,
        AnalyzerConfigOptionsProvider options)
    {
        private readonly Lazy<IMethodSymbol?> _entryPoint = new(() =>
            compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication
                or OutputKind.WindowsRuntimeApplication
                ? compilation.GetEntryPoint(CancellationToken.None)
                : null);

        public void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            IMethodSymbol method = invocation.TargetMethod;

            CheckBanned(context, method);

            if (ambientAuthority is not null
                && method.Name == "Acquire"
                && SymbolEqualityComparer.Default.Equals(method.ContainingType, ambientAuthority)
                && !IsCompositionRoot(context))
            {
                Report(context, Rules.AcquireOutsideCompositionRoot, invocation.Syntax.GetLocation(),
                    context.ContainingSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat));
            }

            CheckUnsafeHandle(context, method);
            CheckPathArguments(context, method, invocation.Arguments);
        }

        public void AnalyzeObjectCreation(OperationAnalysisContext context)
        {
            var creation = (IObjectCreationOperation)context.Operation;
            if (creation.Constructor is { } constructor)
            {
                CheckBanned(context, constructor);
                CheckPathArguments(context, constructor, creation.Arguments);
            }
        }

        public void AnalyzeMemberReference(OperationAnalysisContext context)
        {
            ISymbol? member = context.Operation switch
            {
                IPropertyReferenceOperation p => p.Property,
                IFieldReferenceOperation f => f.Field,
                IEventReferenceOperation e => e.Event,
                IMethodReferenceOperation m => m.Method,
                _ => null,
            };

            if (member is null)
            {
                return;
            }

            CheckBanned(context, member);
            if (member is IMethodSymbol method)
            {
                CheckUnsafeHandle(context, method);
            }
        }

        private void CheckBanned(OperationAnalysisContext context, ISymbol symbol)
        {
            if (bans.Count == 0)
            {
                return;
            }

            if (symbol is IMethodSymbol { ReducedFrom: { } extension })
            {
                symbol = extension;
            }

            (DiagnosticDescriptor Rule, string Message) ban;
            if (!TryFindBan(symbol.OriginalDefinition, out ban) || IsInsideNameOf(context.Operation))
            {
                return;
            }

            string name = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
            Report(context, ban.Rule, context.Operation.Syntax.GetLocation(), name,
                ban.Message.Length == 0 ? "banned in this project" : ban.Message);
        }

        private bool TryFindBan(ISymbol symbol, out (DiagnosticDescriptor, string) ban)
        {
            if (bans.TryGetValue(symbol, out ban))
            {
                return true;
            }

            for (INamedTypeSymbol? type = symbol.ContainingType; type is not null; type = type.ContainingType)
            {
                if (bans.TryGetValue(type.OriginalDefinition, out ban))
                {
                    return true;
                }
            }

            for (INamespaceSymbol? ns = symbol.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
            {
                if (bans.TryGetValue(ns, out ban))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInsideNameOf(IOperation operation)
        {
            for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
            {
                if (current is INameOfOperation)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsCompositionRoot(OperationAnalysisContext context)
        {
            SyntaxTree tree = context.Operation.Syntax.SyntaxTree;
            if (options.GetOptions(tree).TryGetValue(Rules.CompositionRootOption, out string? value)
                && string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            IMethodSymbol? entryPoint = _entryPoint.Value;
            for (ISymbol? symbol = context.ContainingSymbol; symbol is not null and not INamespaceSymbol; symbol = symbol.ContainingSymbol)
            {
                if (SymbolEqualityComparer.Default.Equals(symbol, entryPoint)
                    || HasCompositionRoot(symbol)
                    || (symbol is IMethodSymbol { AssociatedSymbol: { } property } && HasCompositionRoot(property)))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasCompositionRoot(ISymbol symbol) =>
            compositionRoot is not null && symbol.GetAttributes()
                .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, compositionRoot));

        /// <summary>
        /// Reports a public <c>Unsafe*</c> member of this library: the members that hand out
        /// what a capability wraps.
        /// </summary>
        private void CheckUnsafeHandle(OperationAnalysisContext context, IMethodSymbol method)
        {
            if (method.Name.StartsWith("Unsafe", StringComparison.Ordinal)
                && method.DeclaredAccessibility == Accessibility.Public
                && IsThisLibrary(method))
            {
                Report(context, Rules.UnsafeHandle, context.Operation.Syntax.GetLocation(),
                    method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat));
            }
        }

        private static bool IsThisLibrary(ISymbol symbol) =>
            symbol.ContainingAssembly?.Name.StartsWith("Cap.", StringComparison.Ordinal) == true;

        /// <summary>
        /// Reports a path argument to this library that was built by joining strings.
        /// </summary>
        /// <remarks>
        /// Only the argument as written at the call is examined; a path built on one line and
        /// passed on the next is not followed. The rule is aimed at the reflex of writing
        /// <c>dir.OpenFile(prefix + "/" + name)</c>, which is visible at the call, rather than
        /// at every string that ever held a separator.
        /// </remarks>
        private void CheckPathArguments(OperationAnalysisContext context, IMethodSymbol method, ImmutableArray<IArgumentOperation> arguments)
        {
            if (arguments.IsEmpty || !IsThisLibrary(method) || TakesAmbientAuthority(method))
            {
                return;
            }

            foreach (IArgumentOperation argument in arguments)
            {
                if (argument.Parameter is { } parameter
                    && parameter.Type.SpecialType == SpecialType.System_String
                    && IsPathParameter(parameter.Name)
                    && IsJoined(Unwrap(argument.Value)))
                {
                    Report(context, Rules.ConcatenatedPath, argument.Syntax.GetLocation(),
                        method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat));
                }
            }
        }

        /// <summary>
        /// A method that takes the token is one that is handed an ordinary path on purpose —
        /// opening the first directory is exactly where a program names a place in full.
        /// </summary>
        private bool TakesAmbientAuthority(IMethodSymbol method) =>
            ambientAuthority is not null && method.Parameters.Any(
                p => SymbolEqualityComparer.Default.Equals(p.Type, ambientAuthority));

        private static bool IsPathParameter(string name) =>
            name is "path" or "from" or "to" || name.EndsWith("Path", StringComparison.Ordinal);

        private bool IsJoined(IOperation value)
        {
            // Entirely constant: written out by the programmer, nothing arrived from elsewhere.
            if (value.ConstantValue.HasValue)
            {
                return false;
            }

            switch (value)
            {
                case IInvocationOperation invocation:
                    IMethodSymbol method = invocation.TargetMethod;
                    if (SymbolEqualityComparer.Default.Equals(method.ContainingType, path))
                    {
                        return method.Name is "Combine" or "Join";
                    }

                    if (SymbolEqualityComparer.Default.Equals(method.ContainingType, stringType))
                    {
                        return method.Name switch
                        {
                            "Concat" => invocation.Arguments.Any(a => ContainsSeparator(a.Value)),
                            "Join" => invocation.Arguments.Length > 0 && IsSeparator(Unwrap(invocation.Arguments[0].Value)),
                            _ => false,
                        };
                    }

                    return false;

                case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary
                    when binary.Type?.SpecialType == SpecialType.System_String:
                    return ContainsSeparator(binary);

                case IInterpolatedStringOperation interpolated:
                    return interpolated.Parts.Any(part => part switch
                    {
                        IInterpolatedStringTextOperation text => IsSeparator(text.Text),
                        IInterpolationOperation hole => IsSeparator(Unwrap(hole.Expression)),
                        _ => false,
                    });

                default:
                    return false;
            }
        }

        private bool ContainsSeparator(IOperation operation)
        {
            operation = Unwrap(operation);
            return operation switch
            {
                IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary =>
                    ContainsSeparator(binary.LeftOperand) || ContainsSeparator(binary.RightOperand),
                IArrayCreationOperation { Initializer: { } initializer } =>
                    initializer.ElementValues.Any(ContainsSeparator),
                _ => IsSeparator(operation),
            };
        }

        private bool IsSeparator(IOperation operation)
        {
            if (operation.ConstantValue is { HasValue: true, Value: var constant })
            {
                return constant switch
                {
                    string s => s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0,
                    char c => c is '/' or '\\',
                    _ => false,
                };
            }

            return operation switch
            {
                IFieldReferenceOperation field =>
                    SymbolEqualityComparer.Default.Equals(field.Field.ContainingType, path)
                    && field.Field.Name is "DirectorySeparatorChar" or "AltDirectorySeparatorChar",
                IInvocationOperation { TargetMethod.Name: "ToString", Instance: { } instance } =>
                    IsSeparator(Unwrap(instance)),
                _ => false,
            };
        }

        private static IOperation Unwrap(IOperation operation)
        {
            while (true)
            {
                switch (operation)
                {
                    case IConversionOperation { IsImplicit: true } conversion:
                        operation = conversion.Operand;
                        break;
                    case IParenthesizedOperation parenthesized:
                        operation = parenthesized.Operand;
                        break;
                    default:
                        return operation;
                }
            }
        }

        private void Report(OperationAnalysisContext context, DiagnosticDescriptor rule, Location location, params object[] arguments)
        {
            DiagnosticDescriptor effective = strict ? StrictRules.Get(rule) : rule;
            context.ReportDiagnostic(Diagnostic.Create(effective, location, arguments));
        }
    }

    private static class StrictRules
    {
        private static readonly Dictionary<string, DiagnosticDescriptor> ById =
            Rules.All.ToDictionary(rule => rule.Id, Rules.ToStrict, StringComparer.Ordinal);

        public static DiagnosticDescriptor Get(DiagnosticDescriptor rule) => ById[rule.Id];
    }
}
