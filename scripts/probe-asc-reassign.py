"""Проба `M5`: публичный `kompas_replace_component` + сохранение + перечитывание из файла.

Зачем отдельным прибором. Замечания `ASSEMBLY_CHECKPOINT-2.md` §1 требуют измерить потерю компонентов
при переназначении копии двумя маршрутами (`M1`-`M4`, сделано прибором `scripts/probes/asc/`) и
отдельно `M5`: публичный путь продукта на постановке, где маршрут даёт потерю. Постановки `M1`-`M4`
потери не дали ни на одном маршруте, поэтому `M5` измеряется на постановке приёмки (`ASC.09`: левый
`bracket.m3d` один раз, правый - дважды) с сохранением и перечитыванием ПОСЛЕ КАЖДОЙ замены: так видно,
какая именно замена теряет вставки, а не только итог.

Границы. Проба ничего не правит в продукте и не судит его: она печатает числа (компонентов в
документе, в файле, пути из файла) и пишет их в JSON. Файлы клиента не меняются: копии лежат в
`scratch/asc-reassign/copies`, верхняя сборка открывается из `scratch/asc-reassign/top.m3d`.

Usage:
  python scripts/probe-asc-reassign.py --host <KompasMcp.Host.exe> [--out <файл.json>]
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
WORK = os.path.join(ROOT, "scratch", "asc-reassign")


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


def message_of(envelope):
    error = (envelope or {}).get("error")
    return error.get("message") if isinstance(error, dict) else None


def result_of(envelope):
    return (envelope or {}).get("result") or {}


def doc_of(envelope):
    body = result_of(envelope)
    return body.get("document_id") or body.get("id") or (envelope or {}).get("document_id")


def main():
    host = argument("--host")
    if not host or not os.path.exists(host):
        print("нужен --host <KompasMcp.Host.exe>")
        return 2
    out_path = argument("--out", os.path.join(WORK, "asc-reassign.json"))
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

        # S1: the fixture keeps the source part documents open (its make_part does not close them), while
        # this probe closed them. The flag reproduces that factor alone.
        parts_open = "--parts-open" in sys.argv
        left = make_part(session, app_id, os.path.join(WORK, "left", "bracket.m3d"), 30.0, 20.0, 10.0,
                         keep_open=parts_open)
        right = make_part(session, app_id, os.path.join(WORK, "right", "bracket.m3d"), 10.0, 10.0, 5.0,
                          keep_open=parts_open)
        if not left or not right:
            print("детали не построены")
            return 1

        nested = "--nested" in sys.argv
        top_open = "--top-open" in sys.argv
        # S0: the byte copy is read while NOTHING else is open, so the read cannot depend on a neighbour.
        copy_away = top_open or "--copy-away" in sys.argv
        # T1-T3: which insert pattern the copy carries. Names differ (t1, t2) or collide (t3).
        setting = argument("--setting", "t0")
        if setting in ("t1", "t2"):
            plate = make_part(session, app_id, os.path.join(WORK, "plate", "plate.m3d"), 30.0, 20.0, 10.0,
                              keep_open=parts_open)
            if not plate:
                print("деталь plate не построена")
                return 1
        inserts = {"t0": [left, right, right], "t1": [plate, left] if setting == "t1" else [],
                   "t2": [plate, plate, left] if setting == "t2" else [],
                   "t3": [left, right]}.get(setting)
        if not inserts:
            print("неизвестная постановка: %s" % setting)
            return 1
        # S1 decision row: the copy gets a name of its own, so it no longer collides with the open client
        # document of the same name.
        unique = "--unique-name" in sys.argv
        top_name = "top-cp.m3d" if (top_open and unique) else "top.m3d"
        top = os.path.join(WORK, "preflight" if nested or copy_away else "", top_name)
        os.makedirs(os.path.dirname(top), exist_ok=True)
        assembly = new_document(session, app_id, "assembly", "asc-m5")
        if not assembly:
            print("сборка не создана")
            return 1
        for source in inserts:
            code = insert(session, assembly, source)
            if code:
                print("вставка отвергнута: %s" % code)
                return 1
        # S1: the fixture saves the client's assembly, then takes the copy from disk while that same
        # document stays OPEN and the copy carries the same file name in a `preflight/` subfolder.
        client_top = os.path.join(WORK, "top.m3d") if copy_away else top
        code = save_as(session, assembly, client_top)
        if code:
            print("сохранение отвергнуто: %s" % code)
            return 1
        in_memory = len(links(session, assembly))
        if copy_away:
            os.makedirs(os.path.dirname(top), exist_ok=True)
            with open(client_top, "rb") as reader, open(top, "wb") as writer:
                writer.write(reader.read())
        else:
            close(session, assembly)
        read = count_file(session, app_id, top)
        rows.append({"stage": "saved", "setting": setting, "top_open": top_open,
                     "in_memory": in_memory, "in_file": read,
                     "paths": [item.get("source_path") for item in links_read(session, app_id, top)]})
        print("чтение копии: постановка=%s документ клиента открыт=%s в памяти=%s в файле=%s"
              % (setting, top_open, in_memory, read), flush=True)
        if "--read-copy" in sys.argv:
            # The reading is the whole measurement: nothing is replaced on this run.
            if top_open:
                close(session, assembly)
            with open(out_path, "w", encoding="utf-8") as handle:
                json.dump({"rows": rows}, handle, ensure_ascii=False, indent=1)
            print("JSON: %s" % out_path)
            return 0

        copies = copy_graph(left, right)
        code, steps, final = replace_stepwise(session, app_id, top, copies)
        rows.extend(steps)
        print("итог M5: code=%s в файле=%s" % (code, final))
        if top_open:
            close(session, assembly)
    finally:
        session.close()

    with open(out_path, "w", encoding="utf-8") as handle:
        json.dump({"rows": rows}, handle, ensure_ascii=False, indent=1)
    print("JSON: %s" % out_path)
    return 0


def make_part(session, app_id, path, width, height, depth, keep_open=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    created = session.tool("kompas_create_document", {"application_id": app_id, "kind": "part"})
    document = doc_of(created)
    if not document:
        return None
    revision = revision_of(session, document)
    sketch_env = session.tool("kompas_create_sketch", {
        "document_id": document, "expected_revision": revision, "plane": {"base": "xy"}})
    sketch = (result_of(sketch_env).get("sketch_ref") or {}).get("id") or result_of(sketch_env).get("id")
    revision = revision_of(session, document)
    session.tool("kompas_edit_sketch", {
        "sketch_ref": sketch, "expected_revision": revision, "mode": "append",
        "entities": [{"kind": "rectangle", "start_mm": [0, 0],
                      "width_mm": width, "height_mm": height}]})
    session.tool("kompas_finish_sketch", {"sketch_ref": sketch, "require_closed_profile": False})
    session.tool("kompas_extrude", {
        "sketch_ref": sketch, "expected_revision": revision_of(session, document),
        "operation": "base", "end_condition": "blind", "depth_mm": depth,
        "direction": "positive"})
    saved = session.tool("kompas_save_document", {
        "document_id": document, "expected_revision": revision_of(session, document),
        "target_path": path})
    if not keep_open:
        close(session, document)
    return path if code_of(saved) is None else None


def new_document(session, app_id, kind, name):
    created = session.tool("kompas_create_document",
                           {"application_id": app_id, "kind": kind, "name": name})
    return doc_of(created)


def revision_of(session, document):
    context = session.tool("kompas_get_context", {"document_id": document, "detail": "minimal"})
    return result_of(context).get("revision") or 1


def insert(session, document, source):
    envelope = session.tool("kompas_insert_component", {
        "document_id": document, "expected_revision": revision_of(session, document),
        "source_path": source})
    return code_of(envelope)


def save_as(session, document, path):
    envelope = session.tool("kompas_save_document", {
        "document_id": document, "expected_revision": revision_of(session, document),
        "target_path": path})
    return code_of(envelope)


def save_in_place(session, document):
    """Сохранение В ТОТ ЖЕ ФАЙЛ: ровно то, что делает контрольная точка на копии."""
    envelope = session.tool("kompas_save_document", {
        "document_id": document, "expected_revision": revision_of(session, document)})
    return envelope


def close(session, document, policy="discard"):
    if document:
        session.tool("kompas_close_document", {"document_id": document, "dirty_policy": policy})


def open_document(session, app_id, path, access):
    envelope = session.tool("kompas_open_document", {
        "application_id": app_id, "path": path, "access": access})
    return doc_of(envelope), code_of(envelope)


def links(session, document):
    envelope = session.tool("kompas_list_components", {"document_id": document, "recursive": True})
    return result_of(envelope).get("components") or []


def count_file(session, app_id, path):
    document, _code = open_document(session, app_id, path, "read_only")
    if not document:
        return None
    try:
        return len(links(session, document))
    finally:
        close(session, document)


def copy_graph(left, right):
    """Копия графа так, как её делает контрольная точка: файл на уникальный источник, имя с номером."""
    folder = os.path.join(WORK, "copies")
    os.makedirs(folder, exist_ok=True)
    copies = {}
    for ordinal, source in enumerate([left, right], start=1):
        target = os.path.join(folder, "%03d-%s" % (ordinal, os.path.basename(source)))
        with open(source, "rb") as reader, open(target, "wb") as writer:
            writer.write(reader.read())
        copies[source] = target
    return copies


def replace_stepwise(session, app_id, top, copies):
    """Заменить по одному компоненту, ПОСЛЕ КАЖДОЙ замены сохранить и перечитать файл."""
    document, code = open_document(session, app_id, top, "edit")
    if not document:
        return code, [], None
    steps = []
    final = None
    try:
        step = 0
        while step < 8:
            rows = links(session, document)
            row = next((item for item in rows
                        if (item.get("source_path") or "") in copies), None)
            if row is None:
                break
            step += 1
            replaced = session.tool("kompas_replace_component", {
                "document_id": document,
                "expected_revision": revision_of(session, document),
                "component_ref": row.get("component_ref"),
                "source_path": copies[row["source_path"]]})
            code = code_of(replaced)
            in_memory = len(links(session, document))
            single = "--single-save" in sys.argv
            if single:
                steps.append({"stage": "step-%d" % step, "replaced": row.get("source_path"),
                              "code": code, "in_memory": in_memory})
                print("шаг %d (без сохранения): %s -> %s code=%s memory=%s"
                      % (step, row.get("source_path"), copies[row["source_path"]], code, in_memory),
                      flush=True)
                if code is not None:
                    break
                continue
            inplace = "--inplace" in sys.argv
            target = top if inplace else os.path.join(WORK, "step-%d.m3d" % step)
            saved = save_in_place(session, document) if inplace else save_as(session, document, target)
            close(session, document, "save" if "--saveclose" in sys.argv else "discard")
            in_file = count_file(session, app_id, target)
            steps.append({"stage": "step-%d" % step, "replaced": row.get("source_path"),
                          "code": code, "message": message_of(replaced),
                          "in_memory": in_memory, "in_file": in_file,
                          "save_code": code_of(saved),
                          "paths": [item.get("source_path") for item in links_read(session, app_id, target)]})
            final = in_file
            print("шаг %d: %s -> %s code=%s memory=%s file=%s"
                  % (step, row.get("source_path"), copies[row["source_path"]], code, in_memory, in_file),
                  flush=True)
            if code is not None:
                # A refused replacement leaves the source unchanged: repeating it would loop forever.
                break
            document, _c = open_document(session, app_id, top, "edit")
            if not document:
                break
        if "--single-save" in sys.argv and document:
            final_doc = document
            save_in_place(session, final_doc)
            close(session, final_doc, "save")
            document = None
            final = count_file(session, app_id, top)
            print("после единственного сохранения: в файле=%s" % final, flush=True)
    finally:
        close(session, document)
    return None, steps, final


def links_read(session, app_id, path):
    document, _code = open_document(session, app_id, path, "read_only")
    if not document:
        return []
    try:
        return links(session, document)
    finally:
        close(session, document)


if __name__ == "__main__":
    sys.exit(main())
