# Расширение сборок - маршруты, измерения и решения

Наряд `ASSEMBLY_EXT_RESEARCH` (10.11.2026). Здесь - история исследования блока «расширение сборок»:
какие документированные маршруты существуют для трёх тем (массивы компонентов, виды сопряжений сверх
профиля `mates-minimal-v1`, правка компонентов внутри подсборок), что из этого проверено живьём и что
решено включать в продукт. Код продукта этим нарядом не менялся.

Граница документации соблюдена: каждая возможность ниже опирается на страницу справки SDK v24 из
зеркала, а не на имя в TLB.

## <a id="pages"></a>Страницы справки

- Сняты в `reference/kompas-sdk-v24/docs-wire-20261110-assembly/` сценарием
  `_fetch_docs_wire_20261110_assembly.py` (рядом, в корне зеркала): **583 страницы** по оглавлению
  Help+Manual, **17** по ссылкам из них. Состав берётся префиксами оглавления `_hmcontent.js`, не
  придумывается: `ifeaturepattern*`, `i*pattern*`, `ks*PartArrayDefinition*`, `ksDeletedCopyCollection*`,
  `imate*`, `mateconstraint*`, `ksmate*`, `ipart7*`, `iparts7*`, `kspart*`, `iplacement3d*`,
  `iopendocumentparam*`, `ipartdocument*`, `iassemblydocument*`, `obj3dtype`, `copytype`.
- Отрицательный контроль (404 = довод «маршрута нет», а не догадка): `kscomponentpattern`,
  `icomponentpattern`, `ipartarray`, `kspart_setnestedplacement`, `imatenested`.
- Часть нужного уже была в зеркале и НЕ переснималась: `ifeaturepattern*`, `ilinearpattern*`,
  `icircularpattern*`, `imirrorpattern*`, `imateconstraint3d*`, `mateconstrainttype`,
  `ipart7.html`, `iparts7.html`, `kspartcollection*`, `obj3dtype` (каталог `sdk-docs/`) и
  `imate3dtransmission*`, `imate3dcamgear*` (`docs-wire-20261019-mates/`).

## <a id="component-arrays"></a>Массивы компонентов

Массив компонентов в KOMPAS - это **операция копирования** (`IFeaturePattern`) с собственным номером
типа объекта, а не отдельное семейство интерфейсов. Соответствие «номер → интерфейс» напечатано на
странице `copytype.html` («Типы операций копирования»):

| Что копируется | Номер `ksObj3dTypeEnum` | Интерфейс параметров | Страница |
|---|---|---|---|
| компоненты по сетке | `o3d_meshPartArray` = 39 | `ILinearPattern` | `ilinearpattern.html` |
| компоненты по концентрической сетке | `o3d_circPartArray` = 38 | `ICircularPattern` | `icircularpattern.html` |
| компоненты по кривой | `o3d_curvePartArray` = 40 | `IPathPattern` | `ipathpattern.html` |
| компоненты по образцу | `o3d_derivPartArray` = 41 | `IDerivedPattern` | `iderivedpattern.html` |
| компоненты по точкам | `o3d_PartsPointDrivenPattern` = 508 | `IPointDrivenPattern` | `ipointdrivenpattern.html` |
| компоненты по таблице из файла | `o3d_PartsTablePattern` = 523 | `ITablePattern` | `itablepattern.html` |

**Маршрут записи (API7, проверен живьём).** `IPart7` отвечает на `IModelContainer` (страница
`ipart7.html`, строка «Иерархия»), у контейнера - `FeaturePatterns` (`imodelcontainer_props.html`:
«Указатель на интерфейс коллекции операций копирования»), у коллекции - `Add(ksObj3dTypeEnum)`
(`ifeaturepatterns_add.html`: «В зависимости от типа объекта создается операция копирования
определенного типа»). Дальше `QI` к интерфейсу вида, `AddInitialObjects` (`ifeaturepattern_addinitialobjects.html`:
«объект - тип VARIANT-а VT_DISPATCH или список объектов - тип VARIANT-а VT_ARRAY | VT_DISPATCH»), затем
`Update()`. Пригодность источника проверяется самим ядром: `IsSuitableObject` («TRUE - указанный
объект можно скопировать с помощью данной операции»).

**Чтение.** `GetExemplarsCounts` (`ifeaturepattern_getexemplarscounts.html`; примечание: «Два
направления копирования имеют массивы по сетке и по концентрической сетке. Для массивов остальных
типов Count2 равен 1»), `Count1/Count2/Step1/Step2/Axis1/Axis2/BuildingType` у `ILinearPattern`,
`Exemplar` и `InstanceDeletedIndexes`. Экземпляры массива читаются как обычные компоненты: они
появляются в `ksDocument3D.PartCollection(true)` и в `IPart7.Parts` (см. пробы).

**Маршрут записи (API5, документирован, НЕ проверен).** `ksPart.NewEntity(o3d_meshPartArray)` →
`ksMeshPartArrayDefinition` (страница `ksmeshpartarraydefinition.html`), у него `PartArray`
(«Получить указатель на массив компонентов для копирования»), `SetAxis1/SetAxis2`,
`SetCopyParamAlongAxis`, `count1/step1/...`, `DeletedCollection`. Для остальных трёх видов -
`ksCircularPartArrayDefinition`, `ksCurvePartArrayDefinition`, `ksDerivativePartArrayDefinition` с
теми же членами в своей редакции. Продукт этот маршрут не использует: он повторял бы уже написанный
API7-путь массивов.

**Измерено (проба, см. `#probes`).** Массив по сетке из одного компонента: компонентов 1 → 3, начала
`(0,0,0)`, `(30,0,0)`, `(60,0,0)`, `GetExemplarsCounts` = 3 и 1, после сохранения и переоткрытия -
те же три начала и один признак массива. `Exemplar(1,0)` на этом массиве вернул **null** - перечень
экземпляров берётся из коллекции компонентов, а не из `Exemplar`.

## <a id="mate-types"></a>Виды сопряжений сверх профиля

Перечисление `MateConstraintType` (`mateconstrainttype.html`) целиком:

| Значение | Тип | Профиль `mates-minimal-v1` | Документированный интерфейс параметров | Годен |
|---|---|---|---|---|
| 0 | `mc_Coincidence` | да | `IMateConstraint3D` (`Alignment`, `BaseObject1/2`) | да, в профиле |
| 1 | `mc_Parallel` | да | `IMateConstraint3D` | да, в профиле |
| 2 | `mc_Perpendicular` | да | `IMateConstraint3D` | да, в профиле |
| 3 | `mc_Tangency` | да | `IMateConstraint3D` + `IMate3DByTangent` (`TangentType`) | да, в профиле; вид касания не входит |
| 4 | `mc_Concentric` | да | `IMateConstraint3D` | да, в профиле |
| 5 | `mc_Distance` | да | `IMateConstraint3D` (`ParamValue`) | да, в профиле |
| 6 | `mc_Angle` | да | `IMateConstraint3D` + `IMate3DByAngle` (`Angle3D`, `Axis`) | да, в профиле; плоский угол и ось не входят |
| 7 | `mc_InPlace` | нет | нет отдельного интерфейса | **не годен**: это режим СОЗДАНИЯ компонента («создание компонента "на месте"»), у типа нет параметров и нет документированного вызова, который его создаёт |
| 9 | `mc_Transmission` | да | `IMate3DTransmission` | да, в профиле |
| 10 | `mc_CamGear` | нет | `IMate3DCamGear` (`CamFaces`, `FollowerFace`, `RotationAxis`, `Trajectory`) | **не годен**: маршрут документирован, но набор объектов, при котором сопряжение действительно (`Valid`), не установлен (`probes.md#mechanical-mates`) |
| 11 | `mc_Symmetric` | нет | `IMate3DSymmetry` (`Plane`: `IModelObject`) | **да, проверено живьём** |
| 14 | `mc_Dependent` | нет | `IMate3DDependentPosition` (`BySample`, `SampleObject1/2`: `IPart7`) | **да, проверено живьём** |

`MateConstraintType` из интеропа содержит ещё `mc_Unknown = -1` - служебное значение, не вид
сопряжения.

**Расширение параметров существующих видов** (документировано, НЕ проверено живьём, в первый наряд не
входит): `IMate3DByAngle.Angle3D` («TRUE - пространственный угол, FALSE - плоский угол») и `Axis`
(«Ось плоского угла»), `IMate3DByTangent.TangentType` (`ksMateTangentTypeEnum`: по точке, по
образующей, по окружности). Обе страницы - `ksapi_imate3dbyangle_*` и `ksapi_imate3dbytangent_*`.

**Чего в перечислении нет.** Объединение компонентов и вычитание компонентов - это ОПЕРАЦИИ
(`o3d_UnionComponents` = 64 → `IUnionComponents`, `o3d_MoldCavity` = 65 → `IMoldCavity`), а не виды
сопряжений; в таблицу видов они не входят.

## <a id="nested-components"></a>Компоненты внутри подсборок

**Чтение.** Вложенный компонент достижим рекурсией `IPart7.Parts` → `IParts7::Part`: страница
`ipart7.html` описывает её дословно («От каждого элемента коллекции-компонента может быть получена
вложенная коллекция компонентов c помощью свойства IPart7::Parts. И так далее, пока имеются вложенные
коллекции компонентов»). Адреса API5 у вложенного компонента НЕТ: измерено, что
`ksDocument3D.PartCollection(true)` на сборке «подсборка + свободная деталь» даёт 2 записи, а не 3.

**Размещение.** `IPart7.Placement` - свойство ТОЛЬКО ДЛЯ ЧТЕНИЯ (`ipart7_placement.html`), но
возвращённый `IPlacement3D` меняется методами: `InitByMatrix3D` («Установить систему координат по
матрице», `iplacement3d_initbymatrix3d.html`), после чего `IPart7.UpdatePlacement(Redraw)`
(«Установить локальную систему координат детали», `ipart7_updateplacement.html`). Измерено: на
ВЛОЖЕННОМ компоненте запись работает и читается обратно.

**Источник.** `IPart7.FileName` - записываемое свойство (`ipart7_filename.html`: «Имя файла можно
установить только у компонента, вставленного в сборку», «Свойство вступает в силу после вызова метода
IModelObject::Update»). Замена верхнего уровня тем же маршрутом уже в продукте
(`kompas_replace_component`); измерено, что на вложенном компоненте она тоже работает и переживает
сохранение с переоткрытием. Документированная альтернатива для замены в массиве компонентов -
`ksPartCollection.SetByIndex` («Заменить компонент в массиве по индексу»,
`kspartcollection_setbyindex.html`).

**Подсборка как документ.** Два документированных входа, оба на `IPart7`:
`GetOpenDocumentParam()` → `IOpenDocumentParam` (`Visible`, `ReadOnly`) и
`BeginEdit(param)` → `IKompasDocument3D` (`ipart7_beginedit.html`); закрытие - `EndEdit`.
Измерено: `IPart7.BeginEdit` даёт документ подсборки, `EndEdit(true)` возвращает TRUE. API5-путь
`ksPart.BeginEdit` («Запустить режим редактирования на месте», `kspart_beginedit.html`) на вложенном
компоненте вернул **null** - у API5 нет объекта, который его адресует, и ограничение страницы
(«работает только для компонента, вставленного в сборку», «только для визуального документа») здесь не
единственная причина.

**Условия сохранения (измерено).** Правка вложенного компонента переживает сохранение ТОЛЬКО верхней
сборки: после `Save()` и переоткрытия размещение то же. Отметка файла подсборки при этом МЕНЯЕТСЯ
(размер и время записи), то есть сохранение верхней сборки записывает и файл подсборки. Признак
`IPart7.NeedRebuild` справка обещает, но в `Interop.KompasAPI7.dll` его нет (ошибка компиляции
CS1061), поэтому ждать перестроения приходится по бюджету, а не по флагу.

## <a id="decisions"></a>Решения

1. **Массивы компонентов входят в блок** и идут тем же маршрутом, что массивы операций и тел:
   `IModelContainer.FeaturePatterns.Add` → `QI` к виду → источники → `Update()`. Проверены живьём
   сетка (39); остальные виды (38, 40, 41) документированы тем же маршрутом, но не проверялись.
   Виды по точкам (508) и по таблице (523) документированы и в первый наряд не входят.
2. **Сопряжения.** Новые виды - `symmetric` (11) и `dependent` (14): оба документированы, оба
   проверены живьём геометрией. `InPlace` (7) и `CamGear` (10) в блок НЕ входят с названными
   причинами. Расширение параметров `angle`/`tangency` - кандидат следующего наряда, не проверено.
3. **Правка внутри подсборок входит в блок** маршрутом API7: чтение - рекурсия `IPart7.Parts`,
   размещение - `IPlacement3D.InitByMatrix3D` + `UpdatePlacement`, источник - `FileName` + `Update`,
   подсборка как документ - `BeginEdit`/`EndEdit`. Действующий `LIMIT` адаптера «у вложенного
   компонента нет адреса API5, правка не поддерживается» остаётся верным для API5 и СНИМАЕТСЯ для
   API7.
4. **Контракт меняется аддитивно**: необязательное значение `copy_kind=components`, новые имена видов
   сопряжений, адресация вложенного компонента через уже существующий `component:` ref с глубиной.
   По `AGENTS.md` это MINOR.

## <a id="not-included"></a>Не входит

- Массивы компонентов по точкам и по таблице (508, 523), массив по образцу (41): документированы, не
  проверены.
- Зеркальный массив компонентов: `o3d_mirrorOperation` = 48 документирован как зеркальный массив
  ОПЕРАЦИЙ; что он копирует компоненты, справка не утверждает, и это не проверено.
- `mc_InPlace`, `mc_CamGear` - причины в таблице видов.
- Плоский угол и ось (`IMate3DByAngle`), вид касания (`IMate3DByTangent`): маршрут есть, измерений нет.
- Объединение и вычитание компонентов (`IUnionComponents`, `IMoldCavity`): это операции, а не
  сопряжения, и отдельный блок моделирования.

History: `docs/acceptance/evidence/20261110-assembly-ext-*/README.md` - строки проб с ожиданием и
ответом КОМПАС; `ASSEMBLY_EXT_RESEARCH_REPORT_20261110.md` - перечень маршрутов, проект профиля и
порядок нарядов реализации.
