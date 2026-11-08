"""Проба: чем КОМПАС подтверждает запись файла при сохранении документа.

Зачем отдельный прибор. Строка долга 88: `kompas_save_document` ответил `succeeded`, а файл-цель
остался прежним - новое содержимое легло в `<имя>.$$$` и поверх цели не переименовалось. Справка v24
(`ksdocument3d_saveas.html`, `ksapi_ikompasdocument_saveas.html`) описывает только логический результат
вызова (`TRUE`/`true` - успех, `false` - например, нет прав на перезапись) и ничего не говорит ни о
временном файле, ни о его замене. Поэтому признак «файл записан ЭТИМ вызовом» приходится выбирать по
замеру: прибор снимает отметку времени, размер и sha256 цели и `<имя>.$$$` ДО и ПОСЛЕ каждого вызова на
постановках, где исход известен (запись в новый файл, перезапись того же файла, сохранение без правок,
занятая цель).

Границы. Проба ничего не правит в продукте и не судит его: она печатает числа и пишет их в JSON. Файлы
клиента не меняются: всё лежит в `scratch/save-write/`.

Usage:
  python scripts/probe-save-write.py --host <KompasMcp.Host.exe> [--out <файл.json>] [--locked-only]
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
WORK = os.path.join(ROOT, "scratch", "save-write")


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

    def call(self, method, params=None, timeout=600):
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

    def tool(self, name, arguments, timeout=600):
        payload = dict(arguments)
        payload.setdefault("operation_id", str(uuid.uuid4()))
        result = self.call("tools/call", {"name": name, "arguments": payload}, timeout=timeout)
        envelope = result.get("structuredContent") or result
        message = str(((envelope or {}).get("error") or {}).get("message") or "")
        if "Неизвестное поле" in message and "operation_id" in payload:
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


def details_of(envelope):
    error = (envelope or {}).get("error")
    return (error or {}).get("details") if isinstance(error, dict) else None


def result_of(envelope):
    return (envelope or {}).get("result") or {}


def doc_of(envelope):
    body = result_of(envelope)
    return body.get("document_id") or body.get("id") or (envelope or {}).get("document_id")


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def stamp(path):
    """Что лежит на диске по пути: наличие, размер, sha256, время изменения (UTC, ISO)."""
    if not os.path.exists(path):
        return {"exists": False}
    info = os.stat(path)
    return {
        "exists": True,
        "bytes": info.st_size,
        "mtime_utc": datetime.datetime.fromtimestamp(info.st_mtime, datetime.UTC).strftime(
            "%Y-%m-%dT%H:%M:%S.%f"),
        "mtime_ns": info.st_mtime_ns,
        "sha256": sha256(path),
        "readonly_attr": not os.access(path, os.W_OK),
    }


def snapshot(path):
    """Цель и её временный файл сохранения (`<имя>.$$$` - так его назвал отказ строки долга 88)."""
    return {"target": stamp(path), "temp": stamp(path + ".$$$")}


def delta(before, after):
    return {
        "target_changed": (before["target"] != after["target"]),
        "temp_changed": (before["temp"] != after["temp"]),
    }


def describe(envelope, before, after):
    return {
        "code": code_of(envelope),
        "details": details_of(envelope),
        "before": before,
        "after": after,
        "delta": delta(before, after),
    }


def line(step, envelope, before, after):
    """Одна строка на постановку: код ответа, сдвиг времени, смена sha, наличие `.$$$`."""
    target_before, target_after = before["target"], after["target"]
    return ("%-46s code=%-18s цель: было=%s %s -> стало=%s %s mtime_сдвиг=%s sha_тот_же=%s "
            ".$$$: было=%s стало=%s"
            % (step, code_of(envelope),
               target_before.get("exists"), target_before.get("bytes"),
               target_after.get("exists"), target_after.get("bytes"),
               target_before.get("mtime_ns") != target_after.get("mtime_ns"),
               target_before.get("sha256") == target_after.get("sha256"),
               before["temp"].get("exists"), after["temp"].get("bytes")))


def make_part(session, app_id, path, width, height, depth):
    """Деталь с одним телом: плита width x height x depth. Каждый шаг обязан пройти."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    created = session.tool("kompas_create_document", {"application_id": app_id, "kind": "part"})
    document = doc_of(created)
    require("create_document", created)
    sketch_env = session.tool("kompas_create_sketch", {
        "document_id": document, "expected_revision": revision_of(session, document),
        "plane": {"base": "xy"}})
    require("create_sketch", sketch_env)
    sketch = (result_of(sketch_env).get("sketch_ref") or {}).get("id") or result_of(sketch_env).get("id")
    require("edit_sketch", session.tool("kompas_edit_sketch", {
        "sketch_ref": sketch, "expected_revision": revision_of(session, document),
        "mode": "append",
        "entities": [{"kind": "rectangle", "start_mm": [0, 0],
                      "width_mm": width, "height_mm": height}]}))
    require("finish_sketch", session.tool(
        "kompas_finish_sketch", {"sketch_ref": sketch, "require_closed_profile": False}))
    add_extrusion(session, document, sketch, depth)
    return document, sketch


def require(name, envelope):
    """Постановка не состоялась, если подготовительный шаг отвергнут: иначе прибор мерил бы пустоту."""
    if code_of(envelope):
        raise RuntimeError("%s отвергнут: %s %s" % (
            name, code_of(envelope), json.dumps(details_of(envelope), ensure_ascii=False)))
    return envelope


def add_extrusion(session, document, sketch, depth):
    """Ещё одно тело из того же эскиза: модель заведомо отличается от файла."""
    return require("extrude", session.tool("kompas_extrude", {
        "sketch_ref": sketch, "expected_revision": revision_of(session, document),
        "operation": "base", "end_condition": "blind", "depth_mm": depth, "direction": "positive"}))


def revision_of(session, document):
    context = session.tool("kompas_get_context", {"document_id": document, "detail": "minimal"})
    if os.environ.get("PROBE_DEBUG_CONTEXT"):
        print("   context raw: %s" % json.dumps(context, ensure_ascii=False)[:600], flush=True)
    return result_of(context).get("revision") or 1


def save(session, document, path=None, timeout=240):
    arguments = {"document_id": document, "expected_revision": revision_of(session, document)}
    if path is not None:
        arguments["target_path"] = path
    return session.tool("kompas_save_document", arguments, timeout=timeout)


class LockedFile:
    """Держит файл открытым так, что переименовать поверх него нельзя (нет FILE_SHARE_DELETE).

    Обычный `open(path, "rb")` в Python открывает файл с FILE_SHARE_READ | FILE_SHARE_WRITE и БЕЗ
    FILE_SHARE_DELETE - именно то, что нужно: запись внутрь разрешена, замена файла запрещена.
    """

    def __init__(self, path):
        self._path = path
        self._handle = None

    def __enter__(self):
        self._handle = open(self._path, "rb")
        return self

    def __exit__(self, *_):
        self._handle.close()
        return False


def main():
    host = argument("--host")
    if not host or not os.path.exists(host):
        print("нужен --host <KompasMcp.Host.exe>")
        return 2
    out_path = argument("--out", os.path.join(WORK, "save-write.json"))
    os.makedirs(WORK, exist_ok=True)
    config = isolate_config(DEFAULT_CONFIG, os.path.join(WORK, "config.json"))

    rows = []
    session = Session(host, config)
    try:
        connected = session.tool("kompas_connect", {"mode": "launch", "make_visible": False})
        app_id = (connected or {}).get("application_id")
        if not app_id:
            print("kompas_connect не вернул application_id: %s" % connected)
            return 1

        target = os.path.join(WORK, "part.m3d")
        for stale in (target, target + ".$$$"):
            if os.path.exists(stale):
                os.remove(stale)
        document, sketch = make_part(session, app_id, target, 40.0, 30.0, 10.0)

        def step(name, path, expect_save=True, timeout=240, locked=False):
            """Снимок ДО, вызов, снимок ПОСЛЕ; строка в отчёт и в JSON."""
            before = snapshot(path)
            error = None
            holder = LockedFile(path) if locked else None
            if holder:
                holder.__enter__()
            try:
                envelope = save(session, document, None if expect_save is None else path,
                                timeout=timeout)
            except RuntimeError as failure:
                envelope, error = None, str(failure)
            finally:
                if holder:
                    holder.__exit__()
            after = snapshot(path)
            row = dict(step=name, **describe(envelope, before, after))
            row["call_error"] = error
            rows.append(row)
            print(line(name, envelope, before, after), flush=True)
            if error or code_of(envelope):
                print("   details: %s%s" % (json.dumps(details_of(envelope), ensure_ascii=False)
                                            if details_of(envelope) else "", (" ошибка=" + error) if error else ""),
                      flush=True)
            return row

        # S1: SaveAs в НОВЫЙ файл (цели до вызова нет).
        step("S1_SaveAs_new_file", target)

        # S2: правка модели и сохранение В ТОТ ЖЕ файл (target_path не задаётся).
        add_extrusion(session, document, sketch, 20.0)
        step("S2_Save_in_place_after_change", target, expect_save=None)

        # S3: сохранение БЕЗ правок модели: файл тот же по содержимому, но записан заново?
        step("S3_Save_without_change", target, expect_save=None)

        # S4: цель занята читателем без FILE_SHARE_DELETE: переименовать поверх нельзя.
        add_extrusion(session, document, sketch, 30.0)
        step("S4_Save_locked_target", target, expect_save=None, timeout=180, locked=True)

        # S5: SaveAs НА ЗАНЯТУЮ цель (файл существует до вызова, содержимое постороннее).
        other = os.path.join(WORK, "other.m3d")
        if os.path.exists(other):
            os.remove(other)
        with open(other, "wb") as writer:
            writer.write(b"old content, not a KOMPAS file")
        step("S5_SaveAs_locked_existing_target", other, timeout=180, locked=True)

        # S6: SaveAs на СВОБОДНУЮ существующую цель с посторонним содержимым.
        free = os.path.join(WORK, "free.m3d")
        if os.path.exists(free):
            os.remove(free)
        with open(free, "wb") as writer:
            writer.write(b"old content, not a KOMPAS file")
        step("S6_SaveAs_free_existing_target", free)

        session.tool("kompas_close_document", {"document_id": document, "dirty_policy": "discard"})
    finally:
        session.close()

    with open(out_path, "w", encoding="utf-8") as handle:
        json.dump({"rows": rows}, handle, ensure_ascii=False, indent=1)
    print("JSON: %s" % out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
