using System.Reflection;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Domain.References;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Component properties (BOM-03): the field-to-page table, the contract it must match, and the
/// refusal of a reference that does not address a component.</summary>
/// <remarks>TEST: what is checkable without KOMPAS — no published field without its own help page, the
/// strict input schema, and the reference refusal. The document-kind refusal and the values themselves
/// need a live session: they are held by the acceptance rows BOM-03.* of <c>mcp-smoke.py</c>.
/// History: docs/decisions/bom.md#component-properties</remarks>
public class ComponentPropertiesTests
{
    [Fact]
    public void EveryPublishedFieldHasItsOwnHelpPage()
    {
        var published = DataFields();
        var documented = ComponentPropertyPages.All.Select(p => p.Field).ToArray();

        Assert.Equal(documented.OrderBy(f => f, StringComparer.Ordinal),
            published.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void PagesAreDistinctAndPointAtTheInterfaceThatOwnsTheProperty()
    {
        var pages = ComponentPropertyPages.All;

        Assert.Equal(8, pages.Count);
        Assert.Equal(pages.Count, pages.Select(p => p.Page).Distinct(StringComparer.Ordinal).Count());
        Assert.All(pages, p => Assert.EndsWith(".html", p.Page, StringComparison.Ordinal));
        Assert.All(pages, p => Assert.StartsWith("kspart_", p.Page, StringComparison.Ordinal));
    }

    [Fact]
    public void ASchemaCarriesTheTwoInputsAndNothingElse()
    {
        var schema = Tool("kompas_get_component_properties").InputSchema;
        var properties = (JsonObject)schema["properties"]!;

        Assert.False((bool)schema["additionalProperties"]!);
        // `timeout_ms` is added to every tool by the request builder, not declared by this one.
        Assert.Equal(new[] { "component_ref", "document_id" },
            properties.Select(p => p.Key).Except(new[] { "timeout_ms" })
                .OrderBy(k => k, StringComparer.Ordinal));
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("document_id", required);
        Assert.Contains("component_ref", required);
    }

    [Fact]
    public void TheToolIsAReadOfTheBomDomainAndNamesItsLimit()
    {
        var tool = Tool("kompas_get_component_properties");

        Assert.Equal("bom.component_properties", tool.WorkerCommand);
        Assert.False(tool.IsMutation);
        Assert.True(tool.Behaviour.RequiresDocument);
        Assert.Contains("CAPABILITY_UNAVAILABLE", tool.Description);
        Assert.Contains("excluded", tool.Description);
    }

    [Fact]
    public void AnUnknownReferenceIsRefusedAsStaleAndNotLookedUpAgain()
    {
        var registry = new ReferenceRegistry();

        var refusal = Assert.Throws<KompasContractException>(
            () => registry.Require("component:нет-такой", "doc-1", 1));

        Assert.Equal(ErrorCodes.StaleReference, refusal.Code);
        Assert.Equal(RetryPolicy.ReacquireContext, refusal.RetryPolicy);
    }

    [Fact]
    public void AReferenceOfAnotherRevisionIsRefusedAsStale()
    {
        var registry = new ReferenceRegistry();
        var stored = registry.Register("component", "doc-1", revision: 1, payload: null);

        var refusal = Assert.Throws<KompasContractException>(
            () => registry.Require(stored.Id, "doc-1", currentRevision: 2));

        Assert.Equal(ErrorCodes.StaleReference, refusal.Code);
    }

    /// <summary>The published data fields of the contract, in declaration order.</summary>
    private static string[] DataFields() => typeof(ComponentPropertiesDto)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => Snake(p.Name))
        .ToArray();

    /// <summary>PascalCase to the snake_case the contract publishes (<c>MultiBody</c> → <c>multi_body</c>).</summary>
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
