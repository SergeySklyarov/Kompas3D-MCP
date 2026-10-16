using System.Text.Json.Nodes;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Verification;

/// <summary>The verification the envelope publishes for a result that carries its OWN reached level.</summary>
/// <remarks>INVARIANT: the envelope's level and its unverified aspects describe the RESULT, never the call
/// the client made. MEASURED defect: `kompas_export_step` answered `reached_level=syntax_checked` inside the
/// result while the envelope said `call_returned`, and the aspects were one fixed sentence about the payload
/// instead of the ones the result carried. A payload that publishes a full verification block is handled
/// earlier and is not this rule's business; so is the conservative floor for a payload with no level.
/// History: docs/decisions/host.md#verification-aggregation</remarks>
public static class ResultVerification
{
    /// <summary>Payload field carrying the level the call reached.</summary>
    public const string LevelField = "reached_level";

    /// <summary>Payload field carrying what the reached level does NOT cover.</summary>
    public const string AspectsField = "unverified_aspects";

    /// <summary>Aspect named when the payload reached a level but published no aspects at all — a bare
    /// empty list would read as "everything is confirmed".</summary>
    public const string NoAspects = "unverified_aspects_not_published_by_worker_payload";

    /// <summary>The block, or <c>null</c> when the payload is not an object, the call did not succeed, or
    /// no level was published — the caller then keeps its own conservative floor.</summary>
    public static VerificationDto? From(JsonNode? result, OperationStatus status)
    {
        if (status != OperationStatus.Succeeded || result is not JsonObject payload)
        {
            return null;
        }

        var text = JsonScalars.ReadString(payload[LevelField]);
        if (text is null || !TryParse(text, out var level))
        {
            return null;
        }

        var aspects = Strings(payload[AspectsField]);
        return new VerificationDto(
            level,
            new[]
            {
                new NamedCheck("host_validation", true),
                new NamedCheck("worker_reached_level", true, Observed: text),
            },
            aspects.Count > 0 ? aspects : new[] { NoAspects });
    }

    /// <summary>The wire spelling is lower_snake (<c>syntax_checked</c>) while the enum member is
    /// PascalCase (<c>SyntaxChecked</c>), so the separator is dropped before the comparison.</summary>
    public static bool TryParse(string text, out VerificationLevel level) =>
        Enum.TryParse(text.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out level);

    /// <summary>Strings of an array field, in order; an absent field, a non-array and non-string items are
    /// skipped rather than guessed.</summary>
    public static IReadOnlyList<string> Strings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        var found = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (JsonScalars.ReadString(item) is { Length: > 0 } text)
            {
                found.Add(text);
            }
        }

        return found;
    }
}
