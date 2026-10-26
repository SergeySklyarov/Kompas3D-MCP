# Решения по блоку «спецификация / BOM по сборке»

История решений и ссылки на страницы справки. Код сюда не переносится: в коде остаётся строка
`History: docs/decisions/bom.md#<якорь>`.

Страницы сняты 26.10.2026 нарядом `BOM_RESEARCH` в `reference/kompas-sdk-v24/docs-wire-20261026-bom/`
(735 страниц: поддеревья `spec_work.html`, `ksspcdocument.html`, `ksspecification.html` плюс
семейства `ipropertykeeper`, `iproductdatamanager`, `iembodiment`, `iparts7`, `kspartcollection`,
перечисления раздела и признака использования в спецификации). Сценарий съёмки -
`reference/kompas-sdk-v24/_fetch_docs_wire_20261026_bom.py`, отрицательный контроль - 404 на четыре
выдуманных имени.

## #routes — что документировано

Уровень (а) — BOM, который MCP собирает сам из дерева сборки и свойств компонентов:

| Возможность | Интерфейс и член | Страница | Программный маршрут |
|---|---|---|---|
| экземпляры верхнего уровня | `ksDocument3D.PartCollection(bool)` → `ksPartCollection.GetCount/GetByIndex` | `kspartcollection_getcount.html`, `kspartcollection_getbyindex.html` | есть |
| обозначение, наименование, материал | `ksPart.marking`, `.name`, `.material` | `kspart_props.html` | есть |
| фильтр «скрытый» | `ksPart.hidden` | `kspart_props.html` | есть |
| фильтр «исключён из расчёта» | `ksPart.excluded` | `kspart_props.html` | есть |
| признак стандартного изделия | `ksPart.standardComponent` | `kspart_props.html` | есть |
| файл компонента (полный путь) | `ksPart.fileName` | `kspart_props.html` | есть |
| вставка компонента из файла | `IPart7.Parts` → `IParts7.AddFromFile(FileName, ExternalFile, Redraw)` | `iparts7_addfromfile.html` | есть |
| признак использования в спецификации | `IProductDataManager.ObjectSpcUsed(PropObject, SpcUsed)`; сам менеджер — `QueryInterface` у `IKompasDocument` | `iproductdatamanager_objectspcused.html`, `iproductdatamanager.html` | есть (значения — `ksSpcUsedTypeEnum`) |
| исполнения | `IEmbodiment`, `IEmbodimentsManager` | `iembodiment.html` | интерфейс есть; точка входа (`IEmbodimentsManager`) страницей не подтверждена |
| номера системных свойств | `SystemPropertyType`: 4 Обозначение, 5 Наименование, 6 Количество, 15 Позиция, 20 Раздел спецификации | `systempropertytype.html` | значения есть, применение в COM не подтверждено (см. ниже) |
| чтение значения свойства по объекту `IProperty` | `IPropertyKeeper.GetPropertyValue(Property, Value, BaseUnit, FromSource)` | `ipropertykeeper_getpropertyvalue.html` | есть, но объект `IProperty` берётся у `IPropertyMng`, а он в справке есть только как KsAPI |

Уровень (б) — родная спецификация КОМПАС:

| Возможность | Интерфейс и член | Страница | Программный маршрут |
|---|---|---|---|
| объекты спецификации в модели (API5) | `ksDocument3D.GetSpecification()` → `ksSpecification` | `ksdocument3d_getspecification.html`, `ksspecification.html` | есть; проба подтвердила, что возвращается `ksSpecification` |
| чтение объекта по номеру, текста колонки, названия раздела | `ksSpecification.ksGetSpcObject`, `ksGetSpcObjectNumber`, `ksGetSpcObjectColumnText`, `ksGetSpcSectionName` | `ksspecification_methods.html` | есть |
| создание и правка объекта | `ksSpcObjectCreate`, `ksSpcObjectEdit`, `ksSpcObjectEnd`, `ksSpcIncludeReference`, `ksSpcVisible` | `ksspecification_methods.html` | есть |
| документ-спецификация `.spw` | `KompasObject.SpcDocument` / `SpcActiveDocument` → `ksSpcDocument` | `kompasobject_spcdocument.html`, `ksspcdocument.html` | есть |
| создание `.spw` | `ksSpcDocument.ksCreateDocument(ksDocumentParam)` | `ksspcdocument_kscreatedocument.html` | есть |
| объекты и листы `.spw` | `ksGetObjParam`, `ksExistObj`, `ksDeleteObj`, `ksGetSpcSheetSB` | `ksspcdocument_*.html` | есть |
| объекты спецификации (API7) | `ISpecificationDescriptions` → `ISpecificationDescription.BaseObjects` → `ISpecificationBaseObjects.Add` → `ISpecificationObject.Update` | `ispecificationbaseobjects.html` | есть |
| поля объекта (API7) | `ISpecificationObject.Section`, `.AdditionalSection`, `.Columns`, `.ObjectType` | `ispecificationobject_props.html` | есть |

## #not-included — что в план не входит

- **KsAPI.** `ksapi_ipropertymng*`, `ksapi_ipropertykeeper*`, `ksapi_iproductdatamanager_getobjectspcused`
  — это C++ KsAPI, а не COM. Страницы сняты, чтобы это было видно по каталогу, а не по памяти; маршрут
  в план не попадает.
- **`IPropertyManager`** (`ksapi_ipropertymanager*`) — Панель свойств, то есть UI, а не метаданные
  объекта. Совпадение имени с `IPropertyMng` — ловушка, а не маршрут.
- **Отчёты КОМПАС.** Документированного программного маршрута в снятом наборе нет (`IReportTable`
  виден только как получатель свойства в `IPropertyMng`), поэтому «BOM через отчёт» в план не входит.

## #levels — два уровня BOM

- **(а) BOM силами MCP.** Обход `ksPartCollection` (рекурсия — открытием каждого файла-подсборки:
  у `ksPart` в API5 коллекции компонентов НЕТ, её члены — свойства и тела), фильтры по `hidden`,
  `excluded`, `standardComponent`, группировка по полному пути файла + обозначению + исполнению,
  отдельно число экземпляров и число уникальных деталей. Пишется в ответ и, по выбору, в CSV/JSON.
- **(б) Родная спецификация.** Чтение и запись объектов спецификации в модели (`ksSpecification`)
  и документа-спецификации `.spw` (`ksSpcDocument`). Ничего не считает: показывает то, что занёс
  конструктор, и служит сверкой для (а).

Уровни не смешиваются в одном инструменте: у (а) источник — дерево и свойства, у (б) — объекты
спецификации; расхождение между ними и есть то, что стоит показывать клиенту.

## #profile — профиль `bom-minimal-v1`

См. `BOM_RESEARCH_REPORT_20261026.md`, раздел «Проект минимального профиля».

## #probe — прибор

`scripts/probes/bom/` — отдельный проект, не входит в `KompasMcp.sln` и ничего не берёт из `src/`:
прибор, который ехал бы на поставочном графе, не мог бы свидетельствовать о продукте. Строит
постановку документированными маршрутами (`ksDocument3D.Create`, `IParts7.AddFromFile`,
`ksDocument3D.SaveAs`) и читает её обратно документированными свойствами `ksPart`.

Два запуска: первый строит и читает, второй (`--verify <файл сборки>`) переоткрывает сборку
отдельным процессом. Разделение вынужденное: КОМПАС отказывается открывать путь, который ещё
загружен, а закрытый документ остаётся загруженным, пока жив хоть один указатель на него.
