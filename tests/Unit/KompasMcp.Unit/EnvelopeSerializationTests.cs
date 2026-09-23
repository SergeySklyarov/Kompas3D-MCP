using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// A list-valued Worker result must survive being put into the envelope and serialized.
/// </summary>
/// <remarks>
/// Regression test for the "kompas_list_bodies returns []" defect. The Worker was proven to send
/// a correct one-element array (verified on the pipe: body_ref, kind=solid, bbox 100×80×10,
/// face_count 6), so the loss had to be inside the Host's envelope/serialization path. That path
/// carries <c>ResultEnvelope&lt;JsonNode?&gt;</c> where <c>Result</c> may be a JSON <i>array</i>
/// rather than the usual object, and arrays are exactly what the first probe missed.
/// </remarks>
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
}
