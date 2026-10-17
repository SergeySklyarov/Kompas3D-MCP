"""Живая проверка снимка СВЕЖЕСОЗДАННОГО чертежа (F-01 наряда DRAWING_SNAPSHOT_FIX).

Прибор, а не продукт: поднимает Хост по stdio, создаёт ПУСТОЙ чертёж, снимает его через
`kompas_export_image` ДО любого сохранения, затем сохраняет, закрывает и открывает заново и
снимает повторно. Печатает коды ответов и размеры растра - то, что видит клиент.

Usage:
  python scripts/probe-drawing-snapshot.py --host <KompasMcp.Host.exe> [--keep]
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
WORK = os.path.join(ROOT, "scratch", "drawing-snapshot-probe")


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
        payload = dict(arguments)
        payload.setdefault("operation_id", str(uuid.uuid4()))
        result = self.call("tools/call", {"name": name, "arguments": payload}, timeout=timeout)
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


def shoot(session, document_id, revision, label):
    path = os.path.join(WORK, "snapshot-%s.png" % label)
    if os.path.exists(path):
        os.remove(path)
    env = session.tool("kompas_export_image", {
        "document_id": document_id, "expected_revision": revision,
        "format": "png", "save_path": path})
    shot = result_of(env)
    size = os.path.getsize(path) if os.path.exists(path) else 0
    print("%-10s code=%s пиксели=%sx%s байт=%s файл=%s"
          % (label, code_of(env), shot.get("pixel_width"), shot.get("pixel_height"),
             shot.get("bytes_count"), size))
    if code_of(env):
        print("           отказ:", json.dumps((env or {}).get("error"), ensure_ascii=False)[:600])
    return code_of(env), shot, size


def main():
    host = argument("--host")
    if not host:
        print("нужен --host <KompasMcp.Host.exe>")
        return 2
    os.makedirs(WORK, exist_ok=True)
    config = isolate_config(DEFAULT_CONFIG, WORK)

    session = Session(host, config)
    failures = 0
    try:
        session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-drawing-snapshot", "version": "0"}}, timeout=60)
        session.notify("notifications/initialized")
        connect = session.tool("kompas_connect", {"mode": "launch", "make_visible": False})
        app = connect.get("application_id")
        if not app:
            print("kompas_connect не дал application_id")
            return 2

        created = session.tool("kompas_create_document", {
            "application_id": app, "kind": "drawing", "name": "SnapshotProbe"})
        drawing = result_of(created).get("document_id") or created.get("document_id")
        revision = created.get("revision_after") or 1
        print("чертёж:", drawing, "ревизия", revision, "маршрут:",
              (result_of(created).get("notes") or created.get("notes")))
        if not drawing:
            print("ответ create_document:", json.dumps(created, ensure_ascii=False)[:600])
            return 2

        # ДО любого сохранения: ровно тот случай, на котором F-01 отказывал.
        code, _shot, size = shoot(session, drawing, revision, "свежий")
        failures += 0 if code is None and size > 0 else 1

        path = os.path.join(WORK, "SnapshotProbe.cdw")
        saved = session.tool("kompas_save_document", {
            "document_id": drawing, "expected_revision": revision, "target_path": path})
        revision = saved.get("revision_after") or revision
        print("сохранение:", code_of(saved) or "ok", "ревизия", revision)

        closed = session.tool("kompas_close_document", {
            "document_id": drawing, "expected_revision": revision, "save": False})
        print("закрытие:", code_of(closed) or "ok")

        opened = session.tool("kompas_open_document", {"path": path, "access": "edit"})
        reopened = result_of(opened).get("document_id") or opened.get("document_id")
        revision = opened.get("revision_after") or 1
        if reopened:
            code, _shot, size = shoot(session, reopened, revision, "переоткрыт")
            failures += 0 if code is None and size > 0 else 1
        else:
            print("переоткрытие не дало документа:", code_of(opened))
            failures += 1

        print("ИТОГ:", "PASS" if failures == 0 else "FAIL %d" % failures)
        return 0 if failures == 0 else 1
    finally:
        session.close()


if __name__ == "__main__":
    sys.exit(main())
