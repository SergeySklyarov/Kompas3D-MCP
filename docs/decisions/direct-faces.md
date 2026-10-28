# Прямое изменение граней - решения и ссылки на страницы

Наряд `E5_RESEARCH` (28.10.2026). Здесь история решений и ссылки на страницы справки; в коде
останется одна строка `History: docs/decisions/direct-faces.md#<якорь>`. Перечень маршрутов, проект
профиля, порядок нарядов и вопросы - `E5_RESEARCH_REPORT_20261028.md`.

## Зеркало и набор имён страниц

- Каталог: `reference/kompas-sdk-v24/docs-wire-20261028-e5/`, сценарий съёмки -
  `reference/kompas-sdk-v24/_fetch_docs_wire_20261028_e5.py`, состав и коды ответов -
  `wire-report.json` в нём же.
- **Решение: снимаем набор «API интерфейсов. Версия 7»** (имена без префикса `ksapi_`). В справке v24
  два набора с одними и теми же возможностями: `ksapi_ifacemover.html` (KsAPI, только методы) и
  `ifacemover.html` (API7, свойства и методы). Продукт работает через API7, поэтому цитируется
  API7-набор; KsAPI-страницы остаются в `sdk-docs/` и в набор не копируются.
- Оглавление - `_hmcontent.js` (снято 04.10.2026), имена берутся по `hf:`-узлам: префиксы
  `ifacemover*`, `ifaceremover*`, `irestoredsurface*`, `ibodysurfaceremover*`, `isplitsolid*`,
  `isurfacecontainer*`, `imodelobject*`, `ipart7`/`iparts7`, `ibody*`, `iface_*`, `iupdate`,
  `irebuild` плюс один уровень ссылок по белому списку тем (грань, тело, объект модели, контейнер
  поверхностей, обновление, деталь, импорт).
- **Отрицательный контроль - 404 на пять имён**: `ifaceresizer`, `ifacereplacer`, `ifilletchanger`,
  `ifaceoffsetter`, `ifacepositionchanger`. Это и есть довод «документированного маршрута нет» для
  строк `resize_face`, `replace_faces.offset`, `change_fillet`, `change_face_position`: страницы нет,
  а номер типа в `ksConstants3D.tlb` (`o3d_FaceResizer=660`, `o3d_FaceReplacer=659`) маршрутом не
  является (`AGENTS.md`, «Документация КОМПАС - обязательная граница»).

## Вход в блок

`isurfacecontainer.html`: «Дополнительный интерфейс компонента. Данный интерфейс можно получить у
компонента IPart7 посредством вызова метода IUnknown::QueryInterface». Прямое редактирование живёт
в контейнере, который назван «поверхностным»: его свойства `FaceMovers`, `FaceRemovers`,
`RestoredSurfaces`, `BodySurfaceRemovers` отдают коллекции операций с ГРАНЯМИ тела. Другого входа в
справке нет.

## Запись: `Add` + `Update`

Каждый `*_add.html` возвращает интерфейс операции (`IFaceMovers::Add` → `IFaceMover`,
`IFaceRemovers::Add` → `IFaceRemover`, `IRestoredSurfaces::Add` → `IRestoredSurface`), после чего
параметры задаются свойствами и вызывается `IModelObject::Update`
(`imodelobject_methods.html` - единственный метод `Update`). Отсюда правило профиля: ни один
инструмент блока не отвечает успехом без чтения назад (объём/площадь/число граней/`IsSolid` до и
после), иначе это повтор строки долга 62.

Примечание на `ifacemover.html` и `ifaceremover.html` дословно говорит «После задания параметров
ЗАПЛАТКИ требуется вызвать метод IModelObject::Update» - текст скопирован со страницы заплатки;
правило от этого не меняется (`Update` обязателен), но цитировать его как описание перемещения
граней нельзя.

## Что документировано, а что нет

| Строка матрицы | Маршрут | Страница |
|---|---|---|
| `move_faces.tangent_group` | `ISurfaceContainer.FaceMovers` → `IFaceMovers.Add` → `IFaceMover.SetFaces(Faces, TangentFaces)` | `ifacemover_setfaces.html` («TangentFaces - добавлять касательные грани») |
| `delete_faces.keep_body` / `open_shell` | `ISurfaceContainer.FaceRemovers` → `IFaceRemovers.Add` → `IFaceRemover.SaveBody` (BOOL) | `ifaceremover_savebody.html`, `ifaceremover_props.html` |
| `restore_faces.by_face` | `ISurfaceContainer.RestoredSurfaces` → `IRestoredSurfaces.Add` → `IRestoredSurface.Face` | `irestoredsurface_face.html` |
| `split_solid_ref` | `IModelContainer.SplitSolids` → `ISplitSolid.CutObjects` (эскизы, поверхности) | `isplitsolid_cutobjects.html` |
| `change_face_position`, `change_fillet`, `replace_faces.offset`, `resize_face` | документированного программного маршрута НЕТ | 404 на пять имён (см. выше) |

**Решение по `change_fillet`:** для тела с историей радиус меняется правкой признака дерева - этот
маршрут в продукте уже есть (`src/KompasMcp.Api5Adapter/Api5Session.FilletEdit.cs`); прямое
изменение скругления на теле БЕЗ истории не документировано. Строку нельзя закрыть «через
`IFillet`»: `IFillet`/`IFullFillet` создают скругление, а не меняют существующее.

**Решение по `change_face_position`:** отдельного признака «положение грани» в документированных
интерфейсах нет; `IFaceMover` задаёт `Offset` (расстояние) и `Direction` (направление). Вопрос
«сливать ли строку в `move_faces`» - к технику (вопрос В1 отчёта), сам не сливал.

## Чтение результата (чем сравнивать)

- `iface_propers.html` / `iface_methods.html`: `IsPlanar`, `IsCylinder`, `IsCone`, `Body`,
  `ConnectedFaces`, `LimitingEdges`, `Radius`, `GetArea`, `GetAreaEx`, `GetNormal`, `GetPoint`.
  Положение грани после перемещения проверяется через `GetPoint`/`GetNormal`/`GetArea`, а не по
  имени.
- `ibody7_props.html`: `IsSolid` - «Является ли тело твердым или оболочкой (TRUE - твердое тело,
  FALSE - оболочка)». Это готовый признак для `delete_faces.open_shell` и для проверки
  восстановления после `restore_faces`.
- Объём и площадь - существующим маршрутом продукта `ksPart.CalcMassInertiaProperties` →
  `ksMassInertiaParam` (`.v()`, `.F()`), уже реализован в `Api5Session.Geometry.cs`; второй маршрут
  не нужен.
- Грани тела читаются маршрутом продукта `ksBody.FaceCollection()` → `ksFaceCollection`
  (`Api5Session.EdgePlacement.cs`). **Открытый вопрос замера:** как из API5-грани получить `IFace`
  для массива `Faces` (`SAFEARRAY` `VT_DISPATCH`) - кандидаты `ksEntity` → `QueryInterface` и
  `KompasObject.TransferReference`; это первое, что должна померить проба (вопрос В2).

## Тела без истории

Документированный импорт уже есть в продукте: `kompas_import_step` (`ksDocument3D.LoadFromAdditionFormat`
+ `ksAdditionFormatParam`). У импортированного тела нет дерева построения, поэтому:
- применимы операции контейнера поверхностей (они работают с ГРАНЯМИ и ТЕЛОМ, а не с признаками):
  `FaceMovers`, `FaceRemovers`, `RestoredSurfaces`, `BodySurfaceRemovers`, `SplitSolids`;
- НЕ применимы маршруты правки признака (`IFeature7`, правка скругления/выдавливания) - править
  нечего;
- `IBodyReposition` (`ibodyreposition_propers.html`: `RepositionBody`, `RepositionObjects`,
  `Position`, `CopyBoby`) - документированный способ переместить тело или поверхность целиком; это
  не `IFaceMover` и в профиль прямого редактирования граней не входит, но закрывает часть запроса
  «подвинуть тело STEP» без истории.

## Проба: что измерено (не выведено)

Проба `scripts/probes/e5/`, доказательства `docs/acceptance/evidence/20261028-e5-probe-c4cbcc0/`.

- `(ISurfaceContainer)IPart7` работает приведением интерфейсов в .NET; `QueryInterface` вручную не
  нужен.
- Грань API5 отвечает как `IFace` ТОЛЬКО через `KompasObject.TransferInterface(грань, ksAPI7Dual, 0)`;
  прямое приведение даёт 0 из 6.
- `IFaceMover`: `SetFaces([грань], false)` + `Offset` + `Direction` + `Update` даёт ожидаемые числа
  (объём `V + d·A`, площадь `F + P·d`), граней не меняет.
- `IFaceRemover.SaveBody=false` даёт оболочку (граней −1, объём 0).
- `IFaceRemover.SaveBody=true` НЕ делает ничего и не отказывает: `Update()` возвращает true, число
  граней и объём прежние. Причина не установлена - маршрут нельзя считать рабочим.
- `IRestoredSurface` создаёт отдельный объект-поверхность (тел 1 → 2) и оболочку не вылечивает.
- Тело БЕЗ истории (импорт STEP, 11 граней): грани 1 и 2 перемещаются (`Update()=true`, объём растёт),
  грань 0 отказывает честно - `Update()` возвращает FALSE. Удаление с `SaveBody=false` даёт оболочку.
  Прямое редактирование от дерева построения не зависит.
- В `Interop.KompasAPI7.dll` v24 у `IFace` нет `GetNormal`/`ParamUMin..Max`, у `IBody7` нет `IsSolid`
  (компилятор, `CS1061`), хотя справка их описывает: проверка твёрдости - по объёму.

## <a id="implementation"></a>Реализация: `kompas_move_faces` (наряд DIRECT_FACES_CORE)

Маршрут, контракт и проверки первого инструмента блока. Остальные четыре инструмента профиля
`direct-faces-v1` - отдельные наряды этого выпуска.

- **Маршрут.** `IPart7` (приведением) → `ISurfaceContainer` → `FaceMovers.Add()` → `IFaceMover`:
  `SetFaces(Faces, TangentFaces=false)`, `Offset`, `Direction` → `IModelObject.Update()`; затем
  перестроение API7 и подъём ревизии. Грань переносится `KompasObject.TransferInterface(грань,
  ksAPI7Dual, 0)` → `IFace` (`Api7Bridge.TransferTo7`), как в пробе.
- **Сторона не назначается.** `Direction` - логический признак «Сменить направление», а не вектор:
  измерено только значение `true` (объём вырос). Поэтому контракт называет сторону полем `reverse`
  и передаёт его как есть, а результат сообщает `observed_offset_mm` - фактическое смещение со
  знаком. Утверждать «`false` - внутрь» значило бы утверждать непроверенное.
- **Успех подтверждается числами.** Прочитанное сравнивается с ожидаемым: объём после против
  `V + d·A` и площадь после против `F + P·d`, где `A` и `P` - площадь и периметр НАЗВАННЫХ граней
  (читаются `GetArea` и `EdgeCollection`/`GetLength`), `d` - измеренное смещение. Арифметика
  вынесена в `KompasMcp.Domain.Geometry.FaceMoveEffect` и покрыта модульными тестами: без COM её
  иначе не проверить.
- **Отказы.** Пустой список граней, нулевое смещение, повторы в списке и `tangent_group = true`
  отвергаются ДО COM. Грань чужого тела отвергается по COM-указателю владельца: реестр ссылок
  хранит документ и ревизию, но не тело, поэтому владелец спрашивается у модели
  (`details.body_index` / `details.target_body_index`). `Update() = false` - отказ `GEOMETRY_FAILED`
  с `partial_effects`; `Update() = true` без изменения объёма - `NO_GEOMETRY_CHANGE`, а не успех.
- **Площадь как второй сигнал.** Совпадение объёма не доказывает, что перемещена ИМЕННО названная
  грань: у равнозначных граней объём совпадёт. Полоса `P·d` принадлежит периметру названных граней,
  поэтому её несовпадение - отказ, а не предупреждение.
- **Что не сделано.** Касательные грани (`tangent_group`) - наряд `DIRECT_FACES_TANGENT`; тело без
  истории - `DIRECT_FACES_NO_HISTORY`; `save_reopen` и приёмочная группа `mcp-smoke` E5 - следующий
  шаг этого наряда, поэтому ни одно действие строки матрицы не поднято до `verified`.
