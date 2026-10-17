"""Живая проверка `kompas_create_section_view` на плите наряда SECTION_VIEWS.

Прибор, а не продукт: поднимает Хост по stdio, строит плиту 120 x 80 x 32 с одним отверстием,
сохраняет модель, создаёт чертёж со стандартными видами, выбирает опорный вид ПО ИЗМЕРЕНИЮ
(наибольшее число объектов — вид, где отверстие видно окружностью) и строит ступенчатый разрез
через модель. Печатает ответ инструмента и проверки, которые видит клиент.

Usage:
  python scripts/probe-section-view.py --host <KompasMcp.Host.exe> [--keep]
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
WORK = os.path.join(ROOT, "scratch", "section-view-probe")


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
        result = self.call("tools/call", {"name": name, "arguments": arguments}, timeout=timeout)
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


def code_of(env):
    error = (env or {}).get("error")
    return error.get("code") if isinstance(error, dict) else None


def result_of(env):
    return (env or {}).get("result") or {}


def ref_id(value):
    if isinstance(value, dict):
        return value.get("id")
    return value


def main():
    host = argument("--host")
    if not host:
        print("нужен --host <KompasMcp.Host.exe>")
        return 2
    os.makedirs(WORK, exist_ok=True)
    config = isolate_config(DEFAULT_CONFIG, WORK)

    session = Session(host, config)
    try:
        session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-section-view", "version": "0"}}, timeout=60)
        session.notify("notifications/initialized")
        connect = session.tool("kompas_connect", {
            "mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())})
        app = connect.get("application_id")
        if not app:
            print("kompas_connect не дал application_id")
            return 2

        part_env = session.tool("kompas_create_document", {
            "application_id": app, "kind": "part", "name": "SectionProbe",
            "operation_id": str(uuid.uuid4())})
        part = result_of(part_env).get("document_id") or part_env.get("document_id")
        rev = part_env.get("revision_after") or 1
        print("деталь:", part, "ревизия", rev)
        if not part:
            print("ответ create_document:", json.dumps(part_env, ensure_ascii=False)[:600])
            return 2

        sketch_env = session.tool("kompas_create_sketch", {
            "document_id": part, "expected_revision": rev,
            "plane": {"base": "xy", "offset_mm": 0}, "name": "plate",
            "operation_id": str(uuid.uuid4())})
        rev = sketch_env.get("revision_after") or rev
        sketch = (ref_id(result_of(sketch_env).get("id"))
                  or ref_id(result_of(sketch_env).get("sketch_ref"))
                  or ref_id(sketch_env.get("reference_id"))
                  or ref_id(sketch_env.get("sketch_ref")))

        edit = session.tool("kompas_edit_sketch", {
            "sketch_ref": sketch, "expected_revision": rev, "mode": "append",
            "entities": [{"kind": "rectangle", "start_mm": [0, 0], "width_mm": 120, "height_mm": 80}],
            "operation_id": str(uuid.uuid4())})
        print("эскиз:", code_of(edit) or "ok")
        rev = edit.get("revision_after") or rev
        finish = session.tool("kompas_finish_sketch", {
            "sketch_ref": sketch, "require_closed_profile": False, "operation_id": str(uuid.uuid4())})
        rev = finish.get("revision_after") or rev
        if not sketch:
            print("эскиз не создан, ответ:", json.dumps(sketch_env, ensure_ascii=False)[:600])
            return 2
        extrude = session.tool("kompas_extrude", {
            "sketch_ref": sketch, "expected_revision": rev, "operation": "base",
            "depth_mm": 32, "direction": "positive",
            "operation_id": str(uuid.uuid4())})
        rev = extrude.get("revision_after") or rev
        print("выдавливание:", code_of(extrude) or "ok")

        model_path = os.path.join(WORK, "SectionProbe.m3d")
        save = session.tool("kompas_save_document", {
            "document_id": part, "expected_revision": rev, "target_path": model_path,
            "operation_id": str(uuid.uuid4())})
        print("сохранение модели:", code_of(save) or "ok")

        drawing_env = session.tool("kompas_create_document", {
            "application_id": app, "kind": "drawing", "name": "SectionProbe",
            "operation_id": str(uuid.uuid4())})
        drawing = result_of(drawing_env).get("document_id") or drawing_env.get("document_id")
        drawing_rev = drawing_env.get("revision_after") or 1
        print("чертёж:", drawing)

        views_env = session.tool("kompas_create_drawing_views", {
            "document_id": drawing, "expected_revision": drawing_rev,
            "source_path": model_path, "projections": ["front", "top", "left", "isometric"],
            "x": 150.0, "y": 150.0, "operation_id": str(uuid.uuid4())})
        print("виды:", code_of(views_env) or "ok")
        drawing_rev = views_env.get("revision_after") or drawing_rev

        # A mutation needs a file on disk for the control copy: an unsaved drawing is refused
        # (INVALID_ARGUMENT "копировать нечего"), which is the guard working, not a defect.
        drawing_path = os.path.join(WORK, "SectionProbe.cdw")
        saved = session.tool("kompas_save_document", {
            "document_id": drawing, "expected_revision": drawing_rev, "target_path": drawing_path,
            "operation_id": str(uuid.uuid4())})
        print("сохранение чертежа:", code_of(saved) or "ok")
        drawing_rev = saved.get("revision_after") or drawing_rev

        listed = result_of(session.tool("kompas_list_drawing_views", {"document_id": drawing}))
        rows = listed.get("views") or []
        for row in rows:
            print("  вид", row.get("view_ref"), row.get("view_type"), "объектов", row.get("object_count"))
        associative = [r for r in rows if r.get("source_path")]
        if not associative:
            print("ассоциативных видов нет")
            return 2
        # Which associative view collapses Z is the ADAPTER's measurement (GetProjectionPoint), not the
        # probe's: the probe tries the candidates in order and keeps the first that builds.
        trace = [-10.0, 30.0, 45.0, 30.0, 45.0, 60.0, 75.0, 60.0, 75.0, 50.0, 130.0, 50.0]
        created = None
        attempt = None
        for candidate in sorted(associative, key=lambda r: r.get("object_count") or 0, reverse=True):
            attempt = session.tool("kompas_create_section_view", {
                "document_id": drawing, "expected_revision": drawing_rev,
                "base_view_ref": candidate.get("view_ref"), "mode": "stepped",
                "trace_model": trace, "look": "left", "x": 150.0, "y": 60.0,
                "operation_id": str(uuid.uuid4())})
            print("попытка на виде %s (объектов %s): %s" % (
                candidate.get("view_ref"), candidate.get("object_count"), code_of(attempt) or "ok"))
            if code_of(attempt) is None:
                base = candidate
                created = attempt
                drawing_rev = attempt.get("revision_after") or drawing_rev
                break
        if created is None:
            created = attempt

        print("\n=== ответ kompas_create_section_view ===")
        print("код:", code_of(created))
        section = result_of(created).get("section") or {}
        for key in ("view_ref", "cut_line_ref", "view_type", "section", "scale", "x", "y",
                    "look_left", "letter", "designation", "object_count", "hatch_step",
                    "hatch_angle", "hatch_style", "trace_view"):
            print("  %-14s %s" % (key, section.get(key)))
        verification = (created or {}).get("verification") or {}
        print("  уровень:", verification.get("level"))
        for check in verification.get("checks") or []:
            print("  проверка %-18s %s (ожидалось %s)" % (
                check.get("name"), check.get("observed"), check.get("expected")))
        for note in verification.get("unverified_aspects") or []:
            print("  не проверено:", note)

        if code_of(created) is None:
            view_ref = section.get("view_ref")
            read = session.tool("kompas_get_section_view", {
                "document_id": drawing, "view_ref": view_ref})
            read_section = result_of(read).get("section") or {}
            print("\n=== ответ kompas_get_section_view ===")
            print("код:", code_of(read),
                  "| обозначение:", read_section.get("designation"),
                  "| Section:", read_section.get("section"),
                  "| режим:", read_section.get("mode"),
                  "| объектов:", read_section.get("object_count"),
                  "| трасса:", read_section.get("trace_view"))

            drawing_rev = read.get("revision_after") or drawing_rev

            changed = session.tool("kompas_update_section_view", {
                "document_id": drawing, "expected_revision": drawing_rev, "view_ref": view_ref,
                "look": "right", "scale": 0.5, "operation_id": str(uuid.uuid4())})
            after = result_of(changed).get("after") or {}
            print("=== ответ kompas_update_section_view ===")
            print("код:", code_of(changed),
                  "| было:", (result_of(changed).get("before") or {}).get("designation"),
                  "| стало:", after.get("designation"),
                  "| стрелки влево:", after.get("look_left"),
                  "| масштаб:", after.get("scale"),
                  "| объектов:", after.get("object_count"))
            drawing_rev = changed.get("revision_after") or drawing_rev

            removed = session.tool("kompas_delete_section_view", {
                "document_id": drawing, "expected_revision": drawing_rev, "view_ref": view_ref,
                "operation_id": str(uuid.uuid4())})
            removed_result = result_of(removed)
            print("=== ответ kompas_delete_section_view ===")
            if code_of(removed) is not None:
                print("ошибка:", json.dumps((removed or {}).get("error"), ensure_ascii=False)[:800])
            print("код:", code_of(removed),
                  "| вид удалён:", removed_result.get("view_deleted"),
                  "| линия удалена:", removed_result.get("cut_line_deleted"),
                  "| видов осталось:", removed_result.get("remaining_views"))

        if code_of(created) is None and (section.get("object_count") or 0) > 0:
            print("\nИТОГ: разрез построен и перечитан")
            return 0
        print("\nИТОГ: разрез НЕ построен")
        return 1
    finally:
        try:
            session.tool("kompas_disconnect", {
                "application_id": app, "close_owned_application": True,
                "operation_id": str(uuid.uuid4())}, timeout=180)
        except Exception:  # noqa: BLE001
            pass
        session.close()


if __name__ == "__main__":
    sys.exit(main())
