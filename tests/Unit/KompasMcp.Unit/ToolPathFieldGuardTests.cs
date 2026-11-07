using System.Text.Json.Nodes;
using KompasMcp.Host;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Every field that names a filesystem path must be on the path policy's list.</summary>
/// <remarks>INVARIANT: <see cref="ToolInvoker"/> judges the fields named in its <c>PathFields</c> list and only
/// at the TOP level of the arguments, so a new tool with a field called <c>template_path</c> would bypass the
/// policy silently. The rule is checked against the published catalog, which is the single source of truth for
/// what the client can send; a field named <c>path_ref</c> is a REFERENCE to a model object, not a path, and is
/// the one explicit exception. TEST: adding such a field to any schema makes this test fail.
/// History: docs/decisions/host.md#path-fields</remarks>
public sealed class ToolPathFieldGuardTests
{
    /// <summary>Words that make a field name a filesystem path. Matched per <c>_</c>-separated token, so
    /// <c>require_closed_profile</c> is not mistaken for a file field.</summary>
    private static readonly string[] PathWords = { "path", "file", "folder" };

    /// <summary>Fields that carry the word but are NOT filesystem paths, each with its meaning.</summary>
    private static readonly string[] NotPaths = { "path_ref" };

    [Fact]
    public void EveryPathNamedField_IsOnThePolicyListOrNamedAsNotAPath()
    {
        var offenders = new List<string>();
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var (name, nested) in Fields(tool.InputSchema))
            {
                if (!NamesAPath(name)
                    || ToolInvoker.PathFields.Contains(name, StringComparer.Ordinal)
                    || NotPaths.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                offenders.Add(tool.Name + "." + name + (nested ? " (вложенное поле)" : string.Empty));
            }
        }

        Assert.True(offenders.Count == 0,
            "Поле пути вне политики: " + string.Join(", ", offenders)
            + ". Политика судит только PathFields и только верхний уровень аргументов — добавьте поле в "
            + "PathFields либо в явный список исключений (в NotPaths) с причиной.");
    }

    [Fact]
    public void NoPolicyField_IsNestedInsideAnotherObject()
    {
        var offenders = new List<string>();
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var (name, nested) in Fields(tool.InputSchema))
            {
                if (nested && ToolInvoker.PathFields.Contains(name, StringComparer.Ordinal))
                {
                    offenders.Add(tool.Name + "." + name);
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Поле пути вложено в другой объект: " + string.Join(", ", offenders)
            + ". Политика читает аргументы только на верхнем уровне, поэтому такое поле она не увидит.");
    }

    private static bool NamesAPath(string name) => name
        .Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Any(token => PathWords.Contains(token, StringComparer.Ordinal));

    /// <summary>Every named field of a schema with whether it sits INSIDE another object (or a resolved
    /// <c>$defs</c> entry), where the top-level argument walk cannot see it.</summary>
    private static IEnumerable<(string Name, bool Nested)> Fields(JsonObject schema)
    {
        var defs = schema["$defs"] as JsonObject;
        var walkedDefs = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<(string, bool)>();

        Walk(schema, isArguments: true);
        return found;

        void Walk(JsonNode? node, bool isArguments)
        {
            switch (node)
            {
                case JsonObject schemaObject:
                    if (schemaObject["$ref"] is JsonValue reference
                        && reference.TryGetValue<string>(out var pointer)
                        && pointer.StartsWith("#/$defs/", StringComparison.Ordinal))
                    {
                        var definition = pointer["#/$defs/".Length..];
                        if (walkedDefs.Add(definition) && defs?[definition] is JsonNode resolved)
                        {
                            Walk(resolved, isArguments: false);
                        }

                        return;
                    }

                    if (schemaObject["properties"] is JsonObject properties)
                    {
                        foreach (var (name, value) in properties)
                        {
                            found.Add((name, !isArguments));
                            Walk(value, isArguments: false);
                        }
                    }

                    foreach (var keyword in new[] { "items", "allOf", "anyOf", "oneOf" })
                    {
                        Walk(schemaObject[keyword], isArguments: false);
                    }

                    break;
                case JsonArray alternatives:
                    foreach (var alternative in alternatives)
                    {
                        Walk(alternative, isArguments: false);
                    }

                    break;
            }
        }
    }
}
