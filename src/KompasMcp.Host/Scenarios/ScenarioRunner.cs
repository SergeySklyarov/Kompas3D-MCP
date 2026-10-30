using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Contracts.Schema;
using KompasMcp.Host.Catalog;

namespace KompasMcp.Host.Scenarios;

/// <summary>Runs a bounded plan of registered tool calls: validation, references, child operation ids,
/// stop or restore on a failed step.</summary>
/// <remarks>INVARIANT: every step goes through <see cref="ToolInvoker.InvokeAsync"/>, the same entry a
/// direct call uses, so schema checks, the path policy, the journal and the queue are not duplicated
/// here. INVARIANT: the scenario is NOT an atomic transaction — a failed step leaves the effects of the
/// steps before it, and the answer names them.
/// History: docs/decisions/scenario.md#scenario-run</remarks>
internal sealed class ScenarioRunner
{
    /// <summary>The published plan version. A plan of another version is refused, not guessed at.</summary>
    /// <remarks>LIMIT: version 1 only; a newer version needs its own reading rules, not a lenient
    /// fallback. History: docs/decisions/scenario.md#version</remarks>
    internal const int ScenarioVersion = 1;

    /// <summary>Hard bound on the plan length, checked before the first step.</summary>
    /// <remarks>LIMIT: 100 steps; a longer plan is refused, because the server must not become a
    /// general-purpose scripting host. History: docs/decisions/scenario.md#limits</remarks>
    internal const int MaxSteps = 100;

    /// <summary>Tools a step may not call, by registry name.</summary>
    /// <remarks>INVARIANT: a step may not write files, open or close documents, take or drop the
    /// session, or nest a scenario. File destinations are the client's business and a closed document
    /// would invalidate the plan's own <c>document_id</c> mid-run; nesting would defeat the step bound.
    /// History: docs/decisions/scenario.md#forbidden-tools</remarks>
    internal static readonly string[] ForbiddenStepTools =
    {
        "kompas_run_scenario", "kompas_save_document", "kompas_open_document", "kompas_create_document",
        "kompas_close_document", "kompas_import_step", "kompas_export_step", "kompas_export_stl",
        "kompas_export_iges", "kompas_export_image", "kompas_export_drawing", "kompas_export_bom",
        "kompas_export_sheet_metal_flat_dxf", "kompas_connect", "kompas_disconnect",
        "kompas_acquire_session", "kompas_release_session", "kompas_session_status", "kompas_health",
        "kompas_capabilities", "kompas_probe_units",
    };

    /// <summary>How a checkpoint is taken. <c>none</c> is legal only with <c>on_error=stop</c>.</summary>
    internal static readonly string[] CheckpointPolicies = { "document_copy", "none" };

    private readonly string _controlCopyDirectory;
    private readonly AssemblyCheckpoint _assembly;
    private readonly Func<string, JsonObject, CancellationToken, Task<ResultEnvelope<JsonNode?>>> _invoke;

    internal ScenarioRunner(
        string controlCopyDirectory,
        Func<string, JsonObject, CancellationToken, Task<ResultEnvelope<JsonNode?>>> invoke)
    {
        _controlCopyDirectory = controlCopyDirectory;
        _assembly = new AssemblyCheckpoint(controlCopyDirectory, invoke);
        _invoke = invoke;
    }

    internal async Task<ResultEnvelope<JsonNode?>> RunAsync(
        JsonObject arguments, CancellationToken cancellationToken)
    {
        var parentOperationId = JsonScalars.ReadString(arguments["operation_id"]);
        var documentId = JsonScalars.ReadString(arguments["document_id"]);
        var onError = JsonScalars.ReadString(arguments["on_error"]) ?? "stop";
        var policy = JsonScalars.ReadString(arguments["checkpoint_policy"]) ?? "document_copy";
        var externalParts = JsonScalars.ReadString(arguments["external_parts"])
            ?? AssemblyCheckpoint.ExternalPartsCopy;

        if (string.IsNullOrWhiteSpace(parentOperationId) || !Guid.TryParse(parentOperationId, out _))
        {
            return Refuse("operation_id сценария обязателен и должен быть UUID: от него выводятся "
                + "operation_id шагов, и без него повтор сценария создал бы второй эффект.",
                "operation_id", "parent_operation_id_missing");
        }

        if (string.IsNullOrWhiteSpace(documentId))
        {
            return Refuse("document_id сценария обязателен: шаги адресуют документ по нему.",
                "document_id", "document_id_missing");
        }

        if (onError is not ("stop" or "restore_checkpoint"))
        {
            return Refuse($"Неизвестное значение on_error: {onError}.", "on_error", "on_error_unknown");
        }

        if (!CheckpointPolicies.Contains(policy, StringComparer.Ordinal))
        {
            return Refuse($"Неизвестное значение checkpoint_policy: {policy}.",
                "checkpoint_policy", "checkpoint_policy_unknown");
        }

        // A mode the server does not know is refused, not silently read as "copy": the answer promises what
        // the copy folder contains, and an unknown mode would make that promise untrue.
        if (!AssemblyCheckpoint.ExternalPartsModes.Contains(externalParts, StringComparer.Ordinal))
        {
            return Refuse($"Неизвестное значение external_parts: {externalParts}. Допустимо: "
                + string.Join(", ", AssemblyCheckpoint.ExternalPartsModes) + ".",
                "external_parts", "external_parts_unknown");
        }

        if (onError == "restore_checkpoint" && policy == "none")
        {
            return Refuse("on_error=restore_checkpoint требует checkpoint_policy=document_copy: "
                + "восстанавливать нечего, если контрольная точка не снималась.",
                "checkpoint_policy", "checkpoint_not_taken");
        }

        var version = JsonScalars.ReadInt(arguments["scenario_version"]) ?? ScenarioVersion;
        if (version != ScenarioVersion)
        {
            return Refuse($"Версия плана {version} не поддерживается: сервер читает версию {ScenarioVersion}.",
                "scenario_version", "version_unsupported");
        }

        var plan = ReadPlan(arguments["steps"], out var planError, out var errorField);
        if (plan is null)
        {
            return Refuse(planError!, errorField, "plan_refused");
        }

        // ---- Everything above changes nothing; the first effect happens below. ----

        var steps = new JsonArray();
        var results = new Dictionary<string, StepOutcome>(StringComparer.Ordinal);
        var mutated = new List<string>();
        string? failedStep = null;
        JsonObject? failedError = null;
        var checkpoint = new JsonObject { ["policy"] = policy, ["taken"] = false, ["path"] = null };
        var restore = new JsonObject { ["attempted"] = false, ["succeeded"] = false };
        var skipped = false;
        var effectiveDocumentId = documentId;

        foreach (var step in plan)
        {
            if (skipped)
            {
                steps.Add(StepReport(step, "skipped", ChildOperationId(parentOperationId, step.StepId),
                    null, null, null, null));
                continue;
            }

            if (step.Tool.IsMutation && onError == "restore_checkpoint"
                && checkpoint["taken"]!.GetValue<bool>() == false)
            {
                var taken = await TakeCheckpointAsync(
                        parentOperationId, effectiveDocumentId, externalParts, cancellationToken)
                    .ConfigureAwait(false);
                checkpoint = taken.Report;
                if (!taken.Ok)
                {
                    // INVARIANT: without a checkpoint there is nothing to restore, so no step runs.
                    failedStep = step.StepId;
                    failedError = taken.Error;
                    skipped = true;
                    steps.Add(StepReport(step, "skipped", ChildOperationId(parentOperationId, step.StepId),
                        null, null, null, taken.Error));
                    continue;
                }
            }

            var stepArguments = ResolveArguments(step.Arguments, results, out var resolveError, out var resolvePath);
            if (resolveError is not null)
            {
                failedStep = step.StepId;
                failedError = new JsonObject { ["code"] = "INVALID_ARGUMENT", ["message"] = resolveError, ["field"] = resolvePath };
                skipped = true;
                steps.Add(StepReport(step, "failed", ChildOperationId(parentOperationId, step.StepId),
                    null, null, null, failedError));
                continue;
            }

            // INVARIANT: only fields the step tool DECLARES are filled in. A tool addressed by its own
            // reference (sketch_ref, feature_ref) has no document_id, and the Host refuses a field its
            // schema does not have — injecting it would break every such step.
            if (Declares(step.Tool, "document_id"))
            {
                stepArguments["document_id"] = effectiveDocumentId;
            }

            if (Declares(step.Tool, "operation_id"))
            {
                stepArguments["operation_id"] = ChildOperationId(parentOperationId, step.StepId);
            }

            var envelope = await _invoke(step.Tool.Name, stepArguments, cancellationToken).ConfigureAwait(false);
            var compact = Compact(envelope.Result, out var truncated);
            results[step.StepId] = new StepOutcome(envelope.Result as JsonObject, envelope.RevisionAfter);

            if (envelope.Status == OperationStatus.Succeeded)
            {
                if (step.Tool.IsMutation)
                {
                    mutated.Add(step.StepId);
                }

                steps.Add(StepReport(step, "succeeded", envelope.OperationId,
                    envelope.RevisionBefore, envelope.RevisionAfter, compact, null, truncated));
                continue;
            }

            failedStep = step.StepId;
            failedError = ErrorOf(envelope);
            skipped = true;
            steps.Add(StepReport(step, "failed", envelope.OperationId,
                envelope.RevisionBefore, envelope.RevisionAfter, compact, failedError, truncated));
        }

        if (failedStep is not null && onError == "restore_checkpoint")
        {
            var restored = await RestoreAsync(parentOperationId, effectiveDocumentId,
                JsonScalars.ReadString(checkpoint["path"]),
                JsonScalars.ReadString(checkpoint["application_id"]),
                JsonScalars.ReadInt(checkpoint["read_only_not_restored"]) ?? 0,
                cancellationToken).ConfigureAwait(false);
            restore = restored;
            if (restored["document_id_after"] is JsonValue after
                && after.TryGetValue<string>(out var reopened))
            {
                effectiveDocumentId = reopened;
            }
        }

        var result = new JsonObject
        {
            ["scenario_version"] = ScenarioVersion,
            ["document_id"] = documentId,
            ["document_id_after"] = effectiveDocumentId,
            ["operation_id"] = parentOperationId,
            ["on_error"] = onError,
            ["status"] = failedStep is null ? "succeeded" : "failed",
            ["failed_step"] = failedStep,
            ["partial_effects"] = new JsonArray(mutated.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["steps"] = steps,
            ["checkpoint"] = checkpoint,
            ["restore"] = restore,
            ["atomicity"] = "не транзакция: успешные шаги до отказавшего остаются в модели, "
                + "если восстановление не выполнялось или не удалось",
        };

        return new ResultEnvelope<JsonNode?>
        {
            Status = failedStep is null ? OperationStatus.Succeeded : OperationStatus.Failed,
            OperationId = parentOperationId,
            DocumentId = documentId,
            Result = result,
            Verification = new VerificationDto(
                VerificationLevel.CallReturned,
                new[]
                {
                    new NamedCheck("steps_executed", failedStep is null),
                    new NamedCheck("partial_effects_named", true),
                },
                new[] { "scenario_is_not_a_transaction" }),
            Error = failedStep is null
                ? null
                : new ErrorDto(
                    ErrorCodes.VerificationFailed,
                    $"Сценарий остановлен на шаге {failedStep}: "
                    + (JsonScalars.ReadString(failedError?["message"]) ?? "шаг отказал") + ".",
                    RetryPolicy.Never,
                    null,
                    mutated.Count > 0,
                    new JsonObject
                    {
                        ["failed_step"] = failedStep,
                        ["step_error"] = failedError?.DeepClone(),
                        ["partial_effects"] = new JsonArray(mutated.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
                        ["restore_attempted"] = JsonScalars.ReadBool(restore["attempted"]) ?? false,
                        ["restored"] = JsonScalars.ReadBool(restore["succeeded"]) ?? false,
                    }),
        };
    }

    private static bool Declares(ToolDefinition tool, string field) =>
        tool.InputSchema["properties"] is JsonObject properties && properties.ContainsKey(field);

    // ---- Plan reading ----

    private sealed record StepPlan(string StepId, ToolDefinition Tool, JsonObject Arguments);

    private static List<StepPlan>? ReadPlan(JsonNode? node, out string? error, out string field)
    {
        error = null;
        field = "steps";
        if (node is not JsonArray array || array.Count == 0)
        {
            error = "steps обязателен и непуст: сценарий без шагов ничего не делает.";
            return null;
        }

        if (array.Count > MaxSteps)
        {
            error = $"В плане {array.Count} шагов при пределе {MaxSteps}: такой план не выполняется.";
            return null;
        }

        var plan = new List<StepPlan>(array.Count);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject step)
            {
                error = $"Шаг {i + 1} не объект.";
                field = $"steps[{i}]";
                return null;
            }

            var stepId = JsonScalars.ReadString(step["step_id"]);
            var toolName = JsonScalars.ReadString(step["tool"]);
            if (string.IsNullOrWhiteSpace(stepId))
            {
                error = $"У шага {i + 1} нет step_id: на него ссылаются другие шаги.";
                field = $"steps[{i}].step_id";
                return null;
            }

            if (ids.ContainsKey(stepId))
            {
                error = $"step_id '{stepId}' повторяется: ссылка на него неоднозначна.";
                field = $"steps[{i}].step_id";
                return null;
            }

            if (string.IsNullOrWhiteSpace(toolName))
            {
                error = $"У шага '{stepId}' нет tool.";
                field = $"steps[{i}].tool";
                return null;
            }

            var tool = ToolCatalog.All.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
            if (tool is null)
            {
                error = $"Шаг '{stepId}' называет незарегистрированный инструмент '{toolName}'.";
                field = $"steps[{i}].tool";
                return null;
            }

            if (ForbiddenStepTools.Contains(toolName, StringComparer.Ordinal))
            {
                error = $"Шаг '{stepId}' вызывает '{toolName}', а шагу запрещены файловые операции, "
                    + "открытие/закрытие документов, управление сеансом и вложенный сценарий.";
                field = $"steps[{i}].tool";
                return null;
            }

            var stepArguments = step["arguments"] as JsonObject ?? new JsonObject();
            ids[stepId] = i;
            plan.Add(new StepPlan(stepId, tool, (JsonObject)stepArguments.DeepClone()));
        }

        // References are checked BEFORE the first step: a forward or self reference would make the plan
        // order-dependent in a way the caller cannot see, and the answer would be a partial effect.
        for (var i = 0; i < plan.Count; i++)
        {
            foreach (var reference in References(plan[i].Arguments))
            {
                if (!ids.TryGetValue(reference.FromStep, out var producer))
                {
                    error = $"Шаг '{plan[i].StepId}' ссылается на несуществующий шаг '{reference.FromStep}'.";
                    field = $"steps[{i}].arguments.{reference.Path}.from_step";
                    return null;
                }

                if (producer >= i)
                {
                    error = $"Шаг '{plan[i].StepId}' ссылается на шаг '{reference.FromStep}', который идёт "
                        + "не раньше его: разрешены только ссылки назад, на уже выполненные шаги.";
                    field = $"steps[{i}].arguments.{reference.Path}.from_step";
                    return null;
                }

                var prefix = reference.Field.StartsWith("revision.", StringComparison.Ordinal) ? "revision." : "result.";
                if (!reference.Field.StartsWith(prefix, StringComparison.Ordinal)
                    || reference.Field.Length <= prefix.Length)
                {
                    error = $"Ссылка шага '{plan[i].StepId}' на поле '{reference.Field}' не описывает поле: "
                        + "поле задаётся как result.<имя> или revision.after.";
                    field = $"steps[{i}].arguments.{reference.Path}.field";
                    return null;
                }
            }
        }

        return plan;
    }

    private sealed record StepReference(string FromStep, string Field, string Path);

    private static IEnumerable<StepReference> References(JsonNode? node, string path = "")
    {
        switch (node)
        {
            case JsonObject obj when JsonScalars.ReadString(obj["from_step"]) is { Length: > 0 } from:
                yield return new StepReference(from, JsonScalars.ReadString(obj["field"]) ?? string.Empty, path);
                yield break;
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    foreach (var nested in References(pair.Value, path.Length == 0 ? pair.Key : path + "." + pair.Key))
                    {
                        yield return nested;
                    }
                }

                yield break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var nested in References(array[i], $"{path}[{i}]"))
                    {
                        yield return nested;
                    }
                }

                yield break;
        }
    }

    /// <summary>Replaces every reference with the value the earlier step produced.</summary>
    private sealed record StepOutcome(JsonObject? Result, long? RevisionAfter);

    private static JsonObject ResolveArguments(
        JsonObject arguments,
        IReadOnlyDictionary<string, StepOutcome> results,
        out string? error,
        out string? path)
    {
        error = null;
        path = null;
        var resolved = Resolve(arguments.DeepClone(), results, string.Empty, ref error, ref path);
        return resolved as JsonObject ?? new JsonObject();
    }

    private static JsonNode? Resolve(
        JsonNode? node,
        IReadOnlyDictionary<string, StepOutcome> results,
        string path,
        ref string? error,
        ref string? errorPath)
    {
        if (error is not null)
        {
            return node;
        }

        if (node is JsonObject obj && JsonScalars.ReadString(obj["from_step"]) is { Length: > 0 } from)
        {
            var field = JsonScalars.ReadString(obj["field"]) ?? string.Empty;
            if (!results.TryGetValue(from, out var produced))
            {
                error = $"Шаг '{from}' ещё не выполнялся: ссылка на '{field}' не разрешается.";
                errorPath = path;
                return null;
            }

            // `revision.after` is the envelope's revision, not a field of the result payload: a step
            // that changes the model has no other way to hand its new revision to the next step.
            if (field.StartsWith("revision.", StringComparison.Ordinal))
            {
                if (produced.RevisionAfter is not { } revision)
                {
                    error = $"У шага '{from}' не прочитана ревизия: ссылка на '{field}' не разрешается.";
                    errorPath = path;
                    return null;
                }

                return JsonValue.Create(revision);
            }

            if (produced.Result is null)
            {
                error = $"Шаг '{from}' не вернул результата: ссылка на '{field}' не разрешается.";
                errorPath = path;
                return null;
            }

            JsonNode? current = produced.Result;
            foreach (var segment in field.Split('.', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                current = current is JsonObject container ? container[segment] : null;
                if (current is null)
                {
                    break;
                }
            }

            if (current is null)
            {
                error = $"У шага '{from}' в результате нет поля '{field}'.";
                errorPath = path;
                return null;
            }

            return current.DeepClone();
        }

        // Children are CLONED before they are resolved: a node taken straight out of the tree still has
        // its parent, and attaching it to a rebuilt container throws.
        switch (node)
        {
            case JsonObject map:
                var rebuilt = new JsonObject();
                foreach (var pair in map)
                {
                    rebuilt[pair.Key] = Resolve(pair.Value?.DeepClone(), results,
                        path.Length == 0 ? pair.Key : path + "." + pair.Key, ref error, ref errorPath);
                }

                return rebuilt;
            case JsonArray list:
                var array = new JsonArray();
                foreach (var item in list)
                {
                    array.Add(Resolve(item?.DeepClone(), results, $"{path}[{array.Count}]", ref error, ref errorPath));
                }

                return array;
            default:
                return node?.DeepClone();
        }
    }

    // ---- Checkpoint and restore ----

    private sealed record CheckpointOutcome(bool Ok, JsonObject Report, JsonObject? Error);

    private async Task<CheckpointOutcome> TakeCheckpointAsync(
        string parentOperationId, string documentId, string externalParts,
        CancellationToken cancellationToken)
    {
        var report = new JsonObject
        {
            ["policy"] = "document_copy",
            ["taken"] = false,
            ["path"] = null,
            ["revision"] = null,
        };

        // No operation_id: a read declares none, and the Host rejects a field its schema does not have.
        var context = await _invoke("kompas_get_context", new JsonObject
        {
            ["document_id"] = documentId,
            ["detail"] = "minimal",
        }, cancellationToken).ConfigureAwait(false);

        var revision = JsonScalars.ReadLong(context.Result?["revision"]);
        if (context.Status != OperationStatus.Succeeded || revision is null)
        {
            return new CheckpointOutcome(false, report, new JsonObject
            {
                ["code"] = "CHECKPOINT_FAILED",
                ["message"] = "Ревизия документа не прочитана, контрольная точка не снята.",
                ["reason"] = "revision_not_read",
                ["context_status"] = context.Status.ToString(),
                ["context_error"] = ErrorOf(context)?.DeepClone(),
            });
        }

        // INVARIANT: an assembly is copied as a GRAPH, not as one file — a plain SaveAs does not carry its
        // nested parts. DOC: docs/01_TECHNICAL_SPEC.md §1.9.
        var kind = JsonScalars.ReadString(context.Result?["kind"]);
        if (kind == "assembly")
        {
            var assembly = await _assembly.TakeAsync(
                parentOperationId, documentId, context.Result as JsonObject ?? new JsonObject(),
                externalParts, cancellationToken)
                .ConfigureAwait(false);
            if (!assembly.Ok)
            {
                return new CheckpointOutcome(false, assembly.Report, assembly.Error);
            }

            return new CheckpointOutcome(true, assembly.Report, null);
        }

        if (kind == "drawing")
        {
            // A drawing has no dependency graph; the refusal it had before the assembly route existed stays.
            return new CheckpointOutcome(false, report, new JsonObject
            {
                ["code"] = "CHECKPOINT_FAILED",
                ["message"] = "Копия чертежа контрольной точкой не снимается: чертёж не входит в политику "
                    + "document_copy.",
                ["reason"] = "copy_not_written",
                ["details"] = new JsonObject
                {
                    ["field"] = "checkpoint_policy",
                    ["document_kind"] = "drawing",
                },
            });
        }

        var path = CheckpointPath(parentOperationId, revision.Value);
        try
        {
            // MEASURED: SaveAs does not create the folder, and a missing one makes it answer false.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new CheckpointOutcome(false, report, new JsonObject
            {
                ["code"] = "CHECKPOINT_FAILED",
                ["message"] = $"Каталог контрольных копий недоступен: {ex.GetType().Name}: {ex.Message}",
                ["reason"] = "copy_folder_unavailable",
            });
        }
        var saved = await _invoke("kompas_save_document", new JsonObject
        {
            ["document_id"] = documentId,
            ["expected_revision"] = revision.Value,
            ["target_path"] = path,
            ["operation_id"] = ChildOperationId(parentOperationId, "checkpoint-save"),
        }, cancellationToken).ConfigureAwait(false);

        if (saved.Status != OperationStatus.Succeeded)
        {
            return new CheckpointOutcome(false, report, new JsonObject
            {
                ["code"] = "CHECKPOINT_FAILED",
                ["message"] = "Копия документа не снята: документ не сохранён документированным SaveAs "
                    + "(копией одного файла восстанавливается только деталь).",
                ["reason"] = "copy_not_written",
                ["step_error"] = ErrorOf(saved)?.DeepClone(),
            });
        }

        report["taken"] = true;
        report["path"] = path;
        report["revision"] = revision.Value;
        report["application_id"] = context.ApplicationId;
        return new CheckpointOutcome(true, report, null);
    }

    private async Task<JsonObject> RestoreAsync(
        string parentOperationId, string documentId, string? copyPath, string? applicationId,
        int readOnlyNotRestored, CancellationToken cancellationToken)
    {
        var restore = new JsonObject
        {
            ["attempted"] = true,
            ["succeeded"] = false,
            ["copy_path"] = copyPath,
            ["document_id_after"] = null,
            ["revision_after"] = null,
            // How many components the checkpoint never protected: a read_only component still points at the
            // client's file, so restoring the copy does not roll it back.
            ["read_only_not_restored"] = readOnlyNotRestored,
            ["checks"] = new JsonArray(),
        };

        if (string.IsNullOrWhiteSpace(copyPath) || !File.Exists(copyPath))
        {
            restore["reason"] = "копии нет: восстанавливать нечего";
            return restore;
        }

        // The mutated document is CLOSED WITHOUT SAVING, so the client's own file keeps the state it had
        // before the scenario, and the checkpoint is reopened in its place.
        var closed = await _invoke("kompas_close_document", new JsonObject
        {
            ["document_id"] = documentId,
            ["dirty_policy"] = "discard",
            ["operation_id"] = ChildOperationId(parentOperationId, "restore-close"),
        }, cancellationToken).ConfigureAwait(false);

        if (closed.Status != OperationStatus.Succeeded)
        {
            restore["reason"] = "документ не закрыт, восстановление невозможно";
            restore["close_error"] = ErrorOf(closed)?.DeepClone();
            return restore;
        }

        // The open names the application the plan works in: it was read from the document's context
        // when the checkpoint was taken, and the field is mandatory in that tool's contract.
        var openArguments = new JsonObject
        {
            ["path"] = copyPath,
            ["access"] = "edit",
            ["operation_id"] = ChildOperationId(parentOperationId, "restore-open"),
        };
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            openArguments["application_id"] = applicationId;
        }

        var opened = await _invoke("kompas_open_document", openArguments, cancellationToken)
            .ConfigureAwait(false);

        if (opened.Status != OperationStatus.Succeeded)
        {
            restore["reason"] = "копия не открылась: документ закрыт, модель не восстановлена";
            restore["open_error"] = ErrorOf(opened)?.DeepClone();
            return restore;
        }

        var reopened = JsonScalars.ReadString(opened.Result?["document_id"])
            ?? JsonScalars.ReadString(opened.Result?["id"]);
        var revisionAfter = JsonScalars.ReadLong(opened.Result?["revision"]);
        restore["document_id_after"] = reopened;
        restore["revision_after"] = revisionAfter;
        restore["succeeded"] = reopened is not null;
        ((JsonArray)restore["checks"]!).Add(new JsonObject
        {
            ["name"] = "document_reopened_from_copy",
            ["passed"] = reopened is not null,
        });
        if (reopened is null)
        {
            restore["reason"] = "копия открыта, но идентификатор нового документа не прочитан";
        }

        return restore;
    }

    private string CheckpointPath(string parentOperationId, long revision) =>
        Path.Combine(_controlCopyDirectory, "control-copies", "scenario",
            $"{parentOperationId}-r{revision}.m3d");

    // ---- Child operation id ----

    /// <summary>The step's operation id, derived from the parent and the step id (RFC 4122 v5).</summary>
    /// <remarks>INVARIANT: the same parent and step id give the same UUID, so a repeated scenario
    /// replays each step's recorded outcome instead of applying it twice.
    /// History: docs/decisions/scenario.md#child-operation-id</remarks>
    internal static string ChildOperationId(string parentOperationId, string stepId)
    {
        var namespaceBytes = Guid.Parse(parentOperationId).ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes(stepId);
        var hash = SHA1.HashData(namespaceBytes.Concat(nameBytes).ToArray());
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }

    // ---- Answer shaping ----

    private static JsonObject StepReport(
        StepPlan step, string status, string? operationId, long? revisionBefore, long? revisionAfter,
        JsonNode? result, JsonObject? error, bool truncated = false) => new()
    {
        ["step_id"] = step.StepId,
        ["tool"] = step.Tool.Name,
        ["status"] = status,
        ["operation_id"] = operationId,
        ["revision_before"] = revisionBefore,
        ["revision_after"] = revisionAfter,
        ["result"] = result,
        ["result_truncated"] = truncated,
        ["error"] = error,
    };

    /// <summary>A bounded view of a step result: diagnostics dropped, long lists and strings cut.</summary>
    /// <remarks>LIMIT: the answer carries at most 5 array items and 300 characters per string, because a
    /// full result of a read can be megabytes. History: docs/decisions/scenario.md#result-excerpt</remarks>
    internal static JsonNode? Compact(JsonNode? node, out bool truncated)
    {
        truncated = false;
        return CompactCore(node, 0, ref truncated);
    }

    private static JsonNode? CompactCore(JsonNode? node, int depth, ref bool truncated)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj)
                {
                    if (pair.Key == "diagnostics")
                    {
                        truncated = true;
                        continue;
                    }

                    copy[pair.Key] = CompactCore(pair.Value, depth + 1, ref truncated);
                }

                return copy;
            case JsonArray array:
                var kept = new JsonArray();
                for (var i = 0; i < array.Count && i < 5; i++)
                {
                    kept.Add(CompactCore(array[i], depth + 1, ref truncated));
                }

                if (array.Count > 5)
                {
                    truncated = true;
                }

                return kept;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > 300:
                truncated = true;
                return JsonValue.Create(text[..300]);
            default:
                return node.DeepClone();
        }
    }

    private static JsonObject? ErrorOf(ResultEnvelope<JsonNode?> envelope) => envelope.Error is null
        ? null
        : new JsonObject
        {
            ["code"] = envelope.Error.Code,
            ["message"] = envelope.Error.Message,
            ["partial_effects"] = envelope.Error.PartialEffects,
            ["details"] = envelope.Error.Details?.DeepClone(),
        };

    private static ResultEnvelope<JsonNode?> Refuse(string message, string field, string reason) => new()
    {
        Status = OperationStatus.Failed,
        Verification = new VerificationDto(
            VerificationLevel.ArgumentValidated, new[] { new NamedCheck("scenario_plan", false) }, Array.Empty<string>()),
        Error = new ErrorDto(
            ErrorCodes.InvalidArgument,
            message,
            RetryPolicy.Never,
            null,
            false,
            new JsonObject { ["field"] = field, ["reason"] = reason }),
    };
}
