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
