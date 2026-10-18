"""Живая проверка габарита ВИДА чертежа (пункт 1A наряда DRAWING_SNAPSHOT_FIX).

Прибор, а не продукт: поднимает Хост по stdio, создаёт чертёж по готовой модели плиты, ставит
стандартные виды и разрез, читает габарит каждого вида (поле `gabarit_min`/`gabarit_max` ответа
`kompas_list_drawing_views`) и печатает числа. Затем ставит размер далеко в стороне от разреза и
перечитывает габарит: так измеряется, входят ли в габарит вида его размеры.

Usage:
  python scripts/probe-view-gabarit.py --host <KompasMcp.Host.exe> [--model <файл.m3d>]
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
WORK = os.path.join(ROOT, "scratch", "view-gabarit-probe")
DEFAULT_MODEL = os.path.join(ROOT, "scratch", "mcp-smoke", "section-plate.m3d")


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
        self._declares_operation_id = set()

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
        # Only the tools that DECLARE operation_id accept it; a read tool refuses it as an unknown field.
        if name in self._declares_operation_id:
            payload.setdefault("operation_id", str(uuid.uuid4()))
        result = self.call("tools/call", {"name": name, "arguments": payload}, timeout=timeout)
        return result.get("structuredContent") or result

    def load_tools(self):
        tools = self.call("tools/list", {}).get("tools") or []
        self._declares_operation_id = {
            t.get("name") for t in tools
            if "operation_id" in ((t.get("inputSchema") or {}).get("properties") or {})}

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


def views_of(session, document_id):
    env = session.tool("kompas_list_drawing_views", {"document_id": document_id})
    rows = result_of(env).get("views") or []
    if not rows:
        print("  список видов пуст, сырой ответ:", json.dumps(env, ensure_ascii=False)[:600])
    return rows


def show(views, title):
    print(title)
    for row in views:
        print("  %-16s №%-3s %-12s габарит %s .. %s" % (
            (row.get("name") or "?")[:16], row.get("number"), row.get("view_type"),
            row.get("gabarit_min"), row.get("gabarit_max")))
    return {row.get("name"): (row.get("gabarit_min"), row.get("gabarit_max")) for row in views}


def main():
    host = argument("--host")
    if not host:
        print("нужен --host <KompasMcp.Host.exe>")
        return 2
    model = argument("--model", DEFAULT_MODEL)
    if not os.path.exists(model):
        print("нет файла модели:", model, "- сначала прогоните группу --section-only")
        return 2
    os.makedirs(WORK, exist_ok=True)
    config = isolate_config(DEFAULT_CONFIG, WORK)

    session = Session(host, config)
    try:
        session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-view-gabarit", "version": "0"}}, timeout=60)
        session.notify("notifications/initialized")
        session.load_tools()
        connect = session.tool("kompas_connect", {"mode": "launch", "make_visible": False})
        app = connect.get("application_id")
        if not app:
            print("kompas_connect не дал application_id")
            return 2

        created = session.tool("kompas_create_document", {
            "application_id": app, "kind": "drawing", "name": "ViewGabaritProbe"})
        drawing = result_of(created).get("document_id") or created.get("document_id")
        revision = created.get("revision_after") or 1
        if not drawing:
            print("ответ create_document:", json.dumps(created, ensure_ascii=False)[:600])
            return 2

        env = session.tool("kompas_create_drawing_views", {
            "document_id": drawing, "expected_revision": revision, "source_path": model,
            "projections": ["front", "top", "left", "isometric"], "x": 150.0, "y": 150.0})
        revision = env.get("revision_after") or revision
        print("стандартные виды:", code_of(env) or "ok")
        if code_of(env):
            print("  отказ:", json.dumps(env.get("error"), ensure_ascii=False)[:600])

        first = show(views_of(session, drawing), "габариты после стандартных видов:")

        section_ref = None
        for candidate in sorted(views_of(session, drawing),
                                key=lambda r: r.get("object_count") or 0, reverse=True):
            env = session.tool("kompas_create_section_view", {
                "document_id": drawing, "expected_revision": revision,
                "base_view_ref": candidate.get("view_ref"), "mode": "simple",
                "trace_model": [-10.0, 30.0, 130.0, 30.0], "look": "left",
                "x": 60.0, "y": 120.0})
            revision = env.get("revision_after") or revision
            print("разрез по виду %r: %s" % (candidate.get("name"), code_of(env) or "ok"))
            if code_of(env) is None:
                section_ref = (result_of(env).get("section") or {}).get("view_ref")
                checks = (env.get("verification") or {}).get("checks") or []
                print("  проверки:", [(c.get("name"), c.get("passed"), c.get("observed"))
                                      for c in checks if "within_sheet" in str(c.get("name"))])
                break

        second = show(views_of(session, drawing), "габариты после разреза:")
        section_name = next((name for name in second if name and name not in first), None)
        before = second.get(section_name)
        print("габарит разреза до размеров:", section_name, before)

        if section_ref:
            # Размер ставится ДАЛЕКО в стороне: если он входит в габарит вида, габарит расширится.
            env = session.tool("kompas_add_dimension", {
                "document_id": drawing, "expected_revision": revision,
                "view_ref": section_ref, "dimension_type": "linear",
                "point1": [5.0, 0.0, 0.0], "point2": [5.0, 32.0, 0.0],
                "position": [-60.0, 16.0, 0.0]})
            revision = env.get("revision_after") or revision
            print("размер в стороне:", code_of(env) or "ok")
            after = show(views_of(session, drawing), "габариты после размера в стороне:")
            moved = after.get(section_name)
            print("габарит разреза после размера:", section_name, moved)
            print("ИТОГ 1A.2: размер входит в габарит вида:", moved != before)
            print("  до:     ", before)
            print("  после:  ", moved)
        else:
            print("разрез не построен - измерение габарита с размером не выполнено")

        session.tool("kompas_close_document", {"document_id": drawing, "dirty_policy": "discard"})
        return 0
    finally:
        session.close()


if __name__ == "__main__":
    sys.exit(main())
