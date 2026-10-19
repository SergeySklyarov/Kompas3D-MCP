using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Contracts.Schema;

namespace KompasMcp.Host.Catalog;

/// <summary>One registered tool: its MCP metadata plus where its arguments go.</summary>
public sealed record ToolDefinition(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    ToolBehaviour Behaviour,
    string WorkerCommand)
{
    /// <summary>True when the tool can change the model or the file system and therefore needs
    /// an operation_id and a journal entry.</summary>
    /// <remarks>A session-control tool changes the SERVER's state, not the model, and writes no
    /// operation journal: <see cref="ToolBehaviour.HostLocal"/> exempts it. Otherwise "session release"
    /// would fall into the same class as a model mutation.</remarks>
    public bool IsMutation => !Behaviour.HostLocal && (Behaviour.Destructive || Behaviour.RequiresOperationId);

    /// <summary>Handled by the Host itself, without Worker and without the operation journal.</summary>
    public bool IsHostLocal => Behaviour.HostLocal;
}

/// <summary>Tool behaviour: what it does to the model and what it requires of the client.</summary>
/// <param name="ReplaysOperationId">A Host tool declares <c>operation_id</c> and replays the recorded
/// outcome itself — WITHOUT an operation journal. Separate from <see cref="RequiresOperationId"/>, which
/// would put the tool into <see cref="ToolDefinition.IsMutation"/> and demand a journal record it does
/// not have. Keeps "a non-mutation promises operation_id" checkable by property (rule 3).</param>
public sealed record ToolBehaviour(
    bool ReadOnly,
    bool Destructive,
    bool RequiresOperationId,
    bool RequiresDocument,
    bool RequiresExpectedRevision,
    bool HostLocal = false,
    bool ReplaysOperationId = false);

/// <summary>The tool surface of v1 preview. Every entry here is implemented end to end: an unimplemented
/// tool is not registered, because a mutation that returns success without doing anything is the
/// failure mode the contract explicitly forbids (spec 2.1).</summary>
public static class ToolCatalog
{
    /// <summary>The ONE wording of the declared-expectation rule, appended to every field description
    /// that carries an expectation, so the tools cannot describe the same rule differently.</summary>
    /// <remarks>INVARIANT: a declared expectation that did not hold is a FAILED CHECK with a warning and a
    /// level no higher than structure_checked — NOT a refusal. MEASURED: the model sometimes reads one
    /// operation behind, so refusing here rejected a correct model.
    /// History: docs/decisions/adapter-core.md#declared-expectation-rule</remarks>
    private const string DeclaredExpectationRule =
        " Расхождение с измеренным в допуске проекта НЕ отказ: проверка declared_expectation_not_confirmed идёт с " +
        "заявленным, измеренным, разностью и допуском, в warnings - предупреждение, уровень не выше " +
        "structure_checked (модель могла отстать на операцию). Сравнивать нечем - аспект непроверен.";

    /// <summary>The section-coupling schema — ONE for creation (<c>kompas_loft</c>) and edit
    /// (<c>kompas_update_feature</c>), so the descriptions cannot drift between tools.</summary>
    /// <remarks>MEASURED: <c>ICoupling.SetPoint</c> is documented (<c>icoupling_setpoint.html</c>:
    /// «<c>SetPoint(long Index, double X, double Y, double Z)</c>»), but the given point is PROJECTED
    /// onto the contour and read back in LOCAL sketch coordinates. <c>icoupling_positionoffset.html</c>:
    /// «Величина смещения точки вдоль контура сечения в мм», input «<c>long Index</c> — индекс сечения
    /// в цепочке»; the coupling is set BEFORE the first <c>Update()</c>.
    /// History: docs/decisions/host.md#couplings-schema</remarks>
    public static JsonObject CouplingsSchema { get; } = Sch.Arr(
        Sch.ObjAll(
            "Цепочка соответствия сечений",
            new (string, JsonObject)[]
            {
                ("offsets_mm", Sch.Arr(
                    Sch.Num(
                        "Смещение точки вдоль контура ЭТОГО сечения, мм. Ноль - начало контура; это же " +
                        "значение воспроизводит автоматическое соответствие. Отрицательное значение " +
                        "означает смещение в другую сторону по контуру и отдельно не измерялось, " +
                        "поэтому не запрещается, но и не обещается."),
                    "Точки по сечениям В ПОРЯДКЕ section_refs: ровно одна точка на каждое сечение. " +
                    "Число точек обязано совпасть с числом сечений: цепочка короче набора описывала " +
                    "бы другое соответствие. Несовпадение отвергается до обращения к КОМПАС.",
                    2, 64)),
            },
            "Одна цепочка соответствия: по точке на КАЖДОЕ сечение, в порядке section_refs. " +
            "Несколько цепочек задают несколько соответствий; полное отсутствие цепочек означает " +
            "автоматическое соответствие КОМПАС."),
        "Цепочки соответствия сечений - то, чем операция с заданным соответствием отличается от " +
        "«просто тела по сечениям». Полная замена: заданный список становится всем набором цепочек " +
        "признака. Пустой список означает «без цепочек».",
        // The lower bound is 0, not 1 - a corrected contradiction of the schema's own declaration.
        // The description promised an empty list means "no couplings", while the schema forbade it
        // (`minItems: 1`): MEASURED, `couplings: []` returned INVALID_ARGUMENT "minimum 1, got 0"
        // on $/couplings.
        // History: docs/decisions/host.md#couplings-minitems
        0, 64);

    /// <summary>What the <c>operation_id</c> FIELD says about itself.</summary>
    /// <remarks>MEASURED defect: the published definition (<c>$defs/operation_id</c>) already named UUID v4
    /// and the exact length, but a client that does not expand <c>$ref</c> showed the field as a plain
    /// string, and the first request was refused with INVALID_ARGUMENT only AFTER the call. The rule is
    /// therefore stated at the field as well, so it is visible without resolving anything; the definition
    /// stays the single source and the validator's target.
    /// History: docs/decisions/host.md#operation-id-field</remarks>
    private const string OperationIdField =
        "Идентификатор мутации, UUID v4, 36 символов; правила - в $defs/operation_id.";

    /// <summary>Definitions shared by every tool, so a document id means the same thing everywhere.</summary>
    public static JsonObject SharedDefinitions { get; } = Sch.Props(
        ("operation_id", Sch.Uuid(        "Идентификатор мутации, UUID v4. НОВЫЙ id на каждую новую операцию; тот же id с теми же аргументами " +
        "возвращает уже известный результат и повторно не обращается к КОМПАС; тот же id с другими аргументами - " +
        "OPERATION_ID_CONFLICT.")),
        ("document_id", Sch.Str(        "UUID документа из kompas_create_document/kompas_open_document; «активного документа» нет.","^[0-9a-f]{32}$")),
        ("application_id", Sch.Str("UUID экземпляра КОМПАС из kompas_connect.", "^[0-9a-f]{32}$")),
        ("expected_revision", Sch.Int(        "Ревизия из kompas_get_context. Расхождение - REVISION_CONFLICT без частичных эффектов.")),
        ("timeout_ms", Sch.Int(        "Бюджет наблюдения за операцией. Истечение тайм-аута означает неизвестный исход, а не отмену.", 1000, 600_000)),
        ("cursor", Sch.Str("Непрозрачный указатель страницы, связанный с ревизией.")),
        ("limit", Sch.Int("Размер страницы, по умолчанию 100.", 1, 500, 100)),
        ("overwrite", Sch.Bool("Перезаписывать существующий файл. По умолчанию false (spec 1.12).", false)),
        ("output_path", Sch.Str("Абсолютный путь внутри разрешённого корня экспорта.")),
        ("path", Sch.Str("Абсолютный путь к файлу модели.")),
        ("reference", Sch.Str("Непрозрачная ссылка на элемент топологии, выданная этим сервером (например face:…).")),
        ("vector3", Sch.Vec3("Три конечные координаты в мм.")),
        ("cut_plane", CutPlaneSchema()),
        ("transform", TransformSchema()),
        ("bbox", BboxSchema()),
        ("sketch_entity", SketchEntitySchema()),
        ("selection_predicate", SelectionPredicateSchema()));

    public static IReadOnlyList<ToolDefinition> All { get; } = Build();

    private static IReadOnlyList<ToolDefinition> Build()
    {
        var defs = SharedDefinitions;

        var tools = new List<ToolDefinition>
        {
            ReadOnly("kompas_health", "Состояние сервера",
                "Жив ли Host, в каком состоянии Worker и очередь. К COM не обращается и отвечает, даже когда CAD-канал занят.",
                Sch.Props(("detail", Sch.Enum("minimal — только состояния; diagnostic — добавает статистику COM-фильтра и окружение.", "minimal", "diagnostic"))),
                WorkerCommands.Ping),

            ReadOnly("kompas_capabilities", "Что реально поддержано",
                "Перечень инструментов ЭТОЙ сборки (те же имена, что в tools/list), версия сервера "
                + "(та же, что в serverInfo.version) и среда исполнения: worker_runtime, разрядность, "
                + "ОС, путь к интероп-сборкам, число процессов КОМПАС (running_instances) и число "
                + "записей ROT, из которых выбирает attach (rot_kompas_entries; null с причиной, если "
                + "перечисление не выполнялось). running_instances и rot_kompas_entries — РАЗНЫЕ "
                + "числа и разные вопросы: первый считает процессы ОС, второй — кандидатов attach. "
                + "Ответ одинаков и когда Worker запущен, и когда его нет. Режимы и ограничения "
                + "КАЖДОГО инструмента читаются из его схемы и описания в tools/list и здесь не "
                + "пересказываются: перечень без выдачи ограничений выдавал бы их за проверенные. "
                + "Политика путей видна ЗДЕСЬ, до первой попытки: read_only_roots и writable_roots "
                + "приходят из того же источника, что и отказ PATH_NOT_ALLOWED, поэтому клиент "
                + "выбирает разрешённый путь, не провоцируя пробный отказ.",
                Sch.Props(),
                WorkerCommands.EnvironmentProbe),

            Control("kompas_session_status", "Состояние сеанса",
                "Кто владеет сеансом КОМПАС и можно ли его занять. Различает «сеанс свободен», "
                + "«занят другим Host», «освобождается» и «состояние определить не удалось». "
                + "Владение НЕ берёт, Worker не запускает, к COM не обращается — и отвечает даже "
                + "тогда, когда сеансом владеет другой чат. Ошибка чтения записи владельца НЕ "
                + "выдаётся за свободный сеанс.",
                Sch.Props(),
                HostSession.StatusCommand),

            Control("kompas_acquire_session", "Занять сеанс",
                "Явный захват CAD-сеанса. Обязателен после того, как прежний владелец снял владение "
                + "вызовом kompas_release_session: обычный CAD-вызов владение тогда не берёт. Захват "
                + "создаёт НОВОЕ поколение сеанса и новый Worker: прежние document_id, revision и "
                + "ссылки недействительны, следующий шаг — kompas_connect и kompas_get_context. "
                + "Повторный захват тем же владельцем второго Worker не создаёт и поколение не меняет.",
                Sch.Props(),
                HostSession.AcquireCommand),

            Control("kompas_release_session", "Освободить сеанс",
                "Отдать CAD-сеанс другому чату, не завершая приложение-клиент. По умолчанию отказывает, "
                + "если в сеансе есть несохранённые документы (перечень приходит в ответе: DOCUMENT_DIRTY) "
                + "или незавершённая работа — выполняющаяся, очередная либо фоновая, чей ответ уже ушёл "
                + "(SESSION_RELEASE_BUSY). Сохранение и закрытие делаются отдельными инструментами с "
                + "явными параметрами: сервер не сохраняет и не отказывается от правок молча, "
                + "принудительного освобождения нет. Освобождение останавливает Worker: зарегистрированные "
                + "документы закрываются документированным закрытием «не сохранять», документы с "
                + "несохранёнными изменениями перед этим сохраняются СПАСАТЕЛЬНОЙ КОПИЕЙ в служебный каталог "
                + "(файл клиента не трогается; см. руководство оператора, «Контрольные и спасательные "
                + "копии»), собственный (launched) экземпляр КОМПАС завершается документированным "
                + "KompasObject.Quit(), а если процесс после него всё ещё жив — завершается по своему PID, и "
                + "журнал Worker называет это отдельно. Пользовательский attached КОМПАС остаётся "
                + "запущенным. MCP-транспорт сохраняется: этот Хост остаётся на "
                + "связи и позже может занять сеанс снова. Повторный вызов безопасен и различает "
                + "«уже освобождён» и «освобождён этим запросом». Повтор с тем же operation_id "
                + "возвращает записанный исход и освобождения заново не выполняет; новый operation_id "
                + "начинает освобождение заново. Журнал операций инструмент не пишет: исходы хранятся "
                + "в памяти процесса, поэтому перезапуск Хоста историю повторов не несёт. Отдельный "
                + "отказ DOCUMENT_STATE_UNKNOWN приходит, когда Worker терялся или перезапускался: его "
                + "документы новому Worker не известны, и правки могли остаться несохранёнными. Выход "
                + "один и он явный — acknowledge_unknown_document_state=true.",
                Sch.Props(
                    ("operation_id", Sch.Described(Sch.Nullable(Sch.Ref("#/$defs/operation_id")), OperationIdField)),
                    ("acknowledge_unknown_document_state", Sch.Nullable(Sch.Bool(
                        "Принять на себя неизвестное состояние документов: Worker терялся или "
                        + "перезапускался, и правки его документов могли остаться в КОМПАС "
                        + "несохранёнными. По умолчанию false — освобождение тогда отказывает кодом "
                        + "DOCUMENT_STATE_UNKNOWN. Сервер НЕ проверяет, сохранились ли правки: "
                        + "подтверждение переносит этот риск на клиента.")))),
                HostSession.ReleaseCommand,
                destructive: true,
                replaysOperationId: true),

            Mutation("kompas_connect", "Подключиться к КОМПАС",
                "attach — подключиться к уже запущенному экземпляру (нужен однозначный выбор, иначе AMBIGUOUS_APPLICATION; при НУЛЕ кандидатов код APPLICATION_DISCONNECTED с причиной reason: kompas_process_not_running — процесс не запущен, kompas_process_not_in_rot — запущен, но не зарегистрирован в ROT, rot_empty — перечисление ROT пусто; в details приходят kompas_process_ids, rot_entries_matching_kompas, rot_total_entries и rot_other_entries — СЧЁТЧИК прочих записей ROT; имена чужих документов в отказе не выдаются вовсе); launch — запустить собственный. Обычные инструменты новое приложение не создают. КАНДИДАТЫ attach — экземпляры, зарегистрированные в Running Object Table (ROT) под ProgID КОМПАС, а НЕ все процессы KOMPAS.exe: процесс без записи в ROT кандидатом не является, поэтому running_instances и rot_kompas_entries в kompas_capabilities — разные числа. При нескольких кандидатах нужен явный process_id: выбор «первого попавшегося» запрещён.",
                Sch.Props(
                    ("mode", Sch.Enum("Способ получения экземпляра.", "attach", "launch")),
                    ("process_id", Sch.Nullable(Sch.Int("Явный PID экземпляра КОМПАС для attach."))),
                    ("make_visible", Sch.Bool(
                        "Показать окно КОМПАС. По умолчанию false. Семантика разная для launch и attach: "
                        + "launch — экземпляр наш, поэтому false его явно прячет (как измерялось в P0.4b); "
                        + "attach — экземпляр пользовательский, false НЕ скрывает уже видимое окно, а true "
                        + "его показывает. Документы, создаваемые и открываемые дальше, наследуют "
                        + "фактическую (наблюдённую, а не запрошенную) видимость экземпляра: видимое "
                        + "приложение даёт видимые документы. Поле visible в ответе — наблюдение "
                        + "(свойство Visible приложения И IsWindowVisible главного окна), а не факт "
                        + "вызова: наличие HWND о видимости не говорит ничего.", false))),
                WorkerCommands.Connect,
                requiresDocument: false),

            Mutation("kompas_disconnect", "Отсоединиться",
                "По умолчанию только отсоединяет сервер. close_owned_application закрывает тот экземпляр, "
                + "который сервер запустил сам; пользовательский КОМПАС не завершается никогда. Собственный "
                + "экземпляр завершается так, чтобы не остаться висеть: документы с несохранёнными "
                + "изменениями сначала сохраняются СПАСАТЕЛЬНОЙ КОПИЕЙ в служебный каталог (файл клиента при "
                + "этом не трогается, путь копии приходит в ответе), затем все документы закрываются "
                + "документированным закрытием «не сохранять» (IKompasDocument.Close(kdDoNotSaveChanges)), и "
                + "только потом вызывается KompasObject.Quit(). Принудительное завершение по PID — последняя "
                + "мера: оно применяется, лишь если после Quit() процесс СВОЕГО экземпляра всё ещё жив, и о "
                + "нём сообщает killed_by_pid. Экземпляр, к которому сервер ПОДКЛЮЧИЛСЯ (attach), не "
                + "закрывается и не завершается никогда. Всё завершение ограничено бюджетом времени "
                + "session_end_budget_ms (меньше окна, после которого Хост убивает Worker): копии снимаются, "
                + "пока бюджет позволяет, и документ, которому бюджета не хватило, НАЗВАН причиной в "
                + "rescue_copies, а не пропущен молча; столько же названо полем rescue_skipped_by_budget, "
                + "не закрытые документы — close_skipped_by_budget. Ответ несёт owned_instance_shutdown с "
                + "полями quit_issued, process_exited, process_id, killed_by_pid, rescue_copies, problems, "
                + "session_end_budget_ms, rescue_skipped_by_budget, close_skipped_by_budget и phase_ms "
                + "(миллисекунды по фазам: rescue, close, quit).",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("close_owned_application", Sch.Bool("Штатно завершить собственный (launched) экземпляр.", false))),
                WorkerCommands.Disconnect,
                requiresDocument: false),

            Mutation("kompas_create_document", "Создать документ",
                "Новый несохранённый документ с UUID. Возвращает контекст с revision=1. Создаются "
                + "деталь, сборка и чертёж; фрагмент не создаётся.",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("kind", Sch.Enum("Тип документа: деталь, сборка или чертёж.",
                        "part", "assembly", "drawing")),
                    ("name", Sch.Nullable(Sch.Str("Имя объекта (ksPart.name).", maxLength: 256))),
                    ("marking", Sch.Nullable(Sch.Str("Обозначение.", maxLength: 128)))),
                WorkerCommands.CreateDocument,
                requiresDocument: false),

            Mutation("kompas_open_document", "Открыть документ",
                "Открывает файл из разрешённого корня. Если файл уже открыт, возвращается тот же document_id — второго экземпляра не происходит. "
                + "До обращения к КОМПАС файл проверяется на занятость другим процессом: занятый файл даёт FILE_LOCKED с причиной и, если ОС называет владельца, процессом-владельцем. "
                + "Владелец может быть НЕ назван: поиск идёт с собственным коротким бюджетом, чтобы отказ не опоздал (опоздавший отказ клиент видит как running), и тогда owner_process пуст, а причина названа в owner_process_note. "
                + "Это нужно потому, что на занятый файл КОМПАС показывает модальный диалог и вызов открытия не возвращается, пока диалог не закроют вручную. "
                + "Сессия видит ТОЛЬКО документы того экземпляра КОМПАС, к которому подключена: файл, открытый в другом окне КОМПАС, открывается этим вызовом заново в своём экземпляре.",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("path", Sch.Ref("#/$defs/path")),
                    ("access", Sch.Enum("read_only — без намерения изменять; edit — будет правка.", "read_only", "edit"))),
                WorkerCommands.OpenDocument,
                requiresDocument: false),

            ReadOnly("kompas_list_documents", "Список документов",
                "Документы, зарегистрированные сервером в этом экземпляре. Ничего не сохраняет и не открывает.",
                Sch.Props(("application_id", Sch.Ref("#/$defs/application_id"))),
                WorkerCommands.ListDocuments),

            ReadOnly("kompas_get_context", "Контекст документа",
                "Тип, путь, признак изменения, ревизия, число тел и признаков. feature_count считает "
                + "ТЕ ЖЕ операции дерева (o3d_operationElement = 110), что и kompas_list_features, и "
                + "эскизы в него НЕ входят — их перечисляет kompas_list_sketches. Обязательный первый "
                + "шаг перед любой правкой.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("detail", Sch.Enum(
                        "minimal — только счётчики; full — добавляет габарит и отпечаток состояния. Габарит "
                        + "идёт ДВУМЯ блоками с названной областью: gabarit.full — весь документ-модель "
                        + "(эскизы и вспомогательная геометрия включены), gabarit.bodies — только сплошные "
                        + "тела (сравнивать с list_bodies). Это разные величины: эскиз вне материала "
                        + "увеличивает full и не меняет bodies. Отпечаток состояния (fingerprint) построен "
                        + "по full и потому тоже чувствителен к эскизам.",
                        "minimal", "full"))),
                WorkerCommands.GetContext,
                requiresDocument: true,
                requiresOperationId: false),

            Mutation("kompas_save_document", "Сохранить документ",
                "Save без пути или SaveAs с путём. Возвращает размер и SHA256 файла и обновлённый контекст: успех COM сам по себе не принимается.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("target_path", Sch.Nullable(Sch.Ref("#/$defs/output_path")))),
                WorkerCommands.SaveDocument,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_close_document", "Закрыть документ",
                "По умолчанию отказывается закрыть изменённый документ (DOCUMENT_DIRTY). Сохранение или отказ от изменений задаются явно.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("dirty_policy", Sch.Enum("refuse — отказать; save — сохранить и закрыть; discard — закрыть без сохранения.", "refuse", "save", "discard"))),
                WorkerCommands.CloseDocument,
                requiresDocument: true),

            ReadOnly("kompas_list_features", "Признаки",
                "ОПЕРАЦИИ дерева модели с непрозрачными ссылками, привязанными к ревизии: "
                + "перечисляется коллекция типа o3d_operationElement = 110 — по obj3dtype.html "
                + "«Операции (от o3d_baseExtrusion до o3d_cylindricSpiral)». ЭСКИЗЫ В ЭТОТ ПЕРЕЧЕНЬ "
                + "НЕ ВХОДЯТ: эскиз — отдельный тип дерева (o3d_sketch = 5, «эскиз»), и перечисляет "
                + "его kompas_list_sketches. Число операций в том же смысле даёт feature_count из "
                + "kompas_get_context.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.ListFeatures,
                requiresDocument: true,
                requiresOperationId: false),

            ReadOnly("kompas_list_sketches", "Эскизы",
                "Перечисляет ЭСКИЗЫ детали с непрозрачными ссылками, привязанными к текущей ревизии. "
                + "Маршрут документирован: ksPart.EntityCollection(o3d_sketch = 5) — «При создании "
                + "массив заполняется объектами указанного типа, содержащимися в компоненте» "
                + "(kspart_entitycollection.html); тип «o3d_sketch 5 эскиз» → ksSketchDefinition / "
                + "ISketch (obj3dtype.html). Тот же набор документирован в API7 "
                + "(IModelContainer::Sketchs → ISketchs, isketchs.html), но взят маршрут API5: строка "
                + "обязана нести ТОТ ЖЕ объект, которым адресует эскиз остальной сервер. "
                + "ЭТО НЕ kompas_list_features: там операции дерева (o3d_operationElement = 110), и "
                + "эскизы в тот перечень не входят. Строка несёт ссылку, имя, порядковый номер в "
                + "коллекции (ПОЗИЦИЯ, а не адрес: перестроение её сдвигает), признак создания и имя "
                + "опорной плоскости, прочитанное ksSketchDefinition.GetPlane(). Непрочитанное поле — "
                + "null с причиной в notes, а не ноль и не «нет». "
                + "ССЫЛКА ОДНА НА ОДИН ЭСКИЗ: если сеанс уже держит живую ссылку на тот же эскиз "
                + "(тот же объект COM), возвращается ОНА, а не выпускается вторая; новая выпускается "
                + "только тогда, когда живой ссылки нет. Ссылка живёт до перестроения или "
                + "переоткрытия документа: после них прежняя отвергается STALE_REFERENCE, и её берут "
                + "заново этим инструментом. Ревизию чтение не поднимает. Ответ — ОБЪЕКТ "
                + "{rows, route, notes}: строки, названный маршрут и замечания уровня коллекции. Пустой "
                + "rows означает «эскизов нет», и это отличимо от отказа — пустой массив без маршрута "
                + "не отличался бы от «коллекция не прочитана». "
                + "УДАЛИТЬ эскиз — kompas_delete_feature по ссылке sketch_ref из этой строки.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.ListSketches,
                requiresDocument: true,
                requiresOperationId: false),

            ReadOnly("kompas_list_bodies", "Тела",
                "Тела документа: тип, габарит, число граней и уникальных рёбер. Ссылка на тело — ручка, " +
                "а не идентификатор: каждый вызов перечисляет заново и выдаёт новую строку для того же тела " +
                "(измерено: четыре вызова подряд на неизменённом теле дают четыре разные строки). Сверять тела " +
                "между снимками следует по габариту или объёму, а не по строке ссылки. Прежняя строка при этом " +
                "остаётся рабочей для kompas_measure, пока документ не перестроен: после перестроения, " +
                "перезагрузки или закрытия все ссылки предыдущей ревизии отвергаются с STALE_REFERENCE — " +
                "молчаливого переподбора по геометрии сервер не делает.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.ListBodies,
                requiresDocument: true,
                requiresOperationId: false),

            // ===== assembly domain (order C1, profile assemblies-minimal-v1) =====
            // The route was measured from the help (over the wire) and the shipped wrappers' metadata,
            // and the domain is accepted by a LIVE run: the ASM group passed on the shipped binaries.
            // Capability level is therefore mcp_verified.
            // History: docs/decisions/host.md#assemblies-domain
            ReadOnly("kompas_list_components", "Компоненты сборки",
                "Структура сборки: экземпляры компонентов с именами/марками, признаком «деталь/сборка», "
                + "кратностью, состоянием фиксации/загрузки, числом тел и граней и ссылкой на экземпляр. "
                + "Читается через IAssemblyDocument.TopPart → IPart7.PartsEx; тела и грани — "
                + "ksPart.BodyCollection → ksBody.FaceCollection. Адрес мутаций — номер в плоском "
                + "ksDocument3D.PartCollection(true): вложенные компоненты ЧИТАЮТСЯ, но адреса у них нет, "
                + "и размещение/замена вложенного компонента отвергаются, а не выполняются по догадке. "
                + "Живой прогон: группа ASM проходит на бинарях поставки.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("recursive", Sch.Nullable(Sch.Bool("Обходить вложенные подсборки.", false)))),
                WorkerCommands.ListComponents,
                requiresDocument: true,
                requiresOperationId: false),

            Mutation("kompas_insert_component", "Вставить компонент",
                "Вставляет компонент из файла-источника в сборку документированным "
                + "IPart7.Parts → IParts7.AddFromFile. Ровно один экземпляр на вызов; повтор с тем же "
                + "operation_id второго не создаёт. Компонент вставляется С ГЕОМЕТРИЕЙ (1 тело, 6 граней). "
                + "Заданное размещение и признак фиксации применяются и ПЕРЕЧИТЫВАЮТСЯ; если "
                + "API5-представление вставленного экземпляра не получено, вызов отказывает с частичными "
                + "эффектами, а не молча пропускает параметры.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("source_path", Sch.Str("Абсолютный путь к файлу детали внутри разрешённого корня.")),
                    ("transform", Sch.Nullable(Sch.Ref("#/$defs/transform"))),
                    ("fixed", Sch.Nullable(Sch.Bool("Зафиксировать компонент после вставки.", true)))),
                WorkerCommands.InsertComponent,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_set_component_placement", "Задать размещение компонента",
                "Задаёт размещение компонента жёстким преобразованием (начало и две оси) и перечитывает "
                + "его. Порядок элементов матрицы 4×4 измерен различающим контролем (поворот на 90° "
                + "вокруг Z): тройки идут не подряд — [X,0][Y,0][Z,0][перенос,1].",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("component_ref", Sch.Str("Ссылка на экземпляр из kompas_list_components.")),
                    ("transform", Sch.Ref("#/$defs/transform"))),
                WorkerCommands.SetComponentPlacement,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_replace_component", "Заменить компонент",
                "Заменяет файл-источник компонента с сохранением размещения документированным "
                + "ksPart.fileName + ksPart.Update; результат проверяется чтением обратно. Кратность "
                + "не должна меняться. Применимо к компонентам, вставленным в сборку.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("component_ref", Sch.Str("Ссылка на экземпляр из kompas_list_components.")),
                    ("source_path", Sch.Str("Абсолютный путь к новому файлу-источнику внутри разрешённого корня."))),
                WorkerCommands.ReplaceComponent,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_check_component_links", "Проверить ссылки компонентов",
                "Проверяет ссылки компонентов на файлы-источники: отсутствующий источник НАЗЫВАЕТСЯ, "
                + "а не выдаётся за исправный. ОСТАЁТСЯ НЕИЗМЕРЕННЫМ: чем именно выглядит битая ссылка "
                + "в этой версии (LoadState, отказ Load или пустой FileName) — вердикт опирается на "
                + "наличие файла по пути.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.CheckComponentLinks,
                requiresDocument: true,
                requiresOperationId: false),

            Mutation("kompas_delete_component", "Удалить компонент сборки",
                "Удаляет компонент ВЕРХНЕГО уровня из сборки документированным "
                + "ksDocument3D.DeleteObject (страница «Удалить объект: деталь, операцию, сопряжение»). "
                + "Удаляется ВХОЖДЕНИЕ в сборку, а НЕ файл детали: файл-источник не трогается. "
                + "Зависимые сопряжения перечисляются ДО удаления двумя маршрутами (перебор "
                + "MateConstraintCollection по IModelObject.Part и ответ ядра ObjectConstraints): при "
                + "наличии зависимых и confirm_dependents=false вызов ОТКАЗЫВАЕТ с перечнем и состав "
                + "сборки не меняется. Ядро удаляет зависимые сопряжения ВМЕСТЕ с компонентом — "
                + "измеренная судьба названа в dependent_mates_disposition. Удалён ИМЕННО адресованный "
                + "компонент (по источнику и размещению, а не по порядковому номеру): после удаления "
                + "порядковые номера остальных сдвигаются, ревизия поднимается, и ВСЕ ссылки на "
                + "компоненты прежней ревизии устаревают — перечитайте структуру kompas_list_components. "
                + "Вложенный компонент отвергается CAPABILITY_UNAVAILABLE.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("component_ref", Sch.Str("Ссылка на экземпляр из kompas_list_components.")),
                    ("confirm_dependents", Sch.Nullable(Sch.Bool(
                        "Согласие удалить компонент вместе с зависимыми сопряжениями. По умолчанию "
                        + "false: при зависимых сопряжениях вызов отказывает с их перечнем.", false)))),
                WorkerCommands.DeleteComponent,
                requiresDocument: true,
                requiresRevision: true),

            // ===== interference and gaps (block G1, profile assembly-interference-minimal-v1) =====
            // Routes measured from the help over the wire and from the installed type library; both
            // commands only READ, so neither declares operation_id.
            // History: docs/decisions/assembly.md#interference-contracts
            ReadOnly("kompas_check_interference", "Пересечения компонентов сборки",
                "Проверяет тела компонентов сборки по парам документированным "
                + "ksBody.CheckIntersectionWithBody и называет тип каждого пересечения по "
                + "Intersection_Type (itBody — пересечение образует тело). Адресуются только "
                + "компоненты ВЕРХНЕГО уровня: ссылки берутся из kompas_list_components, вложенный "
                + "компонент отвергается. check_tangent = true означает «считать касания "
                + "пересечениями» и меняет смысл ответа, поэтому он возвращается эхом. "
                + "include_faces добавляет номера пересекаемых и совпадающих граней в нумерации "
                + "FaceCollection того же тела. Отсутствие пересечения (NULL ядра) отличается от "
                + "непрочитанного исхода: у такой пары intersecting = null, а причина названа в basis. "
                + "Серия положений компонента делается связкой с kompas_set_component_placement: "
                + "перемещение шагами и повторный вызов этого инструмента на каждом шаге.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("component_refs", Sch.Nullable(Sch.Arr(
                        Sch.Str("Ссылка на компонент из kompas_list_components."),
                        "Пары составляются по этому списку. Не задан — проверяются все адресуемые "
                        + "компоненты верхнего уровня. Нужно не менее двух РАЗНЫХ компонентов.",
                        2, 64, true))),
                    ("check_tangent", Sch.Nullable(Sch.Bool(
                        "Считать касания пересечениями. По умолчанию false.", false))),
                    ("include_faces", Sch.Nullable(Sch.Bool(
                        "Дочитывать номера пересекаемых и совпадающих граней каждого тела.", false)))),
                WorkerCommands.CheckInterference,
                requiresDocument: true,
                requiresOperationId: false),

            ReadOnly("kompas_measure_gap", "Минимальное расстояние между компонентами",
                "Измеряет минимальное расстояние (и угол, где он определён) между двумя компонентами "
                + "сборки или их гранями документированным IMeasurement3D: справка прямо называет "
                + "комбинации «тело и компонент», «компонент и компонент». Объект — компонент "
                + "верхнего уровня из kompas_list_components либо его грань по номеру в FaceCollection "
                + "(та же нумерация, что у kompas_create_mate). Результат называется именем из "
                + "ksMeasureResultEnum; неопределённое расстояние приходит как null с причиной, а не "
                + "нулём. Единица длины справкой не названа: она измерена живым прогоном как "
                + "миллиметры и названа в units_basis. Требуется КОМПАС v23 и новее — с этой версии "
                + "справка объявляет IPart7.Measurement3D; ниже порога инструмент отказывает "
                + "CAPABILITY_UNAVAILABLE, а не вызывает слот, которого на той версии нет.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("object1", Sch.Obj(
                        "Первый измеряемый объект",
                        new[] { "component_ref" },
                        Sch.Props(
                            ("component_ref", Sch.Str("Ссылка на компонент из kompas_list_components.")),
                            ("face_index", Sch.Nullable(Sch.Int(
                                "Номер грани в FaceCollection компонента. Не задан — измеряется "
                                + "компонент целиком.")))))),
                    ("object2", Sch.Obj(
                        "Второй измеряемый объект",
                        new[] { "component_ref" },
                        Sch.Props(
                            ("component_ref", Sch.Str("Ссылка на компонент из kompas_list_components.")),
                            ("face_index", Sch.Nullable(Sch.Int(
                                "Номер грани в FaceCollection компонента. Не задан — измеряется "
                                + "компонент целиком."))))))),
                WorkerCommands.MeasureGap,
                requiresDocument: true,
                requiresOperationId: false),

            // ===== mate domain (block C2, profile mates-minimal-v1) =====
            // Route = customer's decision: documented API7 path IPart7.MateConstraints →
            // IMateConstraints3D.Add → BaseObject1/2 → Update(). ksDocument3D.AddMateConstraint is NOT
            // used (returned False for every documented combination). Capability level mcp_verified:
            // block C2 accepted by a live run on shipped binaries (MATE 55/55).
            // History: docs/decisions/host.md#mates-domain
            ReadOnly("kompas_list_mates", "Сопряжения сборки",
                "Перечень сопряжений: тип, выравнивание, фиксация, параметр, оба базовых объекта, "
                + "подтверждение Valid и — для передачи — MotionType/Scale/Direction. Фиксация "
                + "печатается по сырому числу поля fixed (mateconstraintfixed.html: 0 нет, 1 деталь 1, "
                + "2 деталь 2); ksMateFixedTypeEnum называет 0 «Неопределено» — расхождение названо.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.ListMates,
                requiresDocument: true,
                requiresOperationId: false),

            Mutation("kompas_create_mate", "Создать сопряжение",
                "Создаёт сопряжение граней двух компонентов (Add → BaseObject1/2 → Update); "
                + "подтверждение — Valid, а не Update()=true.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("constraint_type", Sch.Enum(
                        "Тип по MateConstraintType; transmission — передача (создаётся и читается, ход "
                        + "ведомого не проверяется), cam_gear вне контракта.",
                        "coincidence", "parallel", "perpendicular", "tangency",
                        "concentric", "distance", "angle", "transmission")),
                    ("first_component_ref", Sch.Str("Ссылка на первый компонент из kompas_list_components.")),
                    ("first_face_index", Sch.Int("Номер грани первого компонента в его FaceCollection.")),
                    ("second_component_ref", Sch.Str("Ссылка на второй компонент.")),
                    ("second_face_index", Sch.Int("Номер грани второго компонента.")),
                    // INVARIANT: an explicit value is confirmed by the MEASURED orientation of the two
                    // faces, not by the number the kernel keeps; `closest` names no orientation and is
                    // never checked. MEASURED on two placements: opposite lands on -1, cooriented on +1.
                    // History: docs/decisions/mates.md#alignment-geometry-criterion
                    ("alignment", Sch.Nullable(Sch.Enum(
                        "Вариант выравнивания: opposite/cooriented подтверждаются измеренной "
                        + "ориентацией граней, closest не проверяется.",
                        "opposite", "cooriented", "closest"))),
                    // INVARIANT: the parameter is bound to the type and checked by the Host. Per
                    // mateconstrainttype.html only mc_Distance (5, «постоянное расстояние») and
                    // mc_Angle (6, «постоянный угол») are parametric; for the rest an accepted number
                    // would be recorded and ignored. The help does NOT name the UNITS — the server
                    // confirms by reading back. History: docs/decisions/host.md#mate-param
                    ("param_value", Sch.Nullable(Sch.Num(
                        "Параметр: обязателен для distance/angle, запрещён для остальных типов; "
                        + "единицы задаёт КОМПАС."))),
                    ("motion1", Sch.Nullable(Sch.Enum("MotionType1 (transmission).",
                        "rotation", "linear"))),
                    ("motion2", Sch.Nullable(Sch.Enum("MotionType2 (transmission).",
                        "rotation", "linear"))),
                    ("scale1", Sch.Nullable(Sch.Num("Scale1 > 0 (transmission)."))),
                    ("scale2", Sch.Nullable(Sch.Num("Scale2 > 0 (transmission)."))),
                    ("direction1", Sch.Nullable(Sch.Bool("Direction1 (transmission)."))),
                    ("direction2", Sch.Nullable(Sch.Bool("Direction2 (transmission).")))),
                WorkerCommands.CreateMate,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_set_mate_parameter", "Задать параметр сопряжения",
                "Задаёт параметр сопряжения (расстояние или угол) и перечитывает его.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mate_ref", Sch.Str("Ссылка на сопряжение из kompas_list_mates.")),
                    ("param_value", Sch.Num("Новое значение параметра."))),
                WorkerCommands.SetMateParameter,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_set_mate_fixed", "Задать фиксацию сопряжением",
                "Задаёт признак фиксации компонентов сопряжением (ksMateFixedTypeEnum: none/first/second) "
                + "и перечитывает его. Нумерация документирована: ksmatefixedtypeenum.html "
                + "(ksMFixedUnknown=0, ksMFixedPart1=1, ksMFixedPart2=2) и mateconstraintfixed.html "
                + "«0 нет фиксации, 1 фиксировать деталь 1, 2 фиксировать деталь 2»; публичное имя "
                + "none следует второму источнику. Несовпадение перечитанного значения — отказ, а не "
                + "успех с предупреждением.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mate_ref", Sch.Str("Ссылка на сопряжение из kompas_list_mates.")),
                    ("fixed", Sch.Enum("Признак фиксации.", "none", "first", "second"))),
                WorkerCommands.SetMateFixed,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_delete_mate", "Удалить сопряжение",
                "Удаляет сопряжение документированным RemoveMateConstraint(constraintType, obj1, obj2); "
                + "оба объекта берутся у самого сопряжения через GetBaseObj(1|2).",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mate_ref", Sch.Str("Ссылка на сопряжение из kompas_list_mates."))),
                WorkerCommands.DeleteMate,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_measure", "Измерить",
                "Габарит, объём, площадь, центр масс. Масса считается только при переданной плотности: "
                + "сервер её не угадывает. ЧТО ЧИТАЕТСЯ ЗАВИСИТ ОТ ВИДА ЦЕЛИ, и каждое запрошенное, но "
                + "НЕ ПРОЧИТАННОЕ свойство называется в unverified_aspects со своей причиной — пустое "
                + "значение неотличимо от «забыли заполнить». ТЕЛО: читаются bbox, volume, "
                + "surface_area, centroid; mass — при плотности; inertia — МЦХ тела (осевые, центробежные, "
                + "плоскостные и главные центральные моменты инерции плюс направления главных осей) "
                + "документированным маршрутом ksPart.CalcMassInertiaProperties → ksMassInertiaParam. "
                + "Единицы моментов задаёт АРГУМЕНТ вызова (inertia_units), а не константа кода, и блок "
                + "inertia их называет вместе с системой координат: одно и то же имя Jx означает разные "
                + "величины в центральной и главной центральной системах. ПЛОТНОСТЬ: mass_kg считается по "
                + "АРГУМЕНТУ density_kg_per_m3, а блок inertia — по плотности МАТЕРИАЛА детали, которую он "
                + "называет в поле density_kg_per_m3; при расхождении с аргументом ответ называет обе "
                + "плотности и добавляет аспект inertia_density_mismatch — моменты не выдаются за "
                + "посчитанные по запрошенной плотности. Если интерфейс МЦХ не вернулся "
                + "(у тела нет МЦХ на этой геометрии), запрос inertia даёт отказ INERTIA_NOT_AVAILABLE, а "
                + "не ноль. ГРАНЬ: читается ТОЛЬКО surface_area; bbox у грани документированным "
                + "маршрутом API5 НЕ читается (у ksFaceDefinition его нет) — габарит берите у ТЕЛА "
                + "(kompas_list_bodies); volume, mass и inertia к грани не "
                + "применимы. РЕБРО: ни одно из запрошенных свойств не читается, и каждое названо "
                + "отдельно. Запрос bbox у грани БЕЗ surface_area идёт той же ветвью грани.",
                Sch.Props(
                    ("target_ref", Sch.Ref("#/$defs/reference")),
                    ("properties", Sch.Arr(Sch.Enum("Свойство", "bbox", "volume", "surface_area", "mass", "centroid", "inertia"), "Запрашиваемые свойства.", 1, 6)),
                    ("density_kg_per_m3", Sch.Nullable(Sch.Positive("Плотность материала", "кг/м³"))),
                    ("inertia_units", Sch.Nullable(Sch.Enum(
                        "Единицы моментов инерции (только при запрошенном inertia): mm_kg — длина в мм, "
                        + "масса в кг (по умолчанию); m_kg — длина в м, масса в кг. Единица — АРГУМЕНТ "
                        + "вызова: значение момента с m_kg меньше в 1e6 раз (квадрат отношения длин), и "
                        + "блок inertia называет применённую единицу в поле units.",
                        "mm_kg", "m_kg")))),
                WorkerCommands.Measure,
                requiresDocument: false,
                requiresOperationId: false),

            Mutation("kompas_create_sketch", "Создать эскиз",
                "Эскиз на плоскости: base (стандартная xy/xz/yz, при необходимости со смещением "
                + "offset_mm) ЛИБО reference (ссылка вида plane — смещённая или вспомогательная "
                + "плоскость, например построенная от грани инструментом kompas_create_aux_geometry). "
                + "Грань и ребро опорой не являются и отвергаются по виду ссылки. Координаты дальше "
                + "задаются в локальной системе эскиза. "
                + "ССЫЛКА НА СОЗДАННЫЙ ЭСКИЗ живёт до перестроения или переоткрытия документа: после "
                + "них прежняя отвергается STALE_REFERENCE, а новую даёт kompas_list_sketches — тот же "
                + "эскиз носит ОДНУ живую ссылку, и перечень возвращает именно её, а не вторую. "
                + "УДАЛИТЬ созданный эскиз — kompas_delete_feature по этой же ссылке.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("plane", PlaneSchema()),
                    ("name", Sch.Nullable(Sch.Str("Имя эскиза.", maxLength: 256)))),
                WorkerCommands.CreateSketch,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_edit_sketch", "Изменить эскиз",
                                "Добавить (append), заменить целиком (replace) или очистить (delete_entities) примитивы эскиза. " +
                "Замена и очистка удаляют ФАКТИЧЕСКИЙ состав эскиза: объекты перечисляются тем же маршрутом, " +
                "которым читает kompas_list_sketch_entities, и удаляются по одному - вся геометрия, а не только " +
                "нарисованная сервером. Не-геометрия (размеры, обозначения) очисткой не затрагивается. Состав " +
                "перечислить не удалось - отказ CAPABILITY_UNAVAILABLE, а не удаление «примерно того». После правки " +
                "состав перечитывается и сверяется по числу и видам: расхождение - GEOMETRY_FAILED с перечнем " +
                "оставшихся объектов в details. Если зависимое тело исчезло, правка не выдаётся за успешную. " +
                "Валидация пачки - до обращения к COM. ФОРМА ПОЛИЛИНИИ И СПЛАЙНА: kind=polyline рисуется ОДНИМ " +
                "объектом ломаной и читается ОДНОЙ строкой kind=ksDrPolyline с points_count и closed, " +
                "collection_counts даёт poly_lines = 1; kind=spline - один сплайн NURBS. Площадь профиля и " +
                "ожидаемый объём считаются по вершинам контура. ПРИЧИНА ПУСТОЙ ПЛОЩАДИ НАЗЫВАЕТСЯ: profile_area_mm2 " +
                "= null вместе с profile_area_unavailable - причиной с местом дефекта (номера примитивов в порядке " +
                "передачи, с нуля, и координата точки). Это анализ ВХОДА сервером, а не ответ ядра; профиль клиента " +
                "не правится, и по своему анализу сервер в правке не отказывает. ДЛИНА ПАЧКИ: предел числа " +
                "примитивов задан схемой MCP, а не ядром.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mode", Sch.Enum("append — дорисовать; replace — очистить и нарисовать; delete_entities — только очистить. Последние два удаляют весь ФАКТИЧЕСКИЙ состав эскиза, перечисленный тем же документированным маршрутом, что и kompas_list_sketch_entities, и подтверждаются перечитыванием состава, а не ответами вызовов.", "append", "replace", "delete_entities")),
                    ("entities", Sch.Arr(Sch.Ref("#/$defs/sketch_entity"), "Примитивы эскиза. Пустой список допустим только при mode=delete_entities; для append и replace сервер требует хотя бы один примитив. Предел " + SketchLimits.MaxEntitiesPerCall + " примитивов на вызов задан СХЕМОЙ MCP, а не ядром КОМПАС: тот же объём ядро принимает и несколькими вызовами.", 0, SketchLimits.MaxEntitiesPerCall))),
                WorkerCommands.EditSketch,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_set_sketch_plane", "Сменить опорную плоскость эскиза",
                "Меняет ОПОРНУЮ плоскость СУЩЕСТВУЮЩЕГО эскиза: зависимое тело перестраивается по "
                + "новой опоре, профиль эскиза не меняется. Маршрут — документированный "
                + "ksSketchDefinition.SetPlane («Изменить базовую плоскость эскиза», "
                + "kssketchdefinition_setplane.html), затем обязательный sketch.Update(): измерено, "
                + "что EndEdit() правку НЕ применяет, а Update() применяет, а RebuildModel и "
                + "RebuildDocument после него не добавляют ничего (проба --sketch-plane, отчёт "
                + "docs/acceptance/image/sketch-plane-probe-report.md). "
                + "Форма plane — ТА ЖЕ, что у kompas_create_sketch: base+offset_mm ЛИБО reference, "
                + "одновременно ровно одно; второй диалект опоры не заводится. reference годится "
                + "только вида plane (смещённая или вспомогательная плоскость): грань и ребро "
                + "отвергаются по виду ссылки ДО COM. "
                + "ОТКАЗ НЕ-ПЛОСКОСТИ ПРОИСХОДИТ ДО COM И НАЗЫВАЕТСЯ КОДОМ, и это не осторожность, а "
                + "измерение: ядро ПРИНИМАЕТ в опору плоскую ГРАНЬ (все шесть граней коробки "
                + "перепривязывают зависимое тело, SP.9) и ОТВЕРГАЕТ ребро и тело (SetPlane=False, "
                + "SP.8). Продукт этот ответ не наследует: reference, ведущая не на плоскость, "
                + "отвергается INVALID_ARGUMENT по виду ссылки из реестра, без обращения к COM. "
                + "ПРИМЕНЕНИЕ ПОДТВЕРЖДАЕТСЯ ГАБАРИТОМ И ОБЪЁМОМ зависимого тела до и после вместе с "
                + "ПОВТОРНЫМ ЧТЕНИЕМ опоры документированным GetPlane(): успешный код SetPlane "
                + "применением не объявляется. Опора той же плоскостью — законный вызов, и он "
                + "измеренно НЕ меняет геометрию. "
                + "СМЕНА ОПОРЫ — МУТАЦИЯ САМА ПО СЕБЕ: принятая ядром смена поднимает ревизию и "
                + "делает документ изменённым (dirty), независимо от того, сдвинулось ли тело. Поле "
                + "geometry_changed отвечает на ДРУГОЙ вопрос — изменился ли габарит или объём "
                + "зависимого тела, — и по нему нельзя судить, была ли мутация: опора может "
                + "смениться при неизменном габарите, поэтому revision растёт всегда, а "
                + "geometry_changed — нет. "
                + "ПРЕДЫСТОРИЯ: опора эскиза не выражалась ничем — из 43 опубликованных схем "
                + "плоскость задавал только kompas_create_sketch при СОЗДАНИИ, а kompas_edit_sketch "
                + "принимает лишь entities. Расширение kompas_edit_sketch полем plane отвергнуто "
                + "ИЗМЕРЕНИЕМ: размывание на 301 строке при нулевом приросте своих (прибор "
                + "scratch/_calibrate_read_signature.py, раздел edit).",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("plane", PlaneSchema())),
                WorkerCommands.SetSketchPlane,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_finish_sketch", "Завершить эскиз",
                "Закрывает редактирование. Замкнутость профиля в API5 не проверяется — это честно возвращается в unverified_aspects, а выдавливание всё равно покажет настоящий отказ. СВЕРХ ЭТОГО сервер публикует СВОЙ разбор входа: profile_input_check = passed | failed | not_available и profile_input_reason — причина с местом, тот же текст, что у profile_area_unavailable в kompas_edit_sketch. passed означает «вход согласован по анализу сервера», а НЕ подтверждение ядра: profile_closed_confirmed остаётся false всегда. При require_closed_profile = true и profile_input_check = failed вызов НЕ отказывает — первой строкой warnings приходит предупреждение (решает выдавливание). not_available означает, что анализировать нечего: эскиз нарисован другим клиентом, документ переоткрыт, в профиле сплайн или он пуст.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("require_closed_profile", Sch.Bool("Требование замкнутости профиля. Ядро замкнутость не подтверждает — profile_closed_confirmed всегда false; при true сервер добавляет свой разбор входа в profile_input_check и на failed отвечает предупреждением в warnings, а не отказом.", true))),
                WorkerCommands.FinishSketch,
                requiresDocument: false),

            Mutation("kompas_extrude", "Выдавить",
                                "Базовое выдавливание, приклеивание или вырезание. После вызова перечитывается объём: без " +
                "подтверждённого изменения геометрии успех не выдаётся. Для boss и cut целевое тело обязано быть " +
                "указано и действительно выбирается средствами КОМПАС; перед мутацией сверяются габариты контура и " +
                "заявленного тела, после - объём каждого тела, поэтому no-op (Create=true без изменений) не " +
                "выдаётся за успех. В ответе ТРИ разные величины названы раздельно: volume_mm3 - СУММА объёмов всех " +
                "тел документа (не объём целевого тела и не объединение: у перекрывающихся тел они различаются); " +
                "volume_delta_mm3 - приращение материала ЭТИМ признаком (у cut - снятое); volume_delta_basis - из " +
                "какого тела оно взято (target_body_N, new_body_volume, existing_body_N_delta, not_attributable); " +
                "document_volume_before_mm3 и document_volume_after_mm3 - сумма по документу до и после. У base " +
                "приращение берётся у НОВОГО тела, а не вычитанием из нуля, и подтверждается проверкой " +
                "body_change_attribution: измениться обязано ровно одно тело. Поэтому на многотельном документе " +
                "второй и третий вызовы подтверждаются измерением, а не занижаются до call_returned. ПРОВЕРКА " +
                "ОБЪЁМА ОПИРАЕТСЯ НА ВЕРХНЮЮ ГРАНИЦУ: аналитическая призма профиля (площадь x глубина; при " +
                "end_condition=through - площадь x протяжённость тела) есть ВЕРХНЯЯ ГРАНИЦА сдвинутого материала. " +
                "Совпала с измеренной прибавкой - пройдена проверка volume_delta. Меньше призмы - НЕ провал: " +
                "приклеивание, пересекающее тело, добавляет меньше призмы, а вырезание через полость снимает " +
                "меньше; тогда выдаётся пройденная проверка volume_upper_bound, ожидание называется невычисленным " +
                "(expected_volume_not_computed в unverified_aspects), уровень остаётся call_returned. БОЛЬШЕ " +
                "призмы, ноль и обратный знак - провал. Точного объёма пересечения инструмента с телом " +
                "документированного маршрута нет, поэтому призма не выдаётся за точное ожидание. ОТДЕЛЬНО от призмы " +
                "принимается ЯВНОЕ ожидание expected_volume_delta_mm3 - знаковая величина сдвинутого материала " +
                "(положительная у base/boss, у cut - снятое): с ним сравнение на равенство в допуске проекта, и " +
                "призму оно НЕ ослабляет. Сторону материала ответ называет полями material_removed_toward и " +
                "material_added_toward и источником material_toward_source (measured_box_shift - по сдвигу " +
                "габарита, sketch_normal_rule - по документированному правилу нормали плоскости эскиза, оно " +
                "работает и когда габарит не двигается). Если одностороннее выдавливание не изменило тело " +
                "(NO_GEOMETRY_CHANGE), в details приходят requested_direction, attempted_material_toward, " +
                "opposite_direction, а когда по габариту проверено - body_side и body_lies_opposite: сообщение " +
                "говорит, куда ушла операция и что задать вместо direction. Автоматического повтора с другим " +
                "направлением и отката нет: признак остаётся в дереве, его ссылка - feature_ref в details. ОТКАЗ " +
                "SetSketch=false НЕСЁТ ТОТ ЖЕ РАЗБОР ПРОФИЛЯ, что Create()=false, в части, применимой ДО создания " +
                "признака: состояние эскиза, profile_area_mm2 и profile_area_unavailable записанного профиля плюс " +
                "код ошибки КОМПАС; причина профиля называется ПЕРВОЙ - как анализ ВХОДА сервером, а не ответ ядра. " +
                "Состояние профиля с местом дефекта даёт kompas_edit_sketch (profile_area_unavailable) и " +
                "kompas_finish_sketch (profile_input_check), поэтому причину видно ДО выдавливания. СТОРОНА " +
                "ЗАДАЁТСЯ РОВНО ОДНИМ из direction и material_toward: оба или ни одного - INVALID_ARGUMENT до " +
                "обращения к КОМПАС. material_toward называет ОДНУ сторону, поэтому end_condition=through (работает " +
                "только при direction=symmetric) требует direction. Предпочтителен material_toward: сторона в " +
                "координатах детали, и клиенту не нужно знать правило, по которому positive у cut и у base/boss " +
                "означает противоположные стороны.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("operation", Sch.Enum("base — первое тело; boss — добавить; cut — вырезать.", "base", "boss", "cut")),
                    ("end_condition", Sch.Nullable(Sch.Enum(
                        "blind - на глубину depth_mm (значение по умолчанию); through - насквозь. through принят только для cut: " +
                        "«насквозь» работает при direction=symmetric, а число глубины в этом режиме игнорируется.",
                        "blind", "through"))),
                    ("depth_mm", Sch.Nullable(Sch.PositiveMm(
                        "Глубина",
                        "Обязательна при end_condition=blind и запрещена при end_condition=through — "
                        + "сервер не принимает «насквозь с глубиной», потому что КОМПАС это число "
                        + "всё равно отбрасывает."))),
                    ("direction", Sch.Nullable(Sch.Enum(
                                                "Сторона выдавливания СЛОВОМ КОМПАС. НЕОБЯЗАТЕЛЬНО: сторону задаёт ровно одно из direction " +
                        "и material_toward. ВНИМАНИЕ: это НЕ одно геометрическое направление для всех операций - " +
                        "правило КОМПАС «для вырезаемого элемента выдавливания направление противоположно нормали» " +
                        "означает, что у cut positive снимает материал ПРОТИВ нормали эскиза, а у base/boss " +
                        "positive добавляет ПО нормали. Предпочтителен material_toward: он называет сторону в " +
                        "координатах детали, и сервер сам переводит её по этому правилу. symmetric выражается " +
                        "ТОЛЬКО здесь. Фактическую сторону ответ называет полями material_removed_toward (cut) или " +
                        "material_added_toward (base/boss), а источник - material_toward_source: measured_box_shift " +
                        "(по сдвигу габарита тела) или sketch_normal_rule (по правилу нормали; работает и когда " +
                        "габарит не двигается - вырез внутри тела). Прочитанная нормаль публикуется в " +
                        "material_toward_sketch_normal. Расхождение источников - в warnings, а не молчаливый выбор; " +
                        "когда сторону назвать нельзя, причина в material_toward_unavailable.",
                        "positive", "negative", "symmetric"))),
                    ("material_toward", Sch.Nullable(Sch.Enum(
                                                "Сторона, в которую снимается (cut) или добавляется (base/boss) материал, в КООРДИНАТАХ " +
                        "ДЕТАЛИ: +x, -x, +y, -y, +z, -z. Рекомендуемый способ задать сторону: клиенту не нужно " +
                        "знать, что direction=positive у выреза и у приклеивания означает ПРОТИВОПОЛОЖНЫЕ стороны. " +
                        "Сервер переводит её в positive/negative по нормали плоскости эскиза. Ось обязана совпадать " +
                        "с нормалью (или быть ей обратной) в допуске: иначе INVALID_ARGUMENT ДО создания признака, " +
                        "и в details приходят нормаль, угол и допуск - для наклонной опоры следует задать " +
                        "direction. symmetric этим полем НЕ выражается. В ответе: material_toward_requested - " +
                        "запрошенная сторона, direction_resolved - слово КОМПАС, в которое она переведена; " +
                        "расхождение с фактической стороной приходит в warnings и unverified_aspects, а не " +
                        "выбирается молча. Знак минус принимается и как дефис, и как U+2212 из ответа.",
                        "+x", "-x", "+y", "-y", "+z", "-z"))),
                    ("target_body_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание СДВИНУТОГО признаком материала, мм³: положительная величина - "
                        + "добавленное базовым выдавливанием или приклеиванием, у вырезания - снятое. ЗНАК - ЧАСТЬ "
                        + "ВЕЛИЧИНЫ, сравнивать модуль нельзя. Принимается ОТДЕЛЬНО от призмы профиля, которая без "
                        + "него служит только верхней границей: пересекающийся boss добавляет меньше призмы, а cut "
                        + "через полость снимает меньше, и это не провал. С ожиданием проверка численная (равенство "
                        + "в допуске проекта), без него - верхняя граница."
                        + DeclaredExpectationRule, -1e18d, 1e18d)))),
                WorkerCommands.Extrude,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_fillet", "Скругление",
                "Скругление по явным edge_ref из kompas_read_topology (рёбра конечного тела, не EntityCollection(7)). Успехом считается не HRESULT: радиус перечитывается с созданного признака, число граней обязано вырасти ровно на число рёбер, а ПОВЕРХНОСТЬ обязан прилегать к запрошенному ребру — ось цилиндра параллельна ребру и отстоит от обеих граней, смежных с ним, на радиус. Перенос на другое ребро — отказ GEOMETRY_FAILED (details.code = fillet_on_other_edge): объёмом он неразличим на теле с равнозначными углами. Знак изменения объёма не утверждается: на ВЫПУКЛОМ ребре скругление материал снимает, на ВХОДЯЩЕМ — добавляет.",
                Sch.Props(
                    ("edge_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Рёбра, полученные kompas_read_topology. Позиции в коллекции не принимаются — они не являются постоянными идентификаторами.",
                        1, 64)),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("radius_mm", Sch.PositiveMm("Радиус скругления")),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание изменения объёма как «до − после», если выводимо у вызывающего (например 4·(1−π/4)·r²·h для четырёх выпуклых параллельных рёбер). Знак несёт сторона угла: на ВЫПУКЛОМ ребре скругление материал снимает, и ожидание положительно; на ВХОДЯЩЕМ добавляет, и то же выражение входит со знаком минус. С ожиданием проверка численная, без него — только изменение объёма." + DeclaredExpectationRule,
                        -1e18d, 1e18d)))),
                WorkerCommands.Fillet,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_chamfer", "Фаска",
                "Фаска по явным edge_ref из kompas_read_topology (рёбра конечного тела, а не элементы построения). Два " +
                "способа: two_distances - двумя катетами, distance_angle - расстоянием и углом в ГРАДУСАХ. Успехом " +
                "считается не факт записи: катеты и сторона перечитываются с созданного признака, число граней " +
                "обязано вырасти ровно на число рёбер, а НОВАЯ ГРАНЬ обязана отсекать обе грани, смежные с " +
                "запрошенным ребром, на заданные катеты. Перенос на другое ребро - отказ GEOMETRY_FAILED " +
                "(details.code = chamfer_on_other_edge): объёмом он неразличим на теле с равнозначными углами. " +
                "Знак изменения объёма не утверждается: на ВЫПУКЛОМ ребре фаска материал снимает, на ВХОДЯЩЕМ - " +
                "добавляет. direction меняет, какой катет ложится на какую грань; при равных катетах это " +
                "неразличимо. Способ distance_angle создаётся отдельным маршрутом, который строится не во всяком " +
                "сеансе: если он недоступен, вызов отказывает кодом CAPABILITY_UNAVAILABLE и ничего не создаёт, а " +
                "не подменяется двумя катетами.",
                Sch.Props(
                    ("edge_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Рёбра, полученные kompas_read_topology. Позиции в коллекции не принимаются - они не являются постоянными " +
                        "идентификаторами.",
                        1, 64)),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mode", Sch.Enum(
                        "two_distances - двумя катетами; distance_angle - расстоянием и углом, угол в градусах. Способ " +
                        "distance_angle создаётся отдельным маршрутом, который строится не во всяком сеансе: если он " +
                        "недоступен, вызов отказывает CAPABILITY_UNAVAILABLE и ничего не создаёт.",
                        "two_distances", "distance_angle")),
                    ("distance1_mm", Sch.PositiveMm("Первый катет",
                        "При mode=distance_angle это РАССТОЯНИЕ, а не катет.")),
                    ("distance2_mm", Sch.Nullable(Sch.PositiveMm(
                        "Второй катет",
                        "Обязателен при mode=two_distances, запрещён при mode=distance_angle. " +
                        "Для фаски под 45° задайте его равным distance1_mm."))),
                    ("angle_deg", Sch.Nullable(Sch.Num(
                        "Угол фаски в ГРАДУСАХ, только строго между 0 и 90. Обязателен при mode=distance_angle, запрещён при " +
                        "mode=two_distances.",
                        0d, 90d))),
                    ("direction", Sch.Nullable(Sch.Bool(
                        "Сторона фаски. При неравных катетах меняет, какой катет ложится на какую из двух граней; объём при этом " +
                        "не различается, поэтому по объёму сторона не проверяется. Без поля - false.", false))),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание изменения объёма как «до − после»: N·(d₁·d₂/2)·L для N параллельных выпуклых " +
                        "рёбер длиной L. Знак несёт сторона угла: на ВЫПУКЛОМ ребре фаска материал снимает " +
                        "(ожидание положительно), на ВХОДЯЩЕМ добавляет (то же выражение со знаком минус). С ним " +
                        "проверка численная, без него - только изменение объёма." + DeclaredExpectationRule,
                        -1e18d, 1e18d)))),
                WorkerCommands.Chamfer,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_hole", "Родное отверстие",
                                "Родное отверстие на явной опорной грани face_ref из kompas_read_topology. Три режима, все - " +
                "маршрутом API7 того же сеанса: в API5 параметров режима не существует (типа ksHoleDefinition в " +
                "обёртке нет), режимные числа живут на HoleParameters, приведённом к интерфейсу СВОЕГО режима. " +
                "blind_flat - глухое с плоским дном: ksDTValue плюс ksEFFlat. through_counterbore - цековка: пилот " +
                "насквозь плюс КОЛЬЦЕВАЯ выточка π/4·(D²−d²)·h. through_countersink - зенковка: пилот насквозь плюс " +
                "конус; снятое сверх пилота равно π·h/3·(rM² + rP·rM − 2·rP²), где h - глубина, которую вернул САМ " +
                "объект, rM - радиус устья, rP - радиус пилота. Глубина зенковки ПРОИЗВОДНА и записью не " +
                "управляется: объект возвращает (rM − rP)/tan(угол/2), поэтому глубина публикуется как " +
                "countersink_depth_read_back_mm. Позиция вне начала координат задаётся парой " +
                "offset_x_mm/offset_y_mm и работает маршрутом Point3DParamSurface + OffsetType=ksOffsetByCoords; " +
                "AssociationVertex и DirectionObject дают DISP_E_TYPEMISMATCH, эскиз со смещённой окружностью до " +
                "API7 не доезжает, DepthVertex/DepthFace читаются как null. Если мост API7 недоступен, вызов " +
                "отказывает CAPABILITY_UNAVAILABLE и ничего не создаёт. Успех - не Update(): параметры " +
                "перечитываются с созданного признака, граней обязано стать больше, объём - уменьшиться, а при " +
                "заданном ожидании он сверяется числом. Признак, оставшийся в дереве с ошибкой ядра (object_error), " +
                "отвергается GEOMETRY_FAILED, неизменившееся тело - NO_GEOMETRY_CHANGE; в обоих случаях ответ несёт " +
                "feature_ref для kompas_delete_feature.",
                Sch.Props(
                    ("face_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mode", Sch.Enum(
                        "blind_flat — глухое с плоским дном; through_counterbore — цековка (пилот " +
                        "насквозь + кольцевая выточка); through_countersink — зенковка (пилот " +
                        "насквозь + конус).",
                        "blind_flat", "through_counterbore", "through_countersink")),
                    ("diameter_mm", Sch.PositiveMm(
                        "Диаметр отверстия",
                        "У цековки и зенковки это диаметр ПИЛОТА, а не выточки и не устья.")),
                    ("depth_mm", Sch.Nullable(Sch.PositiveMm(
                        "Глубина",
                        "Обязательна при mode=blind_flat, запрещена при сквозных режимах: сквозное "
                        + "отверстие числа глубины не принимает."))),
                    ("counterbore_diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Диаметр выточки",
                        "Только для mode=through_counterbore. Обязателен там и должен быть БОЛЬШЕ "
                        + "диаметра пилота: выточка уже пилота не снимает материал, потому что "
                        + "измеренная формула M.2 — кольцо π/4·(D²−d²)·h."))),
                    ("counterbore_depth_mm", Sch.Nullable(Sch.PositiveMm(
                        "Глубина выточки",
                        "Только для mode=through_counterbore, где обязательна."))),
                    ("countersink_diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Диаметр УСТЬЯ зенковки",
                        "Только для mode=through_countersink. Обязателен там и должен быть больше "
                        + "диаметра пилота."))),
                    ("countersink_angle_deg", Sch.Nullable(Sch.Num(
                        "Угол зенковки в ГРАДУСАХ, строго между 0 и 180 — только для " +
                        "mode=through_countersink, где обязателен. Измерялись 60, 90 и 120 (M.3). " +
                        "Глубина при способе «диаметр + угол» ПРОИЗВОДНА: объект возвращает " +
                        "(rM − rP)/tan(угол/2), где rM — радиус устья, rP — радиус пилота, а " +
                        "запись глубины не действует (запись 2, 4 и 6 даёт один и тот же снятый " +
                        "материал). Серия N.2 развела устье при неизменных пилоте и угле: " +
                        "Ø14/16/18/20/24 → h = 2/3/4/5/7.",
                        0d, 180d))),
                    ("offset_x_mm", Sch.Nullable(Sch.Num(
                                                "Смещение центра отверстия вдоль X опорной грани, мм. Задаётся ВМЕСТЕ с offset_y_mm: одна " +
                        "координата без второй оставила бы другую на догадку сервера. Без пары отверстие остаётся в " +
                        "начале координат поверхности. Смещения заданы в кадре ОПОРНОЙ ГРАНИ, а прочитанная ось - в " +
                        "координатах детали; сверить их можно только когда кадр грани совпадает с кадром детали " +
                        "(грань перпендикулярна оси Z). Для остальных граней проверка position_applied не выдаётся, " +
                        "причина - position_check_frame_unavailable в unverified_aspects. Центр оси публикуется в " +
                        "center_mm, когда ось диаметра найдена на теле; если оси нет, поле пусто, и отметка об этом " +
                        "не утверждает прочитанный центр.",
                        -1e6d, 1e6d))),
                    ("offset_y_mm", Sch.Nullable(Sch.Num(
                        "Смещение центра отверстия вдоль Y опорной грани, мм.",
                        -1e6d, 1e6d))),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание уменьшения объёма: π·r²·h для глухого; " +
                        "π·r²·h + π/4·(D²−d²)·h_выточки для цековки; " +
                        "π·r²·h + π·h_факт/3·(rM² + rP·rM − 2·rP²) для зенковки, причём h_факт — " +
                        "глубина, которую ВЕРНУЛ объект, а не запрошенная. С ним проверка " +
                        "численная, без него — только направление изменения. " +
                        "ОСЬ ОБЯЗАНА ЛЕЖАТЬ НА ЗАПРОШЕННОЙ ГРАНИ: если найденная цилиндрическая " +
                        "грань запрошенного диаметра не перпендикулярна её нормали либо её цилиндр " +
                        "не пересекает её плоскость — отказ GEOMETRY_FAILED " +
                        "(details.code = hole_on_other_face) со ссылкой на оставшийся признак. " +
                        "Проверка положения (center_mm) этим не заменяется: она про точку, эта — " +
                        "про грань." + DeclaredExpectationRule,
                        0d, 1e18d)))),
                WorkerCommands.Hole,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_rotated", "Вращение",
                                "Признак вращения: плоский замкнутый эскиз-профиль разворачивается вокруг явной оси. Если маршрут " +
                "вращения недоступен, вызов отказывает CAPABILITY_UNAVAILABLE и ничего не создаёт. operation=base - " +
                "первое тело, boss - приклейка к существующему, cut - вырезание; действие задаёт вид операции, " +
                "клиент его не переопределяет. БОБЫШКА СРАЩИВАЕТСЯ: прирост равен ОБЪЁМУ ТЕЛА МИНУС ПЕРЕСЕЧЕНИЕ, " +
                "тел остаётся одно. ПРОФИЛЬ задаётся sketch_ref и берётся из ПЛОСКОГО замкнутого эскиза. ОСЬ " +
                "ОБЯЗАТЕЛЬНА и задаётся двумя точками axis_point1_mm/axis_point2_mm: без оси вращение не строится, " +
                "точки обязаны различаться. УГОЛ УПРАВЛЯЕТ РАЗВЁРТКОЙ ДО 360 У ВСЕХ ТРЁХ ВИДОВ. base: полный оборот " +
                "- ОДИН вызов с angle_deg=360, отказ начинается только выше 360. cut: полный оборот выражается " +
                "только на заготовке, вмещающей инструмент ЦЕЛИКОМ: развёртка СИММЕТРИЧНА плоскости эскиза, поэтому " +
                "на плите, выдавленной в одну сторону, 180, 270 и 360 снимают одно и то же. direction=reverse " +
                "отвергается CAPABILITY_UNAVAILABLE. Доступны normal, both и middle_plane, причём сектор двигает " +
                "ТОЛЬКО middle_plane, и различается это ГАБАРИТОМ на НЕПОЛНОМ обороте (на полном тело " +
                "осесимметрично). Тонкая стенка (thin_wall_mm) отвергается CAPABILITY_UNAVAILABLE: вращение " +
                "тонкостенным не проверено. target_body_ref ПРИНИМАЕТСЯ и проверяется ПОСЛЕ операции: назначить " +
                "цель нельзя (селектора тела нет), ядро относит операцию к ПЕРЕСЕКАЕМОМУ телу и трогает РОВНО одно; " +
                "несовпадение или лишнее тронутое тело - GEOMETRY_FAILED с partial_effects. Неизвестная ссылка " +
                "отвергается до мутации. Коллекция тел после операции ПЕРЕУПОРЯДОЧИВАЕТСЯ, поэтому тела " +
                "сопоставляются по объёму и габариту, а не по индексу. УСПЕХ - не факт записи: параметры " +
                "перечитываются с созданного признака, обязана появиться поверхность вращения (цилиндрическая грань " +
                "читается вместе с габаритом: объём не отличает цилиндр от плиты того же объёма), знак изменения " +
                "объёма сверяется с видом операции. При expected_volume_mm3 проверка численная, без него - только " +
                "форма и знак. Признак переживает save → close → reopen: kompas_get_feature читает угол, " +
                "направление, состояние оси и вид операции, kompas_update_feature правит угол и направление.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("operation", Sch.Enum(
                        "Вид операции вращения. base - первое тело; boss - приклейка к существующему телу (тел остаётся " +
                        "одно, прирост равен объёму тела минус пересечение); cut - вырезание. Действие выбирается видом " +
                        "операции и клиентом не переопределяется.",
                        "base", "boss", "cut")),
                    ("angle_deg", Sch.Num(
                        "Угол развёртки в ГРАДУСАХ, строго больше 0 и не больше 360. Полный оборот (360) ВЫРАЖАЕТСЯ одним " +
                        "вызовом и даёт полный цилиндр у всех трёх видов операции. Значение больше 360 отвергается " +
                        "INVALID_ARGUMENT.",
                        0d, 360d)),
                    ("axis_point1_mm", Sch.Vec3(
                        "Первая точка оси вращения в координатах МОДЕЛИ, мм. Ось строится в этой же детали как вспомогательная " +
                        "геометрия по двум точкам. Ось обязательна: без неё вращение не строится вовсе. Точки обязаны " +
                        "различаться: вырожденная ось не задаёт развёртки.")),
                    ("axis_point2_mm", Sch.Vec3(
                        "Вторая точка оси вращения в координатах МОДЕЛИ, мм. Порядок точек задаёт " +
                        "направление оси.")),
                    ("direction", Sch.Nullable(Sch.Enum(
                        "Сторона развёртки относительно оси. normal - по умолчанию; both - сектор по обе стороны от оси; " +
                        "middle_plane - единственное направление, которое ДВИГАЕТ сектор. reverse ОТВЕРГАЕТСЯ " +
                        "CAPABILITY_UNAVAILABLE: при нём признак не строится вовсе.",
                        "normal", "reverse", "both", "middle_plane"))),
                    ("thin_wall_mm", Sch.Nullable(Sch.PositiveMm(
                        "Толщина тонкой стенки вращения",
                        "НЕ поддержана: вращение проверено только на СПЛОШНОМ теле. Любое значение отвергается " +
                        "CAPABILITY_UNAVAILABLE."))),
                    ("target_body_ref", Sch.Described(
                        Sch.Nullable(Sch.Ref("#/$defs/reference")),
                        "Ссылка на тело, которое операция обязана изменить. Целевое тело вращению НАЗНАЧИТЬ нельзя: селектора " +
                        "тела у вращения нет. Поэтому ссылка проверяется ПОСЛЕ операции по поимённым снимкам тел: ядро относит " +
                        "операцию к ПЕРЕСЕКАЕМОМУ телу, а не к первому в коллекции, и трогает ровно одно тело; если изменилось не " +
                        "объявленное тело или тронуто ещё какое-то, вызов отвечает GEOMETRY_FAILED с partial_effects, а не " +
                        "согласием.")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание ОБЪЁМА детали после операции, мм³. Для полного сектора r²/2·(α − sin α)·h при α в " +
                        "радианах и оси, смещённой от центра; полуоборот цилиндра даёт π·r²·h/2, четверть - π·r²·h/4. С ним " +
                        "проверка численная, без него - только форма и знак изменения." + DeclaredExpectationRule,
                        0d, 1e18d)))),
                WorkerCommands.Rotated,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_sweep", "Элемент по траектории",
                                "Кинематическая операция: новое тело по одному замкнутому профилю и непрерывной плоской траектории " +
                "(SM-04, профиль mechanical-core-v1). Маршрут - API5 o3d_baseEvolution=45 → " +
                "ksBaseEvolutionDefinition. Интерфейс ОБЪЯВЛЕН УСТАРЕВШИМ в самой справке («Рекомендуется " +
                "использовать вместо него интерфейс ksBossLoftDefinition»); рекомендованный приклеенный маршрут " +
                "o3d_bossEvolution=46 измерен отдельно и строит ТО ЖЕ тело на тех же различающих эталонах. Базовый " +
                "тип выбран потому, что обязанные строки описаны как базовые. РЕЖИМ ОРИЕНТАЦИИ задаётся shift_mode " +
                "по документированным значениям ksbaseevolutiondefinition_sketchshifttype.html: parallel = 0, " +
                "keep_angle = 1, orthogonal = 2 (они же получены измерением). На ПРЯМОЙ траектории parallel и " +
                "orthogonal дают одно тело; различающая постановка - только дуга, поэтому приёмка режима обязана " +
                "стоять на ДУГЕ, а не на отрезке. ДОКУМЕНТИРОВАННЫЕ ОГРАНИЧЕНИЯ ПЕРЕНЕСЕНЫ В КОНТРАКТ: " +
                "ортогональный режим «только для объекта-сечения, недоступно при вырезании телом», и касательная " +
                "траектории не должна быть параллельна плоскости сечения (aw2303904.html). УСПЕХОМ СЧИТАЕТСЯ НЕ " +
                "Create(): после построения перечитывается GetPathLength(1) - метод работает только на построенной " +
                "операции, - число тел обязано вырасти, а объём измениться. Тонкая стенка выразима только через " +
                "API5 SetThinParam: в API7 IEvolution члена ThinParam нет вовсе (OQ-A4) - названная граница. ЧЕГО " +
                "ИНСТРУМЕНТ НЕ ДЕЛАЕТ И ГОВОРИТ ПРЯМО: тело-инструмент вырезания по траектории не задаётся (OQ-A2), " +
                "форма сечения по законам не задаётся (OQ-A3), разрыв траектории не «залечивается» - непрерывность " +
                "обязан проверять вызывающий до построения.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("path_ref", Sch.Ref("#/$defs/reference")),
                    ("shift_mode", Sch.Nullable(Sch.Enum(
                        "Тип движения сечения по траектории: parallel — параллельно самому себе (0); " +
                        "keep_angle — сохраняет исходный угол с направляющей (1); orthogonal — " +
                        "ортогонально направляющей (2). Значения документированы страницей " +
                        "ksbaseevolutiondefinition_sketchshifttype.html. Без поля — orthogonal. На " +
                        "прямой траектории parallel и orthogonal неразличимы; различать их нужно на дуге.",
                        "parallel", "keep_angle", "orthogonal"))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                                                "Аналитическое ожидание объёма детали после операции, мм³. Для прямой траектории и " +
                        "постоянного профиля это S × L. С ним проверка численная, без него — только число тел и " +
                        "направление изменения." + DeclaredExpectationRule,
                        0d, 1e18d))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.Sweep,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_loft", "Элемент по сечениям",
                "Новое тело по двум и более сечениям; порядок массива - порядок соединения. " +
                "ЦЕПОЧКИ СООТВЕТСТВИЯ СЕЧЕНИЙ задаются параметром couplings, и явное соответствие " +
                "ЗАМЕЩАЕТ автоматическое: цепочка со смещениями 0 / 0 даёт то же тело, что и без " +
                "цепочки, а смещение точки сечения тело меняет. Наружу выходит СМЕЩЕНИЕ вдоль контура " +
                "(PositionOffset, мм), а не координаты точки: поданная точка проецируется на контур и " +
                "читается обратно в локальных координатах эскиза. Элемент создаётся ПРИКЛЕЕННЫМ типом: " +
                "базового типа документированный маршрут не предлагает. " +
                "ОБЪЁМ СЧИТАЕТСЯ ПО ФОРМУЛЕ УСЕЧЁННОЙ ПИРАМИДЫ V = h/3·(A₁ + A₂ + √(A₁A₂)), а не " +
                "«средней площадью × высота» - это важно для expected_volume_mm3. " +
                "ПОРЯДОК СЕЧЕНИЙ ОБЪЁМ НЕ РАЗЛИЧАЕТ при концентрических параллельных сечениях, " +
                "поэтому «порядок соблюдён» доказывается различающей постановкой - разной формой, " +
                "поворотом сечений или габаритом; без неё это названо в unverified_aspects, а не " +
                "выдано за проверенное. " +
                "ПАРАЛЛЕЛЬНОСТЬ ПЛОСКОСТЕЙ СЕЧЕНИЙ проверяет вызывающая сторона: сам loft её не " +
                "требует и не запрещает, поэтому отказ на непараллельные плоскости приходит " +
                "именованным кодом. " +
                "Границы, названные прямо: все сечения плоские; все замкнуты ИЛИ все разомкнуты; " +
                "первое и последнее может быть точкой эскиза при замкнутых остальных. Тонкая стенка " +
                "не выражается. Условия на концах, направляющие кривые, ось и купол не " +
                "поддерживаются; неподдержанное значение building отвергается до COM, а не " +
                "приводится к auto молча.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("section_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Сечения В ПОРЯДКЕ соединения: эскизы, контуры, пространственные кривые или " +
                        "грани. Не менее двух: по одному сечению тело не строится. Порядок массива и " +
                        "есть порядок соединения, и сервер его не переупорядочивает.",
                        2, 64)),
                    ("building", Sch.Nullable(Sch.Enum(
                        "Способ построения у крайних сечений. Поддержан только auto: другое значение " +
                        "отвергается до COM, а не подменяется молча. Без поля - auto.",
                        "auto", "by_normal", "by_object", "cupola"))),
                    ("closed", Sch.Nullable(Sch.Bool(
                        "Замкнуть траекторию соединения сечений. Это НЕ «замкнутая оболочка»: " +
                        "замыкается траектория, а не тело. Без поля - false.",
                        false))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма детали после операции, мм³, по формуле " +
                        "усечённой пирамиды V = h/3·(A₁ + A₂ + √(A₁A₂)). С ним проверка численная, " +
                        "без него - только число тел и изменение объёма." + DeclaredExpectationRule,
                        0d, 1e18d))),
                    ("couplings", Sch.Nullable(Sch.Described(
                        (JsonObject)CouplingsSchema.DeepClone(),
                        "Цепочки соответствия сечений. " +
                        (string)CouplingsSchema["description"]!))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.Loft,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_shell", "Оболочка",
                                "Оболочка постоянной толщины с удалением выбранных граней (SM-13). Маршрут - API5 " +
                "o3d_shellOperation=43 → ksShellDefinition. ПУСТОЙ СПИСОК ГРАНЕЙ НЕ ДАЁТ ЗАМКНУТОЙ ОБОЛОЧКИ, И ЭТО " +
                "ИЗМЕРЕНО НА ОБОИХ API: Update() = True означает «принято», а неизменное число граней - «не " +
                "применено». Поэтому пустой список отвергается до COM именованным отказом, а не выдаётся за " +
                "замкнутую оболочку. НАПРАВЛЕНИЕ СТЕНКИ ЗАДАЁТСЯ ЗНАЧЕНИЕМ, И СООТВЕТСТВИЕ ПОДТВЕРЖДЕНО ДВАЖДЫ - " +
                "документацией (ksshelldefinition_thintype.html: «TRUE - внутрь, FALSE - наружу») и измерением на " +
                "обоих API: сторона различается ОБЪЁМОМ, а не текстом ответа, а толщина проверяется вторым числом. " +
                "tangent_faces ОБЪЯВЛЕН В КОНТРАКТЕ, НО true ОТВЕРГАЕТСЯ: у API5 ksShellDefinition члена " +
                "«касательные грани» нет вовсе, а маршрут инструмента - API5; false принимается. Так параметр виден " +
                "вызывающему, и ни один вызов не получает успех за работу, которой не было. Разные толщины " +
                "отдельных граней вне выпуска (SM-13.shell.mode_variable_thickness, explicitly_out_of_scope); путь " +
                "получения IMultiThicknessGroupsManager остаётся открытым вопросом OQ-A13.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("face_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Грани, снимаемые перед образованием стенки — ссылки face: из " +
                        "kompas_read_topology. Список обязан быть НЕПУСТЫМ: пустой не даёт замкнутой " +
                        "оболочки (измерено на обоих API), и такой вызов отвергается до COM. Позиции " +
                        "в коллекции не принимаются — они не постоянные идентификаторы.",
                        1, 64)),
                    ("thickness_mm", Sch.PositiveMm("Толщина стенки")),
                    // The NAME of this field is a defect fix, not style: the command record
                    // (`ShellCommand`) carries `ThinDirection` (`thin_direction` on the wire). The Host
                    // passes arguments AS IS and the Worker binds by snake_case, so the former
                    // `direction` never reached the command, and `thin_direction` was FORBIDDEN.
                    // History: docs/decisions/host.md#thin-direction
                    ("thin_direction", Sch.Nullable(Sch.Enum(
                        "Направление формирования стенки: inward — материал внутрь (thinType = true, " +
                        "измерено 21632 при t = 2); outward — наружу (false, 24832). Соответствие " +
                        "подтверждено и документацией, и измерением на обоих API. Без поля — inward. " +
                        "ИМЯ ПОЛЯ совпадает с именем, которое связывает команда Worker " +
                        "(thin_direction), а не с прежним `direction`: прежнее имя до команды не " +
                        "доходило, и это измерено пробой scratch/b5-shell-direction.py.",
                        "inward", "outward"))),
                    ("tangent_faces", Sch.Nullable(Sch.Bool(
                        "Касательные грани — IShell.SetFaces(Faces, TangentFaces) в API7. Параметр " +
                        "объявлен, но маршрут этого инструмента — API5, где члена «касательные грани» " +
                        "нет вовсе: значение true отвергается именованным отказом, а не игнорируется " +
                        "молча. Принимается только false. Без поля — false.", false))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                                                "Аналитическое ожидание объёма детали после операции, мм³. С ним проверка численная, без " +
                        "него — только направление изменения и число граней. СНЯТА ОБЯЗАНА БЫТЬ ЗАПРОШЕННАЯ ГРАНЬ: " +
                        "если в её плоскости тело по-прежнему несёт грани суммарной площадью не меньше запрошенной " +
                        "— отказ GEOMETRY_FAILED (details.code = shell_face_not_removed) со ссылкой на оставшийся " +
                        "признак. Перенос на другую грань объёмом неразличим на теле с равнозначными гранями." +
                        DeclaredExpectationRule, 0d, 1e18d))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.Shell,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_draft", "Уклон от основания",
                "Уклон плоских граней тела от основания (SM-12). Маршрут - API5 NewEntity(o3d_incline=42) → " +
                "ksInclineDefinition; основание - плоская грань тела ЛИБО плоскость, ровно одна форма. " +
                "СТОРОНА ЗАДАЁТСЯ ОТНОСИТЕЛЬНО ТЕЛА: outward добавляет материал, inward убирает. Собственный флаг " +
                "продукта direction отсчитывается от нормали ОБЪЕКТА ОСНОВАНИЯ, поэтому одно и то же значение на " +
                "плоскости и на грани даёт противоположный результат: адаптер вычисляет флаг из стороны и " +
                "ориентации основания, а не передаёт слово вызывающего, и умолчания у поля нет. УГОЛ - В ГРАДУСАХ, " +
                "0 < |angle_deg| < 90; это граница контракта (0° ничего не делает, 90° вырождает грань), а не " +
                "утверждение об API. НЕСКОЛЬКО ГРАНЕЙ С ОДНИМ УГЛОМ - ОДИН ПРИЗНАК, а не признак на каждую. " +
                "Create() = true при неизменном объёме - ОТКАЗ; исключение одно - основание посередине тела, где " +
                "клинья компенсируются по построению, и там подтверждает объявленный объём. Многотельный контекст " +
                "отвергается: профиль drafts-minimal-v1 однотельный.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("face_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Уклоняемые плоские грани ОДНОГО тела с одним углом: ссылки face: из " +
                        "kompas_read_topology. Список обязан быть НЕПУСТЫМ и без повторов; неплоская грань " +
                        "отвергается до COM, потому что её плоскость читается до мутации.",
                        1, 64)),
                    ("base_face_ref", Sch.Described(
                        Sch.Nullable(Sch.Ref("#/$defs/reference")),
                        "Основание - плоская грань тела (ссылка face:). Взаимоисключающе с base_plane: обе " +
                        "формы сразу или ни одной - INVALID_ARGUMENT до COM.")),
                    ("base_plane", Sch.Described(
                        Sch.Nullable(PlaneSchema(
                            "Основание - плоскость в той же форме, что опора эскиза: base (xy/xz/yz, при " +
                            "необходимости со offset_mm) ЛИБО reference на плоскость вида plane. Плоскость, " +
                            "параллельная уклоняемой грани, отвергается ядром.")),
                        "Основание - плоскость. Взаимоисключающе с base_face_ref.")),
                    ("angle_deg", Sch.Num(
                        "Угол уклона в ГРАДУСАХ. Единица измерена: 10° на эталонном брусе даёт 382367.30 " +
                        "мм³ (inward) и 417632.70 (outward), радианный вариант дал бы 335163.92 и не " +
                        "совпал ни разу. Допустимо 0 < |angle_deg| < 90; 0, ±90, NaN и бесконечность " +
                        "отвергаются до COM.", -90d, 90d)),
                    ("direction", Sch.Enum(
                        "Сторона уклона ОТНОСИТЕЛЬНО ТЕЛА: outward - материал добавляется, inward - убирается. " +
                        "Обязательное поле, умолчания нет: собственное умолчание продукта на плоскости и на " +
                        "грани означает разное.",
                        "outward", "inward")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма детали после операции, мм³. С ним проверка численная, " +
                        "без него - только изменение объёма и число граней." + DeclaredExpectationRule,
                        0d, 1e18d))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.Draft,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_draft_from_edges", "Уклон от базовой линии",
                "Уклон граней от базовой линии (SM-12). Маршрут ТОЛЬКО API7: IDraftsFromEdges.Add → " +
                "IDraftFromEdges (BaseLine, DirectionObject, SideFace, Angle, Reverse) → Update; определения " +
                "этого типа в API5 нет, и маршрут API5 для него не пробуется. БАЗОВАЯ ЛИНИЯ - ребро или цепочка " +
                "рёбер: она задаёт начальную границу уклоняемой грани. НАПРАВЛЕНИЕ - плоскость (direction_plane) " +
                "либо ссылка на плоскую грань, прямолинейное ребро или ось (direction_ref); поверхность вращения " +
                "в профиль drafts-minimal-v1 не входит и отвергается CAPABILITY_UNAVAILABLE с названием причины. " +
                "СТОРОНА (side_face_ref) необязательна: без неё сторону выбирает система, и какую грань она взяла, " +
                "этим маршрутом не читается - это названо в unverified_aspects, а не выдано за известное. " +
                "reverse - смена направления; в ответе значение указано явно. Угол в градусах, как у kompas_draft.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("base_line_edge_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Рёбра базовой линии: одно ребро или цепочка. Ссылки edge: из kompas_read_topology. " +
                        "Список обязан быть НЕПУСТЫМ - пустая базовая линия отвергается до COM.",
                        1, 64)),
                    ("direction_ref", Sch.Described(
                        Sch.Nullable(Sch.Ref("#/$defs/reference")),
                        "Направляющий объект по ссылке: плоская грань, прямолинейное ребро или ось. " +
                        "Взаимоисключающе с direction_plane.")),
                    ("direction_plane", Sch.Described(
                        Sch.Nullable(PlaneSchema(
                            "Направляющий объект - плоскость в форме опоры эскиза: base (xy/xz/yz со " +
                            "offset_mm) ЛИБО reference на плоскость вида plane.")),
                        "Направляющий объект - плоскость. Взаимоисключающе с direction_ref.")),
                    ("side_face_ref", Sch.Described(
                        Sch.Nullable(Sch.Ref("#/$defs/reference")),
                        "Грань, задающая сторону уклона; обязана прилегать к базовой линии. Не задана - " +
                        "сторону выбирает система, и выбранная грань не читается.")),
                    ("angle_deg", Sch.Num(
                        "Угол уклона в ГРАДУСАХ; 0 < |angle_deg| < 90, как у kompas_draft. Отсчёт - от " +
                        "проекции направляющего объекта на плоскость сечения поверхности уклона.",
                        -90d, 90d)),
                    ("reverse", Sch.Nullable(Sch.Bool(
                        "Сменить направление (Reverse признака). Без поля - false; значение, которое " +
                        "применилось, называется в ответе.", false))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма детали после операции, мм³. С ним проверка численная, " +
                        "без него - только изменение объёма и число граней." + DeclaredExpectationRule,
                        0d, 1e18d))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.DraftFromEdges,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_pattern_grid", "Массив по сетке",
                                "Массив экземпляров в узлах параллелограммной сетки. ЧТО КОПИРУЕТСЯ, решает copy_kind, и это два " +
                "вида массива, а не флаг поведения: operations - копии наследуют область применения операции и " +
                "новых тел НЕ появляется; bodies - копируются тела, число тел растёт. ВТОРОЕ НАПРАВЛЕНИЕ " +
                "НЕОБЯЗАТЕЛЬНО: без axis2_point1_mm, axis2_point2_mm и count2 массив идёт по одной линии, count2 = " +
                "1. УГЛЫ НАЗЫВАЮТ НАПРАВЛЕНИЕ: angle2_deg - угол МЕЖДУ направлениями (прямоугольная сетка - 90; при " +
                "0 сетка вырождается в линию; 270 модель приводит к 180), angle1_deg наклоняет ПЕРВУЮ ось, поэтому " +
                "прямоугольность задаётся взаимной перпендикулярностью осей и angle2_deg; оба угла не заданы - " +
                "значения модели остаются (0 и 90). ОСЬ ЗАДАЁТСЯ ДВУМЯ ТОЧКАМИ МОДЕЛИ той же детали; ВЕКТОР " +
                "НАПРАВЛЕНИЯ НЕ ПРИНИМАЕТСЯ - названное ограничение, а не умолчание. geometry_pattern=true " +
                "ОТВЕРГАЕТСЯ CAPABILITY_UNAVAILABLE: документирован КОМПАСом, но продуктом не проверен. УСПЕХ - не " +
                "факт записи: параметры перечитываются, сверяются число экземпляров, объём и число тел. При " +
                "expected_hole_radius_mm и expected_hole_height_mm дополнительно читаются оси цилиндрических граней " +
                "- проверка ПО КАЖДОМУ ЭКЗЕМПЛЯРУ, потому что объём не отличает четыре отверстия от трёх и одного " +
                "наложенного. Объём сверяется с expected_volume_mm3 в ЕДИНОМ допуске проекта (1e-3 мм³ или 1e-9 " +
                "относительных, берётся больший), несовпадение - ОТКАЗ; счётные величины сверяются ТОЧНО.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("copy_kind", Sch.Enum(
                        "Что копируется. operations - массив ОПЕРАЦИЙ: копии наследуют область применения исходной операции, " +
                        "новых тел не появляется. bodies - массив ТЕЛ: копируются тела, число тел растёт. Это два разных вида " +
                        "массива, а не флаг поведения.",
                        "operations", "bodies")),
                    ("source_refs", Sch.Arr(Sch.Ref("#/$defs/reference"),
                        "Исходные объекты массива: feature:-ссылки при copy_kind=operations, body:-ссылки при copy_kind=bodies. " +
                        "Пустой список отвергается до обращения к КОМПАС: массив без исходных объектов не собирается. Смешивать " +
                        "виды нельзя - вид массива выбирается один.", 1)),
                    ("axis1_point1_mm", Sch.Vec3("Первая точка оси первого направления, координаты МОДЕЛИ, мм.")),
                    ("axis1_point2_mm", Sch.Vec3("Вторая точка оси первого направления. Точки обязаны различаться.")),
                    ("step1_mm", Sch.PositiveMm("Шаг копирования по первой оси",
                        "Шаг откладывается вдоль первой оси от исходного объекта: крайние экземпляры разнесены на шаг, умноженный " +
                        "на count1 минус 1.")),
                    ("count1", Sch.Int("Число экземпляров по первой оси, включая исходный. Не меньше 1.", 1, 100_000)),
                    ("angle1_deg", Sch.Nullable(Sch.Num(
                        "Угол наклона первой оси сетки, ГРАДУСЫ. Не задан - значение модели остаётся как есть (0), то есть ось " +
                        "берётся как построена. 90 уводит направление С построенной оси, поэтому прямоугольность задаётся не этим " +
                        "углом, а взаимной перпендикулярностью осей и angle2_deg.", -360d, 360d))),
                    ("direction1", Sch.Nullable(Sch.Bool(
                        "Направление копирования вдоль первой оси. По умолчанию true."))),
                    ("boundary_instances_step_factor1", Sch.Nullable(Sch.Bool(
                        "Интерпретация значения шага на границе первого направления. По умолчанию false."))),
                    ("axis2_point1_mm", Sch.Nullable(Sch.Vec3(
                        "Первая точка оси ВТОРОГО направления. Не задана — массив по одной линии."))),
                    ("axis2_point2_mm", Sch.Nullable(Sch.Vec3("Вторая точка оси второго направления."))),
                    ("step2_mm", Sch.Nullable(Sch.Num(
                        "Шаг по второму направлению, мм. Обязателен, когда count2 больше 1.", 0d, 1e9d))),
                    ("count2", Sch.Nullable(Sch.Int(
                        "Число экземпляров по второму направлению. Не задано — направление не участвует.", 1, 100_000))),
                    ("angle2_deg", Sch.Nullable(Sch.Num(
                        "Угол МЕЖДУ направлениями сетки, ГРАДУСЫ. Прямоугольная сетка - 90; 0 совмещает второе направление с " +
                        "первым и вырождает сетку в линию, а копии продолжают первую ось; 270 модель приводит к 180 и уводит " +
                        "второе направление в противоположную сторону. Не задан - значение модели остаётся как есть (90).", -360d, 360d))),
                    ("direction2", Sch.Nullable(Sch.Bool("Направление копирования вдоль второй оси."))),
                    ("boundary_instances_step_factor2", Sch.Nullable(Sch.Bool(
                        "Интерпретация шага на границе второго направления."))),
                    ("building_type", Sch.Nullable(Sch.Enum(
                        "Способ построения массива: save_all - сохранять все экземпляры, save_along_perimeter и " +
                        "save_along_axially - сохранять по периметру либо по оси, chess_order_by_axis1 и chess_order_by_axis2 - " +
                        "шахматный порядок по первой либо по второй оси.",
                        "save_all", "save_along_perimeter", "save_along_axially",
                        "chess_order_by_axis1", "chess_order_by_axis2"))),
                    ("geometry_pattern", Sch.Nullable(Sch.Bool(
                        "Геометрическое копирование. true ОТВЕРГАЕТСЯ CAPABILITY_UNAVAILABLE: режим документирован КОМПАСом, но " +
                        "продуктом не проверен, и записывать непроверенную настройку нельзя."))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма документа после операции, мм³. С ним объём сверяется числом в едином " +
                        "допуске проекта, без него численная проверка объёма не выполняется." + DeclaredExpectationRule, 0d, 1e18d))),
                    ("expected_body_count", Sch.Nullable(Sch.Int(
                        "Ожидаемое число тел после операции. Для массива операций оно равно числу тел до операции, для массива " +
                        "тел - растёт. Сравнение точное.", 0, 100_000))),
                    ("expected_hole_radius_mm", Sch.Nullable(Sch.Num(
                        "Радиус цилиндрической грани-экземпляра, мм (Ø10 → 5). Без него поимённая " +
                        "проверка экземпляров не выполняется, и это честно помечается в ответе.", 0d, 1e6d))),
                    ("expected_hole_height_mm", Sch.Nullable(Sch.Num(
                        "Высота цилиндрической грани-экземпляра, мм (толщина пластины для сквозного " +
                        "отверстия).", 0d, 1e6d))),
                    ("expected_hole_count", Sch.Nullable(Sch.Int(
                        "Аналитическое ожидание числа экземпляров-отверстий. Точное совпадение.", 0, 100_000))),
                    ("expected_hole_centers_mm", Sch.Nullable(Sch.Arr(
                        Sch.Arr(Sch.Num("Компонента координаты, мм."),
                            "Точка оси экземпляра. Ровно три координаты [x, y, z], мм.", 3, 3),
                        "Аналитические координаты осей каждого экземпляра в координатах модели, мм. Объём не отличает четыре " +
                        "отверстия от трёх и одного наложенного - этот набор отличает.")))),
                WorkerCommands.PatternGrid,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_pattern_circular", "Массив по концентрической сетке",
                                "Массив по концентрической сетке (SM-19). Маршрут: " +
                "IModelContainer.FeaturePatterns.Add(o3d_circularCopy=36) → QI(ICircularPattern) → запись " +
                "параметров → Update(). СООТНЕСЕНИЕ НАПРАВЛЕНИЙ ВЗЯТО СО СТРАНИЦЫ СПРАВКИ " +
                "icircularpattern_props.html, А НЕ ИЗ ОЖИДАНИЯ: Count1 и Step1 - РАДИАЛЬНОЕ направление (шаг в мм), " +
                "Count2 и Step2 - КОЛЬЦЕВОЕ (угловой шаг в ГРАДУСАХ). Поэтому кольцевое направление - пара (count2, " +
                "step2_deg), а не (count1, step1_mm). ПОЛНЫЙ ОБОРОТ ВЫРАЖАЕТСЯ ПАРОЙ (count2, step2_deg): " +
                "отдельного признака полного оборота у ICircularPattern нет ни в справке, ни в интероп-сборке, а " +
                "ожидаемая семантика - step2_deg = 360 / count2. save_initial_orientation есть ТОЛЬКО у кругового " +
                "массива (у ILinearPattern этого члена нет) и по умолчанию доворачивает экземпляры до радиального " +
                "направления (false). Шаг вдоль оси (step_by_axis_mm) и reverse_direction документированы той же " +
                "страницей. geometry_pattern=true ОТВЕРГАЕТСЯ CAPABILITY_UNAVAILABLE. Успех - не Update(): " +
                "параметры перечитываются, сверяются число экземпляров, объём документа и число тел, а при заданных " +
                "expected_hole_* - оси цилиндрических граней по КАЖДОМУ экземпляру.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("copy_kind", Sch.Enum(
                        "operations — o3d_circularCopy=36 (новых тел не появляется); " +
                        "bodies — o3d_BodiesCircularCopy=529 (копируются тела).",
                        "operations", "bodies")),
                    ("source_refs", Sch.Arr(Sch.Ref("#/$defs/reference"),
                        "Исходные объекты массива: feature:-ссылки при operations, body:-ссылки при bodies.", 1)),
                    ("axis_point1_mm", Sch.Vec3("Первая точка оси массива в координатах МОДЕЛИ, мм.")),
                    ("axis_point2_mm", Sch.Vec3("Вторая точка оси массива. Точки обязаны различаться.")),
                    ("count1", Sch.Nullable(Sch.Int(
                        "Число экземпляров в РАДИАЛЬНОМ направлении (Count1 по странице справки). " +
                        "Не задано — 1.", 1, 100_000))),
                    ("step1_mm", Sch.Nullable(Sch.Num(
                        "Шаг в РАДИАЛЬНОМ направлении, мм. Действует, когда count1 больше 1.", 0d, 1e9d))),
                    ("count2", Sch.Int(
                        "Число экземпляров в КОЛЬЦЕВОМ направлении (Count2).", 1, 100_000)),
                    ("step2_deg", Sch.Num(
                        "УГЛОВОЙ шаг в КОЛЬЦЕВОМ направлении, ГРАДУСЫ. Единица названа самой страницей " +
                        "справки («Step2 — Угловой шаг (градусы)»). Калибровочная проба обязательна: при " +
                        "трактовке «радианы» расхождение 57,3 раза, и шаг 90 рад неотличим от одной " +
                        "позиции. Полный оборот — step2_deg = 360 / count2.", 0d, 360d)),
                    ("step_by_axis_mm", Sch.Nullable(Sch.Num(
                        "Шаг вдоль оси массива (StepByAxis), мм. Ненулевое значение даёт смещение " +
                        "экземпляров вдоль оси.", -1e9d, 1e9d))),
                    ("boundary_instances_step_factor1", Sch.Nullable(Sch.Bool(
                        "Интерпретация шага на границе РАДИАЛЬНОГО направления."))),
                    ("boundary_instances_step_factor2", Sch.Nullable(Sch.Bool(
                        "Интерпретация шага на границе КОЛЬЦЕВОГО направления."))),
                    ("reverse_direction", Sch.Nullable(Sch.Bool("Направление построения массива."))),
                    ("save_initial_orientation", Sch.Nullable(Sch.Bool(
                        "Ориентация экземпляров (SaveInitialOrientation). false - экземпляры " +
                        "доворачиваются до радиального направления (поворот вокруг оси массива); " +
                        "true - экземпляры сохраняют ориентацию исходного объекта (только перенос). " +
                        "По умолчанию false. Для несимметричного источника (паз, впадина зуба) " +
                        "значения дают разную геометрию. Член есть только у кругового массива."))),
                    ("building_type", Sch.Nullable(Sch.Enum(
                        "Способ построения, ksCircularPatternBuildingTypeEnum.",
                        "save_all", "chess_order_by_axis1", "chess_order_by_axis2"))),
                    ("geometry_pattern", Sch.Nullable(Sch.Bool(
                        "Геометрическое копирование. true отвергается CAPABILITY_UNAVAILABLE."))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                                                "Аналитическое ожидание объёма документа, мм³." + DeclaredExpectationRule, 0d, 1e18d))),
                    ("expected_body_count", Sch.Nullable(Sch.Int("Ожидаемое число тел после операции.", 0, 100_000))),
                    ("expected_hole_radius_mm", Sch.Nullable(Sch.Num("Радиус цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_height_mm", Sch.Nullable(Sch.Num("Высота цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_count", Sch.Nullable(Sch.Int("Ожидаемое число экземпляров-отверстий.", 0, 100_000))),
                    ("expected_hole_centers_mm", Sch.Nullable(Sch.Arr(
                        Sch.Arr(Sch.Num("Компонента координаты, мм."),
                            "Точка оси экземпляра. Ровно три координаты [x, y, z], мм.", 3, 3),
                        "Аналитические координаты осей каждого экземпляра, мм.")))),
                WorkerCommands.PatternCircular,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_pattern_mirror", "Зеркальный массив",
                                "Зеркальное отражение операций либо тел относительно ПЛОСКОСТИ (SM-23). Маршрут: " +
                "IModelContainer.FeaturePatterns.Add(o3d_mirrorOperation=48 либо o3d_mirrorAllOperation=49) → " +
                "QI(IMirrorPattern). У второго вида дополнительно ставится IChooseBodies7 (ChooseBodiesType, " +
                "Bodies). ПЛОСКОСТЬ - ЯВНЫЙ ОБЪЕКТ, а не текущее выделение окна: это требование dep.refs.planes, и " +
                "оно же снимает вопрос о знаке нормали - сторону отражения задаёт плоскость, а не порядок " +
                "выделения. Знак нормали YOZ (в −X) остаётся фактом модели и живёт в контракте плоскости. " +
                "СОХРАННОСТЬ ИСХОДНИКА ПРОВЕРЯЕТСЯ ИЗМЕРЕНИЕМ: save_initial_objects=true добавляет отражённую " +
                "копию, false ЗАМЕНЯЕТ исходник ею; обе половины различаются в одной постановке. ПРОПУСКОВ " +
                "ЭКЗЕМПЛЯРОВ У ЗЕРКАЛЬНОГО МАССИВА НЕ БЫВАЕТ: пользовательская справка glava_48_obzhie_svedeniy " +
                "прямо говорит, что исключение экземпляров недоступно для зеркального массива и массива по образцу. " +
                "Успех - не Update(): параметры перечитываются, объём и число тел сверяются с аналитикой, а на " +
                "многотельной детали возвращаются поимённые снимки тел и список тел, НЕ тронутых ни исходником, ни " +
                "отражением: «суммарный объём вырос вдвое» не отличает удвоение тел от удвоения одного тела.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mode", Sch.Enum(
                        "selected_operations — o3d_mirrorOperation=48, отражение ЯВНО выбранных операций; " +
                        "all_bodies — o3d_mirrorAllOperation=49, «зеркально отразить все», выбор тел " +
                        "идёт через IChooseBodies7.",
                        "selected_operations", "all_bodies")),
                    ("source_refs", Sch.Arr(Sch.Ref("#/$defs/reference"),
                        "Исходные объекты: feature:-ссылки для selected_operations (непустой список); " +
                        "для all_bodies — либо пусто («все тела»), либо явные body:-ссылки.", 0)),
                    ("plane", Sch.Ref("#/$defs/cut_plane")),
                    ("save_initial_objects", Sch.Bool(
                        "Сохранять исходные объекты (SaveInitialObjects). true — исходник остаётся и " +
                        "добавляется отражённая копия; false — исходник ЗАМЕНЯЕТСЯ отражённой копией. " +
                        "Справка imirrorpattern_saveinitialobjects.html ограничивает свойство ТОЛЬКО " +
                        "операцией o3d_mirrorAllOperation: у остальных операций зеркального " +
                        "копирования возможность скрыть экземпляры отсутствует, и измерение это " +
                        "подтверждает — при mode=selected_operations запись false читается обратно " +
                        "как true и геометрия не меняется. Половины различаются измерением только " +
                        "при mode=all_bodies.")),
                    ("choose_bodies_type", Sch.Nullable(Sch.Enum(
                        "Тип действия над телами для IChooseBodies7.ChooseBodiesType, ksChooseBodiesType. " +
                        "Действует только при mode=all_bodies.",
                        "new_body", "automatic", "manual", "all_bodies"))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                                                "Аналитическое ожидание объёма документа, мм³. При mode=all_bodies измеряется по КАЖДОМУ " +
                        "телу, а не суммой: у save_initial_objects=true исходники остаются и добавляются " +
                        "отражённые, у false исходники заменяются отражёнными. Суммарный объём при зеркале один и " +
                        "тот же, поэтому одноё суммы недостаточно. При mode=selected_operations половины НЕ " +
                        "различаются — справка ограничивает свойство только o3d_mirrorAllOperation." + DeclaredExpectationRule,
                        0d, 1e18d))),
                    ("expected_body_count", Sch.Nullable(Sch.Int("Ожидаемое число тел после операции.", 0, 100_000))),
                    ("expected_hole_radius_mm", Sch.Nullable(Sch.Num("Радиус цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_height_mm", Sch.Nullable(Sch.Num("Высота цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_count", Sch.Nullable(Sch.Int("Ожидаемое число экземпляров-отверстий.", 0, 100_000))),
                    ("expected_hole_centers_mm", Sch.Nullable(Sch.Arr(
                        Sch.Arr(Sch.Num("Компонента координаты, мм."),
                            "Точка оси экземпляра. Ровно три координаты [x, y, z], мм.", 3, 3),
                        "Аналитические координаты осей каждого экземпляра, мм.")))),
                WorkerCommands.PatternMirror,
                requiresDocument: false,
                requiresRevision: true),

            ReadOnly("kompas_get_pattern", "Параметры массива",
                "Что сервер видит в существующем признаке массива либо зеркала: семейство (по ответу на " +
                "QI, а не по памяти вызывающего), направления, шаги, количества, углы, способ построения, " +
                "флаги границы, ось/плоскость, SaveInitialObjects, число исходных объектов и число " +
                "удалённых экземпляров, имя владельца и updateStamp. " +
                "СООТНЕСЕНИЕ НАПРАВЛЕНИЙ — со страницы справки icircularpattern_props.html: Count1/Step1 " +
                "радиальные, Count2/Step2 кольцевые, причём Step2 подписан как угловой шаг в градусах. " +
                "Пустое поле означает «не прочитано», а не «ноль»: GetExemplarsCounts возвращает false на " +
                "непостроенном признаке, и это разные ответы. " +
                "У зеркального массива поле пропусков экземпляров читается, но объявляется НЕПРИМЕНИМЫМ " +
                "со ссылкой на glava_48_obzhie_svedeniy: исключение экземпляров для зеркального массива " +
                "недоступно. Это предметная неприменимость с источником, а не пустая клетка. " +
                "Признак сопоставляется с элементом IModelContainer.FeaturePatterns по имени владельца, " +
                "а не по индексу: КОМПАС переставляет элементы коллекции. Несопоставленный признак — это " +
                "названная причина, а не отказ всего чтения.",
                Sch.Props(("feature_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.PatternRead),

            Mutation("kompas_boolean", "Булева операция над телами",
                "Объединение, разность или пересечение тел с ЯВНОЙ целью и ЯВНЫМ набором инструментов " +
                "(docs/05 SM-15). Адресация — только ссылками из kompas_list_bodies: ни «текущее окно», " +
                "ни индекс 0, ни порядок коллекции целью не являются. " +
                "Маршрут измерен 18.09.2026 (проба --boolean, прогон b10ffb70b7d24b6497417bc6639581b1, " +
                "PASS 13 · FAIL 0): IModelContainer.Booleans.Add() → IBoolean, поля BaseObject, " +
                "ModifyObjects, BooleanType, SaveCopyModifyObjects, затем Update(). " +
                "РАЗНОСТЬ = ЦЕЛЬ МИНУС ИНСТРУМЕНТЫ (измерено на эталоне: A=[0,40]×[0,30]×[0,20] минус " +
                "B=[20,60]×[0,30]×[0,20] даёт 12000 мм³ с габаритом (0,0,0)…(20,30,20)). " +
                "keep_tools=true оставляет инструменты отдельными телами на прежнем месте: это ДРУГАЯ " +
                "величина, чем объём объединения, и в ответе они не смешиваются. " +
                "ЧЕГО ЯДРО НЕ ДЕЛАЕТ и потому делает сервер: повтор ссылки ядро принимает МОЛЧА и " +
                "потребляет тело дважды, поэтому повтор, цель среди инструментов и ссылка из другой " +
                "детали отвергаются до обращения к КОМПАС. " +
                "ГРАНИЦЫ ЯДРА измерены: контакт по грани и вложенность принимаются, а касание по ребру, " +
                "касание в точке и несвязные тела ОТВЕРГАЮТСЯ (Update()=false) — это честный отказ, а не " +
                "сбой сервера. Результат из нескольких кусков ядро представляет ОДНИМ телом с несколькими " +
                "кусками (multi_body_parts=true), а не телами по числу кусков; поэтому ответ сообщает " +
                "ФАКТИЧЕСКИЙ состав, а не обещанный. Успешный Update() доказательством не считается: " +
                "состав и объёмы перечитываются с модели.",
                Sch.Props(
                    ("target_body_ref", Sch.Ref("#/$defs/reference")),
                    ("tool_body_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Тела-инструменты из kompas_list_bodies. Непустой список, без повторов и без тела-цели.",
                        1, 64, unique: true)),
                    ("operation", Sch.Enum(
                        "union — объединение; difference — цель минус инструменты; intersect — пересечение.",
                        "union", "difference", "intersect")),
                    ("keep_tools", Sch.Bool(
                        "ОБЯЗАТЕЛЬНОЕ поле: политика сохранения инструментов задаётся явно, умолчания " +
                        "нет. true — инструменты остаются отдельными телами на прежнем месте " +
                        "(IBoolean.SaveCopyModifyObjects); false — потребляются операцией. " +
                        "Копия цели не поддерживается: это отдельный режим вне обязательного объёма.")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание СУММАРНОГО объёма тел документа после операции, мм³ "
                        + "(не объём тела-результата: он публикуется отдельной проверкой "
                        + "result_body_volume и служит наблюдением, а не ожиданием)." + DeclaredExpectationRule,
                        0d, 1e18d))),
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.SolidBoolean,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_split", "Разделить тело плоскостью",
                "Разделение тела плоскостью на части (docs/05 SM-16). Возвращается ПОЛНЫЙ список частей, " +
                "каждая со своей ссылкой. Маршрут измерен 18.09.2026 (проба --split, прогон " +
                "124682af57a242728ea765f1aae4816c, PASS 11 · FAIL 0): IModelContainer.SplitSolids.Add() → " +
                "ISplitSolid с единственным содержательным членом CutObjects, затем Update(). " +
                "Отдельного выбора «какие части сохранить» НЕТ и он не нужен: разделение сохраняет ВСЕ " +
                "части по построению — измерено, что брусок 24000 мм³ при плоскости x=10 даёт тела 6000 " +
                "и 18000, сумма 24000, а постороннее тело остаётся нетронутым. Именно это измерение сняло " +
                "блокировку OQ-A18. Плоскость задаётся точкой и нормалью в МОДЕЛЬНЫХ координатах либо " +
                "ссылкой на существующую плоскость; знак нормали на разделение не влияет (измерено " +
                "перестановкой двух точек построения). Успешный Update() доказательством не считается: " +
                "части перечитываются с модели.",
                Sch.Props(
                    ("target_body_ref", Sch.Ref("#/$defs/reference")),
                    ("plane", Sch.Ref("#/$defs/cut_plane")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание суммарного объёма частей, мм³. НЕ единое правило "
                        + "заявленного ожидания: при создании это признак «сверить сумму частей с "
                        + "исходным телом» (сумма частей равна объёму исходного тела, поэтому сверка "
                        + "прошла бы и на бездействии), а при ПРАВКЕ поле отвергается INVALID_ARGUMENT — "
                        + "для состава частей служит expected_part_volumes_mm3.", 0d, 1e18d))),
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.SolidSplit,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_cut_by_plane", "Отсечь тело по одну сторону плоскости",
                "Отсечение тела плоскостью с ЯВНЫМ выбором оставляемой стороны (docs/05 SM-16). " +
                "Сторона задаётся знаком s = n·(p − p₀), а не словами «левая/правая»: без системы " +
                "координат это не адрес. Соответствие измерено 18.09.2026 (проба --split, шаг SP.7): при " +
                "нормали (1,0,0) и плоскости x=10 keep_side=positive (s>0) оставляет 18000 мм³ с " +
                "габаритом (10,0,0)…(40,30,20), keep_side=negative (s<0) оставляет 6000 мм³ с габаритом " +
                "(0,0,0)…(10,30,20). Маршрут: IModelContainer.Cuts.Add() → ICut с " +
                "BuildingType=ksCutByPlane, CutObject=плоскость, Direction, затем Update(). " +
                "Посторонние тела в обоих случаях остаются нетронутыми.",
                Sch.Props(
                    ("target_body_ref", Sch.Ref("#/$defs/reference")),
                    ("plane", Sch.Ref("#/$defs/cut_plane")),
                    ("keep_side", Sch.Enum(
                        "Оставляемая сторона по знаку s = n·(p − p₀): positive — s>0 (сторона, куда " +
                        "смотрит нормаль), negative — s<0.",
                        "positive", "negative")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма остатка, мм³." + DeclaredExpectationRule, 0d, 1e18d))),
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.SolidCutByPlane,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_reposition", "Перенести или повернуть тело",
                "Перенос тела на вектор и поворот вокруг оси (docs/05 SM-17). Маршрут измерен 18.09.2026 " +
                "(проба --reposition, прогон 929f08886f1348fe921943052a4026b0, PASS 10 · FAIL 0): " +
                "IModelContainer.BodyRepositions.Add() → IBodyReposition, RepositionBody=тело, затем Update(). " +
                "ЗАПИСЬ ИДЁТ ДОКУМЕНТИРОВАННЫМИ ПАРАМЕТРАМИ РАЗМЕЩЕНИЯ, А НЕ МАТРИЦЕЙ (перемерено 19.09.2026, " +
                "проба --reposition-params, прогон a336120926fc4652a8bf737562568271, шаг RP.25): Position.OrientationType " +
                "= ksEulerCorners + LocalCSParameters → ILocalCSEulerParam (тройка углов; порядок спряжения PNR, " +
                "единицы — градусы) и Position.ParameterType = ksPDisplace + Parameters → IPoint3DParamDisplace " +
                "(перенос). Матрица 4×4 раскладывается на ориентацию и перенос; перенос пишется ВСЕГДА, даже " +
                "нулевой, иначе у признака остался бы чужой тип параметров точки. ПОЧЕМУ НЕ МАТРИЦЕЙ: документ " +
                "хранит ПАРАМЕТРЫ ориентации, поэтому матричный вид (InitByMatrix3D) не переживает переоткрытия — " +
                "у записанного поворота после save→close→reopen GetVector(OX) отдаёт (1,0,0) при габарите, " +
                "поворот подтверждающем (RP.16), WriteToFile даёт единичную матрицу (RP.18), и верное чтение " +
                "живёт ровно до следующего открытия (RP.20). " +
                "ВАЖНО И ИЗМЕРЕНО: маршруты из 12 чисел и SetDisplacementByAxis принимаются с Update()=true и " +
                "тело НЕ двигают (OQ-A19). Поэтому сервер перечитывает положение после вызова и сверяет его с " +
                "заданным ПО МАТРИЦЕ, а не по числам: параметризация углами неоднозначна, и равенство тройки " +
                "отвергло бы верную запись. «Успех без движения» возвращается как NO_GEOMETRY_CHANGE, а не как " +
                "выполненная операция. " +
                "Правило знака измерено: правое правило вокруг направления оси, (x,y) → (−y,x) для +90° " +
                "вокруг Z. Точка оси учитывается: поворот +90° вокруг Z через (5,0,0) переводит брусок " +
                "[10,30]×[0,10]×[0,5] в (−5,5,0)…(5,25,5). Ось задаётся ЛИБО axis_direction_mm, ЛИБО " +
                "axis_point2_mm — обе сразу или ни одной это ошибка. Объём и число тел сохраняются, " +
                "меняется положение только выбранного тела. Правка параметра применяется к ИСХОДНЫМ " +
                "входам, а не к текущему положению (измерено: повторная запись того же вектора оставляет " +
                "положение прежним, обнуление возвращает тело домой).",
                Sch.Props(
                    ("target_body_ref", Sch.Ref("#/$defs/reference")),
                    ("kind", Sch.Enum("translate — перенос; rotate — поворот вокруг оси.", "translate", "rotate")),
                    ("vector_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("axis_point_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("axis_direction_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("axis_point2_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("angle_deg", Sch.Nullable(Sch.Num(
                        "Угол поворота в ГРАДУСАХ. Знак — правое правило вокруг направления оси. " +
                        "Обязателен при kind=rotate, запрещён при kind=translate.", -360d, 360d))),
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.SolidReposition,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_get_feature", "Параметры признака",                            "Что сервер видит в определении существующего признака: семейство, имя, стороны выдавливания с условием " +
            "конца и глубиной, тонкая стенка, параметры фаски (катеты, transfer, а когда доступен маршрут чтения " +
            "угла - угол и способ построения), скругление и родное отверстие, параметры ВРАЩЕНИЯ (угол, " +
            "направление, состояние оси, вид операции), состояние (IsValid, excluded, rollback, objectError), " +
            "updateStamp и владелец. Уровень чтения - structure_checked: это перечитывание модели, а не " +
            "доказательство геометрии. ПУСТОЕ ПОЛЕ ОЗНАЧАЕТ «НЕ ПРОЧИТАНО», А НЕ НОЛЬ: ноль вместо неудавшегося " +
            "чтения не подставляется, непрочитанные поля перечислены поимённо с причиной (поле угла фаски пусто, " +
            "когда маршрут чтения недоступен или фасок несколько). Признак вращения сопоставляется с коллекцией " +
            "вращений по СОСТАВУ; при 0 или нескольких совпадениях rotated = null и в unverified_aspects " +
            "добавляется rotation_not_matched - «не сопоставлено», а не «ноль». Несопоставленный признак семейства " +
            "- названная причина solid_feature_not_matched, а не отказ чтения: имя, IsValid и updateStamp читаются " +
            "и без параметров семейства. ЧЕТЫРЕ СЕМЕЙСТВА ОПЕРАЦИЙ НАД ТЕЛАМИ - булева операция, разделение, " +
            "отсечение, изменение положения - лежат в блоке solid и опознаются по типу признака в дереве: " +
            "solid.operation и solid.keep_tools (вид операции, сохранение инструмента телом), solid.plane (опора " +
            "разделения или отсечения: point_mm, единичная нормаль normal_mm и три точки point1_mm…point3_mm, из " +
            "которых нормаль выведена), solid.keep_side (true - сторона нормали). ИЗМЕНЕНИЕ ПОЛОЖЕНИЯ: читаются ВСЕ " +
            "поля преобразования, кроме ТОЧКИ ОСИ. reposition_kind - translate либо rotate; reposition_vector_mm - " +
            "вектор переноса (у поворота null и назван неприменимым); reposition_axis_direction_mm и " +
            "reposition_angle_deg - единичное направление оси и угол в градусах (у переноса null). " +
            "reposition_axis_point_mm НЕ ЧИТАЕТСЯ: всегда null и всегда названо в unreadable_parameters (у поворота " +
            "«не читается», у переноса «неприменимо»), поэтому kompas_update_feature требует точку оси в запросе. " +
            "Признак с матричным способом размещения не читается молча: все пять полей названы непрочитанными, это " +
            "отказ, а не нули (матричный способ не переживает save → close → reopen). При частичном чтении в " +
            "unverified_aspects добавляется solid_params_partly_unreadable. СЕМЕЙСТВА sweep, loft И shell лежат в " +
            "одноимённых блоках; признак опознаётся по виду определения, а не по номеру типа в дереве - они " +
            "расходятся, поэтому принимаются оба вида. sweep: shift_mode (parallel/keep_angle/orthogonal), " +
            "section_count, path_part_count, path_length_mm; operation_result может быть null с причиной " +
            "evolution_operation_result_not_read. loft: building (auto/by_normal/by_object/cupola), closed, " +
            "section_count, couplings_count; признак сопоставляется с элементом коллекции ПО ПОРЯДКУ среди " +
            "односемейных, при несопоставлении все поля null с причиной loft_not_matched. loft.section_refs - " +
            "СЕЧЕНИЯ КАК ССЫЛКИ, выведенные из определения признака: это единственная валюта правки элемента по " +
            "сечениям, ссылка от создания умирает на первой мутации документа, kompas_rebuild отзывает все ссылки " +
            "документа целиком, а перечислить эскизы отдельным инструментом продукт не умеет (kompas_list_features " +
            "отдаёт только элементы операций). Пустой список означает «в определении сечений нет», null - «не " +
            "прочитано». shell: thickness_mm, thin_direction (inward/outward) и removed_face_count читаются ДВУМЯ " +
            "маршрутами, расхождение половин называется shell_halves_disagree и не сглаживается. " +
            "shell.removed_face_refs - снятые грани как ссылки: в топологии тела этих граней НЕТ, " +
            "kompas_read_topology их не отдаёт, а набор снимаемых граней - вход правки. Значение вне объявленного " +
            "набора отдаётся ЧИСЛОМ, а не подменяется именем и не превращается в null. ССЫЛКА НА ОПОРНЫЙ ЭСКИЗ " +
            "публикуется в необязательном sketch_ref тем же помощником и в той же форме, что у " +
            "kompas_list_features, поэтому один эскиз получает ОДИН живой адрес от обоих инструментов. Поле " +
            "заполнено только у семейств выдавливания; где ссылка не получена - null и строка " +
            "sketch_reference_not_resolved в unverified_aspects с причиной. У признаков ВЫДАВЛИВАНИЯ сторона " +
            "материала в координатах детали выводится из записанного направления и прочитанной нормали плоскости " +
            "эскиза - тем же правилом, что у kompas_extrude: material_toward и material_toward_source " +
            "(sketch_normal_rule), нормаль в material_toward_sketch_normal; когда направление или нормаль не " +
            "читаются, причина идёт в unverified_aspects.",
                Sch.Props(("feature_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.GetFeature),

            ReadOnly("kompas_get_sketch_status", "Определённость эскиза",
                                "Отвечает на вопрос «этот эскиз сейчас полностью определён?» - читается тот же агрегированный " +
                "статус системы ограничений, который КОМПАС показывает знаками «+», «−», «!». Маршрут: перенос " +
                "эскиза в API7 и чтение ISketch.ConstraintsState. ЗНАЧЕНИЯ: 1 = fully_defined " +
                "(is_fully_defined=true); 2 = under_defined (false); 0 = unknown (null) - так КОМПАС отвечает, " +
                "когда состояние не установлено, в частности на пустом эскизе; значение вне объявленного " +
                "перечисления тоже unknown. Значение 3 (ksStateUnresolvedRedundancy) на живой модели НЕ получено, " +
                "поэтому публикуется консервативно как unknown с причиной unresolved_redundancy_not_verified: " +
                "объявленное состояние не выдаётся за измеренное. ЧИСЛА СТЕПЕНЕЙ СВОБОДЫ НЕТ: маршрут отдаёт " +
                "состояние, а не счётчик, поэтому degrees_of_freedom всегда null и никогда не вычисляется из числа " +
                "размеров. ЧТО НЕ ДОКАЗЫВАЕТСЯ: координаты, ширина/высота/радиус при создании, замкнутость контура, " +
                "успешное выдавливание и корректный объём «+» не подтверждают, а «+» не доказывает, что эскиз " +
                "удобно параметризован. ЧТЕНИЕ НЕ МЕНЯЕТ МОДЕЛЬ: пятикратное чтение не изменило объём и счётчики " +
                "топологии, поэтому вызов не входит в режим правки, не открывает сеанс на эскизе, не перестраивает " +
                "документ и не меняет ревизию. ПАРАМЕТРИЧЕСКИЙ РЕЖИМ ДОКУМЕНТА ЗДЕСЬ НЕ ЧИТАЕТСЯ, и это названное " +
                "решение: член IKompasDocument2D1.ParametricMode доступен только у документа ФРАГМЕНТА эскиза, а " +
                "тот существует лишь внутри сеанса правки; открывать сеанс только ради чтения нельзя, потому что " +
                "сеанс на эскизе НЕ нейтрален для наблюдения - сохранение, следующее за открытым и закрытым " +
                "сеансом, несёт ДРУГОЕ состояние ограничений (1 → 2 после переоткрытия) при неизменных списке " +
                "ограничений, ревизии и объёме. Режим публикует kompas_list_sketch_constraints, который этот сеанс " +
                "открывает и без того. ЗАПИСЬ ОГРАНИЧЕНИЙ ЕСТЬ: наложить - kompas_create_sketch_constraint, " +
                "прочитать - kompas_list_sketch_constraints, снять - kompas_delete_sketch_constraint. Этот вызов " +
                "остаётся ЧТЕНИЕМ и не перемещает объекты ради получения «+». Полная определённость по-прежнему НЕ " +
                "требуется от выдавливания, вращения или завершения эскиза. ГРАНИЦА: агрегатное состояние НЕ " +
                "отвечает ни на один отрезок с двумя закреплёнными концами (остаётся 2 и до, и после переоткрытия), " +
                "ни на контур, СЛИТЫЙ С ВЫДАВЛИВАНИЕМ (тоже 2). Состояние 1 ДОСТИГАЕТСЯ и переживает переоткрытие " +
                "на контуре из четырёх отрезков БЕЗ выдавливания с полным набором ограничений.",
                Sch.Props(("sketch_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.SketchStatus,
                requiresOperationId: false),

            Mutation("kompas_update_feature", "Изменить параметр признака",
                                                                "Правка существующего признака на месте, без удаления и пересоздания. СЕМЕЙСТВА И " +
                                "ПОЛЯ. Выдавливание: depth_mm и/или end_condition и/или sketch_ref (уклон и тонкая " +
                                "стенка сохраняются как прочитаны). Фаска: distance1_mm, distance2_mm, direction, " +
                                "angle_deg. Вращение: rotation_angle_deg, rotation_direction. Скругление: " +
                                "radius_mm, edge_refs, base_object_refs. Отверстие: diameter_mm и поля СВОЕГО " +
                                "режима (depth_mm у blind_flat, counterbore_* у through_counterbore, countersink_* " +
                                "у through_countersink). Кинематическая операция: shift_mode. Элемент по сечениям: " +
                                "section_refs, couplings. Оболочка: thickness_mm, thin_inward, face_refs. Булева " +
                                "операция: operation. Разделение: plane. Отсечение: plane и keep_side. Изменение " +
                                "положения: reposition_kind и поля reposition_*. ПОЛЯ ПРИНАДЛЕЖАТ СВОЕМУ СЕМЕЙСТВУ: " +
                                "поле чужого семейства отвергается INVALID_ARGUMENT, в details - foreign_fields. " +
                                "Угол вращения задаётся rotation_angle_deg: у фаски angle_deg означает угол фаски, " +
                                "поэтому angle_deg на вращении отвергается INVALID_ARGUMENT. ПОЛНОТА ЗАПРОСА: у " +
                                "булевой операции, разделения, отсечения и изменения положения определение " +
                                "перечисляется ЦЕЛИКОМ - недостающее не додумывается из состояния модели. Порядок " +
                                "работы: прочитать признак (kompas_get_feature), подставить прочитанное, изменить " +
                                "нужное поле. Исключение - ТОЧКА ОСИ ПОВОРОТА не читается и не публикуется, поэтому " +
                                "запрос несёт её сам; остальные поля преобразования подставляются (reposition_kind, " +
                                "reposition_vector_mm, reposition_axis_point_mm, reposition_axis_direction_mm, " +
                                "reposition_angle_deg). АДРЕС ПРИЗНАКА: признак находится по позиции среди " +
                                "признаков ТОГО ЖЕ ТИПА в дереве; если таких признаков больше, чем элементов в " +
                                "коллекции, вызов отвергается CAPABILITY_UNAVAILABLE до правки - позиция перестала " +
                                "быть индексом, и запись ушла бы в чужой признак (так бывает у изменения положения, " +
                                "где тот же тип носит вспомогательная «Копия тела»). Вращение сопоставляется по " +
                                "СОСТАВУ (угол и направление), скругление - по составу входов, отверстие - по " +
                                "ЕДИНСТВЕННОСТИ отверстия: имя признака идентификатором не является. При нуле или " +
                                "нескольких совпадениях - CAPABILITY_UNAVAILABLE ДО правки: запись в чужой признак " +
                                "испортила бы чужую геометрию. СКРУГЛЕНИЕ: ДВЕ ВАЛЮТЫ ПРАВКИ НАБОРА. edge_refs - " +
                                "полная замена набора рёбрами тела; base_object_refs - собственные входы признака, " +
                                "эта валюта выражает СОКРАЩЕНИЕ. Два поля вместе запрещены, как запрещены radius_mm " +
                                "и edge_refs: иначе ответ не отличил бы «применилось и то, и другое» от " +
                                "«применилось одно из двух». Рёбер углов после создания скругления в топологии НЕТ " +
                                "(углы заняты цилиндрическими гранями), а исходные угловые рёбра отвечают " +
                                "STALE_REFERENCE ещё до правки. РАСШИРЕНИЕ набора (1→2, 2→3) НЕ ВЫРАЖЕНО ни одной " +
                                "валютой: исход - GEOMETRY_FAILED с partial_effects=true и details; правьте СОСТАВ. " +
                                "Исчезновение признака после записи и перестроения - отказ, а не успешная правка. " +
                                "ФАСКА: угол правится вместе с катетом, способ «расстояние и угол» сохраняется, " +
                                "производный катет пересчитывается ядром и НЕ записывается, если клиент не задал " +
                                "его явно; если фаска построена «расстоянием и углом», а angle_deg не задан - " +
                                "CAPABILITY_UNAVAILABLE до правки. ВРАЩЕНИЕ: перепривязка профиля и оси не " +
                                "выполняется, sketch_ref на вращении отвергается. ВЫДАВЛИВАНИЕ: sketch_ref меняет " +
                                "опорный эскиз того же признака; при сквозном вырезании это единственный параметр, " +
                                "подтверждаемый измерением. Значение перечитывается с нового объекта определения, " +
                                "признак сверяется по числу и имени, геометрия - объёмом против " +
                                "expected_volume_mm3. ЭЛЕМЕНТ ПО СЕЧЕНИЯМ: замена цепочек ПОЛНАЯ; если признак " +
                                "несёт цепочки, вызов, меняющий section_refs без couplings, отвергается " +
                                "INVALID_ARGUMENT с кодом couplings_must_be_restated. ПОДТВЕРЖДЕНИЕ: объём " +
                                "перечитывается и сверяется с expected_volume_mm3, у отверстия - с " +
                                "expected_volume_delta_mm3 (снятый материал, объём до минус объём после; знак - " +
                                "часть величины). НЕ МЕНЯЕТСЯ: sketch_ref на кинематической операции - " +
                                "CAPABILITY_UNAVAILABLE; замкнутость элемента по сечениям задаётся только при " +
                                "создании; касательные грани оболочки не выражаются; режим существующего отверстия " +
                                "правкой не меняется, а поле чужого режима отвергается INVALID_ARGUMENT; глубина " +
                                "зенковки производна и записанной не утверждается - в checks публикуется " +
                                "ПРОЧИТАННОЕ с проверкой countersink_depth_derived.",
                Sch.Props(
                    ("feature_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("depth_mm", Sch.Nullable(Sch.PositiveMm("Новая глубина",
                        "Только вместе с end_condition=blind."))),
                    ("end_condition", Sch.Nullable(Sch.Enum(
                        "Новое условие конца. through измерен и поддержан только для вырезания.",
                        "blind", "through"))),
                    ("sketch_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("distance1_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый первый катет фаски",
                        "Только для семейства chamfer."))),
                    ("distance2_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый второй катет фаски",
                        "Только для семейства chamfer; без него при правке одного катета фаска "
                        + "остаётся равносторонней."))),
                    ("angle_deg", Sch.Nullable(Sch.Num(
                        "Новый угол фаски, градусы (строго между 0 и 90). При правке угла способ построения «расстояние и " +
                        "угол» сохраняется, а второй катет пересчитывается ядром; производный катет НЕ записывается, если " +
                        "клиент не задал его явно: запись «прежнего» числа закрепила бы устаревшую производную. Если " +
                        "фаска построена «расстоянием и углом», а angle_deg НЕ задан, вызов отвергается " +
                        "CAPABILITY_UNAVAILABLE до правки: иначе способ построения молча сменился бы на два катета и угол " +
                        "потерялся.",
                        0d, 90d))),
                    ("direction", Sch.Nullable(Sch.Bool(
                        "Новая сторона фаски. Только для семейства chamfer. У семейства уклон (draft) это " +
                        "флаг продукта: сторона уклона относительно ТЕЛА переводится в него по нормали " +
                        "основания, поэтому смена одного основания без angle_deg сторону не переворачивает."))),
                    ("base_face_ref", Sch.Described(
                        Sch.Nullable(Sch.Ref("#/$defs/reference")),
                        "Новое основание уклона (семейство draft) — плоская грань тела. Взаимоисключающе с " +
                        "base_plane: обе формы сразу или ни одной — INVALID_ARGUMENT до правки. Сторона " +
                        "переносится через правку: флаг пересчитывается от нормали НОВОГО основания.")),
                    ("base_plane", Sch.Described(
                        Sch.Nullable(PlaneSchema(
                            "Новое основание уклона — плоскость в форме опоры эскиза: base (xy/xz/yz со " +
                            "offset_mm) ЛИБО reference на плоскость вида plane.")),
                        "Новое основание уклона (семейство draft) — плоскость. Взаимоисключающе с " +
                        "base_face_ref.")),
                    ("radius_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый радиус скругления",
                        "Только для семейства скругления. Признак находится по совпадению радиуса, а не по имени; при " +
                        "неоднозначном совпадении (несколько скруглений с тем же радиусом) вызов отвергается " +
                        "CAPABILITY_UNAVAILABLE ДО правки: записать радиус в чужой признак нельзя. С edge_refs в одном " +
                        "вызове не сочетается."))),
                    ("edge_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                                                "Новый НАБОР рёбер скругления - ПОЛНАЯ замена набора рёбрами тела; сокращение выражается " +
                        "base_object_refs. Обе валюты сразу и edge_refs с radius_mm не сочетаются: INVALID_ARGUMENT " +
                        "до правки. Рёбра берутся с ЖИВОЙ модели - объект от создания отвечает STALE_REFERENCE. " +
                        "Признак находится по СОСТАВУ входов, и владелец обязан совпасть с feature_ref; при нуле " +
                        "или нескольких совпадениях - CAPABILITY_UNAVAILABLE до правки, кандидаты в details " +
                        "(feature_ref_inputs, matched_owner_inputs). Рёбер углов после создания скругления в " +
                        "топологии НЕТ, поэтому набор правится составом. РАСШИРЕНИЕ (1→2, 2→3) не выражается: " +
                        "полная замена не сохраняет прежнее ребро, исход - GEOMETRY_FAILED с partial_effects=true. " +
                        "Исчезновение признака после записи - отказ, а не успешная правка.",
                        1, 64))),
                    (                        "base_object_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                                                "Новый НАБОР по СОБСТВЕННЫМ входам признака (семейство скругления) - валюта СОКРАЩЕНИЯ; " +
                        "замена состава выражается и рёбрами тела (edge_refs), поэтому валюты раздельны и вместе не " +
                        "передаются: INVALID_ARGUMENT до правки. Значения - ссылки input:<hex> из " +
                        "fillet.base_object_input_refs ответа kompas_get_feature; edge:<hex> сюда не подставляются, " +
                        "у собственного входа нет строки реестра рёбер. Набор - ПОЛНАЯ замена, пустой список " +
                        "отвергается INVALID_ARGUMENT, РАСШИРЕНИЕ не выражается: новое ребро не является " +
                        "собственным входом. Ссылки привязаны к ревизии: старые отсекаются STALE_REFERENCE.",
                        1, 64))),
                    ("rotation_angle_deg", Sch.Nullable(Sch.Num(
                        "Новый угол ВРАЩЕНИЯ (семейство rotation), градусы. Отдельное поле, а не angle_deg: у фаски " +
                        "angle_deg означает угол фаски, у вращения - угол развёртки, поэтому вызов с angle_deg на " +
                        "признаке вращения отвергается INVALID_ARGUMENT. Пишется только угол: перепривязка профиля и оси " +
                        "существующего вращения не выполняется, sketch_ref на вращении отвергается. Значение больше 360 " +
                        "отвергается: сектор не может занять больше целого оборота. С angle_deg в одном вызове не " +
                        "сочетается.",
                        0.0000001d, 360d))),
                    ("rotation_direction", Sch.Nullable(Sch.Enum(
                        "Новое направление вращения (семейство rotation). reverse ОТВЕРГАЕТСЯ CAPABILITY_UNAVAILABLE: при " +
                        "нём признак не строится вовсе.",
                        "normal", "reverse", "both", "middle_plane"))),
                    ("shift_mode", Sch.Nullable(Sch.Enum(
                                                "Новый режим движения сечения КИНЕМАТИЧЕСКОЙ операции (семейство sweep): parallel - " +
                        "образующая переносится параллельно себе, keep_angle - сохраняет исходный угол с " +
                        "направляющей, orthogonal - плоскость образующей ортогональна направляющей. Режимы " +
                        "РАЗЛИЧИМЫ ТОЛЬКО НА ДУГЕ: на прямой параллельный и ортогональный дают одно тело. Профиль и " +
                        "траектория НЕ меняются: sketch_ref на кинематической операции отвергается " +
                        "CAPABILITY_UNAVAILABLE.",
                        "parallel", "keep_angle", "orthogonal"))),
                    ("section_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                                                "Новый набор сечений ЭЛЕМЕНТА ПО СЕЧЕНИЯМ (семейство loft) в порядке соединения - ПОЛНАЯ " +
                        "замена, не добавление. Замкнутость правимым параметром НЕ объявлена: запись читается " +
                        "обратно прежним значением, поэтому она задаётся только при создании (kompas_loft.closed). " +
                        "Признак сопоставляется по ПОРЯДКУ среди односемейных, и при расхождении числа признаков в " +
                        "дереве с числом элементов коллекции - CAPABILITY_UNAVAILABLE: адрес не доказан, а править " +
                        "«наугад» значило бы изменить не тот объект.",
                        2, 64))),
                    ("couplings", Sch.Nullable(Sch.Described(
                        (JsonObject)CouplingsSchema.DeepClone(),
                        "Новый набор цепочек соответствия ЭЛЕМЕНТА ПО СЕЧЕНИЯМ (семейство loft) - ПОЛНАЯ " +
                        "ЗАМЕНА. " +
                        (string)CouplingsSchema["description"]! +
                        " ОТСУТСТВИЕ ПОЛЯ - НЕ «оставить как было»: если признак несёт цепочки, а правка " +
                        "меняет section_refs и цепочки не называет, вызов отвергается INVALID_ARGUMENT с " +
                        "кодом couplings_must_be_restated. Присваивание нового набора сечений СБРАСЫВАЕТ " +
                        "цепочки, поэтому проверка стоит ДО записи сечений: после неё молчаливая потеря " +
                        "соответствия была бы неотличима от «признак цепочек не нёс». Пустой список " +
                        "означает «без цепочек»."))),
                    ("thickness_mm", Sch.Nullable(Sch.Num(
                        "Новая толщина стенки ОБОЛОЧКИ (семейство shell), мм. Режим оболочки - ПАРА (толщина, " +
                        "направление), поэтому пишутся ОБА параметра: недостающая половина берётся С МОДЕЛИ, а не из " +
                        "умалчиваемого значения, иначе «изменилось ровно запрошенное» стало бы неотличимо от «изменилось " +
                        "ещё и это». Правка применяется перестроением: без него модель остаётся прежней, хотя запись и " +
                        "принята. Толщина обязана быть положительным конечным числом.",
                        0.0000001d, 1e6d))),
                    ("thin_inward", Sch.Nullable(Sch.Bool(
                        "Новое направление стенки ОБОЛОЧКИ (семейство shell): true - внутрь, false - наружу. Отдельное " +
                        "поле, а не общий direction фаски: у фаски direction - сторона фаски, у оболочки - сторона " +
                        "стенки, и одно имя для двух разных предметов сделало бы ответ неоднозначным. Пишется вместе с " +
                        "thickness_mm: режим - пара."))),
                    ("face_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Новый НАБОР удаляемых граней ОБОЛОЧКИ (семейство shell) - ПОЛНАЯ замена, а не добавление: " +
                        "передаётся то, что должно остаться снятым. Грани берутся из kompas_read_topology, а не позиции в " +
                        "коллекции. Пустой список отвергается INVALID_ARGUMENT: при пустом списке операция принимается, а " +
                        "тело не меняется, и такой вызов выглядел бы выполненным. Ссылки привязаны к ревизии документа: " +
                        "после правки перечитайте kompas_get_feature, старые отсекаются STALE_REFERENCE.",
                        1, 64))),
                    ("reposition_kind", Sch.Nullable(Sch.Enum(
                        "Вид преобразования (семейство reposition) - правка признака изменения положения. Обязателен: без " +
                        "вида непонятно, что менять. Повторный kompas_reposition этому не замена: он создаёт ВТОРОЙ " +
                        "признак и накапливает смещение, тогда как правка меняет параметры существующего признака " +
                        "относительно его ИСХОДНЫХ входов.",
                        "translate", "rotate"))),
                    ("reposition_vector_mm", Sch.Nullable(Sch.Described(
                        Sch.Ref("#/$defs/vector3"),
                        "Новый вектор переноса, мм, модельные координаты. Обязателен для translate."))),
                    ("reposition_axis_point_mm", Sch.Nullable(Sch.Described(
                        Sch.Ref("#/$defs/vector3"),
                        "Новая точка на оси поворота, мм. Обязательна для rotate."))),
                    ("reposition_axis_direction_mm", Sch.Nullable(Sch.Described(
                        Sch.Ref("#/$defs/vector3"),
                        "Новое направление оси поворота. Взаимоисключающе с reposition_axis_point2_mm."))),
                    ("reposition_axis_point2_mm", Sch.Nullable(Sch.Described(
                        Sch.Ref("#/$defs/vector3"),
                        "Новая вторая точка оси поворота. Взаимоисключающе с reposition_axis_direction_mm."))),
                    ("reposition_angle_deg", Sch.Nullable(Sch.Num(
                        "Новый угол поворота (семейство reposition), градусы. Обязателен для rotate. " +
                        "Отдельное поле, а не angle_deg: у фаски тот член означает угол фаски, у " +
                        "вращения — угол развёртки, у изменения положения — угол поворота тела. Одно " +
                        "имя для трёх разных величин сделало бы ответ неоднозначным.",
                        -360d, 360d))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                                                "Аналитическое ожидание объёма после правки; без него правка не подтверждается " +
                        "геометрически. У семейства reposition объём - ИНВАРИАНТ жёсткого преобразования, поэтому " +
                        "там он подтверждает лишь жёсткость, а положение подтверждает expected_bbox_mm." + DeclaredExpectationRule,
                        0d, 1e18d))),
                    ("expected_bbox_mm", Sch.Nullable(Sch.Ref("#/$defs/bbox"))),
                    ("plane", Sch.Nullable(Sch.Described(
                        Sch.Ref("#/$defs/cut_plane"),
                        "Новая опора признака РАЗДЕЛЕНИЯ (семейство split) или ОТСЕЧЕНИЯ (семейство cut_by_plane). Форма " +
                        "та же, что у одноимённого поля kompas_split и kompas_cut_by_plane. Правка идёт переносом ТРЁХ " +
                        "ТОЧЕК ПОСТРОЕНИЯ СОБСТВЕННОЙ опоры признака; подстановка ДРУГОЙ, заново построенной плоскости " +
                        "результата НЕ даёт, поэтому plane_ref на признаке разделения и отсечения отвергается " +
                        "CAPABILITY_UNAVAILABLE: доказать, что ссылка указывает именно на опору ЭТОГО признака, нечем. " +
                        "Плоскость при правке НЕ создаётся - опора берётся у самого признака, поэтому документ не " +
                        "накапливает неиспользованные плоскости. Определение перечисляется ЦЕЛИКОМ: plane обязателен и " +
                        "для разделения, и для отсечения, а неполный запрос отвергается INVALID_ARGUMENT до обращения к " +
                        "КОМПАС."))),
                    ("keep_side", Sch.Nullable(Sch.Enum(
                        "Новая оставляемая сторона для семейства cut_by_plane: positive - s > 0, negative - s < 0, где s " +
                        "= n·(p − p₀). Обязательна вместе с plane: сторона - такой же параметр признака, как опора, и не " +
                        "читается обратно с живого признака.",
                        "positive", "negative"))),
                    ("target_body_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("expected_part_volumes_mm3", Sch.Nullable(Sch.Arr(
                        Sch.Num("Объём части, мм³.", 0d, 1e18d),
                                                "Аналитическое ожидание объёмов ЧАСТЕЙ после правки разделения (семейство split) - " +
                        "единственная величина, которая эту правку подтверждает: сумма объёмов частей при правке не " +
                        "меняется, поэтому сохранение объёма - инвариант, а не подтверждение. Сопоставление по " +
                        "допуску объёма и с учётом кратности: каждому ожиданию своё тело, одно тело не закрывает " +
                        "два ожидания, порядок частей не важен. Несовпадение - отказ NO_GEOMETRY_CHANGE с " +
                        "partial_effects и фактическим составом в details. Минимум два значения.",
                        2, 64))),
                    ("operation", Sch.Nullable(Sch.Enum(
                        "Новый ВИД существующей булевой операции (семейство boolean). Правка применяется записью вида и " +
                        "перестроением: запись без перестроения геометрию не меняет. Объём разности и объём пересечения " +
                        "могут быть РАВНЫ, поэтому объявляйте ещё и expected_bbox_mm - габарит различает стороны, одного " +
                        "объёма здесь мало. Опорные тела существующей операции НЕ меняются: набора инструментов среди " +
                        "полей правки нет.",
                        "union", "difference", "intersect"))),
                    ("pattern", Sch.Nullable(Sch.Obj(
                        "pattern_edit",
                        Array.Empty<string>(),
                        Sch.Props(
                            ("count1", Sch.Nullable(Sch.Int(
                                "Новое число экземпляров по первому направлению. У массива по сетке - по оси 1, у " +
                                "кругового - в РАДИАЛЬНОМ направлении. Минимум 1.",
                                1, 1_000_000))),
                            ("count2", Sch.Nullable(Sch.Int(
                                "Новое число экземпляров по второму направлению. У массива по сетке - по оси 2, у " +
                                "кругового - в КОЛЬЦЕВОМ направлении. Минимум 1.",
                                1, 1_000_000))),
                            ("step1_mm", Sch.Nullable(Sch.PositiveMm(
                                "Новый шаг по первому направлению",
                                "У кругового это РАДИАЛЬНЫЙ шаг."))),
                            ("step2_mm", Sch.Nullable(Sch.PositiveMm(
                                "Новый шаг по второму направлению массива ПО СЕТКЕ",
                                "У кругового неприменим: там второе направление кольцевое, и его " +
                                "шаг — step2_deg. Смешение единиц отвергается INVALID_ARGUMENT до " +
                                "мутации."))),
                            ("step2_deg", Sch.Nullable(Sch.Num(
                                "Новый УГЛОВОЙ шаг кольцевого направления кругового массива, " +
                                "ГРАДУСЫ. У массива по сетке неприменим (там Step2 — миллиметры). " +
                                "Строго больше 0: нулевой шаг кладёт все экземпляры кольца друг на " +
                                "друга, и это одна позиция, а не массив.",
                                0.0000001d, 360d))),
                            ("angle1_deg", Sch.Nullable(Sch.Num(
                                "Новый угол наклона первой оси массива ПО СЕТКЕ, градусы. У кругового " +
                                "неприменим.", -360d, 360d))),
                            ("angle2_deg", Sch.Nullable(Sch.Num(
                                "Новый угол наклона второй оси массива ПО СЕТКЕ, градусы. " +
                                "Прямоугольность — 90°. У кругового неприменим.", -360d, 360d))),
                            ("direction1", Sch.Nullable(Sch.Bool(
                                "Новое направление копирования вдоль первой оси (сетка). У кругового " +
                                "неприменимо."))),
                            ("direction2", Sch.Nullable(Sch.Bool(
                                "Новое направление копирования вдоль второй оси (сетка). У кругового " +
                                "неприменимо."))),
                            ("building_type", Sch.Nullable(Sch.Enum(
                                "Новый способ построения. Слова те же, что при создании; какие из " +
                                "них применимы, решает семейство: у сетки — save_all, " +
                                "save_along_perimeter, save_along_axially, chess_order_by_axis1, " +
                                "chess_order_by_axis2; у кругового — save_all, chess_order_by_axis1, " +
                                "chess_order_by_axis2. Неизвестное слово и слово чужого семейства " +
                                "отвергаются INVALID_ARGUMENT до мутации.",
                                "save_all", "save_along_perimeter", "save_along_axially",
                                "chess_order_by_axis1", "chess_order_by_axis2"))),
                            ("step_by_axis_mm", Sch.Nullable(Sch.Num(
                                "Новый шаг вдоль оси кругового массива, мм. У сетки неприменим. " +
                                "Ноль допустим (умолчание признака).", 0d, 1e9d))),
                            ("reverse_direction", Sch.Nullable(Sch.Bool(
                                "Новое направление построения кругового массива. У сетки неприменимо."))),
                            ("save_initial_orientation", Sch.Nullable(Sch.Bool(
                                "Новая ориентация экземпляров кругового массива: false - доворачивать до радиального " +
                                "направления, true - сохранять исходную ориентацию (только перенос). Умолчание при " +
                                "СОЗДАНИИ - false; при правке пишется только переданное значение, своего умолчания правка " +
                                "не применяет. Поле есть ТОЛЬКО у кругового массива: у массива по сетке и у зеркального " +
                                "оно не поддержано и отвергается INVALID_ARGUMENT."))),
                            ("save_initial_objects", Sch.Nullable(Sch.Bool(
                                "Новое значение скрытия исходных объектов ЗЕРКАЛЬНОГО массива. Поле есть только у " +
                                "зеркального массива и применимо ТОЛЬКО к операции зеркального копирования ВСЕХ тел: у " +
                                "зеркального копирования выбранных операций возможность скрыть экземпляры отсутствует - " +
                                "запись читается обратно прежним значением, а геометрия не меняется. На массиве по сетке " +
                                "и на круговом отвергается INVALID_ARGUMENT."))),
                            ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                                "Аналитическое ожидание объёма документа ПОСЛЕ правки. Без него " +
                                "применение подтверждается только чтением параметров обратно, и это " +
                                "честно помечается в unverified_aspects." + DeclaredExpectationRule, 0d, 1e18d))),
                            ("expected_body_count", Sch.Nullable(Sch.Int(
                                "Аналитическое ожидание числа тел после правки. Сравнение точное: к " +
                                "счётным величинам допуск не применяется.", 0, 1_000_000)))),
                                                "Новые параметры СУЩЕСТВУЮЩЕГО признака массива - по сетке, кругового или зеркального. " +
                        "Правка пишет в живой признак, имя и место в дереве сохраняются. Признак сопоставляется по " +
                        "имени оболочки дерева и штампу обновления, как в kompas_get_pattern; несопоставление - " +
                        "CAPABILITY_UNAVAILABLE до правки. Заданные члены перезаписываются, незаданные остаются, " +
                        "поэтому запрос несёт ВСЕ параметры режима, которые должны сохраниться. «Принято» не " +
                        "означает «применено»: после перестроения модель читается обратно, каждому члену отвечает " +
                        "проверка read_back_<член>. Ось массива и плоскость зеркала не меняются. С полями других " +
                        "семейств не сочетается: INVALID_ARGUMENT до правки."))),
                    ("diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый диаметр ОТВЕРСТИЯ (семейство hole)",
                        "Пилота у цековки и зенковки, самого отверстия у глухого и сквозного цилиндрического. Адрес НЕ " +
                        "угадывается: соответствие признака дерева и записи об отверстии доказывается ЕДИНСТВЕННОСТЬЮ " +
                        "отверстия в документе, и при нескольких отверстиях вызов отвергается CAPABILITY_UNAVAILABLE до " +
                        "обращения к КОМПАС - запись в первое отверстие изменила бы чужое. Режим правкой не меняется: он " +
                        "читается из модели, и поле чужого режима отвергается INVALID_ARGUMENT с перечнем полей своего " +
                        "режима. Нулевой диаметр отвергается: КОМПАС принимает ноль и строит признак без материала при " +
                        "неизменном объёме."))),
                    ("counterbore_diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый диаметр ВЫТОЧКИ цековки (режим through_counterbore)",
                        "Поле ЧУЖОГО режима для остальных: на глухом и на зенковке отвергается INVALID_ARGUMENT до " +
                        "обращения к КОМПАС с перечнем полей своего режима."))),
                    ("counterbore_depth_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новая глубина ВЫТОЧКИ цековки (режим through_counterbore)",
                        "Читается обратно из модели (counterbore_depth_read_back в checks). Поле чужого режима для " +
                        "остальных."))),
                    ("countersink_diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый диаметр УСТЬЯ зенковки (режим through_countersink)",
                        "Устья, а не пилота: пилот задаётся diameter_mm. Способ построения зенковки - «диаметр + угол»."))),
                    ("countersink_angle_deg", Sch.Nullable(Sch.Num(
                        "Новый угол ЗЕНКОВКИ (режим through_countersink), градусы, строго между 0 и 180. Глубина зенковки " +
                        "при этом способе ПРОИЗВОДНА от угла и устья, поэтому записанное число не утверждается - в checks " +
                        "публикуется ПРОЧИТАННОЕ, с проверкой countersink_depth_derived. Сверяйте прочитанное, а не " +
                        "запрошенное.",
                        0.0000001d, 180d))),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                                                "Аналитическое ожидание СНЯТОГО правкой материала (семейство hole), мм³: объём_до − " +
                        "объём_после. ЗНАК - ЧАСТЬ ОПРЕДЕЛЕНИЯ ВЕЛИЧИНЫ: у «стало глубже» положительна, у «стало " +
                        "мельче» отрицательна, модуль сравнивать нельзя. Дельта отличает «применилось ровно " +
                        "запрошенное» от «применилось не то»; expected_volume_mm3 проверяется отдельно. Без " +
                        "ожидания уровень остаётся call_returned, в unverified_aspects - " +
                        "expected_volume_delta_not_supplied." + DeclaredExpectationRule,
                        -1e18d, 1e18d)))),
                WorkerCommands.UpdateFeature,
                requiresDocument: false,
                requiresRevision: true),

            // LIMIT: a suppression saved to the file cannot be removed by this server after reopening -
            // the suppressed feature leaves every collection, and the help documents the collection
            // escape only for objects after the rollback pointer (DOC:
            // ksdocument3d_enablerollbackfeaturesincollections.html - «По умолчанию объекты, исключенные
            // с помощью указателя (зеленая полоска) дерева, не выдаются»). The description names it.
            // History: docs/decisions/adapter-core.md#suppress-after-reopen
            Mutation("kompas_set_feature_suppressed", "Подавить или восстановить признак",
                "excluded=true отключает признак, false включает обратно. Эффект измерен: подавление сквозного окна "
                + "возвращает объём, снятие — обратно. Направление изменения объёма зависит от типа признака (приклейка — "
                + "меньше, вырезание — больше), поэтому сверяется число, а не знак. Это не удаление: имя и объект признака "
                + "сохраняются, НО подавленный признак исчезает из EntityCollection(o3d_operationElement=110) — "
                + "kompas_list_features его не показывает, пока подавление не снято. "
                + "ПОСЛЕ СОХРАНЕНИЯ И ПЕРЕОТКРЫТИЯ СНЯТЬ ПОДАВЛЕНИЕ ЭТИМ СЕРВЕРОМ НЕЛЬЗЯ: подавленного признака нет ни в "
                + "одной коллекции, а документированного маршрута его выдачи нет (enableRollBackFeaturesInCollections "
                + "выдаёт только объекты после указателя дерева). Снимайте до сохранения или в КОМПАС. "
                + "ПРИ СНЯТИИ ПОДАВЛЕНИЯ ГЕОМЕТРИЯ СВЕРЯЕТСЯ С СОСТОЯНИЕМ ДО ПОДАВЛЕНИЯ: сервер запоминает объём, "
                + "число тел и граней (проверка pre_suppression_state_recorded) и при снятии сравнивает "
                + "(restored_to_pre_suppression_state). Не совпало — вызов НЕ успешен: отказ GEOMETRY_FAILED с "
                + "partial_effects=true и числами «до / подавлено / после снятия» в details; модель НЕ откатывается "
                + "молча. Сверка невозможна (подавление вне сеанса, либо модель менялась) — названо в unverified_aspects. "
                + "ЗАЯВЛЕННЫЙ expected_volume_mm3, не совпавший с измеренным, подчиняется ОБЩЕМУ правилу заявленного "
                + "ожидания: вызов остаётся успешным, проверка объявляется непройденной (declared_expectation_not_confirmed "
                + "с числами), в warnings идёт предупреждение, уровень не выше structure_checked. Отказом остаётся только "
                + "сверка снятия с состоянием до подавления — она ловила настоящую неверную модель.",
                Sch.Props(
                    ("feature_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("suppressed", Sch.Bool("true — подавить, false — восстановить.")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма после изменения. Без него геометрия не подтверждается; "
                        + "если задано и не совпало — вызов отклоняется кодом GEOMETRY_FAILED." + DeclaredExpectationRule,
                        0d, 1e18d)))),
                WorkerCommands.SuppressFeature,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_delete_feature", "Удалить признак, эскиз или объект вспомогательной геометрии",
                                "Удаляет объект модели детали по ссылке из перечня: ПРИЗНАК (kompas_list_features), ЭСКИЗ " +
                "(kompas_list_sketches) или объект ВСПОМОГАТЕЛЬНОЙ ГЕОМЕТРИИ - плоскость, ось, точку " +
                "(kompas_list_aux_geometry). Имя инструмента оставлено прежним для совместимости. Маршрут: " +
                "ksDocument3D.DeleteObject; объект вспомогательной геометрии принимается только после переноса " +
                "API7→API5, и перенос назван в проверке deletion_target_transfer. КОМПОНЕНТ СБОРКИ этим " +
                "инструментом не удаляется: ссылка вида component отвергается INVALID_ARGUMENT ДО COM с указанием " +
                "на kompas_delete_component. ЧТО СТАНЕТ С ЗАВИСИМЫМИ - ЗАВИСИТ ОТ ВИДА ОБЪЕКТА: у ПРИЗНАКА " +
                "достоверного перечня зависимых в API нет, поэтому возвращаются кандидаты - признаки, идущие в " +
                "дереве после удаляемого; у ЭСКИЗА перечень строится через GetSketch() определения признака и " +
                "достоверен; у ВСПОМОГАТЕЛЬНОЙ ПЛОСКОСТИ - эскизы, лежащие на ней (ksSketchDefinition.GetPlane()); " +
                "для ОСИ И ТОЧКИ маршрута нет, и сервер говорит об этом прямо, а не выдаёт пустой перечень за " +
                "«зависимых нет». ЯДРО УНОСИТ ЗАВИСИМЫЕ С СОБОЙ: удаление эскиза с выдавливанием удаляет и " +
                "выдавливание, удаление плоскости уносит лежащий на ней эскиз; независимые объекты остаются. Каскад " +
                "бывает ТРАНЗИТИВНЫМ - это называется в cascade_composition_not_confirmed, а не выдаётся за " +
                "проверенное. ЕСЛИ ЗАВИСИМЫЕ ЕСТЬ ИЛИ ПЕРЕЧЕНЬ ПОСТРОИТЬ НЕ УДАЛОСЬ, удаление отказывает " +
                "DEPENDENT_FEATURES без обращения к КОМПАС, пока не передан confirm_dependents=true (умолчание " +
                "false). Операция необратима: сохранённая копия документа - единственный откат.",
                Sch.Props(
                    ("feature_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("confirm_dependents", Sch.Nullable(Sch.Bool(
                        "Согласие удалить объект, на котором построено другое или перечень зависимых "
                        + "построить не удалось. По умолчанию false: при зависимых вызов отказывает "
                        + "их перечнем, а модель не меняется.", false))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма после удаления." + DeclaredExpectationRule, 0d, 1e18d)))),
                WorkerCommands.DeleteFeature,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_rebuild", "Перестроить",
                "Перестроение документа. Все ссылки, выданные для предыдущей ревизии, после него "
                + "отвергаются. Перестроение обязано пересчитывать УЖЕ построенное, поэтому объём "
                + "документа до и после сверяется, и при расхождении перестроение ПОВТОРЯЕТСЯ (не "
                + "более трёх раз) — измерено, что одно перестроение может оставить признак "
                + "неприменённым, а следующее возвращает модель. Число попыток и причина названы в "
                + "context.unverified_aspects: rebuild_repeated, если модель совпала лишь со второго "
                + "раза, и rebuild_changed_model, если не совпала за все попытки. Отказом это не "
                + "является: перестроение не мутация клиента.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.Rebuild,
                requiresDocument: true),

            Mutation("kompas_export_step", "Экспорт STEP",
                "Экспорт нативным конвертером. В ответе — фактически достигнутый уровень проверки: file_created или syntax_checked, но не геометрия.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("output_path", Sch.Ref("#/$defs/output_path"))),
                WorkerCommands.ExportStep,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_export_stl", "Экспорт STL",
                                "Сохранение модели в файл STL маршрутом ksDocument3D.SaveAsToAdditionFormat с IAdditionFormatParam " +
                "(format_STL=6). Параметры триангуляции: max_edge_length_mm (плотность сетки STL на v24 этим " +
                "параметром не управляется - измерено; значение записывается в параметры экспорта и служит допуском " +
                "сверки габарита триангуляции с габаритом тела), normal_angle_deg - максимально допустимое угловое " +
                "отклонение нормалей; двоичный или текстовый файл задаётся полем binary. Возврат конвертера true НЕ " +
                "считается доказательством файла: сервер читает файл с диска, считает треугольники ИЗ ФАЙЛА " +
                "(двоичный - из заголовка, текстовый - по строкам facet normal), называет способ счёта " +
                "(triangle_count_source) и сверяет габарит триангуляции с габаритом тела в пределах шага. Уровень: " +
                "structure_checked - файл и число треугольников; geometry_checked - габарит совпал. Путь " +
                "проверяется политикой корней ДО COM; формат STL вне D3FormatConvType - FORMAT_UNAVAILABLE; отказ " +
                "конвертера или пустой файл - EXPORT_FAILED. Документ - деталь; сборка и чертёж дают " +
                "WRONG_DOCUMENT_KIND. 3MF и другие форматы не публикуются: документированного маршрута нет.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("target_path", Sch.Ref("#/$defs/output_path")),
                    ("binary", Sch.Bool(
                        "Двоичный (true) или текстовый (false) STL. Двоичный — по умолчанию; для "
                        + "текстового точный размер файла и число треугольников зависят от "
                        + "форматирования, поэтому сверяется число треугольников, а не размер.", true)),
                    ("max_edge_length_mm", Sch.Positive(
                        "На КОМПАС-3D v24 плотность сетки STL этим параметром не управляется "
                        + "(измерено); значение записывается в параметры экспорта и используется как "
                        + "допуск сверки габарита триангуляции с габаритом тела",
                        "мм", "Влияет на допуск сверки габарита, а не на число треугольников.")),
                    ("normal_angle_deg", Sch.Positive(
                        "Максимально допустимое угловое отклонение нормалей в соседних точках", "градусы"))),
                WorkerCommands.ExportStl,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_import_step", "Импорт STEP",
                "Чтение STEP. Возвращает, что реально появилось: документы, типы, число тел и "
                + "компонентов. Успехом считается только подтверждённая геометрия. "
                + "created_document_ids несёт ИДЕНТИФИКАТОРЫ ДОКУМЕНТОВ ХОСТА — те же значения, "
                + "которые выдаёт kompas_list_documents, и они принимаются инструментами, "
                + "адресующими документ (относительно 0.8.0: раньше указатель COM, непригодный для "
                + "адресации). "
                + "target_path ПРИМЕНЯЕТСЯ: импортированный документ сохраняется по этому пути "
                + "документированным маршрутом ksDocument3D.SaveAs, запись подтверждается чтением "
                + "пути документа; отказ сохранения — отказ импорта с partial_effects=true. "
                + "Неудачный импорт НЕ ОСТАВЛЯЕТ зарегистрированных документов: то, что открыл "
                + "неудачный маршрут, закрывается и снимается с регистрации до следующей попытки; "
                + "если закрыть не удалось, документ оставлен и назван (partial_effects=true и "
                + "details.documents_left_open), чтобы ответ не лгал. desired_kind сверяется с "
                + "прочитанным типом (ksdocument3d_isdetail.html): несовпадение — непройденная проверка "
                + "desired_kind_matched, а не отказ; auto — без ожидания.",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("input_path", Sch.Ref("#/$defs/path")),
                    ("desired_kind", Sch.Enum("auto — ожидания нет; part/assembly — ожидание, сверяемое с прочитанным типом (desired_kind_matched).", "auto", "part", "assembly")),
                    ("target_path", Sch.Nullable(Sch.Ref("#/$defs/output_path")))),
                WorkerCommands.ImportStep,
                requiresDocument: false),

            Mutation("kompas_export_image", "Снимок модели или чертежа",
                                "Растровый снимок документированным маршрутом API5: ksDocument3D.RasterFormatParam → " +
                "ksRasterFormatParam → SaveAsToRasterFormat; ЧЕРТЁЖ снимается тем же маршрутом на своём указателе " +
                "(ksDocument2D.RasterFormatParam → ksRasterFormatParam → ksDocument2D.SaveAsToRasterFormat), " +
                "параметры снимка те же. Проекция окна (view, keep_view) - маршрут трёхмерного документа: для " +
                "чертежа view отвергается INVALID_ARGUMENT до обращения к КОМПАС. Картинка возвращается " +
                "image-блоком и/или файлом; габарит читается ИЗ ЗАГОЛОВКА файла, а не берётся из запроса. Два " +
                "режима маршрута взаимно исключающие: с непустым именем файла ядро пишет файл и оставляет массив " +
                "байт пустым, с пустым - отдаёт массив и файла не создаёт. Когда нужны и файл, и картинка, рендер " +
                "делается ОДИН раз. Проекция задаётся полем view по имени типа " +
                "(front/rear/up/down/left/right/isometric/dimetric), а не по локализованной подписи; применённая " +
                "проекция подтверждается ОБРАТНЫМ ЧТЕНИЕМ типа, а не возвратом SetCurrent. Прежний вид возвращается " +
                "после снимка, если не задан keep_view, но только когда он ВОССТАНОВИМ (прочитан и его тип " +
                "опубликован): в видимом окне до первого SetCurrent ни одна проекция не отвечает IsCurrent=true, и " +
                "вызов с keep_view=false отказывает VIEW_UNAVAILABLE ДО смены вида, не трогая окно. keep_view=true " +
                "- согласие оставить запрошенную проекцию. Смена вида НЕ помечает документ изменённым и ревизию не " +
                "двигает. СНИМОК - ВСПОМОГАТЕЛЬНЫЙ КАНАЛ, А НЕ ДОКАЗАТЕЛЬСТВО: изображение сервером не разбирается " +
                "и не подтверждает ни геометрию, ни размеры. РАЗМЕР БЕЗ ПАРАМЕТРОВ ПОДБИРАЕТСЯ В ДВА ПРОХОДА: если " +
                "не заданы ни resolution, ни scale, сервер делает ПРОБНЫЙ снимок в память с умолчанием ядра, " +
                "измеряет длинную сторону и вычисляет масштаб так, чтобы итоговая длинная сторона была около " +
                "long_side_px (по умолчанию 1024). Формулы базы растра нет: длинная сторона строго пропорциональна " +
                "(resolution / 120) × scale, а база зависит от проекции конкретной детали " +
                "(docs/04_KOMPAS_API_NOTES.md §4.66), поэтому подбор идёт ИЗМЕРЕНИЕМ. Снимок без параметров " +
                "занимает примерно вдвое больше времени (два рендера); файл пишет ТОЛЬКО второй проход, вид окна " +
                "меняется и возвращается ОДИН раз на оба прохода. Вычисленный масштаб зажимается к пределам ядра " +
                "(10 рендерится, 100 - RASTER_EMPTY, 1000 - RASTER_REFUSED), и зажим называется. Промах дальше 10 % " +
                "от цели - не отказ, а причина в sizing_note. Прежнее поведение - передать явные resolution или " +
                "scale. В ответе: sizing (explicit | auto_two_pass), probe_long_side_px, applied_scale, " +
                "target_long_side_px.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("format", Sch.Enum(
                        "Формат снимка. Перечень проверяется ДО обращения к КОМПАС: измерено, что "
                        + "значение вне перечня ядро принимает и молча подменяет другим форматом "
                        + "(99 дало BMP 440886 байт при запросе PNG). WMF не публикуется: справка "
                        + "говорит, что сохранение в WMF не поддерживается и файл пишется в EMF.",
                        "png", "jpg", "bmp", "tif")),
                    ("resolution", Sch.Nullable(Sch.Int(
                        "Значение extResolution. Не задано — член не записывается и действует "
                        + "умолчание ядра (измерено на контрольной пластине: 394×537 пикселей). "
                        + "Измеренная зависимость габарита: 50 → 165×224, 100 → 328×448, "
                        + "200 → 655×894.", 1, 100_000))),
                    ("scale", Sch.Nullable(Sch.Num(
                        "Значение extScale — множитель габарита снимка. Измерено: 0.5 → 165×224, "
                        + "1 → 328×448, 2 → 655×894. Явное значение отменяет подбор сервера.",
                        exclusiveMin: true, exclusiveMinValue: 0d))),
                    ("long_side_px", Sch.Nullable(Sch.Int(
                        "Целевая длинная сторона снимка в пикселях; не задано — 1024. Имеет смысл ТОЛЬКО "
                        + "когда не заданы ни resolution, ни scale: они — явные значения, и вместе с "
                        + "целевым размером вызов отвергается INVALID_ARGUMENT до COM. Пределы: "
                        + $"{KompasMcp.Domain.Imaging.RasterLimits.MinLongSidePixels}..{KompasMcp.Domain.Imaging.RasterLimits.MaxLongSidePixels}.",
                        KompasMcp.Domain.Imaging.RasterLimits.MinLongSidePixels,
                        KompasMcp.Domain.Imaging.RasterLimits.MaxLongSidePixels))),
                    ("save_path", Sch.Nullable(Sch.Str(
                        "Куда записать файл. Не задано — файл не создаётся вовсе. Путь проверяется "
                        + "политикой ДО обращения к КОМПАС: измерено, что ядро путь не проверяет — "
                        + "на запрещённых символах оно записало усечённый пустой файл, а "
                        + "несуществующий каталог создало само."))),
                    ("return_image_content", Sch.Nullable(Sch.Bool(
                        "Вернуть картинку image-блоком в ответе. По умолчанию true. Хотя бы одно из "
                        + "двух — картинка или save_path — обязано быть задано.", true))),
                    ("view", Sch.Nullable(Sch.Enum(
                        "Проекция, в которой снимается модель. Не задано — снимается ТЕКУЩИЙ вид "
                        + "документа, и он не меняется; задано — вид применяется перед снимком и "
                        + "ВОЗВРАЩАЕТСЯ после (если не задан keep_view). Возврат возможен только "
                        + "тогда, когда прежний вид ВОССТАНОВИМ: он прочитан и его тип входит в "
                        + "опубликованный перечень; в видимом окне до первого SetCurrent он не "
                        + "читается, и вызов с keep_view=false отказывает ДО смены вида. "
                        + "Значения — типы из ksViewProjectionType по именам, а не локализованные "
                        + "подписи вида («#Спереди»): имена зависят от языка, типы — нет. Измерено: "
                        + "смена проекции НЕ помечает документ изменённым, ревизия не растёт. Если в "
                        + "документе нет проекции этого типа — отказ VIEW_UNAVAILABLE, а не снимок "
                        + "текущего вида под чужим именем.",
                        "front", "rear", "up", "down", "left", "right", "isometric", "dimetric"))),
                    ("keep_view", Sch.Nullable(Sch.Bool(
                        "Оставить запрошенную проекцию после снимка вместо возврата прежней. По "
                        + "умолчанию false (возврат). Требует view: без него вызов отвергается "
                        + "INVALID_ARGUMENT, потому что сохранять было бы нечего. Обязателен, когда "
                        + "прежний вид не читается (видимое окно до первого SetCurrent): "
                        + "keep_view=true — согласие оставить запрошенную проекцию, без него вызов "
                        + "отказывает VIEW_UNAVAILABLE, не меняя вид окна.", false)))),
                WorkerCommands.ExportImage,
                requiresDocument: true,
                requiresRevision: true),

            // DRW — чертёжный блок. Маршруты документированы справкой v24 (см. коммент-блоки адаптера
            // и docs/04_KOMPAS_API_NOTES.md). Размеры привязаны к ТОЧКАМ вида, а не к топологии модели:
            // ассоциативность размера к модели НЕ заявляется — справка её не документирует.
            // History: docs/decisions/drawings.md#tools

            Mutation("kompas_create_drawing_views", "Создать стандартные виды чертежа",
                                "Строит группу стандартных ассоциативных видов модели маршрутом API7: " +
                "IKompasDocument2D.ViewsAndLayersManager → IViews.AddStandartViews(FileName, ProjectionName, " +
                "ProjectionsTypes, X, Y, Scale, DX, DY). ИСТОЧНИК - ФАЙЛ МОДЕЛИ НА ДИСКЕ: стандартный вид есть " +
                "проекция ФАЙЛА, несохранённая модель даёт отказ DOCUMENT_NOT_FOUND. Типы видов - из перечисления " +
                "ProjectionType: front, rear, top, bottom, left, right, isometric, iso_yzx, iso_zxy, dimetric; имя " +
                "вне перечня отвергается INVALID_ARGUMENT со списком поддержанных ДО обращения к КОМПАС. Ответ - " +
                "виды, ПЕРЕЧИТАННЫЕ из чертежа (номер, тип, масштаб, файл-источник, имя проекции), а не пересказ " +
                "запроса; число СОЗДАННЫХ видов считается сравнением коллекции до и после. Габарит каждого вида " +
                "читается (ksGetObjGabaritRect по указателю ksGetViewReference) и сверяется с местом внутри рамок " +
                "листа (GetPlaceInsideFrames): проверка views_within_sheet называет числа, вид за рамкой даёт " +
                "предупреждение view_outside_sheet с именем вида, а не молчаливый успех. Вид сам не переносится - " +
                "куда его сдвинуть, решает клиент.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("source_path", Sch.Ref("#/$defs/path")),
                    ("projection_name", Sch.Nullable(Sch.Str(
                        "Имя проекции из списка проекций документа-источника "
                        + "(IAssociationView.ProjectionName). Не задано — умолчание КОМПАС «Спереди», "
                        + "а не догадка сервера."))),
                    ("projections", Sch.Nullable(Sch.Arr(
                        Sch.Enum("Тип вида из перечисления ProjectionType (projectiontype.html).",
                            "front", "rear", "top", "bottom", "left", "right",
                            "isometric", "iso_yzx", "iso_zxy", "dimetric"),
                        "Набор типов видов. Пусто — набор по умолчанию документа, а не выдуманный сервером."))),
                    ("x", Sch.Num("Координата X точки привязки группы видов, в миллиметрах чертежа.", defaultTo: 0d)),
                    ("y", Sch.Num("Координата Y точки привязки группы видов, в миллиметрах чертежа.", defaultTo: 0d)),
                    ("scale", Sch.Nullable(Sch.Num(
                        "Масштаб видов. Не задано — умолчание КОМПАС (1:1), а не догадка сервера.",
                        exclusiveMin: true, exclusiveMinValue: 0d))),
                    ("dx", Sch.Nullable(Sch.Num(
                        "Зазор между видами по X, в миллиметрах чертежа. Не задано — умолчание ядра.",
                        exclusiveMin: true, exclusiveMinValue: 0d))),
                    ("dy", Sch.Nullable(Sch.Num(
                        "Зазор между видами по Y, в миллиметрах чертежа. Не задано — умолчание ядра.",
                        exclusiveMin: true, exclusiveMinValue: 0d)))),
                WorkerCommands.CreateDrawingViews,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_create_section_view", "Создать разрез или сечение модели на чертеже",
                                                                "Строит ассоциативный РАЗРЕЗ или СЕЧЕНИЕ по существующему ассоциативному виду " +
                                "маршрутом API7: ISymbols2DContainer.CutLines → ICutLines.Add() → ICutLine.Points / " +
                                "ArrowPos → IDrawingObject.Update, затем IViews.Add(vt_Section) → " +
                                "IAssociationView.BaseObject = линия, Section по полю kind (cut - разрез, FALSE; " +
                                "section - сечение, TRUE), масштаб и точка привязки, снова Update. Значение Section " +
                                "пишется ДО первого Update: после постройки оно не переключается. СТУПЕНЧАТАЯ " +
                                "трасса: четыре и более точек, участки чередуются секущими и переходными, соседние " +
                                "перпендикулярны, все секущие параллельны. Трасса задаётся РОВНО в одной системе: " +
                                "trace_model (мм модели, переводятся проекцией опорного вида) либо trace_view (мм " +
                                "вида). Сторона стрелок - слово look (left/right) или вектор look_vector, " +
                                "перпендикулярный секущим. НЕ ПОДДЕРЖИВАЕТСЯ и названо прямо: ломаный и местный " +
                                "разрезы, задание буквы (ICutLine.Text только для чтения - букву присваивает " +
                                "КОМПАС), разрезы сборок. Ответ - вид, ПЕРЕЧИТАННЫЙ из чертежа: kind, Section, " +
                                "трасса, сторона стрелок, буква, обозначение, масштаб, точка привязки, число " +
                                "объектов, шаг и угол штриховки. Пустой вид (ObjectCount = 0) - GEOMETRY_FAILED с " +
                                "откатом созданного, а не успех. Габарит сверяется с местом внутри рамок листа: " +
                                "views_within_sheet с числами, при выходе за рамку - предупреждение " +
                                "view_outside_sheet с именем вида (вид не переносится).",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("base_view_ref", Sch.Ref("#/$defs/reference")),
                    ("mode", Sch.Enum("Режим разреза: simple — две точки, stepped — линия с перегибами.",
                        "simple", "stepped")),
                    ("kind", Sch.Nullable(Sch.Enum(
                        "Вид изображения: cut — разрез (по умолчанию), section — сечение. Пишется в "
                        + "Section до первой постройки вида; после неё не переключается.",
                        "cut", "section"))),
                    ("trace_model", Sch.Nullable(Sch.Arr(
                        Sch.Num("Координата точки трассы в миллиметрах модели."),
                        "Точки трассы в координатах МОДЕЛИ, подряд x0, y0, x1, y1, ... Число координат "
                        + "чётное. Переводится проекцией опорного вида. Взаимоисключимо с trace_view."))),
                    ("trace_view", Sch.Nullable(Sch.Arr(
                        Sch.Num("Координата точки трассы в миллиметрах вида."),
                        "Точки трассы уже в координатах ВИДА, подряд x0, y0, ... — та система, в которой "
                        + "линия разреза хранит трассу. Взаимоисключимо с trace_model."))),
                    ("look", Sch.Nullable(Sch.Enum(
                        "Сторона, куда смотрят стрелки, относительно направления трассы "
                        + "(icutline_arrowpos.html: TRUE — слева).",
                        "left", "right"))),
                    ("look_vector", Sch.Nullable(Sch.Vec2(
                        "Направление взгляда вектором в координатах вида; обязан быть перпендикулярен "
                        + "секущим участкам. Взаимоисключимо с look."))),
                    ("x", Sch.Num("Координата X точки привязки разреза, в миллиметрах чертежа.", defaultTo: 0d)),
                    ("y", Sch.Num("Координата Y точки привязки разреза, в миллиметрах чертежа.", defaultTo: 0d)),
                    ("scale", Sch.Nullable(Sch.Num(
                        "Масштаб разреза. Не задано — умолчание КОМПАС (1:1).",
                        exclusiveMin: true, exclusiveMinValue: 0d))),
                    ("projection_link", Sch.Nullable(Sch.Bool(
                        "Сохранять ли привязку к проекции (это ПРИЗНАК, а не имя проекции). "
                        + "Не задано — как у опорного вида."))),
                    ("letter", Sch.Nullable(Sch.Str(
                        "Буква разреза. НЕ ПОДДЕРЖИВАЕТСЯ: ICutLine.Text только для чтения "
                        + "(icutline_text.html), букву присваивает КОМПАС. Заданная буква — отказ "
                        + "CAPABILITY_UNAVAILABLE, а не молчание.")))),
                WorkerCommands.CreateSectionView,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_update_section_view", "Изменить разрез или сечение чертежа",
                                "Меняет трассу существующего разреза или сечения, сторону стрелок, масштаб и точку привязки: трасса " +
                "пишется в опорную линию вида (ICutLine.Points, ICutLine.ArrowPos), геометрия — в вид " +
                "(IView.Scale/X/Y), каждая запись завершается IDrawingObject.Update. view_updated — только для " +
                "правок геометрии (trace_view, scale, x, y); при правке только look она названа неприменимой, а " +
                "возврат Update() остаётся в observed. Новая трасса проверяется ТЕМ ЖЕ правилом, что при создании, " +
                "до записи. Ответ — тот же вид ДО и ПОСЛЕ, поэтому добавление вида не может сойти за правку. НЕ " +
                "ПОДДЕРЖИВАЕТСЯ и названо прямо: смена вида изображения — поле kind принимает только тот вид, " +
                "который у вида уже есть; запрос другого вида — отказ CAPABILITY_UNAVAILABLE с подсказкой удалить " +
                "вид и построить заново (измерено: у построенного вида Section не переключается, Update отвечает " +
                "false); буква — ICutLine.Text только для чтения. Габарит вида после правки сверяется с листом: " +
                "views_within_sheet, при выходе за рамку — предупреждение view_outside_sheet с именем вида (вид не " +
                "переносится).",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("view_ref", Sch.Ref("#/$defs/reference")),
                    ("kind", Sch.Nullable(Sch.Enum(
                        "Вид изображения, который у вида уже есть: cut или section. Другой вид — отказ "
                        + "CAPABILITY_UNAVAILABLE: удалите вид и постройте заново.",
                        "cut", "section"))),
                    ("trace_view", Sch.Nullable(Sch.Arr(
                        Sch.Num("Координата точки трассы в миллиметрах вида."),
                        "Новая трасса в координатах ВИДА, подряд x0, y0, ... Не задано — трасса не "
                        + "меняется."))),
                    ("look", Sch.Nullable(Sch.Enum(
                        "Новая сторона стрелок относительно направления трассы.", "left", "right"))),
                    ("scale", Sch.Nullable(Sch.Num(
                        "Новый масштаб разреза.", exclusiveMin: true, exclusiveMinValue: 0d))),
                    ("x", Sch.Nullable(Sch.Num("Новая координата привязки по X, в миллиметрах чертежа."))),
                    ("y", Sch.Nullable(Sch.Num("Новая координата привязки по Y, в миллиметрах чертежа.")))),
                WorkerCommands.UpdateSectionView,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_get_section_view", "Прочитать разрез или сечение чертежа",
                "Читает вид-разрез по ссылке: тип вида, вид изображения (kind: cut — разрез, section — "
                + "сечение, по прочитанному Section), значение Section, масштаб, точку привязки, "
                + "трассу линии разреза в мм вида, сторону стрелок, букву, обозначение (например "
                + "«А-А(1:1)»), файл-источник, число объектов, шаг/угол/стиль штриховки. Линия берётся "
                + "как СОБСТВЕННЫЙ опорный объект вида (IAssociationView.BaseObject), поэтому разрез, "
                + "переоткрытый с диска, читается тем же маршрутом, что и только что созданный. "
                + "Вид другого типа отвергается INVALID_ARGUMENT с названным типом: чужой вид разрезом "
                + "не считается. Режим (simple/stepped) ВЫВОДИТСЯ из числа точек трассы: ядро его не "
                + "хранит, и это названо, а не выдано за прочитанное. Габарит разреза читается и "
                + "сверяется с местом внутри рамок листа: views_within_sheet с числами, при выходе за "
                + "рамку — предупреждение view_outside_sheet с именем вида. Только чтение.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("view_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.GetSectionView,
                requiresDocument: true),

            Mutation("kompas_delete_section_view", "Удалить разрез или сечение чертежа",
                "Удаляет вид-разрез документированным IDrawingObject.Delete() и, если не задан "
                + "keep_cut_line, линию разреза — отдельным Delete(), потому что ядро вид удаляет, а "
                + "линию оставляет (измерено). Ответ называет, что именно удалено: вид, линия, сколько "
                + "видов осталось. Вид другого типа отвергается INVALID_ARGUMENT с названным типом до "
                + "любого удаления. Сечение удаляется тем же маршрутом, что разрез.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("view_ref", Sch.Ref("#/$defs/reference")),
                    ("keep_cut_line", Sch.Nullable(Sch.Bool(
                        "Оставить линию разреза в чертеже. Не задано — удаляется вместе с видом.")))),
                WorkerCommands.DeleteSectionView,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_list_drawing_views", "Перечень видов чертежа",
                "Читает виды чертежа через IKompasDocument2D.ViewsAndLayersManager.Views: номер, имя, "
                + "тип, масштаб, координаты точки привязки и — для ассоциативных видов — файл-источник, "
                + "имя проекции, видимость скрытых и осевых линий, число объектов. Габарит вида читается "
                + "документированным маршрутом (IView.Number → ksGetViewReference → ksGetObjGabaritRect, "
                + "справка v24: iview_number.html, ksdocument2d_ksgetviewreference.html, "
                + "ksdocument2d_ksgetobjgabaritrect.html) и отдаётся полями gabarit_min/gabarit_max в мм "
                + "листа; вид за рамкой назван предупреждением view_outside_sheet с его именем, а "
                + "непрочитанный габарит — пометкой, а не нулём. У вида-разреза называется вид "
                + "изображения (kind: cut — разрез, section — сечение) по прочитанному Section. Только "
                + "чтение.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.ListDrawingViews,
                requiresDocument: true),

            ReadOnly("kompas_list_dimensions", "Перечень размеров вида чертежа",
                                "Читает размеры выбранного вида через ISymbols2DContainer, полученный из IView: LineDimensions, " +
                "RadialDimensions, DiametralDimensions. Для каждого размера отдаётся тип отрисовки " +
                "(IDrawingObject.DrawingObjectType), признак Valid, значение в миллиметрах и точки. ЧТЕНИЕ ИЗ " +
                "ПЕРЕОТКРЫТОГО ДОКУМЕНТА: инструмент берёт размеры ТЕКУЩЕГО дескриптора документа, поэтому после " +
                "close/open он читает объекты переоткрытого файла, а не сохранённый ранее DTO или освобождённую " +
                "RCW. ОГРАНИЧЕНИЕ, НАЗВАННОЕ ПРЯМО: значение линейного размера считается по координатам точек " +
                "(X1/Y1/X2/Y2), потому что справка не публикует готовое номинальное значение; ассоциативность " +
                "размера к модели не читается и не заявляется. ВАЖНО: value_mm — это ГЕОМЕТРИЯ размера (для " +
                "диаметрального и радиального — РАДИУС в миллиметрах листа, БЕЗ учёта масштаба вида), а НЕ номинал; " +
                "число, которое показывает размер, отдаётся отдельным полем nominal_mm (диаметр для диаметрального, " +
                "радиус для радиального, расстояние для линейного), рядом — caption (текст подписи) и " +
                "caption_overridden (число переопределено, а не вычислено). Три последних поля читаются " +
                "документированным IDimensionText и пусты, если объект на него не отвечает; число берётся из ТЕКСТА " +
                "подписи, потому что IDimensionText.NominalValue перечитывается как собственное значение ядра " +
                "(листовой радиус, удвоенный у диаметрального), а не как показанное число.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("view_ref", Sch.Ref("#/$defs/reference")),
                    ("limit", Sch.Nullable(Sch.Num(
                        "Предел числа возвращаемых размеров. Не задано — умолчание сервера.",
                        exclusiveMin: true, exclusiveMinValue: 0d)))),
                WorkerCommands.ListDimensions,
                requiresDocument: true),

            Mutation("kompas_add_dimension", "Поставить размер на виде",
                                "Линейный, радиальный или диаметральный размер в выбранном виде через ISymbols2DContainer, " +
                "полученный из IView: LineDimensions.Add(), RadialDimensions.Add(), DiametralDimensions.Add() - " +
                "каждый Add() без аргументов возвращает объект, координаты которого задаются затем. ОГРАНИЧЕНИЕ " +
                "НАЗВАНО ПРЯМО: размер привязан к ТОЧКАМ вида в координатах вида, а не к топологии модели; " +
                "АССОЦИАТИВНОСТЬ РАЗМЕРА К МОДЕЛИ НЕ ЗАЯВЛЯЕТСЯ. Ответ - размер, ПЕРЕЧИТАННЫЙ из объекта (значение, " +
                "точки, признак Valid), а не эхо запроса. Изменение модели влечёт перечитывание вида, но НЕ " +
                "обновление размера. КООРДИНАТЫ И МАСШТАБ: точки размера - в миллиметрах ВИДА (модели), а радиус " +
                "диаметрального и радиального ядро берёт в миллиметрах ЛИСТА; сервер умножает радиус на масштаб " +
                "вида, поэтому value_mm задаётся в миллиметрах модели. Число размера ядро выводит из листового " +
                "радиуса, поэтому при масштабе ≠ 1:1 сервер задаёт его через IDimensionText.NominalValue и тем " +
                "снимает автономинал: в таком виде число диаметрального и радиального размера задаётся явно и за " +
                "геометрией НЕ следует. Созданный " +
                "размер перечитывается: расхождение центра, радиуса или числа с заданными - ОТКАЗ, а не успех.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("view_ref", Sch.Ref("#/$defs/reference")),
                    ("dimension_type", Sch.Enum(
                        "Тип размера. Перечень проверяется ДО обращения к КОМПАС.",
                        "linear", "radial", "diametral")),
                    ("point1", Sch.Vec3(
                        "Первая точка привязки линейного размера; для радиального и диаметрального — "
                        + "центр измеряемой дуги или окружности, в координатах вида.")),
                    ("point2", Sch.Nullable(Sch.Vec3(
                        "Вторая точка привязки линейного размера. Для радиального и диаметрального "
                        + "игнорируется (справка описывает центр Xc/Yc и радиус, а не вторую точку)."))),
                    ("position", Sch.Nullable(Sch.Vec3(
                        "Положение размерной линии: для линейного — X3/Y3, для радиального и "
                        + "диаметрального — точка полки. Не задано — член не пишется, и действует "
                        + "умолчание ядра."))),
                    ("value_mm", Sch.Nullable(Sch.Num(
                        "Значение размера, мм: для радиального и диаметрального — радиус. Не задано — "
                        + "радиус выводится из point2 (точки на окружности); для линейного значение "
                        + "читается как расстояние между точками.",
                        exclusiveMin: true, exclusiveMinValue: 0d)))),
                WorkerCommands.AddDimension,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_set_title_block", "Заполнить основную надпись",
                                "Записывает значения в ячейки основной надписи через ILayoutSheet.Stamp → IStamp: IStamp.Text(Id) " +
                "ВОЗВРАЩАЕТ IText (чтение — не установщик), запись идёт через IText.Str, затем IStamp.Update(). " +
                "ГРАНИЦА, НАЗВАННАЯ ПРЯМО: справка SDK НЕ документирует числовые идентификаторы ячеек — " +
                "соответствие «наименование / обозначение / материал / масштаб / разработал / проверил» номерам " +
                "ячеек в ней отсутствует. Поэтому номера ячеек задаёт вызывающий (cells = {«<номер>»: «значение»}), " +
                "а сервер ПЕРЕЧИТЫВАЕТ каждую ячейку после Update() и возвращает признак совпадения; несовпадение " +
                "не скрывается, а попадает в unverified_aspects. Ключ, не являющийся числом, отвергается " +
                "INVALID_ARGUMENT, потому что Text(Id) принимает Int32.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("cells", Sch.Map(
                        Sch.Str("Текст ячейки."),
                        "Объект «номер ячейки» → «текст»: ключ — строковый номер ячейки (Int32), "
                        + "значение — строка текста. Соответствие понятий (наименование, обозначение, …) "
                        + "номерам справка не документирует; номера берутся из раскладки самой основной "
                        + "надписи."))),
                WorkerCommands.SetTitleBlock,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_export_drawing", "Выгрузить чертёж в DXF или DWG",
                                "Экспорт чертежа документированным маршрутом конвертера: IApplication.Converter → " +
                "IConverter.GetFilter(docType, saveAs, out command) → IConverter.Convert(InputFile, Outfile, " +
                "Command, ShowParam). Справка задаёт результат Convert прямо: 1 — успешное завершение, 0 — неудача. " +
                "Опубликованы ТОЛЬКО форматы, для которых справка называет программный маршрут: dxf и dwg " +
                "(iconverter_getfilter.html: FORMAT_DXF=1, FORMAT_DWG=2). PDF в справке SDK НЕ описан как " +
                "программный маршрут и отвергается FORMAT_UNAVAILABLE по имени, а не предпринимается вслепую. " +
                "Результат подтверждается НЕЗАВИСИМОЙ проверкой файла: существование, непустой размер и форма " +
                "содержимого — конвертер v24 пишет выгрузку ZIP-контейнером (сигнатура «PK»), и это НАЗВАНО в " +
                "ответе, как и обычные сигнатуры (заголовок секции для DXF, «AC10xx» для DWG). Так " +
                "документированный успех, не давший файла, не проходит, а документированная неудача не превращается " +
                "в успех из-за наличия файла. Перезапись существующего файла возможна только с явным overwrite=true.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("output_path", Sch.Ref("#/$defs/output_path")),
                    ("format", Sch.Enum(
                        "Формат выгрузки. PDF не документирован как программный маршрут и не принимается.",
                        "dxf", "dwg")),
                    ("overwrite", Sch.Nullable(Sch.Bool(
                        "Перезаписать существующий файл. По умолчанию false: перезапись без явного "
                        + "согласия — молчаливая потеря данных.", false)))),
                WorkerCommands.ExportDrawing,
                requiresDocument: true),

            Mutation("kompas_edit_view", "Изменить вид чертежа",
                "Изменяет СУЩЕСТВУЮЩИЙ вид документированными записываемыми свойствами: масштаб "
                + "(IView.Scale) и точку привязки (IView.X / IView.Y). Справка v24 задаёт их прямо "
                + "записываемыми и требует IDrawingObject.Update: «Свойство вступает в силу после вызова "
                + "метода IDrawingObject::Update» (iview_scale.html, iview_x.html, iview_y.html, "
                + "idrawingobject_update.html). Ответ даёт вид ДО и ПОСЛЕ и признак совпадения "
                + "перечитанных полей с заданными: положительное доказательство — ИЗМЕНЕНИЕ И "
                + "ПЕРЕЧИТЫВАНИЕ того же объекта, а не добавление нового вида. Не заданное свойство не "
                + "пишется (null — «не трогать», а не выдуманное умолчание). После правки габарит вида "
                + "сверяется с местом внутри рамок листа: views_within_sheet с числами, при выходе за "
                + "рамку — предупреждение view_outside_sheet с именем вида (вид не переносится). "
                + "Смена масштаба вида, в котором уже есть размеры, ОТВЕРГАЕТСЯ до изменения документа "
                + "(CAPABILITY_UNAVAILABLE, в details — имя вида и число размеров): листовой радиус "
                + "существующих размеров ядром не пересчитывается, и их геометрия разошлась бы с "
                + "масштабированной проекцией. Обход назван в тексте отказа: удалить размеры вида, "
                + "сменить масштаб, проставить размеры заново. Смена только точки привязки и смена "
                + "масштаба вида без размеров работают как прежде. "
                + "Отдельно: подавление вида "
                + "(IView.Visible, «видимый или погашенный») и удаление (IDrawingObject.Delete) "
                + "документированы, но этим инструментом НЕ выполняются — наличие маршрута изменения "
                + "масштаба их не подтверждает.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("view_ref", Sch.Ref("#/$defs/reference")),
                    ("scale", Sch.Nullable(Sch.Num(
                        "Новый масштаб вида. Не задано — масштаб не трогается.",
                        exclusiveMin: true, exclusiveMinValue: 0d))),
                    ("x", Sch.Nullable(Sch.Num("Новая координата привязки по X, в миллиметрах чертежа."))),
                    ("y", Sch.Nullable(Sch.Num("Новая координата привязки по Y, в миллиметрах чертежа.")))),
                WorkerCommands.EditView,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_rebuild_drawing_views", "Перестроить чертёж после изменения модели",
                "Перестраивает чертёж документированным маршрутом API7, чтобы ассоциативный вид заново "
                + "построил проекцию из изменённой модели: IDrawingDocument приводится к "
                + "IKompasDocument2D1, вызывается RebuildDocument() (справка v24: "
                + "ikompasdocument2d1_rebuilddocument.html — «Метод позволяет перестроить документ», "
                + "TRUE при успехе). ИЗМЕРЕНО ОТРАЖЕНИЕМ по поставляемому interop: "
                + "IKompasDocument2D1.RebuildDocument() объявлен и возвращает Boolean, тогда как "
                + "IDrawingDocument.RebuildViews (названный справкой) НЕ объявлен НИ ОДНИМ типом сборки "
                + "— отсутствие названо, а не выдано за рабочий маршрут. Ответ несёт возвращённое "
                + "значение (ПРОВЕРКА, а не вердикт) и вид, ПЕРЕЧИТАННЫЙ из свежего чтения коллекции: "
                + "перестроение подтверждается перечитыванием, а не булевым результатом вызова. "
                + "ГРАНИЦА: сверка геометрии проекции с изменённой моделью сервером не выполняется "
                + "(IView габарит не публикует).",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("view_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.RebuildDrawingViews,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_set_technical_demand", "Записать технические требования чертежа",
                                "Пишет блок технических требований документированным маршрутом API7: " +
                "IDrawingDocument.TechnicalDemand → ITechnicalDemand.Text (read-only свойство, отдающее интерфейс " +
                "IText) → IText.Str (запись) → ITechnicalDemand.Update() («применить заданные параметры технических " +
                "требований») → ПЕРЕЧИТЫВАНИЕ. Ответ несёт перечитанный текст и признак IsCreated («отображение " +
                "технических требований в документе»): успех Update() сам по себе доказательством не считается. " +
                "РАЗМЕЩЕНИЕ БЛОКА: применяется документированное авторазмещение (ITechnicalDemand.AutoPlacement, " +
                "itechnicaldemand_autoplacement.html), затем габариты блока (BlocksGabarits, " +
                "itechnicaldemand_blocksgabarits.html) сверяются со свободным местом листа " +
                "(ILayoutSheet.GetPlaceInsideFrames, ilayoutsheet_getplaceinsideframes.html); проверка " +
                "demand_within_sheet называет числа. Выход блока за пределы листа — явное предупреждение " +
                "demand_outside_sheet в unverified_aspects, а не молчаливый успех.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("text", Sch.Str(
                        "Полный текст технических требований. Строки разделяются переводом строки."))),
                WorkerCommands.SetTechnicalDemand,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_get_title_block", "Прочитать ячейки основной надписи",
                                "ЧИТАЕТ ячейки основной надписи и НИЧЕГО не записывает: IStamp.Text(Id) — свойство только для " +
                "чтения, отдающее IText, а IText.Str читается без изменения. Отдельный маршрут чтения нужен потому, " +
                "что установщик не свидетельствует сохранность: kompas_set_title_block СНАЧАЛА присваивает " +
                "IText.Str, и перечитывание совпадёт даже на документе, потерявшем значение при " +
                "сохранении/переоткрытии. Этот инструмент не несёт ожидаемого значения вовсе, поэтому годится как " +
                "независимая проверка «что реально лежит в документе сейчас». ГРАНИЦА: справка SDK НЕ документирует " +
                "соответствие номеров ячеек понятиям (наименование, обозначение, …) — номера задаёт вызывающий.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("cell_ids", Sch.Arr(
                        Sch.Str("Строковый номер ячейки (Int32 для IStamp.Text(Id))."),
                        "Номера ячеек для чтения. Пустой список ничего не читает."))),
                WorkerCommands.GetTitleBlock,
                requiresDocument: true),

            ReadOnly("kompas_get_technical_demand", "Прочитать технические требования",
                                "ЧИТАЕТ блок технических требований и НИЧЕГО не записывает: IDrawingDocument.TechnicalDemand → " +
                "ITechnicalDemand.Text (свойство только для чтения, отдающее IText) → IText.Str (чтение); IsCreated " +
                "— «отображение технических требований в документе». Отдельный маршрут чтения нужен потому, что " +
                "kompas_set_technical_demand СНАЧАЛА перезаписывает текст, и перечитывание совпадёт даже на " +
                "документе, потерявшем блок при сохранении/переоткрытии. Этот инструмент не несёт ожидаемого текста " +
                "и потому служит независимой проверкой. РАЗМЕЩЕНИЕ: читаются признак авторазмещения и габариты " +
                "блока и сверяются со свободным местом листа (ITechnicalDemand.AutoPlacement/BlocksGabarits, " +
                "ILayoutSheet.GetPlaceInsideFrames) — ничего не записывается.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.GetTechnicalDemand,
                requiresDocument: true),

            // ── block VM "variables and material" (profile variables-material-minimal-v1) ──
            ReadOnly("kompas_list_variables", "Внешние переменные детали",
                                "ЧИТАЕТ внешние параметрические переменные ВЕРХНЕГО компонента детали и ничего не записывает: " +
                "IPart.VariableCollection() отдаёт «указатель на интерфейс массива внешних переменных», " +
                "перечисление — GetCount()/GetByIndex(), чтение — IVariable.name (только чтение), IVariable.value " +
                "(double) и IVariable.Expression (строка). ГРАНИЦА ОБЛАСТИ: это ВНЕШНИЕ переменные компонента, а НЕ " +
                "редактор всех параметров модели; поле scope отвечает «external/top_part» именно поэтому. Поле с " +
                "непрочитанным значением остаётся null с причиной в diagnostics — нулём или пустой строкой оно не " +
                "подменяется. Пустая коллекция — допустимое состояние и названа полем collection_empty; недоступная " +
                "коллекция пустым списком НЕ притворяется и приводит к отказу. Чтение не меняет ревизию и не " +
                "требует expected_revision.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("limit", Sch.Nullable(Sch.Int(
                        "Сколько переменных прочитать. Полный размер коллекции возвращается полем total "
                        + "всегда, поэтому усечённый ответ не примут за короткую коллекцию.",
                        min: 0,
                        max: 5000)))),
                WorkerCommands.ListVariables,
                requiresDocument: true),

            Mutation("kompas_set_variable", "Изменить внешнюю переменную",
                                "МЕНЯЕТ значение ИЛИ выражение одной внешней переменной детали, адресуя её ТОЧНЫМ именем. Порядок: " +
                "сначала проверяются документ, ревизия и наличие ИМЕННО этой переменной, затем выполняется запись " +
                "документированного свойства (IVariable.value или IVariable.Expression), затем обязательная " +
                "перестройка ksDocument3D.RebuildDocument, после чего значение и выражение ПЕРЕЧИТЫВАЮТСЯ из ВНОВЬ " +
                "ПОЛУЧЕННОЙ коллекции. ИЗМЕРЕНО: ksPart.RebuildModel, чья страница обещает передать внешние " +
                "переменные в модель, геометрию НЕ двигает и после перестройки документа возвращает прежнюю " +
                "глубину; перестраивает именно RebuildDocument. Ровно одно из value и expression; оба сразу или ни " +
                "одного — отказ до COM. В режиме value переменная, чьё выражение СЧИТАЕТ значение (формула или " +
                "ссылка), ОТКЛОНЯЕТСЯ с возвратом текущего выражения: запись числа уничтожила бы его молча. " +
                "Постоянное выражение-число — не формула, а само значение, поэтому такая переменная принимает " +
                "запись числа (ИЗМЕРЕНО: ядро обновляет её выражение до записанного числа). Возврат перестройки = " +
                "TRUE — исход вызова, а НЕ подтверждение результата: подтверждением служит перечитанное значение и " +
                "независимое измерение. Выражение считает КОМПАС; собственный вычислитель сервер не подставляет и " +
                "строку как код не исполняет.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("name", Sch.Str(
                        "Точное имя внешней переменной. Сравнивается ПОЛНОСТЬЮ и С УЧЁТОМ РЕГИСТРА "
                        + "(GetByName(name, testFullName=TRUE, testIgnoreCase=FALSE)); поиск подстрокой "
                        + "не выполняется.",
                        minLength: 1)),
                    ("value", Sch.Nullable(Sch.Num(
                        "Новое числовое значение в собственной размерности переменной. Только конечное "
                        + "число. Допустимо лишь для переменной БЕЗ активного выражения."))),
                    ("expression", Sch.Nullable(Sch.Str(
                        "Новое выражение; его считает КОМПАС. Пустая строка снимает выражение.")))),
                WorkerCommands.SetVariableValue,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_get_material", "Материал и плотность детали",
                                "ЧИТАЕТ обозначение материала верхнего компонента детали и его ФИЗИЧЕСКУЮ плотность. Имя — " +
                "ksPart.material (свойство ТОЛЬКО ДЛЯ ЧТЕНИЯ; справка прямо говорит «Обозначение материала можно " +
                "получить только у детали»). Плотность — документированный маршрут МЦХ API5: " +
                "ksPart.CalcMassInertiaProperties(ST_MIX_M|ST_MIX_KG).r, где единицу возвращаемых данных задаёт " +
                "АРГУМЕНТ вызова. При ST_MIX_M|ST_MIX_KG плотность публикуется в кг/м³ БЕЗ пересчёта сервером — " +
                "поля density_kg_per_m3, density_unit, density_route. ИЗМЕРЕНО: единицу задаёт АРГУМЕНТ вызова " +
                "документированного маршрута, сервер значение не пересчитывает, и оно переживает сохранение и " +
                "переоткрытие. ГРАНИЦА: чтение имени и чтение плотности — ДВА разных исхода; успешное чтение имени " +
                "не означает успешного чтения плотности, и они сообщаются раздельно. Нулевая или нечисловая " +
                "плотность, а также деталь без тела дают НАЗВАННЫЙ отказ, а не измеренную плотность; плотность из " +
                "справочника сервера НЕ подставляется. Поле get_density_raw_diagnostic несёт СТАРЫЙ getter " +
                "ksPart.GetDensity() как ДИАГНОСТИКУ: его страница называет г/куб.мм, а ядро отдаёт значение, " +
                "согласованное с г/куб.см (измерено), поэтому оно НЕ публикуется как плотность.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.GetMaterial,
                requiresDocument: true),

            Mutation("kompas_set_material", "Назначить материал и плотность",
                                "НАЗНАЧАЕТ материал и плотность верхнего компонента детали: ksPart.SetMaterial(name, density) и " +
                "затем обязательный ksPart.Update («Изменение материала вступает в силу после вызова метода " +
                "ksPart::Update»), после чего имя и плотность ПЕРЕЧИТЫВАЮТСЯ. ЕДИНИЦЫ: SetMaterial документирован в " +
                "г/куб.см, и сервер переводит вход по единственному документированному переводу (7850 кг/м³ → 7.85 " +
                "г/см³). Плотность подтверждается ПЕРЕЧИТЫВАНИЕМ тем же документированным маршрутом МЦХ " +
                "(ksPart.CalcMassInertiaProperties(ST_MIX_M|ST_MIX_KG).r), который отдаёт кг/м³ — ту же единицу, " +
                "что запрошена, поэтому сравнение одноимённое: поля read_density_kg_per_m3_after и density_matches " +
                "с допуском density_tolerance_kg_per_m3. Имя материала подтверждается отдельно (name_matches); " +
                "несовпавшая или непрочитанная плотность подтверждения НЕ даёт. ГРАНИЦЫ (документированы справкой): " +
                "компонент должен быть деталью и не должен быть деталью из библиотеки моделей или стандартным " +
                "элементом; в детали метод действует на ВЕРХНИЙ компонент и на подсборку не распространяется. Ответ " +
                "строится ПЕРЕЧИТЫВАНИЕМ, а не эхом запроса: запрошенные значения лежат в отдельных полях, чтобы " +
                "расхождение было видно, а не сглажено.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("material_name", Sch.Str(
                        "Обозначение материала как оно записывается в деталь.",
                        minLength: 1)),
                    ("density_kg_per_m3", Sch.Num(
                        "Плотность материала, кг/м³. Только конечное положительное значение. Перед вызовом "
                        + "сервер переводит её в документированную для SetMaterial единицу г/см³.",
                        exclusiveMin: true,
                        exclusiveMinValue: 0d))),
                WorkerCommands.SetMaterial,
                requiresDocument: true,
                requiresRevision: true),

            // ── block G3 "variable creation and parameter binding" (profile variables-bind-minimal-v1) ──
            Mutation("kompas_create_variable", "Создать переменную детали",
                                "СОЗДАЁТ именованную переменную верхнего компонента детали документированным маршрутом API7: " +
                "IPart7.AddVariable(Name, Value, Note) → IVariable7, затем .External и .Expression. Третий аргумент " +
                "AddVariable - ПРИМЕЧАНИЕ, а не формула; выражение задаётся отдельным свойством. Порядок: проверка " +
                "имени IsVariableNameValid ДО записи → отказ, если переменная с таким именем уже есть (перезапись " +
                "не выполняется) → AddVariable → External → Expression → ksDocument3D.RebuildDocument → " +
                "ПЕРЕЧИТЫВАНИЕ из заново полученной коллекции. Пустое expression ядром НЕ поддерживается (запись " +
                "сообщает об успехе, выражение остаётся прежним, переменная покидает коллекцию), поэтому если " +
                "выражение не задано, ставится выражение-константа, равное значению. Ответ строится ПЕРЕЧИТЫВАНИЕМ, " +
                "а не эхом запроса; случаи «создана, но не внешняя» и «создана, но нет в коллекции» - НАЗВАННЫЕ " +
                "отказы с последствиями, а не ложный успех. Выражение считает КОМПАС.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("name", Sch.Str(
                        "Имя переменной: латинские буквы, цифры и «_», первый символ - буква или «_» "
                        + "(проверяется ядром через IPart7.IsVariableNameValid ДО записи).",
                        minLength: 1)),
                    ("value", Sch.Num(
                        "Числовое значение в собственной размерности переменной. Только конечное число.")),
                    ("note", Sch.Nullable(Sch.Str(
                        "Примечание переменной - третий аргумент AddVariable. Не формула."))),
                    ("expression", Sch.Nullable(Sch.Str(
                        "Выражение, которое считает КОМПАС. Если не задано, ставится константа, равная value: "
                        + "пустое выражение ядром не поддерживается."))),
                    ("external", Sch.Nullable(Sch.Bool(
                        "Делать переменную внешней. Внешняя переменная видна в kompas_list_variables и "
                        + "может управлять параметром операции. Если поле не задано, переменная внешняя.",
                        true)))),
                WorkerCommands.CreateVariable,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_list_feature_parameters", "Параметры операции",
                                "ЧИТАЕТ переменные-параметры ОДНОЙ операции и ничего не записывает: признак (ссылка из " +
                "kompas_list_features) → ksEntity.GetFeature() → ksFeature.VariableCollection → перечисление " +
                "GetCount/GetByIndex и чтение ksVariable.name, displayName, parameterNote, value, Expression, " +
                "external. На строку отдаются имя, отображаемое имя, ИМЯ ПАРАМЕТРА (parameterNote), значение, " +
                "выражение, признак внешней переменной и порядковый номер. АДРЕС ПАРАМЕТРА - ЕГО ИМЯ (поле name), а " +
                "НЕ значение и не позиция: два параметра с равным значением по значению неразличимы. Непрочитанное " +
                "поле остаётся null с причиной, а не нулём или пустой строкой. Признак без переменных - пустой " +
                "список с основанием, а не отказ; признак, у которого GetFeature не получен, - " +
                "CAPABILITY_UNAVAILABLE с причиной; устаревшая ссылка - STALE_REFERENCE. Чтение не меняет ревизию и " +
                "не требует expected_revision.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("feature_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.ListFeatureParameters,
                requiresDocument: true),

            Mutation("kompas_bind_parameter", "Привязать параметр операции",
                "ЗАПИСЫВАЕТ выражение параметра операции, адресуя параметр его ТОЧНЫМ именем (поле name из "
                + "kompas_list_feature_parameters), а НЕ значением или позицией: два параметра с равным "
                + "значением по значению неразличимы. Порядок: найти параметр по точному имени в коллекции "
                + "признака (ноль или несколько совпадений - INVALID_ARGUMENT с перечнем имён, запись НЕ "
                + "происходит) → записать ksVariable.Expression → ksDocument3D.RebuildDocument → заново "
                + "получить признак и коллекцию → прочитать Expression и вычисленное значение → по "
                + "возможности измерить объём модели и сверить с expected_volume_mm3 (справка v24: "
                + "variables_in_tree.html - «В ячейке Выражение введите … ссылку на другую переменную», "
                + "ksvariable_expression.html, ksdocument3d_rebuilddocument.html). "
                + "«Записано» и «применилось» - разные утверждения: ответ несёт expression_read_back, "
                + "value_after, rebuild_succeeded и volume_delta; если выражение прочиталось иначе, значение "
                + "не вычислилось или перестроение не удалось - НАЗВАННЫЙ отказ с состоянием модели "
                + "(revision_after в details), а не succeeded. Ссылка на несуществующую переменную - ошибка "
                + "выражения: ядро его не примет или не вычислит. Пустое expression отвергается до COM. "
                + "СНЯТИЕ привязки - expression задаётся числом (строкой): параметр становится константой; "
                + "отдельный режим не вводится. Выражение считает КОМПАС; собственного вычислителя сервер "
                + "не содержит и строку как код не исполняет."
                + DeclaredExpectationRule,
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("feature_ref", Sch.Ref("#/$defs/reference")),
                    ("parameter_name", Sch.Str(
                        "Точное name параметра из kompas_list_feature_parameters. Сравнивается посимвольно; "
                        + "ноль или несколько совпадений - отказ без записи.",
                        minLength: 1)),
                    ("expression", Sch.Str(
                        "Непустое выражение: имя переменной, формула или числовая константа. Константа снимает "
                        + "привязку. Выражение считает КОМПАС.",
                        minLength: 1)),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Ожидаемый объём модели после привязки, мм³. Только конечное число.")))),
                WorkerCommands.BindParameter,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_probe_units", "Замер единиц",
                "Строит известную геометрию и возвращает сырые показания всех измерительных вызовов. Калибровка, а не догадка: именно так подтверждалось, что GetLength(0) — сантиметры.",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("known_length_mm", Sch.PositiveMm("Длина отрезка, который будет нарисован")),
                    ("known_radius_mm", Sch.PositiveMm("Радиус окружности, которая будет нарисована"))),
                WorkerCommands.UnitProbe,
                requiresDocument: false),

            ReadOnly("kompas_read_topology", "Топология тела",
                "Грани и рёбра из конечного тела (GetMainBody→FaceCollection→EdgeCollection), а не из EntityCollection: коллекция рёбер модели содержит эскизные и служебные контуры.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("body_ref", Sch.Ref("#/$defs/reference")),
                    ("include", Sch.Enum("Что читать.", "faces", "edges", "both"))),
                WorkerCommands.ReadTopology,
                requiresDocument: true),

            // read_topology and resolve_selection mint structural handles but change no document or
            // CAD state — reads per docs/02 §2.1, which requires operation_id of mutations only.
            // Declaring them through Mutation() with requiresOperationId:false made IsMutation true
            // while the schema never published the field, so every legitimate call died in the journal.
            ReadOnly("kompas_resolve_selection", "Однозначный выбор",
                                "Структурный предикат по граням тела: surface_type, area_range_mm2, normal_direction, " +
                "normal_angle_tolerance_deg - это весь предикат; нормаль задаётся в системе координат детали, " +
                "которой принадлежит тело. Два подходящих кандидата - AMBIGUOUS_SELECTION, а не молчаливый выбор " +
                "первого. Поле вне набора отвергается схемой (additionalProperties=false) с INVALID_ARGUMENT, а не " +
                "игнорируется.",
                Sch.Props(
                    ("body_ref", Sch.Ref("#/$defs/reference")),
                    ("predicate", Sch.Ref("#/$defs/selection_predicate")),
                    ("require_unique", Sch.Bool("Требовать ровно одного совпадения.", true)),
                    ("limit", Sch.Ref("#/$defs/limit"))),
                WorkerCommands.ResolveSelection),

            Mutation("kompas_create_aux_geometry", "Создать плоскость, ось или точку детали",
                                "Создаёт объект вспомогательной геометрии КАК ОБЪЕКТ МОДЕЛИ, а не числа в аргументах чужого вызова: " +
                "смещённую или наклонную плоскость, ось по двум точкам (либо по цилиндрической поверхности, либо по " +
                "ребру), точку по координатам или смещением от опорной вершины. Маршрут - документированный API7 " +
                "(IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D, IModelContainer.GetPoints3D, IPlanes3D.Add / " +
                "IAxes3D.Add / IPoints3D.Add; типы obj3dtype.html: o3d_planeOffset=14, o3d_planeAngle=15, " +
                "o3d_axis2Points=10, o3d_axisConeFace=11, o3d_axisEdge=12, o3d_point3D=70). ВИД И СПОСОБ РАЗДЕЛЕНЫ: " +
                "kind - «что это» (plane/axis/point), mode - «как построено» " +
                "(offset/angle/by_2_points/by_face/by_edge/coordinates/displace); сочетание, которого нет, " +
                "отвергается INVALID_ARGUMENT со списком поддержанных. sub_kind в ответе - НАТИВНЫЙ тип КОМПАСа " +
                "(o3d_planeOffset, o3d_axis2Points, ksPDisplace), а не способ. ПОЛЯ ЧУЖИХ СПОСОБОВ НЕ ИГНОРИРУЮТСЯ: " +
                "поле вне набора своего способа отвергается INVALID_ARGUMENT с foreign_fields и allowed_fields - " +
                "принятое и проигнорированное поле доживает до приёмки, выглядя как выполненная работа. ОПОРА " +
                "ПЛОСКОСТИ - РОВНО ОДИН СПОСОБ: base_plane (xy/xz/yz) ЛИБО base_face_ref, обе сразу - отказ. " +
                "Базовая плоскость ищется в коллекции ПО ТИПУ ОБЪЕКТА (ModelObjectType), а не по позиции. Для " +
                "plane/angle базовая прямая задаётся base_axis_ref. ОТВЕТ - ПРОЧИТАННОЕ ИЗ МОДЕЛИ, а не пересказ " +
                "запроса: и числа, и САМ СПОСОБ берутся тем же маршрутом чтения, которым их увидит клиент. " +
                "Прочитанный mode может не совпасть с запрошенным - тогда mode_read_back идёт с passed=false и " +
                "обеими величинами; mode=null означает, что нативный способ в словаре запроса не назван (причина в " +
                "diagnostics, нативный тип в sub_kind). Пустое числовое поле означает «не прочитано» и названо в " +
                "diagnostics. Создание подтверждается перестроением и поднимает ревизию. УДАЛИТЬ объект - " +
                "kompas_delete_feature по reference_id из ответа. РАЗМЕЩЕНИЕ ПРОВЕРЯЕТСЯ: для plane/angle " +
                "прочитанные угол и базовая прямая сверяются с запрошенными, расхождение - GEOMETRY_FAILED " +
                "(details.code = plane_on_other_angle или plane_on_other_base_line) со ссылкой на оставшийся " +
                "объект; непрочитанное значение - пробел, а не отказ.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("kind", Sch.Enum("Что создаётся.", "plane", "axis", "point")),
                    ("mode", Sch.Enum("Как построено. Набор полей зависит от способа.",
                        "offset", "angle", "by_2_points", "by_face", "by_edge",
                        "coordinates", "displace")),
                    ("offset_mm", Sch.Nullable(Sch.Num("Смещение вдоль нормали базовой опоры, мм. Только plane/offset."))),
                    ("angle_deg", Sch.Nullable(Sch.Num("Угол наклона относительно базовой плоскости, градусы. Только plane/angle."))),
                    ("direction", Sch.Nullable(Sch.Bool("Знак направления: для смещённой плоскости — вдоль нормали или против, для наклонной — сторона отсчёта угла. Не задан — берётся умолчание КОМПАСа, а не наше."))),
                    ("base_plane", Sch.Nullable(Sch.Enum("Базовая плоскость именем. Взаимоисключающе с base_face_ref.", "xy", "xz", "yz"))),
                    ("base_axis_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("base_face_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("point1_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("point2_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("face_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("edge_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("coordinates_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("association_vertex_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("displacement_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
                    ("name", Sch.Nullable(Sch.Str("Имя объекта. Имя — не адрес: адресом служит ссылка, выданная перечислением.", maxLength: 200)))),
                WorkerCommands.CreateAuxGeometry,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_list_aux_geometry", "Вспомогательная геометрия детали",
                "Перечисляет и читает плоскости, оси и точки детали как ОБЪЕКТЫ МОДЕЛИ с их "
                + "параметрами: у плоскости — смещение или угол, опора, знак направления, точка и "
                + "нормаль поверхности (IPlane3D.Surface → MathSurface3D.GetPoint/GetNormal); у оси — "
                + "вершины и опорный объект; у точки — координаты и опорный объект. "
                + "СТРОКА НЕСЁТ ТРИ РАЗНЫХ ПО СМЫСЛУ ПОЛЯ: kind — ЧТО это (plane/axis/point), mode — "
                + "КАК построено, в том же словаре, что принимает kompas_create_aux_geometry "
                + "(offset/angle/by_2_points/by_face/by_edge/coordinates/displace), sub_kind — "
                + "НАТИВНЫЙ тип КОМПАСа (o3d_planeOffset, o3d_axis2Points, ksPDisplace и т. п.). "
                + "mode=null означает, что нативный способ построения в словаре запроса не назван "
                + "(объект построен не этим сервером или способом, которого инструмент не предлагает): "
                + "тогда он назван в sub_kind и в notes, а не угадан по имени объекта. Значения mode "
                + "здесь ТЕ ЖЕ, что у ответа создания. "
                + "МАРШРУТ: IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D и IModelContainer.GetPoints3D "
                + "с перечислением по Count и индексированному свойству. "
                + "ИМЯ КАК АДРЕС РАЗРЕШАЕТСЯ ПЕРЕЧИСЛЕНИЕМ, И ЭТО ИЗМЕРЕНО: справка документирует "
                + "IAxes3D.GetAxis3DByName, IPoints3D.GetPoint3DByName и ISketchs.GetSketchByName, но "
                + "в поставленном Interop.KompasAPI7.dll целевой сборки нет НИ ОДНОГО члена с "
                + "подстрокой ByName (измерено прибором tools/KompasMcp.InteropScan 21.09.2026), "
                + "поэтому имя сопоставляется по документированным членам Count + индексированное "
                + "свойство + Name. ВОЗВРАЩАЕМЫЙ INDEX УСТОЙЧИВЫМ АДРЕСОМ НЕ ЯВЛЯЕТСЯ: перестроение "
                + "его сдвигает, и он так и не называется. "
                + "ИМЯ НЕ УНИКАЛЬНО — AMBIGUOUS_SELECTION, ИМЕНИ НЕТ — DOCUMENT_NOT_FOUND: "
                + "молчаливый выбор первого сделал бы адрес неоднозначным. "
                + "СТРОКА НЕСЁТ reference_id — ссылку на сам объект, ТОГО ЖЕ ВИДА (plane/axis/point), "
                + "который принимают инструменты-потребители: kompas_create_sketch полем reference у "
                + "опоры, kompas_create_aux_geometry полями base_axis_ref/base_face_ref, "
                + "kompas_update_plane полем plane_ref. Ссылки из перечня и ссылки из ответа создания "
                + "взаимозаменяемы: если у объекта уже есть живая ссылка в этом сеансе, возвращается "
                + "ОНА, а не выпускается вторая. Объект, который зарегистрировать нельзя, остаётся в "
                + "перечне с reference_id=null и причиной в notes — строка не пропадает. "
                + "ПУСТОЕ ПОЛЕ ОЗНАЧАЕТ «НЕ ПРОЧИТАНО», А НЕ НОЛЬ, и причина названа в notes. "
                + "УДАЛИТЬ объект — kompas_delete_feature по его reference_id.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("include", Sch.Enum("Что читать.", "planes", "axes", "points", "all")),
                    ("name", Sch.Nullable(Sch.Str("Имя одного объекта. Ищется перечислением коллекции.", maxLength: 200))),
                    ("limit", Sch.Nullable(Sch.Ref("#/$defs/limit")))),
                WorkerCommands.ListAuxGeometry,
                requiresDocument: true),

            Mutation("kompas_update_plane", "Изменить плоскость детали",
                "Меняет УЖЕ СОЗДАННУЮ плоскость детали как объект модели: смещение вдоль нормали, "
                + "угол наклона, знак направления, опору. Маршрут — документированные сеттеры API7: "
                + "IPlane3DByOffset.Offset, IPlane3DByAngle.Angle, IPlane3DBy*.BasePlane, затем "
                + "Update() и перестроение модели. "
                + "ПОЛЕ ОБЯЗАНО СООТВЕТСТВОВАТЬ ВИДУ ПЛОСКОСТИ, И ВИД БЕРЁТСЯ ИЗ МОДЕЛИ, А НЕ ИЗ "
                + "ЗАПРОСА: у смещённой плоскости (mode=offset) нет угла, у наклонной (mode=angle) нет "
                + "смещения; чужое поле отвергается INVALID_ARGUMENT с перечнем foreign_fields и "
                + "allowed_fields. Плоскость, способ построения которой в словаре запроса не назван, "
                + "отвергается CAPABILITY_UNAVAILABLE с нативным типом в sub_kind, а не правится по "
                + "догадке о виде. Ответ несёт kind (plane), mode и sub_kind так же, как создание и "
                + "перечисление. "
                + "ПРИЗНАК ПРИМЕНЕНИЯ ВЫВОДИТСЯ ИЗ ПОВТОРНОГО ЧТЕНИЯ тем же маршрутом, которым "
                + "значение увидит клиент: успешный код Update() применением не объявляется, и ответ "
                + "несёт прочитанное обратно значение вместе с applied_evidence. "
                + "ПРЕДЫСТОРИЯ, КОТОРУЮ ЭТОТ ИНСТРУМЕНТ ЗАКРЫВАЕТ: правка признака полем plane "
                + "ПРИНИМАЛАСЬ (код отказа отсутствовал) и геометрию НЕ меняла — измерено строкой "
                + "DEP.DPL.04.edit прежнего прогона. Здесь правка идёт отдельным маршрутом, а не "
                + "расширением чужого поля.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("plane_ref", Sch.Ref("#/$defs/reference")),
                    ("offset_mm", Sch.Nullable(Sch.Num("Новое смещение вдоль нормали, мм. Только для смещённой плоскости."))),
                    ("angle_deg", Sch.Nullable(Sch.Num("Новый угол наклона, градусы. Только для наклонной плоскости."))),
                    ("base_plane", Sch.Nullable(Sch.Enum("Новая опора именем.", "xy", "xz", "yz"))),
                    ("direction", Sch.Nullable(Sch.Bool("Знак направления: вдоль нормали или против, сторона отсчёта угла.")))),
                WorkerCommands.UpdatePlane,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_list_sketch_entities", "Сущности эскиза",
                                "Перечисляет сущности СУЩЕСТВУЮЩЕГО эскиза и читает ОДНУ из них по УСТОЙЧИВОМУ АДРЕСУ. Маршрут: " +
                "ISketch.BeginEditEx(true) → IKompasDocument2D.ViewsAndLayersManager.Views → IView → " +
                "IDrawingContainer.GetObjects(ksAllObj) → адрес IKompasDocument1.GetObjectId → ISketch.EndEdit(). " +
                "АДРЕС - строка GetObjectId, которую FindObjectById принимает обратно, поэтому она переживает " +
                "перестроение и переоткрытие; ИНДЕКС КОЛЛЕКЦИИ УСТОЙЧИВЫМ АДРЕСОМ НЕ ОБЪЯВЛЯЕТСЯ. Вход в эскиз " +
                "ТОЛЬКО НА ЧТЕНИЕ (BeginEditEx(true)): BeginEdit() открыл бы эскиз на запись. ЧТЕНИЕ НЕ ПОДНИМАЕТ " +
                "РЕВИЗИЮ. АДРЕС, КОТОРОГО НЕТ, - DOCUMENT_NOT_FOUND; адрес, разрешившийся в несколько сущностей, - " +
                "AMBIGUOUS_SELECTION. ПУСТОЕ ПОЛЕ ОЗНАЧАЕТ «НЕ ПРОЧИТАНО», А НЕ НОЛЬ: у графического объекта API7 " +
                "имени нет вовсе, и это названо в notes. ПОЛИЛИНИЯ И СПЛАЙН ПРИХОДЯТ ОДНИМ ОБЪЕКТОМ: строка " +
                "ksDrPolyline несёт entity_kind=polyline, points_count и closed (IPolyLine2D), строка ksDrBezier - " +
                "entity_kind=spline и те же поля (IBezier); у прочих видов они null. ВЕРШИНЫ КРИВЫХ ЧИТАЮТСЯ ЗДЕСЬ " +
                "ЖЕ: points_kind называет, ЧТО это за точки, - vertices у полилинии, through_points у сплайна Безье " +
                "(точки, через которые кривая ПРОХОДИТ; касательные не подмешиваются) и control_points у NURBS " +
                "(полюса, через которые кривая НЕ проходит). У НЕЗАМКНУТОЙ кривой start_mm и end_mm - первая и " +
                "последняя точка; у замкнутой они null. ПОЛНЫЙ СПИСОК - по полю include_points (по умолчанию " +
                "false): без него points_mm=null и points_complete=false; СПИСОК НЕ ОБРЕЗАЕТСЯ МОЛЧА, причина в " +
                "notes. СВОБОДНЫЕ КОНЦЫ одним чтением: x1_mm/y1_mm и x2_mm/y2_mm у отрезка и дуги, start_mm и " +
                "end_mm у незамкнутых полилинии и сплайна; конец без совпадающего конца другой сущности в допуске и " +
                "есть разрыв контура. ГЕОМЕТРИЯ, КОТОРУЮ ДВИГАЕТ УПРАВЛЯЮЩИЙ РАЗМЕР, ПУБЛИКУЕТСЯ ЗДЕСЬ ЖЕ: " +
                "length_mm (ILineSegment.Length), radius_mm (IArc/ICircle.Radius), angle_deg - наклон отрезка к оси " +
                "X в градусах (ILineSegment.Angle), поэтому угол МЕЖДУ двумя отрезками есть разность их наклонов. " +
                "Поле, неприменимое к виду, остаётся null - это «неприменимо», а не ноль.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("address", Sch.Nullable(Sch.Str("Адрес одной сущности, выданный этим же перечислением.", maxLength: 512))),
                    ("include_points", Sch.Nullable(Sch.Bool(
                        "Вернуть полный список точек полилинии или сплайна в points_mm. По умолчанию "
                        + "false: points_mm=null, points_complete=false, а start_mm/end_mm незамкнутой "
                        + "кривой читаются и так — их достаточно, чтобы найти свободные концы. Список "
                        + "никогда не обрезается молча."))),
                    ("limit", Sch.Nullable(Sch.Ref("#/$defs/limit")))),
                WorkerCommands.ListSketchEntities),

            Mutation("kompas_edit_sketch_entity", "Изменить сущность эскиза по адресу",
                "Правит РОВНО ОДНУ существующую сущность эскиза, названную УСТОЙЧИВЫМ АДРЕСОМ: "
                + "назначает номер слоя (action=set_layer) либо удаляет её (action=delete). "
                + "Маршрут: адрес разрешается IKompasDocument1.FindObjectById, затем ISketch.BeginEdit() "
                + "— НА ЗАПИСЬ, а не BeginEditEx(true) — IDrawingObject.LayerNumber / "
                + "IDrawingObject.Delete(), затем ISketch.EndEdit(). "
                + "ЭТО НЕ ТО ЖЕ, ЧТО РЕЖИМ delete_entities ЧУЖОЙ СХЕМЫ: delete_entities пересобирает "
                + "контур целиком, а здесь удаляется одна сущность по адресу и остальные остаются на "
                + "своих местах — это и есть различающий признак адресности. "
                + "ПРИМЕНЕНИЕ ПОДТВЕРЖДАЕТСЯ ПОВТОРНЫМ РАЗРЕШЕНИЕМ ТОГО ЖЕ АДРЕСА: у delete — тем, "
                + "что адрес больше не разрешается; у set_layer — прочитанным номером слоя. Код, "
                + "который не отказал, применением не объявляется, и ответ несёт поля resolved_after, "
                + "layer_after и счётчики сущностей до и после правки. "
                + "ПОЛЕ ЧУЖОГО ДЕЙСТВИЯ ОТВЕРГАЕТСЯ: layer_number при action=delete — "
                + "INVALID_ARGUMENT с перечнем foreign_fields.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("address", Sch.Str("Адрес сущности, выданный kompas_list_sketch_entities.", maxLength: 512)),
                    ("action", Sch.Enum("Что сделать с сущностью.", "set_layer", "delete")),
                    ("layer_number", Sch.Nullable(Sch.Int("Номер слоя. Обязателен при action=set_layer.", 0, 1_000_000)))),
                WorkerCommands.EditSketchEntity,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_create_sketch_constraint", "Наложить ограничение на объект эскиза",
                                "Ставит РОВНО ОДНО параметрическое ограничение на РОВНО ОДИН объект эскиза, названный устойчивым " +
                "адресом. Маршрут - API7: IDrawingObject1.NewConstraint() → IParametriticConstraint (свойства " +
                "задаются ДО Create()) → Create(). Объект берётся из контейнера эскиза (IView → QI " +
                "IDrawingContainer → GetObjects(ksAllObj)), а не поиском по точке: IView1.FindObject на целевой " +
                "установке не зарегистрирован (REGDB_E_CLASSNOTREG). ИМЯ ИНТЕРФЕЙСА - С ОПЕЧАТКОЙ ПРОДУКТА: в " +
                "справке и обёртке он называется ParametriticConstraint. ПРИМЕНЕНИЕ ПОДТВЕРЖДАЕТСЯ ПЕРЕЧИТЫВАНИЕМ " +
                "СПИСКА ОГРАНИЧЕНИЙ ОБЪЕКТА, а не возвратом Create(): объект-шаблон сохраняет значения по умолчанию " +
                "(Value = 0, Expression пусто) и после успешного создания, поэтому чтение из него выдало бы " +
                "созданный размер за пустой. Ответ несёт список ДО и ПОСЛЕ, состояние объекта и состояние эскиза. " +
                "ЯДРО ДВИГАЕТ ГЕОМЕТРИЮ: ksCHorizontal на отрезке 25 мм под 30° доворачивает его до 0° ВОКРУГ " +
                "СЕРЕДИНЫ отрезка, а не вокруг начала; сервер это движение не скрывает и не отменяет. ПОДТВЕРЖДЕНИЕ " +
                "ПО ГЕОМЕТРИИ: после Create()=true и действующей записи сервер перечитывает геометрию и проверяет " +
                "ОТНОШЕНИЕ, которое ограничение обязано установить - горизонталь и вертикаль дают 0° и 90°, " +
                "параллельность и перпендикулярность - угол между направлениями, коллинеарность - параллельность И " +
                "нулевое расстояние между прямыми, равенство - совпадение длин или радиусов, совпадение, " +
                "выравнивание и симметрия точек - расстояния между ними и до оси, касание - расстояние от центра до " +
                "прямой или сумма либо разность радиусов, совпадение центров - расстояние между центрами, " +
                "биссектриса - равенство углов с партнёром и с осью. Отношение не выполнено в допуске (длина 1e-6 " +
                "мм, угол 1e-6°) - GEOMETRY_FAILED с числами до и после. Отношение, не выражаемое числом " +
                "(закрепление точки, фиксация длины и угла, размерные типы), названо в unverified_aspects с " +
                "причиной. БИССЕКТРИСА (ksCBisector): объект - САМА биссектриса, партнёр и ось - две прямые; " +
                "постановка ИЗМЕРЕНА, но справкой v24 не описана. ЦЕНА ВНИМАНИЯ: ограничение после создания не " +
                "редактируется - по справке его нужно удалить и создать новое, поэтому действия «правка» нет. " +
                "ИЗМЕРЕНО: из 16 геометрических типов создаются все 16. «Точка на кривой» и «точка на середине " +
                "кривой» принимают ОБЪЕКТОМ точку, а не кривую: на отрезке ядро отвечает отказом. РАЗМЕРНЫЕ ТИПЫ " +
                "(ksCAssociation, ksCFixedDim, ksCDimWithVariable) оставлены для опытного клиента: их место - " +
                "ОБЪЕКТ-РАЗМЕР эскиза, где они создаются и управляют геометрией, а на отрезке Create()=false и " +
                "вызов получает названный отказ. Обычный способ поставить управляющий размер - " +
                "kompas_create_sketch_dimension.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("constraint_type", Sch.Enum(
                        "Тип ограничения — именем из ksConstraintTypeEnum. Перечень объявлен в одном месте "
                        + "(KompasMcp.Contracts.SketchConstraintTypes) и сверяется с объявленным в сборке "
                        + "констант; числа литералами не дублируются.",
                        SketchConstraintTypes.All.ToArray())),
                    ("object_ref", Sch.Str(
                        "Адрес объекта, выданный kompas_list_sketch_entities.", maxLength: 512)),
                    ("point_index", Sch.Nullable(Sch.Int(
                        "Индекс точки на объекте, с 0; у окружности и дуги 0 — центр. Не задан — не передаётся.",
                        0, 1_000_000))),
                    ("partner_ref", Sch.Nullable(Sch.Str(
                        "Адрес второго объекта. Взаимоисключим с partner_refs.", maxLength: 512))),
                    ("partner_refs", Sch.Nullable(Sch.Arr(
                        Sch.Str("Адрес объекта-партнёра.", maxLength: 512),
                        "Несколько партнёров — форма SAFEARRAY, объявленная справкой для ассоциативной "
                        + "связи. Взаимоисключима с partner_ref.", min: 1, max: 32))),
                    ("partner_point_index", Sch.Nullable(Sch.Int(
                        "Индекс точки на объекте-партнёре.", 0, 1_000_000))),
                    ("axis_ref", Sch.Nullable(Sch.Str(
                        "Адрес объекта-оси для типов, у которых ось есть (ksCSymmetryTwoPoints, ksCBisector).",
                        maxLength: 512))),
                    ("value", Sch.Nullable(Sch.Num(
                        "Значение управляющего размера. ТОЛЬКО для ksCDimWithVariable: по справке SDK 24 "
                        + "Value применимо только к «Размеру с переменной». Ставится на ОБЪЕКТ-РАЗМЕР "
                        + "эскиза (на отрезке ядро отвечает отказом). Обычный способ поставить "
                        + "управляющий размер — kompas_create_sketch_dimension, где значение "
                        + "подтверждается перечитанной геометрией."))),
                    ("expression", Sch.Nullable(Sch.Str(
                        "Выражение управляющего размера. ТОЛЬКО для ksCDimWithVariable.", maxLength: 512))),
                    ("variable", Sch.Nullable(Sch.Str(
                        "Имя переменной управляющего размера. ТОЛЬКО для ksCDimWithVariable. По справке "
                        + "имя обязано быть уникальным, иначе ограничение не создаётся.", maxLength: 256)))),
                WorkerCommands.CreateSketchConstraint,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_create_sketch_dimension", "Управляющий размер эскиза: линейный, диаметральный, радиальный, угловой",
                                "Ставит УПРАВЛЯЮЩИЙ размер на объект эскиза и делает его движущим: измеряемая величина становится " +
                "равной заданному значению. ЧЕТЫРЕ ВИДА, поле kind: linear (УМОЛЧАНИЕ - размер длины на ОДНОМ " +
                "отрезке), diametral (на ОКРУЖНОСТИ, значение - ДИАМЕТР), radial (на ДУГЕ или ОКРУЖНОСТИ, значение " +
                "- РАДИУС), angular (между ДВУМЯ отрезками, значение - УГОЛ в градусах). МАРШРУТ: у каждого вида " +
                "СВОЯ коллекция ISymbols2DContainer - LineDimensions / DiametralDimensions / RadialDimensions / " +
                "AngleDimensions; далее Add() → интерфейс размера → IDrawingObject.Update(). Линейный строится ПО " +
                "КОНЦАМ измеряемого отрезка; у трёх прочих видов объект задаётся ОПОРНЫМ " +
                "(IDiametralDimension.BaseObject, IRadialDimension.BaseObject, " +
                "IAngleDimension.BaseObject1/BaseObject2), и координаты с радиусом берутся с опоры. УГЛОВОЙ Add " +
                "ОТЛИЧАЕТСЯ: IAngleDimensions.Add(ksDrADimension) принимает тип объекта. ЧТО ДЕЛАЕТ РАЗМЕР " +
                "УПРАВЛЯЮЩИМ - У ВИДОВ РАЗНОЕ: линейный связывается АССОЦИАЦИЕЙ (ksCAssociation с массивом " +
                "отрезков), и без неё значение длину НЕ двигает; у диаметрального, радиального и углового ядро " +
                "ассоциацию ОТВЕРГАЕТ (Create = false), а значение двигает геометрию и без неё - их связывает " +
                "опорный объект. Порядок записи: ksCFixedDim → ksCDimWithVariable со значением. ПОДТВЕРЖДЕНИЕ - ПО " +
                "ФАКТУ: значение размера перечитано равным запрошенному И измеряемая геометрия перечитана равной " +
                "значению (у линейного - три факта, включая совпадение точек размера с концами отрезка). ВИД " +
                "ОБЪЕКТА ПРОВЕРЯЕТСЯ ДО СОЗДАНИЯ: несоответствие - INVALID_ARGUMENT с названным видом. ПОЛЕ ЧУЖОГО " +
                "ВИДА ОТВЕРГАЕТСЯ (second_object_ref и value_deg - только angular, value_mm - не для angular, " +
                "offset_mm - только linear). ЗНАЧЕНИЕ ОБЯЗАТЕЛЬНО И ПОЛОЖИТЕЛЬНО: размер нулевой длины измерения не " +
                "выражает. ПЕРЕМЕННАЯ ДЕТАЛИ этим инструментом НЕ привязывается: переменную размера ядро назначает " +
                "само, она живёт у ПРИЗНАКА ЭСКИЗА и публикуется полем dimension_variable; привязка - " +
                "kompas_create_variable и kompas_bind_parameter.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("object_ref", Sch.Str(
                        "Адрес измеряемого объекта, как его выдаёт kompas_list_sketch_entities: отрезок "
                        + "для linear, ОКРУЖНОСТЬ для diametral, дуга или окружность для radial и ПЕРВЫЙ "
                        + "отрезок для angular.", maxLength: 512)),
                    ("kind", Sch.Nullable(Sch.Enum(
                        "Вид размера. Не задан — linear, то есть прежнее поведение.",
                        SketchDimensionKinds.All.ToArray()))),
                    ("second_object_ref", Sch.Nullable(Sch.Str(
                        "Адрес ВТОРОГО измеряемого отрезка. ТОЛЬКО для angular, где обязателен: угол "
                        + "измеряется между двумя разными отрезками.", maxLength: 512))),
                    ("value_mm", Sch.Nullable(Sch.Num(
                        "Значение размера, мм: длина отрезка для linear, ДИАМЕТР (не радиус) для "
                        + "diametral, РАДИУС для radial. Для angular не принимается — там value_deg.",
                        0.000001d, 1_000_000d))),
                    ("value_deg", Sch.Nullable(Sch.Num(
                        "Значение угла в градусах. ТОЛЬКО для angular.", 0.000001d, 359.999999d))),
                    ("offset_mm", Sch.Nullable(Sch.Num(
                        "На сколько отвести линию размера от отрезка, мм. ТОЛЬКО для linear; не задано — "
                        + "10. Знак относится к стороне от начала координат эскиза. У прочих видов "
                        + "положение линии задаётся их собственными членами (Angle, Radius, Xc/Yc), "
                        + "которые этот инструмент не трогает.", -1_000d, 1_000d)))),
                WorkerCommands.CreateSketchDimension,
                requiresDocument: false,
                requiresRevision: true),

            ReadOnly("kompas_list_sketch_constraints", "Ограничения объектов эскиза",
                                "Перечисляет объекты эскиза и читает НАЛОЖЕННЫЕ на них параметрические ограничения: тип, индексы " +
                "точки, партнёра и сегмента, признак действительности, значение, выражение и имя переменной, а " +
                "также параметрическое состояние КАЖДОГО объекта (ksObjectConstraintsStateEnum). Без object_ref " +
                "перечисляются все объекты, с ним - один названный адресом. МАРШРУТ: ISketch.BeginEditEx(true) - " +
                "ТОЛЬКО НА ЧТЕНИЕ - → IKompasDocument2D.ViewsAndLayersManager.Views → IView → QI(IDrawingContainer) " +
                "→ GetObjects(ksAllObj) → QI(IDrawingObject1) → IDrawingObject1.Constraints → ISketch.EndEdit(). " +
                "ИЗМЕРЕННАЯ ЛОВУШКА: объект и его ограничения живы ТОЛЬКО ВНУТРИ сеанса редактирования; после " +
                "EndEdit() тот же объект отвечает Constraints → null и нулевыми координатами без исключения. СПИСОК " +
                "МОЖЕТ ПРИЙТИ НЕ МАССИВОМ: при ОДНОМ ограничении тип VARIANT - VT_DISPATCH, а не " +
                "VT_ARRAY|VT_DISPATCH; одиночная запись " +
                "разворачивается. ЧТЕНИЕ НЕ ПОДНИМАЕТ РЕВИЗИЮ и не входит в режим правки эскиза. ПУСТОЕ ПОЛЕ " +
                "ОЗНАЧАЕТ «НЕ ПРОЧИТАНО», А НЕ НОЛЬ: причина в notes. СОСТОЯНИЕ ЭСКИЗА И СОСТОЯНИЕ ОБЪЕКТА - РАЗНЫЕ " +
                "ВЕЛИЧИНЫ ИЗ РАЗНЫХ СЛОВАРЕЙ: IDrawingObject1.ConstraintsState - ksObjectConstraintsStateEnum (0 " +
                "неизвестно, 1 полностью определён, 2 переопределён, 3 информационный, 4 неверная проекция), а " +
                "агрегатное состояние эскиза читает kompas_get_sketch_status по ksConstraintsStateEnum (0 " +
                "неизвестно, 1 определён, 2 недоопределён, 3 требует внимания); число 2 у них означает РАЗНОЕ. " +
                "ПАРАМЕТРИЧЕСКИЙ РЕЖИМ ДОКУМЕНТА публикуется полем parametric_mode " +
                "(IKompasDocument2D1.ParametricMode): читается у документа ФРАГМЕНТА эскиза через типизированный " +
                "двойник, потому что поставленная обёртка объявляет IKompasDocument2D1 усечённо (члены кончаются на " +
                "ChangeObjectsOrder), а установленная kAPI7.tlb несёт член в слотах 74 и 75. Только ЧТЕНИЕ: не " +
                "прочитан - null с причиной в notes, и никогда не false. ГРАНИЦА, ИЗМЕРЕННАЯ И НАЗВАННАЯ: сеанс на " +
                "эскизе НЕ нейтрален для наблюдения и ФЛАГ состояния НЕУСТОЙЧИВ - на контуре с полным набором " +
                "ограничений без сеанса после переоткрытия читается 1, с открытым и закрытым сеансом 2; на контуре " +
                "с управляющим размером направление ОБРАТНОЕ. Чтение не меняет ревизию и объём, но меняет " +
                "состояние, которое унесёт СЛЕДУЮЩЕЕ сохранение; причина не установлена. ЧТО ПРИ ЭТОМ ЦЕЛО: " +
                "параметрическое поведение сохраняется (размер держит значение, нарушение отвергается " +
                "VERIFICATION_FAILED), а настоящая правка эскиза отдаёт состояние 1. Клиенту: не судите о модели по " +
                "одному этому флагу и не открывайте чтение ограничений между последней правкой эскиза и " +
                "сохранением, если флаг определённости важен.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("object_ref", Sch.Nullable(Sch.Str(
                        "Адрес одного объекта, выданный kompas_list_sketch_entities. Без него перечисляются "
                        + "все объекты эскиза.", maxLength: 512))),
                    ("limit", Sch.Nullable(Sch.Ref("#/$defs/limit")))),
                WorkerCommands.ListSketchConstraints),

            Mutation("kompas_delete_sketch_constraint", "Снять ограничения объекта эскиза",
                "Снимает ЛИБО ОДНО ограничение объекта по позиции в списке (all = false, "
                + "constraint_index), ЛИБО ВСЕ ограничения этого объекта (all = true). Удаляются "
                + "ограничения ОДНОГО объекта, названного устойчивым адресом: «снять все ограничения "
                + "эскиза» одним вызовом не выражается, потому что тогда неясно, у чего снимать. "
                + "МАРШРУТ: ISketch.BeginEdit() — НА ЗАПИСЬ — → перечисление объектов → "
                + "IDrawingObject1.Constraints (список читается ВНУТРИ сеанса) → "
                + "IParametriticConstraint.Delete() для одной записи либо IDrawingObject1.DeleteConstraints() "
                + "для всех → перечитывание списка → ISketch.EndEdit(). "
                + "ПРИМЕНЕНИЕ ПОДТВЕРЖДАЕТСЯ ПЕРЕЧИТЫВАНИЕМ: ответ несёт число записей ДО и ПОСЛЕ, "
                + "снятое ВНУТРИ ОДНОГО СЕАНСА, — иначе разница описывала бы сеансы, а не удаление. "
                + "ИЗМЕРЕНО: Delete() на списке из одной записи возвращает True и оставляет 0; "
                + "DeleteConstraints() на списке из двух возвращает True и оставляет 0; на ПУСТОМ списке "
                + "DeleteConstraints() отвечает False — это ответ про пустой список, а не про метод, и "
                + "зачитывать его как свойство удаления нельзя. "
                + "ПОЛЕ ЧУЖОГО ДЕЙСТВИЯ ОТВЕРГАЕТСЯ: constraint_index при all = true — INVALID_ARGUMENT "
                + "с перечнем foreign_fields.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("object_ref", Sch.Str(
                        "Адрес объекта, выданный kompas_list_sketch_entities.", maxLength: 512)),
                    ("all", Sch.Nullable(Sch.Bool(
                        "Снять ВСЕ ограничения объекта (DeleteConstraints). По умолчанию false — снимается "
                        + "одна запись по constraint_index. Поле НЕ обязательное: значение по умолчанию "
                        + "объявлено, и требовать его передачу значило бы наказывать клиента за доверие "
                        + "к схеме.", @default: false))),
                    ("constraint_index", Sch.Nullable(Sch.Int(
                        "Позиция в списке ограничений объекта, который выдаёт kompas_list_sketch_constraints. "
                        + "Обязательна при all = false.", 0, 1_000_000)))),
                WorkerCommands.DeleteSketchConstraint,
                requiresDocument: false,
                requiresRevision: true),
        };

        foreach (var tool in tools)
        {
            // Only the definitions the tool's own schema refers to are attached: the answer of
            // tools/list is context the client pays for in every session.
            // History: docs/decisions/host.md#schema-defs-pruned
            Sch.WithReferencedDefs(tool.InputSchema, defs);
        }

        return tools;
    }

    private static ToolDefinition ReadOnly(string name, string title, string description, JsonObject properties, string command, bool requiresDocument = false, bool requiresOperationId = false)
        => new(name, title, description, Request(title, properties),
            new ToolBehaviour(ReadOnly: true, Destructive: false, RequiresOperationId: requiresOperationId, RequiresDocument: requiresDocument, RequiresExpectedRevision: false),
            command);

    private static ToolDefinition Mutation(
        string name,
        string title,
        string description,
        JsonObject properties,
        string command,
        bool requiresDocument,
        bool requiresRevision = false,
        bool requiresOperationId = true)
    {
        // A mutation always carries operation_id, and the schema has to say so: the Host rejects
        // unknown fields, so a required field must be declared here or the first legitimate call is
        // refused.
        if (requiresOperationId)
        {
            // The description travels WITH the $ref: see OperationIdField for why the rule is repeated
            // at the field instead of only in the definition.
            properties["operation_id"] = Sch.Described(Sch.Ref("#/$defs/operation_id"), OperationIdField);
        }

        return new ToolDefinition(
            name,
            title,
            description,
            Request(title, properties),
            new ToolBehaviour(ReadOnly: false, Destructive: true, RequiresOperationId: requiresOperationId, RequiresDocument: requiresDocument, RequiresExpectedRevision: requiresRevision),
            command);
    }

    /// <summary>A session-control tool: handled by the Host itself, not the Worker.</summary>
    /// <remarks>A separate factory: these tools write no operation journal and must answer when there
    /// is no CAD channel at all. The <c>destructive</c> mark on release is honest: it ends the session
    /// and closes the documents. Why release still has <c>operation_id</c>: line <c>S03b</c> of
    /// <c>mcp-smoke.py</c> shows rule §2.1 requires a tool annotated <c>destructiveHint=true</c> to
    /// declare it; release declares it and replays the outcome itself, field NOT required.
    /// History: docs/decisions/host.md#control-operation-id</remarks>
    private static ToolDefinition Control(
        string name,
        string title,
        string description,
        JsonObject properties,
        string command,
        bool destructive = false,
        bool replaysOperationId = false)
        => new(name, title, description, Request(title, properties),
            new ToolBehaviour(ReadOnly: false, Destructive: destructive, RequiresOperationId: false, RequiresDocument: false, RequiresExpectedRevision: false, HostLocal: true, ReplaysOperationId: replaysOperationId),
            command);

    /// <summary>Every request also carries the observation budget.</summary>
    private static JsonObject Request(string title, JsonObject properties)
    {
        var withTimeout = (JsonObject)properties.DeepClone();
        withTimeout["timeout_ms"] = Sch.Nullable(Sch.Ref("#/$defs/timeout_ms"));

        // Which fields are required is decided where the field is declared, not by a rule over the
        // finished object: an omitted optional field must stay optional, otherwise every tool
        // would demand values that its own description calls optional.
        var required = RequiredOf(withTimeout);
        return Sch.Obj(title, required, withTimeout);
    }

    private static IReadOnlyList<string> RequiredOf(JsonObject properties)
    {
        var required = new List<string>();
        foreach (var pair in properties)
        {
            // A field is optional when it can be null, when it is a union with null, or when the
            // declaration said so with x-optional.
            if (pair.Value is not JsonObject schema || !IsOptional(schema))
            {
                required.Add(pair.Key);
            }
        }

        return required;
    }

    private static bool IsOptional(JsonObject schema)
    {
        if (schema["x-optional"]?.GetValue<bool>() == true)
        {
            return true;
        }

        if (schema["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null"))
        {
            return true;
        }

        if (schema["anyOf"] is JsonArray branches && branches.Any(b => b is JsonObject o && o["type"]?.GetValue<string>() == "null"))
        {
            return true;
        }

        return false;
    }

    private static JsonObject TransformSchema() => Sch.ObjAll("transform", new[]
    {
        ("origin_mm", Sch.Ref("#/$defs/vector3")),
        ("x_axis", Sch.Arr(Sch.Num("Компонента единичной оси X."),
            "Ось X правого базиса, единичная. Ровно три компоненты [x, y, z] (безразмерные).", 3, 3)),
        ("y_axis", Sch.Arr(Sch.Num("Компонента единичной оси Y."),
            "Ось Y: единичная и ортогональная X. Z = X×Y вычисляет сервер. Ровно три компоненты [x, y, z] "
            + "(безразмерные).", 3, 3)),
    }, "Жёсткое размещение: начало и две ортонормированные оси правого базиса.");

    private static JsonObject BboxSchema() => Sch.ObjAll("bbox", new[]
    {
        ("min_mm", Sch.Ref("#/$defs/vector3")),
        ("max_mm", Sch.Ref("#/$defs/vector3")),
    },
    "Габарит, мм: min_mm и max_mm в системе координат детали.");

    /// <summary>The plane for B3 operations: a ready reference or a point with a normal.</summary>
    /// <remarks>The plane's SIDE is set by the sign <c>s = n·(p − p₀)</c> in the cut command itself,
    /// because "the left side" without a coordinate system is not an address. "base plane + offset" is
    /// declared but not supported: the API7 route with an offset was never measured, and a call with it
    /// refuses CAPABILITY_UNAVAILABLE rather than guessing the sign.
    /// History: docs/decisions/host.md#cut-plane-side</remarks>
    private static JsonObject CutPlaneSchema() => new()
    {
        ["type"] = "object",
        ["title"] = "cut_plane",
        ["description"] = "Плоскость операции: plane_ref ЛИБО point_mm вместе с normal_mm. "
            + "Одновременно — только одно. base (базовая плоскость со смещением) объявлен, но не "
            + "поддержан: маршрут не измерен, вызов отказывает CAPABILITY_UNAVAILABLE.",
        ["properties"] = Sch.Props(
            ("plane_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
            ("point_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
            ("normal_mm", Sch.Nullable(Sch.Ref("#/$defs/vector3"))),
            ("base", Sch.Nullable(Sch.Enum(
                "Объявлено, но НЕ поддержано: маршрут вспомогательной плоскости со смещением "
                + "не измерен, и вызов с этим полем отказывает CAPABILITY_UNAVAILABLE (до мутации, "
                + "retry_policy=never). Задайте плоскость точкой и нормалью. base вместе с другим "
                + "способом (plane_ref или точка с нормалью) - противоречивый запрос: "
                + "INVALID_ARGUMENT, а не отказ возможности.", "xy", "xz", "yz"))),
            ("offset_mm", Sch.Nullable(Sch.Num(
                "Смещение для base, мм. Имеет смысл только вместе с base: без него смещение не "
                + "выражает плоскость, и вызов отвергается INVALID_ARGUMENT, а не принимается и "
                + "молча игнорируется.", -1e18d, 1e18d)))),
    };

    /// <summary>The sketch support: a named standard plane (optionally offset) OR a ready plane
    /// reference — exactly one of the two.</summary>
    /// <remarks>INVARIANT: the rule is stated in words and enforced by the SERVER, not by
    /// <c>oneOf</c>/<c>anyOf</c>, which not every MCP client evaluates; the form is ONE for both tools.
    /// MEASURED: <c>base</c> sat in <c>required</c> and its enum lacked <c>null</c>, so the "reference
    /// only" form was refused by the client's schema before reaching the server.
    /// History: docs/decisions/adapter-sketch.md#sketch-plane-reference</remarks>
    private static JsonObject PlaneSchema(string? description = null) => new()
    {
        ["type"] = "object",
        ["title"] = "plane",
        ["description"] = description ?? "Опора эскиза: ИМЕННО ОДНО из двух — base (стандартная плоскость xy/xz/yz, "
            + "при необходимости со смещением offset_mm) ЛИБО reference (ссылка вида plane). Оба поля "
            + "сразу или ни одного — INVALID_ARGUMENT до обращения к ядру: сервер не выбирает поле за "
            + "вызывающего. Форма одна и та же у kompas_create_sketch и kompas_set_sketch_plane.",
        ["properties"] = Sch.Props(
            ("base", PlaneBaseSchema()),
            ("reference", Sch.Described(
                Sch.Nullable(Sch.Ref("#/$defs/reference")),
                "Ссылка вида plane — смещённая или вспомогательная плоскость. Грань, ребро, тело, ось и "
                + "точка опорой НЕ являются: такая ссылка отвергается INVALID_ARGUMENT по виду ссылки ДО "
                + "обращения к ядру (ответ ядра на грань продуктом не наследуется). Плоскость у грани "
                + "получают инструментом kompas_create_aux_geometry: kind=plane, mode=offset, "
                + "base_face_ref=<ссылка на грань>, offset_mm=<смещение>; его reference_id и подаётся "
                + "здесь. offset_mm вместе с reference отвергается, а не игнорируется молча.")),
            ("offset_mm", Sch.Num("Смещение вдоль нормали базовой плоскости, мм. Отсчёт идёт вдоль нормали плоскости, а не вдоль оси координат: у плоскости yz нормаль направлена в -X, поэтому положительное смещение уводит опору в отрицательную сторону X. Имеет смысл только вместе с base.", -1e6, 1e6, defaultTo: 0d))),
        ["additionalProperties"] = false,
    };

    /// <summary>The <c>base</c> field: a named standard plane, or <c>null</c>.</summary>
    /// <remarks>MEASURED: <c>Nullable(Enum(...))</c> produced <c>type: ["string","null"]</c> with
    /// <c>enum: ["xy","xz","yz"]</c> — a <c>base: null</c> sent by a client failed the enum of its own
    /// field. <c>null</c> is added to the enum rather than dropped from the type, so "omitted" and
    /// "null" stay the same request.
    /// History: docs/decisions/adapter-sketch.md#sketch-plane-reference</remarks>
    private static JsonObject PlaneBaseSchema()
    {
        var schema = Sch.Nullable(Sch.Enum(
            "Стандартная плоскость: xy, xz или yz. Не задана — опора берётся из reference.",
            "xy", "xz", "yz"));
        ((JsonArray)schema["enum"]!).Add(null);
        return schema;
    }

    private static JsonObject SketchEntitySchema() => new()
    {
        ["type"] = "object",
        ["title"] = "sketch_entity",
        ["description"] = "Примитив эскиза в локальных миллиметрах. Набор полей зависит от kind; лишние поля отклоняются. "
            + "kind=polyline рисуется ОДНИМ объектом ломаной, kind=spline - одним сплайном (кривая Безье через "
            + "заданные вершины); оба перечитываются одним объектом.",
        ["properties"] = Sch.Props(
            ("kind", Sch.Enum("Тип примитива.", "line", "circle", "arc", "rectangle", "polyline", "spline")),
            ("start_mm", Sch.Nullable(Sch.Vec2("Начало / угол прямоугольника."))),
            ("end_mm", Sch.Nullable(Sch.Vec2("Конец отрезка."))),
            ("center_mm", Sch.Nullable(Sch.Vec2("Центр дуги/окружности."))),
            ("radius_mm", Sch.Nullable(Sch.PositiveMm("Радиус"))),
            ("start_deg", Sch.Nullable(Sch.Num(
                "Начальный угол дуги, градусы. Отсчёт от оси +X системы координат эскиза; положительный угол — "
                + "против часовой стрелки. Угол конца дуги равен start_deg + sweep_deg.", -720, 720))),
            ("sweep_deg", Sch.Nullable(Sch.Num(
                "Размах дуги, градусы. Знак задаёт направление: положительный — против часовой стрелки, "
                + "отрицательный — по часовой. Угол конца = start_deg + sweep_deg, длина дуги = |sweep_deg|. "
                + "Размах больше 180° ядро чтит: дуга идёт по указанной стороне, а не по меньшей.",
                -720, 720))),
            ("start_point_mm", Sch.Nullable(Sch.Arr(Sch.Num("Координата эскиза, мм."),
                "Ровно две координаты [x, y], мм. Дуга по концам: начальная точка на окружности. "
                + "Взаимоисключающе с start_deg/sweep_deg: "
                + "дуга задаётся ЛИБО углами, ЛИБО концами, и смешение отвергается до COM. Точка обязана "
                + "лежать на окружности (center_mm, radius_mm) с относительным допуском 1e-6.", 2, 2))),
            ("end_point_mm", Sch.Nullable(Sch.Arr(Sch.Num("Координата эскиза, мм."),
                "Ровно две координаты [x, y], мм. Дуга по концам: конечная точка на окружности. "
                + "Обязательна вместе с start_point_mm и "
                + "clockwise.", 2, 2))),
            ("clockwise", Sch.Nullable(Sch.Bool(
                "Дуга по концам: направление отрисовки. true — по часовой стрелке, false — против. "
                + "ОБЯЗАТЕЛЬНО при задании дуги концами и запрещено при задании углами: те же две точки "
                + "называют две разные дуги, и умолчание нарисовало бы дополняющую."))),
            ("width_mm", Sch.Nullable(Sch.PositiveMm("Ширина прямоугольника"))),
            ("height_mm", Sch.Nullable(Sch.PositiveMm("Высота прямоугольника"))),
            ("points_mm", Sch.Nullable(Sch.Arr(Sch.Arr(Sch.Num("Координата, мм."),
                "Точка. Ровно две координаты [x, y], мм.", 2, 2),
                "Вершины полилинии или сплайна, мм. Предел " + SketchLimits.MaxPolylineVertices + " вершин на один примитив "
                + "задан СХЕМОЙ MCP, а не ядром. Полилиния рисуется ОДНИМ объектом ломаной и перечитывается одним "
                + "объектом: в kompas_list_sketch_entities на неё приходит одна строка с entity_kind=polyline и "
                + "полями points_count и closed, а collection_counts показывает poly_lines = 1, а НЕ N отрезков. "
                + "Замкнутость задаётся полем closed и передаётся ядру собственным признаком ломаной. Сплайн "
                + "рисуется одним объектом - кривой Безье ЧЕРЕЗ заданные вершины - и перечитывается одной строкой "
                + "с entity_kind=spline; замкнутость несёт его собственный признак closed. Полилиния и сплайн - "
                + "разные объекты: точки полилинии соединяются отрезками, сплайн проходит через них кривой.",
                2, SketchLimits.MaxPolylineVertices))),
            ("closed", Sch.Nullable(Sch.Bool("Замкнутая полилиния: замыкающее ребро из последней вершины в первую. Замкнутый сплайн: замыкающий участок кривой. У обоих это собственный признак ядра, а не повторённая вершина.")))),
        ["required"] = new JsonArray("kind"),
        ["additionalProperties"] = false,
    };

    private static JsonObject SelectionPredicateSchema() => new()
    {
        ["type"] = "object",
        ["title"] = "selection_predicate",
        ["description"] = "Структурный предикат по граням тела. Все перечисленные поля сервер применяет; "
            + "лишнее поле отвергается схемой (additionalProperties=false), а не игнорируется.",
        ["properties"] = Sch.Props(
            ("surface_type", Sch.Nullable(Sch.Enum("Тип поверхности.", "plane", "cylinder", "cone", "sphere", "torus", "other"))),
            // INVARIANT: the normal is expressed in the coordinate system of the part whose body is
            // being searched — the tool works from a body reference and has no other frame.
            // History: docs/decisions/contracts.md#selection-predicate-coordinate-space
            ("normal_direction", Sch.Described(
                Sch.Nullable(Sch.Ref("#/$defs/vector3")),
                "Направление нормали в системе координат детали, которой принадлежит тело: инструмент "
                + "работает от ссылки на тело и другой системы координат не имеет.")),
            ("normal_angle_tolerance_deg", Sch.Nullable(Sch.Num("Допуск направления нормали, градусы.", 0, 90, defaultTo: 0.5d))),
            ("area_range_mm2", Sch.Nullable(Sch.Arr(Sch.Num("Граница площади, мм².", 0, 1e12), "Диапазон площади [мин, макс].", 2, 2)))),
        ["additionalProperties"] = false,
    };
}
