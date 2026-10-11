"""Acceptance probe: how long one MCP tool call took, median per tool, from a smoke run's journal.

WHY. The cost of a change to the adapter is not only "does it work": the customer asked how long the
operations over the body now take, and a number "it got slower" without a measurement is not an answer.
The smoke instrument already writes `started_utc` and `finished_utc` for every call into
`probe-logs/smoke-operations-*.jsonl`; this probe reads that journal and reports the median per tool.

WHY THE MEDIAN AND NOT THE MEAN. A single slow document (an open, a tessellation) moves the mean by
seconds; the median answers "what does a client usually wait for".

Usage:
  python scripts/measure-operation-time.py <run-directory> [<run-directory> ...] [--tools a,b,c]

Exit code 0 when every requested directory yielded a journal, 1 otherwise.
"""

import datetime
import json
import os
import statistics
import sys


def journal_path(run_directory):
    logs = os.path.join(run_directory, "probe-logs")
    if not os.path.isdir(logs):
        return None
    names = sorted(n for n in os.listdir(logs)
                   if n.startswith("smoke-operations-") and n.endswith(".jsonl"))
    return os.path.join(logs, names[-1]) if names else None


def durations(path):
    """(tool, seconds) for every finished call: the LAST record of each operation_id carries the finish."""
    started = {}
    rows = []
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                continue
            operation = record.get("operation_id")
            if not operation:
                continue
            if record.get("outcome") == "in_flight" or record.get("finished_utc") is None:
                started[operation] = record
                continue
            first = started.get(operation) or record
            try:
                begin = _seconds(first.get("started_utc"))
                end = _seconds(record.get("finished_utc"))
            except (TypeError, ValueError):
                continue
            if end is not None and begin is not None and end >= begin:
                rows.append((record.get("tool") or "?", end - begin))
    return rows


def _seconds(stamp):
    if not stamp:
        return None
    # A trailing 'Z' and a '+00:00' offset both mean UTC; fromisoformat understands the offset form.
    return datetime.datetime.fromisoformat(stamp.replace("Z", "+00:00")).timestamp()


def main(argv):
    tools = None
    if "--tools" in argv:
        index = argv.index("--tools")
        tools = {t.strip() for t in argv[index + 1].split(",") if t.strip()}
        argv = argv[:index] + argv[index + 2:]
    if not argv:
        print("укажите каталог прогона: python scripts/measure-operation-time.py <run-dir> [...]")
        return 1

    failed = False
    for run_directory in argv:
        path = journal_path(run_directory)
        if path is None:
            print(f"{run_directory}: журнала операций нет")
            failed = True
            continue
        rows = durations(path)
        per_tool = {}
        for tool, seconds in rows:
            if tools is not None and tool not in tools:
                continue
            per_tool.setdefault(tool, []).append(seconds)
        print(f"{os.path.basename(run_directory)}: вызовов {len(rows)}"
              + (f", из них выбрано {sum(len(v) for v in per_tool.values())}" if tools else ""))
        for tool in sorted(per_tool):
            values = per_tool[tool]
            print(f"  {tool:<32} n={len(values):<4} медиана={statistics.median(values):7.3f} с"
                  f"  мин={min(values):7.3f}  макс={max(values):7.3f}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
