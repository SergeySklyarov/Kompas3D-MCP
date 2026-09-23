using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// The same JSON value must read back identically whether the node was built in memory or arrived
/// as parsed text. That equivalence is not free: JsonNode stores either the CLR value or a
/// JsonElement, and the strict accessors only accept one of them.
/// </summary>
public class JsonScalarsTests
{
    private static JsonNode RoundTrip(JsonNode node) =>
        JsonNode.Parse(node.ToJsonString())!;

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4_000_000_000L)]
    [InlineData(-7L)]
    public void Long_SurvivesRoundTrip(long value)
    {
        var built = new JsonObject { ["v"] = value };
        var parsed = RoundTrip(built);

        Assert.Equal(value, JsonScalars.ReadLong(built["v"]));
        Assert.Equal(value, JsonScalars.ReadLong(parsed["v"]));
    }

    [Fact]
    public void Int_FromNumberNode_RegardlessOfStorage()
    {
        var parsed = JsonNode.Parse("""{"v":250}""")!;
        Assert.Equal(250, JsonScalars.ReadInt(parsed["v"]));
    }

    [Fact]
    public void Double_AcceptsFractionAndScientific()
    {
        var parsed = JsonNode.Parse("""{"a":1.5,"b":1e-9,"c":3}""")!;
        Assert.Equal(1.5d, JsonScalars.ReadDouble(parsed["a"])!.Value);
        Assert.Equal(1e-9d, JsonScalars.ReadDouble(parsed["b"])!.Value);
        Assert.Equal(3d, JsonScalars.ReadDouble(parsed["c"])!.Value);
    }

    [Fact]
    public void String_SurvivesRoundTrip()
    {
        var built = new JsonObject { ["v"] = "face:abc" };
        Assert.Equal("face:abc", JsonScalars.ReadString(RoundTrip(built)["v"]));
        Assert.Null(JsonScalars.ReadString(JsonNode.Parse("42")));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"yes\"", null)]
    [InlineData("null", null)]
    public void Bool_OnlyAcceptsJsonBooleans(string literal, bool? expected)
    {
        Assert.Equal(expected, JsonScalars.ReadBool(JsonNode.Parse(literal)));
    }

    [Fact]
    public void DeepClonedNode_ReadsLikeTheOriginal()
    {
        var built = new JsonObject { ["revision"] = 3L, ["kind"] = "part", ["closed"] = false };
        var clone = (JsonObject)built.DeepClone();

        Assert.Equal(3L, JsonScalars.ReadLong(clone["revision"]));
        Assert.Equal("part", JsonScalars.ReadString(clone["kind"]));
        Assert.Equal(false, JsonScalars.ReadBool(clone["closed"]));
    }

    [Fact]
    public void IndexingAnArrayByName_IsTheTrapThisClassExistsFor()
    {
        // A result is an object for single-entity reads and an ARRAY for listings, and
        // array["key"] throws instead of returning null. A diagnostic line that assumed objects
        // turned kompas_list_bodies into a protocol error, which a test then reported as "the
        // document has no bodies". Anything reading result fields must go through JsonScalars.
        var rows = JsonNode.Parse("""[{"body_ref":"body:1"}]""")!;

        Assert.True(rows is JsonArray);
        Assert.False(rows is JsonObject);
        Assert.Throws<InvalidOperationException>(() => rows["body_ref"]);
    }
}
