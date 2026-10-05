"""Strip C# comments so "comments changed, code unchanged" can be checked byte for byte.

Why a token scanner and not a regex: `//` inside a string literal is not a comment, and this
repository contains file paths and error text with slashes. A regex would cut them and report a
false difference. The scanner below walks the source once, tracks string / verbatim-string /
raw-string / char-literal state, and drops only real comments.

Usage:
  python scripts/strip-cs-comments.py emit <root> <outdir> [--glob .cs]
      Walk <root>, write every matching file to <outdir> with comments removed (same relative path).

  python scripts/strip-cs-comments.py compare <dirA> <dirB>
      Recursively compare two emitted trees; exit non-zero and list differing files.

  python scripts/strip-cs-comments.py count <file> ...
      Print, per file, the number of comment lines and comment bytes (for the before/after table).

  python scripts/strip-cs-comments.py cyrillic <file> ...
      Print every comment containing Cyrillic. English comments must be clean; the only permitted
      Cyrillic is a quotation from the КОМПАС help.
"""

import os
import sys


def _scan(text):
    """Return (code_text, comment_line_count, comment_byte_count, comments).

    code_text is the source with every // and /* */ comment removed. Block-comment newlines are
    preserved so line numbering stays stable. comments is a list of (line_no, fragment).
    """
    out = []
    n = len(text)
    i = 0
    comment_lines = 0
    comment_bytes = 0
    comments = []

    def drop_comment(fragment, keep_newlines, line_no):
        nonlocal comment_lines, comment_bytes
        comment_bytes += len(fragment.encode("utf-8"))
        comments.append((line_no, fragment))
        # A comment line is a physical line that carries comment text.
        stripped = fragment.strip()
        if stripped:
            comment_lines += fragment.count("\n") + 1
        if keep_newlines:
            out.append("\n" * fragment.count("\n"))

    def line_at(pos):
        return text.count("\n", 0, pos) + 1

    while i < n:
        c = text[i]

        # --- line comment -------------------------------------------------
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            if j == -1:
                j = n
            drop_comment(text[i:j], keep_newlines=False, line_no=line_at(i))
            i = j
            continue

        # --- block comment ------------------------------------------------
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            if j == -1:
                j = n
            else:
                j += 2
            drop_comment(text[i:j], keep_newlines=True, line_no=line_at(i))
            i = j
            continue

        # --- verbatim / interpolated-verbatim string  @"..."  $@"..." ------
        if c == "@" and i + 1 < n and text[i + 1] == '"':
            out.append(text[i])
            i += 1
            out.append('"')
            i += 1
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out.append('""')
                        i += 2
                        continue
                    out.append('"')
                    i += 1
                    break
                out.append(text[i])
                i += 1
            continue

        # --- raw string  """..."""  (also $"""...""") ---------------------
        if c == '"' and text.startswith('"""', i):
            quotes = 0
            while i + quotes < n and text[i + quotes] == '"':
                quotes += 1
            opener = '"' * quotes
            out.append(opener)
            i += quotes
            closer = '"' * quotes
            k = text.find(closer, i)
            if k == -1:
                out.append(text[i:])
                i = n
            else:
                out.append(text[i:k + quotes])
                i = k + quotes
            continue

        # --- regular / interpolated string  "..."  $"..."
        if c == '"':
            out.append('"')
            i += 1
            while i < n:
                ch = text[i]
                if ch == "\\":
                    out.append(text[i:i + 2])
                    i += 2
                    continue
                out.append(ch)
                i += 1
                if ch == '"':
                    break
            continue

        # --- char literal  '...'  (do not mistake it for a string) --------
        if c == "'":
            out.append("'")
            i += 1
            while i < n:
                ch = text[i]
                if ch == "\\":
                    out.append(text[i:i + 2])
                    i += 2
                    continue
                out.append(ch)
                i += 1
                if ch == "'":
                    break
            continue

        out.append(c)
        i += 1

    return "".join(out), comment_lines, comment_bytes, comments


def emit(root, outdir, pattern=".cs"):
    written = 0
    for dirpath, _dirs, files in os.walk(root):
        if os.sep + "obj" + os.sep in dirpath + os.sep or os.sep + "bin" + os.sep in dirpath + os.sep:
            continue
        for name in files:
            if not name.endswith(pattern):
                continue
            src = os.path.join(dirpath, name)
            rel = os.path.relpath(src, root)
            dst = os.path.join(outdir, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(src, "r", encoding="utf-8") as fh:
                text = fh.read()
            code, _lines, _bytes, _comments = _scan(text)
            # Canonicalise: a removed comment leaves a blank line behind, and the number of those
            # blank lines is exactly what changes between revisions. Blank lines carry no code, so
            # the comparison keeps only non-blank lines with trailing whitespace stripped. This is
            # what makes "the code text is unchanged" a statement about code, not about layout.
            kept = [ln.rstrip() for ln in code.split("\n") if ln.strip()]
            with open(dst, "w", encoding="utf-8", newline="") as fh:
                fh.write("\n".join(kept) + "\n")
            written += 1
    return written


def compare(dir_a, dir_b):
    def collect(root):
        found = {}
        for dirpath, _dirs, files in os.walk(root):
            for name in files:
                p = os.path.join(dirpath, name)
                found[os.path.relpath(p, root).replace(os.sep, "/")] = p
        return found

    a = collect(dir_a)
    b = collect(dir_b)
    problems = []
    for rel in sorted(set(a) | set(b)):
        if rel not in a:
            problems.append(f"only in {dir_b}: {rel}")
            continue
        if rel not in b:
            problems.append(f"only in {dir_a}: {rel}")
            continue
        with open(a[rel], "rb") as fh:
            da = fh.read()
        with open(b[rel], "rb") as fh:
            db = fh.read()
        if da != db:
            problems.append(f"DIFFERS: {rel}")
    if problems:
        print("COMMENT-STRIPPED COMPARISON: FAIL")
        for p in problems:
            print("  " + p)
        return 1
    print(f"COMMENT-STRIPPED COMPARISON: PASS ({len(a)} files identical)")
    return 0


def count(paths):
    total_lines = 0
    total_bytes = 0
    for path in paths:
        with open(path, "r", encoding="utf-8") as fh:
            text = fh.read()
        _code, lines, cbytes, _comments = _scan(text)
        total_lines += lines
        total_bytes += cbytes
        print(f"{lines:6d} lines  {cbytes:8d} bytes  {path}")
    print(f"{total_lines:6d} lines  {total_bytes:8d} bytes  TOTAL")
    return 0


def cyrillic(paths):
    """Report Cyrillic code points found INSIDE comments (string literals are not inspected).

    The cleanup target is English comments; the only permitted Cyrillic is a quotation from the
    КОМПАС help. Every hit is printed so it can be justified one by one.
    """
    cyr = set()
    for lo, hi in ((0x0400, 0x04FF), (0x0500, 0x052F)):
        cyr.update(chr(c) for c in range(lo, hi + 1))
    total = 0
    for path in paths:
        with open(path, "r", encoding="utf-8") as fh:
            text = fh.read()
        _code, _lines, _bytes, comments = _scan(text)
        for line_no, fragment in comments:
            hits = sorted({ch for ch in fragment if ch in cyr})
            if hits:
                total += 1
                first = fragment.strip().splitlines()[0][:100]
                print(f"{path}:{line_no}: {''.join(hits)} | {first}")
    if total:
        print(f"CYRILLIC IN COMMENTS: {total} comment(s) — each must be a help quotation")
        return 1
    print("CYRILLIC IN COMMENTS: none")
    return 0


def main(argv):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    if len(argv) >= 2 and argv[0] == "emit":
        if len(argv) < 3:
            print(__doc__)
            return 2
        written = emit(argv[1], argv[2])
        print(f"emitted {written} files to {argv[2]}")
        return 0
    if len(argv) >= 3 and argv[0] == "compare":
        return compare(argv[1], argv[2])
    if len(argv) >= 2 and argv[0] == "count":
        return count(argv[1:])
    if len(argv) >= 2 and argv[0] == "cyrillic":
        return cyrillic(argv[1:])
    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
