using System.Text.Json.Nodes;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: a registered tool must be callable with exactly the arguments its own published schema
/// demands — a tool that cannot be called is the stub the catalog forbids.</summary>
/// <remarks>MEASURED: two defects broke this at once and were invisible to every acceptance run until a test
/// called <c>kompas_read_topology</c> over MCP — an entry declared through <c>Mutation()</c> with
/// <c>requiresOperationId: false</c> never published the field the Host routed through the journal, and
/// <c>IsMutation</c> is <c>Destructive || RequiresOperationId</c>, so the journal received a null operation id
/// and the call died inside the Host as ArgumentNullException before KOMPAS.
/// History: docs/decisions/tests.md#tool-catalog-2</remarks>
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

        // These two register structural references, but they change neither document nor model, so docs/02 §2.1
        // makes them reads. The assertion that matters is IsMutation == false: that flag chooses between the
        // journal and a direct dispatch.
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
    }

    [Fact]
    public void NonMutations_DoNotAdvertiseOperationId()
    {
        // INVARIANT (rule refined, not weakened): the sign is not "declared or not" but "declared only if the
        // tool ITSELF replays the outcome of a repeat". <c>kompas_release_session</c> must declare the field
        // (row S03b for destructiveHint=true) but writes no journal.
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
        // INVARIANT: declaring the field and using it is one check, not two — the field must be BOTH in the
        // schema AND supported by behaviour (<c>ReplaysOperationId</c>), while the tool stays a Host tool.
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
        // references, changes no model and enters no edit mode. So it must be a read, else the call enters the
        // journal and bumps the revision for an operation that never happened.
        var tool = Tool("kompas_get_sketch_status");

        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.Equal("sketch.status", tool.WorkerCommand);
    }

    [Fact]
    public void SketchStatus_DemandsASketchRefAndNothingElse()
    {
        // INVARIANT: addressing is an explicit reference — no active document, no selection, no "first sketch
        // that comes along"; without this check a refactor could add a convenient optional field and bring
        // guessing back into the contract.
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
        // must survive: the "!" value is not confirmed live, there is no degree of freedom, and the read does not
        // change the model.
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
        // MEASURED (rows EX34/EX43): four consecutive calls on an unchanged body returned four different strings,
        // while the earlier string stayed usable for kompas_measure — a body reference is a handle, not an
        // identifier, and a tester who does not know it writes an assertion that can only fail. The description
        // is the only place a client reads it, so it is asserted here.
        var description = Tool("kompas_list_bodies").Description;

        Assert.Contains("ручка", description, StringComparison.Ordinal);
        Assert.Contains("габарит", description, StringComparison.Ordinal);
        Assert.Contains("STALE_REFERENCE", description, StringComparison.Ordinal);
    }

    // ── the shared `plane` form of kompas_create_sketch / kompas_set_sketch_plane ────────────────

    private const string SupportTools = "kompas_create_sketch";

    private static JsonObject PlaneProperty(string tool)
    {
        var properties = (JsonObject)Tool(tool).InputSchema["properties"]!;
        return (JsonObject)properties["plane"]!;
    }

    /// <summary>INVARIANT: the "reference only" support must pass the published schema — it is the form
    /// the documented scenario «auxiliary plane → sketch by reference» is expressed in.</summary>
    /// <remarks>MEASURED: <c>base</c> used to be in <c>required</c> AND its own enum did not carry
    /// <c>null</c>, so this form was refused by the client's schema before the call reached the server.
    /// The assertion goes through the SAME validator the Host uses, on a parsed payload: a schema read
    /// as a JSON tree and a schema used to validate are two different claims.
    /// History: docs/decisions/tests.md#tool-catalog-2</remarks>
    [Theory]
    [InlineData("kompas_create_sketch")]
    [InlineData("kompas_set_sketch_plane")]
    public void SketchPlane_ReferenceOnlyForm_PassesThePublishedSchema(string tool)
    {
        var schema = Tool(tool).InputSchema;
        var required = schema["required"] as JsonArray;
        Assert.NotNull(required);
        Assert.DoesNotContain("base", required!.Select(node => node!.GetValue<string>()));

        var payload = JsonNode.Parse("""
            {"document_id":"0123456789abcdef0123456789abcdef","expected_revision":1,
             "operation_id":"11111111-2222-3333-4444-555555555555",
             "plane":{"reference":"plane:0123456789abcdef0123456789abcdef"}}
            """)!.AsObject();
        if (tool == "kompas_set_sketch_plane")
        {
            // The support change names the sketch it re-anchors; creation does not.
            payload["sketch_ref"] = "sketch:0123456789abcdef0123456789abcdef";
        }

        Assert.Empty(JsonSchemaValidator.Validate(schema, payload));
    }

    /// <summary>INVARIANT: a client that fills every declared field with <c>null</c> must not be refused
    /// by the enum of the field it is not using.</summary>
    /// <remarks>MEASURED: the enum carried only the three plane names while the type already allowed
    /// <c>null</c>, so <c>base: null</c> failed its own field. History: docs/decisions/tests.md#tool-catalog-2</remarks>
    [Fact]
    public void SketchPlane_BaseEnum_CarriesNull()
    {
        var baseSchema = (JsonObject)((JsonObject)PlaneProperty(SupportTools)["properties"]!)["base"]!;
        var types = (JsonArray)baseSchema["type"]!;
        var values = (JsonArray)baseSchema["enum"]!;

        Assert.Contains("null", types.Select(node => node?.GetValue<string>()));
        Assert.Contains(values, node => node is null);
    }

    /// <summary>INVARIANT: the description states the rule the server enforces in words, because
    /// <c>oneOf</c>/<c>anyOf</c> are not evaluated by every client and a rule the client ignores is a
    /// rule that does not exist. It also names what is NOT accepted, so the refusal is not a surprise.</summary>
    [Fact]
    public void SketchPlane_DescriptionNamesTheRuleAndTheFaceRefusal()
    {
        var plane = PlaneProperty(SupportTools);
        var reference = (JsonObject)((JsonObject)plane["properties"]!)["reference"]!;
        var text = plane["description"]!.GetValue<string>() + " " + reference["description"]!.GetValue<string>();

        Assert.Contains("ИМЕННО ОДНО", text, StringComparison.Ordinal);
        Assert.Contains("INVALID_ARGUMENT", text, StringComparison.Ordinal);
        Assert.Contains("Грань", text, StringComparison.Ordinal);
        Assert.Contains("kompas_create_aux_geometry", text, StringComparison.Ordinal);
    }

    /// <summary>INVARIANT: ONE form for both tools. A second dialect would make "the same support" mean
    /// two different things, and a client reading one tool would learn the wrong rule for the other.</summary>
    [Fact]
    public void SketchPlane_IsTheSameFormForBothTools()
    {
        Assert.Equal(
            PlaneProperty("kompas_create_sketch").ToJsonString(),
            PlaneProperty("kompas_set_sketch_plane").ToJsonString());
    }
}
