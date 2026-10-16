"""Measure the session-end time against the NUMBER of unsaved documents (order DEBT_SHUTDOWN_MANY_DOCS).

WHY. The order `SHUTDOWN_NO_ORPHANS` measured the rescue-copy + close sequence on ONE document
(763-783 ms) and left the number of documents unbounded. The Host waits at most
`GracefulShutdownWindowMs` = 20 s for the Worker to leave and then KILLS it; the PID of the launched
KOMPAS instance is terminated by the Worker itself, so a Worker killed mid-sequence can leave the
instance orphaned. This instrument answers the open question of the debt row 22: HOW the time grows
with N and WHERE the 20 s window is crossed.

WHAT IS MEASURED, and on which route. Every document is built through the SAME product path the
acceptance group `SD` uses for the orphan scenario: create a part, save it to a file, close it,
re-open it FROM DISK and modify it. MEASURED (probe SO): a document CREATED by the process and then
modified does NOT orphan the instance; a document OPENED FROM DISK and modified does. A row built on
`create_document` alone would measure a case the order is not about.

The session is ended the way the Host ends it when the client goes away: the Host's stdin is closed,
and the instrument waits for the launched KOMPAS PID to leave. The whole point is the OUTSIDE
observation: the instrument reads the process list itself, it does not trust the Worker's report
(which a killed Worker never writes).

Only a PID the instrument proved itself (`process_id` from `kompas_connect`) is ever stopped. Killing
by name is forbidden by the project spec and appears nowhere here.

Usage:
  python scripts/measure-shutdown-many-docs.py --host <host.exe> --out <json> [--counts 1,5,10]
      [--timeout 120] [--self-test]

Nothing outside `scratch/` is written.
History: docs/decisions/host.md#shutdown-many-docs
"""

from __future__ import annotations

import importlib.util
import json
import math
import os
import subprocess
import sys
import time
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from localconfig import isolate_config, local_config  # noqa: E402

_spec = importlib.util.spec_from_file_location("mcp_smoke", os.path.join(ROOT, "scripts", "mcp-smoke.py"))
smoke = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(smoke)


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def kompas_pids():
    """PIDs of KOMPAS.exe read from the OS. An empty answer is NOT read as 'no processes'."""
    done = subprocess.run(["tasklist", "/FI", "IMAGENAME eq KOMPAS.exe", "/FO", "CSV", "/NH"],
                          capture_output=True, text=True)
    found = set()
    if done.returncode != 0:
        return None
    for line in done.stdout.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].upper().startswith("KOMPAS"):
            try:
                found.add(int(parts[1]))
            except ValueError:
                pass
    return found


def error_code(env):
    return ((env or {}).get("error") or {}).get("code")


def result(env):
    return (env or {}).get("result") or {}


def pids_of(image):
    done = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + image, "/FO", "CSV", "/NH"],
                          capture_output=True, text=True)
    if done.returncode != 0:
        return None
    found = set()
    for line in done.stdout.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].upper().startswith(image.split(".")[0].upper()):
            try:
                found.add(int(parts[1]))
            except ValueError:
                pass
    return found


def force_stop(pid):
    if pid:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, text=True)


def build_dirty_part(probe, app, client_path, name, depth=10.0, entities=0):
    """A part SAVED to disk, then OPENED from disk and modified - the orphan scenario of the group SD.

    `entities` adds that many line segments to the sketch before the extrusion: the rescue copy is a
    SaveAs of the whole file, so the size of the document is what the per-document cost tracks.
    """
    env, code = probe.call_tool("kompas_create_document",
                                {"application_id": app, "kind": "part", "name": name})
    doc = result(env).get("id") or result(env).get("document_id")
    if not doc:
        return None, "create_document: %s" % code
    env, code = probe.call_tool("kompas_create_sketch", {
        "document_id": doc, "expected_revision": probe.revision(doc),
        "plane": {"base": "xy", "offset_mm": 0}, "name": name + "-s"})
    sketch = result(env).get("id")
    if not sketch:
        return None, "create_sketch: %s" % code
    # A CLOSED polyline of many vertices, not many loose segments: a sketch of loose segments is not a
    # profile and the extrusion refuses it (GEOMETRY_FAILED), which would measure the wrong thing. The
    # polygon is the same load the bulk-limit instrument uses and it extrudes as one profile.
    # ONE closed contour either way: the small load is a rectangle, the large one is a closed polyline of
    # `entities` vertices. Two disjoint contours would make the base extrusion an ambiguous request and
    # the size of the document would be measured through a refusal instead of through a save.
    if entities >= 3:
        geometry = [{"kind": "polyline", "closed": True, "points_mm": [
            [20.0 * math.cos(2.0 * math.pi * i / entities),
             20.0 * math.sin(2.0 * math.pi * i / entities)] for i in range(entities)]}]
    else:
        geometry = [{"kind": "rectangle", "start_mm": [0.0, 0.0],
                     "width_mm": 100.0, "height_mm": 80.0}]
    env, code = probe.call_tool("kompas_edit_sketch", {
        "sketch_ref": sketch, "expected_revision": probe.revision(doc), "mode": "append",
        "entities": geometry})
    if code:
        return None, "edit_sketch: %s" % code
    env, code = probe.call_tool("kompas_finish_sketch",
                                {"sketch_ref": sketch, "require_closed_profile": False})
    if code:
        return None, "finish_sketch: %s" % code
    env, code = probe.call_tool("kompas_extrude", {
        "sketch_ref": sketch, "expected_revision": probe.revision(doc),
        "operation": "base", "depth_mm": depth, "direction": "positive"})
    if code:
        return None, "extrude: %s" % code

    env, code = probe.call_tool("kompas_save_document",
                                {"document_id": doc, "expected_revision": probe.revision(doc),
                                 "target_path": client_path})
    if code:
        return None, "save_document: %s" % code
    probe.call_tool("kompas_close_document", {"document_id": doc, "dirty_policy": "discard"})

    env, code = probe.call_tool("kompas_open_document",
                                {"application_id": app, "path": client_path, "access": "edit"})
    doc = result(env).get("document_id") or result(env).get("id")
    if not doc:
        return None, "open_document: %s" % code

    _e, fenv, _r = probe.raw_tool("kompas_list_features", {"document_id": doc})
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

    env, code = probe.call_tool("kompas_update_feature", {
        "feature_ref": target, "expected_revision": probe.revision(doc), "depth_mm": depth + 3.0})
    if code:
        return None, "update_feature: %s" % code

    ctx = probe.context(doc)
    if ctx.get("dirty") is not True:
        return None, "документ не помечен изменённым: dirty=%s" % ctx.get("dirty")
    return doc, None


class Probe:
    """A thin wrapper over `mcp_smoke.Client` that adds the two helpers the measurement needs."""

    def __init__(self, host, config):
        self.client = smoke.Client(host, config)
        self.client.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "measure-shutdown-many-docs", "version": "0"}}, timeout=60)
        self.client.notify("notifications/initialized")

    def call_tool(self, tool, args, timeout=300):
        payload = dict(args)
        if self.client.declares_operation_id(tool):
            payload.setdefault("operation_id", str(uuid.uuid4()))
        _e, env, _r = self.client.tool(tool, payload, timeout=timeout)
        return env, error_code(env)

    def raw_tool(self, tool, args, timeout=300):
        payload = dict(args)
        if self.client.declares_operation_id(tool):
            payload.setdefault("operation_id", str(uuid.uuid4()))
        return self.client.tool(tool, payload, timeout=timeout)

    def revision(self, doc):
        return self.context(doc).get("revision") or 1

    def context(self, doc):
        _e, env, _r = self.client.tool("kompas_get_context", {"document_id": doc, "detail": "minimal"})
        return result(env)

    def close(self):
        self.client.close()


def read_shutdown_entry(path):
    """The LAST `owned instance shutdown` line of the Worker log: the report the client never sees on the
    transport-end path (the Host writes the Shutdown frame and does not read the reply)."""
    last = None
    if not path or not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if row.get("message") == "owned instance shutdown":
                last = row
    return last


def measure(host, base_config, count, workdir, timeout_s, entities=0):
    os.makedirs(workdir, exist_ok=True)
    isolated = isolate_config(base_config, workdir, "sd-many-n%d" % count)
    with open(base_config, encoding="utf-8-sig") as fh:
        cfg = json.load(fh)
    with open(isolated, encoding="utf-8-sig") as fh:
        worker_log = json.load(fh).get("worker_log_path")
    rescue_dir = os.path.join(cfg.get("control_copy_directory") or workdir, "control-copies", "rescue")

    def rescue_snapshot():
        try:
            return {f: os.path.getsize(os.path.join(rescue_dir, f))
                    for f in os.listdir(rescue_dir)
                    if os.path.isfile(os.path.join(rescue_dir, f))}
        except OSError:
            return {}

    before_rescue = rescue_snapshot()
    baseline_kompas = kompas_pids() or set()
    baseline_workers = pids_of("KompasMcp.Worker.exe") or set()

    probe = Probe(host, isolated)
    pid = None
    record = {"count": count, "built": 0, "problem": None, "seconds_to_exit": None,
              "exited": False, "rescue_new": [], "rescue_bytes": 0, "survived": [], "kill_used": None}
    try:
        env, code = probe.call_tool("kompas_connect", {"mode": "launch", "make_visible": False})
        app = (env or {}).get("application_id")
        pid = result(env).get("process_id")
        record["process_id"] = pid
        if not app:
            record["problem"] = "launch: %s" % code
            raise RuntimeError(record["problem"])

        for i in range(count):
            client_path = os.path.join(workdir, "doc-%02d.m3d" % i)
            _doc, problem = build_dirty_part(probe, app, client_path, "SD-N%02d" % i, entities=entities)
            if problem:
                record["problem"] = "документ %d: %s" % (i, problem)
                raise RuntimeError(record["problem"])
            record["built"] += 1

        _e, inv, _r = probe.raw_tool("kompas_session_status", {})
        dirty = ((inv or {}).get("result") or {}).get("documents_dirty")
        record["documents_dirty"] = dirty

        # END THE SESSION the way the Host ends it when the client disappears: close stdin.
        started = time.monotonic()
        probe.client.p.stdin.close()

        deadline = started + timeout_s
        while time.monotonic() < deadline:
            current = kompas_pids()
            if current is not None and pid not in current:
                record["exited"] = True
                break
            time.sleep(0.25)
        record["seconds_to_exit"] = round(time.monotonic() - started, 3)
    except RuntimeError as ex:
        # The build-up failed: the reason is already in `record["problem"]`, the session must still be
        # ended and the process list cleaned - hence the `finally` below.
        record["problem"] = record["problem"] or str(ex)
    finally:
        try:
            probe.client.p.wait(timeout=20)
        except Exception:
            try:
                probe.client.p.kill()
            except Exception:
                pass

        # The report and the cleanup run even when the scenario failed early: a failed build-up must not
        # leave a KOMPAS of THIS run alive, and the reason it failed must reach the record.
        entry = read_shutdown_entry(worker_log)
        if entry is not None:
            copies = entry.get("rescue_copies") or []
            # The log names the field `path` (anonymous object `new { DocumentId, Path, Reason }`); the
            # client-facing answer names the same field `rescue_copy`. Both are read.
            rescued = [c for c in copies if c.get("path") or c.get("rescue_copy")]
            record["report_rescued"] = len(rescued)
            record["report_named"] = len(copies) - len(rescued)
            record["report_budget_ms"] = entry.get("session_end_budget_ms")
            record["report_rescue_ms"] = entry.get("rescue_ms")
            record["report_close_ms"] = entry.get("close_ms")
            record["report_quit_ms"] = entry.get("quit_ms")
            record["report_killed_by_pid"] = entry.get("killed_by_pid")
            record["report_process_exited"] = entry.get("process_exited")

        after_rescue = rescue_snapshot()
        new_files = sorted(f for f in after_rescue if f not in before_rescue)
        record["rescue_new"] = [{"name": f, "bytes": after_rescue[f]} for f in new_files]
        record["rescue_bytes"] = sum(after_rescue[f] for f in new_files)

        current = kompas_pids()
        record["survived"] = sorted((current - baseline_kompas) if current is not None else [])
        record["worker_new_alive"] = sorted((pids_of("KompasMcp.Worker.exe") or set()) - baseline_workers)
        for survivor in record["survived"]:
            force_stop(survivor)
        if pid:
            force_stop(pid)
    return record


def self_test():
    """The instrument must read the process list honestly: an empty answer is not 'no processes'."""
    pids = kompas_pids()
    if pids is None:
        print("self-test FAIL: tasklist did not answer")
        return 1
    print("self-test OK: tasklist answered, KOMPAS.exe now =", sorted(pids))
    return 0


def main():
    if "--self-test" in sys.argv:
        return self_test()

    host = argument("--host")
    out = argument("--out")
    counts = argument("--counts", "1,5,10")
    timeout_s = int(argument("--timeout", "120"))
    entities = int(argument("--entities", "0"))
    if not host or not out:
        print(__doc__)
        return 2
    if not os.path.isfile(host):
        print("нет Хоста:", host)
        return 2

    base_config = local_config(ROOT)
    out = os.path.abspath(out)
    os.makedirs(os.path.dirname(out), exist_ok=True)

    stamp = time.strftime("%Y%m%d-%H%M%S")
    records = []
    for raw in counts.split(","):
        count = int(raw)
        workdir = os.path.join(ROOT, "scratch", "shutdown-many", "%s-n%d" % (stamp, count))
        print("[measure] N=%d ..." % count, flush=True)
        try:
            record = measure(host, base_config, count, workdir, timeout_s, entities=entities)
        except Exception as ex:  # noqa: BLE001 - a failed build-up is recorded, not hidden
            print("[measure] N=%d: setup failed: %s" % (count, ex), flush=True)
            continue
        records.append(record)
        print("[measure] N=%d: built=%d exited=%s t=%ss rescue=%d files/%d bytes survived=%s "
              "report(rescued=%s named=%s budget=%s rescue_ms=%s close_ms=%s quit_ms=%s killed=%s) %s"
              % (count, record["built"], record["exited"], record["seconds_to_exit"],
                 len(record["rescue_new"]), record["rescue_bytes"], record["survived"],
                 record.get("report_rescued"), record.get("report_named"),
                 record.get("report_budget_ms"), record.get("report_rescue_ms"),
                 record.get("report_close_ms"), record.get("report_quit_ms"),
                 record.get("report_killed_by_pid"), record.get("problem") or ""), flush=True)

    payload = {"host": os.path.abspath(host), "counts": counts, "entities": entities,
               "records": records}
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
    print("out:", out)

    leftovers = kompas_pids()
    if leftovers:
        print("ВНИМАНИЕ: KOMPAS.exe всё ещё в списке процессов:", sorted(leftovers))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
