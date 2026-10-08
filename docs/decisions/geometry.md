# Geometry (Domain) - решения и измерения

Модуль: `src/KompasMcp.Domain/Geometry/`. Здесь - история правок, вынесенная из кода. Действующие
правила остались в коде под метками `INVARIANT:` / `MEASURED:` / `LIMIT:` / `DOC:`.

## <a id="euler"></a>Ориентация углами Эйлера (проба --reposition-params, шаг RP.25)

**Что было.** Положение тела продукт писал МАТРИЦЕЙ (`ILocalCoordinateSystem.InitByMatrix3D`).

**Что измерено.** Проба `--reposition-params`, прогон `a336120926fc4652a8bf737562568271`, шаг RP.25:
матрица переоткрытие НЕ переживает - документ хранит ПАРАМЕТРЫ ориентации. Тройка углов
`RotationAngle`/`NutationAngle`/`PrecessionAngle` переоткрытие ПЕРЕЖИВАЕТ, а переносная часть читается
через `ParameterType = ksPDisplace` + `IPoint3DParamDisplace.DX/DY/DZ`. Единицы - ГРАДУСЫ (угол 30 дал
поворот на 30°: `angle_deg_for_30 = 29.99999999999998`).

**Порядок композиции ИЗМЕРЕН, а не подобран.** Справка задаёт его РИСУНКОМ (`rotation_pict.html` →
`images/_praezession.jpg`), а не текстом. Шаг RP.25 поставил каждый угол ОДИН (90°, остальные нули),
вычислил ось каждого фактора из измеренной матрицы и сравнил три составные постановки со всеми шестью
произведениями. Совпало РОВНО ОДНО - `PNR` (`order_PNR_max_diff = 0`; у остальных пяти - 1). Оси
факторов: P → `(0,0,1)`, N → `(1,0,0)`, R → `(0,0,1)`.

**Что решено.** `M = Rz(прецессия)·Rx(нутация)·Rz(вращение)` - классические углы `z-x-z`.

**Дословно из комментария кода (сжатие 05.10.2026).** RP.25 tried every product and exactly ONE
matched (PNR, max diff 0; the other five, 1). INVARIANT: the 16-number layout is the one of
<see cref="RepositionMatrix"/> - a second layout is a second way to swap rows and columns, and that
defect is invisible on a translation.

## <a id="euler-pole"></a>Знак на полюсе параметризации (строка приёмки B3.59)

**Что было.** При `nutation = 180` знак разности прецессии и вращения был взят неверно: сборка матрицы
из возвращённой тройки давала `[[0,−1,0],[−1,0,0],[0,0,−1]]` вместо `[[0,1,0],[1,0,0],[0,0,−1]]` -
расхождение 2 в двух клетках.

**Что измерено.** Поймано строкой приёмки B3.59 (поворот 180° вокруг оси `(1,1,0)`): продукт отказал
`GEOMETRY_FAILED`, потому что собственная проверка `RequirePlacementRoundTrip` не смогла воспроизвести
записанное размещение.

**Вывод знака.** При `nutation = 180` (`cb = −1`, `sb = 0`) элементы матрицы равны `m00 = cos(a − c)`,
`m01 = sin(a − c)`, `m10 = sin(a − c)`, `m11 = −cos(a − c)`. При `a := 0` получается `cos c = m00` и
`sin c = −m01`, то есть `c = atan2(−m01, m00)`. Прежняя редакция брала `atan2(m01, m00)` - формулу для
ПРОТИВОПОЛОЖНОГО соглашения (`c := 0`) - и смешивала два соглашения в одной ветке. При `nutation = 0`
знак прежний: `c = atan2(m10, m00)`.

## <a id="axis-point"></a>Точка на оси поворота

**Что решено.** Точка оси не является свойством размещения: поворот вокруг любой точки ОДНОЙ прямой даёт
то же размещение. Поэтому из пары «ориентация + перенос» восстанавливается представитель прямой, а не
«та самая» точка; при θ = 0 перенос её не определяет - возвращается `null`, а не ноль.

**Вывод формулы.** Для поворота на угол θ вокруг единичного направления `d` точка `c` переходит в
`c·cos θ + (d × c)·sin θ + d·(d·c)·(1−cos θ)`, поэтому перенос равен `t = (1−cos θ)·u − sin θ·(d × u)`,
где `u = c − (c·d)·d`. Умножив векторно на `d`, получаем `d × t = sin θ·u + (1−cos θ)·(d × u)`; решение
этой пары даёт `u`, откуда `c = u` (представитель с нулевой составляющей вдоль оси).

## <a id="reposition-layout"></a>Раскладка матрицы размещения (проба --reposition, шаг RP.2)

**Что измерено.** 18.09.2026, прогон `929f08886f1348fe921943052a4026b0`: положение пишет ТОЛЬКО матрица
из 16 чисел. Маршруты из 12 чисел («оси, затем начало» и «начало, затем оси») и `SetDisplacementByAxis`
возвращают `Update() = true` и тело не двигают - успешный `Update()` здесь доказательством не является
(отдельный факт о продукте, не о приборе).

**Раскладка.** 3×3, где СТОЛБЦЫ - образы базисных векторов, затем строка переноса, последним числом 1.
Массив хранит тройки подряд (постолбцово), поэтому порядок индексов обратен математической записи
`R[i, j]`.

**Что решено.** У раскладки обязан быть различающий контроль - ПОВОРОТ: на переносе строки и столбцы
различить нельзя (единичный поворот симметричен). 18.09.2026 именно здесь жил дефект: 3×3 уходила в
КОМПАС транспонированной, перенос работал, а поворот отвергался как `NO_GEOMETRY_CHANGE`. Измерено
также (RP.4): поворот бруска `[10,30]×[0,10]×[0,5]` на +90° вокруг Z через начало даёт
`(−10,10,0)…(0,30,5)`, то есть `(x,y) → (−y,x)`.

## <a id="profile-area"></a>Площадь области профиля (KOMPAS-3D v24)

**Дословно из комментария кода (сжатие 05.10.2026).** On KOMPAS-3D v24 a sketch with circles R=10 and
r=5 extruded 10 mm deep gives <c>2356.1944901923607</c> mm³ against π·(100−25)·10 = 2356.194490192345
(6.8e-15 relative), and a 100×80 rectangle with an r=10 circle inside gives <c>76858.4073464102</c> mm³
against (8000−100π)·10 = 76858.40734641021. The depth-2 case is measured too: circles R=10, r=5, r=2
extruded 10 mm give <c>2481.8581963359516</c> mm³ = π·(100−25+4)·10. A line or an arc on its own, a
self-intersecting polyline, a degenerate (non-positive area) contour, a profile drawn outside this
session, or a pair of contours that touch or partially overlap return null. The extrusion then reports
"not computable" and an unverified aspect instead of inventing a target - which is the difference
between "we checked" and "the numbers happened to look fine". Partial overlap is refused on purpose
even though the union of two overlapping circles <i>is</i> exactly computable and was measured (R=10
with centres 15 mm apart, 10 mm deep: <c>5829.873553201979</c> mm³ against the lens formula's
5829.873553201976). One measured special case does not make the general case analytic, and an
expectation special-cased until it matches is exactly how an instrument starts describing itself.
Touching contours are refused as well, and that is deliberate: at tangency the region depends on how
the kernel resolves a shared point or edge, which is not measured, so neither "sum" nor "difference"
is a statement this code is entitled to make.

## <a id="sketch-point-derivation"></a>Точка для ksFindObj, выведенная из модели (проба G)

**Дословно из комментария кода (сжатие 05.10.2026).** For a sketch this server drew, the coordinate
is remembered. For one it did not - a reopened document, or a model built by hand - there is no
memory, and the product used to refuse <c>replace</c>/<c>delete_entities</c> outright. The probe
checked it against two control points that find nothing, and confirmed the edit by measuring the
dependent body (V 76858.4073464102 → 75476.1065788307 for R10 → R12 on a 100×80×10 plate). Everything
else in the mapping - an inclined plane (where 3D→2D needs a transport that was never measured), a
profile of segments, arcs or rectangles (where a cylinder gives no point that provably lies on the
primitive) - must refuse rather than extrapolate. That boundary is the whole point of this type: it
turns "the number happened to work" into a stated precondition.

## <a id="target-body-guard"></a>Проверка целевого тела по габаритам (проба P2)

**Дословно из комментария кода (сжатие 05.10.2026).** The profile may still miss the material
entirely (a hole inside a pocket, a contour in the concave part of an L). Probe P2.6 measured that
declaring a body the contour does not sit over makes <c>SetSketch</c>, <c>Create</c> and
<c>RebuildDocument</c> all return true while the document does not change at all. KOMPAS does not
report that contradiction as an error, so a no-op would otherwise be delivered as a success. The axis
correspondence is asserted by acceptance rows <c>G07_xy</c>/<c>G07_xz</c>/<c>G07_yz</c> in
<c>scripts/mcp-smoke.py</c>, where the same rectangle (u=10..50, v=20..40) with depth 6 produced
exactly the boxes encoded here. A plane the server did not derive from one of the three base planes
has no measured correspondence and yields "unknown", which the caller reports as unverified rather
than guessing a sign - the mistake G07 was left open over. A through cut travels along the normal axis
in both directions from the sketch plane, which sits outside the material by construction (the probe's
cut plane was 10 mm above the bodies), so requiring the body to straddle the plane would refuse the
very operation being measured.

## <a id="sketch-profiles-contour-list"></a>Список контуров профиля и площадь области

**Дословно из комментария кода.** INVARIANT: a profile's area is not the sum of its primitives - a contour
inside another is a hole. The extent is therefore derived from the contour list, never accumulated next to
it. The area is computed on demand rather than cached: it is read once per extrusion, and a cached figure
can go stale against the contour list it describes.

## <a id="profile-area-2"></a>Площадь профиля из цепочки отрезков и дуг (карточка GAP-012, 08.10.2026)

**Что было.** Площадь отвечалась только для окружностей, прямоугольников и ЗАМКНУТЫХ полилиний;
отдельные `line` и `arc` контуром не считались вовсе, и профиль из них давал `null` — то есть
выдавливание настоящей детали шло без ожидания (`expected_basis` не выдавался, уровень
`call_returned`). Клиент мерил такой профиль (21 дуга и 4 отрезка, вырез на 4 мм) отдельным
`kompas_measure`: 33899,92993028507 мм³, то есть площадь 8474,982482571 мм².

**Что измерено.** 08.10.2026, зондом `scratch/_e08_before_after.py` на бинарях поставки 0.4.0
(`artifacts/publish-v0.4.0-57ef4d1`): тот же контур через MCP даёт `expected_volume_not_computable`
и `call_returned`. На бинарях дерева после правки — `expected_basis = profile_area_x_depth`,
`volume_delta` `passed=true`, уровень `geometry_checked`, измеренная дельта `33899.929930285062`.
Аналитика на тех же 25 примитивах (формула Грина: хорда плюс круговой сегмент `r²(θ−sinθ)/2` для
каждой дуги) даёт площадь `8474.982482571266` мм², расхождение с измерением `2·10⁻¹²` мм³.

**Что решено.** Контур собирается из примитивов сшивкой концов с допуском **1e-6 мм** (у клиента
концы совпадают до `2·10⁻¹⁴` мм, а наименьший настоящий зазор между разными вершинами на порядки
больше). Каждая вершина обязана нести РОВНО два конца: один — открытая цепочка, три и больше —
ветвление; и то и другое даёт `null`. Замкнутая цепочка считается формулой Грина, дуга вносит хорду
плюс сегмент со знаком обхода, модуль берётся в конце. Вложенность — тем же правилом чёт-нечёт.
Причина `null` доходит до `unverified_aspects` выдавливания ТЕКСТОМ, а не одним словом: для этого
запись профиля больше не выбрасывается, когда площадь невычислима (иначе выдавливание отвечало
«профиль не записан этим сеансом» о профиле, который этим сеансом нарисован). Замкнутая полилиния
по-прежнему может прийти частями: контур из нескольких полилиний с общими концами собирается в один.

**Что осталось `null`.** Незамкнутая цепочка, ветвление, самопересечение, касание или частичное
перекрытие контуров, примитив без аналитики. Открытую или ветвящуюся цепочку ядро отвергает ещё
раньше (`GEOMETRY_FAILED`, строка E08.07), поэтому «null с названной причиной» измеряется на контуре,
который ядро ПРИНИМАЕТ, а аналитика не разрешает: прямоугольник с касающимся кругом (строка E08.06).

**Тесты.** `tests/Unit/KompasMcp.Unit/ProfileAreaTests.cs` — паз (2 отрезка и 2 полуокружности,
`2rL+πr²`), полукруг, большая дуга (`|sweep|>180`), кольцо из двух цепочек, обе ориентации обхода,
контур из двух открытых полилиний, ветвление и самопересечение → `null` с причиной, и контур
клиента из `Data/plate_cp04a_mcp.json` против аналитического `8474.982482571266` с допуском `1e-6`.
