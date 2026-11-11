using System.Text.Json.Nodes;
using KompasMcp.Domain.Bom;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Deleting a MODEL specification row: the input rule, the published shape and the mutation flags.</summary>
/// <remarks>TEST: what is checkable without KOMPAS — the pure input rule, the schema and the catalog flags. The
/// deletion itself, the absence read back through both roads, the save-and-reopen and every refusal need a live
/// session: they are held by the acceptance rows <c>SPEC-04.*</c> of <c>mcp-smoke.py</c>.
/// History: docs/decisions/bom.md#spec-delete-row</remarks>
public class SpecificationRowDeleteTests
{
    private const string Document = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AMissingReferenceIsRefused()
    {
        var refusal = SpecificationRowDeleteInput.Validate(Document, null);

        Assert.NotNull(refusal);
        Assert.Equal("row_ref", refusal.Field);
        Assert.Contains("не задана", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceToAnotherDocumentIsRefusedRatherThanFollowed()
    {
        var refusal = SpecificationRowDeleteInput.Validate(
            Document, Ref("SPC.402.001", 3, document: "fedcba9876543210fedcba9876543210"));

        Assert.NotNull(refusal);
        Assert.Equal("row_ref.document_id", refusal.Field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AReferenceWithoutItsDocumentIsRefused(string document)
    {
        var refusal = SpecificationRowDeleteInput.Validate(Document, Ref("SPC.402.001", 3, document: document));

        Assert.NotNull(refusal);
        Assert.Equal("row_ref.document_id", refusal.Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void AReferenceWithoutAPositiveRowNumberIsRefused(double? unique)
    {
        var refusal = SpecificationRowDeleteInput.Validate(
            Document, new SpecificationRowRefValues(Document, unique, "SPC.402.001"));

        Assert.NotNull(refusal);
        Assert.Equal("row_ref.unique_number", refusal.Field);
    }

    [Fact]
    public void AReferenceWithoutTheMarkingToCheckIsRefused()
    {
        // The marking is the only sign the reference is not stale: without it the wrong row could be deleted.
        var refusal = SpecificationRowDeleteInput.Validate(
            Document, new SpecificationRowRefValues(Document, 3, Marking: null));

        Assert.NotNull(refusal);
        Assert.Equal("row_ref.marking", refusal.Field);
    }

    [Fact]
    public void TheReferenceOfThisDocumentWithANumberAndAMarkingIsAccepted()
    {
        Assert.Null(SpecificationRowDeleteInput.Validate(Document, Ref("SPC.402.001", 3)));
        // An EMPTY marking is legal: a row may have none, and the comparison then checks that too.
        Assert.Null(SpecificationRowDeleteInput.Validate(Document, Ref(string.Empty, 3)));
    }

    [Fact]
    public void TheSchemaTakesTheReferenceAndNothingElse()
    {
        var schema = Tool("kompas_specification_delete_row").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(node => node!.GetValue<string>()).ToArray();

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(
            new[] { "document_id", "expected_revision", "operation_id", "row_ref" },
            properties.Select(pair => pair.Key).Except(new[] { "timeout_ms" }).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "document_id", "expected_revision", "operation_id", "row_ref" },
            required.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void TheToolIsAMutationOfTheModelThatNeedsTheRevision()
    {
        var tool = Tool("kompas_specification_delete_row");

        Assert.Equal("bom.specification_delete_row", tool.WorkerCommand);
        Assert.True(tool.IsMutation);
        Assert.True(tool.Behaviour.RequiresDocument);
        Assert.True(tool.Behaviour.RequiresExpectedRevision);
        Assert.True(tool.Behaviour.RequiresOperationId);
    }

    [Fact]
    public void TheDescriptionNamesTheDeleteRouteTheStaleReferenceAndTheSpwLimit()
    {
        var description = Tool("kompas_specification_delete_row").Description;

        // A reader must see which road deletes, how the absence is confirmed, what a stale reference means and
        // what the tool does not do.
        Assert.Contains("ISpecificationObject.Delete()", description, StringComparison.Ordinal);
        Assert.Contains("ksGetSpcObjectNumber", description, StringComparison.Ordinal);
        Assert.Contains("STALE_REFERENCE", description, StringComparison.Ordinal);
        Assert.Contains("GEOMETRY_FAILED", description, StringComparison.Ordinal);
        Assert.Contains("восстановить", description, StringComparison.Ordinal);
        Assert.Contains("НЕ пишется", description, StringComparison.Ordinal);
        Assert.Contains(".spw", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolIsReachedThroughTheCatalogAndHasAGroup()
    {
        Assert.Equal("run", CompactCatalog.Reach("kompas_specification_delete_row"));
        Assert.Equal("assembly", CompactCatalog.GroupOf("kompas_specification_delete_row"));
    }

    private static SpecificationRowRefValues Ref(string marking, double unique, string? document = null) =>
        new(document ?? Document, unique, marking);

    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");
}
