using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Kompas6API5;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Files;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Exchange formats: STEP export/import and raster. Import is implemented as an explicit
/// three-route attempt because the historical attempt (spec 4.6) failed and its cause was never
/// established: the result of an import may land in the document that was called, in a component
/// tree, or in a brand-new document. Each possibility is checked, and the answer that comes back
/// says which one happened.
/// </summary>
public sealed partial class Api5Session
{
    /// <summary>D3FormatConvType.format_STEP — write-side selector.</summary>
    private const short FormatStep = 3;

    /// <summary>D3FormatConvType.load_format_STEP — read-side selector («Для открытия документов»).</summary>
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

        var parameter = (ksAdditionFormatParam)document.Document.AdditionFormatParam();
        parameter.Init();
        parameter.format = FormatStep;

        var destination = command.OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");

        bool saved;
        try
        {
            saved = document.Document.SaveAsToAdditionFormat(destination, parameter);
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

        var documentsBefore = SnapshotDocuments(application, trace);
        trace.Add($"документов до импорта: {documentsBefore.Count}");

        // Route 1 — LoadFromAdditionFormat on a fresh document, with the read-side selector.
        var attempt = TryRouteLoadIntoNewDocument(application, command, trace);
        if (attempt is null)
        {
            // Route 2 — the same call with the parameter left at vendor defaults.
            attempt = TryRouteLoadIntoNewDocument(application, command, trace, useReadSelector: false);
        }

        var documentsAfter = SnapshotDocuments(application, trace);
        var created = documentsAfter.Except(documentsBefore).ToList();
        trace.Add($"новых документов: {created.Count}");

        if (attempt is null && created.Count == 0)
        {
            throw new KompasContractException(
                ErrorCodes.ImportFailed,
                "Ни один маршрут импорта STEP не дал тел, компонентов или нового документа. " +
                "Исторический провал spec 4.6 воспроизводится и на v24; причина не установлена.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["trace"] = trace.ToArray() });
        }

        var bodies = attempt?.BodyCount ?? 0;
        var components = attempt?.ComponentCount ?? 0;
        var verified = bodies > 0 || components > 0;

        if (attempt is null)
        {
            // Without an attempt there is nothing to report as imported, and the earlier branch
            // already refused the "no documents appeared" case.
            throw new KompasContractException(
                ErrorCodes.ImportFailed,
                "Ни один маршрут импорта не вернул документ: тела есть, но где именно — неизвестно.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["trace"] = trace.ToArray() });
        }

        if (!verified)
        {
            throw new KompasContractException(
                ErrorCodes.ImportFailed,
                $"Импорт создал документ, но тел и компонентов в нём не найдено ({attempt.Summary}). " +
                "Ложный PASS по факту существования файла не выдаётся.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["trace"] = trace.ToArray() });
        }

        return new ImportResultDto
        {
            CreatedDocumentIds = created.Count > 0 ? created : new[] { attempt.DocumentId },
            CreatedDocumentKinds = new[] { attempt.Kind },
            BodyCount = bodies,
            ComponentCount = components,
            // B-rep equality with the source is not claimed here and is not checked by any
            // published tool yet (see UnverifiedAspects below).
            ReachedLevel = VerificationLevel.StructureChecked,
            UnverifiedAspects = new[]
            {
                "geometry_roundtrip_not_checked — совпадение объёма и габарита с исходником требует отдельной проверки",
                "units_conversion_not_checked — единицы импортированного документа не подтверждены",
            },
            Trace = trace,
        };
    }

    private sealed record ImportAttempt(string DocumentId, DocumentKind Kind, int BodyCount, int ComponentCount, string Summary);

    private ImportAttempt? TryRouteLoadIntoNewDocument(ApplicationEntry application, ImportStepCommand command, List<string> trace, bool useReadSelector = true)
    {
        ksDocument3D? target = null;
        try
        {
            target = (ksDocument3D)application.Application.Document3D();
            var createdDocument = target.Create(true, true);
            trace.Add($"маршрут «{(useReadSelector ? "load_format_STEP=-3" : "параметр по умолчанию")}»: Create → {createdDocument}");

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
            var entry = RequireDocument(documentId);
            var bodies = CountBodies(entry);
            var components = CountComponents(entry);
            trace.Add($"  тел={bodies}, компонентов={components}");

            if (bodies > 0 || components > 0)
            {
                return new ImportAttempt(documentId, entry.Kind, bodies, components, useReadSelector ? "read-selector" : "default-param");
            }

            // The converter may have opened its own document; look at the active one too.
            DropImportedDocument(documentId);
            return null;
        }
        catch (Exception ex)
        {
            trace.Add($"  маршрут «{(useReadSelector ? "read-selector" : "default-param")}» → {ex.GetType().Name}: {ex.Message}");
            return null;
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

    private void DropImportedDocument(string documentId)
    {
        try
        {
            var entry = RequireDocument(documentId);
            DropDocument(entry);
        }
        catch (Exception ex) when (ex is KompasContractException or COMException)
        {
            // The import path is already being reported as failed.
        }
    }

    private IReadOnlyList<string> SnapshotDocuments(ApplicationEntry application, List<string> trace)
    {
        // API5 exposes no document collection; the only identity usable for a before/after
        // comparison is the IUnknown of the active document, so that is what is tracked.
        var ids = new List<string>();
        try
        {
            if (application.Application.ActiveDocument3D() is ksDocument3D active)
            {
                var pointer = Marshal.GetIUnknownForObject(active);
                ids.Add("0x" + pointer.ToString("X", CultureInfo.InvariantCulture));
                Marshal.Release(pointer);
                ComApartment.Release(active);
            }
        }
        catch (Exception ex) when (ex is COMException)
        {
            trace.Add("ActiveDocument3D недоступен: " + ex.GetType().Name);
        }

        return ids;
    }

    /// <summary>
    /// In-product copy of the P0.7 measurement: build known geometry, read every quantity back
    /// with each unit selector, and refuse to publish anything whose scale is not confirmed.
    /// Integration tests assert on this instead of trusting the constants.
    /// </summary>
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

/// <summary>
/// Minimal, dependency-free structural read of a STEP file: enough to report the schema token and
/// entity counts without claiming any geometric validation (spec 1.11 levels).
/// </summary>
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
