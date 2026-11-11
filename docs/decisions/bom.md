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
  `file_path` наряда: политика Host судит поля по имени, и новое имя обошло бы её). Ответ несёт размер
  и SHA256 файла, который лежит на диске.
- **Перезапись — только с флагом `overwrite`** (spec §1.12, наряд `BOM_EXPORT_FIXES`, 27.10.2026).
  Необязательное поле `overwrite` (bool, по умолчанию false) того же вида, что у
  `kompas_export_sheet_metal_flat_dxf`. Существующий файл без флага отвергается `PATH_NOT_ALLOWED` тем
  же текстом, что у DXF-инструмента, ДО построения строк и до записи: файл и модель не тронуты.
  `allowOverwrite` у `AtomicPublish.WriteFrom` берётся из команды, а не литералом. До этого правила у
  инструмента не было, и он молча перезаписывал существующий файл — расхождение с §1.12, поймано
  клиентской приёмкой 0.12.0.

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

## #spec-ext — расширение блока «спецификация» (наряд `SPEC_EXT_RESEARCH`, 10.11.2026)

Исследование блока заказчика: создание и правка строк спецификации, чтение `.spw`. Код продукта не
менялся; страницы, маршруты и замеры - `SPEC_EXT_RESEARCH_REPORT_20261110.md`. Доказательства -
`docs/acceptance/evidence/20261110-spec-ext-7d463a0/`, прибор - `scripts/probes/spec/`.

- **Документная сторона снята отдельно.** К наборам `#routes` (API7 «Работа со спецификацией» и API5
  `ksSpecification`/`ksSpcDocument`) добавлен `docs-wire-20261110-spec/`: `IApplication.Documents` →
  `IDocuments` (`Add`, `Open`, `OpenDocument`, `Item`), `IOpenDocumentParam`, `IKompasDocument`,
  `ISpecificationDocument`, `DocumentTypeEnum`, `ksIsSlaveSpcOpened`. Сценарий -
  `_fetch_docs_wire_20261110_spec.py`.
- **`.spw` читается по документной ссылке - предел `#spec-read` снят не обходом, а документом.**
  `IDocuments.Open(PathName, Visible, ReadOnly)` и `IDocuments.Add(ksDocumentSpecification, Visible)`
  дают `IKompasDocument` (`idocuments_open.html`, `idocuments_add.html`, `ikompasdocument.html`:
  «может быть получен … от IDocuments::Item и методов IDocuments::Add, IDocuments::Open»), а
  `SpecificationDescriptions` объявлено свойством БАЗОВОГО `IKompasDocument`
  (`ikompasdocument_specificationdescriptions.html`), не `IKompasDocument3D`. Пункт «`.spw` вне
  объёма» из `#spec-read` относится к `KompasObject.SpcDocument`/`DocumentKind`; новый маршрут его
  не повторяет и прежний `LIMIT:` заменяет на «читается, если документ открыт через `IDocuments`».
- **API5-дорога к `.spw` остаётся активной-документной.** `SpcActiveDocument` при неактивном
  документе-спецификации даёт `NULL` (`kompasobject_spcactivedocument.html`); после
  `IApplication.ActiveDocument = <spw>` измерено чтение через `GetSpecification` +
  `ksGetSpcObjectColumnText`. Поэтому профиль опирается на API7, а API5-чтение - независимая сверка
  при активном документе.
- **Создание строки модели - API7 и API5; сохраняется оба.** API7:
  `BaseObjects.Add(section, attrNumb)` + колонки + `Update()` (`ispecificationbaseobjects_add.html`:
  «объект будет добавлен в коллекцию, но в документе не появится»; `Update` создаёт объект).
  API5: `ksSpcObjectCreate…ksSpcObjectEnd` (прежний прибор `#probe`). Замер: строки переживают
  сохранение и переоткрытие, `ksGetSpcObjectNumber` совпадает с `UniqueNumber`.
- **Правка существующей строки модели сохраняется ТОЛЬКО через API5.** `ksSpcObjectEdit` +
  `ksSetSpcObjectColumnText`/`ksSpcCount` + `ksSpcObjectEnd`. Две документированные записи API7
  (`ISpecificationColumn.Text.Str`, `ISpecificationColumnItems.Item.Value` с `Update()`) отвечают
  `Update() = false`, меняют текст в сессии и НЕ попадают в файл. Причина не установлена; правило для
  инструментов: успех правки подтверждается перечитыванием, а не возвратом `Update`.
- **Раздел строки не переносится.** `Section` - только чтение, «переместить уже созданные объекты из
  одного раздела в другой нельзя» (`ispecificationobject_section.html`); `SetSection` возвращает
  `TRUE` и меняет раздел в сессии, но после переоткрытия раздел прежний. `LIMIT:` - в контракте.
- **Удаление строки - только API7.** `ISpecificationObject.Delete()` сохраняется; `ksDeleteSpcObject`
  в оглавлении v24 отсутствует, `ksSpcDocument.ksDeleteObj` удаляет объект документа-спецификации, не
  строку модели. В «не входит»: удаление строки модели через API5.
- **Связь с компонентом документирована, но не подтверждена постановкой.**
  `ISpecificationBaseObjects.AddWithParam(…, IPropertyKeeper, …)` связывает строку и объект общим
  ключом информационного объекта; точки входа в `IPropertyKeeper` в COM нет (в зеркале - KsAPI,
  `#not-included`). В профиль не входит, вопрос заказчику - в отчёте.
- **Связь модели и `.spw`.** `ISpecificationDescription.SpecificationDocumentName` - полное имя
  подключённого `.spw` у чертежа/сборки; `ISpecificationBaseObject.Documents` - документы, чья
  геометрия подключена к строке, «только для объектов в документе спецификации»
  (`ispecificationbaseobject_documents.html`); `ksSpcDocument.ksGetSpcSheetSB` - листы сборочного
  чертежа. Замер: у строк модели и у строки `.spw` без геометрии `Documents = null`, у модели без
  подключённого `.spw` имя пустое.
- **Синхронизация - отдельными свойствами, не побочным эффектом.** `IDocuments.DocumentSynchronize`,
  `ISpecificationDescription.DelegateMode` и режимы 3/4 `ksOpenDocument` («без синхронизации со
  сборочным чертежом») документированы; в профиль входят только как прочитанные значения при
  открытии, без собственного режима.
- **Профиль - предложением:** `spec-ext-v1`, четыре инструмента (`kompas_get_specification_document`,
  `kompas_specification_add_row`/`edit_row`/`delete_row`), контракт аддитивный → MINOR. Порядок
  нарядов - раздел 6 отчёта.

## #spec-read-spw — документ-спецификация `.spw` читается по документной ссылке (наряд `SPEC_READ_SPW`, 11.11.2026)

Первый наряд блока `spec-ext-v1`: вид документа «спецификация» в реестре и `kompas_get_specification_document`.
Предел `#spec-read` («`.spw` вне объёма») снят не обходом, а документом: `IDocuments.Open` отдаёт
`IKompasDocument`, а `SpecificationDescriptions` объявлено свойством БАЗОВОГО `IKompasDocument`, а не
`IKompasDocument3D` (`idocuments_open.html`, `ikompasdocument.html`,
`ikompasdocument_specificationdescriptions.html`).

- **Ответ называет `document.document_id` (наряд `CLIENT_FINDINGS_0_14_0`, 11.11.2026).** ИЗМЕРЕНО
  клиентской приёмкой 0.14.0: ответ по `path` называл `path`, `kind` и `opened_by_tool`, но не id, и
  закрыть открытый инструментом `.spw` можно было только перебором `kompas_list_documents`. Поле
  аддитивное (MINOR): тем же id документ закрывает `kompas_close_document`, и конверт `document_id`
  тоже его называет - id берётся из ответа, а не угадывается. Строка `SPEC-01.close_by_answer`
  проверяет и поле, и закрытие по нему (закрытие подтверждается списком сеанса, а не кодом ответа).
- **Открытие - документированным `IDocuments.Open(PathName, Visible, ReadOnly)`.** `ReadOnly` = TRUE:
  чтение не пишет файл. Тип подтверждается ответом документа (`DocumentType` →
  `ksDocumentSpecification` = 3, `documenttypeenum.html`), а не расширением: файл другого типа
  закрывается обратно и отвергается `CAPABILITY_UNAVAILABLE` с типом. Значение 3 измерено на
  постановке прибора исследования. Открытие НЕ активирует документ:
  ответ несёт `document.opened_by_tool`, закрытие остаётся за клиентом, как у прочих открытий.
- **Один вход, не два.** Ровно одно из `path` и `document_id`; правило живёт чистым кодом
  (`SpecificationDocumentInput.Parse`), применяется Хостом до журнала и до COM и повторяется Worker-ом.
  Открытие по `path` без `application_id` возможно только при ОДНОМ подключённом экземпляре; при
  нескольких - `AMBIGUOUS_APPLICATION` с выходом «kompas_open_document с application_id, затем чтение
  по document_id».
- **Строки читаются API7, `section` берётся из стиля.** `SpecificationDescriptions.Active` →
  `Objects` → `ISpecificationObject.Columns` → `Column[тип, 1, 0]` → `ColumnItems.Item(i).Value` по
  ТИПУ колонки (`ispecificationobject_columns.html`, `ispecificationcolumns_column.html`,
  `ispecificationcolumnitems_item.html`). Название раздела - `SpecificationStyle.Sections` по номеру
  (`ispecificationstyle_sections.html`, `ispecificationsectionstyle_number.html`,
  `ispecificationsectionstyle_name.html`): api5-дорога требует активного документа, а стиль - нет.
  Колонки, которой нет в стиле, соответствует `null` и заметка `specification_column_absent`; «нет
  колонки» и «колонка пуста» - разные ответы.
- **Объекты без колонок - документированный тип, а не `__ComObject`.** Список описания содержит не
  только базовые объекты: имя раздела, комментарий, начало блока, резервную и пустую строку,
  объект-заготовку (`ksspecificationobjecttypeenum.html`, `ispecificationobject_objecttype.html`).
  Прежняя заметка называла тип .NET-обёртки и скрывала, что именно пропущено; теперь называется
  документированный тип, и это общий код обоих инструментов. Измерено на постановке
  `spec-asm.m3d`: 3 объекта из 7 - «имя раздела (3)», «пустая строка (6)» и «начало блока (4)», при
  4 базовых строках.
- **Вторая дорога - только у активного документа, и это названо.** API5
  `SpcActiveDocument().GetSpecification()` возвращает NULL, если документ не активен
  (`kompasobject_spcactivedocument.html`); чтение его не активирует. Когда дорога доступна, значения
  сверяются (`ksGetSpcObject` + `ksGetSpcObjectNumber` = сверка указателя, `ksGetSpcSectionName`,
  `ksGetSpcObjectColumnText`), и расхождение идёт в `notes`; когда недоступна - в `unverified_aspects`
  приходит `api5_road_unavailable` с причиной. Молчания «дорога одна» нет.
- **Сохранность `.spw` считается по его строкам.** У документа-спецификации нет `ksDocument3D`, поэтому
  отпечаток (`ComputeFingerprint`) строится по числу объектов и текстам их колонок. Измерено: открытый
  по `path` `.spw` показывается в `kompas_list_documents` и `kompas_get_context` как `dirty=false`, и
  `kompas_close_document` с `dirty_policy=refuse` закрывает его; `DOCUMENT_DIRTY` здесь была бы ложью.
  Сохранение идёт документированным `IKompasDocument.Save/SaveAs` и подтверждается файлом, как у
  чертежа; сохранение «на место» у открытого только для чтения документа по-прежнему отвергается.
- **Пределы наряда.** Запись строк `.spw` (создание, правка, удаление) не входит - это наряды
  `SPEC_ADD_ROW`/`SPEC_EDIT_ROW`/`SPEC_DELETE_ROW`. Создание `.spw` инструментом не вводится:
  постановка приёмки берётся копией входа исследования. Синхронизация `.spw` со сборкой и листы
  сборочного чертежа не входят.

## #spec-add-row — создание строки спецификации модели (наряд `SPEC_ADD_ROW`, 11.11.2026)

Второй наряд блока `spec-ext-v1`: `kompas_specification_add_row` создаёт строку МОДЕЛИ (деталь или
сборка). Запись строк `.spw` остаётся вне блока: у документа-спецификации свои строки и свой инструмент
чтения, и инструмент модели их не пишет (`WRONG_DOCUMENT_KIND`).

- **Создание - два шага, и успех мерится ВТОРЫМ.** `ISpecificationDescription.BaseObjects.Add(section, 0)`
  кладёт объект в КОЛЛЕКЦИЮ, а в документе он появляется только после `ISpecificationObject.Update()`
  (`ispecificationbaseobjects_add.html`: «объект будет добавлен в коллекцию, но в документе не появится»;
  `ispecificationobject_update.html`). Поэтому `Update() = false` - отказ `CAPABILITY_UNAVAILABLE` со
  снимком, а не успех, и строка, попавшая в коллекцию, удаляется обратно (`Delete()`); ответ называет,
  удалось ли это (`collection_object_deleted`), а неудалённый объект делает отказ `partial_effects`.
- **Строка публикуется только после ПЕРЕЧИТЫВАНИЯ обеими дорогами.** API7: описание документа заново →
  `Objects` → объект с тем же `UniqueNumber` → `Columns` → `ColumnItems`; API5:
  `ksDocument3D.GetSpecification()` → `ksGetSpcObject(unique)` → `ksGetSpcObjectNumber` (сверка
  указателя) → `ksGetSpcObjectColumnText` + `ksGetSpcSectionName`. Расхождение любого поля с запрошенным
  - `GEOMETRY_FAILED` со снимком (`details.failure_snapshot`: запрос, значения обеих дорог, номера);
  ложного успеха нет.
- **Строка опознаётся как НОВЫЙ объект списка, а не по номеру созданной ручки.** Измерено: `UniqueNumber`
  объекта, который вернул `BaseObjects.Add` (и у которого прочитан уже ПОСЛЕ `Update()`), - номер
  коллекции, а не строки; документ даёт строке другой номер, и поиск по номеру ручки строку не находит,
  тогда как читатель того же документа показывает её сразу. Поэтому проверка берёт описание у документа
  ЗАНОВО, считает номера объектов до создания и объявляет строкой тот единственный объект, которого до
  вызова не было; номер строки берётся у документа (и подтверждается `ksGetSpcObjectNumber`). Запасной
  путь - совпадение записанных значений, и он принимается только при ОДНОМ кандидате: при двух
  одинаковых строках ответ - отказ со списком того, что документ держит, а не выбор. Тот же класс
  ошибки стоил прошлому наряду «трёх не-базовых объектов»: имя .NET-обёртки (`__ComObject`)
  называлось вместо документированного типа (`ksSpecificationObjectTypeEnum`), и история этого разбора
  остаётся здесь, а в коде - один инвариант.
- **У документа нет описания - оно создаётся и это НАЗВАНО.** `ISpecificationDescriptions.Active = NULL`
  у модели - законное состояние (`ispecificationdescriptions_add.html`: «Если LayoutName и StyleID не
  заданы, то добавляется описание спецификации по умолчанию»), поэтому `Add("", 0, "")` вызывается, а
  ответ несёт `specification_description_created`. Отказ вместо создания сделал бы инструмент
  непригодным для свежей детали; молчаливое создание было бы скрытой правкой модели. Измерено на детали,
  созданной `kompas_create_document`: описание создано, строка прочитана обратно обеими дорогами.
- **Раздел проверяется по стилю ДО `Add`.** Номер раздела принадлежит стилю (`ISpecificationStyle.Sections`
  → `Number`/`Name`), а не порядку ГОСТ. Неизвестный номер - `CAPABILITY_UNAVAILABLE` с перечнем
  доступных разделов в причине (измерено: 5 «Документация», 10 «Комплексы», 15 «Сборочные единицы»,
  20 «Детали», 23 «Программные изделия» у постановки). Ноль отвергается `INVALID_ARGUMENT` дословной
  формулировкой справки: «Объект c номером раздела 0.0. добавить нельзя».
- **Колонки проверяются на самом объекте, а не на стиле.** Наличие колонки нужно знать ДО `Update`, и
  спрашивать его надёжнее у объекта (`ISpecificationObject.Columns` → `Column[тип, 1, 0]`), чем у
  раздела стиля, где настройки могут быть частичными. Значение пишется по `ValueType` элемента
  (`ksValueTypeInteger` - VT_I4, `ksValueTypeFloat` - VT_R8, `ksValueTypeString` - VT_BSTR); текст,
  который элемент не принимает, - `INVALID_ARGUMENT`, объект удаляется обратно. Позиция пишется только
  если задана: без неё нумерацию ставит документ. Измерено: строка с позицией 5 читается обратно с
  позицией 5, количество «4» - как «4».
- **Сравнение значений типоспецифично.** Число сравнивается как число («03» и «3» - одно количество),
  текст - как текст («007» и «7» - разные обозначения). Иначе канонический вид, в который документ
  приводит число, отвергал бы законный вызов.
- **Номер строки подтверждается, а не берётся на веру.** `UniqueNumber` читается у созданного объекта и
  сверяется с ответом `ksGetSpcObjectNumber` по указателю; только после этого строка публикуется с
  `row_ref {document_id, unique_number, marking}`. Уровень проверки - `structure_checked`, когда прошли
  все проверки перечитывания.
- **Пределы.** Строка живёт в модели до `kompas_save_document`; переживание сохранения и переоткрытия
  проверяет приёмка (`SPEC-02.save_reopen`), и это названо в `unverified_aspects` самого ответа. Связь
  строки с компонентом (`ISpecificationBaseObjects.AddWithParam` + `IPropertyKeeper`) не создаётся:
  точки входа в COM нет, постановка не измерена - граница документации. Перенос раздела (`SetSection`
  не сохраняется), правка и удаление строк - другие наряды. Удаление объекта, который ещё не создан в
  документе (ветка «нет колонки»), этим нарядом НЕ измерялось: отказ называет результат `Delete()`,
  каким он был.
- **Поля, которые не включены.** `subsection_number`, `additional_section`, `block` из отчёта
  исследования в контракт не вошли: маршрут (`AddWithParam`/`ISpecificationObjectCreateParam`)
  документирован, но постановкой не проверен, и включать его по одному лишь наличию в справке нельзя.

## #spec-edit-row — правка строки спецификации модели (наряд `SPEC_EDIT_ROW`, 11.11.2026)

Третий наряд блока `spec-ext-v1`: `kompas_specification_edit_row` правит существующую строку МОДЕЛИ.
Маршрут - API5 `ksSpcObjectEdit`...`ksSpcObjectEnd`, тот самый, что в исследовании оказался единственным
сохраняющимся; API7-запись не повторена и здесь.

- **Ссылка на строку, и почему чтение публикует её номер.** Строка адресуется `row_ref
  {document_id, unique_number, marking}`, как его отдаёт создание. Для строки, созданной не этим
  сеансом, брать ссылку было не из чего: обозначение не уникально, других адресов у строки нет. Поэтому
  строки ЧТЕНИЯ (`kompas_get_specification`, `kompas_get_specification_document`) публикуют
  `unique_number` - номер, который документ присвоил строке («присваивается при создании»,
  `ispecificationobject_uniquenumber.html`). Правка адресуется только им и маркировкой; поиск строки
  по обозначению не делается.
- **Маршрут и его члены.** `ksDocument3D.GetSpecification()` → `ksGetSpcObject(unique_number)` →
  `ksGetSpcObjectNumber` (сверка указателя) → `ksSpcObjectEdit` → `ksSetSpcObjectColumnText(тип колонки,
  1, 0)` для обозначения и наименования, `ksSpcCount(1, текст)` для количества, `ksSpcPosition` для
  позиции → `ksSpcObjectEnd`. Справка: «После вызова данного метода у объекта можно изменить атрибуты,
  геометрию, линии выноски, позицию, массу» и «Редактирование объекта завершится вызовом метода
  ksSpecification::ksSpcObjectEnd» (`ksspecification_ksspcobjectedit.html`).
- **`ksSpcObjectEdit = 0` - отказ ДО мутации.** Документ строку на редактирование не открыл, поэтому
  ответ `CAPABILITY_UNAVAILABLE` без частичных эффектов. Отказ сеттера или `ksSpcObjectEnd` посреди
  правки - тот же код, но с частичными эффектами: строка перечитывается, и ответ называет, что в ней
  уже изменилось, со снимком.
- **`ksSpcObjectEnd` вызывается ВСЕГДА, и его возврат - не приговор.** Измерено (и подтверждено прежним
  прибором): функция отвечает указателем, а не `1`, поэтому успех по ней не судится. Вызывается она и
  после отказа сеттера: объект открыт на редактирование, и оставить его открытым значило бы скрыть
  эффект.
- **Успех - перечитывание, и не только запрошенных полей.** После `ksSpcObjectEnd` описание берётся у
  документа заново, строка находится по номеру, указатель подтверждается, и обе дороги (API7 и API5)
  сверяются с запросом. Поля, которых вызов НЕ называл, обязаны совпасть с прежними: изменение
  неназванного поля - `GEOMETRY_FAILED` со снимком, а не «правка удалась». Проверки ответа называют и
  `section_unchanged`/`section_number_unchanged`: перенос раздела этой правкой не делается.
- **Ссылка сверяется ДО правки, тремя признаками.** Строка должна быть в списке описания (ровно одна с
  таким номером), указатель API5 должен ответить тем же номером, а обозначение строки - совпасть с
  `row_ref.marking`. Любое расхождение - `STALE_REFERENCE` без частичных эффектов: так отвергается и
  ссылка, выданная на прежнее состояние строки после правки обозначения.
- **Колонки и тип значения проверяются до `ksSpcObjectEdit`.** Колонка нужного ТИПА ищется на самом
  объекте (`ispecificationcolumns_column.html`), а значение количества проверяется по `ValueType`
  колонки: на постановке приёмки колонка количества числовая, и «не число» отвергается
  `INVALID_ARGUMENT` до мутации (измерено приёмкой, строка `SPEC-03.negative_value_type`).
- **Происхождение строки: документ называет только то, что называет.** Строки, которые КОМПАС ведёт от
  компонентов, правятся как обычные: документированного признака связи с КОМПОНЕНТОМ в COM нет (точки
  входа в `IPropertyKeeper` нет, решение наряда `SPEC_EXT_RESEARCH`). Читаются и попадают в `notes` два
  документированных члена, которые о происхождении говорят: `ISpecificationObject.State`
  (`ispecificationobject_state.html`: создан в данном документе / пришёл из вставки / из вставки, но
  редактировался здесь) и `ISpecificationBaseObject.SyncronizeWithProperties`
  (`ispecificationbaseobject_syncronizewithproperties.html`: «Брать данные из свойств и передавать данные
  в свойства объекта»). Ни один не называет компонент, и ответ этого не утверждает. Значение состояния,
  которое объявлено в interop, но не описано справкой, называется номером, а не выдуманным смыслом.
- **Поля, которые не включены.** `subsection_number`, `additional_section`, `block` не вошли: их
  документированный маршрут (`AddWithParam`/`ISpecificationObjectCreateParam`) описан для СОЗДАНИЯ, а
  перенос раздела у существующей строки справка запрещает (`ispecificationobject_section.html`) и
  `SetSection` не сохраняется (измерено). Масса (`ksSpcMassa`) и составные значения
  (`ksSpcChangeValue`) не измерены. Удаление строки - наряд `SPEC_DELETE_ROW`.
- **Пределы ответа.** Правка живёт в модели до `kompas_save_document`; её переживание сохранением и
  переоткрытием проверяет приёмка (`SPEC-03.save_reopen`), и это названо в `unverified_aspects` самого
  ответа. Ревизия следующего вызова приходит ревизией конверта, как у всякой мутации, а не отдельным
  полем результата.

## #spec-delete-row — удаление строки спецификации модели (наряд `SPEC_DELETE_ROW`, 11.11.2026)

Четвёртый, последний наряд блока `spec-ext-v1`: `kompas_specification_delete_row` удаляет строку МОДЕЛИ
документированным API7 `ISpecificationObject.Delete()`. Вход - только та ссылка, которую выдали
создание, правка и чтение: удаление значений не меняет, и других полей у него нет.

- **Маршрут - API7, и это единственный документированный.** `ispecificationobject_delete.html`:
  «Метод позволяет удалить базовый или вспомогательный объект спецификации из документа и из
  коллекции», то есть базовая строка удаляется тем же членом, что и вспомогательный объект. API5-дороги
  нет: страницы `ksDeleteSpcObject` в оглавлении v24 не существует, а `ksSpcDocument.ksDeleteObj`
  удаляет объект ДОКУМЕНТА-спецификации `.spw`. Замер исследования (раздел 4, факт 4) подтвердил, что
  удаление сохраняется: после переоткрытия строки нет, остальные на месте.
- **Ссылка сверяется до вызова, тремя признаками.** Строка должна быть в списке описания ровно одна с
  таким номером, указатель API5 должен ответить тем же номером, а обозначение строки - совпасть с
  `row_ref.marking`. Любое расхождение - `STALE_REFERENCE` без частичных эффектов: так отвергается и
  ссылка на уже удалённую строку, и ссылка, выданная на прежнее состояние строки после правки
  обозначения.
- **Значения строки читаются обеими дорогами ДО удаления.** После `Delete()` строку не прочитать
  вовсе, поэтому расхождение дорог видно только до неё: проверки ответа `marking_read_back` и т. д.
  сравнивают API7 и API5 значения на живой строке, а `row` ответа - это она же, какой её увидели.
- **Успех - отсутствие, прочитанное обеими дорогами, а не возврат `Delete()`.** После вызова список
  перечитывается: API7 `Objects` (строки с этим номером нет), API5 `ksGetSpcObject` по удалённому
  номеру (указателя нет), строк ровно на одну меньше, прочие строки на месте по номеру и значениям,
  новых строк нет. `Delete() = false` или исключение - отказ `CAPABILITY_UNAVAILABLE` со снимком;
  `true` при оставшейся строке или исчезнувшая не та строка - `GEOMETRY_FAILED` со снимком. Ложного
  успеха нет ни в одной ветке.
- **Соседние строки сверяются по номеру И значениям.** Перепись берётся одним и тем же извлекателем до
  и после вызова, поэтому разница двух списков - это удаление и ничего больше. Изменение или пропажа
  чужой строки, как и появление новой, названы в отказе по номерам, а не спрятаны в «строк стало
  меньше».
- **Происхождение строки: читается то, что документ говорит, и ничего не додумывается.** `State`
  (`ispecificationobject_state.html`: создан в данном документе / пришёл из вставки / из вставки, но
  редактировался здесь) и `SyncronizeWithProperties`
  (`ispecificationbaseobject_syncronizewithproperties.html`) читаются у объекта ДО удаления и попадают
  в `notes`. Если документ не называет строку своей либо синхронизация включена, ответ добавляет
  предупреждение `specification_row_may_return`: КОМПАС может восстановить строку от компонента при
  перестроении или сохранении. Сама строка при этом удаляется - запрета на такое удаление справка не
  даёт, а документированного признака связи с КОМПОНЕНТОМ в COM нет (точки входа в `IPropertyKeeper`
  нет, решение наряда `SPEC_EXT_RESEARCH`), поэтому он и не ищется.
- **Восстанавливается ли строка от компонента - не измерено, и вот почему.** Постановка приёмки
  (`spec-asm.m3d` входа исследования) содержит строки, созданные `BaseObjects.Add`, - все они
  `ssIndependent`. Строку, которую КОМПАС ведёт от компонента, документированным маршрутом не создать:
  `AddWithParam` требует `IPropertyKeeper`, у которого в COM нет точки входа, а «заполнить
  спецификацию из состава» программного маршрута в профиле нет. Поэтому измерено только то, что
  измеримо: состояние строки читается и называется, а восстановление остаётся названным риском, а не
  проверенным фактом. Это `LIMIT:`, а не долг - недокументированный маршрут не подбирается.
- **Измерено приёмкой: `SyncronizeWithProperties` включён у строки, созданной `BaseObjects.Add`.** Её
  `State` при этом `ssIndependent` - документ называет строку своей, но синхронизация со свойствами у
  неё включена. Поэтому предупреждение `specification_row_may_return` выдаётся и на такой строке, и его
  текст говорит ровно то, что следует из двух прочитанных членов: данные могут быть перезаполнены из
  свойств, а строка, которую документ ведёт от компонента, - восстановлена; связи с компонентом у
  конкретной строки инструмент не утверждает.
- **Отказы до мутации и после.** `INVALID_ARGUMENT` (нет `row_ref`, чужой `document_id`, не задано
  обозначение, номер не положителен) - до журнала и до COM; `DOCUMENT_NOT_FOUND`,
  `WRONG_DOCUMENT_KIND` (не модель), `STALE_REFERENCE` - до вызова; `REVISION_CONFLICT` - до COM;
  `CAPABILITY_UNAVAILABLE`/`GEOMETRY_FAILED` - после вызова, со снимком того, что документ держит.
- **Пределы.** Перенумерация позиций после удаления не делается: позиции остальных строк остаются
  теми, что записал документ. Удаление объектов `.spw` (`ksSpcDocument.ksDeleteObj`), запись строк
  `.spw` и удаление описания спецификации (`ISpecificationDescription.Delete()`) не входят. Удаление
  живёт в модели до `kompas_save_document`; его переживание сохранением и переоткрытием проверяет
  приёмка (`SPEC-04.save_reopen`), и это названо в `unverified_aspects` самого ответа.




