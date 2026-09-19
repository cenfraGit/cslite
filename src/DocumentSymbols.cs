using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CsLite;

/// <summary>
/// The outline of a file: namespaces, types, and their members, nested the way
/// they are written.
/// </summary>
/// <remarks>
/// Built from the syntax tree rather than by asking for every declared symbol,
/// because the tree already has the shape we need and keeps working on a file
/// that does not currently compile. The semantic model is consulted only for
/// the detail text beside each entry, and a file mid-edit simply gets less
/// detail rather than no outline.
/// </remarks>
internal static class DocumentSymbols
{
    /// <summary>How the text beside a symbol's name is rendered.</summary>
    private static readonly SymbolDisplayFormat DetailFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut
                          | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static async Task<IReadOnlyList<DocumentSymbol>> ForAsync(
        Document document, CancellationToken token)
    {
        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax unit) return [];

        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);

        return unit.Members
            .SelectMany(member => Describe(member, text, model, token))
            .ToList();
    }

    /// <summary>
    /// Turns one declaration into symbols. A field declares several names at
    /// once, which is why this returns a sequence rather than a single symbol.
    /// </summary>
    private static IEnumerable<DocumentSymbol> Describe(
        MemberDeclarationSyntax member, SourceText text, SemanticModel? model, CancellationToken token)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax names:
                yield return Symbol(
                    names.Name.ToString(), SymbolKind.Namespace, names, names.Name, text, model, token,
                    Children(names.Members, text, model, token));
                break;

            case TypeDeclarationSyntax type:
                yield return Symbol(
                    type.Identifier.Text, KindOf(type), type, type.Identifier, text, model, token,
                    Children(type.Members, text, model, token));
                break;

            case EnumDeclarationSyntax enumeration:
                yield return Symbol(
                    enumeration.Identifier.Text, SymbolKind.Enum, enumeration, enumeration.Identifier,
                    text, model, token,
                    enumeration.Members
                        .Select(value => Symbol(
                            value.Identifier.Text, SymbolKind.EnumMember, value, value.Identifier,
                            text, model, token, []))
                        .ToList());
                break;

            case DelegateDeclarationSyntax @delegate:
                yield return Symbol(
                    @delegate.Identifier.Text, SymbolKind.Function, @delegate, @delegate.Identifier,
                    text, model, token, []);
                break;

            case MethodDeclarationSyntax method:
                yield return Symbol(
                    method.Identifier.Text, SymbolKind.Method, method, method.Identifier,
                    text, model, token, []);
                break;

            case ConstructorDeclarationSyntax constructor:
                yield return Symbol(
                    constructor.Identifier.Text, SymbolKind.Constructor, constructor,
                    constructor.Identifier, text, model, token, []);
                break;

            case DestructorDeclarationSyntax destructor:
                yield return Symbol(
                    "~" + destructor.Identifier.Text, SymbolKind.Constructor, destructor,
                    destructor.Identifier, text, model, token, []);
                break;

            case PropertyDeclarationSyntax property:
                yield return Symbol(
                    property.Identifier.Text, SymbolKind.Property, property, property.Identifier,
                    text, model, token, []);
                break;

            case IndexerDeclarationSyntax indexer:
                yield return Symbol(
                    "this[]", SymbolKind.Property, indexer, indexer.ThisKeyword,
                    text, model, token, []);
                break;

            case EventDeclarationSyntax @event:
                yield return Symbol(
                    @event.Identifier.Text, SymbolKind.Event, @event, @event.Identifier,
                    text, model, token, []);
                break;

            case OperatorDeclarationSyntax @operator:
                yield return Symbol(
                    "operator " + @operator.OperatorToken.Text, SymbolKind.Operator, @operator,
                    @operator.OperatorToken, text, model, token, []);
                break;

            case ConversionOperatorDeclarationSyntax conversion:
                yield return Symbol(
                    conversion.Type.ToString(), SymbolKind.Operator, conversion,
                    conversion.Type, text, model, token, []);
                break;

            // A field or event field names several variables in one statement,
            // and each of them deserves its own entry in the outline.
            case BaseFieldDeclarationSyntax field:
                var kind = field is EventFieldDeclarationSyntax
                    ? SymbolKind.Event
                    : field.Modifiers.Any(modifier => modifier.Text is "const")
                        ? SymbolKind.Constant
                        : SymbolKind.Field;

                foreach (var variable in field.Declaration.Variables)
                {
                    yield return Symbol(
                        variable.Identifier.Text, kind, variable, variable.Identifier,
                        text, model, token, []);
                }

                break;
        }
    }

    private static IReadOnlyList<DocumentSymbol> Children(
        IEnumerable<MemberDeclarationSyntax> members,
        SourceText text,
        SemanticModel? model,
        CancellationToken token)
        => members.SelectMany(member => Describe(member, text, model, token)).ToList();

    private static int KindOf(TypeDeclarationSyntax type) => type switch
    {
        InterfaceDeclarationSyntax => SymbolKind.Interface,
        StructDeclarationSyntax => SymbolKind.Struct,
        _ => SymbolKind.Class,   // class and record alike
    };

    private static DocumentSymbol Symbol(
        string name,
        int kind,
        SyntaxNode node,
        SyntaxNodeOrToken identifier,
        SourceText text,
        SemanticModel? model,
        CancellationToken token,
        IReadOnlyList<DocumentSymbol> children)
    {
        string? detail = null;

        if (model is not null)
        {
            // A declaration that does not bind -- because the file is mid-edit --
            // still belongs in the outline, just without its signature.
            var declared = model.GetDeclaredSymbol(node, token);
            if (declared is not null and not INamespaceSymbol)
                detail = declared.ToDisplayString(DetailFormat);
        }

        return new DocumentSymbol(
            string.IsNullOrEmpty(name) ? "?" : name,
            kind,
            Conversions.ToRange(text, node.Span),
            Conversions.ToRange(text, identifier.Span))
        {
            Detail = detail,
            Children = children.Count == 0 ? null : children,
        };
    }
}
