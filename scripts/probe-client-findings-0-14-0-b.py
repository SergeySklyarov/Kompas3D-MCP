"""Измерение находок клиентской приёмки 0.14.0 (наряд `CLIENT_FINDINGS_0_14_0_B`).

Зачем отдельным прибором. Наряд требует СНАЧАЛА воспроизвести каждый пункт на поставке `668444a` и
только потом править код: проба прогоняется на любой сборке Host (старая поставка / новый Debug) и
пишет ОДИН JSON с ответами инструментов. Она ничего не решает за наряд - она называет измеренное.

Что измеряется:
  A. основная надпись: часть 4 `R1.drw_stamp_name` - записанные ячейки 1 и 2, затем перестроение
     видов: что стало с ячейками и что ответил инструмент;
  B. вложенный ЗАФИКСИРОВАННЫЙ компонент: часть 2 `R1.nested_placement` - отказ и его причина;
  C. габарит в ответе `kompas_measure`: часть 4 `R1.gabarit_conservative` - назван ли
     консервативный габарит обрезанной аналитической поверхности в unverified_aspects.

Границы. Работа идёт в --out (разрешённый корень конфигурации), модели посторонних каталогов не
правятся. Проба не судит сборку: коды и тексты отказа печатаются как есть.

Usage:
  python scripts/probe-client-findings-0-14-0-b.py --host <KompasMcp.Host.exe> --out <каталог> \
      [--config <json>] [--json <файл отчёта>]
"""

import argparse
import importlib.util
import json
import os
import sys
import uuid

_SMOKE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mcp-smoke.py")
_SPEC = importlib.util.spec_from_file_location("mcp_smoke", _SMOKE)
_MODULE = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_MODULE)
Client = _MODULE.Client

MODEL_NAME = "P4-STAMP-MODEL"
MODEL_MARKING = "P4-001"
STAMP_NAME_TEXT = "ЧЕРТЁЖ П4"
STAMP_DESIGNATION_TEXT = "P4-777"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--config", default=os.path.join("config", "kompas-mcp.local.json"))
    parser.add_argument("--out", required=True)
    parser.add_argument("--json", default=None)
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    client = Client(args.host, os.path.abspath(args.config))
    facts = {"host": os.path.abspath(args.host)}

    def call(tool, payload, timeout=300):
        body = dict(payload)
        if client.declares_operation_id(tool):
            body.setdefault("operation_id", str(uuid.uuid4()))
        _err, env, _raw = client.tool(tool, body, timeout=timeout)
        return env or {}

    def result(env):
        return env.get("result") or {}

    def error(env):
        return env.get("error") if isinstance(env.get("error"), dict) else {}

    def code(env):
        return error(env).get("code")

    def rev_of(doc):
        env = call("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        return result(env).get("revision") or 1

    def aspects(env):
        return (result(env).get("verification") or {}).get("unverified_aspects") or []

    def cells(doc, ids):
        env = call("kompas_get_title_block", {"document_id": doc, "cell_ids": [str(i) for i in ids]})
        return {row.get("cell_id"): row.get("read_back") for row in (result(env).get("cells") or [])}

    def draw_box(document, size, name):
        """Эскиз-прямоугольник на xy и выдавливание: тело нужно и модели штампа, и вложенному компоненту."""
        width, height = size
        sketch = call("kompas_create_sketch", {
            "document_id": document, "expected_revision": rev_of(document),
            "plane": {"base": "xy", "offset_mm": 0.0}, "name": name})
        sketch_id = result(sketch).get("id")
        call("kompas_edit_sketch", {
            "sketch_ref": sketch_id, "expected_revision": rev_of(document), "mode": "append",
            "entities": [{"kind": "rectangle", "start_mm": [-width / 2, -height / 2],
                          "width_mm": width, "height_mm": height}]})
        call("kompas_finish_sketch", {
            "sketch_ref": sketch_id, "expected_revision": rev_of(document),
            "require_closed_profile": True})
        return call("kompas_extrude", {
            "sketch_ref": sketch_id, "expected_revision": rev_of(document),
            "operation": "base", "depth_mm": 10.0, "direction": "positive",
            "end_condition": "blind"})

    try:
        call("kompas_acquire_session", {})
        connect = call("kompas_connect", {"mode": "launch", "make_visible": False})
        app = result(connect).get("application_id")
        facts["connect"] = {"application_id": app, "error": code(connect)}
        if not app:
            raise SystemExit("connect: " + str(code(connect)))

        # ── A. основная надпись: запись ячеек и ПЕРЕСТРОЕНИЕ ──────────────────────────────────────
        # Две постановки: модель С обозначением (видно, что ядро возвращает СВОЙСТВО модели) и модель
        # БЕЗ обозначения - постановка находки 2, где записанное обозначение пропадало.
        def stamp_scenario(label, marking):
            part = call("kompas_create_document", {
                "application_id": app, "kind": "part", "name": MODEL_NAME + "-" + label,
                "marking": marking})
            part_doc = result(part).get("id")
            draw_box(part_doc, (60.0, 40.0), "stamp-box-" + label)
            model_file = os.path.join(os.path.abspath(args.out), "p4-stamp-model-" + label + ".m3d")
            filed = call("kompas_save_document", {
                "document_id": part_doc, "expected_revision": rev_of(part_doc),
                "target_path": model_file})
            drw_env = call("kompas_create_document", {"application_id": app, "kind": "drawing"})
            doc = result(drw_env).get("id")
            scenario = {
                "part_id": part_doc,
                "model_file": {"path": result(filed).get("path"),
                               "bytes": result(filed).get("byte_length"), "error": code(filed)},
                "drawing": doc,
                "cells_before_views": cells(doc, [0, 1, 2, 6]),
            }
            views = call("kompas_create_drawing_views", {
                "document_id": doc, "expected_revision": rev_of(doc), "source_path": model_file,
                "projections": ["front"], "x": 200.0, "y": 200.0})
            scenario["create_views"] = {
                "error": code(views),
                "created": result(views).get("created_count"),
                "title_block_changes": result(views).get("title_block_changes"),
                "aspects": aspects(views),
                "cells": cells(doc, [1, 2]),
            }
            rows = (result(views).get("views") or [])
            view_ref = next((r.get("view_ref") for r in rows if r.get("source_path")), None)
            if view_ref:
                # Вид мог выйти за рамку листа: внутрь возвращает документированный IView.X.
                call("kompas_edit_view", {"document_id": doc, "expected_revision": rev_of(doc),
                                          "view_ref": view_ref, "x": 120.0})
            stamp = call("kompas_set_title_block", {
                "document_id": doc, "expected_revision": rev_of(doc),
                "cells": {"1": STAMP_NAME_TEXT, "2": STAMP_DESIGNATION_TEXT}})
            scenario["title_block_set"] = {
                "error": code(stamp),
                "cells": result(stamp).get("cells"),
                "aspects": aspects(stamp),
            }
            cdw = os.path.join(os.path.abspath(args.out), "p4-stamp-" + label + ".cdw")
            call("kompas_save_document", {"document_id": doc, "expected_revision": rev_of(doc),
                                          "target_path": cdw})
            call("kompas_close_document", {"document_id": doc, "dirty_policy": "save"})
            reopened = call("kompas_open_document", {"application_id": app, "path": cdw,
                                                     "access": "edit"})
            doc2 = result(reopened).get("id")
            scenario["on_disk_before_rebuild"] = {
                "path": cdw, "exists": os.path.isfile(cdw),
                "cells": cells(doc2, [1, 2]) if doc2 else None}
            listing = call("kompas_list_drawing_views", {"document_id": doc2}) if doc2 else {}
            assoc = next((v for v in (result(listing).get("views") or []) if v.get("source_path")), {})
            rebuilt = call("kompas_rebuild_drawing_views", {
                "document_id": doc2, "expected_revision": rev_of(doc2),
                "view_ref": assoc.get("view_ref")}) if doc2 else {}
            scenario["rebuild_after_reopen"] = {
                "error": code(rebuilt),
                "rebuild_returned": result(rebuilt).get("rebuild_returned"),
                "title_block_changes": result(rebuilt).get("title_block_changes"),
                "aspects": aspects(rebuilt),
                "cells_after": cells(doc2, [1, 2]) if doc2 else None,
            }
            if doc2:
                call("kompas_save_document", {"document_id": doc2,
                                              "expected_revision": rev_of(doc2),
                                              "target_path": cdw})
                call("kompas_close_document", {"document_id": doc2, "dirty_policy": "save"})
                again = call("kompas_open_document", {"application_id": app, "path": cdw,
                                                      "access": "edit"})
                doc3 = result(again).get("id")
                scenario["on_disk_after_rebuild"] = {"cells": cells(doc3, [1, 2]) if doc3 else None}
                if doc3:
                    call("kompas_close_document", {"document_id": doc3, "dirty_policy": "discard"})
            return scenario

        facts["scenario_marking"] = stamp_scenario("wm", MODEL_MARKING)
        part_id = facts["scenario_marking"].get("part_id")
        model_path = (facts["scenario_marking"].get("model_file") or {}).get("path")
        facts["scenario_no_marking"] = stamp_scenario("nm", None)

        # ── C. габарит в ответе kompas_measure ────────────────────────────────────────────────────
        bodies = result(call("kompas_list_bodies", {"document_id": part_id})) or []
        bodies = bodies if isinstance(bodies, list) else []
        if bodies:
            measured = call("kompas_measure", {
                "target_ref": bodies[0].get("body_ref"), "properties": ["bbox", "volume"]})
            facts["measure"] = {
                "error": code(measured),
                "bbox": result(measured).get("bbox"),
                "volume_mm3": result(measured).get("volume_mm3"),
                "unverified_aspects": result(measured).get("unverified_aspects"),
            }

        # ── B. вложенный ЗАФИКСИРОВАННЫЙ компонент ───────────────────────────────────────────────
        def nested_placement_probe(label, part_fixed):
            sub = result(call("kompas_create_document", {"application_id": app, "kind": "assembly"}))
            sub_id = sub.get("id")
            call("kompas_insert_component", {
                "document_id": sub_id, "expected_revision": rev_of(sub_id),
                "source_path": model_path, "fixed": part_fixed})
            sub_path = os.path.join(os.path.abspath(args.out), "sub-" + label + ".m3d")
            call("kompas_save_document", {"document_id": sub_id,
                                          "expected_revision": rev_of(sub_id),
                                          "target_path": sub_path})
            call("kompas_close_document", {"document_id": sub_id, "dirty_policy": "save"})

            top = result(call("kompas_create_document", {"application_id": app, "kind": "assembly"}))
            top_id = top.get("id")
            inserted = call("kompas_insert_component", {
                "document_id": top_id, "expected_revision": rev_of(top_id),
                "source_path": sub_path, "fixed": False})
            listing = call("kompas_list_components", {"document_id": top_id, "recursive": True})
            entries = result(listing).get("components") or []
            nested = next((row for row in entries if (row.get("depth") or 0) > 0), None)
            nested_ref = (nested or {}).get("component_ref")
            placed = call("kompas_set_component_placement", {
                "document_id": top_id, "expected_revision": rev_of(top_id),
                "component_ref": nested_ref,
                "transform": {"origin_mm": [15.0, 0.0, 0.0], "x_axis": [1.0, 0.0, 0.0],
                              "y_axis": [0.0, 1.0, 0.0]}}) if nested_ref else {}
            entry = {
                "insert_error": code(inserted),
                "nested_ref": nested_ref,
                "components": entries,
                "placement_error": code(placed),
                "placement_message": (error(placed).get("message") or "")[:600],
                "placement_details": error(placed).get("details"),
                "partial_effects": error(placed).get("partial_effects"),
            }
            if top_id:
                call("kompas_close_document", {"document_id": top_id, "dirty_policy": "discard"})
            return entry

        facts["nested_fixed"] = nested_placement_probe("fixed", True)
        facts["nested_free"] = nested_placement_probe("free", False)

        call("kompas_release_session", {})
    except SystemExit as stop:
        facts["stopped"] = str(stop)
    finally:
        report = json.dumps(facts, ensure_ascii=False, indent=1)
        print(report)
        if args.json:
            with open(args.json, "w", encoding="utf-8") as handle:
                handle.write(report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
