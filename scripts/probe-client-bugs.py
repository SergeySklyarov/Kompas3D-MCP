"""Проба дефектов заказчика 22.10.2026: состояние эскизов и loft по многоконтурным сечениям.

Зачем отдельным прибором. Наряд `CLIENT_BUGS_20261022` (п. 4 и 5) требует не пересказать ответ
заказчика, а ИЗМЕРИТЬ его на КОПИИ его модели: что читается у каждого эскиза (ограничения с `valid`,
размеры, агрегатный статус), что меняет сохранение-переоткрытие и что отвечает ядро на loft по трём
кольцевым сечениям (R26/R8, R20/R8, R12/R8 на Z 16/40/65), а также на одноконтурном варианте.

Границы. Модель заказчика НЕ правится: работа идёт с копией в `scratch/cb22/`. Проба не судит
продукт - она называет ответы и пишет их в JSON.

Usage:
  python scripts/probe-client-bugs.py --host <KompasMcp.Host.exe> --out <файл.json>
"""

import json
import os
import queue
import shutil
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
SOURCE_MODEL = os.path.join(ROOT, "bugs-inbox", "MCP_stress_20261022.m3d")


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
        """Конверт ответа: structuredContent, иначе первый текстовый блок, разобранный как JSON.

        MEASURED: отказ приходит ТЕКСТОВЫМ блоком конверта, а `application_id`/`document_id` стоят в
        КОРНЕ конверта, не в `result` - без разбора обоих видов причина отказа читается пустой.
        """
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
        """Мутация: `operation_id` обязателен контрактом, и без него отказ INVALID_ARGUMENT."""
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


def message_of(envelope):
    error = (envelope or {}).get("error") or {}
    return error.get("message") if isinstance(error, dict) else None


def details_of(envelope):
    error = (envelope or {}).get("error") or {}
    return (error.get("details") or {}) if isinstance(error, dict) else {}


def revision_of(session, document_id):
    return body(session.tool("kompas_get_context", {"document_id": document_id,
                                                    "detail": "minimal"})).get("revision")


def sketches_state(session, document_id, tag, out):
    """Что читается у каждого эскиза: ограничения с `valid`, размеры, агрегатный статус."""
    listing = session.tool("kompas_list_sketches", {"document_id": document_id})
    collected = []
    for row in body(listing).get("rows") or []:
        ref = row.get("sketch_ref")
        if not ref:
            continue
        status = session.tool("kompas_get_sketch_status", {"sketch_ref": ref})
        constraints = session.tool("kompas_list_sketch_constraints", {"sketch_ref": ref})
        dimensions = session.tool("kompas_list_dimensions", {"sketch_ref": ref})
        collected.append({
            "name": row.get("name"),
            "sketch_ref": row.get("sketch_ref"),
            "plane": row.get("plane"),
            "status": {key: body(status).get(key) for key in
                       ("definition_status", "is_fully_defined", "raw_state", "native_state_name",
                        "degrees_of_freedom")},
            "constraint_objects": [
                {"object_ref": obj.get("object_ref"),
                 "state": obj.get("state_name") or obj.get("state_raw"),
                 "rows": [{"type": r.get("type"), "valid": r.get("valid"),
                           "value": r.get("value"), "point_index": r.get("point_index")}
                          for r in (obj.get("constraints") or [])]}
                for obj in body(constraints).get("objects") or []],
            "dimensions": body(dimensions).get("rows") or [],
            "dimensions_code": code_of(dimensions),
            "dimensions_message": message_of(dimensions),
        })
    out[tag] = collected
    return collected


def main():
    host = os.path.abspath(argument("--host", os.path.join(
        ROOT, "src", "KompasMcp.Host", "bin", "x64", "Debug", "net10.0-windows", "KompasMcp.Host.exe")))
    out_path = os.path.abspath(argument("--out", os.path.join(ROOT, "scratch", "cb22", "probe.json")))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    work = os.path.join(ROOT, "scratch", "cb22")
    os.makedirs(work, exist_ok=True)
    copy = os.path.join(work, "MCP_stress_20261022_copy.m3d")
    if not os.path.exists(SOURCE_MODEL):
        raise SystemExit(f"нет модели заказчика: {SOURCE_MODEL}")
    shutil.copy2(SOURCE_MODEL, copy)

    # MEASURED: изолированная конфигурация требует СУЩЕСТВУЮЩЕГО каталога, иначе первый же вызов
    # владения отвечает JOURNAL_UNAVAILABLE («Не найдена часть пути …owner.json.tmp»).
    config_dir = os.path.join(work, "config")
    os.makedirs(config_dir, exist_ok=True)
    config = isolate_config(DEFAULT_CONFIG, config_dir)

    session = Session(host, config)
    out = {"model_copy": copy, "host": host}
    try:
        session.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "probe-client-bugs", "version": "1"}})
        session.notify("notifications/initialized")
        connect = session.mutate("kompas_connect", {"mode": "launch", "make_visible": False})
        app_id = (connect or {}).get("application_id")
        out["connect_code"] = code_of(connect)
        out["application_id"] = app_id

        opened = session.mutate("kompas_open_document", {"application_id": app_id, "path": copy,
                                                         "access": "edit"})
        document_id = (opened or {}).get("document_id")
        out["open_code"] = code_of(opened)
        out["open_message"] = message_of(opened)
        out["document_id"] = document_id

        # (1) что читается у каждого эскиза ДО правок
        before = sketches_state(session, document_id, "before", out)

        # (2) loft по трём кольцевым сечениям заказчика (Z 16 / 40 / 65)
        # Кольца заказчик назвал sketch_5 (Z 16), sketch_8 (Z 40), sketch_11 (Z 65); плоскость в
        # строках перечисления не публикуется, поэтому выбор идёт ПО ИМЕНАМ, а не по Z.
        by_name = {row.get("name"): row for row in before}
        three = [by_name[name] for name in ("sketch_5", "sketch_8", "sketch_11") if name in by_name]
        out["ring_candidates"] = [{"name": row.get("name"),
                                   "status": row.get("status")} for row in three]
        if len(three) == 3:
            loft = session.mutate("kompas_loft", {
                "document_id": document_id,
                "section_refs": [row.get("sketch_ref") for row in three],
                "expected_revision": revision_of(session, document_id)})
            out["loft"] = {"code": code_of(loft), "message": message_of(loft),
                           "details": details_of(loft)}
        else:
            out["loft"] = {"code": None, "note": "не нашлось трёх эскизов с плоскостью XY"}

        # (2b) одноконтурный вариант на НОВОМ документе: тот же loft, но в каждом сечении ОДИН контур
        single = session.mutate("kompas_create_document", {"application_id": app_id, "kind": "part",
                                                           "name": "cb22-single-contour"})
        single_id = (single or {}).get("document_id")
        out["single_document_code"] = code_of(single)
        if single_id:
            refs = []
            for z in (0.0, 20.0):
                session.mutate("kompas_create_sketch", {
                    "document_id": single_id, "expected_revision": revision_of(session, single_id),
                    "plane": {"base": "xy", "offset_mm": z}, "name": f"single-{int(z)}"})
                # Ссылка берётся ПО ИМЕНИ: `kompas_list_sketches` отдаёт строки не в порядке
                # создания, и `rows[0]` дал один и тот же эскиз дважды (loft отверг повтор сечения).
                listing = body(session.tool("kompas_list_sketches", {"document_id": single_id}))
                sketch_ref = next((r.get("sketch_ref") for r in listing.get("rows") or []
                                   if r.get("name") == f"single-{int(z)}"), None)
                session.mutate("kompas_edit_sketch", {
                    "sketch_ref": sketch_ref, "expected_revision": revision_of(session, single_id),
                    "mode": "replace",
                    "entities": [{"kind": "circle", "center_mm": [0.0, 0.0], "radius_mm": 20.0 - z / 10.0}]})
                refs.append(sketch_ref)
            single_loft = session.mutate("kompas_loft", {
                "document_id": single_id, "section_refs": refs,
                "expected_revision": revision_of(session, single_id)})
            out["loft_single_contour"] = {"code": code_of(single_loft),
                                          "message": message_of(single_loft),
                                          "details": details_of(single_loft)}
            session.mutate("kompas_close_document", {"document_id": single_id,
                                                     "dirty_policy": "discard"})

        # (3) сохранение и переоткрытие: что меняет сам файл
        reopen_path = os.path.join(work, "reopen.m3d")
        saved = session.mutate("kompas_save_document", {"document_id": document_id,
                                                        "target_path": reopen_path,
                                                        "expected_revision": revision_of(session, document_id)})
        out["save_code"] = code_of(saved)
        session.mutate("kompas_close_document", {"document_id": document_id, "dirty_policy": "discard"})
        reopened = session.mutate("kompas_open_document", {"application_id": app_id,
                                                           "path": reopen_path, "access": "edit"})
        document_id2 = (reopened or {}).get("document_id")
        out["reopen_code"] = code_of(reopened)
        if document_id2:
            sketches_state(session, document_id2, "after_reopen", out)
            session.mutate("kompas_close_document", {"document_id": document_id2,
                                                     "dirty_policy": "discard"})
    finally:
        session.close()
        with open(out_path, "w", encoding="utf-8") as handle:
            json.dump(out, handle, ensure_ascii=False, indent=1)
        print(json.dumps({"out": out_path, "open": out.get("open_code"),
                          "loft": (out.get("loft") or {}).get("code"),
                          "sketches": len(out.get("before") or [])}, ensure_ascii=False))


if __name__ == "__main__":
    main()
