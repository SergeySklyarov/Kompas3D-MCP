using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KompasMcp.Contracts;

/// <summary>
/// Reads scalars out of a <see cref="JsonNode"/> without assuming how the node is stored.
/// </summary>
/// <remarks>
/// This exists because a JsonNode has two very different shapes depending on where it came from:
/// one built in memory wraps the CLR value (<c>JsonValue&lt;long&gt;</c>), while one that arrived
/// over the wire or through <c>DeepClone()</c> wraps a <c>JsonElement</c>.
/// <c>JsonNode.GetValue&lt;T&gt;()</c> and <c>TryGetValue&lt;T&gt;()</c> are strict about that
/// distinction, so code that works on hand-built documents fails on parsed ones — twice in this
/// project: once in the schema validator (every numeric argument crashed) and once reading the
/// Worker's <c>revision</c> back in the Host, which silently produced a stale revision chain.
///
/// Anything crossing the pipe or the MCP boundary should read scalars through these helpers.
/// </remarks>
public static class JsonScalars
{
    public static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out string? text))
        {
            return text;
        }

        return value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    public static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out long direct))
        {
            return direct;
        }

        if (value.TryGetValue(out int small))
        {
            return small;
        }

        return value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number
            ? element.TryGetInt64(out var fromElement) ? fromElement : (long)element.GetDouble()
            : null;
    }

    public static int? ReadInt(JsonNode? node)
    {
        var longValue = ReadLong(node);
        return longValue is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }

    public static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out double direct))
        {
            return direct;
        }

        if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var parsed))
        {
            return parsed;
        }

        return double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text)
            ? text
            : null;
    }

    public static bool? ReadBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out bool direct))
        {
            return direct;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return null;
    }

    public static JsonNode? CopyOf(JsonNode? node) => node?.DeepClone();
}
