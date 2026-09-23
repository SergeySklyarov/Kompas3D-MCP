using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Api5Adapter;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Api5Adapter.Sta;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Files;

namespace KompasMcp.Worker;

/// <summary>
/// Translates an IPC command frame into one call on the COM session. This is the only place where
/// a command is allowed to reach КОМПАС, so it is also the only place that can enforce the two
/// rules the contract depends on: everything is serialised onto one STA thread, and a command whose
/// result was not observed is reported as OUTCOME_UNKNOWN instead of being retried.
/// </summary>
public sealed class CommandDispatcher
{
    /// <summary>
    /// Commands that must NOT be queued onto the STA lane, because answering them while the CAD
    /// lane is busy is precisely why the Host can tell "КОМПАС busy" apart from "Worker dead".
    /// </summary>
    private static readonly HashSet<string> ControlLaneCommands = new(StringComparer.Ordinal)
    {
        WorkerCommands.Ping,
        WorkerCommands.EnvironmentProbe,
    };

    private readonly StaExecutor _sta;
    private readonly WorkerLog _log;
    private readonly Api5Session _session = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>Контрольные копии файлов документов: снимаются перед каждой мутацией.</summary>
    private readonly DocumentControlCopies _controlCopies = new();

    private long _handled;
    private long _failed;
    private long _timedOut;

    /// <summary>Set when a command's outcome could not be observed; the Host must restart us.</summary>
    public bool NeedsReconciliation { get; private set; }

    public CommandDispatcher(StaExecutor sta, WorkerLog log)
    {
        _sta = sta;
        _log = log;
    }

    public async Task<IpcFrame> HandleAsync(IpcFrame request, CancellationToken cancellationToken)
    {
        var budgetMs = BudgetFor(request);
        Interlocked.Increment(ref _handled);

        try
        {
            var payload = await DispatchAsync(request, budgetMs, cancellationToken).ConfigureAwait(false);
            return new IpcFrame
            {
                ProtocolVersion = IpcFrame.CurrentProtocolVersion,
                RequestId = request.RequestId,
                Kind = IpcFrameKind.Response,
                Command = request.Command,
                Completed = true,
                Payload = payload,
            };
        }
        catch (KompasContractException contract)
        {
            // An unknown outcome is a different kind of failure from a rejection: it means the
            // model may have changed, so the Worker marks itself untrusted until restarted.
            if (contract.Code == ErrorCodes.OutcomeUnknown)
            {
                NeedsReconciliation = true;
            }

            Interlocked.Increment(ref _failed);
            _log.Write("warn", "command refused", new { command = request.Command, code = contract.Code, message = contract.Message });
            return Failure(request, contract.ToErrorDto());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request, new ErrorDto(
                ErrorCodes.CancelNotConfirmed,
                "Команда не была выполнена: сессия завершается.",
                RetryPolicy.SameOperationId,
                null,
                false,
                null));
        }
        catch (Exception ex)
        {
            NeedsReconciliation = true;
            Interlocked.Increment(ref _failed);
            var hresult = ComHResult.From(ex);
            _log.Write("error", "command threw", new { command = request.Command, type = ex.GetType().Name, message = ex.Message, hresult });

            // An unexpected exception during a mutation means the outcome is unknown, not "failed
            // cleanly": saying so is what stops the client from repeating a half-applied change.
            var unknown = ex is COMException && hresult is int code && ComHResult.IsDisconnected(code);
            return Failure(request, new ErrorDto(
                unknown ? ErrorCodes.OutcomeUnknown : Classify(ex, hresult),
                ex.Message,
                unknown ? RetryPolicy.AfterReconciliation : RetryPolicy.SameOperationId,
                hresult,
                unknown,
                new JsonObject { ["exception"] = ex.GetType().Name }));
        }
    }

    private static string Classify(Exception ex, int? hresult) => (ex, hresult) switch
    {
        (_, int code) when ComHResult.IsBusy(code) => ErrorCodes.ComBusy,
        (_, int code) when ComHResult.IsDisconnected(code) => ErrorCodes.ApplicationDisconnected,
        (_, int code) when ComHResult.IsRegistrationFailure(code) => ErrorCodes.ComRegistrationError,
        (COMException, int code) when code == ComHResult.CO_E_SERVER_EXEC_FAILURE => ErrorCodes.LicenseUnavailable,
        (InvalidComObjectException, _) => ErrorCodes.ApplicationDisconnected,
        _ => ErrorCodes.VerificationFailed,
    };

    private static int BudgetFor(IpcFrame request) => request.Command switch
    {
        // The probe can enumerate the ROT and the process list: fast, but not free.
        WorkerCommands.EnvironmentProbe => 10_000,
        WorkerCommands.Ping => 2_000,
        WorkerCommands.Connect => 180_000,
        WorkerCommands.ExportStep or WorkerCommands.ImportStep => 300_000,
        // Растровый снимок рендерит документ и (в файловом режиме) пишет файл: это дольше чтения,
        // но короче конвертера. Бюджет назван числом, а не унаследован от умолчания: снимок
        // большого разрешения измерялся секундами (проба P6), и упор в общий бюджет выглядел бы
        // как отказ продукта.
        WorkerCommands.ExportImage => 240_000,
        // Родное отверстие идёт маршрутом API7 (мост + TransferInterface + RebuildModel): это
        // дольше чисто API5-мутации, поэтому бюджет выше умолчания, а не «на глазок».
        WorkerCommands.Hole => 240_000,
        // Операции над телами B3 идут тем же маршрутом API7 плюс перечитывание всех тел документа
        // после перестроения, поэтому бюджет тот же, что у отверстия.
        WorkerCommands.SolidBoolean or WorkerCommands.SolidSplit
            or WorkerCommands.SolidCutByPlane or WorkerCommands.SolidReposition => 240_000,
        // B5: кинематика и оболочка идут маршрутом API5, но обе перестраивают документ и
        // перечитывают тела после перестроения; сечения идут мостом API7 (TransferInterface на
        // каждое сечение) плюс Rebuild. Бюджет тот же, что у операций над телами, — и по той же
        // причине, а не «на глазок».
        WorkerCommands.Sweep or WorkerCommands.Loft or WorkerCommands.Shell => 240_000,
        // Вспомогательная геометрия идёт маршрутом API7 (мост + QI(IAuxiliaryGeomContainer) +
        // Rebuild), а перечисление читает коллекцию вызовом COM на каждый элемент. Бюджет выше
        // умолчания по той же причине, что у отверстия: это не API5-мутация.
        WorkerCommands.CreateAuxGeometry or WorkerCommands.ListAuxGeometry => 240_000,
        WorkerCommands.UpdatePlane => 240_000,
        // Смена опоры эскиза перестраивает зависимое тело: бюджет тот же, что у прочих мутаций
        // геометрии, а не у́же — измеренная ступень применения (`sketch.Update()`) входит в него.
        WorkerCommands.SetSketchPlane => 240_000,
        WorkerCommands.ListSketchEntities or WorkerCommands.EditSketchEntity => 240_000,
        _ => 120_000,
    };

    private async Task<JsonNode?> DispatchAsync(IpcFrame request, int budgetMs, CancellationToken cancellationToken)
    {
        if (ControlLaneCommands.Contains(request.Command))
        {
            return request.Command switch
            {
                WorkerCommands.Ping => KompJson.ToNode(new JsonObject
                {
                    ["worker_pid"] = Environment.ProcessId,
                    ["worker_utc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["uptime_ms"] = _uptime.ElapsedMilliseconds,
                    ["handled"] = Interlocked.Read(ref _handled),
                    ["failed"] = Interlocked.Read(ref _failed),
                    ["timed_out"] = Interlocked.Read(ref _timedOut),
                    ["needs_reconciliation"] = NeedsReconciliation,
                    ["build"] = BuildIdentity.Collect(),
                    ["sta"] = new JsonObject(_sta.Statistics().ToDictionary(kv => kv.Key, kv => (JsonNode?)JsonValue.Create(kv.Value))),
                    ["com_filter"] = new JsonObject(_sta.MessageFilter.Snapshot().ToDictionary(kv => kv.Key, kv => (JsonNode?)JsonValue.Create(kv.Value))),
                }),
                _ => KompJson.ToNode(EnvironmentSnapshot.Collect()),
            };
        }

        // CAD lane: one STA thread, one command at a time, with an observation budget. A timeout
        // here does not cancel the COM call — it can't — so the result is explicitly unknown.
        var task = request.Command switch
        {
            WorkerCommands.Connect => _sta.Run(() => Connect(request), "connect", cancellationToken),
            WorkerCommands.Disconnect => _sta.Run(() => Disconnect(request), "disconnect", cancellationToken),
            WorkerCommands.ListDocuments => _sta.Run(() => List(request), "doc.list", cancellationToken),
            WorkerCommands.CreateDocument => _sta.Run(() => Create(request), "doc.create", cancellationToken),
            WorkerCommands.OpenDocument => _sta.Run(() => Open(request), "doc.open", cancellationToken),
            WorkerCommands.GetContext => _sta.Run(() => Context(request), "doc.context", cancellationToken),
            WorkerCommands.SaveDocument => _sta.Run(() => Save(request), "doc.save", cancellationToken),
            WorkerCommands.CloseDocument => _sta.Run(() => Close(request), "doc.close", cancellationToken),
            WorkerCommands.ListFeatures => _sta.Run(() => Features(request), "feat.list", cancellationToken),
            WorkerCommands.ListBodies => _sta.Run(() => Bodies(request), "body.list", cancellationToken),
            WorkerCommands.Measure => _sta.Run(() => Measure(request), "geom.measure", cancellationToken),
            WorkerCommands.ResolveSelection => _sta.Run(() => Resolve(request), "geom.resolve", cancellationToken),
            WorkerCommands.ReadTopology => _sta.Run(() => Topology(request), "topo.read", cancellationToken),
            WorkerCommands.CreateSketch => _sta.Run(() => CreateSketch(request), "sketch.create", cancellationToken),
            WorkerCommands.EditSketch => _sta.Run(() => EditSketch(request), "sketch.edit", cancellationToken),
            WorkerCommands.SetSketchPlane => _sta.Run(() => SetSketchPlane(request), "sketch.set_plane", cancellationToken),
            WorkerCommands.FinishSketch => _sta.Run(() => FinishSketch(request), "sketch.finish", cancellationToken),
            WorkerCommands.SketchStatus => _sta.Run(() => SketchStatus(request), "sketch.status", cancellationToken),
            WorkerCommands.Extrude => _sta.Run(() => Extrude(request), "feat.extrude", cancellationToken),
            WorkerCommands.Fillet => _sta.Run(() => Fillet(request), "feat.fillet", cancellationToken),
            WorkerCommands.Chamfer => _sta.Run(() => Chamfer(request), "feat.chamfer", cancellationToken),
            WorkerCommands.Hole => _sta.Run(() => Hole(request), "feat.hole", cancellationToken),
            WorkerCommands.Rotated => _sta.Run(() => Rotated(request), "feat.rotated", cancellationToken),
            WorkerCommands.Sweep => _sta.Run(() => Sweep(request), "feat.sweep", cancellationToken),
            WorkerCommands.Loft => _sta.Run(() => Loft(request), "feat.loft", cancellationToken),
            WorkerCommands.Shell => _sta.Run(() => Shell(request), "feat.shell", cancellationToken),
            WorkerCommands.SolidBoolean => _sta.Run(() => SolidBoolean(request), "solid.boolean", cancellationToken),
            WorkerCommands.SolidSplit => _sta.Run(() => SolidSplit(request), "solid.split", cancellationToken),
            WorkerCommands.SolidCutByPlane => _sta.Run(() => SolidCutByPlane(request), "solid.cut_by_plane", cancellationToken),
            WorkerCommands.SolidReposition => _sta.Run(() => SolidReposition(request), "solid.reposition", cancellationToken),
            WorkerCommands.GetFeature => _sta.Run(() => Feature(request), "feat.get", cancellationToken),            WorkerCommands.UpdateFeature => _sta.Run(() => UpdateFeature(request), "feat.update", cancellationToken),
            WorkerCommands.PatternGrid => _sta.Run(() => PatternGrid(request), "pattern.grid", cancellationToken),
            WorkerCommands.PatternCircular => _sta.Run(() => PatternCircular(request), "pattern.circular", cancellationToken),
            WorkerCommands.PatternMirror => _sta.Run(() => PatternMirror(request), "pattern.mirror", cancellationToken),
            WorkerCommands.PatternRead => _sta.Run(() => PatternRead(request), "pattern.read", cancellationToken),
            WorkerCommands.SuppressFeature => _sta.Run(() => SuppressFeature(request), "feat.suppress", cancellationToken),
            WorkerCommands.DeleteFeature => _sta.Run(() => DeleteFeature(request), "feat.delete", cancellationToken),
            WorkerCommands.Rebuild => _sta.Run(() => Rebuild(request), "doc.rebuild", cancellationToken),
            WorkerCommands.ExportStep => _sta.Run(() => ExportStep(request), "export.step", cancellationToken),
            WorkerCommands.ImportStep => _sta.Run(() => ImportStep(request), "import.step", cancellationToken),
            WorkerCommands.ExportImage => _sta.Run(() => ExportImage(request), "export.image", cancellationToken),
            WorkerCommands.UnitProbe => _sta.Run(() => UnitProbe(request), "probe.units", cancellationToken),
            WorkerCommands.CreateAuxGeometry => _sta.Run(() => CreateAuxGeometry(request), "aux.create", cancellationToken),
            WorkerCommands.ListAuxGeometry => _sta.Run(() => ListAuxGeometry(request), "aux.list", cancellationToken),
            WorkerCommands.UpdatePlane => _sta.Run(() => UpdatePlane(request), "aux.update_plane", cancellationToken),
            WorkerCommands.ListSketchEntities => _sta.Run(() => ListSketchEntities(request), "sketch.entities", cancellationToken),
            WorkerCommands.EditSketchEntity => _sta.Run(() => EditSketchEntity(request), "sketch.entity_edit", cancellationToken),
            WorkerCommands.Shutdown => _sta.Run(ShutdownPayload, "shutdown", cancellationToken),
            _ => throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Команда '{request.Command}' не реализована в Worker этой сборки.",
                RetryPolicy.Never),
        };

        var completed = await Task.WhenAny(task, Task.Delay(budgetMs, cancellationToken)).ConfigureAwait(false);
        if (completed != task)
        {
            Interlocked.Increment(ref _timedOut);
            NeedsReconciliation = true;
            _log.Write("error", "command budget expired", new { command = request.Command, budget_ms = budgetMs });
            throw new KompasContractException(
                ErrorCodes.OutcomeUnknown,
                $"Команда '{request.Command}' не завершилась за {budgetMs / 1000} с. КОМПАС может продолжать её выполнять; " +
                "повтор запрещён, требуется согласование по фактическому состоянию модели.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        var outcome = await task.ConfigureAwait(false);
        return outcome is null ? null : KompJson.ToNode(outcome);
    }

    private static T Argument<T>(IpcFrame request)
    {
        if (request.Payload is null)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, $"Команда '{request.Command}' не получила payload.");
        }

        // Round-tripping through the JSON text rather than JsonNode.Deserialize<T> keeps one
        // serialisation contract (snake_case, enum naming) shared with the Host.
        //
        // A payload that does not fit the typed contract is an ARGUMENT defect, not a failure of the
        // Worker: the caller sent a field of the wrong shape, an unknown enum member or a missing
        // required property, and can fix it. Letting the raw JsonException escape made the generic
        // catch classify it as VERIFICATION_FAILED with NeedsReconciliation=true, which told the
        // client the OUTCOME was unknown and to reconcile — measured on 19.09.2026 by client
        // acceptance B3, where plane.base hit exactly this path and the declared
        // CAPABILITY_UNAVAILABLE became unreachable. The typed shape is fixed in the contract
        // (CutPlaneDto); this catch is the second line of defence for hand-built IPC frames, and it
        // names the JSON path instead of hiding the defect behind "verification failed".
        try
        {
            return JsonSerializer.Deserialize<T>(request.Payload.ToJsonString(), KompJson.Options)
                ?? throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Payload команды '{request.Command}' не разбирается как {typeof(T).Name}.");
        }
        catch (JsonException ex)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Payload команды '{request.Command}' не соответствует контракту {typeof(T).Name}: {ex.Message}",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["path"] = ex.Path,
                    ["line"] = ex.LineNumber,
                    ["byte_position"] = ex.BytePositionInLine,
                    ["code"] = "payload_not_matching_contract",
                });
        }
    }

    /// <summary>
    /// Every mutation answer carries the revision the client needs for its next call, plus the
    /// document it belongs to. Without it a caller would have to re-read the context after each
    /// edit, and a guessed revision is exactly the silent race the contract is meant to prevent.
    /// </summary>
    /// <remarks>
    /// The mutation runs FIRST and the revision is read AFTER it. Evaluating
    /// <c>document.Revision</c> as an argument alongside the mutation call reports the pre-mutation
    /// revision — which C# does by evaluating arguments left to right, and which made the next
    /// legitimate command fail with REVISION_CONFLICT against a revision that never existed.
    /// </remarks>
    private JsonNode? TaggedAfter(string documentId, Func<object?> mutation, DocumentEntry document)
    {
        // КОНТРОЛЬНАЯ КОПИЯ СНИМАЕТСЯ ДО МУТАЦИИ, и это единственная точка, через которую проходят
        // ВСЕ мутации: разложенная по обработчикам, она неизбежно отстала бы от списка команд.
        var copy = _controlCopies.Before(document.Path, document.Id, document.Revision);
        object? outcome;
        try
        {
            outcome = mutation();
        }
        catch (Exception ex)
        {
            // СБОЙ: файл возвращается к состоянию до мутации. Модель в памяти НЕ откатывается —
            // это названо и в ответе, и в причине, а не выдано за полный откат.
            var restoreFailure = copy.Made ? _controlCopies.Restore(document.Path, copy.Path) : null;
            throw Reclassify(ex, copy, restoreFailure);
        }

        var node = Tagged(documentId, document.Revision, outcome) ?? new JsonObject();
        // Копия называется и СТРОКОЙ, и ПОЛЯМИ. Строка читается человеком, поля — прибором: разбор
        // человекочитаемой формулировки сделал бы приёмку зависящей от редакции текста.
        node["control_copy"] = DocumentControlCopies.Describe(copy);
        node["control_copy_made"] = copy.Made;
        node["control_copy_path"] = copy.Path;
        return node;
    }

    /// <summary>
    /// Донести до клиента, что контрольная копия снята и что при сбое с ней стало. Исходный код и
    /// политика повтора СОХРАНЯЮТСЯ: сбой не превращается в другой сбой из-за того, что рядом с ним
    /// появилась копия.
    /// </summary>
    private static Exception Reclassify(Exception ex, ControlCopyResult copy, string? restoreFailure)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["control_copy"] = DocumentControlCopies.Describe(copy),
            ["control_copy_made"] = copy.Made,
            ["control_copy_path"] = copy.Path,
            ["restored"] = copy.Made && restoreFailure is null,
            ["restore_failure"] = restoreFailure,
            ["rollback_scope"] = "файл документа; модель в памяти КОМПАСа не откатывается",
        };

        if (ex is KompasContractException contract)
        {
            if (contract.Details is not null)
            {
                foreach (var (key, value) in contract.Details)
                {
                    details[key] = value;
                }
            }

            return new KompasContractException(
                contract.Code,
                contract.Message,
                contract.RetryPolicy,
                contract.PartialEffects,
                contract.Hresult,
                details);
        }

        return new KompasContractException(
            ErrorCodes.GeometryFailed,
            "Мутация не завершилась: " + ex.Message,
            RetryPolicy.SameOperationId,
            partialEffects: true,
            details: details);
    }

    private JsonNode? Tagged(string documentId, long revision, object? outcome)
    {
        var node = outcome is null
            ? new JsonObject()
            : KompJson.ToNode(outcome)?.AsObject() ?? new JsonObject();

        node["document_id"] = documentId;
        node["revision"] = revision;
        return node;
    }

    private static IpcFrame Failure(IpcFrame request, ErrorDto error) => new()
    {
        ProtocolVersion = IpcFrame.CurrentProtocolVersion,
        RequestId = request.RequestId,
        Kind = IpcFrameKind.Response,
        Command = request.Command,
        Completed = true,
        Error = error,
    };

    private object? Connect(IpcFrame request)
    {
        var command = Argument<ConnectCommand>(request);
        var application = _session.Connect(command);

        // A launched КОМПАС needs up to a minute on a cold start; report what it actually took.
        var openDocuments = _session.Documents.Count(d => d.ApplicationId == application.Id);
        return application.ToDto(openDocuments);
    }

    private object? Disconnect(IpcFrame request)
    {
        var command = Argument<DisconnectCommand>(request);
        _session.Disconnect(command.ApplicationId, command.CloseOwnedApplication);
        return new JsonObject { ["application_id"] = command.ApplicationId, ["disconnected"] = true };
    }

    private object? List(IpcFrame request)
    {
        var command = Argument<ListDocumentsCommand>(request);
        return _session.Documents
            .Where(d => d.ApplicationId == command.ApplicationId)
            .Select(d => _session.Context(d, includeTopology: false))
            .ToList();
    }

    private object? Create(IpcFrame request)
    {
        var document = _session.CreateDocument(Argument<CreateDocumentCommand>(request));
        return _session.Context(document, includeTopology: false);
    }

    private object? Open(IpcFrame request)
    {
        var document = _session.OpenDocument(Argument<OpenDocumentCommand>(request));
        return _session.Context(document, includeTopology: false);
    }

    private object? Context(IpcFrame request)
    {
        var command = Argument<GetContextCommand>(request);
        return _session.Context(_session.RequireDocument(command.DocumentId), command.Detail == "full");
    }

    private object? Save(IpcFrame request)
    {
        var command = Argument<SaveDocumentCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return _session.SaveDocument(document, command.TargetPath);
    }

    private object? Close(IpcFrame request)
    {
        var command = Argument<CloseDocumentCommand>(request);
        _session.CloseDocument(_session.RequireDocument(command.DocumentId), command.DirtyPolicy);
        return new JsonObject { ["document_id"] = command.DocumentId, ["closed"] = true };
    }

    private object? CreateSketch(IpcFrame request)
    {
        var command = Argument<CreateSketchCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.CreateSketch(command), document);
    }

    private object? EditSketch(IpcFrame request)
    {
        var command = Argument<EditSketchCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.EditSketch(command), document);
    }

    /// <summary>
    /// Смена опорной плоскости эскиза — мутация: ревизия поднимается, ссылки документа переезжают
    /// вместе с ней. Проверка ревизии идёт до COM, как у прочих мутаций: ссылка на эскиз уже несёт
    /// документ, поэтому <c>RequireDocument</c> здесь не нужен — его делает
    /// <c>DocumentForReference</c>.
    /// </summary>
    private object? SetSketchPlane(IpcFrame request)
    {
        var command = Argument<SetSketchPlaneCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.SetSketchPlane(command), document);
    }

    private object? FinishSketch(IpcFrame request)
    {
        var command = Argument<FinishSketchCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        return TaggedAfter(document.Id, () => _session.FinishSketch(command), document);
    }

    private object? Extrude(IpcFrame request)
    {
        var command = Argument<ExtrudeCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Extrude(command), document);
    }

    private object? Fillet(IpcFrame request)
    {
        var command = Argument<FilletCommand>(request);
        if (command.EdgeRefs is not { Count: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs должен содержать хотя бы одно ребро.");
        }

        // The document is discovered from the first edge reference — the same rule as extrude, and
        // the reason a reference from another document cannot be smuggled into this mutation.
        var document = _session.DocumentForReference(command.EdgeRefs[0]);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Fillet(command), document);
    }

    private object? Chamfer(IpcFrame request)
    {
        var command = Argument<ChamferCommand>(request);
        if (command.EdgeRefs is not { Count: > 0 })
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "edge_refs должен содержать хотя бы одно ребро.");
        }

        // Документ берётся из первого ребра — то же правило, что у скругления: ссылка из другого
        // документа не может протащиться в эту мутацию.
        var document = _session.DocumentForReference(command.EdgeRefs[0]);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Chamfer(command), document);
    }

    private object? Hole(IpcFrame request)
    {
        var command = Argument<HoleCommand>(request);

        // Документ берётся из опорной грани: отверстие адресуется поверхностью, на которой оно
        // начинается, и ссылка из другого документа не может протащиться в эту мутацию.
        var document = _session.DocumentForReference(command.FaceRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Hole(command), document);
    }

    private object? Rotated(IpcFrame request)
    {
        var command = Argument<RotatedCommand>(request);

        // Документ берётся из эскиза-профиля: вращение адресуется телом развёртки, и ссылка из
        // другого документа не может протащиться в эту мутацию. Ось задаётся координатами модели,
        // а не ссылкой, поэтому второго источника документа здесь нет — и это осознанно: ссылка на
        // ось позволила бы взять её из чужой детали, что вращением не проверялось.
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Rotated(command), document);
    }

    /// <summary>
    /// Кинематическая операция (SM-04). Документ берётся из ЭСКИЗА-ПРОФИЛЯ — того же правила, что
    /// у выдавливания и вращения: профиль адресует операцию, и ссылка из другой детали не может
    /// протащиться в мутацию.
    /// </summary>
    /// <remarks>
    /// Траектория — ВТОРАЯ ссылка, и её принадлежность тому же документу проверяет адаптер (он
    /// отказывает именованным <c>INVALID_ARGUMENT</c> с обоими идентификаторами). Здесь она не
    /// «приводится» к названному документу: подмена одной ссылки другой — это молчаливая работа не
    /// там, где просили. Документ берётся ровно из одной ссылки, а не из <c>document_id</c>: двух
    /// независимых утверждений о месте работы у этой операции нет.
    /// </remarks>
    private object? Sweep(IpcFrame request)
    {
        var command = Argument<SweepCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Sweep(command), document);
    }

    /// <summary>
    /// Элемент по сечениям (SM-05). Документ берётся по <c>document_id</c>, а не по ссылке на
    /// сечение: сечений несколько, и «какое из них главное» — вопрос без ответа. Адаптер сам сверяет,
    /// что каждое сечение принадлежит этому документу, и отказывает иначе.
    /// </summary>
    private object? Loft(IpcFrame request)
    {
        var command = Argument<LoftCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Loft(command), document);
    }

    /// <summary>
    /// Оболочка (SM-13). Документ берётся по <c>document_id</c> — по той же причине, что у сечений:
    /// удаляемых граней может быть несколько, и «главная» из них не определена. Адаптер сверяет
    /// каждую грань с названным документом и отказывает иначе.
    /// </summary>
    private object? Shell(IpcFrame request)
    {
        var command = Argument<ShellCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Shell(command), document);
    }

    private object? SolidBoolean(IpcFrame request)
    {
        var command = Argument<BooleanCommand>(request);

        // Документ берётся из ТЕЛО-ЦЕЛИ, а не из document_id: так ссылка из другой детали не может
        // протащиться в мутацию. Дополнительно сверяется, что вызывающий назвал тот же документ,
        // который назвала ссылка, — расхождение это ошибка адресации, а не «используем что дали».
        var document = DocumentForTarget(command.DocumentId, command.TargetBodyRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.SolidBoolean(command), document);
    }

    private object? SolidSplit(IpcFrame request)
    {
        var command = Argument<SplitCommand>(request);
        var document = DocumentForTarget(command.DocumentId, command.TargetBodyRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.SolidSplit(command), document);
    }

    private object? SolidCutByPlane(IpcFrame request)
    {
        var command = Argument<CutByPlaneCommand>(request);
        var document = DocumentForTarget(command.DocumentId, command.TargetBodyRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.SolidCutByPlane(command), document);
    }

    private object? SolidReposition(IpcFrame request)
    {
        var command = Argument<RepositionCommand>(request);
        var document = DocumentForTarget(command.DocumentId, command.TargetBodyRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.SolidReposition(command), document);
    }

    /// <summary>
    /// Документ операции B3: из ссылки на тело-цель, со сверкой названного идентификатора.
    /// </summary>
    /// <remarks>
    /// Ссылка и <c>document_id</c> — два независимых утверждения вызывающего о том, где идёт работа.
    /// Если они расходятся, доверять одному из них значило бы молча работать не там, где просили:
    /// ссылка из другой детали отвергается, а не «приводится» к названному документу.
    /// </remarks>
    private DocumentEntry DocumentForTarget(string documentId, string targetBodyRef)
    {
        var document = _session.DocumentForReference(targetBodyRef);
        if (!string.Equals(document.Id, documentId, StringComparison.Ordinal))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"target_body_ref принадлежит документу '{document.Id}', а назван документ '{documentId}'.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = documentId,
                    ["reference_document_id"] = document.Id,
                    ["target_body_ref"] = targetBodyRef,
                });
        }

        return document;
    }

    /// <summary>Параметры признака — чтение, идёт мимо журнала, как список признаков и measure.</summary>
    private object? Feature(IpcFrame request) => _session.GetFeature(Argument<GetFeatureCommand>(request));

    /// <summary>
    /// Массив по сетке (SM-18). Документ берётся по <c>document_id</c>, а не по ссылке на исходный
    /// объект: исходных объектов может быть несколько, и «какой из них главный» — вопрос без
    /// ответа. Адаптер сам сверяет, что каждая ссылка принадлежит этому документу, и отказывает
    /// иначе.
    /// </summary>
    private object? PatternGrid(IpcFrame request)
    {
        var command = Argument<PatternGridCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.PatternGrid(command), document);
    }

    /// <summary>Массив по концентрической сетке (SM-19). Документ — по <c>document_id</c>.</summary>
    private object? PatternCircular(IpcFrame request)
    {
        var command = Argument<PatternCircularCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.PatternCircular(command), document);
    }

    /// <summary>Зеркальный массив (SM-23). Документ — по <c>document_id</c>.</summary>
    private object? PatternMirror(IpcFrame request)
    {
        var command = Argument<PatternMirrorCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.PatternMirror(command), document);
    }

    /// <summary>
    /// Чтение параметров массива. Идёт мимо журнала мутаций: оно ничего не меняет. Ревизия
    /// возвращается, но не поднимается — тем же правилом, что у <c>get_feature</c> и
    /// <c>sketch.status</c>.
    /// </summary>
    private object? PatternRead(IpcFrame request)
    {
        var command = Argument<PatternReadCommand>(request);
        var document = _session.DocumentForReference(command.FeatureRef);
        return Tagged(document.Id, document.Revision, _session.PatternRead(command));
    }
    /// <summary>
    /// Определённость эскиза — чтение, идёт мимо журнала мутаций по той же причине, что
    /// <c>measure</c> и <c>get_feature</c>: оно ничего не создаёт и не меняет. Ревизия при этом
    /// возвращается, чтобы клиент мог продолжить цепочку, но НЕ поднимается: измерено (S.7), что
    /// пятикратное чтение статуса не изменило ни объём, ни счётчики топологии.
    /// </summary>
    /// <remarks>
    /// <c>Tagged</c>, а не <c>TaggedAfter</c>: поднимать ревизию за читающий вызов значило бы
    /// объявить модель изменённой, чего не произошло. Ревизия берётся из реестра ссылок — она уже
    /// сверена в <c>RequireSketch</c> внутри адаптера.
    /// </remarks>
    private object? SketchStatus(IpcFrame request)
    {
        var command = Argument<GetSketchStatusCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        return Tagged(document.Id, document.Revision, _session.GetSketchStatus(command));
    }

    private object? UpdateFeature(IpcFrame request)
    {
        var command = Argument<UpdateFeatureCommand>(request);
        var document = _session.DocumentForReference(command.FeatureRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.UpdateFeature(command), document);
    }

    private object? SuppressFeature(IpcFrame request)
    {
        var command = Argument<SuppressFeatureCommand>(request);
        var document = _session.DocumentForReference(command.FeatureRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.SetFeatureSuppressed(command), document);
    }

    private object? DeleteFeature(IpcFrame request)
    {
        var command = Argument<DeleteFeatureCommand>(request);
        var document = _session.DocumentForReference(command.FeatureRef);
        GuardRevision(document, command.ExpectedRevision);
        // Ответ приходит без ссылки на признак: он удалён, и выдавать «живой» handle на него было
        // бы обещанием, которое сервер выполнить не может.
        return TaggedAfter(document.Id, () => _session.DeleteFeature(command), document);
    }

    private object? Rebuild(IpcFrame request)
    {
        var command = Argument<RebuildCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.Rebuild(command), document);
    }

    private object? ExportStep(IpcFrame request)
    {
        var command = Argument<ExportStepCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ExportStep(command));
    }

    private object? ExportImage(IpcFrame request)
    {
        var command = Argument<ExportImageCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ExportImage(command));
    }

    private static void GuardRevision(DocumentEntry document, long expectedRevision)
    {
        if (expectedRevision == document.Revision)
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.RevisionConflict,
            $"Ожидалась ревизия {expectedRevision}, фактически {document.Revision}: модель изменилась после чтения контекста.",
            RetryPolicy.ReacquireContext,
            details: new Dictionary<string, object?>
            {
                ["expected_revision"] = expectedRevision,
                ["current_revision"] = document.Revision,
            });
    }

    private object? Features(IpcFrame request) => _session.ListFeatures(Argument<ListFeaturesCommand>(request));

    private object? Bodies(IpcFrame request) => _session.ListBodies(Argument<ListBodiesCommand>(request));

    private object? Measure(IpcFrame request) => _session.Measure(Argument<MeasureCommand>(request));

    private object? Resolve(IpcFrame request) => _session.ResolveSelection(Argument<ResolveSelectionCommand>(request));

    private object? Topology(IpcFrame request) => _session.ReadTopology(Argument<ReadTopologyCommand>(request));

    private object? ImportStep(IpcFrame request) => _session.ImportStep(Argument<ImportStepCommand>(request));

    private object? UnitProbe(IpcFrame request) => _session.UnitProbe(Argument<UnitProbeCommand>(request));

    /// <summary>
    /// Создание объекта вспомогательной геометрии — мутация: ревизия поднимается, ссылки документа
    /// переезжают вместе с ней. Проверка ожидаемой ревизии обязательна по той же причине, что у
    /// остальных мутаций: без неё две правки одной модели переплелись бы без предупреждения.
    /// </summary>
    private object? CreateAuxGeometry(IpcFrame request)
    {
        var command = Argument<CreateAuxGeometryCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.CreateAuxGeometry(command), document);
    }

    /// <summary>
    /// Перечисление вспомогательной геометрии — чтение. Ревизия возвращается, но НЕ поднимается:
    /// обход коллекций ничего не создаёт и не меняет, и объявить модель изменённой значило бы
    /// обесценить ссылки вызывающего за вызов, который их не трогал.
    /// </summary>
    private object? ListAuxGeometry(IpcFrame request)
    {
        var command = Argument<ListAuxGeometryCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ListAuxGeometry(command));
    }

    /// <summary>
    /// Правка существующей плоскости — мутация: ревизия поднимается, ссылки документа переезжают
    /// вместе с ней.
    /// </summary>
    private object? UpdatePlane(IpcFrame request)
    {
        var command = Argument<UpdatePlaneCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.UpdatePlane(command), document);
    }

    /// <summary>
    /// Перечисление сущностей эскиза — чтение: ревизия возвращается, но НЕ поднимается. Вход в
    /// эскиз идёт на чтение (<c>BeginEditEx(true)</c>), поэтому обход не меняет модель, и объявить
    /// её изменённой значило бы обесценить ссылки вызывающего за вызов, который их не трогал.
    /// </summary>
    private object? ListSketchEntities(IpcFrame request)
    {
        var command = Argument<ListSketchEntitiesCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        return Tagged(document.Id, document.Revision, _session.ListSketchEntities(command));
    }

    /// <summary>Адресная правка одной сущности эскиза — мутация.</summary>
    private object? EditSketchEntity(IpcFrame request)
    {
        var command = Argument<EditSketchEntityCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.EditSketchEntity(command), document);
    }

    private object? ShutdownPayload()
    {
        ShutdownOwnedSessions();
        return new JsonObject { ["sessions_closed"] = true };
    }

    /// <summary>
    /// Detach from every session. Documents belonging to the server are closed without saving;
    /// an attached КОМПАС is left running because it is the user's process.
    /// </summary>
    public void ShutdownOwnedSessions()
    {
        foreach (var application in _session.Applications.ToArray())
        {
            try
            {
                _session.Disconnect(application.Id, closeOwnedApplication: application.Ownership == ApplicationOwnership.Launched);
            }
            catch (Exception ex)
            {
                _log.Write("warn", "session shutdown failed", new { application = application.Id, type = ex.GetType().Name });
            }
        }
    }
}

/// <summary>
/// JSONL worker log: one object per line, UTC timestamps, bounded size. Diagnostics never go to
/// the pipe (that channel carries frames only) and never to the Host's stdout.
/// </summary>
public sealed class WorkerLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;

    /// <summary>File the log is written to, or null when it went to stderr.</summary>
    public string? FilePath { get; }

    private WorkerLog(StreamWriter? writer, string? path)
    {
        _writer = writer;
        FilePath = path;
    }

    public static WorkerLog Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new WorkerLog(null, null);
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 8L * 1024 * 1024)
            {
                System.IO.File.Delete(path);
            }

            return new WorkerLog(new StreamWriter(path, append: true) { AutoFlush = false }, path);
        }
        catch (IOException)
        {
            // A log that cannot be written must not stop the CAD lane; stderr still gets the line.
            return new WorkerLog(null, null);
        }
    }

    public void Write(string level, string message, object? fields = null)
    {
        var line = Build(level, message, fields);
        lock (_gate)
        {
            _writer?.WriteLine(line);
            _writer?.Flush();
        }

        if (_writer is null)
        {
            Console.Error.WriteLine(line);
        }
    }

    private string Build(string level, string message, object? fields)
    {
        var node = FieldsNode(fields);
        node["ts_utc"] = DateTimeOffset.UtcNow.ToString("O");
        node["level"] = level;
        node["message"] = message;
        node["worker_pid"] = Environment.ProcessId;
        return node.ToJsonString(KompJson.Options);
    }

    private static JsonObject FieldsNode(object? fields)
    {
        if (fields is null)
        {
            return new JsonObject();
        }

        return JsonSerializer.SerializeToNode(fields, KompJson.Options)?.AsObject() ?? new JsonObject();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
    }
}

/// <summary>
/// Startup environment facts, gathered without touching КОМПАС so a wedged CAD lane cannot hide
/// them from kompas_health.
/// </summary>
internal static class EnvironmentSnapshot
{
    public static object Collect() => new
    {
        workerRuntime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        processBitness = Environment.Is64BitProcess ? "x64" : "x86",
        machineBitness = Environment.Is64BitOperatingSystem ? "x64" : "x86",
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        interopDirectory = KompasInteropResolver.ResolvedDirectory,
        interopProbed = KompasInteropResolver.ProbedDirectories,
        interopLoaded = KompasInteropResolver.LoadedAssemblies,
        localServer = KompasInteropResolver.LocalServerPath(Api5Session.KompasProgId),
        runningInstances = KompasInteropResolver.SnapshotProcessIds("KOMPAS").Length,
    };
}
