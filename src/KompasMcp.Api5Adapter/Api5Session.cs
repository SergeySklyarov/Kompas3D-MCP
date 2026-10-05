using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Documents;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.Paths;

namespace KompasMcp.Api5Adapter;

/// <summary>One KOMPAS instance the Worker owns or is attached to, plus the documents registered against
/// it. Every method here must be called on the Worker's single STA thread; nothing in this class
/// is thread-safe by design, because the COM objects behind it are not.</summary>
/// <remarks>
/// Identity rules that the contract depends on:
/// <list type="bullet">
/// <item>A document is addressed by server UUID, never by "the active tab". <c>Document3D()</c> is
/// a factory (proved in P0.5: two calls give different IUnknowns), so there is no ambient document
/// to misuse.</item>
/// <item>Revisions are server-side counters bumped on mutation, rebuild, reload and restore.
/// External edits made in the KOMPAS UI cannot be trusted to raise events here, so a cheap
/// fingerprint is compared before every mutation and the document is marked
/// <c>conservative</c> — the limitation is reported, not hidden.</item>
/// <item>COM references are held only in this object and released exactly when the document is
/// closed or the session ends (spec 1.6).</item>
/// </remarks>
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
    /// object belongs to (spec 1.6). A launch that cannot be attributed to exactly one new PID is
    /// refused rather than adopted.</summary>
    public ApplicationEntry Connect(ConnectCommand command)
    {
        if (command.Mode == ConnectMode.Launch)
        {
            return Launch(command);
        }

        return Attach(command);
    }

    /// <summary>Applies the requested visibility and records the document mode from the OBSERVED result.</summary>
    /// <remarks>The semantics differ by the instance's origin, in line with the requirement not to touch
    /// another user's window: <c>launch</c> — the instance is ours, so either value applies (make_visible=true
    /// shows, false explicitly hides, as P0.4b did); <c>attach</c> — the instance is the user's. true shows
    /// it; false is a request for "invisible" that is not applied to another's window: the server does not
    /// hide an already visible window. Documents then inherit the application's actual visibility, not
    /// "hidden by default": opening a file in an invisible window of the user's KOMPAS would hide the result
    /// of their work.</remarks>
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
        // Scoped to the ProgID this adapter can actually drive: one running KOMPAS also registers
        // its API7 CLSID, which is not a usable attach target and would look like a second instance.
        //
        // Every matching ROT entry is a candidate, including ones whose COM object failed to bind
        // or whose PID cannot be resolved. Filtering those out before the ambiguity check made a
        // second, unattributable instance vanish from the count, and an implicit attach then
        // "successfully" picked the first one — the silent choice this error code exists to prevent.
        var entries = RunningObjectTable.EnumerateKompasEntries(KompasProgId);
        var candidates = entries
            .Select(e => (Entry: e, Pid: e.Object is null ? null : ProcessIdOf(e.Object)))
            .ToList();

        if (candidates.Count == 0)
        {
            // Report the unfiltered ROT total too: "no KOMPAS entries" and "the enumerator returned
            // nothing at all" are different diagnoses — the first is a property of KOMPAS, the
            // second is a bug of ours. Without this the refusal is unfalsifiable.
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

    /// <summary>PID behind the application's main window. Returns null while the instance is headless,
    /// which is exactly when the process-diff route has to carry the attribution.</summary>
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
            // Killing the process is forbidden; Quit() is the documented shutdown of an instance
            // the server started itself, and only for owned instances (spec 1.6).
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
    /// <remarks>The unsaved flag comes ONLY from <see cref="SaveStateOf"/>, i.e. from the fingerprint and the
    /// state the server itself keeps: the target version has no documented "document modified" property, and
    /// passing off what is visible in the UI as one would be a fabrication (see <see cref="SaveStateOf"/>).
    /// That is why the inventory runs on the CAD lane: the fingerprint has to be read through COM.</remarks>
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

        // API5's ksDocument3D.Create makes only a part or an assembly: there is no third option
        // that produces a drawing or a fragment. Reporting success while handing back an assembly
        // labelled "drawing" is precisely the mutation-stub failure the contract forbids, so the
        // request is refused instead.
        if (command.Kind is DocumentKind.Drawing or DocumentKind.Fragment)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Создание {command.Kind.ToString().ToLowerInvariant()} в этой сборке не реализовано: API5 Document3D.Create даёт только деталь или сборку. " +
                "Инструмент не выдаётся за поддержанный, чтобы не вернуть сборку под видом нужного типа.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = command.Kind.ToString(),
                    ["supported_kinds"] = new[] { "part", "assembly" },
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

        // Attribute assignment is only committed by Update(): proved in P0.6, where name and
        // marking were silently lost across save/reopen without it.
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

        // ACCESS IS REMEMBERED BECAUSE IT DECIDES THE OUTCOME OF A LATER WRITE.
        // A document opened `read_only` must not be saved "in place": the save call carries no path field
        // and the Host has nothing to check — its policy judges the CALL's fields, not the document's path.
        // So `kompas_open_document(path under read_only_roots)` + `kompas_save_document` without target_path
        // wrote straight into a root declared "read-only" (defect H4, review 05.10.2026). The access flag is
        // what closes that write.
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
        ksDocument3D document,
        ksPart part,
        ApplicationEntry application,
        DocumentKind kind,
        string? path,
        bool kindVerified)
    {
        var entry = new DocumentEntry(
            Guid.NewGuid().ToString("N"),
            application.Id,
            kind,
            NormalizePath(path) ?? NormalizePath(SafeFileName(part)),
            document,
            part)
        {
            Revision = 1,
            KindVerified = kindVerified,
            DocumentsVisible = application.DocumentsVisible,
        };

        // A document can be created "invisibly" and still end up on screen (and vice versa), so its state
        // is re-read from the document itself, and presentation is applied only when the instance runs in
        // visible mode.
        var (activated, _) = PresentDocument(application.Application, document, application.DocumentsVisible);
        entry.ActiveReported = activated;
        entry.Visible = application.DocumentsVisible ? ObserveDocumentVisible(document) : false;

        entry.Fingerprint = ComputeFingerprint(entry);

        // Open: KOMPAS read it from disk, so the model matches the file. Create: the document exists only
        // in memory and was never written — a change the close must notice (otherwise "refuse" on a new
        // document would let the loss of the whole built model through).
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
    /// An unknown handle is the same STALE_REFERENCE the resolution path would report, so a caller
    /// cannot learn the owner of a handle that is already dead.</summary>
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

    /// <summary>File name KOMPAS reports for a component, or null. It is read defensively because an
    /// unsaved document reports an empty string and some interop versions expose the member as a
    /// property rather than a getter.</summary>
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
            // Document visibility is a separate answer, not derived from the application's: the application
            // can be shown while the document stays in invisible mode.
            DocumentVisible = document.Visible,
            DocumentsVisibleMode = document.DocumentsVisible,
            DocumentActiveReported = document.ActiveReported,
            // KOMPAS events are not wired in this build, so an edit made by hand in the UI can
            // only be caught by the pre-mutation fingerprint check. Saying so beats implying more.
            ExternalChangeDetection = ExternalChangeDetection.Conservative,
        };
    }

    public bool IsDirty(DocumentEntry document) => DocumentSaveTracking.IsDirty(SaveStateOf(document));

    /// <summary>Document save state: whether the model KOMPAS holds is exactly what is on disk. Kept by the
    /// server because the target version has no documented "document modified" flag (see
    /// <see cref="DocumentSaveTracking"/>).</summary>
    /// <remarks>The fingerprint answers exactly one question — did anyone other than us change the model: a
    /// UI edit does not pass through <see cref="BumpRevision"/> and there is nothing else to notice it. An
    /// unreadable fingerprint does NOT prove immutability and yields "unknown", not "clean": previously the
    /// reflected <c>IsSaved</c> and the fingerprint comparison together reported <c>dirty=false</c> right
    /// after a mutation, defeating both close guards.</remarks>
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
    /// It is deliberately coarse: its job is to catch "the user changed the model", not to
    /// identify which feature moved. It is NOT evidence that the file on disk is current — that
    /// question is answered by <see cref="DocumentSaveState"/>, and conflating the two is exactly
    /// the defect this pair replaced.</summary>
    public string ComputeFingerprint(DocumentEntry document)
    {
        try
        {
            var bodies = CountBodies(document);
            if (bodies < 0)
            {
                // The body collection did not answer. A fingerprint of "0:0:…" would read as an empty
                // document and turn a read failure into an observed discrepancy — i.e. an invented external
                // edit. A read failure is named a read failure.
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
        try
        {
            var bodies = (ksBodyCollection)document.PartNow().BodyCollection();
            // Same refresh rule as ListBodies: without it a collection read right after a rebuild
            // can report stale membership and the count disagrees with the measurement.
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

    /// <summary>Assembly component count — from the STRUCTURE (<c>IPart7.PartsEx</c>), not the API5
    /// collection.</summary>
    /// <remarks>MEASURED 04.10.2026 by a live run, refuting the former route: the API5 collection
    /// <c>EntityCollection(o3d_part = 104)</c> on an assembly with ONE inserted component returned <b>7</b>
    /// — not a component count. The count comes from the API7 structure:
    /// <c>IAssemblyDocument.TopPart</c> → <c>IPart7.PartsEx(ksAllParts)</c>, recursively over subassemblies.
    /// The error was visible only on a live assembly: before it there were no assembly tools.
    /// LIMIT: the walk is bounded by depth — a subassembly cycle (if possible) must not loop the server. The
    /// limit is named as a number, not "reasonable": 64 levels.</remarks>
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
                // unknown node is NOT continued, and that is named. An unread value must not PERMIT the walk.
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
    /// <param name="invalidateAll">
    /// True when the model was re-read or rebuilt rather than edited by this server: then handles
    /// from the previous revision must not resolve. False for an ordinary mutation, where the
    /// sketch or feature just produced is precisely what the next command needs.
    /// </param>
    public void BumpRevision(DocumentEntry document, string reason, bool invalidateAll = false)
    {
        document.Revision++;
        References.RevisionForward(document.Id, document.Revision, invalidateAll);

        // The single point every mutation passes through — so the redraw does not depend on which operation
        // changed the model and is not wired into each call separately. In hidden mode it does nothing (see
        // RefreshViewAfterMutation).
        RefreshViewAfterMutation(document);

        if (invalidateAll)
        {
            // Analytic profile areas belong to sketch handles; once those handles stop resolving,
            // a stale area must not be reused as the expected volume of a later feature. The same
            // reasoning covers what a target-body check reads: the drawn profile's extent and the
            // plane it was drawn on.
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
        // confirmed write to file (SaveDocument). Writing a fingerprint here too would make the document
        // "unchanged" after every mutation, defeating both the refusal and the save on close.
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
                // A failed save throws — the document then stays open and still modified, not "closed with
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
            document.Document.close();
        }
        catch (Exception ex) when (ex is COMException)
        {
            // Already closed by the user; the registry entry is gone regardless.
        }
        finally
        {
            ComApartment.Release(document.PartNow());
            ComApartment.Release(document.Document);
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

        // WRITING TO THE FILE OF A DOCUMENT OPENED READ-ONLY.
        // A call without target_path carries no path field, so the Host's policy does not check it:
        // `kompas_open_document(path, access=read_only)` + `kompas_save_document` wrote straight into the
        // source file — including into a root declared "read-only" (defect H4, review 05.10.2026). Saving
        // "in place" is therefore closed here, while "save as" to a named path stays allowed: the Host judges
        // such a path.
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
        if (targetPath is null)
        {
            ok = document.Document.Save();
        }
        else
        {
            ok = document.Document.SaveAs(path);
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
        document.Document.UpdateDocumentParam();
        // "Save as" to a named path was checked by the Host as writable, so from now on writing to the
        // document's file is allowed: the access flag follows the path rather than staying forever from the
        // open.
        if (targetPath is not null)
        {
            document.Access = DocumentAccess.Edit;
        }

        BumpRevision(document, "save");

        // The only place savedness is declared confirmed: the operation returned success AND the file was
        // re-read from disk. There is no way, and no need, to clear the flag inside CAD for a green run —
        // it is the file that is read.
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
    /// application visibility after make_visible is applied — not from what was requested: showing the
    /// application does not mean showing already open documents, and hiding a user's window on attach with
    /// make_visible=false was not intended either.</summary>
    public bool DocumentsVisible { get; set; }

    /// <summary>The latest window observation: the COM property, the Windows answer, and the document window
    /// titles.</summary>
    public Api5Session.WindowObservation? Window { get; set; }

    public Api5Session.WindowObservation Observe() =>
        (Window = Api5Session.ObserveApplicationWindow(Application));

    public ApplicationInfoDto ToDto(int openDocuments)
    {
        // The observation is taken afresh: the window state can change outside the session (the user
        // minimised or closed it), and the response must reflect the actual state.
        var observed = Observe();
        return new ApplicationInfoDto
        {
            ApplicationId = Id,
            ProcessId = ProcessId,
            Version = Version,
            Ownership = Ownership,
            ConnectedAs = ConnectedAs,
            ExecutablePath = KompasInteropResolver.LocalServerPath(Api5Session.KompasProgId),

            // Was: Visible = ProcessIdOf(Application) is not null — i.e. "a PID is obtained from the HWND".
            // A hidden window has an HWND, so the field was falsely positive by construction. Now: visible
            // when both the COM property and Windows agree.
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
public sealed class DocumentEntry
{
    public DocumentEntry(string id, string applicationId, DocumentKind kind, string? path, ksDocument3D document, ksPart part)
    {
        Id = id;
        ApplicationId = applicationId;
        Kind = kind;
        Path = path;
        Document = document;
        Part = part;
    }

    public string Id { get; }

    public string ApplicationId { get; }

    public DocumentKind Kind { get; internal set; }

    public string? Path { get; internal set; }

    /// <summary>The access mode the document is open in. It decides whether ITS file may be written: a save
    /// call without <c>target_path</c> carries no path field and the Host's policy does not judge it (see the
    /// comment in <c>OpenDocument</c>). Defaults to <see cref="DocumentAccess.Edit"/> — a created document has
    /// no file of its own yet.</summary>
    public DocumentAccess Access { get; set; } = DocumentAccess.Edit;

    public ksDocument3D Document { get; }

    public ksPart Part { get; }

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
    /// <remarks>
    /// The handle captured at creation goes stale once a feature is created: measured on v24, a
    /// cached <c>ksPart</c> started returning an empty <c>BodyCollection</c> and a null
    /// <c>GetMainBody()</c> for a document that demonstrably had a solid body and saved it to disk.
    /// Re-acquiring from the document per operation is therefore not a micro-optimisation to skip —
    /// it is what makes reads agree with what KOMPAS actually holds. <see cref="Part"/> is kept for
    /// identity checks and release bookkeeping only.
    /// </remarks>
    public ksPart PartNow() => (ksPart)Document.GetPart(-1);

    public long Revision { get; set; }

    /// <summary>Fingerprint of the last OBSERVATION of the model: it catches an edit made outside MCP. It is
    /// not a savedness flag — <see cref="SaveState"/> handles that.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Savedness state: whether a write to file confirmed that the current model is on disk. Kept by
    /// the server because the target version has no documented "document modified" flag.</summary>
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

    /// <summary><c>o3d_baseRotated</c> — the number a rotation is CREATED under through the factory
    /// <c>IModelContainer.Rotateds.Add</c> (MEASURED R.24: <c>(int)ksObj3dTypeEnum.o3d_baseRotated = 27</c>).</summary>
    /// <remarks>As with a hole, there are two different numbering systems here and they must not be confused.
    /// 27/28/29 are FACTORY numbers (the <c>Add</c> argument); in the API5 tree the created feature appears
    /// under its own number <see cref="Rotated3D"/> = 29. Searching for the feature by 27/28/29 would never
    /// find it — the same defect as searching for a hole by 52, fixed the same way.</remarks>
    public const int BaseRotated = 27;

    /// <summary><c>o3d_bossRotated</c> — boss by rotation, factory number (MEASURED R.24: 28).</summary>
    public const int BossRotated = 28;

    /// <summary><c>o3d_cutRotated</c> — cut by rotation, factory number (MEASURED R.24: 29).</summary>
    public const int CutRotated = 29;

    /// <summary><c>o3d_Rotated3D</c> — the number under which a finished rotation feature lies in the API5
    /// tree.</summary>
    /// <remarks>MEASURED 17.09.2026 by acceptance SM-03 (row RO.10t), and the measurement refuted the
    /// expectation: the tree was expected to lag the factory by 531 as with a hole (52→583), showing the
    /// rotation under 584. Row RO.10t printed the tree types after a cut by rotation:
    /// <c>features=2 types=['25', '29']</c> — the base plate under 25 (<c>o3d_bossExtrusion</c>) and the cut
    /// by rotation under <b>29</b>. So for a rotation the FACTORY number and the tree number COINCIDE
    /// (29 = <c>o3d_cutRotated</c>), unlike a hole. The conclusion is not "the number is the same" but "the
    /// two numbering systems behave differently across families, and analogy must not be assumed" — which is
    /// why the value here is measured, not derived.</remarks>
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

    /// <summary><c>o3d_Hole3D</c> — the number under which a finished hole feature lies in the API5
    /// tree.</summary>
    /// <remarks>MEASURED by probe N.1 on 17.09.2026, fixing a real defect: the adapter searched for the
    /// feature by <see cref="HoleOperation"/> = 52 and so NEVER found it when the hole was created by the
    /// API7 route — no <c>feature_ref</c> was issued and editing was unreachable. The probe printed both tree
    /// collections before and after creation: <c>NewEntity(52).type = 52 (o3d_holeOperation)</c> while
    /// <c>IHoles3D[0].ModelObjectType = 583 (o3d_Hole3D)</c>, and exactly one entry appeared in the tree —
    /// <c>OperationElement(110)[1] type=583 ("Hole:1")</c>. 52 never appeared in the tree. Two different
    /// numbering systems, and they must not be confused.</remarks>
    public const int Hole3D = 583;

    /// <summary>The type a BOOLEAN OPERATION feature is seen under in the API5 tree
    /// (<c>o3d_aggregate</c>).</summary>
    /// <remarks>MEASURED 18.09.2026 with the instrument <c>scratch/b3-measure-feature-types.py</c> via
    /// <c>kompas_list_features</c> (which also printed <c>entity.type</c>): after <c>kompas_boolean</c>
    /// exactly one entry appears in the tree — <c>type=69 "Boolean operation:1"</c>. The same number as
    /// <c>ksObj3dTypeEnum.o3d_aggregate</c>.</remarks>
    public const int BooleanOperation = 69;

    /// <summary>The split feature type in the API5 tree (<c>o3d_SplitSolid</c>): MEASURED 633.</summary>
    /// <remarks>After <c>kompas_split</c> exactly one new entry appears in the tree —
    /// <c>type=633 "Cut:1"</c>.</remarks>
    public const int SplitSolid = 633;

    /// <summary>The cut-by-plane feature type in the API5 tree (<c>o3d_cutByPlane</c>): MEASURED 50.</summary>
    /// <remarks>After <c>kompas_cut_by_plane</c> exactly one new entry appears in the tree —
    /// <c>type=50 "Section:1"</c>.</remarks>
    public const int CutByPlane = 50;

    /// <summary>The reposition feature type in the API5 tree: MEASURED <b>79</b>.</summary>
    /// <remarks>After <c>kompas_reposition</c> exactly one new entry appears in the tree —
    /// <c>type=79 "Change of position : Body 1"</c>. <b>79 is NOT 569.</b> The number 569
    /// (<c>o3d_BodyReposition</c>) belongs to creating the object, while in the tree the feature lies under
    /// 79. Exactly the same lesson as with a hole (<see cref="HoleOperation"/> = 52 versus
    /// <see cref="Hole3D"/> = 583): the creation side and the tree side are numbered differently, and one
    /// must not be taken for the other. The same number 79 is also carried by the auxiliary feature "Body
    /// copy", which implements <c>keep_tools=true</c>. The collision is harmless only because the filter is
    /// applied WITHIN one operation: for a boolean operation the expected type is 69 and the copy (79) never
    /// becomes a candidate; for a reposition the expected type is 79 and there is exactly one new entry of
    /// that type. One must not rely on "79 means reposition" outside the operation's context.</remarks>
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
