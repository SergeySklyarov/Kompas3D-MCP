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

/// <summary>Translates an IPC command frame into one call on the COM session. The only place a command may reach
/// KOMPAS: everything is serialised onto one STA thread, and an unobserved result is OUTCOME_UNKNOWN.</summary>
public sealed class CommandDispatcher
{
    /// <summary>Commands that must NOT be queued onto the STA lane, because answering them while the CAD lane is busy
    /// is precisely why the Host can tell "KOMPAS busy" apart from "Worker dead".</summary>
    private static readonly HashSet<string> ControlLaneCommands = new(StringComparer.Ordinal)
    {
        WorkerCommands.Ping,
        WorkerCommands.EnvironmentProbe,
    };

    /// <summary>Commands that CHANGE the model. An unexpected exception in a mutation and in a read are DIFFERENT
    /// states: in a read "not read" is a refusal, in a mutation the model may have changed.</summary>
    private static readonly HashSet<string> MutationCommands = new(StringComparer.Ordinal)
    {
        WorkerCommands.Connect,
        WorkerCommands.Disconnect,
        WorkerCommands.CreateDocument,
        WorkerCommands.OpenDocument,
        WorkerCommands.SaveDocument,
        WorkerCommands.CloseDocument,
        WorkerCommands.CreateSketch,
        WorkerCommands.EditSketch,
        WorkerCommands.SetSketchPlane,
        WorkerCommands.FinishSketch,
        WorkerCommands.Extrude,
        WorkerCommands.Fillet,
        WorkerCommands.Chamfer,
        WorkerCommands.Hole,
        WorkerCommands.Rotated,
        WorkerCommands.Sweep,
        WorkerCommands.Loft,
        WorkerCommands.Shell,
        WorkerCommands.SolidBoolean,
        WorkerCommands.SolidSplit,
        WorkerCommands.SolidCutByPlane,
        WorkerCommands.SolidReposition,
        WorkerCommands.UpdateFeature,
        WorkerCommands.PatternGrid,
        WorkerCommands.PatternCircular,
        WorkerCommands.PatternMirror,
        WorkerCommands.SuppressFeature,
        WorkerCommands.DeleteFeature,
        WorkerCommands.Rebuild,
        WorkerCommands.ExportStep,
        WorkerCommands.ImportStep,
        WorkerCommands.ExportImage,
        WorkerCommands.CreateAuxGeometry,
        WorkerCommands.UpdatePlane,
        WorkerCommands.EditSketchEntity,
        WorkerCommands.InsertComponent,
        WorkerCommands.SetComponentPlacement,
        WorkerCommands.ReplaceComponent,
        WorkerCommands.CreateMate,
        WorkerCommands.SetMateParameter,
        WorkerCommands.SetMateFixed,
        WorkerCommands.DeleteMate,
        WorkerCommands.CreateDrawingViews,
        WorkerCommands.EditView,
        WorkerCommands.RebuildDrawingViews,
        WorkerCommands.AddDimension,
        WorkerCommands.SetTitleBlock,
        WorkerCommands.ExportDrawing,
        WorkerCommands.SetTechnicalDemand,
        WorkerCommands.SetVariableValue,
        WorkerCommands.SetVariableExpression,
        WorkerCommands.SetMaterial,
        WorkerCommands.UnitProbe,
    };

    private readonly StaExecutor _sta;
    private readonly WorkerLog _log;
    private readonly Api5Session _session = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>Control copies of document files, taken before every mutation into the SERVICE directory.</summary>
    private readonly DocumentControlCopies _controlCopies;

    private long _handled;
    private long _failed;
    private long _timedOut;

    /// <summary>Set when a command's outcome could not be observed; the Host must restart us.</summary>
    public bool NeedsReconciliation { get; private set; }

    public CommandDispatcher(StaExecutor sta, WorkerLog log, string controlCopyDirectory)
    {
        _sta = sta;
        _log = log;
        _controlCopies = new DocumentControlCopies(controlCopyDirectory);
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
            // AN UNEXPECTED EXCEPTION IN A MUTATION IS NOT A "CLEAN FAILURE": the flag comes from
            // <see cref="MutationCommands"/>, so the client is not told to repeat an applied mutation.
            var isMutation = MutationCommands.Contains(request.Command);

            if (isMutation)
            {
                NeedsReconciliation = true;
            }

            Interlocked.Increment(ref _failed);
            var hresult = ComHResult.From(ex);
            _log.Write("error", "command threw", new { command = request.Command, type = ex.GetType().Name, message = ex.Message, hresult, isMutation });

            // An unexpected exception during a mutation means the outcome is unknown, not "failed
            // cleanly": saying so is what stops the client from repeating a half-applied change.
            var unknown = ex is COMException && hresult is int code && ComHResult.IsDisconnected(code);
            return Failure(request, new ErrorDto(
                unknown || isMutation ? ErrorCodes.OutcomeUnknown : Classify(ex, hresult),
                ex.Message,
                unknown || isMutation ? RetryPolicy.AfterReconciliation : RetryPolicy.SameOperationId,
                hresult,
                unknown || isMutation,
                new JsonObject
                {
                    ["exception"] = ex.GetType().Name,
                    ["is_mutation"] = isMutation,
                }));
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
        // Not free: it enumerates the ROT and the process list.
        WorkerCommands.EnvironmentProbe => 10_000,
        WorkerCommands.Ping => 2_000,
        WorkerCommands.Connect => 180_000,
        WorkerCommands.ExportStep or WorkerCommands.ImportStep => 300_000,
        // Renders and (in file mode) writes a file; MEASURED in seconds.
        WorkerCommands.ExportImage => 240_000,
        // API7 route (bridge + TransferInterface + RebuildModel): longer than a pure API5 mutation.
        WorkerCommands.Hole => 240_000,
        // Same API7 route plus a re-read of all bodies after the rebuild.
        WorkerCommands.SolidBoolean or WorkerCommands.SolidSplit
            or WorkerCommands.SolidCutByPlane or WorkerCommands.SolidReposition => 240_000,
        // API5 route but both rebuild and re-read bodies; sections go through the API7 bridge.
        WorkerCommands.Sweep or WorkerCommands.Loft or WorkerCommands.Shell => 240_000,
        // API7 route (bridge + QI(IAuxiliaryGeomContainer) + Rebuild), one COM call per element.
        WorkerCommands.CreateAuxGeometry or WorkerCommands.ListAuxGeometry => 240_000,
        WorkerCommands.UpdatePlane => 240_000,
        // Rebuilds the dependent body; the MEASURED apply step (`sketch.Update()`) fits in it.
        WorkerCommands.SetSketchPlane => 240_000,
        WorkerCommands.ListSketchEntities or WorkerCommands.EditSketchEntity => 240_000,
        // Reads a file and rebuilds the document; enumeration makes one COM call per component.
        WorkerCommands.InsertComponent or WorkerCommands.ReplaceComponent => 240_000,
        WorkerCommands.ListComponents or WorkerCommands.SetComponentPlacement
            or WorkerCommands.CheckComponentLinks => 240_000,
        // Rebuilds the assembly (Update() + RebuildDocument); enumeration reads every mate object.
        WorkerCommands.ListMates or WorkerCommands.CreateMate or WorkerCommands.SetMateParameter
            or WorkerCommands.SetMateFixed or WorkerCommands.DeleteMate => 240_000,
        // Drawing views/export go through the API7 bridge (TransferInterface + AddStandartViews or the
        // converter), which is longer than a pure API5 call; dimensions and the stamp are API7 too.
        WorkerCommands.CreateDrawingViews or WorkerCommands.ExportDrawing
            or WorkerCommands.AddDimension or WorkerCommands.SetTitleBlock
            or WorkerCommands.EditView or WorkerCommands.SetTechnicalDemand
            or WorkerCommands.GetTitleBlock or WorkerCommands.GetTechnicalDemand => 240_000,
        // Variable writes go through Set + RebuildModel + a re-read of the collection; the material write
        // goes through SetMaterial + Update + a re-read of name and density.
        WorkerCommands.SetVariableValue or WorkerCommands.SetVariableExpression
            or WorkerCommands.SetMaterial => 240_000,
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
            WorkerCommands.ListComponents => _sta.Run(() => ListComponents(request), "asm.list_components", cancellationToken),
            WorkerCommands.InsertComponent => _sta.Run(() => InsertComponent(request), "asm.insert_component", cancellationToken),
            WorkerCommands.SetComponentPlacement => _sta.Run(() => SetComponentPlacement(request), "asm.set_placement", cancellationToken),
            WorkerCommands.ReplaceComponent => _sta.Run(() => ReplaceComponent(request), "asm.replace_component", cancellationToken),
            WorkerCommands.CheckComponentLinks => _sta.Run(() => CheckComponentLinks(request), "asm.check_links", cancellationToken),
            WorkerCommands.ListMates => _sta.Run(() => ListMates(request), "mate.list", cancellationToken),
            WorkerCommands.CreateMate => _sta.Run(() => CreateMate(request), "mate.create", cancellationToken),
            WorkerCommands.SetMateParameter => _sta.Run(() => SetMateParameter(request), "mate.set_parameter", cancellationToken),
            WorkerCommands.SetMateFixed => _sta.Run(() => SetMateFixed(request), "mate.set_fixed", cancellationToken),
            WorkerCommands.DeleteMate => _sta.Run(() => DeleteMate(request), "mate.delete", cancellationToken),
            WorkerCommands.CreateDrawingViews => _sta.Run(() => CreateDrawingViews(request), "drawing.create_views", cancellationToken),
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
        object? outcome;
        try
        {
            outcome = mutation();
        }
        catch (Exception ex)
        {
            // FAILURE: the file is returned to its pre-mutation state, BUT ONLY WHEN THAT MAKES SENSE.
            // The pure ControlCopyRestorePolicy decides: a read_only document is not overwritten, and a
            // failure before COM deserves no write to the user's file.
            var contract = ex as KompasContractException;
            var decision = ControlCopyRestorePolicy.Decide(
                copy.Made, document.Access, contract?.Code, contract?.PartialEffects ?? false);
            var restoreFailure = decision.Restore
                ? _controlCopies.Restore(document.Path, copy.Path, document.Access)
                : null;
            throw Reclassify(ex, copy, decision, restoreFailure);
        }

        // SUCCESS: THE COPY IS DELETED. It was there for the failure case; left behind, it would
        // accumulate a whole document file per edit. A failed delete is NAMED in the response:
        // "the directory does not grow" and "the copy could not be removed" are different claims.
        var copyCleanupFailure = _controlCopies.DeleteAfterSuccess(copy.Path);

        var node = Tagged(documentId, document.Revision, outcome) ?? new JsonObject();
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

        return node;
    }

    /// <summary>Carry to the client that a control copy was taken and what became of it on failure. The original code
    /// and retry policy are PRESERVED: a failure does not change because a copy appeared.</summary> <remarks>For an
    /// UNEXPECTED mutation exception the policy is "after reconciliation", not "same operation_id": partialEffects=true
    /// means repeating would apply the mutation twice.</remarks>
    private static Exception Reclassify(
        Exception ex, ControlCopyResult copy, RestoreDecision decision, string? restoreFailure)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["control_copy"] = DocumentControlCopies.Describe(copy),
            ["control_copy_made"] = copy.Made,
            ["control_copy_path"] = copy.Path,
            // WHY THE FILE WAS NOT RESTORED — NAMED. "Not restored" without a reason is indistinguishable
            // from "we forgot to restore", and here the reason is substantive: a read_only document, or a
            // failure that never reached COM.
            ["restore_attempted"] = decision.Restore,
            ["restore_decision"] = decision.Reason,
            ["restored"] = decision.Restore && restoreFailure is null,
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
            ErrorCodes.OutcomeUnknown,
            "Мутация не завершилась: " + ex.Message + ". Исход неизвестен: файл возвращён к состоянию " +
            "до мутации, но модель в памяти КОМПАСа — нет, поэтому повтор требует согласования.",
            RetryPolicy.AfterReconciliation,
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

        // A launched KOMPAS needs up to a minute on a cold start; report what it actually took.
        var openDocuments = _session.Documents.Count(d => d.ApplicationId == application.Id);
        return application.ToDto(openDocuments);
    }

    private object? Disconnect(IpcFrame request)
    {
        var command = Argument<DisconnectCommand>(request);
        _session.Disconnect(command.ApplicationId, command.CloseOwnedApplication);
        return new JsonObject { ["application_id"] = command.ApplicationId, ["disconnected"] = true };
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

    /// <summary>Changing a sketch's support plane is a mutation: the revision is bumped and the document's references
    /// move with it. The revision is checked before COM; a sketch reference carries the document.</summary>
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

    // DRW (block drawings): the three mutating tools go through the common mutation point so the
    // envelope carries the revision AND the control copy is taken; the read goes through Tagged.
    // History: docs/decisions/drawings.md#worker

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

    private object? Measure(IpcFrame request) => _session.Measure(Argument<MeasureCommand>(request));

    private object? Resolve(IpcFrame request) => _session.ResolveSelection(Argument<ResolveSelectionCommand>(request));

    private object? Topology(IpcFrame request) => _session.ReadTopology(Argument<ReadTopologyCommand>(request));

    private object? ImportStep(IpcFrame request) => _session.ImportStep(Argument<ImportStepCommand>(request));

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

    /// <summary>Detach from every session. Documents belonging to the server are closed without saving; an attached
    /// KOMPAS is left running because it is the user's process.</summary> <remarks>Queued commands are NOT interrupted
    /// — a COM call cannot be cancelled from outside, so the outcome of an operation the Host called
    /// <c>OUTCOME_UNKNOWN</c> is determined by the model.</remarks>
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
