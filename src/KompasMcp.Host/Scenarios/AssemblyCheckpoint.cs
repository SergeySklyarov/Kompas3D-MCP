using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Contracts.Schema;
using KompasMcp.Domain.Files;

namespace KompasMcp.Host.Scenarios;

/// <summary>Outcome of an assembly checkpoint: the report for the scenario answer, or the NAMED refusal.</summary>
internal sealed record AssemblyCheckpointOutcome(bool Ok, JsonObject Report, JsonObject? Error);

/// <summary>Takes a checkpoint of an assembly by copying its whole dependency graph and reassigning links
/// inside the copies.</summary>
/// <remarks>INVARIANT: the client's files are never written — components are copied, and only the copies are
/// opened, reassigned and saved. INVARIANT: nothing is written before the plan is built, so every refusal
/// leaves no folder behind. LIMIT: a drawing has no graph and is refused, as before.
/// DOC: kspart_filename.html (the component's file name is the <c>ksPart.fileName</c> property the
/// replace route writes), kspart_setfilename.html (notes: «Метод используется для компонентов, вставленных
/// в сборку»; «Компонент не должен быть деталью из библиотеки моделей или стандартным элементом»),
/// ipart7_saveas.html, ikompasdocument_close.html.
/// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
internal sealed class AssemblyCheckpoint
{
    /// <summary>How external components are referenced in the copy.</summary>
    internal const string ExternalPartsCopy = "copy";

    internal const string ExternalPartsReadOnly = "read_only";

    internal static readonly string[] ExternalPartsModes = { ExternalPartsCopy, ExternalPartsReadOnly };

    private readonly string _controlCopyDirectory;
    private readonly Func<string, JsonObject, CancellationToken, Task<ResultEnvelope<JsonNode?>>> _invoke;

    internal AssemblyCheckpoint(
        string controlCopyDirectory,
        Func<string, JsonObject, CancellationToken, Task<ResultEnvelope<JsonNode?>>> invoke)
    {
        _controlCopyDirectory = controlCopyDirectory;
        _invoke = invoke;
    }

    /// <summary>The folder a checkpoint of this document and revision is written to.</summary>
    internal string FolderFor(string parentOperationId, long revision) => Path.Combine(
        _controlCopyDirectory, "control-copies", "scenario", $"{parentOperationId}-r{revision}");

    internal async Task<AssemblyCheckpointOutcome> TakeAsync(
        string parentOperationId, string documentId, ResultEnvelope<JsonNode?> context,
        string externalParts, CancellationToken cancellationToken)
    {
        var document = context.Result as JsonObject ?? new JsonObject();
        var report = new JsonObject
        {
            ["policy"] = "document_copy",
            ["taken"] = false,
            ["path"] = null,
            ["kind"] = "assembly",
            ["external_parts"] = externalParts,
        };

        var revision = JsonScalars.ReadLong(document["revision"]);
        var sourcePath = JsonScalars.ReadString(document["path"]);
        if (revision is null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return Refuse(report, "revision_not_read", "Ревизия или путь сборки не прочитаны: контрольная "
                + "точка не снята.", Array.Empty<string>());
        }

        // INVARIANT: every open of a copy names the application — the field is mandatory in that contract,
        // and an empty id makes the open fail for a missing field. It is read from the envelope first, then
        // from the body, and finally from the document list.
        // DOC: ksdocument3d_open.html.
        var applicationId = context.ApplicationId
            ?? JsonScalars.ReadString(document["application_id"]);
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            var listed = await _invoke("kompas_list_documents", new JsonObject(), cancellationToken)
                .ConfigureAwait(false);
            applicationId = (listed.Result as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(row => JsonScalars.ReadString(row["id"]) == documentId
                    || JsonScalars.ReadString(row["document_id"]) == documentId)
                .Select(row => JsonScalars.ReadString(row["application_id"]))
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        }
        var folder = FolderFor(parentOperationId, revision.Value);
        var graph = await ReadGraphAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (!graph.Ok)
        {
            return Refuse(report, "graph_not_read", graph.Message, Array.Empty<string>());
        }

        var plan = AssemblyCopyPlan.Build(graph.Links, folder, externalParts);
        if (!plan.Ok)
        {
            return Refuse(report, plan.Refusal!.Reason, plan.Refusal.Message, plan.Refusal.Files);
        }

        // INVARIANT: the volume is checked BEFORE the first write, so a copy that cannot fit is a refusal
        // and not a folder left half-written next to the client's assembly.
        var (fits, neededBytes, availableBytes) = AssemblyCopyPlan.Space(plan.Entries, folder);
        if (!fits)
        {
            return Refuse(report, "disk_space",
                $"На томе каталога копии не хватает места: нужно {neededBytes} байт (с запасом "
                + $"{AssemblyCopyPlan.DiskReserveBytes}), свободно {availableBytes}. Контрольная точка не "
                + "снимается.", Array.Empty<string>());
        }

        var dirty = await DirtySourcesAsync(plan.Entries, applicationId, cancellationToken)
            .ConfigureAwait(false);
        if (dirty.Count > 0)
        {
            return Refuse(report, "component_dirty",
                "У компонента есть несохранённые изменения: "
                + string.Join(", ", dirty.Take(5))
                + ". Копия с диска их не содержит, поэтому контрольная точка не снимается: сохраните "
                + "или закройте эти документы.",
                dirty);
        }

        // ---- Everything above changed nothing; the first write is the folder below. ----
        var created = false;
        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                created = true;
            }

            Directory.CreateDirectory(Path.Combine(folder, "files"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refuse(report, "copy_folder_unavailable",
                $"Каталог копии недоступен: {ex.GetType().Name}: {ex.Message}", Array.Empty<string>());
        }

        try
        {
            foreach (var entry in plan.Entries)
            {
                if (entry.CopyPath is not null)
                {
                    File.Copy(entry.SourcePath, entry.CopyPath, overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_written",
                $"Файлы графа не скопированы: {ex.GetType().Name}: {ex.Message}", Array.Empty<string>());
        }

        // A nested assembly is reassigned BEFORE the assembly that inserts it, so the parent never points at
        // a file whose own links still lead to the client's originals.
        var failures = new List<string>();
        foreach (var nested in AssemblyCopyPlan.BottomUp(plan.Entries))
        {
            var reassigned = await ReassignAsync(
                parentOperationId, nested.CopyPath!, plan.Entries, externalParts, applicationId,
                cancellationToken)
                .ConfigureAwait(false);
            if (!reassigned.Ok)
            {
                failures.Add($"{Path.GetFileName(nested.SourcePath)}: {reassigned.Message}");
            }
        }

        if (failures.Count > 0)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_written", "Ссылки вложенных сборок не переназначены: "
                + string.Join("; ", failures), failures);
        }

        // INVARIANT: the whole route — open a copy, reassign it, save it, read the links back from the FILE —
        // is proven on a copy of the top file taken from disk BEFORE the live document is moved. A route that
        // fails here refuses while the client's document is still its own file.
        var topCopy = Path.Combine(folder, Path.GetFileName(sourcePath));
        var preflight = Path.Combine(folder, "preflight");
        var preflightTop = Path.Combine(preflight, Path.GetFileName(sourcePath));
        try
        {
            Directory.CreateDirectory(preflight);
            File.Copy(sourcePath, preflightTop, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "top_file_not_copied",
                $"Верхний файл сборки не скопирован для предварительного прогона: {ex.GetType().Name}: "
                + ex.Message, new[] { sourcePath });
        }

        var preflightTopReassigned = await ReassignAsync(
            parentOperationId, preflightTop, plan.Entries, externalParts, applicationId, cancellationToken)
            .ConfigureAwait(false);
        var preflightVerified = preflightTopReassigned.Ok
            ? await VerifyAsync(
                    parentOperationId, preflightTop, plan.Entries, graph.ComponentCount, applicationId,
                    folder, "preflight", cancellationToken)
                .ConfigureAwait(false)
            : new VerifyResult(new[] { preflightTopReassigned.Message });

        DeleteFolder(preflight);

        if (preflightVerified.Mismatches.Count > 0)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_verified",
                "Предварительный прогон копии не сошёлся с графом, живой документ не тронут: "
                + string.Join("; ", preflightVerified.Mismatches),
                preflightVerified.Mismatches);
        }

        // The top file goes through the SAME route as a detail: SaveAs, so unsaved edits of the top assembly
        // are inside the copy. The live document becomes the copy; the client's own file is not written.
        var saved = await _invoke("kompas_save_document", new JsonObject
        {
            ["document_id"] = documentId,
            ["expected_revision"] = revision.Value,
            ["target_path"] = topCopy,
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "checkpoint-save"),
        }, cancellationToken).ConfigureAwait(false);

        if (saved.Status != OperationStatus.Succeeded)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_written", "Копия верхней сборки не снята: документ не сохранён "
                + "документированным SaveAs.", Array.Empty<string>());
        }

        // ---- From here the live document IS the copy: a refusal must not delete the folder under it. ----
        // SaveAs leaves the copy under a NEW id: the plan's own id would address nothing afterwards. The id
        // is taken from the session's document list BY PATH, because the save result names the id it wrote
        // only when the session re-registered the document, and a guess here costs every later step.
        var sessionRows = await ListDocumentsAsync(applicationId, cancellationToken).ConfigureAwait(false);
        var copyDocumentId = sessionRows
            .Where(row => string.Equals(JsonScalars.ReadString(row["path"]), topCopy,
                StringComparison.OrdinalIgnoreCase))
            .Select(row => JsonScalars.ReadString(row["id"]))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            ?? JsonScalars.ReadString(saved.Result?["context"]?["id"])
            ?? documentId;
        var revisionNow = saved.RevisionAfter ?? revision.Value;
        var moved = new JsonObject
        {
            ["document_id_after"] = copyDocumentId,
            ["path"] = topCopy,
            ["notes"] = "Документ теперь открыт из копии в каталоге контрольной точки: правки идут в копию, "
                + "файл клиента не изменён.",
        };

        var topReassigned = await ReassignDocumentAsync(
            parentOperationId, copyDocumentId, revisionNow, plan.Entries, externalParts, cancellationToken)
            .ConfigureAwait(false);
        if (!topReassigned.Ok)
        {
            return Refuse(report, "top_refs_not_reassigned",
                $"Ссылки верхней сборки не переназначены: {topReassigned.Message}", Array.Empty<string>(),
                moved);
        }

        if (topReassigned.Revision is { } afterReassign && afterReassign != revisionNow)
        {
            var resaved = await _invoke("kompas_save_document", new JsonObject
            {
                ["document_id"] = copyDocumentId,
                ["expected_revision"] = afterReassign,
                ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "checkpoint-resave"),
            }, cancellationToken).ConfigureAwait(false);
            if (resaved.Status != OperationStatus.Succeeded)
            {
                return Refuse(report, "copy_not_saved",
                    "Копия с переназначенными ссылками не сохранена.", Array.Empty<string>(), moved);
            }
        }

        // INVARIANT: verification reads the FILE, so the live document of the copy is closed first. While it
        // was open, the open-by-path returned the very document in memory and the check proved nothing.
        var closed = await _invoke("kompas_close_document", new JsonObject
        {
            ["document_id"] = copyDocumentId,
            ["dirty_policy"] = "discard",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "checkpoint-close"),
        }, cancellationToken).ConfigureAwait(false);
        if (closed.Status != OperationStatus.Succeeded)
        {
            var cause = closed.Error is null ? string.Empty
                : $": {closed.Error.Code}: {closed.Error.Message}";
            return Refuse(report, "copy_not_closed",
                "Копия снята, но закрыть её документ перед проверкой не удалось" + cause,
                Array.Empty<string>(), moved);
        }

        var verification = await VerifyAsync(
            parentOperationId, topCopy, plan.Entries, graph.ComponentCount, applicationId, folder, "copy",
            cancellationToken)
            .ConfigureAwait(false);

        // The plan continues on the copy: the verification document is closed without saving, so the copy is
        // reopened for edit. A refusal here names the state instead of reporting a checkpoint that is not one.
        var reopened = await OpenForPlanAsync(
            parentOperationId, topCopy, applicationId, cancellationToken).ConfigureAwait(false);
        if (reopened is null)
        {
            return Refuse(report, "reopen_failed",
                "Копия снята, но заново открыть её для плана не удалось: документ закрыт, шаги не выполнялись.",
                Array.Empty<string>(), moved);
        }

        // The manifest names every file of the graph; a read_only row has no copy path, so a reader cannot
        // mistake the client's own file for a protected copy.
        var manifest = AssemblyCopyPlan.Manifest(
            sourcePath, externalParts, DateTimeOffset.UtcNow, plan.Entries,
            verified: verification.Mismatches.Count == 0, verification.Mismatches);
        var manifestPath = Path.Combine(folder, "manifest.json");
        var manifestWritten = TryWriteAtomic(manifestPath, manifest, out var manifestReason);

        if (verification.Mismatches.Count > 0)
        {
            return Refuse(report, "copy_not_verified",
                "Ссылки в копии не совпали с графом: " + string.Join("; ", verification.Mismatches),
                verification.Mismatches, moved);
        }

        if (!manifestWritten)
        {
            return Refuse(report, "manifest_not_written", $"Manifest не записан: {manifestReason}",
                Array.Empty<string>(), moved);
        }

        report["taken"] = true;
        report["path"] = topCopy;
        report["document_path"] = topCopy;
        report["document_id_after"] = reopened;
        report["revision"] = revision.Value;
        report["application_id"] = applicationId;
        report["manifest_path"] = manifestPath;
        report["file_count"] = plan.Entries.Count;
        report["copied"] = plan.Entries.Count(e => e.Mode == AssemblyCopyPlan.ModeCopy);
        report["read_only"] = plan.Entries.Count(e => e.Mode == AssemblyCopyPlan.ModeReadOnly);
        report["verified"] = true;
        report["read_only_not_restored"] = plan.Entries.Count(e => e.Mode == AssemblyCopyPlan.ModeReadOnly);
        report["notes"] = externalParts == ExternalPartsReadOnly
            ? "external_parts=read_only: компоненты не копировались, ссылки копии ведут на исходные файлы, "
                + "и контрольная точка их не защищает. Правки идут в копию верхней сборки, файл клиента не "
                + "изменён."
            : "Правки идут в копию верхней сборки в каталоге контрольной точки, файл клиента не изменён.";

        return new AssemblyCheckpointOutcome(true, report, null);
    }

    /// <summary>Open the copy for edit and read its id, or <c>null</c> when the open was refused.</summary>
    private async Task<string?> OpenForPlanAsync(
        string parentOperationId, string path, string? applicationId, CancellationToken cancellationToken)
    {
        var arguments = new JsonObject
        {
            ["path"] = path,
            ["access"] = "edit",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "checkpoint-reopen"),
        };
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            arguments["application_id"] = applicationId;
        }

        var opened = await _invoke("kompas_open_document", arguments, cancellationToken)
            .ConfigureAwait(false);
        if (opened.Status != OperationStatus.Succeeded)
        {
            return null;
        }

        return JsonScalars.ReadString(opened.Result?["document_id"])
            ?? JsonScalars.ReadString(opened.Result?["id"]);
    }

    // ---- Graph ----

    /// <summary>The session's document contexts, as the documented list command answers them.</summary>
    private async Task<JsonObject[]> ListDocumentsAsync(string? applicationId, CancellationToken cancellationToken)
    {
        // DOC: kompas_list_documents answers with an ARRAY of document contexts and filters by
        // application_id, so the id is passed here rather than read from the answer.
        var arguments = new JsonObject();
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            arguments["application_id"] = applicationId;
        }

        var listed = await _invoke("kompas_list_documents", arguments, cancellationToken)
            .ConfigureAwait(false);
        return (listed.Result as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
    }

    private sealed record GraphRead(bool Ok, string Message, IReadOnlyList<AssemblyComponentLink> Links, int ComponentCount);

    private async Task<GraphRead> ReadGraphAsync(string documentId, CancellationToken cancellationToken)
    {
        var listed = await _invoke("kompas_list_components", new JsonObject
        {
            ["document_id"] = documentId,
            ["recursive"] = true,
        }, cancellationToken).ConfigureAwait(false);

        if (listed.Status != OperationStatus.Succeeded)
        {
            return new GraphRead(false, "Структура сборки не прочитана: граф зависимостей неизвестен.",
                Array.Empty<AssemblyComponentLink>(), 0);
        }

        var rows = (listed.Result?["components"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .ToArray();
        var byRef = rows
            .Where(row => JsonScalars.ReadString(row["component_ref"]) is not null)
            .ToDictionary(
                row => JsonScalars.ReadString(row["component_ref"])!,
                row => JsonScalars.ReadString(row["source_path"]),
                StringComparer.Ordinal);

        var links = rows.Select(row => new AssemblyComponentLink(
                JsonScalars.ReadString(row["source_path"]) ?? string.Empty,
                JsonScalars.ReadString(row["parent_ref"]) is { } parent && byRef.TryGetValue(parent, out var parentPath)
                    ? parentPath
                    : null,
                IsAssembly: row["is_detail"]?.GetValue<bool>() == false,
                Insertions: JsonScalars.ReadInt(row["instance_count"]) ?? 1,
                Depth: JsonScalars.ReadInt(row["depth"]) ?? 0))
            .ToArray();

        return new GraphRead(true, string.Empty, links, rows.Length);
    }

    /// <summary>Source files whose open document reports unsaved changes.</summary>
    /// <remarks>INVARIANT: a copy is taken from the FILE, so an unsaved edit of a component would be absent
    /// from the copy and the restore would silently roll the client back further than the scenario.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private async Task<IReadOnlyList<string>> DirtySourcesAsync(
        IReadOnlyList<AssemblyCopyEntry> entries, string? applicationId, CancellationToken cancellationToken)
    {
        var sources = entries.Select(entry => entry.SourcePath).ToArray();
        var documents = await ListDocumentsAsync(applicationId, cancellationToken).ConfigureAwait(false);
        var dirty = new List<string>();
        foreach (var row in documents)
        {
            var path = JsonScalars.ReadString(row["path"]);
            if (path is null || !sources.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = JsonScalars.ReadString(row["id"]);
            if (id is null)
            {
                continue;
            }

            var context = await _invoke("kompas_get_context", new JsonObject
            {
                ["document_id"] = id,
                ["detail"] = "minimal",
            }, cancellationToken).ConfigureAwait(false);
            if (context.Result?["dirty"]?.GetValue<bool>() == true)
            {
                dirty.Add(path);
            }
        }

        return dirty;
    }

    // ---- Reassignment inside the copies ----

    private sealed record ReassignResult(bool Ok, string Message, long? Revision, int? Components = null);

    /// <summary>Open a copied subassembly, point its components at the copies, save and close it.</summary>
    /// <remarks>INVARIANT: the copy is closed after saving, so a client's own open document is the only one
    /// left open; a copy that fails to close is named, not ignored.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private async Task<ReassignResult> ReassignAsync(
        string parentOperationId, string copyPath, IReadOnlyList<AssemblyCopyEntry> entries,
        string externalParts, string? applicationId, CancellationToken cancellationToken)
    {
        var openArguments = new JsonObject
        {
            ["path"] = copyPath,
            ["access"] = "edit",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "copy-open-" + StableKey(copyPath)),
        };
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            openArguments["application_id"] = applicationId;
        }

        var opened = await _invoke("kompas_open_document", openArguments, cancellationToken)
            .ConfigureAwait(false);

        if (opened.Status != OperationStatus.Succeeded)
        {
            // The underlying refusal is repeated in the message: "the copy did not open" alone names no
            // cause, and the cause is what the caller has to act on.
            var cause = opened.Error is null ? string.Empty
                : $": {opened.Error.Code}: {opened.Error.Message}";
            return new ReassignResult(false, $"копия вложенной сборки не открылась{cause}", null);
        }

        var copyDocumentId = JsonScalars.ReadString(opened.Result?["document_id"])
            ?? JsonScalars.ReadString(opened.Result?["id"]);
        if (string.IsNullOrWhiteSpace(copyDocumentId))
        {
            return new ReassignResult(false, "идентификатор открытой копии не прочитан", null);
        }

        var reassigned = await ReassignDocumentAsync(
            parentOperationId, copyDocumentId, opened.RevisionAfter, entries, externalParts, cancellationToken)
            .ConfigureAwait(false);

        var saved = reassigned.Ok && reassigned.Revision is { } revision
            ? await _invoke("kompas_save_document", new JsonObject
            {
                ["document_id"] = copyDocumentId,
                ["expected_revision"] = revision,
                ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "copy-save-" + StableKey(copyPath)),
            }, cancellationToken).ConfigureAwait(false)
            : null;

        // INVARIANT: the copy still holds the components it was planned with AFTER the save. A save that
        // loses instances leaves a file that is not a copy of the assembly, and the check has to name it
        // here — the reopen would only show the smaller count with no cause.
        var afterSave = reassigned.Ok
            ? await CountComponentsAsync(copyDocumentId, cancellationToken).ConfigureAwait(false)
            : null;

        await _invoke("kompas_close_document", new JsonObject
        {
            ["document_id"] = copyDocumentId,
            ["dirty_policy"] = "save",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "copy-close-" + StableKey(copyPath)),
        }, cancellationToken).ConfigureAwait(false);

        if (!reassigned.Ok)
        {
            return reassigned;
        }

        if (saved is not null && saved.Status != OperationStatus.Succeeded)
        {
            return new ReassignResult(false, "копия вложенной сборки не сохранена после переназначения", null);
        }

        if (afterSave is { } left && reassigned.Components is { } planned && left != planned)
        {
            return new ReassignResult(false,
                $"после сохранения копии «{Path.GetFileName(copyPath)}» в ней {left} компонентов вместо "
                + $"{planned}", null);
        }

        return new ReassignResult(true, string.Empty, saved?.RevisionAfter ?? reassigned.Revision);
    }

    /// <summary>How many components the document reports, or <c>null</c> when the read failed.</summary>
    private async Task<int?> CountComponentsAsync(string documentId, CancellationToken cancellationToken)
    {
        var listed = await _invoke("kompas_list_components", new JsonObject
        {
            ["document_id"] = documentId,
        }, cancellationToken).ConfigureAwait(false);
        return listed.Status == OperationStatus.Succeeded
            ? (listed.Result?["components"] as JsonArray ?? new JsonArray()).Count
            : null;
    }

    /// <summary>Point every component of one document at its copy, by the documented source replacement.</summary>
    /// <remarks>DOC: kspart_setfilename.html — «Метод используется для компонентов, вставленных в сборку»;
    /// «Компонент не должен быть деталью из библиотеки моделей или стандартным элементом». A component the
    /// route refuses is NAMED with the code and message the route returned, and the checkpoint is refused
    /// rather than left half-reassigned.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private async Task<ReassignResult> ReassignDocumentAsync(
        string parentOperationId, string documentId, long? revision,
        IReadOnlyList<AssemblyCopyEntry> entries, string externalParts, CancellationToken cancellationToken)
    {
        if (externalParts == ExternalPartsReadOnly)
        {
            // read_only keeps the links on the client's files on purpose: nothing to reassign.
            return new ReassignResult(true, string.Empty, revision);
        }

        var copyBySource = entries
            .Where(entry => entry.CopyPath is not null)
            .ToDictionary(entry => entry.SourcePath, entry => entry.CopyPath!, StringComparer.OrdinalIgnoreCase);

        var current = revision;
        var planned = (int?)null;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // INVARIANT: the component list is re-read before EVERY replacement. The replace route bumps the
        // revision and invalidates every ref of the document, so a list read once is stale after the first
        // success — a stale ref is refused, and the copy would keep the client's file in every later link.
        for (var pass = 0; pass <= copyBySource.Count; pass++)
        {
            var listed = await _invoke("kompas_list_components", new JsonObject
            {
                ["document_id"] = documentId,
            }, cancellationToken).ConfigureAwait(false);
            if (listed.Status != OperationStatus.Succeeded)
            {
                return new ReassignResult(false, "компоненты копии не прочитаны", current);
            }

            var rows = (listed.Result?["components"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .ToArray();
            planned ??= rows.Length;
            var row = rows.FirstOrDefault(candidate =>
            {
                var path = JsonScalars.ReadString(candidate["source_path"]);
                return path is not null && !done.Contains(path) && copyBySource.ContainsKey(path);
            });
            if (row is null)
            {
                return new ReassignResult(true, string.Empty, current, planned);
            }

            var source = JsonScalars.ReadString(row["source_path"])!;
            var componentRef = JsonScalars.ReadString(row["component_ref"]);
            if (componentRef is null || current is null)
            {
                return new ReassignResult(false, "ссылка или ревизия копии не прочитаны перед переназначением",
                    null);
            }

            var replaced = await _invoke("kompas_replace_component", new JsonObject
            {
                ["document_id"] = documentId,
                ["expected_revision"] = current.Value,
                ["component_ref"] = componentRef,
                ["source_path"] = copyBySource[source],
                ["operation_id"] = ScenarioRunner.ChildOperationId(
                    parentOperationId, "copy-reassign-" + componentRef),
            }, cancellationToken).ConfigureAwait(false);

            if (replaced.Status != OperationStatus.Succeeded)
            {
                // The route's own code and message are passed on: a cause invented here would send the
                // caller after a library component even when the refusal was, say, a stale revision.
                var refusal = replaced.Error is null
                    ? "маршрут замены источника отказал без кода"
                    : $"{replaced.Error.Code}: {replaced.Error.Message}";
                return new ReassignResult(false,
                    $"компонент «{JsonScalars.ReadString(row["name"]) ?? source}» не переназначен: {refusal}",
                    current);
            }

            done.Add(source);
            current = replaced.RevisionAfter ?? current;

            // INVARIANT: a replace that changed the number of components has dropped or duplicated
            // instances, and the copy no longer matches the graph it was planned from. The route reports
            // this itself; it is refused here rather than left for the verification to notice.
            if (replaced.Verification?.Checks?.FirstOrDefault(check =>
                    check.Name == "component_count_unchanged" && !check.Passed) is { } dropped)
            {
                return new ReassignResult(false,
                    $"маршрут замены источника изменил число компонентов на «{source}»: "
                    + $"{dropped.Observed} (ждали {dropped.Expected})", current);
            }
        }

        return new ReassignResult(true, string.Empty, current);
    }

    // ---- Verification ----

    private sealed record VerifyResult(IReadOnlyList<string> Mismatches);

    /// <summary>Reopen the copy and re-read the links from the FILE, not from memory.</summary>
    /// <remarks>INVARIANT: the caller closes every open document of the copy first — an open-by-path while the
    /// same file is open returns that document from memory and proves nothing about the file. INVARIANT: the
    /// verification document is closed without saving, so checking cannot change what was checked.
    /// DOC: kompas_open_document.html (opening a document by path).</remarks>
    private async Task<VerifyResult> VerifyAsync(
        string parentOperationId, string topCopy, IReadOnlyList<AssemblyCopyEntry> entries,
        int expectedComponents, string? applicationId, string copyFolder, string stage,
        CancellationToken cancellationToken)
    {
        var openArguments = new JsonObject
        {
            ["path"] = topCopy,
            ["access"] = "read_only",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "verify-open-" + stage),
        };
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            openArguments["application_id"] = applicationId;
        }

        var opened = await _invoke("kompas_open_document", openArguments, cancellationToken)
            .ConfigureAwait(false);

        if (opened.Status != OperationStatus.Succeeded)
        {
            var cause = opened.Error is null ? string.Empty
                : $": {opened.Error.Code}: {opened.Error.Message}";
            return new VerifyResult(new[] { "копия верхней сборки не открылась для проверки" + cause });
        }

        var verifyDocumentId = JsonScalars.ReadString(opened.Result?["document_id"])
            ?? JsonScalars.ReadString(opened.Result?["id"]);
        if (string.IsNullOrWhiteSpace(verifyDocumentId))
        {
            return new VerifyResult(new[] { "идентификатор проверочного документа не прочитан" });
        }

        var graph = await ReadGraphAsync(verifyDocumentId, cancellationToken).ConfigureAwait(false);

        await _invoke("kompas_close_document", new JsonObject
        {
            ["document_id"] = verifyDocumentId,
            ["dirty_policy"] = "discard",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "verify-close-" + stage),
        }, cancellationToken).ConfigureAwait(false);

        if (!graph.Ok)
        {
            return new VerifyResult(new[] { graph.Message });
        }

        var (passed, mismatches) = AssemblyCopyPlan.Verify(entries, copyFolder, graph.Links, expectedComponents);
        return new VerifyResult(passed ? Array.Empty<string>() : mismatches);
    }

    // ---- Helpers ----

    private static bool TryWriteAtomic(string path, JsonObject manifest, out string? reason)
    {
        var temporary = AtomicPublish.TemporarySibling(path);
        try
        {
            File.WriteAllText(temporary, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            AtomicPublish.WriteFrom(temporary, path, allowOverwrite: true);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or KompasContractException)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string StableKey(string path) =>
        FileHash.Sha256(System.Text.Encoding.UTF8.GetBytes(path)).Substring(0, 12);

    private static void DeleteFolderIfCreated(string folder, bool created)
    {
        if (!created)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A folder that cannot be deleted is left with its own name; the refusal still says the
            // checkpoint was not taken, and the path is visible to the client.
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A folder that cannot be deleted keeps its own name; the answer still names the state.
        }
    }

    /// <summary>Refuse the checkpoint, naming the cause and the files, and the state when the live document
    /// already points into the copy.</summary>
    /// <remarks>INVARIANT: the reason code is one for every refusal (<c>copy_not_written</c>) and the specific
    /// cause travels in <c>details.cause</c>, so a client reads the same field whatever failed.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private static AssemblyCheckpointOutcome Refuse(
        JsonObject report, string cause, string message, IReadOnlyList<string> files, JsonObject? state = null)
    {
        if (state is not null)
        {
            foreach (var pair in state)
            {
                report[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return new AssemblyCheckpointOutcome(
            false,
            report,
            new JsonObject
            {
                ["code"] = "CHECKPOINT_FAILED",
                ["message"] = message,
                ["reason"] = "copy_not_written",
                ["details"] = new JsonObject
                {
                    ["field"] = "checkpoint_policy",
                    ["cause"] = cause,
                    ["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
                },
            });
    }
}
