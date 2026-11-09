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

    /// <summary>The state key that says the client's live document is now the copy.</summary>
    /// <remarks>INVARIANT: a refused checkpoint that moved the document must name that effect where the
    /// client reads effects, so the answer carries this flag and the scenario copies it into
    /// <c>partial_effects</c>. History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    internal const string DocumentMoved = "document_moved";

    /// <summary>Upper bound on documented saves of one copy file until its temporary sibling is gone.</summary>
    /// <remarks>LIMIT: 3 saves; a copy whose file is still not replaced after them is refused with the
    /// per-attempt evidence. History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    internal const int SaveAttempts = 3;

    /// <summary>Upper bound on reads of the copy file before the verdict — a reading is not a state.</summary>
    /// <remarks>LIMIT: 2 reads; a copy whose links disagree on every read is refused and both readings are
    /// named. History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    internal const int VerifyReads = 2;

    private readonly string _controlCopyDirectory;
    private readonly Func<string, JsonObject, CancellationToken, Task<ResultEnvelope<JsonNode?>>> _invoke;

    /// <summary>The same entry, admitting ONE copy file outside the client's roots.</summary>
    /// <remarks>INVARIANT: the grant is the file this call reads or writes, computed here — a client
    /// argument never reaches it. History: docs/decisions/scenario.md#checkpoint-service-path</remarks>
    private readonly Func<string, JsonObject, string, CancellationToken, Task<ResultEnvelope<JsonNode?>>> _invokeCopy;

    internal AssemblyCheckpoint(
        string controlCopyDirectory,
        Func<string, JsonObject, string?, CancellationToken, Task<ResultEnvelope<JsonNode?>>> invoke)
    {
        _controlCopyDirectory = controlCopyDirectory;
        _invoke = (tool, arguments, token) => invoke(tool, arguments, null, token);
        _invokeCopy = (tool, arguments, copyPath, token) => invoke(tool, arguments, copyPath, token);
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
            return Refuse(report, "graph_not_read", graph.Message, Array.Empty<string>(),
                stepError: graph.Error);
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
                $"Каталог копии недоступен: {folder}: {ex.GetType().Name}: {ex.Message}", Array.Empty<string>());
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
        JsonObject? failureError = null;
        foreach (var nested in AssemblyCopyPlan.BottomUp(plan.Entries))
        {
            var reassigned = await ReassignAsync(
                parentOperationId, nested.CopyPath!, plan.Entries, externalParts, applicationId,
                cancellationToken)
                .ConfigureAwait(false);
            if (!reassigned.Ok)
            {
                failures.Add($"{Path.GetFileName(nested.SourcePath)}: {reassigned.Message}");
                failureError ??= reassigned.Error;
            }
        }

        if (failures.Count > 0)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_written", "Ссылки вложенных сборок не переназначены: "
                + string.Join("; ", failures), failures, stepError: failureError);
        }

        // MEASURED: a byte copy of the top file reads back ONE component per file NAME when the graph
        // holds DIFFERENT files under that name; one file inserted twice reads as its insert count, and
        // whether the client's document is open changes nothing.
        // The plan renames copies with a number, so the copy verified below carries no colliding names:
        // the route is proven on the copy КОМПАС writes (the SaveAs below), read from the FILE.
        // LIMIT: a failed verification here leaves the folder and the moved document, and the refusal
        // names that state; nothing is written to the client's files.
        var topCopy = Path.Combine(folder, Path.GetFileName(sourcePath));

        // The top file goes through the SAME route as a detail: SaveAs, so unsaved edits of the top assembly
        // are inside the copy. The live document becomes the copy; the client's own file is not written.
        var topSave = await SaveCopyAsync(
            parentOperationId, "checkpoint-save", documentId, revision.Value, topCopy, topCopy,
            cancellationToken).ConfigureAwait(false);
        var saved = topSave.Envelope;

        if (saved.Status != OperationStatus.Succeeded)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_written", "Копия верхней сборки не снята: документ не сохранён "
                + "документированным SaveAs.", Array.Empty<string>(), stepError: ErrorOf(saved),
                evidence: topSave.Evidence());
        }

        if (!topSave.Settled)
        {
            // The live document already IS the copy, so its folder is NOT deleted under it.
            return Refuse(report, "copy_not_saved",
                $"Копия верхней сборки не дописана: содержимое модели осталось во временном файле "
                + $"'{topCopy}{SavedFileWrite.TemporarySuffix}', файл копии не заменён. Файл клиента не изменён.",
                Array.Empty<string>(),
                new JsonObject
                {
                    ["path"] = topCopy,
                    [DocumentMoved] = true,
                    ["notes"] = "Документ открыт из копии, файл копии не дописан, файл клиента не изменён.",
                },
                evidence: topSave.Evidence());
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
        var revisionNow = topSave.Revision ?? revision.Value;
        var moved = new JsonObject
        {
            ["document_id_after"] = copyDocumentId,
            ["path"] = topCopy,
            [DocumentMoved] = true,
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
                moved, stepError: topReassigned.Error, evidence: ReassignEvidence(topReassigned));
        }

        if (topReassigned.Revision is { } afterReassign && afterReassign != revisionNow)
        {
            var resaved = await SaveCopyAsync(
                parentOperationId, "checkpoint-resave", copyDocumentId, afterReassign, topCopy, null,
                cancellationToken).ConfigureAwait(false);
            if (resaved.Envelope.Status != OperationStatus.Succeeded)
            {
                return Refuse(report, "copy_not_saved",
                    "Копия с переназначенными ссылками не сохранена.", Array.Empty<string>(), moved,
                    stepError: ErrorOf(resaved.Envelope), evidence: resaved.Evidence());
            }

            if (!resaved.Settled)
            {
                // The reassigned links live only in the model: closing the copy now would read the file as it
                // was BEFORE the reassignment, and the verdict would describe that older state.
                return Refuse(report, "copy_not_saved",
                    $"Копия с переназначенными ссылками не дописана: содержимое модели осталось во временном "
                    + $"файле '{topCopy}{SavedFileWrite.TemporarySuffix}', файл копии не заменён. Файл клиента "
                    + "не изменён.",
                    Array.Empty<string>(), moved, evidence: resaved.Evidence());
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
            // INVARIANT: the state names an id only when that document is open. A failed close usually
            // leaves the document open, but the answer is read from the session's list, not assumed.
            var stillOpen = (await ListDocumentsAsync(applicationId, cancellationToken).ConfigureAwait(false))
                .Any(row => string.Equals(JsonScalars.ReadString(row["id"]), copyDocumentId,
                    StringComparison.Ordinal));
            var closeState = new JsonObject
            {
                ["path"] = topCopy,
                ["notes"] = stillOpen
                    ? "Документ копии остался открыт: правки идут в копию, файл клиента не изменён."
                    : "Документ копии закрыт, шаги не выполнялись: копию нужно открыть заново.",
            };
            if (stillOpen)
            {
                closeState["document_id_after"] = copyDocumentId;
            }

            return Refuse(report, "copy_not_closed",
                "Копия снята, но закрыть её документ перед проверкой не удалось" + cause,
                Array.Empty<string>(), closeState, stepError: ErrorOf(closed));
        }

        var verification = await VerifyAsync(
            parentOperationId, topCopy, plan.Entries, graph.ComponentCount, applicationId, folder, "copy",
            cancellationToken)
            .ConfigureAwait(false);

        // The plan continues on the copy: the verification document is closed without saving, so the copy is
        // reopened for edit. A refusal here names the state instead of reporting a checkpoint that is not one.
        var reopened = await OpenForPlanAsync(
            parentOperationId, topCopy, applicationId, cancellationToken).ConfigureAwait(false);
        if (reopened.DocumentId is null)
        {
            // INVARIANT: the copy is closed at this point, so no id of it may be offered as a live document.
            var closedState = new JsonObject
            {
                ["path"] = topCopy,
                ["notes"] = "Копия снята, но её документ закрыт: шаги не выполнялись, копию нужно открыть "
                    + "заново.",
            };
            return Refuse(report, "reopen_failed",
                "Копия снята, но заново открыть её для плана не удалось: документ закрыт, шаги не выполнялись.",
                Array.Empty<string>(), closedState, stepError: reopened.Error);
        }

        // The plan continues on the REOPENED document, so every later refusal names its id — the id from
        // before the close addresses nothing.
        moved["document_id_after"] = reopened.DocumentId;
        moved["notes"] = "Документ теперь открыт из копии в каталоге контрольной точки: правки идут в копию, "
            + "файл клиента не изменён.";

        // The manifest names every file of the graph; a read_only row has no copy path, so a reader cannot
        // mistake the client's own file for a protected copy.
        var manifest = AssemblyCopyPlan.Manifest(
            sourcePath, externalParts, DateTimeOffset.UtcNow, plan.Entries,
            verified: verification.Mismatches.Count == 0, verification.Mismatches);
        var manifestPath = Path.Combine(folder, "manifest.json");
        var manifestWritten = TryWriteAtomic(manifestPath, manifest, out var manifestReason);

        if (verification.Mismatches.Count > 0)
        {
            var evidence = ReassignEvidence(topReassigned);
            evidence["verification_reads"] = new JsonArray(verification.ReadCounts
                .Select(count => (JsonNode)JsonValue.Create(count)!).ToArray());
            return Refuse(report, "copy_not_verified",
                "Ссылки в копии не совпали с графом: " + string.Join("; ", verification.Mismatches),
                verification.Mismatches, moved, stepError: verification.Error, evidence: evidence);
        }

        if (!manifestWritten)
        {
            return Refuse(report, "manifest_not_written", $"Manifest не записан: {manifestReason}",
                Array.Empty<string>(), moved);
        }

        report["taken"] = true;
        report["path"] = topCopy;
        report["document_path"] = topCopy;
        report["document_id_after"] = reopened.DocumentId;
        report[DocumentMoved] = true;
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

    /// <summary>The reopened document's id, or the refused call's error when the open was refused.</summary>
    /// <remarks>INVARIANT: a refusal that came from a refused call carries that call's code and message, so
    /// the client reads the same <c>step_error</c> here as it does on a detail.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private async Task<(string? DocumentId, JsonObject? Error)> OpenForPlanAsync(
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

        var opened = await _invokeCopy("kompas_open_document", arguments, path, cancellationToken)
            .ConfigureAwait(false);
        if (opened.Status != OperationStatus.Succeeded)
        {
            return (null, ErrorOf(opened));
        }

        return (JsonScalars.ReadString(opened.Result?["document_id"])
            ?? JsonScalars.ReadString(opened.Result?["id"]), null);
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

    private sealed record GraphRead(
        bool Ok, string Message, IReadOnlyList<AssemblyComponentLink> Links, int ComponentCount,
        JsonObject? Error = null);

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
                Array.Empty<AssemblyComponentLink>(), 0, ErrorOf(listed));
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
                // MEASURED: the structure read answers ONE ROW PER INSERTION, and `instance_count` is the
                // number of insertions of that file in its parent — the same number on every row of that
                // file. Summing the field therefore doubled it (two rows of two insertions read as four);
                // the count of rows is the count of insertions.
                Insertions: 1,
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

    private sealed record ReassignResult(
        bool Ok, string Message, long? Revision, int? Components = null, JsonObject? Error = null,
        JsonArray? Record = null);

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

        var opened = await _invokeCopy("kompas_open_document", openArguments, copyPath, cancellationToken)
            .ConfigureAwait(false);

        if (opened.Status != OperationStatus.Succeeded)
        {
            // The underlying refusal is repeated in the message: "the copy did not open" alone names no
            // cause, and the cause is what the caller has to act on.
            var cause = opened.Error is null ? string.Empty
                : $": {opened.Error.Code}: {opened.Error.Message}";
            return new ReassignResult(false, $"копия вложенной сборки не открылась{cause}", null,
                Error: ErrorOf(opened));
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
            ? await SaveCopyAsync(
                parentOperationId, "copy-save-" + StableKey(copyPath), copyDocumentId, revision, copyPath, null,
                cancellationToken).ConfigureAwait(false)
            : null;

        // INVARIANT: the copy still holds the components it was planned with AFTER the save. A save that
        // loses instances leaves a file that is not a copy of the assembly, and the check has to name it
        // here — the reopen would only show the smaller count with no cause.
        var afterSave = reassigned.Ok
            ? await CountComponentsAsync(copyDocumentId, cancellationToken).ConfigureAwait(false)
            : null;

        var closed = await _invoke("kompas_close_document", new JsonObject
        {
            ["document_id"] = copyDocumentId,
            ["dirty_policy"] = "save",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "copy-close-" + StableKey(copyPath)),
        }, cancellationToken).ConfigureAwait(false);

        if (!reassigned.Ok)
        {
            return reassigned;
        }

        if (saved is not null && saved.Envelope.Status != OperationStatus.Succeeded)
        {
            return new ReassignResult(false, "копия вложенной сборки не сохранена после переназначения",
                null, Error: ErrorOf(saved.Envelope));
        }

        // INVARIANT: the copy is CLOSED right after this, so a save whose content is still in the temporary
        // file would lose the reassignment with the closed document — the verification would then read the
        // file as it was BEFORE the replacement and report a copy that was never written.
        if (saved is { Settled: false })
        {
            return new ReassignResult(false,
                $"копия «{Path.GetFileName(copyPath)}» не дописана: содержимое модели осталось во временном "
                + $"файле '{copyPath}{SavedFileWrite.TemporarySuffix}'", null);
        }

        if (afterSave is { } left && reassigned.Components is { } planned && left != planned)
        {
            return new ReassignResult(false,
                $"после сохранения копии «{Path.GetFileName(copyPath)}» в ней {left} компонентов вместо "
                + $"{planned}", null);
        }

        // INVARIANT: an unclosed copy makes every later read of it a read of memory, so the close result is
        // named here instead of being dropped.
        if (closed.Status != OperationStatus.Succeeded)
        {
            var cause = closed.Error is null ? string.Empty
                : $": {closed.Error.Code}: {closed.Error.Message}";
            return new ReassignResult(false,
                $"копия «{Path.GetFileName(copyPath)}» не закрыта после сохранения{cause}", null,
                Error: ErrorOf(closed));
        }

        return new ReassignResult(true, string.Empty, saved?.Envelope.RevisionAfter ?? reassigned.Revision);
    }

    /// <summary>The copy's component rows, or the refused call's error when the read failed.</summary>
    /// <remarks>INVARIANT: a failed read is not the same as an empty copy, so the call's own error travels
    /// back with the <c>null</c> rows and reaches <c>step_error</c> of the refusal.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private async Task<(JsonObject[]? Rows, JsonObject? Error)> ListRowsAsync(
        string documentId, CancellationToken cancellationToken)
    {
        var listed = await _invoke("kompas_list_components", new JsonObject
        {
            ["document_id"] = documentId,
        }, cancellationToken).ConfigureAwait(false);
        return listed.Status == OperationStatus.Succeeded
            ? ((listed.Result?["components"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray(),
                null)
            : (null, ErrorOf(listed));
    }

    /// <summary>How many components the document reports, or <c>null</c> when the read failed.</summary>
    private async Task<int?> CountComponentsAsync(string documentId, CancellationToken cancellationToken) =>
        (await ListRowsAsync(documentId, cancellationToken).ConfigureAwait(false)).Rows?.Length;

    /// <summary>What one save of a copy file settled to: the last answer, the revision, and the evidence.</summary>
    /// <remarks>INVARIANT: <c>Settled</c> is decided by the FILE, not by the save's boolean — the caller closes
    /// the document right after, and a save whose content is still in the temporary file would be lost with it.
    /// History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    private sealed record SettledSave(
        ResultEnvelope<JsonNode?> Envelope, long? Revision, JsonArray Attempts, bool Settled)
    {
        /// <summary>The per-attempt facts, under the key the refusal carries them with.</summary>
        public JsonObject Evidence() => new() { ["save_attempts"] = Attempts.DeepClone() };
    }

    /// <summary>Save a copy by the documented route and repeat it while KOMPAS leaves the model's content in
    /// <c>&lt;file&gt;.$$$</c> beside an unreplaced file.</summary>
    /// <remarks>MEASURED: a save can answer success while the model's new content ends up in
    /// <c>&lt;file&gt;.$$$</c> and the file keeps its PREVIOUS bytes; the same documented save then replaces the
    /// file. INVARIANT: each attempt carries its own operation id, else a repeat would return the recorded
    /// answer without touching КОМПАС. DOC: ksdocument3d_saveas.html («TRUE - в случае успешного завершения»),
    /// ikompasdocument_save.html, ikompasdocument_saveas.html.
    /// History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    private async Task<SettledSave> SaveCopyAsync(
        string parentOperationId, string operationKey, string documentId, long? revision, string path,
        string? targetPath, CancellationToken cancellationToken)
    {
        var attempts = new JsonArray();
        var current = revision;
        var settled = false;
        ResultEnvelope<JsonNode?>? last = null;
        for (var attempt = 1; attempt <= SaveAttempts; attempt++)
        {
            var arguments = new JsonObject
            {
                ["document_id"] = documentId,
                ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId,
                    attempt == 1 ? operationKey : $"{operationKey}-repeat{attempt}"),
            };
            if (current is { } expected)
            {
                arguments["expected_revision"] = expected;
            }

            var saveAs = attempt == 1 && targetPath is not null;
            if (saveAs)
            {
                arguments["target_path"] = targetPath;
            }

            last = saveAs
                ? await _invokeCopy("kompas_save_document", arguments, targetPath!, cancellationToken)
                    .ConfigureAwait(false)
                : await _invoke("kompas_save_document", arguments, cancellationToken).ConfigureAwait(false);

            if (last.RevisionAfter is { } after)
            {
                current = after;
            }

            var temporary = SavedFileWrite.Capture(path + SavedFileWrite.TemporarySuffix);
            attempts.Add(new JsonObject
            {
                ["attempt"] = attempt,
                ["mode"] = saveAs ? "SaveAs" : "Save",
                ["status"] = last.Status.ToString(),
                ["revision_after"] = last.RevisionAfter,
                ["target"] = Stamp(SavedFileWrite.Capture(path)),
                ["temporary"] = Stamp(temporary),
            });

            if (last.Status != OperationStatus.Succeeded)
            {
                return new SettledSave(last, current, attempts, Settled: false);
            }

            if (temporary is not { Exists: true })
            {
                settled = true;
                break;
            }
        }

        return new SettledSave(last!, current, attempts, settled);
    }

    /// <summary>One file stamp as JSON: the SAME keys on every state, so a reader compares like with like.</summary>
    private static JsonObject Stamp(SavedFileStamp? stamp) => new(SavedFileWrite.Describe(stamp)
        .Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value))));

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
        // MEASURED: a replace revokes the references of the document and a re-read hands out NEW ones, so a
        // row cannot be remembered between reads. The copy is therefore chosen by the row's POSITION, and the
        // list is re-read before every replace because the replace revokes the refs read before it.
        var (first, firstError) = await ListRowsAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return new ReassignResult(false, "компоненты копии не прочитаны", current, Error: firstError);
        }

        var planned = first
            .Select(row => JsonScalars.ReadString(row["source_path"]))
            .ToArray();
        var record = new JsonArray();
        for (var index = 0; index < planned.Length; index++)
        {
            var source = planned[index];
            if (source is null || !copyBySource.TryGetValue(source, out var copyPath))
            {
                continue;
            }

            var (rows, rowsError) = await ListRowsAsync(documentId, cancellationToken).ConfigureAwait(false);
            if (rows is null)
            {
                return new ReassignResult(false, "компоненты копии не прочитаны", current, Error: rowsError);
            }

            // INVARIANT: the row at this position is still the row the plan was built for. The replace is
            // addressed by position, so a reordered list would put a copy on a foreign component while the
            // count and the folder both still matched — a silent swap of the copy's geometry.
            // MEASURED: the list keeps answering the PREVIOUS source_path of a replaced row until the
            // document is saved in place, so an already-reassigned row legitimately still names its source.
            if (rows.Length != planned.Length || index >= rows.Length)
            {
                return new ReassignResult(false,
                    $"порядок строк копии изменился: строк {rows.Length} при {planned.Length} прочитанных",
                    current);
            }

            var sourceNow = JsonScalars.ReadString(rows[index]["source_path"]);
            if (!string.Equals(sourceNow, planned[index], StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sourceNow, copyPath, StringComparison.OrdinalIgnoreCase))
            {
                return new ReassignResult(false,
                    $"порядок строк копии изменился: строка {index + 1} ведёт на "
                    + $"«{sourceNow ?? "(путь не прочитан)"}» вместо «{planned[index]}» или «{copyPath}»",
                    current);
            }

            var componentRef = JsonScalars.ReadString(rows[index]["component_ref"]);
            if (componentRef is null)
            {
                return new ReassignResult(false,
                    $"ссылка компонента в строке {index + 1} копии не прочитана", current);
            }

            if (current is null)
            {
                return new ReassignResult(false, "ревизия копии не прочитана перед переназначением", null);
            }

            var replacedEnvelope = await _invokeCopy("kompas_replace_component", new JsonObject
            {
                ["document_id"] = documentId,
                ["expected_revision"] = current.Value,
                ["component_ref"] = componentRef,
                ["source_path"] = copyPath,
                ["operation_id"] = ScenarioRunner.ChildOperationId(
                    parentOperationId, "copy-reassign-" + componentRef),
            }, copyPath, cancellationToken).ConfigureAwait(false);

            if (replacedEnvelope.Status != OperationStatus.Succeeded)
            {
                // The route's own code and message are passed on: a cause invented here would send the
                // caller after a library component even when the refusal was, say, a stale revision.
                var refusal = replacedEnvelope.Error is null
                    ? "маршрут замены источника отказал без кода"
                    : $"{replacedEnvelope.Error.Code}: {replacedEnvelope.Error.Message}";
                return new ReassignResult(false,
                    $"компонент «{JsonScalars.ReadString(rows[index]["name"]) ?? source}» не переназначен: "
                    + refusal, current, Error: ErrorOf(replacedEnvelope));
            }

            current = replacedEnvelope.RevisionAfter ?? current;

            // The row as the replace answered it: this is the difference between "the replacement never
            // reached the model" and "it did, and the save lost it" when the copy is read back later.
            record.Add(new JsonObject
            {
                ["row"] = index + 1,
                ["name"] = JsonScalars.ReadString(rows[index]["name"]),
                ["source_before"] = JsonScalars.ReadString(replacedEnvelope.Result?["source_path_before"])
                    ?? sourceNow,
                ["source_after"] = JsonScalars.ReadString(replacedEnvelope.Result?["source_path_after"]),
                ["revision_after"] = replacedEnvelope.RevisionAfter,
            });

            // INVARIANT: a replace that changed the number of components has dropped or duplicated
            // instances, and the copy no longer matches the graph it was planned from. The route reports
            // this itself; it is refused here rather than left for the verification to notice.
            if (replacedEnvelope.Verification?.Checks?.FirstOrDefault(check =>
                    check.Name == "component_count_unchanged" && !check.Passed) is { } dropped)
            {
                return new ReassignResult(false,
                    $"маршрут замены источника изменил число компонентов на «{source}»: "
                    + $"{dropped.Observed} (ждали {dropped.Expected})", current);
            }
        }

        return new ReassignResult(true, string.Empty, current, planned.Length, Record: record);
    }

    /// <summary>The reassignment's own rows as refusal evidence, capped so one answer stays readable.</summary>
    private static JsonObject ReassignEvidence(ReassignResult reassigned)
    {
        var evidence = new JsonObject();
        if (reassigned.Record is { Count: > 0 } record)
        {
            evidence["reassignments"] = new JsonArray(record.Take(20)
                .Select(node => node?.DeepClone()).ToArray());
            evidence["reassignments_total"] = record.Count;
        }

        return evidence;
    }

    // ---- Verification ----

    /// <summary>The mismatches found, and the refused call's error when a read of the copy failed.</summary>
    /// <remarks>INVARIANT: a verification that could not read the copy is a refusal caused by a refused call,
    /// so it carries that call's code; a graph that was read and disagrees carries none.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private sealed record VerifyResult(
        IReadOnlyList<string> Mismatches, JsonObject? Error = null, IReadOnlyList<int>? Readings = null)
    {
        /// <summary>Mismatches counted on EVERY read, in order: the trajectory the verdict rests on.</summary>
        public IReadOnlyList<int> ReadCounts => Readings ?? Array.Empty<int>();
    }

    /// <summary>Read the copy's links from the FILE until the reading stops disagreeing or reads run out.</summary>
    /// <remarks>INVARIANT: the verdict names a STATE, not one reading — a copy whose links are still being
    /// written shows them only on a later read, and a copy that disagrees on every read is refused with the
    /// trajectory instead of a single count.
    /// DOC: kompas_open_document.html (opening a document by path).
    /// History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    private async Task<VerifyResult> VerifyAsync(
        string parentOperationId, string topCopy, IReadOnlyList<AssemblyCopyEntry> entries,
        int expectedComponents, string? applicationId, string copyFolder, string stage,
        CancellationToken cancellationToken)
    {
        var readings = new List<int>(VerifyReads);
        VerifyResult? last = null;
        for (var attempt = 1; attempt <= VerifyReads; attempt++)
        {
            last = await ReadCopyAsync(
                parentOperationId, topCopy, entries, expectedComponents, applicationId, copyFolder,
                stage + "-" + attempt, cancellationToken).ConfigureAwait(false);
            readings.Add(last.Mismatches.Count);
            if (last.Mismatches.Count == 0)
            {
                return new VerifyResult(Array.Empty<string>(), null, readings);
            }
        }

        return new VerifyResult(last!.Mismatches, last.Error, readings);
    }

    /// <summary>One read of the copy's links from the file, with the document closed after it.</summary>
    /// <remarks>INVARIANT: the caller closes every open document of the copy first — an open-by-path while the
    /// same file is open returns that document from memory and proves nothing about the file. INVARIANT: the
    /// verification document is closed without saving, so checking cannot change what was checked.
    /// DOC: kompas_open_document.html (opening a document by path).</remarks>
    private async Task<VerifyResult> ReadCopyAsync(
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

        var opened = await _invokeCopy("kompas_open_document", openArguments, topCopy, cancellationToken)
            .ConfigureAwait(false);

        if (opened.Status != OperationStatus.Succeeded)
        {
            var cause = opened.Error is null ? string.Empty
                : $": {opened.Error.Code}: {opened.Error.Message}";
            return new VerifyResult(
                new[] { "копия верхней сборки не открылась для проверки" + cause }, ErrorOf(opened));
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
            return new VerifyResult(new[] { graph.Message }, graph.Error);
        }

        var (passed, mismatches) = AssemblyCopyPlan.Verify(entries, copyFolder, graph.Links, expectedComponents);
        return new VerifyResult(passed ? Array.Empty<string>() : mismatches);
    }

    // ---- Helpers ----

    private static JsonObject? ErrorOf(ResultEnvelope<JsonNode?> envelope) => envelope.Error is null
        ? null
        : new JsonObject
        {
            ["code"] = envelope.Error.Code,
            ["message"] = envelope.Error.Message,
            // INVARIANT: the refusal carries the refused call's own details, so the КОМПАС error code
            // reaches the client through the checkpoint refusal and not only through the step.
            ["details"] = envelope.Error.Details?.DeepClone(),
        };

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

    /// <summary>Refuse the checkpoint, naming the cause and the files, and the state when the live document
    /// already points into the copy.</summary>
    /// <remarks>INVARIANT: the reason code is one for every refusal (<c>copy_not_written</c>) and the specific
    /// cause travels in <c>details.cause</c>, so a client reads the same field whatever failed.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private static AssemblyCheckpointOutcome Refuse(
        JsonObject report, string cause, string message, IReadOnlyList<string> files, JsonObject? state = null,
        JsonObject? stepError = null, JsonObject? evidence = null)
    {
        if (state is not null)
        {
            foreach (var pair in state)
            {
                report[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var details = new JsonObject
        {
            ["field"] = "checkpoint_policy",
            ["cause"] = cause,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
        };
        // INVARIANT: what the steps answered before the refusal travels with it, so the cause is read from
        // the answer instead of being guessed from a repeat run.
        // History: docs/decisions/scenario.md#checkpoint-settle
        if (evidence is not null)
        {
            foreach (var pair in evidence)
            {
                details[pair.Key] = pair.Value?.DeepClone();
            }
        }
        var error = new JsonObject
        {
            ["code"] = "CHECKPOINT_FAILED",
            ["message"] = message,
            ["reason"] = "copy_not_written",
            ["details"] = details,
        };
        // INVARIANT: a refusal caused by a refused call carries that call's code and message, so the
        // client reads the same field here as it does on a detail.
        // History: docs/decisions/scenario.md#assembly-checkpoint
        if (stepError is not null)
        {
            error["step_error"] = stepError;
        }

        return new AssemblyCheckpointOutcome(false, report, error);
    }
}
