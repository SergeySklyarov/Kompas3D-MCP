using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Documents;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.Paths;

namespace KompasMcp.Api5Adapter;

/// <summary>One KOMPAS instance the Worker owns or is attached to, plus the documents registered
/// against it. Every method must run on the Worker's single STA thread.</summary>
/// <remarks>INVARIANT: a document is addressed by server UUID, never by "the active tab"; revisions are
/// server-side counters; COM references are held here and released on close (spec 1.6).
/// History: docs/decisions/adapter-core.md#session-identity-rules</remarks>
public sealed partial class Api5Session : IDisposable
{
    private readonly Dictionary<string, DocumentEntry> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApplicationEntry> _applications = new(StringComparer.Ordinal);

    public KompasMcp.Domain.References.ReferenceRegistry References { get; } = new();

    public bool IsConnected => _applications.Count > 0;

    public IReadOnlyCollection<ApplicationEntry> Applications => _applications.Values;

    public IReadOnlyCollection<DocumentEntry> Documents => _documents.Values;

    public DocumentEntry RequireDocument(string documentId)
    {
        if (!_documents.TryGetValue(documentId, out var document))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Документ '{documentId}' не зарегистрирован в этой сессии сервера.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = documentId,
                    ["known_documents"] = _documents.Keys.ToArray(),
                });
        }

        return document;
    }

    public DocumentEntry TryFindDocumentByPath(string normalizedPath)
    {
        foreach (var document in _documents.Values)
        {
            if (document.Path is not null && PathsEqual(document.Path, normalizedPath))
            {
                return document;
            }
        }

        return null!;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------------------------------------

    /// <summary>Attach to an existing instance or launch a new one, and prove which process the returned
    /// object belongs to (spec 1.6): a launch not attributable to exactly one new PID is refused.</summary>
    public ApplicationEntry Connect(ConnectCommand command)
    {
        if (command.Mode == ConnectMode.Launch)
        {
            return Launch(command);
        }

        return Attach(command);
    }

    /// <summary>Applies the requested visibility and records the document mode from the OBSERVED result.</summary>
    /// <remarks>INVARIANT: <c>launch</c> applies either value (the instance is ours); <c>attach</c> — true
    /// shows, false is NOT applied (another user's window is not hidden). Documents inherit the
    /// application's actual visibility, not "hidden by default".
    /// History: docs/decisions/adapter-core.md#session-compaction</remarks>
    private ApplicationEntry WithVisibility(ApplicationEntry entry, ConnectMode mode, bool makeVisible)
    {
        if (mode == ConnectMode.Attach && !makeVisible)
        {
            var current = entry.Observe();
            entry.DocumentsVisible = current.Visible;
            return entry;
        }

        entry.Window = ApplyApplicationVisibility(entry.Application, makeVisible);
        entry.DocumentsVisible = entry.Window.Visible;
        return entry;
    }

    private ApplicationEntry Launch(ConnectCommand command)
    {
        var serverType = Type.GetTypeFromProgID(KompasProgId, throwOnError: false)
            ?? throw new KompasContractException(
                ErrorCodes.ComRegistrationError,
                $"ProgID '{KompasProgId}' не зарегистрирован: КОМПАС недоступен для COM.");

        var before = KompasInteropResolver.SnapshotProcessIds("KOMPAS");
        object created;
        try
        {
            created = Activator.CreateInstance(serverType)
                ?? throw new KompasContractException(
                    ErrorCodes.LicenseUnavailable,
                    "Activator.CreateInstance вернул null: запуск КОМПАС не состоялся (лицензия или сессия пользователя).");
        }
        catch (COMException ex)
        {
            throw new KompasContractException(
                ComHResult.IsRegistrationFailure(ex.HResult) ? ErrorCodes.ComRegistrationError : ErrorCodes.ApplicationDisconnected,
                $"Запуск КОМПАС не удался: {ex.Message}",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult);
        }

        var after = KompasInteropResolver.SnapshotProcessIds("KOMPAS");
        var appeared = after.Except(before).ToArray();
        var appearedNote = appeared.Length == 1 ? appeared[0] : (int?)null;

        // Two ways to bind the object to a process; at least one must succeed (P0.4).
        var pid = ResolveProcessId(created, appeared);
        if (pid is null)
        {
            throw new KompasContractException(
                ErrorCodes.AmbiguousApplication,
                $"Запущен КОМПАС, но привязать объект к PID не удалось (новых процессов: {appeared.Length}). " +
                "Сеанс не регистрируется: дальше команды могли бы уйти не в тот экземпляр.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["new_process_ids"] = appeared });
        }

        return WithVisibility(
            Register(created, pid.Value, ApplicationOwnership.Launched, "launch"),
            ConnectMode.Launch, command.MakeVisible);
    }

    private ApplicationEntry Attach(ConnectCommand command)
    {
        // Scoped to the ProgID this adapter can drive: one running KOMPAS also registers its API7 CLSID,
        // which would look like a second instance. Every matching ROT entry is a candidate, including ones
        // whose COM object failed to bind or whose PID cannot be resolved: filtering them out made an
        // unattributable instance vanish and an implicit attach then picked the first one.
        // History: docs/decisions/adapter-core.md#session-compaction
        var entries = RunningObjectTable.EnumerateKompasEntries(KompasProgId);
        var candidates = entries
            .Select(e => (Entry: e, Pid: e.Object is null ? null : ProcessIdOf(e.Object)))
            .ToList();

        if (candidates.Count == 0)
        {
            // Report the unfiltered ROT total too: "no KOMPAS entries" and "the enumerator returned
            // nothing" are different diagnoses — without this the refusal is unfalsifiable.
            var (total, names) = RunningObjectTable.EnumerateAllEntries();
            var reason = total == 0
                ? "Перечисление ROT не вернуло ни одной записи вообще: либо КОМПАС не зарегистрирован в ROT, либо перечислитель их не видит."
                : $"В ROT {total} записей, но ни одна не сопоставима с КОМПАС.";

            throw new KompasContractException(
                ErrorCodes.AmbiguousApplication,
                reason + " Attach не выполнен; используйте mode=launch.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["rot_entries_matching_kompas"] = entries.Count,
                    ["rot_total_entries"] = total,
                    ["rot_sample_names"] = names.Take(10).ToArray(),
                });
        }

        if (command.ProcessId is int wanted)
        {
            var match = candidates.FirstOrDefault(c => c.Pid == wanted);
            if (match.Entry?.Object is null)
            {
                throw new KompasContractException(
                    ErrorCodes.AmbiguousApplication,
                    $"Среди экземпляров КОМПАС нет процесса {wanted} (наблюдены: " +
                    string.Join(", ", candidates.Select(c => c.Pid?.ToString() ?? "не атрибутирован")) + ").",
                    RetryPolicy.SameOperationId,
                    details: new Dictionary<string, object?>
                    {
                        ["requested_process_id"] = wanted,
                        ["observed_process_ids"] = candidates.Select(c => c.Pid).ToArray(),
                    });
            }

            return WithVisibility(
                Register(match.Entry.Object, match.Pid!.Value, ApplicationOwnership.Attached, "attach"),
                ConnectMode.Attach, command.MakeVisible);
        }

        var distinctPids = candidates.Select(c => c.Pid).Distinct().Count();
        if (candidates.Count > 1 || distinctPids > 1)
        {
            throw new KompasContractException(
                ErrorCodes.AmbiguousApplication,
                $"Найдено {candidates.Count} экземпляров КОМПАС (PID: {string.Join(", ", candidates.Select(c => c.Pid?.ToString() ?? "?"))}). " +
                "Нужен явный process_id: выбирать «первый попавшийся» запрещено.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?>
                {
                    ["candidates"] = candidates.Select(c => c.Entry.DisplayName).ToArray(),
                    ["process_ids"] = candidates.Select(c => c.Pid).ToArray(),
                });
        }

        var only = candidates[0];
        if (only.Pid is null)
        {
            throw new KompasContractException(
                ErrorCodes.AmbiguousApplication,
                "Единственный экземпляр не удаётся сопоставить с процессом; attach без привязки к PID не регистрируется.",
                RetryPolicy.SameOperationId);
        }

        return WithVisibility(
            Register(only.Entry.Object!, only.Pid.Value, ApplicationOwnership.Attached, "attach"),
            ConnectMode.Attach, command.MakeVisible);
    }

    private int? ResolveProcessId(object kompasObject, int[] newlyAppeared)
    {
        if (newlyAppeared.Length == 1)
        {
            return newlyAppeared[0];
        }

        return ProcessIdOf(kompasObject);
    }

    /// <summary>PID behind the application's main window; null while the instance is headless, which is
    /// exactly when the process-diff route has to carry the attribution.</summary>
    public static int? ProcessIdOf(object kompasObject)
    {
        try
        {
            if (kompasObject is not KompasObject app)
            {
                return null;
            }

            var raw = app.ksGetHWindow();
            var handle = new IntPtr(raw);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            return processId == 0 ? null : (int)processId;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private ApplicationEntry Register(object comObject, int processId, ApplicationOwnership ownership, string connectedAs)
    {
        if (comObject is not KompasObject app)
        {
            throw new KompasContractException(
                ErrorCodes.ComRegistrationError,
                "Объект не приводитесь к KompasObject — версия или разрядность не те.");
        }

        var version = ReadVersion(app);
        var entry = new ApplicationEntry(
            Guid.NewGuid().ToString("N"),
            app,
            processId,
            ownership,
            connectedAs,
            version);

        _applications[entry.Id] = entry;
        return entry;
    }

    public const string KompasProgId = "KOMPAS.Application.5";

    private static string ReadVersion(KompasObject app)
    {
        try
        {
            int major = 0, minor = 0, build = 0, revision = 0;
            app.ksGetSystemVersion(out major, out minor, out build, out revision);
            return $"{major}.{minor}.{build}.{revision}";
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException)
        {
            return "unknown";
        }
    }

    public void Disconnect(string applicationId, bool closeOwnedApplication)
    {
        if (!_applications.TryGetValue(applicationId, out var application))
        {
            throw new KompasContractException(ErrorCodes.ApplicationDisconnected, $"Сеанс '{applicationId}' не найден.");
        }

        foreach (var document in _documents.Values.Where(d => d.ApplicationId == applicationId).ToArray())
        {
            DropDocument(document);
        }

        try
        {
            // Killing the process is forbidden; Quit() is the documented shutdown of an instance the
            // server started itself, and only for owned instances (spec 1.6).
            if (closeOwnedApplication && application.Ownership == ApplicationOwnership.Launched)
            {
                application.Application.Quit();
            }
        }
        finally
        {
            ComApartment.Release(application.Application);
            _applications.Remove(applicationId);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Documents
    // ---------------------------------------------------------------------------------------------

    /// <summary>Session inventory: the instances and documents this adapter holds, with their unsaved flag.
    /// Changes nothing — it reads the registry and the document fingerprint.</summary>
    /// <remarks>The unsaved flag comes ONLY from <see cref="SaveStateOf"/> (the fingerprint and the state the
    /// server itself keeps), because the target version has no documented "document modified" property. That
    /// is why the inventory runs on the CAD lane: the fingerprint has to be read through COM.</remarks>
    public object Inventory()
    {
        var documents = Documents.Select(document =>
        {
            var state = SaveStateOf(document);
            return new
            {
                document_id = document.Id,
                application_id = document.ApplicationId,
                kind = document.Kind.ToString().ToLowerInvariant(),
                path = document.Path,
                revision = document.Revision,
                dirty = DocumentSaveTracking.IsDirty(state),
                save_state = state.ToString().ToLowerInvariant(),
            };
        }).ToArray();

        var applications = Applications.Select(application => new
        {
            application_id = application.Id,
            process_id = application.ProcessId,
            ownership = application.Ownership.ToString().ToLowerInvariant(),
            connected_as = application.ConnectedAs,
            version = application.Version,
            document_count = documents.Count(d => d.application_id == application.Id),
        }).ToArray();

        return new { applications, documents };
    }

    public ApplicationEntry RequireApplication(string applicationId) =>
        _applications.TryGetValue(applicationId, out var application)
            ? application
            : throw new KompasContractException(
                ErrorCodes.ApplicationDisconnected,
                $"Экземпляр '{applicationId}' не подключён; сначала kompas_connect.",
                RetryPolicy.SameOperationId);

    public DocumentEntry CreateDocument(CreateDocumentCommand command)
    {
        var application = RequireApplication(command.ApplicationId);

        // API5's ksDocument3D.Create makes only a part or an assembly. A DRAWING is created by the
        // documented 2D route instead: KompasObject.Document2D() + ksDocumentParam (type from DocType,
        // regime) + ksDocument2D.ksCreateDocument(par) — ksdocument2d_kscreatedocument.html,
        // ksdocumentparam_type.html, doctype.html (lt_DocSheetStandart = 1).
        // History: docs/decisions/drawings.md#create-drawing
        if (command.Kind == DocumentKind.Drawing)
        {
            return CreateDrawingDocument(application, command);
        }

        if (command.Kind == DocumentKind.Fragment)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Создание {command.Kind.ToString().ToLowerInvariant()} в этой сборке не реализовано: " +
                "фрагмент не входит в блок DRW, а выдавать деталь или сборку за фрагмент запрещено.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = command.Kind.ToString(),
                    ["supported_kinds"] = new[] { "part", "assembly", "drawing" },
                });
        }

        var document = (ksDocument3D)application.Application.Document3D();

        // Create(invisible, isDetail): the first argument is exactly the invisibility flag (P0.4b), taken
        // from the instance's observed mode, so a hidden session stays hidden.
        var isPart = command.Kind == DocumentKind.Part;
        if (!document.Create(!application.DocumentsVisible, isPart))
        {
            ComApartment.Release(document);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"Create(invisible=true, isDetail={isPart}) вернул false для типа {command.Kind}.",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        var part = (ksPart)document.GetPart(-1);
        if (command.Name is not null)
        {
            part.name = command.Name;
        }

        if (command.Marking is not null)
        {
            part.marking = command.Marking;
        }

        // Attribute assignment is committed only by Update() (P0.6: name and marking were silently
        // lost across save/reopen without it).
        part.Update();
        document.RebuildDocument();

        return RegisterDocument(document, part, application, command.Kind, path: null, kindVerified: true);
    }

    public DocumentEntry OpenDocument(OpenDocumentCommand command)
    {
        var application = RequireApplication(command.ApplicationId);

        var existing = TryFindDocumentByPath(command.Path);
        if (existing is not null && existing.ApplicationId == application.Id)
        {
            // Never open the same file twice behind the user's back (spec 2.4).
            return existing;
        }

        // A DRAWING ('.cdw') is opened through the documented 2D route, not Document3D: the 3D handle
        // has no parts and would misreport the kind. The extension is the only signal available BEFORE
        // the open, and it is confirmed by the document's own answer afterwards.
        // History: docs/decisions/drawings.md#open-drawing
        if (IsDrawingPath(command.Path))
        {
            return OpenDrawingDocument(application, command);
        }

        var document = (ksDocument3D)application.Application.Document3D();
        if (!document.Open(command.Path, !application.DocumentsVisible))
        {
            ComApartment.Release(document);
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Открыть документ не удалось: {command.Path}",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["path"] = command.Path });
        }

        var isDetail = CallIsDetail(document);
        var kind = isDetail switch
        {
            true => DocumentKind.Part,
            false => DocumentKind.Assembly,
            null => command.Access == DocumentAccess.ReadOnly ? DocumentKind.Part : DocumentKind.Part,
        };

        if (kind == DocumentKind.Part && command.Access == DocumentAccess.Edit && IsReadOnlyHint(command.Path))
        {
            // Nothing to enforce here beyond reporting; KOMPAS itself refuses the write later.
        }

        var part = (ksPart)document.GetPart(-1);
        var entry = RegisterDocument(document, part, application, kind, command.Path, kindVerified: isDetail is not null);

        // ACCESS IS REMEMBERED BECAUSE IT DECIDES THE OUTCOME OF A LATER WRITE: a document opened
        // `read_only` must not be saved "in place" — the save call carries no path field, so the Host's
        // policy does not judge it (defect H4). The access flag closes that write.
        // History: docs/decisions/adapter-core.md#readonly-save-guard
        entry.Access = command.Access;
        return entry;
    }

    private static bool IsReadOnlyHint(string path)
    {
        try
        {
            return new FileInfo(path).IsReadOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether a path names a drawing sheet. INVARIANT: the extension only routes the OPEN; the
    /// kind is confirmed by the document afterwards, so a wrongly-named file becomes a named refusal.</summary>
    private static bool IsDrawingPath(string path) =>
        string.Equals(Path.GetExtension(path), ".cdw", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>regim</c> of <c>ksOpenDocument</c> for a drawing: FALSE = visible. The blind mode
    /// (TRUE) is documented for hidden batch generation and MEASURED to cost the drawing its API7
    /// identity after a converter export — see <see cref="OpenDrawingDocument"/>.</summary>
    private const bool DrawingOpenRegimVisible = false;

    /// <summary>Create a drawing. The API7 documented route <c>IDocuments.Add</c> is preferred; the API5
    /// 2D route is the fallback (see History).</summary>
    /// <remarks>MEASURED: with cell, text, write order and re-read expression held identical, a drawing
    /// made through API7 <c>Documents.Add</c> reads its <c>IStamp.Text[id].Str</c> back UNCHANGED after
    /// <c>Update()</c>, while one made through API5 <c>ksCreateDocument</c> reads an empty string — the
    /// difference is a property of the creation ROUTE; the internal cause is not established. API7 is
    /// chosen first so a title-block write round-trips and the drawing carries a NATIVE
    /// <c>IDrawingDocument</c>. History: docs/decisions/drawings.md#create-drawing</remarks>
    private DocumentEntry CreateDrawingDocument(ApplicationEntry application, CreateDocumentCommand command)
    {
        // ROUTE 1 (preferred) — documented API7 IDocuments.Add. The document types differ between the two
        // APIs, so the API7 enum member is used here and the API5 DocType in the fallback below.
        var bridge = BridgeForApplication(application);
        if (bridge.Application() is { Documents: { } documents })
        {
            try
            {
                if (documents.Add(DocumentTypeEnum.ksDocumentDrawing, application.DocumentsVisible)
                        is IDrawingDocument added)
                {
                    return RegisterDrawing7(application, command, added, route: "API7.Documents.Add");
                }
            }
            catch (COMException ex)
            {
                // Fall through to the API5 route rather than fail: the choice is a preference, and a
                // refused preferred route must not cost the caller the working one. The failure is kept
                // for the note below when BOTH routes fail.
                _lastDrawingRouteFailure = $"IDocuments.Add прервался: {ex.Message}";
            }
        }
        else
        {
            _lastDrawingRouteFailure = "API7 недоступен: " + (bridge.BridgeFailure ?? "причина не названа");
        }

        // ROUTE 2 (fallback) — documented API5 2D ksCreateDocument.
        return CreateDrawingDocumentApi5(application, command, _lastDrawingRouteFailure);
    }

    /// <summary>Why the preferred API7 drawing route was not taken (carried into a both-routes refusal).</summary>
    private string? _lastDrawingRouteFailure;

    /// <summary>Register a drawing created through the API7 route: it has a NATIVE <c>IDrawingDocument</c>
    /// and no API5 2D handle of its own, so <see cref="DocumentEntry.Drawing"/> stays null and every 2D
    /// route reaches the sheet through <see cref="DocumentEntry.Drawing7"/>.</summary>
    private DocumentEntry RegisterDrawing7(
        ApplicationEntry application, CreateDocumentCommand command, IDrawingDocument added, string route)
    {
        var entry = RegisterDocument(
            document: null, part: null, application, DocumentKind.Drawing, path: null,
            kindVerified: true, drawing: null, drawing7: added);
        entry.LastRevisionReason = "create:" + route;
        entry.Drawing7Resolution = "native:" + route;
        return entry;
    }

    /// <summary>Create a drawing through the documented API5 2D route (fallback of
    /// <see cref="CreateDrawingDocument"/>).</summary>
    private DocumentEntry CreateDrawingDocumentApi5(
        ApplicationEntry application, CreateDocumentCommand command, string? api7Failure)
    {
        var application5 = application.Application;
        var param = application5.GetParamStruct(KompasStructTypes.DocumentParam) as ksDocumentParam;
        if (param is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "KompasObject.GetParamStruct(ksDocumentParam) не вернул параметры документа: "
                + "создать чертёж этим маршрутом нельзя. Предпочтительный маршрут API7 тоже не сработал: "
                + (api7Failure ?? "причина не названа") + ".",
                RetryPolicy.Never);
        }

        param.Init();
        param.type = KompasDocTypes.DrawingSheetStandard;
        param.regime = (short)(application.DocumentsVisible ? 0 : 1);
        if (command.Name is { Length: > 0 })
        {
            // The name is a document attribute, not a file name (fileName would imply saving).
            param.comment = command.Name;
        }

        var drawing = application5.Document2D() as ksDocument2D;
        if (drawing is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "KompasObject.Document2D() не вернул ksDocument2D: создать чертёж этим маршрутом нельзя. "
                + "Предпочтительный маршрут API7 тоже не сработал: " + (api7Failure ?? "причина не названа") + ".",
                RetryPolicy.Never);
        }

        bool created;
        try
        {
            created = drawing.ksCreateDocument(param);
        }
        catch (COMException ex)
        {
            ComApartment.Release(drawing);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                $"ksCreateDocument прервался: {ex.Message}. Чертёж не создан. Предпочтительный маршрут "
                + "API7 тоже не сработал: " + (api7Failure ?? "причина не названа") + ".",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult);
        }

        if (!created)
        {
            ComApartment.Release(drawing);
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "ksCreateDocument вернул false: ядро отказало в создании чертежа.",
                RetryPolicy.SameOperationId);
        }

        ComApartment.Release(param);
        return RegisterDocument(
            document: null, part: null, application, DocumentKind.Drawing, path: null,
            kindVerified: true, drawing);
    }

    /// <summary>Open an existing drawing through the documented API5 2D route.</summary>
    /// <remarks>DOC: <c>ksdocument2d_ksopendocument.html</c> — <c>ksOpenDocument(nameDoc, regim)</c>,
    /// where <c>regim</c> is «0 - видимый, 1 - невидимый ("слепой")» and «Невидимый режим применяется
    /// при пакетном (скрытом) формировании документа». The drawing is opened with <c>regim = 0</c>
    /// (visible): the blind mode is documented for hidden BATCH GENERATION, and a drawing the session
    /// must keep addressing through API7 is not that case.
    /// MEASURED: a drawing opened with <c>regim = 1</c> stops being addressable through API7 as soon as
    /// <c>IConverter.Convert</c> runs on it — the API7 collection drops to 0, a fresh transfer no longer
    /// yields a drawing, <c>ViewsAndLayersManager</c> is lost and <c>RebuildDocument()</c> does not
    /// restore it; the same export leaves a <c>regim = 0</c> drawing intact, and this holds with the
    /// APPLICATION invisible. LIMIT: the internal cause is NOT established — the note in
    /// <c>iconverter_convert.html</c> («конвертация будет происходить в новый документ системы
    /// КОМПАС») is conditioned on a missing output file name, which this tool always passes, so it does
    /// not explain the observation and is not cited as its cause.
    /// History: docs/decisions/drawings.md#open-drawing</remarks>
    private DocumentEntry OpenDrawingDocument(ApplicationEntry application, OpenDocumentCommand command)
    {
        var drawing = application.Application.Document2D() as ksDocument2D;
        if (drawing is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "KompasObject.Document2D() не вернул ksDocument2D: открыть чертёж этим маршрутом нельзя.",
                RetryPolicy.Never);
        }

        bool opened;
        try
        {
            opened = drawing.ksOpenDocument(command.Path, DrawingOpenRegimVisible);
        }
        catch (COMException ex)
        {
            ComApartment.Release(drawing);
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"ksOpenDocument прервался: {ex.Message}. Чертёж не открыт.",
                RetryPolicy.SameOperationId,
                hresult: ex.HResult,
                details: new Dictionary<string, object?> { ["path"] = command.Path });
        }

        if (!opened)
        {
            ComApartment.Release(drawing);
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Открыть чертёж не удалось: {command.Path}",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["path"] = command.Path });
        }

        var entry = RegisterDocument(
            document: null, part: null, application, DocumentKind.Drawing, command.Path,
            kindVerified: true, drawing);
        entry.Access = command.Access;
        return entry;
    }

    private static bool? CallIsDetail(ksDocument3D document)
    {
        try
        {
            var method = typeof(ksDocument3D).GetMethod("IsDetail");
            return method?.Invoke(document, null) as bool?;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
            return null;
        }
    }

    private DocumentEntry RegisterDocument(
        ksDocument3D? document,
        ksPart? part,
        ApplicationEntry application,
        DocumentKind kind,
        string? path,
        bool kindVerified,
        ksDocument2D? drawing = null,
        IDrawingDocument? drawing7 = null)
    {
        var entry = new DocumentEntry(
            Guid.NewGuid().ToString("N"),
            application.Id,
            kind,
            NormalizePath(path) ?? NormalizePath(part is null ? null : SafeFileName(part)),
            document,
            part,
            drawing,
            drawing7)
        {
            Revision = 1,
            KindVerified = kindVerified,
            DocumentsVisible = application.DocumentsVisible,
        };

        // A document can be created "invisibly" and still end up on screen (and vice versa), so its state
        // is re-read from the document itself. A drawing has no 3D presentation call, so its activation
        // is left UNREAD (null) rather than substituted by the requested mode.
        if (document is not null)
        {
            var (activated, _) = PresentDocument(application.Application, document, application.DocumentsVisible);
            entry.ActiveReported = activated;
            entry.Visible = application.DocumentsVisible ? ObserveDocumentVisible(document) : false;
        }

        entry.Fingerprint = ComputeFingerprint(entry);

        // Open: KOMPAS read it from disk, so the model matches the file. Create: the document exists only
        // in memory and was never written — a change the close must notice.
        entry.SaveState = entry.Path is null
            ? DocumentSaveTracking.AfterCreate()
            : DocumentSaveTracking.AfterOpen();

        _documents[entry.Id] = entry;
        return entry;
    }

    /// <summary>Document that owns a minted reference, without resolving or validating it.</summary>
    public string? OwningDocumentId(string referenceId) =>
        References.TryGet(referenceId, out var stored) && stored is not null ? stored.DocumentId : null;

    /// <summary>Document owning a reference, for commands addressed by reference rather than by document id.
    /// An unknown handle is the same STALE_REFERENCE the resolution path would report.</summary>
    public DocumentEntry DocumentForReference(string referenceId)
    {
        var documentId = OwningDocumentId(referenceId)
            ?? throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка '{referenceId}' не найдена в реестре.",
                RetryPolicy.ReacquireContext);

        return RequireDocument(documentId);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>File name KOMPAS reports for a component, or null. Read defensively: an unsaved document
    /// reports an empty string and some interop versions expose the member as a property, not a getter.</summary>
    private static string SafeFileName(ksPart part)
    {
        try
        {
            return part.fileName ?? string.Empty;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
            return string.Empty;
        }
    }

    public DocumentContextDto Context(DocumentEntry document, bool includeTopology)
    {
        var bodies = CountBodies(document);
        var features = CountFeatures(document);
        return new DocumentContextDto
        {
            Id = document.Id,
            ApplicationId = document.ApplicationId,
            Kind = document.Kind,
            Path = document.Path,
            Dirty = IsDirty(document),
            Revision = document.Revision,
            FeatureCount = features,
            BodyCount = bodies,
            ComponentCount = document.Kind == DocumentKind.Assembly ? CountComponents(document) : 0,
            UnitSystem = "mm",
            Origin = new double[] { 0, 0, 0 },
            Fingerprint = document.Fingerprint,
            // Document visibility is separate: the application can be shown while the document is not.
            DocumentVisible = document.Visible,
            DocumentsVisibleMode = document.DocumentsVisible,
            DocumentActiveReported = document.ActiveReported,
            // KOMPAS events are not wired in this build: a UI edit is caught only by the pre-mutation check.
            ExternalChangeDetection = ExternalChangeDetection.Conservative,
        };
    }

    public bool IsDirty(DocumentEntry document) => DocumentSaveTracking.IsDirty(SaveStateOf(document));

    /// <summary>Document save state: whether the model KOMPAS holds is exactly what is on disk. Kept by the
    /// server because the target version has no documented "document modified" flag.</summary>
    /// <remarks>The fingerprint answers one question — did anyone other than us change the model (a UI edit
    /// does not pass through <see cref="BumpRevision"/>). An unreadable fingerprint does NOT prove
    /// immutability and yields "unknown", not "clean".
    /// History: docs/decisions/adapter-core.md#save-state-fingerprint</remarks>
    public DocumentSaveState SaveStateOf(DocumentEntry document)
    {
        var observed = ComputeFingerprint(document);
        if (observed == DocumentSaveTracking.UnreadableFingerprint)
        {
            return DocumentSaveTracking.AfterUnreadableObservation();
        }

        if (document.Fingerprint is not null && observed != document.Fingerprint)
        {
            // The baseline is not moved: until the document is saved or changed via MCP, a noticed
            // discrepancy stays noticed rather than being "forgotten" after the first read.
            return DocumentSaveTracking.AfterExternalChange();
        }

        return document.SaveState;
    }

    /// <summary>Cheap state digest used because KOMPAS change events are not subscribed in this build.
    /// Deliberately coarse: its job is to catch "the user changed the model", not to identify which
    /// feature moved, and it is NOT evidence that the file on disk is current (that is
    /// <see cref="DocumentSaveState"/>).</summary>
    public string ComputeFingerprint(DocumentEntry document)
    {
        try
        {
            var bodies = CountBodies(document);
            if (bodies < 0)
            {
                // The body collection did not answer. A fingerprint of "0:0:…" would turn a read failure
                // into an invented external edit; a read failure is named a read failure.
                return DocumentSaveTracking.UnreadableFingerprint;
            }

            var features = CountFeatures(document);
            var gabarit = TryGetGabarit(document, out var dims);
            var dimsText = gabarit ? string.Join(";", dims.Select(d => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture))) : "n/a";
            return $"{bodies}:{features}:{dimsText}";
        }
        catch (Exception ex) when (ex is COMException)
        {
            return DocumentSaveTracking.UnreadableFingerprint;
        }
    }

    public bool TryGetGabarit(DocumentEntry document, out double[] dimensions)
    {
        dimensions = Array.Empty<double>();
        if (document.Document is null)
        {
            // A drawing has no 3D model space: there is no gabarit to read, and "not read" is the honest
            // answer (a zero box would be an invented measurement).
            return false;
        }

        try
        {
            if (!document.PartNow().GetGabarit(true, true, out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
            {
                return false;
            }

            dimensions = new[] { x2 - x1, y2 - y1, z2 - z1, x1, y1, z1, x2, y2, z2 };
            return true;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
            return false;
        }
    }

    public int CountBodies(DocumentEntry document)
    {
        if (document.Document is null)
        {
            // A drawing has no body collection. A flat sheet is not "a body", and 0 would read as
            // "the part has no geometry".
            return -1;
        }

        try
        {
            var bodies = (ksBodyCollection)document.PartNow().BodyCollection();
            // Same refresh rule as ListBodies: a collection read right after a rebuild can report stale
            // membership, disagreeing with the measurement.
            bodies.refresh();
            return bodies.GetCount();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return -1;
        }
    }

    public int CountFeatures(DocumentEntry document)
    {
        if (document.Document is null)
        {
            return 0;
        }

        try
        {
            var collection = (ksEntityCollection)document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.OperationElement));
            return collection.GetCount();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }
    }

    /// <summary>Assembly component count — from the STRUCTURE (<c>IPart7.PartsEx</c>), not the API5 collection.</summary>
    /// <remarks>MEASURED: the API5 <c>EntityCollection(o3d_part = 104)</c> route is not a component count;
    /// the count comes from API7 <c>IAssemblyDocument.TopPart</c> → <c>IPart7.PartsEx(ksAllParts)</c>,
    /// recursively over subassemblies. LIMIT: the walk is bounded by depth (64 levels), so a subassembly
    /// cycle cannot loop the server.
    /// History: docs/decisions/adapter-core.md#component-count-structure</remarks>
    public int CountComponents(DocumentEntry document)
    {
        var notes = new List<string>();
        var top = TopPart7(document, notes);
        if (top is null)
        {
            return 0;
        }

        var count = 0;
        CountComponentsInto(top, ref count, depth: 0, notes);
        return count;
    }

    private const int MaxComponentDepth = 64;

    private void CountComponentsInto(IPart7 node, ref int count, int depth, List<string> notes)
    {
        if (depth >= MaxComponentDepth)
        {
            notes.Add($"component_depth_limit — обход структуры остановлен на глубине {MaxComponentDepth}");
            return;
        }

        foreach (var child in ChildrenOf(node, notes))
        {
            count++;
            var isDetail = Bool(() => child.Detail);
            if (isDetail is null)
            {
                // An unread "detail/assembly" flag is not replaced by `true` or `false`: the walk under an
                // unknown node is NOT continued, and an unread value must not PERMIT the walk.
                notes.Add("component_detail_unread — признак «деталь/сборка» не прочитан: обход под " +
                          "этим узлом не продолжен, число компонентов может быть неполным");
                continue;
            }

            if (isDetail == false)
            {
                CountComponentsInto(child, ref count, depth + 1, notes);
            }
        }
    }

    /// <summary>Raise the revision and move the document's references with it.</summary>
    /// <param name="reason">Recorded for diagnostics ("extrude", "rebuild", …).</param>
    /// <param name="invalidateAll">True when the model was re-read or rebuilt rather than edited by this
    /// server: then handles from the previous revision must not resolve. False for an ordinary mutation.</param>
    public void BumpRevision(DocumentEntry document, string reason, bool invalidateAll = false)
    {
        document.Revision++;
        References.RevisionForward(document.Id, document.Revision, invalidateAll);

        // The single point every mutation passes through, so the redraw does not depend on which operation
        // changed the model. In hidden mode it does nothing (see RefreshViewAfterMutation).
        RefreshViewAfterMutation(document);

        if (invalidateAll)
        {
            // Analytic profile areas belong to sketch handles; once those stop resolving, a stale area must
            // not be reused as the expected volume of a later feature. The same covers the drawn profile's
            // extent and the plane it was drawn on.
            foreach (var orphan in _sketchProfiles.Keys.Where(key => !References.TryGet(key, out _)).ToArray())
            {
                _sketchProfiles.Remove(orphan);
            }

            foreach (var orphan in _sketchProfileBox.Keys.Where(key => !References.TryGet(key, out _)).ToArray())
            {
                _sketchProfileBox.Remove(orphan);
            }

            foreach (var orphan in _sketchPlaneBase.Keys.Where(key => !References.TryGet(key, out _)).ToArray())
            {
                _sketchPlaneBase.Remove(orphan);
            }
        }

        document.Fingerprint = ComputeFingerprint(document);
        document.LastRevisionReason = reason;

        // Bumping the revision does NOT confirm savedness: the only place a document is declared saved is a
        // confirmed write to file (SaveDocument); doing it here would defeat both close guards.
        document.SaveState = DocumentSaveTracking.AfterMutation();
    }

    public void CloseDocument(DocumentEntry document, DirtyPolicy dirtyPolicy)
    {
        var state = SaveStateOf(document);
        switch (DocumentSaveTracking.Decide(state, dirtyPolicy))
        {
            case CloseAction.Refuse:
                throw new KompasContractException(
                    ErrorCodes.DocumentDirty,
                    "Документ изменён и не сохранён; закрытие отклонено.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["document_id"] = document.Id,
                        ["dirty_state"] = state.ToString(),
                    });

            case CloseAction.SaveThenClose:
                // A failed save throws — the document stays open and still modified, not "closed with
                // the edit lost".
                SaveDocument(document, targetPath: null);
                break;
        }

        DropDocument(document);
    }

    private void DropDocument(DocumentEntry document)
    {
        References.InvalidateDocument(document.Id);
        _documents.Remove(document.Id);
        try
        {
            // A drawing created through the API7 route closes through its own API7 IKompasDocument.Close;
            // one created/opened through API5 closes through its 2D handle. A 3D document closes through
            // the 3D handle. Each branch names the handle it actually holds.
            if (document.Document is not null)
            {
                document.Document.close();
            }
            else if (document.Drawing is not null)
            {
                document.Drawing.ksCloseDocument();
            }
            else if (document.Drawing7 is not null)
            {
                // Save-then-close was already performed by CloseDocument before the drop, so the drop
                // itself discards. DOC: ikompasdocument_close.html + documentcloseoptions.html
                // (kdDoNotSaveChanges = 0). MEASURED: the close returns Boolean, reported nowhere — the
                // registry entry is removed regardless, exactly as for the other two handles.
                if (document.Drawing7 is IKompasDocument kompasDocument)
                {
                    kompasDocument.Close(DocumentCloseOptions.kdDoNotSaveChanges);
                }
            }
        }
        catch (Exception ex) when (ex is COMException)
        {
            // Already closed by the user; the registry entry is gone regardless.
        }
        finally
        {
            if (document.Document is not null)
            {
                ComApartment.Release(document.PartNow());
            }

            ComApartment.Release(document.Document);
            ComApartment.Release(document.Drawing);
        }
    }

    public SaveDocumentResult SaveDocument(DocumentEntry document, string? targetPath)
    {
        var path = targetPath is null ? document.Path : NormalizePath(targetPath);
        if (path is null)
        {
            // The document has no file name: per the docs Save() writes "to the file with the previously set
            // name", and calling it without a name yields either a name prompt or false. A new document with
            // no path therefore refuses explicitly rather than "saving" silently.
            throw new KompasContractException(
                ErrorCodes.SaveFailed,
                "Сохранить документ без имени нельзя: путь не задан ни документом, ни вызовом.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
        }

        // WRITING TO THE FILE OF A DOCUMENT OPENED READ-ONLY. A call without target_path carries no path
        // field, so the Host's policy does not check it (defect H4). Saving "in place" is closed here,
        // while "save as" to a named path stays allowed.
        // History: docs/decisions/adapter-core.md#readonly-save-guard
        if (targetPath is null && document.Access == DocumentAccess.ReadOnly)
        {
            throw new KompasContractException(
                ErrorCodes.PathNotAllowed,
                $"Сохранение документа «{document.Id}» в его файл '{path}' отклонено: документ открыт " +
                "с access=read_only. Запись в исходный файл этим доступом не разрешена; укажите " +
                "target_path внутри записываемого корня.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["path"] = path,
                    ["access"] = "read_only",
                });
        }

        bool ok;
        if (document.Document is not null)
        {
            ok = targetPath is null
                ? document.Document.Save()
                : document.Document.SaveAs(path);
        }
        else if (document.Drawing is not null)
        {
            // A drawing saves through its documented 2D route: ksDocument2D.ksSaveDocument(fileName)
            // (ksdocument2d.html). It has no "save to previous name" form, so a document that has a path
            // but no target_path still passes that path explicitly.
            ok = document.Drawing.ksSaveDocument(path);
        }
        else if (document.Drawing7 is IKompasDocument kompasDocument)
        {
            // An API7-created drawing has no API5 2D handle and saves through IKompasDocument.
            // DOC: ikompasdocument_save.html — Save() writes to the document's own file;
            // ikompasdocument_saveas.html — SaveAs(path) writes to a named file. MEASURED: both return
            // Void, so the write is confirmed ONLY by the file re-read below (ReadBackSavedFile), never
            // by a return value — the same discipline as the API5 3D route's boolean, taken further.
            if (targetPath is null)
            {
                kompasDocument.Save();
            }
            else
            {
                kompasDocument.SaveAs(path);
            }
            ok = true;
        }
        else
        {
            throw new KompasContractException(
                ErrorCodes.SaveFailed,
                $"Документ «{document.Id}» не имеет ни 3D-, ни 2D-дескриптора: сохранять нечем.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
        }

        if (!ok)
        {
            // A failed save does not clear the state: the document stays modified.
            throw new KompasContractException(
                ErrorCodes.SaveFailed,
                $"Сохранение не подтверждено возвращаемым значением ({(targetPath is null ? "Save" : "SaveAs")}).",
                RetryPolicy.SameOperationId,
                partialEffects: true);
        }

        var written = ReadBackSavedFile(path);
        if (written is null)
        {
            throw new KompasContractException(
                ErrorCodes.SaveFailed,
                $"Сохранение вернуло успех, но файл не перечитан по пути '{path}'.",
                RetryPolicy.SameOperationId,
                partialEffects: true,
                details: new Dictionary<string, object?> { ["path"] = path });
        }

        document.Path = path;
        document.Document?.UpdateDocumentParam();
        // "Save as" to a named path was checked by the Host as writable, so writing to the document's file
        // is now allowed: the access flag follows the path rather than staying forever from the open.
        if (targetPath is not null)
        {
            document.Access = DocumentAccess.Edit;
        }

        BumpRevision(document, "save");

        // The only place savedness is declared confirmed: the operation returned success AND the file was
        // re-read from disk — it is the file that is read, not a flag inside CAD.
        document.SaveState = DocumentSaveTracking.AfterConfirmedSave();
        document.SavedFileSha256 = written.Value.Sha256;
        document.SavedFileByteLength = written.Value.ByteLength;
        document.SavedAtUtc = written.Value.WrittenUtc;

        var info = new FileInfo(path);
        return new SaveDocumentResult(
            path,
            written.Value.ByteLength,
            written.Value.Sha256,
            Context(document, includeTopology: false),
            document.Revision);
    }

    /// <summary>Re-read the saved file: existence, size, hash and write time. This is the "subsequent read"
    /// that confirms savedness, unlike a flag change inside CAD, which says nothing about the file.</summary>
    private static (long ByteLength, string Sha256, DateTime WrittenUtc)? ReadBackSavedFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0)
            {
                return null;
            }

            return (info.Length, FileHash.Sha256(path), info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var document in _documents.Values.ToArray())
        {
            try
            {
                DropDocument(document);
            }
            catch (Exception ex) when (ex is KompasContractException or COMException)
            {
                // Shutdown must not be blocked by one stuck document.
            }
        }

        _applications.Clear();
    }
}

/// <summary>An application the Worker holds a live COM handle to.</summary>
public sealed record ApplicationEntry(
    string Id,
    KompasObject Application,
    int ProcessId,
    ApplicationOwnership Ownership,
    string ConnectedAs,
    string Version)
{
    /// <summary>The mode in which this instance should create and open documents. Derived from the observed
    /// application visibility after make_visible is applied, not from what was requested: showing the
    /// application does not mean showing already open documents.</summary>
    public bool DocumentsVisible { get; set; }

    /// <summary>The latest window observation: the COM property, the Windows answer, and the document window
    /// titles.</summary>
    public Api5Session.WindowObservation? Window { get; set; }

    public Api5Session.WindowObservation Observe() =>
        (Window = Api5Session.ObserveApplicationWindow(Application));

    public ApplicationInfoDto ToDto(int openDocuments)
    {
        // The observation is taken afresh: the window state can change outside the session.
        var observed = Observe();
        return new ApplicationInfoDto
        {
            ApplicationId = Id,
            ProcessId = ProcessId,
            Version = Version,
            Ownership = Ownership,
            ConnectedAs = ConnectedAs,
            ExecutablePath = KompasInteropResolver.LocalServerPath(Api5Session.KompasProgId),

            // Visible when both the COM property and Windows agree: "a PID is obtained from the HWND" was
            // falsely positive by construction, since a hidden window also has an HWND.
            Visible = observed.Visible,
            ApplicationVisibleByCom = observed.ComProperty,
            ApplicationWindowVisibleByWindows = observed.WindowVisible,
            WindowHandle = observed.WindowHandle,
            DocumentVisibleTitles = observed.VisibleChildTitles,
            VisibilityObservationError = observed.Error,
            DocumentsVisible = DocumentsVisible,
            OpenDocumentCount = openDocuments,
        };
    }

    public static int? ProcessIdOf(KompasObject application) => Api5Session.ProcessIdOf(application);
}

/// <summary>A document registered by the server, with its live COM handles and revision.</summary>
/// <remarks>MEASURED: a DRAWING has no <c>ksDocument3D</c>/<c>ksPart</c> at all — it is an API5
/// <c>ksDocument2D</c> (created by <c>ksCreateDocument</c>) and an API7 <c>IDrawingDocument</c>. The two
/// 3D handles are therefore nullable, and every 3D route names its absence instead of dereferencing null.
/// History: docs/decisions/drawings.md#document-entry</remarks>
public sealed class DocumentEntry
{
    public DocumentEntry(
        string id, string applicationId, DocumentKind kind, string? path,
        ksDocument3D? document, ksPart? part, ksDocument2D? drawing = null,
        IDrawingDocument? drawing7 = null)
    {
        Id = id;
        ApplicationId = applicationId;
        Kind = kind;
        Path = path;
        Document = document;
        Part = part;
        Drawing = drawing;
        Drawing7 = drawing7;
    }

    public string Id { get; }

    public string ApplicationId { get; }

    public DocumentKind Kind { get; internal set; }

    public string? Path { get; internal set; }

    /// <summary>The access mode the document is open in. It decides whether ITS file may be written: a save
    /// call without <c>target_path</c> carries no path field and the Host's policy does not judge it.
    /// Defaults to <see cref="DocumentAccess.Edit"/> — a created document has no file of its own yet.</summary>
    public DocumentAccess Access { get; set; } = DocumentAccess.Edit;

    /// <summary>The API5 3D document handle. Null for a drawing.</summary>
    public ksDocument3D? Document { get; }

    /// <summary>The 3D handle as a NON-NULL value, or a NAMED refusal. Every 3D-only operation reads the
    /// document through this member so that calling one on a drawing yields <c>DOCUMENT_KIND_MISMATCH</c>
    /// rather than a null dereference — the crash would be an instrument defect, not a fact about the
    /// document. Same contract as <see cref="Require3D"/>, kept as a property for call-site ergonomics.</summary>
    public ksDocument3D Document3D => Document
        ?? throw new KompasContractException(
            ErrorCodes.WrongDocumentKind,
            $"Документ «{Id}» имеет тип {Kind}: трёхмерный маршрут к нему не применим.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["kind"] = Kind.ToString() });

    /// <summary>The API5 root part handle. Null for a drawing.</summary>
    public ksPart? Part { get; }

    /// <summary>The API5 2D document handle (<c>ksDocument2D</c>). Non-null only for a drawing; a 3D
    /// document reaches 2D only through a sketch edit, which is a different, transient handle.</summary>
    public ksDocument2D? Drawing { get; }

    /// <summary>The API7 drawing handle. Set at creation when the drawing was made through the API7 route
    /// (<c>IDocuments.Add</c>), and — MEASURED — ALSO cached here after the FIRST successful resolution of
    /// a reopened drawing by the bridge. A reopened drawing has no native handle, so without the cache
    /// every call re-transfers the API5 2D handle; that transfer was measured to stop yielding a drawing
    /// once another document has been created and closed in the same session, while the draft obtained
    /// beforehand keeps working. Holding the first successful value keeps later calls from depending on a
    /// transfer that can degrade. Null when the drawing was never resolved as an API7 drawing.
    /// History: docs/decisions/drawings.md#create-drawing</summary>
    public IDrawingDocument? Drawing7 { get; internal set; }

    /// <summary>Which stage produced <see cref="Drawing7"/>, for the refusal diagnosis: the API7 route
    /// that minted the native handle, or the bridge transfer / fresh transfer / collection scan that
    /// resolved a reopened drawing. Recorded so a LATER refusal can say whether the handle it is now
    /// missing was ever obtained, and how.</summary>
    public string? Drawing7Resolution { get; internal set; }

    /// <summary>The 3D handle, or a NAMED refusal: a drawing has no <c>ksDocument3D</c>, and a null
    /// dereference would be an instrument crash, not a fact about the document.</summary>
    public ksDocument3D Require3D() => Document
        ?? throw new KompasContractException(
            ErrorCodes.WrongDocumentKind,
            $"Документ «{Id}» имеет тип {Kind}: трёхмерный маршрут к нему не применим.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?> { ["kind"] = Kind.ToString() });

    /// <summary>The visibility mode this document was created or opened in: inherited from the application
    /// instance. Separate from <see cref="Visible"/>: what was requested and what the document reports about
    /// itself are different quantities, and conflating the two is what produced a false visible=true.</summary>
    public bool DocumentsVisible { get; set; }

    /// <summary>Re-read document state (<c>!ksDocument3D.invisibleMode</c>), null — could not be read.</summary>
    public bool? Visible { get; set; }

    /// <summary>What <c>SetActive()</c> answered: document presentation separate from application
    /// presentation.</summary>
    public bool? ActiveReported { get; set; }

    /// <summary>Current root part of the document.</summary>
    /// <remarks>The handle captured at creation goes stale once a feature is created: a cached <c>ksPart</c>
    /// returns an empty <c>BodyCollection</c> and a null <c>GetMainBody()</c> for a document that has a solid
    /// body. Re-acquiring per operation is what makes reads agree with what KOMPAS holds; <see cref="Part"/>
    /// is kept for identity checks and release bookkeeping only.
    /// History: docs/decisions/adapter-core.md#part-now-reacquire</remarks>
    public ksPart PartNow() => (ksPart)Require3D().GetPart(-1);

    public long Revision { get; set; }

    /// <summary>Fingerprint of the last OBSERVATION of the model: it catches an edit made outside MCP. It is
    /// not a savedness flag — <see cref="SaveState"/> handles that.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Savedness state: whether a write to file confirmed the current model is on disk.</summary>
    public DocumentSaveState SaveState { get; set; } = DocumentSaveState.Unknown;

    /// <summary>Hash of the file re-read after the last confirmed save.</summary>
    public string? SavedFileSha256 { get; set; }

    /// <summary>Size of the same file in bytes.</summary>
    public long? SavedFileByteLength { get; set; }

    /// <summary>Write time of the same file by the machine clock (UTC).</summary>
    public DateTime? SavedAtUtc { get; set; }

    public string LastRevisionReason { get; set; } = "create";

    /// <summary>False when the kind had to be assumed rather than read from the document.</summary>
    public bool KindVerified { get; init; }
}

public sealed record SaveDocumentResult(string? Path, long ByteLength, string Sha256, DocumentContextDto Context, long Revision);

/// <summary>Struct-type selectors for <c>KompasObject.GetParamStruct(Int16)</c>, from
/// <c>StructType2DEnum</c> (<c>Interop.Kompas6Constants.dll</c>). The member names are the vendor's;
/// the values are read from the shipped assembly, not guessed — a wrong number returns a different
/// parameter block or nothing.</summary>
public static class KompasStructTypes
{
    /// <summary><c>StructType2DEnum.ko_DocumentParam</c> — the <c>ksDocumentParam</c> block that
    /// <c>ksDocument2D.ksCreateDocument</c> consumes: MEASURED 35.</summary>
    public const short DocumentParam = 35;
}

/// <summary>Document type selectors for <c>ksDocumentParam.type</c>, from the <c>DocType</c> help
/// enumeration (<c>doctype.html</c>). They are NOT the same numbers as <c>DocumentTypeEnum</c>:
/// <c>lt_DocSheetStandart</c> = 1 where <c>DocumentTypeEnum.ksDocumentDrawing</c> is also 1, but the
/// neighbour values diverge (fragment is 3 here, 2 there), so the two must not be interchanged.</summary>
public static class KompasDocTypes
{
    /// <summary><c>DocType.lt_DocSheetStandart</c> — a drawing on a standard-format sheet.</summary>
    public const short DrawingSheetStandard = 1;
}

/// <summary>Entity type numbers, taken from the vendor enum instead of the reference scripts.</summary>
public static class KompasObjectTypes
{
    public const int PlaneXoy = 1;
    public const int PlaneXoz = 2;
    public const int PlaneYoz = 3;
    public const int Sketch = 5;
    public const int Edge = 7;
    public const int Face = 6;
    public const int PlaneOffset = 14;
    public const int BaseExtrusion = 24;
    public const int BossExtrusion = 25;
    public const int CutExtrusion = 26;

    /// <summary><c>o3d_baseRotated</c> — the factory number a rotation is CREATED under
    /// (<c>IModelContainer.Rotateds.Add</c>): MEASURED 27.</summary>
    /// <remarks>27/28/29 are FACTORY numbers, not the tree number: a finished rotation lies under
    /// <see cref="Rotated3D"/> = 29 — the same two-numbering trap as a hole.
    /// History: docs/decisions/adapter-core.md#rotated3d-numbering</remarks>
    public const int BaseRotated = 27;

    /// <summary><c>o3d_bossRotated</c> — boss by rotation, factory number (MEASURED R.24: 28).</summary>
    public const int BossRotated = 28;

    /// <summary><c>o3d_cutRotated</c> — cut by rotation, factory number (MEASURED R.24: 29).</summary>
    public const int CutRotated = 29;

    /// <summary><c>o3d_Rotated3D</c> — the number under which a finished rotation feature lies in the API5 tree.</summary>
    /// <remarks>MEASURED: for a rotation the FACTORY number and the tree number COINCIDE (29 =
    /// <c>o3d_cutRotated</c>), unlike a hole — the value is measured, not derived.
    /// History: docs/decisions/adapter-core.md#rotated3d-numbering</remarks>
    public const int Rotated3D = 29;
    public const int BaseLoft = 30;
    public const int BossLoft = 31;
    public const int CutLoft = 32;
    public const int Fillet = 34;
    public const int Chamfer = 33;
    public const int BaseEvolution = 45;
    /// <summary><c>o3d_holeOperation</c> — the number a hole feature is CREATED under via
    /// <c>ksPart.NewEntity</c>. This is NOT the number under which it appears in the tree after
    /// creation.</summary>
    public const int HoleOperation = 52;

    /// <summary><c>o3d_Hole3D</c> — the number under which a finished hole feature lies in the API5 tree.</summary>
    /// <remarks>MEASURED, fixing a real defect: searching by <see cref="HoleOperation"/> = 52 never found the
    /// feature created by the API7 route, so no <c>feature_ref</c> was issued and editing was unreachable.
    /// Two numbering systems that must not be confused.
    /// History: docs/decisions/adapter-core.md#hole3d-numbering</remarks>
    public const int Hole3D = 583;

    /// <summary>The type a BOOLEAN OPERATION feature is seen under in the API5 tree
    /// (<c>o3d_aggregate</c>): MEASURED 69.</summary>
    /// <remarks>After <c>kompas_boolean</c> exactly one tree entry appears, <c>type=69</c>; the same number
    /// as <c>ksObj3dTypeEnum.o3d_aggregate</c>.
    /// History: docs/decisions/adapter-core.md#object-type-numbers</remarks>
    public const int BooleanOperation = 69;

    /// <summary>The split feature type in the API5 tree (<c>o3d_SplitSolid</c>): MEASURED 633.</summary>
    /// <remarks>After <c>kompas_split</c> exactly one new tree entry appears, <c>type=633</c>.
    /// History: docs/decisions/adapter-core.md#object-type-numbers</remarks>
    public const int SplitSolid = 633;

    /// <summary>The cut-by-plane feature type in the API5 tree (<c>o3d_cutByPlane</c>): MEASURED 50.</summary>
    /// <remarks>After <c>kompas_cut_by_plane</c> exactly one new tree entry appears, <c>type=50</c>.
    /// History: docs/decisions/adapter-core.md#object-type-numbers</remarks>
    public const int CutByPlane = 50;

    /// <summary>The reposition feature type in the API5 tree: MEASURED <b>79</b>.</summary>
    /// <remarks>After <c>kompas_reposition</c> exactly one new tree entry appears, <c>type=79</c>.
    /// <b>79 is NOT 569</b> (<c>o3d_BodyReposition</c> creates the object) — the same two-numbering trap
    /// as a hole. The same number 79 is also carried by the auxiliary "Body copy" feature
    /// (<c>keep_tools=true</c>); the collision is harmless only because the filter is applied WITHIN one
    /// operation. Do not rely on "79 means reposition" outside the operation's context.
    /// History: docs/decisions/adapter-core.md#body-reposition-numbering</remarks>
    public const int BodyRepositionFeature = 79;

    public const int Polyline3d = 53;
    public const int Part = 104;
    public const int Body = 115;
    public const int FaceCollection = 116;
    public const int FeatureCollection = 119;
    public const int EdgeCollection = 121;
    public const int OperationElement = 110;
    public const int CurveElement = 111;
    public const int ConstructionElement = 109;

    public static short Of(int value) => checked((short)value);
}
