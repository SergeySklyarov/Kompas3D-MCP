using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>BOM domain: grouping, filters, the sum invariant and the contract of two tools.</summary>
/// <remarks>TEST: what is checkable without KOMPAS — the grouping key, the filter counting, the invariant
/// <c>sum(rows) + skipped = walked</c>, the strict schema, and the description naming the full-path rule.
/// The "the route reads the fixture" check is NOT here: it requires a run on the target version (group
/// <c>bom</c> of <c>mcp-smoke.py</c>). History: docs/decisions/bom.md#bom-tool</remarks>
public class BomDomainTests
{
    private static BomInstance Instance(string? file, string? marking = null, string? name = null,
        bool? hidden = false, bool? excluded = false, bool? standard = false, string? material = null) =>
        new(file, name, marking, material, hidden, excluded, standard);

    private static BomOptions Default(string groupBy = BomSummary.GroupByFile) =>
        new(IncludeHidden: false, IncludeExcluded: false, IncludeStandard: false, groupBy);

    [Fact]
    public void Grouping_ByFullPath_KeepsTwoFilesWithTheSameNameApart()
    {
        var instances = new[]
        {
            Instance(@"C:\a\part.m3d"), Instance(@"C:\a\part.m3d"), Instance(@"C:\a\part.m3d"),
            Instance(@"C:\b\part.m3d"),
        };

        var summary = BomSummary.Build(instances, Default());

        Assert.Equal(2, summary.Rows.Count);
        Assert.Equal(3, summary.Rows[0].Instances);
        Assert.Equal(1, summary.Rows[1].Instances);
        Assert.Equal(2, summary.UniqueFiles);
    }

    [Fact]
    public void Grouping_ByFileAndDesignation_SplitsOneFileByMarking()
    {
        var instances = new[]
        {
            Instance(@"C:\a\part.m3d", marking: "A-1"),
            Instance(@"C:\a\part.m3d", marking: "A-1"),
            Instance(@"C:\a\part.m3d", marking: "A-2"),
        };

        var summary = BomSummary.Build(instances, Default(BomSummary.GroupByFileDesignation));

        Assert.Equal(2, summary.Rows.Count);
        Assert.Equal(2, summary.Rows[0].Instances);
        Assert.Equal("A-2", summary.Rows[1].Marking);
        Assert.Equal(1, summary.UniqueFiles);
        Assert.Equal(3, summary.Instances);
    }

    [Fact]
    public void Filters_CountSkippedByReason_AndTheSumStaysEqual()
    {
        var instances = new[]
        {
            Instance(@"C:\a\part.m3d"),
            Instance(@"C:\a\part.m3d", hidden: true),
            Instance(@"C:\a\part.m3d", excluded: true),
            Instance(@"C:\lib\bolt.m3d", standard: true),
            Instance(@"C:\sub.m3d"),
            Instance(@"C:\sub.m3d"),
        };

        var summary = BomSummary.Build(instances, Default());

        Assert.Equal(3, summary.Instances);
        Assert.Equal(1, summary.Skipped.Hidden);
        Assert.Equal(1, summary.Skipped.Excluded);
        Assert.Equal(1, summary.Skipped.Standard);
        Assert.Equal(instances.Length, summary.Instances + summary.Skipped.Total);
    }

    [Fact]
    public void Filters_IncludeFlagsReturnTheInstances()
    {
        var instances = new[]
        {
            Instance(@"C:\a\part.m3d", hidden: true),
            Instance(@"C:\a\part.m3d", excluded: true),
            Instance(@"C:\lib\bolt.m3d", standard: true),
        };

        var summary = BomSummary.Build(instances, new BomOptions(true, true, true, BomSummary.GroupByFile));

        Assert.Equal(3, summary.Instances);
        Assert.Equal(0, summary.Skipped.Total);
    }

    [Fact]
    public void Filters_AnUnreadFlagDoesNotDropTheInstance()
    {
        var instances = new[] { Instance(@"C:\a\part.m3d", hidden: null, excluded: null, standard: null) };

        var summary = BomSummary.Build(instances, Default());

        Assert.Single(summary.Rows);
        Assert.Equal(1, summary.Instances);
        Assert.Equal(0, summary.Skipped.Total);
    }

    [Fact]
    public void Filters_FirstReasonWins_WhenAnInstanceIsBothHiddenAndExcluded()
    {
        var instances = new[] { Instance(@"C:\a\part.m3d", hidden: true, excluded: true) };

        var summary = BomSummary.Build(instances, Default());

        Assert.Equal(1, summary.Skipped.Hidden);
        Assert.Equal(0, summary.Skipped.Excluded);
        Assert.Equal(1, summary.Skipped.Total);
    }

    [Theory]
    [InlineData("file", true)]
    [InlineData("file+designation", true)]
    [InlineData("name", false)]
    [InlineData(null, false)]
    public void GroupBy_IsCheckedAgainstTheTwoAllowedKeys(string? value, bool allowed) =>
        Assert.Equal(allowed, BomSummary.IsGroupBy(value));

    [Fact]
    public void Tools_AreRegisteredWithTheirOwnWorkerCommands()
    {
        Assert.Equal("bom.get", Tool("kompas_get_bom").WorkerCommand);
        Assert.Equal("bom.export", Tool("kompas_export_bom").WorkerCommand);
        Assert.False(Tool("kompas_get_bom").IsMutation);
        Assert.True(Tool("kompas_export_bom").IsMutation);
    }

    [Fact]
    public void Schemas_AreStrictAndDeclareTheFilters()
    {
        foreach (var name in new[] { "kompas_get_bom", "kompas_export_bom" })
        {
            var schema = Tool(name).InputSchema;
            var properties = (JsonObject)schema["properties"]!;
            Assert.False((bool)schema["additionalProperties"]!);
            foreach (var field in new[]
                     {
                         "document_id", "include_hidden", "include_excluded", "include_standard", "recurse",
                         "group_by",
                     })
            {
                Assert.True(properties.ContainsKey(field), $"{name}: нет поля {field}");
            }
        }

        var required = ((JsonArray)Tool("kompas_export_bom").InputSchema["required"]!)
            .Select(n => n!.GetValue<string>())
            .ToArray();
        Assert.Contains("output_path", required);
        Assert.Contains("operation_id", required);
    }

    [Fact]
    public void Descriptions_NameTheFullPathRuleAndTheSumInvariant()
    {
        var description = Tool("kompas_get_bom").Description;
        Assert.Contains("ПОЛНОМУ пути", description);
        Assert.Contains("instances_walked", description);
        Assert.Contains("hidden/excluded", description);
    }

    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");
}
