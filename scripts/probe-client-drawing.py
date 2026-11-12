"""Scenario of the customer's drawing (README-DEVELOPER) through the real MCP session.

  python scripts/probe-client-drawing.py --host <KompasMcp.Host.exe> --model <inductor_core.m3d>
         --out <writable dir> --png <export png>

WHY IT EXISTS. The наряд `CLIENT_BUGS_0_14_0_DRAWING` must show the customer's own drawing built
again through MCP after the fixes: three associative views at 2:1, two diametral dimensions, the title
block with the name, the technical-requirements block; then save -> close -> reopen and a sheet PNG.
INVARIANT: every number printed here is READ from the document, never echoed from the request; a
refusal is printed with its code and stops the scenario, because a half-built drawing proves nothing.
History: docs/decisions/drawings.md#client-bugs-0-14-0-drawing
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import sys
import uuid

# The MCP client lives in the acceptance instrument; it is loaded by path because the file name has a
# dash. Only its `Client` is used, so the probe talks to the SAME transport the instrument does.
_SMOKE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mcp-smoke.py")
_SPEC = importlib.util.spec_from_file_location("mcp_smoke", _SMOKE)
_MODULE = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_MODULE)
Client = _MODULE.Client

NAME_TEXT = "Сердечник дросселя"
MARKING = "inductor_core"
DEMAND_TEXT = ("Сердечник дросселя.\nНаружный диаметр 23 мм. Внутренний диаметр 9 мм.\n"
               "Поверхность - тор; радиус сечения 3,5 мм.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--config", default=os.path.join("config", "kompas-mcp.local.json"))
    parser.add_argument("--model", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--png", required=True)
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    client = Client(args.host, os.path.abspath(args.config))
    facts = {}

    def call(tool, payload, timeout=300):
        body = dict(payload)
        if client.declares_operation_id(tool):
            body.setdefault("operation_id", str(uuid.uuid4()))
        _err, env, _raw = client.tool(tool, body, timeout=timeout)
        return env or {}

    def result(env):
        return env.get("result") or {}

    def code(env):
        error = env.get("error") or {}
        return error.get("code") if isinstance(error, dict) else None

    def rev_of(doc):
        env = call("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        return result(env).get("revision") or 1

    def must(step, env):
        failure = code(env)
        if failure:
            facts[step] = {"error": failure, "message": (env.get("error") or {}).get("message", "")[:400]}
            raise SystemExit("ОТКАЗ на шаге %s: %s" % (step, failure))
        return env

    # The previous owner may have released the session explicitly; then ownership is taken only on
    # request, and `kompas_connect` refuses SESSION_NOT_ACQUIRED. Acquiring twice is harmless.
    call("kompas_acquire_session", {})
    connect = must("connect", call("kompas_connect", {"mode": "launch", "make_visible": False}))
    app = result(connect).get("application_id")
    facts["connect"] = {"application_id": app, "version": result(connect).get("version")}

    opened = must("open_model", call("kompas_open_document", {
        "application_id": app, "path": os.path.abspath(args.model), "access": "read_only"}))
    facts["open_model"] = {"document_id": result(opened).get("id"),
                           "dirty": result(opened).get("dirty")}

    created = must("create_drawing", call("kompas_create_document", {"application_id": app,
                                                                    "kind": "drawing"}))
    drw = result(created).get("id")
    facts["create_drawing"] = {"document_id": drw}

    views = must("create_views", call("kompas_create_drawing_views", {
        "document_id": drw, "expected_revision": rev_of(drw),
        "source_path": os.path.abspath(args.model), "projections": ["front", "top", "left"],
        "x": 70.0, "y": 210.0, "scale": 2.0, "dx": 30.0, "dy": 45.0}))
    facts["create_views"] = {"created": result(views).get("created_count"),
                             "checks": [c.get("name") for c in (result(views).get("verification") or {}).get("checks") or []]}

    top = next((v for v in (result(views).get("views") or []) if v.get("view_type") == "vt_Standart"), {})
    front = next((v for v in (result(views).get("views") or []) if v.get("view_type") == "vt_Arbitrary"), {})
    facts["top_view"] = {"number": top.get("number"), "scale": top.get("scale")}
    facts["front_view"] = {"number": front.get("number"), "scale": front.get("scale")}

    stamp = must("title_block", call("kompas_set_title_block", {
        "document_id": drw, "expected_revision": rev_of(drw),
        "cells": {"1": NAME_TEXT, "2": MARKING, "6": "2:1"}}))
    facts["title_block"] = {"cells": result(stamp).get("cells"),
                            "level": (result(stamp).get("verification") or {}).get("level")}

    dims = {}
    # Два диаметра ставятся в РАЗНЫЕ виды: справка v24 не документирует у диаметрального размера полки,
    # поэтому position ему не назначается (сервер отказывает INVALID_ARGUMENT), и разнести надписи можно
    # только видом: наружный диаметр - на виде сверху, внутренний - на виде спереди.
    for label, radius, view in (("outer", 11.5, top), ("inner", 4.5, front)):
        env = must("dimension_" + label, call("kompas_add_dimension", {
            "document_id": drw, "expected_revision": rev_of(drw), "view_ref": view.get("view_ref"),
            "dimension_type": "diametral", "point1": [0.0, 0.0, 0.0], "value_mm": radius}))
        dims[label] = {"view": view.get("number"), **result(env).get("dimension")}
    facts["dimensions"] = dims

    refused_position = call("kompas_add_dimension", {
        "document_id": drw, "expected_revision": rev_of(drw), "view_ref": top.get("view_ref"),
        "dimension_type": "diametral", "point1": [0.0, 0.0, 0.0], "position": [26.0, 16.0, 0.0],
        "value_mm": 11.5})
    facts["dimension_position_refused"] = {
        "code": code(refused_position),
        "details": ((refused_position.get("error") or {}).get("details") or {})
        if isinstance(refused_position.get("error"), dict) else {},
    }

    demand = must("technical_demand", call("kompas_set_technical_demand", {
        "document_id": drw, "expected_revision": rev_of(drw), "text": DEMAND_TEXT}))
    demand_checks = {c.get("name"): c for c in ((result(demand).get("verification") or {}).get("checks") or [])}
    facts["technical_demand"] = {
        "is_created": result(demand).get("is_created"),
        "text_read_back": ((demand_checks.get("text_read_back") or {}).get("passed")),
        "within_sheet": (demand_checks.get("demand_within_sheet") or {}).get("observed"),
        "within_sheet_passed": (demand_checks.get("demand_within_sheet") or {}).get("passed"),
        "level": (result(demand).get("verification") or {}).get("level"),
    }

    cdw = os.path.join(args.out, "inductor_core.cdw")
    saved = must("save", call("kompas_save_document", {
        "document_id": drw, "expected_revision": rev_of(drw), "target_path": cdw}))
    facts["save"] = {"path": result(saved).get("path"), "sha256": result(saved).get("sha256"),
                     "bytes": result(saved).get("byte_length"),
                     "dirty": (result(saved).get("context") or {}).get("dirty")}

    call("kompas_close_document", {"document_id": drw, "dirty_policy": "save"})
    reopened = must("reopen", call("kompas_open_document", {
        "application_id": app, "path": cdw, "access": "edit"}))
    drw2 = result(reopened).get("id")
    facts["reopen"] = {"document_id": drw2, "dirty": result(reopened).get("dirty")}

    stamp_after = must("title_block_after_reopen",
                       call("kompas_get_title_block", {"document_id": drw2, "cell_ids": ["1", "2", "6"]}))
    facts["title_block_after_reopen"] = result(stamp_after).get("cells")

    demand_after = must("demand_after_reopen",
                        call("kompas_get_technical_demand", {"document_id": drw2}))
    facts["demand_after_reopen"] = {"text": result(demand_after).get("text"),
                                    "is_created": result(demand_after).get("is_created")}

    views_after = must("views_after_reopen", call("kompas_list_drawing_views", {"document_id": drw2}))
    after_rows = result(views_after).get("views") or []
    top_after = next((v for v in after_rows if v.get("view_type") == "vt_Standart"), {})
    front_after = next((v for v in after_rows if v.get("view_type") == "vt_Arbitrary"), {})
    dims_after = {}
    for label, view in (("outer", top_after), ("inner", front_after)):
        env = must("dims_after_reopen_" + label, call("kompas_list_dimensions", {
            "document_id": drw2, "view_ref": view.get("view_ref")}))
        dims_after[label] = result(env).get("dimensions")
    facts["dimensions_after_reopen"] = dims_after

    png = must("png", call("kompas_export_image", {
        "document_id": drw2, "expected_revision": rev_of(drw2), "format": "png",
        "long_side_px": 1600, "save_path": os.path.abspath(args.png), "return_image_content": False}))
    facts["png"] = {"path": result(png).get("save_path"), "sha256": result(png).get("sha256")}

    call("kompas_release_session", {})
    print(json.dumps(facts, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
