namespace KompasMcp.Domain.Documents;

/// <summary>The contract of <c>kompas_import_step</c>: which documents the answer names, and what a
/// failed route may leave behind.</summary>
/// <remarks>INVARIANT: <c>created_document_ids</c> carries HOST ids - what the session registry
/// (<c>kompas_list_documents</c>) issues and every addressing tool accepts - never a COM handle.
/// INVARIANT: a failed route leaves no REGISTERED document; only what could not be removed is named,
/// so a refusal never claims a clean session it does not have.
/// History: docs/decisions/adapter-core.md#import-step-contract</remarks>
public static class ImportStepContract
{
    /// <summary>The ids the session registry gained between two snapshots, in a deterministic order.
    /// The registry is what <c>kompas_list_documents</c> answers, so these are exactly the ids a client
    /// can address; a before/after comparison of COM handles is not a document identity.</summary>
    public static IReadOnlyList<string> CreatedDocumentIds(
        IEnumerable<string> registeredBefore,
        IEnumerable<string> registeredAfter)
    {
        var before = new HashSet<string>(registeredBefore, StringComparer.Ordinal);
        return registeredAfter
            .Where(id => !before.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Documents a route created but could not remove: created minus removed. Non-empty means
    /// the refusal must carry <c>partial_effects = true</c> and name them, instead of answering as if
    /// the session were unchanged.</summary>
    public static IReadOnlyList<string> DocumentsLeftOpen(
        IEnumerable<string> created,
        IEnumerable<string> removed)
    {
        var gone = new HashSet<string>(removed, StringComparer.Ordinal);
        return created
            .Where(id => !gone.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }
}
