using System.Text.Json.Nodes;
using KompasMcp.Contracts.Schema;
using KompasMcp.Domain.Schema;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// The validator must treat a payload that arrived over the wire exactly like one built in
/// memory. Parsed nodes wrap a JsonElement rather than a CLR double, and an earlier version threw
/// InvalidOperationException on every numeric argument — a request crash, not a validation result.
/// </summary>
public class JsonSchemaValidatorTests
{
    private static readonly JsonObject NumberSchema = Sch.Obj(
        "args",
        new[] { "depth_mm" },
        Sch.Props(("depth_mm", Sch.Num("мм", exclusiveMin: true, exclusiveMinValue: 0d))));

    [Theory]
    [InlineData("5", true)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("1e-9", true)]
    public void ParsedNumber_IsValidated_NotCrashed(string literal, bool expectValid)
    {
        var payload = JsonNode.Parse($$"""{"depth_mm": {{literal}}}""")!.AsObject();

        var violations = JsonSchemaValidator.Validate(NumberSchema, payload);

        Assert.Equal(expectValid, violations.Count == 0);
    }

    [Fact]
    public void ParsedNumber_InNestedArray_IsValidated()
    {
        var schema = Sch.Obj("args", new[] { "points" }, Sch.Props(
            ("points", Sch.Arr(Sch.Vec3("точка"), "множество точек", 1, 4))));
        var payload = JsonNode.Parse("""{"points":[[0,0,0],[1.5,2,3]]}""")!.AsObject();

        Assert.Empty(JsonSchemaValidator.Validate(schema, payload));
    }

    [Fact]
    public void UnknownField_IsRejected_NotIgnored()
    {
        var payload = JsonNode.Parse("""{"depth_mm":5,"surprise":true}""")!.AsObject();

        var violations = JsonSchemaValidator.Validate(NumberSchema, payload);

        var violation = Assert.Single(violations);
        Assert.Equal("additionalProperties", violation.Keyword);
        Assert.Equal("$/surprise", violation.Path);
    }

    [Fact]
    public void MissingRequired_IsReportedWithItsOwnPath()
    {
        var violations = JsonSchemaValidator.Validate(NumberSchema, new JsonObject());

        var violation = Assert.Single(violations);
        Assert.Equal("required", violation.Keyword);
        Assert.Equal("$/depth_mm", violation.Path);
    }

    [Fact]
    public void WrongType_IsReportedInsteadOfThrowing()
    {
        var payload = JsonNode.Parse("""{"depth_mm":"five"}""")!.AsObject();

        var violations = JsonSchemaValidator.Validate(NumberSchema, payload);

        Assert.Contains(violations, v => v.Keyword == "type");
    }

    [Fact]
    public void IntegerField_RejectsFraction()
    {
        var schema = Sch.Obj("args", new[] { "limit" }, Sch.Props(("limit", Sch.Int("целое", 1, 100))));
        var payload = JsonNode.Parse("""{"limit":1.5}""")!.AsObject();

        Assert.Contains(JsonSchemaValidator.Validate(schema, payload), v => v.Keyword == "type");
    }

    [Fact]
    public void EnumRejectsUnknownLiteral()
    {
        var schema = Sch.Obj("args", new[] { "mode" }, Sch.Props(("mode", Sch.Enum("режим", "append", "replace"))));

        Assert.Empty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"mode":"replace"}""")!.AsObject()));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"mode":"sideways"}""")!.AsObject()));
    }

    [Fact]
    public void RefResolvesAgainstSharedDefinitions()
    {
        var schema = Sch.WithDefs(
            Sch.Obj("args", new[] { "operation_id" }, Sch.Props(("operation_id", Sch.Ref("#/$defs/operation_id")))),
            Sch.Props(("operation_id", Sch.Uuid())));

        Assert.Empty(JsonSchemaValidator.Validate(schema, JsonNode.Parse($$"""{"operation_id":"{{Guid.NewGuid()}}"}""")!.AsObject()));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"operation_id":"not-a-uuid"}""")!.AsObject()));
    }

    [Fact]
    public void NullableRefBranch_AcceptsNullAndRejectsGarbage()
    {
        var schema = Sch.WithDefs(
            Sch.Obj("args", Array.Empty<string>(), Sch.Props(("path", Sch.Nullable(Sch.Ref("#/$defs/path"))))),
            Sch.Props(("path", Sch.Str())));

        Assert.Empty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"path":null}""")!.AsObject()));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"path":42}""")!.AsObject()));
    }

    [Fact]
    public void ValidatorReadsClonedSchema()
    {
        // Every published schema is DeepCloned on its way into the catalog (shared $defs, nullable
        // wrappers). Cloning changes a node's backing store to JsonElement, and GetValue<T> on such
        // a node throws — which previously surfaced as an unhandled server error for any numeric
        // argument. Validating the clone is therefore the shape that matters.
        var cloned = (JsonObject)NumberSchema.DeepClone();
        var payload = JsonNode.Parse("""{"depth_mm":10}""")!.AsObject();

        var violations = JsonSchemaValidator.Validate(cloned, payload);

        Assert.Empty(violations);
        Assert.NotEmpty(JsonSchemaValidator.Validate(cloned, JsonNode.Parse("""{"depth_mm":-10}""")!.AsObject()));
    }

    [Fact]
    public void ClonedSchemaStillRejectsUnknownField()
    {
        var cloned = (JsonObject)NumberSchema.DeepClone();
        var payload = JsonNode.Parse("""{"depth_mm":10,"extra":1}""")!.AsObject();

        Assert.Contains(JsonSchemaValidator.Validate(cloned, payload), v => v.Keyword == "additionalProperties");
    }

    [Fact]
    public void StringConstraints_AreCheckedOnClonedSchema()
    {
        var schema = (JsonObject)Sch.Obj(
            "args",
            new[] { "kind" },
            Sch.Props(("kind", Sch.Str("вид", pattern: "^[a-z]+$")))).DeepClone();

        Assert.Empty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"kind":"line"}""")!.AsObject()));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"kind":"LINE"}""")!.AsObject()));
    }

    [Fact]
    public void ArrayBounds_AreCheckedOnClonedSchema()
    {
        var schema = (JsonObject)Sch.Obj(
            "args",
            new[] { "v" },
            Sch.Props(("v", Sch.Arr(Sch.Num(), "три числа", 3, 3)))).DeepClone();

        Assert.Empty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"v":[1,2,3]}""")!.AsObject()));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, JsonNode.Parse("""{"v":[1,2]}""")!.AsObject()));
    }

    [Fact]
    public void UnsupportedKeyword_FailsSchemaCheck_NotSilentlyIgnored()
    {
        // A schema the validator cannot fully interpret must not be used: it would advertise more
        // strictness than it enforces.
        var schema = Sch.Obj("args", Array.Empty<string>(), Sch.Props(("x", new JsonObject { ["type"] = "string", ["pattern"] = "ok", ["dependentRequired"] = new JsonObject() })));

        Assert.Throws<InvalidOperationException>(() => JsonSchemaValidator.AssertSchemaIsUnderstood(schema));
    }

    [Fact]
    public void SeveralViolations_AreAllReported()
    {
        var payload = JsonNode.Parse("""{"depth_mm":-1,"extra":1}""")!.AsObject();

        var violations = JsonSchemaValidator.Validate(NumberSchema, payload);

        Assert.Equal(2, violations.Count);
    }
}
