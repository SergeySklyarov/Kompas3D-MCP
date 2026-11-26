"""Проба: какой вид файла пишется, когда расширение цели не совпадает с видом документа.

ЗАЧЕМ. Клиентская приёмка 0.15.0 (строка `R1.pattern_components_grid`, находка `F1`) сохранила
сборку в путь `...\\asm-grid.m3d` и получила успех с `kind=assembly`, а на диске лежала деталь
(`FileType=4`). Файл той же строки приёмки 0.14.0 (`...\\asm-grid.m3d`) оказался сборкой
(`FileType=6`). Наряд требует назвать причину и показать отличие 0.14.0 от текущей сборки, поэтому
проба ГОВОРИТ ОБОИМ Хостам одно и то же и снимает вид записанного файла из его собственной метки
`FileInfo` (zip-элемент), а не из ответа сохранения.

ГРАНИЦЫ. Проба ничего не правит в продукте; каталоги и копии - в `scratch/save-extension/`. Каждый
Хост работает на СВОЁМ экземпляре КОМПАС (launch), поэтому прогоны не делят сеанс.

Usage:
  python scripts/probe-save-extension.py --host <KompasMcp.Host.exe> --label 0.15.0 [--out <json>]
"""

import datetime
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import uuid
import zipfile

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from localconfig import isolate_config, local_config  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, "scratch", "save-extension")
FIXTURE = os.path.join(ROOT, "scratch", "spec-probe", "fixture")


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


class Session:
    """Один Хост по stdio: тот же способ запуска, что у остальных приборов проекта."""

    def __init__(self, host, config):
        self._proc = subprocess.Popen(
            [host, "--config", config],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
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
            except ValueError:
                continue
            if message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError("%s: %s" % (method, message["error"]))
                return message.get("result")

    def tool(self, name, arguments, timeout=600):
        return self.call("tools/call", {"name": name, "arguments": arguments}, timeout)

    def close(self):
        try:
            self._proc.stdin.close()
            self._proc.wait(timeout=15)
        except Exception:  # noqa: BLE001
            self._proc.kill()


def structured(reply):
    return (reply or {}).get("structuredContent") or {}


def code_of(reply):
    error = (structured(reply) or {}).get("error") or {}
    return error.get("code")


def file_info(path):
    """Вид файла из его собственной метки: так его называет КОМПАС, а не имя файла."""
    try:
        with zipfile.ZipFile(path) as archive:
            if "FileInfo" not in archive.namelist():
                return {"error": "no FileInfo"}
            text = archive.read("FileInfo").decode("utf-16")
    except Exception as ex:  # noqa: BLE001
        return {"error": str(ex)}
    fields = {}
    for line in text.splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            if key.strip() in ("FileType", "FileTypeName"):
                fields[key.strip()] = value.strip()
    return fields


def run_host(host, label, visible):
    workspace = os.path.join(WORK, label)
    shutil.rmtree(workspace, ignore_errors=True)
    os.makedirs(workspace, exist_ok=True)
    for name in ("spec-asm.m3d", "p1.m3d", "p2.m3d"):
        shutil.copy(os.path.join(FIXTURE, name), os.path.join(workspace, name))

    config = isolate_config(local_config(ROOT), workspace, prefix="save-ext-" + label)
    session = Session(host, config)
    rows = []
    try:
        session.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                    "clientInfo": {"name": "save-extension", "version": "0"}}, timeout=120)
        session._proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        session._proc.stdin.flush()
        connected = structured(session.tool("kompas_connect", {
            "mode": "launch", "make_visible": visible, "operation_id": str(uuid.uuid4())}))
        application_id = (connected.get("result") or {}).get("application_id") or connected.get("application_id")
        rows.append({"step": "connect", "visible": visible, "application_id": application_id})

        def save_case(step, document_id, target, label_text):
            revision = ((structured(session.tool("kompas_get_context", {
                "document_id": document_id, "detail": "minimal"})).get("result") or {}).get("revision"))
            reply = session.tool("kompas_save_document", {
                "document_id": document_id, "expected_revision": revision, "target_path": target,
                "operation_id": str(uuid.uuid4())})
            saved = structured(reply)
            rows.append({"step": step, "case": label_text, "target": target,
                         "code": code_of(reply), "answer": saved.get("result") or saved.get("error")})
            return reply

        # Случай 1: свежая сборка Хоста, сохранение в НОВЫЙ путь .m3d.
        created = structured(session.tool("kompas_create_document", {
            "application_id": application_id, "kind": "assembly", "name": "SaveExt",
            "operation_id": str(uuid.uuid4())}))
        document_id = (created.get("result") or {}).get("document_id") or created.get("document_id")
        target = os.path.join(workspace, "fresh.m3d")
        save_case("save_fresh_m3d", document_id, target, "новая сборка без компонентов -> новый .m3d")
        session.tool("kompas_close_document", {
            "document_id": document_id, "dirty_policy": "discard", "operation_id": str(uuid.uuid4())})
        rows.append({"step": "fresh_m3d_file", "file": target, "file_info": file_info(target)})

        # Случай 1b: сборка С КОМПОНЕНТОМ, сохранение в новый путь .m3d: приёмка сохраняла именно такую.
        created = structured(session.tool("kompas_create_document", {
            "application_id": application_id, "kind": "assembly", "name": "SaveExtWithComponent",
            "operation_id": str(uuid.uuid4())}))
        document_id = (created.get("result") or {}).get("document_id") or created.get("document_id")
        inserted = structured(session.tool("kompas_insert_component", {
            "document_id": document_id,
            "expected_revision": ((structured(session.tool("kompas_get_context", {
                "document_id": document_id, "detail": "minimal"})).get("result") or {}).get("revision")),
            "source_path": os.path.join(workspace, "p1.m3d"), "transform": None, "fixed": True,
            "operation_id": str(uuid.uuid4())}))
        rows.append({"step": "insert_component", "code": code_of({"structuredContent": inserted}),
                     "answer": inserted.get("result") or inserted.get("error")})
        target1b = os.path.join(workspace, "with-component.m3d")
        save_case("save_assembly_with_component_m3d", document_id, target1b,
                  "сборка с компонентом -> новый .m3d")
        session.tool("kompas_close_document", {
            "document_id": document_id, "dirty_policy": "discard", "operation_id": str(uuid.uuid4())})
        rows.append({"step": "with_component_m3d_file", "file": target1b, "file_info": file_info(target1b)})

        # Случай 2: та же сборка, второй раз, в НОВЫЙ путь .m3d (проверка «второе сохранение иное»).
        reopened = structured(session.tool("kompas_open_document", {
            "application_id": application_id, "path": os.path.join(workspace, "spec-asm.m3d"),
            "access": "edit", "operation_id": str(uuid.uuid4())}))
        fixture_id = (reopened.get("result") or {}).get("document_id") or reopened.get("document_id")
        rows.append({"step": "open_fixture", "document_id": fixture_id})
        target2 = os.path.join(workspace, "copy.m3d")
        save_case("save_assembly_m3d", fixture_id, target2, "открытая сборка -> новый .m3d")
        session.tool("kompas_close_document", {
            "document_id": fixture_id, "dirty_policy": "discard", "operation_id": str(uuid.uuid4())})
        rows.append({"step": "copy_m3d_file", "file": target2, "file_info": file_info(target2)})

        # Случай 3: контроль - та же сборка в путь .a3d.
        reopened = structured(session.tool("kompas_open_document", {
            "application_id": application_id, "path": os.path.join(workspace, "spec-asm.m3d"),
            "access": "edit", "operation_id": str(uuid.uuid4())}))
        fixture_id = (reopened.get("result") or {}).get("document_id") or reopened.get("document_id")
        target3 = os.path.join(workspace, "copy.a3d")
        save_case("save_assembly_a3d", fixture_id, target3, "открытая сборка -> новый .a3d")
        session.tool("kompas_close_document", {
            "document_id": fixture_id, "dirty_policy": "discard", "operation_id": str(uuid.uuid4())})
        rows.append({"step": "copy_a3d_file", "file": target3, "file_info": file_info(target3)})

        # Вид каждого записанного файла - тем же Хостом, повторным открытием.
        for step, path in (("reopen_fresh_m3d", target), ("reopen_copy_m3d", target2),
                           ("reopen_copy_a3d", target3)):
            if not os.path.exists(path):
                rows.append({"step": step, "path": path, "missing": True})
                continue
            opened = structured(session.tool("kompas_open_document", {
                "application_id": application_id, "path": path, "access": "read_only",
                "operation_id": str(uuid.uuid4())}))
            result = opened.get("result") or {}
            rows.append({"step": step, "path": path, "code": code_of(opened),
                         "kind": result.get("kind"), "component_count": result.get("component_count")})
            if result.get("document_id"):
                session.tool("kompas_close_document", {
                    "document_id": result["document_id"], "dirty_policy": "discard",
                    "operation_id": str(uuid.uuid4())})
    finally:
        session.close()
        # Экземпляр Хоста свой КОМПАС не закрывает: проба гасит его через компенсацию не пытается,
        # это делает прибор полного прогона (mcp-smoke) при следующем запуске.
    return rows


def main():
    host = argument("--host")
    label = argument("--label", "host")
    visible = "--visible" in sys.argv
    out = argument("--out", os.path.join(WORK, "save-extension-%s.json" % label))
    if not host or not os.path.exists(host):
        raise SystemExit("нужен --host <путь к KompasMcp.Host.exe>")
    os.makedirs(WORK, exist_ok=True)
    rows = run_host(host, label, visible)
    report = {"probe": "save-extension", "label": label, "host": host, "visible": visible,
              "measured_at": datetime.datetime.now().strftime("%Y-%m-%dT%H:%M:%S"), "rows": rows}
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)
    for row in rows:
        print(json.dumps(row, ensure_ascii=False))
    print("report:", out)


if __name__ == "__main__":
    main()
