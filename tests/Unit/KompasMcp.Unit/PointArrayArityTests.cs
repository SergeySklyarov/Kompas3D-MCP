using System.Text.Json.Nodes;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: a field that accepts a FIXED number of components says so in its own description.
/// A client that does not expand <c>$ref</c> and does not count <c>minItems</c> otherwise shows the field
/// as a bare array, and the wrong arity is refused only after the call.</summary>
/// <remarks>MEASURED defect: <c>kompas_rotated.axis_point1_mm</c> was declared <c>Array&lt;number&gt;</c>
/// in the client's view; a two-element point was refused with "Минимум 3 элементов, получено 2" AFTER the
/// call, although the schema carried <c>minItems = maxItems = 3</c> all along.
/// TEST: PointArrayArityTests (this file).
/// History: docs/decisions/host.md#point-array-arity</remarks>
public class PointArrayArityTests
{
    /// <summary>Planar fields of the sketch entity: the same rule for two components. Listed by name
    /// because a 2-item array is not always a point — a range has two bounds, not two coordinates.</summary>
    private static readonly string[] PlanarPoints =
    {
        "start_mm", "end_mm", "center_mm", "start_point_mm", "end_point_mm", "points_mm",
    };

    [Fact]
    public void EveryThreeComponentArray_NamesItsArity()
    {
        var problems = new List<string>();
        foreach (var tool in ToolCatalog.All)
        {
            Walk(tool.Name, tool.InputSchema, tool.InputSchema["$defs"] as JsonObject, problems);
        }

        Assert.True(problems.Count == 0,
            "поля с minItems = maxItems = 3 не называют своей разрядности:\n  "
            + string.Join("\n  ", problems));
    }

    [Fact]
    public void PlanarPointFields_NameTheirArity()
    {
        var problems = new List<string>();
        foreach (var tool in ToolCatalog.All)
        {
            WalkPlanar(tool.Name, tool.InputSchema, tool.InputSchema["$defs"] as JsonObject, problems);
        }

        Assert.True(problems.Count == 0,
            "двухкоординатные поля не называют своей разрядности:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>NEGATIVE CONTROL: the instrument must fail on a schema carrying the very defect it was
    /// written for, else it could pass because it looks at nothing.</summary>
    [Fact]
    public void Checker_FlagsAPlantedBarePoint()
    {
        var planted = JsonNode.Parse("""
            {"type":"object","properties":{
              "axis_point1_mm":{"type":"array","items":{"type":"number","description":"Компонента, мм."},
                                "description":"Первая точка оси, мм.","minItems":3,"maxItems":3}}}
            """)!.AsObject();

        var problems = new List<string>();
        Walk("planted", planted, null, problems);

        Assert.True(problems.Count == 1 && problems[0].Contains("axis_point1_mm", StringComparison.Ordinal),
            "подставленная схема без разрядности обязана быть отвергнута, получено: "
            + string.Join("; ", problems));
    }

    private static void Walk(string tool, JsonNode? node, JsonObject? defs, List<string> problems)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        if (obj["properties"] is JsonObject properties)
        {
            foreach (var (name, child) in properties)
            {
                var resolved = Resolve(child, defs);
                if (Arity(resolved) == 3 && !Text(resolved, defs).Contains("[x, y, z]", StringComparison.Ordinal))
                {
                    problems.Add($"{tool}/{name}: три компоненты, а описание их не называет — "
                        + $"«{Clip(Text(resolved, defs))}»");
                }

                Walk(tool, child, defs, problems);
            }
        }

        foreach (var key in new[] { "items", "anyOf", "oneOf", "allOf" })
        {
            if (obj[key] is JsonNode branch)
            {
                Walk(tool, branch, defs, problems);
            }
        }
    }

    private static void WalkPlanar(string tool, JsonNode? node, JsonObject? defs, List<string> problems)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        if (obj["properties"] is JsonObject properties)
        {
            foreach (var (name, child) in properties)
            {
                if (PlanarPoints.Contains(name, StringComparer.Ordinal))
                {
                    var resolved = Resolve(child, defs);
                    if (!Text(resolved, defs).Contains("[x, y]", StringComparison.Ordinal))
                    {
                        problems.Add($"{tool}/{name}: две координаты, а описание их не называет — "
                            + $"«{Clip(Text(resolved, defs))}»");
                    }
                }

                WalkPlanar(tool, child, defs, problems);
            }
        }

        foreach (var key in new[] { "items", "anyOf", "oneOf", "allOf" })
        {
            if (obj[key] is JsonNode branch)
            {
                WalkPlanar(tool, branch, defs, problems);
            }
        }
    }

    /// <summary>The fixed length declared by the schema, or <c>null</c> when it is not fixed.</summary>
    private static int? Arity(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
        {
            return null;
        }

        var min = obj["minItems"]?.GetValue<int>();
        var max = obj["maxItems"]?.GetValue<int>();
        return min is not null && min == max ? min : null;
    }

    private static string Text(JsonNode? schema, JsonObject? defs)
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

    private static JsonNode? Resolve(JsonNode? schema, JsonObject? defs)
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
                current = defs[reference[(reference.LastIndexOf('/') + 1)..]];
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

    private static string Clip(string text) => text.Length <= 140 ? text : text[..140] + "…";
}
