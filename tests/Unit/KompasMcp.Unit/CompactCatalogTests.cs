using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KompasMcp.Contracts.Schema;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The compact catalog: a fixed listing over the ONE operation registry.</summary>
/// <remarks>INVARIANT: <c>compact</c> publishes the direct subset plus three tools, and its size does not
/// grow with the number of operations. The tests below hold the two claims a report would otherwise
/// only state: the registry is <see cref="ToolCatalog.All"/> and nothing else, and every Russian word
/// from an operation's own title finds that operation.
/// History: docs/decisions/host.md#catalog-mechanism</remarks>
public sealed class CompactCatalogTests
{
    /// <summary>Every catalog entry has a group: a name outside the table would be unreachable by
    /// group search while <c>find</c> still printed "other" for it.</summary>
    [Fact]
    public void EveryOperationHasAGroup()
    {
        var ungrouped = ToolCatalog.All.Select(t => t.Name).Where(n => CompactCatalog.GroupOf(n) == "other").ToArray();
        Assert.Empty(ungrouped);
    }

    /// <summary>The registry is the catalog, not a copy: every id <c>find</c> can return resolves to a
    /// real tool with a published schema.</summary>
    [Fact]
    public void SearchReturnsOnlyCatalogOperations()
    {
        var hits = CompactCatalog.Search(string.Empty, null);
        Assert.Equal(ToolCatalog.All.Count, hits.Count);
        foreach (var hit in hits)
        {
            Assert.NotNull(CompactCatalog.Operation(hit.Tool.Name));
            Assert.True(hit.Tool.InputSchema.ContainsKey("properties"));
        }
    }

    /// <summary>A Russian word from an operation's own title finds it: the search reads the published
    /// descriptions, so the vocabulary is the product's.</summary>
    [Theory]
    [InlineData("выдавить", "kompas_extrude")]
    [InlineData("эскиз", "kompas_create_sketch")]
    [InlineData("признака", "kompas_get_feature")]
    [InlineData("снимок", "kompas_export_image")]
    [InlineData("extrude", "kompas_extrude")]
    public void RussianWordFindsItsOperation(string query, string expected)
    {
        var ids = CompactCatalog.Search(query, null).Take(5).Select(hit => hit.Tool.Name).ToArray();
        Assert.Contains(expected, ids);
    }

    /// <summary>A group filter is exclusive: the answer carries no row of another group.</summary>
    [Fact]
    public void GroupFilterIsExclusive()
    {
        var hits = CompactCatalog.Search(string.Empty, "sketch");
        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal("sketch", CompactCatalog.GroupOf(hit.Tool.Name)));
    }

    /// <summary>An unknown id is answered with candidates, not with silence.</summary>
    [Fact]
    public void UnknownIdGivesNearestIds()
    {
        var nearest = CompactCatalog.NearestIds("kompas_extrud");
        Assert.Contains("kompas_extrude", nearest);
    }

    /// <summary>The compact listing is the direct subset plus three tools, and it is the same list for
    /// any registry size: nothing per-operation is published.</summary>
    /// <remarks>INVARIANT: <c>full</c> is the untouched catalog, so a client in the default mode sees
    /// exactly what it saw before the mode existed.</remarks>
    [Fact]
    public void CompactListingIsFixedAndFullIsUntouched()
    {
        var compact = CompactCatalog.Listing(CompactCatalog.CompactMode);
        Assert.Equal(CompactCatalog.Direct.Count + CompactCatalog.Tools.Count, compact.Count);
        Assert.Same(ToolCatalog.All, CompactCatalog.Listing(CompactCatalog.FullMode));

        var names = compact.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(CompactCatalog.FindTool, names);
        Assert.Contains(CompactCatalog.DescribeTool, names);
        Assert.Contains(CompactCatalog.RunTool, names);
        // A converted operation is NOT a direct tool: its schema is reached through describe.
        Assert.DoesNotContain("kompas_extrude", names);
        Assert.True(compact.Count < ToolCatalog.All.Count / 4);
    }

    /// <summary>Both instruction texts name only tools the client can call in that mode.</summary>
    [Theory]
    [InlineData(CompactCatalog.FullMode)]
    [InlineData(CompactCatalog.CompactMode)]
    public void InstructionsNamePublishedTools(string mode)
    {
        var published = CompactCatalog.Listing(mode).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in Mentioned(CompactCatalog.Instructions(mode)))
        {
            Assert.Contains(name, published);
        }
    }

    /// <summary>The three tools' schemas are closed objects and survive the startup check the Host
    /// runs on every published schema.</summary>
    [Fact]
    public void CompactToolSchemasAreUnderstood()
    {
        foreach (var tool in CompactCatalog.Tools)
        {
            JsonSchemaValidator.AssertSchemaIsUnderstood(tool.InputSchema);
            Assert.False(tool.InputSchema["additionalProperties"]!.GetValue<bool>());
        }
    }

    /// <summary><c>kompas_run</c> refuses the three argument faults the order names, each with the
    /// field: an unknown key, a wrong type, a missing required field.</summary>
    [Theory]
    [InlineData("""{"document_id":"x","no_such_field":1}""")]
    [InlineData("""{"sketch_ref":42}""")]
    [InlineData("""{}""")]
    public void RunRefusesBadArgumentsByName(string payload)
    {
        var tool = CompactCatalog.Operation("kompas_extrude")!;
        var violations = JsonSchemaValidator.Validate(tool.InputSchema, JsonNode.Parse(payload));
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.StartsWith("$", v.Path));
    }

    /// <summary>The outer <c>kompas_run</c> schema accepts any <c>args</c> keys: the refusal for an
    /// unknown operation field comes from the TARGET schema, so a closed <c>args</c> would refuse every
    /// call before the target schema was consulted.</summary>
    [Fact]
    public void RunSchemaAcceptsFreeFormArgs()
    {
        var run = CompactCatalog.Tools.Single(t => t.Name == CompactCatalog.RunTool);
        var payload = JsonNode.Parse("""{"id":"kompas_extrude","args":{"whatever":1,"nested":{"a":2}}}""");
        Assert.Empty(JsonSchemaValidator.Validate(run.InputSchema, payload));
    }

    /// <summary>The run tool is declared writing: a client must not treat it as a read because the id
    /// it carries happens to be a read.</summary>
    [Fact]
    public void RunIsDeclaredWriting()
    {
        var run = CompactCatalog.Tools.Single(t => t.Name == CompactCatalog.RunTool);
        Assert.False(run.Behaviour.ReadOnly);
        Assert.True(run.Behaviour.Destructive);
        Assert.True(run.IsHostLocal);
    }

    /// <summary>The size of the compact listing cannot grow with the registry: no operation name,
    /// schema or description of an unconverted operation reaches it.</summary>
    /// <remarks>INVARIANT: the published text holds only the direct subset and the three catalog tools,
    /// so converting the remaining operations changes not one character of <c>tools/list</c>. The
    /// per-operation text lives in the ANSWERS of find/describe, which the client asks for one at a
    /// time. MEASURED: the whole listing is 23 287 characters against 363 533 in <c>full</c>.
    /// TEST: this test holds the negative half — a leaked operation name fails it.</remarks>
    [Fact]
    public void CompactListingCarriesNoOperation()
    {
        var text = JsonSerializer.Serialize(
            McpToolFactory.CreateAll(CompactCatalog.Listing(CompactCatalog.CompactMode)), Wire);
        var direct = CompactCatalog.Direct.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var leaked = ToolCatalog.All
            .Select(t => t.Name)
            .Where(name => !direct.Contains(name) && text.Contains("\"" + name + "\"", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(leaked);

        var fullSize = JsonSerializer.Serialize(McpToolFactory.CreateAll(ToolCatalog.All), Wire).Length;
        Assert.True(text.Length * 4 < fullSize, $"compact {text.Length} символов против full {fullSize}");
    }

    /// <summary>Every operation of the registry is reachable, and exactly one way: the direct subset by
    /// its own tool, the rest through <c>kompas_run</c>. No operation is left without a path.</summary>
    /// <remarks>INVARIANT: <see cref="CompactCatalog.Reach"/> answers one of two words for every
    /// registry name, and the two sets do not overlap. A third answer would be the prototype's "exists
    /// but not converted", which this mode no longer has.
    /// History: docs/decisions/host.md#catalog-mechanism</remarks>
    [Fact]
    public void EveryOperationIsReachableExactlyOneWay()
    {
        var direct = CompactCatalog.Direct.Select(t => t.Name).ToArray();
        var viaRun = CompactCatalog.ViaRun;

        Assert.Empty(direct.Intersect(viaRun, StringComparer.Ordinal));
        Assert.Equal(ToolCatalog.All.Count, direct.Length + viaRun.Count);
        Assert.Equal(ToolCatalog.All.Count, CompactCatalog.Search(string.Empty, null).Count);

        foreach (var tool in ToolCatalog.All)
        {
            var reach = CompactCatalog.Reach(tool.Name);
            Assert.Contains(reach, new[] { "direct", "run" });
            Assert.Equal(direct.Contains(tool.Name, StringComparer.Ordinal) ? "direct" : "run", reach);
        }
    }

    /// <summary>The direct subset is a fixed, named set: it is the prototype's eleven tools and nothing
    /// else, so a silent addition to it fails here rather than in a report.</summary>
    /// <remarks>INVARIANT: the set does not grow with the registry. TEST: this test.
    /// History: docs/decisions/host.md#catalog-mechanism</remarks>
    [Fact]
    public void DirectSubsetIsTheNamedEleven()
    {
        var expected = new[]
        {
            "kompas_health", "kompas_connect", "kompas_session_status", "kompas_acquire_session",
            "kompas_release_session", "kompas_disconnect", "kompas_create_document",
            "kompas_open_document", "kompas_close_document", "kompas_get_context",
            "kompas_capabilities",
        };

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal),
            CompactCatalog.Direct.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>The <c>find</c> limit is the size of the registry, not a literal: a bound smaller than
    /// the catalog would hide operations from a search that claims to cover it.</summary>
    [Fact]
    public void FindLimitComesFromTheRegistry()
    {
        var find = CompactCatalog.Tools.Single(t => t.Name == CompactCatalog.FindTool);
        var limit = (find.InputSchema["properties"] as JsonObject)!["limit"] as JsonObject;
        var maximum = limit!["maximum"]!.GetValue<long>();
        Assert.Equal(ToolCatalog.All.Count, (int)maximum);
    }

    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static IEnumerable<string> Mentioned(string text)
    {
        foreach (var word in text.Split(' ', '\n', '\r', '(', ')', ',', '.', ':', '→', '—', '-', '/'))
        {
            var trimmed = word.Trim();
            if (trimmed.StartsWith("kompas_", StringComparison.Ordinal))
            {
                yield return trimmed.TrimEnd('/', ';');
            }
        }
    }
}
