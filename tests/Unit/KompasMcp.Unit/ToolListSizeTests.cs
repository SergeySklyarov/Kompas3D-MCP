using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The published <c>tools/list</c> has a size ceiling.</summary>
/// <remarks>INVARIANT: the whole listing, serialized exactly as the Host sends it, stays under
/// <see cref="CeilingChars"/> characters. The client pays for this text in EVERY session before it
/// asks anything, and it competes with the model's own context: a listing that grows silently takes
/// room from the work. The ceiling is the measured size after the descriptions were cut to the
/// contract plus 10 %, so an honest addition passes and quiet accumulation does not.
/// History: docs/decisions/host.md#tools-list-size-guard</remarks>
public sealed class ToolListSizeTests
{
    /// <summary>Measured size plus 10 %. MEASURED: the listing of 90 tools occupied 333 712 characters
    /// after the shared definitions were pruned to the referenced ones and the eight largest
    /// descriptions were cut to the contract; the same listing measured over the wire by a stdio client
    /// gave 343 533 characters with spaced separators, so the two instruments agree within 0.2 %.</summary>
    private const int CeilingChars = 367_100;

    /// <summary>How the transport writes a listing: attribute-named properties, nulls omitted, and
    /// text counted the way the client reads it. MEASURED: the default encoder escapes every Cyrillic
    /// letter as <c>\uXXXX</c>, which inflates the count 3.2 times; a JSON parser hands the model one
    /// character per letter, so the escaped form would guard a number nobody pays for.</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void PublishedListingStaysUnderTheCeiling()
    {
        var tools = McpToolFactory.CreateAll(ToolCatalog.All);
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
