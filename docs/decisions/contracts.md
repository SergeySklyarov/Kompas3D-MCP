# Contracts - решения и измерения

Модуль: `src/KompasMcp.Contracts/`. Здесь - история правок, вынесенная из кода. Действующие правила
остались в коде под метками `INVARIANT:` / `LIMIT:` / `MEASURED:` / `DOC:` / `TEST:`; сюда переехало
«прежде было…». Факты не переформулированы. Дословные цитаты справки КОМПАС остались в коде в
«кавычках» со ссылкой на страницу.

## <a id="budgets-single-table"></a>Единая таблица бюджетов команды (05.10.2026)

**Что было.** До 05.10.2026 бюджет Хоста задавался одной настройкой `operation_budget_ms` (120 с по
умолчанию), а бюджеты Worker - отдельным `switch` в `CommandDispatcher` (180–300 с для подключения,
STEP, снимка, отверстия, операций над телами, сборки и сопряжений).

**Что измерено.** Разбором пути вызова: Хост сдавался на 120 с раньше, чем Worker доходил до своего
предела, объявлял `OUTCOME_UNKNOWN` и ломал канал (`MarkBroken`); следующий вызов через 20 с убивал
Worker, который ещё работал по своему бюджету. То есть бюджеты Worker выше 120 с были НЕДОСТИЖИМЫ, а
локальный конфиг (240 с) делал исход зависимым от гонки.

**Что решено.** Одна таблица `CommandBudgets` на Хост и Worker: бюджет Хоста - бюджет Worker плюс
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
проба `kompas_health` - случай, ради которого всё и делалось) читали один и тот же поток, перемешивали
байты, и длина-префикс попадала в середину JSON.

**Что измерено.** 18.09.2026, клиент WorkBuddy: «Недопустимая длина кадра 1919951483 байт» (четыре
байта были `{"pr`) и «JsonException: 'o' is an invalid start of a value». Набор приёмки вызывал
инструменты по одному и этот путь не задевал.

**Что решено.** Чтение и запись кадров разведены: ровно один фоновый цикл читает поток и разводит
ответы по `RequestId` (`IpcRequestChannel`); запрос-ответ поверх общего потока больше не живёт в
`IpcChannel`. Повторно заводить цикл чтения на вызывающего запрещено.


interleave their bytes and corrupts the stream (MEASURED 18.09.2026).

## <a id="cancel-after-send"></a>Отмена клиентом после отправки (05.10.2026)

**Что было.** До 05.10.2026 любая `OperationCanceledException` с токеном клиента выходила наружу, и
вызывающий записывал терминальное `cancelled` без требования согласования: клиент получал «команда
отменена в очереди Host и не отправлялась в КОМПАС», хотя кадр уже был записан и Worker выполнял
команду до конца.

**Что измерено.** Клиент, поверивший ответу, повторял мутацию с НОВЫМ `operation_id` - и мутация
применялась дважды.

**Что решено.** Отмена до записи кадра и отмена после неё - РАЗНЫЕ состояния, различаемые флагом
`written`. После отправки мутации ответ - `OUTCOME_UNKNOWN` с требованием согласования, а не
«отменено».


<summary>Send one request and await its answer. Safe to call from many callers at once.</summary>
<param name="isMutation">True when the command changes the model. It decides what a cancellation AFTER the frame
was written means: for a mutation the command is already on its way to KOMPAS, so the answer is
<c>OUTCOME_UNKNOWN</c>, never "cancelled, nothing happened".</param>
<remarks>A timeout is reported as <c>OUTCOME_UNKNOWN</c>, never as a cancellation: the peer may still be executing the
command, so the caller must reconcile rather than assume nothing happened. INVARIANT: a client cancellation AFTER the
frame was written is NOT "the command was never sent" - cancelling the token does not abort the COM call; cancellation
before the frame and after it are different states, told apart by <c>written</c>. History: docs/decisions/contracts.md#cancel-after-send</remarks>

## <a id="document-visible"></a>Видимость приложения и видимость документа (12.09.2026)

**Что было.** Видимость документа смешивалась с видимостью приложения.

**Что измерено.** Дефект 12.09.2026 был именно в их смешении: показать приложение и показать документ -
два разных факта.

**Что решено.** `DocumentVisible` - перечитанное состояние самого документа (`!invisibleMode`),
отдельное от видимости приложения.

## <a id="mates-route"></a>Маршрут сопряжений - решение заказчика (05.10.2026)

**Что было.** Рассматривался метод `ksDocument3D.AddMateConstraint`, документированный как метод
ПОСТОЯННОГО сопряжения.

**Что измерено.** На гранях, полученных документированным путём
`ksPart.BodyCollection() → ksBody.FaceCollection()`, он вернул `False` при всех документированных
сочетаниях параметров; причина не установлена.

**Что решено.** Сопряжения строятся на документированном API7-пути:
`IPart7.MateConstraints` → `IMateConstraints3D.Add(MateConstraintType)` →
`BaseObject1`/`BaseObject2` → `Update()`. Вопрос закрыт решением, а не выводом «метод не работает».


<summary>Mate-domain contracts - block C2 (profile <c>mates-minimal-v1</c>, modes <c>MATE-01…MATE-06</c>).</summary>
<remarks>INVARIANT: mates use the documented API7 path: <c>IPart7.MateConstraints</c> → <c>IMateConstraints3D.Add(MateConstraintType)</c> →
<c>BaseObject1</c>/<c>BaseObject2</c> → <c>Update()</c>; <c>ksDocument3D.AddMateConstraint</c> returned <c>False</c> on documented-path faces, cause not established.
History: docs/decisions/contracts.md#mates-route
INVARIANT: a mate is addressed by "component + face number": the face comes from the documented <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c>
(<c>kspart_bodycollection.html</c>) and moves into API7 as <c>IModelObject</c>; <see cref="ComponentRowDto.BodyCount"/>/<see cref="ComponentRowDto.FaceCount"/>
distinguish "component inserted" from "empty component inserted". INVARIANT: the mate type is passed by NAME, not number; an unknown name is rejected,
not replaced by the nearest known one.</remarks>

## <a id="rotation-operation"></a>Вид операции вращения задаётся вызовом фабрики (17.09.2026)

**Что было.** Действие операции вращения предполагалось управляемым свойством
`IRotated1.OperationResult`.

**Что измерено.** 17.09.2026 (шаг R.26, прогон `95fa8441`): на подготовленной плите 120×120×40
`o3d_bossRotated` с записанным И прочитанным обратно `OperationResult = ksOperationCut` изменил объём
на 0, тогда как та же операция, созданная как `Add(o3d_cutRotated)`, сняла ровно 25132.7412287183 мм³.
`OperationResult` совершает круговой рейс и ни на что не влияет - это метаданные.

**Что решено.** Вид задаётся ВЫБОРОМ ВЫЗОВА фабрики; клиент не может «переключить» уже открытую
операцию. Отсюда же отказ `kompas_update_feature` на смену вида вращения: смена вида означала бы
удаление признака и создание нового, а это не правка на месте.


<summary>Rotation operation kind. THIS value decides the action, not <c>IRotated1.OperationResult</c>.</summary>
<remarks>MEASURED (17.09.2026, step R.26, run <c>95fa8441</c>): on a prepared 120×120×40 plate, <c>o3d_bossRotated</c>
with a written AND read-back <c>OperationResult = ksOperationCut</c> changed the volume by 0, while the same
operation created as <c>Add(o3d_cutRotated)</c> removed exactly 25132.7412287183 mm³. So <c>OperationResult</c>
round-trips and affects NOTHING - it is metadata. The kind is set by the CHOICE of factory call, and the client
cannot "switch" an already-open operation; hence <c>kompas_update_feature</c> refuses a kind change: that would
delete the feature and create a new one, not edit in place. History: docs/decisions/contracts.md#rotation-operation</remarks>

## <a id="rotation-direction"></a>Направление вращения: значения ksDirectionTypeEnum (R.26.sector)

**Что было.** Соответствие значений `ksDirectionTypeEnum` действию операции.

**Что измерено.** Все четыре значения измерены на полуобороте: `Normal` (dtNormal=0) - материал по обе
стороны оси (габарит x[−20,20]); `Both` (dtBoth=2) - тоже по обе стороны, именно это значение
использовал поставляемый файл `BEARING 410` для настоящего частичного вращения (R.22);
`MiddlePlane` (dtMiddlePlane=3) - односторонний (x[0,20]) и единственный, у которого смена направления
двигает сектор; `Reverse` (dtReverse=1) - НЕ строит ничего (`Update()` возвращает False, тел 0).

**Что решено.** `Reverse` отвергается до мутации, а не объявляется «построенным» по коду возврата.
Объём при смене направления на полуобороте НЕ различается (полуцилиндр одинаков с обеих сторон), и
габарит тоже симметричен, поэтому «сектор переехал» доказывается только стороной материала - габаритом,
а не объёмом.


<remarks>MEASURED on a half-turn: <c>Normal</c> (dtNormal=0) and <c>Both</c> (dtBoth=2) put material on both sides
(x[−20,20]); <c>MiddlePlane</c> (dtMiddlePlane=3) is one-sided (x[0,20]) and the ONLY one whose direction change
moves the sector; <c>Reverse</c> (dtReverse=1) builds NOTHING (<c>Update()</c> returns False, 0 bodies) and the
server rejects it before mutating. The shipped <c>BEARING 410</c> used <c>Both</c> for a real partial rotation (R.22).
INVARIANT: on a half-turn the volume does not differ between directions (a half-cylinder is symmetric), so "the
sector moved" is proved only by the side of the material - compare the extent, not the volume.
History: docs/decisions/contracts.md#rotation-direction</remarks>

## <a id="shell-thin-direction"></a>Направление тонкой стенки оболочки (20.09.2026)

**Что было.** Первая редакция пробы ждала ОБРАТНОГО соответствия `thinType` направлению стенки.

**Что измерено.** 20.09.2026 (проба `--b5`, шаг B5.5; короб 100×80×10 с удалённой верхней гранью,
t = 2): `thinType = true` даёт 21631.999999999996 мм³ - внутрь (полость 96·76·8); `thinType = false`
даёт 24832.000000000022 - наружу (104·84·12 − 80000). Отличие 3200.0000000000255 мм³.

**Что решено.** Под сомнение поставлено ожидание, а не измерение. Типы половин разные: API7
`IShell.ThinType` - `long`, API5 `ksShellDefinition.thinType` - `bool`.


<remarks>MEASURED (20.09.2026, probe <c>--b5</c>, step B5.5; 100×80×10 box with the top face removed,
t = 2): <c>thinType = true</c> gives <c>21631.999999999996</c> mm³ - INWARD (cavity 96·76·8);
<c>thinType = false</c> gives <c>24832.000000000022</c> - OUTWARD (104·84·12 − 80000). Difference
<c>3200.0000000000255</c> mm³. INVARIANT: the half types differ - API7 <c>IShell.ThinType</c> is
<c>long</c>, API5 <c>ksShellDefinition.thinType</c> is <c>bool</c>.
History: docs/decisions/contracts.md#shell-thin-direction</remarks>

## <a id="fillet-edge-set-history"></a>Правка набора рёбер скругления: смена вердикта (16–17.09.2026)

**Что было.** Сначала признавалось, что набор рёбер можно править через определение API5
(`ksFilletDefinition.array()` → `Clear()` → `Add()` → `entity.Update()`), а ранняя проба H
(`docs/acceptance/api7/fillet-edge-set.md`) сообщала о применении набора: сокращение дало
79961.3716694115, расширение - 79922.7433388231. Затем в коде стояло утверждение, что «ссылки входов
признака и рёбра тела лежат в РАЗНЫХ контекстах», и на нём строился вывод «сопоставить вход с ребром
тела по `Reference` нельзя».

**Что измерено.** 16.09.2026 восемью пробами: маршрут API5 правку набора на существующем признаке НЕ
даёт. Проба H мерила пересчёт признака по ПОДСТАВЛЕННОМУ входу, а не правку набора существующего
признака: числа верны как геометрия и неверны как доказательство правки. Проба H-2
(`docs/acceptance/api7/fillet-base-objects.md`, 14 PASS / 0 FAIL, воспроизведено четырьмя прогонами)
дала рабочий маршрут API7 и опровергла утверждение о «разных контекстах»: полосы ссылок СОСЕДНИЕ - в
`FL10x` вход признака `1073742308` против перенесённого ребра тела `1073742309`, тип у обоих
`ksObjectEdge`, различие `…065-67` против `…080` - разность ЗНАЧЕНИЙ. Корневая причина прежнего отказа
названа 17.09.2026 и оказалась НЕ той, что предполагалась: свойство `base_object_refs` не было
ОБЪЯВЛЕНО в схеме инструмента `kompas_update_feature`, поэтому Host с `additionalProperties: false`
отвергал вызов валидацией ещё ДО COM. Маршрут и валюта были написаны верно; не хватало публикации.

**Что решено.** Вердикт изменён не пробой H, а пробой H-2. В коде остались действующие правила
(маршрут API7, отрицательный результат по API5, граница «расширение набора не выражается»), а сюда
переехало «прежде было…».

No object captured when the fillet was created is used. After the fillet the corner edges are absent
from the topology (0 of 4 - the corners are occupied by cylindrical faces), the original corner edges
are revoked by the very creation of the fillet (`STALE_REFERENCE` before any edit), and the existing
vertical edges of the filleted corners are not retained by the feature: the set collapses
(`edges_read_back=0`) and the volume returns to the plate. A non-zero `edges_read_back` is obtained
only by a second call in a row - and that is a rebuild from scratch, not a set edit.

Bounds that do not carry over to the general conclusion: set expansion on the 100×80×10 reference was
not measured (the plate has exactly four vertical corners, no fifth); what was measured is shrinkage
(4→3) and replacement at an unchanged size (1→1).

PRODUCT STATE: the route was moved into the adapter, acceptance PASSED (17.09.2026).
`scripts/mcp-smoke.py --fillet-only` gives 46 lines, 0 FAIL: shrinkage `FL10` (4→3,
`V=79942.0575041173` against the analytic `79942.05750411731`), `FL10s` (3→2), `FL10b` (2→1) and
replacement at an unchanged size `FL10x` (1→1) - all `level=geometry_checked`.

BOUND: set EXPANSION is not covered by this field. MEASURED by `FL25` on an L-shaped plate with FREE
corners: expansion (1→2, 2→3) is expressed by neither of the two currencies. A full replacement by body
edges means "build the fillet anew on these edges" - the former edge is not kept, whereas expansion
needs exactly the opposite. The outcome is a refusal AFTER mutation: `GEOMETRY_FAILED` with
`partial_effects=true`. Before 17.09.2026 that outcome was returned as `err=None` and
`level=call_returned`, i.e. the disappearance of the feature was passed off as a successful edit; now
the disappearance of the feature is a refusal. Edit the COMPOSITION (shrinkage and replacement), do not
add edges. Details - `docs/STATUS.md`.


<summary>New SET of fillet edges (family=<c>fillet</c>) - a full replacement, not an addition: what must remain is what is passed. The edges come from
<c>kompas_read_topology</c>, not from positions in a collection.</summary>
<remarks>The route is API7 and MEASURED: probe H-2 (<c>docs/acceptance/api7/fillet-base-objects.md</c>, 14 PASS / 0 FAIL / 0 UNKNOWN, reproduced over four
runs) - <c>IModelContainer.Fillets[i] → IFillet</c>, read/write of <c>IFillet.BaseObjects</c> (full replacement), then the mandatory <c>IFillet.Update()</c>;
all objects are taken from the LIVE model after <c>save → close → reopen</c>, and addressing was checked with TWO fillets of the same radius (H2.7).
THE NEGATIVE RESULT ON THE FORMER API5 ROUTE STILL HOLDS: <c>ksFilletDefinition.array()</c> → <c>Clear()</c> → <c>Add()</c> → <c>entity.Update()</c> does
NOT edit the set on an existing feature (MEASURED on 16.09.2026 by eight probes; <c>edges_read_back=0</c>, the volume returns to the plate). BOUND: set
EXPANSION is not covered - <c>FL25</c> expansion (1→2, 2→3) ends in a refusal AFTER mutation (<c>GEOMETRY_FAILED</c>, <c>partial_effects=true</c>). History: docs/decisions/contracts.md#fillet-edge-set-history</remarks>


<summary>New SET of fillet edges (family=<c>fillet</c>) - a full replacement: what must remain is
what is passed. The edges come from <c>kompas_read_topology</c>, not from positions in a
collection.</summary>
<remarks>DOC: the route is API7 - <c>IModelContainer.Fillets[i] → IFillet</c>, read/write of
<c>IFillet.BaseObjects</c> (full replacement), then the mandatory <c>IFillet.Update()</c>;
objects are taken from the LIVE model, and addressing was checked with TWO fillets of the same
radius. INVARIANT: the negative result on the former API5 route still holds -
<c>ksFilletDefinition.array()</c> → <c>Clear()</c> → <c>Add()</c> → <c>entity.Update()</c> does
NOT edit the set on an existing feature (<c>edges_read_back=0</c>, the volume returns to the
plate). BOUND: set EXPANSION is not covered - expansion ends in a refusal AFTER mutation
(<c>GEOMETRY_FAILED</c>, <c>partial_effects=true</c>).
History: docs/decisions/contracts.md#fillet-edge-set-history</remarks>

## <a id="assembly-domain-contracts"></a>Контракты домена сборок вынесены в AssemblyCommands.cs (наряд C1)

**Что было.** В `WorkerCommands.cs` были объявлены заготовки `ListComponentsCommand` (с
`IncludeSuppressed`/`IncludeHidden`), `SetComponentTransformCommand` (с `CoordinateSpace`) и
`CheckIntersectionsCommand` - от первоначальной спеки.

**Что измерено.** Ни одним инструментом они не использовались: их поля не совпадают с составом блока
C1 (подавления компонентов и контроля пересечений в нём нет).

**Что решено.** Они удалены как нереализованная заготовка, а не как требование: знаменатель профиля от
этого не меняется, счётчик закрытого не растёт. Действующие контракты домена сборок живут в
`AssemblyCommands.cs`.

## <a id="rotation-route"></a>Вращение: маршрут целиком в API7 (шаги R.24…R.26, 17.09.2026)

**Что измерено.** Маршрут измерен 17.09.2026 (шаги R.24…R.26, прогон `95fa8441`) и лежит ЦЕЛИКОМ в
API7: `IModelContainer.Rotateds.Add(type)` → `QI(IRotated)` → запись параметров → `Update()`.
Оболочка API5 (`NewEntity` + `Create()`) на этом объекте НЕ работает - это измерено и было причиной
многомесячной блокировки, а не свойство вращения.


<summary>Rotation (docs/05 SM-03). Route MEASURED on 17.09.2026 (steps R.24…R.26, run
<c>95fa8441</c>) and lies ENTIRELY in API7: <c>IModelContainer.Rotateds.Add(type)</c> →
<c>QI(IRotated)</c> → write parameters → <c>Update()</c>. The API5 wrapper (<c>NewEntity</c> +
<c>Create()</c>) does not work on this object - MEASURED, not a property of rotation.
History: docs/decisions/contracts.md#rotation-route</summary>

## <a id="rotation-operation-result"></a>Вращение: вид операции выбирает исход (18.09.2026)

**Что было.** Утверждалось, что член `OperationResult` «не переключает действие».

**Что измерено.** 18.09.2026: `OperationResult` ВЫБИРАЕТ исход, а вид фабрики
(`Rotateds.Add(27/28/29)`) объявляет, какие значения для него допустимы. Управляемый опыт на одной
геометрии: `ksOperationUnion` даёт сращивание (тел 1→1), `ksOperationNewBody` - второе тело (тел
1→2). Прежнее утверждение происходило из опыта, который писал пару, недопустимую для своего вида, и
потому измерял отказ.


<summary>Rotation (docs/05 SM-03). The operation kind is stated explicitly and in advance: MEASURED on
18.09.2026 that the factory kind (<c>Rotateds.Add(27/28/29)</c>) declares which <c>OperationResult</c>
values are admissible for it. Controlled experiment on one geometry: <c>ksOperationUnion</c> fuses
(bodies 1→1), <c>ksOperationNewBody</c> makes a second body (1→2).
History: docs/decisions/contracts.md#rotation-operation-result</summary>

## <a id="rotation-angle-limit"></a>Вращение: угол в градусах и потолок сектора (18.09.2026)

**Что было.** Утверждалось, что «угол насыщается на 180°, запись 360 даёт половину».

**Что измерено.** 18.09.2026 (проба `FullTurnProbe`, шаги F.1…F.5; независимое чтение `.m3d` пробой
`M3dVerificationProbe`; разбор - `docs/acceptance/api7/full-turn-findings.md`): `Angle[true]` несёт
запрошенный угол напрямую (90→90°, 180→180°, 360→360°), а вторая половина пары, равная первой,
развёртку удваивает. Полный оборот достигается одним вызовом с `angle_deg = 360`, а отказ начинается
только выше 360 - это потолок сектора, а не прежний неверный предел. Прежнее утверждение
ОПРОВЕРГНУТО измерением.

**Ось обязательна и строится в ТОЙ ЖЕ детали.** Измерено (R.24/R.25): вращение, записанное без оси,
не строится вовсе - `Update()` возвращает False и тел остаётся 0. Ось подаётся двумя точками в
координатах МОДЕЛИ: сервер строит её сам как `o3d_axis2Points` в этой же детали, потому что
`IRotated.Axis` принимает модельный объект, а не линию эскиза, и чужая ось из другого документа не
подставляется - ссылки между документами не переносятся.

**Тонкая стенка не проверялась.** Маршрут измерен на сплошном теле (`IThinParameters.Thin = false`).


<remarks>The axis is mandatory and is built in the SAME part. MEASURED (R.24/R.25): a rotation written without an axis
is not built at all - <c>Update()</c> returns False and 0 bodies remain. The axis is given by two points in MODEL
coordinates, built by the server as <c>o3d_axis2Points</c>, because <c>IRotated.Axis</c> accepts a model object, not a
sketch line, and a foreign axis is not substituted. The angle is in DEGREES and equals the built one up to a full turn:
<c>Angle[true]</c> carries the requested angle directly (90→90°, 180→180°, 360→360°), a full turn is reached with one call
at <c>angle_deg = 360</c>, and refusal starts only above 360 (probe <c>FullTurnProbe</c>, steps F.1…F.5; <c>.m3d</c> read-back
by <c>M3dVerificationProbe</c>; analysis <c>docs/acceptance/api7/full-turn-findings.md</c>). Thin wall was not tested.
History: docs/decisions/contracts.md#rotation-angle-limit</remarks>


<summary>Angle in DEGREES, strictly greater than 0 and not more than 360. A full turn (360) is
built with one call: MEASURED on 18.09.2026 that the write yields a full cylinder (probe F.1a:
<c>V=50265.4824574366</c> at r=20, h=40), confirmed by a blind read of the saved <c>.m3d</c>. A
value above 360 is rejected before mutating: a sector cannot exceed a full turn.
History: docs/decisions/contracts.md#rotation-angle-limit</summary>


<remarks><see cref="AngleDeg"/> is what lies in the model as the first slot of the pair
<c>Angle[true]</c>, and it equals the requested angle up to a full turn (MEASURED on 18.09.2026,
probe F.1a and a blind file read).
History: docs/decisions/contracts.md#rotation-angle-limit</remarks>

## <a id="fillet-base-object-references"></a>Скругление: входы признака и валюта правки набора

**Что было.** `BaseObjectReferences` объявлялось «ЕДИНСТВЕННЫМ источником, по которому признак
адресуется в правке набора (`edge_refs`)». Неверно, и проверить это можно не выходя из кода:
`edge_refs` принимает строки реестра ссылок вида `edge:<hex>`, а здесь лежат ЧИСЛА
`IModelObject.Reference` - у них строки реестра нет, и подставить их в `edge_refs` нельзя по типу,
независимо от контекста.

**Что измерено (17.09.2026).** После скругления всех углов у тела 24 ребра: 16 линий и 8 дуг длины
π·3/2 (четверть окружности R3). Все восемь «вертикальных» линий лежат на швах касания цилиндров
`(±47,±40)` и `(±50,±37)`; угловых вертикальных рёбер в топологии НОЛЬ (`len(corner_edges) = 0`) -
их отозвало само скругление.

**Главное: входы признака и рёбра тела НЕ взаимозаменяемы как валюта записи.** Проба H-2 сокращала
набор (4→3, 4→2) объектами, прочитанными ИЗ `BaseObjects`, - ни одно ребро тела при этом не
искалось и не переносилось. Замер 17.09.2026 (FL10): предъявление трёх рёбер тела (дуг) вместо
собственных входов признака СХЛОПЫВАЕТ признак - объём возвращается к пластине
(`79999.99999999999`), `level=call_returned`. Замена при неизменном размере (1→1), наоборот, работает
перенесёнными рёбрами (FL10x, `level=geometry_checked`). Отсюда: СОКРАЩЕНИЕ набора и ЗАМЕНА состава -
разные операции с разной валютой, и одним полем `edge_refs` выражается только вторая.

**Что решено.** Введены `BaseObjectInputRefs` - ссылки реестра `input:<hex>` на СОБСТВЕННЫЕ входы
признака. Это и есть валюта сокращения, которой не хватало. Правило одно для всех ссылок сервера:
клиент не сочиняет и не переносит идентификаторы, а подставляет выданные. Ссылки минтит чтение
признака на текущую ревизию документа, поэтому они стареют так же, как `edge:`-ссылки.

Here are registry strings minted by THIS server, so `UpdateFeatureCommand.BaseObjectRefs` accepts them.
An empty field means "not read" (the API7 bridge was not built or there are several fillets with the
same radius); an empty list - the feature holds no input. These are different states, as with
`BaseObjectReferences`.

That is, `EdgeRefs` expresses replacement but NOT shrinkage. Separately: an early acceptance presented
BODY EDGES to this very field and collapsed the feature into the plate - presenting them here is still
forbidden, they are rejected by the reference kind. The set from `base_object_references` is the only
measured currency of shrinkage. This is the same format the server already returns references in, so
the client substitutes the numbers as they are. The set is a FULL replacement, not an addition: what
must remain is what is passed. An empty list is rejected (`INVALID_ARGUMENT`).

It cannot be combined with `EdgeRefs` in one call (`INVALID_ARGUMENT` before mutation): two different
compositions in one request are indistinguishable in the response, and "one of the two applied" would
look like "both applied". An unknown or foreign `input:` reference is rejected before mutation. A
reference issued to another document is rejected as `STALE_REFERENCE` (`FL26`), inputs of a foreign
feature of the same document - as `CAPABILITY_UNAVAILABLE` (`FL27`); in both cases the model does not
change.

BOUND: set EXPANSION is NOT expressed by this field. A new edge is not a feature's own input, and the
currencies must not be mixed. MEASURED by `FL25` (L-shaped plate with free corners): expansion (1→2,
2→3) ends in a refusal AFTER mutation with `partial_effects=true`. Before 17.09.2026 the same outcome
was returned as success with `level=call_returned` - that was a wrong message and it is fixed: the
disappearance of the feature is now a refusal, not "success at a reduced level".


<summary>New set by the feature's OWN inputs (family=<c>fillet</c>) - the second currency of the same edit subject, added on 17.09.2026.</summary>
<remarks>Shrinking a set and replacing its composition are different operations with different currencies (MEASURED, not inferred): shrinkage is expressed
only by objects read FROM the feature's <c>BaseObjects</c>, while <see cref="EdgeRefs"/> takes BODY edges. Acceptance confirms both separately: shrinkage
<c>FL10</c> (4→3), <c>FL10s</c>, <c>FL10b</c> - by this field; replacement at an unchanged size <c>FL10x</c> (1→1) - by <see cref="EdgeRefs"/>, all at
<c>level=geometry_checked</c>. A feature's own input is an <c>IModelObject</c> from <c>IFillet.BaseObjects</c>, addressed by <c>IModelObject.Reference</c>
(the same number in <c>fillet.base_object_references</c> from <c>kompas_get_feature</c>); it has NO registry <c>edge:&lt;hex&gt;</c> string, so a number cannot
be substituted into <see cref="EdgeRefs"/> by type. The form is <c>input:&lt;hex Reference&gt;</c> (<c>1073742308</c> → <c>input:40000164</c>), a FULL
replacement; it cannot be combined with <see cref="EdgeRefs"/> in one call. BOUND: expansion is NOT expressed here (<c>FL25</c>: 1→2, 2→3 → refusal). History: docs/decisions/contracts.md#fillet-base-object-references</remarks>


<summary>Fillet parameters. The radius is read from API7 (<c>IFillet.Radius1</c>): API5 <c>ksFilletDefinition.radius</c> is readable but
NOT applied when written to an existing feature (MEASURED by row FL04r), so the live model is authoritative; empty = "not read".</summary>
<param name="BaseObjectReferences">Feature input references - <c>IModelObject.Reference</c> of each element of <c>IFillet.BaseObjects</c>;
an observable of the composition, NOT the address for editing the set (<c>edge_refs</c> takes registry <c>edge:&lt;hex&gt;</c> strings,
here lie NUMBERS with none). History: docs/decisions/contracts.md#fillet-base-object-references</param>
<param name="BaseObjectInputRefs">Registry references to the feature's OWN inputs - <c>input:&lt;hex&gt;</c>, one per element of
<c>IFillet.BaseObjects</c>, in the same order as <paramref name="BaseObjectReferences"/>; the REDUCTION currency that was missing.
They age like <c>edge:</c> references (minted by <c>kompas_get_feature</c> against the current revision): after a mutation <c>REVISION_CONFLICT</c> or <c>STALE_REFERENCE</c> forces a context re-read; <c>null</c> - not read, empty list - no input.</param>


<summary>Fillet parameters. The radius is read from API7 (<c>IFillet.Radius1</c>): API5
<c>ksFilletDefinition.radius</c> is readable but NOT applied when written to an existing feature
(MEASURED by row FL04r), so the live model is authoritative; empty = "not read".</summary>
<param name="BaseObjectReferences">Feature input references - <c>IModelObject.Reference</c> of each
element of <c>IFillet.BaseObjects</c>; an observable of the composition, NOT the address for editing
the set (<c>edge_refs</c> takes registry <c>edge:&lt;hex&gt;</c> strings, here lie NUMBERS with none).
History: docs/decisions/contracts.md#fillet-base-object-references</param>
<param name="BaseObjectInputRefs">Registry references to the feature's OWN inputs -
<c>input:&lt;hex&gt;</c>, one per element of <c>IFillet.BaseObjects</c>, in the same order as
<paramref name="BaseObjectReferences"/>; the REDUCTION currency that was missing. They age like
<c>edge:</c> references (minted by <c>kompas_get_feature</c> against the current revision): after a
mutation <c>REVISION_CONFLICT</c> or <c>STALE_REFERENCE</c> forces a context re-read; <c>null</c> -
not read, empty list - no input.</param>


<summary>New set by the feature's OWN inputs (family=<c>fillet</c>) - the second currency of the
same edit subject.</summary>
<remarks>MEASURED: shrinking a set and replacing its composition are different operations with
different currencies - shrinkage is expressed only by objects read FROM the feature's
<c>BaseObjects</c>, while <see cref="EdgeRefs"/> takes BODY edges. A feature's own input is an
<c>IModelObject</c> from <c>IFillet.BaseObjects</c>, addressed by <c>IModelObject.Reference</c>
(the same number in <c>fillet.base_object_references</c> from <c>kompas_get_feature</c>); it has
NO registry <c>edge:&lt;hex&gt;</c> string, so a number cannot be substituted into
<see cref="EdgeRefs"/> by type. The form is <c>input:&lt;hex Reference&gt;</c>, a FULL replacement;
it cannot be combined with <see cref="EdgeRefs"/> in one call. BOUND: expansion is NOT expressed
here (expansion ends in a refusal).
History: docs/decisions/contracts.md#fillet-base-object-references</remarks>

## <a id="reposition-position-member"></a>Перенос тела: член `Position` значение не хранит (18.09.2026)

**Что было.** Первая редакция правки сверяла записанный перенос с `IBodyReposition.Position.X/Y/Z`.

**Что измерено.** 18.09.2026: этот член перенос НЕ несёт - после `InitByMatrix3D` с вектором
`(7,−11,13)` он читается как `(0,0,0)`, тогда как габарит тела верен. Сверять с членом, который не
хранит значение, значит измерять прибор, а не продукт.

**Что решено.** Проверка перенесена на ГЕОМЕТРИЮ: при переносе «сдвинулось» и «осталось» по объёму
неразличимы, габарит различает. Это же требование записано в приёмке (строка `B3L.04`: у переноса
объём до и после равен 1 000, и строка, сверяющая одни объёмы, прошла бы на полном бездействии).


<summary>Analytic expectation of the body bbox after the edit.</summary>
<remarks>Needed where volume cannot serve as the expectation. Under a rigid transformation the volume is an INVARIANT, so
<see cref="ExpectedVolumeMm3"/> for the <c>reposition</c> family confirms only that the transformation stayed rigid, not that the body went
where it was asked: for a translation "moved" and "stayed" are indistinguishable by volume. The bbox distinguishes, and the same requirement
is written into acceptance (row `B3L.04`: for a translation the volume before and after is 1 000, and a row checking only volumes would pass
on complete inaction). Not by reading the parameter back: the first revision compared the written translation against
<c>IBodyReposition.Position.X/Y/Z</c>. MEASURED on 18.09.2026: that member does NOT carry the translation - after <c>InitByMatrix3D</c> with
vector <c>(7,−11,13)</c> it reads as <c>(0,0,0)</c>, while the bbox is correct - so the check was moved to geometry. History: docs/decisions/contracts.md#reposition-position-member</remarks>

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
`6 000 / 18 000` прежние). Авторитетная ссылка - SP.9 с отрицательным контролем E-B; SP.6 остаётся
верным как факт о плоскости, но не как доказательство правки признака.

**Маршрут - перенос ТОЧЕК СОБСТВЕННОЙ опоры признака, и это измерено, а не выведено.** Шаг `SP.9`
пробы `--split` (прогон `c9cd7660468c44aa97b410e253ee2cb1`) сравнил два маршрута на одном и том же
признаке. `CutObjects` читается обратно как ОДИН объект (не массив) и отвечает `IPlane3DBy3Points`;
перенос его трёх точек построения с последующим `Update()` и пересборкой меняет геометрию (E-A: части
`9 000 / 15 000`, признаков разделения `1 → 1`). Подстановка ВТОРОЙ, заново построенной плоскости в
`CutObjects` при `Update() = true` результат НЕ меняет - отрицательный контроль E-B. Поэтому
реализация правит опору признака, а не подставляет другую плоскость, и `Update() = true` здесь
доказательством не считается: геометрия сверяется отдельно.

**Цена маршрута и границы.** Вспомогательная плоскость при правке НЕ создаётся: объект опоры берётся
у самого признака, поэтому документ не накапливает неиспользованные плоскости. `plane_ref` на
признаке разделения/отсечения отвергается кодом `CAPABILITY_UNAVAILABLE`: подстановка чужой плоскости
измеренно ничего не делает (E-B).


<summary>New support of a SPLIT feature (<c>family=split</c>) or CUT feature (<c>family=cut_by_plane</c>).</summary>
<remarks>Why a plane rather than a repeated call: order B3 §5 requires an edit to change the parameters of an EXISTING feature relative to its ORIGINAL
inputs. A repeated <c>kompas_split_body</c> creates a SECOND split feature and cuts the already obtained part. MEASURED on 18.09.2026 (probe <c>--split</c>,
step SP.9): editing the support moves the parts <c>6 000 / 18 000</c> at <c>x = 10</c> to <c>9 000 / 15 000</c> at <c>x = 15</c> with an unchanged feature
count <c>1 → 1</c> and sum <c>24 000</c>. The shape is the same as the like-named field of <c>kompas_split_body</c> and <c>kompas_cut_body</c> (<c>#/$defs/cut_plane</c>):
<c>plane_ref</c> or <c>point_mm</c> + <c>normal_mm</c> in model coordinates; the side is <c>s = n·(p − p₀)</c>, and a zero or non-numeric normal is rejected before COM.
The route moves the CONSTRUCTION POINTS of the feature's OWN support (step SP.9, run <c>c9cd7660468c44aa97b410e253ee2cb1</c>): <c>CutObjects</c> reads back as ONE
<c>IPlane3DBy3Points</c>, moving its points + <c>Update()</c> changes the geometry (E-A); substituting a SECOND plane does NOT (E-B); <c>plane_ref</c> is rejected <c>CAPABILITY_UNAVAILABLE</c> here, and no auxiliary plane is created. History: docs/decisions/contracts.md#split-support-route</remarks>


<summary>New support of a SPLIT feature (<c>family=split</c>) or CUT feature
(<c>family=cut_by_plane</c>).</summary>
<remarks>Why a plane rather than a repeated call: order B3 §5 requires an edit to change the
parameters of an EXISTING feature relative to its ORIGINAL inputs - a repeated
<c>kompas_split_body</c> creates a SECOND split feature and cuts the already obtained part.
The shape is the same as the like-named field of <c>kompas_split_body</c> and
<c>kompas_cut_body</c> (<c>#/$defs/cut_plane</c>): <c>plane_ref</c> or <c>point_mm</c> +
<c>normal_mm</c> in model coordinates; the side is <c>s = n·(p − p₀)</c>, and a zero or
non-numeric normal is rejected before COM.
MEASURED: the route moves the CONSTRUCTION POINTS of the feature's OWN support - <c>CutObjects</c>
reads back as ONE <c>IPlane3DBy3Points</c>, moving its points + <c>Update()</c> changes the
geometry (E-A); substituting a SECOND plane does NOT (E-B); <c>plane_ref</c> is rejected
<c>CAPABILITY_UNAVAILABLE</c> here, and no auxiliary plane is created.
History: docs/decisions/contracts.md#split-support-route</remarks>


<summary>New support of a SPLIT (<c>family=split</c>) or CUT (<c>family=cut_by_plane</c>) feature.</summary>
<remarks>Why a plane rather than a repeated call: an edit must change the parameters of an
EXISTING feature relative to its ORIGINAL inputs - a repeated <c>kompas_split_body</c> creates a
SECOND split feature and cuts the already obtained part. The shape is the same as the like-named
field of <c>kompas_split_body</c>/<c>kompas_cut_body</c>: <c>plane_ref</c> or <c>point_mm</c> +
<c>normal_mm</c>; the side is <c>s = n·(p − p₀)</c>.
MEASURED: the route moves the CONSTRUCTION POINTS of the feature's OWN support; substituting a
SECOND plane does NOT, and <c>plane_ref</c> is rejected <c>CAPABILITY_UNAVAILABLE</c>.
History: docs/decisions/contracts.md#split-support-route</remarks>

## <a id="json-schema-additional-properties-default"></a>Валидатор: «additionalProperties: false по умолчанию» - соглашение, не правило (18.09.2026)

The first revision applied the convention as a rule and rejected a CORRECT call -
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


<summary>New parameters of an EXISTING pattern feature (edit per docs/05 §4.3: not "delete and create a similar one"); passed in the <c>pattern</c> field of <c>kompas_update_feature</c>.</summary>
<remarks>A pattern has no depth, no radius, no sketch: its members belong to THREE API7 interfaces (<c>ILinearPattern</c>,
<c>ICircularPattern</c>, <c>IMirrorPattern</c>), and some names coincide only in appearance - <c>save_initial_orientation</c>
exists only on circular, <c>save_initial_objects</c> only on mirror, <c>step2_deg</c> only on circular. A flat set of fields
on <see cref="UpdateFeatureCommand"/> would read as "all this applies to any pattern". Axes and plane (<c>Axis1/Axis2</c>,
<c>Plane</c>) are non-editable: they accept <c>IModelObject</c>, no <c>feature:</c> reference reaches them, and no B4 run
measured changing the support of an existing pattern; <c>Vector1/Vector2</c> are absent (OQ-B-03). An edit must carry ALL mode
parameters that should remain - members not set stay as they are, the only way to tell "exactly the requested thing changed" from "this changed too". History: docs/decisions/contracts.md#pattern-edit-members</remarks>


<summary>New parameters of an EXISTING pattern feature (edit per docs/05 §4.3: not "delete and
create a similar one"); passed in the <c>pattern</c> field of <c>kompas_update_feature</c>.</summary>
<remarks>A pattern has no depth, no radius, no sketch: its members belong to THREE API7 interfaces
(<c>ILinearPattern</c>, <c>ICircularPattern</c>, <c>IMirrorPattern</c>), and some names coincide only
in appearance. A flat set of fields on <see cref="UpdateFeatureCommand"/> would read as "all this
applies to any pattern". Axes and plane are non-editable: they accept <c>IModelObject</c>, no
<c>feature:</c> reference reaches them, and no run measured changing the support of an existing
pattern; <c>Vector1/Vector2</c> are absent (OQ-B-03). An edit must carry ALL mode parameters that
should remain - the only way to tell "exactly the requested thing changed" from "this changed too".
History: docs/decisions/contracts.md#pattern-edit-members</remarks>

## <a id="pattern-edit-routing"></a>PatternEditDto: маршрут правки признака массива (queue B4)

Grouping makes the family boundary visible in the contract itself, not only in the documentation. For
the other editable families the branch is chosen by `entity.type`, and that is a measured number.


<summary>New parameters of a PATTERN feature (family=<c>pattern</c>, queue B4: SM-18 / SM-19 / SM-23).</summary>
<remarks>Why a separate field rather than flat command fields: the <c>pattern</c> family is the only one whose edit subject belongs to THREE API7 interfaces at
once, and some names coincide only in appearance - a flat <c>count2</c> would read as "applicable to any pattern" (for a grid it means instances along axis 2,
for a circular pattern along the RING), and a flat <c>save_initial_orientation</c> would look applicable to a grid where no such member exists. The feature is
identified BY THE FIELD ITSELF, not by the tree type number: the pattern type number was not measured in this session, so the feature is matched to an element
of <c>IModelContainer.FeaturePatterns</c> by the same instrument as reading (<see cref="PatternReadCommand"/>) - tree wrapper name and update stamp; if the
field is not set, no branch is chosen and the edit behaviour does not change. Mixing with other families (depth, radius, plane, boolean operation kind) is
rejected before mutation: "one of the two applied" is later indistinguishable from "both applied". History: docs/decisions/contracts.md#pattern-edit-routing</remarks>


<summary>New parameters of a PATTERN feature (family=<c>pattern</c>, queue B4: SM-18 / SM-19 /
SM-23).</summary>
<remarks>Why a separate field rather than flat command fields: the <c>pattern</c> family is the
only one whose edit subject belongs to THREE API7 interfaces at once, and some names coincide
only in appearance - a flat <c>count2</c> would read as "applicable to any pattern", and a flat
<c>save_initial_orientation</c> would look applicable to a grid where no such member exists. The
feature is identified BY THE FIELD ITSELF, not by the tree type number: it is matched to an
element of <c>IModelContainer.FeaturePatterns</c> by the same instrument as reading
(<see cref="PatternReadCommand"/>) - tree wrapper name and update stamp. If the field is not set,
no branch is chosen. Mixing with other families (depth, radius, plane, boolean operation kind) is
rejected before mutation: "one of the two applied" is indistinguishable from "both applied".
History: docs/decisions/contracts.md#pattern-edit-routing</remarks>


<summary>New parameters of a PATTERN feature (family=<c>pattern</c>, queue B4: SM-18 / SM-19 /
SM-23).</summary>
<remarks>Why a separate field rather than flat command fields: the <c>pattern</c> family is the
only one whose edit subject belongs to THREE API7 interfaces at once, and some names coincide
only in appearance - a flat <c>count2</c> would read as "applicable to any pattern". The feature
is identified BY THE FIELD ITSELF, not by the tree type number: it is matched to an element of
<c>IModelContainer.FeaturePatterns</c> by the same instrument as reading
(<see cref="PatternReadCommand"/>). If the field is not set, no branch is chosen; mixing with
other families is rejected. History: docs/decisions/contracts.md#pattern-edit-routing</remarks>

## <a id="hole-edit-address"></a>Правка отверстия: адрес признака не угадывается (шаг M.6, 20.09.2026)

A feature name is not an identifier: it does not survive the API5↔API7 transition (MEASURED on the
chamfer, F.8). A mode change was not measured.


<summary>New hole diameter (family=<c>hole</c>), mm: the pilot of a counterbore and countersink, the hole itself for a blind and a through cylindrical hole.</summary>
<remarks>Route MEASURED on 20.09.2026 (probe <c>scratch/_hole_edit_probe.py</c>, leg 2 - raw helper <c>scratch/hole-edit-raw</c>, report
<c>docs/acceptance/api7/hole-modes.md</c> § M.6): the existing hole is taken by <c>IHoles3D.Hole3D[index]</c>, its own mode's members are written, then
<c>IModelObject.Update()</c> and a rebuild; a through cylindrical Ø10 → Ø12 on a 10 mm plate removed <c>345.575191895</c> mm³ = π·(36−25)·10, surviving
<c>save → close → reopen</c>. The address is not guessed: the "tree feature ↔ <c>Holes3D</c> record" correspondence is proved by the uniqueness of the hole;
with several holes the call is rejected <c>CAPABILITY_UNAVAILABLE</c> before COM (writing to <c>Holes3D[0]</c> would change someone else's hole), and a
feature name is not an identifier (does not survive the API5↔API7 transition - MEASURED on the chamfer, F.8). The mode is NOT changed by an edit: it is read
(<c>IHole3D.HoleType</c>) and serves as the frame - a foreign-mode field is rejected <c>INVALID_ARGUMENT</c> before COM. A mode change was not measured. History: docs/decisions/contracts.md#hole-edit-address</remarks>


<summary>New hole diameter (family=<c>hole</c>), mm: the pilot of a counterbore and countersink,
the hole itself for a blind and a through cylindrical hole.</summary>
<remarks>DOC: the existing hole is taken by <c>IHoles3D.Hole3D[index]</c>, its own mode's members
are written, then <c>IModelObject.Update()</c> and a rebuild.
INVARIANT: the address is not guessed - the "tree feature ↔ <c>Holes3D</c> record"
correspondence is proved by the uniqueness of the hole; with several holes the call is rejected
<c>CAPABILITY_UNAVAILABLE</c> before COM (writing to <c>Holes3D[0]</c> would change someone
else's hole), and a feature name is not an identifier (does not survive the API5↔API7
transition). The mode is NOT changed by an edit: it is read (<c>IHole3D.HoleType</c>) and serves
as the frame - a foreign-mode field is rejected <c>INVALID_ARGUMENT</c> before COM. A mode change
was not measured. History: docs/decisions/contracts.md#hole-edit-address</remarks>


<summary>New hole diameter (family=<c>hole</c>), mm: the pilot of a counterbore and countersink,
the hole itself for a blind and a through cylindrical hole.</summary>
<remarks>DOC: the existing hole is taken by <c>IHoles3D.Hole3D[index]</c>, its own mode's members
are written, then <c>IModelObject.Update()</c> and a rebuild. INVARIANT: the address is not
guessed - the "tree feature ↔ <c>Holes3D</c> record" correspondence is proved by the uniqueness
of the hole; with several holes the call is rejected <c>CAPABILITY_UNAVAILABLE</c> before COM.
The mode is NOT changed by an edit: it is read (<c>IHole3D.HoleType</c>) and serves as the frame -
a foreign-mode field is rejected <c>INVALID_ARGUMENT</c> before COM.
History: docs/decisions/contracts.md#hole-edit-address</remarks>

## <a id="solid-feature-reposition-read"></a>SolidFeatureDto: чтение параметров переноса (шаг RP.25)

The route was MEASURED at step RP.25 of probe `--reposition-params` (run
`a336120926fc4652a8bf737562568271`): both the triple of angles and the displacement are read from a
REOPENED document BEFORE assembly and BEFORE any write.

A feature written by a matrix is not readable - and this is a refusal, not zeros. Such a feature is
recognized by the READ `OrientationType = 0 (ksAxisOrientation)`: it has no orientation parameters,
and the matrix form of placement on a reopened document is unit while the geometry is preserved, so
the kind cannot be derived from it - this is exactly what produced a false `RepositionKind = "translate"`
for a written rotation. Existing features are not silently converted: all five fields are named
unreadable with this reason.


<summary>Parameters of a B3 feature (boolean, split, cut, reposition), read FROM THE MODEL - the <c>read</c> action of order §7.</summary>
<remarks>Only fields of its own family are filled; for other families they are <c>null</c>. An empty field means "not read", not zero,
and the reason goes into <see cref="UnreadableParameters"/>. Of the five reposition fields ONE is not published -
<see cref="RepositionAxisPointMm"/>: an axis point has no documented member and is not a placement property (a rotation about any point of
one line gives the same placement), so it is a REPRESENTATIVE, always <c>null</c>, named "not readable" for a rotation / "not applicable"
for a translation. The other four are read by the documented parametric route: <c>OrientationType = ksEulerCorners</c> +
<c>LocalCSParameters → ILocalCSEulerParam</c>, and <c>ParameterType = ksPDisplace</c> + <c>Parameters → IPoint3DParamDisplace</c>.
History: docs/decisions/contracts.md#solid-feature-reposition-read</remarks>


<summary>Parameters of a B3 feature (boolean, split, cut, reposition), read FROM THE MODEL - the
<c>read</c> action of order §7.</summary>
<remarks>Only fields of its own family are filled; for other families they are <c>null</c>. An
empty field means "not read", not zero, and the reason goes into
<see cref="UnreadableParameters"/>. Of the five reposition fields ONE is not published -
<see cref="RepositionAxisPointMm"/>: an axis point has no documented member and is not a placement
property, so it is a REPRESENTATIVE, always <c>null</c>, named "not readable" for a rotation /
"not applicable" for a translation. The other four are read by the documented parametric route:
<c>OrientationType = ksEulerCorners</c> + <c>LocalCSParameters → ILocalCSEulerParam</c>, and
<c>ParameterType = ksPDisplace</c> + <c>Parameters → IPoint3DParamDisplace</c>.
History: docs/decisions/contracts.md#solid-feature-reposition-read</remarks>

## <a id="boolean-edit-kind"></a>Правка вида булевой операции (шаг BO.11, 18.09.2026)

Reference §6.1: A ∪ B - 36 000 in bbox (0,0,0)…(60,30,20), A − B - 12 000 in x ≤ 20, A ∩ B -
12 000 in x ∈ [20,40]. Experiment E-E confirms that it is the pair "write → Update()" that applies:
a write without Update() (but with a rebuild) does not change the geometry. If another family ever
needs its own "operation kind", it MUST take a QUALIFIED name (as the rotation angle did -
RepositionAngleDeg) rather than introduce a second Operation field.


<summary>New KIND of an existing boolean operation (family=<c>boolean</c>): <c>union</c>, <c>difference</c> or <c>intersect</c>.</summary>
<remarks>Route MEASURED on 18.09.2026 (probe <c>--boolean</c>, step <c>BO.11</c>, run <c>a2f5cf0a2ad342c59c36807101a65d51</c>, log
<c>docs/acceptance/api7/boolean-ops.json</c>): re-writing <c>IBoolean.BooleanType</c> on an EXISTING feature followed by <c>Update()</c> and a rebuild
changes the geometry (E-A: <c>36 000 → 12 000</c>, bbox <c>x ≤ 20</c>; E-D: <c>12 000 → 36 000</c>, features <c>1 → 1</c>). The control is mandatory: on
the reference the difference and intersection volumes are EQUAL (12 000), so E-C goes difference → intersection - the volume stays 12 000 while the bbox
changes to <c>x ∈ [20,40]</c>. Support bodies are NOT changed (<c>BaseObject</c> and <c>ModifyObjects</c> are writable but editing the tool set was not
measured); <c>ksBooleanUnknown</c> (0) is normalized to <c>ksUnion</c> (E-B); the field name coincides with the <c>operation</c> parameter of <c>kompas_boolean</c>.
History: docs/decisions/contracts.md#boolean-edit-kind</remarks>


<summary>New KIND of an existing boolean operation (family=<c>boolean</c>): <c>union</c>,
<c>difference</c> or <c>intersect</c>.</summary>
<remarks>MEASURED: re-writing <c>IBoolean.BooleanType</c> on an EXISTING feature followed by
<c>Update()</c> and a rebuild changes the geometry. The control is mandatory: on the reference
the difference and intersection volumes are EQUAL, so the distinguishing case compares the bbox
while the volume stays. Support bodies are NOT changed (<c>BaseObject</c> and
<c>ModifyObjects</c> are writable but editing the tool set was not measured);
<c>ksBooleanUnknown</c> (0) is normalized to <c>ksUnion</c>; the field name coincides with the
<c>operation</c> parameter of <c>kompas_boolean</c>.
History: docs/decisions/contracts.md#boolean-edit-kind</remarks>

## <a id="sketch-plane-command"></a>Команда смены опорной плоскости эскиза

<summary>Changes the SUPPORT plane of an EXISTING sketch through the documented
<c>ksSketchDefinition.SetPlane</c> («Изменить базовую плоскость эскиза»,
<c>kssketchdefinition_setplane.html</c>), then a mandatory <c>sketch.Update()</c>.</summary>
<remarks>The SECOND half of the <c>edit</c> action of row
<c>AUX-SKETCH.plane_and_profile_lifecycle</c>: the first half (profile) is expressed by
<c>kompas_edit_sketch</c>, while the support had no expression at all before. MEASURED by probe
<c>tools/KompasMcp.Api7Probe --sketch-plane</c> (report
<c>docs/acceptance/image/sketch-plane-probe-report.md</c>). A separate command, not a field on
another tool: extending <c>kompas_edit_sketch</c> with a <c>plane</c> field measured as
blurring (301 foreign rows for zero rows of its own).</remarks>


<summary>Request to change the support plane of an existing sketch (command <see cref="WorkerCommands.SetSketchPlane"/>).</summary>
<remarks>The shape of <see cref="Plane"/> is THE SAME as for sketch creation: <c>base</c>+<c>offset_mm</c> OR <c>reference</c>,
exactly one at a time. No second dialect of support is introduced: two shapes of one concept diverge, and the divergence looks
like a difference in product behaviour.
A non-plane refusal happens BEFORE COM and is named by a code. MEASURED (probe <c>--sketch-plane</c>, steps SP.8/SP.9): the core
ACCEPTS a flat face as support - all six faces of a box rebind the dependent body - and REJECTS an edge and a body
(<c>SetPlane = False</c>). The product does not inherit this answer: a <c>reference</c> not leading to a plane is rejected by the
KIND of the reference from the registry, without touching COM.</remarks>

## <a id="chamfer-route"></a>Маршрут фаски

<summary>Chamfer by explicit edge references (docs/05 SM-11). MEASURED by probe F on
12.09.2026: in API5 this is <c>NewEntity(o3d_chamfer=33)</c> +
<c>ksChamferDefinition.SetChamferParam</c>; the «расстояние и угол» mode is API7 only
(<c>IChamfer.Angle</c>).</summary>

## <a id="hole-route"></a>Маршрут отверстия

<summary>Native hole (docs/05 SM-07). Modes MEASURED by probe M on 16.09.2026 and reachable only
through API7 of the same session: mode parameters live not on <c>IHole3D</c> but on
<c>HoleParameters</c> cast to the interface of its own mode (<c>ISpotfacingHoleParameters</c>,
<c>ICountersinkHoleParameters</c>). A position off the origin is set by
<c>IHoleDisposal.Point3DParamSurface</c> + <c>OffsetType=ksOffsetByCoords</c>.</summary>

## <a id="sweep-route"></a>Маршрут кинематического элемента

<summary>Sweep - «Элемент по траектории» (docs/05 SM-04).</summary>
<remarks>The route is documented API5, and this follows from the help rather than from
convenience. <c>obj3dtype.html</c> documents <c>o3d_baseEvolution = 45 →
ksBaseEvolutionDefinition</c>, but <c>ievolutions_add.html</c> lists only
<c>o3d_bossEvolution</c> and <c>o3d_cutEvolution</c> as valid for <c>IEvolutions::Add</c> - the
base type is absent from the list. MEASURED on 20.09.2026 (step B5.7): <c>IEvolutions.Add(45)</c>
returns <c>KompasAPI7.EvolutionClass</c>, i.e. an object IS handed back, but body validity along
this path was not measured and it is not accepted as the documented route. The working route is
<c>ksPart.NewEntity(45)</c> + <c>ksBaseEvolutionDefinition</c>, confirmed by volume (step
B5.1).</remarks>


<summary>Sweep - «Элемент по траектории» (docs/05 SM-04).</summary>
<remarks>DOC: the route is documented API5. <c>obj3dtype.html</c> documents
<c>o3d_baseEvolution = 45 → ksBaseEvolutionDefinition</c>, but <c>ievolutions_add.html</c>
lists only <c>o3d_bossEvolution</c> and <c>o3d_cutEvolution</c> as valid for
<c>IEvolutions::Add</c> - the base type is absent. MEASURED: <c>IEvolutions.Add(45)</c> returns
<c>KompasAPI7.EvolutionClass</c> (an object IS handed back), but body validity along this path
was not measured, so it is not accepted as the route. The working route is
<c>ksPart.NewEntity(45)</c> + <c>ksBaseEvolutionDefinition</c>, confirmed by volume.
History: docs/decisions/contracts.md#sweep-route</remarks>

## <a id="loft-route"></a>Маршрут элемента по сечениям

<summary>Loft (docs/05 SM-05).</summary>
<remarks>The route is documented API5, for the same reason as sweep. <c>obj3dtype.html</c>
documents <c>o3d_baseLoft = 30 → ksBaseLoftDefinition</c>, but <c>ilofts_add.html</c> lists only
<c>o3d_bossLoft</c> and <c>o3d_cutLoft</c> for <c>ILofts::Add</c>. The working route is
<c>ksPart.NewEntity(30)</c> + <c>ksBaseLoftDefinition</c>, confirmed by volume <c>28000</c>
(step B5.4).</remarks>

## <a id="shell-route"></a>Маршрут оболочки

<summary>Shell (docs/05 SM-13).</summary>
<remarks>The route is documented API7, and it is the only one of the three that needs no base
type: <c>ishells_add.html</c> declares <c>IShells::Add()</c> with no type argument at all.
MEASURED (step B5.7): <c>IModelContainer.Shells.Add()</c> returns <c>KompasAPI7._ShellClass</c>
and casts to <c>IShell</c>. Volumes confirmed: 21632 / 24832 / 40256 (steps B5.5, B5.6).</remarks>

## <a id="boolean-route"></a>Маршрут булевой операции

<summary>Boolean operation on bodies with an explicit target and tools (docs/05 SM-15).</summary>
<remarks>Route MEASURED on 18.09.2026 by probe <c>--boolean</c> (run <c>b10ffb70b7d24b6497417bc6639581b1</c>, PASS 13 · FAIL 0):
<c>IModelContainer.Booleans.Add()</c> → <c>IBoolean</c>, fields <c>BaseObject</c> (target), <c>ModifyObjects</c> (tool array),
<c>BooleanType</c>, <c>SaveCopyModifyObjects</c>, then <c>Update()</c>.
Difference operand order MEASURED: <b>target minus tools</b>. The <c>ksBooleanType</c> values - <c>ksIntersect=1</c>,
<c>ksDifference=2</c>, <c>ksUnion=3</c> - come from the enum, not from the catalog (the catalog called union zero and was wrong).
The core neither rejects a repeated reference nor checks that the target is outside the tool set: both checks MUST live in
the contract, before the COM call.</remarks>


<summary>Boolean operation on bodies with an explicit target and tools (docs/05 SM-15).</summary>
<remarks>DOC: route is the documented API7 boolean path: <c>IModelContainer.Booleans.Add()</c> →
<c>IBoolean</c> with <c>BaseObject</c> (target), <c>ModifyObjects</c> (tool array),
<c>BooleanType</c>, <c>SaveCopyModifyObjects</c>, then <c>Update()</c>.
MEASURED: difference operand order is target minus tools. The <c>ksBooleanType</c> values -
<c>ksIntersect=1</c>, <c>ksDifference=2</c>, <c>ksUnion=3</c> - come from the enum, not the
catalog (the catalog called union zero and was wrong).
INVARIANT: the core neither rejects a repeated reference nor checks that the target is outside
the tool set; both checks MUST live in the contract, before the COM call.
History: docs/decisions/contracts.md#boolean-route</remarks>


<summary>Boolean operation on bodies with an explicit target and tools (docs/05 SM-15).</summary>
<remarks>DOC: route is the documented API7 boolean path: <c>IModelContainer.Booleans.Add()</c> →
<c>IBoolean</c> with <c>BaseObject</c> (target), <c>ModifyObjects</c> (tool array),
<c>BooleanType</c>, <c>SaveCopyModifyObjects</c>, then <c>Update()</c>.
MEASURED: difference operand order is target minus tools; the <c>ksBooleanType</c> values -
<c>ksIntersect=1</c>, <c>ksDifference=2</c>, <c>ksUnion=3</c> - come from the enum, not the catalog.
INVARIANT: the core neither rejects a repeated reference nor checks that the target is outside
the tool set; both checks MUST live in the contract, before the COM call.
History: docs/decisions/contracts.md#boolean-route</remarks>

## <a id="split-route"></a>Маршрут разделения тела

<summary>Splitting a body into parts by a plane (docs/05 SM-16).</summary>
<remarks>Route MEASURED on 18.09.2026 by probe <c>--split</c> (run
<c>124682af57a242728ea765f1aae4816c</c>, PASS 11 · FAIL 0):
<c>IModelContainer.SplitSolids.Add()</c> → <c>ISplitSolid</c> with the single meaningful member
<c>CutObjects</c>, then <c>Update()</c>. Splitting keeps ALL parts by construction - no separate
"kept set" member is needed, and this measurement lifted blocker OQ-A18.</remarks>

## <a id="cut-by-plane-route"></a>Маршрут отсечения плоскостью

<summary>Cutting a body by a plane with a chosen kept side (docs/05 SM-16).</summary>
<remarks>Route MEASURED on 18.09.2026 by probe <c>--split</c>, step SP.7:
<c>IModelContainer.Cuts.Add()</c> → <c>ICut</c> with <c>BuildingType = ksCutByPlane</c>,
<c>CutObject</c> = plane, <c>Direction</c> = side choice, then <c>Update()</c>.
<para>Sign rule MEASURED: with normal <c>(1,0,0)</c> and plane <c>x = 10</c>,
<c>Direction = true</c> keeps the side <b>along the normal</b> (<c>s &gt; 0</c>, V = 18 000),
<c>false</c> the opposite one (<c>s &lt; 0</c>, V = 6 000).</para></remarks>

## <a id="reposition-route"></a>Маршрут переноса тела

<summary>Body translation and rotation (docs/05 SM-17).</summary>
<remarks>Route MEASURED on 18.09.2026 by probe <c>--reposition</c> (run
<c>929f08886f1348fe921943052a4026b0</c>, PASS 10 · FAIL 0):
<c>IModelContainer.BodyRepositions.Add()</c> → <c>IBodyReposition</c>, <c>RepositionBody</c> =
body, the placement is written by <c>Position.InitByMatrix3D</c>, then <c>Update()</c>.
<para><b>Only a homogeneous 4×4 matrix writes the placement</b> (OQ-A19): routes from 12 numbers
(«axes then origin» and «origin then axes») and <c>SetDisplacementByAxis</c> return
<c>Update() = true</c> and do NOT move the body. A successful <c>Update()</c> is therefore not
proof here, so the adapter MUST verify the placement after the call rather than trust the
returned value.</para>
<para>Rotation direction MEASURED as the right-hand rule: <c>(x,y) → (−y,x)</c>. On a
translation the row/column layout cannot be told apart (a unit rotation is symmetric), so the
layout is proved by a rotation, not a translation.</para></remarks>


<summary>Body translation and rotation (docs/05 SM-17).</summary>
<remarks>DOC: route is the documented API7 path: <c>IModelContainer.BodyRepositions.Add()</c> →
<c>IBodyReposition</c> with <c>RepositionBody</c> = body, the placement written by
<c>Position.InitByMatrix3D</c>, then <c>Update()</c>.
MEASURED: only a homogeneous 4×4 matrix writes the placement (OQ-A19) - routes from 12 numbers
(«axes then origin» and «origin then axes») and <c>SetDisplacementByAxis</c> return
<c>Update() = true</c> and do NOT move the body, so a successful <c>Update()</c> is not proof
here and the adapter MUST verify the placement after the call.
MEASURED: rotation direction is the right-hand rule, <c>(x,y) → (−y,x)</c>; on a translation
the row/column layout cannot be told apart, so the layout is proved by a rotation.
History: docs/decisions/contracts.md#reposition-route</remarks>

## <a id="sketch-status-route"></a>Маршрут статуса эскиза

<summary>Parametric definiteness of an existing sketch - the status KOMPAS shows with the
«+», «−», «!» signs.</summary>
<remarks>Route MEASURED on 17.09.2026 by probe S (<c>docs/acceptance/api7/sketch-definition.md</c>,
run <c>82880ed0b14a4e299bb0e93d7f8a7f2f</c>, PASS 9 · FAIL 0 · UNKNOWN 6) and lies in API7:
<c>TransferInterface(sketchEntity, ksAPI7Dual, 0)</c> → <c>ISketch.ConstraintsState</c> of type
<c>ksConstraintsStateEnum</c>. Five repeated reads changed neither volume nor topology counts
(S.7), so the command runs as a READ - without <c>BeginEdit</c>, <c>Update</c> or rebuild.</remarks>

## <a id="pattern-grid-route"></a>Маршрут массива по сетке

<summary>Grid pattern (docs/05 SM-18). Route is API7 only:
<c>IModelContainer.FeaturePatterns.Add(o3d_meshCopy=35)</c> → <c>QI(ILinearPattern)</c>.</summary>
<remarks>Not API5: the API5 definitions (<c>ksMeshCopyDefinition</c>,
<c>ksMeshPartArrayDefinition</c>) exist in the metadata dump, but patterns have no product route
through <c>NewEntity + Create()</c> - on rotation (SM-03) it was MEASURED that the API5 wrapper
around an API7 factory object builds nothing (<c>Create()</c> returns <c>true</c>, the object
appears in the tree, the volume does not change). The experiment is not repeated here because the
API7 route is published by SDK page <c>copytype.html</c> («o3d_meshCopy 35 ILinearPattern»).</remarks>


<summary>Grid pattern (docs/05 SM-18). Route is API7 only:
<c>IModelContainer.FeaturePatterns.Add(o3d_meshCopy=35)</c> → <c>QI(ILinearPattern)</c>.</summary>
<remarks>DOC: the API7 route is published by SDK page <c>copytype.html</c>
(«o3d_meshCopy 35 ILinearPattern»). Not API5: the API5 definitions (<c>ksMeshCopyDefinition</c>,
<c>ksMeshPartArrayDefinition</c>) exist in the metadata dump, but patterns have no product route
through <c>NewEntity + Create()</c> - on rotation it was MEASURED that the API5 wrapper around
an API7 factory object builds nothing (<c>Create()</c> returns <c>true</c>, the object appears
in the tree, the volume does not change).
History: docs/decisions/contracts.md#pattern-grid-route</remarks>

## <a id="aux-geometry-route"></a>Маршрут вспомогательной геометрии

<summary>Creates a part auxiliary-geometry object - a plane, axis or point
(<c>dep.refs.planes</c>, <c>dep.refs.axes</c>, <c>dep.refs.points_axes</c>).</summary>
<remarks>DOC: route is documented API7 and taken from the v24 help (step 0 of the product-routes
order, report <c>DEPENDENCIES_PRODUCT_ROUTES_STEP0_REPORT_20260921.md</c> §6.1–6.3):
<c>IAuxiliaryGeomContainer.GetPlanes3D/GetAxes3D</c>, <c>IModelContainer.GetPoints3D</c>, then
<c>Add(ksObj3dTypeEnum)</c> with a type from the official table <c>obj3dtype.html</c>:
<c>o3d_planeOffset</c> = 14, <c>o3d_planeAngle</c> = 15, <c>o3d_axis2Points</c> = 10,
<c>o3d_axisConeFace</c> = 11, <c>o3d_axisEdge</c> = 12, <c>o3d_point3D</c> = 70.</remarks>

## <a id="aux-list-route"></a>Маршрут перечисления вспомогательной геометрии

<summary>Enumerates and reads part auxiliary-geometry objects: planes, axes, points.</summary>
<remarks><b>A name is resolved to an address by enumeration, and this is MEASURED, not
chosen.</b> The help documents <c>IAxes3D.GetAxis3DByName</c>, <c>IPoints3D.GetPoint3DByName</c>
and <c>ISketchs.GetSketchByName</c>, but the shipped <c>Interop.KompasAPI7.dll</c> of the target
assembly has NOT ONE member containing the substring <c>ByName</c> (instrument
<c>tools/KompasMcp.InteropScan</c>, 21.09.2026). A name is therefore matched by enumerating the
collection through the documented members <c>Count</c> + indexed property + <c>Name</c>. The
returned collection index is NOT declared a stable address: a rebuild shifts it.</remarks>

## <a id="update-plane-route"></a>Маршрут правки плоскости

<summary>Edits an ALREADY CREATED plane as a part object: offset, tilt angle, support
(<c>dep.refs.planes</c>, action <c>edit</c>).</summary>
<remarks>A separate route, not a field on another call. MEASURED by row <c>DEP.DPL.04.edit</c>
of an earlier run: editing the feature through a <c>plane</c> field was ACCEPTED (failure code
<c>None</c>) and did NOT change the geometry. Here the edit goes through the documented setters
<c>IPlane3DByOffset.Offset</c> / <c>IPlane3DByAngle.Angle</c> / <c>IPlane3DBy*.BasePlane</c>,
then <c>Update()</c> and <c>RebuildModel</c>, and the answer carries the value READ BACK: a
successful code is not passed off as an applied edit.</remarks>


<summary>Edits an ALREADY CREATED plane as a part object: offset, tilt angle, support
(<c>dep.refs.planes</c>, action <c>edit</c>).</summary>
<remarks>A separate route, not a field on another call. MEASURED: editing the feature through a
<c>plane</c> field was ACCEPTED (failure code <c>None</c>) and did NOT change the geometry.
Here the edit goes through the documented setters <c>IPlane3DByOffset.Offset</c> /
<c>IPlane3DByAngle.Angle</c> / <c>IPlane3DBy*.BasePlane</c>, then <c>Update()</c> and
<c>RebuildModel</c>, and the answer carries the value READ BACK: a successful code is not passed
off as an applied edit.
History: docs/decisions/contracts.md#update-plane-route</remarks>

## <a id="sketch-entities-route"></a>Маршрут перечисления сущностей эскиза

<summary>Enumerates the entities of an EXISTING sketch with a STABLE ADDRESS (<c>dep.sketch.entities</c>, actions <c>discover</c> and <c>read</c>).</summary>
<remarks>DOC: route documented by the v24 help: <c>ISketch.BeginEditEx(true)</c> → <c>IFragmentDocument.ViewsAndLayersManager.Views</c> →
<c>IView</c> → <c>IDrawingContainer.GetObjects(ksAllObj)</c> → address <c>IKompasDocument1.GetObjectId</c> → <c>ISketch.EndEdit()</c>.
The address is not "the Nth object of the collection" nor a coordinate: it is the <c>GetObjectId</c> string that
<c>FindObjectById</c> accepts back, so it survives a model rebuild and a document reopen. A collection index is NOT declared a stable address.</remarks>

## <a id="sketch-entity-edit-route"></a>Маршрут правки сущности эскиза

<summary>Address-targeted edit of ONE existing sketch entity (<c>dep.sketch.entities</c>,
action <c>edit</c>).</summary>
<remarks>The earlier sketch-edit schema accepted only a mode
(<c>append</c>/<c>replace</c>/<c>delete_entities</c>) and a WHOLE NEW set of primitives - i.e.
it recreated the contour rather than editing an entity. MEASURED by the distinguishing control of
row <c>DEP.DSE.04.edit</c>: after <c>replace</c> the base-body volume changed from 80000 to 24000,
so the contour was RECREATED. Here the edit targets exactly one entity, and the answer carries the
state read AFTER the edit - "accepted" and "applied" differ by measurement, not by wording.</remarks>

## <a id="sweep-shift-mode"></a>Правка режима движения сечения

<summary>New section-motion mode of a sweep (family=<c>sweep</c>). A separate field, not a shared
<c>direction</c>: for a chamfer <c>direction</c> is the chamfer side, for a shell the wall side, and
one name for three different things would make the answer ambiguous.</summary>
<remarks><b>Route MEASURED on 20.09.2026</b> (probe <c>--b5</c>, step B5.13): on ONE feature
changing the mode <c>orthogonal → parallel → orthogonal</c> gave volumes
<c>24674.011002723353 → 15707.963267948984 → 24674.011002723353</c>, and <c>sketchShiftType</c>
read back 0 and 2. The setup distinguishes only on an ARC: on a straight path both modes give one
body.</remarks>


<remarks>DOC: ksevolutionshiftsketchtypeenum.html - the numbers were read from the official SDK v24 help
(opened over the wire 20.09.2026), not inferred by analogy: <c>ksEvShiftParallel = 0</c> -
«образующая переносится параллельно самой себе»; <c>ksEvShiftKeepAngle = 1</c> - «образующая при переносе
сохраняет исходный угол с направляющей»; <c>ksEvShiftOrtogonal = 2</c> - «плоскость образующей
выставляется и сохраняется ортогональной направляющей» (vendor spelling preserved).
MEASURED (20.09.2026, probe <c>--b5</c>, steps B5.1/B5.2): on an R50/90° arc the orthogonal mode gave
<c>24674.011002723353</c> against the expected <c>S × L = 24674.011002723397</c>, while the parallel one
gave <c>15707.963267948984</c> - a difference of <c>8966.047734774369</c> mm³. Hence the orthogonality-mode
acceptance row MUST stand on an ARC, not a segment.</remarks>


MEASURED on an arc: the orthogonal mode matched the expected <c>S × L</c>, while the parallel one did
not - the difference was large. Hence the orthogonality-mode acceptance row MUST stand on an ARC, not
a segment.
History: docs/decisions/contracts.md#sweep-shift-mode</remarks>

## <a id="loft-section-refs"></a>Правка набора сечений loft

<summary>New set of loft sections (family=<c>loft</c>), in joining order.</summary>
<remarks><b>Route MEASURED on 20.09.2026</b> (step B5.13): rebinding sections on an already built feature changes the geometry -
40×40+20×20 give <c>28000</c>, 40×40+40×40 give the prism <c>48000</c>, returning to the previous set restores <c>28000</c>.
The <c>closed</c> field is absent here deliberately, and this is a measured fact, not an omission: writing <c>ILoft.Closed</c> on a
built feature returns <c>Update() = True</c>, but the read-back gives <c>False</c> and the volume stays the same - "accepted" does not
mean "applied". So closure is set ONLY at creation (<c>kompas_loft.closed</c>) and is not declared editable here.</remarks>

## <a id="loft-couplings-edit"></a>Правка цепочек соответствия loft

<summary>New set of coupling chains (family=<c>loft</c>) - a <b>full replacement</b>, as with
<see cref="SectionRefs"/>.</summary>
<remarks><para><b>Why a separate field rather than "leave as it was".</b> A chain describes the
point correspondence of a SPECIFIC section set: <c>ICoupling.Count</c> is «количество сечений в
цепочке» (<c>icoupling_count.html</c>), and <c>PositionOffset(Index)</c> is addressed by the
section index in the chain (<c>icoupling_positionoffset.html</c>). So after the section set is
replaced the old chain no longer describes this feature, and leaving it "as it was" would silently
keep a foreign correspondence. If the feature carries chains and an edit changes the sections and
does NOT name the chains, the call is rejected with a named refusal; an empty list means "no
chains".</para>
<para><b>Route MEASURED on 20.09.2026</b> (probe <c>--b5</c>, steps B5.17 and B5.18): a chain is
set by <c>ILoft.AddCoupling()</c> → <c>ICoupling</c> → <c>PositionOffset(Index)</c>; on a pyramid
40×40 → 20×20 at h = 30 offsets <c>0 / 0</c> give <c>28000</c> (as without a chain), offset
<c>0 / 20</c> mm (25 % of the 80 mm contour) gives <c>20000</c>, reverting gives <c>28000</c>.
Chains are replaced by <c>ClearCouplings()</c> + <c>AddCoupling()</c>; <c>ICoupling.Delete()</c> was
MEASURED working, but a full replacement does not depend on the deletion order.</para></remarks>


<summary>New set of coupling chains (family=<c>loft</c>) - a <b>full replacement</b>, as with
<see cref="SectionRefs"/>.</summary>
<remarks>Why a separate field rather than "leave as it was": a chain describes the point
correspondence of a SPECIFIC section set (<c>ICoupling.Count</c> is «количество сечений в
цепочке», <c>icoupling_count.html</c>; <c>PositionOffset(Index)</c> is addressed by the section
index, <c>icoupling_positionoffset.html</c>), so after the section set is replaced the old chain
would silently keep a foreign correspondence. If an edit changes the sections and does NOT name
the chains, the call is rejected with a named refusal; an empty list means "no chains".
DOC: the chain is set by <c>ILoft.AddCoupling()</c> → <c>ICoupling</c> →
<c>PositionOffset(Index)</c>, replaced by <c>ClearCouplings()</c> + <c>AddCoupling()</c>.
MEASURED: an explicit correspondence replaces the automatic one, and the offset unit is mm.
History: docs/decisions/contracts.md#loft-couplings-edit</remarks>

## <a id="shell-thickness-edit"></a>Правка толщины стенки оболочки

<summary>New shell wall thickness (family=<c>shell</c>), mm.</summary>
<remarks><b>Route MEASURED on 20.09.2026</b> (step B5.13): on one feature <c>t = 2 → 4 → 4 → 2</c>
gave <c>21632 → 40256 → 53056 → 21632</c>, values read back. The third number is the outward
direction at <c>t = 4</c>: outer box <c>108×88×14 = 133056</c> minus the cavity <c>80000</c>. The
edit carries BOTH mode parameters - thickness and direction - because otherwise "exactly the
requested thing changed" is indistinguishable from "this changed too".</remarks>

## <a id="shell-face-refs"></a>Правка набора удалённых граней оболочки

<summary>New SET of removed shell faces (family=<c>shell</c>) - a full replacement, not an addition to the existing ones: what is passed is what
should remain removed. Faces come from <c>kompas_read_topology</c>, not collection positions.</summary>
<remarks>Route MEASURED on 20.09.2026 (probe <c>--b5</c>, step B5.14): on one feature of a 100×80×10 box a shell <c>t = 2</c> inward with the top face
removed gives <c>21632</c> at <c>11</c> faces; adding the second face (bottom, 100×80) makes the cavity through and gives <c>7040</c> at <c>10</c> faces;
reverting restores <c>21632</c> at <c>11</c> (negative control: re-writing the same set does not move the volume). A separate route, not a carry-over from
the fillet: <c>Clear()</c> + <c>Add()</c> over <c>ksEntityCollection</c> measurably did NOT work for the FILLET (row <c>FL04r</c>), so the shell was
verified by its own run. An empty list is rejected here too: with it the operation is accepted (<c>Create/Update = true</c>) but the body does not
change - the volume stays <c>80000</c> at <c>6</c> faces.</remarks>


<summary>New SET of removed shell faces (family=<c>shell</c>) - a full replacement: what is passed
is what should remain removed. Faces come from <c>kompas_read_topology</c>, not collection
positions.</summary>
<remarks>MEASURED: changing the removed-face set changes the read-back volume and face count, and
re-writing the same set does not move the volume. A separate route, not a carry-over from the
fillet: <c>Clear()</c> + <c>Add()</c> over <c>ksEntityCollection</c> measurably did NOT work for
the FILLET. An empty list is rejected here too: with it the operation is accepted
(<c>Create/Update = true</c>) but the body does not change.
History: docs/decisions/contracts.md#shell-face-refs</remarks>

## <a id="rotation-angle-edit"></a>Правка угла вращения

<summary>New ROTATION angle (family=<c>rotation</c>), degrees. A separate field, not the shared <see cref="AngleDeg"/>: for a chamfer that member
means the chamfer angle, for a rotation the sweep angle, and one quantity under one name for two families would make the answer ambiguous. A call
with <see cref="AngleDeg"/> on a rotation feature is rejected with INVALID_ARGUMENT pointing at this field rather than interpreted silently.</summary>
<remarks>Route MEASURED on 18.09.2026 (probe <c>FullTurnProbe</c>, step <c>F.2</c>): on ONE feature (reference R20 H40, axis by two model points)
changing the angle 360 → 180 → 360 gave volumes <c>50265.4824574366 → 25132.7412287183 → 50265.4824574366</c> at bbox
<c>z[−20,20] → z[−0,20] → z[−20,20]</c> - a geometric change, not just a written number. The order "write the angle to <c>IRotated.Angle[true]</c> →
<c>IRotated.Update()</c> → <c>RebuildModel</c>/<c>RebuildDocument</c>" is part of the contract, as at creation. Angle only: changing the profile and
axis of an existing rotation is NOT done by this call (separate routes, neither measured), so <see cref="SketchRef"/> on a rotation feature is rejected.</remarks>


<summary>New ROTATION angle (family=<c>rotation</c>), degrees. A separate field, not the shared
<see cref="AngleDeg"/>: that member means the chamfer angle, and one quantity under one name for
two families would make the answer ambiguous. A call with <see cref="AngleDeg"/> on a rotation
feature is rejected with INVALID_ARGUMENT pointing at this field rather than interpreted
silently.</summary>
<remarks>MEASURED: changing the angle on ONE feature gave a geometric change, not just a written
number. The order "write the angle to <c>IRotated.Angle[true]</c> → <c>IRotated.Update()</c> →
<c>RebuildModel</c>/<c>RebuildDocument</c>" is part of the contract, as at creation. Angle only:
changing the profile and axis of an existing rotation is NOT done by this call, so
<see cref="SketchRef"/> on a rotation feature is rejected.
History: docs/decisions/contracts.md#rotation-angle-edit</remarks>


<summary>New ROTATION angle (family=<c>rotation</c>), degrees. A separate field, not the shared
<see cref="AngleDeg"/>: that member means the chamfer angle. A call with <see cref="AngleDeg"/> on
a rotation feature is rejected with INVALID_ARGUMENT pointing at this field.</summary>
<remarks>MEASURED: changing the angle on ONE feature gave a geometric change, not just a written
number. The order "write the angle to <c>IRotated.Angle[true]</c> → <c>IRotated.Update()</c> →
<c>RebuildModel</c>/<c>RebuildDocument</c>" is part of the contract, as at creation. Angle only:
changing the profile and axis is NOT done by this call, so <see cref="SketchRef"/> on a rotation
feature is rejected. History: docs/decisions/contracts.md#rotation-angle-edit</remarks>

## <a id="reposition-kind-edit"></a>Правка вида переноса

<summary>Kind of transformation when editing a REPOSITION feature (family=<c>reposition</c>).</summary>
<remarks>Why separate fields rather than re-creating: order B3 §5 requires an edit to change the parameters of an EXISTING feature relative to
its ORIGINAL inputs, not to apply them to the current placement. A repeated <c>kompas_reposition</c> call creates a SECOND feature and shifts the
body from the current placement, so the offset accumulates. MEASURED by probe RP.6: editing feature[0] by re-writing the same vector leaves the
bbox <c>(17,−11,13)…(37,−1,18)</c>, and returning the vector to zero brings the body home - the parameter is applied to the original body.
Prefixed names, as with <see cref="RotationAngleDeg"/>: in <c>kompas_reposition</c> the same quantities are <c>kind</c>, <c>vector_mm</c>,
<c>axis_point_mm</c> and <c>angle_deg</c>; here <c>angle_deg</c> is taken by the CHAMFER angle, so the rotation angle is
<see cref="RepositionAngleDeg"/> (one name for two quantities would make the answer ambiguous).</remarks>


<summary>Kind of transformation when editing a REPOSITION feature (family=<c>reposition</c>).</summary>
<remarks>Why separate fields rather than re-creating: order B3 §5 requires an edit to change the
parameters of an EXISTING feature relative to its ORIGINAL inputs, not to apply them to the
current placement - a repeated <c>kompas_reposition</c> call creates a SECOND feature and shifts
the body from the current placement, so the offset accumulates. MEASURED: re-writing the same
vector leaves the bbox unchanged and returning the vector to zero brings the body home - the
parameter is applied to the original body. Prefixed names, as with
<see cref="RotationAngleDeg"/>: in <c>kompas_reposition</c> the same quantities are <c>kind</c>,
<c>vector_mm</c>, <c>axis_point_mm</c> and <c>angle_deg</c>; here <c>angle_deg</c> is taken by
the CHAMFER angle, so the rotation angle is <see cref="RepositionAngleDeg"/>.
History: docs/decisions/contracts.md#reposition-kind-edit</remarks>

## <a id="cut-keep-side-edit"></a>Правка сохраняемой стороны отсечения

<summary>New kept side for <c>family=cut_by_plane</c>: <c>positive</c> - <c>s &gt; 0</c>,
<c>negative</c> - <c>s &lt; 0</c>.</summary>
<remarks>The mapping is MEASURED at step SP.7: <c>ICut.Direction = true</c> keeps the side along the
normal (<c>s &gt; 0</c>, V = 18 000), <c>false</c> the opposite one (<c>s &lt; 0</c>, V = 6 000). When
editing the side the feature is the same: the edit changes the REMAINDER, not the body
count.</remarks>

## <a id="split-part-volumes"></a>Правка ожидания объёмов частей

<summary>Analytic expectation of the PART volumes after a split edit (<c>family=split</c>).</summary>
<remarks>The only quantity that confirms a split edit. The sum of part volumes does not change on an edit (24 000 both at <c>x = 10</c> and at
<c>x = 15</c>), so preservation of volume is an invariant, not a confirmation: a row checking only the sum would pass on complete inaction. Only the
PER-VOLUME composition distinguishes them: <c>[6 000, 18 000]</c> versus <c>[9 000, 15 000]</c>. The matching is by volume equality WITHIN TOLERANCE
(0.01 mm³ abs. / 1e-6 rel.) and with multiplicity: each expected volume gets its own body, one body cannot cover two expectations; the order of the
parts does not matter. If an expectation is declared and does not match, the call returns <c>NO_GEOMETRY_CHANGE</c> with <c>partial_effects=true</c>
and the actual composition in <c>details</c> - a sign that the parameter was applied not to the feature's original inputs.</remarks>


<summary>Analytic expectation of the PART volumes after a split edit (<c>family=split</c>).</summary>
<remarks>The only quantity that confirms a split edit. The SUM of part volumes does not change on
an edit, so preservation of volume is an invariant, not a confirmation - a row checking only the
sum would pass on complete inaction. Only the PER-VOLUME composition distinguishes them. The
matching is by volume equality WITHIN TOLERANCE (0.01 mm³ abs. / 1e-6 rel.) and with
multiplicity: each expected volume gets its own body; order does not matter. If a declared
expectation does not match, the call returns <c>NO_GEOMETRY_CHANGE</c> with
<c>partial_effects=true</c> and the actual composition in <c>details</c>.
History: docs/decisions/contracts.md#split-part-volumes</remarks>

## <a id="fillet-radius-edit"></a>Правка радиуса скругления

<summary>New fillet radius (mm); family=<c>fillet</c>. Editable only through the API7 route: in API5
<c>ksFilletDefinition</c> has a radius, but writing to it on an existing feature is NOT applied -
MEASURED on 16.09.2026 by row FL04r: the setter returns success while <c>entity.Update()</c> +
<c>RebuildDocument()</c> leave the volume unchanged (see <c>Api5Session.UpdateFilletRadius</c>). The
radius is therefore written to <c>IFillet.Radius1</c> on the live model, and the API5 feature is
matched to it by RADIUS EQUALITY, not by name (F.8: a name set in API5 reads differently in
API7).</summary>

## <a id="cut-area-scope-edit"></a>Правка области действия признака

<summary>Body the feature's scope is directed at when editing (family=<c>cut_by_plane</c>).</summary>
<remarks>Why the field is on the edit: order B3 §3.2 requires "assigning and RESTORING the scope at create/edit/rebuild/save-reopen". At edit it can only be
re-read - if the feature ended up in the "All objects" default (help page <c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>), moving the support would remove
material from unrelated bodies, and without this field there would be nothing to fix it with except deletion and rebuilding. An absent field is not "all the
same": if not set, the adapter MUST READ the scope of the live feature before mutation and refuse if it is not addressed (<c>ChooseType ≠ ksChBodies</c> or
an empty body list). Route MEASURED on 19.09.2026 by probe <c>--cut-area</c>: CA.4 (addressing accepted: A=12000, S=1000), CA.5 (negative control with
another body: A=18000, S=500), CA.6 (editing the support preserves addressing), CA.7 (addressing survives <c>save → close → open</c>).</remarks>


<summary>Body the feature's scope is directed at when editing (family=<c>cut_by_plane</c>).</summary>
<remarks>Why the field is on the edit: order B3 §3.2 requires "assigning and RESTORING the scope
at create/edit/rebuild/save-reopen". If the feature ended up in the "All objects" default (help
page <c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>), moving the support would remove material
from unrelated bodies, and without this field there would be nothing to fix it with except
deletion and rebuilding. An absent field is not "all the same": if not set, the adapter MUST READ
the scope of the live feature before mutation and refuse if it is not addressed
(<c>ChooseType ≠ ksChBodies</c> or an empty body list). MEASURED: addressing is preserved through
a support edit and survives <c>save → close → open</c>.
History: docs/decisions/contracts.md#cut-area-scope-edit</remarks>


<summary>Body the feature's scope is directed at when editing (family=<c>cut_by_plane</c>).</summary>
<remarks>Why the field is on the edit: the scope must be assignable and RESTORABLE at
create/edit/rebuild/save-reopen. If the feature ended up in the "All objects" default (help page
<c>rezultat_oper_v_zavisimosti_ot_s_o.html</c>), moving the support would remove material from
unrelated bodies, with nothing to fix it but deletion and rebuilding. An absent field is not
"all the same": if not set, the adapter MUST READ the scope of the live feature before mutation
and refuse if it is not addressed (<c>ChooseType ≠ ksChBodies</c> or an empty body list).
MEASURED: addressing is preserved through a support edit and survives <c>save → close → open</c>.
History: docs/decisions/contracts.md#cut-area-scope-edit</remarks>

## <a id="hole-counterbore-edit"></a>Правка цековки

<summary>New counterbore relief diameter (mode <c>through_counterbore</c>), mm. A field of a FOREIGN mode for the rest: on a blind hole and on a
countersink it is rejected <c>INVALID_ARGUMENT</c>.</summary>
<remarks>Route MEASURED on 20.09.2026 (step M.6): a relief Ø18×4 → Ø20×5 removed <c>474.380490692</c> mm³ over the previous - the ring difference
π/4·(D²−d²)·h (1178.097244573 against 703.716754404, formula M.2). It is written to <c>ISpotfacingHoleParameters.SpotfacingDiameter</c>; on a foreign
mode this interface is UNREACHABLE on the object - MEASURED by control (c) of the probe.</remarks>

## <a id="hole-countersink-edit"></a>Правка зенковки

<summary>New countersink MOUTH diameter (mode <c>through_countersink</c>), mm - the mouth, not the pilot:
the pilot is set by <see cref="DiameterMm"/>.</summary>
<remarks>
<b>Route MEASURED on 20.09.2026</b> (step M.6): a mouth Ø20 → Ø24 at 90° removed
<c>605.280184592</c> mm³ over the previous - the difference <c>π·h/3·(rM² + rP·rM − 2·rP²)</c>
with the derivative <c>h = (rM − rP)/tan(angle/2)</c> (1128.878960190 against 523.598775598).
</remarks>

## <a id="hole-volume-delta"></a>Правка ожидания снятого объёма

<summary>Analytic expectation of the material REMOVED by the edit, mm³: <c>volume_before − volume_after</c>.</summary>
<remarks>The sign is part of the definition of the quantity, not decoration: a hole removes material, so "became deeper" is positive and "became
shallower" is negative, and comparing the magnitude is not allowed. This is where the first edition of the probe erred - it compared the volume
increment (positive) with the analytic "removed" and gave a false "mismatch" on correct geometry: an INSTRUMENT defect, not a fact about the product.
Why a delta rather than the full volume: the full document volume depends on everything in it, while the delta is tied to the edit and distinguishes
"exactly what was requested applied" from "something else applied". <see cref="ExpectedVolumeMm3"/> is also accepted, checked separately. Without a
declared expectation the edit is not confirmed by a number: the level stays <c>call_returned</c>, and <c>expected_volume_delta_not_supplied</c> appears
in <c>unverified_aspects</c>.</remarks>


<summary>Analytic expectation of the material REMOVED by the edit, mm³:
<c>volume_before − volume_after</c>.</summary>
<remarks>The sign is part of the definition of the quantity: a hole removes material, so "became
deeper" is positive and "became shallower" is negative, and comparing the magnitude is not
allowed. Why a delta rather than the full volume: the full document volume depends on everything
in it, while the delta is tied to the edit and distinguishes "exactly what was requested
applied" from "something else applied". <see cref="ExpectedVolumeMm3"/> is also accepted, checked
separately. Without a declared expectation the level stays <c>call_returned</c>, and
<c>expected_volume_delta_not_supplied</c> appears in <c>unverified_aspects</c>.
History: docs/decisions/contracts.md#hole-volume-delta</remarks>


<summary>Analytic expectation of the material REMOVED by the edit, mm³:
<c>volume_before − volume_after</c>.</summary>
<remarks>The sign is part of the definition of the quantity: a hole removes material, so "became
deeper" is positive and "became shallower" is negative; comparing the magnitude is not allowed.
Why a delta rather than the full volume: the full document volume depends on everything in it,
while the delta is tied to the edit and distinguishes "exactly what was requested applied" from
"something else applied". <see cref="ExpectedVolumeMm3"/> is also accepted. Without a declared
expectation the level stays <c>call_returned</c>, and <c>expected_volume_delta_not_supplied</c>
appears in <c>unverified_aspects</c>. History: docs/decisions/contracts.md#hole-volume-delta</remarks>

## <a id="pattern-grid-axis"></a>Ось массива по сетке

<summary>Grid pattern (docs/05 SM-18, profile <c>mechanical-core-v1</c>, queue B4).</summary>
<remarks>The axis is given by two MODEL points, not a reference (same decision as rotation, SM-03): <c>ILinearPattern.Axis1/Axis2</c>
accept <c>IModelObject</c>, and the server builds the axis itself as <c>o3d_axis2Points</c> in the SAME part - an axis reference
could drag one in from a foreign part, which patterns never checked. The direction vector is absent deliberately: in <c>kAPI7.tlb</c>
<c>ILinearPattern.Vector1/Vector2</c> declare getters only (<c>_get_Vector1</c>, no <c>_set_Vector1</c>), and in <c>Interop.KompasAPI7.dll</c>
these members are not declared AT ALL (checked with <c>KompasMcp.InteropScan --type-members ILinearPattern</c>); direction is set by an
axis, not a vector - open question OQ-B-03. The second direction is optional: if <see cref="Axis2Point1Mm"/> and <see cref="Count2"/>
are not set, this is a single-row pattern (<c>SM-18.grid.single_row</c>), <c>Count2 = 1</c>.</remarks>


<summary>Grid pattern (docs/05 SM-18, profile <c>mechanical-core-v1</c>, queue B4).</summary>
<remarks>The axis is given by two MODEL points, not a reference (same decision as rotation):
<c>ILinearPattern.Axis1/Axis2</c> accept <c>IModelObject</c>, and the server builds the axis itself
as <c>o3d_axis2Points</c> in the SAME part - an axis reference could drag one in from a foreign part,
which patterns never checked. MEASURED: <c>ILinearPattern.Vector1/Vector2</c> declare getters only in
<c>kAPI7.tlb</c> and are not declared AT ALL in <c>Interop.KompasAPI7.dll</c>, so direction is set by
an axis, not a vector (open question OQ-B-03). The second direction is optional: if
<see cref="Axis2Point1Mm"/> and <see cref="Count2"/> are not set, this is a single-row pattern
(<c>SM-18.grid.single_row</c>), <c>Count2 = 1</c>.
History: docs/decisions/contracts.md#pattern-grid-axis</remarks>

## <a id="pattern-grid-angles"></a>Углы массива по сетке

<summary>Tilt angle of the first grid axis, DEGREES. Not set - the model value stays as is (0), i.e. the axis is taken as built.</summary>
<remarks>The type was MADE OPTIONAL BY MEASUREMENT, not for looks. While the field was mandatory "not set" was inexpressible and
the adapter ALWAYS wrote zero. For the second axis this measurably broke the grid: Angle2 is the angle BETWEEN directions, zero
aligns the second direction with the first, and a rectangular grid degenerated into a line (MEASURED: copies continued the first
axis - x = 20, 40, 60, 50, 70, 90 at y = 20). The tool schema already declared both angles optional (Sch.Nullable), so the change
does not alter the wire contract - only the behaviour when the field is omitted.</remarks>

## <a id="pattern-grid-angle2"></a>Угол между направлениями сетки

<summary>Angle BETWEEN grid directions, DEGREES. A rectangular grid is 90°; 0° aligns the second
direction with the first. Not set - the model value stays as is (90°).</summary>
<remarks>"Angle between directions", not "tilt of the second axis": MEASURED in one setup with the
second axis (0,0,0)→(0,−80,0) and step 30 - at 0° copies continued the first axis, at 90° they
stood along the second, and at 270° the model normalized the value to 180° and sent the second
direction the opposite way. The second axis itself sets WHICH way the angle is laid out.</remarks>

## <a id="pattern-circular-mapping"></a>Соответствие направлений кругового массива

<summary>Circular pattern (docs/05 SM-19).</summary>
<remarks><para><b>The direction mapping is taken from the help page, not from expectation.</b> SDK
page <c>icircularpattern_props.html</c> (checked over the wire) names the members:
<c>Count1</c> - «Количество экземпляров в РАДИАЛЬНОМ направлении», <c>Step1</c> - «Шаг копирования в
РАДИАЛЬНОМ направлении», <c>Count2</c> - «Количество экземпляров в КОЛЬЦЕВОМ направлении»,
<c>Step2</c> - «УГЛОВОЙ шаг (ГРАДУСЫ) — шаг копирования в КОЛЬЦЕВОМ направлении». So the circular
direction here is the PAIR (Count2, Step2), not (Count1, Step1) as assumed before the page was
checked. This closes OQ-B-02.</para>
<para><b>A full turn is expressed by the pair (Count2, Step2), not by a flag.</b>
<c>ICircularPattern</c> has no separate "full turn" member in either the interop assembly or the
help; the expected semantics is <c>Step2 = 360 / Count2</c>, and it is measured by both halves in one
setup (step 90° at Count2 = 4 versus step 120° in the negative control: a 360° duplicate is visible
by volume, the difference exactly one hole's volume).</para></remarks>


<summary>Circular pattern (docs/05 SM-19).</summary>
<remarks>DOC: the direction mapping is taken from SDK page <c>icircularpattern_props.html</c>, not
from expectation: <c>Count1</c>/<c>Step1</c> are the RADIAL direction, <c>Count2</c>/<c>Step2</c> the
CIRCULAR one («Количество экземпляров в КОЛЬЦЕВОМ направлении», «УГЛОВОЙ шаг (ГРАДУСЫ)»). So the
circular direction here is the PAIR (Count2, Step2), not (Count1, Step1); this closes OQ-B-02.
INVARIANT: a full turn is expressed by the pair (Count2, Step2), not by a flag -
<c>ICircularPattern</c> has no separate "full turn" member; the semantics is <c>Step2 = 360 /
Count2</c>, measured by both halves in one setup (a 360° duplicate is visible by volume, the
difference exactly one hole's volume).
History: docs/decisions/contracts.md#pattern-circular-mapping</remarks>

## <a id="pattern-mirror-route"></a>Маршрут зеркального массива

<summary>Mirror pattern (docs/05 SM-23).</summary>
<remarks><para><b>The plane is an explicit object, not the current window selection.</b> This is a
requirement of dependency <c>dep.refs.planes</c>, and it also removes the question of the normal sign:
the reflection side is set by the plane itself, not by the order of the selection points. The YOZ
normal sign (pointing in −X) remains a fact of the model and is recorded in the plane contract
(<c>PlaneRefDto</c>), not hidden in reference resolution.</para>
<para><b>The type numbers are published, not picked.</b> <c>copytype.html</c>:
<c>o3d_mirrorOperation=48</c> - «зеркальный массив» (<c>IMirrorPattern</c>),
<c>o3d_mirrorAllOperation=49</c> - «зеркально отразить все» (the same <c>IMirrorPattern</c>,
additionally <c>IChooseBodies7</c>).</para></remarks>

## <a id="pattern-mirror-save-initial"></a>Область действия SaveInitialObjects

<summary>Keep the source objects (<c>SaveInitialObjects</c>). <c>true</c> adds a reflected copy,
leaving the source; <c>false</c> replaces the source with it.
<para>The scope is bounded by the help, and this is MEASURED. Page
<c>imirrorpattern_saveinitialobjects.html</c> says plainly: «Свойство работает ТОЛЬКО для
<c>o3d_mirrorAllOperation</c>» and «у других операций зеркального копирования возможность
скрыть экземпляры отсутствует». MEASURED by a run on both operations: on
<c>o3d_mirrorAllOperation</c> (mode <see cref="PatternMirrorMode.AllBodies"/>) a write of
<c>false</c> reads back as <c>false</c> and the bodies really are replaced by the reflected ones
(2 → 2 bodies of 4 000), while on <c>o3d_mirrorOperation</c> (mode
<see cref="PatternMirrorMode.SelectedOperations"/>) a write of <c>false</c> reads back as
<c>true</c> and the geometry does not change at all. Requiring the halves to differ on operation
48 would require what the help denies it; the parameter is accepted, and its non-acceptance is
named as a route note rather than passed off as a worked property.</para></summary>


<summary>Keep the source objects (<c>SaveInitialObjects</c>). <c>true</c> adds a reflected copy,
leaving the source; <c>false</c> replaces the source with it.
DOC: the scope is bounded by page <c>imirrorpattern_saveinitialobjects.html</c>: «Свойство
работает ТОЛЬКО для <c>o3d_mirrorAllOperation</c>» и «у других операций зеркального копирования
возможность скрыть экземпляры отсутствует». MEASURED on both operations: on
<c>o3d_mirrorAllOperation</c> a write of <c>false</c> reads back as <c>false</c> and the bodies
really are replaced by the reflected ones, while on <c>o3d_mirrorOperation</c> a write of
<c>false</c> reads back as <c>true</c> and the geometry does not change at all. Requiring the
halves to differ on operation 48 would require what the help denies it, so the parameter is
accepted and its non-acceptance is named as a route note.
History: docs/decisions/contracts.md#pattern-mirror-save-initial</remarks>

## <a id="sweep-route-b5"></a>Маршрут кинематического элемента (B5)

<summary>Sweep: a flat closed profile is carried along a continuous path
(docs/05 SM-04, profile <c>mechanical-core-v1</c>, queue B5).</summary>
<remarks>
<b>The document is taken from the PROFILE, not from a separate field.</b> The same device as for
rotation: a reference from a foreign part cannot be dragged into the mutation, because there is no
second document source here.
<b>The route is documented by an API5 page, not inferred by analogy with rotation.</b>
<c>ksbaseevolutiondefinition.html</c> («Основание - кинематический элемент (Интерфейсы
ksBaseEvolutionDefinition, IBaseEvolutionDefinition)») describes an interface that «можно
получить, используя метод интерфейса элемента модели <c>ksEntity::GetDefinition</c>»; its
composition is <c>sketchShiftType</c>, <c>SetSketch</c>/<c>GetSketch</c>, <c>PathPartArray</c>,
<c>GetPathLength(bitVector)</c>, <c>Get/SetThinParam</c>. The page marks the interface
<b>deprecated</b> and recommends the glued <c>ksBossLoftDefinition</c> (the text says exactly that -
for a sweep <c>ksBossEvolutionDefinition</c> was expected; the discrepancy inside the help is
recorded as it is). The glued route <c>NewEntity(46)</c> was MEASURED separately and gives the SAME
body (step B5.8), but the mandatory rows of this stage are described as <b>base</b>, so base type 45
is used.
<b>The path is also a sketch, and this is MEASURED, not assumed.</b> Steps B5.1/B5.2: the path
holder returned by <c>ksBaseEvolutionDefinition.PathPartArray()</c> is cast to
<c>ksEntityCollection</c>, and <c>Add(sketch)</c> returns <c>True</c>. A path break is a
<b>refusal</b>, not a partial result.
<b>The orthogonality mode must be checked ON AN ARC.</b> On a straight path
<see cref="SweepShiftMode.Parallel"/> and <see cref="SweepShiftMode.Orthogonal"/> give one body -
MEASURED that on an R50/90° arc they differ by <c>8966.047734774369</c> mm³.
</remarks>

## <a id="loft-route-b5"></a>Маршрут элемента по сечениям (B5)

<summary>Loft: the body is built on an ordered set of sections
(docs/05 SM-05, profile <c>mechanical-core-v1</c>, queue B5).</summary>
<remarks>
<b>The route is API7, and this follows from the set of mandatory rows, not from convenience.</b> The
mandatory row <c>SM-05.base.mode_couplings</c> requires <b>section correspondence chains</b>, and
API5 has none at all: neither <c>ksBaseLoftDefinition</c> nor <c>ksBossLoftDefinition</c> declares
either <c>AddCoupling</c> or <c>Coupling</c>. In API7 they are documented:
<c>iloft_propers.html</c> lists <c>Coupling</c> and <c>CouplingsCount</c>,
<c>iloft_addcoupling.html</c> - <c>AddCoupling()</c> → <c>ICoupling</c>. MEASURED (step B5.9):
<c>AddCoupling()</c> returned <c>KompasAPI7.CouplingClass</c>, <c>CouplingsCount = 1</c>.
<b>The API7 factory is documented for the glued type.</b> <c>ilofts_add.html</c>: «Допустимыми
значениями <c>LoftType</c> являются <c>o3d_bossLoft</c>, <c>o3d_cutLoft</c> для коллекции операций
<c>IModelContainer::Lofts</c>»; «после получения нового интерфейса нужно задать параметры
операции и вызвать метод <c>IModelObject::Update</c>». The sections are set by the
<c>ILoft.Sketchs</c> property of type <c>VARIANT</c> - «массив <c>SAFEARRAY</c> объектов
<c>LPDISPATCH</c>» (<c>iloft_sketchs.html</c>), and this is MEASURED: assigning an array gave a read
of <c>System.Object[]</c> of 2 elements, <c>Update() = True</c>, volume <c>28000</c> - the same
reference as the API5 route (step B5.4).
<b>The section order is set by the caller, and it is itself part of the requirement.</b> With
concentric parallel sections the volume does NOT distinguish the ORDER - MEASURED that a truncated
pyramid 40×40 → 20×20 at h = 30 gives <c>28000</c> either way. So "the order was honoured" is proved
by a distinguishing setup (a different shape or a rotation of the sections, the bounding box, the
face count), not by the volume, and the acceptance row must take this into account.
<b>The volume is computed by the truncated-pyramid formula</b>
<c>V = h/3 · (A₁ + A₂ + √(A₁A₂))</c>, not "average area × height": for 40/20 at h = 30 that is
<c>28000</c> against <c>30000</c>, and the <c>2000</c> mm³ difference is enough to tell the correct
formula from the wrong one.
</remarks>

## <a id="loft-couplings-create"></a>Цепочки соответствия loft

<summary>Section correspondence chains - what makes the mandatory row <c>SM-05.base.mode_couplings</c>
different from "just a body through sections".</summary>
<remarks>
<b>The route is MEASURED, not chosen.</b> Documented: <c>iloft_addcoupling.html</c> -
<c>AddCoupling()</c> returns a pointer to <c>ICoupling</c>; <c>icoupling_count.html</c> -
<c>Count</c> is «Количество сечений в цепочке»; <c>icoupling_positionoffset.html</c> -
<c>PositionOffset(Index)</c> is «Величина смещения точки вдоль контура сечения в мм», where
<c>Index</c> is the section index in the chain. MEASURED (step B5.17, 20.09.2026): on a pyramid
40×40 → 20×20 at h = 30 a chain of two points with offsets <c>0 / 0</c> gives <c>28000</c> -
exactly as without a chain, i.e. an explicit correspondence <b>replaces</b> the automatic one;
shifting the second section's point by <c>25 %</c> of the contour (20 mm of 80) gives
<c>20000</c>, i.e. <b>the chain content is applied</b> (difference 8000 mm³ against a tolerance
of 0.01), and reverting the point returns <c>28000</c>.
<b>The unit is confirmed by a number.</b> The perimeter of the 20×20 section is 80 mm; MEASURED
that <c>PositionOffset = 5</c> reads as <c>Position = 6.25 %</c> - exactly <c>5/80</c>. So the
offset goes out in mm, not as a fraction: the fraction depends on the contour length, which the
caller may not know.
<b>Point coordinates do not go out, and this is a MEASURED decision.</b> <c>ICoupling.SetPoint</c>
is documented, but it is MEASURED that the supplied point <b>is projected onto the contour</b>
and read back <b>in the local coordinates of the section sketch</b>: supplied <c>(10; 10; 30)</c>
(the centre of the 20×20 square), read <c>(20; 10; 30)</c> - the middle of the right side, 10 mm
along the contour. Publishing a parameter whose forward and inverse halves do not coincide would
mean promising a round-trip that does not exist.
<b>Build order.</b> The chain is set BEFORE the first <c>Update()</c> - that is how the factory is
documented («задать параметры операции и вызвать <c>IModelObject::Update</c>»). MEASURED
(step B5.18): a chain set before the first <c>Update()</c> gives <c>CouplingsCount = 1</c> and
volume <c>20000</c> - the same value as a chain added after the build, i.e. one build is enough.
</remarks>

<summary>
Section correspondence chain: one offset per EACH section, in the order of
<see cref="LoftCommand.SectionRefs"/>. The number of offsets must match the number of sections -
this is checked by <c>ICoupling.Count</c> («количество сечений в цепочке», MEASURED: 2 for two
sections).
</summary>

<summary>
Offsets along the section contours, mm - <c>ICoupling.PositionOffset(Index)</c>, where
<c>Index</c> is the section index in the chain. Zero means the start of the contour; this same
value reproduces the automatic correspondence (MEASURED: <c>0 / 0</c> gives <c>28000</c>, as
without a chain).
</summary>

## <a id="shell-route-b5"></a>Маршрут оболочки (B5)

<summary>Shell: a cavity of the given thickness is subtracted from the body, optionally with faces removed
(docs/05 SM-13, profile <c>mechanical-core-v1</c>, queue B5).</summary>
<remarks>
<b>An empty face list does NOT give a closed shell, and this is MEASURED on BOTH APIs.</b> The
order's expectation: without removed faces <c>t = 2</c> inward gives <c>36224</c>
(<c>80000 − 96·76·6</c>). MEASURED on API5 (step B5.6): <c>80000</c>. MEASURED on API7
(step B5.10, four setups on the same 100×80×10 box): <c>79999.99999999999</c> with <b>6 faces</b> -
exactly as the original box, whereas an open shell gives <c>21632</c> with <b>11</b> faces. That is,
<c>Update() = True</c> means "accepted", and an unchanged face count means "not applied"; this is a
second independent sign next to the volume. Therefore an empty face list is <b>refused before
COM</b> with a named refusal rather than passed off as a closed shell.
<b>The wall direction is set by the VALUE, and the correspondence is confirmed twice.</b>
Documentation: <c>ksshelldefinition_thintype.html</c> - «<c>TRUE</c> - внутрь, <c>FALSE</c> -
наружу» for API5. Measurement on API5 (step B5.5): <c>true</c> → <c>21631.999999999996</c>,
<c>false</c> → <c>24832.000000000022</c>. Measurement on API7 (step B5.10):
<c>ThinType = 1</c> → <c>21631.999999999996</c>, <c>ThinType = 0</c> →
<c>24832.000000000022</c>. The half types are different - API7 <c>ThinType</c> is declared
<c>long</c> (in interop - <c>ksDirectionTypeEnum</c>), API5 <c>thinType</c> - <c>bool</c>.
</remarks>

<summary>
Faces removed before the wall is formed - <c>face:</c> references. The list must be
<b>non-empty</b>: an empty one does not give a closed shell (MEASURED on both APIs - the volume
stays <c>80000</c>, the face count <c>6</c>, as for the original body), and such a call is
refused with a named refusal before touching COM.
</summary>

## <a id="shell-tangent-faces"></a>Тангенциальные грани оболочки

<summary>Tangent faces - <c>IShell.SetFaces(Faces, TangentFaces)</c> in API7.</summary>
<remarks>The parameter is <b>declared</b> because it exists in the API7 route and an undeclared parameter is invisible to the product:
<c>additionalProperties: false</c> would reject the call before COM and that would read as "not supported". But <c>true</c> is <b>refused with a named
refusal</b> rather than silently ignored: API5 <c>ksShellDefinition</c> has no "tangent faces" member at all, and this tool's route is API5. Silent
ignoring would give success for work that did not happen - the defect the contract forbids. The mode <c>SM-13.shell.mode_tangent_faces</c> is not part
of the mandatory scope of this stage, so the bound is named rather than hidden.</remarks>

## <a id="loft-read-section-refs"></a>Чтение сечений loft

<summary>Loft parameters read from the model.</summary>
<remarks><see cref="SectionRefs"/> - SECTIONS AS REFERENCES, derived from the feature definition (<c>ksBaseLoftDefinition.Sketches()</c> /
<c>ksBossLoftDefinition.Sketches()</c>, help pages <c>ksbaseloftdefinition_sketches.html</c> and <c>ksbossloftdefinition_sketches.html</c>, returning
<c>ksEntityCollection</c>), not saved since creation. A loft edit changes ONLY the section set (<c>kompas_update_feature</c>, field <c>section_refs</c>),
and has no other currency. A reference issued at creation lives until the first document mutation, and <c>kompas_rebuild</c> revokes all document
references wholesale; the product cannot list sketches separately - <c>kompas_list_features</c> returns only shaping elements
(<c>EntityCollection(o3d_operationElement)</c>), and MEASURED: on a document with two sketches and one loft the tree shows ONE row. Without this field,
editing an existing loft is inexpressible in a new session or after <c>save → close → reopen</c>, where order §11 requires the opposite; <c>null</c> means "not read", an empty list "there are none in the definition" - different states, not merged.</remarks>


<summary>Loft parameters read from the model.</summary>
<remarks><see cref="SectionRefs"/> - SECTIONS AS REFERENCES, derived from the feature definition
(<c>ksBaseLoftDefinition.Sketches()</c> / <c>ksBossLoftDefinition.Sketches()</c>, help pages
<c>ksbaseloftdefinition_sketches.html</c> and <c>ksbossloftdefinition_sketches.html</c>, returning
<c>ksEntityCollection</c>), not saved since creation. A loft edit changes ONLY the section set
(<c>kompas_update_feature</c>, field <c>section_refs</c>) and has no other currency. A reference
issued at creation lives until the first document mutation, and <c>kompas_rebuild</c> revokes all
document references wholesale; the product cannot list sketches separately -
<c>kompas_list_features</c> returns only shaping elements
(<c>EntityCollection(o3d_operationElement)</c>), so on a document with two sketches and one loft the
tree shows ONE row. Without this field, editing an existing loft is inexpressible in a new session
or after <c>save → close → reopen</c>; <c>null</c> means "not read", an empty list "there are none in
the definition" - different states.
History: docs/decisions/contracts.md#loft-read-section-refs</remarks>


<summary>Loft parameters read from the model.</summary>
<remarks><see cref="SectionRefs"/> - SECTIONS AS REFERENCES, derived from the feature definition
(<c>ksBaseLoftDefinition.Sketches()</c> / <c>ksBossLoftDefinition.Sketches()</c>, returning
<c>ksEntityCollection</c>), not saved since creation. A loft edit changes ONLY the section set
(<c>kompas_update_feature</c>, field <c>section_refs</c>). A reference issued at creation lives
until the first document mutation, and <c>kompas_rebuild</c> revokes all document references
wholesale; <c>kompas_list_features</c> returns only shaping elements, so two sketches and one loft
show ONE row. Without this field, editing an existing loft is inexpressible in a new session or
after <c>save → close → reopen</c>. History: docs/decisions/contracts.md#loft-read-section-refs</remarks>


<summary>Loft parameters read from the model.</summary>
<remarks><see cref="SectionRefs"/> - SECTIONS AS REFERENCES, derived from the feature definition
(<c>ksBaseLoftDefinition.Sketches()</c> / <c>ksBossLoftDefinition.Sketches()</c>, returning
<c>ksEntityCollection</c>), not saved since creation. A loft edit changes ONLY the section set
(<c>kompas_update_feature</c>, field <c>section_refs</c>). A reference issued at creation lives
until the first document mutation, and <c>kompas_rebuild</c> revokes all document references
wholesale; <c>kompas_list_features</c> returns only shaping elements, so two sketches and one loft show ONE row.
Without this field, editing an existing loft is inexpressible in a new session or after
<c>save → close → reopen</c>. History: docs/decisions/contracts.md#loft-read-section-refs</remarks>

## <a id="shell-read-face-refs"></a>Чтение удалённых граней оболочки

<summary>Shell parameters read from the model.</summary>
<remarks><see cref="RemovedFaceRefs"/> - REMOVED FACES AS REFERENCES, derived from the feature definition
(<c>ksShellDefinition.FaceArray()</c> → <c>ksEntityCollection</c>), for the same reason as <see cref="LoftDto.SectionRefs"/>: the set of removed faces is
the edit input, and without a reference to the faces removed BY THE FEATURE ITSELF a repeated set edit after a mutation is inexpressible. Faces removed
by the feature are ABSENT from the body topology, so <c>kompas_read_topology</c> has nothing to take them from.</remarks>

## <a id="support-plane-read"></a>Чтение опоры признака B3

<summary>Support of a B3 feature, read FROM THE MODEL: a point, a unit normal, and - separately - the
three construction points the normal is derived from.</summary>
<remarks>The three points are published deliberately. The normal is a DERIVED quantity (a cross
product), and a reader is entitled to see what it was derived from rather than take it on faith.
MEASURED on 18.09.2026 (probe <c>--split</c>, step SP.10): for the plane <c>x = 10</c> the read
returns exactly <c>(10,0,0)</c>, <c>(10,1,0)</c>, <c>(10,0,1)</c> and normal <c>(1,0,0)</c>, and for
the plane <c>x = 15</c> the same three points shifted, so the read distinguishes different
supports.</remarks>

## <a id="hole-no-api5-definition"></a>Отверстие без определения API5

<summary>Native-hole parameters. Also trailing: filled only for <c>family = "hole"</c>. Read
entirely from API7 (<c>IHole3D</c> + <c>HoleParameters</c> cast to its own mode), because in API5 a
hole definition does not exist physically - MEASURED on 16.09.2026: among the 67 definitions
declared in the vendor wrapper there are <c>ksChamferDefinition</c> and <c>ksFilletDefinition</c>,
but no <c>ksHoleDefinition</c>.</summary>

## <a id="solid-feature-numbers"></a>Признаки B3 по номеру в дереве

<summary>B3 feature parameters. Also trailing: filled only for a <c>family</c> of
{<c>boolean</c>, <c>split</c>, <c>cut_by_plane</c>, <c>reposition</c>} and null otherwise. Read from
API7, because these families have no API5 definition at all - <c>entity.GetDefinition()</c> returns
null, and they are recognized by the FEATURE NUMBER IN THE TREE (69 / 633 / 50 / 79), MEASURED on
18.09.2026 with instrument <c>scratch/b3-measure-feature-types.py</c>. What is and is not read was
MEASURED by probes <c>--boolean</c> (BO.2–BO.5, BO.10), <c>--split</c> (SP.10) and
<c>--reposition</c> (RP.8–RP.12); the summary is in docs/04_KOMPAS_API_NOTES.md §4.10.9.</summary>

## <a id="sweep-tree-number"></a>Кинематический элемент: номер в дереве

<summary>Sweep parameters. Also trailing: filled only for <c>family = "sweep"</c> and null otherwise.</summary>
<remarks>Read from the TREE, and the tree type number does not equal the creation number. MEASURED on 20.09.2026 (probe <c>--b5</c>, step B5.12):
a feature created by <c>NewEntity(45)</c> (<c>o3d_baseEvolution</c>) appears in the tree under number <b>46</b> (<c>o3d_bossEvolution</c>) and its
definition answers <c>ksBossEvolutionDefinition</c> - <b>not</b> <c>ksBaseEvolutionDefinition</c>. Same class of discrepancy already measured for the
hole (created 52, in tree 583) and rotation (created 27, in tree 584): the family is recognized BY THE DEFINITION INTERFACE (both accepted), not by
the creation type number. Read values at step B5.12: <c>sketchShiftType</c> = 2 (written orthogonal), <c>PathPartArray()</c> = 1 part,
<c>GetPathLength(1)</c> = 100 mm for a 100 mm path, <c>GetSketch()</c> returns an object. <c>OperationResult</c> lives only in API7 (<c>IEvolution</c>):
the value <b>1</b> was read from the document collection; if unavailable or empty the field stays null - "not read", not zero.</remarks>


<summary>Sweep parameters. Also trailing: filled only for <c>family = "sweep"</c> and null
otherwise.</summary>
<remarks>Read from the TREE, and the tree type number does not equal the creation number: a
feature created by <c>NewEntity(45)</c> (<c>o3d_baseEvolution</c>) appears in the tree under
number <b>46</b> (<c>o3d_bossEvolution</c>) and its definition answers
<c>ksBossEvolutionDefinition</c> - <b>not</b> <c>ksBaseEvolutionDefinition</c>. The same class of
discrepancy is measured for the hole and rotation: the family is recognized BY THE DEFINITION
INTERFACE, not by the creation type number. <c>OperationResult</c> lives only in API7
(<c>IEvolution</c>): if unavailable or empty the field stays null - "not read", not zero.
History: docs/decisions/contracts.md#sweep-tree-number</remarks>

## <a id="loft-tree-number"></a>Элемент по сечениям: номер в дереве

<summary>Loft parameters. Trailing: only for <c>family = "loft"</c>.</summary>
<remarks>MEASURED on 20.09.2026 (step B5.12): a feature created by the adapter route
(<c>ILofts.Add(o3d_bossLoft = 31)</c>) appears in the tree under the same number <b>31</b>, and its
definition answers the interface <c>ksBossLoftDefinition</c>. The parameters are read from the
<b>document collection</b> (<c>IModelContainer.Lofts</c> → <c>ILofts</c>), not from the creation
handle: <c>Count</c> = 1, <c>Loft(0)</c> returned <c>Sketchs</c> = 2 elements, <c>Closed</c> =
False, <c>CouplingsCount</c> = 0, <c>BuildingType(true)</c> = 0.</remarks>

## <a id="shell-tree-number"></a>Оболочка: номер в дереве

<summary>Shell parameters. Trailing: only for <c>family = "shell"</c>.</summary>
<remarks>MEASURED on 20.09.2026 (step B5.12): a feature created by <c>NewEntity(43)</c> appears in
the tree under number <b>43</b> (<c>o3d_shellOperation</c>), its definition answers
<c>ksShellDefinition</c>, and from it are read <c>thickness</c> = 2, <c>thinType</c> = true,
<c>FaceArray</c> = 1 face - exactly what was written. From API7 (<c>IShells</c> → <c>IShell</c>) the
same quantities appear as <c>Thickness</c> = 2, <c>ThinType</c> = <c>dt_reverse</c> (inward),
<c>DeletedFaces</c> = 1: two independent halves of one setup.</remarks>

## <a id="sketch-status-mapping"></a>Соответствие состояний эскиза

<summary>Normalized sketch-definiteness status - what the server actually read, not what it assumed.</summary>
<remarks>The mapping to native states is MEASURED, not assigned (probe S, run <c>82880ed0</c>; enum values checked at step S.3):
<c>ksStateWellConstrained</c> (1) → <see cref="FullyDefined"/>; <c>ksStateUnderConstrained</c> (2) → <see cref="UnderDefined"/>;
<c>ksStateUnknown</c> (0) → <see cref="Unknown"/> - KOMPAS itself did not establish a status (obtained both on an empty sketch and
on 11 shipped sketches); <c>ksStateUnresolvedRedundancy</c> (3) → <see cref="NeedsAttention"/> - declared, but live control was
<b>not obtained</b>, published conservatively (see <see cref="SketchStatusResult.Limitations"/>). The API does not return degrees of
freedom: <c>ISketch.ConstraintsState</c> returns a status, not a counter, so <see cref="SketchStatusResult.DegreesOfFreedom"/> is always
<c>null</c>, and deriving it from the number of dimensions is forbidden: constraints relate objects to each other rather than summing.</remarks>


<summary>Normalized sketch-definiteness status - what the server actually read, not what it
assumed.</summary>
<remarks>MEASURED mapping to native states: <c>ksStateWellConstrained</c> (1) →
<see cref="FullyDefined"/>; <c>ksStateUnderConstrained</c> (2) → <see cref="UnderDefined"/>;
<c>ksStateUnknown</c> (0) → <see cref="Unknown"/> (KOMPAS itself did not establish a status);
<c>ksStateUnresolvedRedundancy</c> (3) → <see cref="NeedsAttention"/> - declared, but live control
was <b>not obtained</b>, published conservatively (see
<see cref="SketchStatusResult.Limitations"/>). The API does not return degrees of freedom:
<c>ISketch.ConstraintsState</c> returns a status, not a counter, so
<see cref="SketchStatusResult.DegreesOfFreedom"/> is always <c>null</c>, and deriving it from the
number of dimensions is forbidden.
History: docs/decisions/contracts.md#sketch-status-mapping</remarks>


<summary>Normalized sketch-definiteness status - what the server actually read, not what it assumed.</summary>
<remarks>MEASURED mapping to native states: <c>ksStateWellConstrained</c> (1) →
<see cref="FullyDefined"/>; <c>ksStateUnderConstrained</c> (2) → <see cref="UnderDefined"/>;
<c>ksStateUnknown</c> (0) → <see cref="Unknown"/>; <c>ksStateUnresolvedRedundancy</c> (3) →
<see cref="NeedsAttention"/> - declared, but live control was <b>not obtained</b>, published
conservatively (see <see cref="SketchStatusResult.Limitations"/>). The API returns no degrees of
freedom: <c>ISketch.ConstraintsState</c> returns a status, not a counter, so
<see cref="SketchStatusResult.DegreesOfFreedom"/> is always <c>null</c> and is not derived.
History: docs/decisions/contracts.md#sketch-status-mapping</remarks>

## <a id="sketch-status-result"></a>Снимок определённости эскиза

<summary>Snapshot of sketch definiteness. <see cref="IsFullyDefined"/> is nullable deliberately: <c>null</c> means "not reliably established", and substituting <c>false</c> for it is forbidden - "indeterminate" and "under-defined" are different answers.</summary>


<summary>Snapshot of sketch definiteness. <see cref="IsFullyDefined"/> is nullable deliberately:
<c>null</c> means "not reliably established", and substituting <c>false</c> for it is forbidden -
"indeterminate" and "under-defined" are different answers.</summary>
<param name="DefinitionStatus">Normalized status.</param>
<param name="IsFullyDefined">Only <c>true</c>/<c>false</c>/<c>null</c>; <c>null</c> for <see cref="SketchDefinitionStatus.Unknown"/>
and <see cref="SketchDefinitionStatus.NeedsAttention"/>.</param>
<param name="DegreesOfFreedom">Always <c>null</c>: the confirmed route returns a status, not a number. Not derived from the number
of dimensions and not substituted with zero.</param>
<param name="Diagnostics">Understandable reasons: why the status is what it is and what prevented it.</param>
<param name="Limitations">Bounds of the answer's reliability, named explicitly.</param>

## <a id="cut-plane-shape"></a>Форма cut_plane

<summary>Plane for operations B3: an existing support, a point + normal, or a base plane with an offset.</summary>
<remarks>The three ways are mutually exclusive, and this is checked before the COM call. The plane side is NOT set here: it is set by the sign
<c>s = n·(p − p₀)</c> in the cut command itself, because "the left side" without a coordinate system is not an address. The normal must be non-zero and
finite; the point and the normal are in MODEL coordinates, mm. The field shape matches the PUBLISHED <c>cut_plane</c> schema literally - MEASURED on
19.09.2026 by client acceptance B3 (three FAIL rows: <c>B3C.neg.plane_base_declared</c>, <c>B3C.08.plane_base.cut_by_plane</c>, <c>B3C.08.plane_base.split</c>):
the schema published <c>base</c> as the string <c>xy|xz|yz</c> and <c>offset_mm</c> as a number, while the DTO expected a <c>PlaneRefDto</c> OBJECT - the shape
diverged by one nesting level, so the declared <c>CAPABILITY_UNAVAILABLE</c> was unreachable and the call failed parsing (<c>JsonException</c> at <c>$.plane.base</c>,
<c>VERIFICATION_FAILED</c>); the field was brought to the published shape. <c>offset_mm</c> without <c>base</c> is an argument error, not silence.</remarks>


<summary>Plane for operations B3: an existing support, a point + normal, or a base plane with an
offset.</summary>
<remarks>The three ways are mutually exclusive, and this is checked before the COM call. The plane
side is NOT set here: it is set by the sign <c>s = n·(p − p₀)</c> in the cut command itself, because
"the left side" without a coordinate system is not an address. The normal must be non-zero and
finite; the point and the normal are in MODEL coordinates, mm. INVARIANT: the field shape matches
the PUBLISHED <c>cut_plane</c> schema literally - MEASURED that a divergence by one nesting level
made the declared <c>CAPABILITY_UNAVAILABLE</c> unreachable and the call failed parsing
(<c>JsonException</c> at <c>$.plane.base</c>, <c>VERIFICATION_FAILED</c>). <c>offset_mm</c> without
<c>base</c> is an argument error, not silence.
History: docs/decisions/contracts.md#cut-plane-shape</remarks>

## <a id="cut-plane-form-rule"></a>Правило формы CutPlaneDto

<summary>The shape rule of <see cref="CutPlaneDto"/> - in one place and without COM, so it can be checked by a test rather than only by acceptance on a live model.</summary>
<remarks>The priority is declared and does not depend on the order of fields in JSON: <see cref="CutPlaneFormVerdict.ModesConflict"/> - <c>base</c> together with
<c>plane_ref</c> or a point with a normal, checked FIRST (the request is contradictory, and a declared capability refusal would hide that two ways were named at
once); <see cref="CutPlaneFormVerdict.BaseUnsupported"/> - <c>base</c> named alone; <see cref="CutPlaneFormVerdict.OffsetWithoutBase"/> - an offset without a
base plane; <see cref="CutPlaneFormVerdict.Ok"/> - either <c>plane_ref</c> or a point with a normal. The mutual exclusion "<c>plane_ref</c> versus a point with a
normal" is NOT checked here: a feature edit has its own reason to reject a reference (the edit route transfers the points of its OWN support), and the route
must name it itself.</remarks>


<summary><c>offset_mm</c> without <c>base</c> - the offset does not express a plane: <c>INVALID_ARGUMENT</c>.</summary>


<summary>The shape rule of <see cref="CutPlaneDto"/> - in one place and without COM, so it can be
checked by a test rather than only by acceptance on a live model.</summary>
<remarks>The priority is declared and does not depend on the order of fields in JSON:
<see cref="CutPlaneFormVerdict.ModesConflict"/> - <c>base</c> together with <c>plane_ref</c> or a
point with a normal, checked FIRST (the request is contradictory, and a declared capability refusal
would hide that two ways were named at once); <see cref="CutPlaneFormVerdict.BaseUnsupported"/> -
<c>base</c> named alone; <see cref="CutPlaneFormVerdict.OffsetWithoutBase"/> - an offset without a
base plane; <see cref="CutPlaneFormVerdict.Ok"/> - either <c>plane_ref</c> or a point with a normal.
The mutual exclusion "<c>plane_ref</c> versus a point with a normal" is NOT checked here: a feature
edit has its own reason to reject a reference, and the route must name it itself.
History: docs/decisions/contracts.md#cut-plane-form-rule</remarks>

## <a id="boolean-command-contract"></a>Контракт BooleanCommand

<summary>Boolean operation on bodies: explicit target, explicit tool list, operation kind and tool-keeping
policy (docs/05 SM-15).</summary>
<remarks>Addressing is by references only. Neither "the current window", nor "index 0", nor the collection
order is a target: the declared body is searched among the BodyCollection elements by IUnknown, and
when there is no match the call is refused rather than a position being substituted.
Checks the core does NOT make and therefore the contract does: the target is not part of the tool
set; the set has no repeats; the list is non-empty. The core accepts a repeated reference silently
(MEASURED on 18.09.2026, step BO.9), so it must not be relied on.</remarks>

## <a id="solid-body-dto"></a>Тело результата B3

<summary>Body in the result of operation B3: reference, volume, bounding box and a multi-piece flag.</summary>
<remarks>A separate record, not <c>BodyRowDto</c>, for two reasons. First, acceptance B3 is counted by VOLUMES, and <c>BodyRowDto</c> carries no volume.
Second, <c>MultiBodyParts</c> is no decoration here: the core represents a result of several pieces as ONE body with several pieces (MEASURED on
18.09.2026, step BO.8), and without this field "one body" would read as "material intact". <c>VolumeMm3</c> is <c>null</c> when the volume was not read:
"not read" and "zero" are different answers and must not be mixed.</remarks>

## <a id="export-image-route"></a>Маршрут растрового снимка

<summary>Raster snapshot of the current session's model by the documented API5 route: <c>ksDocument3D.RasterFormatParam()</c> → <c>ksRasterFormatParam</c> →
<c>ksDocument3D.SaveAsToRasterFormat(fileName, rasterPar)</c>.</summary>
<remarks>The two route modes are MUTUALLY EXCLUSIVE, and this is MEASURED (probe P2b of the order, delivery <c>publish-deproutes-r2-20260921</c>): with a
NON-EMPTY file name the core writes the file and <c>resultArrayBytes</c> stays null (six call shapes differing in the order of member writes, a pre-supplied
array, a second write method and a re-read of the property - all gave null); with an EMPTY file name the core returns <c>System.Byte[]</c> (8639 bytes, PNG
magic) and does NOT create a file at all. So "return the picture" and "write the file" are two different route calls, not one with two consequences. Fields
not confirmed by the probe are not part of the contract: <c>IViewProjection7</c> (projection control) is documented but not implemented by this order.
</remarks>


<summary>Raster snapshot of the current session's model by the documented API5 route:
<c>ksDocument3D.RasterFormatParam()</c> → <c>ksRasterFormatParam</c> →
<c>ksDocument3D.SaveAsToRasterFormat(fileName, rasterPar)</c>.</summary>
<remarks>MEASURED: the two route modes are MUTUALLY EXCLUSIVE - with a NON-EMPTY file name the core
writes the file and <c>resultArrayBytes</c> stays null; with an EMPTY file name the core returns
<c>System.Byte[]</c> (PNG magic) and does NOT create a file at all. So "return the picture" and
"write the file" are two different route calls, not one with two consequences.
The optional <c>view</c> field carries a projection by ASCII wire name; the kernel is addressed by
the <c>ksViewProjectionType</c> code, because the kernel's own names ("#Спереди") are localized. An
unknown name is REFUSED, not defaulted: a substituted view is indistinguishable to the caller from
the one requested. The applied projection is confirmed by a READ-BACK of the type, never by
<c>SetCurrent</c>'s return value, and the previous view is restored after the snapshot unless
<c>keep_view</c> is set. MEASURED: a projection change does not raise <c>IKompasDocument.Changed</c>,
so the document revision is untouched by a snapshot.
History: docs/decisions/contracts.md#export-image-route</remarks>

## <a id="view-projection"></a>Проекции вида: имена, типы, публикуемый перечень

<summary>Projection of a snapshot: the published ASCII wire names and the <c>ksViewProjectionType</c> codes they resolve to.</summary>
<remarks>DOC: <c>ksviewprojectiontype.html</c> - <c>ksVPNone -1 … ksVPIsometric 7</c>, <c>ksVPDimetric 8</c>,
<c>ksVPUnfold 9</c>, <c>ksVPUser 10</c>. The API7 numbers do NOT match the API5 <c>ProjectionType</c> numbers
(three isometries there, one here) and the correspondence is not implied by the help page.
PUBLISHED: front 1, rear 2, up 3, down 4, left 5, right 6, isometric 7, dimetric 8. LIMIT: the published list is the
SUBSET the probe found live in a part's collection plus <c>dimetric</c>, which is the CURRENT projection of a freshly
created part (documented <c>ksVPDimetric 8</c>); <c>ksVPUser</c> (10) is deliberately absent because a user projection
has no fixed type to address. A request for a type the collection does not carry refuses <c>VIEW_UNAVAILABLE</c> rather
than snapping the current view under a false label. <c>dimetric</c> was added after leaving it out made the previous
view of every fresh document UNRESTORABLE, so every first <c>view</c> call refused. Wire names are ASCII so that a
product language change cannot break the contract.
History: docs/decisions/contracts.md#view-projection</remarks>

## <a id="sketch-ref-readback"></a>Ссылка на эскиз из признака

<summary>Reference to the sketch this feature is built on, when it is an extrusion and the sketch can be read back through <c>GetSketch()</c>. Null for
feature families without a sketch and when the read-back fails.</summary>
<remarks>This closes the long-standing <c>sketch_reference_not_resolved</c> gap: before it, a sketch that arrived with a reopened document could be
neither named nor edited, because nothing handed the caller a reference to it. It is the missing half of the model-derived sketch route - the
coordinate can now be derived, and this is how the sketch to edit is found in the first place. It is a plain reference minted against the current
revision, so the usual staleness rules apply unchanged (it dies on the next rebuild like any other handle).</remarks>

## <a id="assembly-contracts"></a>Assembly-domain contracts

<summary>Assembly-domain contracts - block C1 (profile <c>assemblies-minimal-v1</c>, modes <c>ASM-01…ASM-07</c>).</summary>
<remarks>INVARIANT: a component is a reference to a file, not a body. What leaves the server is therefore structure -
the component instance, its source file, placement and multiplicity. Two insertions of one part yield TWO instances
of ONE unique part: this is the substantive criterion that separates an assembly from a composition of bodies in one part.
INVARIANT: placement is a rigid transform (<see cref="TransformDto"/>: origin plus two orthonormal axes), not a
"shift from current" - a shift without a coordinate frame is not an address and cannot be read back.</remarks>

## <a id="component-body-count"></a>Component body count

<summary>Number of bodies of the component - <c>ksPart.BodyCollection()</c>
(<c>kspart_bodycollection.html</c>). Published because "the component exists" and "the component
has geometry" are different claims: insertion via <c>CreatePartInAssembly</c> produced a component
with ZERO bodies. MEASURED: 05.10.2026.</summary>

## <a id="paged-result-token"></a>Continuation token

Cursor-bounded list (spec 2.3). A cursor is bound to a revision. (The word "cursor" here means the
pagination continuation token, not any editor.)

## <a id="chamfer-mode"></a>Chamfer mode

<summary>Chamfer construction method (docs/05 SM-11). The method must be named by the caller because
<see cref="DistanceAngle"/> is physically unavailable in API5: there is no angle in either
<c>ksChamferDefinition</c> or <c>SetChamferParam(transfer, d1, d2)</c> - MEASURED by probe F on v24
(12.09.2026).</summary>

## <a id="loft-building"></a>Loft building

<remarks>DOC: ksloftbuildingtype.html - the numbers were read from the official SDK v24 help (over the
wire 20.09.2026): <c>ksLoftAuto = 0</c>, <c>ksLoftByNormal = 1</c>, <c>ksLoftByObject = 2</c>,
<c>ksLoftCupola = 3</c>.</remarks>

## <a id="pattern-copy-kind"></a>Pattern copy kind

<summary>What exactly a pattern copies (docs/05 SM-18/SM-19; user help <c>48_3_1_vibor_kopiruemih_obtktov</c>).</summary>
<remarks>DOC: copytype.html - the value maps to the numeric <c>ksObj3dTypeEnum</c>, and that mapping is
published by the SDK page, not inferred: operations - <c>o3d_meshCopy=35</c> (<c>o3d_circularCopy=36</c>),
bodies - <c>o3d_BodiesMeshCopy=528</c> (<c>o3d_BodiesCircularCopy=529</c>).
INVARIANT: the difference is substantive, not cosmetic - a pattern of OPERATIONS inherits the scope of
the source operation and creates no new bodies (help <c>48_2_osobennoiti_postroeniy_massiviv_v_mnogotelnoy_detali</c>),
while a pattern of BODIES creates body copies and the body count grows. Hence a separate mode, not a flag.</remarks>

## <a id="chamfer-angle"></a>Chamfer angle

<summary>Chamfer angle in DEGREES - MEASURED by probe F.10 (Angle=30 with Distance1=2 removed
20·d·(d·tg 30°) = 46.188021535141 mm³; the radian hypothesis was rejected by the number).
Mandatory for distance_angle, forbidden for two_distances.</summary>

## <a id="hole-pilot-diameter"></a>Hole pilot diameter

<summary>Hole diameter, mm: for counterbore and countersink this is the PILOT diameter, not the recess or the mouth.</summary>

## <a id="rotated-profile-ref"></a>Rotated profile ref

<summary>Profile sketch: an explicit <c>sketch:</c> reference from kompas_create_sketch / kompas_get_feature.</summary>

## <a id="pattern-circular-building"></a>Pattern circular building

<summary>Build method: <c>save_all</c> (0), <c>chess_order_by_axis1</c> (1), <c>chess_order_by_axis2</c> (2).</summary>

## <a id="suppress-route"></a>Suppress route

<summary>
Suppress or restore a feature. Route MEASURED by probe L.7 (12.09.2026):
<c>ksFeature.excluded = true</c> removes the extrusion body down to the plate volume,
<c>false</c> restores the volume; the feature count does not change.
</summary>

## <a id="boolean-result-structure"></a>Boolean result structure

<summary>Actual structure of a boolean result. There are no promises about the body count: the core
represents a result of several pieces as ONE body with several pieces (MEASURED on 18.09.2026, step
BO.8: V = 18 000, <c>MultiBodyParts = true</c>, 12 faces for two pieces of 6).</summary>

## <a id="cut-plane-checks"></a>Cut plane checks

<summary>
The checks the verdict rests on: addressing (material removed from the NAMED body) and the
integrity of unrelated bodies. They are published separately from
<see cref="UnverifiedAspects"/>, because an empty limitation list with a vanished unrelated body
would read as "fully verified" - this is exactly how client acceptance on 19.09.2026 got a false
<c>geometry_checked</c> (defect <c>CUT-PLANE-APPLIED-TO-UNNAMED-BODIES</c>).
</summary>

## <a id="contracts-comment-compaction"></a>Сжатие комментариев контрактов

**Что сделано.** Комментарии в `src/KompasMcp.Contracts/` приведены к минимуму «правило + почему +
источник». История, имена проб, номера открытых вопросов и подробности измерений, от которых правило
не зависит, вынесены сюда; в коде осталась одна строка `History:` на соответствующий маршрутный
раздел.

**Удалённые исторические маркеры (verbatim).** `probe P6`, `probe P6 on a separate line`,
`probe P2.1`, `probe F`, `probe L.7`, `probe L.8`, `this lifted blocker OQ-A18`, `OQ-A19`,
`OQ-B-02`, `OQ-B-03`, `step B5.9`, `profile mechanical-core-v1`, `queue B4`.

**Удалённые цитаты справки (verbatim).** «Свойство работает ТОЛЬКО для <c>o3d_mirrorAllOperation</c>»;
«у других операций зеркального копирования возможность скрыть экземпляры отсутствует»; «Основание -
кинематический элемент (Интерфейсы ksBaseEvolutionDefinition, IBaseEvolutionDefinition)»; «Величина
смещения точки вдоль контура сечения в мм»; «задать параметры операции и вызвать
<c>IModelObject::Update</c>».

**Удалённые подробности измерений (verbatim).** Присвоение массива сечений даёт объём <c>28000</c>,
тот же, что и у маршрута API5; при концентрических параллельных сечениях объём НЕ различает порядок,
поэтому «порядок соблюдён» доказывается различающей постановкой (форма, габаритный бокс, число
граней). Пустой список граней оболочки не даёт замкнутой оболочки - объём остаётся объёмом исходного
бокса, а число граней не меняется, тогда как открытая оболочка меняет и то и другое; значит
<c>Update() = True</c> - это «принято», а неизменное число граней - «не применено».

## <a id="selection-predicate-coordinate-space"></a>Поле `coordinate_space` убрано из предиката выбора граней (07.10.2026)

**Что было.** `$defs/selection_predicate` объявлял `coordinate_space` с `enum ["parent","assembly_world"]`,
а `SelectionPredicateDto` нёс ещё `extremum_axis`/`extremum_mode` и `bbox_range_mm`, которых в
опубликованной схеме не было. `kompas_resolve_selection` отвергал ЛЮБОЕ значение `coordinate_space`
кодом `INVALID_ARGUMENT` (`details {unsupported:[coordinate_space]}`) и так же отвергал `extremum_*` и
`bbox_range_mm`. Схема обещала поле, не работавшее ни при одном значении.

**Что измерено.** Клиентская приёмка выпуска 0.1.0 (`CLIENT_ACCEPTANCE_REPORT_0_1_0_20261007.md`,
«Названное несделанным», п. 1): вызов с `coordinate_space=parent` отвергнут честным
`INVALID_ARGUMENT`. Прибор читает грань от ссылки на ТЕЛО ДЕТАЛИ и сравнивает нормаль в координатах
этой детали — другой системы координат у него нет, поэтому `assembly_world` не имеет смысла, а `parent`
совпадает с тем, что инструмент делает всегда.

**Что решено.** Поле удалено из контракта целиком: из `ToolCatalog.SelectionPredicateSchema`, из
`SelectionPredicateDto`, из перечисления `CoordinateSpace` (иных использований у него не было) и из
ветки отказа `UnsupportedPredicateFields` (вместе с самой ветвью — после удаления полей она не
отвергала ничего). Мёртвые поля `extremum_*`/`bbox_range_mm` удалены из DTO по той же причине: их
единственным читателем была эта ветка, а до DTO они дотянуться не могли — объект схемы закрыт
(`additionalProperties=false`), и Хост валидирует аргументы против опубликованной схемы ДО отправки в
Worker. Теперь неизвестное поле предиката отвергается СХЕМОЙ как лишнее, а не веткой сервера.
Обоснование «одно осмысленное значение — не выбор»: оставить `parent` значило бы продолжать обещать
выбор, которого нет. `assembly_world`/`extremum`/`bbox` в этом наряде НЕ реализуются.

## <a id="create-mate-alignment-criterion"></a>`kompas_create_mate`: выравнивание подтверждается геометрией (07.10.2026)

**Что было.** Поле `alignment` объявлялось вместе со списком подтверждённых сочетаний «тип × значение», и
`kompas_create_mate` отвергал ЛЮБОЕ иное сочетание кодом `INVALID_ARGUMENT` ДО создания сопряжения. Список
был снят на одной геометрии и по одному признаку — перечитанному числу. Измерение MAL показало, что
запрошенное `opposite` у `coincidence` читается обратно как `closest`, но грани встают навстречу
(`s ≈ −1`): сочетание ВЫПОЛНЯЕТСЯ, а карта его запрещала — отказ был ЛОЖНЫМ.

**Что решено.** Карта удалена из контракта целиком — из `ToolCatalog` (описание поля), из Хоста (отказ до
создания) и из Domain (`MateAlignmentPolicy.AllowedValuesFor`). Подтверждение перенесено на ИЗМЕРЕННУЮ
ориентацию: явные значения `opposite`/`cooriented` обязаны дать `s ≈ −1`/`+1`, иначе отказ
`GEOMETRY_FAILED` с `partial_effects=true`. `closest` не проверяется ни числом, ни ориентацией.
Перечитанное число возвращается в ответе (`alignment_read`) как ФАКТ и основанием отказа не является.
Поле `alignment` в схеме сохранено с переписанным описанием; значения `enum` не менялись.

**Что это значит для вызывающего.** Ответ на `coincidence + opposite` теперь УСПЕХ (было
`INVALID_ARGUMENT`), при этом `alignment_read` может не совпадать с запрошенным — это факт, а не ошибка.
Детали и таблица измерений — `docs/decisions/mates.md#alignment-geometry-criterion`.

## <a id="description-units"></a>Единицы в описаниях полей: единица называется один раз и своя (07.10.2026)

**Что было.** Помощник `Sch.PositiveMm(what)` дописывал к своему аргументу «, мм. Только конечное
положительное значение.», и часть вызовов передавала в `what` уже готовую фразу с единицей или с точкой
на конце. Опубликованное описание читалось как «Плотность материала, мм» (`kompas_measure.density_kg_per_m3`),
«Первый катет (или расстояние для distance_angle), мм, мм» (`kompas_chamfer.distance1_mm`) и
«Новая глубина. Только вместе с end_condition=blind., мм» (`kompas_update_feature.depth_mm`).

**Что измерено.** Прибор `ToolDescriptionUnitsTests` проходит по ВСЕМ опубликованным схемам, разрешая
`$ref` и `anyOf`, и до правки находил 23 нарушения: единица не названа или названа дважды. Кроме трёх
названных в наряде это были `kompas_hole` (шесть полей), `kompas_update_feature` (одиннадцать),
`kompas_rotated.thin_wall_mm`, `kompas_extrude.depth_mm`, `kompas_add_dimension.value_mm` (единица не
названа вовсе) и `kompas_update_feature.expected_bbox_mm` (описание не доходило до поля: `$defs/bbox`
описания не имел).

**Что решено.** Единица стала ПАРАМЕТРОМ помощника: `Sch.Positive(what, unit, note)` пишет
«{что}, {единица}. {уточнение} Только конечное положительное значение.», `Sch.PositiveMm` — его частный
случай для мм. Уточнение (`note`) ставится ПОСЛЕ предложения о единице, поэтому попасть между величиной и
её единицей больше не может по построению. Плотность описана через `Sch.Positive("Плотность материала",
"кг/м³")` — не через `PositiveMm`. `$defs/bbox` получил описание с единицей. Единица в приборе
сопоставляется КАК СЛОВО, а не как подстрока: «180 мм³» в предложении об измеренном свидетельстве не
является утверждением о единице поля, и подстрочная проверка требовала бы вычистить из описаний
измеренные числа. После правки прибор находит 0 нарушений; отрицательный контроль (подставленная схема с
«Плотность, мм» и схема с «мм, мм») падает, как и должен.

## <a id="operation-id-format"></a>`operation_id`: формат назван в описании, а не только в `$defs` (07.10.2026)

**Что было.** `format: uuid` и `minLength/maxLength 36` лежали только в `$defs/operation_id`, а
инструменты ссылались на него через `$ref` (у `kompas_release_session` — через `anyOf[$ref, null]`).
Клиент, сворачивающий схему в сигнатуру, терял и то, и другое: поле выглядело как `operation_id: string`.

**Что измерено.** Клиентский агент (отчёт `mcp-bugs.md`, OBS-003) послал `"omega-phase1-connect"` и
получил ожидаемый `INVALID_ARGUMENT` — отказ верный, но узнать требование из видимой сигнатуры было
неоткуда.

**Что решено.** В описание `$defs/operation_id` добавлены формат («UUID v4, 36 символов»), пример
значения и правило повторного использования: новый id на каждую новую операцию, тот же — только для
повтора той же операции. Серверная проверка (`ToolInvoker`) не менялась. Тест
`OperationId_ResolvedDescriptionNamesTheUuidFormat` разрешает ссылку у КАЖДОГО инструмента с этим полем и
требует слова «UUID» и `format: uuid` в разрешённой схеме — проверка идёт по разрешённой схеме, а не по
тексту ссылки.

## <a id="feature-count-scope"></a>Охват `kompas_list_features` и `feature_count` назван (07.10.2026)

**Что было.** Описание обещало «Признаки дерева модели», а `ListFeatures` обходит только коллекцию
`EntityCollection(o3d_operationElement = 110)`. Эскиз в неё не входит: он отдельный тип дерева
(`o3d_sketch = 5` по `obj3dtype.html`). Клиент создал эскиз с окружностью R52 и получил `list_features = []`
при `feature_count = 0` (отчёт `mcp-bugs.md`, MCP-004) — расхождение описания с охватом, а не потеря
геометрии.

**Что решено.** Поведение не менялось: на `feature_count` держатся проверки `feature_count_unchanged` в
`Api5Session.SolidOps.cs`, а эскизы добавлять в перечисление признаков наряд запрещает. Исправлено
ОПИСАНИЕ: `kompas_list_features` называет коллекцию по типу и цитирует справку
(`obj3dtype.html`: `o3d_operationElement = 110` — «Операции (от o3d_baseExtrusion до o3d_cylindricSpiral)»,
`o3d_sketch = 5` — «эскиз»), прямо говорит, что эскизы не входят, и называет инструмент, который их
перечисляет (`kompas_list_sketches`, B3 того же наряда). `kompas_get_context` объясняет, что `feature_count`
считает те же операции и эскизы в него не входят.

## <a id="circular-orientation-default"></a>Ориентация экземпляров кругового массива: умолчание `false` и его смысл в описании (08.10.2026)

**Что было.** `PatternCircularCommand.SaveInitialOrientation` объявлялся как `bool ... = true`, а описание поля в схеме говорило только «Ориентация экземпляров массива (SaveInitialOrientation). Член есть только у кругового массива» — ни смысла true/false, ни умолчания. Клиент, не передавший поле, получал экземпляры, которые **только переносились по окружности и не доворачивались вокруг оси**: вызов отвечал `succeeded`, `error = null`, а геометрия для любого несимметричного источника (паз, впадина зуба, рычаг) была неверной.

**Что измерено.** Клиентский агент (задача Omega 8800, CP05-pre; записи `OBS-014` и разбор ревьюера `MCP-015` в журнале клиента Omega 8800, `Analysis/mcp-bugs.md`) построил диск R = 20 × 2 мм, вырезал насквозь прямоугольник от (19; −0,5) шириной 2 и высотой 1 и сделал круговой массив выреза `count2 = 73`, `step2_deg = 360/73` вокруг Z, не передав `save_initial_orientation`. Независимое `measure` дало **2 373,6071781961587 мм³** и габарит Y **±19,4953700396**; ожидание радиального массива — **2 367,578 мм³**, Y ±20,000. Ревьюер воспроизвёл оба варианта моделью: «копии повёрнуты» — 2 367,578 (Y ±19,9999), «копии только перенесены» — 2 373,607 (Y ±19,4954), то есть **факт совпал с перенесённым вариантом**: ядро КОМПАС отработало ровно так, как велел флаг, а неверным было умолчание MCP. Прибор `mcp-smoke.py` этого не ловил: все его проверки кругового массива (`B4M.05`, `B4M.06`, `B4M.07`) работают на симметричном источнике (сквозное отверстие), у которого ориентация объём не меняет.

**Справка.** `icircularpattern_saveinitialorientation.html` (v24): «Тип данных: BOOL»; значения — «TRUE - сохранять исходную ориентацию, FALSE - доворачивать до радиального направления». **Умолчание справка не называет** — оно объявлено решением продукта. Сводная страница `icircularpattern_props.html` называет свойство «Ориентация экземпляров массива».

**Что решено.** Умолчание — `false` (доворот до радиального направления): это обычный смысл кругового массива для отверстий, пазов и зубьев, и вызов без параметра даёт ожидаемую геометрию. Умолчание живёт **в одном месте** — константа `PatternCircularCommand.DefaultSaveInitialOrientation`; сама команда хранит `bool?`, чтобы «клиент не передал поле» осталось выразимым, а ответ мог назвать применённое значение и признак `save_initial_orientation_defaulted`. Цепочка `Host → Contracts → Api5Session.Pattern → Api7Pattern` второго умолчания не вводит: Host передаёт аргументы как есть (`CanonicalNode` — `DeepClone`), адаптер применяет умолчание один раз. Правка признака (`PatternEditDto`) умолчания не применяет: там `bool?` без значения по умолчанию, и записывается только переданное значение.

**Ответ стал прозрачным.** Создание кругового массива несёт `save_initial_orientation` (значение, **перечитанное** с признака) и `save_initial_orientation_defaulted` (передал ли клиент поле); проверка `read_back_save_initial_orientation` сравнивает записанное значение с перечитанным и отказывает при расхождении — то же правило, что у правки. Это новое **необязательное** поле: клиент, читающий только `readout`, ничего не теряет.

**Побочно измерено.** Схема объявляла поле допускающим `null`, тогда как команда принимала непустой `bool`: легальный `null` был бы ошибкой разбора payload. Тест `NonNullableBool_CannotReceiveJsonNull_SoTheNullableFormIsRequired` измеряет это на самом `System.Text.Json`, а не предполагает. Nullable-объявление снимает расхождение.

**Чего НЕ делали.** Поле обязательным не сделали (это строже, но ломает всех клиентов без параметра) — наряд называет это отдельной альтернативой для заказчика. Линейный и зеркальный массивы не трогали: члена `SaveInitialOrientation` у них нет ни в справке, ни в интероп-сборке.

## <a id="wrong-document-kind"></a>Код отказа для документа не того вида: у блока G1 `WRONG_DOCUMENT_KIND`, у C1 - `INVALID_ARGUMENT` (08.10.2026)

**Что было.** Все команды домена сборок (блок C1) отказывают на документе-детали кодом
`INVALID_ARGUMENT`: так их написал наряд C1, и так они приняты.

**Что решено нарядом G1.** Два инструмента блока G1 (`kompas_check_interference`,
`kompas_measure_gap`) отказывают в том же положении кодом `WRONG_DOCUMENT_KIND`: наряд называет этот
код прямо, и он точнее - вид документа назван, а не «аргумент не тот».

**Почему это расхождение оставлено, а не выровнено.** `ErrorCodes.WrongDocumentKind` существует и
употребляется там, где клиенту нужно отличать «документ другого вида» от прочих дефектов аргумента;
у C1 код уже принят приёмкой и его смена была бы изменением контракта мимо наряда. Расхождение
НАЗВАНО здесь и в описании инструментов, а не сглажено: клиент, работающий с обоими блоками, увидит
разные коды на одном и том же входе, и это записано, а не выяснено опытом.

## <a id="sketch-bulk-response"></a>Несовместимое изменение ответа `kompas_edit_sketch` / `kompas_list_sketch_entities` (08.10.2026)

**Что сломано для клиента.** Полилиния рисовалась отрезками, и чтение возвращало её как N (или N−1)
сущностей `kind=ksDrLineSeg` с `collection_counts.poly_lines = 0`. Теперь `kind=polyline` рисуется
ОДНИМ нативным объектом, и чтение возвращает ОДНУ строку `ksDrPolyline` с `entity_kind=polyline`,
`points_count` и `closed`, а `collection_counts` — `poly_lines = 1`. Клиенту, считавшему отрезки
полилинии, нужно перейти на счёт объектов и на `poly_lines`.

**Что при этом НЕ изменилось.** Площадь профиля (`profile_area_mm2`) и ожидаемый объём выдавливания
для замкнутого контура считаются прежней формулой по вершинам: для того же контура числа те же. Это
подтверждено строкой `SB-05` — контур клиента из приёмки 0.5.0 даёт ту же площадь 8474,982482571266 и
тот же объём 33899,929930285063 при глубине 4.

**Что добавлено.** Вид `kind=spline` (один объект — кривая Безье через заданные вершины); строки
перечисления несут `entity_kind`, `points_count`, `closed`; `collection_counts` получает
`view<n>.beziers` и `view<n>.nurbses`. Поля чужого вида в примитиве эскиза отклоняются по имени.

**Класс изменения.** До `1.0.0` несовместимое изменение ответа даёт MINOR: `0.5.0` → `0.6.0`.

## <a id="raster-sizing"></a>Размер снимка без параметров: подбор в два прохода (10.10.2026)

**Что было.** Без `resolution`/`scale` действовало умолчание ядра: размер растра следовал размеру
модели. Кампания §3d.1 наряда `CLIENT_BUGS_20261010` (`docs/04_KOMPAS_API_NOTES.md` §4.66)
измерила: длинная сторона строго пропорциональна `(resolution / 120) × scale`, а база зависит от
проекции КОНКРЕТНОЙ детали и от текущего вида окна; формулы базы для произвольной детали нет.
Измеренные на кубе коэффициенты (`4,725·s + 123,5` для фронта) и пол (242 px) переносить на другую
форму нельзя — это было бы измерение одного образца, выданное за формулу.

**Что решено (решение заказчика 10.10.2026).** Подбор в ДВА ПРОХОДА, только когда не заданы ни
`resolution`, ни `scale`. Проход 1 — пробный снимок в ПАМЯТЬ с умолчанием ядра; из его заголовка
читается длинная сторона `L`. Затем `scale = target / L`, где `target` = `long_side_px` (по умолчанию
1024). Проход 2 — итоговый, с вычисленным масштабом; ФАЙЛ пишет только он, вид окна меняется и
возвращается ОДИН раз на оба прохода. Вычисление — чистая функция `RasterSizing.FromProbe` в Domain:
непрочитанная или неположительная `L` — ОТКАЗ с названной причиной, а не деление; результат вне
пределов, которые ядро принимает (`MinAutoScale`..`MaxAutoScale`, измерено: 10 рендерится, 100 даёт
`RASTER_EMPTY`, 1000 — `RASTER_REFUSED`), зажимается и зажим НАЗЫВАЕТСЯ. Промах дальше 10 % от цели —
не отказ, а причина в `sizing_note`. Явные `resolution`/`scale` сервер не трогает; `long_side_px`
вместе с ними — `INVALID_ARGUMENT` до COM.

**Чем подтверждено.** Строки `CB10.14`–`CB10.20` группы `--client-bugs-20261010`; модульные тесты
`RasterSizingTests`.

## <a id="face-properties"></a>Свойства грани и ребра: непрочитанное НАЗВАНО (10.10.2026)

**Что было.** Ветка грани читала ТОЛЬКО площадь, а остальные запрошенные свойства молча оставались
`null`; если площадь не запрашивалась, грань уходила в общую ветку `target_kind_..._not_measurable`.
Ретест R-080 это и показал: `kompas_measure` грани с `properties=[bbox]` вернул `bbox=null` при
ПУСТОМ `unverified_aspects` — «не прочитано» ничем не названо.

**Что решено.** INVARIANT: каждое запрошенное и НЕ прочитанное свойство называется в
`unverified_aspects` со своей причиной — пустое значение неотличимо от «забыли заполнить». Ветка
грани обрабатывает ЛЮБОЙ набор свойств: запрос `bbox` без площади больше не уходит в «не измеряется»
целиком. У грани читается только `surface_area`; `bbox` НЕ читается (в зеркале справки v24 маршрута
габарита грани нет: `ksFaceDefinition` отвечает `GetArea`/`GetSurface`, не габаритом) — габарит
берётся у ТЕЛА (`kompas_list_bodies`); `volume` и `mass` к грани не применимы. У ребра не читается ни
одно свойство, и каждое названо отдельно.

**Чем подтверждено.** Строки `CB10.21`–`CB10.22`; долг №17 закрыт.

## <a id="feature-left-in-tree"></a>Поле `feature_left_in_tree` у документа без дерева признаков (10.10.2026)

**Что было.** Поле `feature_left_in_tree` в отказе мутации введено нарядом `CLIENT_BUGS_20261010`
(часть D): число признаков читается ДО и ПОСЛЕ отказа, рост означает признак, оставшийся в дереве.
Проба `Api5Session.TryCountFeatures` шла через `document.PartNow()`, а тот у КАЖДОГО документа без
3D-дескриптора (чертёж, фрагмент) бросает `WRONG_DOCUMENT_KIND` — исключение, которое проба не
ловила. Вызов стоял в `CommandDispatcher.TaggedAfter` ДО мутации и ВНЕ `try`, поэтому КАЖДАЯ мутация
чертежа отказывала до своего начала, а взятая контрольная копия оставалась неубранной.

**Что решено.** INVARIANT: вспомогательное чтение, сделанное для ОТВЕТА, не отменяет мутацию.
Вид документа проверяется ДО `PartNow()`: у детали и сборки дерево признаков модели ЕСТЬ, у чертежа
и фрагмента его НЕТ ПО ПОСТРОЕНИЮ. Для документа без дерева поле `feature_left_in_tree` выводится
как `false`, а рядом кладётся `feature_left_in_tree_note` = «неприменимо: у чертежа нет дерева
признаков модели» — то есть «неприменимо» отделено и от «не измерено», и от «нет». Для 3D-документа
поведение прежнее: рост числа признаков даёт `true`, непрочитанное число — `false` с примечанием
«не измерено». Правило вида вынесено в чистую функцию `ModelFeatureTree` (Domain) и покрыто
модульными тестами; проба в диспетчере дополнительно обёрнута так, что не бросает ни при каком виде
документа.

**Чем подтверждено.** Группа `--drawing-only` (мутации чертежа снова выполняются); модульные тесты
`ModelFeatureTreeTests`; строки таблицы аудита в `DRW_REGRESSION_FIX_REPORT_20261010.md`.
