using System.Text.Json.Nodes;
using KompasMcp.Contracts.Schema;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The rotation contract is checked against the PUBLISHED tool description, not the schema file nor
/// the adapter code.</summary>
/// <remarks>TEST: the reason is measured — a field declared only in C# or only in <c>schemas/*.json</c> is
/// INVISIBLE to the client, because the Host validates the call by its own <c>InputSchema</c>, and the
/// mismatch reads as "the product cannot do it" when the declaration is what is missing (as happened with
/// <c>base_object_refs</c> on fillet). INVARIANT: <c>angle_deg</c> is bounded at 360, not 180 (measured
/// 18.09.2026; experiment <c>F.1</c> got a full cylinder <c>π·r²·h</c> at <c>Angle[true]=360</c>), and the
/// angle of an existing rotation is edited via <c>rotation_angle_deg</c>, not <c>angle_deg</c>.
/// History: docs/decisions/tests.md#rotation-contract</remarks>
public class RotationContractTests
{
    private static JsonObject Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)?.InputSchema
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    private static JsonObject Props(JsonObject schema) =>
        schema["properties"] as JsonObject
        ?? throw new InvalidOperationException("Схема не объявляет properties.");

    /// <summary>INVARIANT: the angle upper bound is a FULL turn, not half. Checked by TWO numbers: 360 must
    /// pass, 361 must be refused. "360 passes" alone is not enough — it would pass with no bound at all, so
    /// the test would not tell the measured ceiling from its absence.</summary>
    [Fact]
    public void Rotated_AngleBound_IsFullTurn_NotHalfTurn()
    {
        var schema = Tool("kompas_rotated");
        var payload360 = JsonNode.Parse(
            """{"sketch_ref":"sketch:0","expected_revision":1,"operation":"base","angle_deg":360,"axis_point1_mm":[0,0,0],"axis_point2_mm":[0,40,0],"operation_id":"00000000-0000-0000-0000-000000000000"}""")!
            .AsObject();
        var payload361 = JsonNode.Parse(
            """{"sketch_ref":"sketch:0","expected_revision":1,"operation":"base","angle_deg":361,"axis_point1_mm":[0,0,0],"axis_point2_mm":[0,40,0],"operation_id":"00000000-0000-0000-0000-000000000000"}""")!
            .AsObject();

        Assert.Empty(JsonSchemaValidator.Validate(schema, payload360));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, payload361));
    }

    /// <summary>INVARIANT: the axis is declared with EXACTLY three numbers per point. Two would leave the
    /// third coordinate to the server's guess; four would be a silently dropped value.</summary>
    [Theory]
    [InlineData("""[0,0]""")]
    [InlineData("""[0,0,0,0]""")]
    public void Rotated_AxisPoint_DemandsExactlyThreeNumbers(string vector)
    {
        var schema = Tool("kompas_rotated");
        var payload = JsonNode.Parse(
            $$"""{"sketch_ref":"sketch:0","expected_revision":1,"operation":"base","angle_deg":90,"axis_point1_mm":{{vector}},"axis_point2_mm":[0,40,0],"operation_id":"00000000-0000-0000-0000-000000000000"}""")!
            .AsObject();

        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, payload));
    }

    /// <summary>INVARIANT: the operation kind is an enumeration, not a string — an unknown value must be
    /// refused BEFORE COM, not coerced to the nearest kind.</summary>
    [Fact]
    public void Rotated_Operation_IsAClosedEnumeration()
    {
        var schema = Tool("kompas_rotated");
        var payload = JsonNode.Parse(
            """{"sketch_ref":"sketch:0","expected_revision":1,"operation":"loft","angle_deg":90,"axis_point1_mm":[0,0,0],"axis_point2_mm":[0,40,0],"operation_id":"00000000-0000-0000-0000-000000000000"}""")!
            .AsObject();

        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, payload));
    }

    /// <summary>INVARIANT: the rotation edit publishes its OWN angle and direction fields rather than reusing
    /// the chamfer's. Without this the rotation feature cannot be edited: <c>angle_deg</c> belongs to the
    /// chamfer, and the adapter deliberately rejects it on rotation.</summary>
    [Fact]
    public void UpdateFeature_PublishesRotationEditFields()
    {
        var props = Props(Tool("kompas_update_feature"));

        Assert.True(props.ContainsKey("rotation_angle_deg"),
            "kompas_update_feature не публикует rotation_angle_deg — угол существующего вращения " +
            "править нечем, хотя маршрут измерен (RO.16/RO.16b).");
        Assert.True(props.ContainsKey("rotation_direction"),
            "kompas_update_feature не публикует rotation_direction — направление существующего " +
            "вращения править нечем.");
    }

    /// <summary>INVARIANT: reading the feature publishes the rotation parameter block. An empty field means
    /// "not read", not zero, so the client needs the object itself, not only the family name.</summary>
    [Fact]
    public void GetFeature_PublishesRotationReadback()
    {
        var props = Props(Tool("kompas_get_feature"));

        Assert.True(props.ContainsKey("feature_ref"),
            "kompas_get_feature не публикует feature_ref, по которому адресуется вращение.");
    }

    /// <summary>INVARIANT: the tool description must name the MEASURED capability, not the old limit. The old
    /// text claimed a full turn by cut is not expressible and a boss onto a non-empty part is refused — both
    /// refuted 18.09.2026 (experiments F.9/F.10, acceptance RO.18/RO.19): "saturation" came from a one-sided
    /// blank, and "the boss makes a second body" from a probe writing NewBody. The test catches the return of
    /// the refuted text. History: docs/decisions/tests.md#rotation-contract</summary>
    [Fact]
    public void Rotated_Description_StatesTheMeasuredFullTurnAndBossFusion()
    {
        var tool = ToolCatalog.All.SingleOrDefault(t => t.Name == "kompas_rotated")
            ?? throw new InvalidOperationException("kompas_rotated не найден в каталоге.");
        var description = tool.Description;

        Assert.Contains("cut: ПОЛНЫЙ ОБОРОТ ВЫРАЖАЕТСЯ", description, StringComparison.Ordinal);
        Assert.Contains("БОБЫШКА СРАЩИВАЕТСЯ", description, StringComparison.Ordinal);
        Assert.DoesNotContain("ПОЛНЫЙ ОБОРОТ НЕ ВЫРАЖАЕТСЯ", description, StringComparison.Ordinal);
        Assert.DoesNotContain("ВИД boss НА НЕПУСТОЙ ДЕТАЛИ ОТВЕРГАЕТСЯ", description, StringComparison.Ordinal);
        Assert.DoesNotContain("target_body_ref отвергается", description, StringComparison.Ordinal);
        Assert.DoesNotContain("НЕ переключает (измерено R.26)", description, StringComparison.Ordinal);
    }

    /// <summary>INVARIANT: <c>target_body_ref</c> must carry a DESCRIPTION, not be a bare reference — the field
    /// is ACCEPTED and checked AFTER the operation, and the client must know that, else it reads as
    /// "unsupported" and the target is never given. The check also guards the schema builder: <c>Nullable()</c>
    /// rebuilds <c>$ref</c> into <c>anyOf</c> and silently drops a description placed on the inner <c>$ref</c>.</summary>
    [Fact]
    public void Rotated_TargetBodyRef_CarriesItsMeasuredMeaning()
    {
        var props = Props(Tool("kompas_rotated"));
        var target = props["target_body_ref"] as JsonObject
            ?? throw new InvalidOperationException("kompas_rotated не объявляет target_body_ref.");

        var description = target["description"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.Contains("ПОСЛЕ операции", description!, StringComparison.Ordinal);
    }
}
