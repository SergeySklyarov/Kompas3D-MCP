using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Documents;
using KompasMcp.Domain.Files;

namespace KompasMcp.Api5Adapter;

/// <summary>Exchange formats: STEP export/import and raster. Import is implemented as an explicit
/// three-route attempt because the historical attempt (spec 4.6) failed and its cause was never
/// established: the result of an import may land in the document that was called, in a component
/// tree, or in a brand-new document. Each possibility is checked, and the answer that comes back
/// says which one happened.</summary>
public sealed partial class Api5Session
{
    /// <summary>D3FormatConvType.format_STEP — write-side selector.</summary>
    private const short FormatStep = 3;

    /// <summary>D3FormatConvType.load_format_STEP — read-side selector — DOC: <c>d3formatconvtype.html</c>, «формат STEP, для открытия документов».</summary>
    private const short LoadFormatStep = -3;

    public ExportResultDto ExportStep(ExportStepCommand command)
    {
        var document = RequireDocument(command.DocumentId);
        if (command.ExpectedRevision != document.Revision)
        {
            throw new KompasContractException(
                ErrorCodes.RevisionConflict,
                $"Экспорт запрошен для ревизии {command.ExpectedRevision}, у документа {document.Revision}.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["requested_revision"] = command.ExpectedRevision,
                    ["current_revision"] = document.Revision,
                });
        }

        var parameter = (ksAdditionFormatParam)document.Document3D.AdditionFormatParam();
        parameter.Init();
        parameter.format = FormatStep;

        var destination = command.OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");

        bool saved;
        try
        {
            saved = document.Document3D.SaveAsToAdditionFormat(destination, parameter);
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                $"SaveAsToAdditionFormat бросил исключение: {ex.Message}",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                partialEffects: File.Exists(destination));
        }

        if (!saved || !File.Exists(destination) || new FileInfo(destination).Length == 0)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                "Экспорт STEP не подтверждён: конвертер вернул отказ или файл пуст.",
                RetryPolicy.SameOperationId,
                partialEffects: File.Exists(destination));
        }

        // Level reached: the file exists and an independent reader opened it. Geometry equality
        // is a separate, explicit roundtrip that this call does not perform and that no published
        // tool performs yet (the contract document plans one under the name kompas_validate_export);
        // it is never implied by this call.
        var structural = StepFileInspector.Inspect(destination);
        var checks = new List<NamedCheck>
        {
            new("file_created", true, Observed: structural.ByteLength.ToString(CultureInfo.InvariantCulture) + " байт"),
            new("header_parsed", structural.HeaderParsed, Observed: structural.Schema ?? "не распознана"),
        };

        var level = structural.HeaderParsed ? VerificationLevel.SyntaxChecked : VerificationLevel.FileCreated;
        return new ExportResultDto
        {
            OutputPath = destination,
            ByteLength = structural.ByteLength,
            Sha256 = FileHash.Sha256(destination),
            ReachedLevel = level,
            UnverifiedAspects = structural.UnverifiedAspects,
            SourceRevision = document.Revision,
        };
    }

    public ImportResultDto ImportStep(ImportStepCommand command)
    {
        var application = RequireApplication(command.ApplicationId);
        var trace = new List<string>();
        trace.Add($"файл: {command.InputPath}");

        if (!File.Exists(command.InputPath))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Файл импорта не найден: {command.InputPath}");
        }

        // The comparison is over HOST ids, not COM handles: the registry is what kompas_list_documents
        // answers, so the diff is exactly the set of ids a client can address.
        // History: docs/decisions/adapter-core.md#import-step-contract
        var registeredBefore = RegisteredDocumentIds();
        trace.Add($"документов до импорта: {registeredBefore.Count}");

        // Documents a failed route could not remove. Empty is the normal case; a non-empty list is what
        // keeps the refusal from claiming a clean session it does not have.
        var leftOpen = new List<string>();

        // Route 1 — LoadFromAdditionFormat on a fresh document, with the read-side selector.
        var attempt = TryRouteLoadIntoNewDocument(application, command, trace, leftOpen, useReadSelector: true);
        if (attempt is null)
        {
            // Route 2 — the same call with the parameter left at vendor defaults.
            attempt = TryRouteLoadIntoNewDocument(application, command, trace, leftOpen, useReadSelector: false);
        }

        var created = ImportStepContract.CreatedDocumentIds(registeredBefore, RegisteredDocumentIds());
        trace.Add($"новых документов: {created.Count}");

        if (attempt is null)
        {
            var leftovers = ImportStepContract.DocumentsLeftOpen(leftOpen, Array.Empty<string>());
            throw new KompasContractException(
                ErrorCodes.ImportFailed,
                "Ни один маршрут импорта STEP не дал тел, компонентов или нового документа. " +
                "Исторический провал spec 4.6 воспроизводится и на v24; причина не установлена.",
                RetryPolicy.Never,
                partialEffects: leftovers.Count > 0,
                details: new Dictionary<string, object?>
                {
                    ["trace"] = trace.ToArray(),
                    ["documents_left_open"] = leftovers,
                });
        }

        // target_path is APPLIED, not ignored: the documented route ksDocument3D.SaveAs writes the
        // imported document to the requested path. DOC: ksdocument3d_saveas.html.
        ApplyTargetPath(attempt.DocumentId, command.TargetPath, trace);

        var unverified = new List<string>
        {
            "geometry_roundtrip_not_checked — совпадение объёма и габарита с исходником требует отдельной проверки",
            "units_conversion_not_checked — единицы импортированного документа не подтверждены",
        };
        if (leftOpen.Count > 0)
        {
            unverified.Add("documents_left_open — неудачный маршрут оставил открытыми: "
                + string.Join(", ", leftOpen));
        }

        return new ImportResultDto
        {
            // The registry diff names the HOST ids; the attempt's own id is the fallback when the
            // comparison is empty. A COM handle is never returned.
            CreatedDocumentIds = created.Count > 0 ? created : new[] { attempt.DocumentId },
            CreatedDocumentKinds = new[] { attempt.Kind },
            BodyCount = attempt.BodyCount,
            ComponentCount = attempt.ComponentCount,
            // B-rep equality with the source is not claimed here and is not checked by any
            // published tool yet (see UnverifiedAspects below).
            ReachedLevel = VerificationLevel.StructureChecked,
            UnverifiedAspects = unverified.ToArray(),
            Trace = trace,
            // The kind was read by the same documented call that classified the document, so the
            // caller's expectation is checked against a fact, not against the request itself.
            DesiredKindCheck = ImportStepContract.DesiredKindCheck(command.DesiredKind, attempt.Kind),
        };
    }

    /// <summary>Apply the caller's target path through the documented route <c>ksDocument3D.SaveAs</c>
    /// (DOC: ksdocument3d_saveas.html — «Сохранить документ-модель (деталь или сборку) с новым
    /// именем») and confirm it by the path the document reports afterwards. The returned boolean alone
    /// is not a confirmation, so the shared save path is used — it re-reads the file — rather than a
    /// second write route. A failure here is a refusal with partial_effects: the document itself was
    /// already created, so the session is not unchanged.</summary>
    private void ApplyTargetPath(string documentId, string? targetPath, List<string> trace)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        var entry = RequireDocument(documentId);
        try
        {
            SaveDocument(entry, targetPath);
        }
        catch (KompasContractException ex)
        {
            throw new KompasContractException(
                ErrorCodes.ImportFailed,
                $"Импорт создал документ, но сохранить его по target_path '{targetPath}' не удалось: {ex.Message}",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["trace"] = trace.ToArray(),
                    ["document_id"] = documentId,
                    ["target_path"] = targetPath,
                });
        }

        trace.Add($"target_path применён: SaveAs → {RequireDocument(documentId).Path}");
    }

    private sealed record ImportAttempt(string DocumentId, DocumentKind Kind, int BodyCount, int ComponentCount, string Summary);

    /// <summary>Try one documented route. On SUCCESS the created document is the answer and stays
    /// registered. On ANY failure — including an exception after the document was created — what this
    /// route opened is closed and unregistered here, BEFORE the next attempt, and what could not be
    /// removed is appended to <paramref name="leftOpen"/> instead of being dropped silently.</summary>
    /// <remarks>INVARIANT: a failed route leaves no registered document. MEASURED: the registration
    /// used to survive an exception thrown after it, so the registry kept documents the refusal never
    /// mentioned.
    /// History: docs/decisions/adapter-core.md#import-step-contract</remarks>
    private ImportAttempt? TryRouteLoadIntoNewDocument(
        ApplicationEntry application,
        ImportStepCommand command,
        List<string> trace,
        List<string> leftOpen,
        bool useReadSelector = true)
    {
        var route = useReadSelector ? "load_format_STEP=-3" : "параметр по умолчанию";
        ksDocument3D? target = null;
        DocumentEntry? entry = null;
        var keep = false;
        try
        {
            target = (ksDocument3D)application.Application.Document3D();
            var createdDocument = target.Create(true, true);
            trace.Add($"маршрут «{route}»: Create → {createdDocument}");

            var parameter = (ksAdditionFormatParam)target.AdditionFormatParam();
            parameter.Init();
            if (useReadSelector)
            {
                parameter.format = LoadFormatStep;
            }

            var loaded = target.LoadFromAdditionFormat(command.InputPath, parameter);
            trace.Add($"  LoadFromAdditionFormat → {loaded}");
            target.RebuildDocument();
            target.UpdateDocumentParam();

            var documentId = RegisterImportedDocument(target, application, trace);
            entry = RequireDocument(documentId);
            // A model just built by an import is a state of the BUILT model only if its own rebuild
            // applied every feature: the counts below must not describe a half-built model.
            SettleBuiltModel(entry);
            var bodies = CountBodies(entry);
            var components = CountComponents(entry);
            trace.Add($"  тел={bodies}, компонентов={components}");

            if (bodies > 0 || components > 0)
            {
                keep = true;
                return new ImportAttempt(
                    entry.Id, entry.Kind, bodies, components,
                    useReadSelector ? "read-selector" : "default-param");
            }

            trace.Add($"  маршрут «{route}» не дал ни тел, ни компонентов");
            return null;
        }
        catch (Exception ex)
        {
            trace.Add($"  маршрут «{route}» → {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (!keep)
            {
                RemoveFailedRouteDocument(application, target, entry, trace, leftOpen);
            }
        }
    }

    /// <summary>Close and unregister the document a failed route opened. The close goes through the
    /// documented route with an explicit "what happens to unsaved changes" — the option-less API5
    /// close() would put the question back to a user who is not there. When the close does not succeed
    /// the document is LEFT (so <c>kompas_list_documents</c> tells the truth) and named.</summary>
    private void RemoveFailedRouteDocument(
        ApplicationEntry application,
        ksDocument3D? target,
        DocumentEntry? entry,
        List<string> trace,
        List<string> leftOpen)
    {
        if (entry is null && target is null)
        {
            return;
        }

        bool closed;
        string? note;
        if (entry is not null)
        {
            closed = TryCloseImportDocument(application, entry, out note);
        }
        else
        {
            closed = TryCloseImportDocument(application, target!, out note);
        }

        if (closed)
        {
            if (entry is not null)
            {
                DropDocument(entry, alreadyClosed: true);
            }

            var label = entry?.Id ?? "(без id)";
            trace.Add(note is null
                ? $"  документ {label} закрыт и снят с регистрации"
                : $"  документ {label} закрыт ({note})");
            return;
        }

        if (entry is not null)
        {
            leftOpen.Add(entry.Id);
            trace.Add($"  документ {entry.Id} оставлен открытым: закрыть не удалось ({note})");
            return;
        }

        // Never registered, so it has no host id to name: give it one, which also makes
        // kompas_list_documents show what is really open.
        var named = RegisterLeftoverDocument(application, target!, trace);
        leftOpen.Add(named);
        trace.Add($"  документ {named} оставлен открытым: закрыть не удалось ({note})");
    }

    /// <summary>Close a document opened by an import route, preferring the documented API7 route
    /// (<c>IKompasDocument.Close(kdDoNotSaveChanges)</c> — DOC: ikompasdocument_close.html) and falling
    /// back to the API5 handle. Returns true only when the document was actually closed; the reason is
    /// named otherwise, so a failed close is never mistaken for a clean one.</summary>
    private bool TryCloseImportDocument(ApplicationEntry application, DocumentEntry entry, out string? note) =>
        TryCloseImportDocumentCore(application, entry.Document, out note);

    private bool TryCloseImportDocument(ApplicationEntry application, ksDocument3D document, out string? note) =>
        TryCloseImportDocumentCore(application, document, out note);

    private bool TryCloseImportDocumentCore(ApplicationEntry application, ksDocument3D? document, out string? note)
    {
        note = null;
        if (document is null)
        {
            note = "у документа нет 3D-дескриптора для закрытия";
            return false;
        }

        try
        {
            if (BridgeForApplication(application).TransferTo7(document) is IKompasDocument document7)
            {
                if (document7.Close(DocumentCloseOptions.kdDoNotSaveChanges))
                {
                    return true;
                }

                note = "IKompasDocument.Close(kdDoNotSaveChanges) вернул false";
                return false;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            note = "перевод в API7 бросил: " + ex.Message;
        }

        try
        {
            // API5 ksDocument3D.close() has no "do not save" choice; it is the last resort and the
            // limitation is named rather than hidden.
            document.close();
            note = "закрыт через API5 ksDocument3D.close(): у него нет выбора «не сохранять»";
            return true;
        }
        catch (Exception ex) when (ex is COMException)
        {
            note = "закрытие бросило: " + ex.Message;
            return false;
        }
    }

    /// <summary>Register a document a failed route left open but never registered, so it has a host id
    /// to be named by and shows up in <c>kompas_list_documents</c>. A registration that fails is
    /// named instead of thrown: the import is already being reported as failed.</summary>
    private string RegisterLeftoverDocument(ApplicationEntry application, ksDocument3D document, List<string> trace)
    {
        try
        {
            return RegisterImportedDocument(document, application, trace);
        }
        catch (Exception ex) when (ex is KompasContractException or COMException or InvalidCastException)
        {
            trace.Add($"  документ, созданный маршрутом, не удалось даже зарегистрировать: {ex.GetType().Name}");
            return "незарегистрированный документ, созданный маршрутом";
        }
    }

    private string RegisterImportedDocument(ksDocument3D document, ApplicationEntry application, List<string> trace)
    {
        var part = (ksPart)document.GetPart(-1);
        var isDetail = CallIsDetail(document);
        var kind = isDetail == false ? DocumentKind.Assembly : DocumentKind.Part;
        var entry = RegisterDocument(document, part, application, kind, path: null, kindVerified: isDetail is not null);
        trace.Add($"  документ зарегистрирован как {kind} (id={entry.Id})");
        return entry.Id;
    }

    /// <summary>Host ids the session registry holds right now. The registry is what
    /// <c>kompas_list_documents</c> answers, so a before/after comparison of these ids names what an
    /// import created in the only identity a client can address.</summary>
    private IReadOnlyList<string> RegisteredDocumentIds() => _documents.Keys.ToArray();

    /// <summary>In-product copy of the P0.7 measurement: build known geometry, read every quantity back
    /// with each unit selector, and refuse to publish anything whose scale is not confirmed.
    /// Integration tests assert on this instead of trusting the constants.</summary>
    public UnitProbeResult UnitProbe(UnitProbeCommand command)
    {
        var application = RequireApplication(command.ApplicationId);
        var document = CreateDocument(new CreateDocumentCommand
        {
            ApplicationId = command.ApplicationId,
            Kind = DocumentKind.Part,
            Name = "unit_probe",
        });

        var raw = new Dictionary<string, double?>(StringComparer.Ordinal);
        var observations = new Dictionary<string, string?>(StringComparer.Ordinal);

        var sketch = CreateSketch(new CreateSketchCommand
        {
            DocumentId = document.Id,
            Plane = new PlaneRefDto { Base = PlaneBase.Xy },
            Name = "unit_probe profile",
            ExpectedRevision = document.Revision,
        });

        EditSketch(new EditSketchCommand
        {
            SketchRef = sketch.Id,
            Mode = SketchEditMode.Replace,
            ExpectedRevision = RequireDocument(document.Id).Revision,
            Entities = new[]
            {
                new SketchEntityDto
                {
                    Kind = SketchEntityKind.Line,
                    StartMm = new double[] { 0, 0 },
                    EndMm = new double[] { command.KnownLengthMm, 0 },
                },
                new SketchEntityDto
                {
                    Kind = SketchEntityKind.Circle,
                    CenterMm = new double[] { 0, command.KnownRadiusMm * 3 },
                    RadiusMm = command.KnownRadiusMm,
                },
            },
        });

        FinishSketch(new FinishSketchCommand { SketchRef = sketch.Id, RequireClosedProfile = false });

        var entry = RequireDocument(document.Id);
        var edges = ReadEdgesForProbe(entry, raw, observations);
        observations["edge_count"] = edges.ToString(CultureInfo.InvariantCulture);

        if (TryGetGabarit(entry, out var dims) && dims.Length >= 3)
        {
            raw["gabarit_x_mm"] = dims[0];
            raw["gabarit_y_mm"] = dims[1];
            raw["gabarit_z_mm"] = dims[2];
        }

        var confirmed = new List<string>();
        // ReadEdgesForProbe names its keys edge{index}_length_selector_{selector}; the earlier
        // lookup used the unprefixed "edge_length_selector_1", which no writer ever created, so the
        // scale was never confirmed no matter how exact the numbers were. Scan the keys that exist
        // for the edge whose millimetre reading matches the drawn length.
        var lineMm = raw.FirstOrDefault(kv => kv.Key.EndsWith("_length_selector_1", StringComparison.Ordinal)
                                              && kv.Value is double v
                                              && Math.Abs(v - command.KnownLengthMm) <= Math.Max(1e-6, 1e-9 * command.KnownLengthMm));
        if (lineMm.Key is not null)
        {
            var index = lineMm.Key.Substring("edge".Length, lineMm.Key.IndexOf('_') - "edge".Length);
            confirmed.Add($"GetLength(1)==mm для ребра {index} (наблюдено {lineMm.Value:R} при {command.KnownLengthMm:R})");

            var selectorKey = $"edge{index}_length_selector_0";
            if (raw.TryGetValue(selectorKey, out var selectorCm) && selectorCm is double cmValue
                && Math.Abs(cmValue * 10d - command.KnownLengthMm) <= Math.Max(1e-6, 1e-6 * command.KnownLengthMm))
            {
                confirmed.Add($"GetLength(0)==см для ребра {index} (наблюдено {cmValue:R}, ×10 = {cmValue * 10d:R})");
            }
        }
        else
        {
            var mmSelectorCount = raw.Count(kv => kv.Key.EndsWith("_length_selector_1", StringComparison.Ordinal));
            observations["length_selector_1_no_match"] =
                $"ни одно из {mmSelectorCount} прочитанных рёбер не равно {command.KnownLengthMm:R} мм";
        }

        return new UnitProbeResult
        {
            KnownLengthMm = command.KnownLengthMm,
            KnownRadiusMm = command.KnownRadiusMm,
            RawReadings = raw,
            Observations = observations,
            ConfirmedScales = confirmed,
            UnverifiedAspects = confirmed.Count > 0
                ? new[] { "volume_scale_not_probed — объём проверяется отдельно, на теле" }
                : new[] { "length_scale_not_confirmed — ни один селектор не совпал с ожидаемым значением" },
        };
    }

    private int ReadEdgesForProbe(DocumentEntry document, Dictionary<string, double?> raw, Dictionary<string, string?> observations)
    {
        if (document.PartNow().GetMainBody() is not ksBody body)
        {
            observations["main_body"] = "null (тело не построено — для зонда единиц это нормально)";
            return 0;
        }

        var faces = (ksFaceCollection)body.FaceCollection();
        var seen = new HashSet<IntPtr>();
        var index = 0;
        for (var f = 0; f < faces.GetCount() && index < 4; f++)
        {
            if (AsInterface<ksFaceDefinition>(faces.GetByIndex(f)) is not ksFaceDefinition face)
            {
                continue;
            }

            var edges = (ksEdgeCollection)face.EdgeCollection();
            for (var e = 0; e < edges.GetCount() && index < 4; e++)
            {
                if (AsInterface<ksEdgeDefinition>(edges.GetByIndex(e)) is not ksEdgeDefinition edge)
                {
                    continue;
                }

                var pointer = Marshal.GetIUnknownForObject(edge);
                var fresh = seen.Add(pointer);
                Marshal.Release(pointer);
                if (!fresh)
                {
                    continue;
                }

                index++;
                foreach (var selector in new uint[] { 0, 1, 2, 3 })
                {
                    try
                    {
                        raw[$"edge{index}_length_selector_{selector}"] = edge.GetLength(selector);
                    }
                    catch (Exception ex) when (ex is COMException)
                    {
                        raw[$"edge{index}_length_selector_{selector}"] = null;
                    }
                }
            }
        }

        return seen.Count;
    }
}

/// <summary>Minimal, dependency-free structural read of a STEP file: enough to report the schema token and
/// entity counts without claiming any geometric validation (spec 1.11 levels).</summary>
public static class StepFileInspector
{
    public sealed record StepFacts(long ByteLength, bool HeaderParsed, string? Schema, int EntityStatements, IReadOnlyList<string> UnverifiedAspects);

    public static StepFacts Inspect(string path)
    {
        var info = new FileInfo(path);
        var text = ReadHead(path, 256 * 1024);

        var schema = FirstMatch(text, "AUTOMOTIVE_DESIGN_THEORY", "AP242", "AUTOMOTIVE_DESIGN", "CONFIG_CONTROL_DESIGN", "AP214", "AP203");
        var headerParsed = text.Contains("ISO-10303-21;HEADER", StringComparison.OrdinalIgnoreCase)
            || text.Contains("FILE_SCHEMA", StringComparison.OrdinalIgnoreCase);
        var statements = CountOccurrences(text, "();");

        var unverified = new List<string>
        {
            "geometry_not_validated — проверена только структура файла, не форма",
            "units_not_confirmed_from_file — запись единиц ищется, но не трактуется как подтверждение масштаба",
        };

        if (schema is null)
        {
            unverified.Add("schema_token_not_found");
        }

        return new StepFacts(info.Length, headerParsed, schema, statements, unverified);
    }

    private static string ReadHead(string path, int limit)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(limit, stream.Length);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return new UTF8Encoding(false).GetString(buffer);
    }

    private static string? FirstMatch(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return needle;
            }
        }

        return null;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
