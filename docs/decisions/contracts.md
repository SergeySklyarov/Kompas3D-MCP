# Contracts — решения и измерения

Модуль: `src/KompasMcp.Contracts/`. Здесь — история правок, вынесенная из кода. Действующие правила
остались в коде под метками `INVARIANT:` / `LIMIT:` / `MEASURED:` / `DOC:` / `TEST:`; сюда переехало
«прежде было…». Факты не переформулированы. Дословные цитаты справки КОМПАС остались в коде в
«кавычках» со ссылкой на страницу.

## <a id="budgets-single-table"></a>Единая таблица бюджетов команды (05.10.2026)

**Что было.** До 05.10.2026 бюджет Хоста задавался одной настройкой `operation_budget_ms` (120 с по
умолчанию), а бюджеты Worker — отдельным `switch` в `CommandDispatcher` (180–300 с для подключения,
STEP, снимка, отверстия, операций над телами, сборки и сопряжений).

**Что измерено.** Разбором пути вызова: Хост сдавался на 120 с раньше, чем Worker доходил до своего
предела, объявлял `OUTCOME_UNKNOWN` и ломал канал (`MarkBroken`); следующий вызов через 20 с убивал
Worker, который ещё работал по своему бюджету. То есть бюджеты Worker выше 120 с были НЕДОСТИЖИМЫ, а
локальный конфиг (240 с) делал исход зависимым от гонки.

**Что решено.** Одна таблица `CommandBudgets` на Хост и Worker: бюджет Хоста — бюджет Worker плюс
`HostMarginMs`. Одна таблица убирает расхождение по построению.

## <a id="json-scalars"></a>Две формы JsonNode

**Что было.** Чтение скаляров из `JsonNode` предполагало одну форму узла. `GetValue<T>()` и
`TryGetValue<T>()` строги к различию: узел, собранный в памяти, оборачивает CLR-значение
(`JsonValue<long>`), а узел, пришедший по проводу или через `DeepClone()`, оборачивает `JsonElement`.

**Что измерено.** Проект натыкался на это дважды: один раз в валидаторе схемы (падал каждый числовой
аргумент), один раз при чтении `revision` Worker'а обратно в Хосте, что молча давало устаревшую
цепочку ревизий.

**Что решено.** Всё, что пересекает трубу или границу MCP, читает скаляры через хелперы `JsonScalars`.

## <a id="ipc-read-loop"></a>Единственный читатель кадрового потока (18.09.2026)

**Что было.** Первая редакция Хоста позволяла каждому вызывающему вести свой цикл чтения; метод
`RequestAsync` жил прямо в `IpcChannel`. Два одновременных вызова инструмента (долгая мутация плюс
проба `kompas_health` — случай, ради которого всё и делалось) читали один и тот же поток, перемешивали
байты, и длина-префикс попадала в середину JSON.

**Что измерено.** 18.09.2026, клиент WorkBuddy: «Недопустимая длина кадра 1919951483 байт» (четыре
байта были `{"pr`) и «JsonException: 'o' is an invalid start of a value». Набор приёмки вызывал
инструменты по одному и этот путь не задевал.

**Что решено.** Чтение и запись кадров разведены: ровно один фоновый цикл читает поток и разводит
ответы по `RequestId` (`IpcRequestChannel`); запрос-ответ поверх общего потока больше не живёт в
`IpcChannel`. Повторно заводить цикл чтения на вызывающего запрещено.

## <a id="cancel-after-send"></a>Отмена клиентом после отправки (05.10.2026)

**Что было.** До 05.10.2026 любая `OperationCanceledException` с токеном клиента выходила наружу, и
вызывающий записывал терминальное `cancelled` без требования согласования: клиент получал «команда
отменена в очереди Host и не отправлялась в КОМПАС», хотя кадр уже был записан и Worker выполнял
команду до конца.

**Что измерено.** Клиент, поверивший ответу, повторял мутацию с НОВЫМ `operation_id` — и мутация
применялась дважды.

**Что решено.** Отмена до записи кадра и отмена после неё — РАЗНЫЕ состояния, различаемые флагом
`written`. После отправки мутации ответ — `OUTCOME_UNKNOWN` с требованием согласования, а не
«отменено».

## <a id="document-visible"></a>Видимость приложения и видимость документа (12.09.2026)

**Что было.** Видимость документа смешивалась с видимостью приложения.

**Что измерено.** Дефект 12.09.2026 был именно в их смешении: показать приложение и показать документ —
два разных факта.

**Что решено.** `DocumentVisible` — перечитанное состояние самого документа (`!invisibleMode`),
отдельное от видимости приложения.

## <a id="mates-route"></a>Маршрут сопряжений — решение заказчика (05.10.2026)

**Что было.** Рассматривался метод `ksDocument3D.AddMateConstraint`, документированный как метод
ПОСТОЯННОГО сопряжения.

**Что измерено.** На гранях, полученных документированным путём
`ksPart.BodyCollection() → ksBody.FaceCollection()`, он вернул `False` при всех документированных
сочетаниях параметров; причина не установлена.

**Что решено.** Сопряжения строятся на документированном API7-пути:
`IPart7.MateConstraints` → `IMateConstraints3D.Add(MateConstraintType)` →
`BaseObject1`/`BaseObject2` → `Update()`. Вопрос закрыт решением, а не выводом «метод не работает».

## <a id="rotation-operation"></a>Вид операции вращения задаётся вызовом фабрики (17.09.2026)

**Что было.** Действие операции вращения предполагалось управляемым свойством
`IRotated1.OperationResult`.

**Что измерено.** 17.09.2026 (шаг R.26, прогон `95fa8441`): на подготовленной плите 120×120×40
`o3d_bossRotated` с записанным И прочитанным обратно `OperationResult = ksOperationCut` изменил объём
на 0, тогда как та же операция, созданная как `Add(o3d_cutRotated)`, сняла ровно 25132.7412287183 мм³.
`OperationResult` совершает круговой рейс и ни на что не влияет — это метаданные.

**Что решено.** Вид задаётся ВЫБОРОМ ВЫЗОВА фабрики; клиент не может «переключить» уже открытую
операцию. Отсюда же отказ `kompas_update_feature` на смену вида вращения: смена вида означала бы
удаление признака и создание нового, а это не правка на месте.

## <a id="rotation-direction"></a>Направление вращения: значения ksDirectionTypeEnum (R.26.sector)

**Что было.** Соответствие значений `ksDirectionTypeEnum` действию операции.

**Что измерено.** Все четыре значения измерены на полуобороте: `Normal` (dtNormal=0) — материал по обе
стороны оси (габарит x[−20,20]); `Both` (dtBoth=2) — тоже по обе стороны, именно это значение
использовал поставляемый файл `BEARING 410` для настоящего частичного вращения (R.22);
`MiddlePlane` (dtMiddlePlane=3) — односторонний (x[0,20]) и единственный, у которого смена направления
двигает сектор; `Reverse` (dtReverse=1) — НЕ строит ничего (`Update()` возвращает False, тел 0).

**Что решено.** `Reverse` отвергается до мутации, а не объявляется «построенным» по коду возврата.
Объём при смене направления на полуобороте НЕ различается (полуцилиндр одинаков с обеих сторон), и
габарит тоже симметричен, поэтому «сектор переехал» доказывается только стороной материала — габаритом,
а не объёмом.

## <a id="shell-thin-direction"></a>Направление тонкой стенки оболочки (20.09.2026)

**Что было.** Первая редакция пробы ждала ОБРАТНОГО соответствия `thinType` направлению стенки.

**Что измерено.** 20.09.2026 (проба `--b5`, шаг B5.5; короб 100×80×10 с удалённой верхней гранью,
t = 2): `thinType = true` даёт 21631.999999999996 мм³ — внутрь (полость 96·76·8); `thinType = false`
даёт 24832.000000000022 — наружу (104·84·12 − 80000). Отличие 3200.0000000000255 мм³.

**Что решено.** Под сомнение поставлено ожидание, а не измерение. Типы половин разные: API7
`IShell.ThinType` — `long`, API5 `ksShellDefinition.thinType` — `bool`.

## <a id="fillet-edge-set-history"></a>Правка набора рёбер скругления: смена вердикта (16–17.09.2026)

**Что было.** Сначала признавалось, что набор рёбер можно править через определение API5
(`ksFilletDefinition.array()` → `Clear()` → `Add()` → `entity.Update()`), а ранняя проба H
(`docs/acceptance/api7/fillet-edge-set.md`) сообщала о применении набора: сокращение дало
79961.3716694115, расширение — 79922.7433388231. Затем в коде стояло утверждение, что «ссылки входов
признака и рёбра тела лежат в РАЗНЫХ контекстах», и на нём строился вывод «сопоставить вход с ребром
тела по `Reference` нельзя».

**Что измерено.** 16.09.2026 восемью пробами: маршрут API5 правку набора на существующем признаке НЕ
даёт. Проба H мерила пересчёт признака по ПОДСТАВЛЕННОМУ входу, а не правку набора существующего
признака: числа верны как геометрия и неверны как доказательство правки. Проба H-2
(`docs/acceptance/api7/fillet-base-objects.md`, 14 PASS / 0 FAIL, воспроизведено четырьмя прогонами)
дала рабочий маршрут API7 и опровергла утверждение о «разных контекстах»: полосы ссылок СОСЕДНИЕ — в
`FL10x` вход признака `1073742308` против перенесённого ребра тела `1073742309`, тип у обоих
`ksObjectEdge`, различие `…065-67` против `…080` — разность ЗНАЧЕНИЙ. Корневая причина прежнего отказа
названа 17.09.2026 и оказалась НЕ той, что предполагалась: свойство `base_object_refs` не было
ОБЪЯВЛЕНО в схеме инструмента `kompas_update_feature`, поэтому Host с `additionalProperties: false`
отвергал вызов валидацией ещё ДО COM. Маршрут и валюта были написаны верно; не хватало публикации.

**Что решено.** Вердикт изменён не пробой H, а пробой H-2. В коде остались действующие правила
(маршрут API7, отрицательный результат по API5, граница «расширение набора не выражается»), а сюда
переехало «прежде было…».

No object captured when the fillet was created is used. After the fillet the corner edges are absent
from the topology (0 of 4 — the corners are occupied by cylindrical faces), the original corner edges
are revoked by the very creation of the fillet (`STALE_REFERENCE` before any edit), and the existing
vertical edges of the filleted corners are not retained by the feature: the set collapses
(`edges_read_back=0`) and the volume returns to the plate. A non-zero `edges_read_back` is obtained
only by a second call in a row — and that is a rebuild from scratch, not a set edit.

Bounds that do not carry over to the general conclusion: set expansion on the 100×80×10 reference was
not measured (the plate has exactly four vertical corners, no fifth); what was measured is shrinkage
(4→3) and replacement at an unchanged size (1→1).

PRODUCT STATE: the route was moved into the adapter, acceptance PASSED (17.09.2026).
`scripts/mcp-smoke.py --fillet-only` gives 46 lines, 0 FAIL: shrinkage `FL10` (4→3,
`V=79942.0575041173` against the analytic `79942.05750411731`), `FL10s` (3→2), `FL10b` (2→1) and
replacement at an unchanged size `FL10x` (1→1) — all `level=geometry_checked`.

BOUND: set EXPANSION is not covered by this field. MEASURED by `FL25` on an L-shaped plate with FREE
corners: expansion (1→2, 2→3) is expressed by neither of the two currencies. A full replacement by body
edges means "build the fillet anew on these edges" — the former edge is not kept, whereas expansion
needs exactly the opposite. The outcome is a refusal AFTER mutation: `GEOMETRY_FAILED` with
`partial_effects=true`. Before 17.09.2026 that outcome was returned as `err=None` and
`level=call_returned`, i.e. the disappearance of the feature was passed off as a successful edit; now
the disappearance of the feature is a refusal. Edit the COMPOSITION (shrinkage and replacement), do not
add edges. Details — `docs/STATUS.md`.

## <a id="assembly-domain-contracts"></a>Контракты домена сборок вынесены в AssemblyCommands.cs (наряд C1)

**Что было.** В `WorkerCommands.cs` были объявлены заготовки `ListComponentsCommand` (с
`IncludeSuppressed`/`IncludeHidden`), `SetComponentTransformCommand` (с `CoordinateSpace`) и
`CheckIntersectionsCommand` — от первоначальной спеки.

**Что измерено.** Ни одним инструментом они не использовались: их поля не совпадают с составом блока
C1 (подавления компонентов и контроля пересечений в нём нет).

**Что решено.** Они удалены как нереализованная заготовка, а не как требование: знаменатель профиля от
этого не меняется, счётчик закрытого не растёт. Действующие контракты домена сборок живут в
`AssemblyCommands.cs`.

## <a id="rotation-route"></a>Вращение: маршрут целиком в API7 (шаги R.24…R.26, 17.09.2026)

**Что измерено.** Маршрут измерен 17.09.2026 (шаги R.24…R.26, прогон `95fa8441`) и лежит ЦЕЛИКОМ в
API7: `IModelContainer.Rotateds.Add(type)` → `QI(IRotated)` → запись параметров → `Update()`.
Оболочка API5 (`NewEntity` + `Create()`) на этом объекте НЕ работает — это измерено и было причиной
многомесячной блокировки, а не свойство вращения.

## <a id="rotation-operation-result"></a>Вращение: вид операции выбирает исход (18.09.2026)

**Что было.** Утверждалось, что член `OperationResult` «не переключает действие».

**Что измерено.** 18.09.2026: `OperationResult` ВЫБИРАЕТ исход, а вид фабрики
(`Rotateds.Add(27/28/29)`) объявляет, какие значения для него допустимы. Управляемый опыт на одной
геометрии: `ksOperationUnion` даёт сращивание (тел 1→1), `ksOperationNewBody` — второе тело (тел
1→2). Прежнее утверждение происходило из опыта, который писал пару, недопустимую для своего вида, и
потому измерял отказ.

## <a id="rotation-angle-limit"></a>Вращение: угол в градусах и потолок сектора (18.09.2026)

**Что было.** Утверждалось, что «угол насыщается на 180°, запись 360 даёт половину».

**Что измерено.** 18.09.2026 (проба `FullTurnProbe`, шаги F.1…F.5; независимое чтение `.m3d` пробой
`M3dVerificationProbe`; разбор — `docs/acceptance/api7/full-turn-findings.md`): `Angle[true]` несёт
запрошенный угол напрямую (90→90°, 180→180°, 360→360°), а вторая половина пары, равная первой,
развёртку удваивает. Полный оборот достигается одним вызовом с `angle_deg = 360`, а отказ начинается
только выше 360 — это потолок сектора, а не прежний неверный предел. Прежнее утверждение
ОПРОВЕРГНУТО измерением.

**Ось обязательна и строится в ТОЙ ЖЕ детали.** Измерено (R.24/R.25): вращение, записанное без оси,
не строится вовсе — `Update()` возвращает False и тел остаётся 0. Ось подаётся двумя точками в
координатах МОДЕЛИ: сервер строит её сам как `o3d_axis2Points` в этой же детали, потому что
`IRotated.Axis` принимает модельный объект, а не линию эскиза, и чужая ось из другого документа не
подставляется — ссылки между документами не переносятся.

**Тонкая стенка не проверялась.** Маршрут измерен на сплошном теле (`IThinParameters.Thin = false`).

## <a id="fillet-base-object-references"></a>Скругление: входы признака и валюта правки набора

**Что было.** `BaseObjectReferences` объявлялось «ЕДИНСТВЕННЫМ источником, по которому признак
адресуется в правке набора (`edge_refs`)». Неверно, и проверить это можно не выходя из кода:
`edge_refs` принимает строки реестра ссылок вида `edge:<hex>`, а здесь лежат ЧИСЛА
`IModelObject.Reference` — у них строки реестра нет, и подставить их в `edge_refs` нельзя по типу,
независимо от контекста.

**Что измерено (17.09.2026).** После скругления всех углов у тела 24 ребра: 16 линий и 8 дуг длины
π·3/2 (четверть окружности R3). Все восемь «вертикальных» линий лежат на швах касания цилиндров
`(±47,±40)` и `(±50,±37)`; угловых вертикальных рёбер в топологии НОЛЬ (`len(corner_edges) = 0`) —
их отозвало само скругление.

**Главное: входы признака и рёбра тела НЕ взаимозаменяемы как валюта записи.** Проба H-2 сокращала
набор (4→3, 4→2) объектами, прочитанными ИЗ `BaseObjects`, — ни одно ребро тела при этом не
искалось и не переносилось. Замер 17.09.2026 (FL10): предъявление трёх рёбер тела (дуг) вместо
собственных входов признака СХЛОПЫВАЕТ признак — объём возвращается к пластине
(`79999.99999999999`), `level=call_returned`. Замена при неизменном размере (1→1), наоборот, работает
перенесёнными рёбрами (FL10x, `level=geometry_checked`). Отсюда: СОКРАЩЕНИЕ набора и ЗАМЕНА состава —
разные операции с разной валютой, и одним полем `edge_refs` выражается только вторая.

**Что решено.** Введены `BaseObjectInputRefs` — ссылки реестра `input:<hex>` на СОБСТВЕННЫЕ входы
признака. Это и есть валюта сокращения, которой не хватало. Правило одно для всех ссылок сервера:
клиент не сочиняет и не переносит идентификаторы, а подставляет выданные. Ссылки минтит чтение
признака на текущую ревизию документа, поэтому они стареют так же, как `edge:`-ссылки.

Here are registry strings minted by THIS server, so `UpdateFeatureCommand.BaseObjectRefs` accepts them.
An empty field means "not read" (the API7 bridge was not built or there are several fillets with the
same radius); an empty list — the feature holds no input. These are different states, as with
`BaseObjectReferences`.

That is, `EdgeRefs` expresses replacement but NOT shrinkage. Separately: an early acceptance presented
BODY EDGES to this very field and collapsed the feature into the plate — presenting them here is still
forbidden, they are rejected by the reference kind. The set from `base_object_references` is the only
measured currency of shrinkage. This is the same format the server already returns references in, so
the client substitutes the numbers as they are. The set is a FULL replacement, not an addition: what
must remain is what is passed. An empty list is rejected (`INVALID_ARGUMENT`).

It cannot be combined with `EdgeRefs` in one call (`INVALID_ARGUMENT` before mutation): two different
compositions in one request are indistinguishable in the response, and "one of the two applied" would
look like "both applied". An unknown or foreign `input:` reference is rejected before mutation. A
reference issued to another document is rejected as `STALE_REFERENCE` (`FL26`), inputs of a foreign
feature of the same document — as `CAPABILITY_UNAVAILABLE` (`FL27`); in both cases the model does not
change.

BOUND: set EXPANSION is NOT expressed by this field. A new edge is not a feature's own input, and the
currencies must not be mixed. MEASURED by `FL25` (L-shaped plate with free corners): expansion (1→2,
2→3) ends in a refusal AFTER mutation with `partial_effects=true`. Before 17.09.2026 the same outcome
was returned as success with `level=call_returned` — that was a wrong message and it is fixed: the
disappearance of the feature is now a refusal, not "success at a reduced level".

## <a id="reposition-position-member"></a>Перенос тела: член `Position` значение не хранит (18.09.2026)

**Что было.** Первая редакция правки сверяла записанный перенос с `IBodyReposition.Position.X/Y/Z`.

**Что измерено.** 18.09.2026: этот член перенос НЕ несёт — после `InitByMatrix3D` с вектором
`(7,−11,13)` он читается как `(0,0,0)`, тогда как габарит тела верен. Сверять с членом, который не
хранит значение, значит измерять прибор, а не продукт.

**Что решено.** Проверка перенесена на ГЕОМЕТРИЮ: при переносе «сдвинулось» и «осталось» по объёму
неразличимы, габарит различает. Это же требование записано в приёмке (строка `B3L.04`: у переноса
объём до и после равен 1 000, и строка, сверяющая одни объёмы, прошла бы на полном бездействии).

## <a id="split-support-route"></a>Разделение/отсечение: правка опоры признака (шаг SP.9, 18.09.2026)

**Зачем плоскость, а не повторный вызов.** Наряд B3 §5 требует, чтобы правка меняла параметры
СУЩЕСТВУЮЩЕГО признака относительно его ИСХОДНЫХ входов. Повторный `kompas_split_body` этому не
удовлетворяет: он создаёт ВТОРОЙ признак разделения и режет уже полученную часть. Измерено 18.09.2026
(проба `--split`, шаг SP.9): правка опоры существующего признака переводит части `6 000 / 18 000` при
`x = 10` в `9 000 / 15 000` при `x = 15` при неизменном числе признаков `1 → 1` и неизменной сумме
`24 000`.

**Поправка 18.09.2026: здесь стояла ссылка на шаг SP.6, и она вводила в заблуждение.** Шаг SP.6
назван «Правка той же плоскости: x=10 → x=15», но его код переносит три точки построения плоскости, а
не правит опору признака; шаг SP.7, в свою очередь, строит отсечение ЗАНОВО в свежем документе под
каждое значение `Direction`. Ни один из них маршрут «записать в существующий признак другую
плоскость» не проверял. По этой ссылке маршрут был выведен из НАЗВАНИЯ шага, а не из его кода, и
реализация подстановки чужой плоскости измеренно не работала (`Update() = true`, части
`6 000 / 18 000` прежние). Авторитетная ссылка — SP.9 с отрицательным контролем E-B; SP.6 остаётся
верным как факт о плоскости, но не как доказательство правки признака.

**Маршрут — перенос ТОЧЕК СОБСТВЕННОЙ опоры признака, и это измерено, а не выведено.** Шаг `SP.9`
пробы `--split` (прогон `c9cd7660468c44aa97b410e253ee2cb1`) сравнил два маршрута на одном и том же
признаке. `CutObjects` читается обратно как ОДИН объект (не массив) и отвечает `IPlane3DBy3Points`;
перенос его трёх точек построения с последующим `Update()` и пересборкой меняет геометрию (E-A: части
`9 000 / 15 000`, признаков разделения `1 → 1`). Подстановка ВТОРОЙ, заново построенной плоскости в
`CutObjects` при `Update() = true` результат НЕ меняет — отрицательный контроль E-B. Поэтому
реализация правит опору признака, а не подставляет другую плоскость, и `Update() = true` здесь
доказательством не считается: геометрия сверяется отдельно.

**Цена маршрута и границы.** Вспомогательная плоскость при правке НЕ создаётся: объект опоры берётся
у самого признака, поэтому документ не накапливает неиспользованные плоскости. `plane_ref` на
признаке разделения/отсечения отвергается кодом `CAPABILITY_UNAVAILABLE`: подстановка чужой плоскости
измеренно ничего не делает (E-B).

## <a id="json-schema-additional-properties-default"></a>Валидатор: «additionalProperties: false по умолчанию» — соглашение, не правило (18.09.2026)

The first revision applied the convention as a rule and rejected a CORRECT call —
expected_bbox_mm ({min_mm,max_mm}) passed the $ref branch and was then called an "unknown field"
at the wrapper level (MEASURED 18.09.2026: INVALID_ARGUMENT with
violations=[$/expected_bbox_mm/min_mm additionalProperties, …/max_mm additionalProperties]).
It showed only on an OBJECT value under a pure anyOf: under an array the parse goes to
ValidateArray, and under an object the loop below was reached for the first time here.

## <a id="pattern-edit-members"></a>PatternEditDto: члены трёх интерфейсов (queue B4)

Exactly those members that the property pages
(ilinearpattern_props.html, icircularpattern_props.html,
imirrorpattern_props.html, checked over the wire) declare on these interfaces:

- ILinearPattern: Angle1/2, Count1/2, Direction1/2, Step1/2, BuildingType.
- ICircularPattern: Count1/2, Step1/2, StepByAxis, ReverseDirection, SaveInitialOrientation, BuildingType.
- IMirrorPattern: SaveInitialObjects.

## <a id="pattern-edit-routing"></a>PatternEditDto: маршрут правки признака массива (queue B4)

Grouping makes the family boundary visible in the contract itself, not only in the documentation. For
the other editable families the branch is chosen by `entity.type`, and that is a measured number.

## <a id="hole-edit-address"></a>Правка отверстия: адрес признака не угадывается (шаг M.6, 20.09.2026)

A feature name is not an identifier: it does not survive the API5↔API7 transition (MEASURED on the
chamfer, F.8). A mode change was not measured.

## <a id="solid-feature-reposition-read"></a>SolidFeatureDto: чтение параметров переноса (шаг RP.25)

The route was MEASURED at step RP.25 of probe `--reposition-params` (run
`a336120926fc4652a8bf737562568271`): both the triple of angles and the displacement are read from a
REOPENED document BEFORE assembly and BEFORE any write.

A feature written by a matrix is not readable — and this is a refusal, not zeros. Such a feature is
recognized by the READ `OrientationType = 0 (ksAxisOrientation)`: it has no orientation parameters,
and the matrix form of placement on a reopened document is unit while the geometry is preserved, so
the kind cannot be derived from it — this is exactly what produced a false `RepositionKind = "translate"`
for a written rotation. Existing features are not silently converted: all five fields are named
unreadable with this reason.

## <a id="boolean-edit-kind"></a>Правка вида булевой операции (шаг BO.11, 18.09.2026)

Reference §6.1: A ∪ B — 36 000 in bbox (0,0,0)…(60,30,20), A − B — 12 000 in x ≤ 20, A ∩ B —
12 000 in x ∈ [20,40]. Experiment E-E confirms that it is the pair "write → Update()" that applies:
a write without Update() (but with a rebuild) does not change the geometry. If another family ever
needs its own "operation kind", it MUST take a QUALIFIED name (as the rotation angle did —
RepositionAngleDeg) rather than introduce a second Operation field.
