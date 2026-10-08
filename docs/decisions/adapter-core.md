# Ядро адаптера API5 - решения и измерения

Модуль: `src/KompasMcp.Api5Adapter/` (`Api5Session*.cs`, `NativeMethods.cs`, `Com/`, `Sta/`). Здесь -
история правок, вынесенная из кода. Действующие правила остались в коде под метками `INVARIANT:` /
`DOC:` / `MEASURED:` / `ASSUMPTION:` / `LIMIT:` / `TEST:`.

## <a id="visibility"></a>Видимость - наблюдение, а не факт вызова

**Что было.** Видимость экземпляра КОМПАС выводилась из одного признака: либо из факта, что сеттер
`Visible` вызван, либо из наличия окна. Наличие HWND (и определяемого по нему PID) выдавалось за
видимость. Документы наследовали **запрошенную** видимость сеанса, а не наблюдённую.

**Что измерено.** Скрытое окно КОМПАС даёт и HWND, и PID - на этом псевдониме и держался пропущенный
дефект: «окно существует» ≠ «окно видимо». При первом запуске наблюдение сразу после показа возвращало
`visible=false` для корректного действия - это гонка перерисовки, а не дефект.

**Что решено.** Показ докладывается ТОЛЬКО наблюдением, двумя независимыми способами - COM-свойством
(`KompasObject.Visible`) и Windows (`IsWindowVisible` на главном окне, `ksGetHWindow`), - а режим
документа перечитывается из самого документа (`ksDocument3D.invisibleMode`), не выводится из запроса.
Поля ответа разделены и не подменяют друг друга; `visible` истинно, только когда оба наблюдения
согласны. После показа наблюдение берётся с коротким ожиданием (гонка перерисовки). Документы
наследуют **наблюдённую** видимость экземпляра. Обновление вида после мутации - `ksRefreshActiveWindow()`
и только в видимом режиме; сброс камеры (`ZoomPrevNextOrAll`, `ksZoom*`) запрещён и контролируется
статическим тестом.

## <a id="arc-sign"></a>Знак и диапазон углов дуги (20.09.2026)

**Что было.** Конечный угол дуги строился как `start_deg + |sweep_deg|` - знак размаха терялся.
Схема объявляет `start_deg` и `sweep_deg` в `[−720, 720]`, и код передавал углы как есть.

**Что измерено.** 20.09.2026 сценарием «Model Mania 2021 bracket» (задание §3.3/§3.5) и зондом
`scratch/_mania_contour_probe.py` на поставленных бинарях:

* четверть диска R30 (дуга старт 0°, размах +90° + два отрезка) даёт объём
  `14137.166941154068 = 20·π·30²/4`;
* ТА ЖЕ четверть, записанная дугой `(90°, −90°)`, даёт `GEOMETRY_FAILED` - концы дуги не там, где
  ожидает контур;
* различающий случай T6 (дуга `(90°, −90°)` с отрезками `(−30,0)→(0,0)→(0,30)`) дал
  `14137.166941154068 = 20·225π`, то есть ВТОРУЮ четверть `[90°,180°]` вместо первой `[0°,90°]`, -
  доказательство, что конечный угол брался как `start+|sweep|`;
* контур Model Mania из восьми касательных дуг СО знаками → `GEOMETRY_FAILED`, тогда как та же
  геометрия с ПОЛОЖИТЕЛЬНЫМ размахом → объём `174799.7403608485` против аналитического
  `174799.740134` (расхождение `1.3·10⁻¹²`).

Второй дефект найден тем же сценарием (задание §3.5). Ядро принимает ДВА УГЛА и отказывает, когда
конечный угол покидает `[−360°, 360°]`. Зонд `scratch/_arc_angle_range_probe.py` на поставке
`publish-mania-20260920`: один и тот же сектор R30, записанный двумя способами, - отказ против успеха:

* R2 (старт 360°, размах +90°, конец 450°) → `GEOMETRY_FAILED` против R1 (0°, +90°) → `14137.166941`;
* R5 (315°, +90°, 405°) → `GEOMETRY_FAILED` против R6 (−45°, +90°) → `14137.166941`;
* R7 (−350°, −90°, −440°) → `GEOMETRY_FAILED` против R8 (10°, −90°) → `14137.166941`.

Различающие случаи R3 (старт ровно 360°, конец 270°) и R4 (конец ровно 360°) ОБА проходят, значит
спусковой крючок - не «старт 360» и не «конец 360», а ВЫХОД за `±360°`. Нормализация углов не
переистолковывает большие дуги: вариант G - дуга `(0°, +270°)` даёт `42411.500823 = 20·(270/360)·π·30²`,
то есть ядро чтит размах больше 180°, а не берёт меньшую дугу.

**Что решено.** Конечный угол - `start_deg + sweep_deg` СО знаком: отрицательный размах уходит на
другую сторону от старта. Углы сдвигаются на целое число оборотов ровно настолько, чтобы конец вошёл в
`[−360°, 360°]`; размах при сдвиге НЕ меняется. Порядок двух углов несущий: при отрицательном размахе
ядро ожидает меньший угол первым, поэтому вызывающий обязан передать `Min`/`Max`. Первая редакция
правки передавала `(start, end)` и изменила результат на входах, которых правка не касалась (R8 дал
`42411.500823` вместо `14137.166941`); аддитивность доказана контролем на НЕтронутых входах, а не
рассуждением о ветвях.

НЕ ИЗМЕРЕНО и потому НЕ трогается: случай `|sweep_deg| > 360` с конечным углом ВНУТРИ диапазона
(например старт −180°, размах +400°). Такой размах одной дугой не выражается (два угла задают не более
оборота), но что делает ядро сегодня - не измерялось, поэтому поведение оставлено прежним, а не
заменено догадкой.

## <a id="feature-suppression"></a>Подавление признака и счётчик коллекции 110 (12–19.09.2026)

**Что было.** Подавление считалось корректным, если число признаков не изменилось: принималось, что
подавленный признак остаётся в модели, а меняется только его состояние.

**Что измерено.** L04/L05, 12.09.2026: подавленный признак ИСЧЕЗАЕТ из
`EntityCollection(o3d_operationElement=110)` - `kompas_list_features` его не показывает, счётчик
уменьшается на единицу. Прежнее допущение «число признаков должно остаться» измеряло не смерть
признака, а его исключение из этой коллекции.

19.09.2026, зонд I, шаги E1–E6, прогон `c90961c6a3ba478697da5bc243040719`, отчёт
`docs/acceptance/api7/feature-identity.json`: признаки семейства B3 не имеют определения API5 вовсе
(§4.10.6: 69/633/50/79 - это номера дерева, а `GetDefinition()` для них null); подавление КАСКАДИРУЕТ -
подавление первого из двух последовательных признаков «изменение положения» убрало из коллекции 110
два элемента (4→2), а восстановление вернуло один (2→3).

**Что решено.** Счётчик ИЗМЕРЯЕТСЯ, а не «объясняется единицей»; отклонение больше единицы называется
отдельным неподтверждённым аспектом, а не замалчивается. Состояние перечитывается свежим объектом
признака: удержанный при записи объект может быть кэшированным представлением, и «мы записали» - не
доказательство, что модель приняла. Направление изменения объёма не утверждается: наблюдаемый факт -
что объём ИЗМЕНИЛСЯ, а каким он должен быть, объявляет вызывающий через `expected_volume_mm3`.

## <a id="delete-position"></a>Удаление признака: позиция в дереве и три отдельные претензии (19.09.2026)

**Что было.** Наличие и позиция признака брались по имени (`IndexOf(name)`), а «удалено» проверялось
строгим «ровно на один меньше».

**Что измерено.** 19.09.2026, зонд I: два последовательных признака одного вида несут ОДНО имя
(«Change of position : Body 1»), поэтому `IndexOf(name)` вернул бы позицию ЧУЖОГО одноимённого признака
и объявил бы не тех зависимых; проверка по имени ложно-отрицательна, если одноимённый сосед остался.
COM-тождество НЕ переживает перестроение: подавление и восстановление базового выдавливания (BG19/BG20)
пересоздаёт элемент дерева, и `FindIt` на старом объекте отвечает `−1`, хотя объект жив и `IsCreated`.
Строгое «ровно на один меньше» сделало КАСКАД ложно-отрицательным: дерево 5→3 дало
`feature_removed=false`, хотя цель исчезла.

**Что решено.** Наличие и позиция берутся ПО ТОЖДЕСТВУ (`FindIt`), имя - только когда оно однозначно;
при неоднозначности позиция НЕИЗВЕСТНА и в качестве кандидатов предлагается всё дерево, так что
удаление без явного согласия невозможно, а не бесплатно («неизвестно, что после него» - не то же
самое, что «после него ничего нет»). Три претензии ведутся РАЗДЕЛЬНО: `feature_removed` (снята ли
ЦЕЛЕВАЯ признак - по тождеству объекта), `cascade_within_candidates` (ушли ли ТОЛЬКО она и объявленные
кандидаты) и `independent_objects_preserved` (целы ли объекты, стоявшие ДО неё). «Цель удалена» и
«каскад прошёл как ожидалось» - РАЗНЫЕ претензии, и ни одна не выводится из изменения общего числа
признаков. Сбой во время мутации - исход неизвестен, а не «чисто не сработало»: повтор той же операции
запрещён.

## <a id="geometry-signatures"></a>Geometry signatures and measurement routes (P0.2/P0.7/P0.8)

Signatures here are taken from `docs/compatibility/kompas-api5-metadata.json` (dumped from the installed
interop by P0.2), not from memory. Three consequences worth stating:

* `GetLength`/`GetArea`/`CalcMassInertiaProperties` take a **UInt32 unit selector**. Leaving it at 0 asks
  for centimetres - that is the whole 10× discrepancy of spec 4.5, reproduced in P0.7 as `GetLength(0)=10`
  on a 100 mm edge. Only `KompasUnits.LengthMm` is passed in this file.
* Volume and area come from `CalcMassInertiaProperties` (`.v()`, `.F()`): `ksBody` has no
  `GetVolume`/`GetArea` at all, contrary to spec 4.5's wording.
* Edges are reached through `GetMainBody() → FaceCollection → EdgeCollection`, never through
  `EntityCollection(o3d_edge)`, which also contains sketch and construction contours (P0.8: 23 objects
  against 14 real body edges).

## <a id="sketch-profiles-contour-list"></a>Sketch profiles: contours, not a running sum (24.09.2026)

The contour list is kept rather than the area, because the area of a profile is not the sum of its
primitives: a contour inside another one is a hole in it. Measured 24.09.2026 - while the entry was a
running sum, a sketch built by appending a circle inside another gave π·125 = 392.699081699 against the
annulus' π·75 = 235.619449019, and the extrusion of a correct ring was reported as an unconfirmed geometry
change. One number cannot carry nesting, so the number is derived from the contours instead of accumulated
next to them.

## <a id="resolve-sketch-plane-base"></a>Resolving a sketch's base plane

The memory entry only exists for a sketch this server created. A sketch that arrived with a reopened
document has none, and without a plane the derivation cannot be allowed at all - which is correct but would
make the new route useless exactly in the case it was built for. `PlaneNormalAxis` already resolves a plane
entity (including an offset plane, through its base) to the model axis it is normal to, measured for the
through-extent calculation; the same resolution answers the question here. An axis has no sign, so this
establishes "the sketch is parallel to XY", not "it faces +Z" - the sign is what probe G left unmeasured,
and `SketchPointDerivation.AxisIsNormalToXyPlane` tolerates either.

## <a id="guard-dependent-body-survived"></a>Refusing a sketch edit that destroyed a dependent body (probe G.9)

Closes the question probe G opened (Q-SKETCH-EDIT-ZERO). Measured in G.9: replacing a Ø20 hole with R90 on
a 100×80 plate makes the profile larger than the material, and KOMPAS answers success to every individual
call while the body disappears - V = 0, 0 faces, 0 bodies. The vendor's return codes are therefore not a
verification of anything, and the only honest evidence is the body measurement. The refusal is reported as
`ErrorCodes.GeometryFailed` with `partialEffects: true`: the model really did change, and saying otherwise
would be its own lie. A general rollback is not claimed - nothing in this adapter can restore a body KOMPAS
consumed.

## <a id="derive-probe-points-from-model"></a>Deriving probe points from the model (probe G)

Route measured by probe G (`docs/acceptance/api7/sketch-geometry-reopen.md`, 11 steps PASS) on a reopened
document: the through cut is found in the tree by type, its sketch via `GetSketch()`, and the coordinate
comes from the cylindrical face the cut left behind - `GetSurfaceParam() → ksCylinderParam` gives centre and
radius, and the point `(cx + r; cy)` lies on the sketch circle. Two control points that find nothing were
part of the measurement, so the point is known to select this primitive rather than a neighbour. Guarded by
`SketchPointDerivation.Verdict`: only a base-XY sketch with a circular profile qualifies. An inclined plane
needs a 3D→2D transport that was never measured, and a segment/arc/rectangle profile has no primitive a
cylinder-derived point provably lies on. Both cases refuse here with the reason stated, instead of
extrapolating from one measurement.

## <a id="extrude-direction-semantics"></a>Extrusion direction semantics (measured, plate 100x80x10)

`SetSideParam(side1, type, depth, draftValue, draftOutward)`: the second argument is the end-condition type,
and depth is the third - as the historical scripts used it. `directionType` selects along/against/both,
separately from `SetSideParam`.

MEASURED SEMANTICS (plate 100x80x10 spanning z=[0,10], sketch on the XY plane at offset o):

```
  directionType 0 dtNormal  -> material goes to the +z side of the sketch plane
  directionType 1 dtReverse -> material goes to the -z side of the sketch plane
  directionType 2 dtBoth    -> material goes to both sides (depth each way)
```

Consequences, each measured with an exact volume rather than inferred:

```
  base  positive          -> bbox z=[0,10]   V=80000
  base  negative          -> bbox z=[-10,0]  V=80000
  base  symmetric         -> bbox z=[-10,10] V=160000
  cut   o=10 positive     -> -392.699082 (= pi*5^2*5, into the plate)
  cut   o=0  negative     -> -392.699082 (the mirrored pair; into the plate from below)
  cut   o=10 negative     -> NO_GEOMETRY_CHANGE (asks for material at z>10, where none is)
  boss  o=10 positive     -> +9000 (30x30x10)
  boss  o=0  negative     -> +9000
```

A direction pointing away from the body is NOT an error: KOMPAS creates the feature and changes nothing,
which the Host surfaces as NO_GEOMETRY_CHANGE. That is why the choice of sketch plane is part of the
caller's contract, not a detail this adapter may paper over.

## <a id="extrude-three-quantities"></a>Three different volume quantities (B3, EXTRUDE-VOLUME-DELTA-ON-MULTIBODY)

THREE DIFFERENT QUANTITIES that used to be one number - that conflation produced 13 false doubts in client
acceptance B3 (defect EXTRUDE-VOLUME-DELTA-ON-MULTIBODY):

* `documentVolume*` - the SUM of all document bodies' volumes (`volume_mm3` is the "after");
* the volume of the body the operation concerns: the declared target (boss/cut) or the NEW body (base);
* the MATERIAL ADDED by this feature - the only quantity comparable with `profile_area × depth`.

On a multi-body document the first and third differ by the volume of everything already present (49 000
versus 1 000), and comparing them declares a discrepancy that is not in the geometry. The sum of individual
volumes is also not the volume of the spatial union: two overlapping bodies give 36 000 + 24 000 = 60 000
against 36 000 for the union.

## <a id="read-body-snapshots"></a>Reading per-body snapshots

Per-body volumes and boxes, read exactly the way every other measurement in this project reads them -
`CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v()` and `ksBody.GetGabarit` - so the numbers compare with
probes P0.7, P2.1 and P2.6. `refresh()` before counting is not decoration: without it a collection read
right after a rebuild can report the previous membership (the `ListBodies` defect). What is deliberately not
used here is `GetMainBody()` - in a multi-body part it answers one body only, which is precisely why an
operation aimed at another body looked like a no-op.

## <a id="resolve-body-target-identity"></a>Resolving a body target by identity (P2.6)

Resolves `target_body_ref` into the body the selector call needs. The reference holds a `ksBody`;
`ksBodyCollection.Add` wants the raw element of the part's body collection (measured in P2.6: the cast to
`ksEntity` and the unpacked `GetDefinition()` are both refused), and nothing in API5 states which position
the referenced body currently occupies. The only identity available on both sides is the object's IUnknown -
the same comparison `ReadTopology` performs to deduplicate edges - so the index is found by pointer and the
element is taken from that same walk.

## <a id="box-change-floor"></a>Box change floor (B3.25, 19.09.2026)

The threshold exists because "the body changed" is NOT the same as "the body's volume changed". MEASURED on
order §4.1 (B3.25 of the 19.09.2026 delivery): on the boolean-edit reference the DIFFERENCE volume and the
INTERSECTION volume are equal (12 000 mm³), and only the box position tells them apart ((0,0,0)…(20,30,20)
versus (20,0,0)…(40,30,20)). While volume was the only change signal, a correctly applied `intersect` edit
looked like "no body changed" and was rejected by the instrument itself - the verdict rested on a field that
does not support it (defect CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT, order §4.1). The threshold is 1 nm: the
box is read as exact B-Rep coordinates, so an untouched body's six numbers match bit-for-bit while any real
shift is orders of magnitude larger. The threshold catches solver noise, not geometry.

## <a id="extrude-forward-side"></a>Side must agree with direction (v24 measurement)

`forward=true` means "extrude to the side the sketch normal points at". `dtReverse=1` already says "the
other side", so passing `forward=true` unconditionally asks for a contradiction: the direction says reverse,
the side says forward. Measured on v24 before this fix: a base extrusion asked for direction=negative
answered `Create()=false` and the feature never appeared (GEOMETRY_FAILED). After the fix: bbox z=[-10,0],
V=80000 - the exact mirror of direction=positive (bbox z=[0,10], V=80000). The side must agree with the
direction. The same guard was needed on ConfigureBoss (before it, boss with direction=negative failed with
GEOMETRY_FAILED) and is harmless-but-not-required on ConfigureCut (see there).

## <a id="apply-body-choice"></a>Applying the body choice (probe P2.6)

Tells the kernel which body the operation must act on, by the route measured in probe P2.6 and nowhere else
in this repository: `def.chooseType = ksChBodies(3)`; `cb = def.ChooseBodies()` (answers `ksChooseBodies`,
whose only members are `BodyCollection()` and `ChooseBodiesType` - there is no `Add` on it);
`cb.ChooseBodiesType = ksManualEditing(2)`;
`((ksBodyCollection)cb.BodyCollection()).Add(<raw element of part.BodyCollection()>)`.

Three things this call is not, all of them measured:

* Not optional. With no declaration KOMPAS cuts whatever body the contour happens to lie over, which for a
  single-body part is indistinguishable from success.
* Not decorative. `chooseType = 2` (parts only, and the part has none) produced `Create = true` and no
  volume change at all.
* Not to be left at `ChooseBodiesType = 0`: on a boss that creates an additional body even where the
  profile overlaps an existing one (2 bodies became 3).

If `Add` does not accept the body, the operation is refused before `Create`: a feature created without an
honoured target is exactly the unaimed mutation the contract exists to prevent.

## <a id="through-extent-mm"></a>Through extent and the main-body fallback

`material` is the body the caller declared. Falling back to `GetMainBody()` is kept for the operations that
name no target, and it is the reason a through cut aimed at a second body used to be unverifiable: in a
multi-body part `GetMainBody()` answers one body, so the expected delta was computed from a body the
operation never touched.

## <a id="fillet-route-and-unwrap-edge"></a>Fillet route and edge unwrapping (probe P2.2)

Fillet over explicitly referenced body edges (docs/03 G04, docs/05 SM-09). Route measured by probe P2.2:
`NewEntity(o3d_fillet=34)` → `ksFilletDefinition` → radius/tangent → `array()` as `ksEntityCollection` →
`Add(edgeEntity)` → `Create()`. Edges come from `kompas_read_topology` (final body), never from
`part.EntityCollection(o3d_edge)`: that collection also holds sketch and construction contours, which is the
error docs/04 §4.5 records and the historical fillet helper used. Success is not the HRESULT: the radius is
read back from a fresh definition object and the face count is required to grow by the number of filleted
edges.

Unwraps an edge from the reference registry into a `ksEntity` - what the fillet collection accepts. Which of
the three routes works is MEASURED (probe P2.2), not assumed; the route is returned with the entity so the
answer names how the edge was obtained. Extracted as a shared helper because both creating a fillet and
editing its edge set take this path: two copies of one unwrapping diverge on the first edit of either.

## <a id="chamfer-route"></a>Chamfer route (probe F, 12.09.2026)

Route MEASURED by probe F on 12.09.2026 on v24 and repeated here call for call:
`NewEntity(o3d_chamfer=33)` → `GetDefinition()` as `ksChamferDefinition` → `SetChamferParam(transfer, d1,
d2)` → `array()` as `ksEntityCollection` → `Add(edge)` → `Create()` → `RebuildDocument()`. On a 100×80×10
plate four vertical corner edges with 2×2 legs removed exactly 20·d₁·d₂ = 80 mm³ (F.2), an edit to 3×3
removed 180 (F.3), and after save→close→reopen the feature was found in the tree and edited again (F.5).
Edges come from `kompas_read_topology` (final body), not from `EntityCollection(o3d_edge)`: that collection
also holds sketch contours - the very defect docs/04 §4.5 records for the historical fillet helper. Success
is not `Create()`: the parameter is re-read from a fresh definition object, the face count must grow by
exactly the number of edges, and the volume must change per the analytic expectation if the caller supplied
one. A zero leg is refused before COM because KOMPAS accepts it and creates a feature with four zero faces
at unchanged volume (F.12) - passing that off as success would lie about the geometry.

## <a id="cylinder-geometry"></a>Cylindrical face geometry (probe P2.5)

Radius, extent, axis origin and direction of a cylindrical face. Route measured by probe P2.5:
`face.GetSurface()` → `ksSurface.GetSurfaceParam()` → `ksCylinderParam { radius, height, GetPlacement() }`,
with the direction taken from `ksPlacement.GetVector(2)`. Two traps that make this route worth pinning down
in code: `GetAxis` returns a POINT (origin + vector), not a direction - reading it as an axis gives a vector
of length ≈64.8 for this part - and the numbers are millimetres, cross-checked there against
`GetArea(LengthMm) = 2πrh` to the last digit. Radius and height do not distinguish position; only the
placement does, which is why all four are published.

## <a id="surface-normal-at-middle"></a>Planar face normal at parameter middle (probe P2.6)

Normal of a planar face through the typed `ksSurface` path: `GetNormal(u, v)` at the middle of the parameter
range, with the sign taken from `normalOrientation`. The control pair in probe P2.6 measured that flag: on
two flat caps the surface normal was the same `(0,0,1)` while `normalOrientation` differed (false at z=0,
true at z=10), so true means "coincides" and false means "reversed". Returns null when the normal cannot be
read. There is deliberately no second, reflected route: the documented signature is `GetNormal(paramU,
paramV, out x, out y, out z)` (https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssurface_getnormal.html), this
call is it, and a fallback that searched for other arities could only ever have returned null while reading
like a working route.

## <a id="circle-edge-params"></a>Circular edge radius and centre (probe P2.6)

Radius and centre of a circular edge, read as `ksCurve3D.GetCurveParam()` → `ksCircle3dParam` (probe P2.6:
`get_radius()`, the curve bbox and the length divided by 2π agree to the last digit on R10). Deliberately
never computed as L/2π: on a straight 100 mm edge that formula yields a plausible-looking 15.915 mm, which
is precisely the kind of number a caller would then believe. A non-circular curve therefore reports no
radius at all.

## <a id="arc-endpoints-range"></a>Arc end-angle range (probe, 20.09.2026)

MEASURED 20.09.2026 on the `publish-mania-20260920` delivery by probe `scratch/_arc_angle_range_probe.py`:
the call fails if and only if `start_deg + sweep_deg` leaves [−360°, 360°]. A start of exactly 360° and an
end of exactly 360° are accepted (R3, R4 of the probe). The angles are therefore shifted by a whole number
of turns - exactly enough for the end to enter the range; the sweep does NOT change under the shift, i.e.
the arc stays the same. If the end angle is already inside the range, the values are returned AS IS: the
call stays as before. But ADDITIVITY HOLDS ONLY TOGETHER WITH THE CALLER'S `Min`/`Max` ORDER - see the
comment in the `SketchEntityKind.Arc` branch: passing "start, end" instead of "smaller, larger" changed the
result on inputs the edit did not touch (measured by control R8).

LIMIT: NOT MEASURED and therefore NOT touched - the case `|sweep_deg| > 360` with the end angle INSIDE the
range (e.g. start −180°, sweep +400°). Such a sweep cannot be expressed by one arc (two angles define at
most one turn), but what the kernel does today was not measured, so the behaviour is left as before rather
than replaced by a guess.

## <a id="edit-sketch-result"></a>Sketch edit result fields

Result of a sketch edit. `DeletedEntities` counts what was actually deleted (by the vendor convention 1 =
success, and deletion goes by the object found at the stored coordinate), while `ExpectedDeleted` is how
many were expected. The difference is not smoothed away: a partial cleanup means a dirty profile, not a
deleted edit.

`ProbePointsFromModel`: True when the coordinates for finding the objects to delete are derived from the
geometry of the dependent body rather than taken from session memory. The caller needs to know: the route
works for a sketch the server did not draw, but is limited to the measured configuration (base XY, a circle
in the profile, through-cut) - see `SketchPointDerivation`.

## <a id="lifecycle-suppress-delete"></a>Suppress, restore and delete a feature (probe L, 12.09.2026)

Suppress, restore and delete a feature (docs/05 §4.4, §7; SM-30 in the catalog). MEASURED by probe L on
12.09.2026 on live v24 (docs/acceptance/api7/sketch-lifecycle.md), not inferred from names: L.7 -
`ksFeature.excluded = true` removes the extrusion body (V becomes the plate's volume), `false` restores it,
and the feature count does not change. One `RebuildDocument()` suffices: unlike a parameter edit (P2.3),
`ksEntity.Update()` is not required here. L.8 - `ksDocument3D.DeleteObject(entity)` deletes the feature:
returns true, feature count minus one. INVARIANT: there is no "dependent features" member in API5 or API7 -
verified by reflection over both assemblies (0 matches for Dependent*/Preceding*/UsedBy*), not assumed. So
the API does not answer "what will fall away with this feature", and the server does not pretend it does: it
returns candidates - features standing after the deleted one in the tree - and their presence blocks deletion
until the caller explicitly agrees.

## <a id="session-identity-rules"></a>Session identity rules (P0.5, spec 1.6)

One KOMPAS instance the Worker owns or is attached to, plus the documents registered against it. Every
method here must be called on the Worker's single STA thread; nothing in this class is thread-safe by
design, because the COM objects behind it are not. Identity rules that the contract depends on:

* A document is addressed by server UUID, never by "the active tab". `Document3D()` is a factory (proved in
  P0.5: two calls give different IUnknowns), so there is no ambient document to misuse.
* Revisions are server-side counters bumped on mutation, rebuild, reload and restore. External edits made in
  the KOMPAS UI cannot be trusted to raise events here, so a cheap fingerprint is compared before every
  mutation and the document is marked `conservative` - the limitation is reported, not hidden.
* COM references are held only in this object and released exactly when the document is closed or the
  session ends (spec 1.6).

## <a id="component-count-structure"></a>Component count from structure, not the API5 collection (04.10.2026)

Assembly component count - from the STRUCTURE (`IPart7.PartsEx`), not the API5 collection. MEASURED
04.10.2026 by a live run, refuting the former route: the API5 collection `EntityCollection(o3d_part = 104)`
on an assembly with ONE inserted component returned 7 - not a component count. The count comes from the API7
structure: `IAssemblyDocument.TopPart` → `IPart7.PartsEx(ksAllParts)`, recursively over subassemblies. The
error was visible only on a live assembly: before it there were no assembly tools. LIMIT: the walk is
bounded by depth - a subassembly cycle (if possible) must not loop the server. The limit is named as a
number, not "reasonable": 64 levels.

## <a id="part-now-reacquire"></a>Re-acquiring the root part per operation (v24)

Current root part of the document. The handle captured at creation goes stale once a feature is created:
measured on v24, a cached `ksPart` started returning an empty `BodyCollection` and a null `GetMainBody()`
for a document that demonstrably had a solid body and saved it to disk. Re-acquiring from the document per
operation is therefore not a micro-optimisation to skip - it is what makes reads agree with what KOMPAS
actually holds. `Part` is kept for identity checks and release bookkeeping only.

## <a id="rotated3d-numbering"></a>Rotation tree number vs factory number (SM-03, 17.09.2026)

`o3d_Rotated3D` - the number under which a finished rotation feature lies in the API5 tree. MEASURED
17.09.2026 by acceptance SM-03 (row RO.10t), and the measurement refuted the expectation: the tree was
expected to lag the factory by 531 as with a hole (52→583), showing the rotation under 584. Row RO.10t
printed the tree types after a cut by rotation: `features=2 types=['25', '29']` - the base plate under 25
(`o3d_bossExtrusion`) and the cut by rotation under 29. So for a rotation the FACTORY number and the tree
number COINCIDE (29 = `o3d_cutRotated`), unlike a hole. The conclusion is not "the number is the same" but
"the two numbering systems behave differently across families, and analogy must not be assumed" - which is
why the value here is measured, not derived. Factory side, MEASURED by probe R.24:
`(int)ksObj3dTypeEnum.o3d_baseRotated = 27`.

## <a id="hole3d-numbering"></a>Hole tree number vs factory number (probe N.1, 17.09.2026)

`o3d_Hole3D` - the number under which a finished hole feature lies in the API5 tree. MEASURED by probe N.1
on 17.09.2026, fixing a real defect: the adapter searched for the feature by `HoleOperation` = 52 and so
NEVER found it when the hole was created by the API7 route - no `feature_ref` was issued and editing was
unreachable. The probe printed both tree collections before and after creation: `NewEntity(52).type = 52
(o3d_holeOperation)` while `IHoles3D[0].ModelObjectType = 583 (o3d_Hole3D)`, and exactly one entry appeared
in the tree - `OperationElement(110)[1] type=583 ("Hole:1")`. 52 never appeared in the tree. Two different
numbering systems, and they must not be confused.

## <a id="body-reposition-numbering"></a>Reposition tree number 79 vs creation number 569 (measured)

The reposition feature type in the API5 tree: MEASURED 79. After `kompas_reposition` exactly one new entry
appears in the tree - `type=79 "Change of position : Body 1"`. 79 is NOT 569. The number 569
(`o3d_BodyReposition`) belongs to creating the object, while in the tree the feature lies under 79. Exactly
the same lesson as with a hole (`HoleOperation` = 52 versus `Hole3D` = 583): the creation side and the tree
side are numbered differently, and one must not be taken for the other. The same number 79 is also carried
by the auxiliary feature "Body copy", which implements `keep_tools=true`. The collision is harmless only
because the filter is applied WITHIN one operation: for a boolean operation the expected type is 69 and the
copy (79) never becomes a candidate; for a reposition the expected type is 79 and there is exactly one new
entry of that type. One must not rely on "79 means reposition" outside the operation's context.

## <a id="kompas-units"></a>Unit selectors (spec 4.5, P0.7)

Unit selectors for the measurement calls, and the one conversion the server performs. In API5 the unit is an
argument, not a property of the model: `GetLength(bitVector)`, `GetArea(bitVector)`,
`CalcMassInertiaProperties(lengthBits | massBits)`. The selector values are `ST_MIX_SM=0, ST_MIX_MM=1,
ST_MIX_DM=2, ST_MIX_M=3` for length and `ST_MIX_GR=0, ST_MIX_KG=16` for mass (from `KAPITypes.ldefin2d`).
Two consequences that the whole adapter follows:

* Coordinates have no unit argument: they are model millimetres, so `GetPoint`, `GetGabarit`, sketch input
  and transform output need no conversion at all.
* `0` is the centimetre selector and is outside the documented interval `[ST_MIX_MM..ST_MIX_M]`. Leaving the
  argument at its default silently returns cm - which is the 10× discrepancy the historical scripts recorded
  (spec 4.5) and what P0.7 reproduced on a real edge (100 mm reported as 10). This class exists so no call
  site can repeat that mistake.

## <a id="kompas-choose"></a>Body-selection enumerations (probe P2.6)

Selectors that tell an extrusion which body it must act on. The two enumerations are the vendor's own
(`ksChooseType` and `ksChooseBodiesType`, both from `Interop.Kompas6Constants3D.dll`) and their values were
copied from that assembly by probe P2.6, not guessed: the members are bare `Int32` on the interop
interfaces, so a wrong number compiles and silently means something else. Measured behaviour, all of it from
`docs/acceptance/p2/p2-probe-report.md` step P2.6:

* `Bodies` is the value the kernel consults the body list with; requested 3 reads back 3 after `Create`.
  Requesting 0 is clamped to 1 - 0 is not a valid `ksChooseType` - and the default when nothing is set is
  also 1.
* `Parts` is not decorative: with a parts-only selector and no parts in the document, `Create` returned true
  and no body lost any volume.
* `NewBody` on a boss made the feature create an additional body even where its profile overlapped an
  existing one (2 bodies became 3). Nothing in this server wants that, so the only value ever written here
  is `ManualEditing`.

## <a id="interop-resolver"></a>Runtime interop resolution (P0 finding)

Loads the vendor KOMPAS interop assemblies from the user's installation at run time. Why this exists (a P0
finding, not a hypothetical): the interop references are declared with `Private=false`, so nothing from the
ASCON installation is copied into our build output or our package - the delivery does not redistribute
licensed binaries. The consequence is that a plain reference is enough to compile but not to run: the first
use of a `Kompas6API5` type throws `FileNotFoundException` unless the assembly is resolved from the install
directory. This class is that resolution step, and it must run before any interop type is touched. Search
order is explicit and reported, because "we silently found some copy of the API" is exactly the kind of
ambiguity that produces a version mismatch nobody can diagnose later.

## <a id="com-message-filter"></a>COM message filter (spec 1.6)

Message filter installed on the Worker's STA thread so a busy KOMPAS is handled by COM's own retry mechanism
– and every retry is counted. Unbounded silent retry would turn a wedged KOMPAS into an unresponsive Worker,
which spec 1.6 forbids. Return values follow the documented convention: `RetryRejectedCall` returns a delay
in milliseconds to retry, 0 to cancel the call, or -1 to let the default (fail) apply. `MessagePending` sets
`ulReject` to `SERVERCALL_RETRYLATER` (retry) or `SERVERCALL_REJECTED` (fail).

## <a id="image-export"></a>Raster image export route (probe P2b, 21.09.2026)

Raster snapshot of the model via the documented API5 route (order `KOMPAS_EXPORT_IMAGE_DEVELOPER_PROMPT.md`
§4). Route: `ksDocument3D.RasterFormatParam()` (`ksdocument3d_rasterformatparam.html`) → `ksRasterFormatParam`
(`ksrasterformatparam_props.html`) → `Init()` → set `format`, `colorBPP`, optionally
`extResolution`/`extScale` and `returnResultAsArrayBytes` → `ksDocument3D.SaveAsToRasterFormat(fileName,
rasterPar)` (`ksdocument3d_saveastorasterformat.html`). MEASURED (probe P2b, delivery
`publish-deproutes-r2-20260921`, build 24.0.0.2799): the two modes are mutually exclusive, not assumed. A
NON-EMPTY file name writes the file (PNG 8639 bytes, 328×448) while `resultArrayBytes` stays `null` - six
call shapes (a pre-filled empty array, another raster write method, re-reading the property, a fresh
parameter object after writing) all gave `null`. An EMPTY file name gives `resultArrayBytes` =
`System.Byte[]`, 8639 bytes, PNG magic `89504e47…`, and NO file appears on disk (directory snapshot
before/after). Hence the method's design: "return an image" and "write a file" are DIFFERENT route calls.
When both are wanted, the render is done ONCE in byte mode and the file is written from those same bytes - a
second render could give a different frame, and "the file and the response are the same" would then be
unverifiable. LIMIT: this method does not control the projection (`IViewProjection7` is documented but left
to a separate order and named as a remainder), does not silently downscale, and does not report "success"
without bytes. The snapshot is the server window's current view; camera state is not captured and is named
unverified in the response.


## <a id="readonly-save-guard"></a>Запись в файл документа, открытого read-only (review 05.10.2026)

ACCESS IS REMEMBERED BECAUSE IT DECIDES THE OUTCOME OF A LATER WRITE. A document opened `read_only` must
not be saved "in place": the save call carries no path field and the Host has nothing to check - its policy
judges the CALL's fields, not the document's path. So `kompas_open_document(path under read_only_roots)` +
`kompas_save_document` without target_path wrote straight into a root declared "read-only" (defect H4,
review 05.10.2026). The access flag is what closes that write.

## <a id="session-compaction"></a>Ядро сеанса - история, вынесенная из кода

Identity rules the contract depends on: (1) a document is addressed by server UUID, never by "the active
tab" - <c>Document3D()</c> is a factory (P0.5: two calls give different IUnknowns); (2) revisions are
server-side counters bumped on mutation, rebuild, reload and restore - external UI edits cannot be trusted
to raise events, so a fingerprint is compared before every mutation and the document is marked
<c>conservative</c>; (3) COM references are held only here and released when the document is closed or the
session ends (spec 1.6).

Visibility semantics: <c>launch</c> - make_visible=true shows, false explicitly hides, as P0.4b did;
<c>attach</c> - false is a request for "invisible" that is not applied to another's window. Opening a file
in an invisible window of the user's KOMPAS would hide the result of their work.

Component count: MEASURED 04.10.2026 by a live run (the error was visible only on a live assembly). LIMIT:
the walk is bounded by depth, named as a number not "reasonable": 64 levels, so a subassembly cycle (if
possible) cannot loop the server.

Rotation numbering: MEASURED 17.09.2026 by acceptance SM-03 (row RO.10t). The two numbering systems behave
differently across families, and analogy must not be assumed - the value is measured, not derived.

Hole numbering: MEASURED by probe N.1 on 17.09.2026, fixing a real defect. Two numbering systems that must
not be confused.

Boolean operation type: MEASURED 18.09.2026 with the instrument <c>scratch/b3-measure-feature-types.py</c>
via <c>kompas_list_features</c> (which also printed <c>entity.type</c>).

Reposition numbering: the filter is applied WITHIN one operation (boolean expects 69, the copy 79 never
becomes a candidate; reposition expects 79, exactly one new entry).


## <a id="lifecycle-compaction"></a>Жизненный цикл признака - история, вынесенная из кода

MEASURED by probe L on 12.09.2026 on live v24 (docs/acceptance/api7/sketch-lifecycle.md), not from names.
Suppressing an already-deleted feature gave CAPABILITY_UNAVAILABLE, plausible but wrong in substance
(row L10). MEASURED 19.09.2026 (probe I) that two consecutive features of one kind carry the SAME name.
Row L09, run 12.09.2026: DeleteObject on a dead object answers true, the tree is unchanged.

Counter: MEASURED (L04/L05 12.09.2026; probe I 19.09.2026, run
<c>c90961c6a3ba478697da5bc243040719</c>, report docs/acceptance/api7/feature-identity.json): restoring
returned one (2→3).

Position: MEASURED 19.09.2026: suppressing and restoring a base extrusion (BG19/BG20) recreates the tree
element and FindIt on the old object answers −1 though it is alive and IsCreated. Two consecutive features
of one kind share a name (probe I). MEASURED 19.09.2026, probe I: a name check would be FALSELY negative
if a same-named neighbour remains. "The target was removed" and "the cascade went as expected" are
DIFFERENT claims. MEASURED 19.09.2026: after suppressing and restoring a base extrusion, <c>FindIt</c> on
the old object answers −1.

Tree position: MEASURED 19.09.2026 (probe I, run <c>c90961c6a3ba478697da5bc243040719</c>); both outcomes
were measured in one run. MEASURED by probe L.7: excluded=true on a 40×20 through hole gave the plate back
its 80000 mm³. MEASURED: suppressing the first of two consecutive reposition features took collection 110
from 4 to 2 while restoring returned one. Row L09: DeleteObject on a dead object answers true, the tree is
unchanged. A strict "exactly one fewer" made a CASCADE falsely negative (a 5→3 tree gave
feature_removed=false though the target was gone). Two consecutive reposition features carry ONE name
("Change of position : Body 1").

## <a id="pattern-route"></a>Маршрут массивов: обоснование и ожидание (SM-18/SM-19/SM-23)

The "type → interface" correspondence is taken from the SDK help page <c>copytype.html</c>, opened on
the wire in this work, not inferred by analogy with rotation. The axis route
<c>IAuxiliaryGeomContainer.Axes3D.Add(o3d_axis2Points)</c> was already MEASURED by rotation (SM-03)
and is reused here as MEASURED, not assumed. DOC: <c>icircularpattern_props.html</c> calls
<c>Count1</c>/<c>Step1</c> RADIAL and <c>Count2</c>/<c>Step2</c> ANNULAR, with <c>Step2</c> labelled
«Угловой шаг (градусы)». This closes OQ-B-02 and refutes the expectation recorded in the work order
(§6.3, §8), where the first direction was considered annular. It is the EXPECTATION that is called
into question, not the measurement. Ранее эта ссылка вела на
docs/decisions/adapter-features.md#pattern-route.

## <a id="chamfer-compaction"></a>Фаска - пояснения, вынесенные из кода

Ранее эта ссылка вела на docs/decisions/adapter-features.md#chamfer-route и
docs/decisions/adapter-features.md#chamfer-method-substitution.

Basis - probe F (<c>docs/acceptance/api7/chamfer.md</c>), not method names. TEST: F.3/F.5 - editing the
legs applies in place both before and after save→close→reopen, and the value is read back. TEST:
F.9/F.10 - with the "distance and angle" method the chamfer is built only via
<c>IChamfer.Angle = ksChamferSideAngle</c>, the angle in DEGREES; the radian hypothesis would have
given a negative number and was refuted by measurement. The names are what
<c>IChamfer.BuildingType.ToString()</c> returns from API7 (API5 has no method at all); needed to
recognise the feature for which the API5 write route loses the build method. Without RebuildModel the
IChamfer write stays a representation: MEASURED by probe E on IExtrusion.Sketch, and the same call
order is mandatory for the chamfer. Search by type and order, not by name: F.8 MEASURED that a name
given in API5 reads differently in API7 - the name is not a feature identifier. If an API7 bridge is
also built to the same document and there is one chamfer in it, angle and method are added. An empty
field means "not read", not "zero". Measuring the angle of "the first chamfer in a row" with several
chamfers would attribute a foreign number to the feature, so on ambiguity the server returns null and
explains the cause in <c>unverified</c>. Differences from the API5 route (UpdateChamfer): the feature
is addressed by INDEX in <c>IModelContainer.Chamfers</c> and matched to the API5 feature by the first
leg (the name is not an identifier - F.8); the second leg is DERIVED from the angle, so it is written
only if the client set it explicitly; success is not "Update() returned true" but that the model gives
the new angle and the leg <c>d₁·tg α</c>. An angle is not physically expressible in API5
(ksChamferDefinition declares no "angle" member), so with angle_deg present IChamfer writes. MEASURED:
writing Angle followed by Update() applies to the model, the build method stays ksChamferSideAngle, and
the kernel recomputes the second leg as d₂ = d₁·tg α. Only calls without angle_deg land here. If the
feature's method is "distance and angle", the API5 write would lose the angle, so such a call is
refused BEFORE the mutation with an explanation of what to do instead. The API5 write route
(SetChamferParam) only knows ksChamferTwoSides and does NOT preserve a "distance and angle" chamfer
(ksChamferSideAngle, d₂ = d₁·tg α), so the write is refused BEFORE the mutation. The method is read
from API7; on an ambiguous match ReadChamferAngle returns null and the write is NOT refused - "method
not read" is not "method is angular". The second leg changes only together with the first or
explicitly: otherwise editing "one leg" would silently become editing both. The order "write →
Update() → RebuildDocument()" is part of the contract: without Update() the model stays as it was
although every setter returned true (P2.3 for extrusions, F.3 for the chamfer). The value is read back
from the new definition object, the feature must stay the same, and geometry is confirmed by volume
measurement.

## <a id="loft-compaction"></a>Элемент по сечениям - пояснения, вынесенные из кода

The mandatory row <c>SM-05.base.mode_couplings</c> requires section correspondence CHAINS, and API5 has
none at all: neither <c>ksBaseLoftDefinition</c> nor <c>ksBossLoftDefinition</c> declares
<c>AddCoupling</c> or <c>Coupling</c>. MEASURED: assigning the <c>ILoft.Sketchs</c> array gave a
read-back of <c>System.Object[]</c> of 2 elements and <c>Update() = True</c>. Parallelism of section
planes is the CALLER's duty, and work order B5 §9.2 requires a NAMED refusal on non-parallel planes.
ILoft itself neither requires nor forbids it: MEASURED (step B5.11) that on non-parallel planes it
either refuses facelessly or builds a body describing something other than requested. A silent
"probably parallel" would be a claim without measurement. The same transfer technique was MEASURED on
IChamfer.BaseObjects (transfer + assignment of object[]). The chain order is documented by the factory
(«задать параметры операции и вызвать <c>IModelObject::Update</c>», <c>ilofts_add.html</c>) and
MEASURED at step B5.18: a chain set before the first Update() is applied in one build (CouplingsCount
= 1, volume 20000 at a 20 mm offset out of 80 - the same value as a chain added after the build). The
content of correspondence chains is not set: the chain's existence was measured, not its
configuration. The build method at the end sections is expressed by
ILoft.BuildingType(BeginSection); on a freshly created feature both the start and the end read 0
(ksLoftAuto). Values 1/2/3 (by normal, by object, dome) were NOT measured on this route, so they are
not accepted silently - otherwise "accepted and ignored" would survive to acceptance looking like a
performed mode. This is not pedantry: a chain shorter than the section set defines a correspondence
not for all sections - i.e. a different body than requested. A chain count without content would prove
only the object's existence, whereas the mandatory row requires a DEFINITE section correspondence.
This checks that the set arrived in full, not that the order was honoured. Ранее эта ссылка вела на
docs/decisions/adapter-features.md#loft-route.

## <a id="pattern-compaction"></a>Массивы - пояснения, вынесенные из кода

The tail is factored out because the three families differ ONLY in the setup while the proof is
common: <c>Update()=true</c> is "accepted", and without reading the model back the response would not
tell "built" from "written". A name match without the stamp does not tell two same-named features
apart. This is the "per instance" measurement: <c>ksCylinderParam.GetPlacement()</c> gives the surface
placement and <c>ksPlacement.GetOrigin</c> the axis point. Without coordinates the check would reduce
to volume, and volume does not tell N holes from N−1 holes and one overlapping. Radius and height are
FILTERED, not "all cylinders" taken: otherwise foreign cylindrical geometry would enter the instance
count and the check would become non-discriminating. Geometric copy is documented by SDK page
<c>ifeaturepattern_geometrypattern.html</c> and user help <c>48_3_3_geometricheskiy_massiv</c>, but has
its own constraints (surface closure, non-intersection of instances, same operation kind) and its own
mode - <c>SM-18.grid.operations.geometry</c>, which is NOT in the mandatory B4 scope. The factory's
numeric type is chosen once, and a "body pattern from operations" does not assemble. Three numbers are
returned: how many matched, what is missing and what is extra - "matched" without "extra" does not tell
a correct grid from one with an added instance. The tolerance is not tuned after a failure.
<c>Num(double?)</c> is already declared in the rotation part and is reused here: two same-named members
in one partial part is a compile error, caught by the build rather than by eye. The user help
<c>glava_48_obzhie_svedeniy</c> states directly that excluding instances is unavailable for a mirror
pattern and a pattern-by-sample. This is a domain inapplicability with a source, not an unclosed
action. The order "write → Update() → Rebuild()" was MEASURED on rotation and is the same here. The
feature name is read from the tree WRAPPER: ksFeature.Name does not exist (a compile error caught by
the build), while the name lives at ksEntity.name.

## <a id="mirror-all-bodies"></a>Зеркальный массив: источник зависит от режима (B4M.10)

WHAT IS THE SOURCE DEPENDS ON THE MODE, and this is MEASURED by running row B4M.10:
selected_operations reflects OPERATIONS, all_bodies reflects BODIES. While the kind was the same
(Operations), the "all bodies" mode was unexecutable in TWO ways: an empty list created the feature but
reflected no body (bodies stay 2, volume 8000), and explicit body: references were rejected
STALE_REFERENCE with "points to __ComObject instead of a feature" - because body references were parsed
as feature references. The tool contract, however, promised body: references for all_bodies from the
start. Ранее эта ссылка вела на docs/decisions/adapter-features.md#mirror-all-bodies.

## <a id="features-compaction"></a>Чтение и правка признака - пояснения, вынесенные из кода

Ранее эта ссылка вела на docs/decisions/adapter-features.md#feature-edit-basis.

The vendor wrapper has no ksHoleDefinition at all (MEASURED - of 67 declared definitions there are
ksChamferDefinition and ksFilletDefinition, but no hole), so the mode parameters live only in API7. The
type is 583 (o3d_Hole3D), not 52 (o3d_holeOperation, the creation factory number, which never appears
in the tree - see FindHoleEntity). A tree entity arrives as a raw __ComObject and refuses QI
(MEASURED). B3 features are recognised by the TREE NUMBER (69 / 633 / 50 / 79) for the same reason as
rotation: they have no API5 definition at all, GetDefinition() returns null, and the API7 object is not
an API5 feature, so neither the definition nor QI will do (MEASURED). MEASURED: a feature created by
NewEntity(45) (o3d_baseEvolution) shows in the tree as 46 (o3d_bossEvolution) and its definition
answers ksBossEvolutionDefinition - NOT ksBaseEvolutionDefinition. Recognition by the creation-response
number would never find it: the same defect was measured at the hole (52 → 583) and at rotation (27 →
584). The parameter set lives on IRotated, and it has no API5 definition at all (entity.GetDefinition()
returns null, MEASURED at the SM-03 acceptance). The feature index is taken by matching on composition,
not by angle: an angle is not an identifier, and FindIndexesByAngle remains a fallback. B3 features are
read from the LIVE model by routes measured in probes BO.2–BO.11, SP.10 and RP.8–RP.12. Some
reposition-family parameters do not read at all, and the reason is named in solid.unreadable_parameters
rather than substituted with a zero. A failure reason is NAMED rather than left as an empty field:
"not read" and "zero" must be distinguishable - silence is a claim too, and an empty field is
indistinguishable from "forgot to fill it in". OperationResult lives only in API7, and its absence is a
route boundary, not a zero. The names and MEASURED reasons already sit in solid.unreadable_parameters,
and duplicating them here would make the summary unreadable. A pattern edit is chosen by the pattern
FIELD itself, not by the tree type number: for the other families the number is measured, for a
pattern it is not, and it must not be guessed. The pattern branch stands BEFORE the API5 definition
read: a pattern feature has no API5 definition at all (the object is created by the API7 factory), so
below it would fall into "this feature is null" and the edit would be unreachable. Couplings were once
accepted and swallowed (MEASURED: a "distance1_mm + couplings" call on a chamfer returned success while
sibling shift_mode and section_refs were rejected INVALID_ARGUMENT). Found by unit test
SolidFeatureClassificationTests. The hole branch stands HERE, not among the branches by API5
definition: a native hole has NO definition - ksHoleDefinition does not exist among the 67 declared
definitions of the vendor interop (recorded in docs/04) - and `kompas_get_feature` reads a hole the
same way (definition_interface = null). Rotation has no API5 definition at all, GetDefinition() returns
null, and QI(IRotated) on a tree entity refuses (MEASURED); a rotation edit changes only angle and
direction, and profile and axis retargeting was not measured. Reposition has no API5 definition at all
(GetDefinition() returns null), and the API7 object is not an API5 feature - so neither the definition
nor QI will do (MEASURED); 569 (o3d_BodyReposition) is the CREATION side, the feature lies under 79 in
the tree. Split and cut have no API5 definition, and the API7 object (ISplitSolid, ICut) is not an API5
feature (MEASURED); 633 is o3d_SplitSolid, 50 is o3d_cutByPlane, and both numbers are read from the
TREE, not from the creation factory (for a split the factory and tree diverge just like the hole
52/583). Boolean is recognised by number 69 (o3d_aggregate) in the tree, not by the API5 definition:
the route through ksAggregateDefinition measurably does not work (it has a writable BooleanType and NO
way to set bodies - OQ-A16). The operation-kind edit by the API7 route was MEASURED: rewriting
IBoolean.BooleanType on an existing feature changes the geometry, and the "write + Update()" pair was
confirmed by control E-E. The B5 branch stands HERE, not earlier: above, families with API5 definitions
have already rejected foreign fields by name, and "shift_mode to a chamfer" reads clearer to the caller
than "the feature does not answer the sweep interface". Recognition is by the FIELD itself, not by the
tree type number: for a sweep the creation number and the tree number diverge (45 → 46, step B5.12),
and addressing by number would edit the wrong feature. Radius and edge set are different routes and
different edit subjects; they must not be mixed in one call: "changed both" would be indistinguishable
from "one of the two applied". Draft and its orientation carry over from what the feature reports: a
parameter the caller never mentioned must not silently reset to zero. SetSketch returns true, but
GetSketch() reads back the PREVIOUS sketch and the volume does not change - the support change is not
applied by this route. The cached-RCW hypothesis was not confirmed: writing to a fresh object gives the
same outcome; the mode stays blocked. MEASURED by row L12: when the refusal came after BumpRevision,
the document got a new revision with an unchanged model, and all issued references went stale because
of an operation that did nothing. The one written through may be a cached view, and "we called
SetSketch" is not evidence that the model accepted it. KOMPAS accepted SetSketch and did not change the
model. Reporting this as "success" would lie to the caller about the support edit, so the route refuses
honestly and after the read-back: the feature is intact, the volume unchanged, nothing happened.

## <a id="save-state-fingerprint"></a>Состояние сохранности и отпечаток модели - история из кода

Отпечаток отвечает ровно на один вопрос - менял ли модель кто-то, кроме нас: правка в UI не проходит
через BumpRevision, и заметить её больше нечем. Нечитаемый отпечаток НЕ доказывает неизменность и даёт
"unknown", а не "clean": ранее отражённый `IsSaved` и сравнение отпечатков вместе сообщали `dirty=false`
сразу после мутации, обесточивая обе защиты закрытия.

## <a id="object-type-numbers"></a>Номера признаков в дереве API5 - история из кода

Ранее эти ссылки вели на комментарии к константам KompasObjectTypes в Api5Session.cs.

Булева операция: после kompas_boolean в дереве появляется ровно одна запись -
`type=69 "Boolean operation:1"`. Разделение: после kompas_split появляется ровно одна новая запись -
`type=633 "Cut:1"`. Сечение плоскостью: после kompas_cut_by_plane появляется ровно одна новая запись -
`type=50 "Section:1"`. Инструмент измерения - `scratch/b3-measure-feature-types.py` через
`kompas_list_features`.

## <a id="plane-offset-sign"></a>Смещение плоскости: знак и направление

DOC: `ksplaneoffsetdefinition_props.html`, «смещение вдоль нормали базовой плоскости». MEASURED:
`direction=true` means offset along the plane's own normal for XY, XZ and YZ alike.

## <a id="view-swap"></a>Applying and restoring a view projection (probe VIEW, 05.10.2026)

Projection control for a snapshot uses the documented API5 route: `ksDocument3D.GetViewProjectionCollection()`
(`ksdocument3d_getviewprojectioncollection.html`) → `ksViewProjectionCollection`
(`ksviewprojectioncollection.html`: `GetCount`, `GetByIndex`, `refresh`, `viewProjectionScheme`) →
`ksViewProjection` (`ksviewprojection.html`: `GetViewProjectonType`, `IsCurrent`, `SetCurrent`). The type
applied is `ksViewProjectionType` (`ksviewprojectiontype.html`) - isometric is 7 - NOT the API5
`ProjectionType`; the two enums do NOT share their numbers and the help page says the correspondence is
not implied. The published names are ASCII (front/rear/up/down/left/right/isometric) because the kernel's
own names ("#Спереди") are localized.

MEASURED (probe VIEW, invisible and visible document): a projection change does NOT raise
`IKompasDocument.Changed` - false before, false after, false after the restore. The document revision is a
server-side counter and is untouched, so restoring the previous view after the snapshot is honest, not a
courtesy. MEASURED: `SetCurrent()` returning TRUE is NOT the result - the type read back from
`GetViewProjectonType()` is, and a mismatch refuses `VIEW_UNAVAILABLE` rather than snapping under a label
the kernel never confirmed. MEASURED: the raster extent is fitted to the model's extent in the CURRENT
projection, so different projections legitimately differ in size (front 399×320, up 399×202, isometric
423×503), and two consecutive snapshots of one projection are byte-identical. MEASURED: the CURRENT
projection of a freshly created part is dimetry (type 8); it reads back through <c>GetViewProjectonType</c>
and, once <c>dimetric</c> is published, is restorable, so a first <c>view</c> without <c>keep_view</c>
succeeds and restores it. LIMIT: a projection type the collection does not carry, or one with no published
name, cannot be restored; a call asking to switch without <c>keep_view</c> then refuses BEFORE the first
<c>SetCurrent</c> rather than move the window. MEASURED: that condition is reachable through documented
calls - a VISIBLE window (`connect(launch, make_visible=true)`, documents inheriting the app's visibility)
answers no `IsCurrent=true` before the first `SetCurrent`, so the refusal is measured live, not only coded.
INVARIANT: an UNCONFIRMED switch (`SetCurrent` invoked, the read-back disagreeing) restores the previous
type EVEN UNDER `keep_view` - the consent covered a CONFIRMED projection, and the window may already have
moved; the outcome is named in the refusal, whose text never ends in an empty tail. The restore itself
never throws - an exception out of the caller's <c>finally</c> would replace a finished snapshot's result
with a refusal.

## <a id="selection-predicate-fields"></a>Предикат выбора граней: применяется ровно то, что объявлено (07.10.2026)

**Что было.** `ResolveSelection` начинался с ветки `UnsupportedPredicateFields`: она собирала имена
полей, которых реализация не применяет (`coordinate_space`, `extremum_axis`/`extremum_mode`,
`bbox_range_mm`), и отказывала `INVALID_ARGUMENT`, если такие поля пришли.

**Что измерено.** Ни одно из этих имён не достижимо: объект `selection_predicate` в опубликованной
схеме закрыт (`additionalProperties=false`), а Хост валидирует аргументы против схемы инструмента
(`ToolInvoker.Validate`) ДО отправки команды в Worker. Ветка отказа не могла сработать ни разу —
мёртвый код, обещавший проверку, которой нет.

**Что решено.** Ветка и метод удалены; поля `coordinate_space`/`extremum_*`/`bbox_range_mm` убраны из
контракта (см. `docs/decisions/contracts.md#selection-predicate-coordinate-space`). `ResolveSelection`
применяет ровно те поля, что объявлены: `surface_type`, `area_range_mm2`, `normal_direction`,
`normal_angle_tolerance_deg`. Нормаль читается в системе координат детали, которой принадлежит тело, —
инструмент работает от ссылки на тело и другой системы координат не имеет.

## <a id="attach-candidates"></a>Кандидаты attach - записи ROT, а не процессы ОС (07.10.2026)

**Что было.** `running_instances` считает процессы `KOMPAS` в ОС, а `attach` выбирает из записей
Running Object Table по ProgID КОМПАС. Числа отвечают на разные вопросы, но в ответе было только одно
из них.

**Что измерено.** Клиентский агент (отчёт `mcp-bugs.md`, замечание к MCP-001): `running_instances = 2`,
а `attach` без `process_id` подключился к PID 14672 без `AMBIGUOUS_APPLICATION`. Код ведёт себя по
своему правилу — кандидатов была одна запись ROT, — но клиент, видя два экземпляра, не может понять,
почему выбор однозначен. Дефект в контракте, а не в выборе.

**Что решено.** В ответ `kompas_capabilities` добавлено `rot_kompas_entries` — число записей ROT,
посчитанное ТЕМ ЖЕ перечислителем (`RunningObjectTable.EnumerateKompasEntries(KompasProgId)`), которым
пользуется `Api5Session.Attach`. Сбой перечисления даёт `null` и названную причину в
`rot_kompas_entries_failure`, а не ноль: «перечислитель бросил» и «КОМПАС не зарегистрирован» —
разные диагнозы, и ноль молча утверждал бы второй. Описания `kompas_connect` и `kompas_capabilities`
называют разницу прямо. Второй процесс при одной записи ROT на живом прогоне 07.10.2026 не наблюдался;
специально этот случай не исследовался (наряд §B4.4).

## <a id="extrude-self-check"></a>Невычислимое ожидание выдавливания - невыданная проверка (08.10.2026)

**Что было.** Когда ожидаемый объём посчитать было не из чего (профиль записан под другой ссылкой,
дуги без аналитики, переоткрытый документ), `kompas_extrude` всё равно выдавал проверки
`expected_basis` и `volume_delta` с `passed=false` и `observed=not_computable`.

**Что измерено.** 08.10.2026 клиентской приёмкой 0.3.0 (два случая: окружность R52 и плита 40×40×10)
и воспроизведено на бинарях дерева (группа R40, строка `R40.02` до правки): при ВЕРНОЙ геометрии
(расхождение 8·10⁻¹¹ мм³) проверки шли с `passed=false`, а в том же ответе лежал
`volume_delta_mm3` с основанием `new_body_volume`.

**Что решено.** Ожидание, которое посчитать нельзя, НЕ выдаёт проверку с `passed=false`: причина
уходит в `unverified_aspects` первой строкой (`expected_volume_not_computable - …` с названием
причины), `geometry_confirmed` остаётся `false`, уровень — `call_returned`. «Не подтверждено» и
«проверено и не совпало» — разные состояния, и клиент должен их различать.

**Тот же приём в соседних местах.** Проверены `Api5Session.Chamfer.cs` (фаска по углу),
`Api5Session.Geometry.cs` (скругление и фаска по рёбрам), `Api5Session.Hole.cs` (создание отверстия и
правка отверстия). Везде проверка `volume_delta` выдаётся только тогда, когда ОБА объёма прочитаны;
непрочитанная пара даёт названную причину в `unverified_aspects` (`volume_delta_not_computable - …`),
а не провал. В правке отверстия тем же правилом закрыта и проверка `volume_expected`.

## <a id="pattern-read-back-mark"></a>Провал сверки параметра при создании массива - помечается (08.10.2026)

**Что было.** `FinishPattern` добавлял проверку `read_back_save_initial_orientation`, но при её
провале не ставил пометку, тогда как правка массива в том же случае ставит
`parameter_not_read_back` в `unverified_aspects` первой строкой.

**Что решено.** Правило вынесено в `PatternReadBackMarks` (`Domain/Geometry`): при провале любой
проверки `read_back_*` пометка идёт первой. Создание и правка берут одну и ту же строку константой,
поэтому разойтись не могут. Правило — чистая функция проверок, поэтому проверяется подменённым
чтением (`PatternReadBackMarkTests`), а не живым КОМПАС. Отказ не вводится: у правки его тоже нет.
