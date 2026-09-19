using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;

namespace CsLite;

/// <summary>
/// The fixes offered for the errors under the cursor: adding a missing using,
/// generating a member, removing unreachable code, and so on.
/// </summary>
/// <remarks>
/// Roslyn keeps its fix service internal, so the providers are found by
/// reflecting over the Features assemblies and instantiating the ones that
/// declare themselves for C#. A provider whose constructor wants MEF imports we
/// cannot supply is skipped; that costs us the fix it offered, not the feature.
/// <para>
/// Fixes come from the compiler's own diagnostics, so this stays inside the
/// rule the rest of the server follows: nothing is executed on your behalf, and
/// no analyzer assembly of yours is ever loaded.
/// </para>
/// </remarks>
internal static class CodeActions
{
    /// <summary>Enough to fill a menu; past this the list stops being a choice.</summary>
    private const int Limit = 32;

    private static readonly Lazy<IReadOnlyList<CodeFixProvider>> Providers = new(Discover);

    /// <summary>Fix providers indexed by the diagnostic they claim to fix.</summary>
    private static readonly Lazy<ILookup<string, CodeFixProvider>> ByDiagnostic = new(
        () => Providers.Value
            .SelectMany(provider => provider.FixableDiagnosticIds.Select(id => (id, provider)))
            .ToLookup(pair => pair.id, pair => pair.provider, StringComparer.Ordinal));

    public static async Task<IReadOnlyList<CodeActionItem>> ForAsync(
        Document document, Range range, CancellationToken token)
    {
        var text = await document.GetTextAsync(token).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
        if (model is null) return [];

        var span = ToSpan(text, range);
        var results = new List<CodeActionItem>();

        // A cursor sitting on an error is a zero-width span, which would match
        // nothing, so a diagnostic counts when it merely touches the selection.
        var diagnostics = model.GetDiagnostics(cancellationToken: token)
            .Where(diagnostic => diagnostic.Location.IsInSource
                                 && diagnostic.Location.SourceSpan.IntersectsWith(span))
            .ToList();

        foreach (var diagnostic in diagnostics)
        {
            foreach (var provider in ByDiagnostic.Value[diagnostic.Id])
            {
                if (results.Count >= Limit) break;

                var offered = new List<RoslynCodeAction>();
                var context = new CodeFixContext(
                    document,
                    diagnostic,
                    (action, _) => offered.Add(action),
                    token);

                try
                {
                    await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Log.Debug($"{provider.GetType().Name} failed on {diagnostic.Id}: {error.Message}");
                    continue;
                }

                foreach (var action in Flatten(offered))
                {
                    if (results.Count >= Limit) break;

                    var item = await DescribeAsync(action, document, diagnostic, text, token)
                        .ConfigureAwait(false);

                    if (item is not null) results.Add(item);
                }
            }
        }

        // The same fix can be offered for two overlapping diagnostics.
        return results
            .GroupBy(item => item.Title, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// Expands one level of nesting, which is how "add using" offers its
    /// alternative namespaces.
    /// </summary>
    private static IEnumerable<RoslynCodeAction> Flatten(IEnumerable<RoslynCodeAction> actions)
    {
        foreach (var action in actions)
        {
            if (action.NestedActions.IsDefaultOrEmpty)
            {
                yield return action;
                continue;
            }

            foreach (var nested in action.NestedActions) yield return nested;
        }
    }

    private static async Task<CodeActionItem?> DescribeAsync(
        RoslynCodeAction action,
        Document document,
        Microsoft.CodeAnalysis.Diagnostic diagnostic,
        Microsoft.CodeAnalysis.Text.SourceText text,
        CancellationToken token)
    {
        ImmutableArray<CodeActionOperation> operations;
        try
        {
            operations = await action.GetOperationsAsync(token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Debug($"could not compute '{action.Title}': {error.Message}");
            return null;
        }

        // Anything that is not a plain edit would have to run on our side --
        // opening documents, starting downloads -- which is out of scope.
        var change = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (change is null) return null;

        var edit = await Edits
            .BetweenAsync(document.Project.Solution, change.ChangedSolution, token)
            .ConfigureAwait(false);

        if (edit is null) return null;

        return new CodeActionItem(action.Title)
        {
            Kind = "quickfix",
            Diagnostics =
            [
                new Diagnostic(
                    Conversions.ToRange(text, diagnostic.Location.SourceSpan),
                    diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error ? 1 : 2,
                    diagnostic.Id,
                    "cslite",
                    diagnostic.GetMessage()),
            ],
            Edit = edit,
        };
    }

    private static Microsoft.CodeAnalysis.Text.TextSpan ToSpan(
        Microsoft.CodeAnalysis.Text.SourceText text, Range range)
    {
        var start = Conversions.ToOffset(text, range.Start);
        var end = Conversions.ToOffset(text, range.End);
        return Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
    }

    // -----------------------------------------------------------------------
    // Finding the providers
    // -----------------------------------------------------------------------

    private static IReadOnlyList<CodeFixProvider> Discover()
    {
        var providers = new List<CodeFixProvider>();
        var skipped = 0;

        foreach (var assemblyName in new[]
                 {
                     "Microsoft.CodeAnalysis.Features",
                     "Microsoft.CodeAnalysis.CSharp.Features",
                 })
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(assemblyName);
            }
            catch (Exception error)
            {
                Log.Warn($"could not load {assemblyName}: {error.Message}");
                continue;
            }

            foreach (var type in TypesOf(assembly))
            {
                if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type)) continue;

                var export = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                if (export is null) continue;
                if (!export.Languages.Contains(LanguageNames.CSharp, StringComparer.Ordinal)) continue;

                try
                {
                    // Providers that take MEF imports have no usable constructor
                    // here; there is nothing to do but leave them out.
                    if (Activator.CreateInstance(type) is CodeFixProvider provider)
                        providers.Add(provider);
                }
                catch (Exception)
                {
                    skipped++;
                }
            }
        }

        Log.Info($"code fixes: {providers.Count} providers available, {skipped} needed services we do not host");
        return providers;
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            // A partially loadable assembly still gives us most of its types.
            return error.Types.OfType<Type>();
        }
        catch (Exception error)
        {
            Log.Debug($"could not read types from {assembly.GetName().Name}: {error.Message}");
            return [];
        }
    }
}
