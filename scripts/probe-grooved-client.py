"""Проба дефектов заказчика на сердечнике с канавками (наряд `CLIENT_BUGS_0_14_0_GROOVED`).

Зачем отдельным прибором. Три дефекта отчёта `report.md` измеряются на КОПИИ моделей заказчика, а не
пересказываются:

  1. `cut` с `direction=symmetric`, `depth_mm=0,9` на торе снимает по 0,9 мм в КАЖДУЮ сторону; проверка
     объёма обязана считать призму как площадь × глубину × 2 и назвать обе стороны. Профиль - четыре
     дуги из журнала заказчика (вызов `823bfe41-…`), то есть тот же вход, на котором получены 4,8099 и
     9,5448 мм³. Остаточное расхождение с призмой НЕ объявляется объяснённым: печатается числом.
  2. STL: два экспорта с разными `max_edge_length_mm`/`normal_angle_deg` дают одно и то же число
     треугольников, точность к сетке не применена, а уровень ответа не выдаёт габарит за геометрию.
     Глубина канавок измеряется независимо, по файлу (чистый Python, без сторонних пакетов).
  3. Габарит тела у обрезанной аналитической поверхности сравнивается с габаритом точной сетки: оба
     числа печатаются, чтобы ограничение метода было видно числом.

Границы. Модели заказчика НЕ правятся: работа идёт с копиями в `scratch/grooved/`. Прибор не судит
продукт - он называет сырые ответы и пишет их в JSON.

Usage:
  python scripts/probe-grooved-client.py --host <KompasMcp.Host.exe> --out <файл.json>
"""

import json
import math
import os
import queue
import shutil
import struct
import subprocess
import sys
import threading
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import isolate_config  # noqa: E402

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_CONFIG = os.path.join(ROOT, "config", "kompas-mcp.local.json")
HANDOFF = os.path.join(ROOT, "bugs-inbox", "inductor_core_grooved-developer-handoff")
SOURCE_MODEL = os.path.join(HANDOFF, "source", "inductor_core.m3d")
RESULT_MODEL = os.path.join(HANDOFF, "result", "inductor_core_grooved.m3d")
CLIENT_STL = os.path.join(HANDOFF, "diagnostics", "native-mcp-stl", "inductor_core_grooved.stl")
CLIENT_FINE_STL = os.path.join(HANDOFF, "diagnostics", "native-mcp-stl", "inductor_core_grooved_fine.stl")

# Четыре дуги профиля A из журнала заказчика (вызов `823bfe41-f73d-49c5-a7d1-583188c6b689`).
PROFILE_A = [
    {"kind": "arc", "center_mm": [8.0, 0.0], "radius_mm": 3.5,
     "start_deg": -75.52248781407008, "sweep_deg": 151.04497562814015},
    {"kind": "arc", "center_mm": [8.8, 3.0983866769659336], "radius_mm": 0.3,
     "start_deg": 75.52248781407008, "sweep_deg": 180},
    {"kind": "arc", "center_mm": [8.0, 0.0], "radius_mm": 2.9,
     "start_deg": 75.52248781407008, "sweep_deg": -151.04497562814015},
    {"kind": "arc", "center_mm": [8.8, -3.0983866769659336], "radius_mm": 0.3,
     "start_deg": 104.47751218592992, "sweep_deg": 180},
]

OUTER_RADIUS_MM = 11.5


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


class Session:
    """Один Host по stdio. Читатель ровно один."""

    def __init__(self, host, config):
        self._proc = subprocess.Popen(
            [host, "--config", config],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            cwd=os.path.expanduser("~"), bufsize=1,
        )
        self._lines = queue.Queue()
        threading.Thread(target=self._pump, daemon=True).start()
        self._next_id = 1

    def _pump(self):
        try:
            for line in self._proc.stdout:
                self._lines.put(line)
        except Exception:  # noqa: BLE001
            pass
        finally:
            self._lines.put(None)

    def _send(self, payload):
        self._proc.stdin.write(json.dumps(payload) + "\n")
        self._proc.stdin.flush()

    def call(self, method, params=None, timeout=600):
        request_id = self._next_id
        self._next_id += 1
        payload = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            payload["params"] = params
        self._send(payload)
        while True:
            try:
                line = self._lines.get(timeout=timeout)
            except queue.Empty:
                raise RuntimeError(f"timeout waiting for {method}")
            if line is None:
                raise RuntimeError(f"stdout closed while waiting for {method}")
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                continue
            if message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError(f"{method}: {message['error']}")
                return message.get("result")

    def notify(self, method):
        self._send({"jsonrpc": "2.0", "method": method})

    def tool(self, name, args, timeout=600):
        result = self.call("tools/call", {"name": name, "arguments": args}, timeout=timeout)
        envelope = result.get("structuredContent")
        if envelope:
            return envelope
        text = next((block.get("text") for block in (result.get("content") or [])
                     if block.get("type") == "text"), None)
        if text:
            try:
                return json.loads(text)
            except json.JSONDecodeError:
                return {"text": text}
        return result

    def mutate(self, name, args, timeout=600):
        payload = dict(args)
        payload.setdefault("operation_id", str(uuid.uuid4()))
        return self.tool(name, payload, timeout=timeout)

    def close(self):
        try:
            self._proc.stdin.close()
        except Exception:  # noqa: BLE001
            pass
        try:
            self._proc.wait(timeout=30)
        except Exception:  # noqa: BLE001
            self._proc.kill()


def body(envelope):
    return (envelope or {}).get("result") or {}


def code_of(envelope):
    error = (envelope or {}).get("error") or {}
    return error.get("code") if isinstance(error, dict) else None


def checks_of(envelope):
    """Проверки живут и в verification.checks, и на верхнем уровне result.checks (измерено)."""
    found = list(((envelope or {}).get("verification") or {}).get("checks") or [])
    found += list(body(envelope).get("checks") or [])
    return {row.get("name"): row for row in found if isinstance(row, dict)}


def aspects_of(envelope):
    found = list(((envelope or {}).get("verification") or {}).get("unverified_aspects") or [])
    found += list(body(envelope).get("unverified_aspects") or [])
    return [str(item) for item in found]


def warnings_of(envelope):
    return [str(item) for item in ((envelope or {}).get("warnings") or [])]


def revision_of(session, document_id):
    return body(session.tool("kompas_get_context", {"document_id": document_id,
                                                    "detail": "minimal"})).get("revision")


def list_bodies(session, document_id):
    rows = body(session.tool("kompas_list_bodies", {"document_id": document_id}))
    return rows if isinstance(rows, list) else []


def measure(session, reference, properties):
    return body(session.tool("kompas_measure", {"target_ref": reference,
                                                "properties": properties}))


def read_binary_stl(path):
    """Треугольники двоичного STL: список из трёх вершин на грань, без сторонних пакетов."""
    with open(path, "rb") as handle:
        raw = handle.read()
    declared = struct.unpack_from("<I", raw, 80)[0]
    count = (len(raw) - 84) // 50
    triangles = []
    for index in range(min(declared, count)):
        offset = 84 + index * 50 + 12
        vertices = [struct.unpack_from("<3f", raw, offset + 12 * vertex)
                    for vertex in range(3)]
        triangles.append(vertices)
    return triangles


def ray_hits(triangles, origin, direction):
    """Все t пересечения луча с сеткой; чистый Python, без numpy."""
    hits = []
    ox, oy, oz = origin
    dx, dy, dz = direction
    for vertices in triangles:
        ax, ay, az = vertices[0]
        bx, by, bz = vertices[1]
        cx, cy, cz = vertices[2]
        e1 = (bx - ax, by - ay, bz - az)
        e2 = (cx - ax, cy - ay, cz - az)
        hx = dy * e2[2] - dz * e2[1]
        hy = dz * e2[0] - dx * e2[2]
        hz = dx * e2[1] - dy * e2[0]
        det = e1[0] * hx + e1[1] * hy + e1[2] * hz
        if abs(det) < 1e-12:
            continue
        inv = 1.0 / det
        sx = ox - ax
        sy = oy - ay
        sz = oz - az
        u = (sx * hx + sy * hy + sz * hz) * inv
        if u < -1e-7 or u > 1 + 1e-7:
            continue
        qx = sy * e1[2] - sz * e1[1]
        qy = sz * e1[0] - sx * e1[2]
        qz = sx * e1[1] - sy * e1[0]
        v = (dx * qx + dy * qy + dz * qz) * inv
        if v < -1e-7 or u + v > 1 + 1e-7:
            continue
        t = (e2[0] * qx + e2[1] * qy + e2[2] * qz) * inv
        if t > 1e-7:
            hits.append(t)
    return sorted(hits)


def groove_depths(triangles):
    """Глубины канавок по сетке: луч из оси в каждом шаге 12° ищет ВНЕШНЮЮ поверхность.

    Ожидание заказчика: набор A - 0,6 мм (углы 0,24,…), набор B - 0,3 мм (12,36,…). Метод - тот же,
    что в `verify-grooves.py` заказчика (внешний радиус 11,5 мм минус дальнее попадание).
    """
    rows = []
    for index in range(30):
        angle = index * 12
        radians = math.radians(angle)
        direction = (math.cos(radians), 0.0, -math.sin(radians))
        hits = ray_hits(triangles, (0.0, 0.0, 0.0), direction)
        if not hits:
            rows.append({"angle_deg": angle, "set": "A" if index % 2 == 0 else "B",
                         "expected_depth_mm": 0.6 if index % 2 == 0 else 0.3,
                         "mesh_depth_mm": None, "note": "луч не встретил сетку"})
            continue
        rows.append({
            "angle_deg": angle,
            "set": "A" if index % 2 == 0 else "B",
            "expected_depth_mm": 0.6 if index % 2 == 0 else 0.3,
            "mesh_depth_mm": round(OUTER_RADIUS_MM - hits[-1], 6),
            "mesh_inner_radius_mm": round(hits[0], 6),
        })
    return rows


def mesh_facts(triangles):
    xs = [vertex[0] for face in triangles for vertex in face]
    ys = [vertex[1] for face in triangles for vertex in face]
    zs = [vertex[2] for face in triangles for vertex in face]
    return {
        "triangles": len(triangles),
        "bbox_min": [min(xs), min(ys), min(zs)],
        "bbox_max": [max(xs), max(ys), max(zs)],
        "size": [max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)],
    }


def depth_summary(rows):
    summary = {}
    for group in ("A", "B"):
        values = [row["mesh_depth_mm"] for row in rows
                  if row["set"] == group and isinstance(row["mesh_depth_mm"], (int, float))]
        if values:
            summary[group] = {"min_mm": min(values), "max_mm": max(values),
                              "count": len(values),
                              "expected_mm": 0.6 if group == "A" else 0.3}
    return summary


def probe_symmetric_cut(session, app_id, work, out):
    """Дефект 1: симметричный вырез на торе, профиль и глубина из журнала заказчика."""
    copy = os.path.join(work, "source-copy.m3d")
    shutil.copy2(SOURCE_MODEL, copy)
    opened = session.mutate("kompas_open_document", {"application_id": app_id, "path": copy,
                                                     "access": "edit"})
    document_id = (opened or {}).get("document_id")
    out["cut"] = {"open_code": code_of(opened), "document_id": document_id}
    if not document_id:
        return

    rows = list_bodies(session, document_id)
    body_ref = rows[0].get("body_ref") if rows else None
    out["cut"]["body_ref"] = body_ref
    context = body(session.tool("kompas_get_context", {"document_id": document_id,
                                                       "detail": "full"}))
    out["cut"]["gabarit_before"] = context.get("gabarit")
    volume_before = measure(session, body_ref, ["volume", "bbox"])
    out["cut"]["measure_before"] = volume_before

    created = session.mutate("kompas_create_sketch", {
        "document_id": document_id, "expected_revision": revision_of(session, document_id),
        "plane": {"base": "xy", "offset_mm": 0.0},
        "name": "A: глубина 0.6, ширина 0.9"})
    out["cut"]["create_sketch_code"] = code_of(created)
    listing = body(session.tool("kompas_list_sketches", {"document_id": document_id}))
    sketch_ref = next((row.get("sketch_ref") for row in listing.get("rows") or []
                       if row.get("name", "").startswith("A:")), None)
    out["cut"]["sketch_ref"] = sketch_ref
    if not sketch_ref:
        out["cut"]["error"] = "эскиз профиля A не найден в перечислении"
        session.mutate("kompas_close_document", {"document_id": document_id,
                                                 "dirty_policy": "discard"})
        return

    edited = session.mutate("kompas_edit_sketch", {
        "sketch_ref": sketch_ref, "expected_revision": revision_of(session, document_id),
        "mode": "append", "entities": PROFILE_A})
    out["cut"]["edit_sketch_code"] = code_of(edited)
    finished = session.mutate("kompas_finish_sketch", {"sketch_ref": sketch_ref,
                                                       "require_closed_profile": True})
    out["cut"]["finish_sketch_code"] = code_of(finished)
    out["cut"]["finish_sketch_result"] = body(finished)

    cut = session.mutate("kompas_extrude", {
        "sketch_ref": sketch_ref, "expected_revision": revision_of(session, document_id),
        "operation": "cut", "direction": "symmetric", "end_condition": "blind",
        "depth_mm": 0.9, "target_body_ref": body_ref})
    checks = checks_of(cut)
    out["cut"]["envelope"] = cut
    out["cut"]["code"] = code_of(cut)
    out["cut"]["expected_basis"] = (checks.get("expected_basis") or {}).get("observed")
    out["cut"]["volume_delta_check"] = checks.get("volume_delta")
    out["cut"]["volume_upper_bound_check"] = checks.get("volume_upper_bound")
    out["cut"]["volume_delta_mm3"] = body(cut).get("volume_delta_mm3")
    out["cut"]["unverified_aspects"] = aspects_of(cut)
    out["cut"]["warnings"] = warnings_of(cut)
    out["cut"]["level"] = ((cut or {}).get("verification") or {}).get("level") or \
        body(cut).get("reached_level")

    volume_after = measure(session, body_ref, ["volume", "bbox"])
    out["cut"]["measure_after"] = volume_after
    before = volume_before.get("volume_mm3")
    after = volume_after.get("volume_mm3")
    if isinstance(before, (int, float)) and isinstance(after, (int, float)):
        removed = before - after
        bound = None
        delta_check = checks.get("volume_delta") or checks.get("volume_upper_bound")
        if delta_check and isinstance(delta_check.get("expected"), str):
            try:
                bound = float(delta_check["expected"].replace("≤ ", "").replace(",", "."))
            except ValueError:
                bound = None
        out["cut"]["removed_mm3"] = removed
        out["cut"]["prism_bound_mm3"] = bound
        out["cut"]["residual_to_bound_mm3"] = None if bound is None else bound - removed
        out["cut"]["residual_note"] = (
            "остаток НЕ объявлен объяснённым: призма профиля - верхняя граница, точный объём "
            "пересечения инструмента с телом документированным маршрутом не читается")

    for name, port in (("step", os.path.join(work, "torus-cut.step")),
                       ("stl", os.path.join(work, "torus-cut.stl"))):
        if name == "step":
            answer = session.mutate("kompas_export_step", {
                "document_id": document_id, "expected_revision": revision_of(session, document_id),
                "output_path": port})
        else:
            answer = session.mutate("kompas_export_stl", {
                "document_id": document_id, "expected_revision": revision_of(session, document_id),
                "target_path": port, "binary": True, "max_edge_length_mm": 0.05,
                "normal_angle_deg": 2.0})
        out["cut"]["export_" + name] = {
            "code": code_of(answer), "bytes": os.path.getsize(port) if os.path.exists(port) else None,
            "triangle_count": body(answer).get("triangle_count"),
            "mesh_step_mm": body(answer).get("mesh_step_mm"),
            "level": body(answer).get("reached_level"),
        }

    session.mutate("kompas_close_document", {"document_id": document_id,
                                             "dirty_policy": "discard"})


def probe_result_model(session, app_id, work, out):
    """Дефекты 2 и 3: STL заказчика и габарит обрезанного тела."""
    copy = os.path.join(work, "result-copy.m3d")
    shutil.copy2(RESULT_MODEL, copy)
    opened = session.mutate("kompas_open_document", {"application_id": app_id, "path": copy,
                                                     "access": "edit"})
    document_id = (opened or {}).get("document_id")
    out["stl"] = {"open_code": code_of(opened), "document_id": document_id}
    if not document_id:
        return

    rows = list_bodies(session, document_id)
    body_ref = rows[0].get("body_ref") if rows else None
    context = body(session.tool("kompas_get_context", {"document_id": document_id,
                                                       "detail": "full"}))
    out["gabarit"] = {
        "context_gabarit": context.get("gabarit"),
        "measure": measure(session, body_ref, ["bbox", "volume"]),
    }

    exports = {}
    for label, length, angle, name in (("coarse", 0.05, 2.0, "result-coarse.stl"),
                                       ("fine", 0.01, 0.2, "result-fine.stl")):
        port = os.path.join(work, name)
        answer = session.mutate("kompas_export_stl", {
            "document_id": document_id, "expected_revision": revision_of(session, document_id),
            "target_path": port, "binary": True, "max_edge_length_mm": length,
            "normal_angle_deg": angle})
        exports[label] = {
            "path": port,
            "code": code_of(answer),
            "triangle_count": body(answer).get("triangle_count"),
            "applied_length_mm": body(answer).get("applied_length_mm"),
            "applied_angle_deg": body(answer).get("applied_angle_deg"),
            "mesh_step_mm": body(answer).get("mesh_step_mm"),
            "box_tolerance_mm": body(answer).get("box_tolerance_mm"),
            "triangle_box": body(answer).get("triangle_box"),
            "body_box": body(answer).get("body_box"),
            "box_deviation_mm": body(answer).get("box_deviation_mm"),
            "level": body(answer).get("reached_level"),
            "checks": checks_of(answer),
            "unverified_aspects": aspects_of(answer),
            "warnings": warnings_of(answer),
        }
    out["stl"]["exports"] = exports

    step_path = os.path.join(work, "result.step")
    answer = session.mutate("kompas_export_step", {
        "document_id": document_id, "expected_revision": revision_of(session, document_id),
        "output_path": step_path})
    out["stl"]["export_step"] = {"code": code_of(answer),
                                 "bytes": os.path.getsize(step_path) if os.path.exists(step_path)
                                 else None}

    session.mutate("kompas_close_document", {"document_id": document_id,
                                             "dirty_policy": "discard"})


def main():
    host = os.path.abspath(argument("--host", os.path.join(
        ROOT, "src", "KompasMcp.Host", "bin", "x64", "Debug", "net10.0-windows",
        "KompasMcp.Host.exe")))
    out_path = os.path.abspath(argument("--out", os.path.join(ROOT, "scratch", "grooved",
                                                              "probe.json")))
    work = os.path.dirname(out_path)
    os.makedirs(work, exist_ok=True)
    for source in (SOURCE_MODEL, RESULT_MODEL):
        if not os.path.exists(source):
            raise SystemExit(f"нет модели заказчика: {source}")

    config_dir = os.path.join(work, "config")
    os.makedirs(config_dir, exist_ok=True)
    config = isolate_config(DEFAULT_CONFIG, config_dir)

    session = Session(host, config)
    out = {"host": host, "work": work}
    try:
        session.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "probe-grooved", "version": "1"}})
        session.notify("notifications/initialized")
        connect = session.mutate("kompas_connect", {"mode": "launch", "make_visible": False})
        app_id = (connect or {}).get("application_id")
        out["connect_code"] = code_of(connect)
        out["application_id"] = app_id
        if not app_id:
            raise SystemExit("нет application_id: подключение не состоялось")

        probe_symmetric_cut(session, app_id, work, out)
        probe_result_model(session, app_id, work, out)
    finally:
        session.close()
        # Независимое измерение сеток: чистый Python, без сторонних пакетов.
        meshes = {}
        for label, path in (("client_shipped", CLIENT_STL),
                            ("client_shipped_fine", CLIENT_FINE_STL),
                            ("mcp_coarse", os.path.join(work, "result-coarse.stl")),
                            ("mcp_fine", os.path.join(work, "result-fine.stl"))):
            if not os.path.exists(path):
                meshes[label] = {"path": path, "missing": True}
                continue
            triangles = read_binary_stl(path)
            rows = groove_depths(triangles)
            meshes[label] = {"path": path, "facts": mesh_facts(triangles),
                             "groove_depths": depth_summary(rows),
                             "groove_rows": rows}
        out["meshes"] = meshes
        with open(out_path, "w", encoding="utf-8") as handle:
            json.dump(out, handle, ensure_ascii=False, indent=1)
        summary = {
            "out": out_path,
            "cut_code": (out.get("cut") or {}).get("code"),
            "cut_basis": (out.get("cut") or {}).get("expected_basis"),
            "cut_removed": (out.get("cut") or {}).get("removed_mm3"),
            "cut_bound": (out.get("cut") or {}).get("prism_bound_mm3"),
            "cut_residual": (out.get("cut") or {}).get("residual_to_bound_mm3"),
            "stl_triangles": {key: (value or {}).get("triangle_count")
                              for key, value in ((out.get("stl") or {}).get("exports") or {}).items()},
            "groove_depths": {key: value.get("groove_depths")
                              for key, value in meshes.items() if not value.get("missing")},
        }
        print(json.dumps(summary, ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main()
