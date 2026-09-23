using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Schema;

/// <summary>One validation violation, addressed by JSON pointer so a client can fix the right field.</summary>
public sealed record SchemaViolation(string Path, string Keyword, string Message);

/// <summary>
/// Validator for the JSON Schema subset this project publishes (spec 2.1):
/// <c>type</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties:false</c>,
/// <c>enum</c>, numeric bounds, <c>items</c>/<c>minItems</c>/<c>maxItems</c>,
/// <c>minLength</c>/<c>maxLength</c>, <c>pattern</c>, <c>$ref</c> into <c>$defs</c>,
/// <c>anyOf</c>/<c>oneOf</c> and nullability as a type array.
/// </summary>
/// <remarks>
/// Deliberately small: the schemas we author are the only inputs, so a full draft-2020-12 engine
/// would be unused surface. Unsupported keywords cause a hard startup failure rather than being
/// skipped, so a schema can never look stricter than it is.
///
/// All scalar reads go through <see cref="ReadString"/> and friends. A schema here always passes
/// through <c>DeepClone()</c> (nullable wrappers, shared <c>$defs</c>), and a cloned node stores a
/// <c>JsonElement</c> rather than the original CLR value; <c>JsonNode.GetValue&lt;T&gt;</c> demands
/// an exact backing-type match and throws <c>InvalidOperationException</c> otherwise. That turned a
/// perfectly valid numeric argument into an unhandled server error — the bug these helpers exist to
/// make impossible, covered by <c>ValidatorReadsClonedSchema</c>.
/// </remarks>
public static class JsonSchemaValidator
{
    private static readonly HashSet<string> KnownKeywords =
        new(StringComparer.Ordinal)
        {
            "$schema", "$ref", "$defs", "title", "description", "type", "properties",
            "required", "additionalProperties", "enum", "const", "minimum", "maximum",
            "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "items", "minItems",
            "maxItems", "uniqueItems", "minLength", "maxLength", "pattern", "format",
            "default", "examples", "oneOf", "anyOf", "deprecated", "readOnly", "writeOnly",
        };

    /// <summary>Thrown at startup (not per call) when a schema uses a keyword this validator ignores.</summary>
    public static void AssertSchemaIsUnderstood(JsonNode schema)
    {
        var problems = new List<string>();
        Walk(schema, "$", problems, new HashSet<JsonNode>(ReferenceEqualityComparer.Instance));
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Схема содержит конструкторы, которые валидатор не разбирает: " + string.Join("; ", problems));
        }
    }

    private static void Walk(JsonNode? node, string path, List<string> problems, HashSet<JsonNode> seen)
    {
        if (node is not JsonObject obj)
        {
            if (node is not null && node.GetValueKind() != JsonValueKind.False)
            {
                problems.Add($"{path}: схема-узел должен быть объектом или false");
            }

            return;
        }

        if (!seen.Add(obj))
        {
            return;
        }

        foreach (var prop in obj)
        {
            if (prop.Key == "$defs")
            {
                if (prop.Value is JsonObject defs)
                {
                    foreach (var def in defs)
                    {
                        Walk(def.Value, $"{path}/$defs/{def.Key}", problems, seen);
                    }
                }

                continue;
            }

            if (!KnownKeywords.Contains(prop.Key))
            {
                problems.Add($"{path}: неизвестное ключевое слово '{prop.Key}'");
                continue;
            }

            switch (prop.Key)
            {
                case "properties":
                    foreach (var child in (JsonObject)prop.Value!)
                    {
                        Walk(child.Value, $"{path}/properties/{child.Key}", problems, seen);
                    }

                    break;
                case "items" when prop.Value is JsonObject:
                    Walk(prop.Value, $"{path}/items", problems, seen);
                    break;
                case "oneOf" or "anyOf" when prop.Value is JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        Walk(arr[i], $"{path}/{prop.Key}/{i}", problems, seen);
                    }

                    break;
            }
        }

        var constrainsSomething = obj.ContainsKey("type") || obj.ContainsKey("$ref") || obj.ContainsKey("oneOf")
            || obj.ContainsKey("anyOf") || obj.ContainsKey("enum") || obj.ContainsKey("const")
            || obj.ContainsKey("properties") || obj.ContainsKey("items") || obj.ContainsKey("required");

        if (!constrainsSomething)
        {
            problems.Add($"{path}: узел без type/$ref/enum/properties/items — проверка невозможна");
        }
    }

    /// <summary>
    /// Validate <paramref name="instance"/> against <paramref name="schema"/>, reporting every
    /// violation found so one round-trip is enough to fix a payload.
    /// </summary>
    public static IReadOnlyList<SchemaViolation> Validate(JsonNode schema, JsonNode? instance)
    {
        var violations = new List<SchemaViolation>();
        var root = schema as JsonObject ?? throw new InvalidOperationException("Корень схемы должен быть объектом.");
        ValidateNode(root, "$", instance, root, violations);
        return violations;
    }

    private static void ValidateNode(JsonObject nodeSchema, string path, JsonNode? value, JsonObject root, List<SchemaViolation> errors)
    {
        if (nodeSchema["$ref"] is JsonNode refNode)
        {
            var pointer = ReadString(refNode) ?? string.Empty;
            if (!TryResolveRef(root, pointer, out var resolved) || resolved is not JsonObject resolvedSchema)
            {
                errors.Add(new SchemaViolation(path, "$ref", $"Не удалось разрешить ссылку {pointer}."));
                return;
            }

            ValidateNode(resolvedSchema, path, value, root, errors);
            return;
        }

        if (nodeSchema["enum"] is JsonArray enumValues)
        {
            var rendered = value?.ToJsonString() ?? "null";
            var allowed = enumValues.Where(e => e is not null).Select(e => e!.ToJsonString()).ToList();
            if (!allowed.Contains(rendered, StringComparer.Ordinal))
            {
                errors.Add(new SchemaViolation(path, "enum", $"Значение {rendered} не входит в [{string.Join(", ", allowed)}]."));
            }
        }

        if (nodeSchema["oneOf"] is JsonArray oneOf)
        {
            var matches = oneOf.Count(v => v is JsonObject sub && CountErrors(sub, value, root) == 0);
            if (matches != 1)
            {
                errors.Add(new SchemaViolation(path, "oneOf", $"Подходит {matches} ветвей вместо одной."));
            }
        }

        if (nodeSchema["anyOf"] is JsonArray anyOf)
        {
            if (!anyOf.Any(v => v is JsonObject sub && CountErrors(sub, value, root) == 0))
            {
                errors.Add(new SchemaViolation(path, "anyOf", "Не подходит ни одна допустимая ветвь."));
            }
        }

        if (nodeSchema["type"] is JsonNode typeNode)
        {
            var types = typeNode is JsonArray typeArray
                ? typeArray.Select(t => ReadString(t) ?? string.Empty).ToList()
                : new List<string> { ReadString(typeNode) ?? string.Empty };

            var actual = KindOf(value);
            if (!types.Any(t => TypeMatches(t, actual, value)))
            {
                errors.Add(new SchemaViolation(path, "type", $"Ожидался тип {string.Join("|", types)}, фактически {actual}."));
                return;
            }
        }

        switch (value?.GetValueKind())
        {
            case JsonValueKind.Object:
                ValidateObject(nodeSchema, path, value!.AsObject(), root, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(nodeSchema, path, value!.AsArray(), root, errors);
                break;
            case JsonValueKind.String:
                ValidateString(nodeSchema, path, value!.GetValue<string>(), errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(nodeSchema, path, value!, errors);
                break;
        }
    }

    private static int CountErrors(JsonObject sub, JsonNode? value, JsonObject root)
    {
        var local = new List<SchemaViolation>();
        ValidateNode(sub, "$", value, root, local);
        return local.Count;
    }

    private static void ValidateObject(JsonObject nodeSchema, string path, JsonObject value, JsonObject root, List<SchemaViolation> errors)
    {
        if (nodeSchema["required"] is JsonArray required)
        {
            foreach (var name in required)
            {
                var key = ReadString(name) ?? string.Empty;
                if (!value.ContainsKey(key))
                {
                    errors.Add(new SchemaViolation($"{path}/{key}", "required", "Обязательное поле отсутствует."));
                }
            }
        }

        var properties = nodeSchema["properties"] as JsonObject;
        var additional = nodeSchema["additionalProperties"];

        // Узел, не описывающий состав ВООБЩЕ (ни properties, ни additionalProperties), — это
        // обёртка вроде anyOf вокруг $ref. Запрещать в ней члены нечем: решение о составе уже
        // принято ветвями anyOf/oneOf выше, и оно положительное (иначе сюда бы не дошло).
        //
        // «additionalProperties: false по умолчанию» — соглашение о том, КАК ПИШУТСЯ схемы этого
        // сервера (spec 2.1), а не правило JSON Schema: в самой JSON Schema отсутствующий
        // additionalProperties означает «разрешено». Первая редакция применяла соглашение как
        // правило и отвергала КОРРЕКТНЫЙ вызов: значение expected_bbox_mm (объект {min_mm,max_mm})
        // проходило проверку ветви $ref → #/$defs/bbox и тут же объявлялось «неизвестным полем»
        // на уровне обёртки. Измерено 18.09.2026: INVALID_ARGUMENT с
        // violations=[$/expected_bbox_mm/min_mm additionalProperties, …/max_mm additionalProperties]
        // при том, что схема объявляет оба поля.
        //
        // Проявлялось это только на ОБЪЕКТНОМ значении под чистым anyOf: под массивом (vector3,
        // reference) разбор уходит в ValidateArray, а под объектом до цикла ниже дело доходило
        // впервые. Поэтому дефект и дожил до B3.
        if (properties is null && additional is null)
        {
            return;
        }

        foreach (var member in value)
        {
            if (properties is not null && properties[member.Key] is JsonObject declared)
            {
                ValidateNode(declared, $"{path}/{member.Key}", member.Value, root, errors);
                continue;
            }

            // "additionalProperties": false is the project default (spec 2.1), so anything not
            // declared is a violation rather than something to be quietly dropped.
            if (additional is null)
            {
                errors.Add(new SchemaViolation($"{path}/{member.Key}", "additionalProperties", "Неизвестное поле запрещено контрактом."));
            }
            else if (additional is JsonObject additionalSchema)
            {
                ValidateNode(additionalSchema, $"{path}/{member.Key}", member.Value, root, errors);
            }
            else if (ReadBool(additional, out var allowsExtra) && !allowsExtra)
            {
                errors.Add(new SchemaViolation($"{path}/{member.Key}", "additionalProperties", "Неизвестное поле запрещено контрактом."));
            }
        }
    }

    private static void ValidateArray(JsonObject nodeSchema, string path, JsonArray value, JsonObject root, List<SchemaViolation> errors)
    {
        if (nodeSchema["minItems"] is JsonNode minItems && ReadInt(minItems, out var min) && value.Count < min)
        {
            errors.Add(new SchemaViolation(path, "minItems", $"Минимум {min} элементов, получено {value.Count}."));
        }

        if (nodeSchema["maxItems"] is JsonNode maxItems && ReadInt(maxItems, out var max) && value.Count > max)
        {
            errors.Add(new SchemaViolation(path, "maxItems", $"Максимум {max} элементов, получено {value.Count}."));
        }

        if (nodeSchema["uniqueItems"] is JsonNode uniqueNode && ReadBool(uniqueNode, out var unique) && unique)
        {
            var rendered = value.Select(v => v?.ToJsonString() ?? "null").ToList();
            if (rendered.Count != rendered.Distinct(StringComparer.Ordinal).Count())
            {
                errors.Add(new SchemaViolation(path, "uniqueItems", "Элементы не уникальны."));
            }
        }

        if (nodeSchema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < value.Count; i++)
            {
                ValidateNode(itemSchema, $"{path}/{i}", value[i], root, errors);
            }
        }
    }

    private static void ValidateString(JsonObject nodeSchema, string path, string value, List<SchemaViolation> errors)
    {
        if (nodeSchema["minLength"] is JsonNode minLength && ReadInt(minLength, out var min) && value.Length < min)
        {
            errors.Add(new SchemaViolation(path, "minLength", $"Длина {value.Length} меньше {min}."));
        }

        if (nodeSchema["maxLength"] is JsonNode maxLength && ReadInt(maxLength, out var max) && value.Length > max)
        {
            errors.Add(new SchemaViolation(path, "maxLength", $"Длина {value.Length} больше {max}."));
        }

        if (ReadString(nodeSchema["pattern"]) is string pattern && !System.Text.RegularExpressions.Regex.IsMatch(value, pattern))
        {
            errors.Add(new SchemaViolation(path, "pattern", $"Значение не соответствует {pattern}."));
        }

        if (ReadString(nodeSchema["format"]) == "uuid" && !Guid.TryParse(value, out _))
        {
            errors.Add(new SchemaViolation(path, "format", "Ожидается UUID."));
        }
    }

    private static void ValidateNumber(JsonObject nodeSchema, string path, JsonNode value, List<SchemaViolation> errors)
    {
        if (!ReadDouble(value, out var number))
        {
            errors.Add(new SchemaViolation(path, "type", "Число не представимо в double."));
            return;
        }

        if (ReadString(nodeSchema["type"]) == "integer" && !double.IsInteger(number))
        {
            errors.Add(new SchemaViolation(path, "type", $"Ожидается целое, получено {number.ToString("0.####", CultureInfo.InvariantCulture)}."));
        }

        if (nodeSchema["minimum"] is JsonNode min && ReadDouble(min, out var minValue) && number < minValue)
        {
            errors.Add(new SchemaViolation(path, "minimum", $"Значение {number} меньше допустимого {minValue}."));
        }

        if (nodeSchema["maximum"] is JsonNode max && ReadDouble(max, out var maxValue) && number > maxValue)
        {
            errors.Add(new SchemaViolation(path, "maximum", $"Значение {number} больше допустимого {maxValue}."));
        }

        // exclusiveMinimum is written as a number by this project's builder; a bare `true`
        // (draft-04 style) is not accepted, so a schema cannot accidentally mean something else.
        if (nodeSchema["exclusiveMinimum"] is JsonNode exMin && ReadDouble(exMin, out var exMinValue) && number <= exMinValue)
        {
            errors.Add(new SchemaViolation(path, "exclusiveMinimum", $"Значение {number} должно быть больше {exMinValue}."));
        }

        if (nodeSchema["exclusiveMaximum"] is JsonNode exMax && ReadDouble(exMax, out var exMaxValue) && number >= exMaxValue)
        {
            errors.Add(new SchemaViolation(path, "exclusiveMaximum", $"Значение {number} должно быть меньше {exMaxValue}."));
        }
    }

    private static string KindOf(JsonNode? value) => value?.GetValueKind() switch
    {
        null => "null",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "unknown",
    };

    private static bool TypeMatches(string expected, string actual, JsonNode? value)
    {
        if (expected == actual)
        {
            return true;
        }

        // "integer" is a refinement of "number"; JSON has no separate integral literal.
        if (expected == "integer" && actual == "number")
        {
            return value is not null && ReadDouble(value, out var numeric) && double.IsInteger(numeric);
        }

        return false;
    }

    private static bool TryResolveRef(JsonObject root, string pointer, out JsonNode? resolved)
    {
        resolved = null;
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        JsonNode? current = root;
        foreach (var segment in pointer[2..].Split('/'))
        {
            var decoded = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj => obj[decoded],
                JsonArray arr when int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index < arr.Count => arr[index],
                _ => null,
            };

            if (current is null)
            {
                return false;
            }
        }

        resolved = current;
        return true;
    }

    // Thin adapters over JsonScalars: the validator's call sites want "try" semantics, and the
    // shared helper exists precisely because the strict JsonNode.GetValue<T> overloads are
    // storage-sensitive. Single call each — reading twice could disagree with itself.
    private static string? ReadString(JsonNode? node) => JsonScalars.ReadString(node);

    private static bool ReadBool(JsonNode? node, out bool value)
    {
        bool? parsed = JsonScalars.ReadBool(node);
        value = parsed ?? false;
        return parsed is not null;
    }

    private static bool ReadDouble(JsonNode? node, out double number)
    {
        double? parsed = JsonScalars.ReadDouble(node);
        number = parsed ?? 0d;
        return parsed is not null;
    }

    private static bool ReadInt(JsonNode? node, out int value)
    {
        int? parsed = JsonScalars.ReadInt(node);
        value = parsed ?? 0;
        return parsed is not null;
    }
}
