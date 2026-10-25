# -*- coding: utf-8 -*-
"""Read the outcome of the 'session end' row AFTER the client dialog is closed.

Why a separate instrument. Closing the dialog is what ends the session, so the session that
prepared the state cannot observe its own end: the process that would write the report is the
process being closed. The state is therefore prepared and written down first
(`session-end-baseline.json`), and the outcome is read afterwards by this permanent instrument.

What is measured, from the OS and nothing else: whether the Host of the session and the KOMPAS
instance it launched are still alive, and which program holds each pid. No MCP call is made - after
the dialog is closed there is nothing left to call.

INVARIANT: the baseline is an argument, not a default. A baseline belongs to ONE session and lives
with that session's evidence, so a default pointing into a delivery directory would name one
delivery forever. `--out` defaults next to the baseline it was read from.

INVARIANT: ONE baseline schema, the one the release order's step writes. Required: `host_pid`,
`kompas_pid`. Reported when present: `worker_pid_at_recording`, `earlier_kompas_pid`,
`document_id`, `document_state`, `host_path`, `kompas_executable`, `application_id`. The row is
decided by `host_pid` and `kompas_pid` alone; `earlier_kompas_pid` belongs to a previous instance
and `worker` is reported, not judged.

MEASURED: one baseline names the unsaved document as prose `document_state` plus a separate
`document_id`, while an earlier form nested it as `unsaved_document.document_id`. A reader that
insists on the nested form wrote `null` for a document the baseline described in full, and `null`
reads as "there was no unsaved document".
LIMIT: an absent optional field is named in `missing_fields`, never filled in.
History: docs/decisions/tests.md#session-end-instrument

Usage:
  PY -I scripts/check-session-end.py --baseline <session-end-baseline.json> [--out <path>]
  PY -I scripts/check-session-end.py --self-test
"""
import argparse
import ctypes
import ctypes.wintypes as w
import io
import json
import os
import sys
from datetime import datetime, timezone

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
STILL_ACTIVE = 259

# Required by the row; every other key is reported when present and named when absent.
REQUIRED = ("host_pid", "kompas_pid")
# (label in the report, key in the baseline, whether the pid decides the row)
PROCESSES = (("host", "host_pid", True),
             ("kompas", "kompas_pid", True),
             ("worker", "worker_pid_at_recording", False),
             ("earlier_kompas", "earlier_kompas_pid", False))
DOCUMENT_KEYS = ("document_id", "document_state")


def utf8_stream(stream):
    """Make a stream speak UTF-8 before the first print.

    MEASURED: on a cp1252 console the Cyrillic lines of this instrument raised `UnicodeEncodeError`,
    so a finished measurement read as a crashed instrument. `errors="replace"` keeps a stream that
    cannot be reconfigured from taking the report down with it.
    """
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    return stream


for _stream in (sys.stdout, sys.stderr):
    utf8_stream(_stream)


def alive(pid):
    """True/False by opening the process, not by trusting a name in a list."""
    handle = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return False
    try:
        code = w.DWORD()
        if not k32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return False
        return code.value == STILL_ACTIVE
    finally:
        k32.CloseHandle(handle)


def process_image(pid):
    """Image path of a live pid, or None when the pid is not alive.

    The path says WHICH program holds the pid: a reused pid is then visible as another program, and
    the row is not read as "the session's KOMPAS is still running".
    """
    handle = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
    if not handle:
        return None
    try:
        size = w.DWORD(32768)
        buffer = ctypes.create_unicode_buffer(size.value)
        if not k32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)):
            return "unknown"
        return buffer.value
    finally:
        k32.CloseHandle(handle)


def read_baseline(path):
    """The baseline as one schema, or a refusal naming what the file does not carry."""
    with open(path, encoding="utf-8") as fh:
        base = json.load(fh)
    absent = [key for key in REQUIRED if base.get(key) is None]
    if absent:
        raise SystemExit(f"базовая линия {path} не по схеме: нет {', '.join(absent)} "
                         f"(нужны {', '.join(REQUIRED)})")
    return base


def run(baseline_path, out_path):
    """Write the report for one baseline and return it."""
    base = read_baseline(baseline_path)

    rows, missing = [], []
    for label, key, decides in PROCESSES:
        if base.get(key) is None:
            missing.append(key)
            continue
        pid = int(base[key])
        # The verdict keeps the exit-code check: a terminated process can still hold a handle, and
        # its image path alone would then read as ALIVE.
        image = process_image(pid) if alive(pid) else None
        rows.append({"process": label, "pid": pid, "alive": image is not None,
                     "image_path": image, "verdict": "ALIVE" if image else "GONE",
                     "decides_row": decides})
    for key in DOCUMENT_KEYS:
        if not base.get(key):
            missing.append(key)

    session_rows = [row for row in rows if row["decides_row"]]
    verdict = "PASS" if session_rows and all(row["verdict"] == "GONE" for row in session_rows) \
        else "FAIL"
    report = {
        "checked_at_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "baseline": os.path.abspath(baseline_path),
        "rows": rows,
        "unsaved_document": {key: base.get(key) for key in DOCUMENT_KEYS},
        "missing_fields": missing,
        "verdict": verdict,
        "verdict_rule": "строка пройдена, если после закрытия диалога не осталось ни Host сеанса, "
                        "ни KOMPAS сеанса; worker называется, но строку не решает",
        "note": ("несохранённая правка в базовой линии не имела файла на диске: сравнивать файл "
                 "клиента не с чем, потеря правки - ожидаемый исход"),
    }

    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)

    for row in rows:
        print("%-14s %-6s %-6s %s" % (row["process"], row["pid"], row["verdict"],
                                      row["image_path"] or ""))
    if missing:
        print("Нет в базовой линии (названо, а не заполнено): %s" % ", ".join(missing))
    print("Итог: %s" % report["verdict"])
    print("Отчёт: %s" % os.path.abspath(out_path))
    return report


def self_test():
    """Both halves of every rule that broke: the print, the measurement, and the two schemas.

    Arm one prints Cyrillic into a stream whose encoding is cp1252 - the console that raised. The
    measurement arms read a live pid (this process) and a dead one. The schema arms feed a baseline
    of the current form and one of the prose form (`document_state`), because a reader that knows
    one form reported the other as "no unsaved document".
    """
    import tempfile

    failures = []

    def check(name, condition, detail=""):
        print("  [%s] %s" % ("PASS" if condition else "FAIL", name)
              + (" - %s" % detail if detail else ""))
        if not condition:
            failures.append(name)

    raw = io.BytesIO()
    narrow = io.TextIOWrapper(raw, encoding="cp1252", newline="\n")
    try:
        utf8_stream(narrow).write("жив завершился Итог\n")
        narrow.flush()
        printed = raw.getvalue()
    except Exception as exc:  # noqa: BLE001 — отказ печати и есть предмет проверки
        printed, failures = b"", failures + ["%s: %s" % (type(exc).__name__, exc)]
    check("кириллица печатается в поток с кодировкой cp1252",
          printed == "жив завершился Итог\n".encode("utf-8"), repr(printed[:40]))

    check("живой процесс измерен живым", alive(os.getpid()) is True)
    check("несуществующий процесс измерен завершившимся", alive(0xFFFFFFF0) is False)

    dead = 0xFFFFFFF0
    with tempfile.TemporaryDirectory(prefix="_session-end-selftest-") as scratch:
        out = os.path.join(scratch, "session-end-check.json")

        def write(name, payload):
            path = os.path.join(scratch, name)
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(payload, fh, ensure_ascii=False)
            return path

        current = write("current.json", {"host_pid": dead, "kompas_pid": dead,
                                         "worker_pid_at_recording": dead,
                                         "document_id": "selftest",
                                         "document_state": "НЕСОХРАНЁННАЯ деталь"})
        report = run(current, out)
        check("базовая линия текущей схемы: две завершившиеся программы дают PASS",
              report["verdict"] == "PASS", report["verdict"])
        check("вердикт назван по каждому PID", len(report["rows"]) == 3
              and all(row["verdict"] == "GONE" for row in report["rows"]),
              [row["process"] for row in report["rows"]])
        check("отчёт записан по указанному пути", os.path.isfile(out), out)

        legacy = write("prose-form.json", {"host_pid": dead, "kompas_pid": dead,
                                        "document_id": "1e0dbfa6",
                                        "document_state": "НЕСОХРАНЁННАЯ деталь «ACC»: dirty=true"})
        legacy_report = run(legacy, out)
        check("базовая линия с прозой `document_state` читается, а не даёт null",
              legacy_report["unsaved_document"]["document_state"].startswith("НЕСОХРАНЁННАЯ")
              and legacy_report["unsaved_document"]["document_id"] == "1e0dbfa6",
              legacy_report["unsaved_document"])

        nested = write("nested-form.json", {"host_pid": dead, "kompas_pid": dead,
                                        "unsaved_document": {"document_id": "6e7d8e13"}})
        nested_report = run(nested, out)
        check("отсутствующее поле названо, а не заполнено",
              {"document_id", "document_state"} <= set(nested_report["missing_fields"]),
              nested_report["missing_fields"])

        try:
            run(write("broken.json", {"kompas_pid": dead}), out)
            refused = False
        except SystemExit:
            refused = True
        check("базовая линия без host_pid названа отказом", refused)

    print("\nСамопроверка прибора: %d FAIL%s"
          % (len(failures), " - %s" % failures if failures else ""))
    return 1 if failures else 0


def main():
    if "--self-test" in sys.argv:
        return self_test()
    ap = argparse.ArgumentParser()
    ap.add_argument("--baseline", required=True)
    ap.add_argument("--out")
    args = ap.parse_args()
    out = args.out or os.path.join(os.path.dirname(os.path.abspath(args.baseline)),
                                   "session-end-check.json")
    report = run(args.baseline, out)
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
