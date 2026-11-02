"""Замер F5: в каком смысле `offset_x_mm/offset_y_mm` - координата на плоскости или сдвиг от центра.

Зачем отдельным прибором. Наряд `SCENARIO_CHECKPOINT_SAVE` (§1.5) принёс отказ клиента:
`through_conic` Ø12 30° со смещением `offset_x_mm=-20` на плите 60x40x10 вернул
`hole_feature_error` с `object_error=32`. Измеренная база (M.5, HO.5) эту клетку не разделяет:
та плита построена с ЦЕНТРОМ в начале координат, а там «координата на плоскости» и «сдвиг от
центра грани» дают одно и то же место. Клиент строил плиту ОТ УГЛА, и там эти два смысла
расходятся, поэтому различает только плита от угла.

Три клетки, каждая - свой документ:
  A. плита ОТ УГЛА (0..60 x 0..40), `blind_flat` Ø12 h6, смещение (25,15);
     ось читается обратно: `center_mm` и `position_applied` называют МЕСТО отверстия.
  B. плита ОТ УГЛА, `through_conic` Ø12 30°, смещение (-20,0) - постановка клиента дословно.
  C. плита С ЦЕНТРОМ в начале координат (-30..30 x -20..20), тот же `through_conic` и то же
     смещение: режим и смещение принимаются, различается только начало отсчёта.

Вывод прибора - числа, а не оценка: он не правит продукт и не судит его.

Запуск:
  python scripts/probe-hole-offset.py --host <KompasMcp.Host.exe> --out <каталог доказательств>
"""

import argparse
import importlib.util
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import isolate_config, local_config  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_SMOKE = os.path.join(ROOT, "scripts", "mcp-smoke.py")
_spec = importlib.util.spec_from_file_location("smoke", _SMOKE)
smoke = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(smoke)

uuid = smoke.uuid
error_code = smoke.error_code

PLATE_W, PLATE_D, PLATE_H = 60.0, 40.0, 10.0
DIAMETER = 12.0
CONIC_ANGLE = 30.0
BLIND_DEPTH = 6.0
OFFSET_X = 25.0
OFFSET_Y = 15.0
CLIENT_OFFSET_X = -20.0


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def faces_of(client, doc, body):
    _e, env, _r = client.tool("kompas_read_topology", {
        "document_id": doc, "body_ref": body, "include": "faces"})
    return ((env or {}).get("result") or {}).get("faces") or []


def top_face(client, doc, body):
    """Верхняя грань: нормаль +Z, наибольшая площадь."""
    best, best_area = None, -1.0
    for face in faces_of(client, doc, body):
        normal = face.get("normal_at_center") or []
        area = face.get("area_mm2")
        if (face.get("surface_type") == "plane" and area is not None
                and len(normal) >= 3 and abs(normal[2] - 1) <= 1e-6 and area > best_area):
            best, best_area = face.get("face_ref"), area
    return best


def build_plate(client, app_id, name, origin):
    """Плита PLATE_W x PLATE_D x PLATE_H; origin - левый нижний угол эскиза.

    Возвращает (document_id, revision, face_ref, body_ref, error).
    """
    _e, env, _r = client.tool("kompas_create_document", {
        "application_id": app_id, "kind": "part", "name": name,
        "operation_id": str(uuid.uuid4())})
    doc = ((env or {}).get("result") or {}).get("document_id") or (env or {}).get("document_id")
    rev = (env or {}).get("revision_after") or 1
    if not doc:
        return None, rev, None, None, "документ не создан: %s" % error_code(env)

    _e, env, _r = client.tool("kompas_create_sketch", {
        "document_id": doc, "expected_revision": rev,
        "plane": {"base": "xy", "offset_mm": 0}, "name": name,
        "operation_id": str(uuid.uuid4())})
    sketch = ((env or {}).get("result") or {}).get("id")
    rev = (env or {}).get("revision_after") or rev
    if not sketch:
        return doc, rev, None, None, "эскиз: %s" % error_code(env)

    _e, env, _r = client.tool("kompas_edit_sketch", {
        "sketch_ref": sketch, "expected_revision": rev, "mode": "append",
        "entities": [{"kind": "rectangle", "start_mm": [origin[0], origin[1]],
                      "width_mm": PLATE_W, "height_mm": PLATE_D}],
        "operation_id": str(uuid.uuid4())})
    rev = (env or {}).get("revision_after") or rev

    _e, env, _r = client.tool("kompas_finish_sketch", {
        "sketch_ref": sketch, "require_closed_profile": False,
        "operation_id": str(uuid.uuid4())})
    rev = (env or {}).get("revision_after") or rev

    _e, env, _r = client.tool("kompas_extrude", {
        "sketch_ref": sketch, "expected_revision": rev, "operation": "base",
        "depth_mm": PLATE_H, "direction": "positive",
        "operation_id": str(uuid.uuid4())}, timeout=240)
    rev = (env or {}).get("revision_after") or rev
    if error_code(env) is not None:
        return doc, rev, None, None, "пластина: %s" % error_code(env)

    _e, env, _r = client.tool("kompas_list_bodies", {"document_id": doc})
    rows = (env or {}).get("result") or []
    body = (rows[0] if isinstance(rows, list) and rows else {}).get("body_ref")
    face = top_face(client, doc, body)
    if not face:
        return doc, rev, None, body, "верхняя грань не найдена"
    return doc, rev, face, body, None


def hole(client, doc, rev, face, args):
    call = dict(args, face_ref=face, expected_revision=rev, operation_id=str(uuid.uuid4()))
    _e, env, _r = client.tool("kompas_hole", call, timeout=300)
    res = (env or {}).get("result") or {}
    ver = (env or {}).get("verification") or {}
    checks = {c.get("name"): c for c in (ver.get("checks") or [])}
    err = (env or {}).get("error") or {}
    return {
        "mode": args.get("mode"),
        "offset": [args.get("offset_x_mm"), args.get("offset_y_mm")],
        "level": ver.get("level"),
        "error_code": error_code(env),
        "error_message": (err.get("message") if isinstance(err, dict) else None),
        "error_details_code": ((err.get("details") or {}).get("code")
                               if isinstance(err, dict) else None),
        "object_error": ((err.get("details") or {}).get("object_error")
                         if isinstance(err, dict) else None),
        "center_mm": res.get("center_mm"),
        "volume_mm3": res.get("volume_mm3"),
        "diameter_read_back_mm": res.get("diameter_read_back_mm"),
        "depth_read_back_mm": res.get("depth_read_back_mm"),
        "placement_route": res.get("placement_route"),
        "unverified_aspects": ver.get("unverified_aspects"),
        "position_applied": (checks.get("position_applied") or {}).get("passed"),
        "position_observed": (checks.get("position_applied") or {}).get("observed"),
        "position_expected": (checks.get("position_applied") or {}).get("expected"),
    }


def run_cell(client, app_id, name, origin, args):
    started = time.time()
    doc, rev, face, body, err = build_plate(client, app_id, name, origin)
    record = {"cell": name, "plate_origin_mm": list(origin), "arguments": args}
    if err:
        record["error"] = err
        record["seconds"] = round(time.time() - started, 2)
        return record
    record["hole"] = hole(client, doc, rev, face, args)
    record["seconds"] = round(time.time() - started, 2)
    client.tool("kompas_close_document", {
        "document_id": doc, "dirty_policy": "discard", "operation_id": str(uuid.uuid4())})
    return record


def main():
    host = argument("--host") or os.path.join(
        ROOT, "src", "KompasMcp.Host", "bin", "x64", "Release", "net10.0-windows",
        "KompasMcp.Host.exe")
    out = argument("--out") or os.path.join(
        ROOT, "docs", "acceptance", "evidence", "20261102-f5-hole-offset")
    os.makedirs(out, exist_ok=True)

    config = isolate_config(local_config(ROOT), out, "f5-offset")
    client = smoke.Client(host, config)
    cells = []
    try:
        client.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                   "clientInfo": {"name": "probe-hole-offset", "version": "0"}},
                    timeout=60)
        client.notify("notifications/initialized")
        _e, env, _r = client.tool("kompas_connect", {
            "mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())},
            timeout=300)
        app_id = (env or {}).get("application_id")
        if not app_id:
            raise SystemExit("КОМПАС не поднят: %s" % error_code(env))

        cells.append(run_cell(client, app_id, "f5-a-corner-blind", [0.0, 0.0], {
            "mode": "blind_flat", "diameter_mm": DIAMETER, "depth_mm": BLIND_DEPTH,
            "offset_x_mm": OFFSET_X, "offset_y_mm": OFFSET_Y}))
        cells.append(run_cell(client, app_id, "f5-b-corner-conic", [0.0, 0.0], {
            "mode": "through_conic", "diameter_mm": DIAMETER, "conic_angle_deg": CONIC_ANGLE,
            "offset_x_mm": CLIENT_OFFSET_X, "offset_y_mm": 0.0}))
        cells.append(run_cell(client, app_id, "f5-c-centre-conic",
                              [-PLATE_W / 2, -PLATE_D / 2], {
                                  "mode": "through_conic", "diameter_mm": DIAMETER,
                                  "conic_angle_deg": CONIC_ANGLE,
                                  "offset_x_mm": CLIENT_OFFSET_X, "offset_y_mm": 0.0}))
        cells.append(run_cell(client, app_id, "f5-d-corner-blind-nooffset", [0.0, 0.0], {
            "mode": "blind_flat", "diameter_mm": DIAMETER, "depth_mm": BLIND_DEPTH}))
    finally:
        try:
            client.tool("kompas_disconnect", {
                "application_id": app_id, "close_owned_application": True,
                "operation_id": str(uuid.uuid4())}, timeout=120)
        except Exception:  # noqa: BLE001
            pass
        client.close()

    payload = {"host": host, "cells": cells,
               "plate_mm": [PLATE_W, PLATE_D, PLATE_H], "diameter_mm": DIAMETER}
    path = os.path.join(out, "f5-hole-offset.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    print("\nдоказательства: %s" % path)


if __name__ == "__main__":
    main()
