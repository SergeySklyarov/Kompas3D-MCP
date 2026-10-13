"""Контрольный опыт: клиент УМИРАЕТ посреди работы, не закрывая stdin Хоста.

ЧТО ПРОВЕРЯЕТСЯ. Клиент приёмки (или чат) завершается аварийно, пока у сервера открыт изменённый
документ. Стенд этого не делает: он закрывает stdin ЯВНО (`Client.close()`). Здесь процесс клиента
исчезает так, как исчезает убитый процесс, — и проверяется СУДЬБА ПРОЦЕССОВ: уходит ли Host, уходит
ли Worker, уходит ли КОМПАС.

ЗАЧЕМ ОТДЕЛЬНЫЙ ФАЙЛ. Проверяемое — не ответ инструмента, а то, что осталось в списке процессов
после смерти клиента. Наблюдать это из того же процесса, который умирает, нельзя.

Запуск: python scripts/shutdown-orphans-parent-death.py --out <каталог доказательств>
"""
import argparse
import json
import os
import subprocess
import sys
import time
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "scripts"))

from localconfig import isolate_config, local_config  # noqa: E402

sys.path.insert(0, ROOT)
import importlib.util  # noqa: E402


def load_smoke():
    path = os.path.join(ROOT, "scripts", "mcp-smoke.py")
    spec = importlib.util.spec_from_file_location("mcp_smoke_import", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)

    smoke = load_smoke()
    host = os.path.join(ROOT, "src", "KompasMcp.Host", "bin", "x64", "Debug", "net10.0-windows",
                        "KompasMcp.Host.exe")
    config = isolate_config(local_config(ROOT), out, "parent-death")

    client = smoke.Client(host, config)
    client.call("initialize", {
        "protocolVersion": "2025-06-18", "capabilities": {},
        "clientInfo": {"name": "parent-death", "version": "0"}}, timeout=60)
    client.notify("notifications/initialized")

    def call(tool, payload, timeout=300):
        data = dict(payload)
        if client.declares_operation_id(tool):
            data.setdefault("operation_id", str(uuid.uuid4()))
        _e, env, _r = client.tool(tool, data, timeout=timeout)
        return env, smoke.error_code(env)

    env, code = call("kompas_connect", {"mode": "launch", "make_visible": False})
    app = (env or {}).get("application_id")
    pid = ((env or {}).get("result") or {}).get("process_id")
    record = {"host_pid": client.p.pid, "kompas_pid": pid, "application_id": app, "code": code}

    path = os.path.join(out, "parent-death-client.m3d")
    if app:
        env, code = call("kompas_create_document",
                         {"application_id": app, "kind": "part", "name": "parent-death"})
        doc = ((env or {}).get("result") or {}).get("id")
        env, code = call("kompas_create_sketch", {
            "document_id": doc, "expected_revision": 1,
            "plane": {"base": "xy", "offset_mm": 0}, "name": "pd-s"})
        sketch = ((env or {}).get("result") or {}).get("id")
        call("kompas_edit_sketch", {
            "sketch_ref": sketch, "expected_revision": 1, "mode": "append",
            "entities": [{"kind": "rectangle", "start_mm": [0.0, 0.0],
                          "width_mm": 100.0, "height_mm": 80.0}]})
        call("kompas_finish_sketch", {"sketch_ref": sketch, "require_closed_profile": False})
        call("kompas_extrude", {"sketch_ref": sketch, "expected_revision": 1,
                                "operation": "base", "depth_mm": 10.0, "direction": "positive"})
        env, code = call("kompas_save_document",
                         {"document_id": doc, "expected_revision": 2, "target_path": path})
        call("kompas_close_document", {"document_id": doc, "dirty_policy": "discard"})
        env, code = call("kompas_open_document",
                         {"application_id": app, "path": path, "access": "edit"})
        doc = ((env or {}).get("result") or {}).get("document_id")
        env, code = call("kompas_list_features", {"document_id": doc})
        rows = (env or {}).get("result") or []
        target = rows[0].get("feature_ref") if rows else None
        if target:
            call("kompas_update_feature",
                 {"feature_ref": target, "expected_revision": 3, "depth_mm": 13.0})
        env, code = call("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        record["dirty"] = ((env or {}).get("result") or {}).get("dirty")

    record["phase"] = "client_dies_now"
    with open(os.path.join(out, "parent-death.json"), "w", encoding="utf-8") as handle:
        json.dump(record, handle, ensure_ascii=False, indent=2)
    sys.stdout.write(json.dumps(record, ensure_ascii=False) + "\n")
    sys.stdout.flush()

    # АВАРИЙНАЯ СМЕРТЬ КЛИЕНТА: без `Client.close()`, без ожидания, без уборки. Так выглядит убитый
    # или упавший процесс клиента; stdin Хоста закрывается вместе с процессом, а не вызовом.
    os._exit(0)


if __name__ == "__main__":
    main()
