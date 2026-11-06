using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>A list-valued Worker result must survive being put into the envelope and serialized.</summary>
/// <remarks>TEST: the Worker sends a correct one-element array, so the loss was inside the Host's
/// envelope/serialization path, which carries <c>ResultEnvelope&lt;JsonNode?&gt;</c> where <c>Result</c> may be
/// a JSON <i>array</i> rather than the usual object — and arrays are exactly what a first probe missed.
/// History: docs/decisions/tests.md#envelope-serialization-2</remarks>
public class EnvelopeSerializationTests
{
    [Fact]
    public void ArrayResult_SurvivesEnvelopeSerialization()
    {
        var rows = JsonNode.Parse("""[{"body_ref":"body:abc","kind":"solid","face_count":6}]""")!;

        var envelope = new ResultEnvelope<JsonNode?>
        {
            Status = OperationStatus.Succeeded,
            Result = rows,
            Verification = new VerificationDto(VerificationLevel.CallReturned, Array.Empty<NamedCheck>(), Array.Empty<string>()),
        };

        var json = JsonSerializer.Serialize(envelope, KompJson.Options);
        var round = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(JsonValueKind.Array, round["result"]!.GetValueKind());
        var array = round["result"]!.AsArray();
        Assert.Single(array);
        Assert.Equal("body:abc", array[0]!["body_ref"]!.GetValue<string>());
    }

    [Fact]
    public void ObjectResult_SurvivesEnvelopeSerialization()
    {
        var context = JsonNode.Parse("""{"id":"doc-1","revision":1,"kind":"part"}""")!;

        var envelope = new ResultEnvelope<JsonNode?>
        {
            Status = OperationStatus.Succeeded,
            Result = context,
        };

        var round = JsonSerializer.SerializeToNode(envelope, KompJson.Options)!.AsObject();

        Assert.Equal("doc-1", round["result"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void AssignedJsonNodeKeepsItsIdentity_WhenReusedAsResult()
    {
        // The Host hands the same JsonNode instance to the envelope and later serializes the
        // envelope; if any step reparents the node, the value silently disappears from the output.
        var rows = JsonNode.Parse("""[{"body_ref":"body:1"},{"body_ref":"body:2"}]""")!;

        var envelope = new ResultEnvelope<JsonNode?> { Status = OperationStatus.Succeeded, Result = rows };

        var direct = JsonSerializer.Serialize(rows, KompJson.Options);
        var viaEnvelope = JsonSerializer.SerializeToNode(envelope, KompJson.Options)!.AsObject()["result"]!.ToJsonString();

        Assert.Equal(direct, viaEnvelope);
    }

    /// <summary>A strict client rejects <c>-0</c>; the answer must never carry it.</summary>
    /// <remarks>TEST: the value is what a face normal of the bottom face is written as — <c>[-0,-0,-1]</c>.
    /// The check is on the TEXT, because that is what the client parses. Negative zero in a nested array,
    /// in an object field, as a <c>float</c>, and untouched neighbours are all part of the case.
    /// History: docs/decisions/host.md#lossless-json</remarks>
    [Fact]
    public void NegativeZero_IsWrittenAsZero_EverywhereInTheAnswer()
    {
        var result = new JsonObject
        {
            ["normal"] = new JsonArray(-0d, -0d, -1d),
            ["nested"] = new JsonObject { ["value"] = -0d, ["other"] = 2.5d },
            ["as_float"] = JsonValue.Create(-0f),
            ["untouched"] = -1d,
        };

        KompJson.NormalizeNegativeZero(result);
        var text = result.ToJsonString();

        Assert.DoesNotContain("-0", text, StringComparison.Ordinal);
        Assert.Equal("""{"normal":[0,0,-1],"nested":{"value":0,"other":2.5},"as_float":0,"untouched":-1}""", text);
    }
}
