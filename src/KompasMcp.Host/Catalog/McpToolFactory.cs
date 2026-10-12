using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace KompasMcp.Host.Catalog;

/// <summary>Maps a catalog entry to the protocol tool the Host publishes in <c>tools/list</c>.</summary>
/// <remarks>INVARIANT: the listing the client receives and the size guard over it are built by THIS
/// function, so a test cannot measure a shape the Host never sends. A mapping copied into a test would
/// keep passing while the published answer grows.
/// History: docs/decisions/host.md#tools-list-size-guard</remarks>
public static class McpToolFactory
{
    /// <summary>One protocol tool. Annotations stay honest: a mutation is not idempotent just because
    /// the journal makes its replay safe.</summary>
    public static Tool Create(ToolDefinition tool) => new()
    {
        Name = tool.Name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = JsonDocument.Parse(tool.InputSchema.ToJsonString()).RootElement,
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = tool.Behaviour.ReadOnly,
            DestructiveHint = tool.Behaviour.Destructive,
            IdempotentHint = null,
            OpenWorldHint = false,
            Title = tool.Title,
        },
        Meta = null,
    };

    /// <summary>The whole listing, in catalog order.</summary>
    public static List<Tool> CreateAll(IReadOnlyList<ToolDefinition> tools) =>
        tools.Select(Create).ToList();
}
