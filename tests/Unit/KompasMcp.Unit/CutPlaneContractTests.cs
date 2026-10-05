using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Agreement between the PUBLISHED shape of a plane and the contract that accepts it.</summary>
/// <remarks>TEST: the check walks the chain "published schema → schema-valid JSON → DTO → contract
/// outcome", and a nested object is the discriminating control: it MUST be rejected by the schema.
/// LIMIT: comparing <c>schemas/*.json</c> with <c>tools/list</c> cannot catch this class of defect by
/// construction — both sides are the same schema. MEASURED (client acceptance B3, 19.09.2026, three FAIL
/// rows, defect <c>PLANE-BASE-DECLARED-REFUSAL-UNREACHABLE</c>): the published schema made
/// <c>plane.base</c> a string and <c>plane.offset_mm</c> a number while the DTO expected an OBJECT one
/// level deeper, so a call matching the published schema failed parsing and the declared refusal was
/// unreachable. History: docs/decisions/tests.md#cut-plane-contract</remarks>
public sealed class CutPlaneContractTests
{
    private static JsonObject CutPlaneSchema() =>
        (JsonObject)ToolCatalog.SharedDefinitions["cut_plane"]!.DeepClone();

    /// <summary>Validator root: the plane schema itself plus the dictionary it references.</summary>
    private static JsonObject CutPlaneRoot()
    {
        var root = CutPlaneSchema();
        root["$defs"] = ToolCatalog.SharedDefinitions.DeepClone();
        return root;
    }

    private static JsonObject PlaneProperty()
    {
        var tool = ToolCatalog.All.Single(t => t.Name == "kompas_split");
        var properties = (JsonObject)tool.InputSchema["properties"]!;
        return (JsonObject)properties["plane"]!;
    }

    private static IReadOnlyList<string> BaseEnumValues()
    {
        var schema = CutPlaneSchema();
        var properties = (JsonObject)schema["properties"]!;
        var baseSchema = (JsonObject)properties["base"]!;
        return ((JsonArray)baseSchema["enum"]!).Select(node => node!.GetValue<string>()).ToArray();
    }

    private static CutPlaneDto Deserialize(JsonObject payload) =>
        JsonSerializer.Deserialize<CutPlaneDto>(payload.ToJsonString(), KompJson.Options)
        ?? throw new InvalidOperationException("payload плоскости не разобран как CutPlaneDto");

    [Fact]
    public void PlaneBase_IsPublishedAsAString_NotAsANestedObject()
    {
        var schema = CutPlaneSchema();
        var properties = (JsonObject)schema["properties"]!;
        var baseSchema = (JsonObject)properties["base"]!;

        // Nullable(Enum(...)) is expressed as a type array — that is "string or null".
        var types = ((JsonArray)baseSchema["type"]!).Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "string", "null" }, types);
        Assert.True(properties.ContainsKey("offset_mm"),
            "offset_mm объявлен в описании поля base и обязан быть в схеме: объявленный параметр, "
            + "которого нет в схеме, невидим продукту.");
    }

    [Fact]
    public void EveryPublishedBaseValue_IsAcceptedByTheContract_AndReachesTheDeclaredRefusal()
    {
        var values = BaseEnumValues();
        Assert.Equal(3, values.Count);

        foreach (var value in values)
        {
            var payload = new JsonObject { ["base"] = value, ["offset_mm"] = 0 };
            var violations = JsonSchemaValidator.Validate(CutPlaneRoot(), payload).ToArray();
            Assert.True(violations.Length == 0,
                $"опубликованное значение base={value} отвергнуто собственной схемой: "
                + string.Join("; ", violations.Select(v => $"{v.Path} {v.Keyword} {v.Message}")));

            var dto = Deserialize(payload);
            Assert.NotNull(dto.Base);
            Assert.Equal(value, dto.Base!.Value.ToString().ToLowerInvariant());
            Assert.Equal(0d, dto.OffsetMm);

            // INVARIANT: the declared outcome must be REACHABLE — the old schema/DTO desync made exactly
            // this check unreachable.
            Assert.Equal(CutPlaneFormVerdict.BaseUnsupported, CutPlaneForm.Validate(dto));
        }
    }

    [Fact]
    public void BaseWithoutOffset_IsStillTheDeclaredRefusal()
    {
        var payload = new JsonObject { ["base"] = BaseEnumValues()[0] };
        Assert.Empty(JsonSchemaValidator.Validate(CutPlaneRoot(), payload));
        Assert.Equal(CutPlaneFormVerdict.BaseUnsupported, CutPlaneForm.Validate(Deserialize(payload)));
    }

    [Fact]
    public void ANestedObjectInBase_IsRejectedByTheSchema()
    {
        // Discriminating control: the old (wrong) DTO shape would accept exactly this, so the check must
        // fail if the shape becomes an object again.
        var payload = new JsonObject
        {
            ["base"] = new JsonObject { ["base"] = "xy", ["offset_mm"] = 0 },
        };

        Assert.NotEmpty(JsonSchemaValidator.Validate(CutPlaneRoot(), payload));
    }

    [Fact]
    public void UnknownBaseValue_IsRejectedByTheSchema()
    {
        var payload = new JsonObject { ["base"] = "zz" };
        Assert.NotEmpty(JsonSchemaValidator.Validate(CutPlaneRoot(), payload));
    }

    [Fact]
    public void OffsetWithoutBase_IsAnArgumentDefect_NotSilence()
    {
        var dto = Deserialize(new JsonObject { ["offset_mm"] = 10 });
        Assert.Equal(CutPlaneFormVerdict.OffsetWithoutBase, CutPlaneForm.Validate(dto));
    }

    [Fact]
    public void BaseTogetherWithAnotherWay_IsAConflict_NotTheDeclaredRefusal()
    {
        var withPoint = Deserialize(new JsonObject
        {
            ["base"] = "xy",
            ["point_mm"] = new JsonArray(0, 0, 0),
            ["normal_mm"] = new JsonArray(0, 0, 1),
        });
        Assert.Equal(CutPlaneFormVerdict.ModesConflict, CutPlaneForm.Validate(withPoint));

        var withReference = Deserialize(new JsonObject { ["base"] = "xy", ["plane_ref"] = "plane:1" });
        Assert.Equal(CutPlaneFormVerdict.ModesConflict, CutPlaneForm.Validate(withReference));
    }

    [Fact]
    public void TheTwoSupportedForms_StayOk()
    {
        var byPoint = Deserialize(new JsonObject
        {
            ["point_mm"] = new JsonArray(0, 0, 10),
            ["normal_mm"] = new JsonArray(0, 0, 1),
        });
        Assert.Equal(CutPlaneFormVerdict.Ok, CutPlaneForm.Validate(byPoint));

        var byReference = Deserialize(new JsonObject { ["plane_ref"] = "plane:1" });
        Assert.Equal(CutPlaneFormVerdict.Ok, CutPlaneForm.Validate(byReference));
    }

    [Fact]
    public void PlaneSchemaOfTheTool_ResolvesToTheSameShapeAsTheSharedDefinition()
    {
        // The tool references $defs/cut_plane, and the Host validates the call through exactly this reference.
        var plane = PlaneProperty();
        Assert.Equal("#/$defs/cut_plane", plane["$ref"]?.GetValue<string>());
    }
}
