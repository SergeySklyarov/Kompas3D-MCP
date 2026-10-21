"""Compare the rows of two `--all` runs of the same commit and name every divergence.

WHY. The catalog mode is measured twice on one tree (`compact` and `full`): the two runs must have
the same rows, and a differing row has to be named, not counted. Row ids repeat inside one report
(a per-document row runs once per document), so a row is identified by its id plus its ordinal
among the rows of that id.

INVARIANT: a row present in one run and absent in the other is reported as a divergence, never as
an equal row: an equal count over a different row set would read as a clean comparison.
Usage: PY scripts/compare-run-rows.py <run_dir_a> <run_dir_b> [--quiet]
Prints one JSON object; exit code 0 unless a report cannot be read.
"""
import json
import os
import sys
from collections import OrderedDict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from run_reports import FULL_RUN_REPORT  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load(run_dir):
    path = os.path.join(run_dir, FULL_RUN_REPORT)
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    ctx = data.get("context") or {}
    return {
        "run_dir": os.path.relpath(os.path.abspath(run_dir), ROOT).replace("\\", "/"),
        "catalog_mode": ctx.get("catalog_mode"),
        "source_commit": ctx.get("source_commit"),
        "source_tree_dirty": ctx.get("source_tree_dirty"),
        "rows": data.get("rows") or [],
        "groups": data.get("groups") or {},
    }


def verdict_sequence(rows):
    """id -> verdicts in report order. TEST: two rows of one id stay two entries."""
    seq = OrderedDict()
    for row in rows:
        seq.setdefault(row.get("id"), []).append(row.get("verdict"))
    return seq


def counts(rows):
    out = {}
    for row in rows:
        verdict = row.get("verdict") or "?"
        out[verdict] = out.get(verdict, 0) + 1
    return out


def compare(a, b):
    sa, sb = verdict_sequence(a["rows"]), verdict_sequence(b["rows"])
    diverged = []
    for rid in list(sa) + [r for r in sb if r not in sa]:
        va, vb = sa.get(rid), sb.get(rid)
        if va != vb:
            diverged.append({"id": rid, "a": va, "b": vb})
    groups = sorted(set(a["groups"]) | set(b["groups"]))
    group_diff = [{"group": g, "a": a["groups"].get(g), "b": b["groups"].get(g)}
                  for g in groups if a["groups"].get(g) != b["groups"].get(g)]
    return {
        "a": {k: a[k] for k in ("run_dir", "catalog_mode", "source_commit", "source_tree_dirty")},
        "b": {k: b[k] for k in ("run_dir", "catalog_mode", "source_commit", "source_tree_dirty")},
        "rows_a": len(a["rows"]),
        "rows_b": len(b["rows"]),
        "counts_a": counts(a["rows"]),
        "counts_b": counts(b["rows"]),
        "ids_only_in_a": [r for r in sa if r not in sb],
        "ids_only_in_b": [r for r in sb if r not in sa],
        "diverged_rows": diverged,
        "group_diff": group_diff,
        "identical": not diverged and not group_diff and len(a["rows"]) == len(b["rows"]),
    }


def main(argv):
    dirs = [x for x in argv if not x.startswith("--")]
    if len(dirs) != 2:
        print(__doc__)
        return 2
    res = compare(load(dirs[0]), load(dirs[1]))
    print(json.dumps(res, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main(sys.argv[1:]))
