using System.Text.Json.Nodes;
using KompasMcp.Contracts;
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

    /// <summary>INVARIANT: the RESOLVED description of <c>operation_id</c> names the format, because
    /// <c>format</c>/<c>minLength</c> live in <c>$defs</c> and a client that folds a schema into a
    /// signature keeps only the text.</summary>
    /// <remarks>MEASURED: a client sent <c>"omega-phase1-connect"</c> and got INVALID_ARGUMENT — the
    /// only place the UUID requirement appeared was <c>$defs/operation_id</c>, and the visible signature
    /// said just <c>operation_id: string</c>. The check is made on the description reached through the
    /// reference, not on the reference itself.
    /// History: docs/decisions/contracts.md#operation-id-format</remarks>
    [Fact]
    public void OperationId_ResolvedDescriptionNamesTheUuidFormat()
    {
        var checkedTools = 0;
        foreach (var tool in ToolCatalog.All)
        {
            var properties = tool.InputSchema["properties"] as JsonObject;
            if (properties?["operation_id"] is not JsonObject field)
            {
                continue;
            }

            var defs = tool.InputSchema["$defs"] as JsonObject;
            var resolved = field;
            if (field["$ref"]?.GetValue<string>() is { } reference && defs is not null)
            {
                resolved = defs[reference[(reference.LastIndexOf('/') + 1)..]] as JsonObject;
            }
            else if (field["anyOf"] is JsonArray branches)
            {
                resolved = branches
                    .Select(b => b as JsonObject)
                    .FirstOrDefault(b => b?["$ref"] is not null);
                if (resolved?["$ref"]?.GetValue<string>() is { } inner && defs is not null)
                {
                    resolved = defs[inner[(inner.LastIndexOf('/') + 1)..]] as JsonObject;
                }
            }

            var description = resolved?["description"]?.GetValue<string>() ?? string.Empty;
            Assert.True(description.Contains("UUID", StringComparison.Ordinal),
                $"{tool.Name}: разрешённое описание operation_id не называет UUID — клиент, "
                + "сворачивающий схему в сигнатуру, теряет формат.");
            Assert.True(resolved?["format"]?.GetValue<string>() == "uuid",
                $"{tool.Name}: разрешённая схема operation_id не несёт format=uuid.");
            checkedTools++;
        }

        Assert.True(checkedTools >= 40, $"проверено инструментов с operation_id: {checkedTools} — "
            + "похоже, разбор ссылки перестал находить поле.");
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

    // ── the shared `selection_predicate` of kompas_resolve_selection ─────────────────────────────

    /// <summary>INVARIANT: the published predicate carries no field the server refuses — a field in the
    /// schema that no value of it can satisfy is a promise the tool does not keep, and the caller pays a
    /// call to learn it.</summary>
    /// <remarks>MEASURED: <c>coordinate_space</c> was declared with <c>enum [parent, assembly_world]</c>
    /// while every call carrying it was refused <c>INVALID_ARGUMENT</c>.
    /// History: docs/decisions/contracts.md#selection-predicate-coordinate-space</remarks>
    [Fact]
    public void SelectionPredicate_DeclaresNoUnsupportedField()
    {
        var predicate = SelectionPredicate();
        var properties = (JsonObject)predicate["properties"]!;

        Assert.Equal(
            new[] { "area_range_mm2", "normal_angle_tolerance_deg", "normal_direction", "surface_type" },
            properties.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.False(predicate["additionalProperties"]!.GetValue<bool>());
    }

    /// <summary>INVARIANT: the closure is the gate. A predicate field outside the declared set must fail
    /// validation of the SAME schema the Host validates against, so the refusal is a schema refusal — not
    /// a server branch that can drift away from the schema it is meant to mirror.</summary>
    [Fact]
    public void SelectionPredicate_UnknownField_IsRefusedByThePublishedSchema()
    {
        var payload = JsonNode.Parse("""
            {"body_ref":"body:0123456789abcdef0123456789abcdef",
             "predicate":{"surface_type":"plane","coordinate_space":"parent"}}
            """)!.AsObject();

        Assert.NotEmpty(JsonSchemaValidator.Validate(Tool("kompas_resolve_selection").InputSchema, payload));
    }

    /// <summary>INVARIANT: <c>normal_direction</c> names the frame it lives in, because the tool resolves
    /// faces from a body reference and a caller who assumes assembly coordinates gets a silently wrong
    /// selection.</summary>
    [Fact]
    public void SelectionPredicate_NormalDirection_NamesItsFrame()
    {
        var properties = (JsonObject)SelectionPredicate()["properties"]!;
        var normal = (JsonObject)properties["normal_direction"]!;

        Assert.Contains("системе координат детали", normal["description"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    private static JsonObject SelectionPredicate()
    {
        var defs = (JsonObject)Tool("kompas_resolve_selection").InputSchema["$defs"]!;
        return (JsonObject)defs["selection_predicate"]!;
    }

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

    /// <summary>The published <c>direction</c> text must state the measured cut inversion, not the old
    /// "positive means along the sketch normal" claim that misled a client.</summary>
    /// <remarks>MEASURED on v24 and DOCUMENTED in the help for <c>directionType</c>: «Для вырезаемого элемента
    /// выдавливания направление противоположно нормали». The schema now says so for cut and points at the
    /// field that names the side in part coordinates.
    /// History: docs/decisions/adapter-core.md#material-direction-toward</remarks>
    [Fact]
    public void Extrude_DirectionDescription_NamesTheCutInversionAndTheMeasuredSideField()
    {
        var direction = (JsonObject)((JsonObject)Tool("kompas_extrude").InputSchema["properties"]!)["direction"]!;
        var text = direction["description"]!.GetValue<string>();

        Assert.Contains("противоположно нормали", text, StringComparison.Ordinal);
        Assert.Contains("material_removed_toward", text, StringComparison.Ordinal);
        Assert.Contains("material_added_toward", text, StringComparison.Ordinal);
    }

    /// <summary>The <c>detail=full</c> text must name BOTH gabarit scopes, so the whole-document box is not
    /// read as the box of the solid bodies.</summary>
    [Fact]
    public void GetContext_DetailDescription_NamesBothGabaritScopes()
    {
        var detail = (JsonObject)((JsonObject)Tool("kompas_get_context").InputSchema["properties"]!)["detail"]!;
        var text = detail["description"]!.GetValue<string>();

        Assert.Contains("gabarit.full", text, StringComparison.Ordinal);
        Assert.Contains("gabarit.bodies", text, StringComparison.Ordinal);
        Assert.Contains("эскизы", text, StringComparison.Ordinal);
        Assert.Contains("list_bodies", text, StringComparison.Ordinal);
    }

    /// <summary>A locked document file gets its own code with its own wording: "not found" would send the
    /// caller looking for a missing path, and the remedy is to close the other process.</summary>
    [Fact]
    public void FileLocked_IsACodeWithItsOwnMessage()
    {
        var message = ErrorMessages.For(ErrorCodes.FileLocked);

        Assert.NotEqual("Неизвестная ошибка.", message);
        Assert.Contains("занят", message, StringComparison.Ordinal);
        Assert.Contains("диалог", message, StringComparison.Ordinal);
    }
}
