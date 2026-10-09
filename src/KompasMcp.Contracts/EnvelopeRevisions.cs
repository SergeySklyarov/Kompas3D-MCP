using System.Text.Json.Nodes;

namespace KompasMcp.Contracts;

/// <summary>Which revision a result envelope reports as the one to continue from.</summary>
/// <remarks>WHY THE ERROR DETAILS ARE READ AT ALL. A succeeded call carries the new revision in its result
/// payload. A FAILED mutation has NO payload - the Worker answers with an error frame only - so the
/// envelope reported <c>revision_after: null</c> even when the model had already moved (a refused
/// extrusion left its feature in the tree), and the client's next call failed <c>REVISION_CONFLICT</c>.
/// The Worker states the document's current revision in the error's details and the envelope falls back
/// to it. INVARIANT: the payload still wins - a success is never overridden by an error field.
/// History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
public static class EnvelopeRevisions
{
    /// <summary>The revision after the operation, from the result payload first and the error details
    /// second; <c>null</c> when neither states one.</summary>
    /// <remarks>INVARIANT: a payload that is NOT an object (a read tool answers with an array - bodies,
    /// features, dimensions) states no revision and must not be indexed: <c>JsonNode.this[string]</c>
    /// THROWS on a non-object node, and the throw escapes the invoker as an unhandled
    /// <c>InvalidOperationException</c> instead of a result. Measured on <c>kompas_list_bodies</c>.</remarks>
    public static long? After(JsonNode? result, JsonObject? errorDetails)
    {
        var payload = result as JsonObject;
        return JsonScalars.ReadLong(payload?["revision"])
            ?? JsonScalars.ReadLong(payload?["revision_after"])
            ?? (errorDetails is null ? null : JsonScalars.ReadLong(errorDetails["revision_after"]));
    }
}
