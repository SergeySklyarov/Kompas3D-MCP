# Part B (SM-18…SM-29) — источники и атрибуция утверждений

Дата прохода: 2026-09-11.Stage: P6.0 (инвентаризация), тип работы: исследование, **без запуска КОМПАС**.

Потолок этого прохода — `metadata_found`. Ни одно утверждение ниже не помечено `runtime_verified`
и ни один COM-вызов не исполнялся: `tools/KompasMcp.P0Probe`, `tools/KompasMcp.Api7Probe`,
`scripts/mcp-smoke.py` не запускались; CAD-сеанс остаётся за основным сеансом.

## 0. Артефакты, которыми я пользовался (и чем они отличаются)

| Ключ в `entries.json` | Что это фактически | Почему авторитетно / когда НЕ авторитетно |
|---|---|---|
| `Bin\kAPI7.tlb (дамп)` | `scratch/api7-index.txt` — построчный дамп установленной библиотеки типов: 838 записей `INTERFACE`, для каждой IID и члены в порядке vtable | Авторитет по составу API7 установленного приложения (env-passport: kAPI7.tlb, mtime 2025-04-21, sha256 `68b7730ef13bde7d…`, 1498 typeinfo / 836 интерфейсов). Отсутствие имени здесь — сильный аргумент, но всё ещё не «отсутствия в продукте нет» для живого позднего связывания |
| `docs/compatibility/kompas-api5-metadata.json` | рефлексия `Libs\PolynomLib\Bin\Client\Interop.Kompas6API5.dll` (1007 типов: 575 interface + 432 class) | Источник имён/сигнатур API5. `env-passport` насчитал в этой же сборке 1018 типов против 1007 в дампе → расхождение учёта типов, см. OQ-B-28 |
| `Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll` | вендорская prebuilt-обёртка, 2298 типов, файл 2025-03-03 (старше TLB) | Её неполнота НЕ доказательство отсутствия (docs/05 §3.1, ADR-003 §4): измеренный пример — `ElementaryBodies`/`PipeElements` отсутствуют в обёртке, объявлены в kAPI7.tlb |
| `Bin\ksConstants3D.tlb`, `Bin\ksConstants.tlb` (через `scratch/tlgen/*.py`) | значения `ksObj3dTypeEnum` (339), `KompasAPIObjectTypeEnum`, `ProcessTypeEnum`, и перечисления режимов массивов | Числа и имена констант взяты из установленного TLB, а не из документации |
| `KOMPAS_ru-RU.zip!…` | **установленная** пользовательская справка v24 (`D:\Programs\KOMPAS-3Dv24\Help\KOMPAS_ru-RU.zip`, 137 МБ, 11088 файлов, 3273 `.html`-топика + `jstopics/*.js`), читалась только из zip, извлечено в `scratch/inventory/part-b/help/` | Совпадает со сборкой продукта (24.0.0.2799), то есть сильнее онлайна. См. §1 про онлайн |
| `https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/…` | онлайн-справка SDK | Статические страницы, читаются `web_fetch` |
| `src/KompasMcp.Host/Catalog/ToolCatalog.cs` | фактический MCP-рельс 26 инструментов | Основание для поля `mcp_support` |

Онлайн-справка пользователя `https://help.ascon.ru/KOMPAS/24/ru-RU/` **не извлекается programmatically**:
`web_fetch` возвращает `Please enable JavaScript to view this site.` при `Status: 200 OK`
(проверено на `glava_49_massiv_po_setke.html`, format=html и format=auto). Поэтому для пользовательской
справки первичен локальный пакет той же сборки, а онлайн-URL оставлен в записях как ссылка на
публичный адрес того же топика (заголовок топика подтверждён поисковой выдачей). Это усиление
источника, а не подмена: имена операций, параметры и правила взяты дословно из установленной справки.

Первичные скрипты разбора (все — только чтение файлов, без COM-активации):
`meta_scan.py`, `api5_types.py`, `enum_dump2.py`, `help_extract.py` в этом каталоге;
их выводы сохранены как `enum-dumps.txt`, `pattern-enums.txt`, `api5-arrays.txt`, `api5-copies.txt`,
`help-*.txt`, `ft[234]-names.txt`, `help/` — чтобы любое утверждение можно было перепроверить без меня.

## 1. Массивы SM-18…SM-23: откуда взято разбиение

- **Состав семейств массивов v24 (7 команд)** — `KOMPAS_ru-RU.zip!glava_48_obzhie_svedeniy.html`:
  «по сетке», «по концентрической сетке», «вдоль кривой», «по точкам», «по таблице»,
  «зеркальный массив», «по образцу (для компонентов сборки)». В `docs/05 §5` названо 5
  (SM-18…SM-22) → «по таблице» и «зеркальный массив как тип массива» добавлены в матрицу (docs/05 §2.2).
- **Что копируется (обязательная размерность)** — `KOMPAS_ru-RU.zip!48_3_1_vibor_kopiruemih_obtktov.html`:
  группа `Тип` = Автоопределение | Операции | Тела или поверхности | Грани |
  Кривые, точки, вспомогательная геометрия | Произвольный | Компоненты;
  для «по образцу» — только Компоненты; для зеркального — без Произвольный и без Компоненты;
  тип выбирается **только при создании**, при редактировании сменить нельзя.
- **Тот же размер в метаданных** — `SDK copytype.html` (таблица o3d → интерфейс) +
  `ksObj3dTypeEnum` установленного `ksConstants3D.tlb`: суффиксы
  `…Copy`(35/36/37 = операции), `…PartArray`(38/39/40/41 = компоненты/сборка),
  `Aux…`(504/505/506/509/520/524 = вспомогательная геометрия), `Bodies…`(521/525/528/529/530 = тела),
  `…AnyCopy`(591/592/593/595/596 = произвольные объекты). То есть «что копируется» кодируется
  **отдельным o3d-типом**, а не флагом — это архитектурно важно для выбора `IFeaturePatterns.Add(Type)`.
- **Геометрический массив** — `KOMPAS_ru-RU.zip!48_3_3_geometricheskiy_massiv.html` (копируются только
  грани и рёбра, без параметров операций; условия замкнутости, непересечения, одного типа операций,
  того же тела в многотельной модели; несовместим с переменными экземпляров) ↔
  `IFeaturePattern.GeometryPattern` (kAPI7.tlb) и `geomArray` в `ksMeshCopyDefinition`/
  `ksCircularCopyDefinition`/`ksCurveCopyDefinition` (дамп API5).
- **Пропуски экземпляров** — `48_3_4_udalenie_i_vosstanovlenie_ekzemplyrov.html`
  (только целиком; недоступно для зеркального и по образцу) ↔
  `IFeaturePattern.InstanceDeletedIndexes` и API5 `DeletedCollection()` →
  `ksDeletedCopyCollection{Add(i1,i2), DetachByBody(i1,i2), FindIt, Clear, refresh}`.
- **Ориентация** — `50_5_orientaciy_ekzemplyrov_massiva.html` («Доворачивать»/«Сохранять исходную») ↔
  `SaveInitialOrientation` у ICircularPattern/IPathPattern/ITablePattern/IPointDrivenPattern;
  `ksPatternExemplarsOrientationTypeEnum{ksOrientationSave=0, ksOrientationByNormal=1, ksOrientationByObject=2}`
  ↔ `OrientationType` у ITablePattern и IPointDrivenPattern; у `ILinearPattern` свойства
  `SaveInitialOrientation` **нет** (ориентация осями/углами сетки).
- **Способ построения** — `ksLinearPatternBuildingTypeEnum{ksLPSaveAll=0, ksLPSaveAlongPerimeter=1,
  ksLPSaveAlongAxially=2, ksLPChessOrderByAxis1=3, ksLPChessOrderByAxis2=4}` и
  `ksCircularPatternBuildingTypeEnum{ksCPSaveAll=0, ksCPChessOrderByAxis1=1, ksCPChessOrderByAxis2=2}`
  из установленного `ksConstants3D.tlb`.
- **Разрушение массива** — `48_3_5_razrushenie_massivov.html`: не для массива операций и граней;
  таблица преобразования типов (тело → тело без истории; вспомогательная плоскость →
  перпендикулярная плоскость; точки → точка по координатам; кривые → кривая без истории; компонент → компонент);
  разрушение мастер-массива разрушает производный.
- **Область применения экземпляров** — `48_2_osobennoiti_postroeniy_massiviv_v_mnogotelnoy_detali.html`:
  экземпляр наследует область применения копируемой операции; новые тела массивом операций не возникают;
  в сборке копия операции вычитает материал тех же компонентов; область применения экземпляров
  менять нельзя (кроме массива произвольных объектов).

## 2. Пофамильная атрибуция ключевых записей

| Запись | Справка пользователя (KOMPAS_ru-RU.zip) | SDK/метаданные (точные имена) |
|---|---|---|
| SM-18.grid | `glava_49_massiv_po_setke`, `49_1_1_bazovay_tochka_ekzemplyra`, `49_3_1_napravlenie_pervoy_osi`, `49_3_2_shag_setki_vdol_pervoy_osi`, `49_5`, `49_6` | kAPI7.tlb `ILinearPattern{Angle1/2, Axis1/2, Vector1/2(get-only), Direction1/2, Step1/2, Count1/2, BoundaryInstancesStepFactor1/2, BuildingType, Set/GetBaseExemplarPlacement}`; API5 `ksMeshPartArrayDefinition{angle1/2, count1/2, step1/2, factor1/2, insideFlag, GetAxis1/2, SetAxis1/2, Get/SetCopyParamAlongAxis, PartArray, DeletedCollection}`, `ksMeshCopyDefinition` (+`geomArray`, `OperationArray`) |
| SM-18.table | `glava_53_massiv_po_tablice`, `56_2_zadanie_poziciy_ekzemplyrov`, `56_2_1_chtenie_koordinat_iz_fayla`, `53_3` | kAPI7.tlb `ITablePattern{FileName, PointsType, SaveInitialOrientation, OrientationType, OrientationObject}`; `IPointsArrFromFile{PointsType, FileName, Symbol, Destroy}` |
| SM-19.circular | `glava_50_massiv_po_koncentr_setke`, `50_1`, `50_3`, `50_4`, `50_5`, `50_6` | `ICircularPattern{Axis, Step1, Count1, Step2, Count2, BoundaryInstancesStepFactor1/2, SaveInitialOrientation, ReverseDirection, Set/GetBaseExemplarPlacement, BuildingType, StepByAxis}`; API5 `ksCircularPartArrayDefinition` (в т.ч. `inverce` — опечатка вендора), `ksCircularCopyDefinition` (`geomArray`) |
| SM-20.path | `glava_51_massiv_vdol_krivoy`, `51_1_575`, `51_2`, `51_3`, `51_4_napravlenie_kopirovaniy`, `51_5`, `653_51_3_8`, `654_51_4` | `IPathPattern{Curves, Count, StartingPoint, ByStep, Step, BoundaryInstancesStepFactor, SaveInitialOrientation, ReverseDirection}`; API5 `ksCurvePartArrayDefinition` (в т.ч. `fullCurve`, `sence` — опечатка вендора), `ksCurveCopyDefinition` |
| SM-21.points | `glava_52_massiv_po_tockam`, `52_2_1`, `52_9_sposobi_zadaniy_bazovoy_tochki`, `54_3`, `idp_array_point_bad_orientation` | `IPointDrivenPattern{DrivenObjects, IsSuitableDrivenObject, ClearDrivenObjects, ProjectionPoints, SaveInitialOrientation, OrientationType, OrientationObject}`; источники `IPointsArrOnCurve`, `IPointsArrOnSurface`, `IPointsArrFromFile` + `ksPointsArrOnCurveTypeEnum`, `ksPointsArrOnSurfaceTypeEnum` |
| SM-22.derived | `glava_56_massiv_po_obrazcu`, `2710_593_massiv_proizv_obj`, `27102_obl_primen_proizv_massiva`, `48_3_5` | `IDerivedPattern{MasterPattern, OrientBySample, SampleExemplar, AllowNesting, AllowDeleted}`; `o3d_derivPartArray=41` |
| SM-23.mirror_array | `glava_55_zerkalniy_massiv`, `55_2_1` | `IMirrorPattern{Plane, SaveInitialObjects}`; API5 `ksMirrorCopyDefinition{GetOperationArray, GetPlane, SetPlane}` |
| SM-23.mirror_all | отдельной страницы нет (см. OQ-B-10); см. `SDK copytype.html` | `o3d_mirrorAllOperation=49` + `IChooseBodies7{ChooseBodiesType, Bodies}`; API5 `ksMirrorCopyAllDefinition{GetPlane, SetPlane, ChooseBodies}` |
| SM-24.move_faces | `cm_make_move_faces`, `face_position` | `IFaceMover{Faces, SetFaces(Faces,TangentFaces), Offset, Direction}`, владелец `ISurfaceContainer.FaceMovers`; `o3d_FaceMover=632`, `prMakeFaceMover=20226` |
| SM-24.delete_faces | `cm_make_face_remover`, `1085_114_12_narushenie_tzelostnosti` | `IFaceRemover{Faces, SaveBody}`, `ISurfaceContainer.FaceRemovers`; `o3d_FaceRemover=95`, `prMakeFaceRemover=20096` |
| SM-24.replace_faces | `cm_make_replace_faces`, `face_replace`, `view_replace_face` | только `o3d_FaceReplacer=659` (ksConstants3D) — интерфейса нет ни в kAPI7.tlb, ни в дампе API5 |
| SM-24.resize_face | `cm_make_face_resizer`, `face_resize`, `view_face_resize` | только `o3d_FaceResizer=660` + `prFaceResizer=20248` — интерфейса нет |
| SM-24.restore_faces | `1085_114_12`, `550_glava49_redaktirovanie_poverkhnostei` | `IRestoredSurface{Face}`, `ISurfaceContainer.RestoredSurfaces`; `o3d_RestoredSurface=620`, `prRestoredSurface=20218` |
| SM-24.change_fillet | `cm_make_fillet_changer`, `rounding_change`, `view_rounding_change` | не найдено |
| SM-25.remove_body_surface | `remove_body`, `review_remove`, `236_gl_24_obshchie_sved_o_telah` | `IBodySurfaceRemover{DeletingObjects}`, `ISurfaceContainer.BodySurfaceRemovers`; `o3d_BodySurfaceRemover=646`, `prBodySurfaceRemover=20237` |
| SM-25.delete_object_generic | `833_2_14_3_8_udal_obj` | `IFeature7{Delete, Excluded, SubFeatures(TreeType,Through,LibObject), ResultBodies, OwnerFeature, OrderNumber, UpdateStamp, Valid, ObjectError, State}` |
| SM-25.retarget_links | `cm_change_ref_object_3d` | `IPart7.ChangeObjectLinks(SourceObjs, DestObjs, RebuildAll)` |
| SM-26.* | `cm_save_body_as_detail`, `cm_spc_insert_billet`, `cm_product_tree_add_billet`, `cm_convert_to_billet`, `1631_pril_oboznach_v_dereve_modeli`, `1184_135_6_1_nastrojki_obwekta_`, `changing_download_type_component`, `objects_lsk` | `ICopyGeometry` (31 член, включая `AddInitialObjectsFromExternalDocument`, `AutoUpdate`, `WatchForSourceChange`, `Synhronise`, `OpenDocument(Visible,ReadOnly)`, `MirrorCopy`, `BuildingType`, `ContextObjects`, `ByCollectionGeometry`); `ICollectionGeometry{Geometry}`; `IBilletObsolete{FileName}` + `IBilletsObsoletes.Add(FileName, Mirror)`; `IPart7{IsBillet, IsLayoutGeometry, LeftHandedCS, MirroringPlacement, ReadOnly, IsLocal, Load/Unload/LoadState, GetOpenDocumentParam/BeginEdit/EndEdit, SaveAs, FindBody, GetBodyById, TransferObjects}`; перечисление `ksCopyGeometryBuildingTypeEnum{ksCGBTWithoutGrouping=0, ksCGBTBodyFaceGrouping=1}` |
| SM-27.thicken_faces | `cm_surface_to_body` (заголовок «Придание толщины»), `847_90_4_pridan_tol_pov`, `idp_stch_stitcherror`, `idp_warning_no_closed_solid` | `ISurfaceThickening{Faces}` (2 члена!), `IModelContainer.SurfaceThickenings`; `o3d_SurfaceThickening=518` |
| SM-27.sew_surfaces | `cm_sew_surface`, `idp_noedgewasstitched`, `236_gl_24_obshchie_sved_o_telah` | `ISurfaceSewer{Shells, Precision, CreateBody}`, `ISurfaceContainer.SurfaceSewers`; `o3d_SurfaceSewer=96`, `prSewSurface=20095` |
| SM-28.union_components | `cm_make_union_comps`, `908_107_3_bulevy_operacii_nad_d` | `IUnionComponents{Parts}`, `IModelContainer.UnionsComponents`; API5 `ksUnionComponentsDefinition{PartArray}`; `o3d_UnionComponents=64`, `prMakeUnionComps=20070` |
| SM-28.cut_components | `cm_make_mold_cavity` (+ раздел «Управление размерами полости») | `IMoldCavity{Parts, Scale, ScaleCentre}`, `IModelContainer.MoldCavities`; API5 `ksMoldCavityDefinition{PartArray}`; `o3d_MoldCavity=65`, `prMakeMoldCavity=20069` |
| SM-28.edit_in_place_context | `cm_3d_edit_component_out_place`, `local_detail_work_features`, `edit_local_detail`, `83_3_9_790_ispolnenija_modeli` | `IPart7{GetOpenDocumentParam, BeginEdit, EndEdit(Rebuild), IsLocal}`, `IOpenDocumentParam{Visible, ReadOnly, ApplyingIndex, Password}`, `IAssemblyDocument{DismantleMode}` |
| SM-29.* | `cm_make_deformation_component`, `object_deformation`, `deformation_features`, `cm_make_deformation_list`, `cm_deformation_componen_saveas`, `334_34_1_vybor_obwektov_dlja_de` | совпадений по имени «Deform» в kAPI7.tlb и в дампе API5 **нет**; в `ksConstants.tlb` есть `prMoveDeformation=10132/prRotateDeformation=10133/prScaleDeformation=10134`, но топик `make_deformation` относится к разделу «3. Черчение», то есть это 2D-команды; в `ksConstants3D.tlb` есть `ksDeformedSurface=24` (тип математической поверхности) |

## 3. Отдельно про ловушки атрибуции, которые я в себе нашёл

1. `cm_linear_array.html`, `cm_circular_array.html`, `cm_object_by_pattern.html`, `cm_objsymmetry.html`,
   `make_deformation.html` — **это страницы 2D/чертежного модуля** (хлебные крошки
   «3. Черчение. Оформление чертежей»), а не твердотельных массивов. Их легко спутать по имени файла.
   В каталоге зафиксированы только топики с хлебными крошками «2. Трехмерное моделирование».
   Если основной сеанс будет сопоставлять команды по `ProcessTypeEnum`, надо помнить, что
   `prChoose*Pattern`/`prBodies*Pattern` и `pr*Deformation` живут в разных предметных областях.
2. `o3d_meshCopy`(35) и `o3d_meshPartArray`(39) — **разные** o3d-типы одного UI-семейства
   (операции vs компоненты/сборка); `o3d_LinearPattern`, `o3d_CircularPattern`, `o3d_PathPattern`,
   `o3d_DerivedPattern`, `o3d_MirrorPattern` в установленном `ksObj3dTypeEnum` **отсутствуют** —
   имена с «Pattern» носят только PointDriven/Table/AnyCopy. Ошибочное ожидание «o3d_LinearPattern»
   привело бы к ложному выводу, что массива по сетке в API нет.
3. `IModelContainer` (33 члена в kAPI7.tlb) не содержит ни FaceMovers, ни FaceRemovers,
   ни BodySurfaceRemovers, ни SurfaceSewers: они в **`ISurfaceContainer`** (22 члена).
   Поиск только по IModelContainer дал бы ложный «blocked_api» для SM-24 и SM-27-сшивки.
4. Имена вендора с опечатками записаны дословно и не «исправлялись»: API5 `inverce` (вместо inverse),
   `sence`; API7 `Synhronise` (вместо Synchronise) и `GetExternalLocalCooordinateSystem`
   (удвоенная «o») в `ICopyGeometry`. Их нужно копировать в адаптеры как есть — это факт
   метаданных установленного приложения, а не моя транскрипция.
