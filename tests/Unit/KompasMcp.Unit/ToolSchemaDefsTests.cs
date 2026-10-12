using System.Text.Json.Nodes;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>A published tool schema is self-contained and carries nothing it does not use.</summary>
/// <remarks>INVARIANT: every <c>$ref</c> of a tool resolves INSIDE that tool's own schema, and no
/// definition is attached without a reference to it. The shared set used to be copied whole into all
/// 90 schemas, so a tool with no inputs of its own carried all 17 definitions and three quarters of
/// the <c>tools/list</c> answer was repetition the client pays for in every session.
/// History: docs/decisions/host.md#schema-defs-pruned</remarks>
public sealed class ToolSchemaDefsTests
{
    private const string DefsPrefix = "#/$defs/";

    [Fact]
    public void EveryRefOfAToolResolvesInsideItsOwnSchema()
    {
        foreach (var tool in ToolCatalog.All)
        {
            var defs = Definitions(tool);
            var unresolved = new List<string>();

            foreach (var pointer in Pointers(tool.InputSchema, skipDefs: false))
            {
                var name = NameOf(pointer);
                if (name is null || !defs.ContainsKey(name))
                {
                    unresolved.Add(pointer);
                }
            }

            Assert.True(unresolved.Count == 0,
                $"{tool.Name}: ссылка не разрешается внутри собственной схемы - " +
                string.Join(", ", unresolved.Distinct(StringComparer.Ordinal)));
        }
    }

    [Fact]
    public void NoDefinitionIsAttachedWithoutAReference()
    {
        foreach (var tool in ToolCatalog.All)
        {
            var defs = Definitions(tool);

            // The closure is recomputed here, independently of the code that builds the schema: the body
            // of the schema is walked without its own $defs, then every kept definition is walked too.
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(Names(tool.InputSchema, skipDefs: true));
            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                if (!reachable.Add(name) || !defs.TryGetValue(name, out var definition))
                {
                    continue;
                }

                foreach (var nested in Names(definition, skipDefs: false))
                {
                    queue.Enqueue(nested);
                }
            }

            var extra = defs.Keys.Except(reachable, StringComparer.Ordinal).ToArray();
            var missing = reachable.Except(defs.Keys, StringComparer.Ordinal).ToArray();

            Assert.True(extra.Length == 0 && missing.Length == 0,
                $"{tool.Name}: состав $defs не совпал с замыканием ссылок; лишние: " +
                string.Join(", ", extra) + "; недостающие: " + string.Join(", ", missing));
        }
    }

    [Fact]
    public void PruningKeepsTheDefinitionsThemselvesUnchanged()
    {
        // The Host validator resolves against the whole shared set; a pruned copy that differs from it
        // would make tools/list and validation disagree about the same $ref.
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var (name, definition) in Definitions(tool))
            {
                var shared = ToolCatalog.SharedDefinitions[name];
                Assert.True(shared is not null, $"{tool.Name}: общее определение {name} исчезло");
                Assert.True(JsonNode.DeepEquals(definition, shared),
                    $"{tool.Name}: определение {name} в схеме инструмента отличается от общего");
            }
        }
    }

    private static IDictionary<string, JsonNode?> Definitions(ToolDefinition tool) =>
        tool.InputSchema["$defs"]?.AsObject()
        ?? throw new InvalidOperationException($"{tool.Name}: схема опубликована без $defs");

    /// <summary>Every <c>$ref</c> pointer of a node, in document order.</summary>
    private static IEnumerable<string> Pointers(JsonNode? node, bool skipDefs)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value is JsonValue reference && reference.TryGetValue<string>(out var pointer))
                    {
                        yield return pointer;
                    }
                    else if (!skipDefs || key != "$defs")
                    {
                        foreach (var nested in Pointers(value, skipDefs))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in Pointers(item, skipDefs))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static string? NameOf(string pointer) =>
        pointer.StartsWith(DefsPrefix, StringComparison.Ordinal)
            ? pointer[DefsPrefix.Length..].Split('/')[0]
            : null;

    /// <summary>The names of the LOCAL definitions a node refers to; a pointer outside this schema is
    /// left out, and <see cref="EveryRefOfAToolResolvesInsideItsOwnSchema"/> is what refuses it.</summary>
    private static IEnumerable<string> Names(JsonNode? node, bool skipDefs) =>
        Pointers(node, skipDefs)
            .Select(NameOf)
            .Where(name => name is not null)
            .Select(name => name!);
}
