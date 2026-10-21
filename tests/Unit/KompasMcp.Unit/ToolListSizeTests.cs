using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The published <c>tools/list</c> has a size ceiling and a fixed number of direct tools.</summary>
/// <remarks>INVARIANT: the listing, serialized exactly as the Host sends it, stays under
/// <see cref="CeilingChars"/> characters, and it publishes exactly <see cref="DirectToolCount"/> direct
/// tools beside the three catalog tools. The client pays for this text in EVERY session before it asks
/// anything, and it competes with the model's own context: a listing that grows silently takes room from
/// the work. History: docs/decisions/host.md#tools-list-size-guard</remarks>
public sealed class ToolListSizeTests
{
    /// <summary>Measured size plus 5 %: an honest addition passes, quiet accumulation does not.</summary>
    /// <remarks>MEASURED: the listing of this commit occupies 22 687 characters, counted the way the
    /// client reads it (see <see cref="Wire"/>). The ceiling is set from THIS instrument's measure: the
    /// default encoder escapes every Cyrillic letter, so a differently encoded dump would guard a number
    /// nobody pays for. History: docs/decisions/host.md#tools-list-size-guard</remarks>
    private const int CeilingChars = 23_821;

    /// <summary>How many direct tools the catalog publishes beside the three catalog tools.</summary>
    /// <remarks>INVARIANT: the number is exact — growth is a deliberate edit of this test, not a silent
    /// addition to the listing. History: docs/decisions/host.md#catalog-mechanism</remarks>
    private const int DirectToolCount = 11;

    /// <summary>How the transport writes a listing: attribute-named properties, nulls omitted, and
    /// text counted the way the client reads it. MEASURED: the default encoder escapes every Cyrillic
    /// letter as <c>\uXXXX</c>, which inflates the count 3.2 times.</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void PublishedListingStaysUnderTheCeiling()
    {
        var tools = McpToolFactory.CreateAll(CompactCatalog.Listing());
        var sizes = tools
            .Select(tool => (tool.Name, Size: JsonSerializer.Serialize(tool, Wire).Length))
            .OrderByDescending(row => row.Size)
            .ToArray();
        var total = JsonSerializer.Serialize(tools, Wire).Length;

        Assert.True(total <= CeilingChars,
            $"tools/list занимает {total} символов при потолке {CeilingChars}. "
            + "Крупнейшие инструменты: "
            + string.Join("; ", sizes.Take(5).Select(row => $"{row.Name} {row.Size}")));
    }

    /// <summary>The direct subset is a fixed size: the listing cannot grow by publishing one more
    /// operation as a tool.</summary>
    [Fact]
    public void DirectToolCountIsFixed()
    {
        Assert.Equal(DirectToolCount, CompactCatalog.Direct.Count);
        Assert.Equal(DirectToolCount + CompactCatalog.Tools.Count, CompactCatalog.Listing().Count);
    }

    /// <summary>The guard must measure the listing the Host actually sends, not a look-alike: the
    /// factory it calls is the same one <c>Program.ListTools</c> calls.</summary>
    [Fact]
    public void GuardMeasuresThePublishedShape()
    {
        var tool = McpToolFactory.Create(ToolCatalog.All[0]);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tool, Wire));
        var root = document.RootElement;

        Assert.Equal(ToolCatalog.All[0].Name, root.GetProperty("name").GetString());
        Assert.Equal(ToolCatalog.All[0].Description, root.GetProperty("description").GetString());
        Assert.True(root.GetProperty("inputSchema").TryGetProperty("additionalProperties", out var closed));
        Assert.False(closed.GetBoolean());
        Assert.True(root.TryGetProperty("annotations", out var annotations));
        Assert.Equal(ToolCatalog.All[0].Title, annotations.GetProperty("title").GetString());
    }
}
