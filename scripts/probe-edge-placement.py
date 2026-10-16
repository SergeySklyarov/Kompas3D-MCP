"""Холодная серия размещения: оболочка, отверстие и наклонная вспомогательная плоскость.

Зачем отдельным прибором. Наряд `DEBT_EDGE_PLACEMENT` (долг 23) закрывает класс «поверхность оказалась
не по запрошенной грани» у трёх операций, адресующих грань или прямую ССЫЛКОЙ. Строки приёмки ведут
ВЕРНЫЙ исход и отказ на исчезнувшей ссылке; проверить, что проверка размещения не даёт ЛОЖНЫХ отказов,
можно только серией: каждая setup исполняется в СВОЁМ экземпляре КОМПАС (свой Хост на опыт), и ни
один верный вызов не должен быть отвергнут.

Что измеряется на опыт:
  setup  - что построено (пластина, грань/прямая, режим);
  refused     - отвергнут ли ВЕРНЫЙ вызов (ожидание: нет);
  placement   - исход проверки размещения в ответе продукта (confirmed / unverified / refusal);
  measured    - признак, прочитанный ИЗ МОДЕЛИ независимым инструментом, а не из ответа создания.

Границы. Проба не правит продукт и не судит его: она называет ответы. Отказ на верном вызове —
единственное, что делает опыт неуспешным.

Usage:
  python scripts/probe-edge-placement.py --host <KompasMcp.Host.exe> --iterations 20 --out <файл>
  python scripts/probe-edge-placement.py --host ... --only shell-inward --out <файл>   # одна постановка

Постановки: shell-inward, shell-outward, hole-blind_flat, hole-through_counterbore, aux-plane_angle.
Каждая может идти СВОИМ процессом (свой каталог --out, своя изолированная конфигурация): серия
занимает десятки минут, и постановки друг от друга не зависят.

Журналы прибора изолированы: Хост получает КОПИЮ конфигурации с перенаправленными путями
(`localconfig.isolate_config`), иначе проба затирала бы журнал клиентского сеанса.
"""

import datetime
import hashlib
import json
import os
import queue
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

PLATE_W, PLATE_D, PLATE_H = 100.0, 80.0, 10.0
PLATE_AREA = PLATE_W * PLATE_D


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


class Session:
    """Один Хост по stdio. Читатель ровно один."""

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

    def call(self, method, params=None, timeout=300):
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
                raise RuntimeError("timeout waiting for %s" % method)
            if line is None:
                raise RuntimeError("stdout closed while waiting for %s" % method)
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == request_id:
                if "error" in msg:
                    raise RuntimeError("%s: %s" % (method, msg["error"]))
                return msg.get("result")

    def notify(self, method):
        self._send({"jsonrpc": "2.0", "method": method})

    def tool(self, name, args, timeout=300):
        result = self.call("tools/call", {"name": name, "arguments": args}, timeout=timeout)
        return result.get("structuredContent") or result

    def close(self):
        try:
            self._proc.stdin.close()
        except Exception:  # noqa: BLE001
            pass
        try:
            self._proc.wait(timeout=30)
        except Exception:  # noqa: BLE001
            self._proc.kill()


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def code_of(env):
    error = (env or {}).get("error")
    return error.get("code") if isinstance(error, dict) else None


def details_of(env):
    error = (env or {}).get("error")
    return (error.get("details") or {}) if isinstance(error, dict) else {}


def result_of(env):
    return (env or {}).get("result") or {}


def checks_of(env):
    return {c.get("name"): c for c in ((env or {}).get("verification") or {}).get("checks") or []}


def ref_id(value):
    """Ссылка приходит и строкой, и объектом `{id: ...}`: инструментам нужна строка."""
    if isinstance(value, dict):
        return value.get("id")
    return value


class One:
    """Один опыт: свой Хост, свой КОМПАС, свой документ."""

    def __init__(self, host, config):
        self.host = host
        self.config = config
        self.session = None
        self.app = None

    def __enter__(self):
        self.session = Session(self.host, self.config)
        self.session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-edge-placement", "version": "0"}}, timeout=60)
        self.session.notify("notifications/initialized")
        connect = self.session.tool("kompas_connect", {
            "mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())}, timeout=300)
        self.app = connect.get("application_id")
        if not self.app:
            raise RuntimeError("kompas_connect не вернул application_id")
        return self

    def __exit__(self, *_exc):
        try:
            if self.app:
                self.session.tool("kompas_disconnect", {
                    "application_id": self.app, "close_owned_application": True,
                    "operation_id": str(uuid.uuid4())}, timeout=180)
        except Exception:  # noqa: BLE001
            pass
        self.session.close()
        return False

    def tool(self, name, args, timeout=300):
        return self.session.tool(name, args, timeout=timeout)

    # ------------------------------------------------------------------ построители

    def new_part(self, name):
        env = self.tool("kompas_create_document", {
            "application_id": self.app, "kind": "part", "name": name,
            "operation_id": str(uuid.uuid4())})
        doc = result_of(env).get("document_id") or env.get("document_id")
        if not doc:
            raise RuntimeError("документ не создан: %s" % code_of(env))
        return doc, (env.get("revision_after") or 1)

    def build_plate(self, doc, rev, name):
        """Пластина 100×80×10 и верхняя грань (нормаль +Z, площадь 8000)."""
        env = self.tool("kompas_create_sketch", {
            "document_id": doc, "expected_revision": rev,
            "plane": {"base": "xy", "offset_mm": 0}, "name": name + "-s",
            "operation_id": str(uuid.uuid4())})
        rev = env.get("revision_after") or rev
        sketch = ref_id(result_of(env).get("id")) or ref_id(env.get("reference_id"))
        if not sketch:
            raise RuntimeError("эскиз не создан: %s" % code_of(env))
        env = self.tool("kompas_edit_sketch", {
            "sketch_ref": sketch, "expected_revision": rev, "mode": "append",
            "entities": [{"kind": "rectangle", "start_mm": [-PLATE_W / 2, -PLATE_D / 2],
                          "width_mm": PLATE_W, "height_mm": PLATE_D}],
            "operation_id": str(uuid.uuid4())})
        rev = env.get("revision_after") or rev
        env = self.tool("kompas_finish_sketch", {
            "sketch_ref": sketch, "require_closed_profile": False,
            "operation_id": str(uuid.uuid4())})
        rev = env.get("revision_after") or rev
        env = self.tool("kompas_extrude", {
            "sketch_ref": sketch, "expected_revision": rev, "operation": "base",
            "depth_mm": PLATE_H, "direction": "positive",
            "operation_id": str(uuid.uuid4())})
        rev = env.get("revision_after") or rev
        if code_of(env) is not None:
            raise RuntimeError("пластина не выдавлена: %s" % code_of(env))
        listed = result_of(self.tool("kompas_list_bodies", {"document_id": doc}))
        rows = listed if isinstance(listed, list) else (listed.get("bodies") or [])
        body = (rows or [{}])[0].get("body_ref")
        if not body:
            raise RuntimeError("тело не найдено после выдавливания")
        return rev, body, self.top_face(doc, body)

    def faces(self, doc, body):
        env = self.tool("kompas_read_topology", {
            "document_id": doc, "body_ref": body, "include": "faces"})
        return result_of(env).get("faces") or []

    def top_face(self, doc, body):
        best, best_area = None, -1.0
        for face in self.faces(doc, body):
            normal = face.get("normal_at_center") or []
            area = face.get("area_mm2")
            if (face.get("surface_type") == "plane" and area is not None and len(normal) >= 3
                    and abs(float(normal[2]) - 1.0) <= 1e-6 and area > best_area):
                best, best_area = face.get("face_ref"), area
        return best

    def coplanar_top_faces(self, doc, body):
        """Плоские грани тела, лежащие в плоскости запрошенной верхней грани (нормаль +Z, z=10)."""
        found = []
        for face in self.faces(doc, body):
            normal = face.get("normal_at_center") or []
            if (face.get("surface_type") != "plane" or len(normal) < 3
                    or abs(abs(float(normal[2])) - 1.0) > 1e-6):
                continue
            found.append({"face_ref": face.get("face_ref"), "area_mm2": face.get("area_mm2")})
        return found


# ---------------------------------------------------------------------------- постановки

def shell_experiment(one, index, direction):
    doc, rev = one.new_part("EP-shell-%02d-%s" % (index, direction))
    rev, body, top = one.build_plate(doc, rev, "ep-shell-%02d" % index)
    if not top:
        raise RuntimeError("верхняя грань не найдена")
    env = one.tool("kompas_shell", {
        "document_id": doc, "face_refs": [top], "thickness_mm": 2.0,
        "thin_direction": direction, "expected_revision": rev,
        "operation_id": str(uuid.uuid4())})
    checks = checks_of(env)
    placement = checks.get("requested_face_removed")
    after = one.coplanar_top_faces(doc, body)
    return {
        "refused": code_of(env) is not None,
        "error": code_of(env),
        "message": ((env or {}).get("error") or {}).get("message") if code_of(env) else None,
        "placement_check": placement,
        "faces_in_requested_plane": after,
        "face_count": len(one.faces(doc, body)),
        "level": ((env or {}).get("verification") or {}).get("level"),
    }


def hole_experiment(one, index, mode):
    doc, rev = one.new_part("EP-hole-%02d-%s" % (index, mode))
    rev, body, top = one.build_plate(doc, rev, "ep-hole-%02d" % index)
    if not top:
        raise RuntimeError("верхняя грань не найдена")
    args = {"face_ref": top, "expected_revision": rev, "mode": mode,
            "diameter_mm": 10.0, "offset_x_mm": 25.0, "offset_y_mm": 15.0,
            "operation_id": str(uuid.uuid4())}
    if mode == "blind_flat":
        args["depth_mm"] = 6.0
    else:
        args["counterbore_diameter_mm"] = 18.0
        args["counterbore_depth_mm"] = 4.0
    env = one.tool("kompas_hole", args)
    checks = checks_of(env)
    cylinders = [f for f in one.faces(doc, body)
                 if f.get("surface_type") == "cylinder" and f.get("radius_mm") is not None
                 and abs(float(f["radius_mm"]) - 5.0) <= 0.01]
    return {
        "refused": code_of(env) is not None,
        "error": code_of(env),
        "message": ((env or {}).get("error") or {}).get("message") if code_of(env) else None,
        "placement_check": checks.get("axis_on_requested_face"),
        "cylinders_measured": [
            {"center_mm": c.get("center_mm"), "axis_mm": c.get("axis_mm"),
             "height_mm": c.get("height_mm"), "radius_mm": c.get("radius_mm")}
            for c in cylinders],
        "level": ((env or {}).get("verification") or {}).get("level"),
    }


def aux_experiment(one, index):
    doc, rev = one.new_part("EP-aux-%02d" % index)
    env = one.tool("kompas_create_aux_geometry", {
        "document_id": doc, "expected_revision": rev, "kind": "axis", "mode": "by_2_points",
        "point1_mm": [0.0, 0.0, 0.0], "point2_mm": [0.0, 20.0, 0.0], "name": "ep-axis",
        "operation_id": str(uuid.uuid4())})
    rev = env.get("revision_after") or rev
    if code_of(env) is not None:
        raise RuntimeError("ось не создана: %s" % code_of(env))
    axis = ref_id(result_of(env).get("reference_id"))
    env = one.tool("kompas_create_aux_geometry", {
        "document_id": doc, "expected_revision": rev, "kind": "plane", "mode": "angle",
        "base_plane": "xy", "base_axis_ref": axis, "angle_deg": 30.0, "name": "ep-tilted",
        "operation_id": str(uuid.uuid4())})
    checks = checks_of(env)
    rows = result_of(one.tool("kompas_list_aux_geometry", {
        "document_id": doc, "include": "all"})).get("rows") or []
    tilted = next((r for r in rows if r.get("name") == "ep-tilted"), None)
    return {
        "refused": code_of(env) is not None,
        "error": code_of(env),
        "message": ((env or {}).get("error") or {}).get("message") if code_of(env) else None,
        "angle_check": checks.get("angle_read_back"),
        "line_check": checks.get("base_line_read_back"),
        "measured": {"angle_deg": (tilted or {}).get("angle_deg"),
                     "line_name": (tilted or {}).get("line_name"),
                     "name": (tilted or {}).get("name")},
    }


def series(host, config, iterations, workdir, label, run):
    records = []
    for index in range(1, iterations + 1):
        record = {"index": index, "setup": label}
        try:
            with One(host, config) as one:
                record.update(run(one, index))
            record["ok"] = not record.get("refused", False)
        except Exception as exc:  # noqa: BLE001
            record["ok"] = False
            record["instrument_error"] = "%s: %s" % (type(exc).__name__, exc)
        records.append(record)
        print("[%s %02d/%02d] %s" % (label, index, iterations,
                                     json.dumps({k: v for k, v in record.items()
                                                 if k not in ("setup",)},
                                                ensure_ascii=False)[:240]), flush=True)
    return records


def summarize(records):
    refused = [r["index"] for r in records if r.get("refused")]
    broken = [r["index"] for r in records if r.get("instrument_error")]
    placement = {}
    for record in records:
        check = record.get("placement_check") or record.get("angle_check")
        key = "нет" if check is None else str(check.get("passed"))
        placement[key] = placement.get(key, 0) + 1
    return {"experiments": len(records), "refused_on_correct_call": refused,
            "instrument_errors": broken, "placement_check_passed": placement}


def main():
    host = os.path.abspath(argument("--host") or "")
    iterations = int(argument("--iterations") or 20)
    out = os.path.abspath(argument("--out") or os.path.join(
        ROOT, "scratch", "edge-placement", "probe-edge-placement.json"))
    config = os.path.abspath(argument("--config") or DEFAULT_CONFIG)
    isolated = isolate_config(config, os.path.dirname(out), "probe-edge-placement")
    workdir = os.path.join(os.path.dirname(out), "models")
    os.makedirs(workdir, exist_ok=True)

    report = {
        "title": "Холодная серия размещения: оболочка, отверстие, наклонная плоскость",
        "host_path": host,
        "host_sha256": sha256(host) if host and os.path.exists(host) else None,
        "config_path": isolated,
        "iterations_per_setup": iterations,
        "when": datetime.datetime.now().isoformat(timespec="seconds"),
        "setups": {},
    }

    plans = (
        ("shell-inward", lambda one, i: shell_experiment(one, i, "inward")),
        ("shell-outward", lambda one, i: shell_experiment(one, i, "outward")),
        ("hole-blind_flat", lambda one, i: hole_experiment(one, i, "blind_flat")),
        ("hole-through_counterbore", lambda one, i: hole_experiment(one, i, "through_counterbore")),
        ("aux-plane_angle", lambda one, i: aux_experiment(one, i)),
    )
    only = argument("--only")
    for label, run in plans:
        if only and label != only:
            continue
        report["setups"][label] = series(host, isolated, iterations, workdir, label, run)

    report["summary"] = {label: summarize(records)
                         for label, records in report["setups"].items()}

    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))
    print("\nЗаписано: " + out)


if __name__ == "__main__":
    main()
