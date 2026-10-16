"""Проба пути проверки объёма выдавливания: призма профиля - ВЕРХНЯЯ ГРАНИЦА, а не равенство.

Зачем отдельным прибором. Строки приёмки `EX46`...`EX49m` живут в группе выдавливания и требуют
полного сеанса группы; этой пробе нужен ОДИН вход и ДВА Хоста - до правки и после, - чтобы назвать
РАЗНИЦУ ответов, а не «стало лучше». Она отвечает на дефект заказчика P2: верная операция с
пересечением тела или полостью получала `volume_delta.passed=false` и уровень `call_returned`.

Что измеряется (два входа, оба - верные операции):
  * A: boss, частично входящий в тело. Эскиз ВНУТРИ пластины (z=5), призма 30x30x10 занимает
    z=5..15, но z=5..10 уже занято телом: прибавится 4500 против призмы 9000;
  * B: вырезание ДВУХ кругов через полый корпус. Пластина с выемкой 60x40 глубиной 8 (пол
    z=2), круги R5 насквозь: снимется только материал ниже пола, а не полные цилиндры.

Usage:
  python scripts/probe-extrusion-volume.py --host <KompasMcp.Host.exe> --label before --out <файл>

Журналы прибора изолированы: Host получает КОПИЮ конфигурации с перенаправленными путями, иначе
проба затирала бы журнал клиентского сеанса (измерено 18.09.2026, см. `localconfig`).
"""

import datetime
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
                raise RuntimeError(f"timeout waiting for {method}")
            if line is None:
                raise RuntimeError(f"stdout closed while waiting for {method}")
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == request_id:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
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


def snapshot(env):
    """Что именно измеряет проба: код отказа, дельта, проверки, уровень, аспекты, предупреждения."""
    error = (env or {}).get("error")
    result = (env or {}).get("result") or {}
    verification = (env or {}).get("verification") or {}
    return {
        "error_code": error.get("code") if isinstance(error, dict) else None,
        "error_message": (error.get("message") or "")[:300] if isinstance(error, dict) else None,
        "volume_delta_mm3": result.get("volume_delta_mm3"),
        "volume_delta_basis": result.get("volume_delta_basis"),
        "document_volume_before_mm3": result.get("document_volume_before_mm3"),
        "document_volume_after_mm3": result.get("document_volume_after_mm3"),
        "level": verification.get("level"),
        "checks": verification.get("checks"),
        "unverified_aspects": verification.get("unverified_aspects"),
        "warnings": (env or {}).get("warnings"),
    }


def main():
    host = os.path.abspath(argument("--host"))
    label = argument("--label") or "run"
    out = os.path.abspath(argument("--out") or os.path.join(
        ROOT, "scratch", "vol", f"probe-extrusion-volume-{label}.json"))
    config = os.path.abspath(argument("--config") or DEFAULT_CONFIG)
    isolated = isolate_config(config, os.path.dirname(out), f"probe-extrusion-volume-{label}")

    report = {
        "title": "Проба пути проверки объёма выдавливания",
        "label": label,
        "host_path": host,
        "host_sha256": _sha256(host),
        "config_path": isolated,
        "when": datetime.datetime.now().isoformat(timespec="seconds"),
        "scenarios": {},
    }

    session = Session(host, isolated)
    try:
        init = session.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "probe-extrusion-volume", "version": "0"}}, timeout=60)
        report["build_stamp"] = (init.get("serverInfo") or {}).get("version")
        session.notify("notifications/initialized")

        connect = session.tool("kompas_connect", {
            "mode": "launch", "make_visible": False, "operation_id": str(uuid.uuid4())})
        app_id = connect.get("application_id")
        if not app_id:
            raise RuntimeError("kompas_connect не вернул application_id")
        report["application_id"] = app_id

        report["scenarios"]["A_boss_overlap"] = _boss_overlap(session, app_id)
        report["scenarios"]["B_cut_two_circles_through_cavity"] = _cut_through_cavity(session, app_id)

        session.tool("kompas_disconnect", {
            "application_id": app_id, "close_owned_application": True,
            "operation_id": str(uuid.uuid4())}, timeout=120)
    finally:
        session.close()

    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    print("\nЗаписано: " + out)


def _sha256(path):
    import hashlib

    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _new_part(session, name):
    env = session.tool("kompas_create_document", {
        "application_id": _APP[0], "kind": "part", "name": name,
        "operation_id": str(uuid.uuid4())})
    result = env.get("result") or {}
    return env.get("document_id") or result.get("id"), (env.get("revision_after") or 1)


def _draw(session, doc, rev, plane_offset, entities, name):
    env = session.tool("kompas_create_sketch", {
        "document_id": doc, "expected_revision": rev,
        "plane": {"base": "xy", "offset_mm": plane_offset}, "name": name,
        "operation_id": str(uuid.uuid4())})
    sketch = (env.get("result") or {}).get("id")
    rev = env.get("revision_after") or rev
    env = session.tool("kompas_edit_sketch", {
        "sketch_ref": sketch, "expected_revision": rev, "mode": "append", "entities": entities,
        "operation_id": str(uuid.uuid4())})
    rev = env.get("revision_after") or rev
    env = session.tool("kompas_finish_sketch", {
        "sketch_ref": sketch, "require_closed_profile": False, "expected_revision": rev,
        "operation_id": str(uuid.uuid4())})
    return sketch, (env.get("revision_after") or rev)


def _extrude(session, doc, rev, sketch, **kwargs):
    args = {"sketch_ref": sketch, "expected_revision": rev,
            "operation_id": str(uuid.uuid4())}
    args.update(kwargs)
    env = session.tool("kompas_extrude", args)
    return env, (env.get("revision_after") or rev)


def _bodies(session, doc):
    env = session.tool("kompas_list_bodies", {"document_id": doc})
    rows = env.get("result")
    return rows if isinstance(rows, list) else []


def _close(session, doc):
    session.tool("kompas_close_document", {
        "document_id": doc, "dirty_policy": "discard", "operation_id": str(uuid.uuid4())})


def _boss_overlap(session, app_id):
    """A: boss, частично входящий в тело. Эскиз внутри пластины, призма накрывает уже занятый объём."""
    _APP[0] = app_id
    doc, rev = _new_part(session, "VOL-A-boss-overlap")
    steps = {}
    try:
        plate, rev = _draw(session, doc, rev, 0, [
            {"kind": "rectangle", "start_mm": [-50, -40], "width_mm": 100, "height_mm": 80}], "a-plate")
        env, rev = _extrude(session, doc, rev, plate, operation="base", depth_mm=10,
                            direction="positive", end_condition="blind")
        steps["base"] = snapshot(env)
        rows = _bodies(session, doc)
        body = rows[0]["body_ref"] if rows else None
        pad, rev = _draw(session, doc, rev, 5, [
            {"kind": "rectangle", "start_mm": [-15, -15], "width_mm": 30, "height_mm": 30}], "a-pad")
        env, rev = _extrude(session, doc, rev, pad, operation="boss", depth_mm=10,
                            direction="positive", end_condition="blind", target_body_ref=body)
        steps["boss"] = snapshot(env)
    finally:
        _close(session, doc)
    return steps


def _cut_through_cavity(session, app_id):
    """B: два круга через полый корпус. Выемка создаёт полость, круги снимают только материал под ней."""
    _APP[0] = app_id
    doc, rev = _new_part(session, "VOL-B-cut-cavity")
    steps = {}
    try:
        plate, rev = _draw(session, doc, rev, 0, [
            {"kind": "rectangle", "start_mm": [-50, -40], "width_mm": 100, "height_mm": 80}], "b-plate")
        env, rev = _extrude(session, doc, rev, plate, operation="base", depth_mm=10,
                            direction="positive", end_condition="blind")
        steps["base"] = snapshot(env)
        rows = _bodies(session, doc)
        body = rows[0]["body_ref"] if rows else None
        pocket, rev = _draw(session, doc, rev, 10, [
            {"kind": "rectangle", "start_mm": [-30, -20], "width_mm": 60, "height_mm": 40}], "b-pocket")
        env, rev = _extrude(session, doc, rev, pocket, operation="cut", depth_mm=8,
                            direction="positive", end_condition="blind", target_body_ref=body)
        steps["pocket"] = snapshot(env)
        rows = _bodies(session, doc)
        body = rows[0]["body_ref"] if rows else None
        circles, rev = _draw(session, doc, rev, 10, [
            {"kind": "circle", "center_mm": [-15, 0], "radius_mm": 5},
            {"kind": "circle", "center_mm": [15, 0], "radius_mm": 5}], "b-circles")
        env, rev = _extrude(session, doc, rev, circles, operation="cut", depth_mm=9,
                            direction="positive", end_condition="blind", target_body_ref=body)
        steps["cut_two_circles"] = snapshot(env)
    finally:
        _close(session, doc)
    return steps


_APP = [None]

if __name__ == "__main__":
    main()
