"""Guard against long dashes in the text this project publishes.

INVARIANT: published text uses the plain hyphen "-" as a dash. EM DASH, EN DASH and their look-alikes
never reach a release title, release notes, a file inside the release ZIP or a public front page.
The customer asked for this rule after long dashes reappeared in a release title.
History: docs/decisions/releases.md#dashes

Usage:
  python scripts/lint-dashes.py                  check PUBLIC_TEXT (exit 1 on any finding)
  python scripts/lint-dashes.py PATH ...         check the given files or folders
  python scripts/lint-dashes.py --fix [PATH ...] replace long dashes with "-" in place, then check
  python scripts/lint-dashes.py --text "..."     check one string (a release title)
  python scripts/lint-dashes.py --release vX.Y.Z check the title and notes of a published GitHub release
  python scripts/lint-dashes.py --self-test
"""

import os
import subprocess
import sys

# The report is Russian and the console here is cp1250: without this the guard crashes on itself.
for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Written as escapes: this file must not carry the characters it forbids.
LONG_DASHES = {
    "\u2014": "EM DASH",
    "\u2013": "EN DASH",
    "\u2012": "FIGURE DASH",
    "\u2015": "HORIZONTAL BAR",
    "\u2E3A": "TWO-EM DASH",
    "\u2E3B": "THREE-EM DASH",
}

# INVARIANT: every file a reader meets outside the source tree: GitHub front pages, release notes, and
# each text file that goes into the release ZIP. A new published text file is added here.
PUBLIC_TEXT = [
    "README.md",
    "KOMPAS3D_MCP.md",
    "LICENSE",
    "AGENTS.md",
    "config/kompas-mcp.example.json",
    "scripts/Install-KompasMcp.ps1",
    "docs/distribution",
    "docs/operator-guide",
]

TEXT_SUFFIXES = (".md", ".txt", ".json", ".ps1", "")


def dash_findings(text):
    """`(line, column, name)` of every long dash in `text`; lines and columns are 1-based."""
    found = []
    for lineno, line in enumerate(text.splitlines(), 1):
        for col, ch in enumerate(line, 1):
            if ch in LONG_DASHES:
                found.append((lineno, col, LONG_DASHES[ch]))
    return found


def replace_dashes(text):
    """The same text with every long dash written as "-"; spacing around the dash is kept."""
    return "".join("-" if ch in LONG_DASHES else ch for ch in text)


def files_under(target):
    """Text files of `target` (a file or a folder), sorted for a stable report."""
    if os.path.isfile(target):
        return [target]
    out = []
    for base, dirs, names in os.walk(target):
        dirs[:] = [d for d in dirs if d not in {"bin", "obj", ".git", "scratch", "node_modules"}]
        out += [os.path.join(base, n) for n in names if os.path.splitext(n)[1].lower() in TEXT_SUFFIXES]
    return sorted(out)


def read_text(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def check_files(targets, fix):
    """Print findings; with `fix`, rewrite the files first. Returns the number of findings left."""
    total = 0
    for target in targets:
        if not os.path.exists(target):
            print(f"{os.path.relpath(target, ROOT)}: нет такого пути")
            total += 1
            continue
        for path in files_under(target):
            try:
                text = read_text(path)
            except (UnicodeDecodeError, OSError):
                continue
            if fix and dash_findings(text):
                # INVARIANT: keep the file's own BOM and line endings; only the dash characters change.
                with open(path, "rb") as f:
                    raw = f.read()
                bom = raw.startswith(b"\xef\xbb\xbf")
                fixed = replace_dashes(raw.decode("utf-8-sig"))
                with open(path, "wb") as f:
                    f.write((b"\xef\xbb\xbf" if bom else b"") + fixed.encode("utf-8"))
                text = read_text(path)
            rel = os.path.relpath(path, ROOT)
            for lineno, col, name in dash_findings(text):
                print(f"{rel}:{lineno}:{col}: {name}")
                total += 1
    return total


def check_text(label, text):
    findings = dash_findings(text)
    for lineno, col, name in findings:
        print(f"{label}:{lineno}:{col}: {name}")
    return len(findings)


def check_release(tag):
    """Title and notes of the published release, read back from GitHub rather than from local files."""
    total = 0
    for field in ("name", "body"):
        done = subprocess.run(["gh", "release", "view", tag, "--json", field, "-q", "." + field],
                              cwd=ROOT, capture_output=True)
        if done.returncode != 0:
            print(f"release {tag}: gh не прочитал поле {field}: "
                  f"{done.stderr.decode('utf-8', 'replace').strip()}")
            return total + 1
        total += check_text(f"release {tag} {'заголовок' if field == 'name' else 'заметки'}",
                            done.stdout.decode("utf-8", "replace"))
    return total


def self_test():
    em, en = "\u2014", "\u2013"
    title = f"KompasMCP 0.1.0 {em} переменные"
    cases = [
        (dash_findings(title) == [(1, 17, "EM DASH")], "длинное тире найдено с позицией"),
        (dash_findings(f"a\nb {en} c") == [(2, 3, "EN DASH")], "короткое типографское тире найдено"),
        (dash_findings("KompasMCP 0.1.0 - переменные") == [], "дефис принят"),
        (dash_findings("3-9 с, B1-B5, «цитата»") == [], "диапазоны с дефисом и кавычки приняты"),
        (replace_dashes(title) == "KompasMCP 0.1.0 - переменные", "замена сохраняет пробелы"),
        (replace_dashes(f"3{en}9") == "3-9", "диапазон получает дефис"),
        (all(dash_findings(ch) for ch in LONG_DASHES), "каждый запрещённый знак ловится"),
        (all(not dash_findings(replace_dashes(ch)) for ch in LONG_DASHES), "после замены находок нет"),
    ]
    for ok, name in cases:
        print(("PASS " if ok else "FAIL ") + name)
    return 0 if all(ok for ok, _ in cases) else 1


def main(argv):
    if "--self-test" in argv:
        return self_test()
    if "--text" in argv:
        i = argv.index("--text")
        if i + 1 >= len(argv):
            print("--text требует строку")
            return 2
        total = check_text("text", argv[i + 1])
    elif "--release" in argv:
        i = argv.index("--release")
        if i + 1 >= len(argv):
            print("--release требует тег")
            return 2
        total = check_release(argv[i + 1])
    else:
        fix = "--fix" in argv
        paths = [a for a in argv if not a.startswith("--")]
        targets = [os.path.abspath(p) for p in paths] or [os.path.join(ROOT, p) for p in PUBLIC_TEXT]
        total = check_files(targets, fix)
    if total:
        print(f"\nДлинных тире: {total}. В публикуемом тексте тире пишется дефисом «-».")
        return 1
    print("Длинных тире не найдено.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
