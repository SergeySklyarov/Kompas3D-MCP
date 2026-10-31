#!/usr/bin/env python3
"""Creates the matrix rows a release profile asks for and checks its references (plan A.2/A.3).

The denominator is the catalog: `matrix.json` holds only the rows that have something to say
(evidence, tests, limitations), and `scripts\\emit-coverage-matrix.py` adds the missing catalog modes
as `not_started`. So "not done" shows in the metric without turning into manual upkeep of 200 rows.

What this instrument does:
1. checks that every `modes[].ref` and every `catalog_refs[]` of a profile exists in the catalog;
2. creates matrix rows for profile modes and fills `profile`, `priority`, `queue`, `depends_on`;
3. never raises a status: a new row is `not_started` everywhere, and a catalog level alone is no
   ground for `verified`;
4. never overwrites what a row already says. A profile is a QUEUE, a row is the RECORD of proof;
   several profiles name the same dependency or mode, so "the last profile wins" would make the file
   depend on file order and would wipe `acceptance`, `required_actions`, `required_of`, `title`,
   `catalog_ref` and `depends_on` of the fuller declaration;
5. is idempotent: a second run does not change the file.

Usage: python scripts\\sync-matrix-rows.py [--dry-run] [--self-test]
"""
import json
import os
import sys
from collections import OrderedDict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COVERAGE = os.path.join(ROOT, "coverage", "solid-v24")
PROFILE_DIR = os.path.join(COVERAGE, "release-profiles")
ACTIONS = ["discover", "create", "read", "edit", "rebuild", "save_reopen",
           "suppress_restore", "delete_dependencies", "negative_tests", "geometry_validation"]

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def load(path):
    # `utf-8-sig`, а не `utf-8`: файлы покрытия записаны С BOM (проектное соглашение, см. `dump()`
    # в scripts/record-b2-rows.py), и обычный `utf-8` падает на первом же байте:
    # `json.decoder.JSONDecodeError: Unexpected UTF-8 BOM`. Ошибка найдена 17.09.2026 при прогоне
    # этой проверки на уже записанной матрице — то есть скрипт молча перестал читать собственный
    # вход, а не «не нашёл расхождений». `utf-8-sig` читает и файлы без BOM тоже.
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle, object_pairs_hook=OrderedDict)


def catalog_index(catalog):
    """{id: (family, kind, node)} — операции и все режимы/варианты одним словарём."""
    index = OrderedDict()
    for family in catalog["families"]:
        for op in family.get("operations") or []:
            index[op["id"]] = (family["id"], "operation", op)
            for key in ("modes", "variants", "copy_variants"):
                for mode in op.get(key) or []:
                    if isinstance(mode, dict) and mode.get("id"):
                        index[mode["id"]] = (family["id"], "mode", mode)
    return index


def row_for(ref, entry, family_id, kind, profile_id):
    row = OrderedDict()
    row["operation_id"] = ref
    row["kind"] = "profile_mode" if kind == "mode" else "profile_operation"
    row["profile"] = profile_id
    row["priority"] = entry.get("priority", "practical_required")
    row["queue"] = entry.get("queue")
    row["context"] = entry.get("context", "part")
    row["catalog_ref"] = family_id
    row["catalog_level"] = entry.get("current_level")
    row["parameters_schema"] = None
    row["adapter_route"] = entry.get("route_note")
    row["actions"] = OrderedDict((a, "not_started") for a in ACTIONS)
    row["tests"] = []
    row["evidence"] = ["coverage/solid-v24/catalog.json#" + ref]
    limitations = []
    if entry.get("gap"):
        limitations.append("разрыв цикла: " + entry["gap"])
    if entry.get("blocking"):
        limitations.append("блокировка выпуска: " + entry["blocking"])
    if entry.get("note"):
        limitations.append(entry["note"])
    row["limitations"] = limitations
    row["depends_on"] = entry.get("depends_on") or []
    return row


def file_form(path):
    """The file's own writing form: BOM, indent width, line terminator."""
    with open(path, "rb") as handle:
        raw = handle.read()
    text = raw.decode("utf-8-sig")
    indent = 1
    for line in text.splitlines():
        body = line.lstrip(" ")
        if body and len(body) != len(line):
            indent = len(line) - len(body)
            break
    return raw.startswith(b"\xef\xbb\xbf"), indent, "\r\n" if b"\r\n" in raw else "\n"


def carries_proof(row):
    """A row that keeps its own evidence: a test, or any action that left `not_started`."""
    return bool(row.get("tests")) or any(v != "not_started" for v in row["actions"].values())


def fill(row, key, value):
    """Set a content field only where the row has nothing yet. A profile is a queue, not the record.

    LIMIT: a later profile edit to an already filled field does not reach the row; such a divergence
    is found by comparing the profile with the row and fixed by an order, not by this instrument.
    """
    if not value or row.get(key):
        return False
    row[key] = value
    return True


def write_matrix(path, matrix):
    """Write `matrix.json` in the FILE's own form: its BOM, its indent, its line terminators.

    INVARIANT: a run that changes a few rows must not restyle the rest of the file, so the form is
    read from the bytes on disk. A literal indent or a text-mode open rewrites every line the run
    never touched. `.gitattributes` is not the source either: it asks for CRLF on `*.json` while the
    tracked file is LF.
    History: docs/decisions/api-audit.md#matrix-profile-sync-write
    """
    bom, indent, eol = file_form(path)
    text = json.dumps(matrix, ensure_ascii=False, indent=indent).replace("\n", eol) + eol
    with open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="") as handle:
        handle.write(text)


def main(argv):
    dry = "--dry-run" in argv
    catalog = load(os.path.join(COVERAGE, "catalog.json"))
    matrix_path = os.path.join(COVERAGE, "matrix.json")
    matrix = load(matrix_path)
    index = catalog_index(catalog)
    problems = []
    touched = []
    skipped = []
    referenced = set()

    rows_by_id = OrderedDict((r["operation_id"], r) for r in matrix["rows"])
    order = [r["operation_id"] for r in matrix["rows"]]

    profiles = sorted(f for f in os.listdir(PROFILE_DIR) if f.endswith(".json"))
    for name in profiles:
        profile = load(os.path.join(PROFILE_DIR, name))
        profile_id = profile["meta"]["profile_id"]
        # The profile file owns `artifact` and `title`; `plan` is not in every profile's meta, and a
        # minimal profile without it must not blank the pointer the matrix already keeps.
        entry = matrix["meta"].setdefault("profiles", {}).setdefault(profile_id, OrderedDict())
        entry["artifact"] = os.path.relpath(os.path.join(PROFILE_DIR, name), ROOT).replace("\\", "/")
        entry["title"] = profile["meta"]["title"]
        fill(entry, "plan", profile["meta"].get("plan"))

        for entry in profile.get("modes") or []:
            ref = entry["ref"]
            if ref not in index:
                problems.append(f"{profile_id}: ref {ref} нет в каталоге")
                continue
            family_id, kind, _node = index[ref]
            referenced.add(ref)
            declared_family = entry.get("family")
            if declared_family and declared_family != family_id:
                problems.append(f"{profile_id}: {ref} — семья в профиле {declared_family}, "
                                f"в каталоге {family_id}")
            existing = rows_by_id.get(ref)
            if existing is None:
                rows_by_id[ref] = row_for(ref, entry, family_id, kind, profile_id)
                order.append(ref)
                touched.append(ref)
                continue
            # Строка с доказательством уже есть: обновляются только поля сопоставления,
            # статусы действий не трогаются.
            #
            # `catalog_level` здесь НЕ обновляется из профиля, и это исправление 17.09.2026.
            # Профиль — это ОЧЕРЕДЬ (что требуется и в каком порядке), а `catalog_level` матрицы —
            # утверждение о фактическом доказательстве строки, которое ведут `record-b2-rows.py` и
            # `emit-coverage-matrix.py`. Пока поле переписывалось из профиля, первый же прогон этой
            # сверки ПОНИЖАЛ уже подтверждённые строки: SM-07 `mcp_verified` → `metadata_found`
            # (уровень режима в профиле не проставлен), SM-09 `mcp_implemented` → `blocked_api`
            # (в `current_level` профиля стоит статус действия, а не уровень) — то есть проверка
            # портила то, что проверяла, и делала это молча: «создано/обновлено: 11» читается как
            # норма. Опаснее всего то, что откат не был заметен: обе записи звучат правдоподобно.
            changed = False
            for key, value in (("profile", profile_id), ("priority", entry.get("priority")),
                               ("queue", entry.get("queue"))):
                if value is not None and existing.get(key) != value:
                    existing[key] = value
                    changed = True
            # `depends_on` belongs to the queue, but a row shared by two profiles holds one list:
            # filling it only where it is empty keeps the file independent of profile order.
            changed = fill(existing, "depends_on", entry.get("depends_on") or []) or changed
            if "kind" not in existing:
                existing["kind"] = "profile_mode" if kind == "mode" else "profile_operation"
                changed = True
            (touched if changed else skipped).append(ref)

        # Профиль ссылается на строки не только режимами и зависимостями: `scenarios[].covers`
        # перечисляет строки, которые сценарий проверяет СКВОЗНО (масса VM-06 — сквозная проверка
        # существующего инструмента, а не режим профиля). Без этого сквозная строка выглядела
        # «больше не упоминается», и прибор требовал снять её вручную — то есть не различал
        # «ссылки нет» и «ссылка не прочитана».
        for scenario in profile.get("scenarios") or []:
            for ref in scenario.get("covers") or []:
                referenced.add(ref)

        for dep in profile.get("common_dependencies") or []:
            for ref in dep.get("catalog_refs") or []:
                if ref not in index and ref not in {f["id"] for f in catalog["families"]}:
                    problems.append(f"{profile_id}: {dep['id']} → catalog_refs {ref} не найдено")
            dep_id = dep["id"]
            referenced.add(dep_id)
            refs = dep.get("catalog_refs") or []
            first = refs[0] if refs else None
            # catalog_ref обязан быть семьёй: на него смотрит инвариант «каждая семья либо
            # имеет строку, либо в очереди». Ссылки профиля на операции и режимы живут в
            # catalog_refs.
            family_ref = index[first][0] if first in index else first
            existing = rows_by_id.get(dep_id)
            if existing is None:
                row = OrderedDict()
                row["operation_id"] = dep_id
                row["kind"] = "common_dependency"
                row["profile"] = profile_id
                row["priority"] = "dependency_of"
                row["queue"] = None
                row["context"] = dep.get("context", "part")
                row["catalog_ref"] = family_ref
                row["catalog_refs"] = refs
                row["title"] = dep["title"]
                row["parameters_schema"] = None
                row["adapter_route"] = dep.get("known_gap")
                row["actions"] = OrderedDict((a, "not_started") for a in ACTIONS)
                row["required_actions"] = dep.get("required_actions") or []
                row["tests"] = []
                row["evidence"] = ["coverage/solid-v24/release-profiles/" + name]
                row["limitations"] = ([dep["known_gap"]] if dep.get("known_gap") else [])
                row["acceptance"] = dep.get("acceptance") or []
                row["required_of"] = dep.get("required_of") or []
                rows_by_id[dep_id] = row
                order.append(dep_id)
                touched.append(dep_id)
            if existing is not None:
                changed = fill(existing, "catalog_ref", family_ref)
                for key, value in (("acceptance", dep.get("acceptance") or []),
                                   ("required_of", dep.get("required_of") or []),
                                   ("required_actions", dep.get("required_actions") or []),
                                   ("title", dep.get("title"))):
                    changed = fill(existing, key, value) or changed
                (touched if changed else skipped).append(dep_id)

        # Режимы профиля с priority != practical_required не обязаны иметь строку матрицы,
        # но если имеют — должны ссылаться честно (проверено выше).
        for mode in profile.get("modes") or []:
            if mode.get("priority") not in ("practical_required", "next", "later"):
                problems.append(f"{profile_id}: {mode['ref']} — неизвестный priority "
                                f"{mode.get('priority')}")

    # Строка, которую создал профиль и которая больше ним не упоминается, снимается: иначе
    # выпуск оставил бы фантомную претензию в матрице. Удаляется только строка без единого
    # доказательства (ни теста, ни одного не-not_started статуса) — ведомые вручную строки
    # неприкосновенны.
    pruned = []
    for ref in list(order):
        row = rows_by_id[ref]
        if ref in referenced or not row.get("profile"):
            continue
        if carries_proof(row):
            problems.append(f"{ref}: строка профиля больше не упоминается, но в ней есть "
                            f"доказательство — снимите её вручную, а не молча")
            continue
        order.remove(ref)
        del rows_by_id[ref]
        pruned.append(ref)

    matrix["rows"] = [rows_by_id[i] for i in order]
    matrix["meta"]["schema_version"] = "1.1"

    # Очередь инвентаризации матрицы — производная от строк, а не независимый список: иначе два
    # «pending» разъезжаются, и генератор ловит это уже постфактум.
    covered = {r["catalog_ref"] for r in matrix["rows"] if r.get("catalog_ref")}
    matrix["pending_families"] = sorted(f["id"] for f in catalog["families"]
                                         if f["id"] not in covered)

    print(f"строк матрицы: {len(matrix['rows'])} (было {len(load(matrix_path)['rows'])}); "
          f"семейств без строк: {len(matrix['pending_families'])}")
    print(f"создано/обновлено: {len(touched)}");
    if pruned:
        print(f"снято строк профиля, без доказательств: {pruned}")
    if skipped:
        print(f"без изменений: {len(skipped)}")
    if problems:
        print("ПРОБЛЕМЫ СОПОСТАВЛЕНИЯ:")
        for item in problems:
            print("  -", item)
        return 1
    if dry:
        print("(--dry-run: matrix.json не изменён)")
        return 0
    write_matrix(matrix_path, matrix)
    print("matrix.json записан")
    return 0


def self_test():
    """Proves the writing form and the refusal on temporary files: the repository matrix is not read."""
    import io
    import tempfile
    from contextlib import redirect_stdout
    global COVERAGE, PROFILE_DIR

    failures, checks = [], 0

    def check(ok, message):
        nonlocal checks
        checks += 1
        if not ok:
            failures.append(message)

    with tempfile.TemporaryDirectory() as tmp:
        sample = {"rows": [], "meta": {"schema_version": "1.0"}}
        for bom in (False, True):
            for indent in (1, 2):
                for eol in ("\n", "\r\n"):
                    label = f"BOM={bom}, отступ={indent}, конец строки={eol!r}"
                    head = b"\xef\xbb\xbf" if bom else b""
                    text = json.dumps(sample, ensure_ascii=False, indent=indent).replace("\n", eol) + eol
                    path = os.path.join(tmp, "form.json")
                    with open(path, "wb") as handle:
                        handle.write(head + text.encode("utf-8"))
                    write_matrix(path, load(path))
                    first = open(path, "rb").read()
                    check(first == head + text.encode("utf-8"), f"{label}: форма файла не сохранена")
                    write_matrix(path, load(path))
                    check(open(path, "rb").read() == first, f"{label}: повторная запись изменила байты")

        coverage = os.path.join(tmp, "coverage", "solid-v24")
        profile_dir = os.path.join(coverage, "release-profiles")
        os.makedirs(profile_dir)
        matrix_path = os.path.join(coverage, "matrix.json")
        catalog = {"families": [{"id": "F1", "operations": [
            {"id": "F1.op", "modes": [{"id": "F1.op.m"}]}]}]}
        row = {"operation_id": "F1.op.m", "kind": "profile_mode", "profile": "p1",
               "priority": "practical_required", "queue": None, "catalog_ref": "F1",
               "actions": {a: "verified" for a in ACTIONS}, "tests": ["T.1"],
               "evidence": [], "limitations": [], "depends_on": []}
        for name, payload in (("catalog.json", catalog),
                              ("matrix.json", {"meta": {"schema_version": "1.1"}, "rows": [row],
                                               "pending_families": []})):
            with open(os.path.join(coverage, name), "w", encoding="utf-8", newline="") as handle:
                handle.write(json.dumps(payload, ensure_ascii=False, indent=1) + "\n")
        profile_path = os.path.join(profile_dir, "p1.json")

        def run(profile):
            with open(profile_path, "w", encoding="utf-8", newline="") as handle:
                handle.write(json.dumps(profile, ensure_ascii=False, indent=1) + "\n")
            with redirect_stdout(io.StringIO()):
                return main([])

        saved = COVERAGE, PROFILE_DIR
        COVERAGE, PROFILE_DIR = coverage, profile_dir
        try:
            before = open(matrix_path, "rb").read()
            code = run({"meta": {"profile_id": "p1", "title": "t"}, "modes": []})
            check(code == 1, f"строка с доказательством без упоминания дала код {code}, а не 1")
            check(open(matrix_path, "rb").read() == before, "отказ записал matrix.json")
            code = run({"meta": {"profile_id": "p1", "title": "t"},
                        "modes": [{"ref": "F1.op.m", "priority": "practical_required"}]})
            check(code == 0, f"строка, названная профилем, дала код {code}, а не 0")
        finally:
            COVERAGE, PROFILE_DIR = saved

    print(f"самопроверка: проверок {checks}, провалов {len(failures)}")
    for item in failures:
        print("  -", item)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(self_test() if "--self-test" in sys.argv else main(sys.argv[1:]))
