using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Editing a MODEL specification row: the input rule, the published shape and the mutation flags.</summary>
/// <remarks>TEST: what is checkable without KOMPAS — the pure input rule, the schema and the catalog flags. The
/// row itself, both read-back roads, the save-and-reopen and every refusal need a live session: they are held
/// by the acceptance rows <c>SPEC-03.*</c> of <c>mcp-smoke.py</c>.
/// History: docs/decisions/bom.md#spec-edit-row</remarks>
public class SpecificationRowEditTests
{
    private const string Document = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AnEmptyChangeSetIsRefused()
    {
        var refusal = SpecificationRowEditInput.Validate(
            Document, Ref("SPC.402.001", 3), null, null, null, null);

        Assert.NotNull(refusal);
        Assert.Equal("row_ref", refusal.Field);
        Assert.Contains("пуст", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", null, null, "marking")]
    [InlineData("   ", null, null, "marking")]
    [InlineData(null, "", null, "name")]
    [InlineData(null, null, "  ", "quantity")]
    public void ABlankValueInAGivenFieldIsRefusedAndTheFieldIsNamed(
        string? marking, string? name, string? quantity, string field)
    {
        var refusal = SpecificationRowEditInput.Validate(Document, Ref("SPC.402.001", 3), marking, name, quantity, null);

        Assert.NotNull(refusal);
        Assert.Equal(field, refusal.Field);
    }

    [Fact]
    public void AReferenceToAnotherDocumentIsRefusedRatherThanFollowed()
    {
        var refusal = SpecificationRowEditInput.Validate(
            Document, Ref("SPC.402.001", 3, document: "fedcba9876543210fedcba9876543210"), "SPC.402.002", null, null, null);

        Assert.NotNull(refusal);
        Assert.Equal("row_ref.document_id", refusal.Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void AReferenceWithoutAPositiveRowNumberIsRefused(double? unique)
    {
        var refusal = SpecificationRowEditInput.Validate(
            Document, new SpecificationRowRefValues(Document, unique, "SPC.402.001"), "SPC.402.002", null, null, null);

        Assert.NotNull(refusal);
        Assert.Equal("row_ref.unique_number", refusal.Field);
    }

    [Fact]
    public void AMissingReferenceIsRefused()
    {
        var refusal = SpecificationRowEditInput.Validate(Document, null, "SPC.402.002", null, null, null);

        Assert.NotNull(refusal);
        Assert.Equal("row_ref", refusal.Field);
    }

    [Fact]
    public void ANegativePositionIsRefused()
    {
        var refusal = SpecificationRowEditInput.Validate(Document, Ref("SPC.402.001", 3), null, null, null, -1);

        Assert.NotNull(refusal);
        Assert.Equal("position", refusal.Field);
    }

    [Fact]
    public void EverySingleFieldAndTheWholeSetAreAccepted()
    {
        Assert.Null(SpecificationRowEditInput.Validate(Document, Ref("SPC.402.001", 3), "SPC.402.002", null, null, null));
        Assert.Null(SpecificationRowEditInput.Validate(Document, Ref("SPC.402.001", 3), null, "Пластина", null, null));
        Assert.Null(SpecificationRowEditInput.Validate(Document, Ref("SPC.402.001", 3), null, null, "4", null));
        Assert.Null(SpecificationRowEditInput.Validate(Document, Ref("SPC.402.001", 3), null, null, null, 0));
        Assert.Null(SpecificationRowEditInput.Validate(
            Document, Ref("SPC.402.001", 3), "SPC.402.002", "Пластина", "4", 7));
    }

    [Fact]
    public void TheSchemaTakesTheReferenceAndLeavesEveryChangedFieldOptional()
    {
        var schema = Tool("kompas_specification_edit_row").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(node => node!.GetValue<string>()).ToArray();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(
            new[] { "document_id", "expected_revision", "marking", "name", "operation_id", "position",
                    "quantity", "row_ref" },
            properties.Select(pair => pair.Key).Except(new[] { "timeout_ms" }).OrderBy(k => k, StringComparer.Ordinal));
        // `position` stays optional: its absence means "the position does not change", not "position 0".
        Assert.Equal(
            new[] { "document_id", "expected_revision", "operation_id", "row_ref" },
            required.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void TheReferenceItselfDemandsTheDocumentTheNumberAndTheMarking()
    {
        var toolProperties = (JsonObject)Tool("kompas_specification_edit_row").InputSchema["properties"]!;
        var rowRef = (JsonObject)toolProperties["row_ref"]!;
        var properties = (JsonObject)rowRef["properties"]!;
        var required = ((JsonArray)rowRef["required"]!).Select(node => node!.GetValue<string>()).ToArray();

        Assert.Equal(
            new[] { "document_id", "marking", "unique_number" },
            properties.Select(pair => pair.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "document_id", "marking", "unique_number" }, required.OrderBy(k => k, StringComparer.Ordinal));
        Assert.False(rowRef["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void TheToolIsAMutationOfTheModelThatNeedsTheRevision()
    {
        var tool = Tool("kompas_specification_edit_row");

        Assert.Equal("bom.specification_edit_row", tool.WorkerCommand);
        Assert.True(tool.IsMutation);
        Assert.True(tool.Behaviour.RequiresDocument);
        Assert.True(tool.Behaviour.RequiresExpectedRevision);
        Assert.True(tool.Behaviour.RequiresOperationId);
    }

    [Fact]
    public void TheDescriptionNamesTheApi5RouteTheStaleReferenceAndTheApi7Limit()
    {
        var description = Tool("kompas_specification_edit_row").Description;

        // A reader must see which road persists, what a stale reference means, and what the tool does not do.
        Assert.Contains("ksSpcObjectEdit", description, StringComparison.Ordinal);
        Assert.Contains("ksSpcObjectEnd", description, StringComparison.Ordinal);
        Assert.Contains("STALE_REFERENCE", description, StringComparison.Ordinal);
        Assert.Contains("GEOMETRY_FAILED", description, StringComparison.Ordinal);
        Assert.Contains("Update() = false", description, StringComparison.Ordinal);
        Assert.Contains("SetSection", description, StringComparison.Ordinal);
        Assert.Contains("НЕ пишется", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolIsReachedThroughTheCatalogAndHasAGroup()
    {
        Assert.Equal("run", CompactCatalog.Reach("kompas_specification_edit_row"));
        Assert.Equal("assembly", CompactCatalog.GroupOf("kompas_specification_edit_row"));
    }

    private static SpecificationRowRefValues Ref(string marking, double unique, string? document = null) =>
        new(document ?? Document, unique, marking);

    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");
}
