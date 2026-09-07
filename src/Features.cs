using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace CsLite;

/// <summary>
/// What the server actually answers: diagnostics, hover, go-to-definition,
/// completion, find-references and rename. Each one is a small translation
/// between Roslyn's model and the wire format.
/// </summary>
internal static class Features
{
    /// <summary>How a symbol is rendered in a hover popup.</summary>
    private static readonly SymbolDisplayFormat HoverFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
                         | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeType
                       | SymbolDisplayMemberOptions.IncludeContainingType
                       | SymbolDisplayMemberOptions.IncludeModifiers
                       | SymbolDisplayMemberOptions.IncludeConstantValue
                       | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeDefaultValue
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut
                          | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // -----------------------------------------------------------------------
    // Diagnostics
    // -----------------------------------------------------------------------

    public static async Task<IReadOnlyList<Diagnostic>> DiagnoseAsync(Document document, CancellationToken token)
    {
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        if (model is null) return [];

        var results = new List<Diagnostic>();

        foreach (var diagnostic in model.GetDiagnostics(cancellationToken: token))
        {
            // Hidden diagnostics drive editor adornments we do not implement,
            // and reporting them just clutters the buffer.
            if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden) continue;
            if (!diagnostic.Location.IsInSource) continue;

            results.Add(new Diagnostic(
                Conversions.ToRange(text, diagnostic.Location.SourceSpan),
                SeverityOf(diagnostic),
                diagnostic.Id,
                "cslite",
                diagnostic.GetMessage()));
        }

        return results;
    }

    private static int SeverityOf(RoslynDiagnostic diagnostic) => diagnostic.Severity switch
    {
        Microsoft.CodeAnalysis.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
        Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
        Microsoft.CodeAnalysis.DiagnosticSeverity.Info => DiagnosticSeverity.Information,
        _ => DiagnosticSeverity.Hint,
    };

    // -----------------------------------------------------------------------
    // Hover
    // -----------------------------------------------------------------------

    /// <summary>Builds a hover popup, in whichever format the client accepts.</summary>
    /// <remarks>
    /// Honouring <paramref name="markdown"/> matters more than it looks. Eglot
    /// advertises "plaintext" only when markdown-mode is absent, and a client
    /// that cannot render markdown will show the literal "```csharp" fence as
    /// the first line of the echo area. Sending a bare signature instead puts
    /// the useful text on line one, which is all the minibuffer shows.
    /// </remarks>
    public static async Task<Hover?> HoverAsync(
        Document document, Position position, bool markdown, CancellationToken token)
    {
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var offset = Conversions.ToOffset(text, position);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, token).ConfigureAwait(false);
        if (symbol is null) return null;

        var signature = symbol.ToDisplayString(HoverFormat);
        var summary = SummaryOf(symbol);

        var body = new StringBuilder();

        if (markdown)
        {
            body.Append("```csharp\n").Append(signature).Append("\n```");
        }
        else
        {
            body.Append(signature);
        }

        if (summary is { Length: > 0 }) body.Append("\n\n").Append(summary);

        // Highlight the token under the cursor rather than the whole expression.
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var range = root is null ? null : Conversions.ToRange(text, root.FindToken(offset).Span);

        var contents = markdown
            ? MarkupContent.Markdown(body.ToString())
            : MarkupContent.PlainText(body.ToString());

        return new Hover(contents, range);
    }

    /// <summary>Pulls the summary text out of a documentation comment, if there is one.</summary>
    /// <remarks>
    /// Hovering "new Greeter()" resolves to the constructor, which usually
    /// carries no documentation of its own. Showing the type's summary there is
    /// what a reader is actually asking for.
    /// </remarks>
    internal static string? SummaryFor(ISymbol symbol) => SummaryOf(symbol);

    private static string? SummaryOf(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();

        if (string.IsNullOrWhiteSpace(xml)
            && symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } type })
        {
            xml = type.GetDocumentationCommentXml();
        }

        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault();
            if (summary is null) return null;

            // Doc comments arrive with the original indentation still attached.
            var words = summary.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words);
        }
        catch (Exception error)
        {
            Log.Debug($"unparseable doc comment on {symbol.Name}: {error.Message}");
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Go to definition
    // -----------------------------------------------------------------------

    public static async Task<IReadOnlyList<Location>> DefinitionAsync(
        Document document, Position position, CancellationToken token)
    {
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var offset = Conversions.ToOffset(text, position);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, token).ConfigureAwait(false);
        if (symbol is null) return [];

        // A symbol reached through a project reference is a metadata symbol
        // here; this maps it back to the source we already have loaded.
        var inSource = await SymbolFinder
            .FindSourceDefinitionAsync(symbol, document.Project.Solution, token)
            .ConfigureAwait(false);

        return (inSource ?? symbol).Locations
            .Select(ToLocation)
            .OfType<Location>()
            .ToList();
    }

    /// <summary>
    /// Converts a Roslyn location, or returns null for one that has no source.
    /// </summary>
    /// <remarks>
    /// Symbols that exist only in a referenced assembly land here. There is
    /// nothing to point an editor at, because decompilation is out of scope.
    /// </remarks>
    private static Location? ToLocation(Microsoft.CodeAnalysis.Location location)
    {
        if (!location.IsInSource || location.SourceTree?.FilePath is not { Length: > 0 } path)
            return null;

        var lineSpan = location.GetLineSpan().Span;
        return new Location(
            Uris.FromPath(path),
            new Range(
                new Position(lineSpan.Start.Line, lineSpan.Start.Character),
                new Position(lineSpan.End.Line, lineSpan.End.Character)));
    }

    // -----------------------------------------------------------------------
    // Find references
    // -----------------------------------------------------------------------

    public static async Task<IReadOnlyList<Location>> ReferencesAsync(
        Document document, Position position, bool includeDeclaration, CancellationToken token)
    {
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var offset = Conversions.ToOffset(text, position);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, token).ConfigureAwait(false);
        if (symbol is null) return [];

        var found = await SymbolFinder
            .FindReferencesAsync(symbol, document.Project.Solution, token)
            .ConfigureAwait(false);

        var results = new List<Location>();

        foreach (var referenced in found)
        {
            if (includeDeclaration)
            {
                results.AddRange(referenced.Definition.Locations
                    .Select(ToLocation)
                    .OfType<Location>());
            }

            foreach (var reference in referenced.Locations)
            {
                // Implicit references have no text of their own to jump to --
                // the foreach that calls GetEnumerator, for instance.
                if (reference.IsImplicit) continue;
                if (ToLocation(reference.Location) is { } location) results.Add(location);
            }
        }

        // The same location can arrive twice when a partial definition is also
        // matched as a reference.
        return results
            .DistinctBy(location => (location.Uri, location.Range.Start.Line, location.Range.Start.Character))
            .OrderBy(location => location.Uri, StringComparer.Ordinal)
            .ThenBy(location => location.Range.Start.Line)
            .ThenBy(location => location.Range.Start.Character)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // Rename
    // -----------------------------------------------------------------------

    /// <summary>
    /// Renames a symbol everywhere it appears in the solution.
    /// </summary>
    /// <remarks>
    /// The edits are expressed against the text as the server currently sees
    /// it, which for an unopened file is what is on disk. Roslyn does the hard
    /// part: overrides, interface implementations and partial declarations all
    /// travel with the symbol.
    /// </remarks>
    public static async Task<WorkspaceEdit?> RenameAsync(
        Document document, Position position, string newName, CancellationToken token)
    {
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var offset = Conversions.ToOffset(text, position);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, token).ConfigureAwait(false);
        if (symbol is null) return null;

        if (!symbol.Locations.Any(location => location.IsInSource))
        {
            throw new InvalidOperationException(
                $"{symbol.Name} is defined in a referenced assembly and cannot be renamed");
        }

        var solution = document.Project.Solution;
        var renamed = await Renamer
            .RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, token)
            .ConfigureAwait(false);

        var changes = new Dictionary<string, IReadOnlyList<TextEdit>>();

        foreach (var projectChange in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var id in projectChange.GetChangedDocuments())
            {
                var before = solution.GetDocument(id);
                var after = renamed.GetDocument(id);
                if (before?.FilePath is not { Length: > 0 } path || after is null) continue;

                var original = await before.GetTextAsync(token).ConfigureAwait(false);
                var edits = (await after.GetTextChangesAsync(before, token).ConfigureAwait(false))
                    .OrderBy(change => change.Span.Start)
                    .Select(change => new TextEdit(
                        Conversions.ToRange(original, change.Span),
                        change.NewText ?? string.Empty))
                    .ToList();

                if (edits.Count > 0) changes[Uris.FromPath(path)] = edits;
            }
        }

        return new WorkspaceEdit(changes);
    }

    // -----------------------------------------------------------------------
    // Completion
    // -----------------------------------------------------------------------

    public static async Task<CompletionList> CompleteAsync(
        Document document, Position position, CancellationToken token)
    {
        var service = CompletionService.GetService(document);
        if (service is null)
        {
            Log.Warn("no completion service; the Features assemblies did not compose");
            return new CompletionList(false, []);
        }

        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var offset = Conversions.ToOffset(text, position);

        var completions = await service.GetCompletionsAsync(document, offset, cancellationToken: token)
            .ConfigureAwait(false);

        var items = new List<CompletionItem>(completions.ItemsList.Count);

        foreach (var item in completions.ItemsList)
        {
            items.Add(new CompletionItem(item.DisplayText)
            {
                Kind = KindOf(item.Tags),
                Detail = string.IsNullOrEmpty(item.InlineDescription) ? null : item.InlineDescription,
                SortText = item.SortText,
                FilterText = item.FilterText,
            });
        }

        // The list is complete for this position, so the editor can narrow it
        // locally as the user keeps typing instead of asking us again.
        return new CompletionList(false, items);
    }

    /// <summary>
    /// Maps Roslyn's tag strings onto LSP kinds, which is only used to pick the
    /// icon Emacs shows beside each candidate.
    /// </summary>
    private static int KindOf(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case "Class": return CompletionItemKind.Class;
                case "Constant": return CompletionItemKind.Constant;
                case "Delegate": return CompletionItemKind.Function;
                case "Enum": return CompletionItemKind.Enum;
                case "EnumMember": return CompletionItemKind.EnumMember;
                case "Event": return CompletionItemKind.Event;
                case "ExtensionMethod":
                case "Method": return CompletionItemKind.Method;
                case "Field": return CompletionItemKind.Field;
                case "Interface": return CompletionItemKind.Interface;
                case "Keyword": return CompletionItemKind.Keyword;
                case "Label":
                case "Local":
                case "Parameter":
                case "RangeVariable": return CompletionItemKind.Variable;
                case "Module":
                case "Namespace": return CompletionItemKind.Module;
                case "Operator": return CompletionItemKind.Operator;
                case "Property": return CompletionItemKind.Property;
                case "Snippet": return CompletionItemKind.Snippet;
                case "Structure": return CompletionItemKind.Struct;
                case "TypeParameter": return CompletionItemKind.TypeParameter;
            }
        }

        return CompletionItemKind.Text;
    }
}
