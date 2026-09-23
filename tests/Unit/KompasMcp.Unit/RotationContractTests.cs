using System.Text.Json.Nodes;
using KompasMcp.Contracts.Schema;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Контракт вращения проверяется по ОПУБЛИКОВАННОМУ описанию инструмента, а не по файлу схемы и не
/// по коду адаптера. Причина названа измерением: поле, объявленное только в C# или только в
/// <c>schemas/*.json</c>, клиенту НЕ видно — Host валидирует вызов по своему <c>InputSchema</c>, и
/// расхождение читается как «продукт не умеет», хотя на деле не хватает объявления. Так уже было с
/// <c>base_object_refs</c> у скругления.
/// </summary>
/// <remarks>
/// Границы здесь взяты из ИЗМЕРЕНИЙ 18.09.2026, а не из прежней записи:
/// <list type="bullet">
/// <item><c>angle_deg</c> ограничен 360, а не 180. Прежний предел стоял на опровергнутой посылке
/// «развёртка насыщается на 180°»; опыт <c>F.1</c> получил полный цилиндр <c>π·r²·h</c> при
/// <c>Angle[true]=360</c>. Тест ловит возврат к 180: это понизило бы возможность, подтверждённую
/// продуктом.</item>
/// <item>Правка угла у существующего вращения идёт полем <c>rotation_angle_deg</c>, а НЕ
/// <c>angle_deg</c>: у фаски своё <c>angle_deg</c>, и одно имя на два семейства сделало бы ответ
/// неоднозначным.</item>
/// </list>
/// </remarks>
public class RotationContractTests
{
    private static JsonObject Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)?.InputSchema
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    private static JsonObject Props(JsonObject schema) =>
        schema["properties"] as JsonObject
        ?? throw new InvalidOperationException("Схема не объявляет properties.");

    /// <summary>
    /// Верхняя граница угла — ПОЛНЫЙ оборот, а не половина. Проверяется ДВУМЯ числами: 360 обязан
    /// проходить, 361 обязан отвергаться. Одного «360 проходит» мало: он прошёл бы и при отсутствии
    /// верхней границы вовсе, то есть тест не отличил бы измеренный потолок от его отсутствия.
    /// </summary>
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

    /// <summary>
    /// Ось объявлена РОВНО тремя числами на точку. Два числа оставили бы третью координату на
    /// догадку сервера, а в ответе оказалась бы не та ось; четыре — молча отброшенное значение.
    /// </summary>
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

    /// <summary>
    /// Вид операции — перечисление, а не строка: неизвестное значение обязано отвергаться ДО COM, а
    /// не приводиться к ближайшему виду.
    /// </summary>
    [Fact]
    public void Rotated_Operation_IsAClosedEnumeration()
    {
        var schema = Tool("kompas_rotated");
        var payload = JsonNode.Parse(
            """{"sketch_ref":"sketch:0","expected_revision":1,"operation":"loft","angle_deg":90,"axis_point1_mm":[0,0,0],"axis_point2_mm":[0,40,0],"operation_id":"00000000-0000-0000-0000-000000000000"}""")!
            .AsObject();

        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, payload));
    }

    /// <summary>
    /// Правка вращения публикует СВОИ поля угла и направления, а не переиспользует поля фаски. Без
    /// этого признак вращения править нечем: <c>angle_deg</c> у фаски, и адаптер отвергает его на
    /// вращении намеренно.
    /// </summary>
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

    /// <summary>
    /// Чтение признака публикует блок параметров вращения. Пустое поле здесь означает «не
    /// прочитано», а не ноль, поэтому клиенту нужен сам объект, а не только имя семейства.
    /// </summary>
    [Fact]
    public void GetFeature_PublishesRotationReadback()
    {
        var props = Props(Tool("kompas_get_feature"));

        Assert.True(props.ContainsKey("feature_ref"),
            "kompas_get_feature не публикует feature_ref, по которому адресуется вращение.");
    }

    /// <summary>
    /// Описание инструмента обязано называть ИЗМЕРЕННУЮ возможность, а не прежний предел. Прежнее
    /// описание утверждало, что полный оборот вырезанием НЕ выражается и что бобышка к непустой
    /// детали отвергается, — оба утверждения опровергнуты 18.09.2026 (опыты F.9/F.10, приёмка
    /// RO.18/RO.19): «насыщение» было следствием односторонней заготовки, а «бобышка даёт второе
    /// тело» — следствием прибора, писавшего NewBody. Клиент, поверивший прежнему тексту, не
    /// попробовал бы работающий вид операции вовсе. Тест ловит возврат опровергнутого текста.
    /// </summary>
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

    /// <summary>
    /// <c>target_body_ref</c> обязан нести ОПИСАНИЕ, а не быть голой ссылкой: поле ПРИНИМАЕТСЯ и
    /// проверяется ПОСЛЕ операции, и клиенту это надо знать — иначе оно читается как «не
    /// поддержано» и цель не указывается вовсе. Проверка сторожит и способ сборки схемы:
    /// <c>Nullable()</c> пересобирает <c>$ref</c> в <c>anyOf</c> и описание, поставленное на
    /// внутренний <c>$ref</c>, отбрасывает молча.
    /// </summary>
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
