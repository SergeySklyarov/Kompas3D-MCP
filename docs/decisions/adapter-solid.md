# Solid-операции и правка признаков — решения и измерения

Модуль: `src/KompasMcp.Api5Adapter/Api5Session.SolidOps.cs` и соседние файлы того же семейства
(`Api5Session.SolidRead.cs`, `Api5Session.FeatureEdit.B5.cs`, `Api5Session.FeatureRead.B5.cs`). Здесь —
история правок, вынесенная из кода. Действующие правила остались в коде под метками `INVARIANT:` /
`DOC:` / `MEASURED:` / `LIMIT:` / `TEST:`.

## <a id="identity"></a>Тождество признака — по COM-объекту, а не по имени (19.09.2026)

**Что было.** «Новый» признак искался как имя, которого не было в снимке дерева. Правило опиралось на
уникальность ОТОБРАЖАЕМОГО имени.

**Что измерено.** Клиентская приёмка 19.09.2026: KOMPAS присваивает двум последовательным признакам
изменения положения ОДНО И ТО ЖЕ имя «Изменение положения : Тело 1». Второй признак отбрасывался
фильтром, и `solid.reposition` отвечал `GEOMETRY_FAILED` при ВЕРНО построенной геометрии. Проба I
(`--identity`, прогон `c90961c6a3ba478697da5bc243040719`, отчёт `docs/acceptance/api7/feature-identity.json`):
адрес элемента устойчив между обходами коллекции 110; `ksEntityCollection.FindIt(entity)` отдаёт индекс с
нуля и `−1` для отсутствующего; коллекция, взятая ДО операции, мутацию НЕ отслеживает (после двух
операций — исходное число элементов и `FindIt = −1` для обоих новых признаков).

**Что решено.** `FeatureTreeSnapshot` — снимок «что было до», а новый элемент — тот, которого снимок не
знает (разность множеств по идентичности COM-объекта). Имя и порядок адресом не являются.

## <a id="feature-address"></a>Адрес созданного признака — элемент дерева API5 (19.09.2026)

**Что было.** Ссылкой на результат служил объект API7 (`IBoolean`, `ISplitSolid`, `ICut`,
`IBodyReposition`).

**Что измерено.** Проба T (шаги TL.2, TL.6, TL.7, TL.9; прогон `1c111eff3cd94007b436c5a3862e48bc`):
признак, созданный фабрикой API7, в дереве API5 ЕСТЬ и отвечает на подавление (`ksFeature.excluded`,
объём 37 000 → 49 000 и обратно) и удаление (`DeleteObject`, тела 2 → 3). Но сам объект API7 признаком
API5 не является, и `RequireFeatureEntity` его отвергает.

**Что решено.** Адрес — сущность дерева API5, опознанная разностью множеств по идентичности COM-объекта
относительно снимка до операции плюс фильтр по ТИПУ признака, РОВНО ОДНА. Иначе `discover`,
`suppress_restore` и `delete_dependencies` были бы невыполнимы для всех одиннадцати строк.

## <a id="feature-type-filter"></a>Фильтр по типу, а не по счёту (18–19.09.2026)

**Что было.** Правило требовало «ровно один новый элемент».

**Что измерено.** На режиме `save_tools` это дало отказ при УСПЕШНОЙ операции: `keep_tools=true`
создаёт ДВА признака — саму операцию и вспомогательную «Копию тела» (измерено: `type=69 «Булева
операция:1»` и `type=79 «Копия тела : Тело 1»`). Оба новых, оба отвечают `ksFeature`.

**Что решено.** Различает тип операции, а не счёт. Числа взяты из измерения
(`scratch/b3-measure-feature-types.py`, `kompas_list_features`): 69 — булева, 633 — разделение, 50 —
отсечение, 79 — изменение положения.

## <a id="plane-form"></a>Форма плоскости: три исхода разделены (18.09.2026)

**Что было.** Нулевая нормаль, невыразимая постановка и отказ ядра были свалены в один
`GEOMETRY_FAILED`.

**Что измерено.** Строка B3.17 приёмки: до разделения нулевая нормаль и плоскость без нормали отвечали
`GEOMETRY_FAILED` — «ядро не смогло» вместо «запрос невыразим».

**Что решено.** `GuardCutPlaneForm`/`ResolveCutPlane` различают: невыразимая постановка —
`INVALID_ARGUMENT` (чинится клиентом); выразимая, но неподдержанный маршрут («базовая плоскость +
смещение») — `CAPABILITY_UNAVAILABLE`, как обещано схемой; отказ ядра на корректных данных —
`GEOMETRY_FAILED`, и только он возвращает `null` с причиной.

## <a id="cut-scope"></a>Область применения отсечения — «Все объекты» (19.09.2026)

**Что было.** Отсечение не назначало область применения.

**Что измерено.** Справка v24 (`rezultat_oper_v_zavisimosti_ot_s_o.html`) называет умолчание прямо:
«По умолчанию область применения операции Сечение — Все объекты», и объект, ЦЕЛИКОМ лежащий со стороны
отсечения, в неё входит. Проба `--cut-area` (прогон `9e7599ce6e2448bb9ef983326eb16439`, 10 PASS · 0 FAIL,
`docs/acceptance/api7/cut-area.json`).

**Что решено.** Создание и правка назначают `ChooseType = ksChBodies` + `ChoosePartsType =
ksChManualEditing` + `ChooseBodies = object[] { transferred }`; незаадресованный признак отвергается
кодом `cut_not_addressed_to_target_body` до мутации.

## <a id="split-parts-selection"></a>Части разделения — по геометрии (18.09.2026)

**Что было.** «Часть» — тело, не совпавшее с целью по объёму; «нетронутое» — остальные БЕЗ доказательства.

**Что измерено.** Шаг SP.2: разделение сохраняет ВСЕ части по построению (брусок 24 000 → 6 000 и
18 000, сумма 24 000). Одного объёма мало: у разных тел объём совпадает (в эталоне B3 объём цели и
инструмента равны — 24 000).

**Что решено.** Часть — тело, не совпавшее ни с одним снимком «до» по ГАБАРИТУ И ОБЪЁМУ; цель ИСКЛЮЧЕНА
из сопоставления (операция её потребила). Идентификация — по геометрии, не по месту в коллекции.

## <a id="cut-remainder-identification"></a>Остаток отсечения — по составу тел (19.09.2026)

**Что было.** Остаток искался как «тело с объёмом, отличным от объёма цели», а «затронуто» считалось
только по телам ПОСЛЕ операции.

**Что измерено.** Клиентская приёмка 19.09.2026 (дефект `CUT-PLANE-APPLIED-TO-UNNAMED-BODIES`, наряд
§3.1): исчезнувшее постороннее тело в список не попадало, и правка, снёсшая чужой брусок, проходила как
`geometry_checked` — ложное подтверждение.

**Что решено.** Остаток и адресность — из сопоставления состава тел (`CompareBodySnapshots`,
`Touched`/`Changed`/`NewBodies`), а не из подбора под ожидание. «Затронуто» шире, чем «изменился объём»:
переехавшее тело тоже затронуто.

## <a id="cut-untouched"></a>Прежний признак «тело не тронуто» — снят (19.09.2026)

**Что было.** `IsUntouched` сравнивал объём кандидата с объёмом ЦЕЛИ до операции.

**Что измерено.** Постороннее тело этим не опознаётся вовсе (у него другой объём), поэтому все
остальные тела объявлялись нетронутыми БЕЗ доказательства, а исчезнувшее тело в список кандидатов не
попадало.

**Что решено.** Заменено сопоставлением состава тел в `SolidCutByPlane`. Метод оставлен как запись о
прежнем маршруте; ни один вызов на него не ссылается — появление ссылки было бы возвратом дефекта
`CUT-PLANE-APPLIED-TO-UNNAMED-BODIES`.

## <a id="edit-reposition"></a>Правка изменения положения (18–19.09.2026)

**Что было.** Правка возвращала уровень `CallReturned` всегда, а повторный `kompas_reposition` создавал
второй признак и накапливал смещение.

**Что измерено.** Шаг RP.6 (проба `--reposition`): повторная запись того же вектора оставляет габарит
`(17,−11,13)…(37,−1,18)`, а возврат вектора в ноль возвращает тело домой — параметр применяется к
ИСХОДНЫМ входам. Шаг RP.2: три маршрута из четырёх вернули `true` и тело не двинули. Строка B3.27
приёмки (наряд §6.6, дефект П6): у жёсткого преобразования объём инвариантен, и положение подтверждает
ГАБАРИТ, а первая редакция требовала `ExpectedVolumeMm3` — объявленный и совпавший габарит отчитывался
как необъявленный.

**Что решено.** Перенос читается обратно и сверяется ПО МАТРИЦЕ (см. `RequirePlacementRoundTrip`);
уровень «геометрия проверена» требует объявленного ожидания (объём и/или габарит), а не именно объёма.

Дополнение (вынесено из кода). The translation is READ BACK (<c>Position.X/Y/Z</c>) and compared with
the matrix that was requested: if the offset accumulated, the read would give a doubled value. Volume
under a rigid transformation must be preserved, and that is checked too — by it "moved" and "stayed"
are indistinguishable, but "the transformation stayed rigid" is visible.

## <a id="edit-split"></a>Правка разделения — перенос точек собственной опоры (18.09.2026)

**Что было.** Обработчик подставлял в признак ДРУГУЮ плоскость.

**Что измерено.** Шаг SP.9 (проба `--split`, прогон `c9cd7660468c44aa97b410e253ee2cb1`): перенос трёх
точек построения СОБСТВЕННОЙ опоры переводит части `6 000 / 18 000` при `x = 10` в `9 000 / 15 000` при
`x = 15`, число признаков `1 → 1`, сумма `24 000`. Отрицательный контроль E-B: подстановка другой
плоскости в `CutObjects` результата не меняет.

**Что решено.** Правка переносит три точки построения; опора читается обратно шагом SP.10 и публикуется
`kompas_get_feature`; подтверждение — состав частей (`expected_part_volumes_mm3`), а не сумма объёмов.

Дополнение (вынесено из кода). The definition is enumerated in full, not "the field being changed". A
split has two kinds of support (an existing plane or a point with a normal). Reading the support back
IS possible — this is MEASURED 18.09.2026 by step SP.10 (<c>CutObjects</c> returns three construction
points and a normal, and different supports read differently), and this is exactly what
<c>kompas_get_feature</c> publishes in the <c>solid.plane</c> block. The requirement of request
completeness is kept not because reading is impossible, but because an answer to a partial request
would not tell "exactly what was asked changed" from "what was not mentioned changed too". The sum of
volumes does not change when a split is edited, so <c>expected_volume_mm3</c> is not accepted here at
all (it is rejected with a pointer to <c>expected_part_volumes_mm3</c>): a row checking the sum would
pass on complete inaction.

## <a id="edit-cut"></a>Правка отсечения — опора и сторона вместе (18.09.2026)

**Что было.** Обработчик подставлял в признак ДРУГУЮ плоскость.

**Что измерено.** Шаг SP.9, опыт E-C: перенос точек опоры на +5 по X меняет остаток с 6000 на 9000.
Опыт E-D: смена ТОЛЬКО `Direction` на том же признаке меняет остаток с 9000 на 15000. Отрицательный
контроль E-B: подстановка другой плоскости не работает.

**Что решено.** Требуются И опора, И сторона; область применения восстанавливается на правке так же, как
назначается на создании (наряд §3.2).

## <a id="edit-boolean"></a>Правка вида булевой операции (18–19.09.2026)

**Что было.** Результат искался по совпадению габарита с ОЖИДАНИЕМ, а объёмы публиковались по всем телам
документа; второй заход — по `changes.Changed` (тела с изменившимся ОБЪЁМОМ).

**Что измерено.** Шаг BO.11 (проба `--boolean`, прогон `a2f5cf0a2ad342c59c36807101a65d51`): перезапись
`IBoolean.BooleanType` на СУЩЕСТВУЮЩЕМ признаке + `Update()` + пересборка меняет геометрию (E-A
`36 000 → 12 000` в габарите `x ≤ 20`; E-D `12 000 → 36 000`). Опыт E-E: применяет именно ПАРА
«запись → `Update()`». Строка B3.25 (19.09.2026, поставка `publish-b3-20260919-targeting`): объём
разности и объём пересечения РАВНЫ (12 000 мм³), корректно применённая правка `intersect` попадала в
`resultBodies.Count == 0` и отвергалась как `NO_GEOMETRY_CHANGE` (дефект
`CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT`, наряд §4.1).

**Что решено.** Результат — тело, изменившееся или появившееся в ЭТОЙ операции (`changes.Touched` =
изменился объём ИЛИ габарит); ожидание, наблюдение и вердикт относятся к ОДНОМУ телу; габарит различает
разность и пересечение.

Дополнение (вынесено из кода). Experiment E-E confirmed that it is the PAIR "write → <c>Update()</c>"
that applies: a write without <c>Update()</c> but with a rebuild does not change the geometry. The same
lesson as in row <c>B3L.04</c> (for a translation the volume before and after is 1 000, and a row
checking only volumes would pass on inaction). The former edition sought the body by matching the
EXPECTATION: <c>rowsAfter.FirstOrDefault(r => BoxMatches(r.Bbox, command.ExpectedBboxMm))</c> and
published the volumes of ALL document bodies as observed — it compared a bounding box with numbers of
another kind and another subject. A body found by SUCH a search may be foreign — a bounding box
matching the expectation does not make the body the result of THIS operation.

## <a id="field-classification"></a>Классификация полей B3 — одна таблица (18–20.09.2026)

**Что было.** Владельцы полей жили в словаре, а значения — в отдельном перечислителе; запрещённые поля
перечислялись руками.

**Что измерено.** Дефект П5: `keep_side` не попал в перечень запрещённых, поэтому вызов
`plane + keep_side` на признаке РАЗДЕЛЕНИЯ принимался, а `keep_side` молча игнорировался. Поле `pattern`
(очередь B4) не попало ни в одну таблицу ролей — падала проверка полноты. Поля отверстия не были
объявлены НИГДЕ (строка F08.16.edit, `docs/STATUS.md`). `couplings` (очередь B5) не попал в перечни чужих
полей: зонд `scratch/_couplings_scope_probe.py` измерил, что вызов «правка признака + couplings»
возвращал успех, геометрия менялась, а цепочки не применялись.

**Что решено.** ОДНА таблица `SolidFields` отвечает на оба вопроса; полнота держится тестом
`SolidFeatureClassificationTests`, сверяющим таблицу с контрактом. Поля чужих семейств отвергаются ДО
COM; `ownFields` (заведено 20.09.2026, наряд SM07 §3.2) отдаёт семейству поле, которое таблица ролей
приписывает другому ведомству (`depth_mm` глухого отверстия).

Дополнение (вынесено из кода). <b>Why one table and not two.</b> The first edition kept owners in a
dictionary and values in a separate enumerator, and these two structures could diverge. They would
have diverged silently: a field added to the enumerator without an entry in the dictionary was NEVER
rejected, because <c>TryGetValue</c> returned <c>false</c> and the "field is foreign" condition
short-circuited to <c>false</c>. This is exactly the same class of defect as P5 (a declared but
swallowed field), only from the other side. <b>Why an enumeration, not a "forbidden list".</b> The
enumeration of what occurs in the command changes together with the contract, and the default here
must be "do not reject": a field not assigned to any family is rejected not here, but by its family or
by the check of inapplicable parameters below. Therefore the completeness of the table is checked
separately — by the test <c>SolidFeatureClassificationTests</c>, which verifies it against the
contract itself: a new command field will not pass until it is assigned to a family, to the
inapplicable, to addressing or to geometry expectations.

## <a id="reposition-read"></a>Чтение изменения положения — из параметров (18.09.2026)

**Что было.** Вид преобразования выводился из матричного вида размещения.

**Что измерено.** Проба `--reposition-params` (прогон `a336120926fc4652a8bf737562568271`, шаг RP.25):
углы Эйлера и перенос читаются из ПЕРЕОТКРЫТОГО документа; отрицательный контроль D0 без записанного
переноса дал `ParameterType = 1 (ksPParamCoord)`. Матричный вид (`GetVector`, `WriteToFile`) на
переоткрытом документе единичен при сохранённой геометрии (RP.16, RP.18).

**Что решено.** Источник — параметры размещения (`OrientationType = ksEulerCorners` +
`LocalCSParameters`; `ParameterType = ksPDisplace` + `Parameters`), а не матрица; вид определяется из
собранной матрицы однозначно.

Дополнение (вынесено из кода). The other four transformation fields are read by the documented
parametric route (<c>OrientationType</c> + <c>LocalCSParameters</c>, <c>ParameterType</c> +
<c>Parameters</c>) measured in step RP.25 of probe <c>--reposition-params</c>; where a feature has no
parameters of that route (it was written as a matrix), ALL FIVE are named — a refusal, not zeros.
INVARIANT: reading does not fail because the bridge is unavailable — feature state (name, IsValid,
updateStamp) reads without API7, so an unavailable route goes to <c>unreadable_parameters</c> rather
than failing all of <c>kompas_get_feature</c>, which would rob the caller of the part that does read.

The document stores orientation and translation as PARAMETERS that survive reopen:
<c>OrientationType = ksEulerCorners</c> + <c>LocalCSParameters → ILocalCSEulerParam</c> (the angle
triple) and <c>ParameterType = ksPDisplace</c> + <c>Parameters → IPoint3DParamDisplace</c> (the
translation). The angle triple and displacement were read from a REOPENED document before the build
and before any write, on two discriminating setups (<c>displacement_after_D1 = (7,−11,13)</c>,
<c>displacement_after_D2 = (1,2,3)</c>, <c>angles_kept_D1 = angles_kept_D2 = true</c>), while the
negative control D0, whose displacement was not written, gave <c>ParameterType = 1 (ksPParamCoord)</c>
and <c>(?,?,?)</c>. The matrix view (<c>GetVector</c>, <c>WriteToFile</c>) is NOT used at all —
neither as source nor as confirmation: it returns what was written only in the writing session and is
singular on a reopened document with the geometry preserved (RP.16, RP.18, RP.20, RP.23). Published:
kind, translation vector, axis direction and angle; the axis point is NOT published because it exists
on no interface of the chain and is not a placement property (see <c>RepositionAxisPointUnreadable</c>)
— for a translation the axis point is NOT APPLICABLE, for a rotation the vector is NOT APPLICABLE, and
both are named so the caller sees the read boundary from the response itself. LIMIT: a feature written
by a matrix is not read — a refusal, not zeros — recognised by the read
<c>OrientationType = 0 (ksAxisOrientation)</c>; see <c>RepositionLegacyReason</c>.

## <a id="reposition-axis-point"></a>Точка оси поворота не публикуется (18.09.2026)

**Что измерено.** LIMIT: ни один интерфейс цепочки (`IBodyReposition`,
`ILocalCoordinateSystem`/`IPoint3D`, `IPoint3DParamDisplace`, `ILocalCSAxesDirectionParam`,
`ILocalCSEulerParam`, `ILocalCSObject`) не имеет документированного члена для точки оси;
`RepositionCentre` и `ILocalCSObject.CoordinateSystem` координат не отдают. Точка оси не является
свойством размещения: `X/Y/Z = c` меняет габарит повёрнутого тела, а `X/Y/Z = c − R·c` его сохраняет.

**Что решено.** Из ориентации и переноса восстанавливается ПРЕДСТАВИТЕЛЬ прямой
(`EulerOrientation.AxisPointFromPlacement`), а не исходный вход. Поле остаётся строкой требования;
причина названа в `unreadable_parameters`.

Дополнение (вынесено из кода). The member lists were read from the product type library. DOC declares
<c>CoordinateSystem</c> as <c>IModelObject</c> (<c>ilocalcsobject_coordinatesystem.html</c>).
INVARIANT: the read requirement is not lifted by this — the field stays a row requirement; only the
reason the product does not publish it is named here.

## <a id="reposition-legacy"></a>Признак, записанный чужим маршрутом, — отказ (18.09.2026)

**Что было.** `reposition_kind` выводился из единичной ориентации и возвращал `"translate"` для
ЗАПИСАННОГО ПОВОРОТА.

**Что измерено.** Шаг RP.16: на переоткрытом документе матричный вид единичен при сохранённой геометрии
(`GetVector(OX) = (1,0,0)` при габарите `(−5,−5,0)…(5,15,5)`); RP.18: `WriteToFile` даёт единичную
матрицу; RP.23: отличить «записано» от «не восстановлено» нечем (`Valid = True` в обоих состояниях,
`Update()` и сборка чтение не восстанавливают). Признак опознаётся по читаемому
`OrientationType = 0 (ksAxisOrientation)`.

**Что решено.** Существующие признаки молча не конвертируются: старый признак остаётся как есть, а
продукт честно сообщает, что параметров преобразования у него нет. «Нет параметров этого маршрута» и
«чтение не удалось» — РАЗНЫЕ строки причины.

Дополнение (вынесено из кода). The wording is generic: the field name is substituted by the caller.
The former revision derived <c>reposition_kind</c> from that singular orientation and returned
<c>"translate"</c> for a WRITTEN ROTATION — a false result, now removed.

## <a id="reposition-axes-removed"></a>Оси размещения убраны из чтения (18.09.2026)

**Что было.** В ответе публиковались оси размещения, вычисленные из матричного вида.

**Что измерено.** Матричный вид на переоткрытом документе единичен при сохранённой геометрии (RP.16,
RP.18), то есть оси из него — следствие, а не вход.

**Что решено.** Оси не публикуются; направление оси поворота читается из параметров размещения, а не из
матрицы. Подробности чтения — в `docs/04_KOMPAS_API_NOTES.md`.

## <a id="b5-read"></a>Чтение семейств B5 — по интерфейсу определения (20.09.2026)

**Что было.** Семейство распознавалось по номеру типа.

**Что измерено.** Проба `--b5`, шаг B5.12: `NewEntity(45)` (`o3d_baseEvolution`) показывается в дереве
как 46 (`o3d_bossEvolution`), `ILofts.Add(31)` — как 31, `NewEntity(43)` — как 43. Такое расхождение уже
стоило дефекта на отверстии (52 → 583) и на вращении (27 → 584). `GetType().Name` COM-объекта всегда
`__ComObject`.

**Что решено.** Семейство распознаётся по интерфейсу определения (объект отвечает
`ksBossEvolutionDefinition` и т. п.), а не по номеру типа.

## <a id="b5-edit"></a>Правка семейств B5 — изменение существующего признака (20.09.2026)

**Что было.** Правка шла через удаление и пересоздание.

**Что измерено.** Проба `--b5`, отчёт `docs/acceptance/api7/b5-sweep-loft-shell.json`: B5.13 — режим
сдвига `24674.011002723353 → 15707.963267948984 → 24674.011002723353`, толщина и направление оболочки
`21632 → 40256 → 53056 → 21632`, переадресация сечений `28000 → 48000 → 28000`; B5.14 — набор снятых
граней `21632 → 7040 → 21632` при `11 → 10 → 11` гранях; B5.15 — входы сдвига (`SetSketch`, путь)
принимаются (`Update = true`) и НЕ применяются.

**Что решено.** Правка пишет в ТОТ ЖЕ признак и подтверждается ГЕОМЕТРИЕЙ, а не ответом `Update()`;
`closed` у элемента по сечениям не объявлен правимым; входы сдвига отвергаются по имени.

Дополнение (вынесено из кода). INVARIANT: fields of other families are rejected, not ignored — the
cost of error is asymmetric: a superfluous refusal is seen at once, while an accepted-and-ignored
number survives to acceptance looking like a completed operation.

## <a id="b5-sweep-edit"></a>Правка режима сдвига (20.09.2026)

**Что измерено.** Шаг B5.13: смена `sketchShiftType` `orthogonal → parallel → orthogonal` дала объёмы
`24674.011002723353 → 15707.963267948984 → 24674.011002723353`; различает только дуга. Шаг B5.15:
`SetSketch` и переадресация `PathPartArray()` принимаются и НЕ применяются, а смена РЕЖИМА на том же
признаке применяется.

**Что решено.** Порядок «запись → `Update()` → пересборка» — часть контракта; объём читается с модели и
сверяется с аналитическим ожиданием; `sketch_ref` отвергается по имени.

Дополнение (вынесено из кода). Without <c>Update()</c> the setter returns success while the model
stays as before. The feature's inputs are not changed by this call, and that is a measured refusal,
not caution.

## <a id="b5-loft-edit"></a>Правка набора сечений элемента по сечениям (20.09.2026)

**Что измерено.** Шаг B5.13: переадресация `ILoft.Sketchs` на построенном признаке меняет геометрию —
`40×40 + 20×20` дают `28000`, `40×40 + 40×40` дают призму `48000`, возврат к прежнему набору — `28000`.
LIMIT: запись `ILoft.Closed` на построенном признаке возвращает `Update() = True`, читается `False`,
объём не меняется.

**Что решено.** Правится ВХОД (набор сечений); `closed` задаётся только при создании; адрес — по порядку
среди признаков того же семейства, при равном числе элементов дерева и коллекции.

## <a id="b5-loft-both-stores"></a>Набор сечений пишется в ДВА хранилища (20.09.2026)

**Что измерено.** Шаг B5.21: запись только в `ILoft.Sketchs` сокращает признак с трёх сечений до двух
(«3 до, 2 после присваивания, 2 после `Update()`»). Но ОДНО чтение `ksBaseLoftDefinition.Sketchs()` делает
определение владельцем ЧИСЛА сечений, и та же запись отменяется первым обновлением («2 после
присваивания, 3 после `Update()`»). Запись в ОБА хранилища (сначала определение, затем
`ILoft.Sketchs`) снова применяется.

**Что решено.** Вход пишется в оба места: сначала в определение, затем в `ILoft.Sketchs`. Чтение
определения признак не «портит» — оно делает определение владельцем входа, а без него не было бы
`kompas_get_feature`.

Дополнение (вынесено из кода). While nobody has read the definition, writing to <c>ILoft.Sketchs</c>
applies — "3 before, 2 after assignment, 2 after <c>Update()</c>, volume 48000" — and repeats four
times in a row. Writing the set INTO BOTH STORES (definition first: "3 before <c>Clear()</c>,
<c>Clear</c>=True, 2 added, now 2"; then <c>ILoft.Sketchs</c>; then <c>Update()</c>) lifts the
cancellation — read 2, volume <c>48000</c>. <c>ksEntity.Update()</c> is the same call that applies the
definition write for the shell. Both stores are written, definition first, then <c>ILoft.Sketchs</c>
(see <c>WriteLoftSectionsToDefinition</c>). Reading the definition does not "spoil" the feature — it
makes the definition the owner of the input; dropping the read would drop <c>kompas_get_feature</c>.
LIMIT: the write does not check that a section stands ABOVE the feature in the tree; that must be
checked on the instrument's side, not the server's, because "above" is defined by tree order, not by a
request field.

## <a id="b5-readdress"></a>Переадресация сечения из дерева (20.09.2026)

**Что было.** Указатель реестра живёт против той ревизии, в которой зарегистрирован, а правка приходит
в следующей.

**Что измерено.** Дерево даёт адрес НА МОМЕНТ ПРАВКИ; доказательство — то же перечисление, что у пробы
(`ksPart.EntityCollection(0).refresh()`).

**Что решено.** Имя принимается только если оно ОДНОЗНАЧНО: коллекции дерева перебираются по очереди,
берётся первая с совпадением; при нескольких совпадениях адрес НЕ доказан и вызывающий обязан отказать
по имени. Совпадение описывается в `note` и на успехе.

## <a id="b5-shell-edit"></a>Правка оболочки — толщина и направление вместе (20.09.2026)

**Что измерено.** Шаг B5.13, дословная запись шага: `t = 2 inward → 4 inward → 4 outward → 2 inward` на одном признаке дали
`21632 → 40256 → 53056 → 21632`. Шаг B5.14: набор снятых граней правится на том же признаке — вторая
грань даёт `7040` при `10` гранях, возврат к прежнему набору `21632` при `11`; повторная запись того же
набора объём не двигает (отрицательный контроль). LIMIT: пустой список граней отвергается — операция
принимается, но тело не меняется.

**Что решено.** Толщина и направление пишутся ВМЕСТЕ (режим оболочки — это пара), недостающая половина
берётся ИЗ МОДЕЛИ; пустой список граней отвергается.

## <a id="b5-section-refs"></a>Сечения элемента по сечениям как ссылки (20.09.2026)

**Что измерено.** DOC: `ksbaseloftdefinition_sketches.html` и `ksbossloftdefinition_sketches.html`
описывают член «Sketches» («Получить указатель на интерфейс массива эскизов элемента по сечениям»), а в
interop он объявлен как `Object Sketchs()` — справка и interop расходятся на одну букву.

**Что решено.** Ссылки выводятся ЗАНОВО из определения, никогда не запоминаются: ссылка времени создания
умирает на первой мутации документа, а `kompas_rebuild` отзывает ВСЕ ссылки документа. `null` —
«не прочитано», пустой список — «сечений нет»; состояния не сливаются.

## <a id="b5-removed-faces"></a>Снятые оболочкой грани как ссылки (20.09.2026)

**Что измерено.** Грани, снятые оболочкой, ОТСУТСТВУЮТ в топологии тела, поэтому `kompas_read_topology`
их не даёт вовсе, а набор снятых граней — это вход правки. Голое `is ksFaceDefinition` даёт ПУСТОЙ
список при `removed_face_count = 1`.

**Что решено.** Ссылки выводятся из `ksShellDefinition.FaceArray()`; элемент приходит как `ksEntity` и
разворачивается через `GetDefinition()` (`AsInterface`), а не голым `is`. `null` — «не прочитано»,
пустой список — «снятых граней нет».

Дополнение (вынесено из кода). Without deriving refs from the definition, re-editing the removed-face
set is inexpressible after a mutation or a reopen.

## <a id="placement-round-trip"></a>Проверка «запись → чтение» размещения — по матрице (18–19.09.2026)

INVARIANT: a successful <c>IBodyReposition.Update()</c> means "accepted", not "applied" — MEASURED
(step RP.2) that three routes out of four returned <c>true</c> and did not move the body, so creation
is additionally confirmed by reading the written parameters back. INVARIANT: comparison is by matrix,
not by the number triple — the Euler-angle parameterisation is ambiguous (at nutation 0 or 180° the
sum of precession and rotation is defined up to redistribution), so requiring equal numbers would
reject a CORRECT write. A matrix is assembled from the read triple and compared with the requested
one: <c>EulerOrientation</c> keeps the conjugation order in one place, and swapping that order
diverges here at once — MEASURED divergence with a foreign order is 1, with the correct one 0 or
2.2·10⁻¹⁶. LIMIT: the tolerance 10⁻⁶ is six orders below the divergence a wrong order produces (1)
and ten orders above the measured residual of the correct decomposition (2.2·10⁻¹⁶); it is
deliberately wider than machine precision so that rounding is not turned into a refusal.

## <a id="plane-form-guard"></a>Проверка формы плоскости — приоритет объявлен (19.09.2026)

MEASURED by the B3 client acceptance (19.09.2026, three FAIL rows): the schema's declared
<c>CAPABILITY_UNAVAILABLE</c> on <c>plane.base</c> was UNREACHABLE — the DTO field shape differed
from the published one, and the call failed while parsing the payload with <c>JsonException</c> and
code <c>VERIFICATION_FAILED</c>. The shape is brought in line with the published one
(<c>CutPlaneDto</c>), and this check makes the declared outcome executable and shared by
<c>kompas_split</c>, <c>kompas_cut_by_plane</c> and the applicable <c>kompas_update_feature</c>.

Priority list (verbatim from the code):

1. <c>base</c> named TOGETHER with another mode (<c>plane_ref</c> or point with normal) —
   <c>INVALID_ARGUMENT</c>: the request is contradictory, and answering it with the declared
   capability refusal would hide from the client that it named two modes at once;
2. <c>base</c> named alone (with or without an offset) — <c>CAPABILITY_UNAVAILABLE</c>: exactly what
   the field description promises;
3. <c>offset_mm</c> without <c>base</c> — <c>INVALID_ARGUMENT</c>: an offset without a base plane
   does not express a plane, and accepting the parameter silently would declare it accepted;
4. <c>plane_ref</c> together with a point or normal — <c>INVALID_ARGUMENT</c> (checked by the calling
   route, because edit refuses a reference for its own reason).

## <a id="same-type-address"></a>Адрес B3-признака — по позиции среди однотипных (18–19.09.2026)

**Why position, not name.** The name in API5 and the name in API7 for one and the same object
diverge — this is MEASURED on a fillet (F.8: a name set in API5 reads differently in API7), and for
the same reason <c>Api7Fillet.FindIndexesByIdenticalRadius</c> and <c>Api7Rotated.FindIndexFor</c>
match by VALUE, not by name. For B3 features there is no identifier value known before the edit at
all (the boolean operands are consumed after the union, the split plane is an auxiliary object), so
the address is taken by position. This is the same technique used to edit a rotation when the tree
entity does not answer <c>QI(IRotated)</c> (<c>RotatedOrdinal</c>), and it is checked by geometry in
acceptance: editing the wrong feature will not give the expected volume and bounding box. The feature
count is returned TOGETHER with the position, not by a separate walk: two walks of one collection
could diverge, and the decision on address suitability is made from both numbers at once
(<c>RequireSameTypeIndex</c>).

## <a id="foreign-solid-fields"></a>Чужие поля B3-семейств — перечисление, не «список запрещённых» (18–20.09.2026)

**Why an enumeration, not a "forbidden list".** The first edition listed the forbidden fields by
hand, and it already suffered from this: <c>keep_side</c> did not make it into the list, so a call
<c>plane + keep_side</c> on a SPLIT feature was accepted and <c>keep_side</c> was silently ignored —
exactly the class of defect the rule "a parameter not declared in the schema does not reach COM" was
written against (§9.1 P4), only from the other side: declared but swallowed. Here every family field
must be ASSIGNED to a family (<c>SolidFields</c>), and a field passed to a family that does not own it
is rejected. **What this check does not promise.** It rejects a passed field but does NOT prove that
the list of family fields is complete: completeness is held by the test
<c>SolidFeatureClassificationTests</c>, which verifies the table against the contract itself. Earlier
there stood here a claim that the table and the enumerator "are verified on refusal"; that was wrong —
they were verified nowhere, and the divergence between them was silent.

`ownFields` (introduced 20.09.2026 by order SM07 §3.2 for one measured case): <c>depth_mm</c> is an
EXTRUDE field in the table and at the same time an OWN field of a blind hole (<c>blind_flat</c>).
Without this list, editing a blind hole was rejected INVALID_ARGUMENT before COM — and this is
MEASURED on the first delivery with the hole branch (rows F08.15/16/19/20.edit, run 20.09.2026): the
depth is read as a foreign field, although the hole branch reads it. The length of the list is held
not by "common sense" but by acceptance: with it a blind hole is edited, while counterbore and
countersink with <c>depth_mm</c> are still rejected — but now BY MODE (<c>ValidateHoleEdit</c>), where
that is measured (HO.13/HO.16).

## <a id="support-plane-edit"></a>Опора при правке SM-16 — три точки, без объекта плоскости (18.09.2026)

**Why without creation.** MEASURED 18.09.2026 (probe <c>--split</c>, step SP.9, step E-B — negative
control): substituting ANOTHER, just-created plane into an existing feature does NOT change the
result — <c>Update()</c> returns <c>true</c> and the parts stay as they were. A different route works
(E-A for split, E-C for cut): transferring the THREE CONSTRUCTION POINTS of the feature's OWN
support. Therefore three points are computed here and no plane object is created at all — otherwise
an unused object would remain in the document on every edit. <c>plane_ref</c> is refused on edit: the
route is measured for the feature's OWN support, and there is no way to prove that the presented
reference is that support — comparing plane references was not measured, and substituting a foreign
plane gives no result (E-B). The point and normal checks are not duplicated but taken from the same
rules as at creation: finiteness here, a non-zero normal — from <c>PlaneBasis.FromNormal</c>, three
construction points — from <c>PlaneBasis.ThreePoints</c>.

## <a id="b5-couplings-edit"></a>Непустые цепочки на существующем признаке — отказ (20.09.2026)

MEASURED 20.09.2026 (B5 acceptance, rows B5S.01/B5S.02): on a BUILT feature <c>ILoft.AddCoupling()</c>
returns <c>ICoupling</c>, <c>PositionOffset</c> accepts offsets, and <c>CouplingsCount</c> reads 1
RIGHT AFTER the write — but the build does not carry the chain: after <c>Update()</c> the model has 0
chains, and the volume matches a body WITHOUT coupling. Reproduced in TWO write orders (one build; and
"build the section set, then re-read <c>ILoft</c> via <c>ILofts::Loft</c> and set the coupling"), so it
is not our write order. At CREATION the same sequence keeps the chain (probe B5.18: CouplingsCount =
1, volume 20000 vs 28000). The documented members (<c>iloft_addcoupling.html</c>,
<c>iloft_clearcouplings.html</c>, <c>iloft_deletecoupling.html</c>) declare no such limit — so this is
a MEASUREMENT of the implementation's behaviour, named here and not silenced. Accepting such a request
would promise a coupling the model never gets and return "done" on a body without it; the refusal
therefore stands BEFORE the write, and the feature is unchanged.


## <a id="solidread-compaction"></a>Чтение B3 — история, вынесенная из кода

INVARIANT: every route here is measured by a probe, not derived from member names (run references sit
at each read; summary in <c>docs/04_KOMPAS_API_NOTES.md</c> §4.10.9). Of the five reposition
transformation fields ONE is not published — <c>reposition_axis_point_mm</c>; the other four read by the
parametric route (see history). Feature state (name, IsValid, updateStamp) reads without API7, so an
unavailable route does not fail all of <c>kompas_get_feature</c>.

MEASURED: the axis point is NOT a placement property — <c>X/Y/Z = c</c> CHANGES the bounding box of a
rotated body ((−5,0,0)…(5,20,5) instead of (−5,−5,0)…(5,15,5)), while <c>X/Y/Z = c − R·c</c> PRESERVES
it; a rotation about any point of ONE line gives the SAME placement, so only a REPRESENTATIVE is
recovered (<see cref="EulerOrientation.AxisPointFromPlacement"/>), not "that" point. DOC:
<c>ilocalcsobject_coordinatesystem.html</c>, <c>CoordinateSystem</c> is <c>IModelObject</c>.

MEASURED (probe <c>--reposition-params</c>, run <c>a336120926fc4652a8bf737562568271</c>): the parametric
route reads <c>1 (ksEulerCorners)</c> live and after reopen, a matrix-written one reads
<c>0 (ksAxisOrientation)</c> (RP.16, RP.22). The matrix view is SINGULAR on a reopened document with
preserved geometry (RP.16), <c>WriteToFile</c> gives a singular matrix (RP.18); nothing tells "written"
from "not restored" (<c>Valid</c> reads <c>True</c> in both states, <c>Update()</c> + rebuild does not
restore the read, RP.23).

MEASURED 18.09.2026 (probe <c>--boolean</c>, steps BO.11 and BO.10): a written <c>IBoolean.BooleanType</c>
reads back as <c>ksUnion</c> / <c>ksDifference</c> / <c>ksIntersect</c> — three DIFFERENT values on three
different writes, so the read distinguishes rather than returning a constant.
<c>SaveCopyModifyObjects</c> read back in BO.5 (<c>true</c>) and BO.2–BO.4, BO.6 (<c>false</c>), and step
BO.10 read both fields from a REOPENED file — the route survives save → close → reopen.

MEASURED 18.09.2026 (probe <c>--split</c>, step SP.10, run <c>95e24af18d694d2cb50890a99983376a</c>): the
support <c>x = 10</c> reads as points <c>(10,0,0)</c>, <c>(10,1,0)</c>, <c>(10,0,1)</c> with normal
<c>(1,0,0)</c>, and <c>x = 15</c> as <c>(15,0,0)</c>, <c>(15,1,0)</c>, <c>(15,0,1)</c> — the read
distinguishes DIFFERENT supports rather than returning a constant; <c>Direction</c> reads <c>true</c> and
<c>false</c> on the same support. Step SP.9 (E-A) showed the support reads from a live feature and its
three points are what the edit moves.

MEASURED in full by probe <c>--reposition-params</c>, run <c>a336120926fc4652a8bf737562568271</c>, step
RP.25 (details in history). The matrix view (<c>GetVector</c>, <c>WriteToFile</c>) is NOT used at all
(RP.16, RP.18, RP.20, RP.23). Published: kind, vector, axis direction, angle (not the axis point).

MEASURED 18.09.2026 by the instrument <c>scratch/b3-measure-feature-types.py</c>.


## <a id="solidops-header"></a>B3 body operations — история, вынесенная из кода

INVARIANT: the basis is measurement, not member names — three isolated probes of 18.09.2026 (<c>--boolean</c>,
<c>--split</c>, <c>--reposition</c>; logs in <c>docs/acceptance/api7/</c>) fixed the routes and their
LIMITS. The kernel neither rejects a repeated reference nor checks the target is not among the tools
(MEASURED, step BO.9: a repeat is accepted silently, bodies 3→2).

**Feature identity.** Probe I, <c>--identity</c>, 19.09.2026, run
<c>c90961c6a3ba478697da5bc243040719</c>, report <c>docs/acceptance/api7/feature-identity.json</c>: the
element address is stable (two consecutive walks of collection 110 return the same COM object at the same
index); <c>ksEntityCollection.FindIt(entity)</c> returns the element index from zero and <c>−1</c> for an
object not in the collection; a collection taken BEFORE the operation does NOT track mutation (after two
operations it still reports the original element count and <c>FindIt = −1</c> for both new features).
Negative control: elements that existed before the operation gave <c>0</c> and <c>1</c>, both new features —
<c>−1</c>. Client acceptance on 19.09.2026 measured that KOMPAS gives two consecutive reposition features
the SAME name «Изменение положения : Тело 1», so the second feature was dropped by the filter and
<c>solid.reposition</c> answered <c>GEOMETRY_FAILED</c> with the geometry built CORRECTLY.

**Feature address.** Probe T (steps TL.2, TL.6, TL.7, TL.9; run
<c>1c111eff3cd94007b436c5a3862e48bc</c>) measured: a feature created by an API7 factory IS present in the
API5 tree, and it answers suppression (<c>ksFeature.excluded</c>, volume 37 000 → 49 000 and back) and
deletion (<c>DeleteObject</c>, bodies 2 → 3). A reference to the API7 object would make <c>discover</c>,
<c>suppress_restore</c> and <c>delete_dependencies</c> impossible for all eleven rows — four of the ten
actions per row closed as "no API". The first edition required "exactly one new element", and on
<c>save_tools</c> this gave a refusal with a SUCCESSFULLY performed operation: <c>keep_tools=true</c>
creates TWO features — the operation itself and the auxiliary «Копия тела» (MEASURED: <c>type=69 «Булева
операция:1»</c> and <c>type=79 «Копия тела : Тело 1»</c>). Numbers from measurement
(<c>scratch/b3-measure-feature-types.py</c>, <c>kompas_list_features</c>): 69 — boolean, 633 — split,
50 — cut, 79 — reposition. Names of two consecutive features of one kind COINCIDE (MEASURED 19.09.2026,
probe I).

**Same-type address.** The number <c>79</c> is carried also by the auxiliary «Копия тела»
(<c>scratch/b3-measure-feature-types.py</c>, 18.09.2026). The price of the refusal: editing a feature next
to which lives a feature of the same number but a different operation is not performed.

**Cut plane form.** MEASURED by the B3 client acceptance (19.09.2026, three FAIL rows): the declared refusal
was UNREACHABLE. MEASURED 18.09.2026 by acceptance row B3.17. The sign of the base plane's normal decides
which side is cut away.

**Body untouched (obsolete).** OBSOLETE 19.09.2026: every other body was declared untouched WITHOUT proof,
and a vanished body was not listed; a call would be a regression of
<c>CUT-PLANE-APPLIED-TO-UNNAMED-BODIES</c>.

**Reposition edit.** MEASURED 18.09.2026 (probe <c>--reposition</c>, step RP.6): rewriting the same vector
leaves the bounding box <c>(17,−11,13)…(37,−1,18)</c>, resetting it to zero brings the body home. Step RP.2:
three routes out of four returned <c>true</c> and did not move the body.

**Foreign fields.** `couplings` was added 20.09.2026: five of the SIX new B5 fields made it into the
foreign-field lists while the sixth did not; a field with no role = a field the adapter will accept and
swallow. SM07 §3.2.

**SM-16 support edit.** MEASURED 18.09.2026 (probe <c>--split</c>, step SP.9, negative control E-B):
comparing plane references was not measured and a foreign plane gives no result (E-B).

**Split edit.** MEASURED 18.09.2026 (probe <c>--split</c>, step SP.9, run
<c>c9cd7660468c44aa97b410e253ee2cb1</c>).

**Cut edit.** MEASURED 18.09.2026 (probe <c>--split</c>, step SP.9, run
<c>c9cd7660468c44aa97b410e253ee2cb1</c>); reading the current support and side from a live feature was not
measured.

**Cut remainder addressing.** Previously there stood here
changed = rows.Where(r => !MatchesAnySnapshot(r, bodiesBefore)): the walk went ONLY over bodies AFTER the
operation, so a vanished body never entered the list, and an edit that swept away a foreign bar looked like
"exactly one body changed" and passed as geometry_checked — the false confirmation from client acceptance
19.09.2026 (defect CUT-PLANE-APPLIED-TO-UNNAMED-BODIES, order §3.1).

**Boolean edit.** MEASURED by probe <c>--boolean</c>, step <c>BO.11</c>, run
<c>a2f5cf0a2ad342c59c36807101a65d51</c>. On the §6.1 reference difference and intersection both have volume
12 000; the bounding box distinguishes them (<c>x ≤ 20</c> vs <c>x ∈ [20,40]</c>). The former edition sought
the body by matching the EXPECTATION and published ALL document volumes as observed — a bounding box compared
with numbers of another kind (defect CHECK-FIELDS-DO-NOT-SUPPORT-THE-VERDICT, §4.1). A second foray
(MEASURED 19.09.2026, delivery <c>publish-b3-20260919-targeting</c>, row B3.25): going by changes.Changed
(changed VOLUME) missed a correct `intersect` edit — difference and intersection volumes are EQUAL
(12 000 mm³), only the bounding box distinguishes them — and it was rejected as NO_GEOMETRY_CHANGE.

## <a id="b5-read-compaction"></a>Чтение семейств B5 — пояснения, вынесенные из кода

Ранее эта ссылка вела на docs/decisions/adapter-solid.md#b5-read, docs/decisions/adapter-solid.md#b5-section-refs,
docs/decisions/adapter-solid.md#b5-removed-faces.

A family is recognised BY THE DEFINITION INTERFACE, not by the type number — the tree number differs from
the creation number (MEASURED). <c>GetType().Name</c> of a COM object is always <c>__ComObject</c>.
MEASURED: <c>GetPathLength(1)</c> on a 100 mm segment returned exactly 100, so millimetres are confirmed
by a number, not a guess. MEASURED: <c>NewEntity(45)</c> yields a feature answering
<c>ksBossEvolutionDefinition</c>. Accessors are passed to the shared reader as delegates because the two
definitions share no interface for these members. The feature is matched to an <c>Evolutions</c> element
BY ORDER among same-family ones, not by name (MEASURED in B4: different types share one display name). A
bare CouplingsCount cannot tell "a coupling exists" from "this coupling". Section refs are derived from
the definition and checked against the SAME section count published as `section_count`: a mismatch is an
incomplete derivation, named and not passed off as "fewer sections". MEASURED (B5.12) that both routes
agree, recorded as a separate check. API5 thinType=true corresponds to API7 ThinType "inward" (MEASURED:
dt_reverse = 1, volume 21632). Otherwise an empty list beside a non-zero counter would look like a fact
about the model. DOC: <c>ksbaseloftdefinition_sketches.html</c> and <c>ksbossloftdefinition_sketches.html</c>
describe the member «Sketches»: «Получить указатель на интерфейс массива эскизов элемента по сечениям»,
returning <c>ksEntityCollection</c>, with the note «Эскизы из данного массива используются для
построения элемента по сечениям». In interop the same member is declared as <c>Object Sketchs()</c>
(read from <c>docs/compatibility/kompas-api5-metadata.json</c>) — the help and interop spellings differ
by one letter, and that is named, not smoothed over. INVARIANT: refs are derived afresh, never
remembered — a creation-time reference dies on the first document mutation and <c>kompas_rebuild</c>
revokes ALL document references, while the product has no separate sketch-enumeration tool, so a fresh
reference can only come from the definition itself. Faces removed by the shell are ABSENT from the body
topology, so <c>kompas_read_topology</c> cannot yield them at all, while the removed-face set is the
edit input. INVARIANT: use <c>AsInterface</c> — a bare <c>is ksFaceDefinition</c> yields an EMPTY list
while <c>removed_face_count = 1</c>, because a <c>FaceArray()</c> element comes as <c>ksEntity</c> and
must be unwrapped via <c>GetDefinition()</c>. Both the tree and the API7 collection enumerate features in
creation order, so the position among same-family ones is a stable address.
