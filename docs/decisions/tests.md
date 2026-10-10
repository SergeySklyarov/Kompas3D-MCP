# Тесты (Unit) - решения и измерения

Модуль: `tests/Unit/KompasMcp.Unit/`. Здесь - история правок, вынесенная из кода тестов.
Действующие правила остались в коде под метками `INVARIANT:` / `MEASURED:` / `LIMIT:` / `TEST:` /
`DOC:` / `ASSUMPTION:`; сюда переехало «прежде было…». Факты не переформулированы.

## <a id="api5-visibility"></a>Видимость сеанса API5 (поручение 12.09.2026, п. 7)

**Что было.** Прежняя реализация выводила видимость из доступности PID: `ProcessIdOf(...) is not
null` давало ложноположительный `visible=true` на скрытое окно, у которого HWND есть.

**Что решено.** Видимость опирается на наблюдение окна (`IsWindowVisible`), а не на косвенный
признак. Запрет на `ZoomPrevNextOrAll` и `ksZoom*` держится статически, потому что камерного
состояния у 3D-документа в API5 спросить нельзя.

## <a id="component-identity"></a>Тождество адреса компонента (05.10.2026)

**Что было.** До 05.10.2026 правило сверки жило разложенным по ветвям адаптера
(`Api5Session.IdentityMatches`): каждая правка признака ломала соседний случай, а регрессия была
невидима модульным тестам.

**Что решено.** Правило вынесено в чистую функцию и закреплено таблицей (находки §1 и §3 задания
05.10.2026).

## <a id="command-budget"></a>Бюджеты команды (дефект M11, 05.10.2026)

**Что было.** Бюджет Хоста задавался одной настройкой (120 с по умолчанию), а бюджеты Worker -
отдельным `switch` в диспетчере (180–300 с). Хост объявлял `OUTCOME_UNKNOWN` и ломал канал раньше,
чем Worker доходил до своего предела; следующий вызов убивал ещё работающий Worker.

**Что решено.** Бюджет Хоста выводится из бюджета Worker на общей таблице.

## <a id="control-copy"></a>Восстановление из контрольной копии (дефект H4, 05.10.2026)

**Что было.** Дефект состоял из двух частей: (1) восстановление шло в обход режима доступа и
перезаписывало файл документа, открытого `access=read_only`; (2) восстановление выполнялось и на
отказах, которые до COM не доходили, - сервер писал в пользовательский файл без причины.

**Что решено.** Восстановление запрещено для read_only и для чистых отказов до COM.

## <a id="cut-plane-contract"></a>Форма плоскости: опубликованная схема и DTO (B3, 19.09.2026)

**Что было.** Дефект `PLANE-BASE-DECLARED-REFUSAL-UNREACHABLE` (клиентская приёмка B3, три строки
FAIL). Схема поставки публиковала `plane.base` строкой `xy|xz|yz` и `plane.offset_mm` числом, а DTO
ждал ОБЪЕКТ - форма расходилась на один уровень вложенности. Вызов по опубликованной схеме падал на
разборе payload (`JsonException` по `$.plane.base`), и объявленный `CAPABILITY_UNAVAILABLE` был
недостижим.

**Что решено.** Форма приведена к строке; дискриминирующий контроль - вложенный объект обязан
отвергаться схемой.

## <a id="document-save"></a>Сохранённость документа

**Что было.** Отпечаток последней наблюдаемой ревизии писался и мутацией, и сохранением, поэтому
«отпечатки равны» получалось сразу после изменения модели: `close(refuse)` закрывал изменённый
документ, а `close(save)` не сохранял.

**Что решено.** Мутация не объявляет документ сохранённым; сохранённым объявляет только
подтверждённая запись.

## <a id="euler-tests"></a>Тесты углов Эйлера (проба --reposition-params, шаг RP.25)

**Что измерено.** Порядок спряжения и единицы измерены пробой `--reposition-params` (прогон
`a336120926fc4652a8bf737562568271`, шаг RP.25): совпало РОВНО ОДНО произведение из шести - `PNR`;
расхождение 0 на всех трёх составных постановках, у остальных пяти - 1. Тройки `(0,0,90)` (ось Z
через (5,0,0)) и `(90,90,0)` (ось (1,1,1)/√3 на 120°) прочитаны ИЗ ДОКУМЕНТА.

**Что решено.** Проверка - эквивалентностью матриц, потому что параметризация при нутации 0/180°
неоднозначна.

## <a id="euler-tests-pole"></a>Полюс параметризации при нутации 180° (строка B3.59)

**Что было.** Знак вращения на полюсе брался по формуле соседнего соглашения. Продукт отказывал
`GEOMETRY_FAILED` с расхождением 2: собственная проверка записи не могла воспроизвести размещение из
прочитанных углов.

**Что измерено.** Дефект найден строкой приёмки B3.59, а не модульными тестами: прежние шесть поз
проверяли либо нутацию 0, либо координатную ось, и полюс при 180° не покрывался ни одной из них.

## <a id="hole-edit"></a>Правка родного отверстия (наряд SM07 §3.4, 20.09.2026)

**Что было (измеренные дефекты).**

- Первая поставка с веткой отверстия: правка ГЛУХОГО отверстия отвергалась `INVALID_ARGUMENT` до
  COM, потому что `depth_mm` - поле выдавливания в таблице ролей и одновременно СВОЁ поле режима
  `blind_flat`. Отказ был на строке, которая обязана проходить; нашла его приёмка
  (F08.15/16/19/20.edit), а не чтение кода.
- `keep_side` у разделения (П5) и `couplings` у фаски (измерено 20.09.2026) принимались и
  проглатывались: чужие семейства их не отвергали.

**Что решено.** Ветка отверстия опознаётся по типу дерева (583) до чтения определения API5; каждое
поле правки отверстия отвергается чужими семействами; производная глубина зенковки не объявлена
записываемой.

## <a id="host-ownership"></a>Модель владельца сеанса (04.10.2026)

**Что было.** Старая модель захватывала владение на старте транспорта и считала `draining`
разрешением взять сеанс. Оба утверждения измеренно ложны: `tools/list` вспомогательного обнаружения
занимал сеанс, а владение, помеченное `draining` до подтверждённой очистки, отдавалось при живом
Worker.

**Что решено.** Старт владения не берёт; «освобождается» не равно «можно взять»; явный release
запрещает неявный захват.

## <a id="ipc-channel"></a>Один читатель канала Host–Worker (18.09.2026; FIX H1, 05.10.2026)

**Что было.** До 18.09.2026 каждый вызывающий вёл свой цикл чтения, поэтому два одновременных вызова
читали поток конкурентно, перемешивали байты и давали `WORKER_UNRESPONSIVE - Недопустимая длина кадра
1919951483 байт` (четыре байта были `{"pr`) и `JsonException: 'o' is an invalid start of a value`.
Измерено со стороны клиента WorkBuddy, который шлёт вызовы параллельно; приёмка звала инструменты по
одному и путь не задевала.

**FIX H1 (05.10.2026).** Прежде любая `OperationCanceledException` с токеном клиента выходила наружу,
и вызывающий писал терминальное `cancelled` без требования согласования: клиент получал «команда
отменена в очереди Host и не отправлялась в KOMPAS», хотя Worker уже выполнял команду. Поверивший
клиент повторял мутацию с НОВЫМ `operation_id` - и мутация применялась дважды.

**Что решено.** Один сериализованный читатель; отмена после записи кадра мутации - `OUTCOME_UNKNOWN`,
а не `cancelled`.

## <a id="journal-tests"></a>Тесты журнала: дефекты и правки

**Что было.**

- (R1, 21.09.2026) Путь чтения (`File.ReadLines` с `FileShare.Read`) запрещал запись живого писателя;
  второй Хост падал необработанным `IOException` до старта транспорта, и клиент остался без
  инструментов.
- (R2) Ручка на запись держалась всю жизнь процесса.
- (FIX A) При недоступной межпроцессной блокировке строка всё равно писалась, а лишь выставлялся
  диагностический флаг - гарантия подменялась наблюдением.
- (FIX A, терминальная запись) Строка исхода могла не лечь после выполненной мутации, а исход
  объявлялся записанным.
- (FIX H2) Рваный хвост только отмечался; первая же запись нового процесса склеивалась с обрывком,
  намерение терялось, и повтор с тем же `operation_id` выполнял мутацию ВТОРОЙ раз.
- (FIX §4, 05.10.2026) Починка без блокировки дописывала `\n` в середину строки, которую в этот
  момент писал другой Хост.
- (Повтор во время выполнения, 04.10.2026) Журнал отвечал `Proceed=true` на незавершённую запись;
  повтор `kompas_create_document` создал два документа.
- (FIX M1) Политика `SameOperationId` записанного отказа была невыполнима: любая запись `failed`
  воспроизводилась всегда.

**Что решено.** См. метки в `JournalAndQueueTests` и `docs/decisions/journaling.md`.

## <a id="release-guard"></a>Решение об освобождении сеанса (дефект H3, 05.10.2026)

**Что было.** Правило жило разложенным по ветвям `HostSession.ReleaseAsync`, и один из обходов
(промежуточный CAD-вызов, поднявший новый Worker с пустой описью) через эти ветви проскакивал. Прежде
подтверждение неизвестного состояния снимало только шаг 1, а шаг 3 («канал сломан») всё равно
отказывал; единственным выходом был обходной шаг - сделать CAD-вызов, чтобы Worker перезапустился, и
повторить.

**Что решено.** Правило вынесено в чистую функцию; подтверждение неизвестного состояния снимает
отказы по сломанному каналу и по непрочитанной описи.

## <a id="rotation-contract"></a>Границы вращения (измерения 18.09.2026)

**Что было.** Прежний предел `angle_deg` стоял на опровергнутой посылке «развёртка насыщается на
180°»; опыт F.1 получил полный цилиндр `π·r²·h` при `Angle[true]=360`. Прежнее описание утверждало,
что полный оборот вырезанием НЕ выражается и что бобышка к непустой детали отвергается, - оба
утверждения опровергнуты опытами F.9/F.10 и приёмкой RO.18/RO.19: «насыщение» было следствием
односторонней заготовки, а «бобышка даёт второе тело» - следствием прибора, писавшего NewBody.

**Что решено.** Граница - 360; правка угла вращения идёт полем `rotation_angle_deg`, а не `angle_deg`.

## <a id="safe-read"></a>Непрочитанное COM-значение (FIX C)

**Что было.** Прежний помощник возвращал `default`, и «чтение не состоялось» было неотличимо от
«прочитано значение по умолчанию»: `default(Sample) = First` печаталось как `"First"`.

**Что решено.** Непрочитанное значение отличается от успешно прочитанного `false`/`0`/первого
значения перечисления.

## <a id="solid-feature"></a>Классификация полей правки B3 и номера типов

**Что было / что измерено.**

- 19.09.2026: добавлено `target_body_ref` (26 → 27) - область применения признака отсечения на ПРАВКЕ.
- Очередь B4: добавлено `pattern` (27 → 28); строка числа и роль поля не были обновлены, и обе
  проверки падали с тех пор.
- 20.09.2026: пять полей правки очереди B5 - `shift_mode`, `section_refs`, `thickness_mm`,
  `thin_inward`, `face_refs` (28 → 33). Шаг C наряда B1–B5 §3.2 добавил шестое - `couplings`
  (33 → 34). Прежнее число 33 было НЕВЕРНО с момента появления `couplings`: проверка падала раньше,
  на неотнесённом поле. Зонд `scratch/_couplings_scope_probe.py` показал, что вызов «`distance1_mm` +
  `couplings`» на фаске дал успех и объём 79840 → 79955, тогда как `shift_mode` и `section_refs` в том
  же вызове отвергались `INVALID_ARGUMENT`.
- 20.09.2026, наряд SM07 §3.2: шесть полей правки родного отверстия (34 → 40), шаг M.6 зонда
  `scratch/_hole_edit_probe.py`.
- Номера типов дерева измерены прибором `scratch/b3-measure-feature-types.py`: у трёх семейств из
  четырёх номер ФАБРИКИ и номер ДЕРЕВА различаются (вращение 29 = 29 вопреки аналогии с отверстием;
  отверстие 52 → 583). Число 569 (`o3d_BodyReposition`, номер СОЗДАНИЯ) - исправленный дефект;
  признак в дереве виден под 79.
- Дефект П6: правило «ожидание объявлено» было записано ДВАЖДЫ; копии разошлись (булева правка
  требовала ожидания, перенос - именно ОБЪЁМ), и объявленный и совпавший габарит отчитывался как
  необъявленный.

## <a id="tool-catalog"></a>Инструмент каталога обязан быть вызываемым

**Что было.** Два дефекта сразу: запись, объявленная через `Mutation()` с `requiresOperationId:
false`, не публиковала поле, которое Хост затем проводил через журнал; а `IsMutation` есть
`Destructive || RequiresOperationId`, поэтому журнал получал null `operation_id` и вызов падал внутри
Хоста `ArgumentNullException` до KOMPAS. Невидимо для всех прогонов приёмки, пока тест не вызвал
`kompas_read_topology` по MCP.

**Правило уточнено 04.10.2026.** «Не-мутация не объявляет `operation_id` вовсе» стало «объявлено,
только если инструмент САМ воспроизводит исход повтора»: `kompas_release_session` обязан объявить
поле (строка S03b требует его при `destructiveHint=true`), но журнала не пишет.

## <a id="typed-com"></a>Граница типизированного COM (проба E, 2026-09-12)

**Что измерено.** Запись `IExtrusion.Sketch` через `IDispatch` в общем процессе роняла его кодом
0xC0000409, а в изолированном - нет; та же запись молча не сохраняла значение и перечитывалась
прежним объектом. Молчаливо непринятая запись, возвращающая S_OK, хуже отказа тем, что приёмка
посчитала бы её успехом.

**Что решено.** Позднее связывание остаётся только в `tools/KompasMcp.Api7Probe` и в `src/` не
переносится.

## <a id="solid-feature-2"></a>Классификация полей правки B3: вынесенная проза

Вынесено из `SolidFeatureClassificationTests` дословно; в коде остались короткие метки
`INVARIANT:` / `MEASURED:` / `TEST:` / `LIMIT:` и одна строка `History:`.

> Classification of the B3 feature-edit fields and addressing by tree type number - what ties the contract, the published schema and the adapter together, which an acceptance run does not replace.
> TEST: two ways of checking. What lives in the contract and catalog (`UpdateFeatureCommand`, the tool schema) is checked BY VALUE - the types are available to the test process. What lives in the adapter is checked BY SOURCE: the adapter assembly does not load without KOMPAS installed, because its interop types are deliberately not copied to the output (`Private=false` in `build/KompasInterop.props`).
> LIMIT: these checks hold the CONSISTENCY of the three places and do not prove that a foreign-field refusal reaches the client - that is proved by acceptance (row `B3.28` sends `plane + keep_side` to a split feature and gets `INVALID_ARGUMENT` with `foreign_fields == ["keep_side"]` on a live model).
> History: docs/decisions/tests.md#solid-feature
>
> Adapter source without comments. Comments are excluded deliberately: the parsed text also describes the parse itself, and without stripping them the test would find names in an explanation, not in the code.
> Which part of `Api5Session` is parsed. Several on purpose: the family-field table is in `Api5Session.SolidOps.cs` and the tree type numbers are in `Api5Session.cs`; merging them would let the test find a name in a foreign file.
> Expectation: what the command fields are for the B3 families. Listed COMPLETELY here, and the listing is the subject of the check, not its decoration: a divergence from the contract and the schema is caught below.
> Fields assigned to families: schema name → owning families.
> INVARIANT: pattern belongs to the pattern family, which READS it; other families reject it via their own branch that returns before the rest - "family", not "not applicable".
> INVARIANT (order SM07 §3.2, queue B2): six native-hole edit fields. Role "family", not "not applicable" - the hole branch READS them, and other families reject them via the shared table (SolidOps.cs). The volume-delta expectation belongs to THIS family deliberately: only the hole reads it, so declaring it a shared expectation would accept it on a translation and a boolean and silently not apply it (same class as keep_side, P5).
> Command fields not belonging to the B3 families: extrusion, chamfer, fillet, rotation and the three families of the last mandatory queue B5 (kinematics, sections, shell). Rejected by a separate adapter check as NOT APPLICABLE to a B3 feature.
> INVARIANT (queue B5 §11): these fields are not assigned to B3 families, so they must be rejected as not applicable rather than reach a family that does not read them.
> MEASURED (order B1–B5 §3.2, step C): queue B5 added SIX editable fields; the sixth, couplings, was in neither this list nor the adapter guard, and the completeness check failed on exactly that. The role is set by EXPERIMENT, not convenience: probe scratch/_couplings_scope_probe.py showed a call "distance1_mm + couplings" on a chamfer returned success and volume 79840 → 79955 (the edit applied, no chain), while shift_mode and section_refs in the same call were rejected INVALID_ARGUMENT. The adapter guard (SolidOps.cs, Features.cs, Rotated.cs, PatternEdit.cs) now names the same field, so "not applicable" here is verified behaviour, not a test note.
> Addressing fields: they select the feature, they do not define it.
> Analytic geometry expectations. They belong to no family deliberately: the same expectation is declared for a translation and a boolean edit, so assigning them to a family would forbid a legal call.
> Schema name → C# property name, per the product's OWN naming policy.
> Parsed adapter table: field name → family names (already as contract strings).
> The family constants live in DIFFERENT adapter files: the family-field table in SolidOps.cs, the pattern-family constant in PatternEdit.cs (queue B4), the hole-family constant in Hole.cs (order SM07, queue B2). Looking for them in one file would demand moving a constant for the test. The table itself is parsed ONLY from SolidOps.cs: extending the parse to a second file would let the test find an entry in a foreign place.
> Discriminating control of the check itself: the table must not only contain the expected but also NOT contain extras. A table with a field added "just in case" would refuse a legal call - rejecting an applicable field as foreign.
> Catches a typo and a rename: a field named in the table by a string absent from the contract would reject nothing - it simply never occurs in a command.
> MEASURED defect class (§9.1 P4): a field exists in the contract and the adapter but is not declared in the schema, so the Host with additionalProperties:false rejects the call before COM and the client sees "unsupported" where everything is implemented. This happened with base_object_refs on the fillet edge-set reduction: the route and currency were ready, the publication was missing.
> Expectations and addressing even more so: without them the edit does not run at all.
> The main completeness check: every command field must have a ROLE. A field in none of the four departments is one the adapter rejects neither as foreign to a family nor as not applicable, i.e. it accepts and silently ignores it. This is how keep_side behaved until it was assigned to split (defect P5): the call passed but the parameter was not applied.
> A new contract field will fail this check - and that is its purpose: the author must decide its role rather than leave the default.
> INVARIANT: completeness also fixes the contract size - growth of the field count shows as this line failing. The count is recounted from the contract, never fitted to a run. It reached 40 with the six native-hole edit fields (order SM07 §3.2), each on a measured route (step M.6 of probe scratch/_hole_edit_probe.py). History: docs/decisions/tests.md#solid-feature
> The check reads the REFUSAL CONDITION, not a list in a comment: what matters is that the adapter rejects exactly these fields. The reverse half (does not reject family fields) is the discriminating control - without it the check would also pass on a condition rejecting everything.
> The condition starts with a DOUBLE paren since 20.09.2026 (order SM07 §3.2): the first field became conditional - `command.DepthMm is not null && !Owned("depth_mm")` - because depth_mm belongs to both extrusion and a blind hole. The regex was updated TOGETHER with the condition, and that is not cosmetics: the old regex simply would not find the condition and would fail the test.
> There is exactly one exception, named explicitly: depth_mm is rejected as not applicable IF the family did not declare it its own. Otherwise the check below would pass on a condition that always rejects depth_mm - which is exactly how the blind-hole edit failed (measured by rows F08.15/16/19/20.edit, 20.09.2026).
> MEASURED by probe scratch/b3-measure-feature-types.py (reading ksEntity.type in the tree via kompas_list_features), not inferred from the vendor enum: in three of four families the FACTORY number and the TREE number differ - measured separately on rotation (29 = 29, against the hole analogy) and on the hole (52 → 583).
> Discriminating control against the very number that suggests itself: 569 - o3d_BodyReposition, the CREATION number. The feature appears in the tree under 79, and a search for 569 would never find it - this is a fixed defect that must not return under the guise of a "refinement".
> The type is an addressing sign, so two families with one number would merge into one department and the edit would hit the wrong feature.
> Root cause of defect P6: the rule "an expectation is declared" was written TWICE. The copies diverged - the boolean edit required an expectation, the translation required specifically VOLUME, so a declared and matching bounding box was reported as undeclared. While the expression stands in one place it has nothing to diverge from; this test holds exactly the uniqueness.

## <a id="journal-tests-2"></a>Тесты журнала: вынесенная проза (продолжение)

Вынесено из `JournalAndQueueTests` дословно; в коде остались метки `INVARIANT:` / `MEASURED:`.

> Idempotency and crash semantics (spec 1.8). The interesting assertions are the ones about what must NOT happen: no second COM call on replay, and no "safe to retry" after a crash.
> History: docs/decisions/tests.md#journal-tests
> INVARIANT: a replay while in flight does NOT allow a second dispatch. MEASURED 04.10.2026: the old journal answered `Proceed=true` to an unfinished record, and a repeated `kompas_create_document` created two documents.
> Read a file the server may still hold open. File.ReadAllText asks for FileShare.Read, which Windows refuses against a live writer handle - so anything observing the journal or the logs while the server runs must open with FileShare.ReadWrite. The rule still holds for the HOST log, which keeps a handle. The "readable while a writer is live" check is a separate test - `SecondInstance_ReadsTheJournalWhileTheFirstIsWriting`.
> INVARIANT (order R1): a journal opened for writing by ANOTHER instance is readable.
> MEASURED (21.09.2026): the read path (`File.ReadLines` with `FileShare.Read`) forbade the write a live writer held, and the second Host died with an unhandled `IOException` before transport start. INVARIANT: a second reader cannot know whether the writer is alive, so an unfinished record reads as `JournalOutcome.OutcomeUnknown` with a reconciliation demand - the crash-recovery rule, unweakened; this is why the journal owner must be one (R3). History: docs/decisions/tests.md#journal-tests
> INVARIANT (order R2): the writes of two instances do not tear each other's lines.
> The write handle is no longer held for the process lifetime: each write is open-append-close in one call. The result is checked, not "the code looks shared": 400 records from two independent instances, read by a third, with no unparsed and no lost line.
> INVARIANT (FIX A): with the cross-process lock unavailable the journal write does NOT happen - no intent recorded, no in-memory operation, no command leaves, and the refusal is NAMED. History: docs/decisions/tests.md#journal-tests
> INVARIANT (FIX A, terminal write): if the outcome line did not land AFTER a completed mutation, the outcome cannot be declared recorded - in memory it is marked for reconciliation, the caller gets `false`, and the durable journal still says `in_flight`.
> INVARIANT (FIX §4, 05.10.2026): a torn tail is repaired ONLY under an acquired lock. While another writer holds the lock the repair does NOT run - else the appended newline would land inside a foreign intact record. The first write repairs the tail under ITS OWN lock. History: docs/decisions/tests.md#journal-tests
> The Host admits a mutation through this queue and then dispatches it to the Worker directly - the queue is the counter of outstanding CAD work, not the thing that executes it. A served command must therefore release its slot, or `capacity` stops meaning "concurrently waiting" and becomes a lifetime budget of mutations per process. That is not a hypothetical: the acceptance run grew past 64 mutations in one session and every later call died with QUEUE_FULL while the Worker was idle.

## <a id="hole-edit-2"></a>Правка родного отверстия: вынесенная проза (продолжение)

Вынесено из `HoleEditClassificationTests` дословно; в коде остались метки `INVARIANT:` / `MEASURED:` / `LIMIT:`.

> Editing a native hole (order SM07 §3.4): three properties the acceptance run does NOT hold, while a disagreement between contract, schema and adapter is caught here.
> INVARIANT, three claims acceptance cannot prove (it proves behaviour on a live model, not the agreement of the three places): (1) the hole feature is recognised in the edit dispatcher BY TREE TYPE, and the branch stands before reading the API5 definition - a hole has no definition at all, so the reverse order would make the edit unreachable; (2) every hole-edit field is rejected by the neighbouring families - otherwise it is accepted and swallowed (measured class: `keep_side` P5, `couplings` 20.09.2026); (3) the derived countersink depth is NOT declared writable and NOT asserted to match the requested one - with the "diameter + angle" method a write into it has no effect (M.3), so requiring a match would demand a false claim from acceptance.
> LIMIT: the check is by SOURCE, not by assembly - the adapter assembly does not load without KOMPAS installed (interop types are not copied to the output, `Private=false` in `build/KompasInterop.props`). Comments are stripped from the parsed text, else the test would find the name in an explanation, not in the code.
> History: docs/decisions/tests.md#hole-edit
> Method body by signature: from it to the next member of the same nesting level.
> INVARIANT: the parse is limited to ONE method. The same file has a READ route (`GetFeature`) where `entity.GetDefinition()` is on the first line, and a whole-file search would find exactly that one: the test would then assert line order in a foreign method.
> Two halves of one cause. First: recognition by the entity type in the tree - as in reading (FindHoleEntity), not by the creation factory number: 52 at creation, 583 in the tree (probe N.1), and a search for 52 would never find the feature. Second: the branch must stand BEFORE reading the API5 definition - a hole has no definition at all (type ksHoleDefinition does not exist in the vendor interop), so recognition through a definition would be impossible.
> Discriminating control: the constant must be the measured tree number, not the factory number - otherwise the test would also pass on recognition by 52.
> INVARIANT: a field in none of the foreign-field lists is one the adapter accepts and does NOT apply - another family does not read it and there is no shared guard for it. This is how keep_side on split (P5) and couplings on chamfer (measured 20.09.2026) behaved.
> MEASURED (at CREATION, same class as a zero chamfer leg, F.12): KOMPAS accepts a zero diameter and builds a feature with no material at unchanged volume. So on edit positivity is checked before COM, not left to the kernel - "accepted" there does not mean "applied". EVERY writable numeric field is checked by name, not "there is some check in the file": a missed field is a field without a check.
> MEASURED defect of the first delivery with the hole branch (20.09.2026): editing a BLIND hole was refused with INVALID_ARGUMENT before COM, because depth_mm is an extrusion field in the role table and at the same time an OWN field of the blind_flat mode. The refusal was on a line that must pass, found by acceptance (F08.15/16/19/20.edit), not by reading the code. Both halves of the exception and its discriminating control are held here.
> INVARIANT (lesson F-11): the feature address is GUARANTEED by the setup, not guessed. For a hole edit the setup is the hole's uniqueness in the document - with several, the correspondence "tree feature ↔ Holes3D entry" is unproved, and the call must be refused BEFORE the write. Both halves are checked: the refusal exists, and the address comes from the READ route - an index into IHoles3D via Api7Hole, not by iterating bodies or by name.
> Discriminating control: the address is NOT taken by indexing the collection by hand - such a write would bypass the only place where the address is proved.

## <a id="target-body-2"></a>Тело-цель выдавливания: вынесенная проза

Вынесено из `TargetBodyGuardTests` дословно.

> The two decisions about an extrusion's target body that do not need KOMPAS: which operations may name a body, and whether a drawn profile can plausibly lie over the body that was named.
> The second one exists because probe P2.6 measured a silent no-op: declaring a body the contour does not sit over makes SetSketch, Create and RebuildDocument all answer true while no body changes volume at all.
> A server that cannot see the contradiction must not be able to report the result as success, and the numbers below are the two configurations that measurement produced - a 100×80 plate at x∈[-50,50] and a blob at x∈[138,162] with a Ø10 contour over it.
> The axis correspondences asserted here are the ones measured by probe P2.4 and re-asserted by acceptance rows G07_xy/G07_xz/G07_yz, not a convention invented for the test.
> This is row A.5a of probe P2.6: declaring the plate while the contour sits over the blob. KOMPAS answered Create=true, RebuildDocument succeeded and ΔV was 0 on both bodies.
> Row A.5b: the same call shape with the body the contour actually lies over - and that one really did remove 250π from the blob and nothing from the plate.
> The probe's cut plane sat 10 mm ABOVE the material and the through cut still removed the full 250π. Requiring the body to straddle the sketch plane would refuse exactly the operation being measured, so the normal axis is deliberately not part of the test.
> docs/03 §3.3 gives 0.001 mm for coordinates; a contour that touches the body's boundary within that slack must not be accused of aiming elsewhere.
> The rectangle of acceptance row G07_xy (u=10..50, v=20..40) produced model min=(10,20,15) max=(50,40,21), i.e. x=u, y=v.
> G07_xz: the same rectangle gave min=(10,15,-40) max=(50,21,-20) - v lands on −z, so a body at positive z is not the one this contour lies over, whatever the plate's own y says.
> G07_yz: min=(-21,-40,-50) max=(-15,-20,-10) - u→−z, v→−y, and x is the normal.
> "cannot say" must never arrive as "refused": a sketch on a referenced plane has no measured correspondence here, and inventing one would reject work that KOMPAS does fine.
> An arc's true extent is inside the box of its circle. Over-approximating is the only safe direction for a test whose single job is to refuse: too small a box would accuse a legitimate target of being the wrong body.
> A polyline with a missing vertex would otherwise contribute a too-small rectangle, and a too-small profile box is the one thing that turns this check into a false refusal.

## <a id="sketch-status-2"></a>Статус эскиза: вынесенная проза

Вынесено из `SketchStatusConversionTests` дословно.

> Converting the raw `ksConstraintsStateEnum` into the published sketch-definiteness status.
> TEST: these tests check the PURE function `SketchStatusResult.FromRawState` and nothing else.
> LIMIT: there is no live KOMPAS here, so no run of this file confirms the read route - the route is confirmed by probe S (`docs/acceptance/api7/sketch-definition.json`), not by a mock value; the test on value 3 is deliberately written NOT to count as proof of reading the state on a live model (see `Redundancy_WithoutLiveVerification_StaysUnknown`).
> INVARIANT: three properties, each easy to lose on the next edit - `is_fully_defined` is nullable (false vs null are different answers); `degrees_of_freedom` is always null and never derived from the dimension count; an unknown enum value is not a success and not "under-defined" but an honest `unknown` with a reason.
> Minus, not "unknown": this is how KOMPAS answered for a free circle in S.5b.
> 0 is KOMPAS's "did not set" answer and is not equal to 2. Mixing them would declare an empty sketch (S.5b: exactly this case) under-defined without grounds.
> raw == null means the call did not arrive (or arrived but has no value). That is not the product's answer, and substituting 0 here would be an invention.
> The route (ISketch.ConstraintsState) returns a state, not a counter. The number of degrees of freedom is not summed from the dimension count: constraints tie objects together, so the sum of dimensions is not the number of remaining freedoms. A zero here would be a claim the measurement never made.
> The key honesty test. Value 3 is declared in ksConstraintsStateEnum but never appeared in a confirmed run on a live model (S.8: 46 shipped sketches), and no constraint-writing branch was found. So "we have a mock" is no ground to publish the state as measured.
> The raw value is kept: "an unknown enum value" and "KOMPAS answered 3" are different things.
> If a live check ever appears the conversion is already ready - and it stays needs_attention, not fully_defined: "needs attention" is not "+".
> A claim about state distinguishability: "!" cannot be reduced to "+", to "−", or to a generic unknown by field value.
> Strict prohibition from the statement: any unknown enum value → unknown, is_fully_defined=null, a diagnostic reason. Not "under-defined" and not "defined": the product said something the server does not understand, and it must be named.
> 0 and 42 give the same DefinitionStatus=Unknown but different Limitations and RawState. That is the point of keeping the raw value separate from the normalised one.
> The adapter passes in the transfer diagnostics and the DOF limitation. The function must not displace them: the client reads the answer, not the adapter's internals.
> The order matters to a human: first "which sketch and by which route it was read", then "why the status is what it is".
> "Is this sketch fully defined right now?" - exactly one state answers true. Everything else, including "not set", gives no right to say "yes".
> The mirror property, no less important: a "no" answer is allowed only where the product explicitly said "under-defined". "Unknown" does not turn into "no".

## <a id="sketch-point-2"></a>Вывод точки эскиза: вынесенная проза

Вынесено из `SketchPointDerivationTests` дословно.

> The decision layer behind the model-derived sketch coordinate: when a point read out of a dependent body may be used to point at an existing sketch primitive, and when the only honest answer is a refusal.
> Every case here is a boundary of one measurement. Probe G showed the derivation works for a base XY sketch with a circular profile consumed by a through cut - and the same probe states plainly that nothing else was measured.
> The tests below pin that boundary down so a later change cannot widen it by accident: the cost of widening wrongly is deleting the wrong sketch object on a model that cannot be rolled back.
> The numbers are the probe's own, which is what makes them worth asserting rather than inventing: a 100×80×10 plate with a Ø20 through hole measures V = 76858.4073464102, and the replacement to R12 measures 75476.1065788307 with a lateral surface of 753.98223686155.
> Probe G measured the 3D→2D transport for the base XY plane only. On XZ or YZ the face origin's (x, y) are not sketch coordinates at all, and reusing them would point the search at an arbitrary place in the profile.
> A sketch that arrived with a reopened document has no remembered plane, and a sketch on a referenced plane has none either. Assuming XY because it is the common case is exactly the silent redefinition the project forbids.
> The point comes from a cylindrical face and lies on a circle. For a segment, an arc, a rectangle or a polyline there is no such coordinate, and handing one over would let ksFindObj hit whatever happens to be nearby.
> One circle in the batch is not enough: the coordinate is used to clear the whole profile, so every primitive it is meant to reach must be one the point can lie on.
> delete_entities carries no primitives. That is a legal command, but it is not a profile to derive a point for, and saying so beats a confusing "not a circle" message.
> Probe G used (cx + r; cy) and checked that (cx + 2r; cy) and (cx; cy) find nothing - the centre is the one point on the circle's plane that is not on the circle. Both ends of a diameter are returned so a radius that happens to coincide with other geometry still has a second chance to select this primitive.
> An axis has no inherent sign: the same hole read from the other side reports the opposite vector. Rejecting one of them would make the route depend on which way KOMPAS happened to orient a face.
> Probe G confirmed the face was the hole wall by area as well as by radius: radius alone cannot tell one cylinder of that size from another in the part. R12 through a 10 mm plate is 2π·12·10 = 753.98223686155, the figure the acceptance row asserts.

## <a id="host-ownership-2"></a>Модель владельца сеанса: вынесенная проза

Вынесено из `HostOwnershipTests` дословно.

> The single owner of the CAD session: acquire, release and generations.
> INVARIANT: the tests check the RULE, not a convenient case - "owner dead" and "owner alive" are checked against a REAL foreign process, since a fabricated pid would only prove that a nonexistent process does not interfere. INVARIANT (model of 04.10.2026): transport start takes no ownership, "releasing" is not "free to take", and an explicit release forbids an implicit acquire. LIMIT: the full cross-process case is measured by two independent MCP clients; a unit test does not replace it.
> History: docs/decisions/tests.md#host-ownership
> A per-class directory: cleanup of one class must not delete another's files.
> A live foreign process: without it "owner alive" cannot be measured.
> The same parse rules as the record itself: otherwise the test would measure its own format.
> INVARIANT: the refusal did NOT overwrite the foreign record - otherwise a refusal would be a way to take ownership.
> INVARIANT: "releasing" is NOT "free to take" - a live owner in `releasing` keeps the exclusive right.
> INVARIANT: transport finished but cleanup unconfirmed (`draining`) - ownership is not handed to a live owner (the old model did, a measured defect).
> INVARIANT: an unfinished release is no reason to start over - a new acquire would create a second generation and a second Worker over a possibly still-live one.
> INVARIANT: a late callback from an OLD generation does not update the new owner's state - a record with our pid but a foreign generation is not ours.
> INVARIANT: the owner record exists but does not parse. "Did not read" and "no owner" are different states - equating them would allow work with an unknown session.
> INVARIANT: a write failure does not turn a live call into a refusal - the host stays the owner, but the trouble is named.

## <a id="tool-catalog-2"></a>Инструмент каталога обязан быть вызываемым: вынесенная проза

Вынесено из `ToolCatalogTests` дословно.

> INVARIANT: a registered tool must be callable with exactly the arguments its own published schema demands. A tool that cannot be called is exactly the stub the catalog forbids.
> MEASURED: two defects broke this at once and were invisible to every acceptance run until a test finally called `kompas_read_topology` over MCP - an entry declared through `Mutation()` with `requiresOperationId: false` never published the field the Host routed through the journal, and `IsMutation` is `Destructive || RequiresOperationId`, so the journal received a null operation id and the call died inside the Host as ArgumentNullException before KOMPAS was reached.
> History: docs/decisions/tests.md#tool-catalog
> These two register structural references, but they change neither document nor model, so docs/02 §2.1 (operation_id is demanded of mutations) makes them reads. The assertion that matters is IsMutation == false: that is the flag choosing between the journal and a direct dispatch, and while it was true the schema published no operation_id to fill it.
> INVARIANT (rule refined 04.10.2026, not weakened): the sign is not "declared or not" but "declared only if the tool ITSELF replays the outcome of a repeat". The Host tool `kompas_release_session` must declare the field (row S03b requires it for destructiveHint=true) but writes no journal. A field the client can send that neither the journal nor the tool uses is still forbidden.
> INVARIANT: declaring the field and using it is one check, not two - "declared and swallowed" is the defect the project catches with a separate class. Here the field must be BOTH in the schema AND supported by behaviour (`ReplaysOperationId`), while the tool stays a Host tool and does not become a model mutation.
> INVARIANT: the tool answers a question about the STATE of an existing sketch - it creates no references, changes no model and enters no edit mode. So it must be a read, else the call would enter the mutation journal and bump the revision for an operation that never happened.
> INVARIANT: addressing is an explicit reference. No active document, no selection, no "first sketch that comes along": without this check the next refactor could add a convenient optional field and bring guessing back into the contract.
> The description is the only place where the client reads what the tool does NOT say. Three statements must survive the next catalog edit: the "!" value is not confirmed live, there is no degree of freedom, and the read does not change the model.
> MEASURED 16.09.2026 (rows EX34/EX43): four consecutive calls on an unchanged body returned four different strings, while the earlier string stayed usable for kompas_measure. That makes a body reference a handle, not an identifier - a tester who does not know it writes an assertion that can only fail, as EX43 did twice before switching to bbox comparison. The description is the only place a client reads it, so it is asserted here.

## <a id="profile-area-2"></a>Аналитическая площадь профиля: вынесенная проза

Вынесено из `ProfileAreaTests` дословно. Измеренные объёмы (`2356.1944901923607`, `76858.4073464102`,
`75727.43399111787`, `2481.8581963359516`, `5829.873553201979`, `79999.99999999999`) сохранены здесь.

> The analytic profile area is what turns "KOMPAS returned true" into a verified geometry change (spec 1.11), so its own numbers must be exact and its refusals must be real.
> The nested cases below are not arithmetic exercises: each was measured on KOMPAS-3D v24 before it was written down (24.09.2026, extruded 10 mm from a sketch on XY), and the measured volumes are quoted next to the expectations. The controls - one circle, disjoint contours - are here for the same reason as the refusals: an instrument that cannot pass is as useless as one that cannot refuse, and without them "summing is wrong" is indistinguishable from "the check is broken".
> Reporting an area for an unclosed contour would let an extrusion compare its volume against a number that has no meaning.
> A bowtie has a shoelace figure but no single enclosed region. Measured 24.09.2026: that figure used to be returned as an expectation - the class of defect this test closes.
> The control for every nested case below: contours that do not meet are two regions, and their areas do add up. Measured on v24: circles R=5@(0,0) and r=3@(100,0) extruded 10 mm give 1068.1415022205315 mm³ = (π·25 + π·9)·10.
> Measured on v24: circles R=10 and r=5, extruded 10 mm, give 2356.1944901923607 mm³, which is π·(100−25)·10 to 6.8e-15 relative. The sum π·125 would be 3926.9908169872415 mm³.
> Containment, not concentricity, is what makes a hole: here the inner centre is 4 mm off.
> Measured on v24: a 100×80 rectangle with an r=10 circle at (50,40), extruded 10 mm, gives 76858.4073464102 mm³ against (8000 − 100π)·10 = 76858.40734641021.
> Measured on v24: the same plate with r=10 at (30,40) and r=6 at (70,40) gives 75727.43399111787 mm³ = (8000 − 100π − 36π)·10.
> Even-odd depth: a contour inside a hole is material. Measured on v24: circles R=10, r=5 and r=2 extruded 10 mm give 2481.8581963359516 mm³ = π·(100−25+4)·10.
> Here the polygon is the contained one, and the order of the checks must not turn it into "the circle is inside the rectangle".
> Two coincident circles enclose one disk, not a region of zero: the formula refuses rather than cancelling itself out into a meaningless target.
> Measured on v24: the extrusion builds the union (5829.873553201979 mm³ for R=10 with centres 15 mm apart, 10 mm deep), but one measured special case is not a general formula - an overlapping pair is reported as uncomputable, never as a sum.
> Tangency: the region depends on how the kernel resolves the shared point, which is not measured, so neither sum nor difference is claimed.
> The measured 80000 mm³ arrives as 79999.99999999999 in KOMPAS; a purely absolute tolerance would have to be either huge (for big models) or flaky (for small ones).

## <a id="host-session-2"></a>Жизненный цикл сеанса Host: вынесенная проза

Вынесено из `HostSessionLifecycleTests` дословно.

> Session lifecycle from the Host side: acquire, release, refusal without ownership.
> LIMIT: there is deliberately no COM here - the tests check what is decided BEFORE COM (routing, ownership, journal write, refusal shape). No test starts a Worker: the channel is created and the process starts only on the first real command, of which there is none here. INVARIANT: a CAD call without ownership is refused BEFORE the journal and BEFORE COM; release creates a state in which an implicit acquire is forbidden; diagnostics answer without ownership too.
> REFUSAL BEFORE THE JOURNAL. Lines are counted, not file existence: the journal is created already at acquire, so "no file" would prove nothing here.
> INVARIANT: the SAME id replays the recorded outcome - "released BY THIS request" stays true even though ownership is already gone. Running the procedure again would answer "not by this request", making replay indistinguishable from re-execution.
> INVARIANT: the same id but a NEW generation does not replay the previous session's outcome - the release runs again and is again "by this request". Without clearing the map the answer would be foreign.
> INVARIANT: the session tools are in the published catalog - they cannot be added "for a list-changed notification", since the base catalog is published at once, including a waiting Host. The check lives here because this is what achieves availability without ownership.

## <a id="reposition-matrix-2"></a>Матрица размещения тела: вынесенная проза

Вынесено из `RepositionMatrixTests` дословно.

> Body placement matrix and plane basis for B3 - checked on the SAME geometry the KOMPAS route was measured on.
> MEASURED: the numbers below are reference §6.6 of the order, obtained by probe `--reposition` (run `929f08886f1348fe921943052a4026b0`, steps RP.3, RP.4, RP.5). If the matrix builder diverges from them, the adapter moves the body elsewhere and KOMPAS does not err - it just performs a different transform. ASSUMPTION: the asymmetric bar `[10,30]×[0,10]×[0,5]` is deliberate - on a symmetric part the angle sign and axis direction are indistinguishable and the test would pass on a wrong matrix.
> Right-hand rule about Z: (x,y) → (−y,x). This is what measurement RP.4 gave.
> DISCRIMINATING control of the layout - the thing that was absent here, and whose absence made a rotation through MCP be rejected as NO_GEOMETRY_CHANGE on 18.09.2026. All the other tests in this class read the matrix through Apply, i.e. check AGREEMENT of build with read, not the layout itself: two mutually transposed errors preserve that agreement entirely. A translation does not catch the defect for the same reason - an identity rotation is symmetric.
> Here the RAW array is compared, exactly what goes into Position.InitByMatrix3D. The reference is the layout by which the rotation was measured in KOMPAS (probe RP.4, RotationZ): three consecutive numbers are the IMAGE of an axis. For +90° about Z: image X = (0,1,0), image Y = (−1,0,0), Z = (0,0,1).
> Negative control: the transposed layout must NOT match the reference - without it the test would not tell a correct layout from a swapped one.
> INVARIANT: the axis point must stay in place - that is what distinguishes a rotation "about an axis" from one "about the origin followed by a translation".
> Negative control: on an asymmetric part −90° must give a DIFFERENT bounding box - a test that also passes on a swapped sign proves nothing.
> Only X and Y are compared: a rotation about Z does not change Z, and a match in Z is not a sign of a swapped sign but a consequence of the axis being perpendicular to it.
> The translation of a rotation about point c equals c − R·c. With c = (5,0,0) and +90° about Z this is (5,0,0) − (0,5,0) = (5,−5,0) - what reference §6.6 records.
> A tilted axis: distances between vertices must be preserved. This catches a matrix that "looks like a rotation" but is not orthogonal.
> The measured plane-creation route is by three model points, where the normal equals (P2−P1)×(P3−P1). INVARIANT: the basis must reproduce the REQUESTED normal, not a rotation of it within the plane.
> The sign s = n·(p − p₀) does not depend on the normal length, so a unit normal gives the same answer but a reproducible one.
> Coordinate comparison with a tolerance. Exact equality is unusable here: `cos 90°` is not zero, and a difference of `6.1e-16` is a way of writing zero, not a build error.

## <a id="release-guard-2"></a>Решение об освобождении сеанса: вынесенная проза

Вынесено из `ReleaseGuardTests` дословно.

> The "may the session be released" decision - as a table, without KOMPAS and without a Worker.
> TEST: exactly the pure function is checked. INVARIANT (defect H3, review 05.10.2026): each table row is a state in which the decision must be made BEFORE contacting the Worker. INVARIANT: "Worker restarted after a break, inventory empty → refusal" - an empty inventory of a new Worker is not "no edits" but "documents lost", and the `DocumentStateUnknown` sign overrides it.
> History: docs/decisions/tests.md#release-guard
> An intermediate CAD call raised a new Worker: the channel is live (canSend=true), the inventory was read and is EMPTY (dirty=0) - all the "old" checks are happy. But the previous Worker's documents are lost, and this state must override the empty inventory.
> INVARIANT: acknowledging an unknown state does NOT cancel the refusal for known unsaved edits - "I don't know" and "I know there are edits" are different states, and the second is cured by saving.
> Right after a channel break a client that has already accepted the unknown state must be able to release the session. The old code cleared only step 1 while step 3 ("channel broken") still refused, leaving a workaround as the only way out. With acknowledgement the inventory is not needed - it adds no information.
> Negative control: acknowledgement does NOT remove the channel refusal when the unknown state is not flagged. There is nothing to acknowledge - the sign is absent, and "acknowledge" must not be a universal skeleton key for any inventory check.
> INVARIANT: the refusal must NAME the way out, not leave the client in a dead end - otherwise the only way remains a workaround that the text does not mention.
> Negative control: an unknown state without acknowledgement refuses EARLIER than the broken channel is checked - otherwise the answer would name "channel broken" where the true cause is lost documents, and the client would look for a way out in the wrong place.

## <a id="ipc-channel-2"></a>Один читатель канала Host–Worker: вынесенная проза

Вынесено из `IpcRequestChannelTests` дословно.

> The single-reader contract of the Host-Worker pipe.
> INVARIANT: one serialised reader owns the stream, so concurrent tool calls each get their own answer. MEASURED: before 18.09.2026 every caller ran its own read loop, so two in-flight calls interleaved their bytes and produced a bad frame length and a JSON parse failure. The fake Worker below answers a "slow" request LATER than a "fast" one, so answers come back in reverse order - the cheapest deterministic shape of the failure. History: docs/decisions/tests.md#ipc-channel
> The echo is the proof of ownership: a mis-routed frame carries the other command.
> The fake worker is allowed to end however it likes; the assertions already ran.
> INVARIANT: a client cancel AFTER a mutation frame was written is NOT "the command was not sent".
> MEASURED (FIX H1, review 05.10.2026): a client that believed "cancelled before dispatch" repeated the mutation with a NEW operation_id and it applied twice. History: docs/decisions/tests.md#ipc-channel
> The peer receives the frame and does NOT answer: the command definitely left, but its outcome is unknown to anyone - the state the old code called "cancelled before dispatch".
> INVARIANT: a cancel BEFORE the frame is written stays a cancel - the command did not reach the Worker, and this is the only case where "cancelled" is a confirmed state.
> The peer goes away - the receipt awaitable stays false, and the test sees it.
> A peer that reads the request but never answers. The request is then genuinely in flight and already written, so the failure cannot be blamed on the write: the reader is the only party that can observe the end of the stream, so the reader is the party that must fail every waiter. Otherwise a dead Worker looks exactly like a wedged one.

## <a id="rotation-contract-2"></a>Границы вращения: вынесенная проза

Вынесено из `RotationContractTests` дословно.

> The rotation contract is checked against the PUBLISHED tool description, not the schema file nor the adapter code.
> TEST: the reason is measured - a field declared only in C# or only in `schemas/*.json` is INVISIBLE to the client, because the Host validates the call by its own `InputSchema`, and the mismatch reads as "the product cannot do it" when the declaration is what is missing (as happened with `base_object_refs` on fillet).
> INVARIANT: `angle_deg` is bounded at 360, not 180 (measured 18.09.2026; experiment `F.1` got a full cylinder `π·r²·h` at `Angle[true]=360`), and the angle of an existing rotation is edited via `rotation_angle_deg`, not `angle_deg`.
> History: docs/decisions/tests.md#rotation-contract
> INVARIANT: the angle upper bound is a FULL turn, not half. Checked by TWO numbers: 360 must pass, 361 must be refused. "360 passes" alone is not enough - it would pass with no bound at all, so the test would not tell the measured ceiling from its absence.
> INVARIANT: the axis is declared with EXACTLY three numbers per point. Two would leave the third coordinate to the server's guess; four would be a silently dropped value.
> INVARIANT: the operation kind is an enumeration, not a string - an unknown value must be refused BEFORE COM, not coerced to the nearest kind.
> INVARIANT: the rotation edit publishes its OWN angle and direction fields rather than reusing the chamfer's. Without this the rotation feature cannot be edited: `angle_deg` belongs to the chamfer, and the adapter deliberately rejects it on rotation.
> INVARIANT: reading the feature publishes the rotation parameter block. An empty field means "not read", not zero, so the client needs the object itself, not only the family name.
> INVARIANT: the tool description must name the MEASURED capability, not the old limit. The old text claimed a full turn by cut is not expressible and a boss onto a non-empty part is refused - both refuted 18.09.2026 (experiments F.9/F.10, acceptance RO.18/RO.19): "saturation" came from a one-sided blank, and "the boss makes a second body" from a probe writing NewBody. The test catches the return of the refuted text. History: docs/decisions/tests.md#rotation-contract
> INVARIANT: `target_body_ref` must carry a DESCRIPTION, not be a bare reference - the field is ACCEPTED and checked AFTER the operation, and the client must know that, else it reads as "unsupported" and the target is never given. The check also guards the schema builder: `Nullable()` rebuilds `$ref` into `anyOf` and silently drops a description placed on the inner `$ref`.

## <a id="euler-tests-2"></a>Тесты углов Эйлера: вынесенная проза

Вынесено из `EulerOrientationTests` дословно.

> Decomposing a placement into Euler angles and rebuilding it - checked by MATRIX EQUIVALENCE.
> INVARIANT: Euler parametrisation is AMBIGUOUS at nutation 0/180, so validity is proved by the matrix rebuilt from the read triple equalling the original, not by matching numbers.
> MEASURED (probe `--reposition-params`, run `a336120926fc4652a8bf737562568271`, step RP.25): exactly ONE of six products matched - `PNR` - with difference 0 on all three composite postures (the other five differ by 1); the triples `(0,0,90)` and `(90,90,0)` are read FROM THE DOCUMENT.
> ASSUMPTION: composite postures use a tilted axis because a symmetric part cannot tell an axis swap.
> History: docs/decisions/tests.md#euler-tests
> Reference RP.25, posture C1: axis (0,0,1) by 90° through (5,0,0). The triple read from the document is (precession, nutation, rotation) = (0, 0, 90).
> Reference RP.25, posture C2: axis (1,1,1)/√3 by 120° through (5,−3,7). The read triple is (90, 90, 0). It also controls that PNR reproduces a TILT, not only a rotation about a coordinate axis.
> Reference RP.25, posture C3: half a turn. Here the skew part is zero, and that is a separate branch of the decomposition - the one on which a degenerate axis derivation would return zero.
> The triple is compared with the MEASURED one (RP.25: euler_angles_C2 = (90, 90, 0)), not with any valid one: a matrix can have several valid triples, so agreement with the document is a separate fact.
> RP.25: euler_angles_C1 = (0, 0, 90) and euler_angles_C3 = (0, 0, 180). Here nutation is zero, so the triple is degenerate; the test checks that the degenerate branch returns EXACTLY the measured one.
> NEGATIVE CONTROL, without which the earlier tests prove nothing: if RotationFromAngles built ANY order they would still pass. The same three numbers in a different order must give a DIFFERENT matrix, and a noticeably different one, not a machine epsilon.
> Round trip over all RP.25 postures at once: the decomposition must be REVERSIBLE, otherwise what was read from the document can be neither checked nor rewritten with the same placement.
> The FULL placement is compared, not only the rotation: orientation comes from the angles, the translation from slots 12…14 - exactly how the product writes the feature (RP.25), so checking half here would not check the route.
> This posture's translation is probe-measured (RP.25: displacement_written_C2 = (−2, −8, 10)) - it equals c − R·c, and its agreement with the document is checked separately from equivalence.
> The axis is recovered up to sign: (d, θ) and (−d, −θ) give one matrix. So the test compares the VALIDITY of the pair, not a triple: rotating by the found angle about the found axis must give the original matrix.
> HERE IT IS PROVED THAT THE AXIS POINT IS NOT READ, supporting the claim that the product does not publish it. Two rotations about ONE line but through DIFFERENT points give the SAME placement - so the input point cannot be recovered from the placement at all.
> What is recovered is a REPRESENTATIVE of the line - the point with zero component along the axis. For (5,0,0) it coincides with the input, for (5,0,7) it does not - exactly the case where publishing a "read" point would pass a derived value off as a recorded one.
> A translation does not determine an axis point: it has no axis. Returning zero here would substitute a value for a missing one - forbidden along the whole read route.
> Discriminating control: a translation BY ZERO is also an identity rotation, and it looks like a translation. This is the pair the product distinguishes when reading (RP.25, posture C1 vs D1).

## <a id="euler-tests-pole-2"></a>Полюс параметризации при нутации 180°: вынесенная проза

Вынесено из `EulerOrientationTests` дословно.

> INVARIANT: the parametrisation pole at nutation 180° - the rebuild must reproduce the matrix.
> MEASURED: found by acceptance row B3.59, not by unit tests; the product refused with GEOMETRY_FAILED and a difference of 2 because the rotation sign at this pole used the neighbouring convention's formula. The matrix here is GIVEN AS NUMBERS, not built from angles - otherwise the test would compare the decomposition with itself and pass on any sign error.
> History: docs/decisions/tests.md#euler-tests-pole
> ASSUMPTION: a test on one axis would also pass on a decomposition valid only for XY planes; axis (1,1,1) touches all three axes, so a sign swap in any of them is visible here.

## <a id="raster-image-2"></a>Растровые изображения: вынесенная проза

Вынесено из `RasterImageTests` дословно.

> Raster header parsing and the format list - what the `kompas_export_image` acceptance rests on.
> INVARIANT: the image size is what the server PUBLISHES, so taking it from the request would publish an intention instead of a fact. LIMIT: header parsing is easy to write "by eye" and miss a byte order swap, after which the answer carries plausible but wrong numbers that a live run cannot catch - there is nothing to compare against. Here there is: bytes assembled in the test from the format spec.
> INVARIANT: a silent format substitution is indistinguishable to the caller from fulfilling the request. MEASURED (probe P5): the kernel accepts a value OUTSIDE the list and substitutes another format (99 gave BMP, 440886 bytes), so an unknown name must be refused here, before COM.
> A file named PNG that is not one: exactly the case the magic check in the adapter exists for. The kernel accepts a value outside the list silently (P5), so "the kernel answered success" says nothing about the format.
> INVARIANT: "not read" and "zero" are different claims, and a silent zero would be a lie - a 9961-byte JPG with a 0×0 size would look like an empty image.
> INVARIANT: the tool schema and the Domain format list must name the SAME formats. Diverging, they would give the client a format the adapter rejects, and the refusal would look like a product defect rather than a mismatch of two lists.

## <a id="component-identity-2"></a>Тождество адреса компонента: вынесенная проза

Вынесено из `ComponentIdentityTests` дословно.

> Component-address identity - as a table, without KOMPAS.
> TEST: exactly the pure function is checked. INVARIANT (findings §1 and §3 of the 05.10.2026 order): source matches, name matches, matrix CHANGED after an own mutation → identity is NOT false. The matrix is mutable state: both an own mutation and a mate change it. Name DIFFERS while source and matrix match → NOT a refusal. API5/API7 name comparison was never measured live, and a systematic format difference would refuse every assembly mutation.
> History: docs/decisions/tests.md#component-identity
> INVARIANT: both the name and the matrix differ - still not a refusal: neither is a measured, immutable sign. Refusing here would rest the work on something unverified.
> INVARIANT: the source is the only sign that decides a refusal - the number points at a component with a different file. Negative control: matching name and matrix do NOT override a source difference, else a number leading into a foreign component with the same matrix would pass.
> INVARIANT: a matching name does NOT confirm the address - confirming by an unverified sign would allow a mutation by an address that was never checked.
> INVARIANT: a matrix difference is NAMED, not swallowed - silence is indistinguishable from "we did not look".
> INVARIANT: on a clean match the note stays clean - the rule is printed where it explains a difference or an unread sign.

## <a id="path-policy-2"></a>Границы путей: вынесенная проза

Вынесено из `PathPolicyTests` дословно.

> File-boundary rules from spec 1.12. Every case here exists because the naive version of the check looks correct and is not.
> "D:\work" is a text prefix of "D:\workspace\evil.a3d": a StartsWith check without a separator admits it, which is the exact bug this test pins.
> The access decision is the contract; the reason string is Russian prose for a human and deliberately not asserted on.
> "\\?\C:\x" bypasses plain containment reasoning, so it must not be reasoned about at all.
> MEASURED (probe P4, order KOMPAS_EXPORT_IMAGE), not inferred: the KOMPAS kernel does NOT refuse such a name. It returned success, the base file `bad` stayed zero, and the payload (8639 bytes) went into an ALTERNATIVE NTFS STREAM: FILE=bad LEN=0 STREAMS=:$DATA=0|name?.png=8639. `Path.GetInvalidPathChars()` lets this through - its set is narrower than the name table - so the check works by components and by the NAME table.
> Separate from the Theory: what matters is that the refusal came NAMED from the component check, not from a canonicalisation exception - otherwise the test would be green for a chance reason.
> INVARIANT: banning ':' must not break an ordinary path - the drive colon is part of the ROOT, not the name.
> Trimming "D:\" to "D:" changes it to "current directory on D", which would then be compared against paths on an entirely different root.
> Temp leftovers are cleaned by the OS.

## <a id="control-copy-2"></a>Восстановление из контрольной копии: вынесенная проза

Вынесено из `ControlCopyTests` дословно.

> Restoring a document file from its control copy: the decision as a table, the behaviour on files.
> INVARIANT (defect H4, review 05.10.2026): a restore never overwrites a document opened `access=read_only`, and never runs for a refusal that never reached COM - writing into a user file without cause. History: docs/decisions/tests.md#control-copy
> REVISION_CONFLICT arrives before COM: the file did not change, so a restore would be a needless write.
> Unexpected exception: no code, outcome unknown - the file is returned.
> Negative control: a partial effect does NOT override the ban on writing to a read_only document. LIMIT: the order of the checks is part of the contract - overwriting a file around the path policy is worse than no rollback, and the reason is spoken, not withheld.
> Access mode read_only: the restore must refuse and NOT touch the document file.

## <a id="control-copy-layout"></a>Раскладка каталога копий (05.10.2026)

Вынесено из `ControlCopyTests` дословно.

> The copy lands in `<root>/control-copies/` - the documented layout, not next to the document and not one level deeper.
> INVARIANT: the folder name is appended ONCE by `DocumentControlCopies`. MEASURED: the shipped example config and the Host default both ended in `control-copies`, so the effective path was `…\control-copies\control-copies\…`. This test pins the layout so a root that already names the folder is caught here rather than in an acceptance run. History: docs/decisions/tests.md#control-copy-layout
> A root already ending in `control-copies` doubles the folder: this is the caller error the example config used to make, and it is shown here rather than left to be discovered as a mystery path.

## <a id="document-save-2"></a>Сохранённость документа: вынесенная проза

Вынесено из `DocumentSaveTrackingTests` дословно.

> Document saved-ness: state transitions and the close decision table.
> DOC: the target version has no documented "document modified" sign on `ksDocument3D` in the v24 help, so the product tracks the state and the only place to check it without KOMPAS is the transitions themselves; behaviour in CAD is proved by the `DL` acceptance rows on shipped binaries. INVARIANT: a mutation never declares the document saved - only a confirmed write does.
> History: docs/decisions/tests.md#document-save
> INVARIANT: a revision bump does not declare the document saved - after a save it is clean, but the next mutation makes it dirty again.
> Opening reads the file from disk: model and file agree.
> INVARIANT: "clean" does not stay clean after a failure - the save was not confirmed and the model has already diverged from the file (otherwise there would be nothing to save).
> refuse: closes only a confirmed-clean document. save: saves before closing everything not confirmed clean. discard: discarding changes closes in any state, including unknown.
> INVARIANT: a distinct string, not a number - zero bodies of an empty document and "the collection did not answer" are different facts, and the first must not read as the second.

## <a id="session-ownership-race-2"></a>Гонки владения сеансом: вынесенная проза

Вынесено из `SessionOwnershipRaceTests` дословно.

> Session-ownership races: two simultaneous acquires cannot both get the right to work.
> INVARIANT: a race is a claim about ORDER, not state, so sequential calls cannot check it. Each participant is a separate `HostOwnership` (its own Host "identity", its own lock-name acquisition), and all start SIMULTANEOUSLY from a barrier. LIMIT: a real cross-process race is measured by two independent MCP clients on the shipped binaries - within one process the pid is shared, so "same pid, foreign generation" works differently. This class checks the atomicity of the state TRANSITION, not cross-process exclusivity as a whole.
> A barrier: without it "simultaneously" would mean "whoever got there first", and the race would not happen.
> The participants are OTHER "identities" with the same pid: they claim to release a foreign session.
> THE OLD OWNER: neither work nor release a foreign session.

## <a id="assembly-domain-2"></a>Домен сборки: вынесенная проза

Вынесено из `AssemblyDomainTests` дословно.

> Assembly domain (order C1, profile `assemblies-minimal-v1`): the contract of five tools and the honesty of their descriptions.
> TEST: the tests check exactly what is checkable without KOMPAS - the tools are registered, mutations declare the mandatory fields, schemas are strict, and descriptions name the CURRENT release limits (e.g. nested components cannot be addressed) rather than the presence of the word "acceptance". The "the route works" check is NOT included and is not substituted: it requires a run on v24.0.0.2799.
> INVARIANT: the release limit is visible to the client, not only in the code - a nested component is READ, but it has no address, and a mutation by a guessed number is forbidden.
> INVARIANT: no description may claim there was no live run - the run happened, and the stale wording reads as "the capability is unconfirmed". What is checked is the CURRENT state, not the presence of the word "acceptance".
> INVARIANT: the source is required (there is nothing to insert without it), placement is not - the server does not invent an origin but leaves the documented KOMPAS default.
> INVARIANT: an empty field means "not read", not zero - the instance count and the "part/assembly" sign are nullable so "did not read" is distinguishable from "read zero".

## <a id="typed-com-2"></a>Граница типизированного COM: вынесенная проза

Вынесено из `TypedComBoundaryGuardTests` дословно.

> The "the product talks to KOMPAS only in a typed way" boundary (order of 12.09.2026, item 4).
> INVARIANT: API7 is allowed in the product (ADR-004 §1, §4) but only through vendor-generated interfaces. Late binding (`Type.InvokeMember`, `dynamic` over an RCW, a direct `IDispatch` call) stays the emergency route of the research probe `tools/KompasMcp.Api7Probe` and is not carried into `src/`.
> MEASURED (probe E, 2026-09-12): writing `IExtrusion.Sketch` through `IDispatch` crashed the shared process with 0xC0000409 and did not crash the isolated one, while the same write silently did not persist and was re-read by the old object. A silently unaccepted write returning S_OK is worse than a refusal precisely because acceptance would count it a success.
> LIMIT: the check is static by source - "no late binding" is a property of code, not an observable number.
> History: docs/decisions/tests.md#typed-com
> Late binding under any name: InvokeMember (including Type.InvokeMember), a local IDispatch declaration with its GetIDsOfNames/Invoke, and a dynamic receiver. Exactly this set forms the emergency route in tools/KompasMcp.Api7Probe/Late.cs - copying any of these forms into the product carries over what the boundary guards against.
> dynamic allows a call that compiles without a signature check and goes to IDispatch.
> The inverse of the same requirement: the emergency route must stay in the probes project. If it disappears from there while still absent from the product, the check below has become a vacuum - that must be learned, not celebrated as a green test.

## <a id="api5-visibility-2"></a>Видимость сеанса API5: вынесенная проза

Вынесено из `Api5SessionVisibilityGuardTests` дословно.

> Visibility guarantees that cannot be checked without KOMPAS, but can be checked in the code.
> INVARIANT (requirement of 12.09.2026, item 7): the "visible to the user" mode refreshes the view after modelling and never resets the user camera and zoom after an operation. The second half bans a specific call - `ZoomPrevNextOrAll` and `ksZoom*` in API5 change exactly the camera.
> LIMIT: the check is static because a 3D document in API5 exposes no camera state (no scale getter on `ksDocument3D`, only `ksGetZoomScale` on the 2D editor), so "the camera did not change" cannot be confirmed by a number at acceptance.
> INVARIANT: the visibility answer is never derived from PID availability - a hidden window has both an HWND and a PID, so `ProcessIdOf(...) is not null` would read as a false `visible=true`.
> History: docs/decisions/tests.md#api5-visibility
> Code lines without comments: a "this used to be" comment matches the forbidden expression as text, but the ban applies to executable code.
> The very line that let the defect through acceptance: "a PID is obtained" was passed off as "visible".
> INVARIANT: hidden mode returns before touching the window - otherwise "hidden mode is checked separately" is a promise, not behaviour.

## <a id="cut-plane-2"></a>Форма плоскости: вынесенная проза

Вынесено из `CutPlaneContractTests` дословно.

> Agreement between the PUBLISHED shape of a plane and the contract that accepts it.
> TEST: the check walks the chain "published schema → schema-valid JSON → DTO → contract outcome", and a nested object is the discriminating control: it MUST be rejected by the schema.
> LIMIT: comparing `schemas/*.json` with `tools/list` cannot catch this class of defect by construction - both sides are the same schema.
> MEASURED (client acceptance B3, 19.09.2026, three FAIL rows, defect `PLANE-BASE-DECLARED-REFUSAL-UNREACHABLE`): the published schema made `plane.base` a string and `plane.offset_mm` a number while the DTO expected an OBJECT one level deeper, so a call matching the published schema failed parsing and the declared refusal was unreachable.
> History: docs/decisions/tests.md#cut-plane-contract
> Nullable(Enum(...)) is expressed as a type array - that is "string or null".
> INVARIANT: the declared outcome must be REACHABLE - the old schema/DTO desync made exactly this check unreachable.
> Discriminating control: the old (wrong) DTO shape would accept exactly this, so the check must fail if the shape becomes an object again.
> The tool references $defs/cut_plane, and the Host validates the call through exactly this reference.

## <a id="sketch-profile-2"></a>Профиль эскиза: вынесенная проза

Вынесено из `SketchProfileTests` дословно.

> A sketch is built by several calls (append per contour, replace to clear), and the extrusion's expected volume is the area of the WHOLE profile - so the accumulation, not just the formula, is what the check stands on.
> Measured 24.09.2026: while the adapter remembered a running sum, drawing the outer circle and then the inner one gave exactly the same figure as drawing both at once - π·100 + π·25 = 392.699081699 against the ring's π·75 = 235.619449019. A scalar cannot carry nesting, so a later edit could never turn the remembered number into a hole; these tests hold the contour list to that.
> The second call is what makes it a ring - and the accumulated profile has to see it.
> A line is not a region, and one unknown primitive makes the whole profile unknown: the extrusion then says "not computable" instead of comparing against a partial figure.

## <a id="json-schema-validator-2"></a>Валидатор схем: вынесенная проза

Вынесено из `JsonSchemaValidatorTests` дословно.

> The validator must treat a payload that arrived over the wire exactly like one built in memory. Parsed nodes wrap a JsonElement rather than a CLR double, and an earlier version threw InvalidOperationException on every numeric argument - a request crash, not a validation result.
> Every published schema is DeepCloned on its way into the catalog (shared $defs, nullable wrappers). Cloning changes a node's backing store to JsonElement, and GetValue<T> on such a node throws - which previously surfaced as an unhandled server error for any numeric argument. Validating the clone is therefore the shape that matters.
> A schema the validator cannot fully interpret must not be used: it would advertise more strictness than it enforces.

## <a id="envelope-serialization-2"></a>Сериализация конверта: вынесенная проза

Вынесено из `EnvelopeSerializationTests` дословно.

> A list-valued Worker result must survive being put into the envelope and serialized.
> Regression test for the "kompas_list_bodies returns []" defect. The Worker was proven to send a correct one-element array (verified on the pipe: body_ref, kind=solid, bbox 100×80×10, face_count 6), so the loss had to be inside the Host's envelope/serialization path. That path carries `ResultEnvelope<JsonNode?>` where `Result` may be a JSON array rather than the usual object, and arrays are exactly what the first probe missed.
> The Host hands the same JsonNode instance to the envelope and later serializes the envelope; if any step reparents the node, the value silently disappears from the output.

## <a id="transform-math-2"></a>Математика преобразований: вынесенная проза

Вынесено из `TransformMathTests` дословно.

> Transform rules from spec 1.10/2.3: the basis must be right-handed and orthonormal, Z is derived, and the composition order is fixed - checked on a rotate-then-translate pair that does not commute, so an accidental swap cannot pass.
> A 90° rotation about Z: X→Y, Y→−X, so Z must stay +Z.
> X·Y = 1 here: the silent normalisation this guards against would produce garbage Z.
> Z = X×Y = (1,0,0)·... → derived; local (1,2,3) maps to origin + X*1 + Y*2 + Z*3.
> Outer: translate by (100,0,0). Inner: rotate 90° about Z.
> Same local point, different world answer: proves the order is honoured, not averaged.

## <a id="command-budget-2"></a>Бюджеты команды: вынесенная проза

Вынесено из `CommandBudgetTests` дословно.

> Command budgets: the Host must never give up before the Worker (defect M11, review 05.10.2026).
> INVARIANT: the Host budget is derived from the Worker budget on one shared table, not set by an independent number - otherwise the Host declares OUTCOME_UNKNOWN and breaks the channel before the Worker reaches its own limit, and the next call kills a Worker that is still working.
> History: docs/decisions/tests.md#command-budget
> INVARIANT: the default budget (120 s) need no longer exceed the Worker budget, but it must be no LESS than the old flat one - the old 120 s Host vs 240 s Worker was the discrepancy closed here.

## <a id="json-scalars-2"></a>JSON-скаляры: вынесенная проза

Вынесено из `JsonScalarsTests` дословно.

> The same JSON value must read back identically whether the node was built in memory or arrived as parsed text. That equivalence is not free: JsonNode stores either the CLR value or a JsonElement, and the strict accessors only accept one of them.
> A result is an object for single-entity reads and an ARRAY for listings, and array["key"] throws instead of returning null. A diagnostic line that assumed objects turned kompas_list_bodies into a protocol error, which a test then reported as "the document has no bodies". Anything reading result fields must go through JsonScalars.

## <a id="safe-read-2"></a>Непрочитанное COM-значение: вынесенная проза

Вынесено из `SafeReadTests` дословно.

> INVARIANT (FIX C): an unread COM value must differ from a successfully read `false`/`0`/first enum value. The old helper returned `default`, making "the read did not happen" indistinguishable from "the default value was read". History: docs/decisions/tests.md#safe-read
> Both null - expected for reference semantics; the difference is caught through TryRead.
> The old Safe gave default(Sample) = First, and "not read" was printed as "First".

## <a id="comment-cleanup-continuation"></a>Вынесенная проза: продолжение (второй проход)

Вынесено из тестов дословно во втором проходе чистки комментариев; код ссылается на те же якоря
`*-2` соответствующих разделов.

### JournalAndQueueTests

> The record is still InFlight: the operation is running.
> Re-sending the same operation_id must not silently dispatch again.
> No terminal record: the crash case - the file still says "in flight", which must NOT be read back as "it never happened".
> The append is flushed at TryBegin, not at completion: that ordering is the whole guarantee, so it is asserted on the file rather than on in-memory state.
> File.ReadAllText asks for FileShare.Read, which Windows refuses against a live writer - so observation opens with FileShare.ReadWrite. The "readable while a writer is live" check is a separate test.
> INVARIANT: the skip is NAMED by a number and the torn tail a tail - "the journal was read" and "not read in full" are different claims, and silence between them is indistinguishable.
> A garbage line IN THE MIDDLE, followed by a newline and a valid line: the cause differs (corruption, not a killed process), and calling it a torn tail would misname what was measured.
> INVARIANT: a second reader cannot know whether the writer is alive, so an unfinished record reads as `JournalOutcome.OutcomeUnknown` with a reconciliation demand - the crash-recovery rule, unweakened; this is why the journal owner must be one (R3).
> A second instance on the same path - and not a single exception.
> Not a single unparsed line: reading runs under the same cross-process lock as writing, so half a line cannot be taken for a torn tail.
> And the first keeps writing: sharing is needed in BOTH directions, not only on read.
> The write handle is no longer held for the process lifetime: each write is open-append-close.
> And it is not on disk either: a refusal is "not recorded", not "recorded without the lock".
> Intent B is not lost - that was the substance of the defect.
> ANOTHER THREAD holds the lock: a named Windows lock belongs to a THREAD, and a second object on the same thread would take it too. So the holder is a separate thread.
> The holder releases the lock - and the SAME record opened without the lock must repair the tail under ITS OWN lock, not glue its line to the fragment.
> And this is not "the old one was forgotten": the restarted operation is again in_flight, and the terminal record overrides the old failure.
> The cancelled one is skipped when the reader reaches it, so it is never handed to COM. With nothing else queued the reader then waits, and a cancelled wait is reported as OperationCanceledException rather than as a null command.
> The Host admits a mutation through this queue and dispatches it to the Worker directly - the queue is the counter of outstanding CAD work, not the executor. A served command must release its slot, or `capacity` becomes a lifetime budget of mutations per process, and every later call dies with QUEUE_FULL while the Worker is idle.
> The backpressure itself must survive the fix: a client that fires mutations faster than CAD serves them is told to back off, not queued without bound (docs/03 §1.13).
> One completion is enough to let the refused one in.
> Holds the journal's cross-process lock FROM ANOTHER THREAD: the "write without the lock" check only makes sense when someone else really holds it. The name comes from `OperationJournal.FileLockPurpose`, not a literal - a private copy would measure a foreign lock.

### SolidFeatureClassificationTests

> The listing below is the SUBJECT of the check, not its decoration: a divergence from the contract and the schema is caught by the assertions.
> INVARIANT (order SM07 §3.2, queue B2): six native-hole edit fields. Role "family", not "not applicable" - the hole branch READS them, others reject them via the shared table (SolidOps.cs). The volume-delta expectation belongs to THIS family: only the hole reads it, so a shared expectation would be accepted on a translation and a boolean and silently not applied (same class as keep_side, P5).
> MEASURED (order B1–B5 §3.2, step C): queue B5 added SIX editable fields; the sixth, couplings, was in neither this list nor the adapter guard. Role set by EXPERIMENT: a call "distance1_mm + couplings" on a chamfer applied (volume 79840 → 79955), while shift_mode and section_refs were rejected INVALID_ARGUMENT.
> The family constants live in DIFFERENT adapter files (SolidOps.cs, PatternEdit.cs, Hole.cs): looking in one would demand moving a constant for the test. The table itself is parsed ONLY from SolidOps.cs - a second file could let the test find an entry in a foreign place.
> Discriminating control: the table must not only contain the expected but also NOT contain extras - a field added "just in case" would refuse a legal call as foreign.
> MEASURED defect class (§9.1 P4): a field in the contract and the adapter but not declared in the schema - the Host with additionalProperties:false rejects the call before COM. Happened with base_object_refs on the fillet edge-set reduction.
> The main completeness check: every command field must have a ROLE. A field in none of the four departments is accepted and silently ignored - how keep_side behaved until assigned to split (defect P5). A new contract field will fail this check - and that is its purpose: the author must decide its role.
> INVARIANT: completeness also fixes the contract size - growth shows as this line failing. The count is recounted from the contract, never fitted to a run; it is 40 with the six native-hole edit fields.
> The check reads the REFUSAL CONDITION, not a comment: the adapter must reject exactly these fields. The reverse half (does not reject family fields) is the discriminating control.
> The condition starts with a DOUBLE paren (order SM07 §3.2): depth_mm became conditional - it belongs to both extrusion and a blind hole. The regex was updated TOGETHER with the condition, else it would not find it and the test would fail.
> Exactly one exception, named explicitly: depth_mm is rejected as not applicable IF the family did not declare it its own - else a condition always rejecting depth_mm would pass (the blind-hole edit failure).
> MEASURED (probe scratch/b3-measure-feature-types.py, reading ksEntity.type via kompas_list_features): in three of four families the FACTORY number and the TREE number differ (rotation 29 = 29; hole 52 → 583).
> Discriminating control against the number that suggests itself: 569 - o3d_BodyReposition, the CREATION number; the feature appears in the tree under 79. A fixed defect that must not return.

### EulerOrientationTests

> RP.25, posture C1: axis (0,0,1) by 90° through (5,0,0); the read triple (precession, nutation, rotation) is (0, 0, 90).
> RP.25, posture C2: axis (1,1,1)/√3 by 120° through (5,−3,7); the read triple is (90, 90, 0). Also controls that PNR reproduces a TILT, not only a rotation about a coordinate axis.
> RP.25, posture C3: half a turn - the skew part is zero, a separate branch where a degenerate axis derivation would return zero.
> The triple is compared with the MEASURED one (RP.25: euler_angles_C2 = (90, 90, 0)), not with any valid one - a matrix can have several valid triples, so agreement with the document is a separate fact.
> RP.25: euler_angles_C1 = (0, 0, 90) and euler_angles_C3 = (0, 0, 180). Here nutation is zero, so the triple is degenerate; the test checks the degenerate branch returns EXACTLY the measured one.
> NEGATIVE CONTROL, without which the earlier tests prove nothing: the same three numbers in a different order must give a DIFFERENT matrix, and a noticeably different one, not a machine epsilon.
> Round trip over all RP.25 postures at once: the decomposition must be REVERSIBLE, else what was read from the document can be neither checked nor rewritten with the same placement.
> The FULL placement is compared, not only the rotation: orientation from the angles, translation from slots 12…14 - exactly how the product writes the feature (RP.25).
> This posture's translation is probe-measured (RP.25: displacement_written_C2 = (−2, −8, 10)) - it equals c − R·c.
> The axis is recovered up to sign: (d, θ) and (−d, −θ) give one matrix. So the test compares the VALIDITY of the pair: rotating by the found angle about the found axis must give the original matrix.
> HERE IT IS PROVED THAT THE AXIS POINT IS NOT READ: two rotations about ONE line but through DIFFERENT points give the SAME placement, so the input point cannot be recovered from the placement.
> What is recovered is a REPRESENTATIVE of the line - the point with zero component along the axis. For (5,0,0) it coincides with the input, for (5,0,7) it does not: publishing a "read" point would pass a derived value off as a recorded one.
> A translation does not determine an axis point: it has no axis. Returning zero here would substitute a value for a missing one - forbidden along the whole read route.
> Discriminating control: a translation BY ZERO is also an identity rotation, and it looks like a translation - the pair the product distinguishes when reading (RP.25, posture C1 vs D1).
> MEASURED: found by acceptance row B3.59, not by unit tests; the product refused with GEOMETRY_FAILED and a difference of 2 because the rotation sign at this pole used the neighbouring convention's formula. The matrix here is GIVEN AS NUMBERS, not built from angles - otherwise the test would compare the decomposition with itself and pass on any sign error.

### HoleEditClassificationTests

> Two halves of one cause. First: recognition by the entity type in the tree (as in reading, FindHoleEntity), not by the creation factory number: 52 at creation, 583 in the tree, and a search for 52 would never find the feature. Second: the branch must stand BEFORE reading the API5 definition - a hole has no definition at all.
> Discriminating control: the constant must be the measured tree number, not the factory number - else the test would also pass on recognition by 52.
> INVARIANT: a field in none of the foreign-field lists is one the adapter accepts and does NOT apply - another family does not read it and there is no shared guard. This is how keep_side on split (P5) and couplings on chamfer behaved.
> (2) Tool schema: the same field is absent there too, else the Host with additionalProperties:false would pass the call through to COM.
> (3) Adapter: the written number is not checked for a match; the derived value is PUBLISHED with explicit text saying it is derived.
> (4) Bridge: on edit, CountersinkDepth is NOT written - checked against the edit method body, not the whole file: at CREATION this member is written deliberately (depth is passed as zero for contract completeness).
> MEASURED (at CREATION, same class as a zero chamfer leg, F.12): KOMPAS accepts a zero diameter and builds a feature with no material at unchanged volume. So on edit positivity is checked before COM - "accepted" there does not mean "applied". EVERY writable numeric field is checked by name.
> MEASURED defect of the first delivery with the hole branch: editing a BLIND hole was refused with INVALID_ARGUMENT before COM, because depth_mm is an extrusion field in the role table and at the same time an OWN field of the blind_flat mode. The refusal was on a line that must pass, found by acceptance, not by reading the code.
> (2) For THROUGH modes the depth is still foreign: the exception was granted to the FAMILY, and the mode decides its own. Both halves are named by mode.
> (3) Discriminating control: the exception was granted to EXACTLY ONE caller - a second `ownFields:` would mean another family declared the field its own.
> INVARIANT (lesson F-11): the feature address is GUARANTEED by the setup, not guessed. For a hole edit the setup is the hole's uniqueness in the document - with several, the correspondence "tree feature ↔ Holes3D entry" is unproved, and the call must be refused BEFORE the write. The address comes from the READ route: an index into IHoles3D via Api7Hole.
> Discriminating control: the address is NOT taken by indexing the collection by hand - that would bypass the only place where the address is proved.

### SketchStatusConversionTests

> ── Values 0/1/2 confirmed by the route ─────────────────────────────────────────────────────
> ── Value 3: declared, but no live check obtained ───────────────────────────────────────────
> ── Values outside the declared enumeration ─────────────────────────────────────────────────
> ── Context attached by the caller is not lost ─────────────────────────────────────────────
> ── The answer to the tool's main question ──────────────────────────────────────────────────
> 0 is KOMPAS's "did not set" answer and is not equal to 2; mixing them would declare an empty sketch (S.5b) under-defined without grounds.
> raw == null means the call did not arrive (or arrived with no value): not the product's answer, and substituting 0 here would be an invention.
> The route (ISketch.ConstraintsState) returns a state, not a counter; degrees of freedom is not summed from the dimension count, and a zero here would be a claim the measurement never made.
> The key honesty test. Value 3 is declared in ksConstraintsStateEnum but never appeared in a confirmed run on a live model (S.8: 46 shipped sketches), and no constraint-writing branch was found - "we have a mock" is no ground to publish the state as measured.
> If a live check ever appears the conversion is already ready - and it stays needs_attention, not fully_defined: "needs attention" is not "+".
> Strict prohibition: any unknown enum value → unknown, is_fully_defined=null, a diagnostic reason - not "under-defined" and not "defined".
> 0 and 42 give the same DefinitionStatus=Unknown but different Limitations and RawState - the point of keeping the raw value separate from the normalised one.
> The adapter passes in the transfer diagnostics and the DOF limitation; the function must not displace them - the client reads the answer, not the adapter's internals.
> "Is this sketch fully defined right now?" - exactly one state answers true; everything else, including "not set", gives no right to say "yes".
> The mirror property: a "no" answer is allowed only where the product explicitly said "under-defined"; "Unknown" does not turn into "no".

## <a id="mate-after-mate-moved"></a>Строка MATE.01.create_after_mate_moved (06.10.2026)

**Что было.** Строка, добавленная после проверки кода 05.10.2026, создавала второе сопряжение
«расстояние 50» на той же паре граней, что и совпадение MATE.01. Первый живой прогон группы MATE
на бинарях поставки `publish-viewcleanup-20261005` (06.10.2026) дал 4 FAIL: два ограничения
противоречат друг другу, оба становятся недействительными, продукт честно отвечает
`VERIFICATION_FAILED` («разность дала 2 строк»), и недействительное сопряжение остаётся в модели и
роняет MATE.02.fields, MATE.03.create_distance и MATE.04.edit. Сама проверка строки (нет
`STALE_REFERENCE`) при этом выполнялась.

**Что решено.** Второе сопряжение - «параллельность» на той же паре граней: проба на тех же
бинарях показала, что оба сопряжения остаются `Valid=true`. После удаления этого сопряжения ссылки
на компоненты и на сопряжение MATE.01 перечитываются: удаление поднимает ревизию и отзывает ссылки
документа (поведение продукта, описанное строкой MATE.05.delete_dependencies).

## <a id="asm-repeat-restores"></a>Строка ASM.04.repeat_after_mutation (06.10.2026)

**Что было.** Строка переносила компонент на 45 мм и оставляла его там, а последующие строки
ASM.04.geometry_validation и ASM.07.geometry_validation сверяют аналитический перенос 30 мм. Первый
живой прогон группы ASM на бинарях поставки дал на них 2 FAIL (`origin.x=[0, 45]`); продукт
выполнил ровно то, что ему задали.

**Что решено.** Третьим вызовом по той же ссылке компонент возвращается на 30 мм; строка требует,
чтобы прошли оба повторных вызова.

## <a id="mania-rebuild-before"></a>Строка MANIA.16.geometry_validation (06.10.2026)

**Что было.** Строка печатала «V до перестроения» и «после», но оба числа читала после
`kompas_rebuild` и сравнивала перестроенную модель саму с собой. В полном прогоне на бинарях
поставки `publish-release-20261006` первое перестроение переоткрытой скобы Model Mania один раз дало
сумму объёмов 93970.540219 вместо 132256.500735; MANIA.16 это пропустила, а поймала только
MANIA.19, где второе перестроение вернуло 132256.500735. Повтор полного прогона и три прогона
группы MANIA на тех же бинарях эффекта не дали.

**Что решено.** Объём «до» снимается до перестроения. Эффект назван в STATUS как
невоспроизведённый; маршрут `kompas_rebuild` уровень проверки геометрии не заявляет.

## <a id="sketch-reference-identity"></a>Тождество ссылки на эскиз - таблица без КОМПАС (08.10.2026)

**Что доказывается.** Правило «одна живая ссылка на один объект» проверено на `ReferenceRegistry`
целиком: выдача, обычный сдвиг ревизии, полная инвалидация, чужое тождество и отсутствие тождества.
Отдельным случаем закреплена ИЗМЕРЕННАЯ ловушка: поиск, читающий карту порядка вставки вместо
текущих записей, отвечает «живой ссылки нет» на живую ссылку. Живой прогон — строки `R40.01` и
`R40.03`.

## <a id="pattern-read-back-mark"></a>Пометка непрочитанного параметра - подменённое чтение (08.10.2026)

**Что доказывается.** `PatternReadBackMarks` — чистая функция проверок: провал `read_back_*` ставит
`parameter_not_read_back` первой строкой; совпавшее чтение, чужой провал и отсутствие проверки
пометки не ставят. Результат чтения ПОДМЕНЁН, поэтому живой КОМПАС не нужен.

## <a id="profile-area-3"></a>Площадь из цепочек, дуга по концам и заявленный объём - таблицы без КОМПАС (08.10.2026)

**Что доказывается.** `ProfileAreaTests` — цепочки из отрезков и дуг: паз (`2rL+πr²`), полукруг,
большая дуга (`|sweep|>180°`, проверяется, что взят БОЛЬШИЙ сегмент, а не меньший), кольцо из двух
цепочек, равенство площади при обеих ориентациях обхода, контур из двух открытых полилиний с общими
концами (карточка PERF-008), ветвление и самопересечение — `null` с НАЗВАННОЙ причиной. Контур
клиента (карточка GAP-012) читается из встроенного ресурса `Data/plate_cp04a_mcp.json` и сверяется с
аналитическим `8474.982482571266` мм² (допуск `1e-6`) и с измеренным КОМПАС объёмом
`33899.92993028507` мм³ (допуск `4e-6`). Дуга по концам сверяется с дугой по углам: одна и та же
геометрия двумя формами, и `clockwise` берёт дополняющую сторону.

**`DeclaredVolumeMarksTests`** — провал ЗАЯВЛЕННОГО объёма: совпадение не помечается, расхождение
помечается `document_volume_not_confirmed`, непрочитанный объём при заявленном ожидании помечается
(«не подтверждено», а не «ноль»), отсутствие ожидания не является провалом. Результат чтения
ПОДМЕНЁН, поэтому живой КОМПАС не нужен. Живая строка — `E08.04`.

## <a id="suppression-restore-comparison"></a>Сверка снятия подавления с состоянием до подавления - таблица без КОМПАС (09.10.2026)

**Что доказывается.** `SuppressionRestorePolicyTests` — решающая функция
`SuppressionRestorePolicy.Compare` (`Domain/Geometry`): возврат к записанному состоянию — `Matched`;
расхождение объёма (в том числе разобранный случай 79214,60183660254 → 79999,99999999999) —
`Mismatched` с разностью; то же при равном объёме, но другом числе тел или граней; расхождение внутри
допуска — `Matched`. Сверка НЕДОСТУПНА (`Unavailable`) и это НАЗВАНО, когда записи нет, когда ссылка
другая, когда ревизия разошлась, когда состояние прочитано не полностью. Непрочитанный объём НЕ
считается ни нулём, ни совпадением; «нет тел» (0, 0, 0) — ЧИТАЕМОЕ состояние и от непрочитанного
отличается. Запись и подмена результата чтения делают живой КОМПАС ненужным.

**Живые строки.** Группа SR (`scripts/mcp-smoke.py --suppress-repeat K`): `SR.<постановка>.repeat`
(K циклов в одном сеансе), `.control` (свежий сеанс), `.declared_correct` и `.declared_wrong`
(заявленное ожидание), `.comparison_unavailable` (мутация между подавлением и снятием).


## <a id="declared-expectation-rule"></a>Единое правило заявленного ожидания (решение заказчика, 09.10.2026)

**Что проверяется модульно.** `DeclaredExpectation.Evaluate` — чистая функция двух чисел, поэтому
все её ветви проверяются без КОМПАС (`DeclaredExpectationTests`): ожидание не задано (ни
подтверждение, ни отказ); задано и совпало; задано и не совпало (отказ с числами и кодом
`declared_expectation_not_confirmed`, `partial_effects=true`, ревизия в `details`); задано, но
сравнить нечем (НАЗВАННЫЙ пробел, не отказ); граница допуска (ровно допуск — подтверждено, чуть
больше — нет).

**Граница допуска и плавающая точка.** Проверка «ровно на допуске» ставится там, где арифметика
точна (абсолютный пол `1e-3`), а на реальном объёме берутся значения строго внутри и строго снаружи:
у самой границы двоичное представление делает сравнение неотличимым от «чуть больше».

**Что модульным не проверяется и названо.** Живые строки — по одной на каждый инструмент с неверным
заявленным ожиданием (отказ с числами) и с верным (успех) — идут через прибор, а не через модульные
тесты: они проверяют, что отказ доходит до клиента по проводу, а не только что функция вернула
вердикт.

## <a id="declared-expectation-order-and-reread"></a>Порядок сверки, повторное чтение и бюджет поиска владельца (наряд PRE_RELEASE_0_6_0, 09.10.2026)

**Повторное чтение объёма (`DeclaredExpectationTests`).** Третьим доводом `Evaluate` идёт ПОВТОР
ИЗМЕРЕНИЯ. Тесты держат четыре ветви: подтверждённое первое чтение повтора НЕ делает (счётчик
обращений делегата равен нулю); оба чтения сходны и оба не совпали — ОТКАЗ; чтения РАЗОШЛИСЬ —
НАЗВАННЫЙ пробел с обоими числами в причине (`declared_expectation_reads_diverged`), не отказ;
второе чтение подтвердило ожидание — подтверждение.

**Снятие подавления (`SuppressionRestorePolicyTests`).** Состояние «до подавления» читается дважды:
расхождение даёт `Unavailable` с причиной «разошлись», а не отказ; согласие — обычное сравнение.
Состояние «после снятия» при несовпадении перечитывается: второе чтение подтвердило — `Matched`;
оба чтения сходны и оба мимо — `Mismatched`; чтения разошлись — `Unavailable`.

**Бюджет поиска владельца (`FileLockOwnerLookupTests`).** Подменяемый поиск (`Func<string,string?>`)
проверяется без Restart Manager и без занятого файла: названный владелец; «никого не нашли» — с
причиной, а не молчаливый null; НЕ уложившийся в бюджет поиск ПОКИНУТ, и вызов возвращается в
порядке бюджета, а не пробы (проба спит 5 с при бюджете 50 мс); отказавший поиск назван типом
исключения. Границы бюджета: сотни миллисекунд, далеко ниже бюджета синхронизации Хоста.

**Порядок отказов модульным не проверяется и назван.** Что отказ «маршрут не применён» идёт первым,
видно только на живом COM-маршруте (строка `L11`): функция сравнивает два числа и о порядке ничего
не знает. Проверка — живой прогон группы `L11`, а не модульный тест.

## <a id="evidence-rebuild"></a>Пересоздание доказательств после чистки `scratch/` (наряд EVIDENCE_REBUILD, 10.10.2026)

Наряд `REPO_SLIMMING` удалил из `scratch/` безвозвратно (`shutil.rmtree`) файлы, на которые
ссылались машинные файлы покрытия: список «оставить» строился только по ссылкам из
`docs/STATUS.md`. Решение заказчика 10.10.2026 - доказательства **пересоздать повторным прогоном**,
а не восстанавливать. `scripts/emit-coverage-matrix.py` показывал **98** нарушений «ссылка на
доказательство не разрешается» по **11** уникальным путям.

**Постоянное место доказательств.** `docs/acceptance/evidence/<ГГГГММДД>-<группа>-<коммит>/` с
файлом группы и `run.json` (каталог не публикуется: `.gitignore` исключает `/docs/acceptance/`).
`scratch/` остаётся только для черновиков; сторож -
`python scripts/check-evidence-links.py` (падает, если машинный файл ссылается на путь, которого
нет). Правило записано в `AGENTS.md` («Карта для агента», «Версии и выпуски»).

**Прогон.** Release-сборка коммита `3e3290f`, дерево чистое, группы запускались по одной
(`scripts/mcp-smoke.py --<группа>-only --host <Release>`):

| Старый путь | Новый путь | Группа | Строк | Итог |
|---|---|---|---|---|
| `scratch/mcp-smoke/rotation-acceptance.json` | `docs/acceptance/evidence/20261010-rotation-3e3290f/rotation-acceptance.json` | `rotation` | 108 | PASS 108 |
| `scratch/mcp-smoke/fillet-acceptance.json` | `docs/acceptance/evidence/20261010-fillet-3e3290f/fillet-acceptance.json` | `fillet` | 50 | PASS 50 |
| `scratch/mcp-smoke/b3-acceptance.json` | `docs/acceptance/evidence/20261010-b3-3e3290f/b3-acceptance.json` | `b3` | 84 | PASS 84 |
| `scratch/mcp-smoke/b3l-acceptance.json` | `docs/acceptance/evidence/20261010-b3l-3e3290f/b3l-acceptance.json` | `b3l` | 25 | PASS 25 |
| `scratch/mcp-smoke/b3m-acceptance.json` | `docs/acceptance/evidence/20261010-b3m-3e3290f/b3m-acceptance.json` | `b3m` | 122 | PASS 122 |
| `scratch/mcp-smoke/b3c-acceptance.json` | `docs/acceptance/evidence/20261010-b3c-3e3290f/b3c-acceptance.json` | `b3c` | 34 | PASS 34 |
| `scratch/mcp-smoke/delivery-b4-20260920/b4-acceptance.json` | `docs/acceptance/evidence/20261010-b4-3e3290f/b4-acceptance.json` | `b4` | 129 | PASS 129 |
| `scratch/mcp-smoke/delivery-b5-20260920/b5-acceptance.json` | `docs/acceptance/evidence/20261010-b5-3e3290f/b5-acceptance.json` | `b5` | 100 | PASS 100 |
| `scratch/drw-runs/20261006-rework3/drawing-acceptance.json` | `docs/acceptance/evidence/20261010-drawing-3e3290f/drawing-acceptance.json` | `drawing` | 75 | PASS 53 · **FAIL 22** |
| `scratch/acceptance-b3-targeting.log` | снята (§2.4) | - | - | - |
| `scratch/b3-area-extra3.log` | снята (§2.4) | - | - | - |

**Журналы разовых прогонов (§2.4).** `acceptance-b3-targeting.log` и `b3-area-extra3.log` не были
файлами группы. Строки, на них ссылавшиеся (`SM-15.*`, `SM-16.*`, `SM-17.*`), проверяются строками
`B3.*`/`B3L.*`/`B3M.*` групп `b3`/`b3l`/`b3m`/`b3c`, ссылки на которые у этих строк уже были;
обе ссылки на журналы сняты, подстановки чужой проверки не делалось.

**Перепривязка.** 36 строк матрицы перепривязаны к новым файлам (их собственные имена строк
прибора все PASS); `meta.evidence_rebuilt` несёт дату, коммит, причину и таблицу `old_to_new`.
Нарушений у `emit-coverage-matrix.py` стало **12** (было 98).

**12 строк НЕ перепривязаны, и это находка, а не умолчание.** Строки группы DRW
(`DRW-01.views.create_standard`, `DRW-02.views.list`, `DRW-03.dimension.add`,
`DRW-03.dimension.add.linear/.radial/.diametral`, `DRW-04.title_block.set`,
`DRW-06.technical_demand`, `dep.drawing.source_file`, `dep.drawing.view_address`,
`dep.drawing.revisions`, `dep.drawing.idempotency`) в новом прогоне **не PASS**: 22 строки из 75
отказывают `WRONG_DOCUMENT_KIND` с сообщением «трёхмерный маршрут к нему не применим».

Причина измерена и названа: коммит `e578b47` (наряд `CLIENT_BUGS_20261010`, часть D) ввёл
`_session.TryCountFeatures(document)` в `TaggedAfter` **до** мутации и **вне** `try`. Проба идёт
маршрутом `DocumentEntry.PartNow()` → `Require3D()`, а у чертежа `Document` (API5 `ksDocument3D`)
пуст по построению, поэтому проба бросает `WRONG_DOCUMENT_KIND`; ловится только
`COMException`/`InvalidCastException`, и контрактное исключение выходит наружу. Через `TaggedAfter`
идут все мутации чертежа (`drawing.create_views`, `drawing.add_dimension`, `drawing.set_title_block`,
`drawing.set_technical_demand`, `drawing.rebuild_views`), поэтому отказывает каждая. Чтения и
`drawing.export` идут через `Tagged` и работают - отсюда 53 PASS.

Регрессию не поймал предыдущий наряд: строки группы DRW в полный (вертикальный) прогон не входят
(`grep -c 'DRW'` по `full.json` = 0), а приёмка `CLIENT_BUGS_20261010` шла полным прогоном.

Ссылки этих 12 строк оставлены прежними (на удалённый файл) - так они ВИДНЫ находкой, а не
выглядят закрытыми. Решение о правке кода продукта - за заказчиком: код продукта этим нарядом не
меняется (`EVIDENCE_REBUILD_DEVELOPER_PROMPT.md` §3).
