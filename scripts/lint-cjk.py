"""Find CJK characters that slipped into this repository's text.

Why this exists as a checked-in tool rather than a one-off grep: while writing Russian comments and
documentation I repeatedly typed stray CJK characters (an input-method slip), and they survive
review precisely because they look like punctuation at a glance. This script is a lint: it exits
non-zero when any .cs/.md/.json/.props file contains a CJK code point, and prints file:line.

Usage: python scripts/lint-cjk.py [path ...]
"""

import os
import sys

# The summary line is Russian and the console here is cp1250: without this the linter crashes on
# its own report, which is a worse experience than the bug it looks for.
for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

CJK_RANGES = [
    (0x3040, 0x30FF),  # hiragana, katakana
    (0x3400, 0x4DBF),  # ext A
    (0x4E00, 0x9FFF),  # unified ideographs
    (0xF900, 0xFAFF),  # compat ideographs
    (0xFF00, 0xFF60),  # fullwidth forms
    (0x2E80, 0x2EFF),  # CJK radicals
    (0x3000, 0x303F),  # CJK punctuation (ideographic full stop, comma, corner brackets)
    # Hangul was missing here, and the omission had a cost: `mcp-config.md` carried
    # "сколько 기다ть синхронно" (HANGUL SYLLABLE GI instead of `ж`) through review. Both this
    # linter and lint-mixed-script.py passed it — the first because these ranges stop at CJK, the
    # second because it only pairs Latin with Cyrillic. A Korean syllable inside a Russian word is
    # the same defect as an ideograph inside one, so it is searched for by the same test.
    (0x1100, 0x11FF),  # hangul jamo
    (0x3130, 0x318F),  # hangul compatibility jamo
    (0xA960, 0xA97F),  # hangul jamo extended-A
    (0xAC00, 0xD7FF),  # hangul syllables
]

# Cyrillic and typical Russian typography is of course allowed; this is a narrow "East Asian" test.
ALLOWED_SUFFIXES = (".cs", ".md", ".json", ".props", ".targets", ".py", ".ps1")

# This file holds the ranges as data and cites the glyphs it searches for, so scanning it would
# report its own table as a defect.
EXCLUDE = {"lint-cjk.py"}


def bad_char(ch):
    code = ord(ch)
    return any(lo <= code <= hi for lo, hi in CJK_RANGES)


def scan(root):
    hits = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in {"bin", "obj", ".nuget", ".git", "scratch", "node_modules", ".qwen"}]
        for name in filenames:
            if name in EXCLUDE:
                continue
            if not name.endswith(ALLOWED_SUFFIXES):
                continue
            path = os.path.join(dirpath, name)
            try:
                with open(path, "r", encoding="utf-8") as fh:
                    for lineno, line in enumerate(fh, 1):
                        offenders = sorted({ch for ch in line if bad_char(ch)})
                        if offenders:
                            rel = os.path.relpath(path, root)
                            hits.append((rel, lineno, "".join(offenders), line.strip()[:120]))
            except (UnicodeDecodeError, OSError):
                continue
    return hits


def main():
    # The console here is cp1250/cp866 and the characters being reported are exactly the ones it
    # cannot encode, so print codepoints rather than the glyphs.
    targets = sys.argv[1:] or [os.path.dirname(os.path.dirname(os.path.abspath(__file__)))]
    total = 0
    for target in targets:
        for rel, lineno, chars, snippet in scan(target):
            codepoints = " ".join(f"U+{ord(c):04X}" for c in chars)
            safe = snippet.encode("ascii", "backslashreplace").decode("ascii")
            print(f"{rel}:{lineno}: CJK {codepoints}: {safe}")
            total += 1
    if total:
        print(f"\nСтрок с иероглифами: {total}")
        return 1
    print("Иероглифов не найдено.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
