using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: a field whose NAME names a unit must be described with THAT unit, once. A
/// description is the only place a client learns the unit, so a wrong or duplicated unit is a
/// contract defect even though nothing fails at runtime.</summary>
/// <remarks>MEASURED defect that made this instrument necessary: <c>Sch.PositiveMm(what)</c> appended
/// ", мм" to a <c>what</c> that already carried a unit or ended with a period, so the published
/// description of <c>kompas_measure.density_kg_per_m3</c> read «Плотность материала, мм» and the one
/// of <c>kompas_chamfer.distance1_mm</c> read «…для distance_angle), мм, мм».
/// RULE REFINEMENT, named rather than softened: the unit is matched as a TOKEN, not as a substring.
/// «180 мм³» inside a measured evidence sentence is not a claim about the field's unit, and a substring
/// test would have made removing measured numbers from descriptions the only way to pass.
/// TEST: ToolDescriptionUnitsTests (this file); the negative control is
/// <see cref="Checker_FlagsAPlantedWrongUnit"/>.</remarks>
public class ToolDescriptionUnitsTests
{
    /// <summary>Millimetre as a standalone unit token: not followed by a superscript or a letter, so
    /// «мм³» (a measured volume) and «ммм» do not count.</summary>
    private static readonly Regex MillimetreToken = new(@"(?<![\p{L}\p{N}])мм(?![\p{L}\p{N}])",
        RegexOptions.Compiled);

    /// <summary>Degree as a word stem: «градус», «градусах», «ГРАДУСЫ».</summary>
    private static readonly Regex DegreeToken = new(@"(?<![\p{L}])град", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string KilogramPerCubicMetre = "кг/м³";

    [Fact]
    public void EveryUnitSuffixedField_IsDescribedWithItsOwnUnitOnce()
    {
        var problems = new List<string>();
        foreach (var tool in ToolCatalog.All)
        {
            Check(tool.Name, tool.InputSchema, problems);
        }

        Assert.True(problems.Count == 0,
            "схемы описывают единицы неверно или дважды:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>NEGATIVE CONTROL: the instrument must fail on a schema carrying the very defect it was
    /// written for. Without this the check could pass because it looks at nothing.</summary>
    [Fact]
    public void Checker_FlagsAPlantedWrongUnit()
    {
        var planted = JsonNode.Parse("""
            {"type":"object","properties":{
              "density_kg_per_m3":{"type":"number","description":"Плотность материала, мм. Только конечное положительное значение."}}}
            """)!.AsObject();

        var problems = new List<string>();
        Check("planted", planted, problems);

        Assert.True(problems.Count == 1 && problems[0].Contains("density_kg_per_m3", StringComparison.Ordinal),
            "подставленная схема с «Плотность, мм» обязана быть отвергнута, получено: "
            + string.Join("; ", problems));
    }

    /// <summary>NEGATIVE CONTROL for the second half of the rule: a doubled unit is a defect too, and
    /// the check that finds it is a separate branch.</summary>
    [Fact]
    public void Checker_FlagsADoubledUnit()
    {
        var planted = JsonNode.Parse("""
            {"type":"object","properties":{
              "distance1_mm":{"type":"number","description":"Первый катет, мм, мм. Только конечное положительное значение."}}}
            """)!.AsObject();

        var problems = new List<string>();
        Check("planted", planted, problems);

        Assert.Contains(problems, p => p.Contains("дважды", StringComparison.Ordinal));
    }

    // ── the instrument ────────────────────────────────────────────────────────────────────────────

    private static void Check(string tool, JsonObject schema, List<string> problems)
    {
        var defs = schema["$defs"] as JsonObject;
        Walk(schema, string.Empty, defs, tool, problems);
        WalkDescriptions(schema, tool, problems);
    }

    private static void Walk(JsonNode? node, string path, JsonObject? defs, string tool, List<string> problems)
    {
        if (node is not JsonObject obj)
        {
            if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", defs, tool, problems);
                }
            }

            return;
        }

        if (obj["properties"] is JsonObject properties)
        {
            foreach (var (name, child) in properties)
            {
                var where = $"{path}/{name}";
                if (name.EndsWith("_mm", StringComparison.Ordinal)
                    || name.EndsWith("_deg", StringComparison.Ordinal)
                    || name.EndsWith("_kg_per_m3", StringComparison.Ordinal))
                {
                    CheckUnit(tool, where, name, child, defs, problems);
                }

                Walk(child, where, defs, tool, problems);
            }
        }

        foreach (var key in new[] { "items", "anyOf", "oneOf", "allOf" })
        {
            if (obj[key] is JsonNode branch)
            {
                Walk(branch, $"{path}/{key}", defs, tool, problems);
            }
        }
    }

    private static void CheckUnit(
        string tool, string where, string name, JsonNode? schema, JsonObject? defs, List<string> problems)
    {
        var text = UnitText(schema, defs);
        if (text.Contains("мм, мм", StringComparison.Ordinal)
            || text.Contains("., мм", StringComparison.Ordinal))
        {
            problems.Add($"{tool}{where}: единица записана дважды — «{Clip(text)}»");
            return;
        }

        var millimetre = MillimetreToken.IsMatch(text);
        var degree = DegreeToken.IsMatch(text);
        var density = text.Contains(KilogramPerCubicMetre, StringComparison.Ordinal);

        if (name.EndsWith("_mm", StringComparison.Ordinal) && (!millimetre || degree || density))
        {
            problems.Add($"{tool}{where}: поле в мм, а описание называет "
                + $"{(degree ? "градусы" : density ? KilogramPerCubicMetre : "ни мм")} — «{Clip(text)}»");
        }

        if (name.EndsWith("_deg", StringComparison.Ordinal) && (!degree || millimetre))
        {
            problems.Add($"{tool}{where}: поле в градусах, а описание "
                + $"{(millimetre ? "называет мм" : "не называет градусов")} — «{Clip(text)}»");
        }

        if (name.EndsWith("_kg_per_m3", StringComparison.Ordinal) && (!density || millimetre))
        {
            problems.Add($"{tool}{where}: поле в кг/м³, а описание "
                + $"{(millimetre ? "называет мм" : "не называет кг/м³")} — «{Clip(text)}»");
        }
    }

    /// <summary>Description of a field AFTER resolving <c>$ref</c> and a single non-null
    /// <c>anyOf</c> branch — plus, for an array, the description of its items: a list of lengths is
    /// documented by its element.</summary>
    private static string UnitText(JsonNode? schema, JsonObject? defs)
    {
        var resolved = Resolve(schema, defs);
        if (resolved is not JsonObject obj)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (obj["description"]?.GetValue<string>() is { } own)
        {
            parts.Add(own);
        }

        if (Resolve(obj["items"], defs) is JsonObject items
            && items["description"]?.GetValue<string>() is { } itemText)
        {
            parts.Add(itemText);
        }

        return string.Join(" ", parts);
    }

    private static JsonObject? Resolve(JsonNode? schema, JsonObject? defs)
    {
        var current = schema;
        for (var hop = 0; hop < 10; hop++)
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            if (obj["$ref"]?.GetValue<string>() is { } reference && defs is not null)
            {
                var name = reference[(reference.LastIndexOf('/') + 1)..];
                current = defs[name];
                continue;
            }

            if (obj["anyOf"] is JsonArray branches)
            {
                var concrete = branches
                    .Where(b => b is JsonObject bObj && bObj["type"]?.GetValue<string>() != "null")
                    .ToArray();
                if (concrete.Length == 1)
                {
                    current = concrete[0];
                    continue;
                }
            }

            return obj;
        }

        return null;
    }

    /// <summary>The second half of the rule is not about a field: NO published description may carry a
    /// doubled unit anywhere — a nested item or a definition included it just as well.</summary>
    private static void WalkDescriptions(JsonNode? node, string tool, List<string> problems)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (key == "description" && value?.GetValue<string>() is { } text
                    && (text.Contains("мм, мм", StringComparison.Ordinal)
                        || text.Contains("., мм", StringComparison.Ordinal)))
                {
                    problems.Add($"{tool}: описание несёт единицу дважды — «{Clip(text)}»");
                }

                WalkDescriptions(value, tool, problems);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                WalkDescriptions(item, tool, problems);
            }
        }
    }

    private static string Clip(string text) =>
        text.Length <= 140 ? text : text[..140] + "…";
}
