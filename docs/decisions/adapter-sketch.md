# Sketch and auxiliary geometry - решения и измерения

Модуль: `src/KompasMcp.Api5Adapter/Api5Session.SketchEntities.cs`,
`Api5Session.SketchPlane.cs`, `Api5Session.SketchStatus.cs`,
`Api5Session.AuxGeometry.cs`. Здесь - история правок, вынесенная из кода. Действующие правила
остались в коде под метками `INVARIANT:` / `DOC:` / `MEASURED:` / `LIMIT:` и ссылкой
`History: docs/decisions/adapter-sketch.md#<anchor>`.

## <a id="sketch-entities"></a>Сущности эскиза как объекты с устойчивым адресом

**Что было.** Действия `discover`, `read` и `edit` зависимости `dep.sketch.entities` стояли
отказами, и причина называлась прямо: перечисления сущностей эскиза в продукте нет, адреса, по
которому читать одну сущность, тоже нет, а схема правки эскиза принимает режим и НОВЫЙ НАБОР
примитивов целиком - то есть пересоздаёт контур, а не правит сущность.

**Что измерено.** Адрес сущности - строка, которую выдаёт `IKompasDocument1.GetObjectId` и
принимает обратно `IKompasDocument1.FindObjectById`. Индекс коллекции адресом не является:
перестроение его сдвигает, и «N-й объект» перестал бы указывать на ту же сущность после первой же
мутации.

**Что решено.** Введены `kompas_list_sketch_entities` и `kompas_edit_sketch_entity`. Адрес -
строка `GetObjectId`; правка подтверждается ПОВТОРНЫМ РАЗРЕШЕНИЕМ АДРЕСА, а не кодом возврата: у
`delete` подтверждением служит то, что адрес больше не разрешается, у `set_layer` - прочитанный
номер слоя.

## <a id="sketch-entity-delete"></a>Режим `delete` - ровно одна сущность

**Что было.** Схема правки эскиза чужой операции (`delete_entities`) пересобирает контур целиком.

**Что решено.** Режим `delete` адресной правки удаляет РОВНО ОДНУ сущность, названную адресом, а
остальные остаются на своих местах - это и есть различающий признак адресности. Вход в
редактирование берётся на ЗАПИСЬ: `BeginEdit()`, а не `BeginEditEx(true)`, иначе правка получила бы
отказ, неотличимый от отсутствия возможности.

## <a id="fragment-document"></a>Адрес принадлежит документу фрагмента (21.09.2026)

**Что было.** Прежняя редакция брала `DocumentOf(sketch7)` - документ ДЕТАЛИ - и разрешала адрес
на нём. Это была неверная посылка.

**Что измерено.** 21.09.2026 различающим замером по получателю и родителю
(`scratch/_probe_dse_dpt.py`, бинари `publish-deproutes-20260921-e`): документ детали отвечает
пустой строкой на все четыре сущности, документ фрагмента выдаёт непустой адрес. `FindObjectById`
объявлен на `IKompasDocument1`, а у `IFragmentDocument` (44 члена, IID
`{E19CE626-DF9C-48C4-A83D-3E3BC7F0DACA}`) его нет.

**Что решено.** Документ-получатель берётся ВНУТРИ сеанса правки, из `BeginEdit()`. Фрагмент
существует только между `BeginEdit()` и `EndEdit()`, поэтому и разрешение адреса, и подтверждение
правки повторным разрешением живут внутри `ApplySketchEntityEdit`.

## <a id="update-return"></a>Возврат `Update()` - показание, а не приговор (21.09.2026)

**Что было.** Прежняя редакция объявляла не-true возврат `IDrawingObject.Update()` отказом правки и
получала `GEOMETRY_FAILED` при живом объекте.

**Что измерено.** 21.09.2026 (`scratch/_probe_dse_edit.py` по бинарям
`publish-deproutes-20260921-f`): `IDrawingObject.LayerNumber = 7` принимается, а `Update()`
возвращает не-true.

**Что решено.** Возврат `Update()` записывается как ПОКАЗАНИЕ; признак применения берётся ЧТЕНИЕМ
ЗНАЧЕНИЯ ОБРАТНО в той же сессии. Правило проекта «успешный код не равен применённой правке» верно
и в обратную сторону: неуспешный код не доказывает, что правка не применилась.

## <a id="sketch-plane"></a>Смена опорной плоскости эскиза

**Что было.** Маршрут API7 (`ISketch.Plane`) на поставленной сборке не отвечает: измерено «эскиз не
приводится к `ISketch`» (проба `--sketch-plane`, шаг SP.3).

**Что измерено.** Первый прогон пробы дал `SetPlane(xz) = True` и неизменившийся габарит - «принято
и не применено». Лестница ступеней с чтением габарита ПОСЛЕ КАЖДОЙ: `definition.EndEdit()` - не
применяет; `sketch.Update()` - применяет; `part.RebuildModel()` и `document.RebuildDocument()` после
него не добавляют ничего (`moved_by_this_route = false` у обеих). Отказ не-плоскости: ядро ПРИНИМАЕТ
в опору плоскую грань (`SetPlane = True`, все шесть граней коробки перепривязывают зависимое тело) и
ОТВЕРГАЕТ ребро и тело (`SetPlane = False`) - шаги SP.8/SP.9.

**Что решено.** Правка идёт API5-членами `ksSketchDefinition.SetPlane`/`GetPlane`; после записи
обязателен `sketch.Update()`, и его имя возвращается в ответе полем `apply_route`. Не-плоскость
отвергается ДО COM по виду ссылки из реестра: ответ ядра на грань продуктом не наследуется, иначе
«грань» стала бы плоскостью по факту принятия её ядром.

## <a id="sketch-status"></a>Определённость эскиза - чтение (17.09.2026)

**Что было.** Статус системы ограничений не читался.

**Что измерено.** 17.09.2026 пробой S (прогон `82880ed0b14a4e299bb0e93d7f8a7f2f`, артефакты
`docs/acceptance/api7/sketch-definition.{md,json}`, PASS 9 · FAIL 0 · UNKNOWN 6), подтверждено на
build 24.0.0.2799. Маршрут: `TransferInterface(sketchEntity, ksAPITypeEnum.ksAPI7Dual, 0)` → `ISketch`
→ `ConstraintsState`. S.7: пятикратное чтение не изменило ни объём (80000), ни тела (1), ни грани
(6), ни рёбра (12). S.5b: пустой эскиз отвечает `ksStateUnknown`.

**Что решено.** Это ЧТЕНИЕ: нет ни `BeginEdit`, ни `EndEdit`, ни `Update`, ни перестроения, ревизия
не поднимается. Числа степеней свободы маршрут не отдаёт - `degrees_of_freedom` всегда `null`.
`ksStateUnknown` - успешный ответ со статусом `unknown`, а не ошибка.

## <a id="aux-geometry"></a>Вспомогательная геометрия как объекты модели

**Что было.** Прежние маршруты выражали плоскость, ось и точку числами в аргументах чужого вызова
(ось вращения - двумя точками, опора эскиза - именем базовой плоскости).

**Что измерено.** Записанными отказами: строка `DEP.DAX.01.discover` прежнего прогона называет
положительный контроль - ось задаётся двумя ЧИСЛОВЫМИ точками в аргументах, объекта нет. Всё
построение - шаг 0 наряда продуктовых маршрутов (отчёт
`DEPENDENCIES_PRODUCT_ROUTES_STEP0_REPORT_20260921.md` §6.1–6.3); имена членов взяты прибором
`tools/KompasMcp.InteropScan` из поставленного interop'а целевой сборки.

**Что решено.** Плоскость, ось и точка создаются и читаются как ОБЪЕКТЫ модели документированным
API7 (`kompas_create_aux_geometry`, `kompas_list_aux_geometry`), закрывая `dep.refs.planes`,
`dep.refs.axes`, `dep.refs.points_axes`. Число в аргументе перечислению недоступно, поэтому
требование закрывается перечислением.

## <a id="aux-named-plane"></a>Именованная опора - по типу, а не перечислением (21.09.2026)

**Что было.** «Найти xy перечислением» `IPlanes3D`.

**Что измерено.** 21.09.2026 (`scratch/_probe_dpl_offset.py`, бинари поставки
`artifacts/publish-deproutes-20260921-b`): в детали с готовым телом (ревизия 4, плита 40×40×10)
`IAuxiliaryGeomContainer.GetPlanes3D` отдаёт `Count = 0` - стандартных плоскостей в коллекции
`IPlanes3D` нет вовсе, тогда как созданные инструментом плоскости появляются (`plane_count`=1 после
опоры на грань).

**Что решено.** Именованная опора берётся документированным `ksPart.GetDefaultEntity` по типу
`o3d_planeXOY/XOZ/YOZ` и переносится в API7 штатным `Api7Bridge.TransferTo7`. Перечисление вернуло бы
«не найдено» на исправном документе, то есть отказ прибора, выданный за отказ продукта; другой
объект вместо запрошенной опоры не подставляется.


## <a id="sketch-status-redundancy"></a>Живой контроль `ksStateUnresolvedRedundancy` не получен

MEASURED: currently `false`, and that is a measured fact, not "not done yet" - probe S read 46 shipped
sketches and got three values (0/1/2); state 3 was NEVER encountered and no control was built for it (no
constraint-write route was found in any of the four branches). While the flag is false, value 3 is
published conservatively as `unknown` with reason `unresolved_redundancy_not_verified` - "declared in the
enum" is not passed off as "measured on the product". A mock test of the conversion is not grounds to
raise this flag.

DOC: the documented route to a standard plane as an OBJECT is <c>ksPart.GetDefaultEntity</c> by type
<c>o3d_planeXOY/XOZ/YOZ</c>; sketch creation already uses it (<c>Api5Session.Geometry.cs:ResolvePlaneEntity</c>),
and this route is measured by acceptance on rows <c>DEP.DPL.01.discover</c> and all sketch modes; the
obtained object is transferred to API7 by the standard <c>Api7Bridge.TransferTo7</c> and substituted into
<c>IPlane3DBy*.BasePlane</c>. MEASURED 21.09.2026, probe <c>scratch/_probe_dpl_offset.py</c> on the shipped
binaries <c>artifacts/publish-deproutes-20260921-b</c>: in a part that already has a body (revision 4, a
40×40×10 plate built), <c>IAuxiliaryGeomContainer.GetPlanes3D</c> reports <c>Count = 0</c> - there are NO
standard planes in the <c>IPlanes3D</c> collection at all, while tool-created planes do appear in it
(<c>plane_count</c>=1 after a face support). So "find xy by enumeration" is not a route but a wish: there is
nothing to enumerate.
