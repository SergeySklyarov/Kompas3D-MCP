using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Domain.Documents;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.Paths;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// One КОМПАС instance the Worker owns or is attached to, plus the documents registered against
/// it. Every method here must be called on the Worker's single STA thread; nothing in this class
/// is thread-safe by design, because the COM objects behind it are not.
/// </summary>
/// <remarks>
/// Identity rules that the contract depends on:
/// <list type="bullet">
/// <item>A document is addressed by server UUID, never by "the active tab". <c>Document3D()</c> is
/// a factory (proved in P0.5: two calls give different IUnknowns), so there is no ambient document
/// to misuse.</item>
/// <item>Revisions are server-side counters bumped on mutation, rebuild, reload and restore.
/// External edits made in the КОМПАС UI cannot be trusted to raise events here, so a cheap
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

    /// <summary>
    /// Attach to an existing instance or launch a new one, and prove which process the returned
    /// object belongs to (spec 1.6). A launch that cannot be attributed to exactly one new PID is
    /// refused rather than adopted.
    /// </summary>
    public ApplicationEntry Connect(ConnectCommand command)
    {
        if (command.Mode == ConnectMode.Launch)
        {
            return Launch(command);
        }

        return Attach(command);
    }

    /// <summary>
    /// Применяет запрошенную видимость и записывает режим документов по НАБЛЮДЁННОМУ результату.
    /// </summary>
    /// <remarks>
    /// Семантика различается по происхождению экземпляра, и это согласовано с требованием не
    /// трогать чужое окно:
    /// <list type="bullet">
    /// <item><c>launch</c> — экземпляр наш, поэтому применяется любое из двух значений:
    /// make_visible=true показывает, false явно прячет (так же, как это делал P0.4b).</item>
    /// <item><c>attach</c> — экземпляр пользовательский. true показывает его; false — заявка
    /// «невидимо», к чужому окну не применяемая: сервер не скрывает уже видимое окно. Документы
    /// тогда наследуют фактическую видимость приложения, а не «скрыто по умолчанию»: открывать
    /// файл в невидимом окне пользовательского КОМПАСа означало бы прятать результат его работы.</item>
    /// </list>
    /// </remarks>
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
        // Scoped to the ProgID this adapter can actually drive: one running КОМПАС also registers
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
            // Report the unfiltered ROT total too: "no КОМПАС entries" and "the enumerator returned
            // nothing at all" are different diagnoses — the first is a property of КОМПАС, the
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

    /// <summary>
    /// PID behind the application's main window. Returns null while the instance is headless,
    /// which is exactly when the process-diff route has to carry the attribution.
    /// </summary>
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

        // Create(invisible, isDetail): первый аргумент — именно невидимость (P0.4b). Ранее здесь
        // стоял неизменный true, поэтому документ не мог появиться на экране даже после показа
        // приложения. Теперь режим берётся от экземпляра: скрытый сеанс остаётся скрытым.
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
            // Nothing to enforce here beyond reporting; КОМПАС itself refuses the write later.
        }

        var part = (ksPart)document.GetPart(-1);
        return RegisterDocument(document, part, application, kind, command.Path, kindVerified: isDetail is not null);
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

        // Документ может быть создан «невидимо» и всё равно оказаться на экране (и наоборот),
        // поэтому состояние перечитывается у него самого, а показ применяется только когда
        // экземпляр работает в видимом режиме.
        var (activated, _) = PresentDocument(application.Application, document, application.DocumentsVisible);
        entry.ActiveReported = activated;
        entry.Visible = application.DocumentsVisible ? ObserveDocumentVisible(document) : false;

        entry.Fingerprint = ComputeFingerprint(entry);

        // Открытие файла: КОМПАС прочитал его с диска, поэтому модель совпадает с файлом.
        // Создание: документ существует только в памяти и ни разу не записан — это изменение,
        // которое закрытие обязано заметить (иначе «refuse» на новом документе пропускал бы
        // потерю всей построенной модели).
        entry.SaveState = entry.Path is null
            ? DocumentSaveTracking.AfterCreate()
            : DocumentSaveTracking.AfterOpen();

        _documents[entry.Id] = entry;
        return entry;
    }

    /// <summary>Document that owns a minted reference, without resolving or validating it.</summary>
    public string? OwningDocumentId(string referenceId) =>
        References.TryGet(referenceId, out var stored) && stored is not null ? stored.DocumentId : null;

    /// <summary>
    /// Document owning a reference, for commands addressed by reference rather than by document id.
    /// An unknown handle is the same STALE_REFERENCE the resolution path would report, so a caller
    /// cannot learn the owner of a handle that is already dead.
    /// </summary>
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

    /// <summary>
    /// File name КОМПАС reports for a component, or null. It is read defensively because an
    /// unsaved document reports an empty string and some interop versions expose the member as a
    /// property rather than a getter.
    /// </summary>
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
            // Видимость документа — отдельный ответ, не производная от видимости приложения:
            // приложение можно показать, а документ оставить в невидимом режиме.
            DocumentVisible = document.Visible,
            DocumentsVisibleMode = document.DocumentsVisible,
            DocumentActiveReported = document.ActiveReported,
            // КОМПАС events are not wired in this build, so an edit made by hand in the UI can
            // only be caught by the pre-mutation fingerprint check. Saying so beats implying more.
            ExternalChangeDetection = ExternalChangeDetection.Conservative,
        };
    }

    public bool IsDirty(DocumentEntry document) => DocumentSaveTracking.IsDirty(SaveStateOf(document));

    /// <summary>
    /// Состояние сохранённости документа: на диске лежит ли именно та модель, которую держит
    /// КОМПАС. Ведётся сервером, потому что документированного признака «документ изменён» у
    /// целевой версии нет (см. <see cref="DocumentSaveTracking"/>).
    /// </summary>
    /// <remarks>
    /// Отпечаток здесь отвечает ровно на один вопрос — менял ли модель кто-то помимо нас: правка
    /// в UI не проходит через <see cref="BumpRevision"/>, и заметить её больше нечем. Нечитаемый
    /// отпечаток неизменность НЕ доказывает и даёт «неизвестно», а не «чисто»: прежде
    /// отражённый <c>IsSaved</c> и сравнение отпечатков вместе выдавали
    /// <c>dirty=false</c> сразу после мутации, то есть обе защиты закрытия обходились.
    /// </remarks>
    public DocumentSaveState SaveStateOf(DocumentEntry document)
    {
        var observed = ComputeFingerprint(document);
        if (observed == DocumentSaveTracking.UnreadableFingerprint)
        {
            return DocumentSaveTracking.AfterUnreadableObservation();
        }

        if (document.Fingerprint is not null && observed != document.Fingerprint)
        {
            // Базовую линию не сдвигаем: пока документ не сохранён или не изменён через MCP,
            // замеченное расхождение остаётся замеченным, а не «забытым» после первого чтения.
            return DocumentSaveTracking.AfterExternalChange();
        }

        return document.SaveState;
    }

    /// <summary>
    /// Cheap state digest used because КОМПАС change events are not subscribed in this build.
    /// It is deliberately coarse: its job is to catch "the user changed the model", not to
    /// identify which feature moved. It is NOT evidence that the file on disk is current — that
    /// question is answered by <see cref="DocumentSaveState"/>, and conflating the two is exactly
    /// the defect this pair replaced.
    /// </summary>
    public string ComputeFingerprint(DocumentEntry document)
    {
        try
        {
            var bodies = CountBodies(document);
            if (bodies < 0)
            {
                // Коллекция тел не ответила. Отпечаток «0:0:…» читался бы как пустой документ и
                // превратил бы отказ чтения в наблюдённое расхождение — то есть в выдуманную
                // внешнюю правку. Отказ чтения называется отказом чтения.
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

    public int CountComponents(DocumentEntry document)
    {
        try
        {
            var collection = (ksEntityCollection)document.PartNow().EntityCollection(KompasObjectTypes.Of(KompasObjectTypes.Part));
            return collection.GetCount();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Raise the revision and move the document's references with it.
    /// </summary>
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

        // Единственная точка, через которую проходят все мутации, — поэтому перерисовка не
        // зависит от того, какая операция меняла модель, и не заводится в каждом вызове отдельно.
        // В скрытом режиме она не делает ничего (см. RefreshViewAfterMutation).
        RefreshViewAfterMutation(document);

        if (invalidateAll)
        {
            // Analytic profile areas belong to sketch handles; once those handles stop resolving,
            // a stale area must not be reused as the expected volume of a later feature. The same
            // reasoning covers what a target-body check reads: the drawn profile's extent and the
            // plane it was drawn on.
            foreach (var orphan in _profileAreaMm2.Keys.Where(key => !References.TryGet(key, out _)).ToArray())
            {
                _profileAreaMm2.Remove(orphan);
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

        // Обновление ревизии сохранённости НЕ подтверждает: единственное место, где документ
        // объявляется сохранённым, — подтверждённая запись в файл (SaveDocument). Прежняя
        // редакция писала сюда же отпечаток и тем самым после каждой мутации делала документ
        // «неизменённым», обходя и отказ, и сохранение при закрытии.
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
                // Отказ сохранения бросает исключение — и тогда документ остаётся открытым и
                // по-прежнему изменённым, а не «закрытым с потерей правки».
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
            // Имя файла документу не задано: Save() по документации пишет «в файл с заданным
            // ранее именем», и вызывать его без имени значит получить либо запрос имени, либо
            // false. Новый документ без пути поэтому отказывает явно, а не «сохраняется» молча.
            throw new KompasContractException(
                ErrorCodes.SaveFailed,
                "Сохранить документ без имени нельзя: путь не задан ни документом, ни вызовом.",
                RetryPolicy.SameOperationId,
                details: new Dictionary<string, object?> { ["document_id"] = document.Id });
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
            // Отказ сохранения состояние не очищает: документ остаётся изменённым.
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
        BumpRevision(document, "save");

        // Единственное место, где сохранённость объявляется подтверждённой: операция вернула
        // успех И файл перечитан с диска. Сбрасывать признак внутри CAD ради зелёного прогона
        // здесь нечем и не нужно — читается именно файл.
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

    /// <summary>
    /// Перечитать сохранённый файл: существование, размер, хеш и время записи. Это и есть
    /// «последующее чтение», которым подтверждается сохранность, — в отличие от смены признака
    /// внутри CAD, которое ничего о файле не говорит.
    /// </summary>
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
    /// <summary>
    /// Режим, в котором этому экземпляру следует создавать и открывать документы. Выведен из
    /// наблюдённой видимости приложения после применения make_visible — а не из того, что
    /// попросили: показать приложение не значит показать уже открытые документы, и прятать
    /// пользовательское окно при attach с make_visible=false тоже не входило в намерение.
    /// </summary>
    public bool DocumentsVisible { get; set; }

    /// <summary>Последнее наблюдение окна: и COM-свойство, и ответ Windows, и заголовки окон документов.</summary>
    public Api5Session.WindowObservation? Window { get; set; }

    public Api5Session.WindowObservation Observe() =>
        (Window = Api5Session.ObserveApplicationWindow(Application));

    public ApplicationInfoDto ToDto(int openDocuments)
    {
        // Наблюдение берётся заново: состояние окна может измениться вне сеанса (пользователь
        // свернул или закрыл окно), а ответ обязан отражать фактическое положение дел.
        var observed = Observe();
        return new ApplicationInfoDto
        {
            ApplicationId = Id,
            ProcessId = ProcessId,
            Version = Version,
            Ownership = Ownership,
            ConnectedAs = ConnectedAs,
            ExecutablePath = KompasInteropResolver.LocalServerPath(Api5Session.KompasProgId),

            // Было: Visible = ProcessIdOf(Application) is not null — то есть «по HWND достаётся
            // PID». Скрытое окно HWND имеет, поэтому поле было ложноположительным по построению.
            // Стало: видно тогда, когда и COM-свойство, и Windows согласны.
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

    public ksDocument3D Document { get; }

    public ksPart Part { get; }

    /// <summary>
    /// Режим видимости, в котором этот документ был создан или открыт: наследуется от экземпляра
    /// приложения. Отдельно от <see cref="Visible"/>: то, что мы попросили, и то, что документ о
    /// себе сообщает, — разные величины, и смешение этих двух как раз и дало ложный visible=true.
    /// </summary>
    public bool DocumentsVisible { get; set; }

    /// <summary>Перечитанное состояние документа (<c>!ksDocument3D.invisibleMode</c>), null — не удалось.</summary>
    public bool? Visible { get; set; }

    /// <summary>Что ответил <c>SetActive()</c>: показ документа отдельно от показа приложения.</summary>
    public bool? ActiveReported { get; set; }

    /// <summary>
    /// Current root part of the document.
    /// </summary>
    /// <remarks>
    /// The handle captured at creation goes stale once a feature is created: measured on v24, a
    /// cached <c>ksPart</c> started returning an empty <c>BodyCollection</c> and a null
    /// <c>GetMainBody()</c> for a document that demonstrably had a solid body and saved it to disk.
    /// Re-acquiring from the document per operation is therefore not a micro-optimisation to skip —
    /// it is what makes reads agree with what КОМПАС actually holds. <see cref="Part"/> is kept for
    /// identity checks and release bookkeeping only.
    /// </remarks>
    public ksPart PartNow() => (ksPart)Document.GetPart(-1);

    public long Revision { get; set; }

    /// <summary>
    /// Отпечаток последнего НАБЛЮДЕНИЯ модели: им ловится правка, сделанная помимо MCP. Он не
    /// является признаком сохранённости — этим занят <see cref="SaveState"/>.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>
    /// Состояние сохранённости: подтверждено ли записью в файл, что на диске лежит текущая
    /// модель. Ведётся сервером, потому что документированного признака «документ изменён» у
    /// целевой версии нет.
    /// </summary>
    public DocumentSaveState SaveState { get; set; } = DocumentSaveState.Unknown;

    /// <summary>Хеш файла, перечитанного после последнего подтверждённого сохранения.</summary>
    public string? SavedFileSha256 { get; set; }

    /// <summary>Размер того же файла в байтах.</summary>
    public long? SavedFileByteLength { get; set; }

    /// <summary>Время записи того же файла по часам машины (UTC).</summary>
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

    /// <summary>
    /// <c>o3d_baseRotated</c> — номер, под которым вращение СОЗДАЁТСЯ через фабрику
    /// <c>IModelContainer.Rotateds.Add</c> (измерено R.24: <c>(int)ksObj3dTypeEnum.o3d_baseRotated = 27</c>).
    /// </summary>
    /// <remarks>
    /// Как и у отверстия, здесь две разные системы нумерации, и путать их нельзя. 27/28/29 — номера
    /// ФАБРИКИ (аргумент <c>Add</c>); в дереве API5 созданный признак виден под собственным номером
    /// <see cref="Rotated3D"/> = 584. Поиск признака по 27/28/29 не нашёл бы его никогда, и это был бы
    /// тот же дефект, что и поиск отверстия по 52 — исправленный ровно так же.
    /// </remarks>
    public const int BaseRotated = 27;

    /// <summary><c>o3d_bossRotated</c> — приклейка вращением, номер фабрики (измерено R.24: 28).</summary>
    public const int BossRotated = 28;

    /// <summary><c>o3d_cutRotated</c> — вырезание вращением, номер фабрики (измерено R.24: 29).</summary>
    public const int CutRotated = 29;

    /// <summary>
    /// <c>o3d_Rotated3D</c> — номер, под которым готовый признак вращения лежит в дереве API5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ИЗМЕРЕНО 17.09.2026 приёмкой SM-03 (строка RO.10t), и измерение опровергло ожидание.</b>
    /// Ожидалось, что дерево, как у отверстия, отстаёт от фабрики на 531 (52→583), и вращение
    /// покажется под 584. Строка RO.10t напечатала типы дерева после разреза вращением:
    /// <c>признаков=2 типы=['25', '29']</c> — то есть базовая пластина видна под 25
    /// (<c>o3d_bossExtrusion</c>) и разрез вращением — под <b>29</b>.
    /// </para>
    /// <para>
    /// Значит, у вращения ФАБРИЧНЫЙ номер и номер дерева СОВПАДАЮТ (29 = <c>o3d_cutRotated</c>),
    /// в отличие от отверстия. Вывод не «номер такой же», а «две системы нумерации ведут себя
    /// по-разному в разных семействах, и предполагать по аналогии нельзя» — это и есть причина,
    /// по которой значение здесь стоит измеренное, а не выведенное.
    /// </para>
    /// </remarks>
    public const int Rotated3D = 29;
    public const int BaseLoft = 30;
    public const int BossLoft = 31;
    public const int CutLoft = 32;
    public const int Fillet = 34;
    public const int Chamfer = 33;
    public const int BaseEvolution = 45;
    /// <summary>
    /// <c>o3d_holeOperation</c> — номер, под которым признак отверстия СОЗДАЁТСЯ через
    /// <c>ksPart.NewEntity</c>. Это НЕ номер, под которым он виден в дереве после создания.
    /// </summary>
    public const int HoleOperation = 52;

    /// <summary>
    /// <c>o3d_Hole3D</c> — номер, под которым готовый признак отверстия лежит в дереве API5.
    /// </summary>
    /// <remarks>
    /// Измерено пробой N.1 от 17.09.2026, и это исправление реального дефекта: адаптер искал
    /// признак по <see cref="HoleOperation"/> = 52 и потому не находил его НИКОГДА, когда отверстие
    /// создавалось маршрутом API7, — <c>feature_ref</c> не выдавался, и правка была недостижима.
    /// Проба напечатала обе коллекции дерева до и после создания:
    /// <c>NewEntity(52).type = 52 (o3d_holeOperation)</c>, а
    /// <c>IHoles3D[0].ModelObjectType = 583 (o3d_Hole3D)</c>, и в дереве появилась ровно одна
    /// запись — <c>OperationElement(110)[1] type=583 («Отверстие:1»)</c>. 52 в дереве не появилось
    /// ни разу. Две разные системы нумерации, и путать их нельзя.
    /// </remarks>
    public const int Hole3D = 583;

    /// <summary>
    /// Тип, под которым признак БУЛЕВОЙ ОПЕРАЦИИ виден в дереве API5 (<c>o3d_aggregate</c>).
    /// </summary>
    /// <remarks>
    /// Измерено 18.09.2026 прибором <c>scratch/b3-measure-feature-types.py</c> через
    /// <c>kompas_list_features</c> (там же напечатано <c>entity.type</c>): после
    /// <c>kompas_boolean</c> в дереве появляется ровно одна запись —
    /// <c>type=69 «Булева операция:1»</c>. Тот же номер, что и у <c>ksObj3dTypeEnum.o3d_aggregate</c>.
    /// </remarks>
    public const int BooleanOperation = 69;

    /// <summary>Тип признака разделения в дереве API5 (<c>o3d_SplitSolid</c>): измерено 633.</summary>
    /// <remarks>
    /// После <c>kompas_split</c> в дереве ровно одна новая запись — <c>type=633 «Разрезать:1»</c>.
    /// </remarks>
    public const int SplitSolid = 633;

    /// <summary>Тип признака отсечения плоскостью в дереве API5 (<c>o3d_cutByPlane</c>): измерено 50.</summary>
    /// <remarks>
    /// После <c>kompas_cut_by_plane</c> в дереве ровно одна новая запись — <c>type=50 «Сечение:1»</c>.
    /// </remarks>
    public const int CutByPlane = 50;

    /// <summary>
    /// Тип признака изменения положения в дереве API5: измерено <b>79</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// После <c>kompas_reposition</c> в дереве ровно одна новая запись —
    /// <c>type=79 «Изменение положения : Тело 1»</c>.
    /// </para>
    /// <para>
    /// <b>79 — это НЕ 569.</b> Число 569 (<c>o3d_BodyReposition</c>) относится к созданию
    /// объекта, а в дереве признак лежит под 79. Ровно тот же урок, что и с отверстием
    /// (<see cref="HoleOperation"/> = 52 против <see cref="Hole3D"/> = 583): сторона создания и
    /// сторона дерева нумеруются по-разному, и брать одно вместо другого нельзя.
    /// </para>
    /// <para>
    /// Тот же номер 79 носит вспомогательный признак «Копия тела», которым реализовано
    /// <c>keep_tools=true</c>. Совпадение номеров безвредно только потому, что фильтр применяется
    /// ВНУТРИ одной операции: у булевой операции ожидаемый тип — 69, и копия (79) в кандидаты не
    /// попадает; у изменения положения ожидаемый тип — 79, и новых записей этого типа ровно одна.
    /// Опираться на «79 — это изменение положения» вне контекста операции нельзя.
    /// </para>
    /// </remarks>
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
