# Соответствие MCP официальной документации КОМПАС-3D v24

Артефакт: `docs/API_COMPLIANCE.json` (машинный) и этот файл, ПОРОЖДЁННЫЙ из него. Вручную не правится: правится источник, потом перегенерируется.

Дата сборки аудита: 2026-10-20T07:20:05+02:00, часы машины +02:00.

## 1. Что измерено

- Целевая версия: КОМПАС-3D v24, сборка 24.0.0.2799, `KOMPAS.Exe` sha256 `13e9913bc8115828914abca3353060279a41043e3543354dbabf964f7fce6756`.
- Документация: https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/, прочитано по проводу; интерфейсов 141, маршрутов 640.
- Измеряемая сборка: `release_build`, коммит `026326f`, дерево ГРЯЗНОЕ; `KompasMcp.Host.dll` sha256 `33b24ab41a88ad430fe33bc1ba0b2555ca01a9a7def239744f48738ce1c841b3`.

## 2. Сверка полноты

| Показатель | Значение |
|---|---|
| tools_on_wire | 96 |
| schemas_on_disk | 96 |
| tools_with_schema | 96 |
| schemas_matching_wire_by_property_names | 96 |
| schemas_matching_wire_by_shape | 96 |
| matrix_rows | 187 |
| catalog_modes_and_variants | 210 |
| com_routes | 650 |
| com_routes_confirmed_by_documentation | 636 |
| com_routes_confirmed_over_wire | 636 |
| com_routes_confirmed_from_mirror | 0 |
| com_routes_unconfirmed | 4 |
| com_routes_confirmed_by_name | 6 |
| documentation_column_complete | True |
| documentation_routes_unmeasured | 0 |
| object_type_constants_checked | 11 |
| object_type_constants_mismatched | 0 |
| rows_claiming_verified | 183 |
| rows_verified_without_any_linked_row | 0 |
| rows_verified_contradicted_by_fail | 0 |
| rows_action_without_dedicated_row | 9 |
| rows_actions_without_required_calls | 12 |
| rows_status_lowered_by_unsupported_actions | 12 |
| acceptance_rows | 1734 |
| acceptance_calls_journal | 16704 |
| acceptance_rows_with_call_window | 1734 |
| required_of_mismatches_matrix_vs_profile | 6 |
| dependency_verdict_control_proved | None |

Инструментов без присутствия в записях покрытия: 0.
Инструментов вне профилей по природе (названный класс): 8 - `kompas_acquire_session` (захват сеанса вторым клиентом - владение сеансом, а не режим); `kompas_capabilities` (что реально поддержано на этой установке - служебный отчёт); `kompas_connect` (подключение к уже запущенному КОМПАС - владение сеансом, а не режим); `kompas_disconnect` (отсоединение от КОМПАС - владение сеансом, а не режим); `kompas_health` (состояние сервера (в том числе разрыв связи) - служебный отчёт); `kompas_probe_units` (калибровка единиц: строит известную геометрию и отдаёт СЫРЫЕ показания измерительных вызовов; измерительный прибор приёмки, а не режим (исполняется строкой DEP.DUN.01.geometry_validation)); `kompas_release_session` (освобождение сеанса - владение сеансом, а не режим); `kompas_session_status` (состояние сеанса и владельца - владение сеансом, а не режим).
Классы присутствия: {'absent': 8, 'route_field': 38, 'route_field+schema_field': 33, 'schema_field': 17}.
COM-маршрутов по сборкам: {'api7': 382, 'api5': 258, 'adapter-twins': 10}.

## 3. Инструменты

| Инструмент | Свойств | Обязательных | Схема совпала по именам | по форме | Присутствие |
|---|---|---|---|---|---|
| `kompas_acquire_session` | 1 | - | да | да | нет |
| `kompas_add_dimension` | 10 | document_id, expected_revision, view_ref, dimension_type, point1, operation_id | да | да | route_field+schema_field |
| `kompas_bind_parameter` | 8 | document_id, expected_revision, feature_ref, parameter_name, expression, operation_id | да | да | route_field+schema_field |
| `kompas_boolean` | 9 | target_body_ref, tool_body_refs, operation, keep_tools, document_id, expected_revision, operation_id | да | да | schema_field |
| `kompas_capabilities` | 1 | - | да | да | нет |
| `kompas_chamfer` | 10 | edge_refs, expected_revision, mode, distance1_mm, operation_id | да | да | schema_field |
| `kompas_check_component_links` | 2 | document_id | да | да | route_field |
| `kompas_check_interference` | 5 | document_id | да | да | route_field+schema_field |
| `kompas_close_document` | 4 | document_id, dirty_policy, operation_id | да | да | route_field |
| `kompas_connect` | 5 | mode, make_visible, operation_id | да | да | нет |
| `kompas_create_aux_geometry` | 20 | document_id, expected_revision, kind, mode, operation_id | да | да | route_field |
| `kompas_create_document` | 6 | application_id, kind, operation_id | да | да | route_field |
| `kompas_create_drawing_views` | 12 | document_id, expected_revision, source_path, x, y, operation_id | да | да | route_field+schema_field |
| `kompas_create_mate` | 17 | document_id, expected_revision, constraint_type, first_component_ref, first_face_index, second_component_ref, second_face_index, operation_id | да | да | route_field+schema_field |
| `kompas_create_section_view` | 16 | document_id, expected_revision, base_view_ref, mode, x, y, operation_id | да | да | route_field+schema_field |
| `kompas_create_sketch` | 6 | document_id, expected_revision, plane, operation_id | да | да | schema_field |
| `kompas_create_sketch_constraint` | 14 | sketch_ref, expected_revision, constraint_type, object_ref, operation_id | да | да | route_field+schema_field |
| `kompas_create_sketch_dimension` | 10 | sketch_ref, expected_revision, object_ref, operation_id | да | да | schema_field |
| `kompas_create_variable` | 9 | document_id, expected_revision, name, value, operation_id | да | да | route_field+schema_field |
| `kompas_cut_by_plane` | 8 | target_body_ref, plane, keep_side, document_id, expected_revision, operation_id | да | да | schema_field |
| `kompas_delete_component` | 6 | document_id, expected_revision, component_ref, operation_id | да | да | route_field |
| `kompas_delete_feature` | 6 | feature_ref, expected_revision, operation_id | да | да | route_field+schema_field |
| `kompas_delete_mate` | 5 | document_id, expected_revision, mate_ref, operation_id | да | да | route_field |
| `kompas_delete_section_view` | 6 | document_id, expected_revision, view_ref, operation_id | да | да | route_field |
| `kompas_delete_sketch_constraint` | 7 | sketch_ref, expected_revision, object_ref, operation_id | да | да | schema_field |
| `kompas_disconnect` | 4 | application_id, close_owned_application, operation_id | да | да | нет |
| `kompas_draft` | 10 | document_id, face_refs, angle_deg, direction, expected_revision, operation_id | да | да | route_field+schema_field |
| `kompas_draft_from_edges` | 11 | document_id, base_line_edge_refs, angle_deg, expected_revision, operation_id | да | да | route_field+schema_field |
| `kompas_edit_sketch` | 6 | sketch_ref, expected_revision, mode, entities, operation_id | да | да | route_field+schema_field |
| `kompas_edit_sketch_entity` | 7 | sketch_ref, expected_revision, address, action, operation_id | да | да | route_field |
| `kompas_edit_view` | 8 | document_id, expected_revision, view_ref, operation_id | да | да | route_field |
| `kompas_export_drawing` | 6 | document_id, output_path, format, operation_id | да | да | route_field+schema_field |
| `kompas_export_image` | 12 | document_id, expected_revision, format, operation_id | да | да | route_field+schema_field |
| `kompas_export_step` | 5 | document_id, expected_revision, output_path, operation_id | да | да | route_field |
| `kompas_export_stl` | 8 | document_id, expected_revision, target_path, binary, max_edge_length_mm, normal_angle_deg, operation_id | да | да | route_field+schema_field |
| `kompas_extrude` | 11 | sketch_ref, expected_revision, operation, operation_id | да | да | route_field+schema_field |
| `kompas_fillet` | 6 | edge_refs, expected_revision, radius_mm, operation_id | да | да | schema_field |
| `kompas_finish_sketch` | 4 | sketch_ref, require_closed_profile, operation_id | да | да | route_field |
| `kompas_get_context` | 3 | document_id, detail | да | да | route_field |
| `kompas_get_feature` | 2 | feature_ref | да | да | route_field+schema_field |
| `kompas_get_material` | 2 | document_id | да | да | route_field+schema_field |
| `kompas_get_pattern` | 2 | feature_ref | да | да | route_field |
| `kompas_get_section_view` | 3 | document_id, view_ref | да | да | route_field |
| `kompas_get_sketch_status` | 2 | sketch_ref | да | да | route_field+schema_field |
| `kompas_get_technical_demand` | 2 | document_id | да | да | route_field |
| `kompas_get_title_block` | 3 | document_id, cell_ids | да | да | route_field |
| `kompas_health` | 2 | detail | да | да | нет |
| `kompas_hole` | 14 | face_ref, expected_revision, mode, diameter_mm, operation_id | да | да | schema_field |
| `kompas_import_step` | 6 | application_id, input_path, desired_kind, operation_id | да | да | route_field+schema_field |
| `kompas_insert_component` | 7 | document_id, expected_revision, source_path, operation_id | да | да | route_field |
| `kompas_list_aux_geometry` | 5 | document_id, include | да | да | route_field |
| `kompas_list_bodies` | 2 | document_id | да | да | route_field |
| `kompas_list_components` | 3 | document_id | да | да | route_field |
| `kompas_list_dimensions` | 4 | document_id, view_ref | да | да | route_field |
| `kompas_list_documents` | 2 | application_id | да | да | route_field |
| `kompas_list_drawing_views` | 2 | document_id | да | да | route_field+schema_field |
| `kompas_list_feature_parameters` | 3 | document_id, feature_ref | да | да | route_field+schema_field |
| `kompas_list_features` | 2 | document_id | да | да | route_field |
| `kompas_list_mates` | 2 | document_id | да | да | route_field |
| `kompas_list_sketch_constraints` | 4 | sketch_ref | да | да | route_field+schema_field |
| `kompas_list_sketch_entities` | 5 | sketch_ref | да | да | route_field |
| `kompas_list_sketches` | 2 | document_id | да | да | route_field+schema_field |
| `kompas_list_variables` | 3 | document_id | да | да | route_field+schema_field |
| `kompas_loft` | 9 | document_id, section_refs, expected_revision, operation_id | да | да | schema_field |
| `kompas_measure` | 5 | target_ref, properties | да | да | route_field+schema_field |
| `kompas_measure_gap` | 4 | document_id, object1, object2 | да | да | route_field+schema_field |
| `kompas_open_document` | 5 | application_id, path, access, operation_id | да | да | route_field |
| `kompas_pattern_circular` | 25 | document_id, expected_revision, copy_kind, source_refs, axis_point1_mm, axis_point2_mm, count2, step2_deg, operation_id | да | да | schema_field |
| `kompas_pattern_grid` | 28 | document_id, expected_revision, copy_kind, source_refs, axis1_point1_mm, axis1_point2_mm, step1_mm, count1, operation_id | да | да | schema_field |
| `kompas_pattern_mirror` | 15 | document_id, expected_revision, mode, source_refs, plane, save_initial_objects, operation_id | да | да | schema_field |
| `kompas_probe_units` | 5 | application_id, known_length_mm, known_radius_mm, operation_id | да | да | нет |
| `kompas_read_topology` | 4 | document_id, body_ref, include | да | да | route_field |
| `kompas_rebuild` | 3 | document_id, operation_id | да | да | route_field |
| `kompas_rebuild_drawing_views` | 5 | document_id, expected_revision, view_ref, operation_id | да | да | route_field |
| `kompas_release_session` | 3 | - | да | да | нет |
| `kompas_replace_component` | 6 | document_id, expected_revision, component_ref, source_path, operation_id | да | да | route_field |
| `kompas_reposition` | 11 | target_body_ref, kind, document_id, expected_revision, operation_id | да | да | schema_field |
| `kompas_resolve_selection` | 5 | body_ref, predicate, require_unique, limit | да | да | route_field |
| `kompas_rotated` | 12 | sketch_ref, expected_revision, operation, angle_deg, axis_point1_mm, axis_point2_mm, operation_id | да | да | schema_field |
| `kompas_save_document` | 5 | document_id, expected_revision, operation_id | да | да | route_field |
| `kompas_session_status` | 1 | - | да | да | нет |
| `kompas_set_component_placement` | 6 | document_id, expected_revision, component_ref, transform, operation_id | да | да | route_field |
| `kompas_set_feature_suppressed` | 6 | feature_ref, expected_revision, suppressed, operation_id | да | да | route_field+schema_field |
| `kompas_set_mate_fixed` | 6 | document_id, expected_revision, mate_ref, fixed, operation_id | да | да | route_field |
| `kompas_set_mate_parameter` | 6 | document_id, expected_revision, mate_ref, param_value, operation_id | да | да | route_field |
| `kompas_set_material` | 6 | document_id, expected_revision, material_name, density_kg_per_m3, operation_id | да | да | route_field+schema_field |
| `kompas_set_sketch_plane` | 6 | document_id, sketch_ref, expected_revision, plane, operation_id | да | да | route_field |
| `kompas_set_technical_demand` | 5 | document_id, expected_revision, text, operation_id | да | да | route_field+schema_field |
| `kompas_set_title_block` | 5 | document_id, expected_revision, cells, operation_id | да | да | route_field+schema_field |
| `kompas_set_variable` | 7 | document_id, expected_revision, name, operation_id | да | да | route_field+schema_field |
| `kompas_shell` | 9 | document_id, face_refs, thickness_mm, expected_revision, operation_id | да | да | schema_field |
| `kompas_split` | 7 | target_body_ref, plane, document_id, expected_revision, operation_id | да | да | schema_field |
| `kompas_sweep` | 7 | sketch_ref, path_ref, expected_revision, operation_id | да | да | schema_field |
| `kompas_update_feature` | 45 | feature_ref, expected_revision, operation_id | да | да | route_field+schema_field |
| `kompas_update_plane` | 9 | document_id, expected_revision, plane_ref, operation_id | да | да | route_field |
| `kompas_update_section_view` | 11 | document_id, expected_revision, view_ref, operation_id | да | да | route_field |

## 4. Режимы и варианты каталога

Всего строк матрицы: 187.

| Операция | Семейство | Уровень каталога | Инструменты | Маршрут | Документация |
|---|---|---|---|---|---|
| `SM-02.base_extrusion.blind` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_baseExtrusion(24) + SetSideParam(etBlind=0) | Подтверждено |
| `SM-02.cut_extrusion.through` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_cutExtrusion(26) + chooseType=3 + ChooseBodies().Bo | Подтверждено |
| `SM-09.fillet.constant_radius_body_edges` | SM-09 | mcp_verified | kompas_fillet | API5 o3d_fillet(34) + ksFilletDefinition{radius,tangent,arra | Не подтверждено |
| `SM-07.native_hole.through_cylindrical` | SM-07 | mcp_verified | kompas_hole | API7 IHoles3D.Add() + IHoleDisposal.BaseSurface(грань) + ksH | Подтверждено |
| `SM-01.primitive_box` | SM-01 | metadata_found | - | API7 IModelContainer.ElementaryBodies (подтвердить типом уст | Не подтверждено |
| `AUX-SKETCH.plane_and_profile_lifecycle` | AUX-SKETCH | mcp_verified | kompas_create_sketch, kompas_edit_sketch, kompas_finish_sketch, kompas_read_topology, kompas_set_sketch_plane | API5: GetDefaultEntity(1/2/3), NewEntity(o3d_planeOffset=14) | Не подтверждено |
| `SM-02.base_extrusion.additional_body` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_baseExtrusion(24) вторым независимым контуром; choo | Подтверждено |
| `SM-02.base_extrusion.direction_negative` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_baseExtrusion(24) + directionType=1 (dtReverse) + S | Подтверждено |
| `SM-02.boss_extrusion.blind` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_bossExtrusion(25) + chooseType=3 + ChooseBodies().B | Подтверждено |
| `SM-02.boss_extrusion.explicit_target_body` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_bossExtrusion(25) + chooseType=3 + ChooseBodies().B | Подтверждено |
| `SM-02.cut_extrusion.blind` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_cutExtrusion(26) + chooseType=3 + ChooseBodies().Bo | Подтверждено |
| `SM-02.cut_extrusion.explicit_target_body` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_cutExtrusion(26) + chooseType=3 + ChooseBodies().Bo | Подтверждено |
| `SM-02.cut_extrusion.direction_symmetric` | SM-02 | mcp_verified | kompas_extrude | API5 o3d_cutExtrusion(26) + directionType=2 (dtBoth) + два S | Подтверждено |
| `SM-03.base_rotated.full_turn` | SM-03 | mcp_verified | kompas_rotated | API7 Api7Rotated.TryCreate: IModelContainer.Rotateds.Add(o3d | Не подтверждено |
| `SM-03.base_rotated.partial_angle` | SM-03 | mcp_verified | kompas_rotated | тот же маршрут, что у полного оборота; угол пишется в Angle[ | Подтверждено |
| `SM-03.base_rotated.axis_explicit` | SM-03 | mcp_verified | kompas_rotated | ось подаётся ДВУМЯ точками контракта (axis_point1_mm, axis_p | Не подтверждено |
| `SM-03.boss_rotated.full_turn` | SM-03 | mcp_verified | kompas_rotated | API7 Api7Rotated.TryCreate: IModelContainer.Rotateds.Add(o3d | Не подтверждено |
| `SM-03.cut_rotated.full_turn` | SM-03 | mcp_verified | kompas_rotated | API7 Api7Rotated.TryCreate: IModelContainer.Rotateds.Add(o3d | Не подтверждено |
| `SM-04.base.mode_orthogonal` | SM-04 | mcp_verified | kompas_sweep | тот же маршрут, что у SM-04.base.single_profile_flat_path; р | Не подтверждено |
| `SM-04.boss` | SM-04 | metadata_found | - | - | Не подтверждено |
| `SM-05.base.mode_couplings` | SM-05 | mcp_verified | kompas_loft | тот же маршрут API7; цепочки — ILoft.AddCoupling() → ICoupli | Не подтверждено |
| `SM-07.native_hole.blind_flat_bottom` | SM-07 | mcp_verified | kompas_hole | API7 IHoles3D.Add() + IHoleDisposal.BaseSurface(грань) + ksH | Подтверждено |
| `SM-07.native_hole.through_counterbore` | SM-07 | mcp_verified | kompas_hole | API7 IHoles3D.Add() + ksHTCounterbore + DepthType=ksDTReachT | Подтверждено |
| `SM-07.native_hole.through_countersink` | SM-07 | mcp_verified | kompas_hole | API7 IHoles3D.Add() + ksHTCountersinking + DepthType=ksDTRea | Подтверждено |
| `SM-07.native_hole.axis_by_face_normal` | SM-07 | mcp_verified | kompas_hole | IHoleDisposal.BaseSurface(перенесённая опорная грань) + IHol | Подтверждено |
| `SM-07.native_hole.position_off_origin` | SM-07 | mcp_verified | kompas_hole | IHoleDisposal.Point3DParamSurface → IPoint3DParamSurface, Of | Не подтверждено |
| `SM-09.fillet.read_radius_back` | SM-09 | mcp_verified | kompas_get_feature | API7 IFillet.Radius1/Radius2/Tangent/BuildingType/BaseObject | Подтверждено |
| `SM-09.fillet.change_radius_in_place` | SM-09 | mcp_verified | kompas_update_feature | API7 IFillet.Radius1 на живой модели: признак API5 сопоставл | Не подтверждено |
| `SM-09.fillet.change_edge_set` | SM-09 | mcp_verified | kompas_update_feature | API7: IModelContainer.Fillets[i] -> IFillet.BaseObjects (чте | Не подтверждено |
| `SM-11.chamfer.mode_two_distances` | SM-11 | mcp_verified | kompas_chamfer | API5 NewEntity(o3d_chamfer=33) + ksChamferDefinition.SetCham | Подтверждено |
| `SM-11.chamfer.mode_distance_angle` | SM-11 | mcp_verified | kompas_chamfer | API7 IModelContainer.Chamfers.Add() + BaseObjects=object[] п | Не подтверждено |
| `SM-11.chamfer.mode_direction` | SM-11 | mcp_verified | kompas_chamfer | API5 SetChamferParam(transfer=direction,…) и API7 IChamfer.D | Подтверждено |
| `SM-13.shell.mode_remove_faces` | SM-13 | mcp_verified | kompas_read_topology, kompas_shell | API5: NewEntity(43 = o3d_shellOperation) → ksShellDefinition | Не подтверждено |
| `SM-13.shell.mode_direction` | SM-13 | mcp_verified | kompas_shell | тот же маршрут API5; направление стенки — ksShellDefinition. | Подтверждено |
| `SM-15.subtract` | SM-15 | mcp_verified | kompas_boolean | API7: IModelContainer (TransferInterface(document, ksAPI7Dua | Не подтверждено |
| `SM-15.intersect` | SM-15 | mcp_verified | kompas_boolean | API7: IModelContainer (TransferInterface(document, ksAPI7Dua | Не подтверждено |
| `SM-15.union.mode_save_tools` | SM-15 | mcp_verified | kompas_boolean | API7: IModelContainer (TransferInterface(document, ksAPI7Dua | Не подтверждено |
| `SM-15.union.mode_multi_tools` | SM-15 | mcp_verified | kompas_boolean | API7: IModelContainer (TransferInterface(document, ksAPI7Dua | Не подтверждено |
| `SM-16.split.mode_keep_parts` | SM-16 | mcp_verified | kompas_split | API7: container.SplitSolids.Add() → QI(ISplitSolid) → CutObj | Не подтверждено |
| `SM-16.cut_by_plane.mode_side_removed` | SM-16 | mcp_verified | kompas_cut_by_plane | API7: container.Cuts.Add() → QI(ICut) → BuildingType = ksCut | Не подтверждено |
| `SM-17.reposition.translate_by_vector` | SM-17 | mcp_verified | kompas_reposition | API7: container.BodyRepositions.Add() → QI(IBodyReposition)  | Не подтверждено |
| `SM-17.reposition.rotate_about_axis` | SM-17 | mcp_verified | kompas_reposition | API7: container.BodyRepositions.Add() → QI(IBodyReposition)  | Не подтверждено |
| `SM-18.grid.single_row` | SM-18 | mcp_verified | kompas_pattern_grid | API7 IModelContainer.FeaturePatterns.Add(o3d_meshCopy=35) →  | Подтверждено |
| `SM-18.grid.rectangular_grid` | SM-18 | mcp_verified | kompas_pattern_grid | то же, плюс второе направление: Axis2/Angle2, Count2/Step2.  | Подтверждено |
| `SM-18.grid.operations` | SM-18 | mcp_verified | kompas_pattern_grid | то же, copy_kind=operations: копии наследуют область примене | Не подтверждено |
| `SM-18.grid.bodies` | SM-18 | mcp_verified | kompas_pattern_grid | FeaturePatterns.Add(o3d_BodiesMeshCopy=528) → QI(ILinearPatt | Подтверждено |
| `SM-19.circular.full_circle_no_duplicate` | SM-19 | mcp_verified | kompas_pattern_circular | FeaturePatterns.Add(o3d_circularCopy=36) → QI(ICircularPatte | Подтверждено |
| `SM-19.circular.angular_range` | SM-19 | mcp_verified | kompas_pattern_circular | то же; Count2 × Step2 < 360° даёт сектор, а не полный круг.  | Подтверждено |
| `SM-19.circular.operations` | SM-19 | mcp_verified | kompas_pattern_circular | то же, copy_kind=operations. В дереве — тип 36. | Не подтверждено |
| `SM-19.circular.bodies` | SM-19 | mcp_verified | kompas_pattern_circular | FeaturePatterns.Add(o3d_BodiesCircularCopy=529) → QI(ICircul | Подтверждено |
| `SM-23.mirror_all.bodies_selection` | SM-23 | mcp_verified | kompas_pattern_mirror | FeaturePatterns.Add(o3d_mirrorAllOperation=49) → QI(IMirrorP | Подтверждено |
| `SM-15.union.mode_save_base_copy` | SM-15 | metadata_found | - | - | Не подтверждено |
| `dep.sketch.entities` | AUX-SKETCH | None | kompas_edit_sketch_entity, kompas_list_sketch_entities | в API5 перечисления объектов эскиза нет (P2.6), поэтому адре | Подтверждено |
| `dep.refs.planes` | AUX-SKETCH | None | kompas_list_aux_geometry, kompas_update_plane | G07 (знаки XZ/YZ) через MCP не подтверждена ПОПРАВКА 21.09.2 | Подтверждено |
| `dep.refs.axes` | AUX-SKETCH | None | kompas_create_aux_geometry, kompas_list_aux_geometry | Ось по двум точкам создаётся `kompas_create_aux_geometry` (` | Подтверждено |
| `dep.refs.points_axes` | AUX-SKETCH | None | kompas_list_aux_geometry | попытка задать позицию отверстия точкой AssociationVertex из | Подтверждено |
| `dep.refs.planar_face` | AUX-SKETCH | None | kompas_read_topology | - | Не подтверждено |
| `dep.refs.edges_faces` | AUX-SKETCH | None | kompas_read_topology | - | Не подтверждено |
| `dep.paths.flat` | AUX-SKETCH | None | - | - | Не подтверждено |
| `dep.selection.unambiguous` | AUX-SKETCH | None | kompas_resolve_selection | G05 (неоднозначный выбор) не проверялась вообще | Не подтверждено |
| `dep.lifecycle.feature_cycle` | SM-30 | None | kompas_delete_feature, kompas_set_feature_suppressed | API5 ksFeature.excluded и ksDocument3D.DeleteObject, измерен | Подтверждено |
| `dep.bodies.multibody` | SM-02 | None | kompas_measure, kompas_read_topology | - | Не подтверждено |
| `dep.pattern.associativity` | SM-18 | None | kompas_get_pattern | OQ-B-01: фактическое поведение при правке источника не прове | Не подтверждено |
| `dep.units.confirm` | dep | None | - | - | Не подтверждено |
| `dep.api7.in_same_adapter` | dep | None | - | Api7Bridge — единственная точка входа в API7 в src/: Applica | Подтверждено |
| `dep.foundation` | dep | None | kompas_get_context | - | Подтверждено |
| `dep.preserve_unknown` | SM-30 | None | - | - | Не подтверждено |
| `SM-04.base.single_profile_flat_path` | SM-04 | mcp_verified | kompas_sweep | API5: NewEntity(45 = o3d_baseEvolution) → ksBaseEvolutionDef | Не подтверждено |
| `SM-05.base.parallel_sections_order` | SM-05 | mcp_verified | kompas_loft | API7: IModelContainer.Lofts.Add(o3d_bossLoft = 31) → ILoft.S | Не подтверждено |
| `SM-13.shell.uniform_thickness` | SM-13 | mcp_verified | kompas_shell | тот же маршрут API5; толщина — ksShellDefinition.thickness,  | Подтверждено |
| `SM-15.union.explicit_target_and_tools` | SM-15 | mcp_verified | kompas_boolean | API7: IModelContainer (TransferInterface(document, ksAPI7Dua | Не подтверждено |
| `SM-16.split.plane_tool` | SM-16 | mcp_verified | kompas_split | API7: container.SplitSolids.Add() → QI(ISplitSolid) → CutObj | Не подтверждено |
| `SM-23.mirror_array.selected_operations` | SM-23 | mcp_verified | kompas_pattern_mirror | FeaturePatterns.Add(o3d_mirrorOperation=48) → QI(IMirrorPatt | Подтверждено |
| `AUX-IMAGE.raster_export` | AUX-IMAGE | mcp_verified | kompas_export_image | API5: ksDocument3D.RasterFormatParam() -> ksRasterFormatPara | Подтверждено |
| `ASM-01.document.create_open` | ASM | mcp_verified | kompas_close_document, kompas_create_document, kompas_get_context, kompas_list_documents, kompas_open_document, kompas_save_document | kompas_create_document(kind=assembly) / kompas_open_document | Подтверждено |
| `ASM-02.component.insert` | ASM | mcp_verified | kompas_insert_component, kompas_list_components, kompas_measure | kompas_insert_component: API7 IPart7.Parts → IParts7.AddFrom | Подтверждено |
| `ASM-03.structure.read` | ASM | mcp_verified | kompas_check_component_links, kompas_list_components, kompas_open_document, kompas_save_document | kompas_list_components: API7 IAssemblyDocument.TopPart → IPa | Не подтверждено |
| `ASM-04.placement.set_read` | ASM | mcp_verified | kompas_list_components, kompas_list_documents, kompas_measure, kompas_open_document, kompas_save_document, kompas_set_component_placement | kompas_set_component_placement: API5 ksDocument3D.DefaultPla | Не подтверждено |
| `ASM-05.component.replace` | ASM | mcp_verified | kompas_list_components, kompas_list_documents, kompas_measure, kompas_open_document, kompas_replace_component, kompas_save_document | kompas_replace_component: ksPart.fileName (kspart_setfilenam | Не подтверждено |
| `ASM-06.links.check` | ASM | mcp_verified | kompas_check_component_links, kompas_open_document, kompas_save_document | kompas_check_component_links: API7 IPart7.FileName + IPart7. | Подтверждено |
| `ASM-07.save_reopen` | ASM | mcp_verified | kompas_close_document, kompas_get_context, kompas_list_documents, kompas_measure, kompas_open_document, kompas_save_document | kompas_save_document / kompas_open_document / kompas_close_d | Подтверждено |
| `dep.assembly.document_lifecycle` | ASM | mcp_verified | kompas_create_document, kompas_get_context, kompas_list_components, kompas_list_documents | kompas_list_components: API7 IAssemblyDocument.TopPart → IPa | Подтверждено |
| `dep.assembly.source_file` | ASM | mcp_verified | kompas_list_components | kompas_list_components: API7 IAssemblyDocument.TopPart → IPa | Подтверждено |
| `dep.assembly.component_address` | ASM | mcp_verified | kompas_list_components, kompas_list_documents, kompas_measure, kompas_open_document, kompas_save_document | kompas_list_components: API7 IAssemblyDocument.TopPart → IPa | Подтверждено |
| `dep.assembly.revisions` | ASM | mcp_verified | kompas_get_context, kompas_list_components, kompas_list_documents, kompas_open_document, kompas_save_document, kompas_set_component_placement | kompas_list_components: API7 IAssemblyDocument.TopPart → IPa | Подтверждено |
| `dep.assembly.idempotency` | ASM | mcp_verified | kompas_insert_component, kompas_list_components, kompas_list_documents | kompas_list_components: API7 IAssemblyDocument.TopPart → IPa | Подтверждено |
| `MATE-01.mate.create` | MATE | mcp_verified | kompas_create_mate, kompas_list_documents, kompas_list_mates, kompas_measure | kompas_create_mate: IPart7.MateConstraints → IMateConstraint | Не подтверждено |
| `MATE-02.mate.read` | MATE | mcp_verified | kompas_list_documents, kompas_list_mates | kompas_list_mates: ksDocument3D.MateConstraintCollection → G | Подтверждено |
| `MATE-03.mate.param_edit` | MATE | mcp_verified | kompas_list_documents, kompas_list_mates, kompas_measure, kompas_set_mate_parameter | kompas_set_mate_parameter: IMateConstraint3D.ParamValue = …  | Не подтверждено |
| `MATE-04.mate.fixed` | MATE | mcp_verified | kompas_list_documents, kompas_list_mates, kompas_set_mate_fixed | kompas_set_mate_fixed: IMateConstraint3D.Fixed = ksMateFixed | Подтверждено |
| `MATE-05.mate.delete` | MATE | mcp_verified | kompas_delete_mate, kompas_list_documents, kompas_list_mates | kompas_delete_mate: ksDocument3D.RemoveMateConstraint(constr | Подтверждено |
| `MATE-06.mate.placement_effect` | MATE | mcp_verified | kompas_list_components, kompas_list_documents, kompas_list_mates, kompas_measure, kompas_open_document, kompas_save_document | kompas_list_components: ksPlacement.GetMatrix3D по компонент | Не подтверждено |
| `MATE-07.mate.create_transmission` | MATE | mcp_verified | kompas_create_mate, kompas_list_documents, kompas_list_mates, kompas_open_document, kompas_save_document | kompas_create_mate, constraint_type=transmission: IPart7.Mat | Не подтверждено |
| `dep.mate.object_address` | dep | mcp_verified | kompas_list_documents, kompas_list_mates | см. строки режимов MATE-01…MATE-06 | Не подтверждено |
| `dep.mate.component_insert` | dep | mcp_verified | kompas_insert_component, kompas_list_mates, kompas_measure | см. строки режимов MATE-01…MATE-06 | Не подтверждено |
| `dep.mate.revisions` | dep | mcp_verified | kompas_list_mates, kompas_set_mate_parameter | см. строки режимов MATE-01…MATE-06 | Не подтверждено |
| `dep.mate.idempotency` | dep | mcp_verified | kompas_create_mate, kompas_list_mates | см. строки режимов MATE-01…MATE-06 | Не подтверждено |
| `dep.mate.save_reopen` | dep | mcp_verified | kompas_list_mates | см. строки режимов MATE-01…MATE-06 | Не подтверждено |
| `DRW-01.views.create_standard` | DRW | mcp_verified | kompas_create_drawing_views, kompas_edit_view, kompas_list_drawing_views, kompas_rebuild_drawing_views | kompas_create_drawing_views: API7 IDrawingDocument.ViewsAndL | Подтверждено |
| `DRW-02.views.list` | DRW | mcp_verified | kompas_list_drawing_views | kompas_list_drawing_views: API7 IViews.View / ViewByNumber / | Подтверждено |
| `DRW-03.dimension.add` | DRW | mcp_verified | kompas_add_dimension, kompas_list_dimensions | kompas_add_dimension: API7 IView как ISymbols2DContainer → L | Не подтверждено |
| `DRW-04.title_block.set` | DRW | mcp_verified | kompas_get_title_block, kompas_open_document, kompas_save_document, kompas_set_title_block | kompas_set_title_block: API7 ILayoutSheet.Stamp → IStamp.get | Не подтверждено |
| `DRW-05.export` | DRW | mcp_verified | kompas_export_drawing | kompas_export_drawing: API7 IApplication.get_Converter(Type. | Подтверждено |
| `dep.drawing.document_lifecycle` | DRW | None | - | - | Не подтверждено |
| `dep.drawing.source_file` | DRW | None | kompas_create_drawing_views, kompas_list_drawing_views | - | Подтверждено |
| `dep.drawing.view_address` | DRW | None | kompas_list_drawing_views | - | Подтверждено |
| `dep.drawing.revisions` | DRW | None | kompas_get_context | - | Подтверждено |
| `dep.drawing.idempotency` | DRW | None | kompas_list_drawing_views | - | Подтверждено |
| `dep.drawing.export_file` | DRW | None | kompas_export_drawing | - | Подтверждено |
| `DRW-06.technical_demand` | DRW | mcp_verified | kompas_get_technical_demand, kompas_open_document, kompas_save_document, kompas_set_technical_demand | kompas_set_technical_demand: API7 IDrawingDocument.Technical | Не подтверждено |
| `DRW-03.dimension.add.linear` | DRW | mcp_verified | kompas_add_dimension | kompas_add_dimension: API7 IView как ISymbols2DContainer → L | Не подтверждено |
| `DRW-03.dimension.add.radial` | DRW | mcp_verified | kompas_add_dimension | kompas_add_dimension: API7 IView как ISymbols2DContainer → L | Не подтверждено |
| `DRW-03.dimension.add.diametral` | DRW | mcp_verified | kompas_add_dimension | kompas_add_dimension: API7 IView как ISymbols2DContainer → L | Не подтверждено |
| `VM-01.variables.read` | VM | mcp_implemented | kompas_list_variables | kompas_list_variables: ksPart.VariableCollection → ksVariabl | Подтверждено |
| `VM-02.variable.set_value` | VM | mcp_implemented | kompas_list_variables, kompas_measure, kompas_open_document, kompas_save_document, kompas_set_variable | kompas_set_variable (mode=value): проверка документа, ревизи | Подтверждено |
| `VM-03.variable.set_expression` | VM | mcp_implemented | kompas_list_variables, kompas_measure, kompas_open_document, kompas_save_document, kompas_set_variable | kompas_set_variable (mode=expression): запись ksVariable.Exp | Подтверждено |
| `VM-04.material.read` | VM | mcp_verified | kompas_get_material | kompas_get_material: ksPart.material (свойство ТОЛЬКО ДЛЯ ЧТ | Подтверждено |
| `VM-05.material.write` | VM | mcp_verified | kompas_get_material, kompas_open_document, kompas_save_document, kompas_set_material | kompas_set_material: ksPart.SetMaterial(name, плотность в до | Не подтверждено |
| `VM-06.mass.computed_via_measure` | VM | mcp_verified | kompas_get_material, kompas_measure | kompas_measure получает плотность ОТ ВЫЗЫВАЮЩЕГО (параметр d | Не подтверждено |
| `dep.vm.part_addressing` | dep | None | - | - | Не подтверждено |
| `dep.vm.revisions` | dep | None | kompas_set_material | - | Подтверждено |
| `dep.vm.idempotency` | dep | None | kompas_get_material | - | Подтверждено |
| `dep.vm.save_reopen` | dep | None | - | - | Не подтверждено |
| `dep.vm.units` | dep | None | - | Единицы плотности: запись — документированные г/куб.см (ksPa | Подтверждено |
| `dep.sketches.enumeration` | AUX-SKETCH | None | kompas_list_sketches | API5 ksPart.EntityCollection(o3d_sketch = 5) — «При создании | Подтверждено |
| `INT-01.pairs.volumetric` | G1 | mcp_verified | kompas_check_interference | ksDocument3D.PartCollection(true) → ksPart.BodyCollection →  | Подтверждено |
| `INT-02.pairs.tangent` | G1 | mcp_verified | kompas_check_interference | ksBody.CheckIntersectionWithBody(other, checkTangent); справ | Подтверждено |
| `INT-03.pairs.faces` | G1 | mcp_verified | kompas_check_interference | ksBody.GetIntersectionFacesWithBody(other, intersectionFaces | Подтверждено |
| `INT-04.gap.min_distance` | G1 | mcp_verified | kompas_measure_gap | IKompasDocument3D.TopPart → проверка версии КОМПАС (не ниже  | Не подтверждено |
| `dep.interference.component_address` | G1 | None | kompas_list_components | ksDocument3D.PartCollection(true) → ksPart; ksPart.BodyColle | Подтверждено |
| `dep.interference.face_address` | G1 | None | - | ksPart.BodyCollection → ksBody.FaceCollection → GetByIndex;  | Подтверждено |
| `dep.interference.api7_measurer` | G1 | None | - | IKompasDocument3D.TopPart → проверка версии КОМПАС (не ниже  | Подтверждено |
| `dep.interference.distance_unit` | G1 | None | kompas_measure_gap | IMeasurement3D.Lmin / GetMinPoint1/2; единица справкой не на | Не подтверждено |
| `dep.interference.read_only` | G1 | None | kompas_check_interference | оба инструмента отвечают requires_operation_id: false и не п | Подтверждено |
| `SKB-01.limits` | G2 | mcp_verified | kompas_edit_sketch | Схема и описание поля берут число из KompasMcp.Contracts.Ske | Не подтверждено |
| `SKB-02.polyline_native` | G2 | mcp_verified | kompas_edit_sketch | ksPolylineByParam(ksPolylineParam.closed) — один объект; чте | Подтверждено |
| `SKB-03.spline` | G2 | mcp_verified | kompas_edit_sketch | ksBezier(closed) + ksBezierPoint(ksBezierPointParam) + ksEnd | Подтверждено |
| `dep.sketch_bulk.limits_source` | G2 | None | kompas_list_sketch_entities | KompasMcp.Contracts.SketchLimits → ToolCatalog (maxItems и т | Подтверждено |
| `dep.sketch_bulk.entity_read_back` | G2 | None | - | ISketch.BeginEditEx(true) → IDrawingContainer.GetObjects(ksA | Подтверждено |
| `dep.sketch_bulk.profile_area_rule` | G2 | None | - | KompasMcp.Domain.Geometry.ProfileArea — общая формула профил | Не подтверждено |
| `VB-01.variable.create` | G3 | mcp_verified | kompas_create_variable, kompas_list_variables | kompas_create_variable: IPart7.IsVariableNameValid ДО записи | Подтверждено |
| `VB-02.feature_parameters.list` | G3 | mcp_verified | kompas_list_feature_parameters, kompas_list_features | kompas_list_feature_parameters: признак (ссылка из kompas_li | Подтверждено |
| `VB-03.parameter.bind` | G3 | mcp_verified | kompas_bind_parameter, kompas_list_feature_parameters, kompas_measure | kompas_bind_parameter: поиск параметра по ТОЧНОМУ имени в ks | Подтверждено |
| `VB-04.parameter.unbind` | G3 | mcp_verified | kompas_bind_parameter, kompas_list_feature_parameters, kompas_measure | kompas_bind_parameter с числовым expression: тот же порядок, | Подтверждено |
| `dep.vb.parameter_address` | G3 | None | kompas_list_feature_parameters | Адрес параметра — поле name из kompas_list_feature_parameter | Подтверждено |
| `dep.vb.units` | G3 | None | - | Значение параметра читается и пишется как число в собственно | Не подтверждено |
| `dep.vb.rebuild_document` | G3 | None | - | После записи выражения вызывается ksDocument3D.RebuildDocume | Подтверждено |
| `dep.vb.read_only_list` | G3 | None | kompas_list_feature_parameters | kompas_list_feature_parameters не требует operation_id и exp | Не подтверждено |
| `dep.vb.idempotency` | G3 | None | kompas_bind_parameter, kompas_create_variable, kompas_list_variables | Оба мутирующих инструмента (kompas_create_variable, kompas_b | Подтверждено |
| `EI-01.stl.export` | G5 | mcp_verified | kompas_export_stl, kompas_list_bodies | kompas_export_stl: политика путей (Host, поле target_path) → | Подтверждено |
| `EI-02.stl.tessellation` | G5 | mcp_verified | kompas_export_stl | kompas_export_stl: max_edge_length_mm -> ksAdditionFormatPar | Подтверждено |
| `EI-03.inertia.central` | G7 | mcp_verified | kompas_measure | kompas_measure со свойством inertia: ksBody.CalcMassInertiaP | Подтверждено |
| `EI-04.inertia.principal` | G7 | mcp_verified | kompas_measure | kompas_measure со свойством inertia: ksMassInertiaParam.jx0/ | Подтверждено |
| `EI-05.inertia.units` | G7 | mcp_verified | kompas_measure | kompas_measure: inertia_units mm_kg → bitVector ST_MIX_MM|ST | Не подтверждено |
| `dep.ei.path_policy` | G5 | None | - | Поле target_path входит в список полей пути Host'а (ToolInvo | Подтверждено |
| `dep.ei.triangle_count_from_file` | G5 | None | - | StlFileInspector: двоичный — (размер − 84) / 50 с чтением ве | Не подтверждено |
| `dep.ei.units_system` | G7 | None | kompas_measure | bitVector собирается из аргумента inertia_units (ST_MIX_MM|S | Подтверждено |
| `dep.ei.read_only_measure` | G7 | None | kompas_measure | kompas_measure зарегистрирован как ReadOnly (requiresOperati | Не подтверждено |
| `dep.ei.idempotency` | G5 | None | kompas_export_step, kompas_export_stl | Мутирующий инструмент: журнал и дедупликация — на уровне Hos | Подтверждено |
| `SKC-01.geometric_types` | G6 | mcp_verified | kompas_create_sketch_constraint | IDrawingObject1.NewConstraint() → задать ConstraintType/Inde | Подтверждено |
| `SKC-02.dimension_types` | G6 | mcp_verified | kompas_create_sketch_dimension | У каждого вида СВОЯ коллекция контейнера ISymbols2DContainer | Не подтверждено |
| `SKC-03.read` | G6 | mcp_verified | kompas_list_sketch_constraints | ISketch.BeginEditEx(true) → контейнер эскиза → QI(IDrawingOb | Подтверждено |
| `SKC-04.delete` | G6 | mcp_verified | kompas_delete_sketch_constraint, kompas_list_sketch_constraints, kompas_measure, kompas_open_document, kompas_save_document | Внутри сеанса записи: перечитать список → Delete() по позици | Подтверждено |
| `SKC-05.state_read` | G6 | mcp_verified | kompas_get_sketch_status, kompas_list_sketch_entities | Агрегатное состояние — ISketch.ConstraintsState; состояние о | Подтверждено |
| `SKC-06.association_bisector` | G6 | metadata_found | kompas_create_sketch_constraint | Тот же маршрут; партнёр передаётся массивом (partner_refs),  | Не подтверждено |
| `dep.sketch_constraints.object_address` | G6 | None | kompas_list_sketch_entities | Устойчивая адресация объекта эскиза для ограничения | Не подтверждено |
| `dep.sketch_constraints.interop_presence` | G6 | None | kompas_list_sketch_constraints | Каждый используемый член присутствует в ПОСТАВЛЕННОМ interop | Не подтверждено |
| `dep.sketch_constraints.type_vocabulary` | G6 | None | kompas_create_sketch_constraint, kompas_list_sketch_constraints | Перечень типов берётся из ОДНОЙ сборки, числа не дублируются | Не подтверждено |
| `dep.sketch_constraints.state_agreement` | G6 | None | kompas_get_sketch_status | Состояние объекта и агрегатное состояние эскиза не подменяют | Подтверждено |
| `dep.sketch_constraints.no_silent_rewrite` | G6 | None | kompas_list_sketch_entities | Перемещение геометрии ядром не скрывается: дельта публикуетс | Не подтверждено |
| `ASM-08.component.delete` | ASM | mcp_verified | kompas_delete_component, kompas_list_components, kompas_list_mates, kompas_open_document, kompas_rebuild, kompas_save_document | kompas_delete_component: API5 ksDocument3D.DeleteObject(ksPa | Подтверждено |
| `SM-02.material_toward` | SM-02 | mcp_verified | kompas_extrude, kompas_get_feature, kompas_list_bodies, kompas_measure, kompas_open_document, kompas_rebuild, kompas_save_document | kompas_extrude, вход material_toward: ExtrusionSide.Directio | Подтверждено |
| `STEP.import` | STEP | mcp_verified | kompas_import_step, kompas_list_bodies, kompas_list_documents, kompas_list_features, kompas_measure, kompas_rebuild | kompas_import_step читает файл в НОВЫЙ документ: API5 ksDocu | Подтверждено |
| `DRW-07.section_view.create.stepped` | DRW | mcp_verified | kompas_create_section_view | kompas_create_section_view (mode=stepped, trace_model либо t | Подтверждено |
| `DRW-07.section_view.create.simple` | DRW | mcp_verified | kompas_create_section_view | kompas_create_section_view (mode=simple) | Не подтверждено |
| `DRW-07.section_view.read` | DRW | mcp_verified | kompas_create_section_view, kompas_get_section_view | kompas_get_section_view (только чтение; на виде другого типа | Не подтверждено |
| `DRW-07.section_view.update` | DRW | mcp_verified | kompas_create_section_view, kompas_update_section_view | kompas_update_section_view (ответ несёт before и after одног | Не подтверждено |
| `DRW-07.section_view.delete` | DRW | mcp_verified | kompas_create_section_view, kompas_delete_section_view | kompas_delete_section_view (необязательный keep_cut_line ост | Не подтверждено |
| `DRW-07.section_view.save_reopen` | DRW | mcp_verified | kompas_create_section_view, kompas_get_section_view | kompas_get_section_view после повторного открытия документа | Не подтверждено |
| `DRW-07.section_view.rebuild` | DRW | mcp_verified | kompas_create_section_view, kompas_get_section_view, kompas_rebuild_drawing_views | kompas_rebuild_drawing_views + kompas_get_section_view | Не подтверждено |
| `DRW-07.section_view.geometry_validation` | DRW | mcp_verified | kompas_create_section_view, kompas_export_drawing, kompas_get_section_view | kompas_export_drawing (dxf) + kompas_get_section_view | Не подтверждено |
| `DRW-07.section_view.negative_tests` | DRW | mcp_verified | kompas_create_section_view | kompas_create_section_view (отказы с описанием частичных изм | Не подтверждено |
| `SM-12.incline.mode_faces_group` | SM-12 | mcp_verified | kompas_delete_feature, kompas_draft, kompas_get_feature, kompas_list_features, kompas_open_document, kompas_save_document, kompas_set_feature_suppressed, kompas_update_feature | kompas_draft: API7 IModelContainer.Inclines -> IInclines.Add | Подтверждено |
| `SM-12.incline.mode_base_face_or_plane` | SM-12 | mcp_verified | kompas_delete_feature, kompas_draft, kompas_get_feature, kompas_list_features, kompas_open_document, kompas_save_document, kompas_set_feature_suppressed, kompas_update_feature | kompas_draft: правка основания по ссылке отвергается (draft_ | Подтверждено |
| `SM-12.incline.mode_direction` | SM-12 | mcp_verified | kompas_delete_feature, kompas_draft, kompas_get_feature, kompas_list_features, kompas_open_document, kompas_save_document, kompas_set_feature_suppressed, kompas_update_feature | kompas_draft, direction: outward|inward -> BOOL признака; ст | Подтверждено |
| `SM-12.draft_from_edges.mode_dir_by_object` | SM-12 | mcp_verified | kompas_delete_feature, kompas_draft_from_edges, kompas_get_feature, kompas_list_features, kompas_open_document, kompas_save_document, kompas_set_feature_suppressed, kompas_update_feature | kompas_draft_from_edges: API7 IModelContainer.DraftsFromEdge | Не подтверждено |
| `SM-12.draft_from_edges.mode_side` | SM-12 | mcp_verified | kompas_delete_feature, kompas_draft_from_edges, kompas_get_feature, kompas_list_features, kompas_open_document, kompas_save_document, kompas_set_feature_suppressed, kompas_update_feature | kompas_draft_from_edges, сторона: SideFace - грань, прилегаю | Подтверждено |

## 5. COM-маршруты адаптера

| Маршрут | Сборка | Вид | Сигнатура | Мест | Документация | Способ |
|---|---|---|---|---|---|---|
| `IPart7.get_PartsEx` | api7 | method | `Object get_PartsEx(Object PartCollectionType)` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.Reference` | api7 | property | `prop Int32 Reference` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.Name` | api7 | property | `prop String Name` | 2 | Подтверждено | перечень интерфейса |
| `IPart7.Marking` | api7 | property | `prop String Marking` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.FileName` | api7 | property | `prop String FileName` | 4 | Подтверждено | перечень интерфейса |
| `IPart7.Detail` | api7 | property | `prop Boolean Detail` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.Fixed` | api7 | property | `prop Boolean Fixed` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.LoadState` | api7 | property | `prop ksLoadStateEnum LoadState` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.GetSummMatrix` | api7 | method | `Object GetSummMatrix(Part7 Part)` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.InstanceCount` | api7 | property | `prop Int32 InstanceCount` | 1 | Подтверждено | перечень интерфейса |
| `ksBodyCollection.GetCount` | api5 | method | `Int32 GetCount()` | 11 | Подтверждено | перечень интерфейса |
| `ksBodyCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 10 | Подтверждено | перечень интерфейса |
| `ksBody.FaceCollection` | api5 | method | `Object FaceCollection()` | 16 | Подтверждено | перечень интерфейса |
| `ksFaceCollection.GetCount` | api5 | method | `Int32 GetCount()` | 18 | Подтверждено | перечень интерфейса |
| `ksPlacement.GetMatrix3D` | api5 | method | `Boolean GetMatrix3D(Object& Result)` | 2 | не подтверждено | - |
| `ksDocument3D.PartCollection` | api5 | method | `Object PartCollection(Boolean refresh)` | 1 | Подтверждено | перечень интерфейса |
| `ksPartCollection.GetCount` | api5 | method | `Int32 GetCount()` | 1 | Подтверждено | перечень интерфейса |
| `ksPartCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.DefaultPlacement` | api5 | method | `placement DefaultPlacement()` | 1 | Подтверждено | перечень интерфейса |
| `ksPlacement.InitByMatrix3D` | api5 | method | `Boolean InitByMatrix3D(Object mtr)` | 2 | Подтверждено | перечень интерфейса |
| `IKompasDocument3D.TopPart` | api7 | property | `prop Part7 TopPart` | 1 | Подтверждено | перечень интерфейса |
| `ksPart.SetPlacement` | api5 | method | `Boolean SetPlacement(Object placement)` | 2 | Подтверждено | перечень интерфейса |
| `ksPart.UpdatePlacement` | api5 | method | `Boolean UpdatePlacement()` | 2 | Подтверждено | перечень интерфейса |
| `ksPart.fixedComponent` | api5 | property | `prop Boolean fixedComponent` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.RebuildDocument` | api5 | method | `Boolean RebuildDocument()` | 13 | Подтверждено | перечень интерфейса |
| `ksPart.GetPlacement` | api5 | method | `Object GetPlacement()` | 2 | Подтверждено | перечень интерфейса |
| `ksPart.fileName` | api5 | property | `prop String fileName` | 6 | Подтверждено | перечень интерфейса |
| `ksPart.Update` | api5 | method | `Boolean Update()` | 3 | Подтверждено | перечень интерфейса |
| `ksDocument3D.DeleteObject` | api5 | method | `Boolean DeleteObject(Object obj)` | 3 | Подтверждено | перечень интерфейса |
| `ksMateConstraint.GetBaseObj` | api5 | method | `Object GetBaseObj(Int16 number)` | 7 | Подтверждено | перечень интерфейса |
| `IModelObject.Part` | api7 | property | `prop Part7 Part` | 1 | Подтверждено | перечень интерфейса |
| `ksPart.name` | api5 | property | `prop String name` | 2 | Подтверждено | перечень интерфейса |
| `ksPart.GetDefaultEntity` | api5 | method | `Object GetDefaultEntity(Int16 objType)` | 3 | Подтверждено | перечень интерфейса |
| `IAuxiliaryGeomContainer.Planes3D` | api7 | property | `prop Planes3D Planes3D` | 3 | Подтверждено | перечень интерфейса |
| `IAuxiliaryGeomContainer.Axes3D` | api7 | property | `prop Axes3D Axes3D` | 3 | Подтверждено | перечень интерфейса |
| `IModelContainer.Points3D` | api7 | property | `prop Points3D Points3D` | 5 | Подтверждено | перечень интерфейса |
| `ksPart.EntityCollection` | api5 | method | `Object EntityCollection(Int16 objType)` | 17 | Подтверждено | перечень интерфейса |
| `ksEntityCollection.GetCount` | api5 | method | `Int32 GetCount()` | 33 | Подтверждено | перечень интерфейса |
| `ksEntityCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 23 | Подтверждено | перечень интерфейса |
| `ksEntity.type` | api5 | property | `prop Int16 type` | 29 | Подтверждено | перечень интерфейса |
| `ksEntity.GetDefinition` | api5 | method | `Object GetDefinition()` | 37 | Подтверждено | перечень интерфейса |
| `ksChamferDefinition.SetChamferParam` | api5 | method | `Boolean SetChamferParam(Boolean transfer, Double distance1, Double dis` | 2 | Подтверждено | перечень интерфейса |
| `ksEntity.Update` | api5 | method | `Boolean Update()` | 8 | Подтверждено | перечень интерфейса |
| `IModelContainer.Inclines` | api7 | property | `prop Inclines Inclines` | 2 | Подтверждено | перечень интерфейса |
| `IIncline.Faces` | api7 | property | `prop Object Faces` | 3 | Подтверждено | перечень интерфейса |
| `IIncline.Plane` | api7 | property | `prop IModelObject Plane` | 2 | Подтверждено | перечень интерфейса |
| `IIncline.Angle` | api7 | property | `prop Double Angle` | 4 | Подтверждено | перечень интерфейса |
| `IIncline.Direction` | api7 | property | `prop Boolean Direction` | 4 | Подтверждено | перечень интерфейса |
| `IModelContainer.DraftsFromEdges` | api7 | property | `prop DraftsFromEdges DraftsFromEdges` | 2 | Подтверждено | перечень интерфейса |
| `IDraftFromEdges.BaseLine` | api7 | property | `prop Object BaseLine` | 2 | Подтверждено | перечень интерфейса |
| `IDraftFromEdges.DirectionObject` | api7 | property | `prop IModelObject DirectionObject` | 1 | Подтверждено | перечень интерфейса |
| `IDraftFromEdges.SideFace` | api7 | property | `prop IModelObject SideFace` | 1 | Подтверждено | перечень интерфейса |
| `IDraftFromEdges.Angle` | api7 | property | `prop Double Angle` | 4 | Подтверждено | перечень интерфейса |
| `IDraftFromEdges.Reverse` | api7 | property | `prop Boolean Reverse` | 4 | Подтверждено | перечень интерфейса |
| `ksPart.GetMainBody` | api5 | method | `Object GetMainBody()` | 18 | Подтверждено | перечень интерфейса |
| `ksEntity.name` | api5 | property | `prop String name` | 21 | Подтверждено | перечень интерфейса |
| `IViews.AddStandartViews` | api7 | method | `Boolean AddStandartViews(String FileName, String ProjectionName, Objec` | 1 | Подтверждено | перечень интерфейса |
| `IView.Scale` | api7 | property | `prop Double Scale` | 6 | Подтверждено | перечень интерфейса |
| `IView.X` | api7 | property | `prop Double X` | 5 | Подтверждено | перечень интерфейса |
| `IView.Y` | api7 | property | `prop Double Y` | 5 | Подтверждено | перечень интерфейса |
| `IKompasDocument2D1.RebuildDocument` | api7 | method | `Boolean RebuildDocument()` | 1 | Подтверждено | перечень интерфейса |
| `ISymbols2DContainer.LineDimensions` | api7 | property | `prop LineDimensions LineDimensions` | 4 | Подтверждено | перечень интерфейса |
| `ISymbols2DContainer.RadialDimensions` | api7 | property | `prop RadialDimensions RadialDimensions` | 3 | Подтверждено | перечень интерфейса |
| `ISymbols2DContainer.DiametralDimensions` | api7 | property | `prop DiametralDimensions DiametralDimensions` | 3 | Подтверждено | перечень интерфейса |
| `ILineDimensions.Count` | api7 | property | `prop Int32 Count` | 1 | Подтверждено | перечень интерфейса |
| `ILineDimensions.get_LineDimension` | api7 | method | `LineDimension get_LineDimension(Object Index)` | 1 | Подтверждено | перечень интерфейса |
| `ILineDimension.X1` | api7 | property | `prop Double X1` | 2 | Подтверждено | перечень интерфейса |
| `ILineDimension.Y1` | api7 | property | `prop Double Y1` | 2 | Подтверждено | перечень интерфейса |
| `ILineDimension.X2` | api7 | property | `prop Double X2` | 2 | Подтверждено | перечень интерфейса |
| `ILineDimension.Y2` | api7 | property | `prop Double Y2` | 2 | Подтверждено | перечень интерфейса |
| `ILineDimension.DrawingObjectType` | api7 | property | `prop DrawingObjectTypeEnum DrawingObjectType` | 1 | Подтверждено | перечень интерфейса |
| `ILineDimension.Valid` | api7 | property | `prop Boolean Valid` | 2 | Подтверждено | перечень интерфейса |
| `IRadialDimensions.Count` | api7 | property | `prop Int32 Count` | 1 | Подтверждено | перечень интерфейса |
| `IRadialDimensions.get_RadialDimension` | api7 | method | `RadialDimension get_RadialDimension(Object Index)` | 1 | Подтверждено | перечень интерфейса |
| `IRadialDimension.Xc` | api7 | property | `prop Double Xc` | 1 | Подтверждено | перечень интерфейса |
| `IRadialDimension.Yc` | api7 | property | `prop Double Yc` | 1 | Подтверждено | перечень интерфейса |
| `IRadialDimension.DrawingObjectType` | api7 | property | `prop DrawingObjectTypeEnum DrawingObjectType` | 1 | Подтверждено | перечень интерфейса |
| `IRadialDimension.Valid` | api7 | property | `prop Boolean Valid` | 1 | Подтверждено | перечень интерфейса |
| `IRadialDimension.Radius` | api7 | property | `prop Double Radius` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimensions.Count` | api7 | property | `prop Int32 Count` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimensions.get_DiametralDimension` | api7 | method | `DiametralDimension get_DiametralDimension(Object Index)` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimension.Xc` | api7 | property | `prop Double Xc` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimension.Yc` | api7 | property | `prop Double Yc` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimension.DrawingObjectType` | api7 | property | `prop DrawingObjectTypeEnum DrawingObjectType` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimension.Valid` | api7 | property | `prop Boolean Valid` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimension.Radius` | api7 | property | `prop Double Radius` | 1 | Подтверждено | перечень интерфейса |
| `IDiametralDimension.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | перечень интерфейса |
| `IText.Str` | api7 | property | `prop String Str` | 2 | Подтверждено | перечень интерфейса |
| `ITechnicalDemand.Text` | api7 | property | `prop Text Text` | 3 | Подтверждено | перечень интерфейса |
| `ITechnicalDemand.AutoPlacement` | api7 | property | `prop Boolean AutoPlacement` | 2 | Подтверждено | перечень интерфейса |
| `ITechnicalDemand.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `ITechnicalDemand.IsCreated` | api7 | property | `prop Boolean IsCreated` | 2 | Подтверждено | перечень интерфейса |
| `IConverter.GetFilter` | api7 | method | `String GetFilter(Int32 DocType, Boolean SaveAs, Int32& Command)` | 1 | Подтверждено | перечень интерфейса |
| `IConverter.Convert` | api7 | method | `Int32 Convert(String InputFile, String Outfile, Int32 Command, Boolean` | 1 | Подтверждено | перечень интерфейса |
| `KompasObject.TransferInterface` | api5 | method | `Object TransferInterface(Object obj, Int32 apiNewType, Int32 objNewTyp` | 5 | Подтверждено | имя файла страницы |
| `KompasObject.ActiveDocument3D` | api5 | method | `Object ActiveDocument3D()` | 1 | Подтверждено | перечень интерфейса |
| `KompasObject.Document2D` | api5 | method | `Object Document2D()` | 3 | Подтверждено | перечень интерфейса |
| `IApplication.Documents` | api7 | property | `prop Documents Documents` | 2 | Подтверждено | перечень интерфейса |
| `IKompasDocument.PathName` | api7 | property | `prop String PathName` | 1 | Подтверждено | перечень интерфейса |
| `IKompasDocument.Name` | api7 | property | `prop String Name` | 1 | Подтверждено | перечень интерфейса |
| `IKompasDocument.DocumentType` | api7 | property | `prop DocumentTypeEnum DocumentType` | 1 | Подтверждено | перечень интерфейса |
| `IViews.Count` | api7 | property | `prop Int32 Count` | 8 | Подтверждено | перечень интерфейса |
| `IViews.get_View` | api7 | method | `IView get_View(Object Index)` | 5 | Подтверждено | перечень интерфейса |
| `IView.Reference` | api7 | property | `prop Int32 Reference` | 4 | Подтверждено | перечень интерфейса |
| `IView.ViewType` | api7 | property | `prop LtViewType ViewType` | 6 | Подтверждено | перечень интерфейса |
| `IView.Number` | api7 | property | `prop Int32 Number` | 5 | Подтверждено | перечень интерфейса |
| `IView.Name` | api7 | property | `prop String Name` | 4 | Подтверждено | перечень интерфейса |
| `IAssociationView.SourceFileName` | api7 | property | `prop String SourceFileName` | 2 | Подтверждено | перечень интерфейса |
| `IAssociationView.ProjectionName` | api7 | property | `prop String ProjectionName` | 1 | Подтверждено | перечень интерфейса |
| `IAssociationView.HiddenLinesVisible` | api7 | property | `prop Boolean HiddenLinesVisible` | 1 | Подтверждено | перечень интерфейса |
| `IAssociationView.CenterLinesVisible` | api7 | property | `prop Boolean CenterLinesVisible` | 1 | Подтверждено | перечень интерфейса |
| `IView.ObjectCount` | api7 | property | `prop Int32 ObjectCount` | 3 | Подтверждено | перечень интерфейса |
| `IAssociationView.Section` | api7 | property | `prop Boolean Section` | 3 | Подтверждено | перечень интерфейса |
| `ksRectParam.GetpBot` | api5 | method | `Object GetpBot()` | 1 | Подтверждено | перечень интерфейса |
| `ksRectParam.GetpTop` | api5 | method | `Object GetpTop()` | 1 | Подтверждено | перечень интерфейса |
| `ksMathPointParam.x` | api5 | property | `prop Double x` | 1 | Подтверждено | групповая страница |
| `ksMathPointParam.y` | api5 | property | `prop Double y` | 1 | Подтверждено | групповая страница |
| `IView.Valid` | api7 | property | `prop Boolean Valid` | 1 | Подтверждено | перечень интерфейса |
| `ILayoutSheets.Count` | api7 | property | `prop Int32 Count` | 2 | Подтверждено | перечень интерфейса |
| `ILayoutSheet.Stamp` | api7 | property | `prop _Stamp Stamp` | 1 | Подтверждено | перечень интерфейса |
| `ITechnicalDemand.BlocksGabarits` | api7 | property | `prop Object BlocksGabarits` | 2 | Подтверждено | перечень интерфейса |
| `ILayoutSheet.GetPlaceInsideFrames` | api7 | method | `Boolean GetPlaceInsideFrames(Double& Left, Double& Top, Double& Right,` | 1 | Подтверждено | перечень интерфейса |
| `IDimensionText.AutoNominalValue` | api7 | property | `prop Boolean AutoNominalValue` | 1 | Подтверждено | перечень интерфейса |
| `IDimensionText.NominalText` | api7 | property | `prop TextLine NominalText` | 2 | Подтверждено | перечень интерфейса |
| `ITextLine.Str` | api7 | property | `prop String Str` | 2 | Подтверждено | перечень интерфейса |
| `IDimensionText.NominalValue` | api7 | property | `prop Double NominalValue` | 3 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.GetVertex` | api5 | method | `Object GetVertex(Boolean start)` | 3 | Подтверждено | перечень интерфейса |
| `ksVertexDefinition.GetPoint` | api5 | method | `Boolean GetPoint(Double& x, Double& y, Double& z)` | 3 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.IsStraight` | api5 | method | `Boolean IsStraight()` | 2 | Подтверждено | перечень интерфейса |
| `ksFaceCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 12 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.GetSurface` | api5 | method | `Object GetSurface()` | 8 | Подтверждено | перечень интерфейса |
| `ksSurface.GetSurfaceParam` | api5 | method | `Object GetSurfaceParam()` | 8 | Подтверждено | перечень интерфейса |
| `ksCylinderParam.GetPlacement` | api5 | method | `Object GetPlacement()` | 5 | Подтверждено | перечень интерфейса |
| `ksPlacement.GetOrigin` | api5 | method | `Boolean GetOrigin(Double& x, Double& y, Double& z)` | 9 | Подтверждено | перечень интерфейса |
| `ksPlacement.GetVector` | api5 | method | `Boolean GetVector(Int32 type, Double& x, Double& y, Double& z)` | 6 | Подтверждено | перечень интерфейса |
| `ksCylinderParam.radius` | api5 | property | `prop Double radius` | 8 | Подтверждено | перечень интерфейса |
| `ksPlaneParam.GetPlacement` | api5 | method | `Object GetPlacement()` | 3 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.GetArea` | api5 | method | `Double GetArea(UInt32 bitVector)` | 4 | Подтверждено | перечень интерфейса |
| `ksSurface.IsCylinder` | api5 | method | `Boolean IsCylinder()` | 5 | Подтверждено | перечень интерфейса |
| `ksCylinderParam.height` | api5 | property | `prop Double height` | 5 | Подтверждено | перечень интерфейса |
| `ksAdditionFormatParam.Init` | api5 | method | `Boolean Init()` | 3 | Подтверждено | перечень интерфейса |
| `ksAdditionFormatParam.format` | api5 | property | `prop Int16 format` | 3 | Подтверждено | перечень интерфейса |
| `KompasObject.Document3D` | api5 | method | `Object Document3D()` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.Create` | api5 | method | `Boolean Create(Boolean invisible, Boolean typeDoc)` | 2 | Подтверждено | перечень интерфейса |
| `ksDocument3D.AdditionFormatParam` | api5 | method | `Object AdditionFormatParam()` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.LoadFromAdditionFormat` | api5 | method | `Boolean LoadFromAdditionFormat(String fileName, Object additionPar)` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.UpdateDocumentParam` | api5 | method | `Boolean UpdateDocumentParam()` | 2 | Подтверждено | перечень интерфейса |
| `IKompasDocument.Close` | api7 | method | `Boolean Close(DocumentCloseOptions closeOptions)` | 2 | Подтверждено | перечень интерфейса |
| `ksDocument3D.close` | api5 | method | `Boolean close()` | 3 | Подтверждено | перечень интерфейса |
| `ksDocument3D.GetPart` | api5 | method | `Object GetPart(Int32 type)` | 3 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.EdgeCollection` | api5 | method | `Object EdgeCollection()` | 3 | Подтверждено | перечень интерфейса |
| `ksEdgeCollection.GetCount` | api5 | method | `Int32 GetCount()` | 3 | Подтверждено | перечень интерфейса |
| `ksEdgeCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 3 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.GetLength` | api5 | method | `Double GetLength(UInt32 bitVector)` | 2 | Подтверждено | перечень интерфейса |
| `ksPart.RebuildModel` | api5 | method | `Boolean RebuildModel()` | 3 | Подтверждено | перечень интерфейса |
| `ksBaseEvolutionDefinition.sketchShiftType` | api5 | property | `prop Int16 sketchShiftType` | 3 | Подтверждено | перечень интерфейса |
| `ksBossEvolutionDefinition.sketchShiftType` | api5 | property | `prop Int16 sketchShiftType` | 2 | Подтверждено | перечень интерфейса |
| `ILoft.Sketchs` | api7 | property | `prop Object Sketchs` | 3 | Подтверждено | перечень интерфейса |
| `ksBaseLoftDefinition.Sketchs` | api5 | method | `Object Sketchs()` | 3 | Подтверждено | named_name_mismatch |
| `ksBossLoftDefinition.Sketchs` | api5 | method | `Object Sketchs()` | 3 | Подтверждено | named_name_mismatch |
| `ksEntityCollection.Clear` | api5 | method | `Boolean Clear()` | 2 | Подтверждено | перечень интерфейса |
| `ksEntityCollection.Add` | api5 | method | `Boolean Add(Object entity)` | 6 | Подтверждено | перечень интерфейса |
| `ksEntityCollection.refresh` | api5 | method | `Boolean refresh()` | 2 | Подтверждено | перечень интерфейса |
| `ksShellDefinition.thickness` | api5 | property | `prop Double thickness` | 5 | Подтверждено | перечень интерфейса |
| `ksShellDefinition.thinType` | api5 | property | `prop Boolean thinType` | 5 | Подтверждено | перечень интерфейса |
| `ksShellDefinition.FaceArray` | api5 | method | `Object FaceArray()` | 4 | Подтверждено | перечень интерфейса |
| `ksBaseEvolutionDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 3 | Подтверждено | перечень интерфейса |
| `ksBaseEvolutionDefinition.PathPartArray` | api5 | method | `Object PathPartArray()` | 3 | Подтверждено | перечень интерфейса |
| `ksBaseEvolutionDefinition.GetPathLength` | api5 | method | `Double GetPathLength(UInt32 bitVector)` | 2 | Подтверждено | перечень интерфейса |
| `ksBossEvolutionDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 2 | Подтверждено | перечень интерфейса |
| `ksBossEvolutionDefinition.PathPartArray` | api5 | method | `Object PathPartArray()` | 1 | Подтверждено | перечень интерфейса |
| `ksBossEvolutionDefinition.GetPathLength` | api5 | method | `Double GetPathLength(UInt32 bitVector)` | 1 | Подтверждено | перечень интерфейса |
| `ksBaseExtrusionDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 4 | Подтверждено | перечень интерфейса |
| `ksBossExtrusionDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 4 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 4 | Подтверждено | перечень интерфейса |
| `ksBaseExtrusionDefinition.SetSketch` | api5 | method | `Boolean SetSketch(Object sketch)` | 2 | Подтверждено | перечень интерфейса |
| `ksBossExtrusionDefinition.SetSketch` | api5 | method | `Boolean SetSketch(Object sketch)` | 2 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.SetSketch` | api5 | method | `Boolean SetSketch(Object sketch)` | 2 | Подтверждено | перечень интерфейса |
| `ksBaseExtrusionDefinition.directionType` | api5 | property | `prop Int16 directionType` | 2 | Подтверждено | перечень интерфейса |
| `ksBaseExtrusionDefinition.GetSideParam` | api5 | method | `Boolean GetSideParam(Boolean side1, Int16& type, Double& depth, Double` | 1 | Подтверждено | перечень интерфейса |
| `ksBaseExtrusionDefinition.GetThinParam` | api5 | method | `Boolean GetThinParam(Boolean& thin, Int16& thinType, Double& normalThi` | 1 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.directionType` | api5 | property | `prop Int16 directionType` | 2 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.GetSideParam` | api5 | method | `Boolean GetSideParam(Boolean side1, Int16& type, Double& depth, Double` | 1 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.GetThinParam` | api5 | method | `Boolean GetThinParam(Boolean& thin, Int16& thinType, Double& normalThi` | 1 | Подтверждено | перечень интерфейса |
| `ksBaseExtrusionDefinition.SetSideParam` | api5 | method | `Boolean SetSideParam(Boolean side1, Int16 type, Double depth, Double d` | 3 | Подтверждено | перечень интерфейса |
| `ksBossExtrusionDefinition.SetSideParam` | api5 | method | `Boolean SetSideParam(Boolean side1, Int16 type, Double depth, Double d` | 3 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.SetSideParam` | api5 | method | `Boolean SetSideParam(Boolean side1, Int16 type, Double depth, Double d` | 3 | Подтверждено | перечень интерфейса |
| `ksEntity.GetFeature` | api5 | method | `Object GetFeature()` | 6 | Подтверждено | перечень интерфейса |
| `ksFeature.IsValid` | api5 | method | `Boolean IsValid()` | 1 | Подтверждено | перечень интерфейса |
| `ksFeature.IsRollBacked` | api5 | method | `Boolean IsRollBacked()` | 1 | Подтверждено | перечень интерфейса |
| `ksFeature.objectError` | api5 | property | `prop Int32 objectError` | 1 | Подтверждено | перечень интерфейса |
| `ksFeature.updateStamp` | api5 | property | `prop UInt32 updateStamp` | 1 | Подтверждено | перечень интерфейса |
| `ksFeature.GetOwnerFeature` | api5 | method | `Object GetOwnerFeature()` | 1 | Подтверждено | перечень интерфейса |
| `ksEntity.excluded` | api5 | property | `prop Boolean excluded` | 1 | Подтверждено | перечень интерфейса |
| `ksFilletDefinition.radius` | api5 | property | `prop Double radius` | 2 | Подтверждено | перечень интерфейса |
| `ksFilletDefinition.tangent` | api5 | property | `prop Boolean tangent` | 2 | Подтверждено | перечень интерфейса |
| `ksFilletDefinition.array` | api5 | method | `Object array()` | 3 | Подтверждено | перечень интерфейса |
| `IModelObject.Reference` | api7 | property | `prop Int32 Reference` | 1 | Подтверждено | перечень интерфейса |
| `ksPart.NewEntity` | api5 | method | `Object NewEntity(Int16 objType)` | 5 | Подтверждено | перечень интерфейса |
| `ksSketchDefinition.SetPlane` | api5 | method | `Boolean SetPlane(Object plane)` | 2 | Подтверждено | перечень интерфейса |
| `ksEntity.Create` | api5 | method | `Boolean Create()` | 7 | Подтверждено | перечень интерфейса |
| `ksPlaneOffsetDefinition.SetPlane` | api5 | method | `Boolean SetPlane(Object plane)` | 1 | Подтверждено | перечень интерфейса |
| `ksPlaneOffsetDefinition.offset` | api5 | property | `prop Double offset` | 1 | Подтверждено | перечень интерфейса |
| `ksPlaneOffsetDefinition.direction` | api5 | property | `prop Boolean direction` | 1 | Подтверждено | перечень интерфейса |
| `ksSketchDefinition.BeginEdit` | api5 | method | `Object BeginEdit()` | 1 | Подтверждено | перечень интерфейса |
| `ksSketchDefinition.GetPlane` | api5 | method | `Object GetPlane()` | 5 | Подтверждено | перечень интерфейса |
| `ksSketchDefinition.EndEdit` | api5 | method | `Boolean EndEdit()` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument2D.ksLineSeg` | api5 | method | `Int32 ksLineSeg(Double x1, Double y1, Double x2, Double y2, Int32 styl` | 5 | Подтверждено | имя файла страницы |
| `ksDocument2D.ksCircle` | api5 | method | `Int32 ksCircle(Double xc, Double yc, Double rad, Int32 style)` | 1 | Подтверждено | имя файла страницы |
| `ksDocument2D.ksArcByPoint` | api5 | method | `Int32 ksArcByPoint(Double xc, Double yc, Double rad, Double x1, Double` | 1 | Подтверждено | имя файла страницы |
| `ksDocument2D.ksArcByAngle` | api5 | method | `Int32 ksArcByAngle(Double xc, Double yc, Double rad, Double f1, Double` | 1 | Подтверждено | имя файла страницы |
| `KompasObject.GetParamStruct` | api5 | method | `Object GetParamStruct(Int16 structType)` | 4 | Подтверждено | перечень интерфейса |
| `KompasObject.GetDynamicArray` | api5 | method | `Object GetDynamicArray(Int32 type)` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument2D.ksPolylineByParam` | api5 | method | `Int32 ksPolylineByParam(Object par)` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument2D.ksBezier` | api5 | method | `Int32 ksBezier(Int16 closed, Int32 style)` | 1 | Подтверждено | имя файла страницы |
| `ksDocument2D.ksBezierPoint` | api5 | method | `Int32 ksBezierPoint(Object par)` | 1 | Подтверждено | имя файла страницы |
| `ksDocument2D.ksEndObj` | api5 | method | `Int32 ksEndObj()` | 1 | Подтверждено | имя файла страницы |
| `ksSketchDefinition.GetSurface` | api5 | method | `Object GetSurface()` | 1 | Подтверждено | перечень интерфейса |
| `ksSurface.IsPlane` | api5 | method | `Boolean IsPlane()` | 1 | Подтверждено | перечень интерфейса |
| `ksPart.BodyCollection` | api5 | method | `Object BodyCollection()` | 6 | Подтверждено | перечень интерфейса |
| `ksBodyCollection.refresh` | api5 | method | `Boolean refresh()` | 6 | Подтверждено | перечень интерфейса |
| `ksBody.GetGabarit` | api5 | method | `Boolean GetGabarit(Double& x1, Double& y1, Double& z1, Double& x2, Dou` | 4 | Подтверждено | перечень интерфейса |
| `ksFaceCollection.refresh` | api5 | method | `Boolean refresh()` | 1 | Подтверждено | перечень интерфейса |
| `ksBossExtrusionDefinition.chooseType` | api5 | property | `prop Int32 chooseType` | 2 | Подтверждено | перечень интерфейса |
| `ksBossExtrusionDefinition.ChooseBodies` | api5 | method | `Object ChooseBodies()` | 1 | Подтверждено | перечень интерфейса |
| `ksBossExtrusionDefinition.directionType` | api5 | property | `prop Int16 directionType` | 1 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.chooseType` | api5 | property | `prop Int32 chooseType` | 2 | Подтверждено | перечень интерфейса |
| `ksCutExtrusionDefinition.ChooseBodies` | api5 | method | `Object ChooseBodies()` | 1 | Подтверждено | перечень интерфейса |
| `ksChooseBodies.ChooseBodiesType` | api5 | property | `prop Int32 ChooseBodiesType` | 1 | Подтверждено | перечень интерфейса |
| `ksChooseBodies.BodyCollection` | api5 | method | `Object BodyCollection()` | 1 | Подтверждено | перечень интерфейса |
| `ksBodyCollection.Add` | api5 | method | `Boolean Add(Object body)` | 1 | Подтверждено | перечень интерфейса |
| `ksPlaneOffsetDefinition.GetPlane` | api5 | method | `Object GetPlane()` | 1 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.GetEntity` | api5 | method | `Object GetEntity()` | 1 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.GetOwnerEntity` | api5 | method | `Object GetOwnerEntity()` | 1 | Подтверждено | перечень интерфейса |
| `ksChamferDefinition.array` | api5 | method | `Object array()` | 1 | Подтверждено | перечень интерфейса |
| `ksChamferDefinition.GetChamferParam` | api5 | method | `Boolean GetChamferParam(Boolean& transfer, Double& distance1, Double& ` | 1 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.IsPlanar` | api5 | method | `Boolean IsPlanar()` | 2 | Подтверждено | перечень интерфейса |
| `ksSurface.GetParamUMin` | api5 | method | `Double GetParamUMin()` | 1 | Подтверждено | перечень интерфейса |
| `ksSurface.GetParamUMax` | api5 | method | `Double GetParamUMax()` | 1 | Подтверждено | перечень интерфейса |
| `ksSurface.GetParamVMin` | api5 | method | `Double GetParamVMin()` | 1 | Подтверждено | перечень интерфейса |
| `ksSurface.GetParamVMax` | api5 | method | `Double GetParamVMax()` | 1 | Подтверждено | перечень интерфейса |
| `ksSurface.GetNormal` | api5 | method | `Boolean GetNormal(Double paramU, Double paramV, Double& x, Double& y, ` | 1 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.normalOrientation` | api5 | property | `prop Boolean normalOrientation` | 1 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.IsCylinder` | api5 | method | `Boolean IsCylinder()` | 1 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.IsCone` | api5 | method | `Boolean IsCone()` | 1 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.IsSphere` | api5 | method | `Boolean IsSphere()` | 1 | Подтверждено | перечень интерфейса |
| `ksFaceDefinition.IsTorus` | api5 | method | `Boolean IsTorus()` | 1 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.IsCircle` | api5 | method | `Boolean IsCircle()` | 1 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.IsArc` | api5 | method | `Boolean IsArc()` | 1 | Подтверждено | перечень интерфейса |
| `ksEdgeDefinition.GetCurve3D` | api5 | method | `Object GetCurve3D()` | 1 | Подтверждено | перечень интерфейса |
| `ksCurve3D.IsCircle` | api5 | method | `Boolean IsCircle()` | 1 | Подтверждено | перечень интерфейса |
| `ksCurve3D.GetCurveParam` | api5 | method | `Object GetCurveParam()` | 1 | Подтверждено | перечень интерфейса |
| `ksCircle3dParam.GetPlacement` | api5 | method | `Object GetPlacement()` | 1 | Подтверждено | перечень интерфейса |
| `ksCircle3dParam.radius` | api5 | property | `prop Double radius` | 1 | Подтверждено | перечень интерфейса |
| `ksBody.IsSolid` | api5 | method | `Boolean IsSolid()` | 1 | Подтверждено | перечень интерфейса |
| `ksMassInertiaParam.xc` | api5 | property | `prop Double xc` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.yc` | api5 | property | `prop Double yc` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.zc` | api5 | property | `prop Double zc` | 1 | Подтверждено | групповая страница |
| `ksBody.CalcMassInertiaProperties` | api5 | method | `Object CalcMassInertiaProperties(UInt32 bitVector)` | 1 | Подтверждено | named_name_mismatch |
| `ksMassInertiaParam.jx` | api5 | property | `prop Double jx` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jy` | api5 | property | `prop Double jy` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jz` | api5 | property | `prop Double jz` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jxy` | api5 | property | `prop Double jxy` | 1 | Подтверждено | перечень интерфейса |
| `ksMassInertiaParam.jxz` | api5 | property | `prop Double jxz` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jyz` | api5 | property | `prop Double jyz` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jx0z` | api5 | property | `prop Double jx0z` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jy0z` | api5 | property | `prop Double jy0z` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jx0y` | api5 | property | `prop Double jx0y` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jx0` | api5 | property | `prop Double jx0` | 1 | Подтверждено | перечень интерфейса |
| `ksMassInertiaParam.jy0` | api5 | property | `prop Double jy0` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.jz0` | api5 | property | `prop Double jz0` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.GetAxisX` | api5 | method | `Boolean GetAxisX(Double& x, Double& y, Double& z)` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.GetAxisY` | api5 | method | `Boolean GetAxisY(Double& x, Double& y, Double& z)` | 1 | Подтверждено | групповая страница |
| `ksMassInertiaParam.GetAxisZ` | api5 | method | `Boolean GetAxisZ(Double& x, Double& y, Double& z)` | 1 | Подтверждено | групповая страница |
| `ksEntity.IsCreated` | api5 | method | `Boolean IsCreated()` | 2 | Подтверждено | перечень интерфейса |
| `ksDocument3D.RasterFormatParam` | api5 | method | `Object RasterFormatParam()` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.SaveAsToRasterFormat` | api5 | method | `Boolean SaveAsToRasterFormat(String fileName, Object rasterPar)` | 1 | Подтверждено | перечень интерфейса |
| `ksRasterFormatParam.Init` | api5 | method | `Boolean Init()` | 1 | Подтверждено | перечень интерфейса |
| `ksRasterFormatParam.format` | api5 | property | `prop Int16 format` | 1 | Подтверждено | перечень интерфейса |
| `ksRasterFormatParam.colorBPP` | api5 | property | `prop Int16 colorBPP` | 1 | Подтверждено | перечень интерфейса |
| `ksRasterFormatParam.extResolution` | api5 | property | `prop Int32 extResolution` | 1 | Подтверждено | перечень интерфейса |
| `ksRasterFormatParam.extScale` | api5 | property | `prop Double extScale` | 1 | Подтверждено | перечень интерфейса |
| `ksRasterFormatParam.returnResultAsArrayBytes` | api5 | property | `prop Boolean returnResultAsArrayBytes` | 1 | Подтверждено | перечень интерфейса |
| `ksRasterFormatParam.resultArrayBytes` | api5 | property | `prop Object resultArrayBytes` | 1 | Подтверждено | перечень интерфейса |
| `ksIntersectionResult.GetCount` | api5 | method | `Int32 GetCount()` | 1 | Подтверждено | перечень интерфейса |
| `ksIntersectionResult.GetIntersectionType` | api5 | method | `Int32 GetIntersectionType(Int32 index)` | 1 | Подтверждено | перечень интерфейса |
| `ksBody.GetIntersectionFacesWithBody` | api5 | method | `Int32 GetIntersectionFacesWithBody(Object otherBody, Object& intersect` | 1 | Подтверждено | перечень интерфейса |
| `IMeasurement3DTwin.set_Object1` | adapter-twins | method | `Void set_Object1(IKompasAPIObject Result)` | 1 | не проверено | - |
| `IMeasurement3DTwin.set_Object2` | adapter-twins | method | `Void set_Object2(IKompasAPIObject Result)` | 1 | не проверено | - |
| `IMeasurement3DTwin.Calculate` | adapter-twins | method | `ksMeasureResultEnum Calculate()` | 1 | не проверено | - |
| `IMeasurement3DTwin.get_Lmin` | adapter-twins | method | `Double get_Lmin()` | 1 | не проверено | - |
| `IMeasurement3DTwin.get_Angle` | adapter-twins | method | `Double get_Angle()` | 1 | не проверено | - |
| `IMeasurement3DTwin.get_IsAngleValid` | adapter-twins | method | `Boolean get_IsAngleValid()` | 1 | не проверено | - |
| `IMeasurement3DTwin.GetMinPoint1` | adapter-twins | method | `Boolean GetMinPoint1(Double& X, Double& Y, Double& Z)` | 1 | не проверено | - |
| `IMeasurement3DTwin.GetMinPoint2` | adapter-twins | method | `Boolean GetMinPoint2(Double& X, Double& Y, Double& Z)` | 1 | не проверено | - |
| `IPart7Twin.get_Measurement3D` | adapter-twins | method | `IMeasurement3DTwin get_Measurement3D()` | 1 | не проверено | - |
| `IApplication.KompasError` | api7 | property | `prop KompasError KompasError` | 1 | Подтверждено | перечень интерфейса |
| `ksFeature.excluded` | api5 | property | `prop Boolean excluded` | 1 | Подтверждено | перечень интерфейса |
| `ksBaseRotatedDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 1 | Подтверждено | перечень интерфейса |
| `ksBossRotatedDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 1 | Подтверждено | перечень интерфейса |
| `ksCutRotatedDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 1 | Подтверждено | перечень интерфейса |
| `ksCutEvolutionDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 1 | Подтверждено | перечень интерфейса |
| `ksCutBySketchDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 1 | Подтверждено | перечень интерфейса |
| `ksRibDefinition.GetSketch` | api5 | method | `Object GetSketch()` | 1 | Подтверждено | перечень интерфейса |
| `ksEntityCollection.FindIt` | api5 | method | `Int32 FindIt(Object entity)` | 2 | Подтверждено | перечень интерфейса |
| `ILofts.Add` | api7 | method | `Loft Add(ksObj3dTypeEnum LoftType)` | 1 | Подтверждено | перечень интерфейса |
| `ILoft.CouplingsCount` | api7 | property | `prop Int32 CouplingsCount` | 3 | Подтверждено | перечень интерфейса |
| `ILoft.ClearCouplings` | api7 | method | `Boolean ClearCouplings()` | 1 | Подтверждено | перечень интерфейса |
| `ILoft.AddCoupling` | api7 | method | `Coupling AddCoupling()` | 1 | Подтверждено | перечень интерфейса |
| `ILoft.Coupling` | api7 | property | `prop Coupling Coupling` | 1 | Подтверждено | перечень интерфейса |
| `ICoupling.Count` | api7 | property | `prop Int32 Count` | 1 | Подтверждено | перечень интерфейса |
| `ICoupling.PositionOffset` | api7 | property | `prop Double PositionOffset` | 1 | Подтверждено | перечень интерфейса |
| `IMateConstraint3D.BaseObject1` | api7 | property | `prop IModelObject BaseObject1` | 2 | Подтверждено | перечень интерфейса |
| `IMateConstraint3D.BaseObject2` | api7 | property | `prop IModelObject BaseObject2` | 2 | Подтверждено | перечень интерфейса |
| `IMateConstraint3D.Alignment` | api7 | property | `prop ksMateConstraintAlignmentEnum Alignment` | 2 | Подтверждено | перечень интерфейса |
| `IMateConstraint3D.ParamValue` | api7 | property | `prop Double ParamValue` | 4 | Подтверждено | перечень интерфейса |
| `IMateConstraint3D.Update` | api7 | method | `Boolean Update()` | 3 | не подтверждено | - |
| `IMateConstraint3D.Valid` | api7 | property | `prop Boolean Valid` | 1 | не подтверждено | - |
| `IMateConstraint3D.Fixed` | api7 | property | `prop ksMateFixedTypeEnum Fixed` | 3 | Подтверждено | перечень интерфейса |
| `ksDocument3D.RemoveMateConstraint` | api5 | method | `Boolean RemoveMateConstraint(Int32 constraintType, Object obj1, Object` | 1 | Подтверждено | перечень интерфейса |
| `IMateConstraint3D.ConstraintType` | api7 | property | `prop MateConstraintType ConstraintType` | 2 | Подтверждено | перечень интерфейса |
| `ksDocument3D.MateConstraintCollection` | api5 | method | `Object MateConstraintCollection()` | 3 | Подтверждено | перечень интерфейса |
| `IMateConstraints3D.Count` | api7 | property | `prop Int32 Count` | 1 | не подтверждено | - |
| `ksMateConstraintCollection.GetCount` | api5 | method | `Int32 GetCount()` | 2 | Подтверждено | перечень интерфейса |
| `ksMateConstraintCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 2 | Подтверждено | перечень интерфейса |
| `ksMateConstraint.constraintType` | api5 | property | `prop Int16 constraintType` | 1 | Подтверждено | перечень интерфейса |
| `ksMateConstraint.distance` | api5 | property | `prop Double distance` | 1 | Подтверждено | перечень интерфейса |
| `ksMateConstraint.direction` | api5 | property | `prop Int16 direction` | 1 | Подтверждено | перечень интерфейса |
| `IMate3DTransmission.MotionType1` | api7 | property | `prop ksMateMotionTypeEnum MotionType1` | 1 | Подтверждено | перечень интерфейса |
| `IMate3DTransmission.MotionType2` | api7 | property | `prop ksMateMotionTypeEnum MotionType2` | 1 | Подтверждено | перечень интерфейса |
| `IMate3DTransmission.RotationAxis1` | api7 | property | `prop IModelObject RotationAxis1` | 1 | Подтверждено | перечень интерфейса |
| `IMate3DTransmission.RotationAxis2` | api7 | property | `prop IModelObject RotationAxis2` | 1 | Подтверждено | перечень интерфейса |
| `IMate3DTransmission.SetScale` | api7 | method | `Boolean SetScale(Double Scale1, Double Scale2)` | 1 | Подтверждено | перечень интерфейса |
| `IMate3DTransmission.Direction1` | api7 | property | `prop Boolean Direction1` | 1 | Подтверждено | перечень интерфейса |
| `IMate3DTransmission.Direction2` | api7 | property | `prop Boolean Direction2` | 1 | Подтверждено | перечень интерфейса |
| `IModelContainer.FeaturePatterns` | api7 | property | `prop FeaturePatterns FeaturePatterns` | 7 | Подтверждено | перечень интерфейса |
| `IFeaturePattern.Owner` | api7 | property | `prop IFeature7 Owner` | 9 | Подтверждено | named_name_mismatch |
| `IRotated.Angle` | api7 | property | `prop Double Angle` | 7 | Подтверждено | перечень интерфейса |
| `ICutLine.Points` | api7 | property | `prop Object Points` | 2 | Подтверждено | перечень интерфейса |
| `ICutLine.ArrowPos` | api7 | property | `prop Boolean ArrowPos` | 2 | Подтверждено | перечень интерфейса |
| `IViews.Add` | api7 | method | `IView Add(LtViewType ViewType)` | 1 | Подтверждено | перечень интерфейса |
| `IAssociationView.BaseObject` | api7 | property | `prop IDrawingObject BaseObject` | 2 | Подтверждено | перечень интерфейса |
| `IAssociationView.ProjectionLink` | api7 | property | `prop Boolean ProjectionLink` | 2 | Подтверждено | перечень интерфейса |
| `IAssociationView.GetProjectionPoint` | api7 | method | `Boolean GetProjectionPoint(Double X, Double Y, Double Z, Double& Xv, D` | 1 | Подтверждено | перечень интерфейса |
| `IViewDesignation.Designation` | api7 | property | `prop String Designation` | 2 | Подтверждено | перечень интерфейса |
| `IHatchParam.Step` | api7 | property | `prop Double Step` | 1 | Подтверждено | перечень интерфейса |
| `IHatchParam.HatchAngle` | api7 | property | `prop Double HatchAngle` | 1 | Подтверждено | перечень интерфейса |
| `IHatchParam.Style` | api7 | property | `prop Int32 Style` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingObject.Delete` | api7 | method | `Boolean Delete()` | 2 | Подтверждено | перечень интерфейса |
| `ISketch.BeginEdit` | api7 | method | `FragmentDocument BeginEdit()` | 2 | Подтверждено | перечень интерфейса |
| `ISketch.BeginEditEx` | api7 | method | `FragmentDocument BeginEditEx(Boolean ReadOnly)` | 2 | Подтверждено | перечень интерфейса |
| `ISketch.EndEdit` | api7 | method | `Boolean EndEdit()` | 3 | Подтверждено | перечень интерфейса |
| `IKompasDocument2D1Twin.get_ParametricMode` | adapter-twins | method | `Boolean get_ParametricMode()` | 1 | не проверено | - |
| `ILineSegment.X1` | api7 | property | `prop Double X1` | 5 | Подтверждено | перечень интерфейса |
| `ILineSegment.Y1` | api7 | property | `prop Double Y1` | 5 | Подтверждено | перечень интерфейса |
| `ILineSegment.X2` | api7 | property | `prop Double X2` | 5 | Подтверждено | перечень интерфейса |
| `ILineSegment.Y2` | api7 | property | `prop Double Y2` | 5 | Подтверждено | перечень интерфейса |
| `ILineSegment.Length` | api7 | property | `prop Double Length` | 4 | Подтверждено | перечень интерфейса |
| `IDrawingObject.Reference` | api7 | property | `prop Int32 Reference` | 2 | Подтверждено | перечень интерфейса |
| `ICircle.Radius` | api7 | property | `prop Double Radius` | 3 | Подтверждено | перечень интерфейса |
| `IArc.Radius` | api7 | property | `prop Double Radius` | 2 | Подтверждено | перечень интерфейса |
| `IDrawingObject.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `IDrawingObject.LayerNumber` | api7 | property | `prop Int32 LayerNumber` | 2 | Подтверждено | перечень интерфейса |
| `IKompasAPIObject.Parent` | api7 | property | `prop IKompasAPIObject Parent` | 2 | Подтверждено | перечень интерфейса |
| `IPlane3D.Name` | api7 | property | `prop String Name` | 6 | Подтверждено | перечень интерфейса |
| `IPlane3D.ModelObjectType` | api7 | property | `prop ksObj3dTypeEnum ModelObjectType` | 1 | Подтверждено | перечень интерфейса |
| `IModelContainer.Booleans` | api7 | property | `prop Booleans Booleans` | 3 | Подтверждено | перечень интерфейса |
| `IBoolean.BooleanType` | api7 | property | `prop ksBooleanType BooleanType` | 4 | Подтверждено | перечень интерфейса |
| `IBoolean.SaveCopyModifyObjects` | api7 | property | `prop Boolean SaveCopyModifyObjects` | 2 | Подтверждено | перечень интерфейса |
| `IModelContainer.Cuts` | api7 | property | `prop Cuts Cuts` | 2 | Подтверждено | перечень интерфейса |
| `ICut.CutObject` | api7 | property | `prop IModelObject CutObject` | 3 | Подтверждено | перечень интерфейса |
| `ICut.Direction` | api7 | property | `prop Boolean Direction` | 3 | Подтверждено | перечень интерфейса |
| `IModelContainer.SplitSolids` | api7 | property | `prop SplitSolids SplitSolids` | 2 | Подтверждено | перечень интерфейса |
| `ISplitSolid.CutObjects` | api7 | property | `prop Object CutObjects` | 3 | Подтверждено | перечень интерфейса |
| `IModelContainer.BodyRepositions` | api7 | property | `prop BodyRepositions BodyRepositions` | 3 | Подтверждено | перечень интерфейса |
| `IPlane3DBy3Points.Point1` | api7 | property | `prop IModelObject Point1` | 3 | Подтверждено | перечень интерфейса |
| `IPlane3DBy3Points.Point2` | api7 | property | `prop IModelObject Point2` | 3 | Подтверждено | перечень интерфейса |
| `IPlane3DBy3Points.Point3` | api7 | property | `prop IModelObject Point3` | 3 | Подтверждено | перечень интерфейса |
| `IPoint3D.X` | api7 | property | `prop Double X` | 7 | Подтверждено | перечень интерфейса |
| `IPoint3D.Y` | api7 | property | `prop Double Y` | 7 | Подтверждено | перечень интерфейса |
| `IPoint3D.Z` | api7 | property | `prop Double Z` | 7 | Подтверждено | перечень интерфейса |
| `ksAdditionFormatParam.formatBinary` | api5 | property | `prop Boolean formatBinary` | 2 | Подтверждено | перечень интерфейса |
| `ksAdditionFormatParam.length` | api5 | property | `prop Double length` | 2 | Подтверждено | перечень интерфейса |
| `ksAdditionFormatParam.angle` | api5 | property | `prop Double angle` | 2 | Подтверждено | перечень интерфейса |
| `ksBaseEvolutionDefinition.SetSketch` | api5 | method | `Boolean SetSketch(Object sketch)` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.IsVariableNameValid` | api7 | method | `Boolean IsVariableNameValid(String Name)` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.AddVariable` | api7 | method | `Variable7 AddVariable(String Name, Double Value, String Note)` | 1 | Подтверждено | перечень интерфейса |
| `IVariable7.External` | api7 | property | `prop Boolean External` | 1 | Подтверждено | перечень интерфейса |
| `IVariable7.Expression` | api7 | property | `prop String Expression` | 1 | Подтверждено | перечень интерфейса |
| `ksFeature.name` | api5 | property | `prop String name` | 1 | Подтверждено | перечень интерфейса |
| `ksFeature.VariableCollection` | api5 | property | `prop Object VariableCollection` | 1 | Подтверждено | перечень интерфейса |
| `ksVariableCollection.GetCount` | api5 | method | `Int32 GetCount()` | 2 | Подтверждено | перечень интерфейса |
| `ksVariableCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 2 | Подтверждено | перечень интерфейса |
| `ksPart.material` | api5 | property | `prop String material` | 2 | Подтверждено | перечень интерфейса |
| `ksPart.density` | api5 | property | `prop Double density` | 1 | Подтверждено | named_name_mismatch |
| `ksPart.CalcMassInertiaProperties` | api5 | method | `Object CalcMassInertiaProperties(UInt32 bitVector)` | 1 | Подтверждено | перечень интерфейса |
| `ksMassInertiaParam.r` | api5 | property | `prop Double r` | 1 | Подтверждено | перечень интерфейса |
| `ksPart.SetMaterial` | api5 | method | `Boolean SetMaterial(String material, Double density)` | 1 | Подтверждено | перечень интерфейса |
| `ksPart.VariableCollection` | api5 | method | `Object VariableCollection()` | 1 | Подтверждено | перечень интерфейса |
| `ksVariableCollection.GetByName` | api5 | method | `Object GetByName(String name, Boolean testFullName, Boolean testIgnore` | 1 | Подтверждено | перечень интерфейса |
| `ksVariable.name` | api5 | property | `prop String name` | 1 | Подтверждено | перечень интерфейса |
| `ksVariable.value` | api5 | property | `prop Double value` | 2 | Подтверждено | перечень интерфейса |
| `ksVariable.Expression` | api5 | property | `prop String Expression` | 2 | Подтверждено | перечень интерфейса |
| `ksVariable.displayName` | api5 | property | `prop String displayName` | 1 | Подтверждено | перечень интерфейса |
| `ksVariable.note` | api5 | property | `prop String note` | 1 | Подтверждено | перечень интерфейса |
| `KompasObject.Visible` | api5 | property | `prop Boolean Visible` | 2 | Подтверждено | перечень интерфейса |
| `KompasObject.ksGetHWindow` | api5 | method | `Int32 ksGetHWindow()` | 2 | Подтверждено | имя файла страницы |
| `ksDocument3D.invisibleMode` | api5 | property | `prop Boolean invisibleMode` | 1 | Подтверждено | имя файла страницы |
| `ksDocument3D.SetActive` | api5 | method | `Boolean SetActive()` | 1 | Подтверждено | перечень интерфейса |
| `KompasObject.ksRefreshActiveWindow` | api5 | method | `Int32 ksRefreshActiveWindow()` | 1 | Подтверждено | перечень интерфейса |
| `KompasObject.ksGetSystemVersion` | api5 | method | `Int32 ksGetSystemVersion(Int32& iMajor, Int32& iMinor, Int32& iRelease` | 1 | Подтверждено | имя файла страницы |
| `KompasObject.Quit` | api5 | method | `Void Quit()` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.SaveAs` | api5 | method | `Boolean SaveAs(String fileName)` | 2 | Подтверждено | перечень интерфейса |
| `ksDocument2D.ksSaveDocument` | api5 | method | `Boolean ksSaveDocument(String fileName)` | 2 | Подтверждено | имя файла страницы |
| `IKompasDocument.SaveAs` | api7 | method | `Void SaveAs(String PathName)` | 2 | Подтверждено | перечень интерфейса |
| `ksDocument2D.ksCloseDocument` | api5 | method | `Boolean ksCloseDocument()` | 2 | Подтверждено | имя файла страницы |
| `ksPart.marking` | api5 | property | `prop String marking` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.Open` | api5 | method | `Boolean Open(String fileName, Boolean invisible)` | 1 | Подтверждено | перечень интерфейса |
| `ksDocumentParam.Init` | api5 | method | `Boolean Init()` | 1 | Подтверждено | перечень интерфейса |
| `ksDocumentParam.type` | api5 | property | `prop Int16 type` | 1 | Подтверждено | перечень интерфейса |
| `ksDocumentParam.regime` | api5 | property | `prop Int16 regime` | 1 | Подтверждено | перечень интерфейса |
| `ksDocumentParam.comment` | api5 | property | `prop String comment` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument2D.ksCreateDocument` | api5 | method | `Boolean ksCreateDocument(Object par)` | 1 | Подтверждено | имя файла страницы |
| `ksDocument2D.ksOpenDocument` | api5 | method | `Boolean ksOpenDocument(String nameDoc, Boolean regim)` | 1 | Подтверждено | имя файла страницы |
| `ksPart.GetGabarit` | api5 | method | `Boolean GetGabarit(Boolean full, Boolean customizable, Double& x1, Dou` | 1 | Подтверждено | перечень интерфейса |
| `ksDocument3D.Save` | api5 | method | `Boolean Save()` | 1 | Подтверждено | перечень интерфейса |
| `IKompasDocument.Save` | api7 | method | `Void Save()` | 1 | Подтверждено | перечень интерфейса |
| `ksViewProjectionCollection.refresh` | api5 | method | `Boolean refresh()` | 3 | Подтверждено | перечень интерфейса |
| `ksDocument3D.GetViewProjectionCollection` | api5 | method | `Object GetViewProjectionCollection()` | 1 | Подтверждено | перечень интерфейса |
| `ksViewProjectionCollection.GetCount` | api5 | method | `Int32 GetCount()` | 3 | Подтверждено | перечень интерфейса |
| `ksViewProjectionCollection.GetByIndex` | api5 | method | `Object GetByIndex(Int32 index)` | 3 | Подтверждено | перечень интерфейса |
| `ksViewProjection.IsCurrent` | api5 | method | `Boolean IsCurrent()` | 1 | Подтверждено | перечень интерфейса |
| `ksViewProjection.GetViewProjectonType` | api5 | method | `Int32 GetViewProjectonType()` | 1 | Подтверждено | перечень интерфейса |
| `ksViewProjectionCollection.viewProjectionScheme` | api5 | property | `prop Int32 viewProjectionScheme` | 1 | Подтверждено | перечень интерфейса |
| `IPlanes3D.Count` | api7 | property | `prop Int32 Count` | 3 | Подтверждено | перечень интерфейса |
| `IPlanes3D.get_Plane3D` | api7 | method | `IPlane3D get_Plane3D(Object Index)` | 2 | Подтверждено | перечень интерфейса |
| `IAxes3D.Count` | api7 | property | `prop Int32 Count` | 2 | Подтверждено | перечень интерфейса |
| `IAxes3D.get_Axis3D` | api7 | method | `IAxis3D get_Axis3D(Object Index)` | 1 | Подтверждено | перечень интерфейса |
| `IPoints3D.Count` | api7 | property | `prop Int32 Count` | 2 | Подтверждено | перечень интерфейса |
| `IPoints3D.get_Point3D` | api7 | method | `IPoint3D get_Point3D(Object Index)` | 1 | Подтверждено | перечень интерфейса |
| `IPoint3D.Name` | api7 | property | `prop String Name` | 2 | Подтверждено | перечень интерфейса |
| `IPoints3D.Add` | api7 | method | `IPoint3D Add()` | 2 | Подтверждено | перечень интерфейса |
| `IPoint3D.Update` | api7 | method | `Boolean Update()` | 5 | Подтверждено | перечень интерфейса |
| `IPoint3D.ParameterType` | api7 | property | `prop ksPoint3DTypeEnum ParameterType` | 1 | Подтверждено | перечень интерфейса |
| `IPoint3D.SetAssociationObject` | api7 | method | `Boolean SetAssociationObject(IModelObject NewVal)` | 1 | Подтверждено | перечень интерфейса |
| `IPoint3D.Parameters` | api7 | property | `prop IKompasAPIObject Parameters` | 1 | Подтверждено | перечень интерфейса |
| `IPoint3DParamDisplace.DX` | api7 | property | `prop Double DX` | 3 | Подтверждено | перечень интерфейса |
| `IPoint3DParamDisplace.DY` | api7 | property | `prop Double DY` | 3 | Подтверждено | перечень интерфейса |
| `IPoint3DParamDisplace.DZ` | api7 | property | `prop Double DZ` | 3 | Подтверждено | перечень интерфейса |
| `IPoint3D.AssociationObject` | api7 | property | `prop IModelObject AssociationObject` | 1 | Подтверждено | перечень интерфейса |
| `IPlanes3D.Add` | api7 | method | `IPlane3D Add(ksObj3dTypeEnum PlaneType)` | 2 | Подтверждено | перечень интерфейса |
| `IPlane3DByOffset.BasePlane` | api7 | property | `prop IModelObject BasePlane` | 2 | Подтверждено | перечень интерфейса |
| `IPlane3DByOffset.Offset` | api7 | property | `prop Double Offset` | 3 | Подтверждено | перечень интерфейса |
| `IPlane3DByOffset.Direction` | api7 | property | `prop Boolean Direction` | 2 | Подтверждено | перечень интерфейса |
| `IPlane3D.Update` | api7 | method | `Boolean Update()` | 5 | Подтверждено | перечень интерфейса |
| `IPlane3DByAngle.BasePlane` | api7 | property | `prop IModelObject BasePlane` | 2 | Подтверждено | перечень интерфейса |
| `IPlane3DByAngle.BaseLine` | api7 | property | `prop IModelObject BaseLine` | 3 | Подтверждено | перечень интерфейса |
| `IPlane3DByAngle.Angle` | api7 | property | `prop Double Angle` | 3 | Подтверждено | перечень интерфейса |
| `IPlane3DByAngle.Direction` | api7 | property | `prop Boolean Direction` | 2 | Подтверждено | перечень интерфейса |
| `IPlane3D.Surface` | api7 | property | `prop MathSurface3D Surface` | 1 | Подтверждено | перечень интерфейса |
| `IMathSurface3D.ParamUMin` | api7 | property | `prop Double ParamUMin` | 1 | Подтверждено | имя файла страницы |
| `IMathSurface3D.ParamVMin` | api7 | property | `prop Double ParamVMin` | 1 | Подтверждено | имя файла страницы |
| `IMathSurface3D.GetPoint` | api7 | method | `Boolean GetPoint(Double ParamU, Double ParamV, Double& X, Double& Y, D` | 1 | Подтверждено | перечень интерфейса |
| `IMathSurface3D.GetNormal` | api7 | method | `Boolean GetNormal(Double ParamU, Double ParamV, Double& X, Double& Y, ` | 1 | Подтверждено | перечень интерфейса |
| `IAxes3D.Add` | api7 | method | `IAxis3D Add(ksObj3dTypeEnum Type)` | 3 | Подтверждено | перечень интерфейса |
| `IAxis3D.Name` | api7 | property | `prop String Name` | 4 | Подтверждено | перечень интерфейса |
| `IAxis3DBy2Points.Point1` | api7 | property | `prop IModelObject Point1` | 3 | Подтверждено | перечень интерфейса |
| `IAxis3DBy2Points.Point2` | api7 | property | `prop IModelObject Point2` | 3 | Подтверждено | перечень интерфейса |
| `IAxis3D.Update` | api7 | method | `Boolean Update()` | 3 | Подтверждено | перечень интерфейса |
| `IAxis3DByConeface.Face` | api7 | property | `prop IModelObject Face` | 2 | Подтверждено | перечень интерфейса |
| `IAxis3DByEdge.Edge` | api7 | property | `prop IModelObject Edge` | 2 | Подтверждено | перечень интерфейса |
| `IAxis3D.MathCurve` | api7 | property | `prop MathCurve3D MathCurve` | 1 | Подтверждено | перечень интерфейса |
| `KompasObject.ksGetApplication7` | api5 | method | `Object ksGetApplication7()` | 1 | Подтверждено | перечень интерфейса |
| `IPart7.RebuildModel` | api7 | method | `Boolean RebuildModel(Boolean Redraw)` | 1 | Подтверждено | перечень интерфейса |
| `IChamfer.Name` | api7 | property | `prop String Name` | 1 | Подтверждено | перечень интерфейса |
| `IChamfer.BuildingType` | api7 | property | `prop ksChamferBuildingTypeEnum BuildingType` | 1 | Подтверждено | перечень интерфейса |
| `IChamfer.BaseObjects` | api7 | property | `prop Object BaseObjects` | 2 | Подтверждено | перечень интерфейса |
| `IChamfer.Distance1` | api7 | property | `prop Double Distance1` | 3 | Подтверждено | перечень интерфейса |
| `IChamfer.Angle` | api7 | property | `prop Double Angle` | 3 | Подтверждено | перечень интерфейса |
| `IChamfer.Direction` | api7 | property | `prop Boolean Direction` | 3 | Подтверждено | перечень интерфейса |
| `IChamfer.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `IModelContainer.Chamfers` | api7 | property | `prop Chamfers Chamfers` | 2 | Подтверждено | перечень интерфейса |
| `IChamfer.Distance2` | api7 | property | `prop Double Distance2` | 2 | Подтверждено | перечень интерфейса |
| `IChamfer.Tangent` | api7 | property | `prop Boolean Tangent` | 1 | Подтверждено | перечень интерфейса |
| `IModelContainer.Holes3D` | api7 | property | `prop Holes3D Holes3D` | 4 | Подтверждено | перечень интерфейса |
| `IHole3D.HoleParameters` | api7 | property | `prop IKompasAPIObject HoleParameters` | 6 | Подтверждено | перечень интерфейса |
| `IHole3D.Diameter` | api7 | property | `prop Double Diameter` | 7 | Подтверждено | перечень интерфейса |
| `IHole3D.Depth` | api7 | property | `prop Double Depth` | 3 | Подтверждено | перечень интерфейса |
| `ISpotfacingHoleParameters.SpotfacingDiameter` | api7 | property | `prop Double SpotfacingDiameter` | 3 | Подтверждено | перечень интерфейса |
| `ISpotfacingHoleParameters.SpotfacingDepth` | api7 | property | `prop Double SpotfacingDepth` | 3 | Подтверждено | перечень интерфейса |
| `ICountersinkHoleParameters.CountersinkDiameter` | api7 | property | `prop Double CountersinkDiameter` | 3 | Подтверждено | перечень интерфейса |
| `ICountersinkHoleParameters.CountersinkAngle` | api7 | property | `prop Double CountersinkAngle` | 3 | Подтверждено | перечень интерфейса |
| `ICountersinkHoleParameters.CountersinkDepth` | api7 | property | `prop Double CountersinkDepth` | 4 | Подтверждено | перечень интерфейса |
| `IHole3D.HoleType` | api7 | property | `prop ksHoleTypeEnum HoleType` | 6 | Подтверждено | перечень интерфейса |
| `IHole3D.DepthType` | api7 | property | `prop ksDepthTypeEnum DepthType` | 6 | Подтверждено | перечень интерфейса |
| `IHole3D.EndFaceType` | api7 | property | `prop ksEndFaceTypeEnum EndFaceType` | 2 | Подтверждено | перечень интерфейса |
| `IHole3D.Update` | api7 | method | `Boolean Update()` | 7 | Подтверждено | перечень интерфейса |
| `ICountersinkHoleParameters.CountersinkType` | api7 | property | `prop ksCountersinkTypeEnum CountersinkType` | 2 | Подтверждено | перечень интерфейса |
| `IHoleDisposal.BaseSurface` | api7 | property | `prop IModelObject BaseSurface` | 4 | Подтверждено | перечень интерфейса |
| `IHoleDisposal.Perpendicular` | api7 | property | `prop Boolean Perpendicular` | 3 | Подтверждено | перечень интерфейса |
| `IHoleDisposal.Point3DParamSurface` | api7 | property | `prop IKompasAPIObject Point3DParamSurface` | 2 | Подтверждено | перечень интерфейса |
| `IHoleDisposal.OffsetType` | api7 | property | `prop ksPoint3DSurfaceParamTypeEnum OffsetType` | 1 | Подтверждено | перечень интерфейса |
| `IPoint3DParamSurface.Offset1` | api7 | property | `prop Double Offset1` | 1 | Подтверждено | перечень интерфейса |
| `IPoint3DParamSurface.Offset2` | api7 | property | `prop Double Offset2` | 1 | Подтверждено | перечень интерфейса |
| `IPoint3DParamSurface.SetSurfaceObject` | api7 | method | `Boolean SetSurfaceObject(IModelObject NewVal)` | 1 | Подтверждено | перечень интерфейса |
| `IModelContainer.Rotateds` | api7 | property | `prop Rotateds Rotateds` | 4 | Подтверждено | перечень интерфейса |
| `IAxis3DBy2Points.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | перечень интерфейса |
| `IAxis3DBy2Points.Valid` | api7 | property | `prop Boolean Valid` | 1 | Подтверждено | перечень интерфейса |
| `IRotated.Profile` | api7 | property | `prop IModelObject Profile` | 2 | Подтверждено | перечень интерфейса |
| `IRotated.Axis` | api7 | property | `prop IModelObject Axis` | 2 | Подтверждено | перечень интерфейса |
| `IRotated.Direction` | api7 | property | `prop ksDirectionTypeEnum Direction` | 2 | Подтверждено | перечень интерфейса |
| `IRotated.RotatedType` | api7 | property | `prop ksRotatedTypeEnum RotatedType` | 2 | Подтверждено | перечень интерфейса |
| `IRotated.ToroidShapeType` | api7 | property | `prop Boolean ToroidShapeType` | 1 | Подтверждено | перечень интерфейса |
| `IRotated1.OperationResult` | api7 | property | `prop ksOperationResultEnum OperationResult` | 1 | Подтверждено | перечень интерфейса |
| `IThinParameters.Thin` | api7 | property | `prop Boolean Thin` | 1 | Подтверждено | перечень интерфейса |
| `IRotated.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `IModelContainer.Fillets` | api7 | property | `prop Fillets Fillets` | 4 | Подтверждено | перечень интерфейса |
| `IFillet.Radius1` | api7 | property | `prop Double Radius1` | 2 | Подтверждено | перечень интерфейса |
| `IFillet.Radius2` | api7 | property | `prop Double Radius2` | 2 | Подтверждено | перечень интерфейса |
| `IFillet.Tangent` | api7 | property | `prop Boolean Tangent` | 2 | Подтверждено | перечень интерфейса |
| `IFillet.BaseObjects` | api7 | property | `prop Object BaseObjects` | 3 | Подтверждено | перечень интерфейса |
| `IFillet.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `IBoolean.Name` | api7 | property | `prop String Name` | 1 | Подтверждено | перечень интерфейса |
| `IBoolean.BaseObject` | api7 | property | `prop IKompasAPIObject BaseObject` | 1 | Подтверждено | перечень интерфейса |
| `IBoolean.ModifyObjects` | api7 | property | `prop Object ModifyObjects` | 1 | Подтверждено | перечень интерфейса |
| `IBoolean.SaveCopyBaseObject` | api7 | property | `prop Boolean SaveCopyBaseObject` | 1 | Подтверждено | перечень интерфейса |
| `IBoolean.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `ISplitSolid.Name` | api7 | property | `prop String Name` | 1 | Подтверждено | перечень интерфейса |
| `ISplitSolid.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | перечень интерфейса |
| `ICut.Name` | api7 | property | `prop String Name` | 1 | Подтверждено | перечень интерфейса |
| `ICut.BuildingType` | api7 | property | `prop ksCutBuildingTypeEnum BuildingType` | 1 | Подтверждено | перечень интерфейса |
| `ICut.ChooseType` | api7 | property | `prop ksChooseType ChooseType` | 3 | Подтверждено | перечень интерфейса |
| `ICut.ChoosePartsType` | api7 | property | `prop ksChoosePartsType ChoosePartsType` | 2 | Подтверждено | перечень интерфейса |
| `ICut.ChooseBodies` | api7 | property | `prop Object ChooseBodies` | 3 | Подтверждено | перечень интерфейса |
| `ICut.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `IBodyReposition.Name` | api7 | property | `prop String Name` | 1 | Подтверждено | перечень интерфейса |
| `IBodyReposition.RepositionBody` | api7 | property | `prop IKompasAPIObject RepositionBody` | 1 | Подтверждено | перечень интерфейса |
| `IBodyReposition.Position` | api7 | property | `prop LocalCoordinateSystem Position` | 3 | Подтверждено | перечень интерфейса |
| `IBodyReposition.Update` | api7 | method | `Boolean Update()` | 2 | Подтверждено | перечень интерфейса |
| `ILocalCoordinateSystem.OrientationType` | api7 | property | `prop ksOrientationTypeEnum OrientationType` | 1 | Подтверждено | перечень интерфейса |
| `ILocalCoordinateSystem.LocalCSParameters` | api7 | property | `prop IKompasAPIObject LocalCSParameters` | 1 | Подтверждено | перечень интерфейса |
| `ILocalCSEulerParam.PrecessionAngle` | api7 | property | `prop Double PrecessionAngle` | 2 | Подтверждено | перечень интерфейса |
| `ILocalCSEulerParam.NutationAngle` | api7 | property | `prop Double NutationAngle` | 2 | Подтверждено | перечень интерфейса |
| `ILocalCSEulerParam.RotationAngle` | api7 | property | `prop Double RotationAngle` | 2 | Подтверждено | перечень интерфейса |
| `ILocalCoordinateSystem.ParameterType` | api7 | property | `prop ksPoint3DTypeEnum ParameterType` | 1 | Подтверждено | перечень интерфейса |
| `ILocalCoordinateSystem.Parameters` | api7 | property | `prop IKompasAPIObject Parameters` | 1 | Подтверждено | перечень интерфейса |
| `IModelContainer.Lofts` | api7 | property | `prop Lofts Lofts` | 2 | Подтверждено | перечень интерфейса |
| `ILoft.Closed` | api7 | property | `prop Boolean Closed` | 1 | Подтверждено | перечень интерфейса |
| `ILoft.BuildingType` | api7 | property | `prop ksLoftBuildingType BuildingType` | 1 | Подтверждено | перечень интерфейса |
| `IModelContainer.Shells` | api7 | property | `prop Shells Shells` | 2 | Подтверждено | перечень интерфейса |
| `IShell.Thickness` | api7 | property | `prop Double Thickness` | 1 | Подтверждено | перечень интерфейса |
| `IShell.ThinType` | api7 | property | `prop ksDirectionTypeEnum ThinType` | 1 | Подтверждено | перечень интерфейса |
| `IShell.DeletedFaces` | api7 | property | `prop Object DeletedFaces` | 1 | Подтверждено | перечень интерфейса |
| `IModelContainer.Evolutions` | api7 | property | `prop Evolutions Evolutions` | 2 | Подтверждено | перечень интерфейса |
| `ILinearPattern.InitialObjects` | api7 | property | `prop Object InitialObjects` | 1 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Axis1` | api7 | property | `prop IModelObject Axis1` | 2 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Step1` | api7 | property | `prop Double Step1` | 4 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Count1` | api7 | property | `prop Int32 Count1` | 3 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Angle1` | api7 | property | `prop Double Angle1` | 4 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Direction1` | api7 | property | `prop Boolean Direction1` | 4 | Подтверждено | перечень интерфейса |
| `ILinearPattern.BoundaryInstancesStepFactor1` | api7 | property | `prop Boolean BoundaryInstancesStepFactor1` | 2 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Axis2` | api7 | property | `prop IModelObject Axis2` | 2 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Step2` | api7 | property | `prop Double Step2` | 4 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Count2` | api7 | property | `prop Int32 Count2` | 4 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Angle2` | api7 | property | `prop Double Angle2` | 4 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Direction2` | api7 | property | `prop Boolean Direction2` | 4 | Подтверждено | перечень интерфейса |
| `ILinearPattern.BoundaryInstancesStepFactor2` | api7 | property | `prop Boolean BoundaryInstancesStepFactor2` | 2 | Подтверждено | перечень интерфейса |
| `ILinearPattern.BuildingType` | api7 | property | `prop ksLinearPatternBuildingTypeEnum BuildingType` | 2 | Подтверждено | перечень интерфейса |
| `ILinearPattern.GeometryPattern` | api7 | property | `prop Boolean GeometryPattern` | 2 | Подтверждено | перечень интерфейса |
| `ILinearPattern.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | перечень интерфейса |
| `ICircularPattern.InitialObjects` | api7 | property | `prop Object InitialObjects` | 1 | Подтверждено | перечень интерфейса |
| `ICircularPattern.Axis` | api7 | property | `prop IModelObject Axis` | 2 | Подтверждено | перечень интерфейса |
| `ICircularPattern.Count1` | api7 | property | `prop Int32 Count1` | 3 | Подтверждено | перечень интерфейса |
| `ICircularPattern.Step1` | api7 | property | `prop Double Step1` | 4 | Подтверждено | перечень интерфейса |
| `ICircularPattern.BoundaryInstancesStepFactor1` | api7 | property | `prop Boolean BoundaryInstancesStepFactor1` | 2 | Подтверждено | перечень интерфейса |
| `ICircularPattern.Count2` | api7 | property | `prop Int32 Count2` | 3 | Подтверждено | перечень интерфейса |
| `ICircularPattern.Step2` | api7 | property | `prop Double Step2` | 4 | Подтверждено | перечень интерфейса |
| `ICircularPattern.BoundaryInstancesStepFactor2` | api7 | property | `prop Boolean BoundaryInstancesStepFactor2` | 2 | Подтверждено | перечень интерфейса |
| `ICircularPattern.StepByAxis` | api7 | property | `prop Double StepByAxis` | 4 | Подтверждено | перечень интерфейса |
| `ICircularPattern.ReverseDirection` | api7 | property | `prop Boolean ReverseDirection` | 4 | Подтверждено | перечень интерфейса |
| `ICircularPattern.SaveInitialOrientation` | api7 | property | `prop Boolean SaveInitialOrientation` | 4 | Подтверждено | перечень интерфейса |
| `ICircularPattern.BuildingType` | api7 | property | `prop ksCircularPatternBuildingTypeEnum BuildingType` | 2 | Подтверждено | перечень интерфейса |
| `ICircularPattern.GeometryPattern` | api7 | property | `prop Boolean GeometryPattern` | 2 | Подтверждено | перечень интерфейса |
| `ICircularPattern.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | перечень интерфейса |
| `IMirrorPattern.InitialObjects` | api7 | property | `prop Object InitialObjects` | 1 | Подтверждено | перечень интерфейса |
| `IMirrorPattern.Plane` | api7 | property | `prop IModelObject Plane` | 2 | Подтверждено | перечень интерфейса |
| `IMirrorPattern.SaveInitialObjects` | api7 | property | `prop Boolean SaveInitialObjects` | 6 | Подтверждено | перечень интерфейса |
| `IChooseBodies7.ChooseBodiesType` | api7 | property | `prop ksChooseBodiesType ChooseBodiesType` | 1 | Подтверждено | перечень интерфейса |
| `IChooseBodies7.Bodies` | api7 | property | `prop Object Bodies` | 1 | Подтверждено | перечень интерфейса |
| `IMirrorPattern.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | перечень интерфейса |
| `IFeaturePattern.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | named_name_mismatch |
| `IMirrorPattern.GeometryPattern` | api7 | property | `prop Boolean GeometryPattern` | 1 | Подтверждено | перечень интерфейса |
| `IFeaturePattern.GeometryPattern` | api7 | property | `prop Boolean GeometryPattern` | 1 | Подтверждено | перечень интерфейса |
| `IFeaturePattern.GetExemplarsCounts` | api7 | method | `Boolean GetExemplarsCounts(Int32& Count1, Int32& Count2)` | 1 | Подтверждено | перечень интерфейса |
| `IFeaturePattern.Exemplar` | api7 | property | `prop IModelObject Exemplar` | 1 | Подтверждено | перечень интерфейса |
| `IFeaturePattern.InitialObjects` | api7 | property | `prop Object InitialObjects` | 1 | Подтверждено | перечень интерфейса |
| `IFeaturePattern.InstanceDeletedIndexes` | api7 | property | `prop Object InstanceDeletedIndexes` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingObject1.Constraints` | api7 | property | `prop Object Constraints` | 2 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.ConstraintType` | api7 | property | `prop ksConstraintTypeEnum ConstraintType` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.Partner` | api7 | property | `prop Object Partner` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.Index` | api7 | property | `prop Int32 Index` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.PartnerIndex` | api7 | property | `prop Int32 PartnerIndex` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.SegmentIndex` | api7 | property | `prop Int32 SegmentIndex` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.PartnerSegmentIndex` | api7 | property | `prop Int32 PartnerSegmentIndex` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.Valid` | api7 | property | `prop Boolean Valid` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.Value` | api7 | property | `prop Double Value` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.Expression` | api7 | property | `prop String Expression` | 1 | Подтверждено | перечень интерфейса |
| `IParametriticConstraint.Variable` | api7 | property | `prop String Variable` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingObject1.NewConstraint` | api7 | method | `ParametriticConstraint NewConstraint()` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingObject1.DeleteConstraints` | api7 | method | `Boolean DeleteConstraints()` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingObject1.ConstraintsState` | api7 | property | `prop ksObjectConstraintsStateEnum ConstraintsState` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.LineSegments` | api7 | property | `prop LineSegments LineSegments` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.Circles` | api7 | property | `prop Circles Circles` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.Arcs` | api7 | property | `prop Arcs Arcs` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.PolyLines2D` | api7 | property | `prop PolyLines2D PolyLines2D` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.Nurbses` | api7 | property | `prop Nurbses Nurbses` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.Beziers` | api7 | property | `prop Beziers Beziers` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.Rectangles` | api7 | property | `prop Rectangles Rectangles` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.Points` | api7 | property | `prop Points Points` | 1 | Подтверждено | перечень интерфейса |
| `IKompasDocument1.GetObjectId` | api7 | method | `String GetObjectId(IKompasAPIObject Object, IKompasAPIObject Parent)` | 2 | Подтверждено | перечень интерфейса |
| `IDrawingContainer.get_Objects` | api7 | method | `Object get_Objects(Object ObjType)` | 1 | Подтверждено | перечень интерфейса |
| `IKompasAPIObject.Type` | api7 | property | `prop KompasAPIObjectTypeEnum Type` | 1 | Подтверждено | перечень интерфейса |
| `IDrawingObject.DrawingObjectType` | api7 | property | `prop DrawingObjectTypeEnum DrawingObjectType` | 1 | Подтверждено | перечень интерфейса |
| `IModelObject.Name` | api7 | property | `prop String Name` | 2 | Подтверждено | перечень интерфейса |
| `ILineSegment.Angle` | api7 | property | `prop Double Angle` | 1 | Подтверждено | перечень интерфейса |
| `IArc.Xc` | api7 | property | `prop Double Xc` | 1 | Подтверждено | перечень интерфейса |
| `IArc.Yc` | api7 | property | `prop Double Yc` | 1 | Подтверждено | перечень интерфейса |
| `IArc.X1` | api7 | property | `prop Double X1` | 1 | Подтверждено | перечень интерфейса |
| `IArc.Y1` | api7 | property | `prop Double Y1` | 1 | Подтверждено | перечень интерфейса |
| `IArc.X2` | api7 | property | `prop Double X2` | 1 | Подтверждено | перечень интерфейса |
| `IArc.Y2` | api7 | property | `prop Double Y2` | 1 | Подтверждено | перечень интерфейса |
| `IArc.X3` | api7 | property | `prop Double X3` | 1 | Подтверждено | перечень интерфейса |
| `IArc.Y3` | api7 | property | `prop Double Y3` | 1 | Подтверждено | перечень интерфейса |
| `ICircle.Xc` | api7 | property | `prop Double Xc` | 1 | Подтверждено | перечень интерфейса |
| `ICircle.Yc` | api7 | property | `prop Double Yc` | 1 | Подтверждено | перечень интерфейса |
| `IPoint.X` | api7 | property | `prop Double X` | 1 | Подтверждено | перечень интерфейса |
| `IPoint.Y` | api7 | property | `prop Double Y` | 1 | Подтверждено | перечень интерфейса |
| `IPolyLine2D.PointsCount` | api7 | property | `prop Int32 PointsCount` | 1 | Подтверждено | перечень интерфейса |
| `IPolyLine2D.Closed` | api7 | property | `prop Boolean Closed` | 1 | Подтверждено | перечень интерфейса |
| `IBezier.PointsCount` | api7 | property | `prop Int32 PointsCount` | 1 | Подтверждено | перечень интерфейса |
| `IBezier.Closed` | api7 | property | `prop Boolean Closed` | 1 | Подтверждено | перечень интерфейса |
| `INurbs.PointsCount` | api7 | property | `prop Int32 PointsCount` | 1 | Подтверждено | перечень интерфейса |
| `INurbs.Closed` | api7 | property | `prop Boolean Closed` | 1 | Подтверждено | перечень интерфейса |
| `IPolyLine2D.GetPoint` | api7 | method | `Boolean GetPoint(Int32 Index, Double& X, Double& Y)` | 1 | Подтверждено | перечень интерфейса |
| `IBezier.GetPoint` | api7 | method | `Boolean GetPoint(Int32 Index, Double& XBase, Double& YBase, Double& XL` | 1 | Подтверждено | перечень интерфейса |
| `INurbs.GetPoint` | api7 | method | `Boolean GetPoint(Int32 Index, Double& X, Double& Y, Double& Weight)` | 1 | Подтверждено | перечень интерфейса |
| `ISketch.Plane` | api7 | property | `prop IModelObject Plane` | 2 | Подтверждено | перечень интерфейса |
| `ISketch.Update` | api7 | method | `Boolean Update()` | 1 | Подтверждено | перечень интерфейса |
| `IKompasDocument1.FindObjectById` | api7 | method | `IKompasAPIObject FindObjectById(String Id, IKompasAPIObject Parent)` | 1 | Подтверждено | перечень интерфейса |
| `IKompasDocument2D.ViewsAndLayersManager` | api7 | property | `prop ViewsAndLayersManager ViewsAndLayersManager` | 1 | Подтверждено | перечень интерфейса |

## 6. Типы объектов

Проверено по документированной таблице: 11, расхождений 0, констант без названного имени 26.

## 7. Находки

### F-01 - несоответствие документации (член вызывается, страницы нет)

Серьёзность: высокая.

4 COM-член(ов), вызываемых адаптером, не имеют подтверждающей страницы ни на проводе, ни в зеркале справки: ksPlacement.GetMatrix3D (HTTP 404); IMateConstraint3D.Update (HTTP 404); IMateConstraint3D.Valid (HTTP 404); IMateConstraints3D.Count (HTTP 404)

Исправление: перечень заказчику: либо документированный маршрут, либо исключение, названное как исключение

### F-02 - подкласс F-01: страницы нет по ожидаемому адресу (HTTP 404)

Серьёзность: высокая.

4 маршрут(ов) из F-01: по адресу `<интерфейс>_<член>.html` страницы нет (HTTP 404), и в перечне своего интерфейса член тоже не назван: ksPlacement.GetMatrix3D; IMateConstraint3D.Update; IMateConstraint3D.Valid; IMateConstraints3D.Count

Исправление: перечень заказчику вместе с F-01: либо документированный маршрут, либо исключение, названное как исключение

### F-06 - заявка не подтверждена вызовами (отказ сторожа)

Серьёзность: средняя.

12 строк(и): DRW-07.section_view.create.simple, DRW-07.section_view.create.stepped, DRW-07.section_view.delete, DRW-07.section_view.geometry_validation, DRW-07.section_view.read, DRW-07.section_view.rebuild, DRW-07.section_view.save_reopen, DRW-07.section_view.update, MATE-07.mate.create_transmission, SM-12.draft_from_edges.mode_dir_by_object, SM-12.incline.mode_base_face_or_plane, STEP.import

Исправление: маршрут строки, посвящённая строка приёмки или понижение заявки

### F-07 - пробел трассируемости (действие не названо строкой)

Серьёзность: низкая.

9 строк(и): DRW-07.section_view.create.simple, DRW-07.section_view.create.stepped, DRW-07.section_view.delete, DRW-07.section_view.read, DRW-07.section_view.rebuild, DRW-07.section_view.save_reopen, DRW-07.section_view.update, SM-12.draft_from_edges.mode_dir_by_object, SM-12.incline.mode_base_face_or_plane

Исправление: назвать действие в имени строки или объявить маршрут строки

## 8. Что осталось неподтверждённым

- `report_route_check` - в текущих отчётах приёмки поле режима аргументов (`args_keys`) есть, но отдельный сторож «отчёт прежней редакции прибора» не строился: перевыпуск отчёта на текущей поставке делает его неприменимым
- `com_snapshot_check` - снапшот COM-маршрутов больше не отдельный файл: инвентарь строится в самом аудите (`api-audit-routes.py`) и сверяется по времени правки исходников адаптера

