using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Creating a MODEL specification row: the input rule, the published shape and the mutation
/// flags.</summary>
/// <remarks>TEST: what is checkable without KOMPAS — the pure input rule, the schema and the catalog
/// flags. The row itself, both roads, the save-and-reopen and every refusal need a live session: they are
/// held by the acceptance rows <c>SPEC-02.*</c> of <c>mcp-smoke.py</c>.
/// History: docs/decisions/bom.md#spec-add-row</remarks>
public class SpecificationRowTests
{
    [Fact]
    public void SectionZeroIsRefusedWithTheVendorWording()
    {
        var refusal = SpecificationRowInput.Validate(0, "SPC.402.001", "Пластина", "2", null);

        Assert.NotNull(refusal);
        Assert.Equal("section_number", refusal.Field);
        Assert.Contains("Объект c номером раздела 0.0. добавить нельзя", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ispecificationbaseobjects_add.html", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeSectionIsTheSameRefusal()
    {
        var refusal = SpecificationRowInput.Validate(-3, "SPC.402.001", "Пластина", "2", null);

        Assert.NotNull(refusal);
        Assert.Equal("section_number", refusal.Field);
    }

    [Theory]
    [InlineData("", "Пластина", "2", "marking")]
    [InlineData("   ", "Пластина", "2", "marking")]
    [InlineData("SPC.402.001", "", "2", "name")]
    [InlineData("SPC.402.001", null, "2", "name")]
    [InlineData("SPC.402.001", "Пластина", "   ", "quantity")]
    [InlineData("SPC.402.001", "Пластина", null, "quantity")]
    public void BlankRequiredFieldsAreRefusedAndTheFieldIsNamed(
        string marking, string? name, string? quantity, string field)
    {
        var refusal = SpecificationRowInput.Validate(20, marking, name, quantity, null);

        Assert.NotNull(refusal);
        Assert.Equal(field, refusal.Field);
    }

    [Fact]
    public void ANegativePositionIsRefused()
    {
        var refusal = SpecificationRowInput.Validate(20, "SPC.402.001", "Пластина", "2", -1);

        Assert.NotNull(refusal);
        Assert.Equal("position", refusal.Field);
    }

    [Fact]
    public void AWellFormedRequestIsAcceptedWithAndWithoutAPosition()
    {
        Assert.Null(SpecificationRowInput.Validate(20, "SPC.402.001", "Пластина", "2", null));
        Assert.Null(SpecificationRowInput.Validate(20, "SPC.402.001", "Пластина", "2", 0));
        Assert.Null(SpecificationRowInput.Validate(20, "SPC.402.001", "Пластина", "2", 7));
    }

    [Fact]
    public void TheSchemaDemandsTheRowFieldsAndLeavesPositionOptional()
    {
        var schema = Tool("kompas_specification_add_row").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(node => node!.GetValue<string>()).ToArray();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        // `timeout_ms` is added to every tool by the request builder, not declared by this one;
        // `operation_id` is declared because the tool is a mutation.
        Assert.Equal(
            new[] { "document_id", "expected_revision", "marking", "name", "operation_id", "position",
                    "quantity", "section_number" },
            properties.Select(pair => pair.Key).Except(new[] { "timeout_ms" }).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "document_id", "expected_revision", "marking", "name", "operation_id", "quantity",
                    "section_number" },
            required.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void TheToolIsAMutationOfTheModelThatNeedsTheRevision()
    {
        var tool = Tool("kompas_specification_add_row");

        Assert.Equal("bom.specification_add_row", tool.WorkerCommand);
        Assert.True(tool.IsMutation);
        Assert.True(tool.Behaviour.RequiresDocument);
        Assert.True(tool.Behaviour.RequiresExpectedRevision);
        Assert.True(tool.Behaviour.RequiresOperationId);
    }

    [Fact]
    public void TheDescriptionNamesTheRouteTheLimitAndThatNoComponentLinkIsCreated()
    {
        var description = Tool("kompas_specification_add_row").Description;

        // A reader must be able to tell a created row from a call that only entered the collection.
        Assert.Contains("Update()", description, StringComparison.Ordinal);
        Assert.Contains("GEOMETRY_FAILED", description, StringComparison.Ordinal);
        Assert.Contains("IPropertyKeeper", description, StringComparison.Ordinal);
        Assert.Contains("НЕ пишется", description, StringComparison.Ordinal);
        Assert.Contains("0.0.", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolIsReachedThroughTheCatalogAndHasAGroup()
    {
        Assert.Equal("run", CompactCatalog.Reach("kompas_specification_add_row"));
        Assert.Equal("assembly", CompactCatalog.GroupOf("kompas_specification_add_row"));
    }

    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");
}
