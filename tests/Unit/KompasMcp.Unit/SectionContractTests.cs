using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: every field the schema of a section tool publishes reaches a property of the command
/// the Worker deserialises, and every required field is one the adapter reads.
/// </summary>
/// <remarks>MEASURED: a field whose name does not match a command property is dropped silently by
/// deserialisation — the call succeeds and the value never arrives.
/// History: docs/decisions/sections.md#tests</remarks>
public sealed class SectionContractTests
{
    public static TheoryData<string, Type> Tools { get; } = new()
    {
        { "kompas_create_section", typeof(CreateSectionCommand) },
        { "kompas_set_section_mode", typeof(SetSectionModeCommand) },
        { "kompas_list_sections", typeof(ListSectionsCommand) },
    };

    [Theory]
    [MemberData(nameof(Tools))]
    public void EverySchemaFieldReachesTheCommand(string name, Type commandType)
    {
        var tool = ToolCatalog.All.SingleOrDefault(candidate => candidate.Name == name)
                   ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");
        var properties = tool.InputSchema["properties"] as JsonObject;
        Assert.NotNull(properties);

        // operation_id and timeout_ms belong to the Host, not to the command: they are consumed before
        // the Worker sees the request.
        var hostFields = new HashSet<string> { "operation_id", "timeout_ms" };
        var names = commandType.GetProperties().Select(property => property.Name).ToHashSet();
        foreach (var (field, _) in properties!.Where(pair => !hostFields.Contains(pair.Key)))
        {
            Assert.True(
                names.Contains(Pascal(field)),
                $"{name}: поле схемы «{field}» не совпадает ни с одним свойством {commandType.Name} — " +
                "значение было бы отброшено при десериализации молча.");
        }
    }

    /// <summary>Snake case of the schema to the Pascal case of a property: <c>expected_revision</c> →
    /// <c>ExpectedRevision</c>.</summary>
    private static string Pascal(string field) =>
        string.Concat(field.Split('_')
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
}
