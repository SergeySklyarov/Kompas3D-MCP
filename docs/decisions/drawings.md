# Чертёжный блок DRW - решения и измерения

Модуль: `src/KompasMcp.Api5Adapter/Api5Session.Drawings.cs`, `src/KompasMcp.Contracts/DrawingCommands.cs`.
Здесь - история правок, вынесенная из кода. Действующие правила остались в коде под метками
`INVARIANT:` / `DOC:` / `MEASURED:` / `ASSUMPTION:` / `LIMIT:` / `TEST:`. Ссылки вида
`History: docs/decisions/drawings.md#<якорь>` ведут на разделы ниже.

## <a id="routes"></a>Шаг 0 - документированные маршруты (справка v24)

**Что было.** Блок DRW требует построения видов, размеров, основной надписи и выгрузки. Каждый пункт
должен опираться на конкретную страницу официальной справки, а не на наличие имени в TLB.

**Что измерено.** Проверенные страницы справки `help.ascon.ru/KOMPAS_SDK/24/ru-RU/` (все отдали 200;
несуществующий адрес - контроль 404):

- `iviews_addstandartviews.html` - `AddStandartViews(FileName, ProjectionName, ProjectionsTypes, X, Y,
  Scale, DX, DY)`; возвращает `bool`.
- `projectiontype.html` - `ProjectionType`: `vp_Front=1, vp_Rear=2, vp_Up=3, vp_Down=4, vp_Left=5,
  vp_Right=6, vp_IsoXYZ=7, vp_IsoYZX=8, vp_IsoZXY=9, vp_Dio=10`.
- `ltviewtype.html` - `LtViewType`: `vt_System=0, vt_Normal=1, vt_Standart=3, …`.
- `iassociationview_props.html`, `iassociationview_projectionname.html` - `ProjectionName` - имя из
  списка проекций документа-источника; `SourceFileName`, `HiddenLinesVisible`, `CenterLinesVisible`,
  `Scale`, `X`, `Y`.
- `idrawingdocument.html`, `iviewsandlayersmanager.html` - путь к коллекции видов:
  `IDrawingDocument.ViewsAndLayersManager` → `IViewsAndLayersManager.Views`.
- `isymbols2dcontainer.html` - контейнер размеров получается из `IView`; коллекции `LineDimensions`,
  `RadialDimensions`, `DiametralDimensions`, `AngleDimensions` и др.
- `ilinedimensions_add.html` / `iradialdimensions_add.html` / `idiametraldimensions_add.html` -
  `Add()` без аргументов возвращает объект размера.
- `ilinedimension_props.html` - `X1,Y1,X2,Y2` (точки привязки), `X3,Y3` (размерная линия),
  `Orientation`, `Angle`, `Valid`.
- `iradialdimension_props.html`, `idiametraldimensions.html` - `Radius`, `Xc`, `Yc`,
  `DimensionType`.
- `ilayoutsheet_stamp.html`, `istamp_text.html`, `istamp_update.html` - `ILayoutSheet.Stamp` → `IStamp`;
  `Text(Id)` только ЧТЕНИЕ и возвращает `IText`; `GetNextColumnId(Id)`, `Clear(Id)`, `Update()`.
- `itext_str.html` - `Str` доступно для чтения и записи; при замене текст пересоздаётся.
- `itechnicaldemand*.html` - `IDrawingDocument.TechnicalDemand`; `Text` (чтение → `IText`), `Update()`,
  `Synchronize()`, `IsCreated`, `AutoPlacement`, `Delete()`. Маршрут ДОКУМЕНТИРОВАН.
- `iapplication_converter.html`, `iconverter_getfilter.html`, `iconverter_convert.html` -
  `IApplication.Converter` → `IConverter.Convert(InputFile, Outfile, Command, ShowParam)`;
  `GetFilter(DocType, SaveAs, out Command)`; `FORMAT_DXF=1`, `FORMAT_DWG=2`.
- `ksdocument2d_kscreatedocument.html`, `ksdocumentparam.html`, `ksdocumentparam_type.html`,
  `ksdocumentparam_regime.html`, `doctype.html` - создание чертежа: `ksDocument2D.ksCreateDocument(par)`,
  где `par` - `ksDocumentParam`; `type` - из `DocType` (`lt_DocSheetStandart=1`), `regime` - 0/1.

**Что решено.** Реализованы пункты 1-5. Пункт 6 (технические требования, `ITechnicalDemand`) имеет
документированный маршрут, но в опубликованный набор инструментов в этой итерации не выведен -
маршрут записан здесь как известный, инструмент добавляется отдельным заданием (за рамками объёма
наряда, не технический долг).

**ПЕРВЫЙ ОТЧЁТ ОШИБСЯ ЗДЕСЬ, И ЭТО ИСПРАВЛЕНО (второй заход).** Пункт 6 наряда сформулирован
условно: «Техтребования (`ITechnicalDemand`) - только если шаг 0 дал маршрут; иначе снять». Шаг 0
маршрут ДАЛ (см. `itechnicaldemand_text`, `itechnicaldemand_update`, `idrawingdocument_technicaldemand`),
значит условие выполнено и пункт обязателен. Формулировка «за рамками объёма наряда» была неверной:
документированная обязательная функция не может быть названа работой вне наряда без решения
заказчика, а такого решения в проверенных материалах нет. Пункт реализован инструментом
`kompas_set_technical_demand` (см. раздел ниже). Условие «иначе снять» НЕ сработало.

**Что НЕ документировано и потому не реализовано.** PDF как программный маршрут экспорта: справка
называет только DXF/DWG, поэтому `kompas_export_drawing` отвергает PDF кодом `FORMAT_UNAVAILABLE` по
имени. Числовые идентификаторы ячеек основной надписи: справка описывает `IStamp.Text(Id)`, но
таблицы «понятие → номер ячейки» в ней нет - номера передаёт вызывающий, и это названо в описании
инструмента и в `unverified_aspects`.

## <a id="contracts"></a>Контракты DRW

**Что решено.** Пять команд - `CreateDrawingViewsCommand`, `ListDrawingViewsCommand`,
`AddDimensionCommand`, `SetTitleBlockCommand`, `ExportDrawingCommand` - с результатами
`DrawingViewRowDto`, `DimensionRowDto`, `TitleBlockCellDto`. Каждая запись несёт маршрут и границы в
XML-doc. Тип документа, не являющегося чертежом, отвергается кодом `DOCUMENT_KIND_MISMATCH` до любого
COM-вызова, а не COM-исключением.

**Добавлены во втором заходе.** `EditViewCommand`/`EditViewResult` (изменение вида) и
`SetTechnicalDemandCommand`/`SetTechnicalDemandResult` (пункт 6 наряда).

## <a id="create-drawing"></a>Создание чертежа (расширение документа)

**Что было.** `KompasObject.Document3D().Create()` создаёт только деталь или сборку;
`kompas_create_document(kind=drawing)` отвергал запрос.

**Что измерено / решено.** Документированный маршрут API5 2D: `KompasObject.GetParamStruct(35)`
(`StructType2DEnum.ko_DocumentParam = 35`, прочитано из `Interop.Kompas6Constants.dll`) →
`ksDocumentParam` → `Init()`, `type = lt_DocSheetStandart (1)`, `regime = 0|1` (видимый/«слепой») →
`KompasObject.Document2D()` → `ksDocument2D.ksCreateDocument(param)`. Открытие - `ksOpenDocument(path,
!visible)`; закрытие - `ksCloseDocument()`; сохранение - `ksSaveDocument(path)`.

**Следствие для модели кода.** `DocumentEntry.Document`/`Part` стали nullable (у чертежа их нет), у
чертежа непустой `Drawing` (`ksDocument2D`). Чтобы это не превратилось в разыменование null на
3D-маршрутах, добавлено свойство `Document3D`, которое даёт именованный отказ
`DOCUMENT_KIND_MISMATCH`, а не падение; все прежние обращения `document.Document.` на 3D-маршрутах
переведены на `document.Document3D`.

## <a id="open-drawing"></a>Открытие чертежа

**Что решено.** Расширение `.cdw` только МАРШРУТИЗИРУЕТ открытие на 2D-путь; вид документа
подтверждается самим документом после открытия, поэтому файл с неверным именем даёт названный отказ,
а не молчаливо открытый не тот документ.

## <a id="adapter"></a>Адаптер DRW

**Что решено.** Операции живут в `Api5Session.Drawings.cs`. Каждый ответ строится ПЕРЕЧТЕНИЕМ из
документа: виды - ряды из `IViews`, размер - из объекта размера (`Valid`, координаты, радиус),
ячейки надписи - из `IStamp.Text(Id).Str` после `Update()`. Признак успеха вызова (`bool`) сам по себе
не является доказательством и в уровень проверки не превращается.

## <a id="create-views"></a>Создание стандартных видов

**Что измерено.** `AddStandartViews` возвращает `bool` «вызов принят»; число СОЗДАННЫХ видов
получается сравнением коллекции ДО и ПОСЛЕ по `IView.Reference`. Источник - файл модели на диске:
несохранённая модель даёт `DOCUMENT_NOT_FOUND` до COM-вызова, потому что стандартный вид есть проекция
ФАЙЛА.

**Ограничение.** Габарит вида не читается: `IView` его не публикует, справка маршрута не даёт. Поле
остаётся null, и это названо в `unverified_aspects` / `notes`, а не заполнено приблизительно.

## <a id="view-edit"></a>Изменение вида и его отказ

**Что было НЕВЕРНО названо.** Первый отчёт утверждал, что «отдельного документированного маршрута
ИЗМЕНЕНИЯ вида справка v24 не даёт». Это утверждение ложно: справка v24 документирует изменение
масштаба через запись `IView.Scale` с обязательным последующим `IDrawingObject.Update`.

**Что измерено по справке.** `iview_scale.html` - `Scale` ЧИТАЕТСЯ И ЗАПИСЫВАЕТСЯ (Automation
`iObject.Scale = Scale`, COM `put_Scale`); примечание: «Свойство вступает в силу после вызова метода
`IDrawingObject::Update`». `idrawingobject_update.html` - `BOOL Update()` возвращает TRUE при успехе,
«Метод Update необходимо вызвать для объекта, свойства которого были изменены … чтобы эти изменения
вступили в силу». `iview_x.html` / `iview_y.html` - координаты привязки, тот же маршрут обновления.
`iview_visible.html` - «Состояние вида - видимый или погашенный», ЧИТАЕТСЯ И ЗАПИСЫВАЕТСЯ, тоже
вступает в силу после `Update` - это и есть документированный маршрут ПОДАВЛЕНИЯ/ВОЗВРАТА вида.
`idrawingobject_delete.html` - `BOOL Delete()`, «После успешного выполнения метода объект будет
удален из модели. Свойство `Valid` для объекта будет возвращать FALSE» - это документированный
маршрут УДАЛЕНИЯ.

**Что решено.** Открыт инструмент `kompas_edit_view`: он ИЗМЕНЯЕТ существующий вид по ссылке
(масштаб и, при задании, точку привязки), вызывает `IDrawingObject.Update()` и ПЕРЕЧИТЫВАЕТ вид -
положительное доказательство даётся сравнением ДО/ПОСЛЕ одного и того же объекта, а не добавлением
нового. Подавление (`Visible`) и удаление (`Delete`) названы документированными и остаются отдельными
действиями - наличие маршрута изменения масштаба их НЕ подтверждает, и в интерфейс они в этой
итерации не выведены.

**ЧТО ИЗМЕРЕНО ЖИВЬЁМ (06.10.2026).** Запись `IView.Scale` с последующим `Update()` РАБОТАЕТ на
виде, построенном `AddStandartViews`: масштаб перечитывается изменённым (1 → 2), то есть
документированный маршрут редактирования вида подтверждён. НО `Update()` при этом НЕ является
надёжным признаком исхода: на СИСТЕМНОМ виде, который уже есть у свежего чертежа (`vt_System`),
та же запись возвращает `false`, и свойство не применяется. Поэтому булево значение `Update()`
публикуется как ОТДЕЛЬНАЯ проверка (`update_returned_true`), а решение об успехе принимается
ТОЛЬКО по перечитыванию вида. Строить отказ на `Update()=false` было бы дефектом: измерение
показывает, что этот признак расходится с фактическим состоянием вида.

**ДЕФЕКТ RCW, НАЙДЕННЫЙ ЗДЕСЬ.** Прежний код хранил в реестре ссылок САМ RCW вида
(`References.Register("view", …, view)`) и тут же освобождал его в `finally`. Следующая команда по
такой ссылке падала с «COM object that has been separated from its underlying RCW cannot be used» -
это подтверждено живьём. Исправлено: в payload лежит АДРЕС вида (`IView.Reference`), а разрешение
перечитывает коллекцию и ищет по адресу. Тот же класс дефекта устранён у линейного размера
(payload - `null`: у `IDimension` адреса нет, а ссылка на размер обратно не разрешается).

## <a id="dimensions"></a>Размеры

**Что решено.** Контейнер `ISymbols2DContainer` получается из `IView`; `Add()` соответствующей
коллекции возвращает объект, координаты которого задаются свойствами. Линейный размер пишет
`X1/Y1/X2/Y2` и, если задано, `X3/Y3`; радиальный и диаметральный - `Xc/Yc/Radius`.

**Граница, названная прямо.** Размер привязан к ТОЧКАМ вида в координатах вида, а не к топологии
модели. Ассоциативность размера к модели НЕ заявляется: справка документирует точки вида, а не
привязку размера к геометрии. Значение линейного размера сервер считает как расстояние между точками;
радиус радиального/диаметрального - либо из `value_mm`, либо как расстояние «центр → точка на
окружности».

## <a id="stamp-cells"></a>Ячейки основной надписи

**Что измерено.** `IStamp.Text(Id)` - ТОЛЬКО ЧТЕНИЕ и возвращает `IText`; запись идёт через
`IText.Str`, затем `IStamp.Update()`. Числовые идентификаторы ячеек справкой НЕ документированы.

**Что решено.** Номера ячеек передаёт вызывающий; после `Update()` каждая ячейка ПЕРЕЧИТЫВАЕТСЯ, и
признак совпадения попадает в ответ. Несовпадение не скрывается: оно и отсутствие таблицы
«понятие → номер» названы в `unverified_aspects`. Ключ, не являющийся числом, отвергается
`INVALID_ARGUMENT`.

## <a id="technical-demand"></a>Технические требования (пункт 6 наряда)

**Что измерено по справке.** `idrawingdocument_technicaldemand.html` - `IDrawingDocument.TechnicalDemand`
отдаёт указатель на `ITechnicalDemand` («доступно только для чтения» - это про свойство, а не про
текст). `itechnicaldemand_text.html` - `Text` отдаёт интерфейс `IText`; запись идёт в `IText.Str`.
`itechnicaldemand_update.html` - `Update()` «применить заданные параметры технических требований».
`itechnicaldemand_iscreated.html` - `IsCreated` (BOOL), «признак отображения технических требований в
документе», только чтение.

**Что решено.** Открыт инструмент `kompas_set_technical_demand`: он пишет текст в `IText.Str`, вызывает
`Update()` и ПЕРЕЧИТЫВАЕТ блок - совпадение текста и признак `IsCreated` и есть доказательство. Как и
в ячейках штампа, признак `Update()` сам по себе в уровень проверки не превращается.

**Названная граница.** Размещение блока на листе (`AutoPlacement`, `BlocksGabarits`) сервером не
задаётся и не проверяется - это названо в `unverified_aspects`, а не выдано за настроенное.

## <a id="export-formats"></a>Форматы выгрузки

**Что измерено.** `IConverter.GetFilter(DocType, SaveAs, out Command)` заполняет код команды;
`FORMAT_DXF=1`, `FORMAT_DWG=2`. PDF в справке SDK не описан как программный маршрут.

**Что решено.** Опубликованы только dxf и dwg. PDF отвергается кодом `FORMAT_UNAVAILABLE` по имени, а
не предпринимается вслепую. Результат подтверждается проверкой файла: существование, непустой размер,
сигнатура. Перезапись существующего файла - только с явным `overwrite=true`.

**ЧТО ИЗМЕРЕНО ЖИВЬЁМ ПРО ФОРМУ ВЫГРУЗКИ (06.10.2026).** Конвертер v24 пишет И DXF, И DWG как
**ZIP-контейнер** (первые байты `PK\x03\x04`), а не как текстовый DXF с заголовком секции и не как
`AC10xx`-файл. Внутри контейнера лежат члены `FileInfo`, `Sources`, `Contents`, `Preview`,
`MetaProductInfo`, `Options.xml`, а также `images/<...>.TechnicalDemand` - то есть блок техтребований
попал в выгрузку. Поэтому проверка формы расширена: она ПРИНИМАЕТ и текстовую форму, и ZIP-контейнер, и
НАЗЫВАЕТ найденное (`zip:Contents`, `dxf_section_header`, `dwg_ac10xx`), а не выдаёт одну форму за
единственно возможную. Это не поблажка прибору: нераспознанная форма по-прежнему называется, а не
проходит за успех.

**ИСПРАВЛЕН ДЕФЕКТ ТРАКТОВКИ РЕЗУЛЬТАТА (второй заход).** Прежний код отвергал документированный
успех: условие `converted != 0` считало успехом `0`, тогда как `iconverter_convert.html` задаёт
прямо: «Возвращаемое значение: `1` - в случае успешного завершения, `0` - в случае неудачи».
Трактовка приведена к справке v24: успех - только `1`. Причина, по которой дефект держался: отчёт
называл это «расхождением, которое разрешит живое измерение», то есть ставил под сомнение справку, а
не код. Справка целевой версии однозначна, поэтому это исправление дефекта, а не исследование.
Независимая проверка файла сохранена как ОТДЕЛЬНОЕ подтверждение: она ловит документированный успех,
не давший файла, и не превращает неудачу в успех из-за наличия файла.

## <a id="tools"></a>Инструменты Host

**Что решено.** Пять инструментов объявлены в `ToolCatalog.cs` с русскими описаниями, маршрутами и
границами. Мутирующие (`create_drawing_views`, `add_dimension`, `set_title_block`, `export_drawing`)
требуют `expected_revision` и `operation_id`; чтение (`list_drawing_views`) - нет. Пути `source_path`
(чтение) и `output_path` (запись) уже входят в `ToolInvoker.PathFields` и проверяются политикой путей.

## <a id="worker"></a>Worker

**Что решено.** Три мутирующие команды проходят через общий узел мутаций (`TaggedAfter`) - берётся
контрольная копия и в конверт попадает ревизия; чтение идёт через `Tagged`. Бюджет команд - 240 000 мс
(маршрут API7 с TransferInterface, `AddStandartViews`/конвертером длиннее чистого API5-вызова).
