using System.Text.Json.Nodes;

namespace KompasMcp.Contracts.Schema;

/// <summary>Tiny builder for the JSON Schema subset this project publishes. The schemas built here are simultaneously
/// (a) what <c>tools/list</c> advertises, (b) what the Host validates incoming arguments against, and (c) what
/// <c>schemas/*.json</c> ships to clients — so the three cannot drift apart, which was the whole point of making one
/// source of truth (spec 2.1).</summary>
public static class Sch
{
    public const string Draft = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>Object whose declared property set is closed and whose required list is explicit.</summary>
    public static JsonObject Obj(string title, IReadOnlyList<string> required, JsonObject properties, string? description = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "object",
            ["title"] = title,
            ["properties"] = properties,
            // Unknown fields are rejected, never silently dropped (spec 2.1).
            ["additionalProperties"] = false,
        };

        if (required.Count > 0)
        {
            obj["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        }

        if (description is not null)
        {
            obj["description"] = description;
        }

        return obj;
    }

    /// <summary>Convenience form: every listed property is required.</summary>
    public static JsonObject ObjAll(string title, (string Name, JsonObject Schema)[] properties, string? description = null) =>
        Obj(title, properties.Select(p => p.Name).ToArray(), Props(properties), description);

    public static JsonObject Props(params (string Name, JsonObject Schema)[] properties)
    {
        var obj = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            obj[name] = schema;
        }

        return obj;
    }

    /// <summary>Free-form object used as a map: arbitrary keys, every value the given schema. A closed object
    /// with a fixed property list is the wrong shape for a key set the DOCUMENTATION does not enumerate —
    /// putting <c>additionalProperties</c> inside <c>properties</c> would publish a literal key named
    /// "additionalProperties" and the schema would no longer be a map. INVARIANT: the value schema lives on the
    /// object itself, keys stay unconstrained.</summary>
    public static JsonObject Map(JsonObject valueSchema, string? description = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = valueSchema,
        };
        if (description is not null)
        {
            obj["description"] = description;
        }

        return obj;
    }

    public static JsonObject Str(string? description = null, string? pattern = null, int? minLength = null, int? maxLength = null)    {
        var obj = new JsonObject { ["type"] = "string" };
        if (description is not null)
        {
            obj["description"] = description;
        }

        if (pattern is not null)
        {
            obj["pattern"] = pattern;
        }

        if (minLength is not null)
        {
            obj["minLength"] = minLength.Value;
        }

        if (maxLength is not null)
        {
            obj["maxLength"] = maxLength.Value;
        }

        return obj;
    }

    public static JsonObject Uuid(string? description = null)
    {
        var obj = Str(description ?? "UUID v4.", minLength: 36, maxLength: 36);
        obj["format"] = "uuid";
        return obj;
    }

    public static JsonObject Bool(string? description = null, bool? @default = null)
    {
        var obj = new JsonObject { ["type"] = "boolean" };
        if (description is not null)
        {
            obj["description"] = description;
        }

        if (@default is not null)
        {
            obj["default"] = @default.Value;
        }

        return obj;
    }

    /// <summary>Floating-point value. Bounds are part of the contract, so a negative length is rejected before KOMPAS
    /// is ever touched. TEST: G09</summary>
    public static JsonObject Num(
        string? description = null,
        double? min = null,
        double? max = null,
        bool exclusiveMin = false,
        double? exclusiveMinValue = null,
        double? defaultTo = null)
    {
        var obj = new JsonObject { ["type"] = "number" };
        if (description is not null)
        {
            obj["description"] = description;
        }

        if (min is not null)
        {
            obj["minimum"] = min.Value;
        }

        if (max is not null)
        {
            obj["maximum"] = max.Value;
        }

        if (exclusiveMin)
        {
            obj["exclusiveMinimum"] = exclusiveMinValue ?? min ?? 0d;
            obj.Remove("minimum");
        }

        if (defaultTo is not null)
        {
            obj["default"] = defaultTo.Value;
        }

        return obj;
    }

    /// <summary>Positive number in a NAMED unit. The unit is a parameter, not a suffix baked into the
    /// helper, because a quantity whose unit is not mm must not be published with "мм".</summary>
    /// <param name="note">Clarification placed AFTER the unit sentence. It is deliberately not a
    /// replacement for the whole description: MEASURED defect — the older single-argument form appended
    /// ", мм" to a <paramref name="what"/> that already carried a unit or ended with a period, and the
    /// published description read «Первый катет (…), мм, мм» or «…blind., мм». The unit word is written
    /// exactly once, by this helper.</param>
    /// <remarks>TEST: ToolDescriptionUnitsTests</remarks>
    public static JsonObject Positive(string what, string unit, string? note = null) =>
        Num($"{what}, {unit}.{(note is null ? string.Empty : " " + note)} "
            + "Только конечное положительное значение.",
            exclusiveMin: true, exclusiveMinValue: 0d);

    /// <summary>Positive number in millimetres. See <see cref="Positive"/> for the <paramref name="note"/>
    /// contract.</summary>
    public static JsonObject PositiveMm(string what, string? note = null) => Positive(what, "мм", note);

    public static JsonObject Int(string? description = null, long? min = null, long? max = null, long? defaultTo = null)
    {
        var obj = new JsonObject { ["type"] = "integer" };
        if (description is not null)
        {
            obj["description"] = description;
        }

        if (min is not null)
        {
            obj["minimum"] = min.Value;
        }

        if (max is not null)
        {
            obj["maximum"] = max.Value;
        }

        if (defaultTo is not null)
        {
            obj["default"] = defaultTo.Value;
        }

        return obj;
    }

    public static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
    };

    /// <summary>Nullable variant. JSON Schema expresses "string or null" as a type array, but a $ref cannot carry a
    /// type — wrapping it in anyOf is what keeps "$ref or null" actually checkable instead of quietly becoming
    /// "anything".</summary>
    public static JsonObject Nullable(JsonObject schema)
    {
        var clone = (JsonObject)schema.DeepClone();
        if (clone["$ref"] is JsonNode reference)
        {
            clone.Remove("$ref");
            return new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    new JsonObject { ["$ref"] = reference.DeepClone() },
                    new JsonObject { ["type"] = "null" }),
            };
        }

        if (clone["type"] is not JsonNode type)
        {
            throw new InvalidOperationException("Nullable() применим только к схеме с type или $ref.");
        }

        clone["type"] = new JsonArray(JsonValue.Create(type.GetValue<string>()), JsonValue.Create("null"));
        return clone;
    }

    /// <summary> Adds a description to an already-built schema object. Needed where the object is produced by a wrapper
    /// (<see cref="Nullable"/>) rather than by a helper that takes a description: a nullable <c>$ref</c> is rebuilt as
    /// <c>anyOf</c>, so a description set on the inner <c>$ref</c> would be dropped on the floor. </summary>
    public static JsonObject Described(JsonObject schema, string description)
    {
        schema["description"] = description;
        return schema;
    }

    public static JsonObject Arr(JsonObject items, string? description = null, int? min = null, int? max = null, bool unique = false)
    {
        var obj = new JsonObject { ["type"] = "array", ["items"] = items };
        if (description is not null)
        {
            obj["description"] = description;
        }

        if (min is not null)
        {
            obj["minItems"] = min.Value;
        }

        if (max is not null)
        {
            obj["maxItems"] = max.Value;
        }

        if (unique)
        {
            obj["uniqueItems"] = true;
        }

        return obj;
    }

    /// <summary>Three finite coordinates in public units.</summary>
    public static JsonObject Vec3(string description) =>
        Arr(Num(description + " Компонента, мм."), description, 3, 3);

    public static JsonObject Ref(string pointer) => new() { ["$ref"] = pointer };

    public static JsonObject WithFormat(this JsonObject obj, string format)
    {
        obj["format"] = format;
        return obj;
    }

    /// <summary>Attaches ONLY the shared definitions the schema refers to, with the transitive closure. A
    /// JsonNode may have only one parent, so the kept definitions are cloned per schema: sharing the
    /// instance would throw the moment a second tool took the same node.</summary>
    /// <remarks>INVARIANT: a published tool schema is self-contained - every <c>$ref</c> in it resolves
    /// inside it - and carries no definition nothing refers to. Attaching the whole shared set to every
    /// tool made most of the <c>tools/list</c> answer repetition: a tool with no inputs of its own still
    /// carried all 17 definitions, and the client pays for them in every session.
    /// History: docs/decisions/host.md#schema-defs-pruned</remarks>
    public static JsonObject WithReferencedDefs(JsonObject schema, JsonObject defs)
    {
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectReferences(schema, wanted, seen);

        var kept = new JsonObject();
        for (var index = 0; index < wanted.Count; index++)
        {
            var name = wanted[index];
            if (defs[name] is not JsonObject definition)
            {
                throw new InvalidOperationException(
                    $"Схема ссылается на #/$defs/{name}, которого нет в общих определениях.");
            }

            kept[name] = (JsonObject)definition.DeepClone();

            // A kept definition may refer to another one; walking the growing list IS the closure.
            CollectReferences(definition, wanted, seen);
        }

        schema["$defs"] = kept;
        return schema;
    }

    /// <summary>Appends every local <c>$defs</c> pointer of a node that <paramref name="seen"/> does not
    /// hold yet, in discovery order.</summary>
    private static void CollectReferences(JsonNode? node, List<string> wanted, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref")
                    {
                        if (value is JsonValue reference
                            && reference.TryGetValue<string>(out var pointer)
                            && DefinitionName(pointer) is { } name
                            && seen.Add(name))
                        {
                            wanted.Add(name);
                        }

                        continue;
                    }

                    CollectReferences(value, wanted, seen);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectReferences(item, wanted, seen);
                }

                break;
        }
    }

    /// <summary>The definition a local pointer names, or null for a pointer that leaves this schema. A
    /// pointer deeper than the definition itself (<c>#/$defs/x/properties/y</c>) still names <c>x</c>.</summary>
    private static string? DefinitionName(string pointer)
    {
        const string prefix = "#/$defs/";
        if (!pointer.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = pointer[prefix.Length..];
        var slash = rest.IndexOf('/');
        return (slash < 0 ? rest : rest[..slash]).Replace("~1", "/").Replace("~0", "~");
    }
}
