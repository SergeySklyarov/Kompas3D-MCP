# Соответствие MCP официальной документации КОМПАС-3D v24

Аудит от 2026-09-21 (+02:00, часы машины). Цель — **КОМПАС-3D v24**, сборка 24.0.0.2799, установка `D:\Programs\KOMPAS-3Dv24` (`kompas.exe` `8c17b394da430db8…`), interop `Interop.Kompas6API5, Version=1.0.0.0, Culture=neutral, PublicKeyToken=810e8d71c7a3e510`.

Документация — официальная справка SDK целевой версии <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/>: прочитано страниц 2178 (читалась по проводу; зеркало справки лежит вне поставки).

Поставка, на которой оценивалась реализация: `artifacts/publish-journalfix-20260921` — Host.dll `e8c08d41f9aadbf12c926f3375311121eef826d1c5a3efcdfd271dd063cbfa6b`, Worker.dll `0e28f4b449fc93c177997cd1f99e89742242ad31119a19438d4f4f935893877f`, Api5Adapter.dll `98c59a2e725efeccc6998b17e752ff9632991cfb33f159cec79940e6558b96b9`; полный прогон 1086 строк, отказов 0. Паспорт: **не издан** — наряд §0.4 запрещает издавать его до повторной клиентской приёмки; это состояние, а не пробел. Опознание поставки взято измерением: `scratch/mcp-smoke/delivery-journalfix-20260921/package-check.json`. Поставка взята по измерению, а не по времени изменения файлов. В колонке «проверка и хеш поставки» стоит короткий хеш Host.dll — тот же бинарь, что в измерении.

**Два независимых вердикта на маршрут.** Документация: Подтверждено / Противоречие / Не подтверждено. Реализация: Подтверждено / Дефект / Недостаточно проверки / Не реализовано. «Документировано» означает **страницу официальной справки**, а не наличие члена в TLB, interop или IntelliSense: найденный в TLB член сам по себе маршрут официальным не делает.

**Итог одной строкой.** Проверено 50 инструментов и 74 строк покрытия; документация подтверждена у 70 строк, реализация — у 56; 3 строк объявлены планом, 15 — «Недостаточно проверки» (режимов 0, общих зависимостей 15). Находок 11: F-01 — исправленный дефект продукта (мёртвая ветка удалена); F-02 — исправленный дефект продукта (мёртвая ветка удалена, состояние сохранённости отделено от ревизии); F-03 — состояние объёма будущей работы (не дефект); F-04 — названная граница непубликуемого поля (не дефект); F-05 — исправленный дефект доказательств (не дефект продукта); F-06 — исправленный дефект прибора аудита; F-07 — исправленный дефект прибора аудита; F-08 — названный пробел трассируемости доказательств (не дефект продукта); F-09 — исправленный дефект продукта (знак дуги не доходил до конечного угла); F-10 — исправленный дефект продукта (объявленный диапазон углов дуги шире выражаемого; первая редакция правки была НЕ аддитивной, и это поймал контроль зонда); F-11 — исправленный дефект прибора ПРИЁМКИ (строка `MANIA.17` читала объём ПЕРВОГО тела документа при накапливающихся телах и проходила, не различая ничего; объявленный диапазон `sweep_deg` шире принимаемого — расхождение ОБЪЯВЛЕНИЯ). Утверждение «MCP полностью согласуется с документацией» **не делается**: у 4 строк маршрут не подтверждён документально, потому что его нет в коде.

## 1. Сверка полноты

| Что сверялось | Источник | Результат |
|---|---|---|
| Инструменты в `tools/list` | поставленный Host по проводу | 50 |
| Схемы в `schemas/` | файлы поставки | 50 из 50; имена свойств совпадают с проводом у 50, значимая форма схемы (типы, `required`, `enum`, вложенность, `additionalProperties`, разрешённые `$ref`) — у 50 |
| Строки матрицы покрытия | `coverage/solid-v24/matrix.json` | 74 |
| Строки со ссылкой на схему инструмента | там же | 57 из 74 (у 17 поле пустое) |
| Присутствие инструмента в матрице | сверка имён: собственная строка ЛИБО объявленный маршрут строки | собственной строкой 43, объявленным маршрутом 15; без обоих способов — нет |
| Вызываемые COM-маршруты | исходники `KompasMcp.Api5Adapter`, тип получателя установлен | 305 (API5 137 / API7 168); страница члена или список членов интерфейса найдены у 243, не подтверждено 62; унаследованных 9, индексаторов 6 |
| Обращения без типа получателя | там же | 1254 (из них 362 — члены, уже покрытые типизированным маршрутом); получателей НЕ COM — 918 |
| Отражённые и строковые обращения | там же | 1 |
| Числовые идентификаторы типов объектов | таблица `obj3dtype.html` | совпало 35, нет в таблице 0 |
| Строки приёмки в доказательствах | 5 групповых отчётов поставки | 943 уникальных, вердикты: PASS |
| Ссылки на страницы справки в этом документе | проверка каждой по проводу | открылось 207 из 207; битых 0; не проверено 0 |

Строка покрытия связывается с инструментом по семейству: матрица не даёт этой связи для 17 строк из 74, поэтому маршруты читались ещё и из исходников адаптера — иначе часть вызовов выпала бы из аудита только потому, что забыта в матрице (находка F-05).

Число проверок берётся из полного прогона — 1086 строк; 943 уникальных строк приёмки, собранных из 5 групповых отчётов, — это те же проверки по частям, и с полным прогоном они не складываются. Отдельные сверки этого аудита (`tools/list` по проводу, страницы справки, таблица `obj3dtype.html`, чтение исходников) показаны своими строками выше и в приёмку не входят.

Ни одна строка не выдана за проверенную без записи приёмки: строк с вердиктом реализации «Подтверждено» и без строк приёмки — 0 (измерено сверкой вердикта с доказательствами).

Ссылки документа проверены по проводу: открылось 207 из 207, битых 0, не проверено 0. Битая ссылка считается дефектом прибора, а не подтверждением документации, поэтому проверялась каждая.

## 2. Проверенные семейства

Маршрут подтверждён документально, реализация проверена на бинарях поставки. «Действия» — из матрицы: сколько из десяти действий строки имеют состояние `verified`/`not_applicable`. Строки общих зависимостей этих семейств — в разделе 4.

### SM-02 — Выдавливание API5 (базовое / приклеивание / вырезание)
**Инструмент:** `kompas_extrude`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs — создание/правка признака выдавливания`; `src/KompasMcp.Api5Adapter/Api5Session.SolidRead.cs — чтение признака`.
**Существенные условия:** Тип объекта берётся из документированной таблицы Obj3dType (24/25/26); глубина и признак «на всю длину» — параметры SetSideParam(etBlind=0 / etThroughAll=1) из Direction_Type / End_Type; область применения у приклеивания и вырезания задаётся ChooseBodies + BodyCollection; реальный объект появляется после Create, перестроение — RebuildDocument.
**Документированные интерфейсы и члены:**
- `ksPart.NewEntity` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kspart_newentity.html>
- `ksBaseExtrusionDefinition.SetSideParam` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseextrusiondefinition_setsideparam.html>
- `ksBaseExtrusionDefinition.GetSideParam` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseextrusiondefinition_getsideparam.html>
- `ksBaseExtrusionDefinition.GetThinParam` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseextrusiondefinition_getthinparam.html>
- `ksBaseExtrusionDefinition.SetSketch / GetSketch` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseextrusiondefinition_setsketch.html>
- `ksBossExtrusionDefinition.ChooseBodies` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbossextrusiondefinition_choosebodies.html>
- `ksEntity.Create / Update` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentity_create.html>
- `ksDocument3D.RebuildDocument` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument3d_rebuilddocument.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-02.base_extrusion.blind` | 10/10 verified | 28 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.cut_extrusion.through` | 10/10 verified | 39 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.base_extrusion.additional_body` | 10/10 verified | 15 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.base_extrusion.direction_negative` | 10/10 verified | 12 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.boss_extrusion.blind` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.boss_extrusion.explicit_target_body` | 10/10 verified | 13 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.cut_extrusion.blind` | 10/10 verified | 13 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.cut_extrusion.explicit_target_body` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-02.cut_extrusion.direction_symmetric` | 10/10 verified | 11 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-09 — Скругление (создание API5, чтение и правка API7)
**Инструмент:** `kompas_fillet`, `kompas_get_feature`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.FilletEdit.cs — правка набора рёбер и радиуса`; `src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs — создание скругления`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — IFillet`.
**Существенные условия:** Правка набора рёбер и радиуса идёт ТОЛЬКО маршрутом API7 (IFillet.BaseObjects — полная замена набора; IFillet.Radius1) с обязательным Update() и перестроением; маршрут API5 (ksFilletDefinition.Array → Clear → Add) правку набора не даёт — измерено и названо. Расширение набора не выражается ни одной из двух валют.
**Документированные интерфейсы и члены:**
- `ksFilletDefinition.radius / tangent / Array` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksfilletdefinition_props.html>
- `IFillet.Radius1 / Radius2 / Tangent / BuildingType / BaseObjects` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ifillet_propers.html>
- `ksEntityCollection.GetCount / GetByIndex / FindIt / Add / Clear` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentitycollection_methods.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-09.fillet.constant_radius_body_edges` | 10/10 verified | 25 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-09.fillet.read_radius_back` | 10/10 verified | 18 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-09.fillet.change_radius_in_place` | 10/10 verified | 15 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-09.fillet.change_edge_set` | 10/10 verified | 35 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-07 — Родное отверстие API7
**Инструмент:** `kompas_hole`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Hole.cs — Hole()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — создание отверстия и позиционирование`.
**Существенные условия:** Форма и дно — ksHTBase / ksHTCounterbore / ksHTCountersinking и ksEFFlat / ksDTValue / ksDTReachThrough; позиция — Point3DParamSurface + ksOffsetByCoords и Offset1/Offset2; ось по нормали грани — BaseSurface + Perpendicular; параметры пишутся до Update().
**Документированные интерфейсы и члены:**
- `IHoles3D.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iholes3d_add.html>
- `IHoleDisposal.BaseSurface / Perpendicular / Point3DParamSurface` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iholedisposal_propers.html>
- `ICountersinkHoleParameters.*` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/icountersinkholeparameters_propers.html>
- `ISpotfacingHoleParameters.*` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ispotfacingholeparameters_propers.html>
- `ksDepthTypeEnum / ksEndFaceTypeEnum / ksHoleTypeEnum` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdepthtypeenum.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-07.native_hole.through_cylindrical` | 10/10 verified | 25 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-07.native_hole.blind_flat_bottom` | 10/10 verified | 30 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-07.native_hole.through_counterbore` | 10/10 verified | 26 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-07.native_hole.through_countersink` | 10/10 verified | 27 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-07.native_hole.axis_by_face_normal` | 10/10 verified | 16 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-07.native_hole.position_off_origin` | 10/10 verified | 25 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-11 — Фаска (API5 и API7)
**Инструмент:** `kompas_chamfer`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Chamfer.cs`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — IChamfer`.
**Существенные условия:** Способ построения задаётся перечислением (два расстояния / расстояние и угол); направление — Direction; правка угла идёт маршрутом API7 (IChamfer.Angle) с Update() и RebuildModel(); производный катет не записывается, если клиент его не задал.
**Документированные интерфейсы и члены:**
- `ksChamferDefinition.SetChamferParam / GetChamferParam` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kschamferdefinition_setchamferparam.html>
- `ksChamferDefinition.Array` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kschamferdefinition_array.html>
- `IChamfer.BuildingType / Distance1 / Angle / Direction` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ichamfer.html>
- `ksChamferBuildingTypeEnum / ksChamferTwoSides / ksChamferSideAngle` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kschamferbuildingtypeenum.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-11.chamfer.mode_two_distances` | 10/10 verified | 30 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-11.chamfer.mode_distance_angle` | 10/10 verified | 24 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-11.chamfer.mode_direction` | 10/10 verified | 17 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-03 — Вращение API7 (базовое / приклеивание / вырезание)
**Инструмент:** `kompas_rotated`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Rotated.cs — Rotated()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — TryCreateRotated, ось по двум точкам`.
**Существенные условия:** Тип объекта — 27/28/29 по таблице Obj3dType; сечение и ось задаются до Update(); угол — Angle[true] в градусах, при частичном угле Angle[false] обнуляется; вид операции (объединение/вырезание) — OperationResult из ksOperationResultEnum; мост API5→API7 документирован (ksGetApplication7 + TransferInterface).
**Документированные интерфейсы и члены:**
- `IModelContainer.Rotateds.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/irotateds_add.html>
- `IRotated.Profile / Axis / Angle / Direction / RotatedType` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/irotated_props.html>
- `IRotated1.OperationResult` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/irotated1_operationresult.html>
- `IAuxiliaryGeomContainer.Axes3D + IAxis3DBy2Points.Point1/Point2` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iaxis3dby2points_props.html>
- `KompasObject.ksGetApplication7 / TransferInterface` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksgetapplication7.html>
- `IPart7.RebuildModel` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipart7_rebuildmodel.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-03.base_rotated.full_turn` | 10/10 verified | 26 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-03.base_rotated.partial_angle` | 10/10 verified | 26 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-03.base_rotated.axis_explicit` | 10/10 verified | 27 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-03.boss_rotated.full_turn` | 10/10 verified | 45 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-03.cut_rotated.full_turn` | 10/10 verified | 31 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-15 — Булевы операции API7
**Инструмент:** `kompas_boolean`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.SolidOps.cs — SolidBoolean()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — IBoolean`.
**Существенные условия:** Тип операции — ksUnion/ksDifference/ksIntersect из перечисления (значения 3/2/1, НЕ из пользовательской справки); цель и инструменты — перенесённые в API7 объекты; сохранение инструментов — SaveCopyModifyObjects. Положительный union произвольных разнесённых тел не является требованием: обычное объединение требует пересечения или общей поверхности.
**Документированные интерфейсы и члены:**
- `IModelContainer.Booleans.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imodelcontainer.html>
- `IBoolean.BaseObject / ModifyObjects / BooleanType` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iboolean_propers.html>
- `IBoolean.SaveCopyModifyObjects / SaveCopyBaseObject` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iboolean_savecopymodifyobjects.html>
- `ksBooleanType (ksUnion/ksDifference/ksIntersect)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksoperationresultenum.html>
- указатель (не подтверждение маршрута): `IBoolean.SaveCopyBaseObject` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iboolean_savecopybaseobject.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Не подтверждено** — возможность запланирована: маршрут в коде отсутствует и в матрице не заявлен, поэтому документированность МАРШРУТА в этом аудите не подтверждена. Документированные интерфейсы семейства в справке v24 есть — это указатель для будущей реализации, а не подтверждение маршрута / маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-15.subtract` | 10/10 verified | 15 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-15.intersect` | 10/10 verified | 15 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-15.union.mode_save_tools` | 10/10 verified | 12 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-15.union.mode_multi_tools` | 10/10 verified | 12 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-15.union.mode_save_base_copy` | 0/10 verified; открыты: `discover`, `create`, `read`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `negative_tests`, `geometry_validation` | строк приёмки нет | Не реализовано | не требуется в этом аудите: строка объявлена планом и за реализованную не выдаётся |
| `SM-15.union.explicit_target_and_tools` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-16-split — Разделение тела (API7)
**Инструмент:** `kompas_split`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.SolidOps.cs — SolidSplit()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — ISplitSolid`.
**Существенные условия:** Опора разделения — секущие объекты (эскизы и поверхности), читаются и правятся переносом трёх точек СОБСТВЕННОЙ опоры признака.
**Документированные интерфейсы и члены:**
- `IModelContainer.SplitSolids.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imodelcontainer.html>
- `ISplitSolid.CutObjects` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/isplitsolid_props.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-16.split.mode_keep_parts` | 10/10 verified | 18 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-16.split.plane_tool` | 10/10 verified | 19 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-16-cut — Отсечение по одну сторону (API7)
**Инструмент:** `kompas_cut_by_plane`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.SolidOps.cs — SolidCutByPlane()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — ICut`.
**Существенные условия:** Способ построения — ksCutByPlane; сторона — Direction (читается как записана); опора — CutObject.
**Документированные интерфейсы и члены:**
- `IModelContainer.Cuts.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imodelcontainer.html>
- `ICut.BuildingType / CutObject / Direction` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/icut_propers.html>
- `ksCutBuildingTypeEnum` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kscutbuildingtypeenum.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-16.cut_by_plane.mode_side_removed` | 10/10 verified | 28 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-17 — Перенос и поворот тела (API7)
**Инструмент:** `kompas_get_feature`, `kompas_reposition`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.SolidOps.cs — SolidReposition()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — ReadPlacement / WritePlacement`; `src/KompasMcp.Domain/Geometry/EulerOrientation.cs — ось+угол ↔ углы Эйлера`.
**Существенные условия:** Размещение выражается ПАРАМЕТРАМИ: OrientationType = ksEulerCorners + три угла Эйлера (порядок композиции справка задаёт рисунком — проверен по матрице, порядок PNR, градусы) и ParameterType = ksPDisplace + DX/DY/DZ. Точки оси поворота среди документированных членов цепочки нет, и она не является свойством размещения.
**Документированные интерфейсы и члены:**
- `IModelContainer.BodyRepositions.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ibodyrepositions_add.html>
- `IBodyReposition.RepositionBody / Position` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ibodyreposition_propers.html>
- `ILocalCoordinateSystem.OrientationType` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcoordinatesystem_props.html>
- `IPoint3D.ParameterType (унаследован в ILocalCoordinateSystem)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3d_props.html>
- `ILocalCoordinateSystem.LocalCSParameters → ILocalCSEulerParam` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcseulerparam_props.html>
- `IPoint3D.Parameters → IPoint3DParamDisplace` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3dparamdisplace_props.html>
- `ILocalCSEulerParam.PrecessionAngle / NutationAngle / RotationAngle` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcseulerparam_props.html>
- `IPoint3DParamDisplace.DX / DY / DZ` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3dparamdisplace_props.html>
- `ksEulerCorners / ksPDisplace / ksOrientationTypeEnum` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksorientationtypeenum.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-17.reposition.translate_by_vector` | 10/10 verified | 27 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-17.reposition.rotate_about_axis` | 10/10 verified | 27 строк · PASS · `e8c08d41` | Подтверждено | — |

### SKETCH — Эскиз: создание, правка геометрии, базовые плоскости
**Инструмент:** `kompas_create_sketch`, `kompas_edit_sketch`, `kompas_finish_sketch`, `kompas_get_sketch_status`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs — CreateSketch/EditSketch/FinishSketch`; `src/KompasMcp.Api5Adapter/Api5Session.SketchStatus.cs`.
**Существенные условия:** Геометрия эскиза строится функциями 2D API на интерфейсе ksDocument2D внутри BeginEdit/EndEdit; базовая плоскость эскиза — SetPlane/GetPlane.
**Документированные интерфейсы и члены:**
- `ksSketchDefinition.SetPlane / GetPlane` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssketchdefinition_setplane.html>
- `ksSketchDefinition.BeginEdit / EndEdit` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssketchdefinition_beginedit.html>
- `ksDocument2D.ksLineSeg / ksCircle / ksArcByAngle` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument2d_kslineseg.html>
- `ksPlaneOffsetDefinition.SetPlane / GetPlane` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksplaneoffsetdefinition_props.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `AUX-SKETCH.plane_and_profile_lifecycle` | 10/10 verified | 28 строк · PASS · `e8c08d41` | Подтверждено | — |

### PATTERN — Массивы: по сетке, круговой (концентрический), зеркальный
**Инструмент:** `kompas_get_pattern`, `kompas_pattern_circular`, `kompas_pattern_grid`, `kompas_pattern_mirror`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Pattern.cs — создание`; `src/KompasMcp.Api5Adapter/Api5Session.PatternEdit.cs — правка, чтение`; `src/KompasMcp.Api5Adapter/Api7/Api7Pattern.cs — мост API7 (ILinearPattern / ICircularPattern / IMirrorPattern)`.
**Существенные условия:** Массив создаётся через IModelContainer.FeaturePatterns.Add(ksObj3dTypeEnum) и приведение QI к ILinearPattern / ICircularPattern / IMirrorPattern; типизированный вызов, не отражение. ИЗМЕРЕНО ПРОГОНОМ 20.09.2026: у копирования ТЕЛ СВОЙ номер типа в дереве — o3d_BodiesMeshCopy=528 и o3d_BodiesCircularCopy=529, отличный от копирования ОПЕРАЦИЙ (o3d_meshCopy=35, o3d_circularCopy=36); отображение у них ОДНО («Массив по сетке:1»), поэтому различает только тип. Область действия SaveInitialObjects ограничена справкой: свойство работает ТОЛЬКО для o3d_mirrorAllOperation, у прочих операций зеркального копирования возможность скрыть экземпляры отсутствует — проверено на операции 48 (запись false читается обратно true, геометрия не меняется). Углы задаются nullable и пишутся ТОЛЬКО когда переданы: запись нуля вместо неуказанного угла уничтожает умолчание модели. Angle2 — угол МЕЖДУ направлениями сетки (90° прямоугольная, 0° вырождает сетку в линию), Angle1 отводит направление от построенной оси.
**Документированные интерфейсы и члены:**
- `IModelContainer.FeaturePatterns / IFeaturePatterns.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ifeaturepatterns_add.html>
- `IFeaturePattern` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ifeaturepattern.html>
- `IFeaturePatterns` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ifeaturepatterns.html>
- `ILinearPattern (свойства)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilinearpattern_props.html>
- `ICircularPattern (свойства)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/icircularpattern_props.html>
- `IMirrorPattern (свойства)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imirrorpattern_props.html>
- `IMirrorPattern.SaveInitialObjects` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imirrorpattern_saveinitialobjects.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-18.grid.single_row` | 10/10 verified | 15 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-18.grid.rectangular_grid` | 10/10 verified | 15 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-18.grid.operations` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-18.grid.bodies` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-19.circular.full_circle_no_duplicate` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-19.circular.angular_range` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-19.circular.operations` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-19.circular.bodies` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-23.mirror_all.bodies_selection` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-23.mirror_array.selected_operations` | 10/10 verified | 15 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-04 — Кинематическая операция — элемент по траектории (API5)
**Инструмент:** `kompas_get_feature`, `kompas_sweep`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.FeatureEdit.B5.cs — правка режима движения сечения`; `src/KompasMcp.Api5Adapter/Api5Session.FeatureRead.B5.cs — чтение признака`; `src/KompasMcp.Api5Adapter/Api5Session.Sweep.cs — Sweep()`.
**Существенные условия:** Тип базовой кинематической операции — 45 (o3d_baseEvolution) по таблице Obj3dType; фабрика API7 этот тип НЕ документирует (ievolutions_add.html перечисляет допустимыми только o3d_bossEvolution 46 и o3d_cutEvolution 47), поэтому создание идёт ksPart.NewEntity(45) + ksEntity::GetDefinition → ksBaseEvolutionDefinition. В дереве признак виден под номером 46 (o3d_bossEvolution), поэтому опознание идёт ПО ИНТЕРФЕЙСУ ОПРЕДЕЛЕНИЯ, а не по номеру типа. Траектория присоединяется к ksEntityCollection, полученному от PathPartArray(): держатель отвечает System.__ComObject, приведение к ksEntityCollection работает, а отражение по нему членов не находит вовсе. Режим движения сечения — sketchShiftType 0/1/2 (ksevolutionshiftsketchtypeenum.html): на ПРЯМОЙ траектории режимы неразличимы (одно тело), различающая постановка — дуга R50/90°, где ортогональный режим даёт 24674.011002723353, параллельный — 15707.963267948984 (разность 8966.047734774369 мм³). Длина траектории читается GetPathLength(1) ПОСЛЕ построения: тот же вызов до Create() отвечал нулём, и это был дефект прибора, а не факт о продукте. ПРАВКА ВХОДА не выражается ни одним из двух хранилищ, и это измерено на ОБОИХ (шаги B5.15 и B5.22): SetSketch определения API5 принят и не применён; запись в документированное свойство API7 IEvolution.Sketch (объявленный тип KompasAPI7.IModelObject) принята, Update() = true и тоже не применена; запись в ОБА хранилища подряд — не применена; запись ДО первого обращения к API7 — тоже не применена, причём держатель траектории перечисляет подставленный эскиз, то есть запись ДОШЛА. Признак при этом жив, и прибор видит изменения: правка режима применяется и через определение, и через хранилище API7 (15707.963267948984), поэтому «не применено» отличимо от «признак умер». Имя сечения через свойство API7 прибором НЕ ПРОЧИТАНО (значение приходит как System.__ComObject, и ни прямой, ни обратный перенос не даёт ksEntity) — это названный предел прибора, а не факт о продукте, и «не применено» доказано объёмом, а не именем. Поэтому вход строки читается, но не перепривязывается, а sketch_ref отвергается по имени.
**Документированные интерфейсы и члены:**
- `ksPart.NewEntity(45 = o3d_baseEvolution)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kspart_newentity.html>
- `ksBaseEvolutionDefinition (состав интерфейса)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition.html>
- `ksBaseEvolutionDefinition.SetSketch` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_setsketch.html>
- `ksBaseEvolutionDefinition.sketchShiftType` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_sketchshifttype.html>
- `ksBaseEvolutionDefinition.PathPartArray → ksEntityCollection` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_pathpartarray.html>
- `ksBaseEvolutionDefinition.GetPathLength` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_getpathlength.html>
- `ksEvolutionShiftSketchTypeEnum (0/1/2)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksevolutionshiftsketchtypeenum.html>
- `ksEntity.Create` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentity_create.html>
- `ksEntityCollection.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentitycollection_add.html>
- `Obj3dType: o3d_baseEvolution = 45` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/obj3dtype.html>
- указатель (не подтверждение маршрута): `IEvolution` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ievolution.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Не подтверждено** — возможность запланирована: маршрут в коде отсутствует и в матрице не заявлен, поэтому документированность МАРШРУТА в этом аудите не подтверждена. Документированные интерфейсы семейства в справке v24 есть — это указатель для будущей реализации, а не подтверждение маршрута / маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-04.base.mode_orthogonal` | 10/10 verified | 12 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-04.boss` | 0/10 verified; открыты: `discover`, `create`, `read`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `negative_tests`, `geometry_validation` | строк приёмки нет | Не реализовано | не требуется в этом аудите: строка объявлена планом и за реализованную не выдаётся |
| `SM-04.base.single_profile_flat_path` | 10/10 verified | 13 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-05 — Элемент по сечениям (API7)
**Инструмент:** `kompas_get_feature`, `kompas_loft`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.FeatureEdit.B5.cs — правка набора сечений записью в ОБА хранилища`; `src/KompasMcp.Api5Adapter/Api5Session.FeatureRead.B5.cs — чтение признака и сечений`; `src/KompasMcp.Api5Adapter/Api5Session.Loft.cs — Loft()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — Api7Loft (счётчики, чтение)`.
**Существенные условия:** Цепочки соответствия сечений есть ТОЛЬКО в API7: ни ksBaseLoftDefinition, ни ksBossLoftDefinition не объявляют ни AddCoupling, ни Coupling, поэтому семейство ведётся одним маршрутом API7 — на нём выразимы все его обязательные строки. Фабрика принимает o3d_bossLoft (31) и o3d_cutLoft, параметры задаются до IModelObject::Update, сечения — свойство ILoft.Sketchs типа VARIANT (SAFEARRAY указателей LPDISPATCH). ВХОД ЛЕЖИТ В ДВУХ ХРАНИЛИЩАХ, И ЭТО ИЗМЕРЕНО (шаг B5.21): пока определение API5 не прочитано, запись только в ILoft.Sketchs применяется (3 → 2, объём 48000, воспроизведено четырежды); ОДНОГО чтения ksBaseLoftDefinition.Sketchs() достаточно, чтобы владельцем ЧИСЛА сечений стало определение, и после этого та же запись отменяется первым же обновлением (после присваивания 2, после ksEntity.Update() 3, объём 16114.2858257129); запись набора в ОБА хранилища — сначала ksEntityCollection определения через Clear() + Add(), затем ILoft.Sketchs — применяется (2, объём 48000). Порядок сечений ОБЪЁМОМ не доказывается: концентрические параллельные сечения дают 28000 в любом порядке, и порядок доказывается цепочкой соответствия (0/20 → 20000 против 25999.99700225 на переставленном). Присваивание ILoft.Sketchs СБРАСЫВАЕТ цепочки (1 → 0). Непараллельные плоскости сечений отвергаются вызывающей стороной по измеренному расхождению осей нормалей, а не по умолчанию.
**Документированные интерфейсы и члены:**
- `ILofts.Add (допустимые типы o3d_bossLoft / o3d_cutLoft)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilofts_add.html>
- `ILoft (состав интерфейса)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iloft_propers.html>
- `ILoft.Sketchs (VARIANT, SAFEARRAY LPDISPATCH)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iloft_sketchs.html>
- `ILoft.AddCoupling → ICoupling` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iloft_addcoupling.html>
- `IModelObject.Update` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imodelobject_update.html>
- `IPart7.RebuildModel` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipart7_rebuildmodel.html>
- `ksBaseLoftDefinition.Sketches` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseloftdefinition_sketches.html>
- `ksBossLoftDefinition.Sketches` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbossloftdefinition_sketches.html>
- `ksEntityCollection.Clear / Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentitycollection_clear.html>
- `Obj3dType: o3d_bossLoft = 31` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/obj3dtype.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-05.base.mode_couplings` | 10/10 verified | 14 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-05.base.parallel_sections_order` | 10/10 verified | 13 строк · PASS · `e8c08d41` | Подтверждено | — |

### SM-13 — Оболочка — тонкостенный элемент (API5)
**Инструмент:** `kompas_get_feature`, `kompas_shell`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.FeatureEdit.B5.cs — правка набора удаляемых граней`; `src/KompasMcp.Api5Adapter/Api5Session.FeatureRead.B5.cs — чтение признака`; `src/KompasMcp.Api5Adapter/Api5Session.Shell.cs — Shell()`.
**Существенные условия:** Тип признака — 43 (o3d_shellOperation) по таблице Obj3dType, определение берётся у элемента модели (ksEntity::GetDefinition). Состав: толщина thickness, направление thinType — документировано буквально «TRUE — внутрь, FALSE — наружу» и подтверждено числом на коробе 100×80×10 с удалённой верхней гранью при t = 2: thinType = true → 21631.999999999996 мм³ (полость 96×76×8), thinType = false → 24832.000000000022 мм³ (тело 104×84×12 минус 100×80×10) — оба совпали с аналитикой, то есть соответствие установлено дважды: страницей и объёмом. FaceArray() возвращает ksEntityCollection удаляемых граней. ПУСТОЙ СПИСОК ГРАНЕЙ ОТВЕРГАЕТСЯ ДО COM, и это измеренный отказ, а не осторожность: и API5 (шаг B5.6), и API7 (шаг B5.10, четыре постановки) принимают такой вызов (Create/Update = true), но тело не меняется — 80000 при 6 гранях, ровно как у исходного короба, тогда как открытая оболочка даёт 21632 при 11 гранях. Касательные грани — член ТОЛЬКО API7 (IShell.SetFaces(Faces, TangentFaces)); у API5 ksShellDefinition такого члена нет, поэтому tangent_faces = true отвергается именованно, а не принимается молча. Переменная толщина по граням вне обязательного объёма этапа (OQ-A13).
**Документированные интерфейсы и члены:**
- `ksShellDefinition (состав интерфейса)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksshelldefinition.html>
- `ksShellDefinition.thinType (TRUE — внутрь, FALSE — наружу)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksshelldefinition_thintype.html>
- `ksShellDefinition.FaceArray → ksEntityCollection` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksshelldefinition_facearray.html>
- `ksPart.NewEntity(43 = o3d_shellOperation)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kspart_newentity.html>
- `ksEntity.Create` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentity_create.html>
- `ksEntityCollection.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentitycollection_add.html>
- `Obj3dType: o3d_shellOperation = 43` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/obj3dtype.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-13.shell.mode_remove_faces` | 10/10 verified | 12 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-13.shell.mode_direction` | 10/10 verified | 13 строк · PASS · `e8c08d41` | Подтверждено | — |
| `SM-13.shell.uniform_thickness` | 10/10 verified | 12 строк · PASS · `e8c08d41` | Подтверждено | — |

### None
**Инструмент:** не опубликован.
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Не подтверждено** — строка не отнесена к проверенному семейству: маршрут в коде не заявлен, поэтому документированность МАРШРУТА не подтверждена и не опровергнута

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `AUX-IMAGE.raster_export` | 10/10 verified | 16 строк · PASS · `e8c08d41` | Подтверждено | — |

## 3. Запланированные семейства: маршрута в коде нет

Эти строки присутствуют в матрице как план (`metadata_found`): реализации нет, вызовов COM нет. Документированность **маршрута** здесь не подтверждена — подтверждать нечего; приведённые интерфейсы справки v24 — указатель для будущей реализации, а не доказательство. За реализованные эти строки не выдаются.

### SM-01 — Примитивы API7 (параллелепипед)
**Инструмент:** не опубликован.
**Документированные интерфейсы и члены:**
- указатель (не подтверждение маршрута): `IModelContainer.ElementaryBodies / IElementaryBodies` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imodelcontainer_elementarybodies.html>, также <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ielementarybodies.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Не подтверждено** — возможность запланирована: маршрут в коде отсутствует и в матрице не заявлен, поэтому документированность МАРШРУТА в этом аудите не подтверждена. Документированные интерфейсы семейства в справке v24 есть — это указатель для будущей реализации, а не подтверждение маршрута

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `SM-01.primitive_box` | 0/10 verified; открыты: `discover`, `create`, `read`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `negative_tests`, `geometry_validation` | строк приёмки нет | Не реализовано | не требуется в этом аудите: строка объявлена планом и за реализованную не выдаётся |

## 4. Общие зависимости

Сеанс, жизненный цикл, ссылки, эскизы, единицы, общий слой API7. Это не «возможность», а основание для остальных строк: она либо покрыта прогонами, либо покрыта не полностью, и второе называется прямо. Числа действий здесь — из тех же строк матрицы, что и выше, повторно они не считаются.

### LIFECYCLE — Документ и признак: создание, открытие, сохранение, закрытие, перестроение, удаление — зависимости
**Инструмент:** `kompas_close_document`, `kompas_create_document`, `kompas_delete_feature`, `kompas_list_documents`, `kompas_open_document`, `kompas_rebuild`, `kompas_save_document`, `kompas_set_feature_suppressed`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Features.cs`; `src/KompasMcp.Api5Adapter/Api5Session.Lifecycle.cs`; `src/KompasMcp.Api5Adapter/Api5Session.cs — CreateDocument/OpenDocument/SaveDocument/CloseDocument`.
**Существенные условия:** Документ адресуется UUID, а не «активным»: Document3D() — фабрика, а не текущий документ. ksEntity.Update() = true означает «принято», не «применено»; перестроение — RebuildDocument. Ветка IsSaved из продукта УДАЛЕНА в наряде исправлений (F-02): члена нет ни в справке, ни в interop, а «документ изменён» выводился из сравнения отпечатков. Сохранённость ведётся отдельным состоянием (`DocumentSaveState`) и подтверждается успешным сохранением с перечитыванием файла, а не сбросом признака CAD.
**Документированные интерфейсы и члены:**
- `ksDocument3D.Create / Open / Save / SaveAs / close` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument3d_create.html>
- `ksDocument3D.RebuildDocument / DeleteObject / GetPart` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument3d_rebuilddocument.html>
- `ksDocument3D.SetActive / UpdateDocumentParam` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument3d_setactive.html>
- `ksPart.NewEntity / GetDefaultEntity / RebuildModel` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kspart_newentity.html>
- `ksEntity.Create / Update / GetDefinition / IsCreated` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentity_create.html>
- `ksDocument3D.IsDetail` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument3d_isdetail.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.selection.unambiguous` | 3/10 verified; открыты: `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` | 3 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` |
| `dep.lifecycle.feature_cycle` | 7/10 verified; открыты: `discover`, `create`, `geometry_validation` | 14 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `create`, `geometry_validation` |
| `dep.bodies.multibody` | 3/10 verified; открыты: `discover`, `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies` | 3 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies` |
| `dep.foundation` | 2/10 verified; открыты: `discover`, `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` | 2 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` |
| `dep.preserve_unknown` | 4/10 verified; открыты: `discover`, `create`, `rebuild`, `suppress_restore`, `delete_dependencies`, `negative_tests` | 4 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `create`, `rebuild`, `suppress_restore`, `delete_dependencies`, `negative_tests` |

### SKETCH — Эскиз: создание, правка геометрии, базовые плоскости — зависимости
**Инструмент:** `kompas_create_sketch`, `kompas_edit_sketch`, `kompas_finish_sketch`, `kompas_get_sketch_status`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs — CreateSketch/EditSketch/FinishSketch`; `src/KompasMcp.Api5Adapter/Api5Session.SketchStatus.cs`.
**Существенные условия:** Геометрия эскиза строится функциями 2D API на интерфейсе ksDocument2D внутри BeginEdit/EndEdit; базовая плоскость эскиза — SetPlane/GetPlane.
**Документированные интерфейсы и члены:**
- `ksSketchDefinition.SetPlane / GetPlane` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssketchdefinition_setplane.html>
- `ksSketchDefinition.BeginEdit / EndEdit` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssketchdefinition_beginedit.html>
- `ksDocument2D.ksLineSeg / ksCircle / ksArcByAngle` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument2d_kslineseg.html>
- `ksPlaneOffsetDefinition.SetPlane / GetPlane` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksplaneoffsetdefinition_props.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.sketch.entities` | 7/10 verified; открыты: `rebuild`, `suppress_restore`, `delete_dependencies` | 15 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `rebuild`, `suppress_restore`, `delete_dependencies` |
| `dep.refs.planes` | 6/10 verified; открыты: `rebuild`, `suppress_restore`, `delete_dependencies`, `geometry_validation` | 6 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `rebuild`, `suppress_restore`, `delete_dependencies`, `geometry_validation` |
| `dep.refs.axes` | 5/10 verified; открыты: `edit`, `rebuild`, `suppress_restore`, `delete_dependencies`, `geometry_validation` | 5 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `edit`, `rebuild`, `suppress_restore`, `delete_dependencies`, `geometry_validation` |

### READ — Чтение геометрии и измерения — зависимости
**Инструмент:** `kompas_list_bodies`, `kompas_list_features`, `kompas_measure`, `kompas_read_topology`, `kompas_resolve_selection`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs — Measure/Topology`; `src/KompasMcp.Api5Adapter/Api5Session.SolidRead.cs`.
**Существенные условия:** Объём основного тела читается GetGabarit/CalcMassInertiaProperties по ссылке тела, а не «объём того, что построено сейчас»; площадь грани — GetArea, длина ребра — GetLength; чтение производных величин не заменяет чтение исходных параметров.
**Документированные интерфейсы и члены:**
- `ksBody.IsSolid / GetGabarit / CalcMassInertiaProperties` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbody_issolid.html>
- `ksFaceDefinition.GetArea / GetSurface / GetNormal` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksfacedefinition_getarea.html>
- `ksSurface.GetParamUMin/UMax/VMin/VMax / IsPlanar / IsCylinder` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssurface_getparamumin.html>
- `ksCurve3D.GetLength / GetCurveParam / IsArc / IsCircle` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kscurve3d_getlength.html>
- `ksBodyCollection.* / ksEntityCollection.*` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbodycollection_getcount.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.refs.planar_face` | 4/10 verified; открыты: `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies` | 4 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies` |
| `dep.refs.edges_faces` | 3/10 verified; открыты: `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` | 3 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `create`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` |

### EXCHANGE — Обмен: STEP import/export, единицы измерения — зависимости
**Инструмент:** `kompas_export_step`, `kompas_import_step`, `kompas_probe_units`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Exchange.cs`.
**Существенные условия:** Формат задаётся параметрами конвертации (Init + format), а не расширением файла; единицы — из ksLengthUnitsEnum.
**Документированные интерфейсы и члены:**
- `ksDocument3D.AdditionFormatParam + SaveAsToAdditionFormat` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument3d_saveastoadditionformat.html>
- `ksDocument3D.LoadFromAdditionFormat` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument3d_loadfromadditionformat.html>
- `ksAdditionFormatParam.Init / Properties` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksadditionformatparam_props.html>
- `ksLengthUnitsEnum` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kslengthunitsenum.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.units.confirm` | 1/10 verified; открыты: `discover`, `create`, `read`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `negative_tests` | 1 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `create`, `read`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `negative_tests` |

### CONNECT — Сеанс: подключение, видимость, версия, отсоединение — зависимости
**Инструмент:** `kompas_capabilities`, `kompas_connect`, `kompas_disconnect`, `kompas_get_context`, `kompas_health`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Visibility.cs`; `src/KompasMcp.Api5Adapter/Api5Session.cs — Connect/Disconnect`.
**Существенные условия:** Экземпляр либо подключается (attach) к уже запущенному, либо запускается своим (launch); принадлежность процесса проверяется по PID, а не по видимости окна: ksGetHWindow даёт дескриптор главного окна, и наличие HWND о видимости не говорит.
**Документированные интерфейсы и члены:**
- `KompasObject.ksGetHWindow` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksgethwindow.html>
- `KompasObject.ksGetSystemVersion` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_ksgetsystemversion.html>
- `KompasObject.Quit / TransferInterface` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_quit.html>
- `ksDocument3D.ActiveDocument3D (через KompasObject)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kompasobject_activedocument3d.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.api7.in_same_adapter` | 4/10 verified; открыты: `create`, `rebuild`, `suppress_restore`, `delete_dependencies`, `negative_tests`, `geometry_validation` | 15 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `create`, `rebuild`, `suppress_restore`, `delete_dependencies`, `negative_tests`, `geometry_validation` |

### SM-07 — Родное отверстие API7 — зависимости
**Инструмент:** `kompas_hole`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Hole.cs — Hole()`; `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — создание отверстия и позиционирование`.
**Существенные условия:** Форма и дно — ksHTBase / ksHTCounterbore / ksHTCountersinking и ksEFFlat / ksDTValue / ksDTReachThrough; позиция — Point3DParamSurface + ksOffsetByCoords и Offset1/Offset2; ось по нормали грани — BaseSurface + Perpendicular; параметры пишутся до Update().
**Документированные интерфейсы и члены:**
- `IHoles3D.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iholes3d_add.html>
- `IHoleDisposal.BaseSurface / Perpendicular / Point3DParamSurface` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/iholedisposal_propers.html>
- `ICountersinkHoleParameters.*` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/icountersinkholeparameters_propers.html>
- `ISpotfacingHoleParameters.*` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ispotfacingholeparameters_propers.html>
- `ksDepthTypeEnum / ksEndFaceTypeEnum / ksHoleTypeEnum` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdepthtypeenum.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.refs.points_axes` | 3/10 verified; открыты: `discover`, `edit`, `rebuild`, `suppress_restore`, `delete_dependencies`, `negative_tests`, `geometry_validation` | 3 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `edit`, `rebuild`, `suppress_restore`, `delete_dependencies`, `negative_tests`, `geometry_validation` |

### SM-04 — Кинематическая операция — элемент по траектории (API5) — зависимости
**Инструмент:** `kompas_get_feature`, `kompas_sweep`, `kompas_update_feature`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.FeatureEdit.B5.cs — правка режима движения сечения`; `src/KompasMcp.Api5Adapter/Api5Session.FeatureRead.B5.cs — чтение признака`; `src/KompasMcp.Api5Adapter/Api5Session.Sweep.cs — Sweep()`.
**Существенные условия:** Тип базовой кинематической операции — 45 (o3d_baseEvolution) по таблице Obj3dType; фабрика API7 этот тип НЕ документирует (ievolutions_add.html перечисляет допустимыми только o3d_bossEvolution 46 и o3d_cutEvolution 47), поэтому создание идёт ksPart.NewEntity(45) + ksEntity::GetDefinition → ksBaseEvolutionDefinition. В дереве признак виден под номером 46 (o3d_bossEvolution), поэтому опознание идёт ПО ИНТЕРФЕЙСУ ОПРЕДЕЛЕНИЯ, а не по номеру типа. Траектория присоединяется к ksEntityCollection, полученному от PathPartArray(): держатель отвечает System.__ComObject, приведение к ksEntityCollection работает, а отражение по нему членов не находит вовсе. Режим движения сечения — sketchShiftType 0/1/2 (ksevolutionshiftsketchtypeenum.html): на ПРЯМОЙ траектории режимы неразличимы (одно тело), различающая постановка — дуга R50/90°, где ортогональный режим даёт 24674.011002723353, параллельный — 15707.963267948984 (разность 8966.047734774369 мм³). Длина траектории читается GetPathLength(1) ПОСЛЕ построения: тот же вызов до Create() отвечал нулём, и это был дефект прибора, а не факт о продукте. ПРАВКА ВХОДА не выражается ни одним из двух хранилищ, и это измерено на ОБОИХ (шаги B5.15 и B5.22): SetSketch определения API5 принят и не применён; запись в документированное свойство API7 IEvolution.Sketch (объявленный тип KompasAPI7.IModelObject) принята, Update() = true и тоже не применена; запись в ОБА хранилища подряд — не применена; запись ДО первого обращения к API7 — тоже не применена, причём держатель траектории перечисляет подставленный эскиз, то есть запись ДОШЛА. Признак при этом жив, и прибор видит изменения: правка режима применяется и через определение, и через хранилище API7 (15707.963267948984), поэтому «не применено» отличимо от «признак умер». Имя сечения через свойство API7 прибором НЕ ПРОЧИТАНО (значение приходит как System.__ComObject, и ни прямой, ни обратный перенос не даёт ksEntity) — это названный предел прибора, а не факт о продукте, и «не применено» доказано объёмом, а не именем. Поэтому вход строки читается, но не перепривязывается, а sketch_ref отвергается по имени.
**Документированные интерфейсы и члены:**
- `ksPart.NewEntity(45 = o3d_baseEvolution)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kspart_newentity.html>
- `ksBaseEvolutionDefinition (состав интерфейса)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition.html>
- `ksBaseEvolutionDefinition.SetSketch` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_setsketch.html>
- `ksBaseEvolutionDefinition.sketchShiftType` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_sketchshifttype.html>
- `ksBaseEvolutionDefinition.PathPartArray → ksEntityCollection` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_pathpartarray.html>
- `ksBaseEvolutionDefinition.GetPathLength` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksbaseevolutiondefinition_getpathlength.html>
- `ksEvolutionShiftSketchTypeEnum (0/1/2)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksevolutionshiftsketchtypeenum.html>
- `ksEntity.Create` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentity_create.html>
- `ksEntityCollection.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksentitycollection_add.html>
- `Obj3dType: o3d_baseEvolution = 45` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/obj3dtype.html>
- указатель (не подтверждение маршрута): `IEvolution` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ievolution.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.paths.flat` | 3/10 verified; открыты: `discover`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` | 3 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `edit`, `rebuild`, `save_reopen`, `suppress_restore`, `delete_dependencies`, `geometry_validation` |

### PATTERN — Массивы: по сетке, круговой (концентрический), зеркальный — зависимости
**Инструмент:** `kompas_get_pattern`, `kompas_pattern_circular`, `kompas_pattern_grid`, `kompas_pattern_mirror`.
**Место в коде:** `src/KompasMcp.Api5Adapter/Api5Session.Pattern.cs — создание`; `src/KompasMcp.Api5Adapter/Api5Session.PatternEdit.cs — правка, чтение`; `src/KompasMcp.Api5Adapter/Api7/Api7Pattern.cs — мост API7 (ILinearPattern / ICircularPattern / IMirrorPattern)`.
**Существенные условия:** Массив создаётся через IModelContainer.FeaturePatterns.Add(ksObj3dTypeEnum) и приведение QI к ILinearPattern / ICircularPattern / IMirrorPattern; типизированный вызов, не отражение. ИЗМЕРЕНО ПРОГОНОМ 20.09.2026: у копирования ТЕЛ СВОЙ номер типа в дереве — o3d_BodiesMeshCopy=528 и o3d_BodiesCircularCopy=529, отличный от копирования ОПЕРАЦИЙ (o3d_meshCopy=35, o3d_circularCopy=36); отображение у них ОДНО («Массив по сетке:1»), поэтому различает только тип. Область действия SaveInitialObjects ограничена справкой: свойство работает ТОЛЬКО для o3d_mirrorAllOperation, у прочих операций зеркального копирования возможность скрыть экземпляры отсутствует — проверено на операции 48 (запись false читается обратно true, геометрия не меняется). Углы задаются nullable и пишутся ТОЛЬКО когда переданы: запись нуля вместо неуказанного угла уничтожает умолчание модели. Angle2 — угол МЕЖДУ направлениями сетки (90° прямоугольная, 0° вырождает сетку в линию), Angle1 отводит направление от построенной оси.
**Документированные интерфейсы и члены:**
- `IModelContainer.FeaturePatterns / IFeaturePatterns.Add` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ifeaturepatterns_add.html>
- `IFeaturePattern` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ifeaturepattern.html>
- `IFeaturePatterns` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ifeaturepatterns.html>
- `ILinearPattern (свойства)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilinearpattern_props.html>
- `ICircularPattern (свойства)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/icircularpattern_props.html>
- `IMirrorPattern (свойства)` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imirrorpattern_props.html>
- `IMirrorPattern.SaveInitialObjects` — <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/imirrorpattern_saveinitialobjects.html>
**Источник:** справка SDK КОМПАС-3D v24 (сборка 24.0.0.2799), ссылки выше; маршрут — из `coverage/solid-v24/matrix.json` и исходников адаптера.
**Оценка документации:** **Подтверждено** — маршрут целиком лежит на документированных интерфейсах и членах

| Режим (строка матрицы) | Действия | Проверка и хеш поставки | Реализация | Необходимое исправление |
|---|---|---|---|---|
| `dep.pattern.associativity` | 4/10 verified; открыты: `discover`, `create`, `read`, `suppress_restore`, `delete_dependencies`, `negative_tests` | 45 строк · PASS · `e8c08d41` | Недостаточно проверки | довести прогон: открыты `discover`, `create`, `read`, `suppress_restore`, `delete_dependencies`, `negative_tests` |

## 5. Находки

### F-01 — исправленный дефект продукта (мёртвая ветка удалена)

**Существо.** `Api5Session.PlanarNormal` строит отражённый вызов `GetNormal` только для сигнатур с 0 или 2 параметрами, тогда как документированная сигнатура `ksSurface.GetNormal(paramU, paramV, out x, out y, out z)` имеет пять параметров (две входные величины и три выходные). Для документированной сигнатуры ветка всегда возвращает null.

**Класс и вес.** исправленный дефект продукта (мёртвая ветка удалена); была низкой — на результат не влияла; исправлено.

**Где.** `было: src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs, PlanarNormal (~3052–3072); после исправления `PlanarNormal` в исходниках отсутствует`

**Документация.** <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/kssurface_getnormal.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksfacedefinition_getsurface.html>

**Ожидание.** нормаль плоской грани читается документированным GetNormal

**Наблюдено.** ИСПРАВЛЕНО: отражённая ветка `PlanarNormal` и её точки вызова удалены, остался типизированный `SurfaceNormalAtMiddle`. Прибором с установленным типом получателя подтверждено: `ksSurface.GetNormal(Double paramU, Double paramV, out Double& x, out Double& y, out Double& z)` — ОДНА точка вызова (`Api5Session.Geometry.cs:3033`), `PlanarNormal` в маршрутах не встречается

**Затронутые инструменты.** `kompas_hole`, `kompas_measure`, `kompas_read_topology`

**Что исправлять.** исправлено в наряде 20.09.2026: ветка удалена (выбран второй из двух названных путей — удаление, потому что недостижимая ветка читается как существующий маршрут). Проверки «на отсутствие метода» не заводилось: её отсутствие не является измерением поведения

### F-02 — исправленный дефект продукта (мёртвая ветка удалена, состояние сохранённости отделено от ревизии)

**Существо.** `Api5Session.IsDirty` ищет отражением член `IsSaved` у `ksDocument3D`. Этого члена нет ни в официальной справке SDK v24, ни в interop установленной версии, поэтому `GetMethod("IsSaved")` всегда возвращает null и ветка недостижима. Состояние «документ изменён» обеспечивается сравнением отпечатка, то есть заявленный маршрут не существует, а результат получается другим.

**Класс и вес.** исправленный дефект продукта (мёртвая ветка удалена, состояние сохранённости отделено от ревизии); была низкой — работал отпечаток; исправлено.

**Где.** `было: src/KompasMcp.Api5Adapter/Api5Session.cs:613–634; после исправления член `IsSaved` в продукте отсутствует`

**Измерено.** в зеркале справки (2175 страниц) страниц с `IsSaved` — 0; в `docs/compatibility/kompas-api5-metadata.json` — 0 вхождений; в `Interop.Kompas6API5.dll` (D:\Programs\KOMPAS-3Dv24\Libs\PolynomLib\Bin\Client) строка `IsSaved` отсутствует, `IsDetail` присутствует

**Ожидание.** признак сохранённости документа берётся документированным членом

**Наблюдено.** ИСПРАВЛЕНО: отражённый `IsSaved` из продукта удалён. Состояние сохранённости ведётся отдельным состоянием `DocumentSaveState {Clean, Dirty, Unknown}` (`src/KompasMcp.Domain/Documents/DocumentSaveTracking.cs`) и отделено от состояния ревизии: мутация, чтение контекста и подъём ревизии сохранённость НЕ объявляют, а подтверждается она успешным сохранением с ПЕРЕЧИТЫВАНИЕМ файла. Признак CAD под тест не сбрасывался. Измерено на поставке 20.09.2026 группой DL (12 строк, 33 проверки, FAIL 0): `close(refuse)` даёт `DOCUMENT_DIRTY` и документ остаётся доступен; `close(save)` переживает переоткрытие; `close(discard)` теряет правку; неудачное сохранение (`SAVE_FAILED`) не очищает состояние; документ без пути не даёт ложного успеха. Обработка ошибки чтения состояния проверена управляемым швом: `DocumentSaveState.Unknown` не выдаётся за `Clean` и при политике `refuse` закрытие ОТКЛОНЯЕТСЯ, а не разрешается

**Затронутые инструменты.** `kompas_close_document`, `kompas_save_document`, `kompas_get_context`

**Что исправлять.** исправлено в наряде 20.09.2026: ветка удалена, состояние сохранённости отделено от ревизии, поведение закрытия доказано СОДЕРЖИМЫМ файла и объёмом после переоткрытия, а не отсутствием исключения

### F-03 — состояние объёма будущей работы (не дефект)

**Существо.** Обязательный объём профиля не закрыт: часть режимов и зависимостей профиля лежит на уровне `metadata_found`. Строки SM-01.primitive_box, SM-04.boss, SM-15.union.mode_save_base_copy не реализованы — это ПЛАН, а не текущий дефект, и живой проверки в этом аудите они не требуют.

**Класс и вес.** состояние объёма будущей работы (не дефект); —.

**Где.** `coverage/solid-v24/matrix.json`

**Ожидание.** у каждой ОПУБЛИКОВАННОЙ возможности есть приёмочная запись

**Наблюдено.** у каждой закрытой строки есть запись и доказательство; строки плана названы планом и не выдаются за реализованные

**Что исправлять.** не требуется: это состояние объёма будущей работы. Удаление таких строк подняло бы готовность без реализации и потому запрещено

### F-04 — названная граница непубликуемого поля (не дефект)

**Существо.** `reposition_axis_point_mm` не читается и не публикуется, и это ОБОСНОВАННАЯ граница, а не «не прочитано». Документированного члена для точки оси поворота в цепочке `IBodyReposition → ILocalCoordinateSystem → ILocalCSEulerParam / IPoint3DParamDisplace` нет: поворот вокруг любой точки одной прямой даёт то же размещение, поэтому точка оси не является свойством размещения.

**Класс и вес.** названная граница непубликуемого поля (не дефект); —.

**Где.** `src/KompasMcp.Api5Adapter/Api7/Api7Bridge.cs — ReadPlacement`

**Документация.** <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ibodyreposition_propers.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcoordinatesystem_props.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3d_props.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcseulerparam_props.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3dparamdisplace_props.html>

**Ожидание.** чтение размещения документированными параметрами

**Наблюдено.** чтение есть и переживает переоткрытие; точка оси не читается, и поле не публикуется

**Затронутые инструменты.** `kompas_get_feature`, `kompas_update_feature`

**Что исправлять.** не требуется: поле остаётся названным непубликуемым. Публиковать представитель прямой под именем входного параметра значило бы подменить параметр операции его следствием, а новое требование читать эту точку не создаётся

### F-05 — исправленный дефект доказательств (не дефект продукта)

**Существо.** Прежний отчёт приписывал строке `S02` исполнение инструмента `kompas_list_documents`. Измерено: `S02` (`scripts/mcp-smoke.py`) проверяет ТОЛЬКО `tools/list`, перечисления документов в ней нет, поэтому утверждение о работе инструмента во всех 12 групповых прогонах было ложным.

**Класс и вес.** исправленный дефект доказательств (не дефект продукта); —.

**Где.** `scripts/mcp-smoke.py — строки S02b / S02c / S02d; coverage/solid-v24/matrix.json`

**Измерено.** `scripts/mcp-smoke.py` — S02 собирает список имён инструментов; `scripts/mcp-attach-test.py:143` вызывает `kompas_list_documents` через присоединённый сеанс, но это отдельная диагностика, а не отчёт приёмки поставки; в отчётах групп строки с этим инструментом не было

**Ожидание.** у каждого опубликованного инструмента есть проверка с наблюдаемым результатом

**Наблюдено.** проверка разделена на ДВЕ половины, исполняемые в одной постановке. `S02b` (контрактная): вызов без обязательного `application_id` отвергается кодом INVALID_ARGUMENT — исполняется до сеанса, поэтому работает и в `--contract-only`. `S02c`/`S02d` (поведенческие): на живом сеансе маршрут отвечает списком, а запись несёт id/kind/revision — `S02c` сразу после connect (список законно пуст), `S02d` после создания документа (запись обязана быть). Первая редакция строки падала ВСЕГДА из-за собственного дефекта: `is_err` — boolean `isError`, а сравнивался с None; измерено на поставке 20.09.2026 (S02c/S02d FAIL при err=None и верной записи). Ложное утверждение о `S02` снято

**Затронутые инструменты.** `kompas_list_documents`

**Что исправлять.** исправлено в этом наряде: строки S02b/S02c/S02d, привязка инструмента к семейству жизненного цикла документа (снят пункт «инструмент без строки матрицы»); кроме того снято неоднозначное сопоставление проверок по `startswith` и разделены метрики схемы (имена свойств и значимая форма)

### F-06 — исправленный дефект прибора аудита

**Существо.** Прежний инвентарь COM-членов отбирал вызовы регулярным выражением по ИМЕНИ члена и сверял имя со словарём метаданных, НЕ определяя тип получателя. Измеренный ложный случай: `com_members[member=Add]` указывал на `Api5Session.Chamfer.cs:164,172`, где `checks.Add(...)` — вызов `List<NamedCheck>`, и подкреплялся страницами COM-коллекций. Совпадение имён выдавалось за проверку маршрута.

**Класс и вес.** исправленный дефект прибора аудита; высокая — прибор подтверждал несуществующий маршрут.

**Где.** `scratch/_audit_inventory.py — com_usage; заменено на scratch/_com_routes.py`

**Измерено.** новый прибор разрешает цепочку по объявлениям (параметры, поля, локальные, приведение, `as`/`is`, `foreach`, вывод `var`, `out var`) и берёт сигнатуру и вид доступа из метаданных interop; `List.Add` отнесён к собственным получателям

**Ожидание.** маршрут подтверждается типом получателя, а не совпадением имени

**Наблюдено.** измерено: 204 маршрута (API5 111 / API7 93) против 128 совпадений по имени; 697 обращений к собственным получателям исключены из таблицы COM

**Что исправлять.** исправлено в этом наряде: scratch/_com_routes.py; счёт совпадений по имени сохранён отдельно и назван подготовкой

### F-07 — исправленный дефект прибора аудита

**Существо.** Размещение описывалось как `IPosition.OrientationType / LocalCSParameters / ParameterType / Parameters`. Интерфейса `IPosition` в API7 НЕТ: в индексе `Interop.KompasAPI7` такого имени нет (есть только `IPositionLeader`). Свойства объявлены иначе: `OrientationType` — на `ILocalCoordinateSystem`, `ParameterType` и `Parameters` — на его БАЗОВОМ `IPoint3D`, параметры режимов берутся с `ILocalCoordinateSystem.LocalCSParameters` и `IPoint3D.Parameters`.

**Класс и вес.** исправленный дефект прибора аудита; средняя — владелец маршрута был назван несуществующим интерфейсом.

**Где.** `scratch/_build_api_compliance.py — семейство SM-17; scratch/_com_routes.py`

**Документация.** <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcoordinatesystem.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcoordinatesystem_props.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3d.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3d_props.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ilocalcseulerparam_props.html>; <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ipoint3dparamdisplace_props.html>

**Измерено.** справка v24 задаёт иерархию `IDispatch → IKompasAPIObject → IPoint3D → ILocalCoordinateSystem`; `OrientationType` перечислен на странице свойств `ILocalCoordinateSystem`, `ParameterType` и `Parameters` — на странице свойств `IPoint3D`; переходы к параметрам режимов описаны на страницах интерфейсов `ILocalCSEulerParam` и `IPoint3DParamDisplace`

**Ожидание.** владелец члена называется фактическим интерфейсом с учётом наследования

**Наблюдено.** владельцы исправлены; наследованные члены помечены (`IPoint3D.ParameterType`), переходы описаны парой «свойство → интерфейс»

**Затронутые инструменты.** `kompas_reposition`, `kompas_get_feature`, `kompas_update_feature`

**Что исправлять.** исправлено в этом наряде: семейство SM-17 и подробная таблица маршрутов

### F-08 — названный пробел трассируемости доказательств (не дефект продукта)

**Существо.** Строка матрицы заявляет десять действий и их статусы, а отчёт приёмки объявляет имена строк и вердикты; прямой сверки между ними не было, и `implementation.status` выводился из `catalog_level` и наличия хотя бы одного PASS. Связь «действие → проверка» записана по действиям только у семейства B3 (строки `B3M.<режим>.<действие>`, 10 из 10 на режим); у остальных семейств она записана «предметно» (`G01`, `HO.1`, `FL01`), и по имени строки действие не восстанавливается.

**Класс и вес.** названный пробел трассируемости доказательств (не дефект продукта); средняя — статус строки опирался на ярлык действия, а не на доказательство под ним; измерена тремя независимыми сверками и двумя приборами 20.09.2026.

**Где.** `scripts/mcp-smoke.py — журнал вызовов и окна строк (`CALL_LOG`, `calls_window`); scratch/_build_api_compliance.py — verify_claimed_actions(), derive_actions(), calls_calibration(); scratch/_f08_link_gap.py, scratch/_f08_candidate_ownership.py — разбор остатка; coverage/solid-v24/matrix.json`

**Измерено.** scratch/mcp-smoke/delivery-journalfix-20260921/*.json — журнал вызовов ПО КАТАЛОГУ ОТЧЁТОВ: 14424 вызовов, 1059 строк приёмки с окном, 618 эпох документа. Полный прогон: 1086 строк, 10288 вызовов, отказов 0 — на бинарях поставки `publish-journalfix-20260921`. Групповые отчёты каталога — ПОДМНОЖЕСТВА полного прогона, поэтому их вызовы с ним не складываются: сумма по каталогу (14424) больше полного прогона (10288) ровно на вызовы повторно снятых групп

**Ожидание.** заявленное действие строки подтверждается строкой приёмки, которую можно найти по имени, а не по сходству

**Наблюдено.** сверка ведётся ТРЕМЯ независимыми способами, и все три числа измерены на одной поставке: (1) ПО ИМЕНИ РЕЖИМА, как искал бы рецензент: из 41 строки, заявившей `verified`, у 31 ни одна строка приёмки не называет режим ни идентификатором, ни описанием — находятся только 10 режимов B3; (2) ПО ПРИВЯЗКЕ И ВЫЗОВАМ: у 22 строк есть действие, которого нет в вызовах привязанных проверок; (3) ПО ИМЕНИ СТРОКИ: у 10 строк все действия названы одноимённой строкой, у 31 — нет. Приписок (ни одной разрешившейся ссылки) 0, противоречий (`verified` рядом с FAIL) 0. Числа НАЗВАНЫ отдельными ключами `claimed_actions_check.*`, а не растворены в числе PASS: «ноль отказов» не доказывает непроверенное действие. ОСТАТОК РАЗОБРАН ДО КОНЦА, и это главное измерение наряда: у 23 строк он выглядел как «недостающая привязка», но разбор показал, что привязка недостаёт РОВНО У ОДНОЙ — `SM-02.base_extrusion.blind`, где `G03i` и `G03r` читают признак ТОГО ЖЕ режима (базовое глухое выдавливание 10 мм) и до правки не были привязаны ни к одной строке. У остальных 22 проверки-кандидаты уже принадлежат ДРУГИМ режимам (измерено: 2712 пар «строка × действие» указывают на проверку, привязанную к другой строке; например `edit` скругления лежит в `FL04` у `SM-09.fillet.change_radius_in_place`, а `SM-09.fillet.read_radius_back` его не привязывает), поэтому приписать их значило бы выдать чужое доказательство за своё. Это РАБОТА (проверки для этих режимов либо честное понижение заявленных действий), а не правка отчёта, и она названа, а не закрыта

**Затронутые инструменты.** `kompas_extrude`, `kompas_hole`, `kompas_fillet`, `kompas_chamfer`, `kompas_rotated`, `kompas_create_sketch`, `kompas_edit_sketch`

**Что исправлять.** механизм исправлен и расширен в этом наряде: доказательство теперь измеряется, а не угадывается по имени строки. Прибор получил журнал вызовов, окно на каждую строку и семантическую подпись действия; подпись снабжена положительным контролем (на строках B3M, где действие названо именем строки: 9 действий из 10 выводятся в 10/10 строк) и отрицательным (подпись выполняется и на чужих строках, поэтому вывод по вызовам употребляется как ОТКАЗ, а не как подтверждение). Контроли нашли ЧЕТЫРЕ дефекта самого прибора до публикации: `discover` требовал оба обзора вместо любого; `rebuild` требовал вызова `kompas_rebuild`, тогда как наряд §9.1 определяет действие как проверку перестроения зависимых признаков; подпись считалась по каждой строке отдельно, из-за чего `save_reopen` (сохранение в V07, открытие в V09) не находился ни в одной; привязка по документу отброшена как НЕСОСТОЯТЕЛЬНАЯ — `document_id` есть не у всех вызовов, и она дала бы 75 ложных клеток «не выполнено». ДОПОЛНЕНО 20.09.2026, вторым проходом. (1) Отказ сторожа ТЕПЕРЬ ПОНИЖАЕТ СТАТУС СТРОКИ: `implementation.status` требует не только закрытости по ярлыкам и хотя бы одного PASS, но и того, чтобы вызовы привязанных строк содержали каждое заявленное действие. Прежде этих двух условий хватало, и отчёт говорил «Подтверждено» 21 строке, которой ТА ЖЕ сверка отказывала в заявлении: ярлык читался как доказательство. Число подтверждённых строк поэтому упало 38 → 18 — изменилось ПРАВИЛО ВЫВОДА, а не измерение, и понижение названо отдельным ключом `rows_status_lowered_by_unsupported_actions`. (2) ОСТАТОК РАЗОБРАН ДВУМЯ ПРИБОРАМИ, и он оказался НЕ тем, чем выглядел. `scratch/_f08_link_gap.py` искал проверку-кандидата по вызовам, `scratch/_f08_candidate_ownership.py` спрашивал, КОМУ она принадлежит. Первая редакция прибора считала кандидатом любую строку с нужным инструментом и потому отдавала `edit` строки `L11`/`L12`/`L13` — а они проверяют ОТКАЗ правки; дефект исправлен требованием, чтобы действие ВЫВОДИЛОСЬ из окна. Итог: привязка недостаёт ровно у ОДНОЙ строки (`SM-02.base_extrusion.blind` — `G03i`, `G03r`, привязка добавлена, `coverage/solid-v24/matrix.json` → `meta.f08_linkage_note`), а у остальных 22 проверки-кандидаты принадлежат ДРУГИМ режимам (2712 пар) либо чужим семействам (282) — приписать их было бы припиской чужого доказательства. Остаток — РАБОТА, а не правка отчёта: у 31 строки доказательство надо сделать находимым по имени режима (как `B3M.<NN>.<действие>`), у 22 — либо поставить проверки для этих режимов, либо честно понизить заявленные действия. Открытый пробел назван, а не закрыт переименованием строк и не закрыт дописыванием привязок, которых нет

### F-09 — исправленный дефект продукта (знак дуги не доходил до конечного угла)

**Существо.** `DrawSketchEntity` в ветке `SketchEntityKind.Arc` вычислял конечный угол дуги как `start_deg + Math.Abs(sweep_deg)`. Для отрицательного `sweep_deg` конечный угол уезжал на ДРУГУЮ сторону от начального, и ядро строило дугу, отражённую относительно луча `start_deg`, — не ту, которую запросил вызывающий. Схема `kompas_create_sketch` объявляет «Знак задаёт направление», а комментарий рядом с кодом обещал, что знак «must survive the call»: обещание было объявлено и не выполнено, поэтому это дефект, а не граница возможностей. Найден сценарием «Скоба Model Mania 2021» (наряд §3.3): контур скобы содержит четыре ВОГНУТЫЕ стороны R75, и они выражаются только дугами с отрицательным `sweep_deg` — то есть первой же постановкой, где знак был нужен.

**Класс и вес.** исправленный дефект продукта (знак дуги не доходил до конечного угла); высокая для вызывающего — дуга с ОТРИЦАТЕЛЬНЫМ `sweep_deg` строила отражённую геометрию, то есть не ту, которую просили, и молча; исправлено, поставка переиздана, все прогоны пересняты, различающая пара стоит строкой `MANIA.17`.

**Где.** `было: src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs, `DrawSketchEntity`, ветка `SketchEntityKind.Arc` — конечный угол `start + Math.Abs(sweep)`; после исправления концы дуги УПОРЯДОЧЕНЫ: `start + Math.Min(sweep, 0.0)` и `start + Math.Max(sweep, 0.0)`, флаг направления остался `sweep >= 0 ? 1 : 0``

**Документация.** <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument2d_ksarcbyangle.html>

**Измерено.** ИЗМЕРЕНО ДО И ПОСЛЕ ИСПРАВЛЕНИЯ, на двух разных поставках, одним и тем же зондом `scratch/_mania_contour_probe.py`: ДО — поставка `artifacts/publish-b5f08-20260920`, адаптер `1723bf9eba687c43bacc325afac232181111da2e758b5be441672e68696ef3ad`: контур скобы «как есть», с ЗНАКОМ дуг, → `GEOMETRY_FAILED` («Entity.Create() выдавливания вернул false: признак не появился»); тот же точечный набор, выраженный дугами с углами внутри диапазона, → 174799.7403608485 при аналитическом 174799.740134. ПОСЛЕ — поставка `artifacts/publish-journalfix-20260921`, адаптер `3e656699f8e585eb1e53e50db68e1a2ed7de8ab3a0e1baae35eac59d2fe468c6`: ТОТ ЖЕ знаковый контур → V=174799.74036084852, отказ отсутствует (повторный прогон зонда 20.09.2026, вариант V1). Хеши Host и Worker между поставками НЕ менялись (`dd1d7ffe5ea5587345a9575288ffd6a75e69b9d29164970177bb1e57fbae0c6e` и `b0aba49019dc927a0c1534ddcf337cdc1753695ffa4570cc05799f1cebc7b409`), то есть правка лежит ИМЕННО в адаптере, где и была. В прогоне поставки: `scratch/mcp-smoke/delivery-journalfix-20260921/mania.json` — `MANIA.01.create` площадь контура Грин=8739.987007 против сегментов 8739.987007 (расхождение 0.000e+00) и `MANIA.02.create` V=174799.740361 против площадь×20=174799.740134

**Ожидание.** дуга с отрицательным `sweep_deg` строит ТОТ ЖЕ точечный набор, что та же дуга с положительным `sweep_deg` при переставленных концах

**Наблюдено.** ИСПРАВЛЕНО И ПЕРЕПРОВЕРЕНО РАЗЛИЧАЮЩЕЙ ПАРОЙ. Строка `MANIA.17.negative_tests` строит одну и ту же четверть диска R30 двумя способами — дугой (start 0°, sweep +90°) и дугой (start 90°, sweep −90°) — и требует от обеих одного эталона 20·π·30²/4. Измерено: положительный sweep → V=14137.166941154068; отрицательный sweep → V=14137.166941154068; совпадение True. Пара различающая, а не подтверждающая: при дефекте ветвь с отрицательным sweep уходила на другую сторону и совпадения быть не могло. ПОЧЕМУ ДЕФЕКТ ДОЖИЛ ДО СИХ ПОР — измерено, а не предположено: во всей приёмке до появления этой группы `sweep_deg` встречался РОВНО ОДИН раз и всегда положительным (`scripts/mcp-smoke.py`, строка пути sweep B5.2: start −90°, sweep +90°), поэтому отрицательная ветвь не измерялась ни одной строкой. Группа добавила ещё три точки записи, из них четыре дуги контура скобы — с отрицательным sweep. ДАТИРОВАННАЯ ПОМЕТА 20.09.2026, 22:05 — ЧИСЛА ВЫШЕ БЫЛИ ПОЛУЧЕНЫ ЧУЖИМ ИЗМЕРЕНИЕМ. Строка `MANIA.17` в редакции 21:05 строила оба сектора в ОДНОМ документе и читала объём тела `bs[0]`; тела при выдавливании НАКАПЛИВАЮТСЯ, поэтому `bs[0]` оба раза был первым телом, и пара НЕ РАЗЛИЧАЛА НИЧЕГО — она сравнивала тело с самим собой. Значит утверждение «пара различающая» в тот момент было НЕВЕРНО, и подтверждение исправления F-09 этой строкой было приписано. Дефект продукта (F-09) при этом НЕ опровергнут: он измерен ЗОНДОМ `scratch/_mania_contour_probe.py`, который строит по документу на вариант, и там знаковый контур давал `GEOMETRY_FAILED` до правки и 174799.74036084852 после. После исправления прибора (находка F-11, один документ — одно тело) строка измерена заново и даёт РАЗЛИЧИМЫЕ числа: положительный sweep 14137.166941154068, отрицательный 14137.166941154068. Правило: **проверка, читающая «первый элемент» списка, обязана доказать, что элемент ровно один**

**Затронутые инструменты.** `kompas_create_sketch`, `kompas_edit_sketch`

**Что исправлять.** исправлено 20.09.2026 в наряде §3.3/§3.5: концы дуги упорядочены (`Math.Min`/`Math.Max`), флаг направления не тронут. Дефект НЕ замаскирован: поставка переиздана (`artifacts/publish-mania-20260920`), все прогоны пересняты на её бинарниках (группа `mania` 43 строки, полный прогон 1009 строк, отказов 0), утверждение о знаке не понижено. Различающая проверка оставлена в наборе строкой `MANIA.17`, чтобы отрицательная ветвь не осталась непокрытой снова. ДОПОЛНЕНО 20.09.2026, 22:05: САМА ЭТА СТРОКА оказалась дефектом прибора (находка F-11) — она читала объём первого тела при накапливающихся телах и потому не различала ничего; исправлена адресацией «один документ — одно тело», после чего действительно различает. Правило: **оставленная в наборе проверка считается покрытием только после того, как доказано, что она РАЗЛИЧАЕТ, — иначе запись «проверка оставлена» сама становится припиской**

### F-10 — исправленный дефект продукта (объявленный диапазон углов дуги шире выражаемого; первая редакция правки была НЕ аддитивной, и это поймал контроль зонда)

**Существо.** Схема `kompas_create_sketch` объявляет `start_deg` и `sweep_deg` в диапазоне [−720, 720]. Адаптер передаёт в ядро ДВА УГЛА (`ksArcByAngle(xc, yc, rad, f1, f2, direction, style)`), а не пару «начало + размах», и вызов ОТКАЗЫВАЛ, когда конечный угол покидал [−360°, 360°]. То есть объявленный диапазон шире выражаемого, и вызывающий, не выходя за объявленное, получал отказ. Найдено сценарием «Скоба Model Mania 2021» (наряд §3.3/§3.5) как ОТДЕЛЬНЫЙ факт от знака дуги (F-09): отказ пережил исправление знака, поэтому к нему не сводится.

**Класс и вес.** исправленный дефект продукта (объявленный диапазон углов дуги шире выражаемого; первая редакция правки была НЕ аддитивной, и это поймал контроль зонда); средняя — вызывающий, не выходя за объявленный [−720, 720], получал `GEOMETRY_FAILED` без объяснения; исправлено, поставка переиздана, все прогоны пересняты.

**Где.** `src/KompasMcp.Api5Adapter/Api5Session.Geometry.cs, `DrawSketchEntity` (ветка `SketchEntityKind.Arc`) и новый помощник `ArcEndpoints`; объявленный диапазон — artifacts/*/schemas/kompas_create_sketch.json (`start_deg`, `sweep_deg`: `minimum: -720`, `maximum: 720`)`

**Документация.** <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument2d_ksarcbyangle.html>

**Измерено.** ПОРОГ ИЗМЕРЕН РАЗЛИЧАЮЩЕЙ ПОСТАНОВКОЙ, а не выведен: зонд `scratch/_arc_angle_range_probe.py` держит ТОЧЕЧНЫЙ НАБОР неизменным и меняет только числовое представление дуги (аналитический объём у всех проб один — 20·π·30²/4). Измерено на поставке `artifacts/publish-mania-20260920`: R1 (0°, +90°, конец 90°) → 14137.166941; R2 (360°, +90°, конец 450°) → `GEOMETRY_FAILED`; R3 (360°, −90°, конец 270°) → 14137.166941; R4 (270°, +90°, конец 360°) → 14137.166941; R5 (315°, +90°, конец 405°) → `GEOMETRY_FAILED`; R6 (−45°, +90°, конец 45°) → 14137.166941; R7 (−350°, −90°, конец −440°) → `GEOMETRY_FAILED`; R8 (10°, −90°, конец −80°) → 14137.166941. РАЗДЕЛЯЮЩИЕ СЛУЧАИ: R3 (старт РОВНО 360°) и R4 (конец РОВНО 360°) ПРОХОДЯТ, поэтому триггер — не «старт 360» и не «конец 360», а ВЫХОД ЗА ±360°; пары R5↔R6 и R7↔R8 — это один и тот же сектор, записанный двумя способами, то есть отказ принадлежит ПРЕДСТАВЛЕНИЮ, а не геометрии. Сторон ДВЕ (за +360° и за −360°), иначе правило выведено из половины наблюдений. Приведение углов не переистолковывает большие дуги: зонд контура, вариант G — дуга (0°, +270°) даёт 42411.500823 = 20·(270/360)·π·30², то есть ядро соблюдает размах больше 180°, а не берёт меньшую дугу. ПОСЛЕ ПРАВКИ на поставке `artifacts/publish-arcrange-20260920` ВСЕ ВОСЕМЬ проб дают эталон: R2, R5 и R7 перестали отказывать, а R1, R3, R4, R6 и R8 не изменились. Покрытие в приёмке: `mania.json` — `MANIA.17.negative_tests` проверяет ОБЕ стороны (`angle_range_pairs`), сравнивая сектор, записанный углами за пределом, с его двойником внутри диапазона

**Ожидание.** вызывающий, не выходящий за объявленный диапазон `start_deg`/`sweep_deg`, получает ту же геометрию, что и при записи того же сектора углами внутри диапазона

**Наблюдено.** ИСПРАВЛЕНО: углы сдвигаются на целое число оборотов — ровно на столько, чтобы конечный угол вошёл в [−360°, 360°]; размах при сдвиге НЕ меняется, поэтому дуга остаётся той же. Если конечный угол уже внутри диапазона, значения возвращаются как есть. ДЕФЕКТ ПРИБОРА, ПОЙМАННЫЙ КОНТРОЛЕМ, А НЕ РАССУЖДЕНИЕМ: первая редакция правки передала в ядро `first, second` (начало, конец) вместо прежних `Min`/`Max` — и пробы с ОБОИМИ углами внутри диапазона, которых правка не должна касаться, поменяли результат: R8 дал 42411.500823 вместо 14137.166941, R3 — то же. То есть ПОРЯДОК ДВУХ УГЛОВ НЕСУЩИЙ: при отрицательном sweep прежний код подавал меньший угол первым. Утверждение «правка аддитивна» стояло в комментарии ДО того, как стало верным; верным его сделал возврат `Min`/`Max`. ДОПОЛНЕНО 20.09.2026, 22:05 — ПОСЛЕДНЯЯ ВЕТВЬ ТЕПЕРЬ ИЗМЕРЕНА, а не названа неизмеренной: случай `|sweep_deg| > 360` с конечным углом ВНУТРИ диапазона (например старт −180°, sweep +400° — то, чего правка НЕ касается, потому что она сдвигает углы, но размах не меняет) ОТКАЗЫВАЕТ ИМЕНОВАННО и ДО COM: `INVALID_ARGUMENT`, «Эскиз не прошёл проверку (sweep_deg): Дуга не может перекрыть 400 градусов». Измерено зондом `scratch/_arc_sweep_over_turn_probe.py` на поставке `publish-arcrange-20260920`, восемь проб: B1 (0°,+90°) 14137.166941; B2 (0°,+270°) 42411.500823; B3 (0°,+360°) 56548.667764616344 — полный круг; B4 (−180°,+360°) 56548.667764616344 — тот же полный круг с другого старта, то есть запись полного оборота от старта НЕ зависит; B5/B6 (0°,+400° и −180°,+400°) и B7/B8 (0°,−400° и 10°,−370°) — все четыре `INVALID_ARGUMENT`. Пары B5↔B6 и B7↔B8 различающие по ПРЕДСТАВЛЕНИЮ: первое написание правка сдвигает, второе не трогает, и отказ ОДИНАКОВ, значит поведение не зависит от того, сработала ли правка. Схема при этом объявляет `sweep_deg` до ±720°, то есть объявленный диапазон ШИРЕ принимаемого — но расхождение закрыто ИМЕНОВАННЫМ отказом с названной величиной, а не молчанием и не подменой геометрии; поэтому это граница ОБЪЯВЛЕНИЯ (находка F-11), а не дефект поведения. `start_deg` вне ±720 отказывает на уровне контракта: «Значение 800 больше допустимого 720» — то есть у `start_deg` объявление и приём СОВПАДАЮТ, и расходится только `sweep_deg`. Схема НЕ сужена, и причина названа честно: сузить её запрещает §5 наряда, а не отсутствие измерения, — в отличие от прежней редакции, где причина была именно в неизмеренности

**Затронутые инструменты.** `kompas_create_sketch`, `kompas_edit_sketch`

**Что исправлять.** исправлено 20.09.2026 в наряде §3.5: добавлен `ArcEndpoints`, приводящий конечный угол в выражаемый диапазон; порядок аргументов `Min`/`Max` СОХРАНЁН. Поставка переиздана (`artifacts/publish-arcrange-20260920`), все прогоны пересняты на её бинарниках (группа `mania` 43 строки, полный прогон 1009 строк, отказов 0), зонд повторён — 8 проб из 8. Правило, выведенное здесь: АДДИТИВНОСТЬ ПРАВКИ ДОКАЗЫВАЕТСЯ КОНТРОЛЕМ НА ВХОДАХ, КОТОРЫХ ПРАВКА НЕ КАСАЕТСЯ, а не рассуждением о её ветках. Зонд получил эту проверку как обязательную: без неё две испорченные пробы выглядели бы исправленными. ДОПОЛНЕНО 22:05: ветвь `|sweep_deg| > 360` доведена до измерения и до ПРИЁМКИ — строка `MANIA.17.negative_tests` проверяет теперь и знак, и конечный угол за ±360°, и полный оборот с разных стартов, и размах больше оборота (см. F-11 о том, почему прежняя редакция этой строки ничего не различала)

### F-11 — исправленный дефект прибора ПРИЁМКИ (строка `MANIA.17` читала объём ПЕРВОГО тела документа при накапливающихся телах и проходила, не различая ничего; объявленный диапазон `sweep_deg` шире принимаемого — расхождение ОБЪЯВЛЕНИЯ)

**Существо.** ДВЕ ЧАСТИ, обе измерены 20.09.2026. (1) ДЕФЕКТ ПРИБОРА ПРИЁМКИ. Строка `MANIA.17.negative_tests` строила все секторы в ОДНОМ документе и после каждого выдавливания читала объём тела `bs[0]` — ПЕРВОГО в списке. Выдавливание `operation: base` ДОБАВЛЯЕТ тело, поэтому `bs[0]` навсегда остаётся первым: ОБЕ половины «различающей пары» измеряли ОДНО И ТО ЖЕ тело, и строка проходила, не различая ничего. Обнаружено при попытке расширить строку на полный оборот: новая проба вернула объём четверти диска там, где обязан был получиться полный круг. (2) РАСХОЖДЕНИЕ ОБЪЯВЛЕНИЯ. Схема `kompas_create_sketch` объявляет `sweep_deg` в [−720, 720], а принимается (−360°, 360]: размах больше полного оборота отказывает. У `start_deg` объявление и приём совпадают (вне ±720 отказ на уровне контракта), то есть расходится ТОЛЬКО `sweep_deg`.

**Класс и вес.** исправленный дефект прибора ПРИЁМКИ (строка `MANIA.17` читала объём ПЕРВОГО тела документа при накапливающихся телах и проходила, не различая ничего; объявленный диапазон `sweep_deg` шире принимаемого — расхождение ОБЪЯВЛЕНИЯ); высокая по последствиям доказательства: строка приёмки ЗАЯВЛЯЛА различение, которого не делала, то есть подтверждала исправление F-09 и F-10 чужим измерением; дефект продукта при этом отсутствует — поведение продукта корректно.

**Где.** `прибор: `scripts/mcp-smoke.py`, строка `MANIA.17.negative_tests` (помощник `probe_sector`); объявление: `artifacts/*/schemas/kompas_create_sketch.json`; приём: `src/KompasMcp.Domain/Geometry/SketchValidation.cs:48-51``

**Документация.** <https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/ksdocument2d_ksarcbyangle.html>

**Измерено.** (1) Измерено зондом `scratch/_mania_body_addressing_probe.py` (поставка `publish-arcrange-20260920`): три сектора 90°/180°/270° в одном документе дают тел 1 → 2 → 3 с объёмами по индексам 14137.166941 / 28274.333882 / 42411.500823, а `bs[0]` на ВСЕХ трёх шагах равен 14137.166941 — то есть прежняя строка читала первое тело трижды. Ссылка на тело не годится и как адрес между вызовами: измерено, что набор ссылок в новом перечислении не совпадает со старым (в «новых» оказывались ВСЕ тела, 2 и 3 штуки). ИСПРАВЛЕНО адресацией, которая не угадывается, а обеспечивается: ОДИН ДОКУМЕНТ — ОДНО ТЕЛО; помощник отказывается измерять, если тел не ровно одно («тел не одно: N»). После правки строка даёт РАЗЛИЧАЮЩИЕСЯ числа: положительный sweep 14137.166941154068, отрицательный 14137.166941154068, сторона «+360°» 14137.166941154068 против 14137.166941154068, сторона «−360°» 14137.166941054347 против 14137.166941054347 (у сторон РАЗНЫЕ последние цифры — признак того, что тела действительно разные, а не одно и то же), полный круг 56548.667764616344 на обоих стартах и обоих знаках. (2) Измерено зондом `scratch/_arc_sweep_over_turn_probe.py`: (0°, +360°) и (−180°, +360°) дают 56548.667764616344 = π·R²·h; (0°, +400°), (−180°, +400°), (0°, −400°), (10°, −370°) дают `INVALID_ARGUMENT` «Дуга не может перекрыть N градусов»; (800°, +90°) и (−800°, +90°) дают `INVALID_ARGUMENT` «Аргументы не прошли контракт: Значение 800 больше допустимого 720». Покрытие в приёмке после правки: `mania.json` — `MANIA.17.negative_tests` несёт `angle_range_pairs`, `full_circle_pairs` и `over_turn_pairs`

**Ожидание.** строка приёмки измеряет ИМЕННО то тело, которое создала проверяемая операция, и разные написания одного сектора дают различимые измерения; объявленный диапазон поля совпадает с принимаемым

**Наблюдено.** ИСПРАВЛЕНО (1): адресация тела обеспечивается постановкой (один документ — одно тело), а не выбором элемента списка; помощник отказывается измерять при числе тел, не равном одному, — «одно слово для „пусто“ и „не прочитано“» здесь применено к «одно тело / не одно». Строка переизмерена на поставке `publish-arcrange-20260920`: группа `mania` **43 PASS / 0 FAIL**, полный прогон **1009 PASS / 0 FAIL**. НАЗВАНО, НЕ ИСПРАВЛЕНО (2): объявленный `sweep_deg` шире принимаемого; схема НЕ сужена, потому что §5 наряда запрещает править схемы опубликованных инструментов. Это НЕ технический долг и не отложенная возможность: это расхождение объявления, названное с измеренным порогом, и закрыть его может только наряд, которому разрешено трогать схемы. Молчания нет: вызывающий получает отказ с названной величиной

**Затронутые инструменты.** `kompas_create_sketch`, `kompas_edit_sketch`

**Что исправлять.** часть (1) исправлена 20.09.2026, 21:55: `scripts/mcp-smoke.py`, помощник `probe_sector` — один документ на пробу, отказ измерять при числе тел ≠ 1; группа и полный прогон пересняты на бинарниках поставки `publish-arcrange-20260920` (`src/` НЕ менялся, поэтому поставка та же и переиздание не требуется — измерено: адаптер в поставке байт в байт равен свежей сборке). Правило, выведенное здесь: **адрес объекта, который измеряет проверка, ОБЕСПЕЧИВАЕТСЯ постановкой, а не угадывается по индексу в списке; проверка, читающая „первый элемент“, обязана доказать, что элементов ровно один.** Часть (2) НЕ исправляется в этом наряде по прямому запрету §5 и названа открытой

## 6. Что осталось неподтверждённым

Разделено по причине, а не свалено в один список:

| Причина | Сколько строк | Что это значит |
|---|---|---|
| Маршрута нет в коде (план) | 3 | документированность маршрута подтвердить нечем; за реализованное не выдаётся |
| Действия не доведены до `verified` | 15 | общие зависимости: часть действий не проверена, строка не закрыта |
| Строки со ссылкой на схему инструмента нет в матрице | 17 | связь «режим → инструмент» восстановлена по семейству и по исходникам, а не прочитана из матрицы (F-05) |
| Инструмент без присутствия в матрице | 0 | нет: у каждого опубликованного инструмента есть либо собственная строка, либо объявленный маршрут строки |
| Мёртвые ветки в адаптере | 0 | F-01 и F-02 исправлены в этом наряде: `PlanarNormal` удалён, неподтверждённый `IsSaved` из продукта удалён |
| Знак дуги в эскизе | исправлено | F-09: конечный угол дуги брался как `start + abs(sweep)`, поэтому дуга с отрицательным `sweep_deg` строилась отражённой. Концы упорядочены, поставка переиздана, различающая пара (плюс/минус 90° на четверти диска R30) стоит строкой `MANIA.17` и даёт один и тот же объём |
| Выразимость объявленного диапазона углов | исправлено | F-10: схема объявляет `start_deg` и `sweep_deg` до ±720°, но адаптер передаёт в ядро два угла, и набор с конечным углом 405° отказывал с `GEOMETRY_FAILED`. Порог ИЗМЕРЕН (выход за ±360°), углы приводятся в выражаемый диапазон, порядок аргументов `Min`/`Max` сохранён. Последняя ветвь доведена до измерения: размах больше оборота отказывает ИМЕНОВАННО («Дуга не может перекрыть N градусов»), полный оборот (0°,+360°) и (−180°,+360°) даёт один и тот же объём π·R²·h |
| Строка приёмки читала ЧУЖОЕ тело | исправлено | F-11: `MANIA.17` строила все секторы в одном документе и читала объём тела `bs[0]` — ПЕРВОГО. Тела при выдавливании накапливаются (измерено 1 → 2 → 3), поэтому обе половины «различающей пары» измеряли одно и то же тело: строка проходила, НЕ РАЗЛИЧАЯ НИЧЕГО. Исправлено адресацией «один документ — одно тело» с отказом измерять при числе тел ≠ 1 |
| Объявленный `sweep_deg` шире принимаемого | названо, НЕ исправлено | F-11, часть 2: схема объявляет ±720°, принимается (−360°, 360°]. Схема **не сужена**, и причина названа честно: сузить её запрещает §5 наряда, а не отсутствие измерения. Молчания нет — отказ именованный, с названной величиной. У `start_deg` объявление и приём совпадают |

Документация **не** подтверждена у 4 строк из 74: у 3 это строки-план, у остальных — зависимости и режимы семейств, маршрут которых в коде не заявлен. Ни одна из этих строк не выдана за проверенную.

## 7. Границы аудита

- Проверка документации — по официальной справке SDK целевой версии; TLB, interop, IntelliSense и прежние отчёты основанием не считались. `QueryInterface`, позднее связывание и совместное использование API5/API7 сами по себе нарушением не считаются — проверялась документированность запрошенного интерфейса, исходного объекта и перехода.
- Поведение реализации оценено на бинарях поставки, указанных выше; строки приёмки взяты из групповых отчётов этой же поставки. Полный прогон и групповые прогоны не складывались.
- Рабочий код правился ТОЛЬКО там, где это предписывал наряд: удалены обе недостижимые ветки (F-01, F-02), исправлен знак дуги в эскизе (F-09) и приведение конечного угла дуги в выражаемый диапазон (F-10) — оба последних вскрыл сценарий Model Mania (наряд §3.3/§3.5). Возможности не удалялись, требования ради зелёного результата не менялись, схемы опубликованных инструментов не трогались. Остальное найденное оформлено как пункты исправления (F-03…F-11), причём F-03 и F-04 названы состояниями, а не дефектами. Отдельно: строка приёмки `MANIA.17` САМА оказалась прибором, читавшим чужое тело (F-11) — её подтверждение F-09 и F-10 было приписанным и снято; после исправления адресации строка измерена заново.
- Клиентская приёмка не запускалась, Trust не менялся, в АСКОН обращений не было, модели заказчика для изменения не запускались.
- Зеркало справки лежит вне поставки; секреты и содержимое конфигураций в документ не попали.
- Каждая ссылка на справку в этом документе открывалась по проводу отдельной проверкой (`scratch/_check_compliance_links.py`): битая ссылка читалась бы как подтверждение документации, поэтому исходов три — годная, битая, не проверена (отказ сети назван отдельно, а не зачтён годной).

## 8. Итог

- **Инструментов проверено:** 50 — все опубликованные; схем 50, имена свойств совпадают с проводом у 50, значимая форма — у 50. Инструментов без присутствия в матрице (ни собственной строки, ни объявленного маршрута): нет.
  - **присутствие названо двумя способами** (наряд §3): собственной строкой — 43 инструментов, объявленным маршрутом строки — 15. Разница названа, а не слита в одно число: «строку инструменту дали» и «строку инструмент обслуживает объявленным маршрутом» — разные состояния;
  - **отрицательный контроль присутствия — три половины, сторож доказан: True**: (а) синтетический опубликованный инструмент, не упомянутый нигде, назван — True; (б) инструмент, названный только объявлением поддержки каталога и не опубликованный, назван проверкой каталога — True; (в) у инструмента `kompas_create_aux_geometry`, присутствующего ТОЛЬКО объявленным маршрутом, маршрут снят — назван: True. Половина (в) доказывает, что способ «объявленный маршрут» действительно зачитывается;
  - **объявление поддержки каталога проверено отдельно** (класс дефекта AUX-SKETCH): объявлений прочитано 19 (форм записи: строка — 60, словарь — 36), инструментов НЕТ на проводе — ни одного. Поле `notes` словарной формы НЕ читается (11 объяснений): в нём инструменты названы именно тогда, когда их НЕТ, и чтение имён оттуда дало бы ложную находку на записи, которая поддержку отрицает.
- **Режимов проверено:** 74 строк матрицы; документация подтверждена у 70, реализация — у 56.
- **COM-маршруты:** 243 из 305 имеют страницу члена или список членов интерфейса; числовые идентификаторы типов: 35 совпали, 0 не найдено в таблице.
- **Заявленные действия против доказательств — три сверки (F-08):** строк с заявленным `verified` — 71; приписок (статус без единой привязанной строки) — 0; `verified` рядом с FAIL — 0.
  - **по имени режима** (так искал бы рецензент): у 0 строк НИ ОДНА строка приёмки не называет режим ни идентификатором, ни описанием — доказательство по имени режима не находится;
  - **по привязке и вызовам**: у 0 строк есть действие, которого нет в вызовах привязанных проверок (прибор: 14424 вызовов, 1059 строк с окном);
  - **по имени строки**: у 71 строк все действия названы одноимённой строкой (`B3M.<режим>.<действие>`), у 0 — нет.
  - **следствие для статуса строки**: отказ сторожа понижает `implementation.status` до «Недостаточно проверки» и называется причиной понижения; строк, потерявших «Подтверждено» именно поэтому — 0 (подтверждённых строк стало 56). Изменилось ПРАВИЛО ВЫВОДА, а не измерение: прежде «Подтверждено» требовало закрытости по ярлыкам и хотя бы одного PASS, но не спрашивало, подтверждают ли привязанные строки заявленные действия.
  - **остаток отказа разобран, а не закрыт дописыванием привязок** (`scratch/_f08_link_gap.py`, `scratch/_f08_candidate_ownership.py`): привязка недостаёт РОВНО У ОДНОЙ строки — `SM-02.base_extrusion.blind`, где `G03i` и `G03r` читают признак того же режима и не были привязаны ни к одной строке (привязка добавлена, `coverage/solid-v24/matrix.json` → `meta.f08_linkage_note`); у остальных проверки-кандидаты уже принадлежат ДРУГИМ режимам (2712 пар) либо чужим семействам (282) — приписать их значило бы выдать чужое доказательство за своё. Это работа, а не правка отчёта.
  - **Контроли прибора «действие из вызовов»:** положительный — на строках B3M, где действие названо именем строки, выводятся 8 действий из 9; не выводится geometry_validation (измерение объёма записывается в окно строки `create`); отрицательный — подпись выполняется и на ЧУЖИХ строках, поэтому вывод по вызовам употребляется как отказ, а не как подтверждение; неразличимы по вызовам: geometry_validation и read.
  - **Отрицательный контроль сторожа:** половина «обязан отказать» — отказал, «обязан молчать» — молчит; сторож доказан: да; отказов на реальных данных — 0.
- **Находки:**
  - **F-01** — исправленный дефект продукта (мёртвая ветка удалена). была низкой — на результат не влияла; исправлено
  - **F-02** — исправленный дефект продукта (мёртвая ветка удалена, состояние сохранённости отделено от ревизии). была низкой — работал отпечаток; исправлено
  - **F-03** — состояние объёма будущей работы (не дефект). —
  - **F-04** — названная граница непубликуемого поля (не дефект). —
  - **F-05** — исправленный дефект доказательств (не дефект продукта). —
  - **F-06** — исправленный дефект прибора аудита. высокая — прибор подтверждал несуществующий маршрут
  - **F-07** — исправленный дефект прибора аудита. средняя — владелец маршрута был назван несуществующим интерфейсом
  - **F-08** — названный пробел трассируемости доказательств (не дефект продукта). средняя — статус строки опирался на ярлык действия, а не на доказательство под ним; измерена тремя независимыми сверками и двумя приборами 20.09.2026
  - **F-09** — исправленный дефект продукта (знак дуги не доходил до конечного угла). высокая для вызывающего — дуга с ОТРИЦАТЕЛЬНЫМ `sweep_deg` строила отражённую геометрию, то есть не ту, которую просили, и молча; исправлено, поставка переиздана, все прогоны пересняты, различающая пара стоит строкой `MANIA.17`
  - **F-10** — исправленный дефект продукта (объявленный диапазон углов дуги шире выражаемого; первая редакция правки была НЕ аддитивной, и это поймал контроль зонда). средняя — вызывающий, не выходя за объявленный [−720, 720], получал `GEOMETRY_FAILED` без объяснения; исправлено, поставка переиздана, все прогоны пересняты
  - **F-11** — исправленный дефект прибора ПРИЁМКИ (строка `MANIA.17` читала объём ПЕРВОГО тела документа при накапливающихся телах и проходила, не различая ничего; объявленный диапазон `sweep_deg` шире принимаемого — расхождение ОБЪЯВЛЕНИЯ). высокая по последствиям доказательства: строка приёмки ЗАЯВЛЯЛА различение, которого не делала, то есть подтверждала исправление F-09 и F-10 чужим измерением; дефект продукта при этом отсутствует — поведение продукта корректно

- **Границы, названные прямо:** утверждение «MCP полностью согласуется с документацией» не делается; отсутствие отказов в прогоне не закрывает непроверенные обязательные действия; обязательный объём профиля — 3 строк объявлены планом, и они не считаются сделанными.
