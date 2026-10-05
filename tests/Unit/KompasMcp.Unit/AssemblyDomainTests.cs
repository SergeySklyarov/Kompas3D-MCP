using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Assembly domain (order C1, profile <c>assemblies-minimal-v1</c>): the contract of five tools and
/// the honesty of their descriptions.</summary>
/// <remarks>TEST: the tests check exactly what is checkable without KOMPAS — the tools are registered,
/// mutations declare the mandatory fields, schemas are strict, and descriptions name the CURRENT release
/// limits (e.g. nested components cannot be addressed) rather than the presence of the word "acceptance".
/// The "the route works" check is NOT included and is not substituted: it requires a run on v24.0.0.2799.</remarks>
public class AssemblyDomainTests
{
    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    private static readonly (string Name, string Command)[] Domain =
    {
        ("kompas_list_components", "asm.list_components"),
        ("kompas_insert_component", "asm.insert_component"),
        ("kompas_set_component_placement", "asm.set_placement"),
        ("kompas_replace_component", "asm.replace_component"),
        ("kompas_check_component_links", "asm.check_links"),
    };

    public static TheoryData<string> AllTools { get; } = new(Domain.Select(d => d.Name));

    [Theory]
    [MemberData(nameof(AllTools))]
    public void DomainTool_IsRegisteredWithItsOwnWorkerCommand(string name)
    {
        var tool = Tool(name);
        Assert.Equal(Domain.Single(d => d.Name == name).Command, tool.WorkerCommand);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
    }

    [Theory]
    [InlineData("kompas_insert_component")]
    [InlineData("kompas_set_component_placement")]
    [InlineData("kompas_replace_component")]
    public void AssemblyMutation_DeclaresOperationIdAndExpectedRevision(string name)
    {
        var tool = Tool(name);
        var properties = (JsonObject)tool.InputSchema["properties"]!;
        var required = ((JsonArray)tool.InputSchema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.True(tool.IsMutation);
        Assert.Contains("operation_id", properties.Select(p => p.Key));
        Assert.Contains("operation_id", required);
        Assert.Contains("expected_revision", required);
        Assert.Contains("document_id", required);
    }

    [Theory]
    [InlineData("kompas_list_components")]
    [InlineData("kompas_check_component_links")]
    public void AssemblyRead_IsNotAMutation(string name)
    {
        var tool = Tool(name);
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.True(tool.Behaviour.RequiresDocument);
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void AssemblySchema_IsStrictAndRequiresDocument(string name)
    {
        var schema = Tool(name).InputSchema;
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("document_id", required);
    }

    [Fact]
    public void ListComponents_DescriptionNamesTheNestedAddressingLimit()
    {
        // INVARIANT: the release limit is visible to the client, not only in the code — a nested component
        // is READ, but it has no address, and a mutation by a guessed number is forbidden.
        var description = Tool("kompas_list_components").Description;
        Assert.Contains("вложенн", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PartCollection", description, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertComponent_DescriptionNamesTheDocumentedRoute()
    {
        var description = Tool("kompas_insert_component").Description;
        Assert.Contains("AddFromFile", description, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void AssemblyTool_DescriptionIsNotAStalePromise(string name)
    {
        // INVARIANT: no description may claim there was no live run — the run happened, and the stale wording
        // reads as "the capability is unconfirmed". What is checked is the CURRENT state, not the presence
        // of the word "acceptance".
        var description = Tool(name).Description;
        Assert.DoesNotContain("живой приёмки не было", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("живого прогона не было", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsertComponent_RequiresSourcePathAndKeepsTransformOptional()
    {
        // INVARIANT: the source is required (there is nothing to insert without it), placement is not — the
        // server does not invent an origin but leaves the documented KOMPAS default.
        var schema = Tool("kompas_insert_component").InputSchema;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("source_path", required);
        Assert.DoesNotContain("transform", required);
    }

    [Fact]
    public void ComponentRow_KeepsUnreadFieldsNullable()
    {
        // INVARIANT: an empty field means "not read", not zero — the instance count and the "part/assembly"
        // sign are nullable so "did not read" is distinguishable from "read zero".
        var row = new ComponentRowDto
        {
            ComponentRef = "component:00000000000000000000000000000000",
            Depth = 0,
        };

        Assert.Null(row.InstanceCount);
        Assert.Null(row.IsDetail);
        Assert.Null(row.LoadState);
        Assert.Null(row.Matrix);
    }
}
