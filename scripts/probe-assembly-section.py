"""Проба: сечение сборки в сценарии клиента (наряд `SECTIONS_ASSEMBLY_FIX`, §2.2).

Прибор, а не продукт. Повторяет сценарий клиентской приёмки поставки `publish-v0.10.0-f679843`
(строка `section`): сборка из трёх деталей, вспомогательная ось, включённый разнесённый вид,
`kompas_create_section` со смещённой плоскостью - и меняет по ОДНОМУ фактору, чтобы назвать условие,
при котором ядро не создаёт сечение.

Факторы: число деталей (1/3), разнесённый вид (вкл/выкл), плоскость (`xz`/`xy`), смещение (25/5),
`mode` в том же вызове или отдельно, снимок `kompas_export_image` до сечения.

Печатает таблицу исходов и пишет её в JSON. Ничего в модели не удаляет и не пересоздаёт сечение.

Usage:
  python scripts/probe-assembly-section.py --host <KompasMcp.Host.exe> [--out <файл.json>]
"""

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
WORK = os.path.join(ROOT, "scratch", "assembly-section-probe")


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


class Session:
    """Один Хост по stdio."""

    def __init__(self, host, config):
        self._proc = subprocess.Popen(
            [host, "--config", config],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace",
            cwd=os.path.expanduser("~"), bufsize=1,
        )
        self._lines = queue.Queue()
        self.reads = set()
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

    def call(self, method, params=None, timeout=300):
        request_id = self._next_id
        self._next_id += 1
        payload = {"jsonrpc": "2.0", "id": request_id, "method": method}
        if params is not None:
            payload["params"] = params
        self._proc.stdin.write(json.dumps(payload) + "\n")
        self._proc.stdin.flush()
        while True:
            try:
                line = self._lines.get(timeout=timeout)
            except queue.Empty:
                raise RuntimeError("timeout waiting for %s" % method)
            if line is None:
                raise RuntimeError("stdout closed while waiting for %s" % method)
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                continue
            if message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError("%s: %s" % (method, message["error"]))
                return message.get("result")

    def notify(self, method):
        self._proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method}) + "\n")
        self._proc.stdin.flush()

    def tool(self, name, arguments, timeout=300):
        payload = dict(arguments)
        if name not in self.reads:
            payload.setdefault("operation_id", str(uuid.uuid4()))
        result = self.call("tools/call", {"name": name, "arguments": payload}, timeout=timeout)
        envelope = result.get("structuredContent") or result
        message = str(((envelope or {}).get("error") or {}).get("message") or "")
        # MEASURED: a read tool refuses `operation_id` as an unknown field while a mutation requires
        # it, and tools/list does not say which is which - the refusal names it, so it is learned here.
        if "Неизвестное поле" in message and "operation_id" in payload:
            self.reads.add(name)
            payload.pop("operation_id", None)
            result = self.call("tools/call", {"name": name, "arguments": payload}, timeout=timeout)
            envelope = result.get("structuredContent") or result
        return envelope

    def close(self):
        try:
            self._proc.stdin.close()
        except Exception:  # noqa: BLE001
            pass
        try:
            self._proc.wait(timeout=30)
        except Exception:  # noqa: BLE001
            self._proc.kill()


def code_of(envelope):
    error = (envelope or {}).get("error")
    return error.get("code") if isinstance(error, dict) else None


def result_of(envelope):
    return (envelope or {}).get("result") or {}


def ref_id(value):
    if isinstance(value, dict):
        return value.get("id")
    return value


def rev_after(envelope, fallback):
    value = (envelope or {}).get("revision_after")
    return value if isinstance(value, int) and value > 0 else fallback


VARIANTS = [
    # name, деталей, разнесение, плоскость, смещение, mode в вызове, закраска, снимок до сечения
    ("V1-client", 3, True, "xz", 25, True, None, False),
    ("V2-no-dismantle", 3, False, "xz", 25, True, None, False),
    ("V3-one-part", 1, True, "xz", 25, True, None, False),
    ("V4-xy5", 3, True, "xy", 5, True, None, False),
    ("V5-no-mode", 3, True, "xz", 25, False, None, False),
    ("V6-after-image", 3, True, "xz", 25, True, None, True),
    ("V7-fill", 3, True, "xy", 5, True, True, False),
]


def build_part(session, app):
    """Плита 40 x 40 x 10 в файле: по ней собирается сборка."""
    envelope = session.tool("kompas_create_document", {"application_id": app, "kind": "part"})
    part = result_of(envelope).get("document_id") or envelope.get("document_id")
    if not part:
        return None, "no_document"
    rev = rev_after(envelope, 1)
    sketch_env = session.tool("kompas_create_sketch",
                              {"document_id": part, "expected_revision": rev,
                               "plane": {"base": "xy"}})
    rev = rev_after(sketch_env, rev)
    sketch = (ref_id(result_of(sketch_env).get("sketch_ref"))
              or ref_id(result_of(sketch_env).get("id")))
    edit = session.tool("kompas_edit_sketch",
                        {"sketch_ref": sketch, "expected_revision": rev,
                         "mode": "append",
                         "entities": [{"kind": "rectangle", "start_mm": [0, 0],
                                       "width_mm": 40, "height_mm": 40}]})
    rev = rev_after(edit, rev)
    finish = session.tool("kompas_finish_sketch",
                          {"sketch_ref": sketch, "require_closed_profile": False})
    rev = rev_after(finish, rev)
    extrude = session.tool("kompas_extrude",
                           {"sketch_ref": sketch, "expected_revision": rev,
                            "operation": "base", "end_condition": "blind",
                            "depth_mm": 10, "direction": "positive"})
    rev = rev_after(extrude, rev)
    path = os.path.join(WORK, "probe-plate.m3d")
    save = session.tool("kompas_save_document",
                        {"document_id": part, "expected_revision": rev,
                         "target_path": path})
    return path, (code_of(extrude) or code_of(save) or "ok")


def build_assembly(session, app, part_path, parts, dismantle):
    """Сборка из `parts` экземпляров одной детали; при `dismantle` - ось и шаг разнесения.

    Ревизия ведётся по ответам вызовов (`revision_after`): `kompas_get_context` на сборке с
    включённым разнесением ревизию не отдаёт, и умолчание 1 расходится с настоящей.
    """
    envelope = session.tool("kompas_create_document",
                            {"application_id": app, "kind": "assembly", "name": "PRB"})
    asm = result_of(envelope).get("document_id") or envelope.get("document_id")
    if not asm:
        return None, None, "no_document", 1
    rev = rev_after(envelope, 1)
    origins = [[0, 0, 0], [60, 0, 0], [0, 60, 0]][:parts]
    for origin in origins:
        inserted = session.tool("kompas_insert_component",
                                {"document_id": asm, "expected_revision": rev,
                                 "source_path": part_path, "fixed": False,
                                 "transform": {"origin_mm": origin, "x_axis": [1, 0, 0],
                                               "y_axis": [0, 1, 0]}}, timeout=300)
        rev = rev_after(inserted, rev)
        if code_of(inserted):
            print("   вставка:", code_of(inserted),
                  str(((inserted or {}).get("error") or {}).get("message") or "")[:160])
    first = None
    if dismantle:
        listed = session.tool("kompas_list_components", {"document_id": asm})
        rows = result_of(listed).get("components") or []
        print("   компонентов:", len(rows), code_of(listed) or "ok")
        raw = rows[0].get("component_ref") if rows else None
        first = raw.get("id") if isinstance(raw, dict) else raw
        axis = session.tool("kompas_create_aux_geometry",
                            {"document_id": asm, "expected_revision": rev,
                             "kind": "axis", "mode": "by_2_points",
                             "point1_mm": [0, 0, 0], "point2_mm": [10, 0, 0], "name": "PRB-X"})
        axis_ref = result_of(axis).get("reference_id")
        if code_of(axis):
            print("   ось:", code_of(axis),
                  str(((axis or {}).get("error") or {}).get("message") or "")[:160])
        rev = rev_after(axis, rev)
        applied = session.tool("kompas_set_dismantle",
                               {"document_id": asm, "expected_revision": rev,
                                "mode": True,
                                "steps": [{"component_refs": [first], "distance_mm": 40,
                                           "direction_ref": axis_ref}]})
        rev = rev_after(applied, rev)
        print("   разнесение:", code_of(applied) or "ok",
              str(((applied or {}).get("error") or {}).get("message") or "")[:200],
              json.dumps(((applied or {}).get("error") or {}).get("details") or {}, ensure_ascii=False)[:200])
    return asm, first, "ok", rev


def run_variant(session, app, part_path, variant):
    name, parts, dismantle, base, offset, mode_in_call, fill, snapshot = variant
    asm, _first, setup, rev = build_assembly(session, app, part_path, parts, dismantle)
    row = {"variant": name, "parts": parts, "dismantle": dismantle, "base": base,
           "offset_mm": offset, "mode_in_call": mode_in_call, "fill_cut_planes": fill,
           "snapshot_before": snapshot, "setup": setup}
    if not asm:
        row["code"] = "no_document"
        return row

    if snapshot:
        path = os.path.join(WORK, "probe-shot-%s.bmp" % name)
        if os.path.exists(path):
            os.remove(path)
        shot = session.tool("kompas_export_image",
                            {"document_id": asm, "expected_revision": rev,
                             "format": "bmp", "resolution": 200, "save_path": path,
                             "return_image_content": False})
        row["snapshot_code"] = code_of(shot) or "ok"
        row["snapshot_bytes"] = os.path.getsize(path) if os.path.exists(path) else None
        rev = rev_after(shot, rev)

    arguments = {"document_id": asm, "expected_revision": rev,
                 "steps": [{"plane": {"base": base, "offset_mm": offset}}]}
    if fill is not None:
        arguments["fill_cut_planes"] = fill
    if mode_in_call:
        arguments["mode"] = True
    created = session.tool("kompas_create_section", arguments, timeout=300)
    body = result_of(created)
    row["code"] = code_of(created)
    row["sections_count_before"] = body.get("sections_count_before")
    row["sections_count_after"] = body.get("sections_count_after")
    row["sections"] = len(body.get("sections") or [])
    row["checks"] = {check.get("name"): check.get("passed")
                     for check in ((body.get("verification") or {}).get("checks") or [])}
    error = (created or {}).get("error") or {}
    row["message"] = str(error.get("message") or "")[:200]
    row["details"] = {key: error.get("details", {}).get(key)
                      for key in ("reason", "sections_count_before", "sections_count_after",
                                  "section_update_returned")} if error else {}

    read = session.tool("kompas_list_sections", {"document_id": asm})
    row["listed"] = result_of(read).get("sections_count")
    row["mode_after"] = result_of(read).get("mode")
    return row


def main():
    host = argument("--host")
    if not host:
        print("нужен --host <KompasMcp.Host.exe>")
        return 2
    out = argument("--out") or os.path.join(WORK, "probe.json")
    os.makedirs(WORK, exist_ok=True)
    config = isolate_config(DEFAULT_CONFIG, WORK)

    session = Session(host, config)
    try:
        session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-assembly-section", "version": "0"}}, timeout=60)
        session.notify("notifications/initialized")
        connect = session.tool("kompas_connect",
                               {"mode": "launch", "make_visible": False})
        app = connect.get("application_id")
        if not app:
            print("kompas_connect не дал application_id:", json.dumps(connect, ensure_ascii=False)[:400])
            return 2
        print("приложение:", app)


        part_path, part_code = build_part(session, app)
        print("деталь:", part_code, part_path)
        if not part_path:
            return 2

        only = argument("--only")
        rows = []
        for variant in [item for item in VARIANTS if not only or item[0].startswith(only)]:
            row = run_variant(session, app, part_path, variant)
            rows.append(row)
            print("%-16s код=%-22s до=%s после=%s сечений=%s списком=%s" % (
                row["variant"], row.get("code"), row.get("sections_count_before"),
                row.get("sections_count_after"), row.get("sections"), row.get("listed")))

        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "w", encoding="utf-8") as handle:
            json.dump({"host": host, "rows": rows}, handle, ensure_ascii=False, indent=2)
        print("отчёт:", out)
        return 0
    finally:
        session.close()


if __name__ == "__main__":
    sys.exit(main())
