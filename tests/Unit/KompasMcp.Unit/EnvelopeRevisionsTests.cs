using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Which revision the envelope reports after an operation, especially a refused one.</summary>
/// <remarks>WHY. A refused mutation left the model changed but reported `revision_after: null`, and the
/// client's next call failed REVISION_CONFLICT until it re-read the context. The rule is pure - payload
/// first, error details second - so it is held here without a live session.
/// History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
public class EnvelopeRevisionsTests
{
    [Fact]
    public void SucceededPayloadWins()
    {
        var result = new JsonObject { ["revision"] = 18 };
        var details = new JsonObject { ["revision_after"] = 99 };

        Assert.Equal(18, EnvelopeRevisions.After(result, details));
    }

    [Fact]
    public void FailedMutationReadsTheErrorDetails()
    {
        // The failure path: no result payload at all, and the revision the Worker read at the refusal.
        var details = new JsonObject { ["revision_after"] = 7 };
        Assert.Equal(7, EnvelopeRevisions.After(null, details));
    }

    [Fact]
    public void PayloadRevisionAfterIsAlsoRead()
    {
        var result = new JsonObject { ["revision_after"] = 5 };
        Assert.Equal(5, EnvelopeRevisions.After(result, null));
    }

    [Fact]
    public void NothingStatedMeansNull()
    {
        Assert.Null(EnvelopeRevisions.After(null, null));
        Assert.Null(EnvelopeRevisions.After(new JsonObject(), new JsonObject()));
        // A details object that names the revision as something other than a number is not a revision.
        Assert.Null(EnvelopeRevisions.After(null, new JsonObject { ["revision_after"] = "неизвестно" }));
    }

    [Fact]
    public void NonObjectPayloadIsNotIndexed()
    {
        // MEASURED: a read tool answers with an ARRAY (kompas_list_bodies, list_features, list_dimensions).
        // JsonNode.this[string] THROWS on a non-object node, and the throw escaped the invoker as an
        // unhandled InvalidOperationException - a whole acceptance run died on it. An array states no
        // revision; the error details still do.
        var array = new JsonArray(1, 2, 3);
        Assert.Null(EnvelopeRevisions.After(array, null));
        Assert.Equal(4, EnvelopeRevisions.After(array, new JsonObject { ["revision_after"] = 4 }));
        Assert.Null(EnvelopeRevisions.After(JsonValue.Create("строка"), null));
    }
}
