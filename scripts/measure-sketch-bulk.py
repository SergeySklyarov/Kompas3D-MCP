"""Measure the worst wall time of ONE bulk `kompas_edit_sketch` call, on two builds, alternating.

WHY. The schema limits `SketchLimits.MaxEntitiesPerCall = 5000` and `MaxPolylineVertices = 2000`
were chosen from a measurement whose worst case had to fit half of the synchronous budget
(`docs/decisions/adapter-sketch.md#bulk-limits`). That measurement was taken on another machine.
The same call now reports roughly twice the time, and "the machine got slower" and "the product got
slower" are different answers with different consequences. This instrument answers it by measuring
BOTH builds with ONE procedure, alternating them round by round so that the load of the machine
falls on both equally.

WHAT IS MEASURED. The time from writing the `tools/call` request to reading its reply - the wait a
client sees. The call is sent ONCE and is NOT re-polled on `status = running`: a polled reply would
measure the sync-budget cut-off instead of the work, and the envelope status is recorded so a
cut-off can be told from a finished call.

SHAPE OF THE RUN, fixed before it starts:
- per measurement: new part document, new sketch, ONE `kompas_edit_sketch` call, mode `append`;
- two loads: N line segments, and ONE closed native polyline of V vertices;
- `--rounds` rounds; in every round each build is measured on each load, in the order given;
- `--mode per-build` keeps one Host+KOMPAS per build alive for the whole series; `per-measurement`
  starts a fresh Host+KOMPAS for every single measurement. Both are identical for both builds.

Nothing outside `scratch/` is written; the raw result goes to `--out`.

Usage:
  python scripts/measure-sketch-bulk.py --build HEAD=<host.exe> --build BASE=<host.exe>
      [--rounds 10] [--mode per-build] [--out scratch/sb02/limits.json] [--self-test]
"""

from __future__ import annotations

import ctypes
import hashlib
import importlib.util
import io
import json
import math
import os
import statistics
import sys
import time
import uuid

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from localconfig import isolate_config, local_config  # noqa: E402

_spec = importlib.util.spec_from_file_location("mcp_smoke", os.path.join(ROOT, "scripts", "mcp-smoke.py"))
smoke = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(smoke)

# INVARIANT: both builds must run with the SAME synchronous budget, or their wall times are not
# comparable: the budget is the point at which a call stops being synchronous.
SYNC_BUDGET_MS = 40000


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def error_code(envelope):
    return ((envelope or {}).get("error") or {}).get("code")


def segments(count):
    """The load of `SB-02.limits`: `count` independent line segments in one append call."""
    return [{"kind": "line", "start_mm": [float(i), 0.0], "end_mm": [float(i), 1.0]}
            for i in range(count)]


def polygon(vertices, radius=20.0):
    """The load of the polyline limit: ONE closed native polyline of `vertices` points."""
    return [[radius * math.cos(2 * math.pi * i / vertices), radius * math.sin(2 * math.pi * i / vertices)]
            for i in range(vertices)]


class _FileTime(ctypes.Structure):
    _fields_ = [("low", ctypes.c_uint32), ("high", ctypes.c_uint32)]


class _MemoryStatus(ctypes.Structure):
    _fields_ = [("length", ctypes.c_ulong), ("memory_load", ctypes.c_ulong),
                ("total_phys", ctypes.c_ulonglong), ("avail_phys", ctypes.c_ulonglong),
                ("total_page", ctypes.c_ulonglong), ("avail_page", ctypes.c_ulonglong),
                ("total_virtual", ctypes.c_ulonglong), ("avail_virtual", ctypes.c_ulonglong),
                ("avail_extended_virtual", ctypes.c_ulonglong)]


def cpu_snapshot():
    """(idle, busy) 100 ns ticks since boot. The machine load is recorded, not inferred."""
    idle, kernel, user = _FileTime(), _FileTime(), _FileTime()
    ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user))
    ticks = lambda ft: (ft.high << 32) | ft.low  # noqa: E731
    return ticks(idle), ticks(kernel) + ticks(user)


def machine_load(since=None):
    """CPU percent over the window since `since` (or a 200 ms sample) plus the physical memory load."""
    start = since or cpu_snapshot()
    if since is None:
        time.sleep(0.2)
    idle, busy = cpu_snapshot()
    d_idle, d_busy = idle - start[0], busy - start[1]
    total = d_idle + d_busy
    memory = _MemoryStatus()
    memory.length = ctypes.sizeof(_MemoryStatus)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(memory))
    return {"cpu_percent": round(100.0 * d_busy / total, 1) if total else None,
            "memory_load_percent": int(memory.memory_load),
            "memory_available_mb": round(memory.avail_phys / (1 << 20))}


def timed_call(client, tool, arguments, timeout):
    """ONE raw round trip. `Client.call` writes the request and reads the reply; nothing else is done.

    The reply is NOT re-polled on `status = running`: polling would add the sync-budget cut-off to
    the clock, and the envelope keeps the status so a cut-off stays distinguishable from the work.
    """
    started = time.perf_counter()
    result = client.call("tools/call", {"name": tool, "arguments": arguments}, timeout=timeout)
    return time.perf_counter() - started, result.get("structuredContent"), result


class Session:
    """One Host process and one KOMPAS instance, able to take several measurements in a row."""

    def __init__(self, build, config, visibility):
        self.build = build
        self.config = config
        self.visibility = visibility
        self.client = None
        self.application = None
        self.server_version = None
        self.opened = False

    def open(self):
        self.client = smoke.Client(self.build["host"], self.config)
        init = self.client.call("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "sb02-limit", "version": "0"}}, timeout=120)
        self.server_version = (init.get("serverInfo") or {}).get("version")
        self.client.notify("notifications/initialized")
        envelope = self._call("kompas_connect", {"mode": "launch", "make_visible": self.visibility}, 600)
        if error_code(envelope):
            raise RuntimeError("kompas_connect: %s %s" % (error_code(envelope), envelope))
        self.application = envelope.get("application_id")
        self.opened = True

    def _call(self, tool, arguments, timeout=600):
        payload = dict(arguments)
        if self.client.declares_operation_id(tool):
            payload["operation_id"] = str(uuid.uuid4())
        _is_error, envelope, _result = self.client.tool(tool, payload, timeout=timeout)
        return envelope

    def measure(self, label, entities, tag):
        """New part, new sketch, ONE timed append call, then the document is closed."""
        envelope = self._call("kompas_create_document", {
            "application_id": self.application, "kind": "part", "name": "SB02_%s_%s" % (label, tag)})
        document = envelope.get("document_id") or (envelope.get("result") or {}).get("id")
        revision = envelope.get("revision_after") or 1
        envelope = self._call("kompas_create_sketch", {
            "document_id": document, "expected_revision": revision,
            "plane": {"base": "xy", "offset_mm": 0}, "name": "SB02_%s_%s" % (label, tag)})
        reference = (envelope.get("result") or {}).get("id")
        sketch = reference.get("id") if isinstance(reference, dict) else reference
        revision = envelope.get("revision_after") or revision
        arguments = {"sketch_ref": sketch, "expected_revision": revision, "mode": "append",
                     "entities": entities}
        arguments["operation_id"] = str(uuid.uuid4())
        seconds, envelope, _raw = timed_call(self.client, "kompas_edit_sketch", arguments, 900)
        self._call("kompas_close_document", {"document_id": document, "dirty_policy": "discard"})
        return {"seconds": round(seconds, 3), "status": (envelope or {}).get("status"),
                "error": error_code(envelope), "revision_after": (envelope or {}).get("revision_after"),
                "verification_level": ((envelope or {}).get("verification") or {}).get("level")}

    def close(self):
        if self.client is None:
            return
        try:
            if self.opened:
                self._call("kompas_disconnect", {}, 120)
        except Exception:
            pass
        try:
            self.client.close()
        except Exception:
            pass


def summarise(runs, key):
    values = sorted(r["seconds"] for r in runs if r.get(key))
    if not values:
        return None
    return {"n": len(values), "min": values[0], "median": round(statistics.median(values), 3),
            "max": values[-1], "all": values}


def verdict(medians, factor=1.3):
    """The rule fixed BEFORE the run: the newer build is a regression only at `factor` times or more."""
    newer, older = medians
    if newer is None or older is None:
        return "не измерено"
    return "регрессия продукта" if newer >= older * factor else "машина"


def load_specs(segment_count, polyline_vertices):
    return [("segments_%d" % segment_count, segments(segment_count)),
            ("polyline_%d" % polyline_vertices, [{"kind": "polyline", "points_mm": polygon(polyline_vertices),
                                                  "closed": True}])]


def parse_builds(argv):
    builds = []
    for index, token in enumerate(argv):
        if token == "--build" and index + 1 < len(argv):
            name, _, path = argv[index + 1].partition("=")
            builds.append({"name": name, "host": os.path.abspath(path)})
    return builds


def main():
    if "--self-test" in sys.argv[1:]:
        return self_test()

    builds = parse_builds(sys.argv)
    if len(builds) < 2:
        print("нужно не меньше двух сборок: --build ИМЯ=<путь к KompasMcp.Host.exe>")
        return 2
    rounds = int(argument("--rounds", "10"))
    mode = argument("--mode", "per-build")
    visibility = "--visible" in sys.argv
    segment_count = int(argument("--segment-count", "5000"))
    polyline_vertices = int(argument("--polyline-vertices", "2000"))
    out = os.path.abspath(argument("--out", os.path.join(ROOT, "scratch", "sb02", "limits.json")))
    os.makedirs(os.path.dirname(out), exist_ok=True)

    base = os.path.join(ROOT, "scratch", "sb02", time.strftime("%Y%m%d-%H%M%S"))
    os.makedirs(base, exist_ok=True)
    source_config = local_config(ROOT)
    with io.open(source_config, encoding="utf-8-sig") as handle:
        configured_budget = json.load(handle).get("sync_budget_ms")
    for build in builds:
        build["config"] = isolate_config(source_config, base, prefix="sb02-" + build["name"].lower())
        build["host_sha256"] = sha256(build["host"])
        build["adapter_sha256"] = sha256(os.path.join(os.path.dirname(build["host"]),
                                                      "KompasMcp.Api5Adapter.dll"))

    specs = load_specs(segment_count, polyline_vertices)
    report = {
        "probe": "SB02_LIMIT_MEASURE - worst wall time of one bulk sketch call, two builds",
        "clock_start": time.strftime("%Y-%m-%d %H:%M:%S %z"),
        "mode": mode, "rounds": rounds, "visibility": visibility,
        "segment_count": segment_count, "polyline_vertices": polyline_vertices,
        "sync_budget_ms_configured": configured_budget, "sync_budget_ms_used": SYNC_BUDGET_MS,
        "source_config": source_config,
        "builds": [{k: v for k, v in build.items() if k != "config"} for build in builds],
        "machine_start": None, "machine_end": None,
        "runs": [], "summary": {}, "verdict": {},
    }
    # INVARIANT: the series starts on a machine with no KOMPAS and no other Host/Worker alive. The
    # same preflight the acceptance run uses is applied, and its record goes into the evidence.
    report["preflight"] = smoke.close_foreign_processes(set(), "before-series")
    report["machine_start"] = machine_load()
    cpu_start = cpu_snapshot()
    print("режим %s, окон %d, видимость окна КОМПАС %s" % (mode, rounds, "видимая" if visibility else "скрытая"))

    try:
        if mode == "per-build":
            sessions = [Session(build, build["config"], visibility) for build in builds]
            for session in sessions:
                session.open()
            for round_index in range(1, rounds + 1):
                for load, entities in specs:
                    for session in sessions:
                        result = session.measure(load, entities, "r%d" % round_index)
                        result.update({"build": session.build["name"], "round": round_index, "load": load})
                        report["runs"].append(result)
                        print("  %-6s окно %2d %-16s %7.3f с %s" % (
                            session.build["name"], round_index, load, result["seconds"], result["status"]))
            for session in sessions:
                for build in report["builds"]:
                    if build["name"] == session.build["name"]:
                        build["server_version"] = session.server_version
                session.close()
        else:
            for round_index in range(1, rounds + 1):
                for load, entities in specs:
                    for build in builds:
                        session = Session(build, build["config"], visibility)
                        try:
                            session.open()
                            build["server_version"] = session.server_version
                            result = session.measure(load, entities, "r%d" % round_index)
                        finally:
                            session.close()
                        result.update({"build": build["name"], "round": round_index, "load": load})
                        report["runs"].append(result)
                        print("  %-6s окно %2d %-16s %7.3f с %s" % (
                            build["name"], round_index, load, result["seconds"], result["status"]))
    finally:
        # The raw file is written even when the series broke: a partial series is evidence of what
        # was measured, and a lost file would force the whole series to be run again.
        report["machine_end"] = machine_load(cpu_start)
        report["clock_end"] = time.strftime("%Y-%m-%d %H:%M:%S %z")
        for load, _entities in specs:
            report["summary"][load] = {}
            for build in builds:
                subset = [r for r in report["runs"] if r["load"] == load and r["build"] == build["name"]]
                report["summary"][load][build["name"]] = summarise(subset, "seconds")
            medians = [report["summary"][load][b["name"]]["median"]
                       if report["summary"][load][b["name"]] else None for b in builds]
            report["verdict"][load] = verdict(medians)
        with io.open(out, "w", encoding="utf-8") as handle:
            json.dump(report, handle, ensure_ascii=False, indent=1)
        print("\nсводка:")
        for load in report["summary"]:
            for name, stats in report["summary"][load].items():
                print("  %-16s %-6s %s" % (load, name, stats))
            print("  %-16s правило §1.4: %s" % (load, report["verdict"][load]))
        print("сырые замеры: %s" % out)
    return 0


def self_test():
    """No Host and no config: only the parts this instrument computes itself."""
    failures = []
    check = lambda name, ok: failures.append(name) if not ok else None  # noqa: E731
    check("segments count", len(segments(5000)) == 5000)
    check("segments shape", segments(2)[1]["start_mm"] == [1.0, 0.0])
    check("polygon count", len(polygon(2000)) == 2000)
    check("polygon closed by radius", abs(math.hypot(*polygon(64)[7]) - 20.0) < 1e-9)
    check("median", summarise([{"seconds": 3.0}, {"seconds": 1.0}, {"seconds": 2.0}], "seconds")["median"] == 2.0)
    check("summarise ignores empty", summarise([{"seconds": None}], "seconds") is None)
    check("verdict regression at factor", verdict([1.30, 1.0]) == "регрессия продукта")
    check("verdict machine below factor", verdict([1.29, 1.0]) == "машина")
    check("verdict not measured", verdict([None, 1.0]) == "не измерено")
    check("loads", [name for name, _ in load_specs(5000, 2000)] == ["segments_5000", "polyline_2000"])
    check("parse builds", [b["name"] for b in parse_builds(["--build", "A=x.exe", "--build", "B=y.exe"])] == ["A", "B"])
    load = machine_load()
    check("cpu measured", load["cpu_percent"] is not None)
    check("memory measured", load["memory_load_percent"] > 0)
    print("самопроверка: %d проверок, отказов %d" % (11, len(failures)))
    for name in failures:
        print("  ОТКАЗ: %s" % name)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
