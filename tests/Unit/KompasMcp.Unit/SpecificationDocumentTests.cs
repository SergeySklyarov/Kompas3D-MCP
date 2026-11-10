using System.Reflection;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Reading a <c>.spw</c> DOCUMENT: the "exactly one source" rule and the published shape of the
/// tool.</summary>
/// <remarks>TEST: what is checkable without KOMPAS — the input rule, the schema and the read-only shape.
/// The rows, both roads and every refusal need a live session: they are held by the acceptance rows
/// <c>SPEC-01.*</c> of <c>mcp-smoke.py</c>.
/// History: docs/decisions/bom.md#spec-read-spw</remarks>
public class SpecificationDocumentTests
{
    [Fact]
    public void OnePathIsThePathSource()
    {
        var target = SpecificationDocumentInput.Parse(@"C:\models\spec.spw", null, out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(target);
        Assert.Equal(SpecificationDocumentSource.Path, target.Source);
        Assert.Equal(@"C:\models\spec.spw", target.Value);
    }

    [Fact]
    public void OneDocumentIdIsTheIdSource()
    {
        var target = SpecificationDocumentInput.Parse(null, "0123456789abcdef0123456789abcdef", out var refusal);

        Assert.Null(refusal);
        Assert.NotNull(target);
        Assert.Equal(SpecificationDocumentSource.DocumentId, target.Source);
        Assert.Equal("0123456789abcdef0123456789abcdef", target.Value);
    }

    [Fact]
    public void BothSourcesAreRefused()
    {
        var target = SpecificationDocumentInput.Parse(
            @"C:\models\spec.spw", "0123456789abcdef0123456789abcdef", out var refusal);

        Assert.Null(target);
        Assert.NotNull(refusal);
        Assert.Contains("path", refusal, StringComparison.Ordinal);
        Assert.Contains("document_id", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSourceIsRefusedByTheSameRule()
    {
        var target = SpecificationDocumentInput.Parse(null, null, out var refusal);

        Assert.Null(target);
        Assert.NotNull(refusal);
        Assert.Contains("path", refusal, StringComparison.Ordinal);
        Assert.Contains("document_id", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalSaysWhichTwoSourcesWereGiven()
    {
        SpecificationDocumentInput.Parse(@"C:\a.spw", "doc-1", out var refusal);

        Assert.Contains(@"C:\a.spw", refusal!, StringComparison.Ordinal);
        Assert.Contains("doc-1", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", null)]
    [InlineData(null, "  ")]
    public void BlankIsNotAGivenSource(string? path, string? documentId)
    {
        var target = SpecificationDocumentInput.Parse(path, documentId, out var refusal);

        Assert.Null(target);
        Assert.NotNull(refusal);
    }

    [Fact]
    public void TheSchemaOffersTwoOptionalSourcesAndNothingElse()
    {
        var schema = Tool("kompas_get_specification_document").InputSchema;
        var properties = (JsonObject)schema["properties"]!;

        Assert.False((bool)schema["additionalProperties"]!);
        // `timeout_ms` is added to every tool by the request builder, not declared by this one.
        Assert.Equal(new[] { "document_id", "path" },
            properties.Select(p => p.Key).Except(new[] { "timeout_ms" }).OrderBy(k => k, StringComparer.Ordinal));
        // Neither is required: the "exactly one" rule is enforced by the server, not by the schema.
        Assert.True(schema["required"] is null || ((JsonArray)schema["required"]!).Count == 0);
    }

    [Fact]
    public void TheToolIsAReadOfTheBomDomainAndNamesTheDocumentItReads()
    {
        var tool = Tool("kompas_get_specification_document");

        Assert.Equal("bom.get_specification_document", tool.WorkerCommand);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresDocument);
        Assert.Contains(".spw", tool.Description);
        Assert.Contains("ровно одно", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The document kind on the wire is a WORD, so a client never has to decode a number.</summary>
    [Fact]
    public void TheSpecificationKindTravelsAsAWord()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new SpecificationDocumentDto(@"C:\models\spec.spw", DocumentKind.Specification, true),
            KompJson.Options);

        Assert.Contains("\"kind\":\"specification\"", json, StringComparison.Ordinal);
    }

    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");
}
