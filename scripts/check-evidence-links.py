#!/usr/bin/env python3
"""Guard: a path referenced by a MACHINE file must exist on disk.

WHY THIS EXISTS. `scratch/` was cleaned by REPO_SLIMMING (10.10.2026) with `shutil.rmtree`, and the
"keep" list was built only from links in `docs/STATUS.md`. Machine files - the coverage matrix and
path constants in `scripts/` - were not consulted, so 98 evidence references in
`coverage/solid-v24/matrix.json` pointed at deleted files. `emit-coverage-matrix.py` reported the
damage only afterwards, and only for the matrix. This guard names the SAME class BEFORE the delete:
it walks the machine files, resolves every path they reference, and fails when one is gone.

WHAT IS SCANNED, and why exactly this:
  * `coverage/**/*.json` - every `evidence` array, resolved the same way `emit-coverage-matrix.py`
    resolves it (cut at the first `#` anchor, then at a ` (` note, then existence). Two instruments
    that disagree about what "the reference resolves" means would be worse than one.
  * `scripts/*.py` - module-level UPPER-CASE string constants that LOOK like a repo-relative path.
    Only literals: a path built with `os.path.join(ROOT, ...)` or an f-string is not a constant, and
    guessing at those would make the guard fire on computed outputs.

WHAT IS NOT SCANNED. Prose in `docs/` (journals, decisions, `04_KOMPAS_API_NOTES.md`): a historical
record of a measurement keeps naming the path it was measured against, and a missing file there is
the honest state of history, not a defect. Outputs are not scanned either - a report that has not
been produced yet is not a broken link.

Usage:
    python scripts/check-evidence-links.py [--root <dir>] [--self-test]

Exit code 0 - every referenced path resolves; 1 - at least one does not, each one named.
"""

from __future__ import annotations

import ast
import glob
import json
import os
import re
import sys
import tempfile

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# A path-like literal: no spaces, no quotes, no Cyrillic. Prose constants in `scripts/` are long
# Russian sentences and are excluded by this alone - measured: without it, `record-b2-rows.py`
# contributes a dozen false positives that are text, not paths.
PATH_LIKE = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.\-/\\]*$")

COVERAGE_GLOB = os.path.join("coverage", "**", "*.json")
SCRIPT_GLOB = os.path.join("scripts", "*.py")


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def resolve_reference(ref):
    """The file path a reference names, or "" when the reference names nothing checkable.

    Same two cuts as `emit-coverage-matrix.py`: `path#anchor` and `path (note)`. A reference whose
    left part is not a path at all is skipped rather than reported - a guard that fires on prose is
    a guard nobody keeps.
    """
    if not isinstance(ref, str):
        return ""
    path = ref.split("#", 1)[0]
    if " (" in path:
        path = path.split(" (", 1)[0]
    return path.strip()


def evidence_references(root):
    """(source_file, reference) for every `evidence` entry in every coverage JSON file."""
    out = []
    for path in sorted(glob.glob(os.path.join(root, COVERAGE_GLOB), recursive=True)):
        try:
            with open(path, encoding="utf-8-sig") as fh:
                document = json.load(fh)
        except (OSError, json.JSONDecodeError) as exc:
            # An unreadable coverage file is itself a defect, and it must be named rather than
            # skipped: a silent skip would make the guard quieter exactly where the data is broken.
            out.append((os.path.relpath(path, root).replace("\\", "/"),
                        "НЕ ЧИТАЕТСЯ: %s: %s" % (type(exc).__name__, exc)))
            continue
        source = os.path.relpath(path, root).replace("\\", "/")
        for ref in _evidence_values(document):
            out.append((source, ref))
    return out


def _evidence_values(node):
    """Every string inside every `evidence` array, at any depth."""
    found = []
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "evidence" and isinstance(value, list):
                found.extend(value)
            else:
                found.extend(_evidence_values(value))
    elif isinstance(node, list):
        for item in node:
            found.extend(_evidence_values(item))
    return found


def script_path_constants(root):
    """(source_file, constant_name, value) for module-level path-like string constants."""
    out = []
    for path in sorted(glob.glob(os.path.join(root, SCRIPT_GLOB))):
        try:
            with open(path, encoding="utf-8-sig") as fh:
                tree = ast.parse(fh.read())
        except (OSError, SyntaxError):
            continue
        source = os.path.relpath(path, root).replace("\\", "/")
        for node in tree.body:
            if not isinstance(node, ast.Assign):
                continue
            if not (isinstance(node.value, ast.Constant) and isinstance(node.value.value, str)):
                continue
            for target in node.targets:
                if not (isinstance(target, ast.Name) and target.id.isupper()):
                    continue
                value = node.value.value
                if ("/" in value or "\\" in value) and PATH_LIKE.match(value):
                    out.append((source, target.id, value))
    return out


def findings(root):
    """Every reference that does not resolve, as (source, reference, resolved_path)."""
    missing = []
    for source, ref in evidence_references(root):
        path = resolve_reference(ref)
        if not path:
            continue
        if not os.path.exists(os.path.join(root, path.replace("/", os.sep))):
            missing.append((source, ref, path))
    for source, name, value in script_path_constants(root):
        if not os.path.exists(os.path.join(root, value.replace("/", os.sep))):
            missing.append((source, "%s = %s" % (name, value), value))
    return missing


def self_test():
    """The guard must pass on a clean tree and NAME a removed file on a broken one.

    Both halves are mandatory. A guard that always answers "no findings" passes any broken input, so
    the negative control is the point: one referenced file is deleted, and exactly that reference
    must be reported - by file, by source, and by path.
    """
    import shutil

    checks, failures = [], []

    def check(name, ok, detail=""):
        checks.append(name)
        if not ok:
            failures.append(name)
        print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name, (" — " + detail) if detail else ""))

    with tempfile.TemporaryDirectory(prefix="evidence-links-selftest-") as tmp:
        coverage = os.path.join(tmp, "coverage", "solid-v24")
        scripts = os.path.join(tmp, "scripts")
        os.makedirs(coverage)
        os.makedirs(scripts)
        for name in ("present.json", "gone.json"):
            with open(os.path.join(tmp, name), "w", encoding="utf-8") as fh:
                fh.write("{}")
        with open(os.path.join(scripts, "gone.py"), "w", encoding="utf-8") as fh:
            fh.write("# placeholder\n")
        matrix = {"rows": [
            {"operation_id": "X.01", "evidence": ["present.json", "gone.json#anchor"]},
            {"operation_id": "X.02", "evidence": ["present.json"]},
        ]}
        matrix_path = os.path.join(coverage, "matrix.json")
        with open(matrix_path, "w", encoding="utf-8") as fh:
            json.dump(matrix, fh, ensure_ascii=False)
        with open(os.path.join(scripts, "probe.py"), "w", encoding="utf-8") as fh:
            fh.write("PRESENT_PATH = 'present.json'\n"
                     "GONE_PATH = 'scripts/gone.py'\n"
                     "PROSE = 'это не путь, а текст с пробелами и/или слэшем: да/нет'\n")

        clean = findings(tmp)
        check("чистое дерево: находок нет", clean == [], str(clean))

        os.remove(os.path.join(scripts, "gone.py"))
        os.remove(os.path.join(tmp, "gone.json"))
        broken = findings(tmp)
        names = {(source, ref) for source, ref, _ in broken}
        check("удалённый файл назван по ссылке из coverage",
              ("coverage/solid-v24/matrix.json", "gone.json#anchor") in names, str(sorted(names)))
        check("удалённый файл назван по константе скрипта",
              ("scripts/probe.py", "GONE_PATH = scripts/gone.py") in names)
        check("находок ровно две", len(broken) == 2, str(len(broken)))

        # Отрицательный контроль «не хватает ссылки»: строка, ссылающаяся на несуществующий файл,
        # обязана быть названа. Без него прибор мог бы «проходить» на пустом наборе ссылок.
        matrix["rows"].append({"operation_id": "X.03", "evidence": ["missing.json"]})
        with open(matrix_path, "w", encoding="utf-8") as fh:
            json.dump(matrix, fh, ensure_ascii=False)
        broken = findings(tmp)
        names = {(source, ref) for source, ref, _ in broken}
        check("отсутствующий файл назван", ("coverage/solid-v24/matrix.json", "missing.json") in names)
        check("проза в константе не считается путём",
              not any(source == "scripts/probe.py" and ref.startswith("PROSE") for source, ref in names))
        check("существующий файл не назван",
              not any(ref in ("present.json", "PRESENT_PATH = present.json") for _s, ref in names))
        check("находок ровно три", len(broken) == 3, str(len(broken)))

    print("  проверок пройдено: %d из %d" % (len(checks) - len(failures), len(checks)))
    if failures:
        print("  батарея: КРАСНАЯ — " + ", ".join(failures))
        return 1
    print("  батарея: ЗЕЛЁНАЯ")
    return 0


def main():
    if "--self-test" in sys.argv[1:]:
        return self_test()
    root = os.path.abspath(argument("--root") or ROOT)
    if not os.path.isdir(root):
        print("каталог не найден: " + root)
        return 2
    missing = findings(root)
    if not missing:
        print("ссылки на доказательства: все разрешаются (корень %s)" % root)
        return 0
    print("ссылки на доказательства не разрешаются: %d" % len(missing))
    for source, ref, path in missing:
        print("  - %s: %s" % (source, ref))
        print("      файла нет: %s" % path)
    print("\nМашинный файл ссылается на путь, которого нет. Либо ссылка устарела, либо файл удалён: "
          "чистка обязана сначала собрать список путей, на которые ссылаются отслеживаемые файлы, "
          "и не удалять их (AGENTS.md, «Карта для агента»).")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
