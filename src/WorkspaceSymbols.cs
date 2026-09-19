using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CsLite;

/// <summary>
/// Finding a type or member anywhere in the solution by name.
/// </summary>
/// <remarks>
/// The matching is Roslyn's own pattern matcher, so "cb" finds
/// <c>ControllerBase</c> and "addcls" finds <c>AddClassroom</c> without us
/// implementing any of it. Only declarations in source are searched, which is
/// what you want: jumping to a member of a referenced assembly would land
/// nowhere, exactly as go-to-definition does.
/// </remarks>
internal static class WorkspaceSymbols
{
    /// <summary>
    /// A cap on what crosses the wire. An editor re-filters as you type, so
    /// beyond this the extra results only cost time.
    /// </summary>
    private const int Limit = 256;

    /// <summary>How a symbol's container is named beside it.</summary>
    private static readonly SymbolDisplayFormat ContainerFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static async Task<IReadOnlyList<SymbolInformation>> SearchAsync(
        Solution solution, string query, CancellationToken token)
    {
        // An empty query means "everything", which is never a useful answer for
        // a whole solution.
        if (string.IsNullOrWhiteSpace(query)) return [];

        var found = await SymbolFinder
            .FindSourceDeclarationsWithPatternAsync(solution, query, SymbolFilter.TypeAndMember, token)
            .ConfigureAwait(false);

        var results = new List<SymbolInformation>();

        foreach (var symbol in found)
        {
            // A partial type declares itself in several files; the first source
            // location is the one to offer.
            var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            if (location?.SourceTree?.FilePath is not { Length: > 0 } path) continue;

            // Compiler-generated names are not somewhere anyone wants to land.
            if (symbol.IsImplicitlyDeclared) continue;

            var span = location.GetLineSpan().Span;

            results.Add(new SymbolInformation(
                symbol.Name,
                KindOf(symbol),
                new Location(
                    Uris.FromPath(path),
                    new Range(
                        new Position(span.Start.Line, span.Start.Character),
                        new Position(span.End.Line, span.End.Character))))
            {
                ContainerName = ContainerOf(symbol),
            });
        }

        return results
            .OrderBy(result => Rank(result.Name, query))
            .ThenBy(result => result.Name.Length)
            .ThenBy(result => result.Name, StringComparer.Ordinal)
            .ThenBy(result => result.ContainerName ?? string.Empty, StringComparer.Ordinal)
            .Take(Limit)
            .ToList();
    }

    /// <summary>
    /// Orders an exact name first, then one that starts with the query, then
    /// everything else the pattern matched.
    /// </summary>
    private static int Rank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        return name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static string? ContainerOf(ISymbol symbol)
    {
        if (symbol.ContainingType is { } type) return type.ToDisplayString(ContainerFormat);

        return symbol.ContainingNamespace is { IsGlobalNamespace: false } names
            ? names.ToDisplayString(ContainerFormat)
            : null;
    }

    private static int KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Interface => SymbolKind.Interface,
            TypeKind.Struct => SymbolKind.Struct,
            TypeKind.Enum => SymbolKind.Enum,
            TypeKind.Delegate => SymbolKind.Function,
            _ => SymbolKind.Class,
        },
        IMethodSymbol { MethodKind: MethodKind.Constructor } => SymbolKind.Constructor,
        IMethodSymbol => SymbolKind.Method,
        IPropertySymbol => SymbolKind.Property,
        IEventSymbol => SymbolKind.Event,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => SymbolKind.EnumMember,
        IFieldSymbol { IsConst: true } => SymbolKind.Constant,
        IFieldSymbol => SymbolKind.Field,
        INamespaceSymbol => SymbolKind.Namespace,
        _ => SymbolKind.Field,
    };
}
