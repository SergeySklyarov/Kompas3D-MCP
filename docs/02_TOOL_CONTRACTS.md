# 2. Контракты инструментов и данных

Это проект публичного API, а не описание существующих инструментов. Имена ниже зафиксированы для v1; переименование документировать миграцией. Полные JSON Schema для каждого входа/выхода и контрактные тесты — обязательный результат разработки. Поддержку инструментов по этапам сообщает capabilities; не регистрировать мутацию-пустышку, возвращающую успех.

## 2.1. Общие правила

- JSON Schema: `additionalProperties:false` для объектов запроса; required поля явные. Reject неизвестных полей, NaN, Infinity, отрицательных длин, недопустимых enum.
- Во всех CAD-командах `document_id` обязателен, кроме создания/открытия и session discovery. Операция create/open принимает `application_id`.
- Все мутации, включая экспорт/сохранение файлов: `operation_id` UUID обязателен. Для правки существующего документа также `expected_revision`.
- Публичный `timeout_ms` влияет на бюджет наблюдения; не меняет семантику отмены.
- Контекстные ссылки — непрозрачные строки, например `face:uuid`. Реальный COM pointer и raw numeric ref наружу не выходят.
- Пакет работы возвращается как MCP tool result с `structuredContent` и коротким text summary. **Конверт обязан ехать и в ТЕКСТОВОМ блоке** — не только в `structuredContent`. Правило уточнено 18.09.2026 после клиентской приёмки: клиент, который читает только текстовые блоки (измерено на WorkBuddy AI 5.5.2, build `910352f0`: его `convertMcpResult` собирает видимый модели ответ исключительно из `content`, а `structuredContent` уводит в `mcpMeta` — это видит интерфейс, но не агент), при «короткой сводке» не получает ни `application_id`, ни `document_id`, ни ревизии, ни кода ошибки, ни объёма, и не может выполнить ни одного сценария. MCP 2025-06-18 требует обратной совместимости ровно по этой причине. Формат ответа: первая строка — сводка (`<tool>: <status> | … | verified: <level>`, её читает человек), со второй строки — сериализованный конверт. Ошибки инструмента — `isError:true` и стабильный error DTO; протокольные ошибки отличать от CAD-ошибок. Schema контракта проверяется на выбранном SDK; не вручную реализовывать MCP transport. Проверяется приёмкой: строки `S03c`/`S03d` в `scripts/mcp-smoke.py`. **Подтверждено клиентом 18.09.2026, 19:35** (сеанс `c9a82310`, сценарий §5 выполнен целиком): в сыром транскрипте беседы 74 результата `DeferExecuteTool` несут полный конверт именно в блоке `output` типа `text` (2308…27609 байт), при том что `structuredContent` по-прежнему лежит отдельно в `providerData.toolResult.mcpMeta`. То есть обе ветки работают, и та, которую читает агент, теперь несёт данные.
- MCP annotations readOnly/destructive/idempotent проставить корректно, но не использовать их вместо реальной защиты.

## 2.2. Конверт результата

```json
{
  "contract_version": "1.0",
  "operation_id": "13fa6ab8-3674-4e67-a052-41f070b072a1",
  "status": "succeeded",
  "application_id": "app-uuid",
  "document_id": "doc-uuid",
  "revision_before": 17,
  "revision_after": 18,
  "result": {"feature_id": "feature:uuid", "body_count": 1},
  "verification": {
    "level": "geometry_checked",
    "checks": [{"name": "body_count", "passed": true}],
    "unverified_aspects": []
  },
  "warnings": [],
  "artifacts": [],
  "error": null
}
```

Поля application/document/revision могут быть null для host-only операций. `status` — queued/running/succeeded/failed/cancelled/outcome_unknown. `succeeded` означает выполнение заявленного контракта, а не автоматически доказанную технологичность.

Ошибка:

```json
{
  "code": "STALE_REFERENCE",
  "message": "Грань относится к предыдущей ревизии документа.",
  "retry_policy": "reacquire_context",
  "hresult": null,
  "partial_effects": false,
  "details": {"reference_revision": 17, "current_revision": 18}
}
```

`retry_policy`: never / same_operation_id / reacquire_context / after_reconciliation. Не использовать один флаг retriable для всех случаев.

Минимальный каталог ошибок: INVALID_ARGUMENT, CAPABILITY_UNAVAILABLE, KOMPAS_NOT_INSTALLED, COM_REGISTRATION_ERROR, BITNESS_MISMATCH, LICENSE_UNAVAILABLE, AMBIGUOUS_APPLICATION, APPLICATION_DISCONNECTED, WRONG_DOCUMENT_KIND, DOCUMENT_NOT_FOUND, DOCUMENT_DIRTY, REVISION_CONFLICT, STALE_REFERENCE, AMBIGUOUS_SELECTION, NO_BODY, GEOMETRY_FAILED, NO_GEOMETRY_CHANGE, INTERSECTION_UNKNOWN, COM_BUSY, WORKER_UNRESPONSIVE, OUTCOME_UNKNOWN, QUEUE_FULL, OPERATION_ID_CONFLICT, PATH_NOT_ALLOWED, FILE_EXISTS, SAVE_FAILED, EXTERNAL_REFERENCES, EXPORT_FAILED, IMPORT_FAILED, UNITS_UNVERIFIED, NOT_CONSTANT_THICKNESS, AMBIGUOUS_FLAT_PATTERN, VERIFICATION_FAILED, CANCEL_NOT_CONFIRMED, SESSION_OWNER_ACTIVE, JOURNAL_UNAVAILABLE.

Два последних добавлены 21.09.2026 нарядом `JOURNAL_SHARING_FIX_DEVELOPER_PROMPT.md` §A и описывают состояния ЗАПУСКА, а не вызова CAD:

- `SESSION_OWNER_ACTIVE` — на журнале операций уже работает другой Хост, владелец жив и ОБСЛУЖИВАЕТ сеанс. Второй Хост отказывается начинать работу и НАЗЫВАЕТ pid владельца, его состояние и число обслуженных запросов; отказ доставляется двумя путями — ответом на `initialize` (JSON-RPC, код `-32050`) и конвертом вызова (`status: failed`, `retry_policy: never`), потому что `tools/list` клиент вызывает после `initialize`. Владелец жив, но НЕ обслуживает (завершает работу) — второй Хост ЗАБИРАЕТ владение и работает; владелец мёртв — владение забирается безусловно.
- `JOURNAL_UNAVAILABLE` — журнал операций недоступен для записи по причине ВНЕ правила выше (чужой процесс, права, диск). Хост завершается с кодом 70, называя причину в stderr и в журнале Хоста; **работа без журнала безопасности не начинается молча**.

Коды объявляются здесь потому, что вводить их молча запрещено: до 21.09.2026 ни один из них в каталоге не значился, а `tools/list` при этом их уже отдавал.

## 2.3. Типы данных

**DocumentContext:** id, application_id, kind(part/assembly/drawing/fragment), path|null, dirty, revision, feature_count, body_count, component_count, unit_system, origin, fingerprint, external_change_detection(reliable/conservative/unavailable).

**Reference:** id, kind, document_id, revision, persistent_feature_id|null, bbox, semantic_hint. Реестр хранит COM-объект только внутри Worker.

**Transform:** origin_mm[3], x_axis[3], y_axis[3]; правую Z ось вычислить. Матрица UI при необходимости строится из этого представления. Указать порядок композиции, тестировать на некоммутирующих повороте и переносе.

**Artifact:** id, relative_path, absolute_path (только разрешённый путь), media_type, byte_length, sha256, created_at_utc, operation_id, source_revision, provenance. Ссылки resources вида `kompas://artifacts/{id}` не дают произвольного доступа к файлам.

**PagedResult:** items, next_cursor|null, total_known|null. Cursor привязан к ревизии; при изменении модели вернуть REVISION_CONFLICT, не смешивать страницы.

**BOMRow:** part_key, marking, name, source_path, configuration, classification, quantity, material|null, specification|null, source_instances[], warnings[]. Материал из имени файла не угадывать.

## 2.4. Session, очередь, документы — P0/P1

| Инструмент | Вход сверх общих полей | Результат и условия |
|---|---|---|
| `kompas_health` | detail: minimal/diagnostic | Host жив, Worker состояние, очередь; без обращения к COM при busy |
| `kompas_capabilities` | optional application_id | Реально поддержанные инструменты, API и ограничения |
| `kompas_connect` | mode attach/launch, optional application_selector, `make_visible` | application_id, PID, ownership, version; launch только явно |
| `kompas_disconnect` | application_id, close_owned_application=false | По умолчанию отсоединение, не закрытие документов |
| `kompas_operation_status` | operation_id | Durable state, result, partial_effects |
| `kompas_operation_cancel` | operation_id | cancel_requested/confirmed; не притворяться, что COM отменён |
| `kompas_operation_reconcile` | operation_id | Чтение фактического результата неопределённой операции |
| `kompas_list_documents` | application_id, cursor, limit | id, type, path, dirty; без сохранения |
| `kompas_get_context` | document_id, detail | Контекст, ревизия, fingerprint |
| `kompas_create_document` | application_id, kind | Новый unsaved документ с UUID |
| `kompas_open_document` | application_id, path, access read_only/edit | Если уже открыт — возвращает тот же id; проверка типа |
| `kompas_save_document` | document_id, optional target_path, overwrite=false | Файл и хеш, обновлённый контекст; явная семантика SaveAs |
| `kompas_close_document` | document_id, dirty_policy refuse/save/discard | refuse по умолчанию; discard должен быть явно задан |
| `kompas_checkpoint` | document_id, destination, dependencies copy/read_only | Manifest файлов и ссылки, хеши, исходная ревизия |
| `kompas_restore_checkpoint` | checkpoint_id, document_id, dirty_policy | Проверенный restore, новая ревизия; перечень затронутых файлов |

`kompas_connect` и restore — мутации сеанса, тоже журналируются. Documents API должен различать чтение неизменённого файла и редактирование уже открытого пользователем документа. Не открывать второй экземпляр того же файла молча.

**Видимость (`make_visible`).** Режим по умолчанию — невидимый: сервер не крадёт фокус (P0.4b).
`make_visible=true` показывает экземпляр, и семантика зависит от его происхождения: `launch` —
экземпляр наш, `false` его явно прячет; `attach` — экземпляр пользовательский, `false` уже видимое
окно **не** скрывает, `true` показывает. Никакая видимость не применяется к экземпляру, не
зарегистрированному в сеансе MCP.

Документы наследуют **наблюдённую** видимость экземпляра, а не запрошенную: создаваемые и
открываемые в видимом сеансе документы видимы, в скрытом — скрыты. Поля ответа разделены и не
подменяют друг друга: `visible` (истина только когда COM-свойство `Visible` и win32
`IsWindowVisible` согласны), `application_visible_by_com`, `application_window_visible_by_windows`,
`window_handle`, `visibility_observation_error` — про приложение; `document_visible`
(перечитанный `!invisibleMode` документа), `documents_visible_mode` (запрошенный режим экземпляра),
`document_active_reported` (возврат `SetActive()`) — про документ. Наличие HWND или определимый по
нему PID видимостью не считаются: скрытое окно КОМПАС даёт и то, и другое — на этом псевдониме и
держался пропущенный дефект. Обновление вида после мутации делается `ksRefreshActiveWindow()` и
только в видимом режиме; сброс камеры (`ZoomPrevNextOrAll`, `ksZoom*`) запрещён и контролируется
статическим тестом. Приёмка: `python scripts\mcp-visibility-test.py` (проверяет и окнами Windows).

## 2.5. Чтение и выбор геометрии — P1/P2

| Инструмент | Вход | Результат |
|---|---|---|
| `kompas_list_features` | document_id, cursor, limit | Тип, имя, state, feature_id, родители/зависимости где доступны |
| `kompas_list_bodies` | document_id | Тела, bbox, solid/sheet, refs |
| `kompas_list_faces` | body_ref, filters, cursor | Тип поверхности, площадь с единицами, центр, нормаль если однозначна |
| `kompas_list_edges` | body_ref или face_ref, cursor | Линия/дуга/окружность/иная кривая, длина, вершины, refs |
| `kompas_measure` | target_ref, requested properties | Bbox, объём, площадь; масса только при известной плотности |
| `kompas_resolve_selection` | body_ref, predicate, require_unique=true | Список кандидатов или одна подтверждённая ссылка |
| `kompas_snapshot` | document_id, include tree/topology/properties | Структурированный снимок, artifact для большого результата |
| `kompas_compare_snapshots` | before_id, after_id | Добавленные/изменённые/удалённые элементы и свойства |

Predicate v1 структурный, а не свободный русский текст: surface_type, normal_direction, normal_angle_tolerance_deg, coordinate_space, extremum_axis, extremum_mode, area_range_mm2, bbox_range. «Верхняя» означает явно заданную систему координат и направление. Два одинаковых подходящих кандидата — AMBIGUOUS_SELECTION.

## 2.6. Геометрические мутации — P2

| Инструмент | Минимальные параметры |
|---|---|
| `kompas_create_plane` | base_plane XY/XZ/YZ или planar_face_ref; offset_mm, optional orientation |
| `kompas_create_sketch` | plane_ref, name; явный sketch_local frame |
| `kompas_edit_sketch` | sketch_ref, mode append/replace/delete_entities, entities[] |
| `kompas_finish_sketch` | sketch_ref; проверка замкнутости при требовании профиля |
| `kompas_extrude` | sketch_ref, operation base/boss/cut, depth_mm, direction positive/negative/symmetric, target_body_ref для boss/cut |
| `kompas_revolve` | sketch_ref, axis definition, angle_deg, operation |
| `kompas_loft` | ordered section_refs[] (не менее двух), operation, target_body_ref при добавлении/вырезе; `building` auto/by_normal/by_object/cupola, `closed`; документ берётся по `document_id` |
| `kompas_sweep` | sketch_ref (профиль, задаёт документ), path_ref (траектория, обязана быть в той же детали), `shift_mode` parallel/keep_angle/orthogonal |
| `kompas_shell` | document_id, face_refs[] удаляемых граней (НЕПУСТОЙ список), thickness_mm, `thin_direction` inward/outward, `tangent_faces` (объявлен, `true` отвергается — см. ниже) |
| `kompas_fillet` | edge_refs[], radius_mm, tangent_propagation |
| `kompas_chamfer` | edge_refs[], explicit mode distance_distance/distance_angle и размеры |
| `kompas_update_feature` | feature_ref, typed changes, supported_feature_type |
| `kompas_rebuild` | document_id; новые refs/context после перестроения |
| `kompas_pattern_grid` | copy_kind operations/bodies, source_refs[], axis1_point1_mm/axis1_point2_mm, step1_mm, count1, step2_mm/count2 или axis2_*, angle2_deg; `choose_bodies_type` all_bodies/new_body для копий тел |
| `kompas_pattern_circular` | copy_kind operations/bodies, source_refs[], axis point/direction, count, angle_deg или полный круг; `reverse_direction` |
| `kompas_pattern_mirror` | mode mirror_all/selected_operations, source_refs[], plane или base+offset_mm, `save_initial_objects`, `choose_bodies_type` |
| `kompas_get_pattern` | feature_ref; чтение семейства и параметров существующего массива или зеркала |

**Опубликовано ли это — измерено 21.09.2026, а не подразумевается.** Таблица выше есть контракт
проекта (§2), а не список инструментов на проводе. Из **57** имён, названных в этом документе, на
проводе **31**, не опубликованы **26** (`kompas_create_plane`, `kompas_create_hole`,
`kompas_list_faces`, `kompas_list_edges`, `kompas_snapshot`, `kompas_compare_snapshots`,
`kompas_revolve`, `kompas_insert_component`, `kompas_export_image` и другие). `kompas_create_plane`
— одно из неопубликованных: измерено, что на проводе его нет (инструментов **43**, схем **43**, и
среди них нет ни `kompas_create_plane`, ни `kompas_create_axis`, ни `kompas_create_point`). Запись
каталога `AUX-SKETCH.plane_and_profile_lifecycle`, называвшая его как поддержку MCP, приведена к
измеренному состоянию 21.09.2026 — поле `mcp_support` плюс причина в `limitations`. Плоскость для
эскиза выражается **существующим** `kompas_create_sketch`: его поле `plane` принимает `base`
(xy/xz/yz) **либо** `reference` (ссылку вида `face:…`) плюс `offset_mm`; наклонная плоскость вокруг
оси (`ksPlaneAngleDefinition`) программного маршрута **не имеет** и названа таковой. Инструмент
**не публиковался**: §14.3 наряда о зависимостях запрещает заводить дубли под каждую зависимость.

Массивы: `copy_kind=bodies` копирует ТЕЛА, `operations` — признаки. Это разные признаки и в дереве: измерено 20.09.2026, что у копирования тел СВОЙ номер типа (`o3d_BodiesMeshCopy=528`, `o3d_BodiesCircularCopy=529`) против копирования операций (`o3d_meshCopy=35`, `o3d_circularCopy=36`), при ОДНОМ отображаемом имени («Массив по сетке:1»). `choose_bodies_type=new_body` даёт отдельное тело на экземпляр — измерено: 4 тела по 4000 вместо 2 по 8000. Углы (`angle1_deg`, `angle2_deg`) задаются необязательными и пишутся ТОЛЬКО когда переданы: запись нуля вместо неуказанного угла уничтожает умолчание модели. `angle2_deg` — угол МЕЖДУ направлениями сетки (90° прямоугольная сетка, 0° вырождает её в линию, 270° нормализуется в 180°); `angle1_deg` отводит направление от построенной оси. `save_initial_objects` работает ТОЛЬКО для `o3d_mirrorAllOperation`: у прочих операций зеркального копирования возможность скрыть экземпляры справкой отсутствует, и запись `false` на операции 48 читается обратно как `true` без изменения геометрии — это не «не принято молча», а названная граница. Ссылка на признак берётся ИЗ ДЕРЕВА (`kompas_list_features`), а не из ответа операции: создание массива поднимает ревизию, и ссылка из ответа к следующему вызову уже мертва.

Три семейства очереди B5 (`kompas_sweep`, `kompas_loft`, `kompas_shell`) и их измеренные границы (20.09.2026, проба `--b5`). **Кинематика** идёт маршрутом API5 `o3d_baseEvolution=45` → `ksBaseEvolutionDefinition`; справка объявляет этот интерфейс устаревшим («Рекомендуется использовать вместо него интерфейс `ksBossLoftDefinition`»), а рекомендованный `o3d_bossEvolution=46` измерен отдельно и строит ТО ЖЕ тело — базовый тип выбран потому, что обязанные строки описаны как базовые. `shift_mode` документирован страницей `ksbaseevolutiondefinition_sketchshifttype.html` (0 parallel / 1 keep_angle / 2 orthogonal). Различающая постановка режима — только ДУГА: на прямой траектории `parallel` и `orthogonal` дают одно тело, на R50/90° — `24674.011002723353` против `15707.963267948984`. **Сечения** идут маршрутом API7, и это следует из состава обязательных строк: цепочек соответствия сечений в API5 нет вовсе, а `AddCoupling`/`CouplingsCount` документированы и измерены (`KompasAPI7.CouplingClass`, `CouplingsCount = 1`). Фабрика документирована для приклеенного типа (`ilofts_add.html` перечисляет `o3d_bossLoft`/`o3d_cutLoft`), поэтому используется 31, а не базовый 30. **Оболочка** идёт маршрутом API5 `o3d_shellOperation=43`; `thin_direction` (в схеме именно это имя — оно же в контракте записи; прежнее `direction` в этом документе было расхождением документа со схемой, исправлено 20.09.2026) подтверждено дважды — справкой (`ksshelldefinition_thintype.html`: TRUE внутрь, FALSE наружу) и объёмом на обоих API (`21632` внутрь, `24832` наружу при t=2). `tangent_faces` ОБЪЯВЛЕН, но `true` отвергается именованным `CAPABILITY_UNAVAILABLE`: у API5 `ksShellDefinition` члена «касательные грани» нет вовсе, а молчаливое игнорирование вернуло бы успех за работу, которой не было. **Пустой список `face_refs` отвергается до COM**: измерено на ОБОИХ API, что при пустом списке операция принимается (`Create`/`Update = true`), но тело не меняется — объём остаётся `80000` при 6 гранях, тогда как открытая оболочка даёт `21632` при 11. Ожидание наряда `36224` («замкнутая оболочка») **не подтвердилось ни на одном API** и остаётся названным расхождением, а не закрытым режимом.

Sketch entities v1: line(start/end), circle(center/radius), arc(center/radius/start_deg/sweep_deg), rectangle(origin/width/height), polyline(points/closed). Все в sketch-local мм. Отрицательный sweep имеет определённое направление. Контроль отсутствия нулевых длин и случайно открытого профиля.

Параметрические размеры и ограничения эскиза — отдельная capability. Создание геометрии по координатам не называть полностью constrained sketch. `update_feature` поддерживает сначала выдавливание, отверстие и радиус скругления; неподдержанный тип возвращает ошибку без изменения. Не удалять/перестраивать произвольную историю незаметно.

Отверстие с резьбой: использовать структурную спецификацию `{kind:metric, nominal_diameter_mm:5, pitch_mm:0.8, depth_mm:10, handedness:right, representation:cosmetic|native|nominal_envelope}`. Указывать реально реализованный representation. Диаметр резьбы не равен диаметру сверла под метчик. `kompas_create_hole` можно добавить как типизированную композицию sketch+cut с отдельными метаданными резьбы; это должно быть отражено в результате.

После каждой мутации: проверить наличие признака и ожидаемое изменение геометрии, а не только bool Create. Для требуемого изменения объёма сравнить до/после с допуском; допускаются явные операции без изменения объёма, например изменение имени.

## 2.7. Сборки — P3

| Инструмент | Параметры и поведение |
|---|---|
| `kompas_list_components` | recursive, include_suppressed, include_hidden, cursor; экземпляры с parent_id, transform, source_path |
| `kompas_insert_component` | assembly document_id, file_path, transform, fixed=true; новая instance_ref |
| `kompas_set_component_transform` | instance_ref, transform, coordinate_space; проверить фактическое положение после rebuild |
| `kompas_replace_component` | instance_ref, new_file_path, preserve_transform=true; отдельная проверка refs и BOM |
| `kompas_check_intersections` | explicit target sets, contact_policy, tolerance_mm, limit; AABB+exact ядро |
| `kompas_check_positions` | instance_refs, transforms[], exclude_instance_refs; выполняется на scratch/checkpoint-копии, результат дискретный |
| `kompas_extract_bom` | recursive, filters, grouping, classification_rules, optional output_path/format json/html | 
| `kompas_validate_references` | allowed model roots, resolve_relative=true; missing/external/cycles |

set_transform не считается успешным по одному возврату SetPlacement: перечитать transform и bbox. Провал движения не маскировать созданием второй детали. Для check_positions временные вставки допустимы в изолированной копии и перечисляются как артефакты проверки; исходную сборку не менять.

Пример проверки пересечений: касание двух пластин по Z=1 при толщине1 — contact; перекрытие0,25 — overlap; разнесение10 — clear. Ошибка COM — unknown, не clear.

## 2.8. Экспорт — P4

| Инструмент | Параметры | Особенности |
|---|---|---|
| `kompas_export_step` | document_id, output_path, overwrite=false, validation_level | Результат содержит фактически достигнутую проверку |
| `kompas_import_step` | application_id, input_path, desired_kind auto/part/assembly, target_path|null | Указать созданные документы и типы тел, единицы |
| `kompas_export_flat_dxf` | part document_id, face_ref|null, output_path, tolerance_mm, kerf_compensation=false | Только доказанно плоская деталь постоянной толщины |
| `kompas_export_sheet_unfold` | document_id, method native/calculated, bend_parameters, output paths | Дополнительная capability; без поддержки вернуть ошибку |
| `kompas_export_image` | document_id, view, pixel_size, output_path | Растр документа; состояние камеры и источник явно |
| `kompas_validate_export` | artifact_id, requested checks | Не подменять геометрический roundtrip структурной проверкой |
| `kompas_prepare_package` | document_id, destination, formats, classification_rules, overwrite=false | Сценарий: references→BOM→exports→verify→manifest; partial и failed стадии видны |

Для выданного артефакта обязательны source_revision и SHA256. Экспорт в процессе изменения документа должен быть сериализован; итоговый пакет не смешивает разные ревизии.

## 2.9. Ограниченные сценарии — P5

`kompas_run_scenario` принимает `scenario_version`, `document_id`, `operation_id`, `checkpoint_policy`, массив шагов `{step_id,tool,arguments}`, `on_error=stop|restore_checkpoint`. Разрешены только зарегистрированные инструменты и ссылки на поля результата предыдущего шага вида `{"from_step":"s1","field":"result.sketch_ref"}`.

В первой версии нет eval, импортов, файлового IO, shell, сети и произвольных циклов. Максимум100 шагов; все ссылки назад, без циклических зависимостей. У каждого шага стабильный child operation_id, производный от parent+step_id. Restore означает попытку восстановления с отдельным подтверждённым результатом, не обещанную атомарную транзакцию.

Параметрические циклы можно добавить позже как декларативный bounded repeat с явным верхним пределом. Не начинать разработку с execute_python_script или универсальной строки COM-кода.

## 2.10. Иллюстративный запрос

```json
{
  "document_id": "doc-uuid",
  "expected_revision": 7,
  "operation_id": "35fc1d4a-58a4-441c-91dc-108426c0ce5a",
  "sketch_ref": "sketch-uuid",
  "operation": "base",
  "depth_mm": 10,
  "direction": "positive"
}
```

Повтор с тем же operation_id не создаёт второе выдавливание. После успешной операции возвращается новая ревизия и refs. После внешнего изменения до выполнения — REVISION_CONFLICT без частичных эффектов.
