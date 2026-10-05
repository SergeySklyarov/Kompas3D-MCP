"""Acceptance probe: no LONG comment block, no LONG comment line, no comment GARBAGE.

Three rules, all counted by this one instrument (task COMMENTS_LINE_LENGTH_DEVELOPER_PROMPT.md):

  1. BLOCK. A block is a maximal run of consecutive physical lines whose stripped text starts with
     `//` or `///`. A block is LONG when it has MORE THAN 8 such lines AND carries no verbatim
     КОМПАС help quotation, i.e. no run of text in guillemets «...». A 9+ line block that contains a
     «...» quotation is exempt: the quotation is documentation quoted in the original language.

  2. LINE. A comment line is the stripped text of a physical line that starts with `//` or `///`,
     counted INCLUDING the slashes and any indentation-free prefix. It is LONG when it is longer
     than 120 characters. A line that carries a «...» help quotation may exceed 120 by the length of
     the quotation(s) themselves: its own limit is 120 + len(quotation runs).

  3. GARBAGE. A comment line is dirty when it names run-specific or foreign facts that belong in
     docs/decisions, not in code:
       - a dd.mm.yyyy date;
       - a 16+ hex run id, or a 7+ hex token containing a digit (short run ids, reference numbers);
       - a semantic version N.N.N (a third-party client or build version);
       - a third-party MCP client name (WorkBuddy, Claude, Cursor, VS Code, Postman, ...).
     Justified exceptions are listed in the report; the target is 0.

At HEAD 68389b7 the probe reports 219 LONG blocks (the count in COMMENTS_CLEANUP_LONG_BLOCKS_20261005.md);
the target is 0 for every rule.

Usage:
  python scripts/long-comment-blocks.py <root> [<root> ...] [--min N] [--no-garbage]
  Exit code 0 when no violation is found, 1 otherwise.
"""

import os
import re
import sys

# History narration markers named in COMMENTS_CLEANUP_LONG_BLOCKS_20261005.md. The flag is a
# heuristic to help a human triage; it does NOT enter the pass/fail decision.
HISTORY = re.compile(r"\b(former|formerly|previously|earlier|until)\b", re.IGNORECASE)

# Rule 2: a comment line longer than this (plus the length of any «...» quotation on it) is LONG.
LINE_LIMIT = 120
QUOTE = re.compile(r"\u00ab[^\u00bb]*\u00bb")

# Rule 3: run-specific or foreign facts that must live in docs/decisions, not in code.
GARBAGE = (
    ("date", re.compile(r"\d{2}\.\d{2}\.\d{4}")),
    ("runid", re.compile(r"\b[0-9a-f]{16,}\b")),
    ("hex7", re.compile(r"\b(?=[0-9a-f]*[0-9])[0-9a-f]{7,}\b")),
    ("version", re.compile(r"\b\d+\.\d+\.\d+\b")),
    ("client", re.compile(
        r"\b(WorkBuddy|Claude|Cursor|VS ?Code|Postman|MCP ?Inspector|Insomnia|Kiro|Windsurf|"
        r"Cline|Codex|Copilot|JetBrains|Zed)\b", re.IGNORECASE)),
)


def comment_lines(path):
    """Yield (lineno, stripped) for every physical line whose stripped text starts with //."""
    for idx, line in enumerate(open(path, "r", encoding="utf-8").read().split("\n"), 1):
        stripped = line.strip()
        if stripped.startswith("//"):
            yield idx, stripped


def line_limit_of(stripped):
    """Rule 2 limit for one line: 120 plus the total length of the «...» quotations it carries."""
    return LINE_LIMIT + sum(len(m.group(0)) for m in QUOTE.finditer(stripped))


def garbage_of(stripped):
    """Return the list of garbage kinds found on one comment line (empty when clean)."""
    hits = []
    for kind, pattern in GARBAGE:
        if pattern.search(stripped):
            hits.append(kind)
    return hits


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
            add(path, os.path.relpath(path).replace(os.sep, "/"))
    return rows


def cs_files(roots):
    seen = []
    for root in roots:
        if os.path.isfile(root):
            if root.endswith(".cs"):
                seen.append((root, os.path.relpath(root).replace(os.sep, "/")))
            continue
        for dirpath, _dirs, files in os.walk(root):
            if os.sep + "obj" + os.sep in dirpath + os.sep or os.sep + "bin" + os.sep in dirpath + os.sep:
                continue
            for name in sorted(files):
                if name.endswith(".cs"):
                    path = os.path.join(dirpath, name)
                    seen.append((path, os.path.relpath(path).replace(os.sep, "/")))
    return seen


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
    with_garbage = "--no-garbage" not in argv
    argv = [a for a in argv if a != "--no-garbage"]
    roots = argv or ["src", "tests"]

    block_rows = []
    for root in roots:
        block_rows.extend(scan(root, min_len))
    block_rows.sort(key=lambda r: (-r[2], r[0], r[1]))

    line_rows = []
    garbage_rows = []
    for path, rel in cs_files(roots):
        for lineno, stripped in comment_lines(path):
            limit = line_limit_of(stripped)
            if len(stripped) > limit:
                line_rows.append((rel, lineno, len(stripped), limit))
            if with_garbage:
                kinds = garbage_of(stripped)
                if kinds:
                    garbage_rows.append((rel, lineno, kinds))

    for rel, start, n, is_hist in block_rows:
        tag = " — history" if is_hist else ""
        print(f"{rel}:{start} — {n} lines{tag}")
    print(f"LONG COMMENT BLOCKS (> {min_len} lines, no help quotation): {len(block_rows)}")

    line_rows.sort(key=lambda r: (-r[2], r[0], r[1]))
    for rel, lineno, length, limit in line_rows:
        print(f"{rel}:{lineno} — {length} chars (limit {limit})")
    print(f"LONG COMMENT LINES (> {LINE_LIMIT} chars, quote-adjusted): {len(line_rows)}")

    if with_garbage:
        garbage_rows.sort(key=lambda r: (r[0], r[1]))
        for rel, lineno, kinds in garbage_rows:
            print(f"{rel}:{lineno} — {','.join(kinds)}")
        print(f"COMMENT GARBAGE (dates, run ids, hex, versions, clients): {len(garbage_rows)}")

    bad = bool(block_rows or line_rows or (with_garbage and garbage_rows))
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
