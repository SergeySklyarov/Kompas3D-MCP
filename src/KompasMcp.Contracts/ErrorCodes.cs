namespace KompasMcp.Contracts;

/// <summary>Stable, English, machine-readable error identifiers (spec 2.2). Codes are part of the public contract:
/// never rename, never reuse for a different meaning. Human-readable messages are Russian and live in <see
/// cref="ErrorMessages"/>.</summary>
public static class ErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string CapabilityUnavailable = "CAPABILITY_UNAVAILABLE";
    public const string KompasNotInstalled = "KOMPAS_NOT_INSTALLED";
    public const string ComRegistrationError = "COM_REGISTRATION_ERROR";
    public const string BitnessMismatch = "BITNESS_MISMATCH";
    public const string LicenseUnavailable = "LICENSE_UNAVAILABLE";
    public const string AmbiguousApplication = "AMBIGUOUS_APPLICATION";
    public const string ApplicationDisconnected = "APPLICATION_DISCONNECTED";
    public const string WrongDocumentKind = "WRONG_DOCUMENT_KIND";
    public const string DocumentNotFound = "DOCUMENT_NOT_FOUND";
    public const string DocumentDirty = "DOCUMENT_DIRTY";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string StaleReference = "STALE_REFERENCE";
    public const string AmbiguousSelection = "AMBIGUOUS_SELECTION";
    public const string NoBody = "NO_BODY";
    public const string GeometryFailed = "GEOMETRY_FAILED";
    public const string NoGeometryChange = "NO_GEOMETRY_CHANGE";
    public const string IntersectionUnknown = "INTERSECTION_UNKNOWN";
    public const string ComBusy = "COM_BUSY";
    public const string WorkerUnresponsive = "WORKER_UNRESPONSIVE";
    public const string OutcomeUnknown = "OUTCOME_UNKNOWN";
    public const string QueueFull = "QUEUE_FULL";
    public const string OperationIdConflict = "OPERATION_ID_CONFLICT";
    public const string PathNotAllowed = "PATH_NOT_ALLOWED";
    public const string FileExists = "FILE_EXISTS";
    public const string SaveFailed = "SAVE_FAILED";
    public const string ExternalReferences = "EXTERNAL_REFERENCES";
    public const string ExportFailed = "EXPORT_FAILED";
    public const string ImportFailed = "IMPORT_FAILED";

    /// <summary>The core refused raster export: <c>SaveAsToRasterFormat</c> returned FALSE or threw. A separate code,
    /// not <see cref="ExportFailed"/>: for a raster the method's refusal is the only observable sign, and it must be
    /// distinguishable from "success with no result".</summary>
    public const string RasterRefused = "RASTER_REFUSED";

    /// <summary>The method reported success but there is NO result: neither a byte array nor a file. A silent "success"
    /// without an image does not exist — absence of bytes is a refusal, not a PASS.</summary>
    public const string RasterEmpty = "RASTER_EMPTY";

    /// <summary>The content does not match the declared format: the file (or byte array) magic is wrong. The core does
    /// NOT validate the format — MEASURED: a value outside the list is accepted and yields a different format, so the
    /// check lives here, not in the core.</summary>
    public const string RasterFormatMismatch = "RASTER_FORMAT_MISMATCH";

    /// <summary>The snapshot exceeded the response-context limits (pixel extent or base64 size). No silent downscaling
    /// is done: a smaller image is a separate request with a lower <c>resolution</c>.</summary>
    public const string RasterLimitExceeded = "RASTER_LIMIT_EXCEEDED";

    /// <summary>The requested projection cannot be applied or confirmed: the document has no collection, no entry of
    /// that type, or the read-back after <c>SetCurrent</c> reported a different type. A separate code, not
    /// <see cref="RasterRefused"/>: here the CORE did not refuse the raster — the VIEW could not be established, and
    /// a snapshot taken anyway would carry a label the kernel never confirmed.</summary>
    /// <remarks>Also returned when the CURRENT projection cannot be read and the caller did not set
    /// <c>keep_view=true</c>: nothing could be put back, and switching anyway would move the user's window on a
    /// promise the tool cannot keep. That refusal is taken BEFORE the view changes, and its `details` carry
    /// <c>keep_view_required_without_previous_view: true</c> with the remedy named.</remarks>
    public const string ViewUnavailable = "VIEW_UNAVAILABLE";

    public const string UnitsUnverified = "UNITS_UNVERIFIED";
    public const string NotConstantThickness = "NOT_CONSTANT_THICKNESS";
    public const string AmbiguousFlatPattern = "AMBIGUOUS_FLAT_PATTERN";
    public const string VerificationFailed = "VERIFICATION_FAILED";
    public const string CancelNotConfirmed = "CANCEL_NOT_CONFIRMED";

    /// <summary>A feature has candidate dependents, but the server cannot name them reliably: neither API5 nor API7 has
    /// a "dependent features" member (MEASURED by reflection over both assemblies). Deletion therefore requires an
    /// explicit caller decision rather than silently taking down the tree.</summary>
    public const string DependentFeatures = "DEPENDENT_FEATURES";

    /// <summary>The operation journal is held by an ACTIVE owner: another process of the same user with the same
    /// <c>journal_path</c> already runs a session (served at least one client request). Two Hosts with one config do
    /// not run concurrently, so the second refuses NAMEDLY rather than crashing silently.</summary>
    public const string SessionOwnerActive = "SESSION_OWNER_ACTIVE";

    /// <summary>The operation journal is unavailable for writing for a reason unrelated to ownership: foreign rights, a
    /// locked file, a disk error. Work without a safety journal never starts silently.</summary>
    public const string JournalUnavailable = "JOURNAL_UNAVAILABLE";

    /// <summary>A CAD call arrived from a Host that does not own the session. The refusal comes BEFORE the journal
    /// write and BEFORE any COM call: no operation started and no document was touched.</summary> <remarks>A separate
    /// code, not <see cref="SessionOwnerActive"/>: that one means "ANOTHER process owns the session and ownership
    /// cannot be taken", this one means "ownership CAN be taken, but explicitly". Different remedies; mixing them would
    /// answer one question with two different instructions.</remarks>
    public const string SessionNotAcquired = "SESSION_NOT_ACQUIRED";

    /// <summary>Session release was refused because work is still in progress: an executing, queued or background
    /// operation exists. An empty queue alone is not enough — operations whose synchronous answer has already completed
    /// are counted too.</summary>
    public const string SessionReleaseBusy = "SESSION_RELEASE_BUSY";

    /// <summary>Release is NOT confirmed: the Worker stop is unconfirmed or the owner-state record was not written.
    /// Under no conditions is this counted as a successful release.</summary>
    public const string SessionReleaseFailed = "SESSION_RELEASE_FAILED";

    /// <summary>Ownership state could not be determined: the owner record is unreadable (or the named lock could not be
    /// acquired). "Not read" does NOT mean "the session is free".</summary>
    public const string OwnershipStateUnknown = "OWNERSHIP_STATE_UNKNOWN";

    /// <summary>Session release was refused because the DOCUMENT STATE IS UNKNOWN: the Worker was lost or restarted,
    /// and its document edits may have remained unsaved in KOMPAS.</summary> <remarks>A separate code, not <see
    /// cref="SessionReleaseFailed"/>: that one means "the inventory could not be obtained", this one means "the
    /// inventory WAS obtained and is empty because documents were lost, not because there were no edits". Here release
    /// is possible if the client explicitly acknowledges the unknown state
    /// (<c>acknowledge_unknown_document_state=true</c>); there it is not.</remarks>
    public const string DocumentStateUnknown = "DOCUMENT_STATE_UNKNOWN";
}

/// <summary>Default Russian wording per error code. Callers may override the message when they have more specific
/// context; the code stays the same.</summary>
public static class ErrorMessages
{
    private static readonly Dictionary<string, string> Default = new(StringComparer.Ordinal)
    {
        [ErrorCodes.InvalidArgument] = "Аргумент не прошёл валидацию; обращение к КОМПАС не выполнялось.",
        [ErrorCodes.CapabilityUnavailable] = "Инструмент или тип операции не поддерживается в текущей сборке сервера.",
        [ErrorCodes.KompasNotInstalled] = "КОМПАС-3D v24 не найден по зарегистрированному пути установки.",
        [ErrorCodes.ComRegistrationError] = "COM-класс КОМПАС не зарегистрирован или зарегистрирован неоднозначно.",
        [ErrorCodes.BitnessMismatch] = "Разрядность процесса сервера не совпадает с разрядностью КОМПАС.",
        [ErrorCodes.LicenseUnavailable] = "Лицензия КОМПАС недоступна; операция не может быть выполнена.",
        [ErrorCodes.AmbiguousApplication] = "Найдено несколько экземпляров КОМПАС; нужен явный выбор.",
        [ErrorCodes.ApplicationDisconnected] = "Связь с выбранным экземпляром КОМПАС потеряна.",
        [ErrorCodes.WrongDocumentKind] = "Документ имеет другой тип, чем требует операция.",
        [ErrorCodes.DocumentNotFound] = "Документ не найден среди открытых в этом экземпляре КОМПАС.",
        [ErrorCodes.DocumentDirty] = "Документ содержит несохранённые изменения.",
        [ErrorCodes.RevisionConflict] = "Ревизия документа изменилась после формирования запроса.",
        [ErrorCodes.StaleReference] = "Ссылка на топологию относится к предыдущей ревизии документа.",
        [ErrorCodes.AmbiguousSelection] = "Предикате удовлетворяют несколько элементов; выбор не выполнен.",
        [ErrorCodes.NoBody] = "В документе нет ожидаемого твёрдого тела.",
        [ErrorCodes.GeometryFailed] = "КОМПАС не смог построить геометрию по заданным параметрам.",
        [ErrorCodes.NoGeometryChange] = "Ожидаемое изменение геометрии не подтверждено после операции.",
        [ErrorCodes.IntersectionUnknown] = "Результат проверки пересечения получить не удалось.",
        [ErrorCodes.ComBusy] = "COM-объект занят (RPC_E_SERVERBUSY/CO_E_SERVERCALL_RETRYLATER).",
        [ErrorCodes.WorkerUnresponsive] = "Worker не отвечает; очередь CAD-команд занята или процесс завис.",
        [ErrorCodes.OutcomeUnknown] = "Исход операции не определён; повтор запрещён до согласования.",
        [ErrorCodes.QueueFull] = "Очередь CAD-команд переполнена; сервер применяет backpressure.",
        [ErrorCodes.OperationIdConflict] = "Тот же operation_id уже использован с другими аргументами.",
        [ErrorCodes.PathNotAllowed] = "Путь вне разрешённых корней или содержит недопустимую ссылку.",
        [ErrorCodes.FileExists] = "Файл уже существует; перезапись не запрошена.",
        [ErrorCodes.SaveFailed] = "Сохранение документа не удалось.",
        [ErrorCodes.ExternalReferences] = "Обнаружены внешние ссылки, требующие явного решения.",
        [ErrorCodes.ExportFailed] = "Экспорт не удался.",
        [ErrorCodes.ImportFailed] = "Импорт не удался.",
        [ErrorCodes.RasterRefused] =
            "Ядро отказало в сохранении в растровый формат (SaveAsToRasterFormat вернул false или бросил исключение).",
        [ErrorCodes.RasterEmpty] =
            "Сохранение в растр заявлено успешным, но результата нет: ни массива байт, ни файла.",
        [ErrorCodes.RasterFormatMismatch] =
            "Содержимое не совпадает с запрошенным форматом: магия файла другая.",
        [ErrorCodes.RasterLimitExceeded] =
            "Снимок превысил ограничения ответа (большая сторона 1600 пикселей или 2 МиБ base64). " +
            "Уменьшите resolution — молчаливого ужатия сервер не делает.",
        [ErrorCodes.ViewUnavailable] =
            "Запрошенную проекцию применить или подтвердить не удалось: в документе нет коллекции " +
            "проекций, нет проекции этого типа, либо обратное чтение после SetCurrent назвало другой " +
            "тип, либо текущую проекцию прочитать не удалось, и вернуть её после снимка нечем. "
            + "Снимок не снимается: подписывать картинку неподтверждённым видом нельзя. В "
            + "последнем случае вид окна НЕ изменён, и повтор с keep_view=true означает согласие "
            + "оставить запрошенную проекцию.",
        [ErrorCodes.UnitsUnverified] = "Единицы результата не подтверждены; значение не выдаётся.",
        [ErrorCodes.NotConstantThickness] = "Деталь не является плоской постоянной толщины.",
        [ErrorCodes.AmbiguousFlatPattern] = "Одной проекции недостаточно для однозначного контура.",
        [ErrorCodes.VerificationFailed] = "Заявленная проверка результата не пройдена.",
        [ErrorCodes.CancelNotConfirmed] = "Отмена запрошена, но фактическое прекращение не подтверждено.",
        [ErrorCodes.DependentFeatures] =
            "После удаляемого признака в дереве есть другие: сервер называет их кандидатами " +
            "зависимых и не угадывает. Удаление возможно только с confirm_dependents=true.",
        [ErrorCodes.SessionOwnerActive] =
            "Журнал операций уже принадлежит активному сеансу другого процесса с тем же " +
            "journal_path: два Хоста с одним конфигом не выполняют операции одновременно.",
        [ErrorCodes.JournalUnavailable] =
            "Журнал операций недоступен для записи: работа без журнала безопасности не начинается.",
        [ErrorCodes.SessionNotAcquired] =
            "Этот Хост не владеет сеансом КОМПАС: CAD-вызов отклонён до журнала и до COM. " +
            "Вызовите kompas_acquire_session.",
        [ErrorCodes.SessionReleaseBusy] =
            "Освобождение отклонено: есть незавершённая, очередная или фоновая операция.",
        [ErrorCodes.SessionReleaseFailed] =
            "Освобождение не подтверждено: Worker не остановлен или состояние владельца не записано.",
        [ErrorCodes.OwnershipStateUnknown] =
            "Состояние владения сеансом определить не удалось: запись владельца не читается.",
        [ErrorCodes.DocumentStateUnknown] =
            "Состояние документов сеанса неизвестно: Worker терялся или перезапускался, правки могли " +
            "остаться несохранёнными. Освобождение требует acknowledge_unknown_document_state=true.",
    };

    public static string For(string code) =>
        Default.TryGetValue(code, out var message) ? message : "Неизвестная ошибка.";
}
