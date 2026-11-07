using System.Text.Json.Nodes;
using KompasMcp.Contracts;

namespace KompasMcp.Host;

/// <summary>Which document a mutation addresses when it names no <c>document_id</c> but points at model
/// objects through minted references (<c>sketch_ref</c>, <c>face_ref</c>, <c>edge_refs</c>, ...).</summary>
/// <remarks>The mapping lives in the Worker registry, so it is asked for BEFORE the journal line and the
/// dispatch: an unknown outcome is the case where no reply comes back to learn it from (debt line 84).
/// INVARIANT: one owning document is used; several, none, or an unreachable registry keep the record
/// without one and carry the reason. LIMIT: the Worker then refuses such a call itself (a foreign
/// reference is STALE_REFERENCE), so nothing is applied under the missing document.
/// History: docs/decisions/host.md#reference-document</remarks>
public static class ReferenceDocument
{
    /// <summary>What the references of one call say about the document it mutates.</summary>
    /// <param name="DocumentId">The single owning document, or <c>null</c> when the call did not determine it.</param>
    /// <param name="Reason">Why the document is not determined; <c>null</c> only together with a document.</param>
    public sealed record Decision(string? DocumentId, string? Reason);

    private const string Suffix = "_ref";
    private const string ArraySuffix = "_refs";

    /// <summary>Argument fields that carry minted references: a name ending in <c>_ref</c> or <c>_refs</c>.</summary>
    /// <remarks>LIMIT: the rule is by NAME, not by declared type, because the published schema does not say
    /// which strings are references. A field that is an ADDRESS inside one sketch (<c>object_ref</c>,
    /// <c>partner_ref</c>) is looked up too — the registry does not know it, and an unknown reference is
    /// reported, never mistaken for a second document.</remarks>
    public static IReadOnlyList<string> Fields(JsonObject inputSchema)
    {
        if (inputSchema["properties"] is not JsonObject properties)
        {
            return Array.Empty<string>();
        }

        return properties
            .Select(pair => pair.Key)
            .Where(name => name.EndsWith(Suffix, StringComparison.Ordinal)
                           || name.EndsWith(ArraySuffix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The reference values present in the arguments, in field order; duplicates kept out.</summary>
    public static IReadOnlyList<string> Values(JsonObject arguments, IReadOnlyList<string> fields)
    {
        var values = new List<string>();
        foreach (var field in fields)
        {
            if (!arguments.TryGetPropertyValue(field, out var node) || node is null)
            {
                continue;
            }

            if (node is JsonArray list)
            {
                values.AddRange(list.Select(JsonScalars.ReadString).OfType<string>());
                continue;
            }

            if (JsonScalars.ReadString(node) is { Length: > 0 } single)
            {
                values.Add(single);
            }
        }

        return values.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The document among <paramref name="references"/>, or the reason there is none.</summary>
    /// <param name="owners">Reference → owning document, as the Worker registry answered; unknown references
    /// are absent.</param>
    /// <param name="failure">Why the registry answer is missing, when the call to the Worker failed.</param>
    public static Decision Decide(
        IReadOnlyList<string> references,
        IReadOnlyDictionary<string, string> owners,
        string? failure = null)
    {
        if (failure is not null)
        {
            return new Decision(null,
                $"реестр ссылок Worker не ответил ({failure}), поэтому документ, который меняет мутация, " +
                "не определён.");
        }

        if (references.Count == 0)
        {
            return new Decision(null,
                "в аргументах нет ни document_id, ни ссылки на объект модели: документ, который меняет " +
                "мутация, не определён.");
        }

        var documents = references
            .Select(reference => owners.TryGetValue(reference, out var document) ? document : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (documents.Count == 1)
        {
            return new Decision(documents[0], null);
        }

        if (documents.Count == 0)
        {
            return new Decision(null,
                $"ни одна из {references.Count} ссылок не найдена в реестре Worker: ссылка выпущена другой " +
                "сессией или уже отозвана, и документ мутации не определён.");
        }

        return new Decision(null,
            $"ссылки принадлежат {documents.Count} разным документам: приписать мутацию одному из них нечем. " +
            "Такое обращение Worker отвергает сам — ссылка чужого документа для него STALE_REFERENCE.");
    }
}
