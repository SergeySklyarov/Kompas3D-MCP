using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Api5Adapter;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Api5Adapter.Sta;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Applications;
using KompasMcp.Domain.Documents;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.Com;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Worker;

/// <summary>Translates an IPC command frame into one call on the COM session. The only place a command may reach
/// KOMPAS: everything is serialised onto one STA thread, and an unobserved result is OUTCOME_UNKNOWN.</summary>
public sealed class CommandDispatcher
{
    // INVARIANT: the lane, the action kind and the budget of a command are read from the single table
    // in Contracts, never restated here. Two copies are what let the Host give up on a command before
    // the Worker that was still running it did.
    // History: docs/decisions/contracts.md#budgets-single-table

    private readonly StaExecutor _sta;
    private readonly WorkerLog _log;
    private readonly Api5Session _session = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>The session-level mark that a connection loss was observed anywhere in the adapter.</summary>
    /// <remarks>WHY THIS EXISTS. MEASURED: a killed KOMPAS left ~290 local catches in the adapter answering
    /// their defaults, so a READ came back `succeeded` with an empty list or a -1 counter instead of naming
    /// the loss. The mark is fed by <c>FirstChanceException</c>, which sees the exception BEFORE any local
    /// catch, and a guarded read answers <c>APPLICATION_DISCONNECTED</c> when it is set.
    /// History: docs/decisions/adapter-core.md#disconnect-latch</remarks>
    private readonly ComDisconnectLatch _latch = new();

    /// <summary>Control copies of document files, taken before every mutation into the SERVICE directory.</summary>
    private readonly DocumentControlCopies _controlCopies;

    private long _handled;
    private long _failed;
    private long _timedOut;

    /// <summary>Job objects holding the instances this server launched, one per launch.</summary>
    /// <remarks>INVARIANT: the reference is held for the life of the process on purpose — the OS closing
    /// these handles at exit is what terminates an instance whose Worker died before its own shutdown.
    /// History: docs/decisions/host.md#client-shutdown</remarks>
    private readonly List<OwnedProcessJob> _ownedJobs = new();

    /// <summary>Set when a command's outcome could not be observed; the Host must restart us.</summary>
    public bool NeedsReconciliation { get; private set; }

    public CommandDispatcher(StaExecutor sta, WorkerLog log, string controlCopyDirectory)
    {
        _sta = sta;
        _log = log;
        _controlCopies = new DocumentControlCopies(controlCopyDirectory);

        // The one place the latch is fed. The handler runs on the thread that THREW, synchronously,
        // before the catch that would swallow the loss; it must therefore stay trivial and non-throwing.
        // A loss that is NOT ours is named in the log: "the mark was not set" and "a foreign loss was
        // seen and ignored" are different states, and only the log tells them apart.
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            try
            {
                var before = _latch.IgnoredOutsideSession;
                _latch.Observe(args.Exception);
                if (_latch.IgnoredOutsideSession != before)
                {
                    _log.Write("warn", "connection loss outside a session command, NOT applied", new
                    {
                        type = args.Exception.GetType().Name,
                        message = args.Exception.Message,
                        hresult = ComHResult.From(args.Exception),
                        hresult_name = ComHResult.From(args.Exception) is int code ? ComHResult.Name(code) : null,
                        on_sta_thread = Environment.CurrentManagedThreadId == _sta.StaThreadId,
                    });
                }
            }
            catch
            {
                // A FirstChanceException handler that throws would replace the exception being observed;
                // the log is best effort, the observation is not.
            }
        };
    }

    public async Task<IpcFrame> HandleAsync(IpcFrame request, CancellationToken cancellationToken)
    {
        var budgetMs = WorkerCommandTable.BudgetMs(request.Command);
        Interlocked.Increment(ref _handled);

        // The observation window is open for exactly one session command: outside it a connection-class
        // exception belongs to somebody else (a foreign ROT entry, another instance's dead object) and
        // must not mark OUR session. The window closes on every exit, including the refusal paths.
        var observesLoss = WorkerCommandTable.ObservesConnectionLoss(request.Command);
        if (observesLoss)
        {
            _latch.Arm(_sta.StaThreadId);
        }

        try
        {
            var payload = await DispatchAsync(request, budgetMs, cancellationToken).ConfigureAwait(false);
            await GuardDisconnectedReadAsync(request, cancellationToken).ConfigureAwait(false);
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
            // The refusal's own measurements go to the journal too, not only to the client: a refusal that
            // happens once in dozens of runs is investigated long after the session is gone, and the
            // response is not kept. Written as a separate line so the existing line's shape is unchanged.
            if (contract.Details is not null)
            {
                _log.Write("warn", "command refused details", new { command = request.Command, code = contract.Code, details = contract.Details });
            }

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
            // AN UNEXPECTED EXCEPTION IN A MODEL-CHANGING COMMAND IS NOT A "CLEAN FAILURE": the flag comes
            // from the single command table, so the client is not told to repeat an applied mutation,
            // while a file-writing read is judged like a read.
            var changesModel = WorkerCommandTable.ChangesModel(request.Command);

            if (changesModel)
            {
                NeedsReconciliation = true;
            }

            Interlocked.Increment(ref _failed);
            var hresult = ComHResult.From(ex);
            _log.Write("error", "command threw", new { command = request.Command, type = ex.GetType().Name, message = ex.Message, hresult, changesModel });

            // A connection loss that escaped every local catch. In a MODEL-CHANGING command the outcome is
            // unknown and the client must reconcile first. In a READ (and in a file-writing read) nothing
            // was changed, so "outcome unknown" would be a false alarm: the loss itself is the answer, and
            // reconnecting is the remedy.
            // The loss is seen in TWO shapes, and both must be caught: the exception in hand may be the
            // disconnect itself, or a consequence of one observed during this command whose own HRESULT
            // carries nothing (MEASURED: `kompas_measure` on a dead body throws InvalidCastException).
            var disconnected = ComHResult.IsDisconnected(ex) || _latch.IsSet;
            if (!changesModel && disconnected)
            {
                return Failure(request, DisconnectedRead(request.Command, ex).ToErrorDto());
            }

            // An unexpected exception during a mutation means the outcome is unknown, not "failed
            // cleanly": saying so is what stops the client from repeating a half-applied change.
            return Failure(request, new ErrorDto(
                changesModel ? ErrorCodes.OutcomeUnknown : Classify(ex, hresult),
                ex.Message,
                changesModel ? RetryPolicy.AfterReconciliation : RetryPolicy.SameOperationId,
                hresult,
                changesModel,
                new JsonObject
                {
                    ["exception"] = ex.GetType().Name,
                    ["changes_model"] = changesModel,
                }));
        }
        finally
        {
            if (observesLoss)
            {
                _latch.Disarm();
            }
        }
    }

    /// <summary>Refuse a read whose answer was built while the session-level loss mark is set.</summary>
    /// <remarks>INVARIANT: this runs AFTER the command and BEFORE the answer leaves, so a read that
    /// swallowed the loss in a local catch cannot pass its default off as a measurement. The mark is
    /// cleared by a successful <c>kompas_connect</c>, or by the CONFIRMATION below.
    /// History: docs/decisions/adapter-core.md#disconnect-latch</remarks>
    private async Task GuardDisconnectedReadAsync(IpcFrame request, CancellationToken cancellationToken)
    {
        if (request.Command == WorkerCommands.Connect)
        {
            // The reconnect SUCCEEDED (DispatchAsync returned without throwing): the session the mark
            // described is gone, so the mark goes with it. Clearing it on a FAILED connect would let the
            // next read publish defaults again.
            _latch.Reset();
            return;
        }

        if (!WorkerCommandTable.MustNotAnswerAcrossALoss(request.Command) || !_latch.IsSet)
        {
            return;
        }

        // CONFIRM THE MARK AGAINST OUR OWN OBJECT BEFORE REFUSING. The observer cannot tell whose COM call
        // failed, and a stale ROT entry of a DEAD FOREIGN instance raises the same class of error; binding
        // such an entry happens inside `kompas_connect mode=attach`. A live answer from OUR application
        // means the loss was not ours: the mark is lifted and NAMED, and the read proceeds.
        // The route is captured BEFORE the probe, else the refusal would name `app.probe` — the
        // instrument — instead of the call that failed.
        var route = _sta.CurrentLabel;
        var alive = await _sta.Run(
            () => _session.ProbeApplication(
                ReadString(request.Payload, "application_id"),
                ReadString(request.Payload, "document_id")),
            "app.probe",
            cancellationToken).ConfigureAwait(false);

        if (alive == true)
        {
            var observed = _latch.Observation;
            var reason = "наше приложение ответило на дешёвое обращение: потеря, наблюдённая защёлкой, "
                         + "принадлежит ЧУЖОМУ COM-объекту (например, записи погибшего экземпляра в ROT)";
            _latch.ClearWithReason(reason);
            _log.Write("warn", "disconnect mark cleared: our application answers", new
            {
                command = request.Command,
                route,
                reason,
                observed,
            });
            return;
        }

        throw DisconnectedRead(request.Command, exception: null, route: route);
    }

    /// <summary>The refusal of a read that could not be read because the connection is gone.</summary>
    /// <remarks>WHY `reacquire_context` AND NOT `after_reconciliation`. A read changes nothing, so there is
    /// nothing to reconcile; what IS stale is every reference the caller holds — after the reconnect the
    /// document ids and revisions belong to a dead instance. The remedy is therefore "reconnect, then
    /// re-read the context", which is exactly what this policy means.
    /// History: docs/decisions/adapter-core.md#disconnect-latch</remarks>
    private KompasContractException DisconnectedRead(string command, Exception? exception, string? route = null)
    {
        var observed = _latch.Observation;
        route ??= _sta.CurrentLabel;
        var details = new JsonObject
        {
            // WHICH KOMPAS CALL FAILED: the route the adapter was inside, and the first code that broke.
            ["failed_call"] = route,
            ["reason"] = "connection_lost_during_read",
            ["hresult"] = exception is null ? observed?.HResult : ComHResult.From(exception),
            ["hresult_name"] = exception is null
                ? observed?.Name
                : (ComHResult.From(exception) is int code ? ComHResult.Name(code) : null),
            ["exception_type"] = exception?.GetType().Name ?? observed?.ExceptionType,
            ["exception_message"] = exception?.Message ?? observed?.Message,
            ["observed_at_utc"] = observed?.AtUtc.ToString("O"),
            ["remedy"] = "вызовите kompas_connect (mode=launch или attach) и перечитайте контекст: " +
                         "документы прежнего экземпляра в новой сессии недействительны",
        };

        var name = details["hresult_name"]?.GetValue<string>() ?? "код не назван";
        return new KompasContractException(
            ErrorCodes.ApplicationDisconnected,
            $"Связь с КОМПАС потеряна во время чтения '{command}': вызов КОМПАС отказал ({name}). " +
            "Результат не публикуется: пустой список или непрочитанный счётчик означали бы «модель " +
            "пуста», тогда как модель НЕ ПРОЧИТАНА. Переподключитесь и повторите чтение.",
            RetryPolicy.ReacquireContext,
            partialEffects: false,
            hresult: details["hresult"]?.GetValue<int?>(),
            details: details.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone()));
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

    private async Task<JsonNode?> DispatchAsync(IpcFrame request, int budgetMs, CancellationToken cancellationToken)
    {
        if (WorkerCommandTable.IsControlLane(request.Command))
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
                    // WHETHER THE CAD CONNECTION IS GONE. Set by any connection loss observed in the
                    // adapter, cleared by a successful kompas_connect; named here so `kompas_health`
                    // reports the loss instead of leaving the client to infer it from empty reads.
                    ["cad_disconnected"] = _latch.IsSet,
                    ["cad_disconnected_reason"] = _latch.Observation is { } loss
                        ? new JsonObject
                        {
                            ["hresult"] = loss.HResult,
                            ["hresult_name"] = loss.Name,
                            ["exception_type"] = loss.ExceptionType,
                            ["exception_message"] = loss.Message,
                            ["observed_at_utc"] = loss.AtUtc.ToString("O"),
                        }
                        : null,
                    // A connection loss seen OUTSIDE a session command, i.e. belonging to a FOREIGN COM
                    // object: seen and deliberately NOT applied. Named so "the mark is clear" and
                    // "nothing was seen" stay distinguishable.
                    ["cad_disconnect_ignored_outside_session"] = _latch.IgnoredOutsideSession,
                    ["cad_disconnect_cleared_reason"] = _latch.ClearedReason,
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
            WorkerCommands.SessionInventory => _sta.Run(() => Inventory(request), "session.inventory", cancellationToken),
            WorkerCommands.ListDocuments => _sta.Run(() => List(request), "doc.list", cancellationToken),
            WorkerCommands.CreateDocument => _sta.Run(() => Create(request), "doc.create", cancellationToken),
            WorkerCommands.OpenDocument => _sta.Run(() => Open(request), "doc.open", cancellationToken),
            WorkerCommands.GetContext => _sta.Run(() => Context(request), "doc.context", cancellationToken),
            WorkerCommands.SaveDocument => _sta.Run(() => Save(request), "doc.save", cancellationToken),
            WorkerCommands.CloseDocument => _sta.Run(() => Close(request), "doc.close", cancellationToken),
            WorkerCommands.ListFeatures => _sta.Run(() => Features(request), "feat.list", cancellationToken),
            WorkerCommands.ListSketches => _sta.Run(() => Sketches(request), "sketch.list", cancellationToken),
            WorkerCommands.ListBodies => _sta.Run(() => Bodies(request), "body.list", cancellationToken),
            WorkerCommands.ListCopiesGeometry => _sta.Run(
                () => CopiesGeometry(request), "master.list_copies_geometry", cancellationToken),
            WorkerCommands.CopyGeometry => _sta.Run(
                () => CopyGeometry(request), "master.copy_geometry", cancellationToken),
            WorkerCommands.SynchroniseCopy => _sta.Run(
                () => SynchroniseCopy(request), "master.synchronise_copy", cancellationToken),
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
            WorkerCommands.Draft => _sta.Run(() => Draft(request), "feat.draft", cancellationToken),
            WorkerCommands.DraftFromEdges =>
                _sta.Run(() => DraftFromEdges(request), "feat.draft_from_edges", cancellationToken),
            WorkerCommands.Thread => _sta.Run(() => Thread(request), "feat.thread", cancellationToken),
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
            WorkerCommands.ExportStl => _sta.Run(() => ExportStl(request), "export.stl", cancellationToken),
            WorkerCommands.ImportStep => _sta.Run(() => ImportStep(request), "import.step", cancellationToken),
            WorkerCommands.ExportImage => _sta.Run(() => ExportImage(request), "export.image", cancellationToken),
            WorkerCommands.UnitProbe => _sta.Run(() => UnitProbe(request), "probe.units", cancellationToken),
            WorkerCommands.CreateAuxGeometry => _sta.Run(() => CreateAuxGeometry(request), "aux.create", cancellationToken),
            WorkerCommands.ListAuxGeometry => _sta.Run(() => ListAuxGeometry(request), "aux.list", cancellationToken),
            WorkerCommands.UpdatePlane => _sta.Run(() => UpdatePlane(request), "aux.update_plane", cancellationToken),
            WorkerCommands.ListSketchEntities => _sta.Run(() => ListSketchEntities(request), "sketch.entities", cancellationToken),
            WorkerCommands.EditSketchEntity => _sta.Run(() => EditSketchEntity(request), "sketch.entity_edit", cancellationToken),
            WorkerCommands.CreateSketchConstraint => _sta.Run(() => CreateSketchConstraint(request), "sketch.constraint.create", cancellationToken),
            WorkerCommands.CreateSketchDimension => _sta.Run(() => CreateSketchDimension(request), "sketch.dimension.create", cancellationToken),
            WorkerCommands.ListSketchConstraints => _sta.Run(() => ListSketchConstraints(request), "sketch.constraint.list", cancellationToken),
            WorkerCommands.DeleteSketchConstraint => _sta.Run(() => DeleteSketchConstraint(request), "sketch.constraint.delete", cancellationToken),
            WorkerCommands.ListComponents => _sta.Run(() => ListComponents(request), "asm.list_components", cancellationToken),
            WorkerCommands.InsertComponent => _sta.Run(() => InsertComponent(request), "asm.insert_component", cancellationToken),
            WorkerCommands.SetComponentPlacement => _sta.Run(() => SetComponentPlacement(request), "asm.set_placement", cancellationToken),
            WorkerCommands.ReplaceComponent => _sta.Run(() => ReplaceComponent(request), "asm.replace_component", cancellationToken),
            WorkerCommands.CheckComponentLinks => _sta.Run(() => CheckComponentLinks(request), "asm.check_links", cancellationToken),
            WorkerCommands.DeleteComponent => _sta.Run(() => DeleteComponent(request), "asm.delete_component", cancellationToken),
            WorkerCommands.SetDismantle => _sta.Run(() => SetDismantle(request), "asm.set_dismantle", cancellationToken),
            WorkerCommands.ListDismantleSteps => _sta.Run(() => ListDismantleSteps(request), "asm.list_dismantle_steps", cancellationToken),
            WorkerCommands.CheckInterference => _sta.Run(() => CheckInterference(request), "asm.check_interference", cancellationToken),
            WorkerCommands.MeasureGap => _sta.Run(() => MeasureGap(request), "asm.measure_gap", cancellationToken),
            WorkerCommands.ListMates => _sta.Run(() => ListMates(request), "mate.list", cancellationToken),
            WorkerCommands.CreateMate => _sta.Run(() => CreateMate(request), "mate.create", cancellationToken),
            WorkerCommands.SetMateParameter => _sta.Run(() => SetMateParameter(request), "mate.set_parameter", cancellationToken),
            WorkerCommands.SetMateFixed => _sta.Run(() => SetMateFixed(request), "mate.set_fixed", cancellationToken),
            WorkerCommands.DeleteMate => _sta.Run(() => DeleteMate(request), "mate.delete", cancellationToken),
            WorkerCommands.CreateDrawingViews => _sta.Run(() => CreateDrawingViews(request), "drawing.create_views", cancellationToken),
            WorkerCommands.CreateSectionView => _sta.Run(() => CreateSectionView(request), "drawing.create_section_view", cancellationToken),
            WorkerCommands.GetSectionView => _sta.Run(() => GetSectionView(request), "drawing.get_section_view", cancellationToken),
            WorkerCommands.DeleteSectionView => _sta.Run(() => DeleteSectionView(request), "drawing.delete_section_view", cancellationToken),
            WorkerCommands.UpdateSectionView => _sta.Run(() => UpdateSectionView(request), "drawing.update_section_view", cancellationToken),
            WorkerCommands.ListDrawingViews => _sta.Run(() => ListDrawingViews(request), "drawing.list_views", cancellationToken),
            WorkerCommands.ListDimensions => _sta.Run(() => ListDimensions(request), "drawing.list_dimensions", cancellationToken),
            WorkerCommands.AddDimension => _sta.Run(() => AddDimension(request), "drawing.add_dimension", cancellationToken),
            WorkerCommands.SetTitleBlock => _sta.Run(() => SetTitleBlock(request), "drawing.set_title_block", cancellationToken),
            WorkerCommands.ExportDrawing => _sta.Run(() => ExportDrawing(request), "drawing.export", cancellationToken),
            WorkerCommands.SetTechnicalDemand => _sta.Run(() => SetTechnicalDemand(request), "drawing.set_technical_demand", cancellationToken),
            WorkerCommands.GetTitleBlock => _sta.Run(() => GetTitleBlock(request), "drawing.get_title_block", cancellationToken),
            WorkerCommands.GetTechnicalDemand => _sta.Run(() => GetTechnicalDemand(request), "drawing.get_technical_demand", cancellationToken),
            WorkerCommands.EditView => _sta.Run(() => EditView(request), "drawing.edit_view", cancellationToken),
            WorkerCommands.RebuildDrawingViews => _sta.Run(() => RebuildDrawingViews(request), "drawing.rebuild_views", cancellationToken),
            WorkerCommands.ListVariables => _sta.Run(() => ListVariables(request), "var.list", cancellationToken),
            WorkerCommands.SetVariableValue => _sta.Run(() => SetVariable(request), "var.set_value", cancellationToken),
            WorkerCommands.SetVariableExpression => _sta.Run(() => SetVariable(request), "var.set_expression", cancellationToken),
            WorkerCommands.GetMaterial => _sta.Run(() => GetMaterial(request), "mat.get", cancellationToken),
            WorkerCommands.SetMaterial => _sta.Run(() => SetMaterial(request), "mat.set", cancellationToken),
            WorkerCommands.CreateVariable => _sta.Run(() => CreateVariable(request), "var.create", cancellationToken),
            WorkerCommands.ListFeatureParameters => _sta.Run(() => ListFeatureParameters(request), "feat.parameters", cancellationToken),
            WorkerCommands.BindParameter => _sta.Run(() => BindParameter(request), "feat.bind_parameter", cancellationToken),
            WorkerCommands.Shutdown => _sta.Run(ShutdownPayload, "shutdown", cancellationToken),
            _ => throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Команда '{request.Command}' не реализована в Worker этой сборки.",
                RetryPolicy.Never),
        };

        // INVARIANT: the budget timer dies with the command. Task.Delay otherwise keeps a timer alive
        // for the whole budget (up to 300 s) after the answer is ready, so a run of short commands
        // leaves a pile of pending timers behind. Cancelling changes no outcome: the delay task is the
        // one WhenAny did NOT return, and a cancelled task is never rethrown as unobserved.
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = await Task.WhenAny(task, Task.Delay(budgetMs, timerCancellation.Token)).ConfigureAwait(false);
        if (completed != task)
        {
            Interlocked.Increment(ref _timedOut);
            NeedsReconciliation = true;
            _log.Write("error", "command budget expired", new { command = request.Command, budget_ms = budgetMs });
            throw new KompasContractException(
                ErrorCodes.OutcomeUnknown,
                $"Команда '{request.Command}' не завершилась за {budgetMs / 1000} с. " + TimedOutHint(request.Command) +
                " КОМПАС может продолжать её выполнять; повтор запрещён, требуется согласование по фактическому состоянию модели.",
                RetryPolicy.AfterReconciliation,
                partialEffects: true);
        }

        timerCancellation.Cancel();
        var outcome = await task.ConfigureAwait(false);
        return outcome is null ? null : KompJson.ToNode(outcome);
    }

    /// <summary>The command-specific part of a budget-expiry message.</summary>
    /// <remarks>WHY open has its own wording. The SDK documents no way to suppress a KOMPAS dialog
    /// (<c>ksdocument3d_open.html</c> declares only <c>invisible</c>), and on a file another process holds KOMPAS
    /// shows «документ редактируется пользователем» and the COM call never returns. Without this sentence the
    /// caller reads "budget expired" and cannot tell a modal dialog from a slow document.
    /// History: docs/decisions/files.md#file-locked-guard</remarks>
    private static string TimedOutHint(string command) => command == WorkerCommands.OpenDocument
        ? "Наиболее вероятная причина — КОМПАС показывает модальный диалог (например, «документ редактируется " +
          "пользователем») и ждёт ответа человека: в этом режиме вызов открытия не возвращается. Занятость файла " +
          "проверяется до вызова, поэтому диалог возможен по другой причине (например, лицензия или восстановление)."
        : string.Empty;

    /// <summary>One string field of a raw payload, for the guard that runs BEFORE the typed parse: it must
    /// work even when the command is refused before its payload is understood.
    /// History: docs/decisions/adapter-core.md#disconnect-latch</summary>
    private static string? ReadString(JsonNode? payload, string key) =>
        payload is JsonObject obj ? JsonScalars.ReadString(obj[key]) : null;

    private static T Argument<T>(IpcFrame request)
    {
        if (request.Payload is null)
        {
            throw new KompasContractException(ErrorCodes.InvalidArgument, $"Команда '{request.Command}' не получила payload.");
        }

        // Round-tripping through JSON text rather than JsonNode.Deserialize<T> keeps one serialisation
        // contract (snake_case, enum naming) shared with the Host. A payload not fitting the typed contract is
        // an ARGUMENT defect, not a Worker failure; this catch names the JSON path instead.
        // History: docs/decisions/worker-ipc.md#payload-contract
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

    /// <summary>Every mutation answer carries the revision the client needs for its next call, plus the document it
    /// belongs to.</summary> <remarks>The mutation runs FIRST and the revision is read AFTER it; reading it alongside
    /// the mutation reports the pre-mutation revision and makes the next command fail with REVISION_CONFLICT.</remarks>
    private JsonNode? TaggedAfter(string documentId, Func<object?> mutation, DocumentEntry document)
    {
        // THE CONTROL COPY IS TAKEN BEFORE THE MUTATION, into the service directory, and this is the
        // only point the core, assembly and mate mutations pass through. Reads do NOT go through it.
        var copy = _controlCopies.Before(document.Path, document.Id, document.Revision);
        // Feature count BEFORE the mutation. A refusal that leaves it HIGHER created a feature and left
        // it in the tree — MEASURED, not assumed, so the refusal can name the object to delete.
        var featuresBefore = CountFeaturesSafe(document);
        // The settlement marks of the PREVIOUS command are dropped before this one runs: a command that
        // refuses must not carry the marks of the command before it, and a body mutation collects its own
        // marks in the adapter as it rebuilds. History: docs/decisions/adapter-core.md#rebuild-verify
        _session.ResetRebuildSettlement();
        object? outcome;
        try
        {
            outcome = mutation();
        }
        catch (Exception ex)
        {
            // The settlement of THIS command travels with the refusal too: a refusal whose numbers were
            // read on a model that did not come back to its own volume was computed on a state the model
            // does not owe, and the client must be able to see that.
            ex = WithRebuildSettlement(ex);
            // FAILURE: the file is returned to its pre-mutation state, BUT ONLY WHEN THAT MAKES SENSE.
            // The pure ControlCopyRestorePolicy decides: a read-only document is not overwritten, and a
            // failure before COM deserves no write to the user's file.
            var contract = ex as KompasContractException;
            var decision = ControlCopyRestorePolicy.Decide(
                copy.Made, document.Access, contract?.Code, contract?.PartialEffects ?? false,
                document.OpenInKompas);
            var restoreFailure = decision.Restore
                ? _controlCopies.Restore(document.Path, copy.Path, document.Access)
                : null;

            // The revision the client needs for its NEXT call is read here, where the document is known,
            // and travels in the refusal itself. A refused mutation can leave the model changed (the
            // feature stayed in the tree, or the rollback restores only the FILE, never the in-memory
            // model), so a null revision_after made the client's next call fail REVISION_CONFLICT until
            // it re-read the context. The number is the document's CURRENT revision: the mutation already
            // bumped it if it changed anything, and an unchanged model reads back its old revision.
            throw Reclassify(ex, copy, decision, restoreFailure, document.Revision,
                document.Kind, FeatureLeftInTree(featuresBefore, CountFeaturesSafe(document)));
        }

        // SUCCESS: THE COPY IS DELETED. It was there for the failure case; left behind, it would
        // accumulate a whole document file per edit. A failed delete is NAMED in the response:
        // "the directory does not grow" and "the copy could not be removed" are different claims.
        var copyCleanupFailure = _controlCopies.DeleteAfterSuccess(copy.Path);

        var node = Tagged(documentId, document.Revision, outcome) as JsonObject ?? new JsonObject();
        // The copy is named BOTH as a string and as fields. The string is read by a human, the fields by a
        // probe: parsing the human-readable wording would make acceptance depend on the wording's revision.
        node["control_copy"] = DocumentControlCopies.Describe(copy);
        node["control_copy_made"] = copy.Made;
        node["control_copy_path"] = copy.Path;
        node["control_copy_deleted_after_success"] = copy.Made && copyCleanupFailure is null;
        if (copyCleanupFailure is not null)
        {
            node["control_copy_cleanup_failure"] = copyCleanupFailure;
        }

        PublishRebuildSettlement(node);
        return node;
    }

    /// <summary>Publish what the automatic settlement of this mutation did NOT confirm, in the same names
    /// and the same lists <c>kompas_rebuild</c> uses.</summary>
    /// <remarks>INVARIANT: a result computed on a model that did not come back to its own volume may not
    /// read as <c>geometry_checked</c>; the level is lowered and the reason is warned about, not hidden.
    /// The marks go to the tool's own <c>verification.unverified_aspects</c> (the field the client reads)
    /// and to <c>context.unverified_aspects</c> (the list <c>kompas_rebuild</c> publishes them in).
    /// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
    private void PublishRebuildSettlement(JsonObject node)
    {
        var notes = _session.RebuildSettlementNotes;
        var unestablished = _session.RebuildStabilityUnestablished;
        if (notes.Count == 0 && !unestablished)
        {
            return;
        }

        if (node["verification"] is JsonObject verification
            && JsonSerializer.Deserialize<VerificationDto>(verification.ToJsonString(), KompJson.Options) is { } dto)
        {
            var aspects = new List<string>(dto.UnverifiedAspects);
            aspects.AddRange(notes);
            node["verification"] = KompJson.ToNode(new VerificationDto(
                RebuildSettlement.CapLevel(dto.Level, unestablished), dto.Checks, aspects));
        }

        if (node["context"] is JsonObject context)
        {
            var aspects = (context["unverified_aspects"] as JsonArray) ?? new JsonArray();
            foreach (var note in notes)
            {
                aspects.Add(note);
            }

            context["unverified_aspects"] = aspects;
        }

        if (unestablished)
        {
            var warnings = (node["warnings"] as JsonArray) ?? new JsonArray();
            warnings.Add(RebuildSettlement.ChangedModelWarning);
            node["warnings"] = warnings;
        }
    }

    /// <summary>Carry the settlement marks of a REFUSED command into its <c>details</c>. The original code
    /// and retry policy are preserved: a refusal does not change because a rebuild was not settled.</summary>
    private Exception WithRebuildSettlement(Exception ex)
    {
        var notes = _session.RebuildSettlementNotes;
        if (notes.Count == 0 || ex is not KompasContractException contract)
        {
            return ex;
        }

        var details = new Dictionary<string, object?>();
        if (contract.Details is not null)
        {
            foreach (var (key, value) in contract.Details)
            {
                details[key] = value;
            }
        }

        details["rebuild_settlement"] = notes.ToArray();
        details["rebuild_stability_unestablished"] = _session.RebuildStabilityUnestablished;
        return new KompasContractException(
            contract.Code, contract.Message, contract.RetryPolicy, contract.PartialEffects,
            contract.Hresult, details);
    }

    /// <summary>Whether a refused mutation left a NEW feature in the tree, from the feature count before
    /// and after it. <c>null</c> when either read failed — an unread count is never passed off as "no".</summary>
    /// <remarks>MEASURED, not assumed: the count is read through the same route
    /// <c>kompas_get_context</c> uses. A feature that was created and left raises it by one; a refusal
    /// that never created one leaves it unchanged. History: docs/decisions/adapter-core.md#feature-left.</remarks>
    private static bool? FeatureLeftInTree(int? before, int? after) =>
        before is int b && after is int a ? a > b : null;

    /// <summary>The feature-count probe, wrapped so it can NEVER throw.</summary>
    /// <remarks>INVARIANT: an auxiliary read made for the RESPONSE does not cancel the mutation it only
    /// describes. <see cref="Api5Session.TryCountFeatures"/> already answers null for a document with no
    /// 3D model; this guard covers any remaining failure so that no document kind can turn a mutation into
    /// a refusal before the mutation has run. History: docs/decisions/worker-ipc.md#feature-left.</remarks>
    private int? CountFeaturesSafe(DocumentEntry document)
    {
        try
        {
            return _session.TryCountFeatures(document);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Carry to the client that a control copy was taken and what became of it on failure. The original code
    /// and retry policy are PRESERVED: a failure does not change because a copy appeared.</summary> <remarks>For an
    /// UNEXPECTED mutation exception the policy is "after reconciliation", not "same operation_id": partialEffects=true
    /// means repeating would apply the mutation twice.</remarks>
    private static Exception Reclassify(
        Exception ex, ControlCopyResult copy, RestoreDecision decision, string? restoreFailure,
        long revisionAfter, DocumentKind kind, bool? featureLeftInTree)
    {
        // Whether the model feature tree applies to THIS document kind. A drawing has none, so the count
        // is not "unread" but not applicable, and the two are named differently below.
        var treeApplies = ModelFeatureTree.Applies(kind);
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["control_copy"] = DocumentControlCopies.Describe(copy),
            ["control_copy_made"] = copy.Made,
            ["control_copy_path"] = copy.Path,
            // WHETHER A FEATURE THE CALLER MUST DELETE STAYED IN THE TREE — from the feature count before
            // and after the refused mutation, so it is a reading, not a guess. <c>false</c> when nothing
            // was added; <c>null</c> reads are NAMED, never passed off as "no".
            ["feature_left_in_tree"] = treeApplies && featureLeftInTree is bool left ? left : false,
            // WHY THE FILE WAS NOT RESTORED — NAMED. "Not restored" without a reason is indistinguishable
            // from "we forgot to restore", and here the reason is substantive: a read-only document, or a
            // failure that never reached COM.
            ["restore_attempted"] = decision.Restore,
            ["restore_decision"] = decision.Code,
            ["restore_decision_reason"] = decision.Reason,
            ["restored"] = decision.Restore && restoreFailure is null,
            ["restore_failure"] = restoreFailure,
            // WHAT WAS ROLLED BACK, named as one of three distinct states rather than as prose that
            // reads alike for all of them: nothing / the file only. The in-memory model is NEVER rolled
            // back, and that is stated in every partial-effect refusal, not only in the tool description.
            ["rollback_scope_code"] = decision.Restore
                ? (restoreFailure is null ? "file_restored" : "file_restore_failed")
                : "nothing_rolled_back",
            ["rollback_scope"] = decision.Restore
                ? (restoreFailure is null
                    ? "файл документа возвращён из копии; модель в памяти КОМПАСа не откатывалась"
                    : "файл документа НЕ возвращён (восстановление из копии не удалось); модель в памяти "
                        + "КОМПАСа не откатывалась")
                : "ничего не откачено: файл документа не перезаписывался; модель в памяти КОМПАСа не "
                    + "откатывалась",
            // INVARIANT: a refused MUTATION always states the revision to continue from. The number is the
            // document's current revision at the refusal — the mutation already bumped it when it changed
            // anything, and the rollback restores only the file, so this is the factual model state.
            // An adapter that read its own revision puts that value here and it wins over this one.
            ["revision_after"] = revisionAfter,
            ["revision_after_note"] = "ревизия документа на момент отказа; модель в памяти КОМПАСа не "
                + "откатывается, поэтому продолжать нужно с неё, а не с прежней",
        };

        if (featureLeftInTree is null)
        {
            // WHY THE FIELD IS false. Two DIFFERENT reasons, named apart so neither is read as "no feature
            // stayed": (a) the document has no model feature tree at all (a drawing, by construction);
            // (b) the tree exists but the count could not be read. "Not measured" is not "measured zero".
            details["feature_left_in_tree_note"] = treeApplies
                ? "число признаков до и после отказа не прочитано: остался ли в дереве созданный признак — "
                    + "не измерено, проверьте kompas_list_features"
                : ModelFeatureTree.NotApplicableNote(kind);
        }

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
            ErrorCodes.OutcomeUnknown,
            "Мутация не завершилась: " + ex.Message + ". Исход неизвестен: файл возвращён к состоянию " +
            "до мутации, но модель в памяти КОМПАСа — нет, поэтому повтор требует согласования.",
            RetryPolicy.AfterReconciliation,
            partialEffects: true,
            details: details);
    }

    private JsonNode? Tagged(string documentId, long revision, object? outcome)    {
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

        // A launched KOMPAS needs up to a minute on a cold start; report what it actually took.
        var openDocuments = _session.Documents.Count(d => d.ApplicationId == application.Id);
        var dto = application.ToDto(openDocuments);
        var warning = GuardOwnedInstance(application);
        if (warning is null)
        {
            return dto;
        }

        var node = KompJson.ToNode(dto)?.AsObject() ?? new JsonObject();
        node["warnings"] = new JsonArray(warning);
        return node;
    }

    /// <summary>Puts a launched instance under a kill-on-close job; names the refusal instead of hiding it.</summary>
    /// <remarks>INVARIANT: an attached instance is never guarded — it is the user's process.
    /// MEASURED: a tree kill of the Host never reaches a launched instance (its parent is <c>svchost.exe</c>),
    /// so the job is the only thing that ends it; the normal shutdown path is untouched and still takes the
    /// rescue copy before <c>Quit()</c>. History: docs/decisions/host.md#client-shutdown</remarks>
    private string? GuardOwnedInstance(ApplicationEntry application)
    {
        if (!OwnedInstanceGuard.ShouldGuard(application.Ownership))
        {
            return null;
        }

        var job = OwnedProcessJob.TryCreate(application.ProcessId, out var failure);
        if (job is null)
        {
            _log.Write("warn", "owned instance job object failed", new
            {
                process_id = application.ProcessId,
                reason = failure,
            });
            return failure;
        }

        _ownedJobs.Add(job);
        return null;
    }

    private object? Disconnect(IpcFrame request)
    {
        var command = Argument<DisconnectCommand>(request);
        var shutdown = _session.Disconnect(
            command.ApplicationId, command.CloseOwnedApplication, _controlCopies, new SessionEndBudget());
        var node = new JsonObject { ["application_id"] = command.ApplicationId, ["disconnected"] = true };
        if (shutdown is not null)
        {
            node["owned_instance_shutdown"] = ShutdownNode(shutdown);
        }

        return node;
    }

    /// <summary>The shutdown report as the client sees it: what was rescued, what failed, and whether the
    /// instance is actually gone. A missing field would be indistinguishable from a forgotten write.
    /// History: docs/decisions/host.md#shutdown-no-orphans</summary>
    private static JsonObject ShutdownNode(OwnedShutdownReport shutdown)
    {
        var rescued = new JsonArray();
        foreach (var copy in shutdown.Rescued)
        {
            rescued.Add(new JsonObject
            {
                ["document_id"] = copy.DocumentId,
                ["rescue_copy"] = copy.Path,
                ["reason"] = copy.Reason,
            });
        }

        var problems = new JsonArray();
        foreach (var problem in shutdown.Problems)
        {
            problems.Add(problem);
        }

        return new JsonObject
        {
            ["quit_issued"] = shutdown.QuitIssued,
            ["process_exited"] = shutdown.ProcessExited,
            ["process_id"] = shutdown.ProcessId,
            ["killed_by_pid"] = shutdown.KilledByPid,
            ["rescue_copies"] = rescued,
            ["problems"] = problems,
            // WHY these three: "everything was rescued" and "the budget cut the rescues short" are
            // different outcomes, and the second must not look like the first.
            // History: docs/decisions/host.md#shutdown-many-docs
            ["session_end_budget_ms"] = shutdown.BudgetMs,
            ["rescue_skipped_by_budget"] = shutdown.RescueSkippedByBudget,
            ["close_skipped_by_budget"] = shutdown.CloseSkippedByBudget,
            ["phase_ms"] = new JsonObject
            {
                ["rescue"] = shutdown.RescueMs,
                ["close"] = shutdown.CloseMs,
                ["quit"] = shutdown.QuitMs,
            },
        };
    }

    /// <summary>Session inventory for the release decision. A control command, but on the CAD lane: the unsaved flag is
    /// read through COM.</summary>
    private object? Inventory(IpcFrame request)
    {
        _ = request;
        return _session.Inventory();
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
        // A reopened model is a state of the BUILT model only if its own rebuild applied every feature;
        // the client's first read must not describe a model the kernel left half built. This is the one
        // boundary no mutation covers, so the settlement happens here.
        _session.ResetRebuildSettlement();
        _session.SettleBuiltModel(document);
        return _session.Context(document, includeTopology: false,
            extraUnverified: _session.RebuildSettlementNotes);
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

    /// <summary>Change a sketch's support plane — a MUTATION (control copy, revision bump).</summary>
    /// <remarks>INVARIANT: the revision is checked BEFORE COM, and the sketch reference carries the
    /// document, so the document is not taken from the payload. INVARIANT: the adapter bumps the
    /// revision on an ACCEPTED support change, independently of whether the body moved — the change is
    /// a mutation, not a geometry event.
    /// History: docs/decisions/adapter-sketch.md#set-plane-is-a-mutation</remarks>
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

        // The document comes from the first edge reference — the same rule as extrude, and the reason a
        // reference from another document cannot be smuggled in.
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

        // The document comes from the first edge — the same rule as fillet.
        var document = _session.DocumentForReference(command.EdgeRefs[0]);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Chamfer(command), document);
    }

    private object? Hole(IpcFrame request)
    {
        var command = Argument<HoleCommand>(request);

        // The document comes from the support face: a hole is addressed by the surface it starts on.
        var document = _session.DocumentForReference(command.FaceRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Hole(command), document);
    }

    private object? Rotated(IpcFrame request)
    {
        var command = Argument<RotatedCommand>(request);

        // The document comes from the profile sketch: a rotation is addressed by the revolution body.
        // The axis is given by model coordinates, not a reference — deliberately: an axis reference
        // could come from a foreign part.
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Rotated(command), document);
    }

    /// <summary>Kinematic operation (SM-04). The document comes from the PROFILE SKETCH — the same rule as extrude and
    /// rotated.</summary> <remarks>The trajectory is a SECOND reference; the adapter checks it belongs to the same
    /// document (<c>INVALID_ARGUMENT</c> with both ids), rather than reducing it to the named document.</remarks>
    private object? Sweep(IpcFrame request)
    {
        var command = Argument<SweepCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Sweep(command), document);
    }

    /// <summary>Loft (SM-05). The document comes from <c>document_id</c>, not a section reference: there are several
    /// sections, and the adapter checks every one belongs to this document and refuses otherwise.</summary>
    private object? Loft(IpcFrame request)
    {
        var command = Argument<LoftCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Loft(command), document);
    }

    /// <summary>Shell (SM-13). The document comes from <c>document_id</c> for the same reason as sections: there may be
    /// several faces to remove, and the adapter checks each against the named document.</summary>
    private object? Shell(IpcFrame request)
    {
        var command = Argument<ShellCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Shell(command), document);
    }

    /// <summary>Draft (SM-12). The document comes from <c>document_id</c>, as at shell: there may be several
    /// faces, and the adapter checks each against the named document.</summary>
    private object? Draft(IpcFrame request)
    {
        var command = Argument<DraftCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.Draft(command), document);
    }

    /// <summary>Draft from a base line (SM-12) — the same addressing rule as the draft from a base.</summary>
    private object? DraftFromEdges(IpcFrame request)
    {
        var command = Argument<DraftFromEdgesCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.DraftFromEdges(command), document);
    }

    /// <summary>Thread designation (SM-08) — the same addressing rule as the draft from a base.</summary>
    private object? Thread(IpcFrame request)
    {
        var command = Argument<ThreadCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.CreateThread(command), document);
    }

    private object? SolidBoolean(IpcFrame request)
    {
        var command = Argument<BooleanCommand>(request);

        // The document comes from the TARGET BODY, not from document_id, so a reference from another
        // part cannot be smuggled in. It is checked that the caller named the same document the
        // reference names — a mismatch is an addressing error, not "use what we were given".
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

    /// <summary>The document of a B3 operation: from the target-body reference, with the named identifier
    /// checked.</summary> <remarks>The reference and <c>document_id</c> are two independent claims about where the work
    /// happens. If they diverge, trusting one would silently work in the wrong place.</remarks>
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

    /// <summary>Feature parameters — a read, bypassing the journal, like the feature list and measure.</summary>
    private object? Feature(IpcFrame request) => _session.GetFeature(Argument<GetFeatureCommand>(request));

    /// <summary>Grid pattern (SM-18). The document comes from <c>document_id</c>, not a source-object reference: there
    /// may be several sources, and the adapter checks every one belongs to this document.</summary>
    private object? PatternGrid(IpcFrame request)
    {
        var command = Argument<PatternGridCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.PatternGrid(command), document);
    }

    /// <summary>Circular pattern (SM-19). The document comes from <c>document_id</c>.</summary>
    private object? PatternCircular(IpcFrame request)
    {
        var command = Argument<PatternCircularCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.PatternCircular(command), document);
    }

    /// <summary>Mirror pattern (SM-23). The document comes from <c>document_id</c>.</summary>
    private object? PatternMirror(IpcFrame request)
    {
        var command = Argument<PatternMirrorCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.PatternMirror(command), document);
    }

    /// <summary>Read pattern parameters. Bypasses the mutation journal: it changes nothing. The revision is returned
    /// but not bumped — the same rule as <c>get_feature</c> and <c>sketch.status</c>.</summary>
    private object? PatternRead(IpcFrame request)
    {
        var command = Argument<PatternReadCommand>(request);
        var document = _session.DocumentForReference(command.FeatureRef);
        return Tagged(document.Id, document.Revision, _session.PatternRead(command));
    }
    /// <summary>Sketch status is a read, bypassing the mutation journal like <c>measure</c> and <c>get_feature</c>. The
    /// revision is returned but NOT bumped: MEASURED that reading the status changed neither the volume nor the
    /// topology counters.</summary> <remarks><c>Tagged</c>, not <c>TaggedAfter</c>: bumping the revision for a read
    /// would declare the model changed, which did not happen.</remarks>
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
        // The response carries no reference to the feature: it is deleted, and handing out a "live" handle
        // to it would be a promise the server cannot keep.
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

    private object? ExportStl(IpcFrame request)
    {
        // Tagged, not TaggedAfter: STL writes a FILE, not the model. The revision is checked inside the
        // adapter (REVISION_CONFLICT before the converter runs) and the model revision does not move —
        // exactly like kompas_export_step. History: docs/decisions/geometry.md#stl
        var command = Argument<ExportStlCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ExportStl(command));
    }

    // DRW (block drawings): the three mutating tools go through the common mutation point so the
    // envelope carries the revision AND the control copy is taken; the read goes through Tagged.
    // History: docs/decisions/drawings.md#worker

    private object? UpdateSectionView(IpcFrame request)
    {
        var command = Argument<UpdateSectionViewCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.UpdateSectionView(command), document);
    }

    private object? GetSectionView(IpcFrame request)
    {
        var command = Argument<GetSectionViewCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.GetSectionView(command));
    }

    private object? DeleteSectionView(IpcFrame request)
    {
        var command = Argument<DeleteSectionViewCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.DeleteSectionView(command), document);
    }

    private object? CreateSectionView(IpcFrame request)
    {
        var command = Argument<CreateSectionViewCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.CreateSectionView(command), document);
    }

    private object? CreateDrawingViews(IpcFrame request)
    {
        var command = Argument<CreateDrawingViewsCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.CreateDrawingViews(command), document);
    }

    private object? ListDrawingViews(IpcFrame request)
    {
        var command = Argument<ListDrawingViewsCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ListDrawingViews(command));
    }

    private object? ListDimensions(IpcFrame request)
    {
        var command = Argument<ListDimensionsCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ListDimensions(command));
    }

    private object? AddDimension(IpcFrame request)
    {
        var command = Argument<AddDimensionCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.AddDimension(command), document);
    }

    private object? SetTitleBlock(IpcFrame request)
    {
        var command = Argument<SetTitleBlockCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetTitleBlock(command), document);
    }

    private object? ExportDrawing(IpcFrame request)
    {
        var command = Argument<ExportDrawingCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ExportDrawing(command));
    }

    private object? SetTechnicalDemand(IpcFrame request)
    {
        var command = Argument<SetTechnicalDemandCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetTechnicalDemand(command), document);
    }

    // READ-ONLY: no revision bump, no control copy, no TaggedAfter — the document is not modified, so
    // the returned revision must be the one observed, not a new one. History: docs/decisions/drawings.md#stamp-read
    private object? GetTitleBlock(IpcFrame request)
    {
        var command = Argument<GetTitleBlockCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.GetTitleBlock(command));
    }

    private object? GetTechnicalDemand(IpcFrame request)
    {
        var command = Argument<GetTechnicalDemandCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.GetTechnicalDemand(command));
    }

    private object? EditView(IpcFrame request)
    {
        var command = Argument<EditViewCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.EditView(command), document);
    }

    private object? RebuildDrawingViews(IpcFrame request)
    {
        var command = Argument<RebuildDrawingViewsCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.RebuildDrawingViews(command), document);
    }

    // READ-ONLY: no revision bump and no control copy. INVARIANT: reading variables must not change the
    // revision and must not demand an expected_revision — the caller is not about to write.
    // History: docs/decisions/variables-material.md#read-variables
    private object? ListVariables(IpcFrame request)
    {
        var command = Argument<ListVariablesCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ListVariables(command));
    }

    private object? GetMaterial(IpcFrame request)
    {
        var command = Argument<GetMaterialCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.GetMaterial(command));
    }

    // Both write commands share one handler: the mode is decided by which of value/expression the caller
    // supplied, and the contract refusal for "both or neither" happens before COM.
    private object? SetVariable(IpcFrame request)
    {
        var command = Argument<SetVariableCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetVariable(command), document);
    }

    private object? SetMaterial(IpcFrame request)
    {
        var command = Argument<SetMaterialCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetMaterial(command), document);
    }

    // G3 block. var.create and feat.bind_parameter are mutations (TaggedAfter: control copy, revision,
    // journal). feat.parameters only READS the feature's parameter array: no revision bump and no
    // control copy, so it returns the revision it observed. History: docs/decisions/variables-material.md#g3-route
    private object? CreateVariable(IpcFrame request)
    {
        var command = Argument<CreateVariableCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.CreateVariable(command), document);
    }

    private object? ListFeatureParameters(IpcFrame request)
    {
        var command = Argument<ListFeatureParametersCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ListFeatureParameters(command));
    }

    private object? BindParameter(IpcFrame request)
    {
        var command = Argument<BindParameterCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.BindParameter(command), document);
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

    /// <summary>Sketches of a part — a READ: the revision is returned but not bumped, because walking the
    /// collection changes nothing and a bump would invalidate the caller's references.</summary>
    private object? Sketches(IpcFrame request)
    {
        var command = Argument<ListSketchesCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ListSketches(command));
    }

    private object? Bodies(IpcFrame request) => _session.ListBodies(Argument<ListBodiesCommand>(request));

    private object? CopiesGeometry(IpcFrame request) =>
        _session.ListCopiesGeometry(Argument<ListCopiesGeometryCommand>(request));

    private object? CopyGeometry(IpcFrame request) =>
        _session.CopyGeometry(Argument<CopyGeometryCommand>(request));

    private object? SynchroniseCopy(IpcFrame request) =>
        _session.SynchroniseCopy(Argument<SynchroniseCopyCommand>(request));

    private object? ListComponents(IpcFrame request) =>
        _session.ListComponents(Argument<ListComponentsCommand>(request));

    private object? ListMates(IpcFrame request) =>
        _session.ListMates(Argument<ListMatesCommand>(request));

    // C1/C2 mutations go through the common mutation point (TaggedAfter), so the envelope carries a revision.
    // History: docs/decisions/worker-ipc.md#c1c2-tagging

    private object? CreateMate(IpcFrame request)
    {
        var command = Argument<CreateMateCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.CreateMate(command), document);
    }

    private object? SetMateParameter(IpcFrame request)
    {
        var command = Argument<SetMateParameterCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetMateParameter(command), document);
    }

    private object? SetMateFixed(IpcFrame request)
    {
        var command = Argument<SetMateFixedCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetMateFixed(command), document);
    }

    private object? DeleteMate(IpcFrame request)
    {
        var command = Argument<DeleteMateCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.DeleteMate(command), document);
    }

    private object? InsertComponent(IpcFrame request)
    {
        var command = Argument<InsertComponentCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.InsertComponent(command), document);
    }

    private object? SetComponentPlacement(IpcFrame request)
    {
        var command = Argument<SetComponentPlacementCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetComponentPlacement(command), document);
    }

    private object? ReplaceComponent(IpcFrame request)
    {
        var command = Argument<ReplaceComponentCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.ReplaceComponent(command), document);
    }

    private object? CheckComponentLinks(IpcFrame request) =>
        _session.CheckComponentLinks(Argument<CheckComponentLinksCommand>(request));

    private object? DeleteComponent(IpcFrame request)
    {
        var command = Argument<DeleteComponentCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.DeleteComponent(command), document);
    }

    // The exploded view writes the model (steps and mode), so it bumps the revision like every other
    // mutation of the assembly domain; reading it only READs.
    private object? SetDismantle(IpcFrame request)
    {
        var command = Argument<SetDismantleCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return TaggedAfter(document.Id, () => _session.SetDismantle(command), document);
    }

    private object? ListDismantleSteps(IpcFrame request) =>
        _session.ListDismantleSteps(Argument<ListDismantleStepsCommand>(request));

    // Both G1 commands only READ: they do not pass through TaggedAfter (no control copy, no revision
    // bump), exactly like the other read commands of the assembly domain.
    private object? CheckInterference(IpcFrame request) =>
        _session.CheckInterference(Argument<CheckInterferenceCommand>(request));
    private object? MeasureGap(IpcFrame request) =>
        _session.MeasureGap(Argument<MeasureGapCommand>(request));

    private object? Measure(IpcFrame request) => _session.Measure(Argument<MeasureCommand>(request));

    private object? Resolve(IpcFrame request) => _session.ResolveSelection(Argument<ResolveSelectionCommand>(request));

    private object? Topology(IpcFrame request) => _session.ReadTopology(Argument<ReadTopologyCommand>(request));

    private object? ImportStep(IpcFrame request)
    {
        _session.ResetRebuildSettlement();
        var result = _session.ImportStep(Argument<ImportStepCommand>(request));
        var notes = _session.RebuildSettlementNotes;
        // The imported model is settled before its counts are read; what that settlement did not confirm
        // is named in the same list the import already uses for its own gaps.
        return notes.Count == 0
            ? result
            : result with { UnverifiedAspects = result.UnverifiedAspects.Concat(notes).ToArray() };
    }

    private object? UnitProbe(IpcFrame request) => _session.UnitProbe(Argument<UnitProbeCommand>(request));

    /// <summary>Creating an auxiliary-geometry object is a mutation: the revision is bumped and the document's
    /// references move with it. The expected revision is checked, as for other mutations.</summary>
    private object? CreateAuxGeometry(IpcFrame request)
    {
        var command = Argument<CreateAuxGeometryCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.CreateAuxGeometry(command), document);
    }

    /// <summary>Enumerating auxiliary geometry is a read. The revision is returned but NOT bumped: walking collections
    /// changes nothing, so declaring a change would invalidate the caller's references.</summary>
    private object? ListAuxGeometry(IpcFrame request)
    {
        var command = Argument<ListAuxGeometryCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        return Tagged(document.Id, document.Revision, _session.ListAuxGeometry(command));
    }

    /// <summary>Editing an existing plane is a mutation: the revision is bumped and the document's references move with
    /// it.</summary>
    private object? UpdatePlane(IpcFrame request)
    {
        var command = Argument<UpdatePlaneCommand>(request);
        var document = _session.RequireDocument(command.DocumentId);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.UpdatePlane(command), document);
    }

    /// <summary>Enumerating sketch entities is a read: the revision is returned but NOT bumped. Entering the sketch is
    /// read-only (<c>BeginEditEx(true)</c>), so the walk does not change the model.</summary>
    private object? ListSketchEntities(IpcFrame request)
    {
        var command = Argument<ListSketchEntitiesCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        return Tagged(document.Id, document.Revision, _session.ListSketchEntities(command));
    }

    /// <summary>Targeted edit of one sketch entity — a mutation.</summary>
    /// <summary>Writes one sketch constraint. The document comes from the sketch reference, and the
    /// revision is bumped: a constraint is an edit of the model.</summary>
    private object? CreateSketchConstraint(IpcFrame request)
    {
        var command = Argument<CreateSketchConstraintCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.CreateSketchConstraint(command), document);
    }

    /// <summary>Creates a driving linear dimension on a segment. A dimension is an edit of the model,
    /// so the revision is bumped.</summary>
    private object? CreateSketchDimension(IpcFrame request)
    {
        var command = Argument<CreateSketchDimensionCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.CreateSketchDimension(command), document);
    }

    /// <summary>Reads the constraints of a sketch's objects. Bypasses the mutation journal: it changes
    /// nothing, so the revision is returned but NOT bumped — the same rule as <c>sketch.status</c>.</summary>
    private object? ListSketchConstraints(IpcFrame request)
    {
        var command = Argument<ListSketchConstraintsCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        return Tagged(document.Id, document.Revision, _session.ListSketchConstraints(command));
    }

    /// <summary>Deletes one constraint of an object or all of them. A deletion is an edit of the model,
    /// so the revision is bumped.</summary>
    private object? DeleteSketchConstraint(IpcFrame request)
    {
        var command = Argument<DeleteSketchConstraintCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.DeleteSketchConstraint(command), document);
    }

    private object? EditSketchEntity(IpcFrame request)
    {
        var command = Argument<EditSketchEntityCommand>(request);
        var document = _session.DocumentForReference(command.SketchRef);
        GuardRevision(document, command.ExpectedRevision);
        return TaggedAfter(document.Id, () => _session.EditSketchEntity(command), document);
    }

    private object? ShutdownPayload()
    {
        var reports = ShutdownOwnedSessions();
        var node = new JsonObject { ["sessions_closed"] = true };
        if (reports.Count > 0)
        {
            var array = new JsonArray();
            foreach (var (applicationId, report) in reports)
            {
                var item = ShutdownNode(report);
                item["application_id"] = applicationId;
                array.Add(item);
            }

            node["owned_instance_shutdown"] = array;
        }

        return node;
    }

    /// <summary>Detach from every session. Documents belonging to the server are closed without saving; an attached
    /// KOMPAS is left running because it is the user's process.</summary> <remarks>Queued commands are NOT interrupted
    /// — a COM call cannot be cancelled from outside, so the outcome of an operation the Host called
    /// <c>OUTCOME_UNKNOWN</c> is determined by the model.
    /// MEASURED: this path used to leave an orphan — <c>Quit()</c> answers at once, and a "save changes?"
    /// window nobody answered kept the process alive. Every step is logged, so a run that fell back to
    /// terminating a PID says so. History: docs/decisions/host.md#shutdown-no-orphans</remarks>
    public List<(string ApplicationId, OwnedShutdownReport Report)> ShutdownOwnedSessions()
    {
        var reports = new List<(string, OwnedShutdownReport)>();
        foreach (var application in _session.Applications.ToArray())
        {
            try
            {
                var report = _session.Disconnect(
                    application.Id, closeOwnedApplication: application.Ownership == ApplicationOwnership.Launched, _controlCopies);
                if (report is null)
                {
                    continue;
                }

                reports.Add((application.Id, report));
                _log.Write("info", "owned instance shutdown", new
                {
                    application = application.Id,
                    process_id = report.ProcessId,
                    quit_issued = report.QuitIssued,
                    process_exited = report.ProcessExited,
                    killed_by_pid = report.KilledByPid,
                    rescue_copies = report.Rescued.Select(c => new { c.DocumentId, c.Path, c.Reason }).ToArray(),
                    problems = report.Problems.ToArray(),
                    session_end_budget_ms = report.BudgetMs,
                    rescue_skipped_by_budget = report.RescueSkippedByBudget,
                    close_skipped_by_budget = report.CloseSkippedByBudget,
                    rescue_ms = report.RescueMs,
                    close_ms = report.CloseMs,
                    quit_ms = report.QuitMs,
                });

                if (!report.ProcessExited)
                {
                    _log.Write("warn", "owned instance still alive after shutdown", new
                    {
                        application = application.Id,
                        process_id = report.ProcessId,
                        killed_by_pid = report.KilledByPid,
                    });
                }
            }
            catch (Exception ex)
            {
                _log.Write("warn", "session shutdown failed", new { application = application.Id, type = ex.GetType().Name });
            }
        }

        return reports;
    }
}

/// <summary>JSONL worker log: one object per line, UTC timestamps, bounded size. Diagnostics never go to the pipe
/// (frames only) nor to the Host's stdout.</summary>
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

/// <summary>Startup environment facts, gathered without touching KOMPAS so a wedged CAD lane cannot hide them from
/// kompas_health.</summary>
internal static class EnvironmentSnapshot
{
    public static object Collect()
    {
        var rot = CountRotKompasEntries();
        return new
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
            // The number of instances ATTACH can choose from, counted by the SAME enumerator Attach uses.
            // runningInstances counts OS processes; the two numbers answer different questions, because a
            // KOMPAS process without a ROT entry is not an attach candidate.
            // History: docs/decisions/adapter-core.md#attach-candidates
            rotKompasEntries = rot.Count,
            rotKompasEntriesFailure = rot.Failure,
        };
    }

    /// <summary>ROT entries matching the KOMPAS ProgID, or <c>null</c> with a named reason. A failed
    /// enumeration is NOT reported as zero: "the enumerator threw" and "KOMPAS is not registered" are
    /// different diagnoses, and zero would silently claim the second.</summary>
    private static (int? Count, string? Failure) CountRotKompasEntries()
    {
        try
        {
            return (RunningObjectTable.EnumerateKompasEntries(Api5Session.KompasProgId).Count, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
