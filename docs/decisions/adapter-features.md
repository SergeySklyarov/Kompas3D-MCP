# Признаки-операции - решения и измерения

Модуль: `src/KompasMcp.Api5Adapter/Api5Session.Rotated.cs` (вращение). Здесь - история правок,
вынесенная из кода. Действующие правила остались в коде под метками `INVARIANT:` / `DOC:` /
`MEASURED:` / `LIMIT:` и ссылкой `History: docs/decisions/adapter-features.md#<anchor>`.

## <a id="rotated-route"></a>Маршрут вращения - фабрика API7, а не оболочка API5

**Что было.** Вращение создавалось через API5-оболочку (`NewEntity` + `Create`) вокруг объекта
фабрики API7 - смешанный жизненный цикл: `Create()` возвращал `true`, объект появлялся в дереве, а
объём не менялся. Это не свойство вращения, а дефект маршрута.

**Что измерено.** 17.09.2026, прогон `95fa844107ce41609d6278f8f6c5759f`
(`docs/acceptance/api7/rotation.json`), шаги R.24/R.25/R.26: все три фабричные операции
(`o3d_baseRotated` 27, `o3d_bossRotated` 28, `o3d_cutRotated` 29) строят тело с аналитическим
объёмом 50265.4824574366 против π·r²·h = 50265.4824574367 при r=20, h=40. Результат
воспроизводится на чистом документе, форма подтверждена независимо от объёма (одна цилиндрическая
грань r=20 h=40, габарит 40×40×40), признак переживает save → close → reopen. Разрез снимает
25132.7412287183 с плиты 120×120×40, а boss, записывающий `OperationResult = ksOperationCut`,
меняет объём на 0.

**Что решено.** Вращение создаётся фабрикой `IModelContainer.Rotateds` напрямую. Оболочка API5
(`NewEntity + Create`) на этом объекте не работает - это измерено и было причиной прежней
блокировки.

**Дословно из кода (verbatim, EN).** Run `95fa844107ce41609d6278f8f6c5759f` of 17.09.2026 (`docs/acceptance/api7/rotation.json`), steps R.24/R.25/R.26, established the factory route and its shape. MEASURED: 17.09.2026 - (R.24) all three factory operations (`o3d_baseRotated` 27, `o3d_bossRotated` 28, `o3d_cutRotated` 29) build a body whose analytical volume is 50265.4824574366 against π·r²·h = 50265.4824574367 at r=20, h=40; (R.25) the same result reproduces from scratch on a fresh document, shape verified independently of volume (one cylindrical face r=20 h=40, bounds 40×40×40), and the feature survives save → close → reopen; (R.26) a cut removes 25132.7412287183 from a 120×120×40 plate, while a boss writing `OperationResult = ksOperationCut` changes the volume by 0.

MEASURED: 18.09.2026 (FullTurnProbe F.1…F.5, independently re-read from the saved `.m3d` by M3dVerificationProbe) - `Angle[true]` carries the requested angle directly (360→360°, 180→180°, 90→90°); the second slot of the pair, equal to the first, doubles the sweep. A full turn is built by a single call. This disproved the earlier "saturation at 180°" claim, which came from a step that changed `CutOffByPoint` instead of the angle (`docs/acceptance/api7/full-turn-findings.md`).

The old API5 shell around an API7 factory object was a MIXED lifecycle - `Create()` returned `true`, the object appeared in the tree and the volume did not change - not a property of rotation. LIMIT: carried as refusals, not as caveats.

* An angle beyond a full turn is not accepted: 360° is the sweep's own ceiling (measured 18.09.2026), and a value above it is cut off BEFORE the mutation.
* `dtReverse` builds nothing (measured R.26.sector: `Update()` = False, 0 bodies); rejected before the mutation.
* The axis is mandatory; rotation without an axis is not built at all.
* A thin wall is unmeasured: the route was measured on a SOLID body, so a requested thin wall is refused with CAPABILITY_UNAVAILABLE rather than written as an unmeasured number.
* Attachment to an existing body DOES happen (measured 18.09.2026, probe F.10: `Union` fuses, `NewBody` adds a second body). The operation kind is derived from the factory kind: base→`NewBody`, boss→`Union`, cut→`Cut`.
* The target body is chosen BY GEOMETRY (probe F.11): only the INTERSECTED body is touched, not the first in the collection (KOMPAS reorders bodies, so an index is not an address). The declared `target_body_ref` is therefore verified AFTER the operation.

## <a id="rotated-multi-body"></a>Многoтельное вращение - какое тело тронуто

**Что было.** Прежняя редакция отказывала при многoтельной детали.

**Что измерено.** 18.09.2026 (FullTurnProbe F.11) на детали из двух тел: boss/Union (тел 2→2,
объёмы `[144000; 16000] → [16000; 181699.111843077]`) и cut/Cut (тел 2→2, объёмы
`[144000; 16000] → [131433.629385641; 16000]`) тронули РОВНО ОДНО тело - ПЕРЕСЕКАЕМОЕ, а не первое
в коллекции (КОМПАС переставил тела: `[144000; 16000] → [16000; …]`).

**Что решено.** Операция НЕ выбирает тело за вызывающего молча. Счётчики тел до/после и поимённое
изменение читаются и возвращаются; расхождение с объявленным `target_body_ref` - отказ с
`partialEffects`, а не молчаливое «наверное, то». Тело выбирается ПО ГЕОМЕТРИИ: индекс адресом не
является, потому что КОМПАС переставляет тела.

**Дословно из кода (verbatim, EN).** MEASURED: 18.09.2026 (FullTurnProbe F.11) - in a two-body part, boss/Union (bodies 2→2, volumes `[144000; 16000] → [16000; 181699.111843077]`) and cut/Cut (bodies 2→2, volumes `[144000; 16000] → [131433.629385641; 16000]`) each touched EXACTLY ONE body - the INTERSECTED one, not the first in the collection (KOMPAS swapped the bodies, `[144000; 16000] → [16000; …]`).

Hence the rule: the operation does NOT silently pick a body for the caller. Body counts before/after and the named change are read and returned; a mismatch with the declared target_body_ref is a refusal with partialEffects, not a silent "probably that one".

## <a id="angle-saturation-refuted"></a>Предел угла - 360°, а не 180°

**Что было.** Здесь стояла запись `angle_saturates_at_180` («развёртка линейна до 180° и дальше не
растёт») и отказ на угол > 180 со ссылкой на R.26.angles: «развёртка насыщается на 180°, запись 360
даёт ту же половину цилиндра».

**Что измерено.** 18.09.2026, проба FullTurnProbe (F.1…F.5) и независимое чтение сохранённого
`.m3d` пробой M3dVerificationProbe: `Angle[true]` несёт запрошенный угол напрямую (360→360°,
180→180°, 90→90°), а вторая половина пары, равная первой, развёртку удваивает. Полный оборот одним
вызовом ВЫРАЖАЕТСЯ - измерен объёмом 50265.4824574366 (π·r²·h при r=20, h=40), одной цилиндрической
гранью r=20 h=40 и габаритом 40×40×40. Прежний вывод происходил из шага, менявшего `CutOffByPoint`
вместо угла, и ни в одной строке не записывал `Angle[true] = 360` с полным набором параметров.

**Что решено.** Запись `angle_saturates_at_180` СНЯТА; верхняя граница - 360° (потолок развёртки:
больше полного оборота сектор не занимает). Класс ошибки сохранён как число в `details`, но это
отказ по существу, а не по прежнему неверному пределу. Прежняя редакция возвращала здесь уровень
`call_returned`; строка RO.4 падала «уровень=call_returned» на вызове, у которого ВСЕ пять проверок
прошли, включая численное совпадение объёма.

## <a id="feature-ref-withheld"></a>Ссылка на признак не выдана - уровень не понижается

**Что было.** Первая редакция возвращала `CallReturned` всегда, когда признак не находился в дереве
API5.

**Что измерено.** Строка RO.4 падала «уровень=call_returned» на вызове, у которого ВСЕ пять проверок
прошли, включая численное совпадение объёма. Номер дерева у вращения не измерялся (в отличие от
пары 52→583 у отверстия).

**Что решено.** Уровень НЕ понижается до `call_returned`: отсутствие ссылки и подтверждённость
геометрии - два независимых утверждения, и первое не ослабляет второе. Неадресуемость здесь -
ожидаемое состояние, а не признак неудавшейся геометрии; вызывающий получает
`feature_ref_withheld` вместо ссылки, по которой правка всё равно не сработала бы.

**Дословно из кода (verbatim, EN).** A reference to a feature the API5 tree does not show must not be issued: an edit through it would fail anyway, and the caller would learn about it later. The missing reference and the confirmed geometry are two independent claims, and the first does not weaken the second: the volume matched the analytical one, the parameters were re-read, the surface of revolution was found - all of that is measured and stays measured regardless of whether the feature is visible in the API5 tree. The first revision always returned CallReturned here, and that was a genuine acceptance defect: line RO.4 failed with "level=call_returned" on a call where ALL five checks passed, including the numeric volume match. The tree number for a rotation was never measured (unlike the 52→583 pair for a hole), so non-addressability here is the expected state, not a sign of failed geometry.

## <a id="read-rotated-matching"></a>Сопоставление признака вращения - по углу, а не по углу и направлению

**Что было.** Документирующий комментарий `ReadRotatedFeature` утверждал, что сопоставление идёт «по
совпадению угла И направления».

**Что измерено.** Чтением кода 05.10.2026: цикл сопоставления сравнивает только
`candidate.AngleDeg` с прочитанным углом сущности; направление в сравнении не участвует.

**Что решено.** Комментарий приведён к фактическому поведению: сопоставление идёт по одному углу, а
при нескольких кандидатах возвращается `null` («не прочитано», а не «вот первый»). Слабость угла как
признака тождества (два полуоборота вокруг разных осей совпадут) сохранена в формулировке как
обоснование отказа от догадки.

**Дословно из кода (verbatim, EN).** The angle as an identity token is weak (two half-turns around different axes would match), so with several candidates `null` is returned - "not read", not "here is the first": passing a foreign parameter off as the addressed feature's parameter would be lying about the model. NOTE: the code compares the angle ONLY; the direction is not part of the match (the earlier wording said "angle AND direction", which the code does not do - corrected 05.10.2026). Reading is NOT mutation: `BeginEdit`/`EndEdit`/`Update` are not called, the revision is not bumped.

## <a id="foreign-field-list"></a>Перечень чужих полей - расширяется каждым новым полем

**Что было.** Правка вращения с `couplings` принималась, а цепочки не применялись: перечень полей
B5 (кинематика, сечения, оболочка) был неполон на одно поле.

**Что измерено.** 20.09.2026: поля B5 и семейства отверстия (наряд SM07 §3.2) выбирают ветку по
самому полю, поэтому поле, не попавшее в перечень чужих, уходит в чужую ветку, где просто не
читается, - то есть принимается и игнорируется.

**Что решено.** `couplings` дописан 20.09.2026 тем же порядком, что и в `SolidOps.cs`; поля
отверстия (`diameter_mm` и поля его режимов, `expected_volume_delta_mm3`) дописаны тем же порядком.
Перечень чужих полей обязан получать каждое новое поле контракта, иначе поле принимается и не
применяется.

## <a id="fillet-radius-api7"></a>Радиус скругления - маршрут API7, а не API5 (16.09.2026)

Модуль: `src/KompasMcp.Api5Adapter/Api5Session.FilletEdit.cs`.

**Что было.** Первая версия правила радиус через `ksFilletDefinition.radius`: у определения
`get_radius`/`set_radius` объявлены и читаются (проверено по метаданным P0.2), поэтому маршрут писал
именно в него - и не применялся.

**Что измерено.** 16.09.2026 (строка FL04r, пластина 100×80×10, R3 по четырём вертикальным рёбрам):
`definition.radius = 5` вернуло управление без ошибки, `entity.Update()` вернул true,
`RebuildDocument()` прошёл, определение перечитало радиус 5 - а объём остался прежним
79922.74333882307, то есть геометрия не изменилась. Сеттер принимает значение, геттер его читает,
модель его игнорирует.

**Что решено.** Радиус пишется в `IFillet.Radius1` на живой модели - как угол фаски. Запись, которую
модель игнорирует, объявляется отказом, а не успехом. Признак API5 сопоставляется с `IFillet` по
СВОИМ ВХОДАМ; радиус остаётся только запасным ключом.

## <a id="fillet-edge-set-route"></a>Правка набора рёбер - измеренный маршрут API7 (проба H-2)

**Что было.** Правка набора шла через `ksFilletDefinition.array()` (`Clear()`, затем `Add()`).

**Что измерено.** 16.09.2026 восемью пробами: маршрут API5 правку набора НЕ даёт. После скругления
«угловых вертикальных» рёбер в топологии 0 из 4 - исходные отзывает само создание скругления
(предъявление даёт `STALE_REFERENCE` до правки), а существующие вертикальные рёбра скруглённых углов
признак НЕ УДЕРЖИВАЕТ: любой набор из них схлопывает определение (`edges_read_back = 0`, объём
возвращается к пластине; проверено на 1, 2, 4 и 8 рёбрах). Ненулевой `edges_read_back` даёт только
ВТОРОЙ вызов подряд, и это destroy-and-rebuild, а не правка. Проба H-2
(`docs/acceptance/api7/fillet-base-objects.md`, 14 PASS / 0 FAIL / 0 UNKNOWN, четыре прогона подряд,
собственный `run_id`): рабочий маршрут - `IModelContainer.Fillets[i] → IFillet`; `IFillet.BaseObjects`
читается как `System.Object[]` из `IModelObject`, пишется полной заменой; `IFillet.Update()`
обязателен; все объекты берутся с ЖИВОЙ модели после `save → close → reopen`, а не захваченные при
создании (в этом была ошибка пробы H).

**Что решено.** Маршрут API5 из метода удалён, а не оставлен веткой. Признак API5 сопоставляется с
`IFillet` по СВОИМ ТЕКУЩИМ входам (перенос `ksAPI7Dual`, сравнение устойчивого
`IModelObject.Reference`), а не по имени, индексу или радиусу. Опознание и предъявление - разные
вопросы с разными ключами: опознание по собственным входам, предъявление - объектами переноса.

## <a id="fillet-edge-set-ban-lifted"></a>Запрет на предъявление перенесённых объектов снят (17.09.2026)

**Что было.** Набор разрешалось только ОТБИРАТЬ из собственных входов признака по совпадению
`Reference`, а несовпадающие рёбра отвергались. Обоснованием служило наблюдение H2.7, прочитанное как
«ссылки входов признака и рёбра тела лежат в РАЗНЫХ контекстах и несопоставимы».

**Что измерено.** 17.09.2026 на `FL10x` (1→1): полосы СОСЕДНИЕ - перенесённое ребро `1073742309`
против входа признака `1073742308`, тип у обоих `ksObjectEdge`, обе ссылки устойчивы при повторном
переносе и повторном чтении, а адресные привязки разные. Двойственность API5/API7 (два COM-объекта
про одно ребро) была принята за непроходимую границу. Решающий контроль H2.4: признаку было
предъявлено ребро тела, которого среди его собственных входов ЗАВЕДОМО не было (свободный угол), и
KOMPAS состав принял - состав переехал на другой угол при неизменном размере набора. Проба H-2 такой
сверки не делала никогда, поэтому запрет был не измерением, а догадкой.

**Что решено.** Запрет снят: несовпадающие рёбра предъявляются перенесёнными объектами (H2.4).
Подмножество собственных входов осталось ОТДЕЛЬНОЙ веткой - для него это строго измеренный путь
H2.3/H2.5. Опознание признака по-прежнему идёт по его собственным входам. Границы, не переносимые на
общий вывод: расширение на эталоне 100×80×10 не измерено (у пластины ровно четыре вертикальных угла);
измерены сокращение (4→3, 4→2) и замена при неизменном размере (1→1).

**Дословно из кода (verbatim, EN).** The probe wrote into IFillet.BaseObjects an array of ONE body edge transferred by ksAPI7Dual that was definitely not among the feature's own inputs (a free corner), and KOMPAS accepted it: the composition moved to another corner at an unchanged set size. So the product accepts transferred objects and does NOT require them to match what the feature already holds.

This used to be STRICTER: the set was selected only from the feature's own inputs by Reference match, and everything else was rejected. That rule came from a misunderstanding - it rested on observation H2.7 ("inputs 1073742065–67 against body edge 1073742080") read as "the contexts are incomparable". MEASURED 17.09.2026 on FL10x: the bands are ADJACENT (transferred edge 1073742309 against feature input 1073742308), both are ksObjectEdge, both references are stable across re-read, and the address bindings differ. So this is not "a foreign numbering space" but the ordinary API5/API7 duality: a body ksEntity and a feature IModelObject are two different COM objects for one edge. The probe never made such a check, so the ban was a guess, not a measurement.

What remains of the old rule and why: the feature must still be IDENTIFIED (see above, FindIndexByInputReferences), and that needs its own inputs. Matching references mean the client asks to keep part of what already exists - then the SAME feature objects are taken (not recreated), because for a subset that is the strictly measured H2.3/H2.5 path. NON-matching ones are presented transferred, as in H2.4.

## <a id="fillet-erasure-and-no-effect"></a>Схлопывание признака и запись без эффекта - отказ (17–18.09.2026)

**Что было.** Ответ на предъявление набора рёбер ТЕЛА, который признак не удержал, отдавался с
`err=None` и `level=call_returned`, то есть клиент, читающий только `err`/`level`/`status`, принимал
его за успешную правку.

**Что измерено.** 17.09.2026 (FL25, Г-образная пластина 100×80 с вырезом 40×30, шесть вертикальных
углов): набор [дуга скруглённого угла + вертикальное ребро свободного угла] СХЛОПНУЛ признак - объём
оказался равен 68000, то есть Г-ПЛАСТИНЕ БЕЗ СКРУГЛЕНИЙ, хотя запись и перестроение прошли.
18.09.2026 (та же деталь): набор [дуга + вертикальное ребро СВОБОДНОГО угла] дал `edges_read_back=1`
при предъявленных 2 и объём остался на ОДНОМ угле (67980.68583470576) - исход «не произошло ничего».

**Что решено.** Оба исхода объявлены отказом ДО возврата успеха. Критерий - перечитанный состав и
сохранность признака, а не арифметика по объёму (она зависела бы от числа и вида углов и повторила бы
ошибку подгонки). Мутация уже произошла, поэтому отказ несёт `partial_effects=true`, а ревизия
поднимается; клиент обязан перечитать контекст и документ.

**Дословно из кода (verbatim, EN).** FEATURE ERASURE IS A REFUSAL, NOT A "SUCCESS AT A LOWER LEVEL". MEASURED 17.09.2026 (FL25, an L-shaped plate 100×80 with a 40×30 cut-out, six vertical corners). A set [arc of a filleted corner + vertical edge of a free corner] was presented to the feature - both parts body edges. The write passed, the rebuild passed, the answer returned err=None and level=call_returned, and the volume came out 68000, i.e. the L-PLATE WITHOUT FILLETS: the set did not "fail to apply", it COLLAPSED the feature, erasing the fillet already made. A client reading only err and level would take this for a successful edit.

The cause is not the currency as such but its limit: BODY edges describe corners the feature does NOT hold right now, and presenting such a set means "build the fillet anew on these edges", not "keep the old one and add". For reduction and replacement this is immaterial (all presented edges already belong to the feature - FL10/FL10s/FL10b/FL10x), but on EXTENSION a corner the feature does not own is presented, and the previous composition is lost entirely.

So the case "the feature stopped reading as a fillet OR the feature did not survive" is declared a refusal BEFORE returning success. This is not a weakening of the expectation (the volume is still checked against analytics) and not a substitution of the outcome: the mutation already happened, so the refusal carries partial_effects=true - the client must learn the model changed.

The parent volume is taken as the volume BEFORE the edit plus what the edit removed: if the feature was erased, the geometry returns exactly to "before", not to "before minus the fillet". Hence the criterion is "after the edit the feature does not read as a fillet", not volume arithmetic: that would depend on the number of corners and repeat the fitting error.

A WRITE WITHOUT EFFECT IS ALSO A REFUSAL, NOT A "SUCCESS AT A LOWER LEVEL". MEASURED 18.09.2026 (FL25, an L-shaped plate 100×80 with a 40×30 cut-out). A set [arc of a filleted corner + vertical edge of a FREE corner] was presented to the feature - both parts body edges. The write passed, the rebuild passed, the answer returned err=None and level=call_returned, edges_read_back=1 against 2 presented, and the volume stayed on ONE corner (67980.68583470576). A client reading status and err would take this for a completed extension - exactly the defect row FL25 exists for.

The earlier revision below caught only feature ERASURE (collapse). Between "the feature was erased" and "the set was written" there is a third outcome - "nothing happened" - and it must be a refusal for the same reason: the edit was requested precisely because it changes something. An answer in which the edges_read_back=false check fired cannot be called a success.

The criterion is the RE-READ COMPOSITION, not the volume: the volume depends on the number and kind of corners (fillet of a concave corner ADDS material), and arithmetic on it would repeat the fitting error. partial_effects distinguishes "the model changed into something other than requested" from "the model was untouched": it compares the volume with the volume BEFORE the edit, not with the client's expectation.

## <a id="chamfer-route"></a>Фаска: маршрут и измерения (проба F, 12.09.2026)

**Что измерено** (проба F, `docs/acceptance/api7/chamfer.md`), а не взято из имён методов:
- F.2 - маршрут API5 работает: `NewEntity(o3d_chamfer=33)` →
  `ksChamferDefinition.SetChamferParam(transfer, d1, d2)` → `array()` как `ksEntityCollection` →
  `Add(ребро)` → `Create()` → `RebuildDocument()`. Четыре вертикальных ребра пластины 100×80×10 с
  катетами 2×2 сняли ровно 20·d₁·d₂ = 80 мм³;
- F.3/F.5 - правка катетов применяется на месте и до, и после save→close→reopen, значение
  перечитывается (`ok=True transfer=False d1=3 d2=3`);
- F.4/F.11 - `transfer` (он же `IChamfer.Direction`) меняет, какой катет ложится на какую грань;
  объём этого не различает, различают площади боковых граней;
- F.8 - фаска API5 видна из API7 как `IChamfer`, параметры читаются типизированно, но имя в API7
  читается другое («f-ch2» → «Фаска:1»), поэтому признак опознаётся по объекту реестра и типу, а не
  по имени.

**Что решено.** Правка параметров фаски - НЕ то же самое, что перепривязка опорного эскиза
выдавливания (Q-EDIT-SKETCH, `edit = blocked_api`): там отказ измерен на маршруте смены профиля, а
здесь измерена и работает смена числа. И наоборот: правка угла существующего признака этой пробой НЕ
измерена и потому отказана явно, а не «по наличию свойства».

## <a id="chamfer-method-substitution"></a>Фаска «расстояние и угол» (F.9/F.10)

**Что измерено.** Способом «расстояние и угол» фаска строится только через
`IChamfer.Angle = ksChamferSideAngle`, причём угол - в ГРАДУСАХ: 30 при катете 2 снял
46.188021535141 мм³, что есть 20·d·(d·tg 30°). Радианная гипотеза дала бы отрицательное число и была
отвергнута измерением.

**Что решено.** `SetChamferParam` умеет писать только способ «два катета» - второй катет производен
от угла, поэтому API5-маршрут записи теряет способ построения; для «расстояния и угла» используется
API7-интерфейс `IChamfer`.

**Дословно из кода (verbatim, EN).** A "distance and angle" chamfer lives as ksChamferSideAngle with a DERIVED second leg (d₂ = d₁·tg α). The API5 write route (SetChamferParam) only knows ksChamferTwoSides and does NOT preserve the build method: MEASURED on a 100×80×10 plate, d=2, α=30°, editing distance1_mm=3 without angle_deg changed the method, turned 30° into 45° and gave V=79820 instead of 79896.07695154587. That is a silent wrong result, so the write is refused BEFORE the mutation, naming "delete and recreate" instead. The method is read from API7 (API5 has none at all): on an ambiguous match ReadChamferAngle returns null and the write is NOT refused - "method not read" is not "method is angular".

## <a id="hole-route"></a>Отверстие: маршрут и измерения (проба M, 16.09.2026)

**Что измерено** (проба M, `docs/acceptance/api7/hole-modes.md`):
- M.2 - цековка: пилот Ø10 насквозь, выточка Ø18 глубиной 4. Снято 703.7167544041131 мм³ СВЕРХ
  сквозного отверстия, что есть π/4·(18²−10²)·4 = 703.7167544041137 - кольцо, а не второй полный
  цилиндр. Первый набросок формулы складывал пилот с целым цилиндром Ø18 и тем самым считал пилот
  дважды;
- M.3 - зенковка: правило `π·h/3·(rM² + rP·rM − 2·rP²)` прочитано с таблицы из 11 строк (3 угла ×
  3 входные глубины × 6 диаметров). `CountersinkDepth` ПРОИЗВОДНА: запись 2, 4 и 6 не меняет ничего,
  объект возвращает `(rM − rP)/tan(угол/2)`. Первая редакция называла закон `4/tan(угол/2)` -
  константа была подогнана под единственную строку таблицы, где `rM − rP = 4`; проба N.2 от
  17.09.2026 развела устье при неизменных пилоте и угле (Ø14/16/18/20/24 → h = 2/3/4/5/7) и тем
  самым показала, что «4» - разность радиусов той строки, а не постоянная;
- M.4 - глухое с плоским дном: снято 471.238898038471 против аналитических π·5²·6 =
  471.238898038469. Члена `ksDTBlind` в вендорском перечислении нет вовсе - глухое выражается
  `ksDTValue`.

**Почему маршрут API7, а не API5.** Отверстие в API5 есть (`NewEntity(o3d_hole=52)`), но параметров
режима в его определении нет физически: проба M трижды отвергла попытку записать режимные числа в сам
`IHole3D`, пока не выяснилось, что они живут на `HoleParameters`, приведённом к интерфейсу СВОЕГО
режима. Это структурная причина: «цековка» и «зенковка» отличаются не значением перечисления, а
интерфейсом параметров.

**Дословно из кода (verbatim, EN).** Basis - probe M of 16.09.2026 (`docs/acceptance/api7/hole-modes.md`), not method names.

TEST: M.2 - counterbore: a Ø10 pilot through, a Ø18 recess 4 deep. Removed 703.7167544041131 mm³ OVER the through hole, which is π/4·(18²−10²)·4 = 703.7167544041137 - a ring, not a second full cylinder. A first draft of the formula added the pilot to a full Ø18 cylinder and thereby counted the pilot twice.

TEST: M.3 - countersink: the rule `π·h/3·(rM² + rP·rM − 2·rP²)` was read from an 11-row table (3 angles × 3 entry depths × 6 diameters), and acceptance refuses to pass until the WHOLE table agrees. Key observation - `CountersinkDepth` is DERIVED: writing 2, 4 and 6 changes nothing, the object returns `(rM − rP)/tan(angle/2)`, and one must judge by the returned number. Probe N.2 of 17.09.2026 separated the mouth with the pilot and angle unchanged (Ø14/16/18/20/24 → h = 2/3/4/5/7), showing "4" was the radius difference of that row, not a constant.

TEST: M.4 - blind with a flat bottom: removed 471.238898038471 vs the analytic π·5²·6 = 471.238898038469. The member `ksDTBlind` does not exist in the vendor enum at all - blind is expressed by `ksDTValue`.

TEST: M.5 - position away from the origin: of five routes exactly one shifted it, `Point3DParamSurface` + `OffsetType=ksOffsetByCoords` + `Offset1`/`Offset2`. A Ø10 hole landed exactly at (25, 15). `AssociationVertex` and `DirectionObject` gave DISP_E_TYPEMISMATCH, a sketch with an offset circle did not reach API7, `DepthVertex` and `DepthFace` read as null, `Axis` as False.

ROUTE - API7, not API5: a hole exists in API5 (`NewEntity(o3d_hole=52)`), but its definition physically has no mode parameters - probe M three times rejected writing mode numbers into `IHole3D` itself until it turned out they live on `HoleParameters` cast to the interface of ITS OWN mode. This is a structural cause, not convenience: "counterbore" and "countersink" differ not by an enum value but by the parameter interface.

INVARIANT: volume is read only on the MAIN body - `ReadVolume`, as for fillet and chamfer. The delta expectation is set by the caller; without it only the parameter read-back is confirmed, and the result is honestly marked unproven geometry rather than presented as confirmed.

## <a id="hole-offset-common"></a>Отверстие вне начала координат (M.5)

**Что измерено.** Из пяти маршрутов сдвинуло ровно один: `Point3DParamSurface` +
`OffsetType=ksOffsetByCoords` + `Offset1`/`Offset2`. Отверстие Ø10 встало точно в (25, 15).
`AssociationVertex` и `DirectionObject` дали `DISP_E_TYPEMISMATCH`, эскиз со смещённой окружностью до
API7 не доехал, `DepthVertex` и `DepthFace` читаются как null, `Axis` как False.

## <a id="hole-tree-type"></a>Признак отверстия - 583, а не 52 (проба N.1, 17.09.2026)

**Что было.** Первая версия искала `52` - `o3d_holeOperation`, то есть номер, под которым признак
СОЗДАЁТСЯ через `NewEntity(52)`.

**Что измерено.** Проба N.1 от 17.09.2026 напечатала обе коллекции дерева до и после создания:
признак лежит под `583` (`o3d_Hole3D`).

**Что решено.** Поиск идёт по `KompasObjectTypes.Hole3D` (583), а не по `HoleOperation` (52). Это
исправление измеренного дефекта, а не переименование.

## <a id="hole-edit-route"></a>Правка отверстия: допуск и правила «поле ↔ режим»

**Что измерено.** Допуск сверки записанного числа с перечитанным - `1e-6`, тот же, которым
сверяется записанное с перечитанным при СОЗДАНИИ (`HoleParametersMatch`), и он не «на глаз»: зонд
`scratch/_hole_edit_probe.py` прочитал записанные 10, 12, 20, 24, 5, 4 и 90 БЕЗ расхождения вовсе,
а производная глубина зенковки вернулась как `7.000000000000001` - собственный шум ядра лежит далеко
за пределами этого допуска и не маскирует «не применилось».

**Что решено.** Правила «поле ↔ режим» отклоняются ДО COM: режимные числа разных режимов живут в
разных интерфейсах, и «применили, что смогли, остальное проигнорировали» было бы молча неверной
геометрией. Цена ошибки здесь несимметрична: лишний отказ виден сразу, а принятое и проигнорированное
число доживает до приёмки, выглядя как выполненная операция.

**Дословно из кода (verbatim, EN).** The feature is taken by the DOCUMENTED member `IHoles3D.Hole3D[index]` - the same route `kompas_get_feature` reads - the members of ITS OWN mode are written, `IModelObject.Update()` applied, then the rebuild. The volume changes exactly by the analytic value in all three modes, and controls (b) and (c) show that the change is caused by `Update()` itself: without it the volume does not move, and the parameter interface of a foreign mode is UNREACHABLE on the object.

INVARIANT: the feature address is the same as for the read and is NOT guessed - the "tree feature ↔ `Holes3D` entry" correspondence is proven by the hole being unique in the document: at `count != 1` the correspondence is unproven and the call is refused `CAPABILITY_UNAVAILABLE` before COM. Picking an address by a body list or tree order is forbidden by lesson F-11: the address is ensured by the setup, not by a guess.

LIMIT: the mode is NOT changed by the edit - the existing feature's mode is read from the model (`IHole3D.HoleType`) and frames it: foreign-mode fields are refused by name before COM, and its own members are written. Mode change (`blind_flat` → `through_counterbore` and back) was not measured and is not performed here - "accepted and built differently" is afterwards indistinguishable from "applied".

INVARIANT: the countersink depth is not asserted - at `ksCTDiameterAngle` it is derived (M.3/N.2), so the `countersink_depth_derived` check publishes the READ number and states plainly that the written one is not checked.

## <a id="loft-route"></a>Элемент по сечениям: почему API7 (шаги B5.4/B5.9)

**Почему API7.** Обязательная строка `SM-05.base.mode_couplings` требует цепочек соответствия
сечений, а в API5 их нет вовсе: ни `ksBaseLoftDefinition`, ни `ksBossLoftDefinition` не объявляют ни
`AddCoupling`, ни `Coupling`. В API7 они документированы - `iloft_propers.html` перечисляет
`Coupling` и `CouplingsCount`, `iloft_addcoupling.html` описывает `AddCoupling()` → `ICoupling`.
Измерено (шаг B5.9): `AddCoupling()` вернул `KompasAPI7.CouplingClass`, `CouplingsCount = 1`. Поэтому
семейство ведётся одним маршрутом, на котором выразимы ВСЕ его обязательные строки.

**Фабрика документирована для приклеенного типа.** `ilofts_add.html`: «Допустимыми значениями
`LoftType` являются `o3d_bossLoft`, `o3d_cutLoft` для коллекции операций `IModelContainer::Lofts`»;
«после получения нового интерфейса нужно задать параметры операции и вызвать метод
`IModelObject::Update`». Сечения задаются свойством `ILoft.Sketchs` типа `VARIANT` - «массив
`SAFEARRAY` объектов `LPDISPATCH`» (`iloft_sketchs.html`). Измерено: присваивание массива дало чтение
`System.Object[]` из 2 элементов, `Update() = True`, объём `28000` - тот же эталон, что у
API5-маршрута `NewEntity(30)` (шаг B5.4).

**Что здесь НЕ утверждается.** Порядок сечений объёмом не доказывается: концентрические параллельные
сечения дают `28000` в любом порядке, поэтому «порядок соблюдён» требует различающей постановки и
здесь не выдаётся за проверенное. Параллельность плоскостей сечений - обязанность проверяющей стороны
(§6.3 п. 7 наряда); в этой редакции она НЕ проверяется и названа открытым аспектом. Содержимое
цепочек соответствия не задаётся: измерено существование цепочки, а не её настройка.

## <a id="pattern-route"></a>Массивы: маршрут и что считается доказательством

**Маршрут один и он опубликован.** `IModelContainer.FeaturePatterns.Add(ksObj3dTypeEnum)` →
`QI(ILinearPattern | ICircularPattern | IMirrorPattern)` → запись параметров → `Update()` →
`Rebuild`. Соответствие «тип → интерфейс» взято со страницы справки SDK `copytype.html`, а не
выведено по аналогии с вращением.

**Ось строится в ТОЙ ЖЕ детали по двум точкам модели.** Массивы принимают `IModelObject`, а не
ссылку, и ссылка из чужого документа сюда не протаскивается. Маршрут построения оси -
`IAuxiliaryGeomContainer.Axes3D.Add(o3d_axis2Points)` - уже измерен вращением (SM-03) и здесь
переиспользуется как ИЗМЕРЕННЫЙ, а не как предположенный.

**Что считается доказательством.** Возврат `Update() = true` - это «принято», а не «применено».
Поэтому после перестроения модель ЧИТАЕТСЯ обратно: параметры признака, число экземпляров
(`GetExemplarsCounts`), объём документа, число тел и - для массивов отверстий - ПОИМЁННЫЙ набор
цилиндрических граней с координатами их осей. Последнее и есть проверка «по каждому экземпляру»:
объём не отличает четыре отверстия от трёх плюс одно наложенное.

**Соотнесение направлений кругового массива взято со страницы справки.** `icircularpattern_props.html`
называет `Count1`/`Step1` РАДИАЛЬНЫМИ, а `Count2`/`Step2` - КОЛЬЦЕВЫМИ, причём `Step2` подписан как
«Угловой шаг (градусы)». Это закрывает OQ-B-02 и опровергает ожидание, записанное в наряде (§6.3,
§8), где кольцевым считалось первое направление. Под сомнение поставлено ОЖИДАНИЕ, а не измерение.

## <a id="pattern-edit"></a>Правка массива: отдельный файл и признак без номера

**Почему отдельный файл, а не ветка внутри `UpdateFeature`.** Ветка там выбирается по
`entity.type` - измеренному номеру признака в дереве. Для массива этот номер в сеансе B4 не
измерялся, и выдумывать его нельзя: ошибка здесь означала бы, что правка «не находит» признак ровно
так же, как это было у отверстия (искали по 52, а признак лежит под 583). Поэтому признак массива
опознаётся НЕ по номеру, а тем же прибором, что и чтение: сопоставлением с элементом
`IModelContainer.FeaturePatterns` по имени оболочки дерева и штампу обновления.

**Что считается доказательством.** `Update() = true` - «принято», а не «применено». Поэтому после
перестроения признак ЧИТАЕТСЯ ОБРАТНО, и каждому запрошенному члену соответствует отдельная проверка
`read_back_<член>`, сверяющая записанное число с тем, что модель отдаёт.

**Смена опоры не выполняется.** `Axis1/Axis2`, `Axis` и `Plane` принимают `IModelObject`, а не ссылку
сервера; ни один прогон B4 смену опоры существующего массива не измерял. Вызов, где такая смена
подразумевалась бы, отвергается до мутации, а не выполняется частично.

**Живой объект массива не кэшируется между вызовами.** Адрес COM-объекта не переживает перестроения,
и сохранённый объект правил бы уже не тот признак.

**Дословно из кода (verbatim, EN).** WHY A SEPARATE FILE, not a branch inside `UpdateFeature`: that branch is chosen by `entity.type` - the measured feature number in the tree. For a pattern this number was not measured in session B4 and must not be invented: an error here would mean the edit "does not find" the feature exactly as happened with the hole (searched by 52, the feature lies under 583). The pattern feature is therefore identified NOT by number but by the same instrument as the read (`Api5Session.PatternRead`): matching against an `IModelContainer.FeaturePatterns` element by the tree-wrapper name and update stamp.

INVARIANT: `Update()=true` is "accepted", not "applied" - after the rebuild the feature is READ BACK (`Api7Pattern.ReadPattern`), each requested member gets its own `read_back_<member>` check, and document volume and body count confirm application geometrically if the caller gave an analytic expectation.

LIMIT: support change is not performed - `Axis1/Axis2`, `Axis`, `Plane` take an `IModelObject`, not a server reference, and no B4 run measured a support change of an existing pattern; a call implying one is refused before the mutation.

## <a id="mirror-all-bodies"></a>Зеркальный массив: «зеркально отразить все»

**Числа типов опубликованы, а не подобраны.** `copytype.html`: `o3d_mirrorOperation=48` -
«зеркальный массив» (`IMirrorPattern`), `o3d_mirrorAllOperation=49` - «зеркально отразить все» (тот
же `IMirrorPattern`, дополнительно `IChooseBodies7`).

**Область действия `SaveInitialObjects` ограничена справкой, и это измерено.**
`imirrorpattern_saveinitialobjects.html` говорит прямо: «Свойство работает ТОЛЬКО для
`o3d_mirrorAllOperation`» и «у других операций зеркального копирования возможность скрыть экземпляры
отсутствует». Измерено прогоном на обеих операциях: у `o3d_mirrorAllOperation` (режим
`AllBodies`) запись `false` читается обратно как `false` и тела действительно заменяются отражёнными
(2 → 2 тела по 4 000), а у `o3d_mirrorOperation` (режим `SelectedOperations`) запись `false` читается
обратно как `true` и геометрия не меняется вовсе. Требовать различия половин на операции 48 значило
бы требовать того, что справка у неё отрицает; параметр принимается, а его непринятие называется
заметкой маршрута, а не выдаётся за сработавшее свойство.

## <a id="shell-empty-faces"></a>Оболочка: пустой список граней отвергается

**Что измерено.** Ожидание «пустой список даёт замкнутую оболочку `36224`» не подтвердилось ни на
одном из двух API: API5 (шаг B5.6) дал `80000`, API7 (шаг B5.10, четыре постановки) -
`79999.99999999999` при 6 гранях, ровно как у исходного короба, тогда как открытая оболочка даёт
`21632` при 11 гранях. То есть `Create()/Update()` возвращают `true` («принято»), а тело не меняется
(«не применено»).

**Что решено.** Выдавать такой исход за построенную оболочку запрещено, поэтому вызов с пустым
списком граней именованно отвергается ДО COM - это измеренный отказ, а не запрет по вкусу.

## <a id="sweep-route"></a>Кинематический элемент: маршрут (шаги B5.1/B5.2/B5.7/B5.8)

**Маршрут - документированный API5, и это проверено по справке.** `ksbaseevolutiondefinition.html`
(«Основание — кинематический элемент (Интерфейсы ksBaseEvolutionDefinition, IBaseEvolutionDefinition)»)
описывает интерфейс, который «можно получить, используя метод интерфейса элемента модели
`ksEntity::GetDefinition`», и перечисляет ровно те члены, что здесь используются: `sketchShiftType`,
`SetSketch`, `PathPartArray`, `GetPathLength(bitVector)`. Тип объекта - `o3d_baseEvolution = 45`
(`obj3dtype.html`). При этом `ievolutions_add.html` перечисляет допустимыми значениями
`IEvolutions::Add` только `o3d_bossEvolution` (46) и `o3d_cutEvolution` (47) - базового типа 45 в
списке нет. Измерено 20.09.2026 (шаг B5.7): `IEvolutions.Add(45)` возвращает
`KompasAPI7.EvolutionClass`, то есть объект ВЫДАЁТСЯ, - но валидность тела по этому пути не
измерялась, и «выдан объект» не то же самое, что «документированный маршрут». Поэтому создание идёт
`ksPart.NewEntity(45)` + `ksBaseEvolutionDefinition`, а не через фабрику API7.

**Справка объявляет этот интерфейс устаревшим - и это записано, а не спрятано.** «Данный интерфейс
устарел. Рекомендуется использовать вместо него интерфейс ksBossLoftDefinition» (в тексте страницы
именно так, хотя для кинематического элемента естественен `ksBossEvolutionDefinition` - расхождение
внутри самой справки). Приклеенный маршрут `NewEntity(46)` измерен отдельно (шаг B5.8) и строит ТО ЖЕ
тело: `31415.92653589775` против `31415.926535897932` на эталоне «окружность Ø20 по отрезку 100».
Обязательные строки этапа описаны как базовые, поэтому используется тип 45.

**Режимы движения сечения документированы и совпали с измерением.**
`ksbaseevolutiondefinition_sketchshifttype.html`: 0 - «образующая переносится параллельно самой
себе», 1 - «сохраняет исходный угол с направляющей», 2 - «плоскость образующей выставляется и
сохраняется ортогональной направляющей». Измерено (шаг B5.2): на дуге R50/90° ортогональный режим дал
`S × L = 24674.011002723397`, параллельный отличается на `8966.04773477437` мм³.

**Траектория присоединяется к `ksEntityCollection`, и это измерено.** Шаг B5.1: `PathPartArray()`
возвращает `System.__ComObject`, который успешно приводится к `ksEntityCollection`, и `Add(эскиз)`
возвращает `True`. Рефлексия по `__ComObject` членов не даёт и как способ разведки непригодна.

## <a id="sweep-first-body"></a>Кинематический элемент: объём на первом теле

**Что измерено 20.09.2026.** На ПЕРВОМ теле объём до операции не читается вовсе - `ReadVolume()`
отдаёт `null`, потому что главного тела ещё нет. Это не ноль и не «не выросло»: величины до операции
не существует. Прежняя редакция (`volumeBefore is double before`) объявляла на этом ложный отказ
«не прочитано → 31415.9265358978 мм³» на КАЖДОЙ операции, создающей первое тело.

**Что решено.** Состояния различаются тем, ЧТО ИЗМЕРЕНО: тел 0 - материала до операции не было по
определению модели, и «вырос» означает «стало больше нуля»; тел больше нуля, а объём не прочитан -
величина НЕ ПРОЧИТАНА, и проверка называется непрочитанной, а не ложной. Признак «материал добавлен»
отделён от численного совпадения: кинематическая операция базового типа обязана УВЕЛИЧИТЬ объём, и
это проверяется без аналитического ожидания.

**Дефект прибора, названный отдельно.** Длина траектории читается ПОСЛЕ построения: первый прогон
пробы читал её до `Create()` и получал 0 - это был дефект прибора, а не факт о продукте (шаг B5.3).

## <a id="mint-input-references"></a>Порождение ссылок на собственные входы признака (05.10.2026)

**Дословно из кода (verbatim, EN).** Why here and not in the edge-set edit. A feature's own input is not a body edge: it has no `edge:` registry string and cannot have one, because the registry issues those to BODY edges. `IModelObject.Reference` numbers are not substituted into the edit contract by type. So the SERVER must mint the input string, and it does so at the only place where the input numbers become visible outside - when the feature is read. Same principle as `edge:` references: the client does not invent identifiers, it substitutes issued ones.

INVARIANT: the minting is bound to the document REVISION (not to the feature state), like other references: after a mutation, references of the previous revision are cut off by `Require`, and the client must re-read the context. Otherwise an edit could be presented against a stale composition.

References are not issued when: the API7 bridge is not built, several fillets share the radius (inputs not read - `null`), or there are no inputs at all (empty list). The reference order matches `BaseObjectReferences`, because both come from one walk of `BaseObjects`.

## <a id="fillet-identify-by-inputs"></a>Опознание скругления по собственным входам, радиус - запасной ключ (05.10.2026)

**Дословно из кода (verbatim, EN).** The acceptance addressing experiment (two R3, "change the selected one, keep the second") is built on exactly such a model. Identification by radius returned `null` there - the feature stayed without API7 parameters and without `base_object_input_refs`: the client got no reference to either of the two features and could address neither. That is a denial of a supported scenario, so the key source is the feature's OWN INPUTS - the same composition measure the write route uses (`FindIndexByInputReferences`). The radius remains a FALLBACK and is used exactly when the composition could not be read (MEASURED: on an existing fillet the API5 definition does not return its inputs). Then, and only then, the old rule applies: an unambiguous radius match or `null`.

## <a id="feature-edit-basis"></a>Основание правки признака - измерения P2.3 (05.10.2026)

**Дословно из кода (verbatim, EN).** The entity handed back by `NewEntity(24)` reports 24 while the same committed feature read out of the tree reports 25, so a type number captured at creation is not an identity.

The three extrusion definitions share no common interface for these getters, and `GetThinParam` is declared `out` on base/cut but `ref` on boss, so the families are branched explicitly instead of being unified by reflection.

## <a id="rotated-edit"></a>Правка угла вращения (05.10.2026)

**Дословно из кода (verbatim, EN).** With bounds `z[−20,20] → z[−0,20] → z[−20,20]` - a geometric change, not merely a written number. The order "write angle → `Update()` → rebuild" is part of the contract, as on creation: without `Update()` the setter returns success while the model stays as it was. ONLY the angle is written (and the direction, if given). The profile and axis of an existing rotation are not changed by this call: those routes were not measured on a rotation, and accepting `sketch_ref` would promise a re-binding that does not exist.

## <a id="rotated-find-entity"></a>Поиск признака вращения в дереве (05.10.2026)

**Дословно из кода (verbatim, EN).** The cut is visible as 29, the same number it was created with by the factory. So all three kinds (27/28/29) are checked, not one number: the client chooses the operation kind, and searching only for `cut` would miss `base`.

Selection runs not by name and not by index but by the entity having a profile and an axis (`IRotated` answers the cast). A name here is not an identifier - measured on a chamfer (F.8: `f-ch2` was replaced by the localised feature name) - and the "last element" index without such a check would point at anything if the operation failed. The absence of a meaningful candidate yields `null`, and the caller gets `feature_ref_withheld` instead of a reference an edit would not have worked through anyway.

## <a id="rotated-identify-entity"></a>Опознание признака вращения - по номеру, QI запасной (05.10.2026)

**Дословно из кода (verbatim, EN).** The first revision relied on QI alone and thus silently lost the feature: the branch name ("by QI") was passed off as the recognition result. The type number is the route for a FEATURE FROM THE TREE, not an identifier: it changes between creation time and the tree (for an extrusion 24 → 25, MEASURED P2.3), so recognition must use the SET of numbers of the three rotation kinds, not a single number remembered at creation.

The question "does a `ksEntity` from the tree answer to QI(IRotated)" was tested on 18.09.2026 in three ways: an `is` cast, a runtime-type cast to the interface, and a direct call of the `Angle` member with interception - all three REFUSED on a feature that demonstrably reads from the model (angle 360, type 29, live UpdateStamp). So the QI branch is kept here as a fallback for a payload from the reference registry, but the RELIANCE is on the tree number: otherwise recognition would fall back to a means the tree object does not answer.

## <a id="create-false-snapshot"></a>Отказ `Entity.Create()`: код ошибки, состояние и ревизия (наряды ENTITY_CREATE_FALSE и ENTITY_CREATE_FALSE_FOLLOWUP)

**Зачем.** Клиент (OBS-019) получил `GEOMETRY_FAILED: Entity.Create=false` на вырезе cut/blind/positive
по явному телу, тогда как through/symmetric тем же профилем у него прошли. В первом полном прогоне
доводки 09.10.2026 тем же сообщением упали строки F08.05/06/07 (каскад 30 FAIL), повторные прогоны и
`--f08-only` прошли. Отказ единичный и по требованию не воспроизводится — поэтому снимок пишется В
МОМЕНТ отказа, а не разбирается потом по уцелевшим журналам.

**Прежний вывод «документированного маршрута причины НЕТ» — ОШИБОЧЕН, и вот почему.** Он опирался на
поиск ТОЛЬКО по установленной справке пользователя (`D:\Programs\KOMPAS-3Dv24\Help\KOMPAS_ru-RU.zip`,
11 088 страниц). Справочник SDK лежит не там: `docs/API_COMPLIANCE.md` (строка 5) называет его адресом
<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/>, и в нём маршрут ЕСТЬ. Искали не там — это и есть причина
ошибки, а не отсутствие маршрута.

**Маршрут причины, дословно из справки SDK v24.**

- `KompasObject::ksReturnResult` (<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksreturnresult.html>),
  раздел «API интерфейсов. Версия 5» → «KompasObject — Интерфейс API КОМПАС» → методы. Синтаксис
  Automation: `long ksReturnResult();`. Возвращаемое значение: «Код ошибки в зависимости от типа
  документа: графического или документа-модели при выполнении библиотечной программы». Примечания:
  «Ошибка с номером >0 не является фатальной. Отрицательный номер ошибки приводит к завершению
  программы»; текст ошибки — `KompasObject::ksStrResult`; сброс нефатальной ошибки —
  `KompasObject::ksResultNULL`.
- `KompasObject::ksStrResult` (<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksstrresult.html>),
  «Получить строку сообщения, соответствующую результату работы библиотеки (с кодом ошибки)»,
  синтаксис Automation `BSTR ksStrResult();`; примечание: «Функция сбрасывает флаг ошибки в случае,
  если эта ошибка не является фатальной».
- `KompasObject::ksResultNULL` (<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksresultnull.html>),
  «Обнулить результат работы библиотеки, если ошибка не фатальная», синтаксис Automation
  `long ksResultNULL();`, возвращает 1, если ошибка была обнулена.
- Таблица кодов документа-модели: `ErrorType3d`
  (<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/errortype3d.html>) — «Фатальные ошибки — работа
  прекращается»: `et3dNo3dDocument` = −7, `et3dAbort` = −1; «Нефатальные ошибки — выполнение
  продолжается»: 123…627 (например `et3dError126` = 126 «Контур не разбивает ни одну из граней или
  совпадает с кромкой грани», `et3dError130` = 130 «Ошибочная топология»).

**Относится ли это к Automation API5, а не только к «библиотечной программе»?** Да, по самой странице:
раздел — «API интерфейсов. Версия 5», интерфейс — `KompasObject`, синтаксис указан как «Синтаксис
Automation», а «Аналог данного метода при использовании API экспортных функций — `ReturnResult`»
отделён отдельно. Формулировка «при выполнении библиотечной программы» описывает контекст (внешняя
программа, ведущая КОМПАС через API5), а не исключает Automation: страница прямо даёт Automation-синтаксис.
Ветвь остановки §5 наряда (справка исключает Automation) НЕ наступила.

**Что теперь делается** (`Api5Session.ClearKompasResult` / `ReadKompasResult`):
перед `feature.Create()` вызывается `ksResultNULL()` (сброс чужой нефатальной ошибки — иначе код
предыдущего вызова был бы прочитан как причина этого), после `Create() = false` читаются
`ksReturnResult()` и `ksStrResult()`. Код и текст идут в снимок (`kompas_result_code`,
`kompas_result_text`) и в текст отказа. INVARIANT: нулевой код — это ФАКТ «КОМПАС кода ошибки не
вернул», он так и назван, и выдумывать причину вместо него запрещено; «ПРИЧИНА НЕ УСТАНОВЛЕНА» в
сообщении клиенту остаётся ТОЛЬКО когда кода нет (нулевой или непрочитанный), а при ненулевом коде
называются код и его текст.

**Измерено на живом отказе (`EC9.07`/`EC9.08`/`EC9.15`).** Детерминированный отказ (профиль-отрезок)
дал `kompas_result_code = 54`, `kompas_result_text = «Невозможно выполнить операцию»`; снимок — 17
ключей из 17, ни одного пустого. Сообщение клиенту несёт код и текст, «ПРИЧИНА НЕ УСТАНОВЛЕНА» при
этом не пишется — и это проверяется строкой (`без_отговорки`), а не только декларируется.
**Названное расхождение:** пара (54, «Невозможно выполнить операцию») НЕ соответствует ни одной из
двух документированных таблиц — в таблице документа-модели (`errortype3d.html`, коды −7, −1, 123…627)
числа 54 нет вовсе, а в таблице графического документа (`errorcodes.html`) `etError54` означает
«Режим работы документа задан неверно». Код и текст ЧИТАЮТСЯ и НАЗЫВАЮТСЯ, но их ДЕКОДИРОВАНИЕ по
документированным таблицам НЕ подтверждено; страница `ksreturnresult` сама предупреждает, что коды
двух документов «частично совпадают, поэтому их нужно обрабатывать в зависимости от выполняемых
операций». Документированный способ выбрать таблицу — `IKompasError.IsError3D` (отдельный наряд).

**Код 54 НЕ различает «плохой вход» и «нерегулярный отказ» — измерено (наряд NEST_CREATE_FALSE).**
Один и тот же код приходит и там, где вход заведомо негоден, и там, где он годен. Сторона «негоден»:
профиль, цепочка которого ВЕТВИТСЯ (три примитива в одной точке), и профиль с РАЗОМКНУТОЙ цепочкой —
оба дали `Create()=false`, `kompas_result_code = 54`. Сторона «годен»: кольцо R10/r5 (площадь
235.61944901923448 мм²) базовым выдавливанием в свежем документе — профиль замкнут, площадь посчитана
сервером и совпадает с аналитикой, эскиз прочитан, — и тот же код 54. При этом та же постановка
проходит и в соседних строках той же группы, и в одиннадцати других прогонах тех же бинарей. Вывод
для клиента и для диагностики: по коду 54 различить эти два случая НЕЛЬЗЯ; различает их только разбор
входа, который сервер делает сам и публикует (`profile_area_unavailable`, `profile_input_check`), и
снимок отказа. Декодирование самой пары (54, «Невозможно выполнить операцию») остаётся неподтверждённым
— см. абзац выше.

**Гипотеза «отказ приходит в окне сразу после закрытия второго экземпляра» — НЕ ПОДТВЕРЖДЕНА (наряд
NEST_SECOND_INSTANCE).** Наблюдение, из которого она выросла: перед отказами `NEST.01/02` в основном
приложении был поднят и закрыт ВТОРОЙ экземпляр КОМПАС (`IMG.23`), и через 1,3 с отказало
выдавливание. Проверено опытом, где выдавливания идут СРАЗУ после отключения второго экземпляра, без
пауз прибора: **378 попыток за 126 циклов, ноль отказов**, и контроль паузой ТОЙ ЖЕ длительности —
**60 попыток, ноль отказов**. Различие долей ровно нулевое. Задержка от `disconnect` до первого
выдавливания — 0,076…1,044 с (у наблюдённого случая 1,32 с), возраст сеанса в опыте дошёл до 582 с
(у наблюдённого случая 450 с), то есть окно НАКРЫТО, а не пропущено. Ноль при 378 попытках исключает
частоту ≥0,8 % на попытку (95 %). **Следствие для диагностики: по этому признаку отказ не объясняется.**
Что опыт НЕ исключает и называет прямо: частоту ниже 0,8 % в самом окне; «событие раз за сеанс» (это
свойство ПРОГОНОВ, а не попыток внутри прогона); рабочую ветвь «рабочий документ основного приложения
свежий против отработанного» и предшествующую нагрузку групп — обе опытом не проверялись.

**Отказ задевает РАЗНЫЕ группы, а не только NEST.** Полные прогоны того же дня дали `GEOMETRY_FAILED` на
законных операциях в `MANIA.09`, `EX43`, `B3.07`, `NEST.02/03`. Это тот же класс нерегулярных
`Entity.Create()=false` (код 54), что и в исходном наблюдении, поэтому «свойство группы NEST» им
объяснить нельзя. Причина по-прежнему НЕ УСТАНОВЛЕНА; связь кластеров с измеренным снижением машины
(см. ниже) не установлена и не опровергнута.

**Снимок отказа пойман и несёт 17 ключей из 17.** `NEST.02.create` в полном прогоне №2:
`kompas_result_code` 54, `operation_ordinal` 610, `session_seconds` 498,14, `create_false_count` 3,
площадь профиля 7685,84073464102 (совпадает с аналитикой до последней цифры), документ свежий, эскиз
прочитан. У `NEST.02` порядковый номер 610 и счётчик 3 — те же, что в упавшем прогоне прошлого наряда;
совпадение названо, а не объяснено.

**Отдельно измерено: отказ «занятый файл» может не дойти до клиента за бюджет синхронного ответа.**
Строка `CB9.7` держит файл открытым и ждёт `FILE_LOCKED`. Латентность этого отказа (журнал Хоста,
`kompas_open_document`) в один и тот же день: **368/379 мс** утром, **558 мс** в двух полных прогонах,
**15 012/7 246 мс** сразу после опыта со вторым экземпляром, **1108…1696 мс** через пять минут,
**15 002…15 005 мс** внутри полных прогонов. Задержку даёт запрос владельца файла через Restart Manager
(`Com/FileLockOwner.TryFind`) — он и есть почти вся латентность отказа. Когда Worker не укладывается в
`sync_budget_ms = 15000`, Хост отвечает `OperationStatus.Running` с предупреждением «повторите тот же
вызов с тем же `operation_id`» (`ToolInvoker.cs:530–548`), и `IsError` при этом `false`. **INVARIANT для
клиента: `status = running` — это НЕ успех; исход читается повтором того же вызова с тем же
`operation_id`.** Прибор такого повтора не делает, поэтому строка `CB9.7` падает на длинном ответе —
дефект ПРИБОРА, названный, а не превращённый в факт о продукте. Связь роста латентности с 126 запусками
второго экземпляра по времени совпадает, но НЕ УСТАНОВЛЕНА.

**Где ещё стоило бы это применить** (вне этого наряда, названо, а не сделано): все прочие точки
`Create()`/`Update()`/`RebuildDocument()`, отказ которых сейчас объясняется только отсутствием
геометрии — скругление, фаска, отверстие, оболочка, кинематический, по сечениям, массивы, булевы
операции. Снимок отказа внедрён только у выдавливания, и код ошибки — там же.

**Что читается вместо причины, когда её нет** (`FeatureCreateFailure.Snapshot`,
`Api5Session.ObserveRefusedCreate`): параметры, переданные в определение (`direction_type`,
`end_condition`, `depth_mm`, `draft_mm`, `target_body_ref`, плоскость эскиза в той же формулировке,
что несёт ответ `kompas_create_sketch`); состояние документа (`feature_count`, `body_count`); состояние
эскиза (`ISketch.ConstraintsState` тем же маршрутом, что `kompas_get_sketch_status`); профиль,
нарисованный этим сеансом (число примитивов и аналитическая площадь с причиной её отсутствия); код и
текст ошибки КОМПАС; возраст сеанса и порядковый номер выдавливания.

**INVARIANT.** Набор ключей ОДИН И ТОТ ЖЕ на каждом отказе; непрочитанное значение — строка с
причиной, а не `null` и не пропущенный ключ. Различие «не задано» (тело не выбиралось) и «не
прочитано» (маршрут не ответил) сохранено: это разные утверждения о разных вещах. Ключей 17.

**`ksFeature.objectError` и `IsValid()` документированы — вопрос закрыт.** Обе страницы есть в том же
справочнике SDK: `objectError` (<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksfeature_objecterror.html>) —
«Признак ошибки объекта», тип `long`, синтаксис Automation `objectError = object.objectError` (или
`object.GetObjectError()`), только чтение; `IsValid`
(<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksfeature_isvalid.html>) — «IsVIalid — Получить индикатор
доступности объекта», `BOOL IsValid();`, «Метод позволяет получить информацию о том, что элемент
дерева построения доступен для редактирования». Поэтому чтение `object_error`/`is_valid` в
`kompas_get_feature` из контракта НЕ удаляется: оно опирается на документированные страницы.

**Порядковый номер считает ВЫДАВЛИВАНИЯ, а не команды** (`_extrudeOrdinal`), а возраст сеанса — от
создания `Api5Session`, то есть от старта процесса Worker. Обе величины сравнимы только между
прогонами этого прибора, и это названо в самом ответе.

**Отказ упавшей мутации обязан нести ревизию.** Было: `kompas_extrude`, отказавший
`NO_GEOMETRY_CHANGE`, вернул `revision_after = null`, и следующий вызов с прежней ревизией получил
`REVISION_CONFLICT` («ожидалась 6, фактически 7»), потому что признак СОЗДАН и остался в дереве, а
модель изменилась. Прежняя запись называла это «клиент обязан перечитать контекст» — то есть
перекладывала работу на клиента, тогда как продукт знал ревизию и не сообщал её. Теперь: `TaggedAfter`
(единственная точка, через которую проходят мутации) кладёт `document.Revision` в `details.revision_after`
при отказе, а конверт читает ревизию из результата, затем из `details` ошибки
(`EnvelopeRevisions.After`). Правило: если отказ оставил изменения (`partial_effects = true`) — ответ
несёт ФАКТИЧЕСКУЮ ревизию после операции; если откат прошёл — ревизию после отката (модель в памяти
КОМПАСа не откатывается, поэтому это та же текущая ревизия). `null` остаётся только там, где ревизия
не названа вовсе (отказ до COM, обрыв связи).

## <a id="body-ref-lifetime"></a>Срок жизни ссылки на тело (наряд ENTITY_CREATE_FALSE_FOLLOWUP)

**Дефект: ссылка на тело менялась без изменения модели.** Измерено на сырых ответах клиента: два
`kompas_list_bodies` подряд на ОДНОЙ ревизии 12 вернули РАЗНЫЕ `body_ref` для тех же тел. Причина —
`ListBodies` (и `ReadSolidBodies`) порождали ссылку через `References.Register("body", …)`, то есть
`body:<uuid>` заново на каждый вызов, тогда как эскизы уже шли через `ReferenceForObject`
(одна живая ссылка на объект на ревизию, `adapter-sketch.md#one-live-reference`). Ссылка, меняющаяся
без изменения модели, — нарушение контракта ссылок: клиент держит два разных имени одного тела.

**Исправлено:** оба чтения тел идут через `ReferenceForObject("body", …)`, то есть на одной ревизии
одно тело даёт одну и ту же строку. Живые строки — `EC9.11`.

**Это КОНТРАКТ, и вот правило версий.** Формат строки не меняется (`body:<uuid>`), но меняется её
СМЫСЛ: строка перестаёт быть «новой на каждый вызов» и становится адресом тела, устойчивым на
ревизии. По разделу «Версии и выпуски» `AGENTS.md` смена смысла поля ответа — это MAJOR, а до
`1.0.0` — MINOR; **номер этим нарядом НЕ поднимается** (прямое указание §2.6 наряда, `<Version>` =
0.6.0). Накопленное изменение войдёт в очередной выпуск, и его заметки обязаны назвать это правило:
ссылка на тело живёт до перечитывания модели, а на одной ревизии она одна и та же. Обратная
совместимость для клиента — положительная: адрес, работавший до правки, работает и после неё.

**Срок жизни.** Ссылка на тело живёт, пока документ не перечитан: `kompas_rebuild` (и открытие,
перезагрузка, восстановление) отзывают её (`RevisionForward(invalidateAll: true)`), и попытка
воспользоваться ею даёт `STALE_REFERENCE`, а не молча другую цель — измерено `EC9.12`, `B3.16`.
Обычная мутация ссылку перештамповывает (её держат, потому что клиент может адресовать ею следующую
операцию).

**Защита от неверной адресации — проверена и НЕ внедрена (измеренная цена).** Сопоставление ссылки с
телом идёт по `IUnknown` (`ResolveBodyTarget` → `MatchBodyIndexByPointer`). Этого достаточно против
«объекта больше нет», но НЕ против переиспользованного указателя: тот же адрес может достаться другому
телу, и тогда ссылка молча ушла бы в него. Наряд предлагал закрыть это сверкой ГАБАРИТА, записанного
при выдаче ссылки. Проверка была написана и ИЗМЕРЕНА на полном прогоне — она отвергает ЗАКОННУЮ
работу: клиент, который сдвинул тело и затем адресует ТО ЖЕ тело той же ссылкой, получает
`STALE_REFERENCE` (`target_body_gabarit_changed`), потому что габарит тела законно изменился. Так
упали строки `B3.56` (`translate` → `rotate` того же тела), `B3.59` (`translate_then_rotate`) и — тем
же отказом в предыдущем шаге — `FL05`/`FL06`, `BG20`, `MANIA.07`: **6 FAIL из 1179 строк** против
0 в трёх прежних полных прогонах. Отличить «тело изменилось» от «указатель переиспользован» без
документированного устойчивого идентификатора тела нельзя, поэтому проверка СНЯТА, а не оставлена
отвергающей верные вызовы. Решение о ней — за заказчиком; числа и тексты отказов — в отчёте наряда.
**Остаётся:** устаревшая ссылка после `kompas_rebuild` отвергается (`RevisionForward(invalidateAll:
true)` отзывает её) — измерено `EC9.12`; ссылка, пережившая мутацию, продолжает адресовать ТО ЖЕ тело
по `IUnknown` — измерено `EC9.14`.

**Документированный маршрут для устойчивого идентификатора тела НЕ найден, но найден маршрут
ИНТЕРПРЕТАЦИИ кода ошибки.** `IKompasError` (`GetCode`, `GetDescription`, `IsError3D` —
<https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksapi_ikompaserror_methods.html>) есть в
`Interop.KompasAPI7.dll` и документирован; `IsError3D` отвечает, к какой из двух таблиц относится
сохранённая ошибка. Это отдельный наряд: код, прочитанный `ksReturnResult`, сам по себе не декодируется
(см. `create-false-snapshot`).

**Гипотеза OBS-019.** «Устаревшая ссылка указала на другое тело и вызвала `Entity.Create=false`» —
НЕ ПОДТВЕРЖДЕНА: отказ не воспроизвёлся ни по конфигурации клиента, ни полным прогоном (см. отчёт
`ENTITY_CREATE_FALSE_REPORT_20261009.md`, раздел «Доводка», §1). Различие двух чтений было измерено
как разница СТРОК ссылок, а не как другое тело; тело в том прогоне опознавалось прибором по позиции в
коллекции, и это дефект прибора, а не факт о продукте.

**Опознание по габариту в строках `EC9` оставлено контролем**, а не заменой адресации: строка
`EC9.13` адресует тело ССЫЛКОЙ, как клиент, и проверяет, что материал снят именно с заявленного тела,
а постороннее тело не изменилось.
