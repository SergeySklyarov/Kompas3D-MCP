"""Measure HOW the server process can be taken down, and which way leaves a live KOMPAS behind.

WHY. The 0.8.0 acceptance found the client taking the server down WITHOUT the graceful path: the
Host journal of that session has no `Hosting stopping`, its own `KOMPAS.exe` stayed alive, the Worker
was gone, and no rescue copy was taken. The order asks for measurement, not a guess: the client may
kill only the Host, the whole tree, or the console/parent, and the product's own protection is the
Worker noticing its pipe closed (`docs/decisions/host.md#no-tree-kill`).

WHAT IS MEASURED. For every method: what the Host journal and the Worker journal show, whether the
Worker is still alive, whether the launched `KOMPAS.exe` is still alive, and whether a rescue copy
appeared. The instrument is the OUTSIDE observer: it reads the process list itself, because a killed
Worker writes no report. It also records the PARENT of the launched KOMPAS - the question the order
leaves open is whether `KOMPAS.exe` is a descendant of the Host at all, and a tree kill answers that
by construction.

METHODS. `stdin` (close the Host's stdin, client alive), `kill-host` (TerminateProcess of the Host
alone), `kill-tree` (`taskkill /T /F` on the Host), `kill-parent` (terminate the Host's parent
process). `kill-mcp-pair` is ADDED beyond that list: it kills the Host and the Worker by proved PID
and leaves KOMPAS alone, which is the candidate explanation of the acceptance picture.

THE DOCUMENT IS BUILT THE WAY THE ACCEPTANCE GROUP `SD` BUILDS IT. A part is saved to disk, then
re-opened FROM DISK and modified: MEASURED (probe SO) that a document CREATED by the process and then
modified does NOT orphan the instance, while one opened from disk and modified does.

Only a PID this instrument proved itself (from `kompas_connect` or from its own process snapshot) is
ever stopped. Killing by name appears nowhere in the measurement itself; the one name-based sweep is
the PRE-RUN cleanup, which is what `mcp-smoke.py` already does before a run and is named in the record.

Usage:
  python scripts/measure-client-shutdown.py --host <host.exe> --out <evidence-dir>
      [--methods stdin,kill-host,kill-tree,kill-parent,kill-mcp-pair] [--runs 5]
      [--observe 30] [--self-test]

Nothing outside `scratch/` and the `--out` directory is written.
History: docs/decisions/host.md#client-shutdown
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from localconfig import isolate_config, local_config  # noqa: E402

_spec = importlib.util.spec_from_file_location("mcp_smoke",
                                              os.path.join(ROOT, "scripts", "mcp-smoke.py"))
smoke = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(smoke)

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

KOMPAS = "KOMPAS.exe"
WORKER = "KompasMcp.Worker.exe"
HOST = "KompasMcp.Host.exe"

METHODS = {
    "stdin": "закрытие stdin Хоста, процесс клиента жив",
    "kill-host": "TerminateProcess одного Хоста (taskkill /PID <Хост> /F)",
    "kill-tree": "снятие дерева процессов Хоста (taskkill /PID <Хост> /T /F)",
    "kill-parent": "завершение процесса-родителя Хоста (клиент)",
    "kill-mcp-pair": "Хост и Worker по доказанным PID, КОМПАС не трогается (сверх перечня §1)",
}
DEFAULT_METHODS = "stdin,kill-host,kill-tree,kill-parent,kill-mcp-pair"

# The picture the 0.8.0 acceptance recorded. A method matching it is the candidate explanation.
ACCEPTANCE_PICTURE = {"hosting_stopping": False, "worker_alive": False,
                      "kompas_alive": True, "rescue_new": False}


def error_code(env):
    return ((env or {}).get("error") or {}).get("code")


def result(env):
    return (env or {}).get("result") or {}


def call_tool(client, tool, args, timeout=300):
    payload = dict(args)
    if client.declares_operation_id(tool):
        payload.setdefault("operation_id", str(uuid.uuid4()))
    _e, env, _r = client.tool(tool, payload, timeout=timeout)
    return env, error_code(env)


def context(client, doc):
    _e, env, _r = client.tool("kompas_get_context", {"document_id": doc, "detail": "minimal"})
    return result(env)


def revision(client, doc):
    return context(client, doc).get("revision") or 1


def build_saved_part(client, app, client_path, name, depth=10.0):
    """A part SAVED to disk, then OPENED from disk and modified - the orphan scenario of group SD."""
    env, code = call_tool(client, "kompas_create_document",
                          {"application_id": app, "kind": "part", "name": name})
    doc = result(env).get("id") or result(env).get("document_id")
    if not doc:
        return None, "create_document: %s" % code
    env, code = call_tool(client, "kompas_create_sketch", {
        "document_id": doc, "expected_revision": revision(client, doc),
        "plane": {"base": "xy", "offset_mm": 0}, "name": name + "-s"})
    sketch = result(env).get("id")
    if not sketch:
        return None, "create_sketch: %s" % code
    env, code = call_tool(client, "kompas_edit_sketch", {
        "sketch_ref": sketch, "expected_revision": revision(client, doc), "mode": "append",
        "entities": [{"kind": "rectangle", "start_mm": [0.0, 0.0],
                      "width_mm": 100.0, "height_mm": 80.0}]})
    if code:
        return None, "edit_sketch: %s" % code
    env, code = call_tool(client, "kompas_finish_sketch",
                          {"sketch_ref": sketch, "require_closed_profile": False})
    if code:
        return None, "finish_sketch: %s" % code
    env, code = call_tool(client, "kompas_extrude", {
        "sketch_ref": sketch, "expected_revision": revision(client, doc),
        "operation": "base", "depth_mm": depth, "direction": "positive"})
    if code:
        return None, "extrude: %s" % code
    env, code = call_tool(client, "kompas_save_document",
                          {"document_id": doc, "expected_revision": revision(client, doc),
                           "target_path": client_path}, timeout=300)
    if code:
        return None, "save_document: %s" % code
    call_tool(client, "kompas_close_document",
              {"document_id": doc, "dirty_policy": "discard"}, timeout=180)
    env, code = call_tool(client, "kompas_open_document",
                          {"application_id": app, "path": client_path, "access": "edit"}, timeout=300)
    doc = result(env).get("document_id") or result(env).get("id")
    if not doc:
        return None, "open_document: %s" % code
    _e, fenv, _r = client.tool("kompas_list_features", {"document_id": doc}, timeout=180)
    rows = (fenv or {}).get("result") or []
    target = None
    for row in rows:
        if row.get("kind") in (None, "base_extrusion", "extrusion"):
            target = row.get("feature_ref") or row.get("id")
            break
    if target is None and rows:
        target = rows[0].get("feature_ref") or rows[0].get("id")
    if target is None:
        return None, "list_features: пусто, править нечего"
    env, code = call_tool(client, "kompas_update_feature", {
        "feature_ref": target, "expected_revision": revision(client, doc), "depth_mm": depth + 3.0},
        timeout=240)
    if code:
        return None, "update_feature: %s" % code
    if context(client, doc).get("dirty") is not True:
        return None, "документ не помечен изменённым"
    return doc, None


# ─────────────────────────── process-table helpers ───────────────────────────
def pids_of_image(rows, image):
    if rows is None:
        return None
    return {pid for pid, row in rows.items() if (row.get("image") or "").lower() == image.lower()}


def child_of(rows, parent_pid, image):
    if rows is None:
        return None
    for pid, row in rows.items():
        if row.get("parent_pid") == parent_pid and (row.get("image") or "").lower() == image.lower():
            return pid
    return None


def ancestry(rows, pid, limit=12):
    """The parent chain of a PID, read from the snapshot - the answer to 'is it a descendant?'."""
    chain, seen = [], set()
    while pid and pid not in seen and len(chain) < limit:
        seen.add(pid)
        row = (rows or {}).get(pid)
        if not row:
            break
        chain.append({"pid": pid, "image": row.get("image"), "parent_pid": row.get("parent_pid")})
        pid = row.get("parent_pid")
    return chain


def in_tree(rows, root_pid, pid):
    return pid in smoke.own_process_tree(rows or {}, [root_pid])


def force_stop(pid):
    if pid:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, text=True)


def message_present(path, text, host_pid=None):
    """Whether a journal holds a record whose message contains `text` (optionally for one Host)."""
    if not path or not os.path.isfile(path):
        return None
    found = False
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line or text not in line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if host_pid is not None and row.get("host_pid") not in (None, host_pid):
                continue
            if text in (row.get("message") or ""):
                found = True
    return found


def read_shutdown_entry(path):
    """The LAST `owned instance shutdown` line of the Worker journal - the report a killed Worker
    never writes, and whose absence is itself a measurement."""
    last = None
    if not path or not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if row.get("message") == "owned instance shutdown":
                last = {k: row.get(k) for k in ("process_id", "quit_issued", "process_exited",
                                                "killed_by_pid", "rescue_copies", "problems")}
    return last


def rescue_snapshot(directory):
    try:
        return sorted(f for f in os.listdir(directory) if os.path.isfile(os.path.join(directory, f)))
    except OSError:
        return []


def matches_acceptance_picture(record):
    """Does this run show exactly the picture the 0.8.0 acceptance recorded?"""
    return all(record.get(key) == value for key, value in ACCEPTANCE_PICTURE.items())


def patch_control_copy_directory(isolated, directory):
    """Point the service directory of control/rescue copies at this run, so the copy is found here.

    `isolate_config` moves the journals but leaves `control_copy_directory` alone; a rescue copy left
    in the shared `scratch/` could not be told apart from a copy of another run.
    """
    with open(isolated, encoding="utf-8-sig") as fh:
        data = json.load(fh)
    data["control_copy_directory"] = directory
    with open(isolated, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)
    return data


# ─────────────────────────── one run ───────────────────────────
def arm_run(host_exe, base_config, run_dir, tag):
    """Build the dirty document and return (record, client, paths). Shared by every method."""
    os.makedirs(run_dir, exist_ok=True)
    isolated = isolate_config(base_config, run_dir, "cs-" + tag)
    cfg = patch_control_copy_directory(isolated, run_dir)
    paths = {"isolated": isolated, "host_log": cfg.get("log_path"),
             "worker_log": cfg.get("worker_log_path"),
             "rescue_dir": os.path.join(cfg.get("control_copy_directory") or run_dir,
                                        "control-copies", "rescue")}
    record = {"tag": tag, "isolated_config": isolated, "paths": paths, "problems": []}

    client = smoke.Client(host_exe, isolated)
    record["host_pid"] = client.p.pid
    try:
        client.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                   "clientInfo": {"name": "measure-client-shutdown", "version": "0"}},
                    timeout=60)
        client.notify("notifications/initialized")
        env, code = call_tool(client, "kompas_connect", {"mode": "launch", "make_visible": False})
        app = (env or {}).get("application_id")
        record["kompas_pid"] = result(env).get("process_id")
        if not app:
            record["problems"].append("launch: %s" % code)
            return record, client, paths

        client_path = os.path.join(run_dir, "client.m3d")
        _doc, problem = build_saved_part(client, app, client_path, "CS-" + tag)
        if problem:
            record["problems"].append(problem)
            return record, client, paths
        record["document"] = client_path

        rows = smoke.process_snapshot()
        record["worker_pid"] = child_of(rows, client.p.pid, WORKER)
        record["kompas_ancestry"] = ancestry(rows, record.get("kompas_pid"))
        record["kompas_in_host_tree"] = (in_tree(rows, client.p.pid, record["kompas_pid"])
                                         if record.get("kompas_pid") else None)
        record["kompas_parent_image"] = None
        parent_pid = (rows or {}).get(record.get("kompas_pid") or 0, {}).get("parent_pid")
        if parent_pid:
            record["kompas_parent_image"] = (rows.get(parent_pid) or {}).get("image")
    except Exception as ex:  # noqa: BLE001 - a failed build-up is recorded, not hidden
        record["problems"].append("setup: %s" % ex)
    return record, client, paths


def wait_pid_gone(pid, timeout):
    """Wait for a PID to leave the process table; True when it is gone (or was never named)."""
    if not pid:
        return True
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        rows = smoke.process_snapshot()
        if rows is None or pid not in rows:
            return True
        time.sleep(0.25)
    return False


def observe(record, paths, kompas_pid, observe_s):
    """Wait for the launched KOMPAS to leave, then read the process list and both journals."""
    started = time.monotonic()
    while time.monotonic() - started < observe_s:
        alive = pids_of_image(smoke.process_snapshot(), KOMPAS)
        if alive is not None and kompas_pid not in alive:
            break
        time.sleep(0.25)
    record["seconds_to_exit"] = round(time.monotonic() - started, 2)

    # The Worker writes its shutdown report AFTER the instance is gone, so the journals are read only
    # once the Worker itself has left: reading them at the moment KOMPAS disappears loses that line.
    wait_pid_gone(record.get("worker_pid"), 15)

    rows = smoke.process_snapshot()
    alive_kompas = pids_of_image(rows, KOMPAS)
    record["kompas_alive"] = None if alive_kompas is None else (kompas_pid in alive_kompas)
    record["host_alive"] = None if rows is None else (record.get("host_pid") in rows)
    record["worker_alive"] = None if rows is None else (record.get("worker_pid") in rows)

    host_log, worker_log = paths["host_log"], paths["worker_log"]
    record["hosting_stopping"] = message_present(host_log, "Hosting stopping", record.get("host_pid"))
    record["transport_end"] = message_present(host_log, "transport completed reading messages",
                                              record.get("host_pid"))
    record["worker_host_disconnected"] = message_present(
        worker_log, "host disconnected; worker exiting")
    record["worker_shutdown_report"] = read_shutdown_entry(worker_log)
    after = rescue_snapshot(paths["rescue_dir"])
    record["rescue_new"] = [f for f in after if f not in (record.get("rescue_before") or [])]
    record["rescue_new"] = bool(record["rescue_new"])
    record["matches_acceptance_picture"] = matches_acceptance_picture(record)
    return record


def apply_method(method, record, client, child=None):
    host_pid, worker_pid = record.get("host_pid"), record.get("worker_pid")
    if method == "stdin":
        client.p.stdin.close()
    elif method == "kill-host":
        force_stop(host_pid)
    elif method == "kill-tree":
        subprocess.run(["taskkill", "/PID", str(host_pid), "/T", "/F"],
                       capture_output=True, text=True)
    elif method == "kill-mcp-pair":
        force_stop(host_pid)
        force_stop(worker_pid)
    elif method == "kill-parent":
        child.kill()
    else:
        raise ValueError(method)


def measure(method, host_exe, base_config, workdir, evidence, index, observe_s, baseline):
    tag = "%s-%02d" % (method, index)
    run_dir = os.path.join(workdir, tag)
    record = {"method": method, "index": index, "method_means": METHODS[method],
              "kompas_baseline": sorted(baseline)}
    child = None
    client = None
    try:
        if method == "kill-parent":
            # The Host's parent must be a process the instrument can kill: a child of its own that
            # arms the session and then idles, exactly as a client application would.
            child = subprocess.Popen(
                [sys.executable, "-I", os.path.abspath(__file__), "--child",
                 "--run-dir", run_dir, "--host", host_exe, "--base-config", base_config,
                 "--tag", tag],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            armed = wait_for_armed(run_dir, 420)
            if armed is None:
                record["problems"] = ["родитель не сообщил о готовности"]
                return record, None
            record.update({k: v for k, v in armed.items() if k not in ("paths", "isolated_config")})
            paths = armed["paths"]
            record["paths"] = paths
            record["rescue_before"] = rescue_snapshot(paths["rescue_dir"])
            apply_method(method, record, None, child=child)
            record = observe(record, paths, record.get("kompas_pid"), observe_s)
        else:
            armed, client, paths = arm_run(host_exe, base_config, run_dir, tag)
            record.update(armed)
            if record.get("problems"):
                return record, client
            record["rescue_before"] = rescue_snapshot(paths["rescue_dir"])
            apply_method(method, record, client)
            record = observe(record, paths, record.get("kompas_pid"), observe_s)
    except Exception as ex:  # noqa: BLE001
        record.setdefault("problems", []).append("measure: %s" % ex)
    finally:
        rows = smoke.process_snapshot()
        record["kompas_left_after_run"] = sorted(
            (pids_of_image(rows, KOMPAS) or set()) - (record.get("kompas_baseline") or set()))
        for pid in (record.get("kompas_pid"), record.get("worker_pid"), record.get("host_pid")):
            if pid and rows is not None and pid in rows:
                force_stop(pid)
        if client is not None:
            try:
                client.close()
            except Exception:  # noqa: BLE001
                pass
        if child is not None:
            try:
                child.kill()
                child.wait(timeout=10)
            except Exception:  # noqa: BLE001
                pass
        copy_evidence(record, run_dir, evidence, tag)
    return record, None


def wait_for_armed(run_dir, timeout):
    path = os.path.join(run_dir, "armed.json")
    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.isfile(path):
            try:
                with open(path, encoding="utf-8") as fh:
                    return json.load(fh)
            except ValueError:
                time.sleep(0.5)
                continue
        time.sleep(0.5)
    return None


def copy_evidence(record, run_dir, evidence, tag):
    """Keep the journals of this run next to the summary: they are the measurement's raw form."""
    target = os.path.join(evidence, "runs", tag)
    os.makedirs(target, exist_ok=True)
    for key in ("host_log", "worker_log"):
        source = (record.get("paths") or {}).get(key)
        if source and os.path.isfile(source):
            shutil.copyfile(source, os.path.join(target, os.path.basename(source)))
    record["evidence_dir"] = os.path.relpath(target, ROOT).replace("\\", "/")


# ─────────────────────────── reporting ───────────────────────────
def summarise(records):
    lines = ["| Способ | Опытов | `Hosting stopping` | Worker жив | КОМПАС жив | Копия снята |"
             " КОМПАС в дереве Хоста | Картина приёмки |",
             "|---|---|---|---|---|---|---|---|"]
    for method in METHODS:
        runs = [r for r in records if r.get("method") == method]
        if not runs:
            continue
        ok = [r for r in runs if not r.get("problems")]
        def count(key, value=True):
            return "%d из %d" % (sum(1 for r in ok if r.get(key) is value), len(ok))
        tree = [r.get("kompas_in_host_tree") for r in ok if r.get("kompas_in_host_tree") is not None]
        tree_text = ("да" if all(tree) else "нет" if not any(tree) else "по-разному") if tree else "-"
        lines.append("| `%s` | %d | %s | %s | %s | %s | %s | %s |" % (
            method, len(runs), count("hosting_stopping", False), count("worker_alive", True),
            count("kompas_alive", True), count("rescue_new", False), tree_text,
            "%d из %d" % (sum(1 for r in ok if r.get("matches_acceptance_picture")), len(ok))))
    return lines


def write_reports(records, evidence, host_exe, preflight=None):
    payload = {"host": os.path.abspath(host_exe), "picture_sought": ACCEPTANCE_PICTURE,
               "methods": METHODS, "preflight": preflight, "records": records}
    with open(os.path.join(evidence, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
    with open(os.path.join(evidence, "summary.md"), "w", encoding="utf-8") as fh:
        fh.write("# Снятие сервера клиентом: сводка опытов\n\n")
        fh.write("Хост: `%s`\n\n" % os.path.abspath(host_exe))
        fh.write("Искомая картина приёмки 0.8.0: `Hosting stopping` нет, Worker не жив, КОМПАС жив,"
                 " копии нет.\n\n")
        fh.write("\n".join(summarise(records)) + "\n")
    return payload


def print_summary(records):
    print("\n=== сводка ===")
    for line in summarise(records):
        print(line)
    for record in records:
        if record.get("problems"):
            print("  [%s] неполный опыт: %s" % (record.get("tag"), "; ".join(record["problems"])))


# ─────────────────────────── self-test ───────────────────────────
def self_test():
    """Checks the pure parts the verdict rests on: the tree walk, the journal reader, the picture."""
    failures = []

    def check(name, ok):
        print("  [%s] %s" % ("PASS" if ok else "FAIL", name))
        if not ok:
            failures.append(name)

    tree = {10: {"image": HOST, "parent_pid": 1}, 11: {"image": WORKER, "parent_pid": 10},
            12: {"image": KOMPAS, "parent_pid": 11}, 13: {"image": KOMPAS, "parent_pid": 999}}
    check("own_process_tree идёт по родству, а не по имени",
          smoke.own_process_tree(tree, [10]) == {10, 11, 12})
    check("KOMPAS с чужим родителем в дерево не попадает",
          13 not in smoke.own_process_tree(tree, [10]))
    check("ancestry называет цепочку до корня",
          [e["image"] for e in ancestry(tree, 12)] == [KOMPAS, WORKER, HOST])
    check("in_tree отвечает на вопрос о потомке",
          in_tree(tree, 10, 12) and not in_tree(tree, 10, 13))
    check("child_of находит Worker по родителю", child_of(tree, 10, WORKER) == 11)
    check("child_of не путает имя образа",
          child_of(tree, 10, KOMPAS) is None)

    picture = dict(ACCEPTANCE_PICTURE)
    check("картина приёмки узнаётся", matches_acceptance_picture(picture))
    check("картина приёмки различает каждое поле",
          not any(matches_acceptance_picture({**picture, k: not v}) for k, v in picture.items()))

    probe = os.path.join(ROOT, "scratch", "client-shutdown-selftest.jsonl")
    os.makedirs(os.path.dirname(probe), exist_ok=True)
    with open(probe, "w", encoding="utf-8") as fh:
        fh.write(json.dumps({"message": "sdk: Hosting stopping", "host_pid": 7}) + "\n")
        fh.write(json.dumps({"message": "sdk: Hosting stopping", "host_pid": 8}) + "\n")
    check("журнал читается по хосту", message_present(probe, "Hosting stopping", 7) is True)
    check("чужой хост не зачитывается",
          message_present(probe, "Hosting stopping", 9) is False)
    check("отсутствующая запись - не PASS", message_present(probe, "Hosted", 7) is False)
    check("отсутствующий файл назван, а не прочитан как пусто",
          message_present(probe + ".none", "x") is None)
    check("снимок процессов снимается", smoke.process_snapshot() is not None)
    check("tasklist отвечает (пустой ответ не читается как «процессов нет»)",
          pids_of_image(smoke.process_snapshot(), KOMPAS) is not None)
    os.remove(probe)

    print("self-test: %s" % ("OK" if not failures else "FAIL %s" % failures))
    return 0 if not failures else 1


def child_main(args):
    """The Host's parent: arm the session, publish the record, then idle until the instrument kills it."""
    record, _client, paths = arm_run(args.host, args.base_config, args.run_dir, args.tag)
    record["paths"] = paths
    record["pid"] = os.getpid()
    with open(os.path.join(args.run_dir, "armed.json"), "w", encoding="utf-8") as fh:
        json.dump(record, fh, ensure_ascii=False, indent=2)
    sys.stdout.write("armed %s\n" % json.dumps({k: record.get(k) for k in
                                                ("host_pid", "worker_pid", "kompas_pid")}))
    sys.stdout.flush()
    while True:
        time.sleep(5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host")
    parser.add_argument("--out")
    parser.add_argument("--methods", default=DEFAULT_METHODS)
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--observe", type=float, default=30.0)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--child", action="store_true")
    parser.add_argument("--run-dir")
    parser.add_argument("--base-config")
    parser.add_argument("--tag")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.child:
        child_main(args)
        return 0

    if not args.host or not args.out:
        print(__doc__)
        return 2
    if not os.path.isfile(args.host):
        print("нет Хоста:", args.host)
        return 2

    base_config = local_config(ROOT)
    evidence = os.path.abspath(args.out)
    os.makedirs(evidence, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    workdir = os.path.join(ROOT, "scratch", "client-shutdown", stamp)

    # The same pre-run sweep `mcp-smoke.py` performs before a run: a stray KOMPAS of an earlier run
    # would read as a survivor of THIS one. Name-based, and named in the record as such.
    preflight = smoke.close_foreign_processes([os.getpid()], "before-client-shutdown", log=print)
    baseline = pids_of_image(smoke.process_snapshot(), KOMPAS) or set()

    records = []
    for method in [m for m in args.methods.split(",") if m]:
        if method not in METHODS:
            print("неизвестный способ:", method)
            return 2
        for index in range(1, args.runs + 1):
            print("[measure] %s #%d ..." % (method, index), flush=True)
            record, _ = measure(method, args.host, base_config, workdir, evidence, index,
                                args.observe, baseline)
            records.append(record)
            print("[measure] %s #%d: stopping=%s worker_alive=%s kompas_alive=%s copy=%s"
                  " picture=%s tree=%s %s" % (
                      method, index, record.get("hosting_stopping"), record.get("worker_alive"),
                      record.get("kompas_alive"), record.get("rescue_new"),
                      record.get("matches_acceptance_picture"), record.get("kompas_in_host_tree"),
                      "; ".join(record.get("problems") or [])), flush=True)

    write_reports(records, evidence, args.host, preflight)
    print_summary(records)
    print("out:", os.path.join(evidence, "summary.json"))

    leftovers = pids_of_image(smoke.process_snapshot(), KOMPAS) or set()
    if leftovers:
        print("ВНИМАНИЕ: KOMPAS.exe всё ещё в списке процессов:", sorted(leftovers))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
