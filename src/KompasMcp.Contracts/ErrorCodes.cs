namespace KompasMcp.Contracts;

/// <summary>
/// Stable, English, machine-readable error identifiers (spec 2.2).
/// Codes are part of the public contract: never rename, never reuse for a different meaning.
/// Human-readable messages are Russian and live in <see cref="ErrorMessages"/>.
/// </summary>
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

    /// <summary>
    /// Ядро отказало в растровом экспорте: <c>SaveAsToRasterFormat</c> вернул FALSE либо бросил.
    /// Отдельный код, а не <see cref="ExportFailed"/>: у растра отказ метода — единственный
    /// наблюдаемый признак, и он обязан быть отличим от «метод ответил успехом, а результата нет».
    /// </summary>
    public const string RasterRefused = "RASTER_REFUSED";

    /// <summary>
    /// Метод ответил успехом, но результата НЕТ: ни массива байт, ни файла. Молчаливого «успеха»
    /// без картинки не бывает — отсутствие байтов это отказ, а не PASS.
    /// </summary>
    public const string RasterEmpty = "RASTER_EMPTY";

    /// <summary>
    /// Содержимое не соответствует заявленному формату: магия файла (или массива байт) не та.
    /// Ядро формат НЕ проверяет — измерено, что значение вне перечня принимается и даёт другой
    /// формат (проба P5), поэтому проверка стоит здесь, а не в ядре.
    /// </summary>
    public const string RasterFormatMismatch = "RASTER_FORMAT_MISMATCH";

    /// <summary>
    /// Снимок превысил ограничения контекста ответа (габарит в пикселях или размер base64).
    /// Молчаливого ужатия не делается: уменьшение — отдельный запрос с меньшим <c>resolution</c>.
    /// </summary>
    public const string RasterLimitExceeded = "RASTER_LIMIT_EXCEEDED";

    public const string UnitsUnverified = "UNITS_UNVERIFIED";
    public const string NotConstantThickness = "NOT_CONSTANT_THICKNESS";
    public const string AmbiguousFlatPattern = "AMBIGUOUS_FLAT_PATTERN";
    public const string VerificationFailed = "VERIFICATION_FAILED";
    public const string CancelNotConfirmed = "CANCEL_NOT_CONFIRMED";

    /// <summary>
    /// У признака есть кандидаты зависимых, а сервер не может назвать их достоверно: ни в API5,
    /// ни в API7 члена «зависимые признаков» нет (измерено рефлексией по обеим сборкам и пробой
    /// L.8). Поэтому удаление требует явного решения вызывающего, а не молча сносит дерево.
    /// </summary>
    public const string DependentFeatures = "DEPENDENT_FEATURES";

    /// <summary>
    /// Журнал операций держит АКТИВНЫЙ владелец: другой процесс этого же пользователя с тем же
    /// <c>journal_path</c> уже ведёт сеанс (обслужил хотя бы один запрос клиента). Два Хоста с одним
    /// конфигом не выполняют операции одновременно, поэтому второй отказывает ИМЕНОВАННО, а не
    /// падает и не работает молча.
    /// </summary>
    public const string SessionOwnerActive = "SESSION_OWNER_ACTIVE";

    /// <summary>
    /// Журнал операций недоступен для записи по причине, не связанной с владельцем: чужие права,
    /// занятый файл, ошибка диска. Работа без журнала безопасности не начинается молча.
    /// </summary>
    public const string JournalUnavailable = "JOURNAL_UNAVAILABLE";
}

/// <summary>
/// Default Russian wording per error code. Callers may override the message when they have
/// more specific context; the code stays the same.
/// </summary>
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
    };

    public static string For(string code) =>
        Default.TryGetValue(code, out var message) ? message : "Неизвестная ошибка.";
}
