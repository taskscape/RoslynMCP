using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CSharpMcp.Analysis;

/// <summary>
/// Resolves agent-friendly documentation IDs, metadata names, and qualified symbol names.
/// </summary>
internal static class SymbolResolver
{
    private static readonly SymbolDisplayFormat QualifiedFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut |
                          SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// Resolves exactly one source symbol or returns a precise ambiguity error.
    /// </summary>
    public static async Task<ISymbol> ResolveSingleAsync(
        Solution solution,
        string query,
        string? projectName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var candidates = await ResolveManyAsync(solution, query.Trim(), projectName, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"No source symbol matched '{query}'. Use semantic_search to discover its stable ID.");
        }

        if (candidates.Count > 1)
        {
            var candidateList = string.Join(", ", candidates.Take(10).Select(GetStableId));
            throw new InvalidOperationException($"Symbol '{query}' is ambiguous. Use one of these stable IDs: {candidateList}");
        }

        return candidates[0];
    }

    /// <summary>
    /// Resolves every exact candidate for symbol-info discovery.
    /// </summary>
    public static async Task<IReadOnlyList<ISymbol>> ResolveManyAsync(
        Solution solution,
        string query,
        string? projectName,
        CancellationToken cancellationToken)
    {
        var projects = SelectProjects(solution, projectName).ToArray();
        var candidates = new List<ISymbol>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            if (LooksLikeDocumentationId(query))
            {
                var documented = DocumentationCommentId.GetFirstSymbolForDeclarationId(query, compilation);
                if (documented is not null && documented.Locations.Any(location =>
                    location.SourceTree is { } tree && compilation.ContainsSyntaxTree(tree)))
                {
                    candidates.Add(documented);
                }
            }

            var type = compilation.GetTypeByMetadataName(query);
            if (type is not null && type.Locations.Any(location =>
                location.SourceTree is { } tree && compilation.ContainsSyntaxTree(tree)))
            {
                candidates.Add(type);
            }
        }

        if (candidates.Count == 0)
        {
            var simpleName = ExtractSimpleName(query);
            foreach (var project in projects)
            {
                var declarations = await SymbolFinder.FindDeclarationsAsync(
                    project,
                    simpleName,
                    ignoreCase: false,
                    SymbolFilter.TypeAndMember,
                    cancellationToken).ConfigureAwait(false);

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                candidates.AddRange(declarations.Where(symbol =>
                    symbol.Locations.Any(location => location.SourceTree is { } tree &&
                        compilation?.ContainsSyntaxTree(tree) == true) &&
                    MatchesQuery(symbol, query)));
            }
        }

        return candidates
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(GetStableId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Returns the stable documentation ID when Roslyn provides one, with a qualified display fallback.
    /// </summary>
    public static string GetStableId(ISymbol symbol)
    {
        return symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(QualifiedFormat);
    }

    /// <summary>
    /// Returns a consistently qualified human-readable symbol display.
    /// </summary>
    public static string GetDisplay(ISymbol symbol)
    {
        return symbol.ToDisplayString(QualifiedFormat);
    }

    /// <summary>
    /// Gets a CLR metadata name for a named type, including nested-type separators.
    /// </summary>
    public static string? GetMetadataName(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
        {
            return null;
        }

        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            names.Push(current.MetadataName);
        }

        var typeName = string.Join("+", names);
        return type.ContainingNamespace is { IsGlobalNamespace: false } namespaceSymbol
            ? $"{namespaceSymbol.ToDisplayString()}.{typeName}"
            : typeName;
    }

    private static IEnumerable<Project> SelectProjects(Solution solution, string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return solution.Projects.Where(project => project.Language == LanguageNames.CSharp);
        }

        var selected = solution.Projects.Where(project =>
            project.Language == LanguageNames.CSharp &&
            project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)).ToArray();

        return selected.Length > 0
            ? selected
            : throw new InvalidOperationException($"Project '{projectName}' was not found in the loaded solution.");
    }

    private static bool LooksLikeDocumentationId(string query)
    {
        return query.Length > 2 && query[1] == ':' && "NTFPME".Contains(query[0], StringComparison.Ordinal);
    }

    private static string ExtractSimpleName(string query)
    {
        var withoutParameters = query.Split('(', 2)[0];
        var separator = Math.Max(withoutParameters.LastIndexOf('.'), withoutParameters.LastIndexOf('+'));
        var name = separator >= 0 ? withoutParameters[(separator + 1)..] : withoutParameters;
        var genericMarker = name.IndexOfAny(['`', '<']);

        return genericMarker >= 0 ? name[..genericMarker] : name;
    }

    private static bool MatchesQuery(ISymbol symbol, string query)
    {
        if (symbol.Name.Equals(query, StringComparison.Ordinal))
        {
            return true;
        }

        var display = GetDisplay(symbol);
        var stableId = GetStableId(symbol);
        var metadataName = GetMetadataName(symbol);

        return display.Equals(query, StringComparison.Ordinal) ||
               display.EndsWith(query, StringComparison.Ordinal) ||
               stableId.Equals(query, StringComparison.Ordinal) ||
               (metadataName?.Equals(query, StringComparison.Ordinal) ?? false);
    }

}
