using System.Text.Json.Nodes;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: a registered tool must be callable with exactly the arguments its own published
/// schema demands. A tool that cannot be called is exactly the stub the catalog forbids.</summary>
/// <remarks>MEASURED: two defects broke this at once and were invisible to every acceptance run until a test
/// finally called <c>kompas_read_topology</c> over MCP — an entry declared through <c>Mutation()</c> with
/// <c>requiresOperationId: false</c> never published the field the Host routed through the journal, and
/// <c>IsMutation</c> is <c>Destructive || RequiresOperationId</c>, so the journal received a null operation
/// id and the call died inside the Host as ArgumentNullException before KOMPAS was reached.
/// History: docs/decisions/tests.md#tool-catalog</remarks>
public class ToolCatalogTests
{
    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    public static TheoryData<string> MutationNames { get; } = new(
        ToolCatalog.All.Where(t => t.IsMutation).Select(t => t.Name));

    [Theory]
    [MemberData(nameof(MutationNames))]
    public void Mutation_PublishesTheOperationIdItDemands(string name)
    {
        var schema = Tool(name).InputSchema;
        var properties = schema["properties"] as JsonObject;
        var required = schema["required"] as JsonArray;

        Assert.NotNull(properties);
        Assert.True(
            properties!.ContainsKey("operation_id"),
            $"{name}: помечен как мутация, но схема не объявляет operation_id — обязательное поле " +
            "остаётся недостижимым для клиента, а strict-валидация отвергает вызов.");
        Assert.NotNull(required);
        Assert.Contains("operation_id", required!.Select(node => node!.GetValue<string>()));
    }

    [Theory]
    [InlineData("kompas_read_topology")]
    [InlineData("kompas_resolve_selection")]
    public void ReadsThatMintHandles_AreNotRoutedThroughTheJournal(string name)
    {
        var tool = Tool(name);

        // These two register structural references, but they change neither document nor model, so
        // docs/02 §2.1 (operation_id is demanded of mutations) makes them reads. The assertion that
        // matters is IsMutation == false: that is the flag choosing between the journal and a
        // direct dispatch, and while it was true the schema published no operation_id to fill it.
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
    }

    [Fact]
    public void NonMutations_DoNotAdvertiseOperationId()
    {
        // INVARIANT (rule refined 04.10.2026, not weakened): the sign is not "declared or not" but "declared
        // only if the tool ITSELF replays the outcome of a repeat". The Host tool `kompas_release_session` must
        // declare the field (row S03b requires it for destructiveHint=true) but writes no journal. A field the
        // client can send that neither the journal nor the tool uses is still forbidden.
        foreach (var tool in ToolCatalog.All.Where(t => !t.IsMutation))
        {
            var properties = tool.InputSchema["properties"] as JsonObject;
            var advertises = properties?.ContainsKey("operation_id") == true;
            Assert.True(
                !advertises || tool.Behaviour.ReplaysOperationId,
                $"{tool.Name}: не мутация, но схема обещает operation_id — поле, которое клиент может " +
                "прислать, а журнал его не записывает и инструмент его не воспроизводит.");
        }
    }

    [Fact]
    public void ReleaseSession_DeclaresTheOperationIdItReplays()
    {
        // INVARIANT: declaring the field and using it is one check, not two — "declared and swallowed" is the
        // defect the project catches with a separate class. Here the field must be BOTH in the schema AND
        // supported by behaviour (<c>ReplaysOperationId</c>), while the tool stays a Host tool and does not
        // become a model mutation.
        var tool = Tool("kompas_release_session");
        var properties = (JsonObject)tool.InputSchema["properties"]!;

        Assert.True(properties.ContainsKey("operation_id"));
        Assert.True(tool.Behaviour.ReplaysOperationId);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
    }

    [Fact]
    public void Catalog_NamesEveryWorkerCommandOnce()
    {
        Assert.Equal(
            ToolCatalog.All.Count,
            ToolCatalog.All.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ToolCatalog.All, t => Assert.False(string.IsNullOrWhiteSpace(t.WorkerCommand)));
    }

    [Fact]
    public void SketchStatus_IsAReadThatMintsNoHandles()
    {
        // INVARIANT: the tool answers a question about the STATE of an existing sketch — it creates no
        // references, changes no model and enters no edit mode. So it must be a read, else the call would enter
        // the mutation journal and bump the revision for an operation that never happened.
        var tool = Tool("kompas_get_sketch_status");

        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.Equal("sketch.status", tool.WorkerCommand);
    }

    [Fact]
    public void SketchStatus_DemandsASketchRefAndNothingElse()
    {
        // INVARIANT: addressing is an explicit reference. No active document, no selection, no "first sketch
        // that comes along": without this check the next refactor could add a convenient optional field and
        // bring guessing back into the contract.
        var schema = Tool("kompas_get_sketch_status").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("sketch_ref", required);
        Assert.Single(required);
        Assert.True(
            properties.Count is 1 or 2,
            "kompas_get_sketch_status: схема обещает больше полей, чем контракт — лишнее поле " +
            "клиент может прислать, и никто не скажет, что оно значило.");
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void SketchStatus_DescriptionCarriesTheHonestLimits()
    {
        // The description is the only place where the client reads what the tool does NOT say. Three statements
        // must survive the next catalog edit: the "!" value is not confirmed live, there is no degree of freedom,
        // and the read does not change the model.
        var description = Tool("kompas_get_sketch_status").Description;

        Assert.Contains("degrees_of_freedom всегда null", description, StringComparison.Ordinal);
        Assert.Contains("unresolved_redundancy_not_verified", description, StringComparison.Ordinal);
        Assert.Contains("не меняет ревизию", description, StringComparison.Ordinal);
        Assert.Contains("+", description, StringComparison.Ordinal);
        Assert.Contains("−", description, StringComparison.Ordinal);
        Assert.Contains("!", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ListBodies_PublishesTheHandleContractItsReadersDependOn()
    {
        // MEASURED 16.09.2026 (rows EX34/EX43): four consecutive calls on an unchanged body returned four
        // different strings, while the earlier string stayed usable for kompas_measure. That makes a body
        // reference a handle, not an identifier — a tester who does not know it writes an assertion that can only
        // fail, as EX43 did twice before switching to bbox comparison. The description is the only place a client
        // reads it, so it is asserted here.
        var description = Tool("kompas_list_bodies").Description;

        Assert.Contains("ручка", description, StringComparison.Ordinal);
        Assert.Contains("габарит", description, StringComparison.Ordinal);
        Assert.Contains("STALE_REFERENCE", description, StringComparison.Ordinal);
    }
}
