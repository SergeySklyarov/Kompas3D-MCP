#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Measure the shutdown-with-unsaved-document scenarios of the SHUTDOWN_NO_ORPHANS order.

Each scenario is ONE run of `KompasMcp.Api7Probe --shutdown-orphans <name>` in its own process:
a run that wedges inside `Quit()` cannot go on to the next scenario, so the driver, not the probe,
walks the list. The driver is the OUTSIDE observer the order asks for — it reads the probe's own
report, then looks at the process list itself and names every KOMPAS instance that outlived its run.

Only an instance whose PID the probe itself proved (`own_pid` in the report, found by diffing the
process list around the launch) is ever stopped. Name-based killing is forbidden by the project
spec and appears nowhere in this file.

Usage:
  python scripts/shutdown-orphans-measure.py --out docs/acceptance/evidence/<dir> [--only a,b]

History: docs/decisions/host.md#shutdown-no-orphans
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROBE = os.path.join(
    ROOT, "tools", "KompasMcp.Api7Probe", "bin", "x64", "Debug", "net10.0-windows",
    "KompasMcp.Api7Probe.exe",
)
REPORT_DIR = os.path.join(ROOT, "docs", "acceptance", "api7")

# (scenario, extra args, what the scenario answers). Order = cheapest and most decisive first.
SCENARIOS = (
    ("orphan", [], "документ, СОЗДАННЫЙ процессом и затем изменённый: висит ли Quit()"),
    ("orphan-existing", [], "документ, ОТКРЫТЫЙ с диска и изменённый: висит ли Quit()"),
    ("orphan-visible", ["--shutdown-visible"], "то же на видимом экземпляре"),
    ("close-nosave", [], "закрытие документа kdDoNotSaveChanges перед Quit()"),
    ("hidemessage-yes", [], "подавление диалогов ksHideMessageYes перед Quit()"),
    ("hidemessage-no", [], "подавление диалогов ksHideMessageNo перед Quit()"),
    ("rescue", [], "спасательная копия SaveAs в служебный путь"),
    ("full", ["--shutdown-visible"], "полная последовательность §3.1 на документе 472 КБ"),
    ("full-huge", [], "полная последовательность §3.1 на документе 756 КБ / 300 признаков"),
)


def kompas_pids():
    """PIDs of KOMPAS.exe, read from the OS. A shell pipeline that answers nothing must not be read
    as 'no processes' — the incident that hid three orphans on 12.10.2026."""
    out = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq KOMPAS.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True,
    ).stdout
    pids = set()
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].upper().startswith("KOMPAS"):
            try:
                pids.add(int(parts[1]))
            except ValueError:
                pass
    return pids


def kill_by_pid(pid):
    subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, text=True)


def run_scenario(name, extra, out_dir, timeout_s):
    before = kompas_pids()
    work = os.path.join(out_dir, "work", name)
    cmd = [PROBE, "--shutdown-orphans", name, "--work", work] + extra
    started = time.time()
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout_s)
        timed_out = False
        stdout = proc.stdout
    except subprocess.TimeoutExpired as ex:
        timed_out = True
        stdout = (ex.stdout or b"").decode("utf-8", "replace") if isinstance(ex.stdout, bytes) else (ex.stdout or "")

    # The probe writes one shared report file per run; keep this scenario's copy before the next run.
    for ext in (".json", ".md"):
        src = os.path.join(REPORT_DIR, "shutdown-orphans" + ext)
        if os.path.exists(src):
            shutil.copyfile(src, os.path.join(out_dir, name + ext))

    json_path = os.path.join(out_dir, name + ".json")
    report = None
    if os.path.exists(json_path):
        with open(json_path, encoding="utf-8-sig") as handle:
            report = json.load(handle)

    own_pid = None
    if report:
        for step in report.get("steps", []):
            if step.get("id") == "SO.0":
                own_pid = (step.get("data") or {}).get("own_pid")

    after = kompas_pids()
    # An instance that existed before the run is not ours and is never touched.
    survived = sorted(after - before)
    killed = []
    for pid in survived:
        if own_pid and pid == own_pid:
            kill_by_pid(pid)
            killed.append(pid)

    return {
        "scenario": name,
        "question": dict((s[0], s[2]) for s in SCENARIOS)[name],
        "timed_out": timed_out,
        "seconds": round(time.time() - started, 1),
        "own_pid": own_pid,
        "kompas_survived": survived,
        "killed_by_pid": killed,
        "stdout_tail": stdout.strip().splitlines()[-4:],
        "steps": [
            {
                "id": s["id"],
                "verdict": s["verdict"],
                "conclusion": s.get("conclusion"),
                "observations": s.get("observations"),
                "data": s.get("data"),
            }
            for s in (report or {}).get("steps", [])
            if s.get("id") == "SO.1"
        ],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="evidence directory for the reports")
    parser.add_argument("--only", default="", help="comma-separated scenario names")
    parser.add_argument("--timeout", type=int, default=240, help="per-scenario timeout, seconds")
    args = parser.parse_args()

    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)
    only = {s for s in args.only.split(",") if s}

    results = []
    for name, extra, _ in SCENARIOS:
        if only and name not in only:
            continue
        print(f"[measure] {name} ...", flush=True)
        result = run_scenario(name, extra, out_dir, args.timeout)
        results.append(result)
        step = (result["steps"] or [{}])[0]
        print(
            f"[measure] {name}: {step.get('verdict')} · survived={result['kompas_survived']} · "
            f"killed={result['killed_by_pid']} · {result['seconds']} s",
            flush=True,
        )

    summary = os.path.join(out_dir, "summary.json")
    with open(summary, "w", encoding="utf-8") as handle:
        json.dump(results, handle, ensure_ascii=False, indent=2)

    print("\n=== summary ===")
    for result in results:
        step = (result["steps"] or [{}])[0]
        data = step.get("data") or {}
        print(
            f"{result['scenario']:<18} {step.get('verdict'):<6} "
            f"exited={data.get('process_exited_within_25s')} "
            f"seq_ms={data.get('sequence_ms_before_quit')} "
            f"rescue_ms={data.get('rescue_copy_ms')} "
            f"close_ms={data.get('close_nosave_ms')} "
            f"survived={result['kompas_survived']}"
        )
    print("summary:", summary)

    leftovers = kompas_pids()
    if leftovers:
        print("ВНИМАНИЕ: KOMPAS.exe всё ещё в списке процессов:", sorted(leftovers))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
