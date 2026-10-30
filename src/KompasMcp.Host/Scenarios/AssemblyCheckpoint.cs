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
/// DOC: ipart7_filename.html (a component's file name is set only on a component inserted into an assembly,
/// and takes effect after IModelObject::Update), ipart7_saveas.html, ikompasdocument_close.html.
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

        // The open of a copy names the application the plan works in: it is mandatory in that tool's
        // contract and was read from the document's context.
        // The open of a copy names the application, and that field is mandatory. It is read from the
        // envelope first, then from the body, and finally from the document list: an empty id made every
        // open of a copy fail for a missing field.
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

        // The top file goes through the SAME route as a detail: SaveAs, so unsaved edits of the top assembly
        // are inside the copy. The live document becomes the copy; the client's own file is not written.
        var topCopy = Path.Combine(folder, Path.GetFileName(sourcePath));
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

        // SaveAs leaves the copy under a NEW id: the plan's own id would address nothing afterwards. The id
        // is taken from the session's document list BY PATH, because the save result names the id it wrote
        // only when the session re-registered the document, and a guess here costs every later step.
        var copyDocumentId = await ResolveDocumentIdAsync(topCopy, applicationId, cancellationToken)
            .ConfigureAwait(false)
            ?? JsonScalars.ReadString(saved.Result?["context"]?["id"])
            ?? documentId;
        var revisionNow = saved.RevisionAfter ?? revision.Value;
        var topReassigned = await ReassignDocumentAsync(
            parentOperationId, copyDocumentId, revisionNow, plan.Entries, externalParts, cancellationToken)
            .ConfigureAwait(false);
        if (!topReassigned.Ok)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_written",
                $"Ссылки верхней сборки не переназначены: {topReassigned.Message}", Array.Empty<string>());
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
                return Refuse(report, "copy_not_written", "Копия с переназначенными ссылками не сохранена.",
                    Array.Empty<string>());
            }
        }

        // The manifest names every file of the graph; a read_only row has no copy path, so a reader cannot
        // mistake the client's own file for a protected copy.
        var manifest = AssemblyCopyPlan.Manifest(
            sourcePath, externalParts, DateTimeOffset.UtcNow, plan.Entries, verified: false,
            new[] { "verification_pending" });
        var manifestPath = Path.Combine(folder, "manifest.json");
        var manifestWritten = TryWriteAtomic(manifestPath, manifest, out var manifestReason);

        var verification = await VerifyAsync(
            parentOperationId, topCopy, plan.Entries, graph.ComponentCount, applicationId,
            cancellationToken)
            .ConfigureAwait(false);

        if (manifestWritten && verification.Mismatches.Count == 0)
        {
            manifest["verified"] = true;
            manifest["verification_notes"] = new JsonArray();
            TryWriteAtomic(manifestPath, manifest, out manifestReason);
        }

        if (verification.Mismatches.Count > 0)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_verified",
                "Ссылки в копии не совпали с графом: " + string.Join("; ", verification.Mismatches),
                verification.Mismatches);
        }

        if (!manifestWritten)
        {
            DeleteFolderIfCreated(folder, created);
            return Refuse(report, "copy_not_written", $"Manifest не записан: {manifestReason}",
                Array.Empty<string>());
        }

        report["taken"] = true;
        report["path"] = topCopy;
        report["document_id_after"] = copyDocumentId;
        report["revision"] = revision.Value;
        report["application_id"] = applicationId;
        report["manifest_path"] = manifestPath;
        report["file_count"] = plan.Entries.Count;
        report["copied"] = plan.Entries.Count(e => e.Mode == AssemblyCopyPlan.ModeCopy);
        report["read_only"] = plan.Entries.Count(e => e.Mode == AssemblyCopyPlan.ModeReadOnly);
        report["verified"] = true;
        report["read_only_not_restored"] = plan.Entries.Count(e => e.Mode == AssemblyCopyPlan.ModeReadOnly);
        if (externalParts == ExternalPartsReadOnly)
        {
            report["note"] = "external_parts=read_only: компоненты не копировались, ссылки копии ведут на "
                + "исходные файлы, и контрольная точка их не защищает.";
        }

        return new AssemblyCheckpointOutcome(true, report, null);
    }

    // ---- Graph ----

    /// <summary>The id the session has for a path, or <c>null</c> when the path is not registered.</summary>
    private async Task<string?> ResolveDocumentIdAsync(
        string path, string? applicationId, CancellationToken cancellationToken)
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
        return (listed.Result as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(row => string.Equals(JsonScalars.ReadString(row["path"]), path,
                StringComparison.OrdinalIgnoreCase))
            .Select(row => JsonScalars.ReadString(row["id"]) ?? JsonScalars.ReadString(row["document_id"]))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
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

    // ---- Reassignment inside the copies ----

    private sealed record ReassignResult(bool Ok, string Message, long? Revision);

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

        return new ReassignResult(true, string.Empty, saved?.RevisionAfter ?? reassigned.Revision);
    }

    /// <summary>Point every component of one document at its copy, by the documented source replacement.</summary>
    /// <remarks>DOC: kspart_setfilename.html — «Метод используется для компонентов, вставленных в сборку»;
    /// «Компонент не должен быть деталью из библиотеки моделей или стандартным элементом». A component the
    /// route refuses is NAMED, and the checkpoint is refused rather than left half-reassigned.
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

        var listed = await _invoke("kompas_list_components", new JsonObject
        {
            ["document_id"] = documentId,
        }, cancellationToken).ConfigureAwait(false);
        if (listed.Status != OperationStatus.Succeeded)
        {
            return new ReassignResult(false, "компоненты копии не прочитаны", revision);
        }

        var copyBySource = entries
            .Where(entry => entry.CopyPath is not null)
            .ToDictionary(entry => entry.SourcePath, entry => entry.CopyPath!, StringComparer.OrdinalIgnoreCase);

        var current = revision;
        var rows = (listed.Result?["components"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToArray();
        foreach (var row in rows)
        {
            var source = JsonScalars.ReadString(row["source_path"]);
            var componentRef = JsonScalars.ReadString(row["component_ref"]);
            if (source is null || componentRef is null || !copyBySource.TryGetValue(source, out var target))
            {
                continue;
            }

            if (current is null)
            {
                return new ReassignResult(false, "ревизия копии не прочитана перед переназначением", null);
            }

            var replaced = await _invoke("kompas_replace_component", new JsonObject
            {
                ["document_id"] = documentId,
                ["expected_revision"] = current.Value,
                ["component_ref"] = componentRef,
                ["source_path"] = target,
                ["operation_id"] = ScenarioRunner.ChildOperationId(
                    parentOperationId, "copy-reassign-" + componentRef),
            }, cancellationToken).ConfigureAwait(false);

            if (replaced.Status != OperationStatus.Succeeded)
            {
                return new ReassignResult(false,
                    $"компонент «{JsonScalars.ReadString(row["name"]) ?? source}» не переназначен "
                    + "(маршрут ksPart.SetFileName не принимает библиотечные и стандартные компоненты)",
                    current);
            }

            current = replaced.RevisionAfter ?? current;
        }

        return new ReassignResult(true, string.Empty, current);
    }

    // ---- Verification ----

    private sealed record VerifyResult(IReadOnlyList<string> Mismatches);

    /// <summary>Reopen the copy and re-read the links from the FILE, not from memory.</summary>
    /// <remarks>INVARIANT: the verification document is closed without saving, so checking cannot change what
    /// was checked. History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    private async Task<VerifyResult> VerifyAsync(
        string parentOperationId, string topCopy, IReadOnlyList<AssemblyCopyEntry> entries,
        int expectedComponents, string? applicationId, CancellationToken cancellationToken)
    {
        var openArguments = new JsonObject
        {
            ["path"] = topCopy,
            ["access"] = "read_only",
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "verify-open"),
        };
        if (!string.IsNullOrWhiteSpace(applicationId))
        {
            openArguments["application_id"] = applicationId;
        }

        var opened = await _invoke("kompas_open_document", openArguments, cancellationToken)
            .ConfigureAwait(false);

        if (opened.Status != OperationStatus.Succeeded)
        {
            return new VerifyResult(new[] { "копия верхней сборки не открылась для проверки" });
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
            ["operation_id"] = ScenarioRunner.ChildOperationId(parentOperationId, "verify-close"),
        }, cancellationToken).ConfigureAwait(false);

        if (!graph.Ok)
        {
            return new VerifyResult(new[] { graph.Message });
        }

        var folder = Path.GetDirectoryName(topCopy)!;
        var (passed, mismatches) = AssemblyCopyPlan.Verify(entries, folder, graph.Links, expectedComponents);
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

    private static AssemblyCheckpointOutcome Refuse(
        JsonObject report, string reason, string message, IReadOnlyList<string> files) => new(
        false,
        report,
        new JsonObject
        {
            ["code"] = "CHECKPOINT_FAILED",
            ["message"] = message,
            ["reason"] = reason,
            ["details"] = new JsonObject
            {
                ["field"] = "checkpoint_policy",
                ["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            },
        });
}
