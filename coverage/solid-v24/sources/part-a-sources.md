# P6.0 срез A (SM-04/05/06/10/11/12/13/14/15/16/17) — источники

Дата: 2026-09-11. Срез — документация + метаданные; **ни один COM-объект КОМПАС не запускался**
(CAD-сеансы в это время использовались основной сессией). Все «metadata_found» ниже означают
находку в библиотеке типов/констант установленного приложения либо в дампе prebuilt-interop,
а не исполнение.

## 1. Метаданные (что и откуда извлечено)

| Артефакт | Происхождение | Что даёт |
|---|---|---|
| `docs/compatibility/kompas-api5-metadata.json` | reflection-дамп `Libs\PolynomLib\Bin\Client\Interop.Kompas6API5.dll` (1007 типов, шаг P0.2) | члены API5-определений (ранний prebuilt-слой; отсутствие ≠ отсутствие продукта) |
| `scratch/api5-index.txt`, `scratch/tlgen/_0422828C_…py` | comtypes-генерация из **`Bin\kApi5.tlb`** установленного приложения | список interfaces/dispatch (0422828C = LIBID kApi5.tlb); members=0 у диспинтерфейсов — имена членов видны в interop-дампе и в IDispatch-мета-интерфейсе |
| `scratch/inventory/part-a/api5_tlb_docs_full.txt` | `tlb_help2.py` (офлайн `LoadTypeLibEx(REGKIND_NONE)`; тот же способ, что env-passport) | helpstrings API5-интерфейсов (рус.) |
| `scratch/api7-index.txt`, `scratch/tlgen/_69AC2981_…py` | comtypes-генерация из **`Bin\kAPI7.tlb`** (69AC2981 = его LIBID; 1498 typeinfo, 836 interfaces — сходится с env-passport) | полный список интерфейсов API7, vtable-порядок |
| `scratch/inventory/part-a/api7_family_blocks.txt` | блок-экспорт из api7-index.txt | члены/типы/параметры интересующих интерфейсов |
| `scratch/inventory/part-a/api7_tlb_docs_full.txt` | `tlb_help2.py kAPI7.tlb --ifaces …` | helpstrings API7 (рус.), свойства `IModelContainer` (33 члена) |
| `scratch/tlgen/_2CAF168C_…py` = **`Bin\ksConstants3D.tlb`**; `enum_values.txt`, `enums3d_all.txt` | comtypes + `tlb_help2.py --enums` | `ksObj3dTypeEnum` со значениями и рус. описаниями (`o3d_*`), все семейственные enum-типы со значениями (KSChooseType, ksOperationResultEnum, ksChamferBuildingTypeEnum, ksLoftBuildingType, ksEvolutionShiftSketchTypeEnum, ksEvolutionVersionEnum, ksPipeBuildingTypeEnum, ksPipeWallDirectionEnum, ksRibSideEnum, ksScalingTypeEnum, ksCutBuildingTypeEnum, ksCornerFormEnum, ksChooseBodiesType, ksChoosePartsType, ksFilletOffsetModeEnum, ksMultiThicknessGroupTypeEnum) |
| `docs/acceptance/api7/env-passport.md`, `api7-attribution.md` | прогон зонда API7 2026-09-11 (уже выполненный, не этим срезом) | SHA-256/датировки TLB и interop; живые dispids `IModelContainer` (Holes3D=10013, ElementaryBodies=10032, PipeElements=10033, Extrusions=10003, Sketchs=10002, Points3D=10004); факт «в prebuilt-обёртке PipeElements/ElementaryBodies нет, в TLB объявлены» |
| `scratch/interop_installed_api7.txt` vs `interop_vendor_api7.txt` | tlbimp от установленного TLB (2442 типа) vs вендорская обёртка (2298) | допустимость собственной генерации interop |
| `src/KompasMcp.Host/Catalog/ToolCatalog.cs` | основной код | baseline `mcp_support` (инструментов создания семейств среза нет) |

Использованные точные имена интерфейсов — в `entries.json` по каждому family/operation.
Скрипты извлечения (read-only): `tlb_help2.py`, `dump_block.py`, `dump_enums.py`,
`enum_sections.py`, `probe_meta.py` в этом каталоге.

## 2. Документация (web_fetch 2026-09-11, все URL вернулись 200 OK, если не указано иное)

### SDK (`help.ascon.ru/KOMPAS_SDK/24/ru-RU/`)
- `imodelcontainer_props.html` — полный список свойств контейнера (Evolutions/Lofts/PipeElements/Chamfers/Inclines/Ribs/Shells/Booleans/Cuts/SplitSolids/FullFillets/DraftsFromEdges/Scalings3D/BodyRepositions).
- `imodelcontainer_evolutions.html`, `ievolutions.html`, `ievolution.html`, `ievolution_propers.html`, `ievolutions_add.html` — IEvolution (v18); Add принимает `o3d_bossEvolution|o3d_cutEvolution|o3d_EvolutionSurface` (базового нет — OQ-A1).
- `iloft_propers.html` — свойства ILoft (совпадают с TLB).
- `iboolean.html` — «КОМПАС версия v18».
- `isplitsolid.html` — «Версия: Компас v20».
- `idraftfromedges.html` — «Версия КОМПАС v22».
- `ipipeelement.html` — «КOMPAS v24» (новый).
- Не найдены (404): `ievolution_props.html` (правильно `_propers`), `ipipeelement_props.html`.

### Справка пользователя (`help.ascon.ru/KOMPAS/24/ru-RU/`)
Обход дерева разделов: `3d_modeling.html` → `aw2020629.html` («Тела»), `fy996744.html` («Элементы тел. Редактирование») и цепочки prev/next (главное меню разделов JS-рендерится, child-списки не всегда статичны — обошли ссылочными цепочками).

| Семейство | Страницы |
|---|---|
| SM-04 | `261_gl_27_oper_kin.html`, `aw1443470.html`, `cm_base_evolution_solid.html`, `aw2207083.html`, `aw2209427.html` (сечение; тело-инструмент только Cut/Intersect, запреты ортогонального типа), `aw2263191.html` (траектория), `aw2303904.html` (3 типа движения + «Согласованно с нормалью»), `section_shape_changes.html` (законы/точки + «Разбивать на грани»), `checking_section_shape.html` |
| SM-05 | `266_gl_28_oper_po_sech.html`, `cm_base_loft_solid.html`, `parametry_operaciy_po_secheniyam.html`, `secheniya_elementa.html` (все замкнуты или все разомкнуты; точка как крайнее сечение), `268_28_4_osev_lin_elem_po_sech.html` (ориентирующая линия ≠ направляющие), `704_84_4_2_sposob_postroenija_t.html` (автоматически/по нормали/по объекту/купол + Коэффициент 1/2), `705_84_4_3_traektorija_soedinen.html` (замкнуть; цепочки), `cm_create_loft_coupling_take.html`, `direction_curves.html` (6 ограничений направляющих) |
| SM-06 | `pipe_element.html`, `pipe_element_general_information.html`, `cm_base_pipe_solid.html` (Результат: Объединение/Новое тело/Вычитание/Пересечение; Диаметр/Диаметр 1/Диаметр по объекту; Толщина стенки), `pipe_param.html`, `pipe_path.html` (типы траектории, «Выбирать части кривых», «Выбирать касательные объекты», замкнутая/разомкнутая, «Ломаная» в команде), `pipe_thickness.html` (Внутрь/Наружу; Диаметр 2) |
| SM-10 | `cm_3d_full_fillet.html` (три группы граней, авто-распределение по смежности, «По касательным граням», все грани одного тела/сшитой поверхности) |
| SM-11 | `cm_make_chamfer.html` (По стороне и углу / По двум сторонам; «Сменить направление»; «По касательным ребрам», порог 8,1°; «Допуск»), `angle_shape.html` (Обработка углов: Срезать/Не срезать), `stop_chamfer_filets.html` (остановки: % / длина сегмента / центральный угол; вершина/точка/плоскость; усечение по объекту) |
| SM-12 | `cm_make_draft.html` (Уклон от основания: Основание, Грани, Угол), `cm_draft_from_edges.html` (Уклон от базовой линии: Базовая линия, Базовое направление/Построить вектор, Сторона, Угол, Сменить направление), родитель `fy1465242.html`, обзор `fy1191643.html` |
| SM-13 | `cm_make_shell.html` (Удаляемые грани; Выбирать касательные грани; Наружу/Внутрь; Толщина; Переменная толщина), `fy1436893.html` (индивидуальная толщина: таблица Грани/Толщина, Произвольные толщины, Набор 1..N), родитель `742_88_5_tonkostennaja_obolochk.html` |
| SM-14 | `738_88_4_rebro_zhestkosti.html`, `cm_make_rib.html` (разомкнутая цепочка, достройка до ближайшей грани, Положение параллельно/перпендикулярно, Толщина + Симметричная, Уклон граней, Направление формирования), `740_88_4_3_polozhenie_rebra.html`, `fy1142545.html` |
| SM-15 | `730_87_5_buleva_operacija.html`, `cm_aggregate_oper.html` (Объединение/Вычитание/Пересечение; Базовый объект; Модифицирующие объекты; Сохранить копию базового/модифицирующих) |
| SM-16 | `748_glava89_otsechenie_chasti_d.html`, `cm_make_cut_oper.html` (секущие: плоскость/поверхность/грань/набор граней/тело/эскиз; Способ Контуром/Плоскостью; «Сменить направление»; Область применения), `rezultat_oper_v_zavisimosti_ot_s_o.html` (таблица результата по типу секущего объекта), `fy1438316.html`/`fy1438319.html`/`cm_3d_split_solid.html` (Разрезание: Секущие объекты, Область применения), `cm_choice_of_bodies.html` (сохраняемые тела; исключённые остаются в файле) |
| SM-17 | `783_glava88_masshtabirovanie.html`, `cm_ranging.html` (Равномерно/По осям СК; точка: Координаты/Точка привязки/Построить точку; Сохранить копии; Скрыть исходные кривые), `299_32_9_izmen_polozh_tela.html`, `fy1246804.html`, `cm_body_reposition_main.html` (Способ: Относительно СК / По объекту; Объекты; Сохранить копии) |
| Классификация границ | `aw2020629.html` child-ссылки («Придание толщины…» — SM-27, «Деталь-заготовка» — SM-26 и т.д.) |

Поиск альтернативных URL: `html.duckduckgo.com` — CAPTCHA (не работал); `bing.com/search` — мусор;
`lite.duckduckgo.com/lite` — сработал (оттуда найдены страницы уклонов). `sitemap.xml`/`toc.json` help.ascon.ru — 404.
Корневые/TOC-страницы справки рендерятся JS: обход выполнен по статическим prev/next-цепочкам.

## 3. Сверка ключевых терминов (справка ↔ метаданные)

| UI v24 | API5 (kApi5.tlb) | API7 (kAPI7.tlb) |
|---|---|---|
| «Элемент по траектории» (кинематическая) | `o3d_baseEvolution=45` «Кинематическая операция», `ksBaseEvolutionDefinition` | `IEvolution`/`IModelContainer.Evolutions` «Коллекция кинематических операций» |
| «Элемент по сечениям» | `o3d_baseLoft=30`, `ksBaseLoftDefinition` | `ILoft`/`Lofts` |
| «Трубчатый элемент» | только тип `o3d_PipeElement=673` | `IPipeElement`/`PipeElements` (v24) |
| «Полное скругление» | только тип `o3d_FullFillet=618` | `IFullFillet`/`FullFillets` |
| «Фаска» | `o3d_chamfer=33`, `ksChamferDefinition` | `IChamfer`/`Chamfers` |
| «Уклон от основания» | `o3d_incline=42`, `ksInclineDefinition` | `IIncline`/`Inclines` |
| «Уклон от базовой линии» | только тип `o3d_DraftFromEdges=644` | `IDraftFromEdges`/`DraftsFromEdges` (v22) |
| «Оболочка» | `o3d_shellOperation=43`, `ksShellDefinition` | `IShell`/`Shells` |
| «Ребро жесткости» | `o3d_ribOperation=44`, `ksRibDefinition` | `IRib`/`Ribs` |
| «Булева операция над телами» | `o3d_aggregate=69` «Булева операция» + `ksAggregateDefinition{BooleanType,BodyCollection}` (новое уточнение против старого SM-15 в каталоге!) | `IBoolean`/`Booleans` (v18) |
| «Сечение» | `o3d_cutByPlane=50` «сечение поверхностью», `o3d_cutBySketch=51`, два определения | `ICut`/`Cuts` (один интерфейс, BuildingType Контур/Плоскость, CutObjects[]) |
| «Разрезать» | только тип `o3d_SplitSolid=633` | `ISplitSolid`/`SplitSolids` (v20) |
| «Масштабирование» | только тип `o3d_Scaling3D=531` | `IScaling3D`/`Scalings3D` |
| «Изменение положения тела» | только тип `o3d_BodyReposition=569` | `IBodyReposition`/`BodyRepositions` |

## 4. Чего этот срез НЕ доказывает
- Ничего исполнением: `runtime_verified` не присвоен ни одной записи; список проб-кандидатов — `open-questions.md`.
- Живую реакцию `IDispatch` установленных TLB (объявление ≠ отклик; прецедент A7.4: `Holes3D` живой, `LocalCoordinateSystem` вернул null).
- Единицы полей (кроме объявленных в helpstring: `GetPathLength(ST_MIX_*)`); конвенции Angle — предположение градусы.
- Полноту enumerations у `c_long`-полей без собственного enum (ThinType, StopChamferOffsetMode, CentrePointBuildingType).
