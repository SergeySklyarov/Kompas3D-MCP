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
| фильтр «скрытый» | `ksPart.hidden`, `SetHidden(bool)` | `kspart_hidden.html` | есть, чтение и запись |
| фильтр «исключён из расчёта» | `ksPart.excluded`, `SetExcluded(bool)` | `kspart_excluded.html` | есть, чтение и запись |
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

Объём профиля и решения по спорным маршрутам — `#profile-scope`; состав инструментов и их проверки —
`BOM_RESEARCH_REPORT_20261026.md`, раздел «Проект минимального профиля».

## #profile-scope — объём профиля (решение техлида, поправка тридцать третья)

- **Профиль.** `bom-minimal-v1` — три инструмента из §4 отчёта (`kompas_get_bom`,
  `kompas_get_component_properties`, `kompas_export_bom`). Чтение родной спецификации (`BOM_SPEC_READ`)
  — отдельным нарядом после.
- **Раздел спецификации — вне профиля** до появления документированного COM-маршрута (вариант «в»
  вопроса 2 отчёта). Отдельная проба точки входа `IPropertyMng` не делается: это KsAPI, искать COM-вход
  — угадывание.
- **Исполнения отложены:** точка входа `IEmbodimentsManager` страницей не подтверждена, в профиль не
  входят.

## #bom-tool — инструменты `kompas_get_bom` и `kompas_export_bom` (наряд `BOM_MINIMAL`)

- **Требование v1 `docs/01_TECHNICAL_SPEC.md` §1.11 (BOM).** Закрыто строками матрицы
  `BOM-01.bom.summary` и `BOM-02.bom.export`: BOM строится из ФАКТИЧЕСКИХ экземпляров сборки,
  фильтры `hidden`/`excluded`/`standard` задаются явно, рекурсивность — явный флаг, группировка идёт
  по нормализованному по регистру ПОЛНОМУ пути плюс `marking` (одинаковое имя разных файлов не
  объединяется), число экземпляров и уникальных файлов выдаётся отдельно. Вне объёма по
  `#profile-scope`: конфигурации/исполнения (`IEmbodiment`) и классификация покупных, изготовляемых
  и справочных позиций метаданными проекта; раздел спецификации — наряд `BOM_SPEC_READ`.
- **Маршрут чтения.** `ksDocument3D.PartCollection(true)` → `ksPartCollection.GetCount/GetByIndex` →
  `ksPart`: `fileName`, `name`, `marking`, `material`, `hidden`, `excluded`, `standardComponent`.
  Материал читается тем же свойством, что и у `kompas_get_material`.
- **Группировка — по ПОЛНОМУ пути файла**, никогда по имени файла: в постановке лежат два разных
  `part.m3d` из разных папок, и группировка по имени склеивает их в одну строку (измерено: 4 вместо
  3 + 1). Ключ `file+designation` — полный путь плюс `marking`.
- **Фильтры — работа инструмента, не КОМПАС.** Исключённый и скрытый экземпляры остаются в коллекции
  и в числе экземпляров, поэтому пропуск делает код и считает его в `skipped`. Инвариант ответа:
  `сумма instances по строкам + сумма skipped = instances_walked`. Флаг, который не прочитан, экземпляр
  не отбрасывает: «не прочитано» — не «исключено».
- **Исключённый экземпляр не виден в API7-перечислении.** `kompas_list_components` (API7
  `IPart7.PartsEx`) даёт 5 записей на 6 экземпляров `ksPartCollection`: исключённый из расчёта
  компонент в это перечисление не попадает, скрытый — попадает. Отсюда правило сверки: BOM со
  скрытыми, но без исключённых сравнивается с `kompas_list_components` один к одному.
- **Рекурсия — открытием файла компонента.** У `ksPart` в API5 коллекции компонентов нет, а
  документированного признака «деталь или подсборка» без открытия тоже нет, поэтому `recurse`
  открывает файл КАЖДОГО компонента: деталь даёт пустой список вложенных. Документ, открытый для
  обхода, закрывается в `finally` через `IKompasDocument.Close(kdDoNotSaveChanges)` и в списке
  документов сеанса не остаётся; подсборка, уже открытая клиентом, не закрывается. Кратность
  вложенных — произведение по пути, флаги `hidden`/`excluded` родителя распространяются на
  содержимое. Цикл ссылок и ненаходимый файл — отказ с путём, а не молчаливый пропуск.
- **Выгрузка — из тех же строк.** Один построитель строк на оба инструмента, поэтому ответ и файл
  разойтись не могут. Формат выбирается расширением (`.csv` или `.json`), CSV — UTF-8 с BOM-меткой и
  разделителем `;`. Путь проверяется существующей политикой путей записи (поле `output_path`, а не
  `file_path` наряда: политика Host судит поля по имени, и новое имя обошло бы её), существующий файл
  перезаписывается. Ответ несёт размер и SHA256 файла, который лежит на диске.

## #component-properties — инструмент `kompas_get_component_properties` (наряд `BOM_PROPERTIES`)

- **Требование v1 `docs/01_TECHNICAL_SPEC.md` §1.11 (BOM)** закрывается третьей строкой матрицы
  `BOM-03.component.properties`: свойства экземпляра читаются по той же ссылке, что выдают
  `kompas_list_components`, а не по имени и не по номеру в чужом перечислении.
- **Адрес — существующий, но найденный сопоставлением.** `component_ref` разрешается тем же реестром
  ссылок и тем же адресом, что у `kompas_delete_component` (`ksDocument3D.PartCollection(true)` →
  `ksPartCollection.GetByIndex`), включая сверку источника: иначе ответ описал бы ЧУЖОЙ компонент
  после сдвига номеров. Сам адрес — индекс в плоском `PartCollection(true)`, найденный сопоставлением
  записи с экземпляром по полному пути файла и размещению (`assembly.md#identity-address`); счётчик по
  `IPart7.PartsEx` сдвигался на исключённом экземпляре. Нового формата ссылки нет.
- **Восемь полей — восемь страниц `IPart`.** `file` — `kspart_filename.html`, `name` —
  `kspart_name.html`, `marking` — `kspart_marking.html`, `material` — `kspart_material.html`,
  `hidden` — `kspart_hidden.html`, `excluded` — `kspart_excluded.html`, `standard` —
  `kspart_standardcomponent.html`, `multi_body` — `kspart_multibodyparts.html` («MultiBodyParts —
  Признак того, что компонент состоит из нескольких частей», тип `BOOL`). Список полей лежит в
  `ComponentPropertyPages` (Domain), и модульный тест сверяет его с контрактом: поле без страницы в
  контракт не входит. Полей «на будущее» (`null` без маршрута) нет.
- **Материал — тем же свойством, что у `kompas_get_material` и `kompas_get_bom`** (`ksPart.material`):
  расхождение ответов по одному экземпляру было бы дефектом, а не разными источниками.
- **Вложенный компонент отвергается.** Адреса API5 у него нет, а восемь полей документированы только
  на `IPart`; поэтому ответ — `CAPABILITY_UNAVAILABLE` с названной причиной, а не частичный набор
  полей с `null`. Это `LIMIT:` в описании инструмента и в матрице.
- **`excluded` у доступной ссылки всегда `false`.** Исключённый из расчёта экземпляр не попадает в
  API7-перечисление `kompas_list_components` (измерено в `BOM_MINIMAL`), значит ссылки на него не
  существует. Поле остаётся в контракте, потому что оно документировано, но обхода этого предела нет.
- **Непрочитанное поле — `null`.** «Не прочитано» не превращается ни в пустую строку, ни в `false`.

## #probe — прибор

`scripts/probes/bom/` — отдельный проект, не входит в `KompasMcp.sln` и ничего не берёт из `src/`:
прибор, который ехал бы на поставочном графе, не мог бы свидетельствовать о продукте. Строит
постановку документированными маршрутами (`ksDocument3D.Create`, `IParts7.AddFromFile`,
`ksDocument3D.SaveAs`) и читает её обратно документированными свойствами `ksPart`.

Два запуска: первый строит и читает, второй (`--verify <файл сборки>`) переоткрывает сборку
отдельным процессом. Разделение вынужденное: КОМПАС отказывается открывать путь, который ещё
загружен, а закрытый документ остаётся загруженным, пока жив хоть один указатель на него.

Флаги фильтров ставятся документированными сеттерами тех же страниц (`iPart.SetHidden`,
`iEntity.SetExcluded`) на два названных экземпляра: флаг, ни разу не виденный истинным, не доказывает,
что фильтр работает. Проверка сравнивает флаги по номерам экземпляров в порядке коллекции — до и после
переоткрытия.

## #spec-read — чтение родной спецификации (наряд `BOM_SPEC_READ`)

- **Уровень «б» отдельным инструментом.** `kompas_get_specification` показывает объекты спецификации
  модели и ничего не считает; `kompas_get_bom` строит BOM по экземплярам. Общего ключа строк нет,
  сверка — по `marking`+`name`+количеству, и расхождение выдаётся как есть.
- **Перечисление — API7.** `IKompasDocument.SpecificationDescriptions.Active` →
  `ISpecificationDescription.Objects` возвращает ВЕСЬ список объектов (VARIANT: один объект
  `VT_DISPATCH`, несколько — `VT_ARRAY|VT_DISPATCH`) в визуальном порядке
  (`ikompasdocument_specificationdescriptions.html`, `ispecificationdescriptions_active.html`,
  `ispecificationdescription_objects.html`). Пустое описание (`Active` = NULL) — пустой список и
  заметка, а не отказ.
- **Тексты и раздел — API5.** `UniqueNumber` объекта → `ksGetSpcObject` →
  `ksGetSpcObjectColumnText(тип колонки, ispoln=1, block=0)` и `ksGetSpcSectionName`
  (`ksspecification_ksgetspcobject.html`, `_ksgetspcobjectcolumntext.html`, `_ksgetspcsectionname.html`).
  Колонка называется ТИПОМ (`ksspecificationcolumntypeenum.html`), а не номером столбца: номер зависит
  от стиля, тип — нет.
- **Указатель сверяется.** Номер, прочитанный обратно `ksGetSpcObjectNumber`, обязан совпасть с
  `UniqueNumber`: иначе строка не публикуется. Без этой сверки чужой объект ответил бы под чужим
  именем, а это неверный ответ, а не пропуск.
- **Номер раздела принадлежит стилю.** Измерено прибором: Документация 5, Комплексы 10, Сборочные
  единицы 15, Детали 20, Программные изделия 23 — это не порядок разделов ГОСТ. Поэтому раздел
  читается (`section` — название, `section_number` — номер), но не выводится один из другого.
- **`.spw` вне объёма.** Единственный документированный вход `KompasObject.SpcDocument`/
  `SpcActiveDocument` адресует АКТИВНЫЙ документ сеанса, а не запрошенный `document_id`, и типа
  документа для `.spw` у инструментов открытия нет (`DocumentKind`). Обход этого ограничения не
  делается: `LIMIT:` в описании инструмента и в матрице.
- **Запись — только постановка.** Прибор `scripts/probes/bom/` создаёт объекты
  `ksSpcObjectCreate`/`ksSetSpcObjectColumnText`/`ksSpcCount`/`ksSpcPosition`/`ksSpcObjectEnd` и
  читает их обратно независимым маршрутом. Инструмент ничего не пишет.
