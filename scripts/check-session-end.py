# -*- coding: utf-8 -*-
"""Read the outcome of the 'session end' row AFTER the client dialog is closed.

Why a separate instrument. Closing the dialog is what ends the session, so the session that
prepared the state cannot observe its own end: the process that would write the report is the
process being closed. The state is therefore prepared and written down first
(`session-end-baseline.json`), and the outcome is read afterwards by this permanent instrument.

What is measured, from the OS and nothing else: whether the Host of the session and the KOMPAS
instance it launched are still alive. No MCP call is made - after the dialog is closed there is
nothing left to call.

INVARIANT: the baseline is an argument, not a default. A baseline belongs to ONE session and lives
with that session's evidence, so a default pointing into a delivery directory would name one
delivery forever. `--out` defaults next to the baseline it was read from.
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


def run(baseline_path, out_path):
    """Write the report for one baseline and return it."""
    with open(baseline_path, encoding="utf-8") as fh:
        base = json.load(fh)

    host_alive = alive(int(base["host_pid"]))
    kompas_alive = alive(int(base["kompas_pid"]))

    report = {
        "checked_at_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "baseline": os.path.abspath(baseline_path),
        "host_pid": base["host_pid"],
        "host_alive": host_alive,
        "kompas_pid": base["kompas_pid"],
        "kompas_alive": kompas_alive,
        "unsaved_document_id": (base.get("unsaved_document") or {}).get("document_id"),
        "verdict": "PASS" if (not host_alive and not kompas_alive) else "FAIL",
        "verdict_rule": "строка пройдена, если после закрытия диалога не осталось ни Host сеанса, ни KOMPAS сеанса",
        "note": ("несохранённая правка в базовой линии не имела файла на диске: сравнивать файл клиента "
                 "не с чем, потеря правки - ожидаемый исход"),
    }

    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)

    print("Host  %s: %s" % (base["host_pid"], "жив" if host_alive else "завершился"))
    print("KOMPAS %s: %s" % (base["kompas_pid"], "жив" if kompas_alive else "завершился"))
    print("Итог: %s" % report["verdict"])
    print("Отчёт: %s" % os.path.abspath(out_path))
    return report


def self_test():
    """Both halves of the two rules that broke: the print, and the measurement.

    Arm one prints Cyrillic into a stream whose encoding is cp1252 - the console that raised. Arm two
    measures a live pid (this process) and a dead one, and reads a baseline of two dead pids, so the
    verdict PASS is produced by the rule and not by a missing file.
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

    with tempfile.TemporaryDirectory(prefix="_session-end-selftest-") as scratch:
        baseline = os.path.join(scratch, "session-end-baseline.json")
        out = os.path.join(scratch, "session-end-check.json")
        with open(baseline, "w", encoding="utf-8") as fh:
            json.dump({"host_pid": 0xFFFFFFF0, "kompas_pid": 0xFFFFFFF0,
                       "unsaved_document": {"document_id": "selftest"}}, fh)
        report = run(baseline, out)
        check("две завершившиеся программы дают PASS", report["verdict"] == "PASS",
              report["verdict"])
        check("отчёт записан по указанному пути", os.path.isfile(out), out)

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
