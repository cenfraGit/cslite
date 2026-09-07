using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsLite;

/// <summary>
/// Shows the parameters of the call the cursor is sitting inside.
/// </summary>
/// <remarks>
/// Roslyn keeps its own signature help service internal, so this walks the
/// syntax tree instead: find the innermost argument list containing the cursor,
/// ask the semantic model for the methods that name could bind to, and count
/// commas to work out which parameter is being typed.
/// <para>
/// Working from the member group rather than the resolved symbol matters,
/// because while an argument list is half-typed the call usually does not bind
/// to anything yet, and every overload is still a candidate.
/// </para>
/// </remarks>
internal static class SignatureHelpProvider
{
    /// <summary>How an individual parameter is rendered inside the signature.</summary>
    private static readonly SymbolDisplayFormat ParameterFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeDefaultValue
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut
                          | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat TypeFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static async Task<SignatureHelp?> ForAsync(
        Document document, Position position, CancellationToken token)
    {
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var offset = Conversions.ToOffset(text, position);

        var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (root is null || model is null) return null;

        // Step back one character: with the cursor just after "(", the token at
        // the offset is whatever follows the call.
        var node = root.FindToken(Math.Max(0, offset - 1)).Parent;

        while (node is not null)
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation when Surrounds(invocation.ArgumentList, offset):
                    return Build(
                        CandidatesFor(model, invocation, invocation.Expression, token),
                        invocation.ArgumentList,
                        offset);

                case ObjectCreationExpressionSyntax creation
                    when creation.ArgumentList is not null && Surrounds(creation.ArgumentList, offset):
                    return Build(Constructors(model, creation, token), creation.ArgumentList, offset);
            }

            node = node.Parent;
        }

        return null;
    }

    /// <summary>True when the cursor is between the parentheses, inclusive of both.</summary>
    private static bool Surrounds(ArgumentListSyntax arguments, int offset)
    {
        var open = arguments.OpenParenToken;
        var close = arguments.CloseParenToken;

        if (open.IsMissing) return false;

        // The closing paren is often missing while the call is still being
        // typed, in which case the list runs to the end of what exists.
        var end = close.IsMissing ? arguments.Span.End : close.SpanStart;
        return offset > open.SpanStart && offset <= end;
    }

    private static IReadOnlyList<IMethodSymbol> CandidatesFor(
        SemanticModel model, InvocationExpressionSyntax invocation, ExpressionSyntax target, CancellationToken token)
    {
        // The member group is every method the name could refer to, which is
        // what we want while the arguments are incomplete.
        var group = model.GetMemberGroup(target, token).OfType<IMethodSymbol>().ToList();
        if (group.Count > 0) return group;

        var info = model.GetSymbolInfo(invocation, token);
        if (info.Symbol is IMethodSymbol resolved) return [resolved];

        return info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
    }

    private static IReadOnlyList<IMethodSymbol> Constructors(
        SemanticModel model, ObjectCreationExpressionSyntax creation, CancellationToken token)
    {
        var info = model.GetSymbolInfo(creation, token);
        if (info.Symbol is IMethodSymbol resolved) return [resolved];

        var candidates = info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
        if (candidates.Count > 0) return candidates;

        return model.GetTypeInfo(creation.Type, token).Type is INamedTypeSymbol type
            ? type.InstanceConstructors.Where(c => !c.IsStatic).ToList()
            : [];
    }

    private static SignatureHelp? Build(
        IReadOnlyList<IMethodSymbol> candidates, ArgumentListSyntax arguments, int offset)
    {
        if (candidates.Count == 0) return null;

        var activeParameter = ActiveParameter(arguments, offset);

        var signatures = candidates
            .OrderBy(method => method.Parameters.Length)
            .Select(Describe)
            .ToList();

        // Prefer an overload that actually has a parameter in the slot being
        // typed, so the highlight lands somewhere.
        var active = signatures.FindIndex(signature => signature.Parameters.Count > activeParameter);

        return new SignatureHelp(
            signatures,
            active < 0 ? 0 : active,
            activeParameter);
    }

    /// <summary>Counts the commas between the opening paren and the cursor.</summary>
    private static int ActiveParameter(ArgumentListSyntax arguments, int offset)
    {
        var index = 0;

        foreach (var separator in arguments.Arguments.GetSeparators())
        {
            if (separator.SpanStart < offset) index++;
            else break;
        }

        return index;
    }

    private static SignatureInformation Describe(IMethodSymbol method)
    {
        var label = new StringBuilder();

        if (method.MethodKind == MethodKind.Constructor)
        {
            label.Append(method.ContainingType.ToDisplayString(TypeFormat));
        }
        else
        {
            label.Append(method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(TypeFormat));
            label.Append(' ').Append(method.Name);
        }

        label.Append('(');

        var parameters = new List<ParameterInformation>(method.Parameters.Length);

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (i > 0) label.Append(", ");

            // LSP takes the parameter's position as offsets into the label,
            // which avoids the client having to find it by substring search.
            var start = label.Length;
            label.Append(method.Parameters[i].ToDisplayString(ParameterFormat));

            parameters.Add(new ParameterInformation([start, label.Length]));
        }

        label.Append(')');

        return new SignatureInformation(label.ToString(), Features.SummaryFor(method), parameters);
    }
}
