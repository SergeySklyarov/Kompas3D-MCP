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
                        "Смещение точки вдоль контура ЭТОГО сечения, мм — ICoupling.PositionOffset(Index). " +
                        "Ноль — начало контура; это же значение воспроизводит автоматическое " +
                        "соответствие. Отрицательное значение означает смещение в другую сторону по " +
                        "контуру и отдельно не измерялось, поэтому не запрещается, но и не обещается."),
                    "Точки по сечениям В ПОРЯДКЕ section_refs: ровно одна точка на каждое сечение. " +
                    "Число точек обязано совпасть с числом сечений — ICoupling.Count так и называется, " +
                    "«количество сечений в цепочке» (измерено: 2 у двух сечений). Несовпадение " +
                    "отвергается до COM: цепочка короче набора описывала бы другое соответствие.",
                    2, 64)),
            },
            "Одна цепочка соответствия: по точке на КАЖДОЕ сечение, в порядке section_refs. " +
            "Несколько цепочек задают несколько соответствий; полное отсутствие цепочек означает " +
            "автоматическое соответствие КОМПАС."),
        "Цепочки соответствия сечений — то, чем режим SM-05.base.mode_couplings отличается от «просто " +
        "тела по сечениям». В API5 цепочек нет вовсе: ни ksBaseLoftDefinition, ни ksBossLoftDefinition " +
        "не объявляют ни AddCoupling, ни Coupling, поэтому маршрут семейства — API7. Полная замена: " +
        "заданный список становится всем набором цепочек признака. Пустой список означает «без " +
        "цепочек» и применяется как ClearCouplings().",
        // The lower bound is 0, not 1 — a corrected contradiction of the schema's own declaration.
        // The description promised an empty list means "no couplings" (ClearCouplings()), while the
        // schema forbade it (`minItems: 1`): MEASURED, `couplings: []` returned INVALID_ARGUMENT
        // "minimum 1, got 0" on $/couplings.
        // History: docs/decisions/host.md#couplings-minitems
        0, 64);

    /// <summary>Definitions shared by every tool, so a document id means the same thing everywhere.</summary>
    public static JsonObject SharedDefinitions { get; } = Sch.Props(
        ("operation_id", Sch.Uuid("UUID v4, 36 символов, например 8f14e45f-ceea-467f-a1b2-000000000001. Идентификатор мутации для повторного вызова: НОВЫЙ id на каждую новую операцию; тот же id с теми же аргументами возвращает уже известный результат и повторно не обращается к КОМПАС; тот же id с другими аргументами даёт OPERATION_ID_CONFLICT.")),
        ("document_id", Sch.Str("UUID документа из kompas_create_document/kompas_open_document. Ни одна команда не использует «активный документ».","^[0-9a-f]{32}$")),
        ("application_id", Sch.Str("UUID экземпляра КОМПАС из kompas_connect.", "^[0-9a-f]{32}$")),
        ("expected_revision", Sch.Int("Ревизия из последнего чтения контекста. Расхождение даёт REVISION_CONFLICT без частичных эффектов.")),
        ("timeout_ms", Sch.Int("Бюджет наблюдения за операцией. Не меняет семантику отмены: истечение тайм-аута означает неизвестный исход, а не отмену.", 1000, 600_000)),
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
                + "пересказываются: перечень без выдачи ограничений выдавал бы их за проверенные.",
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
                + "документы закрываются документированным ksDocument3D.close(), собственный (launched) "
                + "экземпляр КОМПАС завершается документированным KompasObject.Quit(), пользовательский "
                + "attached КОМПАС остаётся запущенным. MCP-транспорт сохраняется: этот Хост остаётся на "
                + "связи и позже может занять сеанс снова. Повторный вызов безопасен и различает "
                + "«уже освобождён» и «освобождён этим запросом». Повтор с тем же operation_id "
                + "возвращает записанный исход и освобождения заново не выполняет; новый operation_id "
                + "начинает освобождение заново. Журнал операций инструмент не пишет: исходы хранятся "
                + "в памяти процесса, поэтому перезапуск Хоста историю повторов не несёт. Отдельный "
                + "отказ DOCUMENT_STATE_UNKNOWN приходит, когда Worker терялся или перезапускался: его "
                + "документы новому Worker не известны, и правки могли остаться несохранёнными. Выход "
                + "один и он явный — acknowledge_unknown_document_state=true.",
                Sch.Props(
                    ("operation_id", Sch.Nullable(Sch.Ref("#/$defs/operation_id"))),
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
                "attach — подключиться к уже запущенному экземпляру (нужен однозначный выбор, иначе AMBIGUOUS_APPLICATION); launch — запустить собственный. Обычные инструменты новое приложение не создают. КАНДИДАТЫ attach — экземпляры, зарегистрированные в Running Object Table (ROT) под ProgID КОМПАС, а НЕ все процессы KOMPAS.exe: процесс без записи в ROT кандидатом не является, поэтому running_instances и rot_kompas_entries в kompas_capabilities — разные числа. При нескольких кандидатах нужен явный process_id: выбор «первого попавшегося» запрещён.",
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
                "По умолчанию только отсоединяет сервер. close_owned_application закрывает тот экземпляр, который сервер запустил сам; пользовательский КОМПАС не завершается никогда.",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("close_owned_application", Sch.Bool("Штатно завершить собственный (launched) экземпляр.", false))),
                WorkerCommands.Disconnect,
                requiresDocument: false),

            Mutation("kompas_create_document", "Создать документ",
                "Новый несохранённый документ с UUID. Возвращает контекст с revision=1.",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("kind", Sch.Enum("Тип документа.", "part", "assembly", "drawing", "fragment")),
                    ("name", Sch.Nullable(Sch.Str("Имя объекта (ksPart.name).", maxLength: 256))),
                    ("marking", Sch.Nullable(Sch.Str("Обозначение.", maxLength: 128)))),
                WorkerCommands.CreateDocument,
                requiresDocument: false),

            Mutation("kompas_open_document", "Открыть документ",
                "Открывает файл из разрешённого корня. Если файл уже открыт, возвращается тот же document_id — второго экземпляра не происходит.",
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
                    ("detail", Sch.Enum("minimal — только счётчики; full — добавает габарит и отпечаток состояния.", "minimal", "full"))),
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
                + "не отличался бы от «коллекция не прочитана».",
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
                "Перечень сопряжений: тип, выравнивание, фиксация, параметр, оба базовых объекта и "
                + "подтверждение Valid. Читается документированным MateConstraintCollection → "
                + "ksMateConstraint.GetBaseObj(1|2)/constraintType. Подтверждение — Valid, а не "
                + "успешный вызов. Признак фиксации печатается именем по СЫРОМУ числу поля "
                + "ksMateConstraint.fixed по нумерации mateconstraintfixed.html "
                + "«0 нет фиксации, 1 фиксировать деталь 1, 2 фиксировать деталь 2»; в перечислении "
                + "API5 ksMateFixedTypeEnum то же значение 0 названо «Неопределено» "
                + "(ksmatefixedtypeenum.html) — расхождение формулировок названо, а не сглажено.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.ListMates,
                requiresDocument: true,
                requiresOperationId: false),

            Mutation("kompas_create_mate", "Создать сопряжение",
                "Создаёт сопряжение между гранями двух компонентов документированным API7-путём "
                + "(IMateConstraints3D.Add → BaseObject1/2 → Update). Объект адресуется парой "
                + "«компонент + номер грани»; грань берётся ksPart.BodyCollection → ksBody.FaceCollection. "
                + "Подтверждением служит Valid, а не Update()=true.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("constraint_type", Sch.Enum(
                        "Тип сопряжения именем (MateConstraintType).",
                        "coincidence", "parallel", "perpendicular", "tangency",
                        "concentric", "distance", "angle")),
                    ("first_component_ref", Sch.Str("Ссылка на первый компонент из kompas_list_components.")),
                    ("first_face_index", Sch.Int("Номер грани первого компонента в его FaceCollection.")),
                    ("second_component_ref", Sch.Str("Ссылка на второй компонент.")),
                    ("second_face_index", Sch.Int("Номер грани второго компонента.")),
                    // INVARIANT: an explicit value is confirmed by the MEASURED orientation of the two
                    // faces, not by the number the kernel keeps; `closest` names no orientation and is
                    // never checked. MEASURED on two placements: opposite lands on -1, cooriented on +1.
                    // History: docs/decisions/mates.md#alignment-geometry-criterion
                    ("alignment", Sch.Nullable(Sch.Enum(
                        "Вариант выравнивания направлений (ksMateConstraintAlignmentEnum). "
                        + "opposite и cooriented подтверждаются ИЗМЕРЕННОЙ ориентацией граней "
                        + "(face_normals_dot: -1 и +1); closest — ориентацию выбирает ядро, сервер её не "
                        + "проверяет и возвращает измеренную. Перечитанное значение возвращается как "
                        + "факт и само по себе отказом не является.",
                        "opposite", "cooriented", "closest"))),
                    // INVARIANT: the parameter is bound to the type and checked by the Host. Per
                    // mateconstrainttype.html only mc_Distance (5, «постоянное расстояние») and
                    // mc_Angle (6, «постоянный угол») are parametric; for the rest an accepted number
                    // would be recorded and ignored. The help does NOT name the UNITS — the server
                    // confirms by reading back. History: docs/decisions/host.md#mate-param
                    ("param_value", Sch.Nullable(Sch.Num(
                        "Параметр ограничения: обязателен для distance и angle, запрещён для остальных "
                        + "типов. Единицы задаются КОМПАС (справкой не названы); подтверждается "
                        + "чтением обратно.")))),
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
                "Габарит, объём, площадь, центр масс. Масса считается только при переданной плотности: сервер её не угадывает.",
                Sch.Props(
                    ("target_ref", Sch.Ref("#/$defs/reference")),
                    ("properties", Sch.Arr(Sch.Enum("Свойство", "bbox", "volume", "surface_area", "mass", "centroid"), "Запрашиваемые свойства.", 1, 5)),
                    ("density_kg_per_m3", Sch.Nullable(Sch.Positive("Плотность материала", "кг/м³")))),
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
                + "эскиз носит ОДНУ живую ссылку, и перечень возвращает именно её, а не вторую.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("plane", PlaneSchema()),
                    ("name", Sch.Nullable(Sch.Str("Имя эскиза.", maxLength: 256)))),
                WorkerCommands.CreateSketch,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_edit_sketch", "Изменить эскиз",
                "Добавить (append), заменить целиком (replace) или очистить (delete_entities) примитивы эскиза. Очистка делается объектом, найденным по координате: в API5 нет перечисления объектов эскиза, поэтому на существующий примитив можно указать только точкой, лежащей на нём. Для эскиза, нарисованного этим сервером, координаты помнятся; для эскиза из переоткрытого или чужого документа (reopen) они выводятся из геометрии зависимого тела — цилиндрическая грань сквозного отверстия даёт центр и радиус, а точка (cx + r; cy) лежит на окружности эскиза (маршрут измерен пробой G). Вывод работает только для измеренной конфигурации: эскиз на основной плоскости XY, в профиле окружность, вырезание сквозное; для наклонных плоскостей и профилей из отрезков, дуг и прямоугольников инструмент отказывает CAPABILITY_UNAVAILABLE, а не угадывает. После правки измеряется зависимое тело: если оно исчезло, правка не выдаётся за успешную (измерено G.9 — все вызовы ответили успехом, а объём стал нулём). Валидация всей пачки происходит до обращения к COM. "
                + "ЧТО СЛОМАНО ДЛЯ КЛИЕНТА (выпуск 0.6.0, несовместимое изменение ответа). Полилиния "
                + "рисовалась ОТРЕЗКАМИ: незамкнутая давала N−1 отрезков, замкнутая — N, а "
                + "kompas_list_sketch_entities и collection_counts отдавали N (или N−1) сущностей "
                + "kind=ksDrLineSeg и poly_lines = 0. Теперь kind=polyline рисуется ОДНИМ нативным объектом "
                + "ломаной, и чтение отдаёт ОДНУ строку kind=ksDrPolyline с полями points_count и closed, а "
                + "collection_counts — poly_lines = 1. Клиенту, считавшему отрезки полилинии, нужно "
                + "перейти на счёт объектов и на poly_lines; площадь профиля и ожидаемый объём "
                + "выдавливания считаются прежней формулой по вершинам и для того же контура НЕ меняются. "
                + "Добавлен вид kind=spline (один сплайн NURBS). "
                + "ДЛИНА ПАЧКИ: предел числа примитивов на вызов задан схемой MCP, а не ядром.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mode", Sch.Enum("append — дорисовать; replace — очистить и нарисовать; delete_entities — только очистить. Последние два удаляют найденное по координате: координаты берутся из памяти сервера либо выводятся из зависимого тела. Ограничения вывода — в описании инструмента и в поле probe_points_from_model результата.", "append", "replace", "delete_entities")),
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
                "Закрывает редактирование. Замкнутость профиля в API5 не проверяется — это честно возвращается в unverified_aspects, а выдавливание всё равно покажет настоящий отказ.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("require_closed_profile", Sch.Bool("Требование замкнутости: сервер не может его подтвердить и сообщит об этом.", true))),
                WorkerCommands.FinishSketch,
                requiresDocument: false),

            Mutation("kompas_extrude", "Выдавить",
                "Базовое выдавливание, приклеивание или вырезание. После вызова перечитывается объём: без подтверждённого изменения геометрии успех не выдаётся. Для boss и cut целевое тело обязано быть указано и действительно выбирается средствами КОМПАС (chooseType=ksChBodies + ChooseBodies().BodyCollection().Add(тело), измерено пробой P2.6); перед мутацией сверяются габариты нарисованного контура и заявленного тела, а после — объём каждого тела, поэтому no-op (КОМПАС возвращает Create=true и не меняет ничего) не выдаётся за успех. В ответе ТРИ разные величины названы раздельно, потому что раньше одно поле несло их попеременно: volume_mm3 — СУММА объёмов всех тел документа после операции (это не объём целевого тела и не объём пространственного объединения — у перекрывающихся тел сумма и объединение различаются); volume_delta_mm3 — приращение материала ЭТИМ признаком (у cut — снятое), а volume_delta_basis называет, из какого тела оно взято (target_body_N, new_body_volume, existing_body_N_delta, not_attributable); document_volume_before_mm3 и document_volume_after_mm3 — сумма по документу до и после. У base приращение берётся у НОВОГО тела, а не вычитанием из нуля, и подтверждается проверкой body_change_attribution: измениться обязано ровно одно тело. Поэтому на многотельном документе второй и третий вызовы подтверждаются измерением, а не занижаются до call_returned.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("operation", Sch.Enum("base — первое тело; boss — добавить; cut — вырезать.", "base", "boss", "cut")),
                    ("end_condition", Sch.Nullable(Sch.Enum(
                        "blind — на глубину depth_mm (значение по умолчанию); through — насквозь. through принят только для cut: измерено на v24, что «насквозь» работает при directionType=symmetric, а число глубины в этом режиме игнорируется.",
                        "blind", "through"))),
                    ("depth_mm", Sch.Nullable(Sch.PositiveMm(
                        "Глубина",
                        "Обязательна при end_condition=blind и запрещена при end_condition=through — "
                        + "сервер не принимает «насквозь с глубиной», потому что КОМПАС это число "
                        + "всё равно отбрасывает."))),
                    ("direction", Sch.Enum("positive — по нормали эскиза; negative — против; symmetric — в обе стороны.", "positive", "negative", "symmetric")),
                    ("target_body_ref", Sch.Nullable(Sch.Ref("#/$defs/reference")))),
                WorkerCommands.Extrude,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_fillet", "Скругление",
                "Скругление по явным edge_ref из kompas_read_topology (рёбра конечного тела, не EntityCollection(7)). Успехом считается не HRESULT: радиус перечитывается с созданного признака, число граней обязано вырасти ровно на число рёбер, объём должен уменьшиться.",
                Sch.Props(
                    ("edge_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Рёбра, полученные kompas_read_topology. Позиции в коллекции не принимаются — они не являются постоянными идентификаторами.",
                        1, 64)),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("radius_mm", Sch.PositiveMm("Радиус скругления")),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание уменьшения объёма, если выводимо у вызывающего (например 4·(1−π/4)·r²·h для четырёх параллельных рёбер). С ним проверка численная, без него — только направление изменения.",
                        0d, 1e18d)))),
                WorkerCommands.Fillet,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_chamfer", "Фаска",
                "Фаска по явным edge_ref из kompas_read_topology (рёбра конечного тела, не EntityCollection(7)). " +
                "Способ two_distances идёт маршрутом API5, измеренным пробой F: на пластине 100×80×10 четыре " +
                "вертикальных ребра с катетами 2×2 сняли ровно 20·d₁·d₂ = 80 мм³. Успехом считается не " +
                "Create(): катеты и transfer перечитываются с нового объекта определения, число граней " +
                "обязано вырасти ровно на число рёбер, а объём — уменьшиться. direction (API5 transfer, " +
                "API7 Direction) меняет, какой катет ложится на какую грань; при равных катетах это " +
                "неразличимо. Способ distance_angle — единственный, которого в API5 нет физически " +
                "(угла у ksChamferDefinition не объявлено), и идёт типизированным маршрутом API7 того же " +
                "сеанса: угол в ГРАДУСАХ, измерено F.10 (Angle=30 при катете 2 снял 20·d·(d·tg 30°) = " +
                "46.188021535141 мм³). Если мост API7 недоступен, вызов отказывает кодом " +
                "CAPABILITY_UNAVAILABLE и ничего не создаёт, а не подменяется двумя катетами.",
                Sch.Props(
                    ("edge_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Рёбра, полученные kompas_read_topology. Позиции в коллекции не принимаются — они не являются постоянными идентификаторами.",
                        1, 64)),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("mode", Sch.Enum(
                        "two_distances — двумя катетами (API5); distance_angle — расстоянием и углом (API7, угол в градусах).",
                        "two_distances", "distance_angle")),
                    ("distance1_mm", Sch.PositiveMm("Первый катет",
                        "При mode=distance_angle это РАССТОЯНИЕ, а не катет.")),
                    ("distance2_mm", Sch.Nullable(Sch.PositiveMm(
                        "Второй катет",
                        "Обязателен при mode=two_distances, запрещён при mode=distance_angle. " +
                        "Для фаски под 45° задайте его равным distance1_mm."))),
                    ("angle_deg", Sch.Nullable(Sch.Num(
                        "Угол фаски в ГРАДУСАХ, только строго между 0 и 90. Единицы измерены (проба F.10): " +
                        "Angle=30 при катете 2 снимает 20·d·(d·tg 30°). Обязателен при mode=distance_angle, " +
                        "запрещён при mode=two_distances.",
                        0d, 90d))),
                    ("direction", Sch.Nullable(Sch.Bool(
                        "Сторона фаски (API5 transfer, API7 Direction). Измерено: при неравных катетах меняет, " +
                        "какой катет ложится на какую из двух граней; объём при этом не различается. " +
                        "Без поля — false.", false))),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание уменьшения объёма: N·(d₁·d₂/2)·L для N параллельных рёбер длиной L. " +
                        "С ним проверка численная, без него — только направление изменения.",
                        0d, 1e18d)))),
                WorkerCommands.Chamfer,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_hole", "Родное отверстие",
                "Родное отверстие на явной опорной грани face_ref из kompas_read_topology. " +
                "Три измеренных режима, все — маршрутом API7 того же сеанса, потому что в API5 " +
                "параметров режима не существует физически: в вендорской обёртке " +
                "Interop.Kompas6API5 типа ksHoleDefinition НЕТ вовсе (среди 67 объявленных " +
                "определений есть ksChamferDefinition и ksFilletDefinition, отверстия нет), а " +
                "режимные числа живут не на IHole3D, а на HoleParameters, приведённом к интерфейсу " +
                "СВОЕГО режима. " +
                "blind_flat — глухое с плоским дном: ksDTValue (члена ksDTBlind в вендорском " +
                "перечислении не существует) плюс ksEFFlat; измерено M.4 — Ø10 глубиной 6 сняли " +
                "471.238898038471 против аналитических π·5²·6 = 471.238898038469. " +
                "through_counterbore — цековка: пилот насквозь плюс КОЛЬЦЕВАЯ выточка " +
                "π/4·(D²−d²)·h; измерено M.2 — пилот Ø10, выточка Ø18 глубиной 4 сняли " +
                "703.7167544041131 сверх сквозного против 703.7167544041137 аналитических " +
                "(первый набросок складывал пилот с целым цилиндром Ø18 и тем самым считал пилот " +
                "дважды). " +
                "through_countersink — зенковка: пилот насквозь плюс конус; снятое сверх пилота " +
                "равно π·h/3·(rM² + rP·rM − 2·rP²), где h — глубина, которую вернул САМ объект, " +
                "rM — радиус устья, rP — радиус пилота; правило прочитано с таблицы из 11 строк " +
                "(3 угла × 3 глубины × 6 диаметров) в M.3. Глубина зенковки ПРОИЗВОДНА и " +
                "записью не управляется: объект возвращает (rM − rP)/tan(угол/2), где rM — радиус " +
                "устья, rP — радиус пилота (измерено N.2: устья Ø14/16/18/20/24 при пилоте Ø10 и " +
                "90° дали h = 2/3/4/5/7). Поэтому глубина публикуется в ответе как " +
                "countersink_depth_read_back_mm. " +
                "Позиция вне начала координат задаётся парой offset_x_mm/offset_y_mm и работает " +
                "маршрутом Point3DParamSurface + OffsetType=ksOffsetByCoords: измерено M.5 — " +
                "отверстие Ø10 встало ровно в (25, 15) из пяти проверенных маршрутов, тогда как " +
                "AssociationVertex и DirectionObject дали DISP_E_TYPEMISMATCH, эскиз со смещённой " +
                "окружностью до API7 не доехал, а DepthVertex/DepthFace читаются как null. " +
                "Если мост API7 недоступен, вызов отказывает кодом CAPABILITY_UNAVAILABLE и " +
                "ничего не создаёт. Успехом считается не Update(): параметры перечитываются с " +
                "созданного признака, граней обязано стать больше, объём — уменьшиться, а при " +
                "заданном ожидании он сверяется числом.",
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
                        "Смещение центра отверстия вдоль X опорной грани, мм. Задаётся ВМЕСТЕ с " +
                        "offset_y_mm: одна координата без второй оставила бы другую на догадку " +
                        "сервера. Без пары отверстие остаётся в начале координат поверхности.",
                        -1e6d, 1e6d))),
                    ("offset_y_mm", Sch.Nullable(Sch.Num(
                        "Смещение центра отверстия вдоль Y опорной грани, мм.",
                        -1e6d, 1e6d))),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание уменьшения объёма: π·r²·h для глухого; " +
                        "π·r²·h + π/4·(D²−d²)·h_выточки для цековки; " +
                        "π·r²·h + π·h_факт/3·(rM² + rP·rM − 2·rP²) для зенковки, причём h_факт — " +
                        "глубина, которую ВЕРНУЛ объект, а не запрошенная. С ним проверка " +
                        "численная, без него — только направление изменения.",
                        0d, 1e18d)))),
                WorkerCommands.Hole,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_rotated", "Вращение",
                "Признак вращения: плоский эскиз-профиль разворачивается вокруг явной оси. Весь " +
                "маршрут — фабрика API7 (IModelContainer.Rotateds.Add), и ни одного вызова API5 " +
                "NewEntity/Create здесь нет намеренно. Прежняя блокировка была СМЕШАННЫМ жизненным " +
                "циклом: оболочка NewEntity(27) завершалась Create(), которая возвращала true, " +
                "объект появлялся в дереве, а тело не строилось. Это был дефект МАРШРУТА, а не " +
                "свойства вращения: той же фабрикой все три вида операции дают тело с аналитическим " +
                "объёмом. Если мост API7 недоступен, вызов отказывает CAPABILITY_UNAVAILABLE и " +
                "ничего не создаёт. " +
                "operation=base — первое тело (o3d_baseRotated 27); boss — приклейка к " +
                "существующему телу (28); cut — вырезание (29). Измерено R.24: все три вида дали " +
                "50265.4824574366 против π·r²·h = 50265.4824574367 при r=20, h=40. " +
                "ИСХОД ОПЕРАЦИИ ВЫБИРАЕТ OperationResult, А ДОПУСТИМЫЕ ЗНАЧЕНИЯ ОБЪЯВЛЯЕТ ВИД " +
                "ФАБРИКИ: base пишет ksOperationNewBody, boss — ksOperationUnion, cut — " +
                "ksOperationCut. Прежнее утверждение «тип операции решает ФАБРИКА, а записанный " +
                "OperationResult действие не переключает» ОПРОВЕРГНУТО 18.09.2026 управляемым " +
                "опытом F.10 на ОДНОЙ геометрии, где менялся только этот член: ksOperationUnion " +
                "дал ОДНО тело с приростом 37699.1118430774, ksOperationNewBody — ДВА тела с " +
                "приростом 50265.4824574366. Опыт R.26, на который опиралось прежнее утверждение, " +
                "писал пару, недопустимую для своего вида фабрики, и потому измерял отказ, а не " +
                "свойство. " +
                "БОБЫШКА СРАЩИВАЕТСЯ С СУЩЕСТВУЮЩИМ ТЕЛОМ. Прирост равен ОБЪЁМУ ТЕЛА МИНУС " +
                "ПЕРЕСЕЧЕНИЕ, а не объёму тела. RO.19: плита 120×120×10 (V0=144000) и бобышка R20 " +
                "с осью ВДОЛЬ Z, полный оборот — V=181699.1118430774, прирост 37699.1118430774 = " +
                "16000π − 4000π при ОДНОМ теле. RO.2: ось ЛЕЖИТ в плоскости грани плиты — прирост " +
                "34959.6988175884, и это верно для СВОЕЙ постановки. Прежнее «бобышка даёт второе " +
                "тело» было следствием прибора, который писал ksOperationNewBody для любого вида, " +
                "кроме cut. " +
                "Профиль задаётся sketch_ref и берётся из ПЛОСКОГО замкнутого эскиза: это тело " +
                "развёртки, а не «эскиз вообще». Ось обязательна и задаётся двумя точками модели " +
                "axis_point1_mm/axis_point2_mm — без оси вращение не строится вовсе (измерено: " +
                "Update()=False, тел 0). " +
                "УГОЛ УПРАВЛЯЕТ РАЗВЁРТКОЙ ДО 360 У ВСЕХ ТРЁХ ВИДОВ. Прежнее утверждение " +
                "«насыщение на 180°» ОПРОВЕРГНУТО измерением 18.09.2026: оно происходило из шага " +
                "R.26.angles, который менял CutOffByPoint, а не угол, и ни в одной строке не " +
                "записывал Angle[true]=360 с полным набором параметров развёртки. Angle[true] несёт " +
                "запрошенный угол напрямую (90→90°, 180→180°, 360→360°), а вторая половина пары, " +
                "равная первой, развёртку удваивает. " +
                "base: полный оборот выражается ОДНИМ вызовом с angle_deg=360 и даёт полный " +
                "цилиндр V=50265.4824574366 при r=20, h=40, одна цилиндрическая грань r=20 h=40, " +
                "габарит 40×40×40; отказ начинается только выше 360 — это потолок сектора. " +
                "cut: ПОЛНЫЙ ОБОРОТ ВЫРАЖАЕТСЯ. Прежнее «у cut развёртка ограничена полуоборотом» " +
                "было следствием ОДНОСТОРОННЕЙ заготовки: опыт F.8 шёл на плите, выдавленной в " +
                "одну сторону (z∈[0,40]), тогда как развёртка вращения СИММЕТРИЧНА плоскости " +
                "эскиза (z∈[−20,20]), поэтому пересечение исчерпывалось на половине тела и 180°, " +
                "270°, 360° снимали одно и то же. На плите, вмещающей инструмент ЦЕЛИКОМ (x,y ∈ " +
                "[−60,60], z ∈ [−20,20], V0=576000), cut 360° снимает ровно 50265.4824574366 " +
                "(RO.18z/RO.18g: V=525734.5175425634, одна цилиндрическая грань r=20 h=40), 180° — " +
                "половину, 90° — четверть. Разбор и таблицы — " +
                "docs/acceptance/api7/full-turn-findings.md §11. " +
                "direction=reverse отвергается CAPABILITY_UNAVAILABLE: измерено (R.26.sector), что " +
                "при нём признак не строится вовсе — Update()=False, тел 0. Доступны normal, both и " +
                "middle_plane, причём сектор двигает ТОЛЬКО middle_plane, и различается это " +
                "ГАБАРИТОМ на НЕПОЛНОМ обороте: на полном тело осесимметрично и направление " +
                "неотличимо по построению. " +
                "Тонкая стенка (thin_wall_mm) отвергается CAPABILITY_UNAVAILABLE: маршрут измерен " +
                "на сплошном теле (IThinParameters.Thin = false), тонкая стенка вращения не " +
                "измерялась ни одним прогоном — число не записывается. " +
                "target_body_ref ПРИНИМАЕТСЯ и проверяется ПОСЛЕ операции. НАЗНАЧИТЬ цель нельзя: " +
                "селектора тела (chooseType/ChooseBodies, как у выдавливания) у IRotated и " +
                "IRotated1 нет — они объявлены только у API5-определений ksBossRotatedDefinition и " +
                "ksCutRotatedDefinition. Измерено (F.11) на детали из двух тел, что ядро относит " +
                "операцию к ПЕРЕСЕКАЕМОМУ телу и трогает РОВНО одно; если объявленная цель с " +
                "результатом не совпала, вызов отвечает GEOMETRY_FAILED с partial_effects, а не " +
                "согласием. Неизвестная ссылка отвергается до мутации (RO.8e, RO.17n5). Коллекция " +
                "тел после операции ПЕРЕУПОРЯДОЧИВАЕТСЯ, поэтому тела сопоставляются по объёму и " +
                "габариту, а не по индексу. " +
                "Успехом считается не Update(): параметры перечитываются с созданного признака, " +
                "обязана появиться поверхность вращения (цилиндрическая грань читается через " +
                "ksCylinderParam вместе с габаритом — объём не отличает цилиндр R20 H40 от плиты " +
                "того же объёма), а знак изменения объёма сверяется с видом операции: cut обязан " +
                "снять материал. При заданном expected_volume_mm3 он сверяется числом. Признак " +
                "пережил save → close → reopen с повторно полученными Axis и Profile (измерено " +
                "R.25), поэтому чтение и правка после переоткрытия поддержаны.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("operation", Sch.Enum(
                        "Вид операции вращения. base — первое тело (o3d_baseRotated); boss — " +
                        "приклейка к существующему телу (o3d_bossRotated); cut — вырезание " +
                        "(o3d_cutRotated). Вид задаёт ФАБРИКА, и она же объявляет ДОПУСТИМЫЕ " +
                        "значения OperationResult, а записанное значение выбирает исход: base " +
                        "пишет ksOperationNewBody, boss — ksOperationUnion, cut — ksOperationCut. " +
                        "Измерено 18.09.2026 (проба F.10 — управляемый опыт на ОДНОЙ геометрии, " +
                        "менялся только OperationResult): boss с Union СРАЩИВАЕТСЯ с телом (тел " +
                        "1→1, прирост равен объёму тела минус пересечение), boss с NewBody даёт " +
                        "ВТОРОЕ тело (тел 1→2, прирост равен всему цилиндру). Прежнее утверждение " +
                        "«записанный OperationResult действие НЕ переключает» ОПРОВЕРГНУТО: оно " +
                        "происходило из опыта, который писал NewBody и получал ровно запрошенное.",
                        "base", "boss", "cut")),
                    ("angle_deg", Sch.Num(
                        "Угол развёртки в ГРАДУСАХ, строго больше 0 и не больше 360. Полный оборот " +
                        "(360) ВЫРАЖАЕТСЯ одним вызовом: измерено 18.09.2026, что он даёт полный " +
                        "цилиндр (V=50265.4824574366 при r=20, h=40) — проба F.1a, подтверждено " +
                        "слепым чтением сохранённого .m3d. Прежнее утверждение «насыщается на 180°» " +
                        "ОПРОВЕРГНУТО: оно происходило из шага, менявшего CutOffByPoint, а не угол. " +
                        "Значение больше 360 отвергается INVALID_ARGUMENT: сектор не может занять " +
                        "больше целого оборота.",
                        0d, 360d)),
                    ("axis_point1_mm", Sch.Vec3(
                        "Первая точка оси вращения в координатах МОДЕЛИ, мм. Ось строится в этой же " +
                        "детали как вспомогательная геометрия (Axes3D.Add по двум точкам). Ось " +
                        "обязательна: без неё вращение не строится вовсе (измерено — Update()=False, " +
                        "тел 0). Точки обязаны различаться: вырожденная ось не задаёт развёртки.")),
                    ("axis_point2_mm", Sch.Vec3(
                        "Вторая точка оси вращения в координатах МОДЕЛИ, мм. Порядок точек задаёт " +
                        "направление оси.")),
                    ("direction", Sch.Nullable(Sch.Enum(
                        "Сторона развёртки относительно оси. normal — по умолчанию; both — сектор " +
                        "по обе стороны от оси; middle_plane — единственное направление, которое " +
                        "ДВИГАЕТ сектор (измерено R.26.sector). reverse ОТВЕРГАЕТСЯ " +
                        "CAPABILITY_UNAVAILABLE: измерено, что при нём признак не строится вовсе — " +
                        "Update()=False, тел 0.",
                        "normal", "reverse", "both", "middle_plane"))),
                    ("thin_wall_mm", Sch.Nullable(Sch.PositiveMm(
                        "Толщина тонкой стенки вращения",
                        "НЕ поддержана: маршрут измерен только на СПЛОШНОМ теле "
                        + "(IThinParameters.Thin = false). Любое значение отвергается "
                        + "CAPABILITY_UNAVAILABLE, потому что записать неизмеренное число значило бы "
                        + "выдать непроверенную конфигурацию за проверенную."))),
                    ("target_body_ref", Sch.Described(
                        Sch.Nullable(Sch.Ref("#/$defs/reference")),
                        "Ссылка на тело, которое операция обязана изменить. Целевое тело вращению " +
                        "НАЗНАЧИТЬ нельзя: IRotated и IRotated1 не объявляют ни chooseType, ни " +
                        "ChooseBodies (измерено по интероп-сборке; они есть только у " +
                        "API5-определений ksBossRotatedDefinition и ksCutRotatedDefinition). " +
                        "Поэтому ссылка проверяется ПОСЛЕ операции по поимённым снимкам тел: если " +
                        "изменилось не объявленное тело или тронуто ещё какое-то, вызов отвечает " +
                        "GEOMETRY_FAILED с partial_effects, а не согласием. Измерено (проба F.11) " +
                        "на детали из двух тел, что ядро относит операцию к ПЕРЕСЕКАЕМОМУ телу, а " +
                        "не к первому в коллекции, и что тронуто ровно одно тело.")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание ОБЪЁМА детали после операции, мм³. Для полного " +
                        "сектора r²/2·(α − sin α)·h при α в радианах и оси, смещённой от центра; " +
                        "для измеренного эталона (цилиндр R20 H40, ось по диаметру) полуоборот даёт " +
                        "π·r²·h/2 = 50265.4824574367, четверть — π·r²·h/4 = 25132.7412287183. С ним " +
                        "проверка численная, без него — только форма и знак изменения.",
                        0d, 1e18d)))),
                WorkerCommands.Rotated,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_sweep", "Элемент по траектории",
                "Кинематическая операция: новое тело по одному замкнутому профилю и непрерывной " +
                "плоской траектории (SM-04, профиль mechanical-core-v1, очередь B5). Маршрут — API5 " +
                "o3d_baseEvolution=45 → ksBaseEvolutionDefinition, документированный страницей " +
                "ksbaseevolutiondefinition.html («можно получить, используя метод " +
                "ksEntity::GetDefinition»). Интерфейс ОБЪЯВЛЕН УСТАРЕВШИМ в самой справке — «Данный " +
                "интерфейс устарел. Рекомендуется использовать вместо него интерфейс " +
                "ksBossLoftDefinition»; рекомендованный приклеенный маршрут o3d_bossEvolution=46 " +
                "измерен отдельно (шаг B5.8) и строит ТО ЖЕ тело на тех же различающих эталонах — " +
                "31415.92653589775 против аналитического S·L = π·10²·100 = 31415.926535897932. " +
                "Базовый тип выбран потому, что обязанные строки описаны как базовые, а не потому, " +
                "что он «новее» или «удобнее». " +
                "РЕЖИМ ОРИЕНТАЦИИ задаётся shift_mode, и значения документированы страницей " +
                "ksbaseevolutiondefinition_sketchshifttype.html: parallel = 0, keep_angle = 1, " +
                "orthogonal = 2 — они же получены измерением. На ПРЯМОЙ траектории parallel и " +
                "orthogonal дают одно тело; различающая постановка — только дуга: на R50/90° " +
                "orthogonal дал 24674.011002723353 против аналитического S × L = 24674.011002723397, " +
                "parallel — 15707.963267948984, отличие 8966.047734774369 мм³. Поэтому приёмка режима " +
                "обязана стоять на ДУГЕ, а не на отрезке. " +
                "ДОКУМЕНТИРОВАННЫЕ ОГРАНИЧЕНИЯ ПЕРЕНЕСЕНЫ В КОНТРАКТ: ортогональный режим «только " +
                "для объекта-сечения, недоступно при вырезании телом» (aw2303904.html); касательная " +
                "траектории не должна быть параллельна плоскости сечения (там же). " +
                "УСПЕХОМ СЧИТАЕТСЯ НЕ Create(): после построения перечитывается GetPathLength(1) — " +
                "метод документирован как работающий только на уже построенной операции, — число тел " +
                "обязано вырасти, а объём измениться. " +
                "Тонкая стенка выразима только через API5 SetThinParam: в API7 IEvolution члена " +
                "ThinParam нет вовсе (OQ-A4), и это названная граница, а не строка обязательного " +
                "объёма. ЧЕГО ИНСТРУМЕНТ НЕ ДЕЛАЕТ И ГОВОРИТ ОБ ЭТОМ ПРЯМО: тело-инструмент " +
                "вырезания по траектории не задаётся (OQ-A2, вне состава), форма сечения по законам " +
                "не задаётся (OQ-A3), разрыв траектории не «залечивается» — он приходит отказом " +
                "ядра, и непрерывность обязан проверять вызывающий до построения.",
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
                        "Аналитическое ожидание объёма детали после операции, мм³. Для прямой " +
                        "траектории и постоянного профиля это S × L: измеренный эталон — окружность " +
                        "R10 по отрезку 100 мм даёт π·10²·100 = 31415.926535897932. С ним проверка " +
                        "численная, без него — только число тел и направление изменения.",
                        0d, 1e18d))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.Sweep,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_loft", "Элемент по сечениям",
                "Новое тело по двум и более сечениям; порядок массива = порядок соединения (SM-05). " +
                "Маршрут — API7, и это следует из СОСТАВА ОБЯЗАТЕЛЬНЫХ СТРОК, а не из удобства: " +
                "строка SM-05.base.mode_couplings требует цепочек соответствия сечений, а в API5 их " +
                "нет вовсе — ни ksBaseLoftDefinition, ни ksBossLoftDefinition не объявляют ни " +
                "AddCoupling, ни Coupling. В API7 они документированы (iloft_propers.html: Coupling, " +
                "CouplingsCount; iloft_addcoupling.html: AddCoupling() → ICoupling) и измерены " +
                "(шаг B5.9: AddCoupling() вернул KompasAPI7.CouplingClass, CouplingsCount = 1). " +
                "ЦЕПОЧКИ СООТВЕТСТВИЯ ВЫРАЖАЮТСЯ ПАРАМЕТРОМ couplings, И ЭТО ИЗМЕРЕНО, А НЕ ОБЪЯВЛЕНО: " +
                "на пирамиде 40×40 → 20×20 при h = 30 цепочка со смещениями 0 / 0 даёт 28000 — ровно " +
                "как без цепочки, то есть явное соответствие ЗАМЕЩАЕТ автоматическое; смещение точки " +
                "второго сечения на 20 мм (25 % контура 80 мм) даёт 20000; возврат даёт 28000. " +
                "Цепочка задаётся ДО первого Update() — так документирована фабрика (ilofts_add.html: " +
                "«задать параметры операции и вызвать IModelObject::Update»), и измерено (шаг B5.18), " +
                "что одного построения достаточно. Наружу выходит СМЕЩЕНИЕ вдоль контура " +
                "(PositionOffset, мм), а не координаты точки: измерено, что SetPoint проецирует " +
                "поданную точку на контур и читается обратно в локальных координатах эскиза " +
                "(подано (10; 10; 30) — центр квадрата 20×20, прочитано (20; 10; 30)). " +
                "Фабрика документирована для приклеенного типа (ilofts_add.html: o3d_bossLoft / " +
                "o3d_cutLoft, затем IModelObject::Update), поэтому используется o3d_bossLoft = 31: " +
                "базового o3d_baseLoft = 30 в документированном списке Add НЕТ. Сечения задаются " +
                "свойством ILoft.Sketchs типа VARIANT — «массив SAFEARRAY объектов LPDISPATCH» " +
                "(iloft_sketchs.html); измерено: присваивание object[] дало чтение System.Object[] " +
                "из 2 элементов, Update() = True, объём 28000. " +
                "ОБЪЁМ СЧИТАЕТСЯ ПО ФОРМУЛЕ УСЕЧЁННОЙ ПИРАМИДЫ V = h/3·(A₁ + A₂ + √(A₁A₂)), а не " +
                "«средней площадью × высота»: для 40×40 → 20×20 при h = 30 это 28000 против 30000, и " +
                "расхождение 2000 мм³ достаточно, чтобы отличить верную формулу от ошибочной. " +
                "ПОРЯДОК СЕЧЕНИЙ ОБЪЁМ НЕ РАЗЛИЧАЕТ при концентрических параллельных сечениях " +
                "(измерено: 28000 в обе стороны), поэтому «порядок соблюдён» доказывается различающей " +
                "постановкой — разной формой, поворотом сечений или габаритом, — и это названо в " +
                "unverified_aspects, а не выдано за проверенное. " +
                "ПАРАЛЛЕЛЬНОСТЬ ПЛОСКОСТЕЙ СЕЧЕНИЙ проверяет вызывающая сторона: сам loft её не " +
                "требует и не запрещает, поэтому отказ на непараллельные плоскости — наша " +
                "ответственность и приходит именованным кодом. " +
                "Документированные границы, названные прямо: все сечения плоские; все замкнуты ИЛИ " +
                "все разомкнуты; первое/последнее может быть точкой эскиза при замкнутых остальных " +
                "(secheniya_elementa.html). Тонкая стенка не выражается — в API7 ILoft члена ThinParam " +
                "нет (OQ-A4). Условия на концах, направляющие кривые, ось и купол вне обязательного " +
                "объёма этого этапа; неподдержанное значение building отвергается до COM, а не " +
                "приводится к auto молча.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("section_refs", Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Сечения В ПОРЯДКЕ соединения: эскизы, контуры, пространственные кривые или " +
                        "грани — состав объявлен справкой SDK (iloft_propers.html). Не менее двух: по " +
                        "одному сечению тело не строится. Порядок массива и есть порядок соединения, и " +
                        "сервер его не переупорядочивает.",
                        2, 64)),
                    ("building", Sch.Nullable(Sch.Enum(
                        "Способ построения у крайних сечений — ILoft.BuildingType(BeginSection). " +
                        "Значения документированы страницей ksloftbuildingtype.html: auto = 0, " +
                        "by_normal = 1, by_object = 2, cupola = 3. ИЗМЕРЕН только auto (шаг B5.9: " +
                        "BuildingType(true) = BuildingType(false) = 0), поэтому не-auto отвергается до " +
                        "COM, а не подменяется молча. Без поля — auto.",
                        "auto", "by_normal", "by_object", "cupola"))),
                    ("closed", Sch.Nullable(Sch.Bool(
                        "Замкнуть траекторию соединения сечений — ILoft.Closed. Это НЕ «замкнутая " +
                        "оболочка»: замыкается траектория, а не тело. Запись и обратное чтение " +
                        "измерены (шаг B5.9: записано false, прочитано False). Без поля — false.",
                        false))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма детали после операции, мм³, по формуле усечённой " +
                        "пирамиды V = h/3·(A₁ + A₂ + √(A₁A₂)): измеренный эталон 40×40 → 20×20 при " +
                        "h = 30 даёт 28000. С ним проверка численная, без него — только число тел и " +
                        "изменение объёма.", 0d, 1e18d))),
                    ("couplings", Sch.Nullable(Sch.Described(
                        (JsonObject)CouplingsSchema.DeepClone(),
                        "Цепочки соответствия сечений (SM-05.base.mode_couplings). " +
                        (string)CouplingsSchema["description"]!))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.Loft,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_shell", "Оболочка",
                "Оболочка постоянной толщины с удалением выбранных граней (SM-13). Маршрут — API5 " +
                "o3d_shellOperation=43 → ksShellDefinition. " +
                "ПУСТОЙ СПИСОК ГРАНЕЙ НЕ ДАЁТ ЗАМКНУТОЙ ОБОЛОЧКИ, И ЭТО ИЗМЕРЕНО НА ОБОИХ API. " +
                "Наряд ожидал 36224 (80000 − 96·76·6). Измерено на API5 (шаг B5.6): 80000. Измерено " +
                "на API7 (шаг B5.10, четыре постановки на том же коробе 100×80×10): " +
                "79999.99999999999 при 6 ГРАНЯХ — ровно как у исходного короба, тогда как открытая " +
                "оболочка даёт 21632 при 11 гранях. То есть Update() = True означает «принято», а " +
                "неизменное число граней — «не применено»; это второй независимый признак рядом с " +
                "объёмом. Поэтому пустой список граней отвергается до COM именованным отказом, а не " +
                "выдаётся за замкнутую оболочку. " +
                "НАПРАВЛЕНИЕ СТЕНКИ ЗАДАЁТСЯ ЗНАЧЕНИЕМ, И СООТВЕТСТВИЕ ПОДТВЕРЖДЕНО ДВАЖДЫ. " +
                "Документация: ksshelldefinition_thintype.html — «TRUE — внутрь, FALSE — наружу». " +
                "Измерение API5 (шаг B5.5): true → 21631.999999999996, false → 24832.000000000022. " +
                "Измерение API7 (шаг B5.10): ThinType = 1 → 21631.999999999996, ThinType = 0 → " +
                "24832.000000000022. Отличие 3200.0000000000255 мм³: сторона различается ОБЪЁМОМ, а " +
                "не текстом ответа. Толщина проверяется ВТОРЫМ числом: t = 4 внутрь даёт " +
                "40255.99999999999 против 21632 при t = 2, отличие 18624. " +
                "tangent_faces ОБЪЯВЛЕН В КОНТРАКТЕ, НО true ОТВЕРГАЕТСЯ: у API5 ksShellDefinition " +
                "члена «касательные грани» нет вовсе, а маршрут этого инструмента — API5. Значение " +
                "false принимается. Так параметр виден вызывающему, и при этом ни один вызов не " +
                "получает успех за работу, которой не было. " +
                "Разные толщины отдельных граней вне выпуска (SM-13.shell.mode_variable_thickness, " +
                "explicitly_out_of_scope); путь получения IMultiThicknessGroupsManager остаётся " +
                "открытым вопросом OQ-A13 и задачей этого этапа не является.",
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
                        "Аналитическое ожидание объёма детали после операции, мм³. Измеренные эталоны " +
                        "на коробе 100×80×10 с удалённой верхней гранью: внутрь t = 2 → 21632, наружу " +
                        "t = 2 → 24832, внутрь t = 4 → 40256. С ним проверка численная, без него — " +
                        "только направление изменения и число граней.", 0d, 1e18d))),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision"))),
                WorkerCommands.Shell,
                requiresDocument: true,
                requiresRevision: true),

            Mutation("kompas_pattern_grid", "Массив по сетке",
                "Массив экземпляров в узлах параллелограммной сетки (SM-18). Маршрут ОДИН и он " +
                "опубликован: IModelContainer.FeaturePatterns.Add(o3d_meshCopy=35) → QI(ILinearPattern), " +
                "затем InitialObjects/Axis1/Step1/Count1 и Update(). Соответствие «35 → ILinearPattern» " +
                "взято со страницы справки SDK copytype.html, открытой по проводу, а не выведено по " +
                "аналогии с вращением. Ни одного NewEntity и ни одного Create() здесь нет намеренно: " +
                "оболочка API5 вокруг объекта фабрики API7 не строит ничего — это измерено на вращении " +
                "(SM-03) и повторять этот опыт не нужно. " +
                "ЧТО КОПИРУЕТСЯ решает copy_kind и это ОТДЕЛЬНЫЙ ЧИСЛОВОЙ ТИП фабрики, а не флаг: " +
                "operations — o3d_meshCopy=35 (копии наследуют область применения исходной операции, " +
                "новых тел НЕ появляется), bodies — o3d_BodiesMeshCopy=528 (копируются тела, число тел " +
                "растёт). Различие документировано справкой 48_2_osobennoiti_postroeniy_massiviv_v_mnogotelnoy_detali. " +
                "ВТОРОЕ НАПРАВЛЕНИЕ НЕОБЯЗАТЕЛЬНО: не заданы axis2_point1_mm/axis2_point2_mm и count2 — " +
                "это массив по одной линии (SM-18.grid.single_row), и тогда Count2 = 1. " +
                "УГЛЫ НАЗЫВАЮТ НАПРАВЛЕНИЕ, И ЭТО ИЗМЕРЕНО, А НЕ ПРЕДПОЛОЖЕНО. angle2_deg — угол " +
                "МЕЖДУ направлениями: прямоугольная сетка — это angle2_deg = 90°; при 0° второе " +
                "направление совпадает с первым и сетка вырождается в линию (измерено: копии " +
                "продолжили первую ось — x = 20, 40, 60, 50, 70, 90 при y = 20); при 270° модель " +
                "приводит значение к 180° и уводит второе направление в противоположную сторону. " +
                "Вторая ось задаёт, В КАКУЮ сторону откладывать угол. angle1_deg наклоняет ПЕРВУЮ " +
                "ось и уводит направление с построенной оси (измерено: при 90° копии ушли с оси X " +
                "на Y). Оба угла НЕ заданы — значения модели остаются как есть (0 и 90°). " +
                "ОСЬ ЗАДАЁТСЯ ДВУМЯ ТОЧКАМИ МОДЕЛИ и строится в ТОЙ ЖЕ детали (Axes3D.Add по двум " +
                "точкам) — ссылка из чужой детали сюда не протаскивается. ВЕКТОР НАПРАВЛЕНИЯ НЕ " +
                "ПРИНИМАЕТСЯ: у ILinearPattern.Vector1/Vector2 в kAPI7.tlb объявлены только геттеры, а в " +
                "вендорской интероп-сборке они не объявлены вовсе (измерено прибором InteropScan), " +
                "поэтому направление задаётся осью. Это открытый вопрос OQ-B-03, названный, а не " +
                "обойдённый. " +
                "geometry_pattern=true ОТВЕРГАЕТСЯ CAPABILITY_UNAVAILABLE: геометрический массив " +
                "документирован (48_3_3_geometricheskiy_massiv), но лежит ВНЕ очереди B4 (режим " +
                "SM-18.grid.operations.geometry), и записывать незмеренную настройку нельзя. " +
                "Успехом считается не Update(): параметры перечитываются с созданного признака, " +
                "сверяются число экземпляров (GetExemplarsCounts), объём документа и число тел. Если " +
                "заданы expected_hole_radius_mm/expected_hole_height_mm, дополнительно читаются оси " +
                "цилиндрических граней: это и есть проверка ПО КАЖДОМУ ЭКЗЕМПЛЯРУ — объём не отличает " +
                "четыре отверстия от трёх и одного наложенного, а набор координат отличает. " +
                "Объём сверяется с expected_volume_mm3 в допуске 0,01 мм³ / 1e-6 отн. (больший из двух); " +
                "числа экземпляров и тел сверяются ТОЧНО, допуск к счётным величинам не применяется.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("copy_kind", Sch.Enum(
                        "Что копируется. operations — массив ОПЕРАЦИЙ (o3d_meshCopy=35): копии " +
                        "наследуют область применения исходной операции, новых тел не появляется. " +
                        "bodies — массив ТЕЛ (o3d_BodiesMeshCopy=528): копируются тела, число тел " +
                        "растёт. Это разные типы фабрики, а не флаг поведения.",
                        "operations", "bodies")),
                    ("source_refs", Sch.Arr(Sch.Ref("#/$defs/reference"),
                        "Исходные объекты массива: feature:-ссылки при copy_kind=operations, " +
                        "body:-ссылки при copy_kind=bodies. Пустой список отвергается до COM: массив " +
                        "без исходных объектов не собирается. Смешивать виды нельзя — тип фабрики " +
                        "выбирается один.", 1)),
                    ("axis1_point1_mm", Sch.Vec3("Первая точка оси первого направления, координаты МОДЕЛИ, мм.")),
                    ("axis1_point2_mm", Sch.Vec3("Вторая точка оси первого направления. Точки обязаны различаться.")),
                    ("step1_mm", Sch.PositiveMm("Шаг копирования по первой оси",
                        "Единица (мм) подтверждается калибровочной пробой: шаг 20 при трёх " +
                        "экземплярах даёт базу 40 мм между крайними.")),
                    ("count1", Sch.Int("Число экземпляров по первой оси, включая исходный. Не меньше 1.", 1, 100_000)),
                    ("angle1_deg", Sch.Nullable(Sch.Num(
                        "Угол наклона первой оси сетки, ГРАДУСЫ. Не задан — значение модели остаётся как " +
                        "есть (0), то есть ось берётся как построена. Измерено: 90° уводит направление " +
                        "С построенной оси (копии ушли с оси X на Y), поэтому прямоугольность задаётся " +
                        "не этим углом, а взаимной перпендикулярностью осей и angle2_deg.", -360d, 360d))),
                    ("direction1", Sch.Nullable(Sch.Bool(
                        "Направление копирования вдоль первой оси. По умолчанию true."))),
                    ("boundary_instances_step_factor1", Sch.Nullable(Sch.Bool(
                        "Интерпретация значения шага на границе первого направления " +
                        "(BoundaryInstancesStepFactor1). Документирован страницей ilinearpattern_props.html; " +
                        "по умолчанию false."))),
                    ("axis2_point1_mm", Sch.Nullable(Sch.Vec3(
                        "Первая точка оси ВТОРОГО направления. Не задана — массив по одной линии."))),
                    ("axis2_point2_mm", Sch.Nullable(Sch.Vec3("Вторая точка оси второго направления."))),
                    ("step2_mm", Sch.Nullable(Sch.Num(
                        "Шаг по второму направлению, мм. Обязателен, когда count2 больше 1.", 0d, 1e9d))),
                    ("count2", Sch.Nullable(Sch.Int(
                        "Число экземпляров по второму направлению. Не задано — направление не участвует.", 1, 100_000))),
                    ("angle2_deg", Sch.Nullable(Sch.Num(
                        "Угол МЕЖДУ направлениями сетки, ГРАДУСЫ. Прямоугольная сетка — 90°; 0° " +
                        "совмещает второе направление с первым и вырождает сетку в линию. Измерено " +
                        "прогоном при второй оси (0,0,0)→(0,−80,0) и шаге 30: при 0° копии продолжили " +
                        "первую ось, при 90° встали по второй, при 270° модель привела значение к 180° " +
                        "и увела второе направление в противоположную сторону. Не задан — значение " +
                        "модели остаётся как есть (90°).", -360d, 360d))),
                    ("direction2", Sch.Nullable(Sch.Bool("Направление копирования вдоль второй оси."))),
                    ("boundary_instances_step_factor2", Sch.Nullable(Sch.Bool(
                        "Интерпретация шага на границе второго направления."))),
                    ("building_type", Sch.Nullable(Sch.Enum(
                        "Способ построения массива, ksLinearPatternBuildingTypeEnum. Значения прочитаны " +
                        "из интероп-сборки констант, а не из каталога.",
                        "save_all", "save_along_perimeter", "save_along_axially",
                        "chess_order_by_axis1", "chess_order_by_axis2"))),
                    ("geometry_pattern", Sch.Nullable(Sch.Bool(
                        "Геометрическое копирование. true ОТВЕРГАЕТСЯ CAPABILITY_UNAVAILABLE: режим " +
                        "SM-18.grid.operations.geometry документирован, но в обязательный объём B4 не " +
                        "входит, и записывать незмеренную настройку нельзя."))),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма документа после операции, мм³. На эталонной " +
                        "пластине 100×80×10 (V0 = 80 000) со сквозным отверстием Ø10 " +
                        "(π·25·10 = 785,398163) три отверстия дают 77 643,806470, шесть — 75 287,611810. " +
                        "Без него численная проверка объёма не выполняется.", 0d, 1e18d))),
                    ("expected_body_count", Sch.Nullable(Sch.Int(
                        "Ожидаемое число тел после операции. Для массива операций оно равно числу тел до " +
                        "операции, для массива тел — растёт. Сравнение точное.", 0, 100_000))),
                    ("expected_hole_radius_mm", Sch.Nullable(Sch.Num(
                        "Радиус цилиндрической грани-экземпляра, мм (Ø10 → 5). Без него поимённая " +
                        "проверка экземпляров не выполняется, и это честно помечается в ответе.", 0d, 1e6d))),
                    ("expected_hole_height_mm", Sch.Nullable(Sch.Num(
                        "Высота цилиндрической грани-экземпляра, мм (толщина пластины для сквозного " +
                        "отверстия).", 0d, 1e6d))),
                    ("expected_hole_count", Sch.Nullable(Sch.Int(
                        "Аналитическое ожидание числа экземпляров-отверстий. Точное совпадение.", 0, 100_000))),
                    ("expected_hole_centers_mm", Sch.Nullable(Sch.Arr(
                        Sch.Arr(Sch.Num("Компонента координаты, мм."), null, 3, 3),
                        "Аналитические координаты осей каждого экземпляра в координатах модели, мм. " +
                        "Объём не отличает четыре отверстия от трёх и одного наложенного — этот набор " +
                        "отличает.")))),
                WorkerCommands.PatternGrid,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_pattern_circular", "Массив по концентрической сетке",
                "Массив по концентрической сетке (SM-19). Маршрут: " +
                "IModelContainer.FeaturePatterns.Add(o3d_circularCopy=36) → QI(ICircularPattern) → " +
                "запись параметров → Update(). Соответствие «36 → ICircularPattern» взято со страницы " +
                "справки SDK copytype.html, открытой по проводу. " +
                "СООТНЕСЕНИЕ НАПРАВЛЕНИЙ ВЗЯТО СО СТРАНИЦЫ СПРАВКИ, А НЕ ИЗ ОЖИДАНИЯ. " +
                "icircularpattern_props.html называет Count1 «количеством экземпляров в РАДИАЛЬНОМ " +
                "направлении», Step1 — «шагом копирования в РАДИАЛЬНОМ направлении», Count2 — " +
                "«количеством экземпляров в КОЛЬЦЕВОМ направлении», а Step2 — «УГЛОВЫМ шагом (ГРАДУСЫ) — " +
                "шагом копирования в кольцевом направлении». Поэтому кольцевое направление — это пара " +
                "(count2, step2_deg), а не (count1, step1_mm), как предполагалось до проверки страницы. " +
                "Это закрытие OQ-B-02: ожидание поставлено под сомнение первым, как и требует правило. " +
                "ПОЛНЫЙ ОБОРОТ ВЫРАЖАЕТСЯ ПАРОЙ (count2, step2_deg): отдельного признака «полный оборот» " +
                "у ICircularPattern нет ни в справке, ни в интероп-сборке. Ожидаемая семантика — " +
                "step2_deg = 360 / count2; она проверяется обеими половинами В ОДНОЙ постановке: при " +
                "неверной трактовке последний экземпляр ложится на исходный, и разница видна ровно " +
                "объёмом одного экземпляра. " +
                "save_initial_orientation есть ТОЛЬКО у кругового массива: у ILinearPattern этого члена " +
                "нет ни в справке, ни в интероп-сборке, поэтому между семействами он не переносится; " +
                "умолчание - доворот экземпляров до радиального направления (false). " +
                "Шаг вдоль оси (step_by_axis_mm) и направление построения (reverse_direction) " +
                "документированы той же страницей. " +
                "geometry_pattern=true ОТВЕРГАЕТСЯ CAPABILITY_UNAVAILABLE: вне очереди B4. " +
                "Успехом считается не Update(): параметры перечитываются, сверяются число экземпляров, " +
                "объём документа и число тел, а при заданных expected_hole_* — оси цилиндрических " +
                "граней по КАЖДОМУ экземпляру.",
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
                        "Аналитическое ожидание объёма документа, мм³. На эталонной пластине " +
                        "100×80×10 со сквозным отверстием Ø10 четыре отверстия по 90° дают " +
                        "76 858,407347; при ошибочной трактовке шага как 360/(count2−1) четвёртый " +
                        "экземпляр ложится на первый и объём равен 77 643,806470 — расхождение ровно " +
                        "785,398163 мм³, и это и есть различающее измерение.", 0d, 1e18d))),
                    ("expected_body_count", Sch.Nullable(Sch.Int("Ожидаемое число тел после операции.", 0, 100_000))),
                    ("expected_hole_radius_mm", Sch.Nullable(Sch.Num("Радиус цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_height_mm", Sch.Nullable(Sch.Num("Высота цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_count", Sch.Nullable(Sch.Int("Ожидаемое число экземпляров-отверстий.", 0, 100_000))),
                    ("expected_hole_centers_mm", Sch.Nullable(Sch.Arr(
                        Sch.Arr(Sch.Num("Компонента координаты, мм."), null, 3, 3),
                        "Аналитические координаты осей каждого экземпляра, мм.")))),
                WorkerCommands.PatternCircular,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_pattern_mirror", "Зеркальный массив",
                "Зеркальное отражение операций либо тел относительно ПЛОСКОСТИ (SM-23). Маршрут: " +
                "IModelContainer.FeaturePatterns.Add(o3d_mirrorOperation=48 либо o3d_mirrorAllOperation=49) " +
                "→ QI(IMirrorPattern). Обе строки таблицы и оба числа взяты со страницы справки SDK " +
                "copytype.html, открытой по проводу: «o3d_mirrorOperation 48 IMirrorPattern — Операция " +
                "«зеркальный массив»» и «o3d_mirrorAllOperation 49 IMirrorPattern — Операция «зеркально " +
                "отразить все»». У второго вида дополнительно ставится IChooseBodies7 (ChooseBodiesType, " +
                "Bodies) — интерфейс объявлен на странице copytype.html и найден прибором InteropScan в " +
                "вендорской сборке. " +
                "ПЛОСКОСТЬ — ЯВНЫЙ ОБЪЕКТ, а не текущее выделение окна: это требование зависимости " +
                "dep.refs.planes, и оно же снимает вопрос о знаке нормали — сторону отражения задаёт " +
                "плоскость, а не порядок выделения. Знак нормали YOZ (направлена в −X) остаётся фактом " +
                "модели и живёт в контракте плоскости, а не спрятан в разрешении ссылки. " +
                "СОХРАННОСТЬ ИСХОДНИКА ПРОВЕРЯЕТСЯ ИЗМЕРЕНИЕМ: save_initial_objects=true добавляет " +
                "отражённую копию, false — ЗАМЕНЯЕТ исходник ею. Обе половины обязаны различаться в " +
                "ОДНОЙ постановке, иначе «ноль, который не сдвигали» ничего не различает. " +
                "ПРОПУСКОВ ЭКЗЕМПЛЯРОВ У ЗЕРКАЛЬНОГО МАССИВА НЕ БЫВАЕТ — пользовательская справка " +
                "glava_48_obzhie_svedeniy прямо говорит, что исключение экземпляров недоступно для " +
                "зеркального массива и массива по образцу. Это предметная неприменимость с источником, " +
                "а не незакрытое действие. " +
                "Успехом считается не Update(): параметры перечитываются, объём и число тел сверяются с " +
                "аналитикой, а на многотельной детали возвращаются поимённые снимки тел и список тел, " +
                "НЕ тронутых ни исходником, ни отражением: «суммарный объём вырос вдвое» не отличает " +
                "удвоение тел от удвоения одного тела.",
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
                        "Аналитическое ожидание объёма документа, мм³. При mode=all_bodies измеряется " +
                        "по КАЖДОМУ телу, а не суммой: у save_initial_objects=true исходники " +
                        "остаются и добавляются отражённые (4 тела по 4 000 на заготовке из двух тел " +
                        "20×20×10), у false исходники заменяются отражёнными (2 тела по 4 000). " +
                        "Суммарный объём при зеркале один и тот же, поэтому одноё суммы недостаточно. " +
                        "При mode=selected_operations половины НЕ различаются — справка ограничивает " +
                        "свойство только o3d_mirrorAllOperation.",
                        0d, 1e18d))),
                    ("expected_body_count", Sch.Nullable(Sch.Int("Ожидаемое число тел после операции.", 0, 100_000))),
                    ("expected_hole_radius_mm", Sch.Nullable(Sch.Num("Радиус цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_height_mm", Sch.Nullable(Sch.Num("Высота цилиндрической грани-экземпляра, мм.", 0d, 1e6d))),
                    ("expected_hole_count", Sch.Nullable(Sch.Int("Ожидаемое число экземпляров-отверстий.", 0, 100_000))),
                    ("expected_hole_centers_mm", Sch.Nullable(Sch.Arr(
                        Sch.Arr(Sch.Num("Компонента координаты, мм."), null, 3, 3),
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
                        "Аналитическое ожидание объёма результата. Расхождение не отменяет операцию, но " +
                        "попадает в ответ как неподтверждённый аспект, а не замалчивается.", 0d, 1e18d))),
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
                        "Аналитическое ожидание суммарного объёма частей, мм³.", 0d, 1e18d))),
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
                        "Аналитическое ожидание объёма остатка, мм³.", 0d, 1e18d))),
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

            ReadOnly("kompas_get_feature", "Параметры признака",                "Что сервер видит в определении существующего признака: семейство, имя, стороны выдавливания с условием конца и глубиной, тонкая стенка, параметры фаски (катеты, transfer, а при доступном мосте API7 — угол и способ), параметры скругления и родного отверстия, параметры ВРАЩЕНИЯ (угол, направление, состояние оси, вид операции — читаются из API7 IRotated, потому что определения API5 у вращения нет вовсе), состояние (IsValid, excluded, rollback, objectError), updateStamp и владелец. Уровень чтения — structure_checked: это перечитывание модели, а не доказательство геометрии. Пустое поле угла означает «не прочитано» (мост API7 недоступен или фасок в документе несколько), а не «ноль». Признак вращения сопоставляется с IModelContainer.Rotateds по СОСТАВУ (угол и направление); если совпадений 0 или больше одного, поле rotated = null, а в unverified_aspects добавляется rotation_not_matched — «не сопоставлено», а не «ноль». Читаются и четыре семейства B3 — булева операция, разделение, отсечение и изменение положения; их параметры лежат в блоке solid, потому что определения API5 у этих семейств нет вовсе и они опознаются по НОМЕРУ ПРИЗНАКА В ДЕРЕВЕ (69 / 633 / 50 / 79). В solid.operation и solid.keep_tools — вид булевой операции (union/difference/intersect) и сохранение инструмента отдельным телом; в solid.plane — опора разделения либо отсечения (point_mm, unit-нормаль normal_mm и три точки построения point1_mm…point3_mm, из которых нормаль и выведена); в solid.keep_side — сторона отсечения (true — сторона нормали). Маршруты измерены: IBoolean.BooleanType и SaveCopyModifyObjects прочитаны обратно в трёх разных видах операции и переживают save→close→reopen (шаги BO.2–BO.5, BO.10), опора читается поимённо и разные опоры читаются по-разному (шаг SP.10: x=10 → точки (10,0,0), (10,1,0), (10,0,1), нормаль (1,0,0); x=15 → те же точки со сдвигом), Direction читается как true и false на одной и той же опоре. Для признака ИЗМЕНЕНИЯ ПОЛОЖЕНИЯ читаются ВСЕ поля преобразования, кроме ТОЧКИ ОСИ. solid.reposition_kind — translate либо rotate; solid.reposition_vector_mm — вектор переноса (у поворота null и назван неприменимым: контракт поворота вектора не принимает); solid.reposition_axis_direction_mm и solid.reposition_angle_deg — единичное направление оси и угол в градусах (у переноса null). Источник — документированные ПАРАМЕТРЫ размещения, а не матрица: OrientationType = ksEulerCorners + LocalCSParameters → ILocalCSEulerParam (тройка углов; порядок спряжения измерен и равен PNR, единицы — ГРАДУСЫ: угол 30 дал поворот на 30°) и ParameterType = ksPDisplace + Parameters → IPoint3DParamDisplace (перенос). Маршрут измерен целиком пробой --reposition-params, прогон a336120926fc4652a8bf737562568271, шаг RP.25: и тройка углов, и смещение прочитаны с ПЕРЕОТКРЫТОГО документа ДО сборки и ДО всякой записи на двух постановках различающей пары (D1 = (7,−11,13), D2 = (1,2,3)), а отрицательный контроль D0 (смещение не записывалось) дал ParameterType = 1 (ksPParamCoord) и (?,?,?) — то есть чтение РАЗЛИЧАЕТ постановки, а не отдаёт постоянное. Матричный вид размещения (GetVector, WriteToFile) продуктом НЕ используется вовсе: он отдаёт записанное ТОЛЬКО В СЕССИИ ЗАПИСИ — живой поворот читается (0,1,0) при габарите (−5,−5,0)…(5,15,5), а тот же признак после сохранения, закрытия и открытия — (1,0,0) при ТОЙ ЖЕ геометрии (RP.16), WriteToFile даёт единичную матрицу (RP.18), и отличить «записано» от «не восстановлено» НЕЧЕМ (Valid читается True в обоих состояниях; Update() и сборка чтение не восстанавливают, RP.23). Именно из него прежняя редакция отдавала reposition_kind = translate у ЗАПИСАННОГО ПОВОРОТА, и это ложный ответ, устранённый. ПРИЗНАК, ЗАПИСАННЫЙ МАТРИЦЕЙ, НЕ ЧИТАЕТСЯ И НЕ ПРЕОБРАЗУЕТСЯ МОЛЧА: он опознаётся по ПРОЧИТАННОМУ OrientationType = 0 (ksAxisOrientation), и тогда все пять полей названы непрочитанными с этой причиной — отказ, а не нули; успех на новых признаках поддержку старых не доказывает. НЕ ПУБЛИКУЕТСЯ ОДНО поле — solid.reposition_axis_point_mm: документированного члена для точки оси нет ни у одного интерфейса цепочки (IBodyReposition, ILocalCoordinateSystem/IPoint3D, IPoint3DParamDisplace, ILocalCSAxesDirectionParam, ILocalCSEulerParam, ILocalCSObject — перечни прочитаны из библиотеки типов продукта), а RepositionCentre и ILocalCSObject.CoordinateSystem подтверждают только IModelObject; сверх того точка оси не является свойством размещения — измерено, что запись X/Y/Z = c меняет габарит повёрнутого тела, а запись X/Y/Z = c − R·c его сохраняет, и поворот вокруг любой точки ОДНОЙ И ТОЙ ЖЕ прямой даёт ТО ЖЕ размещение, поэтому из прочитанных ориентации и переноса восстанавливается ПРЕДСТАВИТЕЛЬ прямой, а не исходный вход. Поле всегда null и всегда НАЗВАНО в solid.unreadable_parameters: у поворота — «не читается», у переноса — «неприменимо», и это остаётся ТРЕБОВАНИЕМ строки, а не снятым требованием. Все непрочитанные поля перечислены поимённо с измеренной причиной, а в unverified_aspects добавляется одна строка solid_params_partly_unreadable; ПУСТОЕ ПОЛЕ ОЗНАЧАЕТ «НЕ ПРОЧИТАНО», А НЕ НОЛЬ, и ноль вместо неудавшегося чтения не подставляется никогда. Несопоставленный с коллекцией API7 признак — это названная причина solid_feature_not_matched, а не отказ всего чтения: состояние признака (имя, IsValid, updateStamp) читается и без моста API7. Читаются и три семейства очереди B5 — кинематическая операция (sweep), элемент по сечениям (loft) и оболочка (shell); их параметры лежат в блоках sweep, loft и shell. ОПОЗНАНИЕ ИДЁТ ПО ИНТЕРФЕЙСУ ОПРЕДЕЛЕНИЯ, А НЕ ПО НОМЕРУ ТИПА В ДЕРЕВЕ, и это измерено: признак, созданный NewEntity(45) (o3d_baseEvolution), виден в дереве под номером 46 (o3d_bossEvolution), а определение отвечает ksBossEvolutionDefinition, а не ksBaseEvolutionDefinition (шаг B5.12) — тот же класс расхождения, что уже измерен у отверстия (создаётся 52, в дереве 583) и у вращения (27 против 584). Поэтому принимаются оба интерфейса каждого семейства. sweep: shift_mode (parallel/keep_angle/orthogonal), section_count (1, если профиль привязан), path_part_count, path_length_mm; operation_result читается ТОЛЬКО из API7 (IEvolution), потому что в API5 такого члена нет, и при недоступном мосте поле null с названной причиной evolution_operation_result_not_read. loft: building (auto/by_normal/by_object/cupola), closed, section_count, couplings_count — читаются из КОЛЛЕКЦИИ документа (IModelContainer.Lofts), а не из ответа создания; признак сопоставляется с элементом коллекции ПО ПОРЯДКУ среди односемейных, и при несопоставлении все поля null с причиной loft_not_matched, а не нули. СВЕРХ ЭТОГО публикуются СЕЧЕНИЯ КАК ССЫЛКИ — loft.section_refs: они выводятся из САМОГО ОПРЕДЕЛЕНИЯ признака (ksBaseLoftDefinition.Sketches() / ksBossLoftDefinition.Sketches(), справка ksbaseloftdefinition_sketches.html и ksbossloftdefinition_sketches.html, возвращают ksEntityCollection), а не сохраняются с момента создания. Это не украшение: единственная валюта правки элемента по сечениям — section_refs, ссылка же от создания умирает на первой мутации документа, а kompas_rebuild отзывает все ссылки документа целиком; перечислять эскизы отдельным инструментом продукт не умеет (kompas_list_features отдаёт только EntityCollection(o3d_operationElement), и на документе с двумя эскизами и одним элементом по сечениям в дереве видна ОДНА строка). Без loft.section_refs правка существующего элемента по сечениям невыразима ни в новой сессии, ни после save → close → reopen. Пустой список означает «в определении сечений нет», null — «не прочитано». shell: thickness_mm, thin_direction (inward/outward) и removed_face_count читаются ДВУМЯ маршрутами — с определения API5 (thickness, thinType, FaceArray) и из API7 (IShell.Thickness, ThinType, DeletedFaces); расхождение половин называется shell_halves_disagree, а не сглаживается. Как и у сечений, СНЯТЫЕ ГРАНИ ПУБЛИКУЮТСЯ ССЫЛКАМИ — shell.removed_face_refs, выведенные из ksShellDefinition.FaceArray(): снятых оболочкой граней в топологии тела НЕТ вовсе, поэтому из kompas_read_topology их взять нечем, а набор удаляемых граней — вход правки. Значение вне объявленного набора отдаётся ЧИСЛОМ, а не подменяется именем и не превращается в null: «модель говорит 7» — это факт. Направление тонкой стенки подтверждено дважды — справкой (ksshelldefinition_thintype.html: TRUE внутрь, FALSE наружу) и объёмом на обоих API (21632 внутрь, 24832 наружу при t = 2). ССЫЛКА НА ОПОРНЫЙ ЭСКИЗ ПУБЛИКУЕТСЯ в необязательном поле sketch_ref — тем же помощником и в той же форме, что у kompas_list_features, поэтому один эскиз получает ОДИН живой адрес от обоих инструментов и его можно править, не перечитывая дерево. Поле заполнено только там, где GetSketch() действительно отдаёт объект (семейства выдавливания); там, где ссылка не получена, поле null, а в verification.unverified_aspects добавлена строка sketch_reference_not_resolved с причиной — «не прочитано» остаётся названным, а не молчаливым.",
                Sch.Props(("feature_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.GetFeature),

            ReadOnly("kompas_get_sketch_status", "Определённость эскиза",
                "Отвечает на вопрос «этот эскиз сейчас полностью определён?» — читается тот же " +
                "агрегированный статус системы ограничений, который КОМПАС показывает знаками " +
                "«+», «−», «!». Маршрут: перенос эскиза в API7 и чтение ISketch.ConstraintsState " +
                "(измерено на КОМПАС-3D 24.0.0.2799, проба S, docs/acceptance/api7/sketch-definition.md). " +
                "ЗНАЧЕНИЯ: 1 = fully_defined (is_fully_defined=true); 2 = under_defined " +
                "(is_fully_defined=false); 0 = unknown (is_fully_defined=null) — так КОМПАС отвечает, " +
                "когда сам не установил состояние, в частности на пустом эскизе; любое значение вне " +
                "объявленного перечисления тоже unknown, а не «недоопределён». Значение 3 " +
                "(ksStateUnresolvedRedundancy, «требует внимания») в подтверждённом прогоне на живой " +
                "модели НЕ получено — проба S прочитала 46 эскизов поставки и получила только 0/1/2, — " +
                "поэтому оно публикуется консервативно как unknown с причиной " +
                "unresolved_redundancy_not_verified; объявленное в перечислении состояние не выдаётся " +
                "за измеренное. ЧИСЛА СТЕПЕНЕЙ СВОБОДЫ НЕТ: маршрут отдаёт состояние, а не счётчик, " +
                "поэтому degrees_of_freedom всегда null и никогда не вычисляется из числа размеров. " +
                "ЧТО НЕ ДОКАЗЫВАЕТСЯ: координаты, ширина/высота/радиус при создании, замкнутость " +
                "контура, успешное выдавливание и корректный объём «+» не подтверждают, а «+» в свою " +
                "очередь не доказывает, что эскиз удобно параметризован. " +
                "ЧТЕНИЕ НЕ МЕНЯЕТ МОДЕЛЬ: измерено (пятикратное чтение не изменило объём и счётчики " +
                "топологии), поэтому вызов не входит в режим правки, не перестраивает документ и не " +
                "меняет ревизию. " +
                "Это ТОЛЬКО диагностика: сервер не накладывает ограничений, не фиксирует геометрию и " +
                "не перемещает объекты ради получения «+», и не требует полной определённости от " +
                "выдавливания, вращения или завершения эскиза. Создание ограничений и управляющих " +
                "размеров — отдельная задача и здесь не реализовано.",
                Sch.Props(("sketch_ref", Sch.Ref("#/$defs/reference"))),
                WorkerCommands.SketchStatus,
                requiresOperationId: false),

            Mutation("kompas_update_feature", "Изменить параметр признака",
                "Правка настоящего признака на месте, а не удаление с пересозданием (docs/05 §4.3). Поддержано семейство выдаваний: depth_mm и/или end_condition и/или sketch_ref; уклон и тонкая стенка сохраняются как прочитаны. Поддержано семейство фасок: distance1_mm, distance2_mm и direction — маршрут измерен (F.3: катеты 2×2→3×3 дают 79820 мм³; F.5: то же после save→close→reopen). Поддержано семейство ВРАЩЕНИЯ: rotation_angle_deg и rotation_direction — маршрут измерен 18.09.2026 (проба FullTurnProbe, шаг F.2: на одном признаке смена угла 360 → 180 → 360 дала 50265.4824574366 → 25132.7412287183 → 50265.4824574366 при габарите z[−20,20] → z[−0,20] → z[−20,20], то есть геометрия изменилась, а не только число). Угол вращения задаётся ПОЛЕМ rotation_angle_deg, а не angle_deg: у фаски angle_deg означает угол фаски, и одно имя для двух семейств сделало бы ответ неоднозначным — вызов с angle_deg на признаке вращения отвергается INVALID_ARGUMENT. Перепривязка профиля и оси существующего вращения НЕ выполняется (маршрут не измерялся): sketch_ref на вращении отвергается. Признак вращения сопоставляется с IModelContainer.Rotateds по СОСТАВУ (угол и направление), а не по имени и не по индексу; при неоднозначности вызов отвергается CAPABILITY_UNAVAILABLE до правки. Правка УГЛА идёт отдельным маршрутом API7 (в API5 члена «угол» нет): признак берётся из IModelContainer.Chamfers по совпадению первого катета, в него пишутся Angle и Distance1, затем Update() и RebuildModel(); измерено (CH24) — d=3, α=45° сняли ровно 180 мм³, способ ksChamferSideAngle сохранился, производный катет пересчитан ядром как d₁·tg α, а результат пережил save→close→reopen (CH25, CH25r). Производный катет НЕ записывается, если клиент не задал его явно: запись «прежнего» числа закрепила бы устаревшую производную. Если фаска построена «расстоянием и углом», а angle_deg не задан, вызов отвергается CAPABILITY_UNAVAILABLE до мутации: маршрут API5 молча сменил бы способ построения и потерял угол (измерено: 30° → 45°, V 79953.81197846486 → 79820, расхождение 76.08 мм³). Поддержано семейство скругления: radius_mm — тоже маршрутом API7 (IFillet.Radius1), по той же причине: у ksFilletDefinition радиус есть, но запись в него на существующем признаке НЕ применяется (сеттер и entity.Update() возвращают успех, а объём остаётся прежним). Признак сопоставляется с IModelContainer.Fillets по совпадению радиуса; при неоднозначности — CAPABILITY_UNAVAILABLE, а не «взяли первое». Правка НАБОРА рёбер скругления — поле edge_refs (полная замена набора, выражает замену) и поле base_object_refs (собственные входы признака, выражает сокращение) — РЕАЛИЗОВАНА и ПРИНЯТА ПРОДУКТОМ (17.09.2026): scripts/mcp-smoke.py --fillet-only даёт 46 строк, 0 FAIL, включая сокращение FL10 4→3 (V=79942.0575041173 при аналитике 79942.05750411731), FL10s и FL10b, замена 1→1 FL23 с переездом угла (адрес 1073742400 → 1073742403) и повтор на независимом документе FL24 — все level=geometry_checked. Корневая причина прежнего отказа была не в маршруте (он работал — FL10x проходил) и не в валюте: свойства base_object_refs НЕ БЫЛО В СХЕМЕ этого инструмента, и Host отвергал вызов валидацией ещё до COM, поэтому сокращение просто не доходило до адаптера. Маршрут найден пробой H-2 (docs/acceptance/api7/fillet-base-objects.md, 14 PASS / 0 FAIL / 0 UNKNOWN, воспроизведено четырьмя прогонами): IModelContainer.Fillets[i] → IFillet, чтение и запись IFillet.BaseObjects (полная замена набора), затем обязательный IFillet.Update() и перестроение. Все объекты берутся с ЖИВОЙ модели после save→close→reopen; ни один объект, захваченный при создании скругления, не используется — в этом и была ошибка ранней пробы H. Признак API5 сопоставляется с нужным IFillet НЕ по имени (F.8: имя в API5 и API7 не совпадает), НЕ по индексу (порядок выдачи произволен) и НЕ по радиусу (два скругления одного радиуса неразличимы): опознание идёт по составу входов признака, сравниваемых по устойчивому IModelObject.Reference, а при нуле или нескольких совпадениях вызов отвергается CAPABILITY_UNAVAILABLE ДО мутации — записать набор в чужой признак означало бы молча испортить чужую геометрию. Решающий контроль — замена при НЕИЗМЕННОМ размере набора (1→1): на пластине с одним скруглённым углом вход заменён на ребро свободного угла, состав сместился (50,40) → (50,-40), а объём остался буквально прежним 79980.6858347058 → 79980.6858347058; сценарию «пересчитай то, чем ты обязан быть» двигать нечего, поэтому объём здесь не различает ничего — различает именно состав. Адресация проверена на модели с ДВУМЯ скруглениями одного радиуса: правка Fillets[1] не задела свидетель Fillets[0], и это перепроверено через MCP (FL20/FL21/FL22): входы двух признаков различны, а входы второго с feature_ref первого отвергаются CAPABILITY_UNAVAILABLE ДО мутации с указанием feature_ref_inputs/matched_owner_inputs, тогда как тот же образец с ПРАВИЛЬНЫМ feature_ref проходит — запрет адресный, а не сплошной. ГРАНИЦА, измеренная 17.09.2026 (FL25, Г-образная пластина со СВОБОДНЫМИ углами): РАСШИРЕНИЕ набора (1→2, 2→3) НЕ ВЫРАЖЕНО НИ ОДНОЙ из двух валют. Полная замена по рёбрам тела означает «построй скругление заново по этим рёбрам» — прежнее ребро при этом не сохраняется, тогда как расширению нужно ровно обратное (сохранить свои входы И добавить чужой), а смешивать валюты нельзя. Исход — отказ ПОСЛЕ мутации: GEOMETRY_FAILED с partial_effects=true и details. Отдельно исправлен РЕПОРТ: до 17.09.2026 этот исход возвращался как err=None и level=call_returned, то есть ИСЧЕЗНОВЕНИЕ ПРИЗНАКА ВЫДАВАЛОСЬ ЗА УСПЕШНУЮ ПРАВКУ; теперь исчезновение признака после записи и перестроения — отказ. Правьте СОСТАВ (сокращение и замена), не добавляйте рёбра. ПРЕЖНИЙ ОТРИЦАТЕЛЬНЫЙ РЕЗУЛЬТАТ ОСТАЁТСЯ ВЕРНЫМ и не отменён: маршрут через определение API5 (ksFilletDefinition.array(): Clear, затем Add по каждому ребру) правку набора НЕ даёт — после скругления угловые рёбра в топологии отсутствуют (0 из 4: углы заняты цилиндрическими гранями), исходные угловые рёбра отзывает само создание скругления (STALE_REFERENCE до всякой правки), а существующие вертикальные рёбра скруглённых углов признак не удерживает: набор схлопывается (edges_read_back=0) и объём возвращается к пластине. Проверено на 1, 2, 4 и 8 рёбрах, повторено 6/6 прогонов на свежих документах; ненулевой edges_read_back давал только второй вызов подряд, и это пересборка скругления с нуля, а не правка набора. Ранняя проба H (docs/acceptance/api7/fillet-edge-set.md) сообщала о применении (сокращение дало 79961.3716694115, расширение — 79922.7433388231), но мерила пересчёт признака по ПОДСТАВЛЕННОМУ входу: числа верны как геометрия и неверны как доказательство правки набора. Вывод с радиусом НЕ переносится: один признак — разные параметры, разные механизмы. radius_mm и edge_refs вместе запрещены, иначе ответ не отличит «применилось и то, и другое» от «применилось одно из двух»; edge_refs и base_object_refs вместе тоже запрещены. Подтверждение — измерение объёма против expected_volume_mm3: для четырёх угловых рёбер пластины 100×80×10 при радиусе r снято 4·(1−π/4)·r²·10, то есть R3 даёт 79922.74333882307 мм³. Поля семейства применяются к своему семейству: depth_mm у фаски, distance1_mm у выдавливания, radius_mm у выдавливания и фаски отвергаются, а не игнорируются. sketch_ref — смена опорного эскиза того же признака: единственный измеримый параметр для сквозного вырезания, где число глубины solver игнорирует (P2.1), и этот маршрут не применён (Q-EDIT-SKETCH). Значение перечитывается с нового объекта определения, признак сверяется по числу признаков и имени, а геометрия — измерением объёма против expected_volume_mm3. Поддержаны семейства B3. ИЗМЕНЕНИЕ ПОЛОЖЕНИЯ (reposition_kind и поля reposition_*): правка признака на месте, а не второй kompas_reposition — повторный вызов создал бы ВТОРОЙ признак и накопил смещение, тогда как параметр обязан применяться к ИСХОДНЫМ входам признака (наряд §5). Маршрут измерен 18.09.2026 (проба --reposition, шаг RP.6). РАЗДЕЛЕНИЕ (plane): правка опоры ТОГО ЖЕ признака, маршрут измерен 18.09.2026 пробой --split, шаг SP.9 (перенос трёх точек построения СОБСТВЕННОЙ опоры) — правка x=10 → x=15 переводит части 6000/18000 в 9000/15000 при неизменном числе признаков 1 → 1; подстановка другой, заново построенной плоскости результата НЕ даёт (контроль E-B), поэтому plane_ref здесь отвергается. Сумма объёмов частей при правке не меняется, поэтому expected_volume_mm3 на разделении ОТВЕРГАЕТСЯ, а подтверждение даёт expected_part_volumes_mm3 — состав частей. ОТСЕЧЕНИЕ (plane + keep_side): та же опора того же признака, маршрут измерен шагом SP.9 (E-C: перенос точек опоры x=10 → x=15 переводит остаток 6000 → 9000) и шагом SP.7 (E-D: Direction=true оставляет 18000 при s>0, false оставляет 6000 при s<0). БУЛЕВА ОПЕРАЦИЯ (operation): правка вида существующего признака, маршрут измерен 18.09.2026 пробой --boolean, шаг BO.11 — объединение 36000 → разность 12000 (габарит x ≤ 20) → пересечение 12000 (габарит x ∈ [20,40]) → объединение 36000, признаков 1 → 1; объём разности и объём пересечения РАВНЫ, поэтому габарит здесь не роскошь, а единственное различающее свидетельство. У всех четырёх семейств определение перечисляется ЦЕЛИКОМ: правка не додумывает недостающее из состояния модели, а отвергает неполный запрос. Причина — не в невозможности чтения: параметры булевой операции, разделения и отсечения ЧИТАЮТСЯ и публикуются kompas_get_feature в блоке solid (маршруты измерены пробами BO.2–BO.11 и SP.10), — а в том, что ответ на частичный запрос не отличил бы «изменилось ровно то, что просили» от «изменилось заодно и то, о чём промолчали». Рабочий порядок — прочитать признак, подставить прочитанное и изменить нужное поле; исключение здесь ОДНО, и оно касается признака ИЗМЕНЕНИЯ ПОЛОЖЕНИЯ: ТОЧКА ОСИ ПОВОРОТА не читается и не публикуется (reposition_axis_point_mm — документированного члена для неё нет ни у одного интерфейса цепочки, и она не является свойством размещения: поворот вокруг любой точки одной и той же прямой даёт то же размещение; см. kompas_get_feature), поэтому запрос обязан нести её сам. Остальные поля преобразования читаются и подставляются: reposition_kind, reposition_vector_mm, reposition_axis_direction_mm и reposition_angle_deg читаются документированными ПАРАМЕТРАМИ размещения (OrientationType = ksEulerCorners + LocalCSParameters и ParameterType = ksPDisplace + Parameters), в том числе с переоткрытого документа, — маршрут измерен пробой --reposition-params, прогон a336120926fc4652a8bf737562568271, шаг RP.25. Но подстановка НЕ отменяет требования полноты запроса. Адрес признака — позиция среди признаков ТОГО ЖЕ ТИПА в дереве; если признаков этого типа в дереве больше, чем элементов в коллекции API7 (так бывает у изменения положения: номер 79 носят и «Изменение положения», и вспомогательная «Копия тела»), вызов отвергается CAPABILITY_UNAVAILABLE до мутации, потому что позиция перестала быть индексом и запись ушла бы в чужой признак. Отказ с частичной мутацией несёт в details число revision_after — фактическое состояние модели, без которого следующий вызов клиента упал бы REVISION_CONFLICT. " +
                "ТРИ СЕМЕЙСТВА ПОСЛЕДНЕЙ ОБЯЗАТЕЛЬНОЙ ОЧЕРЕДИ B5 поддержаны и измерены 20.09.2026 (проба --b5, отчёт docs/acceptance/api7/b5-sweep-loft-shell.json): шаг B5.13 — режим, толщина, направление и сечения; B5.14 — набор удаляемых граней; B5.15 — входы кинематики. " +
                "КИНЕМАТИЧЕСКАЯ ОПЕРАЦИЯ (sweep): правится shift_mode; на одном признаке orthogonal → parallel → orthogonal дало 24674.011002723353 → 15707.963267948984 → 24674.011002723353. " +
                "ЭЛЕМЕНТ ПО СЕЧЕНИЯМ (loft): правятся section_refs и couplings; перепривязка сечений дала 28000 → 48000 → 28000, а замена цепочек соответствия — полная (ClearCouplings + AddCoupling), и если признак несёт цепочки, то вызов, меняющий section_refs без couplings, отвергается INVALID_ARGUMENT с кодом couplings_must_be_restated: цепочка описывает соответствие точек конкретного набора сечений, и «оставить как было» после смены набора значило бы оставить чужое соответствие. " +
                "ОБОЛОЧКА (shell): правятся thickness_mm, thin_inward и face_refs; толщина и направление на одном признаке дали 21632 → 40256 → 53056 → 21632, а набор удаляемых граней — 21632 → 7040 → 21632 при 11 → 10 → 11 гранях, с отрицательным контролем на повторную запись того же набора. " +
                "ЧТО ЭТИМ ВЫЗОВОМ НЕ МЕНЯЕТСЯ — измеренные отказы, а не осторожность. Профиль и траектория существующей кинематической операции: SetSketch принимается и не применяется (определение по-прежнему отвечает прежним эскизом), перепривязка PathPartArray() принимается и доходит до держателя, но геометрию не меняет, и после неё перестаёт применяться даже правка режима; поэтому sketch_ref на кинематической операции отвергается CAPABILITY_UNAVAILABLE. Замкнутость элемента по сечениям: запись ILoft.Closed возвращает Update() = True, читается обратно False и объём не меняет — замкнутость задаётся только при создании. Касательные грани оболочки: у API5 ksShellDefinition такого члена нет вовсе, режим не выражается и не выдаётся за выполненный. " +
                "Поля ЧУЖИХ семейств не игнорируются: вызов с полями двух семейств отвергается INVALID_ARGUMENT, и в details перечислены foreign_fields — принятое и проигнорированное число доживает до приёмки, выглядя как выполненная правка. " +
                "Уровень geometry_checked требует объявленного expected_volume_mm3: без него правка не подтверждена числом, и ответ честно остаётся call_returned. " +
                "ОТВЕРСТИЕ поддержано и измерено 20.09.2026 (наряд SM07 §3.2; зонд scratch/_hole_edit_probe.py, отчёт docs/acceptance/api7/hole-modes.md § M.6): правятся diameter_mm и поля СВОЕГО режима — depth_mm у blind_flat, counterbore_diameter_mm и counterbore_depth_mm у through_counterbore, countersink_diameter_mm и countersink_angle_deg у through_countersink. Маршрут — документированный: существующее отверстие берётся членом IHoles3D.Hole3D[index] (тем же, которым его читает kompas_get_feature), в него пишутся члены своего режима, применяется IModelObject.Update(), затем перестроение. Числа: глухое Ø10 6 → 8 мм сняло 157.079632679 мм³ = π·r²·2; выточка Ø18×4 → Ø20×5 — 474.380490692; устье зенковки Ø20 → Ø24 при 90° — 605.280184592; сквозное Ø10 → Ø12 на плите 10 мм — 345.575191895. Все четыре пережили save → close → reopen. Контроли того же зонда: без Update() объём НЕ меняется ни в одном режиме, а интерфейс параметров ЧУЖОГО режима на объекте недостижим. АДРЕС НЕ УГАДЫВАЕТСЯ: признак сопоставляется с записью IHoles3D по ЕДИНСТВЕННОСТИ отверстия в документе, и при нескольких отверстиях вызов отвергается CAPABILITY_UNAVAILABLE до COM (имя признака идентификатором не является — оно не переживает переход API5↔API7). РЕЖИМ ПРАВКОЙ НЕ МЕНЯЕТСЯ: режим существующего признака читается из модели (IHole3D.HoleType) и служит рамкой, поле чужого режима отвергается INVALID_ARGUMENT с перечнем своих полей. Глубина зенковки НЕ утверждается записанной: при способе «диаметр + угол» она производна (измерено M.3: запись 2/4/6 не меняет ничего), поэтому в checks публикуется ПРОЧИТАННОЕ число с проверкой countersink_depth_derived. Ожидание задаётся полем expected_volume_delta_mm3 — СНЯТЫМ материалом (объём до − объём после), и знак здесь часть определения величины: у «стало мельче» дельта отрицательна.",
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
                        "Новый угол фаски, градусы (строго между 0 и 90). Правка угла идёт маршрутом " +
                        "API7 (IChamfer на живой модели), потому что в API5 члена «угол» нет вовсе. " +
                        "Измерено 16.09.2026: у фаски d=2, α=30° запись d=3 и α=45° сняла ровно " +
                        "20·3·(3·tg 45°) = 180 мм³ (V 79953.81197846486 → 79820), способ построения " +
                        "остался ksChamferSideAngle, а второй катет ядро пересчитало как d₁·tg α. " +
                        "Если клиент НЕ задал angle_deg на фаске этого способа, вызов отвергается " +
                        "CAPABILITY_UNAVAILABLE: маршрут API5 умеет только два катета и молча потерял " +
                        "бы угол (измерено: 30° → 45°, расхождение 76.08 мм³).",
                        0d, 90d))),
                    ("direction", Sch.Nullable(Sch.Bool(
                        "Новая сторона фаски (API5 transfer). Только для семейства chamfer."))),
                    ("radius_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый радиус скругления",
                        "Только для семейства скругления. Правится " +
                        "маршрутом API7 (IFillet.Radius1 на живой модели), потому что запись в " +
                        "радиус через API5 на существующем признаке не применяется: сеттер " +
                        "ksFilletDefinition.radius и entity.Update() оставляют объём прежним. " +
                        "Признак API5 сопоставляется с IModelContainer.Fillets по совпадению " +
                        "радиуса, а не по имени. При неоднозначном совпадении (несколько " +
                        "скруглений с тем же радиусом) вызов отвергается CAPABILITY_UNAVAILABLE: " +
                        "записать радиус в чужой признак нельзя."))),
                    ("edge_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Новый НАБОР рёбер скругления (полная замена набора). Маршрут — API7: " +
                        "IFillet.BaseObjects на живой модели + IFillet.Update() + перестроение. " +
                        "Маршрут измерен исследовательской пробой H-2 (docs/acceptance/api7/" +
                        "fillet-base-objects.md, 14 PASS), и проба H-2 обязательна к прочтению " +
                        "перед правкой этого поля. ПРИЁМКА ПРОДУКТА ПРОЙДЕНА (17.09.2026): " +
                        "scripts/mcp-smoke.py --fillet-only даёт 46 строк, 0 FAIL — сокращение " +
                        "набора (FL10 4→3, FL10s 3→2, FL10b 2→1) и замена при неизменном размере " +
                        "(1→1, FL10x) проходят с level=geometry_checked. Замена выражается ЭТИМ " +
                        "полем: рёбра тела. Сокращение выражается полем base_object_refs; обе " +
                        "валюты в одном вызове не сочетаются. Адресация признака: НЕ по имени, " +
                        "НЕ по индексу " +
                        "и НЕ по радиусу, а по СОСТАВУ — набор входов признака API5 сравнивается с " +
                        "набором входов каждого IModelContainer.Fillets[i] через IModelObject." +
                        "Reference. Совпадений 0 или больше одного — вызов отвергается " +
                        "CAPABILITY_UNAVAILABLE до правки, кандидаты уходят в details (иначе " +
                        "нельзя править признак с тем же радиусом, что у соседнего). Найденный " +
                        "владелец обязан СОВПАСТЬ с feature_ref запроса, иначе тоже " +
                        "CAPABILITY_UNAVAILABLE до мутации (FL21). Состав " +
                        "проверяется на живой модели через save → close → reopen: объект, " +
                        "захваченный при создании скругления, не годится — именно на этом ошиблась " +
                        "проба H (в отчёте пробы это разобрано). Прежний маршрут API5 " +
                        "(ksFilletDefinition.array(): Clear(), затем Add()) закрыт: угловые рёбра " +
                        "отзывает само создание скругления, и набор схлопывался (edges_read_back=0, " +
                        "объём возвращался к пластине). Тот отрицательный результат остаётся в силе " +
                        "как факт об API5 и сохранён в kompas_update_feature. Решающий контроль " +
                        "H2.4: замена в наборе ТОГО ЖЕ размера (1→1) при неизменном объёме " +
                        "(79980.6858347058 → 79980.6858347058) — состав ушёл (50,40) → (50,-40). " +
                        "Адресация двух скруглений одного радиуса измерена в H2.7 и перепроверена " +
                        "через MCP (FL20/FL21/FL22). ГРАНИЦА, измеренная 17.09.2026 (FL25): " +
                        "РАСШИРЕНИЕ набора (1→2, 2→3) этим полем НЕ выражается. Полная замена по " +
                        "рёбрам тела означает «построй скругление заново по этим рёбрам» — прежнее " +
                        "ребро не сохраняется, тогда как расширению нужно ровно обратное. Исход — " +
                        "отказ после мутации: GEOMETRY_FAILED, partial_effects=true (до 17.09.2026 " +
                        "это молча выдавалось за успешную правку; теперь исчезновение признака — " +
                        "отказ). Смысл ответа: правьте СОСТАВ (сокращение и замена), а не " +
                        "добавляйте рёбра. " +
                        "С radius_mm в одном вызове не сочетается.",
                        1, 64))),
                    (                        "base_object_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Новый НАБОР по СОБСТВЕННЫМ входам признака (семейство скругления) — вторая " +
                        "валюта того же предмета правки. Значения — ссылки вида input:<hex>, " +
                        "выданные kompas_get_feature в поле fillet.base_object_input_refs. " +
                        "ЭТО ВАЛЮТА СОКРАЩЕНИЯ: сокращение выражается только объектами, " +
                        "прочитанными ИЗ IFillet.BaseObjects признака. Приёмка ПРОДУКТА пройдена " +
                        "(17.09.2026): FL10 4→3 даёт V=79942.0575041173 при аналитике " +
                        "79942.05750411731, FL10s 3→2 и FL10b 2→1 — тоже level=geometry_checked, " +
                        "0 FAIL из 46 строк. Раньше эта валюта не работала не из-за маршрута, а " +
                        "из-за ПУБЛИКАЦИИ: свойства base_object_refs не было в схеме, и Host " +
                        "отвергал вызов валидацией ещё до COM. ЗАМЕНА состава выражается и рёбрами " +
                        "тела (edge_refs, FL10x/FL23 level=geometry_checked), поэтому обе валюты " +
                        "сохранены раздельно. Числа base_object_references в edge_refs не " +
                        "подставляются по типу: у собственного входа нет строки реестра edge:<hex>, " +
                        "её выдаёт этот сервер и только для рёбер ТЕЛА. Набор — ПОЛНАЯ замена, а не " +
                        "добавление: " +
                        "передаётся то, что должно остаться. РАСШИРЕНИЕ этим полем НЕ выражается: " +
                        "новое ребро не является собственным входом признака, а смешивать валюты " +
                        "нельзя (FL25 — измеренный отказ с partial_effects). Пустой список " +
                        "отвергается (INVALID_ARGUMENT). С edge_refs в одном вызове не сочетается " +
                        "(INVALID_ARGUMENT до мутации): два разных состава в одном запросе " +
                        "неразличимы в ответе. Ссылки привязаны к ревизии документа: после мутации " +
                        "перечитайте kompas_get_feature, старые отсекаются STALE_REFERENCE.",
                        1, 64))),
                    ("rotation_angle_deg", Sch.Nullable(Sch.Num(
                        "Новый угол ВРАЩЕНИЯ (семейство rotation), градусы. Отдельное поле, а не " +
                        "angle_deg: у фаски тот член означает угол фаски, у вращения — угол развёртки. " +
                        "Маршрут измерен 18.09.2026 (проба FullTurnProbe, шаг F.2): на одном признаке " +
                        "смена 360 → 180 → 360 дала 50265.4824574366 → 25132.7412287183 → " +
                        "50265.4824574366 при габарите z[−20,20] → z[−0,20] → z[−20,20], то есть " +
                        "геометрическое изменение, а не только записанное число. Пишется только угол: " +
                        "перепривязка профиля и оси существующего вращения не измерялась и не " +
                        "выполняется (sketch_ref на вращении отвергается). Значение больше 360 " +
                        "отвергается: сектор не может занять больше целого оборота. С angle_deg в " +
                        "одном вызове не сочетается.",
                        0.0000001d, 360d))),
                    ("rotation_direction", Sch.Nullable(Sch.Enum(
                        "Новое направление вращения (семейство rotation). reverse ОТВЕРГАЕТСЯ " +
                        "CAPABILITY_UNAVAILABLE: измерено (R.26.sector), что при нём признак не " +
                        "строится вовсе — Update()=False, тел 0.",
                        "normal", "reverse", "both", "middle_plane"))),
                    ("shift_mode", Sch.Nullable(Sch.Enum(
                        "Новый режим движения сечения КИНЕМАТИЧЕСКОЙ операции (семейство sweep), " +
                        "очередь B5. Значения — документированные ksEvolutionShiftSketchTypeEnum " +
                        "(страница ksevolutionshiftsketchtypeenum.html): parallel — образующая " +
                        "переносится параллельно самой себе, keep_angle — сохраняет исходный угол с " +
                        "направляющей, orthogonal — плоскость образующей выставляется и сохраняется " +
                        "ортогональной направляющей. Маршрут измерен 20.09.2026 (проба --b5, шаг " +
                        "B5.13): на ОДНОМ признаке смена orthogonal → parallel → orthogonal дала " +
                        "24674.011002723353 → 15707.963267948984 → 24674.011002723353, то есть " +
                        "изменилась геометрия, а не только записанное число. Постановка РАЗЛИЧАЮЩАЯ " +
                        "ТОЛЬКО НА ДУГЕ: на прямой траектории параллельный и ортогональный режимы дают " +
                        "одно тело (измерено: на дуге R50/90° они различаются на 8966.047734774369 мм³). " +
                        "Профиль и траектория существующей операции этим вызовом НЕ меняются: измерено " +
                        "(шаг B5.15), что SetSketch принимается и не применяется (определение " +
                        "по-прежнему отвечает прежним эскизом, объём не меняется), а перепривязка " +
                        "PathPartArray() принимается, доходит до держателя и тоже не применяется — " +
                        "больше того, после неё перестаёт применяться и правка режима. Поэтому " +
                        "sketch_ref на кинематической операции отвергается CAPABILITY_UNAVAILABLE.",
                        "parallel", "keep_angle", "orthogonal"))),
                    ("section_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Новый набор сечений ЭЛЕМЕНТА ПО СЕЧЕНИЯМ (семейство loft), очередь B5, в " +
                        "порядке соединения — ПОЛНАЯ замена, а не добавление. Маршрут измерен " +
                        "20.09.2026 (проба --b5, шаг B5.13): перепривязка ILoft.Sketchs на уже " +
                        "построенном признаке меняет геометрию — 40×40 + 20×20 дают 28000, " +
                        "40×40 + 40×40 дают призму h/3·(A₁ + A₂ + √(A₁A₂)) = 10·(1600+1600+1600) = " +
                        "48000, возврат к прежнему набору возвращает 28000. Замкнутость (closed) " +
                        "правимым параметром НЕ объявлена: измерено, что запись ILoft.Closed на " +
                        "построенном признаке возвращает Update()=True, читается обратно False и " +
                        "объём не меняет — «принято» не означает «применено». Замкнутость задаётся " +
                        "только при создании (kompas_loft.closed). Признак сопоставляется с " +
                        "IModelContainer.Lofts по ПОРЯДКУ среди односемейных, и сопоставление " +
                        "отвергается CAPABILITY_UNAVAILABLE, если число признаков в дереве не равно " +
                        "числу элементов коллекции: при расхождении адрес не доказан, а править " +
                        "«наугад» значило бы изменить не тот объект.",
                        2, 64))),
                    ("couplings", Sch.Nullable(Sch.Described(
                        (JsonObject)CouplingsSchema.DeepClone(),
                        "Новый набор цепочек соответствия ЭЛЕМЕНТА ПО СЕЧЕНИЯМ (семейство loft), очередь " +
                        "B5 — ПОЛНАЯ ЗАМЕНА (ClearCouplings(), затем AddCoupling() на каждую цепочку). " +
                        (string)CouplingsSchema["description"]! +
                        " ОТСУТСТВИЕ ПОЛЯ — НЕ «оставить как было»: если признак несёт цепочки, а " +
                        "правка меняет section_refs и цепочки не называет, вызов отвергается " +
                        "INVALID_ARGUMENT с кодом couplings_must_be_restated. Цепочка описывает " +
                        "соответствие точек конкретного набора сечений (ICoupling.Count — «количество " +
                        "сечений в цепочке»), и измерено (шаг B5.19), что присваивание ILoft.Sketchs " +
                        "СБРАСЫВАЕТ цепочки: CouplingsCount читался 1, после повторного присваивания " +
                        "того же набора стал 0, объём вернулся с 20000 к 28000. Поэтому проверка стоит " +
                        "ДО записи сечений — после неё молчаливая потеря соответствия была бы " +
                        "неотличима от «признак цепочек не нёс». Пустой список означает «без цепочек»."))),
                    ("thickness_mm", Sch.Nullable(Sch.Num(
                        "Новая толщина стенки ОБОЛОЧКИ (семейство shell), очередь B5, мм. Маршрут " +
                        "измерен 20.09.2026 (проба --b5, шаг B5.13): на одном признаке t = 2 → 4 → 4 " +
                        "→ 2 дало 21632 → 40256 → 53056 → 21632. Режим оболочки — ПАРА (толщина, " +
                        "направление), поэтому пишутся ОБА параметра: недостающая половина берётся " +
                        "С МОДЕЛИ, а не из умалчиваемого значения, иначе «изменилось ровно " +
                        "запрошенное» стало бы неотличимо от «изменилось ещё и это». Порядок «запись " +
                        "→ Update() → пересборка» — часть контракта: без Update() сеттер возвращает " +
                        "успех, а модель остаётся прежней. Толщина обязана быть положительным " +
                        "конечным числом.",
                        0.0000001d, 1e6d))),
                    ("thin_inward", Sch.Nullable(Sch.Bool(
                        "Новое направление стенки ОБОЛОЧКИ (семейство shell): true — внутрь, false — " +
                        "наружу. Отдельное поле, а не общий direction фаски: у фаски direction — " +
                        "сторона фаски, у оболочки — сторона стенки, и одно имя для двух разных " +
                        "предметов сделало бы ответ неоднозначным. Соответствие измерено ДВАЖДЫ — " +
                        "страницей ksshelldefinition_thintype.html («TRUE — внутрь, FALSE — наружу») " +
                        "и объёмом на коробе 100×80×10 с удалённой верхней гранью при t = 2: " +
                        "thinType = true даёт 21632 (полость 96×76×8), thinType = false — 24832 " +
                        "(тело 104×84×12 минус 100×80×10). Оба числа совпали с аналитикой, поэтому " +
                        "направление установлено и документом, и измерением. Пишется вместе с " +
                        "thickness_mm: режим — пара."))),
                    ("face_refs", Sch.Nullable(Sch.Arr(
                        Sch.Ref("#/$defs/reference"),
                        "Новый НАБОР удаляемых граней ОБОЛОЧКИ (семейство shell) — ПОЛНАЯ замена, а " +
                        "не добавление: передаётся то, что должно остаться снятым. Грани — из " +
                        "kompas_read_topology, а не позиции в коллекции. Маршрут измерен 20.09.2026 " +
                        "(проба --b5, шаг B5.14): на одном признаке короба 100×80×10 оболочка t = 2 " +
                        "внутрь со снятой верхней гранью даёт 21632 при 11 гранях; добавление второй " +
                        "грани (нижней, 100×80) делает полость сквозной и даёт (100·80 − 96·76)·10 = " +
                        "7040 при 10 гранях; возврат к прежнему набору возвращает 21632 при 11. " +
                        "Отрицательный контроль стоит там же: повторная запись того же набора объём " +
                        "не двигает. Маршрут Clear() + Add() по ksEntityCollection проверен на " +
                        "оболочке СВОИМ прогоном, а не перенесён со скругления — у скругления он " +
                        "измеренно не работал (строка FL04r). Пустой список отвергается " +
                        "INVALID_ARGUMENT: измерено на обоих API, что при пустом списке операция " +
                        "принимается (Create/Update = true), а тело не меняется — объём остаётся " +
                        "80000 при 6 гранях. Ссылки привязаны к ревизии документа: после мутации " +
                        "перечитайте kompas_get_feature, старые отсекаются STALE_REFERENCE.",
                        1, 64))),
                    ("reposition_kind", Sch.Nullable(Sch.Enum(
                        "Вид преобразования (семейство reposition) — правка признака изменения " +
                        "положения. Обязателен: без вида непонятно, что менять. Повторный " +
                        "kompas_reposition этому не заменяет — он создаёт ВТОРОЙ признак и " +
                        "накапливает смещение, тогда как правка обязана менять параметры " +
                        "существующего признака относительно его ИСХОДНЫХ входов (наряд B3 §5). " +
                        "Маршрут измерен 18.09.2026 пробой --reposition, шаг RP.6: правка " +
                        "признака[0] повторной записью того же вектора оставила габарит " +
                        "(17,−11,13)…(37,−1,18), а возврат вектора в ноль вернул тело домой.",
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
                        "Аналитическое ожидание объёма после правки (G03: 100·80·12 = 96000; фаска 3×3 " +
                        "на четырёх рёбрах h=10: 80000 − 20·3·3 = 79820). Без него правка не может быть " +
                        "подтверждена геометрически. У семейства reposition объём — ИНВАРИАНТ жёсткого " +
                        "преобразования, поэтому там он подтверждает лишь то, что преобразование " +
                        "осталось жёстким; положение подтверждает expected_bbox_mm.",
                        0d, 1e18d))),
                    ("expected_bbox_mm", Sch.Nullable(Sch.Ref("#/$defs/bbox"))),
                    ("plane", Sch.Nullable(Sch.Described(
                        Sch.Ref("#/$defs/cut_plane"),
                        "Новая опора признака РАЗДЕЛЕНИЯ (семейство split) или ОТСЕЧЕНИЯ " +
                        "(семейство cut_by_plane). Форма та же, что у одноимённого поля " +
                        "kompas_split_body и kompas_cut_body. Маршрут измерен 18.09.2026 пробой " +
                        "--split, шаг SP.9: правка идёт переносом ТРЁХ ТОЧЕК ПОСТРОЕНИЯ СОБСТВЕННОЙ " +
                        "опоры признака (E-A для разделения, E-C для отсечения) и переводит части " +
                        "6000/18000 при x=10 в 9000/15000 при x=15 при неизменном числе признаков " +
                        "1 → 1. Подстановка ДРУГОЙ, заново построенной плоскости результата НЕ даёт: " +
                        "Update() возвращает true, а части остаются прежними (отрицательный контроль " +
                        "E-B). Поэтому plane_ref на признаке разделения и отсечения отвергается " +
                        "CAPABILITY_UNAVAILABLE: доказать, что ссылка указывает именно на опору ЭТОГО " +
                        "признака, нечем. Плоскость при правке НЕ создаётся — опора берётся у самого " +
                        "признака, поэтому документ не накапливает неиспользованные плоскости. " +
                        "Определение перечисляется ЦЕЛИКОМ: plane обязателен и для разделения, и для " +
                        "отсечения, а неполный запрос отвергается INVALID_ARGUMENT до COM."))),
                    ("keep_side", Sch.Nullable(Sch.Enum(
                        "Новая оставляемая сторона для семейства cut_by_plane: positive — s > 0, " +
                        "negative — s < 0, где s = n·(p − p₀). Обязательна вместе с plane: сторона — " +
                        "такой же параметр признака, как опора, и не читается обратно с живого " +
                        "признака. Измерено шагом SP.7.",
                        "positive", "negative"))),
                    ("target_body_ref", Sch.Nullable(Sch.Ref("#/$defs/reference"))),
                    ("expected_part_volumes_mm3", Sch.Nullable(Sch.Arr(
                        Sch.Num("Объём части, мм³.", 0d, 1e18d),
                        "Аналитическое ожидание объёмов ЧАСТЕЙ после правки разделения (семейство " +
                        "split) — единственная величина, которая эту правку подтверждает. Сумма " +
                        "объёмов частей при правке не меняется (24000 и при x=10, и при x=15), " +
                        "поэтому сохранение объёма — инвариант, а не подтверждение; различает части " +
                        "только их пообъёмный состав: [6000, 18000] против [9000, 15000]. " +
                        "Сопоставление — по допуску объёма и с учётом кратности: каждому ожиданию " +
                        "своё тело, одно тело не закрывает два ожидания; порядок частей значения не " +
                        "имеет. Несовпадение — отказ NO_GEOMETRY_CHANGE с partial_effects и " +
                        "фактическим составом в details. Минимум два значения: разделение даёт не " +
                        "меньше двух частей.",
                        2, 64))),
                    ("operation", Sch.Nullable(Sch.Enum(
                        "Новый ВИД существующей булевой операции (семейство boolean). Маршрут измерен " +
                        "18.09.2026 пробой --boolean, шаг BO.11: перезапись IBoolean.BooleanType на " +
                        "СУЩЕСТВУЮЩЕМ признаке вместе с Update() меняет геометрию — E-A переводит " +
                        "объединение 36000 в разность 12000 с габаритом x ≤ 20, E-D возвращает " +
                        "12000 → 36000, признаков 1 → 1. Применяет именно ПАРА «запись + Update()»: " +
                        "запись без Update(), но с пересборкой геометрии не меняет (контроль E-E). " +
                        "Объём разности и объём пересечения на эталоне РАВНЫ (12000), поэтому " +
                        "объявляйте ещё и expected_bbox_mm — габарит различает (разность лежит в " +
                        "x ≤ 20, пересечение — в x ∈ [20,40]); одного объёма здесь мало. Опорные " +
                        "тела существующей операции НЕ меняются: правка набора инструментов не " +
                        "измерялась. Значение 0 (ksBooleanUnknown) сеттер нормализует в union — " +
                        "измерено (E-B), записавший 0 получит объединение, а не отказ.",
                        "union", "difference", "intersect"))),
                    ("pattern", Sch.Nullable(Sch.Obj(
                        "pattern_edit",
                        Array.Empty<string>(),
                        Sch.Props(
                            ("count1", Sch.Nullable(Sch.Int(
                                "Новое число экземпляров по первому направлению. У массива по сетке — " +
                                "по оси 1, у кругового — в РАДИАЛЬНОМ направлении (страница " +
                                "icircularpattern_props.html называет Count1 радиальным). Минимум 1.",
                                1, 1_000_000))),
                            ("count2", Sch.Nullable(Sch.Int(
                                "Новое число экземпляров по второму направлению. У массива по сетке — " +
                                "по оси 2, у кругового — в КОЛЬЦЕВОМ направлении. Минимум 1.",
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
                                "Новая ориентация экземпляров (SaveInitialOrientation): false - " +
                                "доворачивать до радиального направления, true - сохранять исходную " +
                                "ориентацию (только перенос). Умолчание при СОЗДАНИИ - false; при " +
                                "правке пишется только переданное значение, своего умолчания правка " +
                                "не применяет. Член есть ТОЛЬКО у ICircularPattern: у ILinearPattern и " +
                                "IMirrorPattern его нет ни в справке, ни в интероп-сборке, поэтому " +
                                "между семействами он не переносится и на них отвергается " +
                                "INVALID_ARGUMENT."))),
                            ("save_initial_objects", Sch.Nullable(Sch.Bool(
                                "Новое значение SaveInitialObjects ЗЕРКАЛЬНОГО массива. Член есть " +
                                "только у IMirrorPattern. Справка " +
                                "imirrorpattern_saveinitialobjects.html ограничивает свойство ТОЛЬКО " +
                                "операцией o3d_mirrorAllOperation: у остальных операций зеркального " +
                                "копирования возможность скрыть экземпляры отсутствует. Измерено: на " +
                                "зеркале по всем телам запись false читается обратно как false и " +
                                "исходные тела заменяются отражёнными, а на зеркале выбранных " +
                                "операций (o3d_mirrorOperation=48) запись false читается обратно как " +
                                "true и геометрия не меняется. На сетке и на круговом отвергается " +
                                "INVALID_ARGUMENT."))),
                            ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                                "Аналитическое ожидание объёма документа ПОСЛЕ правки. Без него " +
                                "применение подтверждается только чтением параметров обратно, и это " +
                                "честно помечается в unverified_aspects.", 0d, 1e18d))),
                            ("expected_body_count", Sch.Nullable(Sch.Int(
                                "Аналитическое ожидание числа тел после правки. Сравнение точное: к " +
                                "счётным величинам допуск не применяется.", 0, 1_000_000)))),
                        "Новые параметры СУЩЕСТВУЮЩЕГО признака массива (SM-18 / SM-19 / SM-23). " +
                        "Правка — запись в ЖИВОЙ объект IModelContainer.FeaturePatterns и Update(), " +
                        "а не пересоздание признака: имя и место в дереве сохраняются. Признак " +
                        "сопоставляется с элементом коллекции по имени оболочки дерева и штампу " +
                        "обновления — тем же прибором, что и kompas_get_pattern; несопоставление " +
                        "даёт CAPABILITY_UNAVAILABLE до мутации, потому что запись «в первый " +
                        "попавшийся» изменила бы чужой признак. Заданные члены перезаписываются, " +
                        "незаданные остаются как есть, поэтому запрос несёт ВСЕ параметры режима, " +
                        "которые должны сохраниться. Update()=true — «принято», а не «применено»: " +
                        "после перестроения модель читается обратно, и каждому запрошенному члену " +
                        "отвечает своя проверка read_back_<член> в checks. Ось массива и плоскость " +
                        "зеркала этим полем НЕ меняются (Axis1/Axis2, Axis, Plane принимают " +
                        "IModelObject, и смена опоры существующего массива не измерялась). С полями " +
                        "других семейств (depth_mm, radius_mm, plane, operation и прочими) не " +
                        "сочетается: смешение отвергается INVALID_ARGUMENT до мутации."))),
                    ("diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый диаметр ОТВЕРСТИЯ (семейство hole)",
                        "Пилота у цековки и зенковки, " +
                        "самого отверстия у глухого и сквозного цилиндрического. Маршрут измерен " +
                        "20.09.2026 (зонд scratch/_hole_edit_probe.py, шаг M.6): сквозное Ø10 → Ø12 " +
                        "на плите 10 мм сняло 345.575191895 мм³ = π·(36−25)·10, и это пережило " +
                        "save → close → reopen. Правка идёт ДОКУМЕНТИРОВАННЫМ маршрутом: " +
                        "существующее отверстие берётся членом IHoles3D.Hole3D[index] — тем же, " +
                        "которым его читает kompas_get_feature, — в него пишутся члены СВОЕГО " +
                        "режима, применяется IModelObject.Update(), затем перестроение. Адрес НЕ " +
                        "угадывается: соответствие признака дерева и записи IHoles3D доказывается " +
                        "ЕДИНСТВЕННОСТЬЮ отверстия в документе, и при нескольких отверстиях вызов " +
                        "отвергается CAPABILITY_UNAVAILABLE до COM — запись в Holes3D[0] изменила " +
                        "бы чужое отверстие. Режим правкой не меняется: он читается из модели " +
                        "(IHole3D.HoleType), и поле чужого режима отвергается INVALID_ARGUMENT с " +
                        "перечнем своих полей. Нулевой диаметр отвергается: КОМПАС принимает ноль и " +
                        "строит признак без материала при неизменном объёме (тот же класс, что " +
                        "нулевой катет фаски, F.12)."))),
                    ("counterbore_diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый диаметр ВЫТОЧКИ цековки (режим through_counterbore)",
                        "Поле ЧУЖОГО " +
                        "режима для остальных: на глухом и на зенковке отвергается INVALID_ARGUMENT " +
                        "до COM с перечнем своих полей. Маршрут измерен 20.09.2026 (шаг M.6): " +
                        "выточка Ø18×4 → Ø20×5 сняла 474.380490692 мм³ сверх прежнего — разность " +
                        "колец π/4·(D²−d²)·h (1178.097244573 против 703.716754404, формула M.2). " +
                        "Пишется в ISpotfacingHoleParameters.SpotfacingDiameter; у чужого режима " +
                        "этот интерфейс на объекте НЕДОСТИЖИМ — измерено контролем (в) того же зонда."))),
                    ("counterbore_depth_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новая глубина ВЫТОЧКИ цековки (режим through_counterbore)",
                        "Записывается " +
                        "в ISpotfacingHoleParameters.SpotfacingDepth и читается обратно из модели " +
                        "(counterbore_depth_read_back в checks). Поле чужого режима для остальных."))),
                    ("countersink_diameter_mm", Sch.Nullable(Sch.PositiveMm(
                        "Новый диаметр УСТЬЯ зенковки (режим through_countersink)",
                        "Устья, а не " +
                        "пилота: пилот задаётся diameter_mm. Маршрут измерен 20.09.2026 (шаг M.6): " +
                        "устье Ø20 → Ø24 при 90° сняло 605.280184592 мм³ сверх прежнего — разность " +
                        "π·h/3·(rM² + rP·rM − 2·rP²) при производной h = (rM − rP)/tan(угол/2) " +
                        "(1128.878960190 против 523.598775598). Пишется вместе с " +
                        "ICountersinkHoleParameters.CountersinkType = ksCTDiameterAngle."))),
                    ("countersink_angle_deg", Sch.Nullable(Sch.Num(
                        "Новый угол ЗЕНКОВКИ (режим through_countersink), градусы, строго между 0 и " +
                        "180. Глубина зенковки при этом способе ПРОИЗВОДНА от угла и устья " +
                        "(измерено M.3: запись 2/4/6 не меняет ничего), поэтому записанное число " +
                        "не утверждается — в checks публикуется ПРОЧИТАННОЕ, с проверкой " +
                        "countersink_depth_derived. Сверяйте прочитанное, а не запрошенное.",
                        0.0000001d, 180d))),
                    ("expected_volume_delta_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание СНЯТОГО правкой материала (семейство hole), мм³: " +
                        "объём_до − объём_после. ЗНАК — ЧАСТЬ ОПРЕДЕЛЕНИЯ ВЕЛИЧИНЫ, а не " +
                        "оформление: у «стало глубже» дельта положительна, у «стало мельче» — " +
                        "отрицательна, и сравнивать модуль нельзя. Именно на этом ошиблась первая " +
                        "редакция зонда: сравнивала приращение объёма с аналитическим «снято» и " +
                        "давала ложное «не совпало» на верной геометрии — дефект ПРИБОРА, а не факт " +
                        "о продукте. Дельта, а не полный объём, потому что она привязана к правке и " +
                        "различает «применилось ровно запрошенное» от «применилось не то»; " +
                        "expected_volume_mm3 принимается наравне и проверяется отдельно. Без " +
                        "объявленного ожидания уровень остаётся call_returned, а в " +
                        "unverified_aspects появляется expected_volume_delta_not_supplied.",
                        -1e18d, 1e18d)))),
                WorkerCommands.UpdateFeature,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_set_feature_suppressed", "Подавить или восстановить признак",
                "excluded=true отключает признак, false включает обратно. Эффект измерен прогонами 12.09.2026 (проба L.7 и строки L04…L06 приёмки): подавление сквозного окна 40×20 вернуло пластине 80000 мм³, восстановление — обратно 72000 мм³. Направление изменения объёма зависит от типа признака (приклейка — меньше, вырезание — больше), поэтому сверяется число, а не знак. Это не удаление: имя и объект признака сохраняются, НО подавленный признак исчезает из EntityCollection(o3d_operationElement=110) — kompas_list_features его не показывает, пока подавление не снято.",
                Sch.Props(
                    ("feature_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("suppressed", Sch.Bool("true — подавить, false — восстановить.")),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма после изменения. Без него геометрия не подтверждается.",
                        0d, 1e18d)))),
                WorkerCommands.SuppressFeature,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_delete_feature", "Удалить признак",
                "Удаляет признак (измерено пробой L.8: ksDocument3D.DeleteObject → true, число признаков минус один). Зависимые перечисляются ДО удаления, но достоверного перечня в API нет: ни в API5, ни в API7 члена «зависимые признаки» не обнаружено, поэтому возвращаются кандидаты — признаки, идущие в дереве после удаляемого. Если кандидаты есть, удаление без confirm_dependents отказывает кодом DEPENDENT_FEATURES без обращения к КОМПАС. Операция необратима: сохранённая до неё копия документа — единственный откат.",
                Sch.Props(
                    ("feature_ref", Sch.Ref("#/$defs/reference")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("confirm_dependents", Sch.Bool("Согласие удалить признак, после которого в дереве есть другие.", false)),
                    ("expected_volume_mm3", Sch.Nullable(Sch.Num(
                        "Аналитическое ожидание объёма после удаления.", 0d, 1e18d)))),
                WorkerCommands.DeleteFeature,
                requiresDocument: false,
                requiresRevision: true),

            Mutation("kompas_rebuild", "Перестроить",
                "Перестроение документа. Все ссылки, выданные для предыдущей ревизии, после него отвергаются.",
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

            Mutation("kompas_import_step", "Импорт STEP",
                "Чтение STEP. Возвращает, что реально появилось: документы, типы, число тел и компонентов. Успехом считается только подтверждённая геометрия.",
                Sch.Props(
                    ("application_id", Sch.Ref("#/$defs/application_id")),
                    ("input_path", Sch.Ref("#/$defs/path")),
                    ("desired_kind", Sch.Enum("auto — определить по результату; part/assembly — ожидание, влияющее только на проверку.", "auto", "part", "assembly")),
                    ("target_path", Sch.Nullable(Sch.Ref("#/$defs/output_path")))),
                WorkerCommands.ImportStep,
                requiresDocument: false),

            Mutation("kompas_export_image", "Снимок модели",
                "Растровый снимок документированным маршрутом API5: "
                + "ksDocument3D.RasterFormatParam → ksRasterFormatParam → SaveAsToRasterFormat. "
                + "Картинка возвращается image-блоком и/или файлом; габарит читается ИЗ ЗАГОЛОВКА "
                + "файла, а не берётся из запроса. Два режима маршрута взаимно исключающие и это "
                + "измерено: с непустым именем файла ядро пишет файл и оставляет массив байт "
                + "пустым, с пустым — отдаёт массив байт и файла не создаёт. Когда нужны и файл, и "
                + "картинка, рендер делается ОДИН раз, и файл пишется из тех же байтов. "
                + "Проекция задаётся параметром view (ksViewProjectionCollection → SetCurrent → "
                + "refresh); применённая проекция подтверждается ОБРАТНЫМ ЧТЕНИЕМ типа, а не "
                + "возвратом SetCurrent. Прежний вид возвращается после снимка, если не задан "
                + "keep_view, — НО вернуть его можно только тогда, когда он ВОССТАНОВИМ (прочитан и "
                + "его тип опубликован): в видимом окне до первого SetCurrent ни одна проекция не "
                + "отвечает IsCurrent=true, и вызов с keep_view=false отказывает VIEW_UNAVAILABLE "
                + "ДО смены вида, не трогая окно пользователя. keep_view=true там означает согласие "
                + "оставить запрошенную проекцию. Измерено: смена вида НЕ помечает документ "
                + "изменённым и ревизию не двигает. СНИМОК — ВСПОМОГАТЕЛЬНЫЙ КАНАЛ, А НЕ "
                + "ДОКАЗАТЕЛЬСТВО: изображение сервером не разбирается, и снимок не подтверждает ни "
                + "геометрию, ни размеры.",
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
                        + "1 → 328×448, 2 → 655×894.", exclusiveMin: true, exclusiveMinValue: 0d))),
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
                "Строит группу стандартных ассоциативных видов модели документированным маршрутом "
                + "API7: IDrawingDocument.ViewsAndLayersManager → IViews.AddStandartViews(FileName, "
                + "ProjectionName, ProjectionsTypes, X, Y, Scale, DX, DY) (справка v24: "
                + "iviews_addstandartviews.html). ИСТОЧНИК — ФАЙЛ МОДЕЛИ НА ДИСКЕ: стандартный вид есть "
                + "проекция ФАЙЛА, и несохранённая модель даёт явный отказ DOCUMENT_NOT_FOUND, а не "
                + "попытку, падающую глубже. Типы видов берутся из перечисления ProjectionType "
                + "(projectiontype.html): front, rear, top, bottom, left, right, isometric, iso_yzx, "
                + "iso_zxy, dimetric; имя вне перечня отвергается INVALID_ARGUMENT со списком "
                + "поддержанных ДО обращения к КОМПАС. Ответ — виды, ПЕРЕЧИТАННЫЕ из чертежа (номер, "
                + "тип, масштаб, файл-источник, имя проекции), а не пересказ запроса; число СОЗДАННЫХ "
                + "видов считается сравнением коллекции до и после. Габарит вида не читается: IView "
                + "его не публикует, и это названо в unverified_aspects, а не оставлено пустым.",
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

            ReadOnly("kompas_list_drawing_views", "Перечень видов чертежа",
                "Читает виды чертежа через IDrawingDocument.ViewsAndLayersManager.Views: номер, имя, "
                + "тип, масштаб, координаты точки привязки и — для ассоциативных видов — файл-источник, "
                + "имя проекции, видимость скрытых и осевых линий, число объектов. Только чтение; "
                + "габарит вида не выдаётся, потому что IView его не публикует (справка маршрута не "
                + "даёт) — поле остаётся null с прямой пометкой, а не заполняется приблизительно.",
                Sch.Props(("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.ListDrawingViews,
                requiresDocument: true),

            ReadOnly("kompas_list_dimensions", "Перечень размеров вида чертежа",
                "Читает размеры выбранного вида через ISymbols2DContainer, полученный из IView: "
                + "LineDimensions, RadialDimensions, DiametralDimensions (справка v24: "
                + "isymbols2dcontainer.html). Для каждого размера отдаётся тип отрисовки "
                + "(IDrawingObject.DrawingObjectType), признак Valid, значение в миллиметрах и точки. "
                + "ЧТЕНИЕ ИЗ ПЕРЕОТКРЫТОГО ДОКУМЕНТА: инструмент берёт размеры ТЕКУЩЕГО дескриптора "
                + "документа, поэтому после close/open он читает объекты переоткрытого файла, а не "
                + "сохранённый ранее DTO или освобождённую RCW. ОГРАНИЧЕНИЕ, НАЗВАННОЕ ПРЯМО: значение "
                + "линейного размера считается по координатам точек (X1/Y1/X2/Y2), потому что справка "
                + "не публикует готовое номинальное значение; ассоциативность размера к модели не "
                + "читается и не заявляется.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("view_ref", Sch.Ref("#/$defs/reference")),
                    ("limit", Sch.Nullable(Sch.Num(
                        "Предел числа возвращаемых размеров. Не задано — умолчание сервера.",
                        exclusiveMin: true, exclusiveMinValue: 0d)))),
                WorkerCommands.ListDimensions,
                requiresDocument: true),

            Mutation("kompas_add_dimension", "Поставить размер на виде",
                "Линейный, радиальный или диаметральный размер в выбранном виде через "
                + "ISymbols2DContainer (справка v24: isymbols2dcontainer.html), полученный из IView: "
                + "LineDimensions.Add(), RadialDimensions.Add(), DiametralDimensions.Add() — каждый "
                + "Add() без аргументов возвращает объект, координаты которого затем задаются "
                + "(ilinedimension_props.html, iradialdimension_props.html). ОГРАНИЧЕНИЕ, НАЗВАННОЕ "
                + "ПРЯМО: размер привязан к ТОЧКАМ вида в координатах вида, а не к топологии модели; "
                + "АССОЦИАТИВНОСТЬ РАЗМЕРА К МОДЕЛИ НЕ ЗАЯВЛЯЕТСЯ — справка документирует точки вида, "
                + "а не привязку размера к геометрии. Ответ — размер, ПЕРЕЧИТАННЫЙ из объекта "
                + "размера (значение, точки, признак Valid), а не эхо запроса. Изменение маршрута "
                + "модели влечёт перечитывание вида, но НЕ обновление размера автоматически.",
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
                "Записывает значения в ячейки основной надписи через ILayoutSheet.Stamp → IStamp "
                + "(справка v24: ilayoutsheet_stamp.html, istamp_text.html, itext_str.html): "
                + "IStamp.Text(Id) ВОЗВРАЩАЕТ IText (чтение — не установщик), запись идёт через "
                + "IText.Str, затем IStamp.Update(). ГРАНИЦА, НАЗВАННАЯ ПРЯМО: справка SDK НЕ "
                + "документирует числовые идентификаторы ячеек — соответствие «наименование / "
                + "обозначение / материал / масштаб / разработал / проверил» номерам ячеек в ней "
                + "отсутствует. Поэтому номера ячеек задаёт вызывающий (cells = {«<номер>»: «значение»}), "
                + "а сервер ПЕРЕЧИТЫВАЕТ каждую ячейку после Update() и возвращает признак совпадения; "
                + "несовпадение не скрывается, а попадает в unverified_aspects. Ключ, не являющийся "
                + "числом, отвергается INVALID_ARGUMENT, потому что Text(Id) принимает Int32.",
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
                "Экспорт чертежа документированным маршрутом конвертера: IApplication.Converter → "
                + "IConverter.GetFilter(docType, saveAs, out command) → "
                + "IConverter.Convert(InputFile, Outfile, Command, ShowParam) (справка v24: "
                + "iapplication_converter.html, iconverter_getfilter.html, iconverter_convert.html). "
                + "Справка задаёт результат Convert прямо: 1 — успешное завершение, 0 — неудача. "
                + "Опубликованы ТОЛЬКО форматы, для которых справка называет программный маршрут: dxf "
                + "и dwg (iconverter_getfilter.html: FORMAT_DXF=1, FORMAT_DWG=2). PDF в справке SDK НЕ "
                + "описан как программный маршрут и отвергается FORMAT_UNAVAILABLE по имени, а не "
                + "предпринимается вслепую. Результат подтверждается НЕЗАВИСИМОЙ проверкой файла: "
                + "существование, непустой размер и форма содержимого — конвертер v24 пишет выгрузку "
                + "ZIP-контейнером (сигнатура «PK»), и это НАЗВАНО в ответе, как и обычные сигнатуры "
                + "(заголовок секции для DXF, «AC10xx» для DWG). Так документированный успех, не "
                + "давший файла, не проходит, а документированная неудача не превращается в успех "
                + "из-за наличия файла. Перезапись существующего файла возможна только с явным "
                + "overwrite=true.",
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
                + "пишется (null — «не трогать», а не выдуманное умолчание). Отдельно: подавление вида "
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
                "Пишет блок технических требований документированным маршрутом API7: "
                + "IDrawingDocument.TechnicalDemand → ITechnicalDemand.Text (read-only свойство, "
                + "отдающее интерфейс IText) → IText.Str (запись) → ITechnicalDemand.Update() "
                + "(«применить заданные параметры технических требований») → ПЕРЕЧИТЫВАНИЕ "
                + "(справка v24: idrawingdocument_technicaldemand.html, itechnicaldemand_text.html, "
                + "itechnicaldemand_update.html, itechnicaldemand_iscreated.html). Ответ несёт "
                + "перечитанный текст и признак IsCreated («отображение технических требований в "
                + "документе»): успех Update() сам по себе доказательством не считается. ГРАНИЦА, "
                + "НАЗВАННАЯ ПРЯМО: размещение блока на листе (AutoPlacement, BlocksGabarits) "
                + "сервером не задаётся и не проверяется.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("expected_revision", Sch.Ref("#/$defs/expected_revision")),
                    ("text", Sch.Str(
                        "Полный текст технических требований. Строки разделяются переводом строки."))),
                WorkerCommands.SetTechnicalDemand,
                requiresDocument: true,
                requiresRevision: true),

            ReadOnly("kompas_get_title_block", "Прочитать ячейки основной надписи",
                "ЧИТАЕТ ячейки основной надписи и НИЧЕГО не записывает: IStamp.Text(Id) — свойство "
                + "только для чтения, отдающее IText, а IText.Str читается без изменения (справка v24: "
                + "istamp_text.html, itext_str.html). Отдельный маршрут чтения нужен потому, что "
                + "установщик не свидетельствует сохранность: kompas_set_title_block СНАЧАЛА присваивает "
                + "IText.Str, и перечитывание совпадёт даже на документе, потерявшем значение при "
                + "сохранении/переоткрытии. Этот инструмент не несёт ожидаемого значения вовсе, поэтому "
                + "годится как независимая проверка «что реально лежит в документе сейчас». "
                + "ГРАНИЦА: справка SDK НЕ документирует соответствие номеров ячеек понятиям (наименование, "
                + "обозначение, …) — номера задаёт вызывающий.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id")),
                    ("cell_ids", Sch.Arr(
                        Sch.Str("Строковый номер ячейки (Int32 для IStamp.Text(Id))."),
                        "Номера ячеек для чтения. Пустой список ничего не читает."))),
                WorkerCommands.GetTitleBlock,
                requiresDocument: true),

            ReadOnly("kompas_get_technical_demand", "Прочитать технические требования",
                "ЧИТАЕТ блок технических требований и НИЧЕГО не записывает: "
                + "IDrawingDocument.TechnicalDemand → ITechnicalDemand.Text (свойство только для чтения, "
                + "отдающее IText) → IText.Str (чтение); IsCreated — «отображение технических требований "
                + "в документе» (справка v24: idrawingdocument_technicaldemand.html, "
                + "itechnicaldemand_text.html, itechnicaldemand_iscreated.html). Отдельный маршрут чтения "
                + "нужен потому, что kompas_set_technical_demand СНАЧАЛА перезаписывает текст, и "
                + "перечитывание совпадёт даже на документе, потерявшем блок при сохранении/переоткрытии. "
                + "Этот инструмент не несёт ожидаемого текста и потому служит независимой проверкой.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.GetTechnicalDemand,
                requiresDocument: true),

            // ── block VM "variables and material" (profile variables-material-minimal-v1) ──
            ReadOnly("kompas_list_variables", "Внешние переменные детали",
                "ЧИТАЕТ внешние параметрические переменные ВЕРХНЕГО компонента детали и ничего не "
                + "записывает: IPart.VariableCollection() отдаёт «указатель на интерфейс массива внешних "
                + "переменных», перечисление — GetCount()/GetByIndex(), чтение — IVariable.name (только "
                + "чтение), IVariable.value (double) и IVariable.Expression (строка) (справка v24: "
                + "kspart_variablecollection.html, ksvariablecollection_getcount.html, "
                + "ksvariablecollection_getbyindex.html, ksvariable_name.html, ksvariable_value.html, "
                + "ksvariable_expression.html). "
                + "ГРАНИЦА ОБЛАСТИ: это ВНЕШНИЕ переменные компонента, а НЕ редактор всех параметров "
                + "модели; поле scope отвечает «external/top_part» именно поэтому. Поле с непрочитанным "
                + "значением остаётся null с причиной в diagnostics — нулём или пустой строкой оно не "
                + "подменяется. Пустая коллекция — допустимое состояние и названа полем collection_empty; "
                + "недоступная коллекция пустым списком НЕ притворяется и приводит к отказу."
                + "Чтение не меняет ревизию и не требует expected_revision.",
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
                "МЕНЯЕТ значение ИЛИ выражение одной внешней переменной детали, адресуя её ТОЧНЫМ именем. "
                + "Порядок: сначала проверяются документ, ревизия и наличие ИМЕННО этой переменной, затем "
                + "выполняется запись документированного свойства (IVariable.value или IVariable.Expression), "
                + "затем обязательная перестройка ksDocument3D.RebuildDocument, после чего значение и "
                + "выражение ПЕРЕЧИТЫВАЮТСЯ из ВНОВЬ ПОЛУЧЕННОЙ коллекции (справка v24: "
                + "ksvariable_value.html, ksvariable_expression.html, "
                + "ksvariablecollection_getbyname.html, ksdocument3d_rebuilddocument.html). "
                + "ИЗМЕРЕНО: ksPart.RebuildModel, чья страница обещает передать внешние переменные в "
                + "модель, геометрию НЕ двигает и после перестройки документа возвращает прежнюю глубину; "
                + "перестраивает именно RebuildDocument. "
                + "Ровно одно из value и expression; оба сразу или ни одного — отказ до COM. "
                + "В режиме value переменная, чьё выражение СЧИТАЕТ значение (формула или ссылка), "
                + "ОТКЛОНЯЕТСЯ с возвратом текущего выражения: запись числа уничтожила бы его молча. "
                + "Постоянное выражение-число — не формула, а само значение, поэтому такая переменная "
                + "принимает запись числа (ИЗМЕРЕНО: ядро обновляет её выражение до записанного числа). "
                + "Возврат перестройки = TRUE — исход вызова, а НЕ подтверждение результата: "
                + "подтверждением служит перечитанное значение и независимое измерение. "
                + "Выражение считает КОМПАС; собственный вычислитель сервер не подставляет и строку как "
                + "код не исполняет.",
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
                "ЧИТАЕТ обозначение материала верхнего компонента детали и его ФИЗИЧЕСКУЮ плотность. "
                + "Имя — ksPart.material (свойство ТОЛЬКО ДЛЯ ЧТЕНИЯ; справка прямо говорит «Обозначение "
                + "материала можно получить только у детали»). Плотность — документированный маршрут МЦХ "
                + "API5: ksPart.CalcMassInertiaProperties(ST_MIX_M|ST_MIX_KG).r, где единицу возвращаемых "
                + "данных задаёт АРГУМЕНТ вызова (справка v24: kspart_calcmassinertiaproperties.html, "
                + "ksmassinertiaparam.html, ksmassinertiaparam_props.html, mtypes.html). При "
                + "ST_MIX_M|ST_MIX_KG плотность публикуется в кг/м³ БЕЗ пересчёта сервером — поля "
                + "density_kg_per_m3, density_unit, density_route. "
                + "ИЗМЕРЕНО: единицу задаёт АРГУМЕНТ вызова документированного маршрута, сервер значение "
                + "не пересчитывает, и оно переживает сохранение и переоткрытие. "
                + "ГРАНИЦА: чтение имени и чтение плотности — ДВА разных исхода; успешное чтение имени не "
                + "означает успешного чтения плотности, и они сообщаются раздельно. Нулевая или нечисловая "
                + "плотность, а также деталь без тела дают НАЗВАННЫЙ отказ, а не измеренную плотность; "
                + "плотность из справочника сервера НЕ подставляется. Поле get_density_raw_diagnostic несёт "
                + "СТАРЫЙ getter ksPart.GetDensity() как ДИАГНОСТИКУ: его страница называет г/куб.мм, а ядро "
                + "отдаёт значение, согласованное с г/куб.см (измерено), поэтому оно НЕ публикуется как "
                + "плотность.",
                Sch.Props(
                    ("document_id", Sch.Ref("#/$defs/document_id"))),
                WorkerCommands.GetMaterial,
                requiresDocument: true),

            Mutation("kompas_set_material", "Назначить материал и плотность",
                "НАЗНАЧАЕТ материал и плотность верхнего компонента детали: ksPart.SetMaterial(name, "
                + "density) и затем обязательный ksPart.Update («Изменение материала вступает в силу "
                + "после вызова метода ksPart::Update»), после чего имя и плотность ПЕРЕЧИТЫВАЮТСЯ "
                + "(справка v24: kspart_setmaterial.html). "
                + "ЕДИНИЦЫ: SetMaterial документирован в г/куб.см, и сервер переводит вход по единственному "
                + "документированному переводу (7850 кг/м³ → 7.85 г/см³). Плотность подтверждается "
                + "ПЕРЕЧИТЫВАНИЕМ тем же документированным маршрутом МЦХ "
                + "(ksPart.CalcMassInertiaProperties(ST_MIX_M|ST_MIX_KG).r), который отдаёт кг/м³ — ту же "
                + "единицу, что запрошена, поэтому сравнение одноимённое: поля read_density_kg_per_m3_after "
                + "и density_matches с допуском density_tolerance_kg_per_m3. Имя материала подтверждается "
                + "отдельно (name_matches); несовпавшая или непрочитанная плотность подтверждения НЕ даёт. "
                + "ГРАНИЦЫ (документированы справкой): компонент должен быть деталью и не должен быть "
                + "деталью из библиотеки моделей или стандартным элементом; в детали метод действует на "
                + "ВЕРХНИЙ компонент и на подсборку не распространяется. "
                + "Ответ строится ПЕРЕЧИТЫВАНИЕМ, а не эхом запроса: запрошенные значения лежат в "
                + "отдельных полях, чтобы расхождение было видно, а не сглажено.",
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
                "Структурный предикат по граням тела. Два подходящих кандидата — AMBIGUOUS_SELECTION, а не молчаливый выбор первого. Применяются surface_type, area_range_mm2, normal_direction и normal_angle_tolerance_deg — это весь предикат; нормаль задаётся в системе координат детали, которой принадлежит тело. Поле вне этого набора отвергается схемой (additionalProperties=false) с INVALID_ARGUMENT, а не игнорируется.",
                Sch.Props(
                    ("body_ref", Sch.Ref("#/$defs/reference")),
                    ("predicate", Sch.Ref("#/$defs/selection_predicate")),
                    ("require_unique", Sch.Bool("Требовать ровно одного совпадения.", true)),
                    ("limit", Sch.Ref("#/$defs/limit"))),
                WorkerCommands.ResolveSelection),

            Mutation("kompas_create_aux_geometry", "Создать плоскость, ось или точку детали",
                "Создаёт объект вспомогательной геометрии КАК ОБЪЕКТ МОДЕЛИ, а не как числа в "
                + "аргументах чужого вызова: смещённую или наклонную плоскость, ось по двум точкам "
                + "(либо по цилиндрической поверхности, либо по ребру), точку по координатам или "
                + "смещением от опорной вершины. Маршрут — документированный API7 (справка v24: "
                + "IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D, IModelContainer.GetPoints3D, "
                + "IPlanes3D.Add / IAxes3D.Add / IPoints3D.Add; типы из официальной таблицы "
                + "obj3dtype.html: o3d_planeOffset=14, o3d_planeAngle=15, o3d_axis2Points=10, "
                + "o3d_axisConeFace=11, o3d_axisEdge=12, o3d_point3D=70). "
                + "ВИД И СПОСОБ РАЗДЕЛЕНЫ: kind отвечает «что это» и принимает только plane/axis/point; "
                + "mode отвечает «как построено» и принимает offset/angle/by_2_points/by_face/by_edge/"
                + "coordinates/displace; сочетание, которого нет, отвергается INVALID_ARGUMENT со "
                + "списком поддержанных. sub_kind в ответе — НАТИВНЫЙ тип КОМПАСа (o3d_planeOffset, "
                + "o3d_axis2Points, ksPDisplace и т. п.), а не способ: способ в kind не возвращается. "
                + "ПОЛЯ ЧУЖИХ СПОСОБОВ НЕ ИГНОРИРУЮТСЯ: поле вне набора своего способа отвергается "
                + "INVALID_ARGUMENT с перечнем foreign_fields и allowed_fields — принятое и "
                + "проигнорированное поле доживает до приёмки, выглядя как выполненная работа. "
                + "ОПОРА ПЛОСКОСТИ ЗАДАЁТСЯ РОВНО ОДНИМ СПОСОБОМ: base_plane (xy/xz/yz) ЛИБО "
                + "base_face_ref; обе сразу — отказ. Базовая плоскость ищется в коллекции ПО ТИПУ "
                + "ОБЪЕКТА (ModelObjectType), а не по позиции: индекс коллекции адресом не является. "
                + "Для plane/angle базовая прямая задаётся base_axis_ref — ссылкой на ось. "
                + "ОТВЕТ — ПРОЧИТАННОЕ ИЗ МОДЕЛИ, а не пересказ запроса: и числа, и САМ СПОСОБ берутся "
                + "тем же маршрутом чтения, которым их увидит клиент. Прочитанный mode может не "
                + "совпасть с запрошенным — тогда проверка mode_read_back в verification идёт с "
                + "passed=false и обеими величинами, а не подменяется запрошенным; mode=null означает, "
                + "что нативный способ построения в словаре запроса не назван (причина в diagnostics, "
                + "нативный тип в sub_kind). Пустое числовое поле означает «не прочитано» и названо в "
                + "diagnostics, а не подменено нулём. Создание подтверждается перестроением и "
                + "поднимает ревизию документа.",
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
                + "здесь ТЕ ЖЕ, что у ответа создания."
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
                + "ПУСТОЕ ПОЛЕ ОЗНАЧАЕТ «НЕ ПРОЧИТАНО», А НЕ НОЛЬ, и причина названа в notes.",
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
                "Перечисляет сущности СУЩЕСТВУЮЩЕГО эскиза и читает ОДНУ из них по УСТОЙЧИВОМУ "
                + "АДРЕСУ. Маршрут — документированная справка v24: ISketch.BeginEditEx(true) → "
                + "IFragmentDocument.ViewsAndLayersManager.Views → IView → "
                + "IDrawingContainer.GetObjects(ksAllObj) → адрес IKompasDocument1.GetObjectId → "
                + "ISketch.EndEdit(). "
                + "АДРЕС — ЭТО СТРОКА GetObjectId, которую FindObjectById принимает обратно, поэтому "
                + "она переживает перестроение модели и переоткрытие документа; ИНДЕКС КОЛЛЕКЦИИ "
                + "УСТОЙЧИВЫМ АДРЕСОМ НЕ ОБЪЯВЛЯЕТСЯ, потому что перестроение его сдвигает. "
                + "Вход в эскиз идёт ТОЛЬКО НА ЧТЕНИЕ (BeginEditEx(true)): вызов BeginEdit() открыл "
                + "бы эскиз на запись, то есть чтение получило бы право менять модель. "
                + "ЧТЕНИЕ НЕ ПОДНИМАЕТ РЕВИЗИЮ: обход ничего не создаёт и не меняет. "
                + "АДРЕС, КОТОРОГО НЕТ, — DOCUMENT_NOT_FOUND; адрес, разрешившийся в несколько "
                + "сущностей, — AMBIGUOUS_SELECTION: молчаливый выбор первой сделал бы адрес "
                + "неоднозначным. ПУСТОЕ ПОЛЕ ОЗНАЧАЕТ «НЕ ПРОЧИТАНО», А НЕ НОЛЬ: у графического "
                + "объекта API7 имени нет вовсе (у IDrawingObject такого члена не существует), и это "
                + "названо в notes, а не выдано за «объект без имени». "
                + "ПОЛИЛИНИЯ И СПЛАЙН ПРИХОДЯТ ОДНИМ ОБЪЕКТОМ: строка полилинии (вид ksDrPolyline) несёт "
                + "entity_kind=polyline и поля points_count и closed, прочитанные IPolyLine2D.PointsCount и "
                + "IPolyLine2D.Closed; строка сплайна (вид ksDrBezier) — entity_kind=spline и те же поля из "
                + "IBezier. У прочих видов эти поля null: непрочитанное не выдаётся за ноль.",
                Sch.Props(
                    ("sketch_ref", Sch.Ref("#/$defs/reference")),
                    ("address", Sch.Nullable(Sch.Str("Адрес одной сущности, выданный этим же перечислением.", maxLength: 512))),
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
        };

        foreach (var tool in tools)
        {
            Sch.WithDefs(tool.InputSchema, defs);
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
            properties["operation_id"] = Sch.Ref("#/$defs/operation_id");
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
        ("x_axis", Sch.Arr(Sch.Num("Компонента единичной оси X."), "Ось X правого базиса, единичная.", 3, 3)),
        ("y_axis", Sch.Arr(Sch.Num("Компонента единичной оси Y."), "Ось Y: единичная и ортогональная X. Z = X×Y вычисляет сервер.", 3, 3)),
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
                "Объявлено, но НЕ поддержано: маршрут вспомогательной плоскости API7 со смещением "
                + "не измерен, и вызов с этим полем отказывает CAPABILITY_UNAVAILABLE (до мутации, "
                + "retry_policy=never). Задайте плоскость точкой и нормалью. base вместе с другим "
                + "способом (plane_ref или точка с нормалью) — противоречивый запрос: "
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
    private static JsonObject PlaneSchema() => new()
    {
        ["type"] = "object",
        ["title"] = "plane",
        ["description"] = "Опора эскиза: ИМЕННО ОДНО из двух — base (стандартная плоскость xy/xz/yz, "
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
            ("offset_mm", Sch.Num("Смещение вдоль нормали базовой плоскости, мм (измерено пробою P2.4: direction=true = вдоль нормали для XY/XZ/YZ; нормаль YOZ направлена в -X, поэтому +15 на YZ даёт x=-15). Имеет смысл только вместе с base.", -1e6, 1e6, defaultTo: 0d))),
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
            + "kind=polyline рисуется ОДНИМ нативным объектом ломаной (ksPolylineByParam), kind=spline — одним сплайном "
            + "(кривая Безье через заданные вершины: ksBezier → ksBezierPoint → ksEndObj); оба перечитываются одним объектом.",
        ["properties"] = Sch.Props(
            ("kind", Sch.Enum("Тип примитива.", "line", "circle", "arc", "rectangle", "polyline", "spline")),
            ("start_mm", Sch.Nullable(Sch.Arr(Sch.Num("Координата эскиза, мм."), "Начало / угол прямоугольника.", 2, 2))),
            ("end_mm", Sch.Nullable(Sch.Arr(Sch.Num("Координата эскиза, мм."), "Конец отрезка.", 2, 2))),
            ("center_mm", Sch.Nullable(Sch.Arr(Sch.Num("Координата эскиза, мм."), "Центр дуги/окружности.", 2, 2))),
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
                "Дуга по концам: начальная точка на окружности. Взаимоисключающе с start_deg/sweep_deg: "
                + "дуга задаётся ЛИБО углами, ЛИБО концами, и смешение отвергается до COM. Точка обязана "
                + "лежать на окружности (center_mm, radius_mm) с относительным допуском 1e-6.", 2, 2))),
            ("end_point_mm", Sch.Nullable(Sch.Arr(Sch.Num("Координата эскиза, мм."),
                "Дуга по концам: конечная точка на окружности. Обязательна вместе с start_point_mm и "
                + "clockwise.", 2, 2))),
            ("clockwise", Sch.Nullable(Sch.Bool(
                "Дуга по концам: направление отрисовки. true — по часовой стрелке, false — против. "
                + "ОБЯЗАТЕЛЬНО при задании дуги концами и запрещено при задании углами: те же две точки "
                + "называют две разные дуги, и умолчание нарисовало бы дополняющую."))),
            ("width_mm", Sch.Nullable(Sch.PositiveMm("Ширина прямоугольника"))),
            ("height_mm", Sch.Nullable(Sch.PositiveMm("Высота прямоугольника"))),
            ("points_mm", Sch.Nullable(Sch.Arr(Sch.Arr(Sch.Num("Координата, мм."), "Точка", 2, 2),
                "Вершины полилинии или сплайна, мм. Предел " + SketchLimits.MaxPolylineVertices + " вершин на один примитив "
                + "задан СХЕМОЙ MCP, а не ядром. Полилиния рисуется ОДНИМ нативным объектом ломаной "
                + "(ksPolylineByParam) и перечитывается одним объектом: в kompas_list_sketch_entities на неё "
                + "приходит одна строка с entity_kind=polyline (вид ksDrPolyline) и полями points_count и closed, "
                + "а collection_counts показывает poly_lines = 1, а НЕ N отрезков. Замкнутость задаётся полем "
                + "closed и передаётся ядру собственным признаком ломаной (ksPolylineParam.closed). Сплайн "
                + "рисуется одним объектом — кривой Безье ЧЕРЕЗ заданные вершины (ksBezier), — и перечитывается "
                + "одной строкой с entity_kind=spline (вид ksDrBezier); замкнутость несёт его собственный признак "
                + "closed. Полилиния и сплайн — разные объекты: точки полилинии соединяются отрезками, сплайн "
                + "проходит через них кривой.", 2, SketchLimits.MaxPolylineVertices))),
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
