using Microsoft.CodeAnalysis;

namespace CsLite;

/// <summary>
/// Turns "the solution used to look like this, now it looks like that" into the
/// edits an editor can apply.
/// </summary>
/// <remarks>
/// Both rename and code fixes work by handing back a whole modified solution,
/// so the difference between the two is where the edits come from.
/// </remarks>
internal static class Edits
{
    public static async Task<WorkspaceEdit?> BetweenAsync(
        Solution before, Solution after, CancellationToken token)
    {
        var changes = new Dictionary<string, IReadOnlyList<TextEdit>>();

        foreach (var projectChange in after.GetChanges(before).GetProjectChanges())
        {
            foreach (var id in projectChange.GetChangedDocuments())
            {
                var original = before.GetDocument(id);
                var revised = after.GetDocument(id);
                if (original?.FilePath is not { Length: > 0 } path || revised is null) continue;

                var text = await original.GetTextAsync(token).ConfigureAwait(false);

                // Edits are expressed against the original text, so they have to
                // be ordered and non-overlapping for the editor to apply them in
                // one pass.
                var edits = (await revised.GetTextChangesAsync(original, token).ConfigureAwait(false))
                    .OrderBy(change => change.Span.Start)
                    .Select(change => new TextEdit(
                        Conversions.ToRange(text, change.Span),
                        change.NewText ?? string.Empty))
                    .ToList();

                if (edits.Count > 0) changes[Uris.FromPath(path)] = edits;
            }
        }

        return changes.Count == 0 ? null : new WorkspaceEdit(changes);
    }
}
