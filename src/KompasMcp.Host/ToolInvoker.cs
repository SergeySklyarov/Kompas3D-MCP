using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Geometry;
using KompasMcp.Domain.Journaling;
using KompasMcp.Domain.Queueing;
using KompasMcp.Domain.Paths;
using KompasMcp.Domain.Schema;
using KompasMcp.Domain.Verification;
using KompasMcp.Host.Catalog;
using KompasMcp.Host.Scenarios;

namespace KompasMcp.Host;

/// <summary>Turns a validated MCP tool call into exactly one Worker command.</summary>
/// <remarks>Ordered so the guarantees hold: schema validation (unknown field, NaN, negative length,
/// bad enum) — no COM yet; path policy — a refusal never touches the model; <b>durable journal
/// append</b> — before dispatch, so a crash afterwards is outcome-unknown; bounded queue → single
/// Worker dispatch; terminal journal record. The same <c>operation_id</c> with the same arguments
/// returns the recorded outcome instead of re-running, making a client retry safe for reads and honest
/// for writes.</remarks>
public sealed class ToolInvoker : IAsyncDisposable
{
    // save_path is a file destination, like output_path/target_path, and the policy must judge it as
    // strictly: MEASURED that the CORE does not check the path — on forbidden characters it wrote a
    // truncated empty file, and a missing directory it created itself; the only defence is a HOST
    // refusal before COM. source_path (order C1) is a READ, like input_path — named because the path
    // policy judges a field by its NAME. History: docs/decisions/host.md#path-fields
    private static readonly string[] PathFields = { "path", "output_path", "target_path", "input_path", "save_path", "source_path" };

    /// <summary>Result size a status answer carries whole. Beyond it the answer holds a bounded excerpt
    /// and says so: <c>result_truncated</c> plus the original <c>result_size_bytes</c>.
    /// LIMIT: the excerpt keeps 5 array items and 300 characters per string (ScenarioRunner.Compact),
    /// so a client compares a long result by its common part, not by equality.</summary>
    private const int StatusResultLimit = 8_000;

    private readonly HostOptions _options;
    private readonly WorkerSupervisor _worker;
    private readonly OperationJournal _journal;
    private readonly CadCommandQueue _queue;
    private readonly PathPolicy _pathPolicy;
    private readonly HostLog _log;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Whether the command of an admitted operation has already left the Host for the Worker.
    /// The difference IS the answer: <c>queued</c> means it still waits its turn, <c>running</c> means it
    /// was handed over. INVARIANT: the flag flips under <see cref="_cadGate"/>, which admits one CAD
    /// command at a time, so "handed over" and "executing" cannot overlap with a second mutation.</summary>
    /// <remarks>LIMIT: a cold Worker start happens inside the send, after the flip, and is therefore
    /// reported as <c>running</c>; the Host has no observable boundary between the two.</remarks>
    private readonly ConcurrentDictionary<string, bool> _dispatched = new(StringComparer.Ordinal);

    /// <summary>Operations whose cancellation was requested while they were already executing. COM is
    /// never interrupted (spec 1.8), so the flag only names the request.</summary>
    private readonly ConcurrentDictionary<string, bool> _cancelRequested = new(StringComparer.Ordinal);

    /// <summary>Admits one CAD command at a time. KOMPAS executes on a single apartment, so parallel
    /// commands would be serialised by COM anyway — with no way for the Host to tell which one waits.
    /// With the gate the waiting command is visibly <c>queued</c> (spec 1.8).</summary>
    private readonly SemaphoreSlim _cadGate = new(1, 1);

    private readonly JsonNode _schemaRoot;

    public ToolInvoker(HostOptions options, WorkerSupervisor worker, OperationJournal journal, HostLog log)
    {
        _options = options;
        _worker = worker;
        _journal = journal;
        _log = log;
        _queue = new CadCommandQueue(options.QueueCapacity);
        _pathPolicy = new PathPolicy(options.ReadOnlyRoots, options.WritableRoots.Concat(options.ExportRoots));

        // One root so $ref inside a tool schema resolves the same way as in the published schema.
        _schemaRoot = new JsonObject
        {
            ["$defs"] = (JsonNode)ToolCatalog.SharedDefinitions.DeepClone(),
        };
    }

    public IReadOnlyList<ToolDefinition> Tools => ToolCatalog.All;

    /// <summary>Public entry point. Every contract error is converted into an envelope here, including
    /// ones raised inside the journal or queue: an escaping <see cref="KompasContractException"/>
    /// would be reported as an unclassified VERIFICATION_FAILED, and OPERATION_ID_CONFLICT in
    /// particular must never lose its code.</summary>
    public Task<ResultEnvelope<JsonNode?>> InvokeAsync(
        string toolName, JsonObject arguments, CancellationToken cancellationToken) =>
        InvokeAsync(toolName, arguments, null, cancellationToken);

    /// <summary>The same entry, with ONE file outside the client's roots admitted for this call.</summary>
    /// <remarks>INVARIANT: <paramref name="servicePath"/> comes from the Host's own scenario code, never
    /// from an argument — no tool field, no <c>kompas_run</c> wrapper and no step can set it, so the client
    /// gains no way past the file policy. The grant covers that one file only and is not inherited by
    /// anything the call invokes. History: docs/decisions/scenario.md#checkpoint-service-path</remarks>
    internal async Task<ResultEnvelope<JsonNode?>> InvokeAsync(
        string toolName, JsonObject arguments, string? servicePath, CancellationToken cancellationToken)
    {
        try
        {
            return await InvokeCoreAsync(toolName, arguments, servicePath, cancellationToken).ConfigureAwait(false);
        }
        catch (KompasContractException contract)
        {
            _log.Write("warn", "contract refusal", new { tool = toolName, code = contract.Code, message = contract.Message });
            return new ResultEnvelope<JsonNode?>
            {
                Status = contract.Code == ErrorCodes.OutcomeUnknown ? OperationStatus.OutcomeUnknown : OperationStatus.Failed,
                OperationId = ReadString(arguments, "operation_id"),
                ApplicationId = ReadString(arguments, "application_id"),
                DocumentId = ReadString(arguments, "document_id"),
                RevisionBefore = ReadLong(arguments, "expected_revision"),
                Verification = new VerificationDto(VerificationLevel.None, Array.Empty<NamedCheck>(), new[] { "effect_on_model_unknown" }),
                Error = contract.ToErrorDto(),
            };
        }
    }

    private async Task<ResultEnvelope<JsonNode?>> InvokeCoreAsync(
        string toolName, JsonObject arguments, string? servicePath, CancellationToken cancellationToken)
    {
        var tool = ToolCatalog.All.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
        {
            return UnknownTool(toolName);
        }

        var operationId = ReadString(arguments, "operation_id");

        var violations = Validate(tool, arguments);
        if (violations.Count > 0)
        {
            return new ResultEnvelope<JsonNode?>
            {
                Status = OperationStatus.Failed,
                OperationId = operationId,
                Verification = new VerificationDto(VerificationLevel.ArgumentValidated, new[] { new NamedCheck("schema", false) }, Array.Empty<string>()),
                Error = new ErrorDto(
                    ErrorCodes.InvalidArgument,
                    $"Аргументы не прошли контракт: {violations[0].Message}",
                    RetryPolicy.Never,
                    null,
                    false,
                    new JsonObject
                    {
                        ["violations"] = new JsonArray(violations
                            .Select(v => (JsonNode)new JsonObject { ["path"] = v.Path, ["keyword"] = v.Keyword, ["message"] = v.Message })
                            .ToArray()),
                    }),
            };
        }

        var pathFailure = CheckPaths(tool, arguments, servicePath);
        if (pathFailure is not null)
        {
            return new ResultEnvelope<JsonNode?>
            {
                Status = OperationStatus.Failed,
                OperationId = operationId,
                DocumentId = ReadString(arguments, "document_id"),
                Verification = new VerificationDto(VerificationLevel.ArgumentValidated, new[] { new NamedCheck("path_policy", false) }, Array.Empty<string>()),
                Error = pathFailure,
            };
        }

        // A scenario is a plan of ORDINARY calls: it re-enters this method per step, so a step gets the
        // same schema check, path policy, journal and queue as a direct call. The command never reaches
        // the Worker. History: docs/decisions/scenario.md#scenario-run
        if (string.Equals(tool.WorkerCommand, WorkerCommands.Scenario, StringComparison.Ordinal))
        {
            return await new ScenarioRunner(
                    _options.ControlCopyDirectory,
                    (name, stepArguments, grant, token) => InvokeAsync(name, stepArguments, grant, token))
                .RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        return tool.IsMutation
            ? await RunMutationAsync(tool, arguments, operationId!, cancellationToken).ConfigureAwait(false)
            : await RunReadAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
    }

    // ---- Validation ----

    private List<SchemaViolation> Validate(ToolDefinition tool, JsonObject arguments)
    {
        FoldMaterialTowardMinus(arguments);
        var violations = new List<SchemaViolation>(JsonSchemaValidator.Validate(tool.InputSchema, arguments));

        if (ReadString(arguments, "operation_id") is { Length: > 0 } operationIdValue && !Guid.TryParse(operationIdValue, out _))
        {
            violations.Add(new SchemaViolation("/operation_id", "format", "operation_id должен быть UUID."));
        }

        violations.AddRange(ModeViolations(tool, arguments));
        return violations;
    }

    /// <summary>Folds the minus sign of <c>material_toward</c> to the published ASCII hyphen BEFORE the schema
    /// enum is applied.</summary>
    /// <remarks>WHY. The answer spells a side with U+2212 («−z») while the published enum lists the ASCII
    /// form, so a client copying the side out of the answer would be refused by our own vocabulary. The two
    /// spellings are ONE request, and the folded form is what the journal records, so a retry with either
    /// spelling replays the same operation instead of conflicting.
    /// INVARIANT: the fold runs on EVERY path that validates arguments, including <c>kompas_run</c>, whose
    /// arguments arrive nested under <c>args</c> and are validated before the re-entry.
    /// History: docs/decisions/host.md#material-toward-input</remarks>
    internal static void FoldMaterialTowardMinus(JsonObject arguments)
    {
        if (arguments["material_toward"] is JsonValue value
            && value.TryGetValue<string>(out var word)
            && word.Contains('\u2212', StringComparison.Ordinal))
        {
            arguments["material_toward"] = word.Replace('\u2212', '-');
        }
    }

    /// <summary>Mode-dependent field rules. The published validator understands anyOf/oneOf but not
    /// if/then/allOf, so a combination cannot be expressed in the schema; it is checked here, before
    /// COM. Spec 4.1 (docs/05) demands exactly this: required, optional and forbidden fields per
    /// mode, unsupported combinations rejected rather than normalised away.</summary>
    private static IEnumerable<SchemaViolation> ModeViolations(ToolDefinition tool, JsonObject arguments)
    {
        if (tool.Name == "kompas_edit_sketch")
        {
            var mode = ReadString(arguments, "mode") ?? "append";
            var empty = arguments["entities"] is not JsonArray list || list.Count == 0;
            if (empty && mode != "delete_entities")
            {
                yield return new SchemaViolation(
                    "/entities", "mode",
                    $"При mode={mode} нужен хотя бы один примитив: пустой список имеет смысл только у delete_entities.");
            }

            yield break;
        }

        if (tool.Name == "kompas_chamfer")
        {
            // The "field ↔ mode" rules live here, not in the schema: the validator would publish an
            // if/then that is silently ignored elsewhere (see the comment above).
            var mode = ReadString(arguments, "mode") ?? "two_distances";
            var hasDistance2 = arguments.TryGetPropertyValue("distance2_mm", out var d2) && d2 is not null;
            var hasAngle = arguments.TryGetPropertyValue("angle_deg", out var angle) && angle is not null;
            switch (mode)
            {
                case "two_distances" when !hasDistance2:
                    yield return new SchemaViolation(
                        "/distance2_mm", "mode",
                        "При mode=two_distances нужен distance2_mm; для фаски под 45° задайте его равным distance1_mm.");
                    break;
                case "two_distances" when hasAngle:
                    yield return new SchemaViolation(
                        "/angle_deg", "mode",
                        "При mode=two_distances поле angle_deg запрещено: угол — способ distance_angle.");
                    break;
                case "distance_angle" when !hasAngle:
                    yield return new SchemaViolation(
                        "/angle_deg", "mode",
                        "При mode=distance_angle нужен angle_deg (градусы, строго между 0 и 90).");
                    break;
                case "distance_angle" when hasDistance2:
                    // The second leg cannot be expressed in this mode: accepting it and staying
                    // silent would pass off as applied a parameter that was requested and not received.
                    yield return new SchemaViolation(
                        "/distance2_mm", "mode",
                        "При mode=distance_angle поле distance2_mm запрещено: сторону задаёт angle_deg.");
                    break;
                case not ("two_distances" or "distance_angle"):
                    yield return new SchemaViolation(
                        "/mode", "enum", $"Неизвестный способ фаски: {mode}.");
                    break;
            }

            yield break;
        }

        if (tool.Name == "kompas_hole")
        {
            // The "field ↔ mode" rules live here, not in the schema (see above). The cost of an error
            // is asymmetric: a spurious refusal is visible at once, while an accepted and ignored
            // number survives to acceptance, because mode parameters of different modes live in
            // DIFFERENT API7 interfaces (ISpotfacingHoleParameters vs ICountersinkHoleParameters).
            var mode = ReadString(arguments, "mode") ?? "blind_flat";
            var holeHasDepth = Present(arguments, "depth_mm");
            var hasBoreDiameter = Present(arguments, "counterbore_diameter_mm");
            var hasBoreDepth = Present(arguments, "counterbore_depth_mm");
            var hasMouth = Present(arguments, "countersink_diameter_mm");
            var hasAngle = Present(arguments, "countersink_angle_deg");
            var hasConicAngle = Present(arguments, "conic_angle_deg");
            var hasOffsetX = Present(arguments, "offset_x_mm");
            var hasOffsetY = Present(arguments, "offset_y_mm");

            if (hasOffsetX != hasOffsetY)
            {
                yield return new SchemaViolation(
                    hasOffsetX ? "/offset_y_mm" : "/offset_x_mm", "mode",
                    "Смещение задаётся парой offset_x_mm и offset_y_mm: одна координата без второй " +
                    "оставляет другую на догадку сервера, а в ответе оказалась бы не та позиция.");
            }

            switch (mode)
            {
                case "blind_flat" when !holeHasDepth:
                    yield return new SchemaViolation(
                        "/depth_mm", "mode", "При mode=blind_flat глубина depth_mm обязательна.");
                    break;
                case "blind_flat" when hasBoreDiameter || hasBoreDepth || hasMouth || hasAngle:
                    yield return new SchemaViolation(
                        "/mode", "mode",
                        "При mode=blind_flat поля выточки и зенковки запрещены: они принадлежат " +
                        "другим режимам и живут в других интерфейсах параметров.");
                    break;
                case "blind_flat" when hasConicAngle:
                    yield return new SchemaViolation(
                        "/conic_angle_deg", "mode",
                        "При mode=blind_flat поле conic_angle_deg запрещено: угол конуса принадлежит " +
                        "режиму through_conic.");
                    break;
                case "blind_sphere" when !holeHasDepth:
                    yield return new SchemaViolation(
                        "/depth_mm", "mode",
                        "При mode=blind_sphere глубина depth_mm обязательна: сферическое дно " +
                        "отсчитывается от конца цилиндра той же глубины.");
                    break;
                case "blind_sphere" when hasBoreDiameter || hasBoreDepth || hasMouth || hasAngle
                    || hasConicAngle:
                    yield return new SchemaViolation(
                        "/mode", "mode",
                        "При mode=blind_sphere поля выточки, зенковки и конуса запрещены: они " +
                        "принадлежат другим режимам и живут в других интерфейсах параметров.");
                    break;
                case "through_conic" when holeHasDepth:
                    yield return new SchemaViolation(
                        "/depth_mm", "mode",
                        "При mode=through_conic поле depth_mm запрещено: режим сквозной, а конус " +
                        "задаётся углом при вершине.");
                    break;
                case "through_conic" when !hasConicAngle:
                    yield return new SchemaViolation(
                        "/conic_angle_deg", "mode",
                        "При mode=through_conic нужен conic_angle_deg: без угла конус неотличим от " +
                        "сквозного цилиндра.");
                    break;
                case "through_conic" when hasBoreDiameter || hasBoreDepth || hasMouth || hasAngle:
                    yield return new SchemaViolation(
                        "/mode", "mode",
                        "При mode=through_conic поля выточки и зенковки запрещены: это другие режимы " +
                        "с другими интерфейсами параметров.");
                    break;
                case "through_counterbore" when holeHasDepth:
                    // Accepting a number and staying silent would pass off as applied a depth that the
                    // through mode does not read.
                    yield return new SchemaViolation(
                        "/depth_mm", "mode",
                        "При mode=through_counterbore поле depth_mm запрещено: режим сквозной и " +
                        "числа глубины не принимает (глубину задаёт counterbore_depth_mm).");
                    break;
                case "through_counterbore" when !hasBoreDiameter || !hasBoreDepth:
                    yield return new SchemaViolation(
                        hasBoreDiameter ? "/counterbore_depth_mm" : "/counterbore_diameter_mm", "mode",
                        "При mode=through_counterbore нужны и counterbore_diameter_mm, и " +
                        "counterbore_depth_mm: без них режим отличается от сквозного отверстия " +
                        "только именем.");
                    break;
                case "through_counterbore" when hasMouth || hasAngle:
                    yield return new SchemaViolation(
                        "/mode", "mode",
                        "При mode=through_counterbore поля зенковки запрещены: это другой режим " +
                        "с другим интерфейсом параметров.");
                    break;
                case "through_countersink" when holeHasDepth:
                    yield return new SchemaViolation(
                        "/depth_mm", "mode",
                        "При mode=through_countersink поле depth_mm запрещено: режим сквозной, а " +
                        "глубина зенковки производна и записана быть не может — объект возвращает " +
                        "(rM − rP)/tan(угол/2) (измерено M.3 и N.2).");
                    break;
                case "through_countersink" when !hasMouth || !hasAngle:
                    yield return new SchemaViolation(
                        hasMouth ? "/countersink_angle_deg" : "/countersink_diameter_mm", "mode",
                        "При mode=through_countersink нужны и countersink_diameter_mm (диаметр " +
                        "устья), и countersink_angle_deg.");
                    break;
                case "through_countersink" when hasBoreDiameter || hasBoreDepth:
                    yield return new SchemaViolation(
                        "/mode", "mode",
                        "При mode=through_countersink поля выточки запрещены: это другой режим с " +
                        "другим интерфейсом параметров.");
                    break;
                case "through_counterdrill" when holeHasDepth:
                    yield return new SchemaViolation(
                        "/depth_mm", "mode",
                        "При mode=through_counterdrill поле depth_mm запрещено: режим сквозной, а " +
                        "глубина выточки задаётся counterbore_depth_mm.");
                    break;
                case "through_counterdrill" when !hasBoreDiameter || !hasBoreDepth || !hasAngle:
                    yield return new SchemaViolation(
                        !hasBoreDiameter ? "/counterbore_diameter_mm"
                            : !hasBoreDepth ? "/counterbore_depth_mm" : "/countersink_angle_deg",
                        "mode",
                        "При mode=through_counterdrill нужны counterbore_diameter_mm (диаметр " +
                        "выточки), counterbore_depth_mm и countersink_angle_deg: без любого из трёх " +
                        "режим отличается от сквозного отверстия только именем.");
                    break;
                case "through_counterdrill" when hasMouth:
                    yield return new SchemaViolation(
                        "/countersink_diameter_mm", "mode",
                        "При mode=through_counterdrill countersink_diameter_mm запрещён: у режима ОДИН " +
                        "диаметр — counterbore_diameter_mm, он же диаметр зенковки.");
                    break;
                case not ("blind_flat" or "blind_sphere" or "through_counterbore" or "through_countersink"
                    or "through_conic" or "through_counterdrill"):
                    yield return new SchemaViolation(
                        "/mode", "enum", $"Неизвестный режим отверстия: {mode}.");
                    break;
            }

            yield break;
        }

        if (tool.Name == "kompas_update_feature")
        {
            // The server learns the feature's family only from COM, so only what is visible from the
            // arguments is checked here: extrude and chamfer fields in one call contradict each other,
            // and silently applying half would be worse than a refusal.
            var chamferFields = new[] { "distance1_mm", "distance2_mm", "angle_deg", "direction" }
                .Where(f => arguments.TryGetPropertyValue(f, out var v) && v is not null)
                .ToList();
            var extrudeFields = new[] { "depth_mm", "end_condition", "sketch_ref" }
                .Where(f => arguments.TryGetPropertyValue(f, out var v) && v is not null)
                .ToList();
            if (chamferFields.Count > 0 && extrudeFields.Count > 0)
            {
                yield return new SchemaViolation(
                    "/" + chamferFields[0], "mode",
                    "Параметры фаски (" + string.Join(", ", chamferFields) + ") и параметры выдавливания ("
                    + string.Join(", ", extrudeFields) + ") в одном вызове противоречат друг другу: "
                    + "семейство признака определяет, какие поля вообще имеют смысл.");
            }

            // The angle edit is not duplicated here: that refusal belongs to the adapter
            // (CAPABILITY_UNAVAILABLE, route never measured), while the "field ↔ mode" rule above is
            // the contract layer. Two checks of one case with different codes would give acceptance
            // two answers to one request.
            yield break;
        }

        // A mate parameter is bound to its type. Accepting a number where the mate type has no
        // parameter (coincidence, parallel, perpendicular, tangency, concentric) is the same defect
        // as an accepted-and-ignored through-hole depth: a recorded value does not mean an applied
        // one. For distance and angle the parameter is mandatory.
        // History: docs/decisions/host.md#mate-param
        if (tool.Name == "kompas_create_mate")
        {
            var constraint = ReadString(arguments, "constraint_type");
            var hasParam = Present(arguments, "param_value");
            var parametric = constraint is "distance" or "angle";
            if (parametric && !hasParam)
            {
                yield return new SchemaViolation(
                    "/param_value", "mode",
                    $"При constraint_type={constraint} параметр param_value обязателен: это расстояние " +
                    "или угол, и без него сопряжение не отличается от беспараметрического типа.");
            }
            else if (!parametric && hasParam)
            {
                yield return new SchemaViolation(
                    "/param_value", "mode",
                    $"При constraint_type={constraint} поле param_value запрещено: у этого типа сопряжения " +
                    "нет параметра, и принятое число было бы записано и проигнорировано.");
            }

            // THE ALIGNMENT IS NOT JUDGED HERE. A map "mate type → allowed values" used to refuse
            // combinations before the mate existed; it was measured on the NUMBER read back, not on
            // the geometry, and it refused `coincidence + opposite` although the faces land opposite.
            // The single judge is the measured face-normal dot in the adapter, after the solve.
            // History: docs/decisions/mates.md#alignment-geometry-criterion

            // A transmission field on another type is the same defect as a parameter where the type
            // has none: the number would be accepted and never applied. And a transmission without its
            // ratio has nothing to write at all.
            // History: docs/decisions/host.md#mate-transmission
            var transmissionFields = new[]
                { "motion1", "motion2", "scale1", "scale2", "direction1", "direction2" }
                .Where(f => Present(arguments, f))
                .ToList();
            if (constraint != "transmission" && transmissionFields.Count > 0)
            {
                yield return new SchemaViolation(
                    "/" + transmissionFields[0], "mode",
                    "Поля " + string.Join(", ", transmissionFields) + " относятся только к "
                    + "constraint_type=transmission: у этого вида сопряжения их нет, и принятое "
                    + "значение было бы записано и проигнорировано.");
            }

            if (constraint == "transmission")
            {
                foreach (var required in new[] { "motion1", "motion2", "scale1", "scale2" })
                {
                    if (!Present(arguments, required))
                    {
                        yield return new SchemaViolation(
                            "/" + required, "mode",
                            "При constraint_type=transmission поле " + required + " обязательно: "
                            + "передача задаётся движением обеих сторон и коэффициентом.");
                    }
                }

                if (Present(arguments, "alignment") || Present(arguments, "param_value"))
                {
                    yield return new SchemaViolation(
                        "/alignment", "mode",
                        "У transmission нет ни выравнивания, ни параметра: направления задаются "
                        + "direction1/direction2, соотношение — scale1/scale2.");
                }
            }

            yield break;
        }

        if (tool.Name != "kompas_extrude")
        {
            yield break;
        }

        var endCondition = ReadString(arguments, "end_condition") ?? "blind";
        var hasDepth = arguments.TryGetPropertyValue("depth_mm", out var depth) && depth is not null;
        var operation = ReadString(arguments, "operation");
        var hasTarget = arguments.TryGetPropertyValue("target_body_ref", out var targetBody) && targetBody is not null;
        var hasDirection = Present(arguments, "direction");
        var hasToward = Present(arguments, "material_toward");

        // The side is given by EXACTLY ONE of the two fields. Accepting "both" would silently pick one and
        // pass off as honoured a request that was contradicted; accepting "neither" would leave the side to a
        // guess, which is the very defect material_toward exists to remove.
        // History: docs/decisions/host.md#material-toward-input
        if (hasDirection == hasToward)
        {
            yield return new SchemaViolation(
                hasDirection ? "/material_toward" : "/direction", "mode",
                "Сторону задаёт РОВНО ОДНО из полей direction (слово КОМПАС positive/negative/symmetric) и "
                + "material_toward (+x…-z в координатах детали): "
                + (hasDirection
                    ? "заданы оба — сервер не выбирает между ними."
                    : "не задано ни одно — сторона осталась бы на догадку."));
        }

        // A through cut is measured to work only with direction=symmetric, and material_toward names ONE side.
        if (hasToward && endCondition == "through")
        {
            yield return new SchemaViolation(
                "/material_toward", "mode",
                "При end_condition=through поле material_toward запрещено: насквозь работает только при "
                + "direction=symmetric, а material_toward выражает одну сторону.");
        }

        // docs/02 makes target_body_ref a field of boss and cut only. `base` creates the first body,
        // so there is nothing to aim at; letting the pair through would report a honoured target that
        // was never requested from KOMPAS.
        if (TargetBodyGuard.TargetBodyRefusedForOperation(operation, hasTarget))
        {
            yield return new SchemaViolation(
                "/target_body_ref", "mode",
                $"При operation={operation} поле target_body_ref запрещено: базовое выдавливание создаёт первое тело, " +
                "а приклеивание и вырезание обязаны указывать, к какому телу применяться.");
        }

        switch (endCondition)
        {
            case "blind" when !hasDepth:
                yield return new SchemaViolation(
                    "/depth_mm", "mode", "При end_condition=blind глубина depth_mm обязательна.");
                break;
            case "through" when hasDepth:
                // Accepting a number that KOMPAS then ignores would let a caller believe a depth
                // was applied. Probe P2.1: depth 1 mm and 1000 mm cut identically through-all.
                yield return new SchemaViolation(
                    "/depth_mm", "mode", "При end_condition=through поле depth_mm запрещено: режим насквозь числа не принимает.");
                break;
            case "through" when operation != "cut":
                yield return new SchemaViolation(
                    "/end_condition", "mode", "Сквозной режим измерен и разрешён только для operation=cut.");
                break;
            case "through" when (ReadString(arguments, "direction") ?? "symmetric") != "symmetric":
                yield return new SchemaViolation(
                    "/direction", "mode", "Насквозь работает только при direction=symmetric: на directionType=0 вырезание не происходит вовсе, на 1 любое условие конца даёт один и тот же результат.");
                break;
        }
    }

    private ErrorDto? CheckPaths(ToolDefinition tool, JsonObject arguments, string? servicePath)
    {
        // `path` on an EDIT open is an intent to WRITE to that file: a document opened with
        // access=edit is later saved by kompas_save_document without target_path, i.e. back to the
        // source file. Judging it as a read would let a write into a "read-only" root slip through.
        // History: docs/decisions/host.md#path-write
        var pathIsWrite = tool.Name == "kompas_open_document"
                          && string.Equals(ReadString(arguments, "access"), "edit", StringComparison.Ordinal);

        foreach (var field in PathFields)
        {
            if (ReadString(arguments, field) is not { Length: > 0 } value)
            {
                continue;
            }

            // INVARIANT: a relative path is REFUSED, not allowed. The decision is made via
            // `Path.GetFullPath` from the HOST's working directory, but the command runs in the Worker
            // (install folder) or the KOMPAS process: the same relative path is judged against one
            // folder and read/written in another. The schema demands an absolute path — now checked,
            // not implied. History: docs/decisions/host.md#relative-path
            if (!Path.IsPathFullyQualified(value))
            {
                return new ErrorDto(
                    ErrorCodes.PathNotAllowed,
                    $"Путь '{value}' в поле '{field}' относительный: сервер принимает только абсолютные " +
                    "пути. Относительный путь судился бы политикой по рабочему каталогу Хоста, а " +
                    "исполнялся бы по рабочему каталогу Worker или процесса КОМПАС.",
                    RetryPolicy.Never,
                    null,
                    false,
                    new JsonObject
                    {
                        ["field"] = field,
                        ["reason"] = "path_not_fully_qualified",
                    });
            }

            // Only an output destination is a write; a source path is read-only intent.
            var writes = field is "output_path" or "target_path" or "save_path"
                         || (field is "path" && pathIsWrite);
            var decision = _pathPolicy.Evaluate(value, intendToWrite: writes);
            if (decision.Access != PathAccess.Denied || IsServicePath(value, servicePath))
            {
                continue;
            }

            return new ErrorDto(
                ErrorCodes.PathNotAllowed,
                $"Путь '{value}' отклонён политикой файлов: {decision.DeniedReason}",
                RetryPolicy.Never,
                null,
                false,
                new JsonObject
                {
                    ["field"] = field,
                    ["read_only_roots"] = new JsonArray(_options.ReadOnlyRoots.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                    ["writable_roots"] = new JsonArray(_options.WritableRoots.Concat(_options.ExportRoots).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                });
        }

        return null;
    }

    /// <summary>Whether the candidate is the one FILE a caller's service grant names.</summary>
    /// <remarks>INVARIANT: the grant names a file, so it is compared as a canonical path for EQUALITY only:
    /// a neighbour in the same folder, a sibling "…-r10" of a granted "…-r1" and a file inside a granted
    /// folder all stay denied. A grant that does not canonicalise admits nothing.
    /// History: docs/decisions/scenario.md#checkpoint-service-path</remarks>
    internal static bool IsServicePath(string candidate, string? servicePath)
    {
        if (string.IsNullOrWhiteSpace(servicePath))
        {
            return false;
        }

        try
        {
            var grant = Path.TrimEndingDirectorySeparator(Path.GetFullPath(servicePath));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return string.Equals(full, grant, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // ---- Execution ----

    /// <summary>The Host's answer-wait budget, from the shared rule in <see cref="CommandBudgets"/>: the
    /// setting may LENGTHEN the wait but cannot make the Host faster than the Worker — else the Host
    /// would be first to declare "outcome unknown" on a command that is merely slow.</summary>
    private int BudgetFor(ToolDefinition tool) =>
        CommandBudgets.HostWaitMs(tool.WorkerCommand, _options.OperationBudgetMs);

    private async Task<ResultEnvelope<JsonNode?>> RunReadAsync(ToolDefinition tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        // INVARIANT: the Host budget is PER COMMAND, not one setting for everything. A single setting
        // (operation_budget_ms, 120 s default) against Worker budgets of 180–300 s made the HOST give
        // up before the Worker and kill a Worker still working. The budget now comes from the shared
        // table and cannot be shorter than the Worker's; the operator setting may LENGTHEN it, not
        // shorten it. History: docs/decisions/host.md#budget-per-command
        var budgetMs = BudgetFor(tool);
        var timeout = TimeSpan.FromMilliseconds(Math.Min(ReadInt(arguments, "timeout_ms") ?? budgetMs, budgetMs));
        try
        {
            var frame = await _worker.SendAsync(tool.WorkerCommand, arguments.DeepClone(), timeout, isMutation: false, cancellationToken).ConfigureAwait(false);

            // Diagnostic for the empty-list defect: what arrived over the pipe, before the
            // envelope touches it. Distinguishes "Worker sent nothing" from "Host lost it".
            _log.Write("debug", "read payload", new
            {
                tool = tool.Name,
                payload_kind = frame.Payload switch
                {
                    null => "null",
                    JsonArray => "array",
                    JsonObject => "object",
                    _ => "value",
                },
                payload_length = frame.Payload?.ToJsonString()?.Length,
                payload_head = frame.Payload?.ToJsonString() is { Length: > 0 } head ? head[..Math.Min(200, head.Length)] : null,
            });

            return frame.Error is null
                ? Envelope(OperationStatus.Succeeded, tool, ReadString(arguments, "operation_id"), frame.Payload, arguments)
                : Envelope(OperationStatus.Failed, tool, ReadString(arguments, "operation_id"), null, arguments, frame.Error);
        }
        catch (KompasContractException contract)
        {
            return Envelope(OperationStatus.Failed, tool, ReadString(arguments, "operation_id"), null, arguments, contract.ToErrorDto());
        }
    }

    private async Task<ResultEnvelope<JsonNode?>> RunMutationAsync(ToolDefinition tool, JsonObject arguments, string operationId, CancellationToken cancellationToken)
    {
        // INVARIANT: a mutation of a document whose earlier operation still needs reconciliation is
        // refused BEFORE the journal and BEFORE the Worker. Its effect on the model is unknown, so the
        // next command would be applied on top of an unknown state — the double application the
        // `after_reconciliation` policy exists to prevent. Reads and other documents are unaffected.
        // A retry of the SAME operation_id is not a new command: the journal replays its recorded
        // outcome, so the refusal must not swallow that path.
        if (ReadString(arguments, "document_id") is { Length: > 0 } targetDocument
            && !_journal.TryGet(operationId, out _)
            && _journal.NeedsReconciliationFor(targetDocument) is { } blocking
            && !tool.PassesReconciliationBan(ReadString(arguments, "dirty_policy")))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNeedsReconciliation,
                $"У документа {targetDocument} операция {blocking.OperationId} ({blocking.Tool}) имеет "
                + "неопределённый исход: её эффект неизвестен, и новая мутация того же документа "
                + "применилась бы поверх неизвестного состояния. Вызов отклонён до журнала и до Worker. "
                + "Выход: kompas_run с tool=kompas_operation_reconcile и этим operation_id, либо "
                + "kompas_close_document с dirty_policy=discard (документ закрывается без сохранения, "
                + "несохранённые изменения теряются) и повторное открытие - новый document_id снимает "
                + "запрет. dirty_policy=save и kompas_save_document запрещены: это запись неизвестного "
                + "состояния в файл. Чтение и мутации других документов не блокируются.",
                RetryPolicy.AfterReconciliation,
                partialEffects: false,
                hresult: null,
                details: new Dictionary<string, object?>
                {
                    ["operation_id"] = blocking.OperationId,
                    ["document_id"] = targetDocument,
                    ["tool"] = blocking.Tool,
                });
        }

        var decision = _journal.TryBegin(operationId, tool.Name, Canonical(tool, arguments), ReadString(arguments, "document_id"), ReadLong(arguments, "expected_revision"));

        if (!decision.Proceed && decision.Existing is { } existing)
        {
            return Replayed(tool, existing, arguments);
        }

        _dispatched[operationId] = false;
        var execution = ExecuteAsync(tool, arguments, operationId, cancellationToken);
        if (!_inFlight.TryAdd(operationId, execution))
        {
            // The journal said "never saw this operation_id", yet a task with it exists. The command
            // is not sent a second time: we await the existing one. The replay defence itself lives in
            // the journal (OperationJournal.TryBegin), atomic across processes; this is a second line.
            await execution.ConfigureAwait(false);
        }

        // Return synchronously while the operation fits the budget; beyond it, hand back an
        // operation id to poll and let the task keep running (spec 1.8).
        var completed = await Task.WhenAny(execution, Task.Delay(_options.SyncBudgetMs, CancellationToken.None)).ConfigureAwait(false);
        if (completed == execution)
        {
            Forget(operationId);
            return await execution.ConfigureAwait(false);
        }

        _ = execution.ContinueWith(t =>
        {
            Forget(operationId);
            _ = t;
        }, TaskScheduler.Default);

        // A command still waiting its turn is `queued`, not `running`: the two answer different
        // questions (did it reach KOMPAS or not), and only the first allows a confirmed cancellation.
        var phase = _dispatched.TryGetValue(operationId, out var dispatched) && dispatched
            ? OperationStatus.Running
            : OperationStatus.Queued;

        return Envelope(phase, tool, operationId, null, arguments,
            warnings: new[]
            {
                // Polling is a repeat of the SAME call with the SAME operation_id: the journal replays
                // the recorded outcome (see Replayed), and an unfinished operation answers queued or
                // running. History: docs/decisions/host.md#running-poll
                $"Операция не завершилась за {_options.SyncBudgetMs / 1000} с: она "
                + (phase == OperationStatus.Queued
                    ? "стоит в очереди Хоста и ещё не передана в Worker (queued)"
                    : "выполняется в Worker (running)")
                + $". Состояние даёт kompas_run с tool=kompas_operation_status и operation_id {operationId} "
                + "(в профиле compact этого имени нет в tools/list); повтор того же вызова с тем же "
                + "operation_id вернёт записанный исход, когда он появится. Отмена "
                + "очередной операции подтверждается, отмена выполняющейся - только запросом: COM не "
                + "прерывается.",
            });
    }

    private async Task<ResultEnvelope<JsonNode?>> ExecuteAsync(ToolDefinition tool, JsonObject arguments, string operationId, CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queued = new QueuedCommand(operationId, tool.Name, cancellation);

        try
        {
            _queue.Enqueue(queued);
        }
        catch (KompasContractException queueFull)
        {
            // Nothing reached the Worker, so this is a clean failure and the same operation_id may
            // legitimately be sent again.
            var recorded = _journal.Fail(operationId, queueFull.ToErrorDto());
            return Envelope(OperationStatus.Failed, tool, operationId, null, arguments, queueFull.ToErrorDto(),
                warnings: recorded ? null : new[] { TerminalWriteNotDurable });
        }

        try
        {
            // INVARIANT: one CAD command at a time leaves this Host. A command waiting here is
            // `queued` (see _dispatched); the flag flips the moment the turn comes, so the answer
            // names what actually happened rather than guessing from the elapsed time.
            await _cadGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                _dispatched[operationId] = true;

                // The same shared budget as reads: the Host waits longer than the Worker (see the comment
                // in RunReadAsync), else its "giving up" is not a timeout observation but a self-refusal.
                var timeout = TimeSpan.FromMilliseconds(BudgetFor(tool));
                var frame = await _worker.SendAsync(tool.WorkerCommand, CanonicalNode(tool, arguments), timeout, isMutation: true, cancellation.Token).ConfigureAwait(false);

                if (frame.Error is null)
                {
                    var result = frame.Payload;
                    var recorded = _journal.Complete(operationId, result?.ToJsonString());
                    return Envelope(OperationStatus.Succeeded, tool, operationId, result, arguments,
                        warnings: recorded ? null : new[] { TerminalWriteNotDurable });
                }

                bool terminalRecorded;
                if (frame.Error.Code == ErrorCodes.OutcomeUnknown)
                {
                    terminalRecorded = _journal.MarkUnknown(operationId, frame.Error.Message);
                }
                else
                {
                    terminalRecorded = _journal.Fail(operationId, frame.Error);
                }

                // INVARIANT: a failed terminal write after a completed mutation is not a normal refusal.
                // The outcome is known only to this process while the durable line says `in_flight`, so the
                // error must not carry the "retry as is" policy — a retry after restart would apply the
                // mutation twice. The policy becomes "after reconciliation", named in a warning.
                // History: docs/decisions/host.md#terminal-write
                var error = terminalRecorded ? frame.Error : ForceReconciliation(frame.Error);
                return Envelope(frame.Error.Code == ErrorCodes.OutcomeUnknown ? OperationStatus.OutcomeUnknown : OperationStatus.Failed,
                    tool, operationId, null, arguments, error,
                    warnings: terminalRecorded ? null : new[] { TerminalWriteNotDurable });
            }
            finally
            {
                _cadGate.Release();
            }
        }
        catch (KompasContractException contract) when (contract.Code == ErrorCodes.QueueFull)
        {
            var recorded = _journal.Fail(operationId, contract.ToErrorDto());
            return Envelope(OperationStatus.Failed, tool, operationId, null, arguments, contract.ToErrorDto(),
                warnings: recorded ? null : new[] { TerminalWriteNotDurable });
        }
        catch (KompasContractException contract)
        {
            // A Worker death during a mutation is NOT an ordinary refusal. The channel broke AFTER
            // the command was sent: the Worker may be inside a COM call. A `failed` status with
            // `needs_reconciliation=false` hid this from both `CountNeedingReconciliation` and
            // `kompas_health`, although `OperationJournal.MarkUnknown` is documented for exactly this.
            // History: docs/decisions/host.md#worker-death-mutation
            if (contract.Code == ErrorCodes.OutcomeUnknown || contract.Code == ErrorCodes.ApplicationDisconnected)
            {
                var recorded = _journal.MarkUnknown(operationId, contract.Message);
                return Envelope(OperationStatus.OutcomeUnknown, tool, operationId, null, arguments,
                    contract.Code == ErrorCodes.ApplicationDisconnected ? DisconnectedAsUnknown(contract) : contract.ToErrorDto(),
                    warnings: recorded ? null : new[] { TerminalWriteNotDurable });
            }

            var failedRecorded = _journal.Fail(operationId, contract.ToErrorDto());
            return Envelope(OperationStatus.Failed, tool, operationId, null, arguments, contract.ToErrorDto(),
                warnings: failedRecorded ? null : new[] { TerminalWriteNotDurable });
        }
        catch (OperationCanceledException)
        {
            // Cancelled before it ever reached the Worker: the model was not touched, so this is a
            // confirmed cancellation rather than CANCEL_NOT_CONFIRMED.
            var recorded = _journal.Cancel(operationId);
            return Envelope(OperationStatus.Cancelled, tool, operationId, default(JsonNode?), arguments,
                warnings: recorded
                    ? new[] { "Команда отменена в очереди Host и не отправлялась в КОМПАС." }
                    : new[] { "Команда отменена в очереди Host и не отправлялась в КОМПАС.", TerminalWriteNotDurable });
        }
        finally
        {
            // The queue admits work, it does not execute it: the command goes to the Worker straight
            // after Enqueue, so the slot must be released here. Without this the bounded channel is a
            // lifetime budget of QueueCapacity mutations per process, and the 65th mutation of a long
            // session fails with QUEUE_FULL.
            _queue.Complete(operationId);
            cancellation.Dispose();
        }
    }

    private ResultEnvelope<JsonNode?> Replayed(ToolDefinition tool, JournalRecord record, JsonObject arguments)
    {
        var status = record.Outcome switch
        {
            JournalOutcome.Succeeded => OperationStatus.Succeeded,
            JournalOutcome.Failed => OperationStatus.Failed,
            JournalOutcome.Cancelled => OperationStatus.Cancelled,
            JournalOutcome.OutcomeUnknown => OperationStatus.OutcomeUnknown,
            _ => OperationStatus.Running,
        };

        // INVARIANT: the envelope does not contradict itself. A terminal write may have failed to land
        // AFTER a completed mutation: the outcome is known only to this process, the record is marked
        // as needing reconciliation, but its Outcome stays `succeeded`. A status requiring
        // reconciliation is `outcome_unknown`, not `succeeded`.
        // History: docs/decisions/host.md#replay-status
        if (status == OperationStatus.Succeeded && record.NeedsReconciliation)
        {
            status = OperationStatus.OutcomeUnknown;
        }

        var warnings = new List<string>
        {
            $"Запись журнальная: этот operation_id уже выполнялся ({record.Outcome.ToString().ToLowerInvariant()}), повторного обращения к КОМПАС не было.",
        };

        if (record.Outcome == JournalOutcome.InFlight)
        {
            // An unfinished record answers running: the operation is IN PROGRESS and the client must
            // await the same call, not a new operation_id (which would restart the mutation).
            warnings.Add(
                "Операция ещё выполняется: повторяйте ЭТОТ ЖЕ вызов с ЭТИМ ЖЕ operation_id, пока "
                + "статус остаётся running; новый operation_id начал бы мутацию заново.");
        }

        if (record.Outcome == JournalOutcome.OutcomeUnknown || status == OperationStatus.OutcomeUnknown)
        {
            warnings.Add("Исход предыдущей попытки неизвестен: сверьте модель с ожиданием (объём, число тел, история признаков), повтор мутации с НОВЫМ operation_id запрещён. Согласование даёт kompas_run с tool=kompas_operation_reconcile и этим operation_id; повтор с тем же operation_id отвечает записанным исходом, но не фактическим состоянием модели.");
        }

        JsonNode? result = null;
        if (record.ResultJson is { Length: > 0 } json)
        {
            try
            {
                result = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                // A corrupt payload must not turn a replay into a success claim.
                warnings.Add("Сохранённый результат не разбирается — возвращаем только состояние.");
            }
        }

        return Envelope(status, tool, record.OperationId, result, arguments,
            error: record.Error is null && status is OperationStatus.Failed or OperationStatus.OutcomeUnknown
                ? new ErrorDto(ErrorCodes.OutcomeUnknown, "Журнал не сохранил деталь ошибки.", RetryPolicy.AfterReconciliation, null, true, null)
                : record.Error,
            warnings: warnings);
    }

    /// <summary>Request as it will actually be sent: the declared fields plus nothing else.</summary>
    private static JsonNode CanonicalNode(ToolDefinition tool, JsonObject arguments) => arguments.DeepClone();

    /// <summary>Stable serialisation used as the idempotency key.</summary>
    private static string Canonical(ToolDefinition tool, JsonObject arguments)
    {
        var ordered = new JsonObject();
        foreach (var pair in arguments.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ordered[pair.Key] = pair.Value?.DeepClone();
        }

        return ordered.ToJsonString(KompJson.Options);
    }

    /// <summary>The warning that names a NON-DURABLE terminal journal write: the mutation is done and
    /// the outcome known to this process, but the line never landed. After a restart the journal reads
    /// `in_flight` and demands reconciliation, so a retry is not safe.</summary>
    private const string TerminalWriteNotDurable =
        "Терминальная запись журнала операций не удалась (межпроцессная блокировка не получена). " +
        "Исход команды известен только этому процессу; после перезапуска он потребует согласования, " +
        "и повтор с тем же operation_id до согласования не безопасен.";

    /// <summary>A channel break to the Worker during a mutation: APPLICATION_DISCONNECTED says "no
    /// connection", but for a MUTATION the same fact means "outcome unknown". The client decides from
    /// it whether a retry is safe, so the code and policy are translated, the cause preserved.</summary>
    private static ErrorDto DisconnectedAsUnknown(KompasContractException contract)
    {
        var error = contract.ToErrorDto();
        var details = error.Details?.DeepClone() as JsonObject ?? new JsonObject();
        details["worker_disconnected_mid_mutation"] = true;
        details["transport_code"] = ErrorCodes.ApplicationDisconnected;
        return error with
        {
            Code = ErrorCodes.OutcomeUnknown,
            RetryPolicy = RetryPolicy.AfterReconciliation,
            PartialEffects = true,
            Details = details,
        };
    }

    /// <summary>Translates the retry policy to "after reconciliation". Applied when the terminal write
    /// did not land: the client must not consider a retry safe.</summary>
    private static ErrorDto ForceReconciliation(ErrorDto error) => error with
    {
        RetryPolicy = RetryPolicy.AfterReconciliation,
        Details = MergeReconciliationFlag(error.Details),
    };

    private static JsonObject? MergeReconciliationFlag(JsonObject? details)
    {
        var merged = details?.DeepClone() as JsonObject ?? new JsonObject();
        merged["terminal_journal_write_failed"] = true;
        merged["needs_reconciliation"] = true;
        return merged;
    }

    private static ResultEnvelope<JsonNode?> Envelope(
        OperationStatus status,
        ToolDefinition tool,
        string? operationId,
        JsonNode? result,
        JsonObject arguments,
        ErrorDto? error = null,
        IReadOnlyList<string>? warnings = null)
    {
        var merged = new List<string>(warnings ?? Array.Empty<string>());

        // A Worker result may carry its own non-fatal findings in a top-level `warnings` array (e.g. two
        // independent sources naming opposite sides of a material change). They are merged into the envelope
        // so a caller reading only `warnings` cannot miss them. Absent or non-array: nothing to merge.
        if (result is JsonObject payload && payload["warnings"] is JsonArray carried)
        {
            foreach (var item in carried)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                {
                    merged.Add(text);
                }
            }
        }

        // Identity fields come from the caller first, the Worker's answer second: kompas_connect has
        // no application_id in its arguments (it invents one), and leaving the envelope null there
        // would hide the session id from the response whose job is to report it.
        var applicationId = ReadString(arguments, "application_id") ?? ReadString(result, "application_id");
        var documentId = ReadString(arguments, "document_id")
            ?? ReadString(result, "document_id")
            ?? (tool.WorkerCommand is WorkerCommands.CreateDocument or WorkerCommands.OpenDocument or WorkerCommands.ImportStep
                ? ReadString(result, "id")
                : null);

        return new ResultEnvelope<JsonNode?>
        {
            Status = status,
            OperationId = operationId,
            ApplicationId = applicationId,
            DocumentId = documentId,
            RevisionBefore = ReadLong(arguments, "expected_revision") ?? ReadLong(result, "revision_before"),
            // A FAILED mutation carries no result payload, so its revision travels inside the error's
            // details: the Worker reads the document's current revision at the refusal and puts it there.
            // Without this fallback the envelope said `revision_after: null` while the model had already
            // moved, and the client's next call failed REVISION_CONFLICT until it re-read the context.
            // History: docs/decisions/adapter-features.md#create-false-snapshot
            RevisionAfter = EnvelopeRevisions.After(result, error?.Details),
            Result = result,
            // The Worker actually observed the model or the file, so its verification level wins. The
            // Host's own value is a floor, never a ceiling: claiming "geometry_checked" here would be
            // overclaiming, and downgrading the Worker's honest answer would hide proof.
            // Three sources, strongest first: the Worker's own verification block; the reached level the
            // RESULT published on its own; the conservative floor for a payload that published neither.
            // INVARIANT: the envelope describes the RESULT, never the call the client made.
            // History: docs/decisions/host.md#verification-aggregation
            Verification = WorkerVerification(result)
                ?? ResultVerification.From(result, status)
                ?? new VerificationDto(
                status == OperationStatus.Succeeded
                    ? tool.Behaviour.ReadOnly ? VerificationLevel.ArgumentValidated : VerificationLevel.CallReturned
                    : VerificationLevel.None,
                new[] { new NamedCheck("host_validation", status != OperationStatus.Failed || error?.Code != ErrorCodes.InvalidArgument) },
                tool.Behaviour.ReadOnly
                    ? new[] { "verification_level_set_by_worker_payload" }
                    : new[] { "geometry_or_file_effect_verified_in_worker_payload" }),
            Warnings = merged,
            Error = error,
        };
    }

    private static ResultEnvelope<JsonNode?> UnknownTool(string toolName) => new()
    {
        Status = OperationStatus.Failed,
        Error = new ErrorDto(
            ErrorCodes.CapabilityUnavailable,
            $"Инструмент '{toolName}' не зарегистрирован в этой сборке. Актуальный список даёт kompas_capabilities.",
            RetryPolicy.Never,
            null,
            false,
            new JsonObject { ["available"] = new JsonArray(ToolCatalog.All.Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray()) }),
    };

    /// <summary>Read the Worker's own verification block, if it sent one. Absent or unparseable means the
    /// Host falls back to the conservative level rather than inventing one.</summary>
    private static VerificationDto? WorkerVerification(JsonNode? result)
    {
        if (result is not JsonObject obj || obj["verification"] is not JsonObject verification)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VerificationDto>(verification.ToJsonString(), KompJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads go through JsonScalars: these nodes arrived over the named pipe and are JsonElement-backed,
    // and the strict GetValue<T>/TryGetValue<T> overloads silently return nothing for that storage.
    private static string? ReadString(JsonNode? node, string key) =>
        node is JsonObject obj ? JsonScalars.ReadString(obj[key]) : null;

    /// <summary>The field is present and not null. Differs from a bare `TryGetPropertyValue` in that an
    /// empty string and zero count as present: the refusal must come from the "field forbidden in this
    /// mode" rule, not from a value that happened to be falsy for a bool.</summary>
    private static bool Present(JsonObject arguments, string key) =>
        arguments.TryGetPropertyValue(key, out var value) && value is not null;

    private static long? ReadLong(JsonNode? node, string key) =>
        node is JsonObject obj ? JsonScalars.ReadLong(obj[key]) : null;

    private static int? ReadInt(JsonNode? node, string key) =>
        node is JsonObject obj ? JsonScalars.ReadInt(obj[key]) : null;

    // ---- Operation control: status, cancel, reconcile ----

    /// <summary>The state one operation is in right now, from the durable journal plus the Host's own
    /// phase. <c>null</c> when the journal never saw this id.</summary>
    public JsonObject? OperationStatusPayload(string operationId)
    {
        if (!_journal.TryGet(operationId, out var record) || record is null)
        {
            return null;
        }

        var status = StateOf(record, _dispatched.TryGetValue(operationId, out var d1) && d1);
        var node = new JsonObject
        {
            ["operation_id"] = record.OperationId,
            ["status"] = Wire(status),
            ["tool"] = record.Tool,
            ["document_id"] = record.DocumentId,
            ["revision_before"] = record.BaseRevision,
            ["needs_reconciliation"] = record.NeedsReconciliation,
            ["cancel_requested"] = _cancelRequested.ContainsKey(operationId),
            ["started_utc"] = record.StartedUtc,
            ["finished_utc"] = record.FinishedUtc,
        };

        if (record.ResultJson is { Length: > 0 } json)
        {
            try
            {
                var parsed = JsonNode.Parse(json);
                node["result_size_bytes"] = json.Length;
                if (json.Length <= StatusResultLimit)
                {
                    node["result"] = parsed;
                    node["result_truncated"] = false;
                }
                else
                {
                    // A long operation's result does not fit the answer limit, and losing it silently
                    // would leave the client unable to compare a poll with a repeat of the same call.
                    node["result"] = ScenarioRunner.Compact(parsed, out var truncated);
                    node["result_truncated"] = true;
                    node["result_excerpt_complete"] = !truncated;
                }
            }
            catch (JsonException)
            {
                node["result_unreadable"] = true;
            }
        }

        if (record.Error is { } error)
        {
            node["error"] = JsonSerializer.SerializeToNode(error, KompJson.Options);
            node["partial_effects"] = error.PartialEffects;
        }

        return node;
    }

    /// <summary>Ask to stop an operation. A queued command is removed before it reaches the Worker, so the
    /// answer is a confirmed cancellation; an executing one only records the request, because COM is never
    /// interrupted (spec 1.8). <c>null</c> when the journal never saw this id.</summary>
    public JsonObject? CancelOperationPayload(string operationId)
    {
        if (!_journal.TryGet(operationId, out var record) || record is null)
        {
            return null;
        }

        var status = StateOf(record, _dispatched.TryGetValue(operationId, out var dispatched) && dispatched);
        var confirmed = false;
        if (status == OperationStatus.Queued)
        {
            if (_queue.TryCancelQueued(operationId))
            {
                _journal.Cancel(operationId);
                status = OperationStatus.Cancelled;
                confirmed = true;
            }
            else
            {
                // INVARIANT: the answer describes the state AFTER the attempt. The queue slot can vanish
                // between the read and the cancel (the command took its turn), and answering "queued"
                // with a refusal to cancel would name a state that no longer exists.
                _journal.TryGet(operationId, out record);
                status = record is null
                    ? OperationStatus.Queued
                    : StateOf(record, _dispatched.TryGetValue(operationId, out var nowDispatched) && nowDispatched);
                if (status == OperationStatus.Running)
                {
                    _cancelRequested[operationId] = true;
                }
            }
        }
        else if (status == OperationStatus.Running)
        {
            _cancelRequested[operationId] = true;
        }

        return new JsonObject
        {
            ["operation_id"] = operationId,
            ["status"] = Wire(status),
            ["cancel_requested"] = _cancelRequested.ContainsKey(operationId),
            ["confirmed"] = confirmed,
            ["reason"] = CancelReason(status),
            ["document_id"] = record?.DocumentId,
            ["needs_reconciliation"] = record?.NeedsReconciliation ?? false,
        };
    }

    /// <summary>The wording of a cancellation answer, chosen by the state the operation is in AFTER the
    /// request: a confirmed cancellation and a request COM will never honour must not read alike.</summary>
    internal static string CancelReason(OperationStatus status) => status switch
    {
        OperationStatus.Cancelled => "Команда снята из очереди Хоста и в Worker не уходила.",
        OperationStatus.Running => "Команда уже в Worker: отмена записана, COM не прерывается, итог "
            + "будет тем, который реально вышел.",
        OperationStatus.Queued => "Очередь не подтвердила снятие: команда уже передаётся в Worker. "
            + "Отмена не подтверждена — повторите вызов: выполняющуюся команду она только пометит.",
        _ => "Операция уже завершена: состояние не меняется.",
    };

    /// <summary>Compare one unknown outcome with the document itself. Three answers only: applied, proven
    /// not applied, or undetermined with a reason. The command is NEVER re-sent (spec 1.8).</summary>
    /// <remarks>MEASURED: the revision is the one observable that every journaled mutation moves, so the
    /// comparison needs no per-tool knowledge. LIMIT: a revision that grew is attributed to this operation
    /// only when the journal holds no other record for the same document started later — two mutations in
    /// the same window cannot be told apart, and the answer says so instead of guessing.
    /// History: docs/decisions/host.md#reconcile-evidence</remarks>
    public async Task<JsonObject?> ReconcilePayloadAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!_journal.TryGet(operationId, out var record) || record is null)
        {
            return null;
        }

        var status = StateOf(record, _dispatched.TryGetValue(operationId, out var d2) && d2);
        if (status != OperationStatus.OutcomeUnknown)
        {
            return new JsonObject
            {
                ["operation_id"] = operationId,
                ["status"] = Wire(status),
                ["outcome"] = "not_needed",
                ["needs_reconciliation"] = record.NeedsReconciliation,
                ["reason"] = "Согласовывать нечего: исход операции определён.",
            };
        }

        if (record.DocumentId is not { Length: > 0 } documentId || record.BaseRevision is not { } baseRevision)
        {
            return Undetermined(operationId, record, "Журнал не записал документ или исходную ревизию: "
                + "сравнивать не с чем, исход остаётся неизвестным.");
        }

        var contextTool = ToolCatalog.All.FirstOrDefault(t => string.Equals(t.Name, "kompas_get_context", StringComparison.Ordinal));
        if (contextTool is null)
        {
            return Undetermined(operationId, record, "Чтение контекста недоступно в этой сборке.");
        }

        var read = await RunReadAsync(contextTool, new JsonObject { ["document_id"] = documentId }, cancellationToken).ConfigureAwait(false);
        if (read.Status != OperationStatus.Succeeded || ReadLong(read.Result, "revision") is not { } revisionNow)
        {
            return Undetermined(operationId, record, "Ревизию документа прочитать не удалось: "
                + (read.Error?.Message ?? "ответ без поля revision") + " Исход остаётся неизвестным.");
        }

        // A revision that grew says SOMETHING changed. It is this operation's change only when the
        // journal holds no other record for the same document started later: otherwise two mutations
        // share the window and no observation separates them.
        var others = _journal.Recent(int.MaxValue).Count(r =>
            !string.Equals(r.OperationId, operationId, StringComparison.Ordinal)
            && string.Equals(r.DocumentId, documentId, StringComparison.Ordinal)
            && r.StartedUtc >= record.StartedUtc);

        // The only proof of "never executed" this Host holds: its own dispatch flag. `null` after a
        // restart — an absent fact, not a negative one.
        bool? commandLeftHost = _dispatched.TryGetValue(operationId, out var left) ? left : null;
        var partialEffects = record.Error?.PartialEffects == true;

        switch (DecideReconcile(baseRevision, revisionNow, others, commandLeftHost, partialEffects))
        {
            case ReconcileVerdict.NotApplied:
            {
                var error = new ErrorDto(
                    ErrorCodes.OperationNotApplied,
                    "Согласование: Хост знает, что команда не передавалась в Worker, — исполнения не было. "
                    + "Повтор с тем же operation_id допустим.",
                    RetryPolicy.SameOperationId,
                    null,
                    false,
                    new JsonObject { ["reconciled"] = "not_applied", ["revision_before"] = baseRevision, ["revision_now"] = revisionNow });

                var recorded = _journal.Fail(operationId, error);
                return new JsonObject
                {
                    ["operation_id"] = operationId,
                    ["status"] = Wire(OperationStatus.Failed),
                    ["outcome"] = "not_applied",
                    ["needs_reconciliation"] = !recorded,
                    ["revision_before"] = baseRevision,
                    ["revision_now"] = revisionNow,
                    ["reason"] = "Команда не уходила из Хоста в Worker: исполнения не было.",
                };
            }

            case ReconcileVerdict.Undetermined:
                return Undetermined(operationId, record, revisionNow < baseRevision
                    ? $"Ревизия документа ({revisionNow}) МЕНЬШЕ записанной ({baseRevision}): документ заменён "
                        + "или переоткрыт, приписать изменение операции нельзя."
                    : partialEffects
                        ? $"Записанная ошибка говорит о частичном следе (partial_effects), ревизия выросла до "
                            + $"{revisionNow}: рост может быть этим следом, а не итогом команды. Исход остаётся "
                            + "неизвестным."
                    : revisionNow == baseRevision
                        ? $"Ревизия документа осталась {revisionNow}, но это доказывает только то, что она не "
                            + "сдвинулась: команда могла ещё исполняться или примениться без сдвига ревизии "
                            + "(истёк бюджет Worker, исключение в мутации, откат файла). Исполнения не "
                            + "опровергнуто — исход остаётся неизвестным."
                    : $"Ревизия выросла до {revisionNow}, но после этой записи по тому же документу начато "
                        + $"ещё операций: {others}. Приписать изменение одной из них нечем — исход остаётся "
                        + "неизвестным.");

            default:
            {
                var evidence = new JsonObject
                {
                    ["reconciled"] = "applied",
                    ["revision_before"] = baseRevision,
                    ["revision_now"] = revisionNow,
                    ["other_operations_on_document"] = 0,
                };
                var completed = _journal.Complete(operationId, evidence.ToJsonString());
                return new JsonObject
                {
                    ["operation_id"] = operationId,
                    ["status"] = Wire(completed ? OperationStatus.Succeeded : OperationStatus.OutcomeUnknown),
                    ["outcome"] = "applied",
                    ["needs_reconciliation"] = !completed,
                    ["revision_before"] = baseRevision,
                    ["revision_now"] = revisionNow,
                    ["reason"] = "Ревизия документа выросла после базовой, и других операций по этому документу "
                        + "в том же окне журнал не знает: изменение принадлежит этой команде.",
                };
            }
        }
    }

    /// <summary>An answer that resolves nothing. The marker stays: only a proven outcome clears it.</summary>
    private JsonObject Undetermined(string operationId, JournalRecord record, string reason)
    {
        var node = new JsonObject
        {
            ["operation_id"] = operationId,
            ["status"] = Wire(OperationStatus.OutcomeUnknown),
            ["outcome"] = "undetermined",
            ["needs_reconciliation"] = record.NeedsReconciliation,
            ["revision_before"] = record.BaseRevision,
            ["document_id"] = record.DocumentId,
            ["reason"] = reason,
        };

        // The recorded error may already name a partial trace; an answer that stays silent about it
        // would let the client read a grown revision as the command's result.
        if (record.Error?.PartialEffects == true)
        {
            node["partial_effects"] = true;
        }

        return node;
    }

    /// <summary>Journal outcome plus the Host phase, in one place: every reader of a state must agree.</summary>
    /// <param name="dispatched">Whether the command already left the Host for the Worker.</param>
    internal static OperationStatus StateOf(JournalRecord record, bool dispatched) => record.Outcome switch
    {
        // A terminal write that did not land leaves the record `succeeded` with the flag set: the
        // outcome is known to this process only, so the honest state is "unknown".
        JournalOutcome.Succeeded => record.NeedsReconciliation ? OperationStatus.OutcomeUnknown : OperationStatus.Succeeded,
        JournalOutcome.Failed => OperationStatus.Failed,
        JournalOutcome.Cancelled => OperationStatus.Cancelled,
        JournalOutcome.OutcomeUnknown => OperationStatus.OutcomeUnknown,
        _ => dispatched ? OperationStatus.Running : OperationStatus.Queued,
    };

    /// <summary>What the comparison of the journal's base revision with the document's current one proves.</summary>
    public enum ReconcileVerdict
    {
        /// <summary>The revision grew and no other operation shared the window.</summary>
        Applied,

        /// <summary>PROVEN that the command never executed: the Host knows it never left for the Worker.</summary>
        NotApplied,

        /// <summary>Nothing follows from the observation; the outcome stays unknown.</summary>
        Undetermined,
    }

    /// <summary>The three-outcome rule, kept pure so it can be tested without a Worker.</summary>
    /// <remarks>INVARIANT: an unchanged revision does NOT prove "not applied" — it moves only after the
    /// COM calls succeed, so a budget expiry, an exception or a file rollback leaves it untouched while
    /// the command may still run or already be applied. <c>not_applied</c> needs the Host's own proof
    /// that the command never left (<paramref name="commandLeftHost"/> false; <c>null</c> is not proof),
    /// and a recorded partial trace or a second operation in the window leaves it undetermined.
    /// History: docs/decisions/host.md#reconcile-evidence</remarks>
    internal static ReconcileVerdict DecideReconcile(
        long baseRevision, long revisionNow, int others, bool? commandLeftHost, bool partialEffects) =>
        commandLeftHost == false ? ReconcileVerdict.NotApplied
        : revisionNow == baseRevision ? ReconcileVerdict.Undetermined
        : revisionNow < baseRevision ? ReconcileVerdict.Undetermined
        : partialEffects ? ReconcileVerdict.Undetermined
        : others > 0 ? ReconcileVerdict.Undetermined
        : ReconcileVerdict.Applied;

    internal static string Wire(OperationStatus status) => status switch
    {
        OperationStatus.Queued => "queued",
        OperationStatus.Running => "running",
        OperationStatus.Succeeded => "succeeded",
        OperationStatus.Failed => "failed",
        OperationStatus.Cancelled => "cancelled",
        _ => "outcome_unknown",
    };

    /// <summary>Drop the in-memory bookkeeping of a finished operation. The journal keeps the durable
    /// record; these maps only describe what THIS process is doing.</summary>
    /// <remarks>INVARIANT: two facts outlive the operation. The dispatch flag stays while the record needs
    /// reconciliation — it is the only proof that the command never left the Host, and `not_applied` may
    /// not be claimed without it. The cancellation request stays for the whole session: a finished
    /// operation that was asked to stop must not later read <c>cancel_requested=false</c>.</remarks>
    private void Forget(string operationId)
    {
        _inFlight.TryRemove(operationId, out _);

        if (_journal.TryGet(operationId, out var record) && record?.NeedsReconciliation == true)
        {
            return;
        }

        _dispatched.TryRemove(operationId, out _);
    }

    public JournalRecord? Status(string operationId)
    {
        _journal.TryGet(operationId, out var record);
        return record;
    }

    /// <summary>Operations whose outcome still needs reconciliation: the counter a client sees.</summary>
    public int CountNeedingReconciliation() => _journal.CountNeedingReconciliation();

    public IReadOnlyDictionary<string, long> QueueStatistics() => _queue.Statistics();

    /// <summary>Operations still running although the synchronous response has already gone.</summary>
    /// <remarks>A queue check cannot replace this: a mutation that overruns <c>SyncBudgetMs</c> answers
    /// <c>running</c> and leaves the queue, so the queue is empty while KOMPAS still works. A release
    /// trusting the empty queue would hand over ownership with a live COM call.</remarks>
    public IReadOnlyList<string> InFlightOperationIds() =>
        _inFlight.Where(pair => !pair.Value.IsCompleted).Select(pair => pair.Key).ToList();

    public async ValueTask DisposeAsync()
    {
        // The journal is owned by Program and disposed there: closing it here would leave the
        // host unable to record anything after an invoker restart.
        await _queue.DisposeAsync().ConfigureAwait(false);
    }
}
