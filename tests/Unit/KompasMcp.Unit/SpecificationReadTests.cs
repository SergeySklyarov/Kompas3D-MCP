using System.Reflection;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Specification read (BOM-04): the field-to-page table and the contract it must match.</summary>
/// <remarks>TEST: what is checkable without KOMPAS — no published field without a documented read call, the
/// input schema, and the read-only shape of the tool. The values themselves and the empty-specification
/// answer need a live session: they are held by the acceptance rows BOM-04.* of <c>mcp-smoke.py</c>.
/// History: docs/decisions/bom.md#spec-read</remarks>
public class SpecificationReadTests
{
    [Fact]
    public void EveryPublishedFieldHasItsOwnDocumentedCall()
    {
        var published = typeof(SpecificationLineDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => Snake(p.Name))
            .ToArray();
        var documented = SpecificationFieldPages.All.Select(p => p.Field).ToArray();

        Assert.Equal(documented.OrderBy(f => f, StringComparer.Ordinal),
            published.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPageIsAHelpPageOfTheSpecificationRoute()
    {
        var pages = SpecificationFieldPages.All;

        Assert.Equal(6, pages.Count);
        Assert.All(pages, p => Assert.EndsWith(".html", p.Page, StringComparison.Ordinal));
        Assert.All(pages, p => Assert.Contains("specification", p.Page, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheToolTakesOneDocumentAndNothingElse()
    {
        var schema = Tool("kompas_get_specification").InputSchema;
        var properties = (JsonObject)schema["properties"]!;

        Assert.False((bool)schema["additionalProperties"]!);
        // `timeout_ms` is added to every tool by the request builder, not declared by this one.
        Assert.Equal(new[] { "document_id" },
            properties.Select(p => p.Key).Except(new[] { "timeout_ms" }).ToArray());
    }

    [Fact]
    public void TheToolIsAReadOfTheBomDomainAndNamesItsLimit()
    {
        var tool = Tool("kompas_get_specification");

        Assert.Equal("bom.get_specification", tool.WorkerCommand);
        Assert.False(tool.IsMutation);
        Assert.True(tool.Behaviour.RequiresDocument);
        // A limit a caller cannot see in the answer is a limit the caller will report as a defect.
        Assert.Contains(".spw", tool.Description);
    }

    /// <summary>PascalCase to the snake_case the contract publishes (<c>SectionNumber</c> →
    /// <c>section_number</c>).</summary>
    private static string Snake(string name)
    {
        var text = new System.Text.StringBuilder();
        foreach (var letter in name)
        {
            if (char.IsUpper(letter) && text.Length > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(letter));
        }

        return text.ToString();
    }

    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");
}
