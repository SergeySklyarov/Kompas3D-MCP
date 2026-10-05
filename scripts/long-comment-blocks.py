"""Acceptance probe: no LONG comment block is left in the source.

The counting rule, fixed 05.10.2026 so that the customer's count and the developer's count agree:

  A BLOCK is a maximal run of consecutive physical lines whose stripped text starts with `//` or
  `///`. A block is LONG when it has MORE THAN 8 such lines AND carries no verbatim КОМПАС help
  quotation, i.e. no run of text in guillemets «...». A block of 9+ lines that contains a «...»
  quotation is exempt, because the quotation is documentation quoted in the original language.

At HEAD 68389b7 this probe reports 219 LONG blocks (the count in
COMMENTS_CLEANUP_LONG_BLOCKS_20261005.md); the target is 0.

Usage:
  python scripts/long-comment-blocks.py <root> [<root> ...] [--min N]
  Exit code 0 when no LONG block is found, 1 otherwise.
"""

import os
import re
import sys

# History narration markers named in COMMENTS_CLEANUP_LONG_BLOCKS_20261005.md. The flag is a
# heuristic to help a human triage; it does NOT enter the pass/fail decision.
HISTORY = re.compile(r"\b(former|formerly|previously|earlier|until)\b", re.IGNORECASE)


def blocks_in(path, min_len):
    lines = open(path, "r", encoding="utf-8").read().split("\n")
    res = []
    run, start = [], 0
    for idx, line in enumerate(lines, 1):
        if line.strip().startswith("//"):
            if not run:
                start = idx
            run.append(line)
        else:
            if len(run) > min_len and "«" not in "".join(run):
                res.append((start, len(run), run))
            run = []
    if len(run) > min_len and "«" not in "".join(run):
        res.append((start, len(run), run))
    return res


def scan(root, min_len):
    """Walk <root>; a file argument is scanned directly, not skipped.

    os.walk() silently yields nothing for a plain file path, which once made a per-file call print
    a false 0. Files are therefore handled explicitly.
    """
    rows = []

    def add(path, rel):
        for start, n, run in blocks_in(path, min_len):
            rows.append((rel, start, n, bool(HISTORY.search(" ".join(run)))))

    if os.path.isfile(root):
        if root.endswith(".cs"):
            add(root, os.path.relpath(root).replace(os.sep, "/"))
        return rows

    for dirpath, _dirs, files in os.walk(root):
        if os.sep + "obj" + os.sep in dirpath + os.sep or os.sep + "bin" + os.sep in dirpath + os.sep:
            continue
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            add(path, os.path.relpath(path, root).replace(os.sep, "/"))
    return rows


def main(argv):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    min_len = 8
    if "--min" in argv:
        i = argv.index("--min")
        min_len = int(argv[i + 1])
        argv = argv[:i] + argv[i + 2:]
    roots = argv or ["src", "tests"]
    rows = []
    for root in roots:
        rows.extend(scan(root, min_len))
    rows.sort(key=lambda r: (-r[2], r[0], r[1]))
    for rel, start, n, is_hist in rows:
        tag = " — history" if is_hist else ""
        print(f"{rel}:{start} — {n} lines{tag}")
    print(f"LONG COMMENT BLOCKS (> {min_len} lines, no help quotation): {len(rows)}")
    return 1 if rows else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
