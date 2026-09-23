using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Соответствие ОПУБЛИКОВАННОЙ формы плоскости и контракта, который её принимает.
/// </summary>
/// <remarks>
/// <para>
/// Класс заведён по измеренному дефекту <c>PLANE-BASE-DECLARED-REFUSAL-UNREACHABLE</c> (клиентская
/// приёмка B3, 19.09.2026, три строки FAIL). Схема поставки и описание инструмента публиковали
/// <c>plane.base</c> строкой <c>xy|xz|yz</c> и соседний <c>plane.offset_mm</c> числом, а DTO ждал на
/// этом месте ОБЪЕКТ — форма расходилась на один уровень вложенности. Вызов, соответствующий
/// опубликованной схеме, падал на разборе payload (<c>JsonException</c> по <c>$.plane.base</c>), и
/// объявленный <c>CAPABILITY_UNAVAILABLE</c> был недостижим.
/// </para>
/// <para>
/// Сравнение <c>schemas/*.json</c> с <c>tools/list</c> этот класс дефектов не ловит по построению: обе
/// стороны — одна и та же схема. Поэтому проверка идёт по цепочке
/// «опубликованная схема → schema-valid JSON → DTO → контрактный исход», и дискриминирующим контролем
/// служит вложенный объект: он обязан быть ОТВЕРГНУТ схемой.
/// </para>
/// </remarks>
public sealed class CutPlaneContractTests
{
    private static JsonObject CutPlaneSchema() =>
        (JsonObject)ToolCatalog.SharedDefinitions["cut_plane"]!.DeepClone();

    /// <summary>Корень для валидатора: сама схема плоскости плюс словарь, на который она ссылается.</summary>
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

        // Nullable(Enum(...)) выражается массивом типов — это и есть «строка или null».
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

            // Объявленный исход обязан быть ДОСТИЖИМ: именно эту проверку делал недостижимой прежний
            // рассинхрон схемы и DTO.
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
        // Дискриминирующий контроль: прежняя (ошибочная) форма DTO принимала бы именно это, поэтому
        // проверка обязана падать, если форма снова станет объектом.
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
        // Инструмент ссылается на $defs/cut_plane, и именно по этой ссылке Host валидирует вызов.
        var plane = PlaneProperty();
        Assert.Equal("#/$defs/cut_plane", plane["$ref"]?.GetValue<string>());
    }
}
