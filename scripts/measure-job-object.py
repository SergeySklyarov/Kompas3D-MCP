"""Probe the documented route for "an instance we launched does not outlive us": a job object.

WHY. The shutdown report measured which way of taking the server down leaves a live `KOMPAS.exe`:
only `taskkill /T /F` on the Host does, because the instance is a COM server child of `svchost.exe`
and not a descendant of the Host. The order `CLIENT_SHUTDOWN_JOB_FIX` asks for the documented fix and
makes THIS probe its stop condition: if `AssignProcessToJobObject` refuses an instance that was
launched through COM, the route is closed and `src/` must not be touched.

WHAT IS MEASURED. Per run: the PID `kompas_connect` reported, the image of its parent, whether
`AssignProcessToJobObject` succeeded and with which Win32 error it did not, and - after the LAST job
handle is closed - whether the instance left and how long it took. The instance is launched by the
REAL Worker through the Host, so the launch route is the product's.

WHO CALLS. The caller is this process (same user, same integrity level, not elevated). Documented
requirements of `AssignProcessToJobObject` are about the TARGET process handle rights
(PROCESS_SET_QUOTA and PROCESS_TERMINATE); the caller's own job membership is recorded, not assumed.
What the probe does NOT prove is named in the report: the call made by `KompasMcp.Worker.exe` itself
is exercised by the acceptance row `SD` on the built product.

Route: Microsoft Learn, "Job Objects"
(https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), "Creating Jobs";
`CreateJobObject`, `SetInformationJobObject` (`JOBOBJECT_EXTENDED_LIMIT_INFORMATION`),
`AssignProcessToJobObject`.

Usage:
  python scripts/measure-job-object.py --host <host.exe> --out <evidence-dir>
      [--runs 5] [--observe 30] [--self-test]

Nothing outside `scratch/` and the `--out` directory is written.
History: docs/decisions/host.md#client-shutdown
"""

from __future__ import annotations

import argparse
import ctypes
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from ctypes import wintypes

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

JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
JobObjectExtendedLimitInformation = 9
PROCESS_TERMINATE = 0x0001
PROCESS_SET_QUOTA = 0x0100
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
ERROR_ACCESS_DENIED = 5

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_int64),
        ("PerJobUserTimeLimit", ctypes.c_int64),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_uint64),
        ("WriteOperationCount", ctypes.c_uint64),
        ("OtherOperationCount", ctypes.c_uint64),
        ("ReadTransferCount", ctypes.c_uint64),
        ("WriteTransferCount", ctypes.c_uint64),
        ("OtherTransferCount", ctypes.c_uint64),
    ]


class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
        ("IoInfo", IO_COUNTERS),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


kernel32.CreateJobObjectW.restype = wintypes.HANDLE
kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
kernel32.SetInformationJobObject.restype = wintypes.BOOL
kernel32.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                             wintypes.DWORD]
kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.CloseHandle.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.GetCurrentProcess.restype = wintypes.HANDLE
kernel32.IsProcessInJob.restype = wintypes.BOOL
kernel32.IsProcessInJob.argtypes = [wintypes.HANDLE, wintypes.HANDLE,
                                    ctypes.POINTER(wintypes.BOOL)]


def last_error():
    return ctypes.get_last_error()


def create_kill_on_close_job():
    """A job whose last handle, once closed, terminates every process assigned to it.

    DOC: https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects - "if the job has the
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE flag specified, closing the last job object handle terminates
    all associated processes and then destroys the job object itself".
    """
    job = kernel32.CreateJobObjectW(None, None)
    if not job:
        return None, last_error()
    info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    ok = kernel32.SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                                          ctypes.byref(info), ctypes.sizeof(info))
    if not ok:
        code = last_error()
        kernel32.CloseHandle(job)
        return None, code
    return job, 0


def assign(job, pid):
    """Assign an already running process to the job; the error code is the measurement, not a crash."""
    handle = kernel32.OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE
                                  | PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return False, last_error(), "OpenProcess"
    try:
        if kernel32.AssignProcessToJobObject(job, handle):
            return True, 0, None
        return False, last_error(), "AssignProcessToJobObject"
    finally:
        kernel32.CloseHandle(handle)


def in_a_job():
    """Whether THIS process is inside a job: the caller's side of the documented restriction."""
    answer = wintypes.BOOL()
    if kernel32.IsProcessInJob(kernel32.GetCurrentProcess(), None, ctypes.byref(answer)):
        return bool(answer)
    return None


def error_name(code):
    return {ERROR_ACCESS_DENIED: "ERROR_ACCESS_DENIED"}.get(code, "")


def verdict(record):
    """One word per run: what the probe established. Kept pure so `--self-test` can hold it."""
    if record.get("assign_error_code"):
        return "assign-failed"
    if not record.get("assigned"):
        return "assign-failed"
    if record.get("kompas_terminated"):
        return "assigned-terminated"
    return "assigned-survived"


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


def force_stop(pid):
    if pid:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True, text=True)


def pids_of_image(rows, image):
    if rows is None:
        return None
    return {pid for pid, row in rows.items() if (row.get("image") or "").lower() == image.lower()}


def parent_image(rows, pid):
    parent_pid = (rows or {}).get(pid or 0, {}).get("parent_pid")
    if not parent_pid:
        return None
    return ((rows.get(parent_pid) or {}).get("image") or None)


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


def one_run(host_exe, base_config, workdir, evidence, index, observe_s, baseline):
    """Launch an instance through the real Worker, put it in the job, drop the last handle, look."""
    tag = "job-%02d" % index
    run_dir = os.path.join(workdir, tag)
    os.makedirs(run_dir, exist_ok=True)
    isolated = isolate_config(base_config, run_dir, "job-" + str(index))
    with open(isolated, encoding="utf-8-sig") as fh:
        cfg = json.load(fh)
    record = {"tag": tag, "isolated_config": isolated,
              "host_log": cfg.get("log_path"), "worker_log": cfg.get("worker_log_path"),
              "kompas_baseline": sorted(baseline), "problems": [],
              "caller_in_a_job": in_a_job(), "pid_of_caller": os.getpid()}

    client = None
    try:
        client = smoke.Client(host_exe, isolated)
        record["host_pid"] = client.p.pid
        client.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                   "clientInfo": {"name": "measure-job-object", "version": "0"}},
                    timeout=60)
        client.notify("notifications/initialized")
        env, code = call_tool(client, "kompas_connect", {"mode": "launch", "make_visible": False})
        pid = result(env).get("process_id")
        record["kompas_pid"] = pid
        if not pid:
            record["problems"].append("launch: %s" % code)
            return record

        rows = smoke.process_snapshot()
        record["kompas_parent_image"] = parent_image(rows, pid)
        record["worker_pid"] = next(iter({p for p, r in (rows or {}).items()
                                          if (r.get("image") or "").lower() == WORKER.lower()
                                          and r.get("parent_pid") == client.p.pid}), None)

        job, code = create_kill_on_close_job()
        if not job:
            record["problems"].append("CreateJobObject/SetInformationJobObject: %s" % code)
            record["job_error_code"] = code
            return record

        assigned, code, stage = assign(job, pid)
        record["assigned"] = assigned
        record["assign_error_code"] = code
        record["assign_error_stage"] = stage
        record["assign_error_name"] = error_name(code)
        if not assigned:
            kernel32.CloseHandle(job)
            return record

        # The last handle goes away here; DOC says that terminates every process in the job.
        started = time.monotonic()
        kernel32.CloseHandle(job)
        record["job_handle_closed"] = True
        record["kompas_terminated"] = wait_pid_gone(pid, observe_s)
        record["seconds_to_exit"] = round(time.monotonic() - started, 2)
        record["kompas_alive_after"] = not record["kompas_terminated"]
    except Exception as ex:  # noqa: BLE001 - a failed build-up is recorded, not hidden
        record["problems"].append("run: %s" % ex)
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
        record["verdict"] = verdict(record)
        copy_evidence(record, evidence, tag)
    return record


def copy_evidence(record, evidence, tag):
    """Keep the journals of this run next to the summary: they are the measurement's raw form."""
    target = os.path.join(evidence, "runs", tag)
    os.makedirs(target, exist_ok=True)
    for key in ("host_log", "worker_log"):
        source = record.get(key)
        if source and os.path.isfile(source):
            shutil.copyfile(source, os.path.join(target, os.path.basename(source)))
    record["evidence_dir"] = os.path.relpath(target, ROOT).replace("\\", "/")


def summarise(records):
    lines = ["| Опыт | `AssignProcessToJobObject` | Код Win32 | КОМПАС после закрытия ручки | Секунд |",
             "|---|---|---|---|---|"]
    for record in records:
        code = record.get("assign_error_code") or 0
        lines.append("| %s | %s | %s | %s | %s |" % (
            record.get("tag"),
            "удалось" if record.get("assigned") else "ОТКАЗ",
            ("%s %s" % (code, record.get("assign_error_name") or "")).strip() or "-",
            {True: "завершён", False: "ЖИВ"}.get(record.get("kompas_terminated"), "не измерено"),
            record.get("seconds_to_exit", "-")))
    return "\n".join(lines)


def write_reports(records, evidence, host_exe):
    os.makedirs(evidence, exist_ok=True)
    summary = {"host": host_exe, "runs": records,
               "counts": {word: sum(1 for r in records if r.get("verdict") == word)
                          for word in ("assign-failed", "assigned-terminated",
                                       "assigned-survived")}}
    with open(os.path.join(evidence, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=2)
    with open(os.path.join(evidence, "summary.md"), "w", encoding="utf-8") as fh:
        fh.write("# Проба: объект задания для собственного экземпляра\n\n")
        fh.write(summarise(records) + "\n")
    return summary


def self_test():
    """Checks that need no KOMPAS: the structures, the flag and the word each run gets."""
    failures = []

    def check(condition, name):
        if not condition:
            failures.append(name)

    check(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE == 0x2000, "флаг JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE")
    check(JobObjectExtendedLimitInformation == 9, "класс JOBOBJECT_EXTENDED_LIMIT_INFORMATION")
    check(ctypes.sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION) == 144,
          "размер JOBOBJECT_EXTENDED_LIMIT_INFORMATION (%d)"
          % ctypes.sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))
    check(ctypes.sizeof(JOBOBJECT_BASIC_LIMIT_INFORMATION) == 64, "размер basic limit information")
    check(ctypes.sizeof(IO_COUNTERS) == 48, "размер IO_COUNTERS")
    check(verdict({"assigned": True, "kompas_terminated": True}) == "assigned-terminated",
          "удалось и завершён")
    check(verdict({"assigned": True, "kompas_terminated": False}) == "assigned-survived",
          "удалось, но жив")
    check(verdict({"assigned": False, "assign_error_code": 5}) == "assign-failed", "отказ")
    check(verdict({"assigned": False}) == "assign-failed", "отказ без кода")
    check(error_name(5) == "ERROR_ACCESS_DENIED", "имя кода отказа")
    check(error_name(87) == "", "имя неизвестного кода")
    job, code = create_kill_on_close_job()
    check(bool(job), "CreateJobObject с KILL_ON_JOB_CLOSE (код %s)" % code)
    if job:
        kernel32.CloseHandle(job)

    for failure in failures:
        print("FAIL: " + failure)
    print("self-test: %d проверок, %d FAIL" % (11, len(failures)))
    return 0 if not failures else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", help="KompasMcp.Host.exe (Release build of the measured commit)")
    parser.add_argument("--out", help="evidence directory")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--observe", type=int, default=30)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if not args.host or not args.out:
        parser.error("--host и --out обязательны без --self-test")

    base_config = local_config(ROOT)
    workdir = os.path.join(ROOT, "scratch", "job-object-%s" % time.strftime("%Y%m%d-%H%M%S"))
    os.makedirs(workdir, exist_ok=True)
    baseline = pids_of_image(smoke.process_snapshot(), KOMPAS) or set()

    records = []
    for index in range(1, args.runs + 1):
        record = one_run(args.host, base_config, workdir, args.out, index, args.observe, baseline)
        records.append(record)
        print("%s: %s" % (record.get("tag"), record.get("verdict")))
        baseline = baseline | set(record.get("kompas_left_after_run") or [])
    summary = write_reports(records, args.out, args.host)
    print(summarise(records))
    print(json.dumps(summary["counts"], ensure_ascii=False))
    return 0 if summary["counts"]["assign-failed"] == 0 else 2


if __name__ == "__main__":
    sys.exit(main())
