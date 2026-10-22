# Листовое моделирование и развёртки - решения и ссылки

Наряд `SHEET_METAL_RESEARCH` (22.10.2026). Здесь история решений и ссылки на страницы справки;
в коде остаётся одна строка `History: docs/decisions/sheet-metal.md#<якорь>`. Отчёт с перечнем
маршрутов, проектом профиля и вопросами заказчику - `SHEET_METAL_RESEARCH_REPORT_20261022.md`.

## Зеркало и набор имён страниц

- Каталог: `reference/kompas-sdk-v24/docs-wire-20261022-sheetmetal/`, сценарий съёмки -
  `reference/kompas-sdk-v24/_fetch_docs_wire_20261022_sheetmetal.py`, состав - `wire-report.json`.
- **Решение: снимаем набор «API интерфейсов. Версия 7» (имена без префикса).** В справке v24 два
  набора: `ksapi_isheetmetalcontainer.html` («API интерфейсов. KsAPI», 22 страницы, только методы) и
  `isheetmetalcontainer.html` («API интерфейсов. Версия 7», 428 страниц, свойства и методы).
  Продукт работает через API7, поэтому KsAPI-набор в зеркало не попадает: цитировать его как
  основание маршрута нельзя, он описывает другой набор интерфейсов.
- Оглавление берётся из `docs-wire-20261004/hmcontent.js` по `hf:`-узлу `isheetmetalcontainer.html`:
  поддерево восстанавливается по уровням без обращения к сети.

## Вход в блок

`isheetmetalcontainer.html`: «ISheetMetalContainer является дополнительным к интерфейсу IPart7 и
позволяет работать с коллекциями объектов гибки… можно получить от IPart7 посредством вызова метода
IUnknown::QueryInterface». `ipart7.html` повторяет это в списке дополнительных интерфейсов.
Другого входа в справке нет - `kompas_get_context`/`kompas_list_features` контейнер не отдают.

## Запись: `Add` + `Update`

`isheetmetalbodies_add.html`: «Метод позволяет создать новый интерфейс базового листового тела»,
«После получения нового интерфейса нужно задать параметры листового тела и вызвать метод
IModelObject::Update». То же в `isheetmetalbends_add.html` и остальных `*_add`. Отсюда правило
профиля: ни один инструмент блока не отвечает успехом без чтения назад (счётчик коллекции + значения
полей), иначе это повтор строки долга 62.

## Развёртка: это состояние тела, а не отдельное тело

`isheetmetalbody_straighten.html` («Позволяет разгибать и сгибать тело», BOOL) и
`isheetmetalbendunfoldparameters*.html` (`IsCreated` - «Заданы ли параметры развертки», `Unfold`,
`UnfoldPlane`, `FixedFaces`, `ExcludedBendObjects`, `UpdateParam`, `DeleteParam`). Страницы «создать
развёртку как тело/вид» нет. Признак `native_sheet_metal` из `docs/01_TECHNICAL_SPEC.md` §1.11
ставится по факту построения через `ISheetMetalBodies`, а не по расчёту.

## DXF: маршрута нет

Ни одна страница семьи не упоминает DXF. Документированная выгрузка - `ksSaveToDXF` для двумерных
документов (`ksdocument2d_kssavetodxf.html`). Вывод: «DXF плоских деталей» по листовой детали
документированного маршрута не имеет; двухшаговый маршрут «`Straighten` → чертёж → конвертер» -
предложение, а не документированный путь, и решается заказчиком (отчёт §5, §7).

## Проба: прибор и что он измерил

<a id="probe"></a>
Прибор - `scripts/probes/sheet-metal/` (C#, свой `.csproj`, x64, interop из `KompasInteropDir`,
в `KompasMcp.sln` не входит, ссылок на `src/` нет). Вход в КОМПАС - маршрут продукта, а не
`CoCreateInstance("KOMPAS.Application.7")`: `KOMPAS.Application.5` → `ksGetApplication7()` →
`TransferInterface(документ, ksAPI7Dual)` → `IKompasDocument3D.TopPart`. Прибор пишет JSON
«ожидалось / прочитано» по каждой проверке.

- **Контейнер.** `TopPart as ISheetMetalContainer` отвечает на живом документе: QI работает.
- **Тело.** `SheetMetalBodies.Add()` → `ISheetMetalBody`; `Sketch` (эскиз, перенесённый в API7 из
  API5), `Thickness = 1.5`, `Radius = 2`, `Direction = dtNormal`, затем `Update()` = TRUE. Чтение
  назад совпало с заданным, счётчик коллекции 0 → 1. `Profile` после задания `Sketch` читается
  непустым, то есть свойства не взаимоисключающие (справка связи не описывает).
- **Сгиб.** `SheetMetalBends.Add()` → `ISheetMetalBend`; `BendObject` = ребро тела, перенесённое в
  API7 (`IModelObject`), `Angle = 90`, `Update()` = TRUE, угол читается назад 90°, счётчик 0 → 1.
- **Развёртка.** Флаг `ISheetMetalBody.Straighten = true` читается назад TRUE, но габарит не
  меняется - флаг состояния геометрию не перестраивает. `UpdateParam()` при пустых входах = FALSE.
  Работает третий маршрут: `UnfoldPlane` + `FixedFaces` + `Unfold = true` → `IsCreated` = TRUE,
  тело плоское (габарит по нормали 0..1.5 вместо -15..1.5). `UpdateParam()` при этом отвечает FALSE,
  то есть его FALSE не означает «не создано» - судить только по `IsCreated` и геометрии.
  `SheetMetalBendedStraightens.Add(o3d_sheetMetalBendUnfold)` возвращает null: операция не создаётся.
- **Сохранение и открытие.** `SaveAs` → `Documents.Open` даёт `ISheetMetalContainer` с теми же
  счётчиками, толщиной, радиусом и `Straighten`.

Доказательства прогона - `docs/acceptance/evidence/20261022-sheetmetal-238fedb/` (JSON «ожидалось /
прочитано», команда, версия КОМПАС, итог PASS 35 / FAIL 3 / INFO 7).

## Что не входит и почему

Отбортовка (свойство отдаёт `ISheetMetalBends`, страницы коллекции нет), обечайка и линейчатая
обечайка (интерфейс есть, коллекции нет), подсечка (свойство называет типом `ISheetMetalLineBends`).
Причина одна: `Add` не документирован - маршрут создания не берётся из наличия имени в TLB.

## Расхождения справки

`isheetmetalcontainer_sheetmetalundercuts.html` называет типом `ISheetMetalLineBends` при
существующем `ISheetMetalUndercut`; `isheetmetalcontainer_sheetmetalruledshells.html` - `ISheetMetalBodies`
при существующем `ISheetMetalRuledShell`. Не исправлялись: правка вендорской страницы невозможна,
в план такие маршруты не берутся до подтверждения.
