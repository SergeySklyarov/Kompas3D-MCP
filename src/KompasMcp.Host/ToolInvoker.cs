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
using KompasMcp.Host.Catalog;

namespace KompasMcp.Host;

/// <summary>
/// Turns a validated MCP tool call into exactly one Worker command.
/// </summary>
/// <remarks>
/// Ordered so that the guarantees hold without extra ceremony:
/// <list type="bullet">
/// <item>schema validation (unknown field, NaN, negative length, bad enum) — no COM call yet;</item>
/// <item>path policy — a refusal here never touches the model;</item>
/// <item><b>durable journal append</b> — before dispatch, so a crash afterwards is recoverable as
/// outcome-unknown rather than as "it never happened";</item>
/// <item>bounded queue → single Worker dispatch;</item>
/// <item>terminal journal record with the result or the error.</item>
/// </list>
/// The same <c>operation_id</c> with the same arguments returns the recorded outcome instead of
/// re-running: that is what makes a client retry after a timeout safe for reads and honest for
/// writes.
/// </remarks>
public sealed class ToolInvoker : IAsyncDisposable
{
    // save_path — тот же класс поля, что output_path/target_path: назначение файла. Назван
    // отдельно, потому что у растрового снимка он означает «куда положить картинку», и политика
    // обязана судить его так же строго: измерено (проба P4 наряда), что ЯДРО путь не проверяет —
    // на запрещённых символах оно записало усечённый пустой файл, а несуществующий каталог
    // создало само. Единственная защита от такого пути — отказ ХОСТА до COM.
    private static readonly string[] PathFields = { "path", "output_path", "target_path", "input_path", "save_path" };

    private readonly HostOptions _options;
    private readonly WorkerSupervisor _worker;
    private readonly OperationJournal _journal;
    private readonly CadCommandQueue _queue;
    private readonly PathPolicy _pathPolicy;
    private readonly HostLog _log;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Public entry point. Every contract error is converted into an envelope here, including the
    /// ones raised deep inside the journal or the queue: a <see cref="KompasContractException"/>
    /// that escapes would be reported by the caller as an unclassified VERIFICATION_FAILED, and
    /// OPERATION_ID_CONFLICT in particular must never lose its code — that code is the whole
    /// answer to "may I send this again".
    /// </summary>
    public async Task<ResultEnvelope<JsonNode?>> InvokeAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await InvokeCoreAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
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

    private async Task<ResultEnvelope<JsonNode?>> InvokeCoreAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
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

        var pathFailure = CheckPaths(tool, arguments);
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

        return tool.IsMutation
            ? await RunMutationAsync(tool, arguments, operationId!, cancellationToken).ConfigureAwait(false)
            : await RunReadAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------------------------

    private List<SchemaViolation> Validate(ToolDefinition tool, JsonObject arguments)
    {
        var violations = new List<SchemaViolation>(JsonSchemaValidator.Validate(tool.InputSchema, arguments));

        if (ReadString(arguments, "operation_id") is { Length: > 0 } operationIdValue && !Guid.TryParse(operationIdValue, out _))
        {
            violations.Add(new SchemaViolation("/operation_id", "format", "operation_id должен быть UUID."));
        }

        violations.AddRange(ModeViolations(tool, arguments));
        return violations;
    }

    /// <summary>
    /// Mode-dependent field rules. The published validator understands anyOf/oneOf but not
    /// if/then/allOf, so a combination cannot be expressed in the schema without adding keywords
    /// that would then be silently ignored everywhere else; it is checked here instead, before COM.
    /// Spec 4.1 (docs/05) demands exactly this: required, optional and forbidden fields per mode,
    /// with unsupported combinations rejected rather than normalised away.
    /// </summary>
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
            // Правила «поля ↔ способ» живут здесь, а не в схеме: валидатор публиковал бы
            // if/then, который в остальных местах молча игнорируется (см. комментарий выше).
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
                    // Второй катет при этом способе нечем выразить: принять и промолчать — значит
                    // выдать за применённый параметр, который запросили и не получили.
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
            // Правила «поля ↔ режим» живут здесь, а не в схеме: валидатор публиковал бы if/then,
            // который в остальных местах молча игнорируется (см. комментарий выше). Цена ошибки
            // здесь не симметрична — лишний отказ виден сразу, а принятое и проигнорированное
            // число доживает до приёмки, потому что режимные параметры разных режимов живут в
            // РАЗНЫХ интерфейсах API7 (ISpotfacingHoleParameters против ICountersinkHoleParameters).
            var mode = ReadString(arguments, "mode") ?? "blind_flat";
            var holeHasDepth = Present(arguments, "depth_mm");
            var hasBoreDiameter = Present(arguments, "counterbore_diameter_mm");
            var hasBoreDepth = Present(arguments, "counterbore_depth_mm");
            var hasMouth = Present(arguments, "countersink_diameter_mm");
            var hasAngle = Present(arguments, "countersink_angle_deg");
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
                case "through_counterbore" when holeHasDepth:
                    // Принять число и промолчать — значит выдать за применённую ту глубину,
                    // которую сквозной режим не читает.
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
                case not ("blind_flat" or "through_counterbore" or "through_countersink"):
                    yield return new SchemaViolation(
                        "/mode", "enum", $"Неизвестный режим отверстия: {mode}.");
                    break;
            }

            yield break;
        }

        if (tool.Name == "kompas_update_feature")
        {
            // Семейство признака сервер узнаёт только из COM, поэтому здесь проверяется то, что
            // видно из аргументов: выдавочные и фасочные поля в одном вызове противоречат друг
            // другу, и молча применить половину было бы хуже отказа.
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

            // Правку угла здесь не дублируем: отказ принадлежит адаптеру (CAPABILITY_UNAVAILABLE,
            // потому что маршрут не измерен), а правило «поля ↔ способ» выше — контрактный слой.
            // Две проверки одного случая с разными кодами дали бы приёмке два ответа на один
            // запрос, и «где именно отказали» перестал бы читаться.
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

        // docs/02 makes target_body_ref a field of boss and cut only. `base` creates the first body,
        // so there is nothing for it to aim at; letting the pair through and ignoring the reference
        // would report a honoured target that was never requested from КОМПАС.
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
                // Accepting a number that КОМПАС then ignores would let a caller believe a depth
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

    private ErrorDto? CheckPaths(ToolDefinition tool, JsonObject arguments)
    {
        foreach (var field in PathFields)
        {
            if (ReadString(arguments, field) is not { Length: > 0 } value)
            {
                continue;
            }

            // Only an output destination is a write; a source path is read-only intent.
            var writes = field is "output_path" or "target_path" or "save_path";
            var decision = _pathPolicy.Evaluate(value, intendToWrite: writes);
            if (decision.Access != PathAccess.Denied)
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

    // ---------------------------------------------------------------------------------------------
    // Execution
    // ---------------------------------------------------------------------------------------------

    private async Task<ResultEnvelope<JsonNode?>> RunReadAsync(ToolDefinition tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(ReadInt(arguments, "timeout_ms") ?? _options.OperationBudgetMs);
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
        var decision = _journal.TryBegin(operationId, tool.Name, Canonical(tool, arguments), ReadString(arguments, "document_id"), ReadLong(arguments, "expected_revision"));

        if (!decision.Proceed && decision.Existing is { } existing)
        {
            return Replayed(tool, existing, arguments);
        }

        var execution = ExecuteAsync(tool, arguments, operationId, cancellationToken);
        if (!_inFlight.TryAdd(operationId, execution))
        {
            // The journal says "not started", yet a task with the same id exists: report the
            // in-flight one rather than dispatching a second mutation.
            await execution.ConfigureAwait(false);
        }

        // Return synchronously while the operation fits the budget; beyond it, hand back an
        // operation id to poll and let the task keep running (spec 1.8).
        var completed = await Task.WhenAny(execution, Task.Delay(_options.SyncBudgetMs, CancellationToken.None)).ConfigureAwait(false);
        if (completed == execution)
        {
            _inFlight.TryRemove(operationId, out _);
            return await execution.ConfigureAwait(false);
        }

        _ = execution.ContinueWith(t =>
        {
            _inFlight.TryRemove(operationId, out _);
            _ = t;
        }, TaskScheduler.Default);

        return Envelope(OperationStatus.Running, tool, operationId, null, arguments,
            warnings: new[]
            {
                // Раньше здесь было «опрашивайте kompas_operation_status» — инструмента с таким
                // именем в каталоге нет и никогда не было. Клиент, поверивший подсказке самого
                // сервера, получал отказ «неизвестный инструмент» вместо состояния операции.
                // Опрос идёт повтором ТОЙ ЖЕ команды с ТЕМ ЖЕ operation_id: журнал воспроизводит
                // записанный исход (см. Replayed), а незавершённая операция отвечает Running.
                $"Операция не завершилась за {_options.SyncBudgetMs / 1000} с и продолжает выполняться; " +
                "повторите тот же вызов с тем же operation_id — журнал вернёт записанный исход, " +
                "пока операция идёт, статус остаётся running.",
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
            _journal.Fail(operationId, queueFull.ToErrorDto());
            return Envelope(OperationStatus.Failed, tool, operationId, null, arguments, queueFull.ToErrorDto());
        }

        try
        {
            var timeout = TimeSpan.FromMilliseconds(_options.OperationBudgetMs);
            var frame = await _worker.SendAsync(tool.WorkerCommand, CanonicalNode(tool, arguments), timeout, isMutation: true, cancellation.Token).ConfigureAwait(false);

            if (frame.Error is null)
            {
                var result = frame.Payload;
                _journal.Complete(operationId, result?.ToJsonString());
                return Envelope(OperationStatus.Succeeded, tool, operationId, result, arguments);
            }

            if (frame.Error.Code == ErrorCodes.OutcomeUnknown)
            {
                _journal.MarkUnknown(operationId, frame.Error.Message);
            }
            else
            {
                _journal.Fail(operationId, frame.Error);
            }

            return Envelope(frame.Error.Code == ErrorCodes.OutcomeUnknown ? OperationStatus.OutcomeUnknown : OperationStatus.Failed,
                tool, operationId, null, arguments, frame.Error);
        }
        catch (KompasContractException contract) when (contract.Code == ErrorCodes.QueueFull)
        {
            _journal.Fail(operationId, contract.ToErrorDto());
            return Envelope(OperationStatus.Failed, tool, operationId, null, arguments, contract.ToErrorDto());
        }
        catch (KompasContractException contract)
        {
            if (contract.Code == ErrorCodes.OutcomeUnknown)
            {
                _journal.MarkUnknown(operationId, contract.Message);
                return Envelope(OperationStatus.OutcomeUnknown, tool, operationId, null, arguments, contract.ToErrorDto());
            }

            _journal.Fail(operationId, contract.ToErrorDto());
            return Envelope(OperationStatus.Failed, tool, operationId, null, arguments, contract.ToErrorDto());
        }
        catch (OperationCanceledException)
        {
            // Cancelled before it ever reached the Worker: the model was not touched, so this is a
            // confirmed cancellation rather than CANCEL_NOT_CONFIRMED.
            _journal.Cancel(operationId);
            return Envelope(OperationStatus.Cancelled, tool, operationId, default(JsonNode?), arguments,
                warnings: new[] { "Команда отменена в очереди Host и не отправлялась в КОМПАС." });
        }
        finally
        {
            // The queue admits work, it does not execute it: the command goes to the Worker straight
            // after Enqueue, so the slot must be released here. Without this the bounded channel is a
            // lifetime budget of QueueCapacity mutations per process, and the 65th mutation of a long
            // session fails with QUEUE_FULL — which is exactly what the first acceptance run of the
            // multi-body U05 group hit.
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

        var warnings = new List<string>
        {
            $"Запись журнальная: этот operation_id уже выполнялся ({record.Outcome.ToString().ToLowerInvariant()}), повторного обращения к КОМПАС не было.",
        };

        if (record.Outcome == JournalOutcome.OutcomeUnknown)
        {
            warnings.Add("Исход предыдущей попытки неизвестен: сверьте модель с ожиданием (объём, число тел, история признаков), повтор мутации с НОВЫМ operation_id запрещён. Отдельного инструмента сверки в каталоге нет — повтор с тем же operation_id отвечает записанным исходом, но не фактическим состоянием модели.");
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

        // Identity fields are taken from the caller first and from the Worker's answer second:
        // kompas_connect has no application_id in its arguments (it is the call that invents one),
        // and leaving the envelope null there would hide the session id from the very response
        // whose job is to report it.
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
            RevisionAfter = ReadLong(result, "revision") ?? ReadLong(result, "revision_after"),
            Result = result,
            // The Worker is the only party that actually observed the model or the file, so its
            // verification level wins. The Host's own value is a floor, never a ceiling: claiming
            // "geometry_checked" here would be overclaiming, and silently downgrading the Worker's
            // honest answer to "call_returned" would hide proof the client is entitled to.
            Verification = WorkerVerification(result) ?? new VerificationDto(
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

    /// <summary>
    /// Read the Worker's own verification block, if it sent one. Absent or unparseable means the
    /// Host falls back to the conservative level rather than inventing one.
    /// </summary>
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

    // Reads go through JsonScalars because these nodes arrived over the named pipe and are
    // JsonElement-backed; the strict GetValue<T>/TryGetValue<T> overloads silently return nothing
    // for that storage, which made the revision chain look stale.
    private static string? ReadString(JsonNode? node, string key) =>
        node is JsonObject obj ? JsonScalars.ReadString(obj[key]) : null;

    /// <summary>
    /// Поле задано и не null. Отличается от «TryGetPropertyValue» тем, что пустая строка и ноль
    /// считаются заданными: отказ должен приходить от правила «поле запрещено в этом режиме», а не
    /// от того, что значение оказалось ложным для bool.
    /// </summary>
    private static bool Present(JsonObject arguments, string key) =>
        arguments.TryGetPropertyValue(key, out var value) && value is not null;

    private static long? ReadLong(JsonNode? node, string key) =>
        node is JsonObject obj ? JsonScalars.ReadLong(obj[key]) : null;

    private static int? ReadInt(JsonNode? node, string key) =>
        node is JsonObject obj ? JsonScalars.ReadInt(obj[key]) : null;

    /// <summary>Cancellation of a queued command; a started one cannot be aborted here.</summary>
    public bool TryCancel(string operationId)
    {
        if (_queue.TryCancelQueued(operationId))
        {
            _journal.Cancel(operationId);
            return true;
        }

        return _inFlight.TryGetValue(operationId, out var task) && !task.IsCompleted;
    }

    public JournalRecord? Status(string operationId)
    {
        _journal.TryGet(operationId, out var record);
        return record;
    }

    public IReadOnlyDictionary<string, long> QueueStatistics() => _queue.Statistics();

    public async ValueTask DisposeAsync()
    {
        // The journal is owned by Program and disposed there: closing it here would leave the
        // host unable to record anything after an invoker restart.
        await _queue.DisposeAsync().ConfigureAwait(false);
    }
}
