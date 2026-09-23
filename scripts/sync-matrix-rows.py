#!/usr/bin/env python3
"""Разворачивает строки матрицы для профилей выпуска и сверяет ссылки (шаг A.2/A.3 плана).

Знаменатель — каталог: `matrix.json` хранит только строки, по которым есть что сказать
(доказательства, тесты, ограничения), а `scripts\\emit-coverage-matrix.py` достраивает отсутствующие
режимы каталога как `not_started`. Так «не сделано» видно в метрике, но не превращается в
ручную работу по ведению двухсот строк.

Что делает этот скрипт:
1. проверяет, что каждая `modes[].ref` и каждая `catalog_refs[]` профиля существуют в каталоге
   (профиль не имеет права ссылаться на режим, которого нет: ссылка «на глаз» — это потерянное
   сопоставление);
2. создаёт или обновляет строки матрицы для режимов профиля, проставляя `profile`, `priority`,
   `queue`, `depends_on` — приоритет отдельно от уровня доказательства (план §6);
3. НЕ поднимает статусы: строка получает `not_started` везде, кроме уже существующей строки с
   доказательством. Уровень каталога сам по себе основанием `verified` не считается;
4. идемпотентен: повторный запуск не меняет файл.

Запуск: python scripts\\sync-matrix-rows.py [--dry-run]
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


def main():
    dry = "--dry-run" in sys.argv
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
        matrix["meta"].setdefault("profiles", {})[profile_id] = {
            "artifact": os.path.relpath(os.path.join(PROFILE_DIR, name), ROOT).replace("\\", "/"),
            "title": profile["meta"]["title"],
            "plan": profile["meta"]["plan"],
        }

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
                               ("queue", entry.get("queue")),
                               ("depends_on", entry.get("depends_on") or [])):
                if value is not None and existing.get(key) != value:
                    existing[key] = value
                    changed = True
            if "kind" not in existing:
                existing["kind"] = "profile_mode" if kind == "mode" else "profile_operation"
                changed = True
            (touched if changed else skipped).append(ref)

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
                changed = False
                if existing.get("catalog_ref") != family_ref:
                    existing["catalog_ref"] = family_ref
                    changed = True
                for key, value in (("acceptance", dep.get("acceptance") or []),
                                   ("required_of", dep.get("required_of") or []),
                                   ("required_actions", dep.get("required_actions") or []),
                                   ("title", dep.get("title"))):
                    if existing.get(key) != value:
                        existing[key] = value
                        changed = True
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
        if row.get("tests") or any(v != "not_started" for v in row["actions"].values()):
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
    with open(matrix_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(matrix, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    print("matrix.json записан")
    return 0


if __name__ == "__main__":
    sys.exit(main())
