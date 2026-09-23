#!/usr/bin/env python3
"""Слияние драфтов инвентаризации P6.0 в coverage/solid-v24/catalog.json.

Отличие от первой (откатанной) версии: структура драфтов не угадывается по именам ключей, а
обходится рекурсивно; семейства, уже описанные в каталоге, НЕ пропускаются, а объединяются; каждый
конфликт записывается в протокол, а не разрешается молча; очередь инвентаризации в catalog.json и
в matrix.json приводится к одному значению.

Уровень доказательства: драфты не исполняли ни одного COM-вызова, поэтому их потолок —
metadata_found. Существующие в каталоге уровни, полученные измерением, никогда не понижаются.

Запуск: python scripts\\merge-inventory-drafts.py [--dry-run]
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COVERAGE = os.path.join(ROOT, "coverage", "solid-v24")
# Драфты версионированы рядом с каталогом (coverage/solid-v24/sources/): доказательство не должно
# жить в scratch/, который в .gitignore и теряется между машинами.
DRAFTS = [
    os.path.join(COVERAGE, "sources", "part-a-entries.json"),
    os.path.join(COVERAGE, "sources", "part-b-entries.json"),
]
SM_ID = re.compile(r"^SM-\d{2}$")
OP_ID = re.compile(r"^SM-\d{2}\..+")
CEILING = "metadata_found"
RANK = {"documented": 0, "metadata_found": 1, "runtime_verified": 2,
        "mcp_implemented": 3, "mcp_verified": 4}
# Поля, которые ведёт измерение: драфт их не перетирает.
EVIDENCE_KEYS = {"level", "evidence", "mcp_support", "modes_verified", "acceptance",
                 "tests", "limitations", "classification"}

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def load(path):
    # `utf-8-sig`, а не `utf-8`: `catalog.json` и `matrix.json` пишутся с BOM (проектное соглашение,
    # их пишет `record-b2-rows.py`), и обычный `utf-8` оставляет BOM в первой строке, отчего
    # `json.load` падает на «Unexpected UTF-8 BOM». Драфты в `sources/` идут БЕЗ BOM — `utf-8-sig`
    # читает и их: он отбрасывает BOM, только если он есть. Найдено 19.09.2026 при проверке, что
    # регенерация каталога не возвращает снятое требование: скрипт не запускался вовсе.
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def walk(node, depth=0):
    """Отдаёт (id, dict, depth) для любого вложенного словаря с recognizable id."""
    out = []
    if isinstance(node, dict):
        ident = node.get("id") or node.get("operation_id")
        if isinstance(ident, str) and (SM_ID.match(ident) or OP_ID.match(ident)):
            out.append((ident, node, depth))
        for value in node.values():
            out.extend(walk(value, depth + 1))
    elif isinstance(node, list):
        for item in node:
            out.extend(walk(item, depth))
    return out


def draft_families(path):
    """{SM-id: record} из драфта, независимо от того, вложены операции в семью или лежат рядом.

    Драфты пришли двумя разными формами: part-a вложенная, part-b плоская («12 family rows +
    36 operations»). Поэтому семья берётся из записи с id вида SM-XX, а операции собираются по
    префиксу id — и из вложенных, и из соседних строк.
    """
    data = load(path)
    families = {}
    by_prefix = {}
    for ident, node, depth in walk(data):
        if SM_ID.match(ident):
            families.setdefault(ident, node)
        if OP_ID.match(ident):
            head = ident.split(".")[0]
            tail = ident[len(head) + 1:]
            if head not in families and depth:
                continue
            by_prefix.setdefault(head, {})[ident] = (node, tail.count("."))

    for head, ops in by_prefix.items():
        fam = families.get(head)
        if fam is None:
            continue
        nested = {i for i, _, _ in walk(fam) if OP_ID.match(i)}
        attached = []
        for ident, (node, dots) in sorted(ops.items()):
            if ident in nested:
                continue  # уже вложена — не дублируем
            attached.append(node)
        if attached:
            fam.setdefault("operations", []).extend(attached)
    return families


def counts_of(record):
    """Операции = id с одной точкой, режимы/варианты = id с двумя и более.

    У part-b режимы и варианты копирования описаны вложенными записями без SM-префикса в id,
    поэтому для них добавлен подсчёт по именам контейнеров; двойного счёта нет: по ключам
    считаются только словари, чей id не распознан как операция или режим.
    """
    ops = modes = 0
    for ident, _, depth in walk(record):
        if not OP_ID.match(ident):
            continue
        tail = ident.split(".", 1)[1]
        if "." in tail:
            modes += 1
        else:
            ops += 1

    counted = {id(node) for _, node, _ in walk(record)}

    def nested_by_keys(node, keys, depth=0):
        total = 0
        if isinstance(node, dict):
            for key, value in node.items():
                if key in keys and isinstance(value, list):
                    total += sum(1 for item in value
                                 if isinstance(item, dict) and id(item) not in counted)
                elif isinstance(value, (dict, list)) and depth < 4:
                    total += nested_by_keys(value, keys, depth + 1)
        elif isinstance(node, list):
            for item in node:
                total += nested_by_keys(item, keys, depth + 1)
        return total

    modes += nested_by_keys(record, {"modes", "variants", "copy_variants"})
    return ops, modes


def clamp(node, where, downgrades):
    level = node.get("level")
    if isinstance(level, str) and RANK.get(level, 0) > RANK[CEILING]:
        downgrades.append((where, level))
        node["level"] = CEILING
        node["level_note"] = f"понижено при слиянии: исследование без исполнения (было {level})"


def merge_family(target, incoming, source, conflicts, downgrades):
    """Объединяет: evidence-поля остаются у цели, остальное дозаполняется из драфта."""
    for key, value in incoming.items():
        if key == "id":
            continue
        if key in target:
            if key in EVIDENCE_KEYS:
                if json.dumps(target[key], ensure_ascii=False, sort_keys=True) != \
                        json.dumps(value, ensure_ascii=False, sort_keys=True):
                    conflicts.append((target["id"], key, "оставлено измерение, драфт перенесён в draft"))
                    target.setdefault("draft", {})[key] = value
                continue
            conflicts.append((target["id"], key, "значение каталога сохранено"))
            continue
        target[key] = value
    target.setdefault("inventory_sources", []).append(source)
    clamp(target, target["id"], downgrades)


def main():
    dry = "--dry-run" in sys.argv
    catalog_path = os.path.join(COVERAGE, "catalog.json")
    matrix_path = os.path.join(COVERAGE, "matrix.json")
    catalog = load(catalog_path)
    matrix = load(matrix_path)

    by_id = {f["id"]: f for f in catalog["families"]}
    pending = set(catalog.get("families_pending_entry", {}).get("ids", []))
    conflicts = []
    downgrades = []
    added = []
    upgraded = []

    for path in DRAFTS:
        if not os.path.exists(path):
            print(f"пропуск: нет {path}")
            continue
        source = os.path.relpath(path, ROOT).replace("\\", "/")
        for fid, record in sorted(draft_families(path).items()):
            ops, modes = counts_of(record)
            counts = (ops, modes)
            for node_id, node, depth in walk(record):
                if depth:
                    clamp(node, f"{fid}/{node_id}", downgrades)
            if fid in by_id:
                merge_family(by_id[fid], record, source, conflicts, downgrades)
                upgraded.append((fid, *counts))
            else:
                record.setdefault("inventory_sources", []).append(source)
                catalog["families"].append(record)
                by_id[fid] = record
                added.append((fid, *counts))
            pending.discard(fid)

    total_ops = sum(c[1] for c in added + upgraded)
    total_modes = sum(c[2] for c in added + upgraded)
    catalog["families_pending_entry"]["ids"] = sorted(pending)

    # Очередь матрицы — это «семейства без строки», а не «семейства без описания»: после слияния
    # каталог раскрывает все 30 групп, но строк матрицы у них ещё нет. Два разных смысла держим в
    # двух разных полях и сверяем генератором.
    covered = {row["catalog_ref"] for row in matrix["rows"]}
    without_rows = sorted(f["id"] for f in catalog["families"] if f["id"] not in covered)
    matrix["pending_families"] = without_rows
    catalog["meta"]["inventory_merged_utc"] = "2026-09-12T00:30:00Z"

    print(f"добавлено семейств: {len(added)}; объединено с существующими: {len(upgraded)}")
    print(f"операций учтено: {total_ops}; режимов/вариантов: {total_modes}")
    for fid, ops, modes in added:
        print(f"  + {fid}: операций {ops}, режимов {modes}")
    for fid, ops, modes in upgraded:
        print(f"  ~ {fid}: из драфта {ops} операций / {modes} режимов (обогащение)")
    print(f"очередь инвентаризации после слияния: {len(pending)} {pending}")
    if downgrades:
        print(f"понижено до потолка исследования: {len(downgrades)}")
        for where, level in downgrades:
            print(f"  {where}: было {level}")
    else:
        print("понижений не потребовалось: выше metadata_found ничего не заявлено")
    print(f"конфликтов разрешено с сохранением измерения: {len(conflicts)}")
    for fid, key, note in conflicts[:25]:
        print(f"  {fid}.{key}: {note}")
    if len(conflicts) > 25:
        print(f"  ... и ещё {len(conflicts) - 25}")

    if dry:
        print("(--dry-run: файлы не изменены)")
        return 0

    with open(catalog_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(catalog, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    with open(matrix_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(matrix, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    # Слияние сознательно не приводит модель данных к каноническому виду: плоский драфт part-b
    # даёт в семье и строку-ссылку, и объект с тем же id. Это работа
    # scripts/normalize-inventory-model.py — одной функции, а не побочного эффекта слияния.
    dupes = sum(1 for f in catalog["families"]
                for o in (f.get("operations") or []) if isinstance(o, str)
                and any(isinstance(x, dict) and x.get("id") == o
                        for x in (f.get("operations") or [])))
    if dupes:
        print(f"двойных представлений операции (строка + объект): {dupes} — "
              f"нормализовать: python scripts\\normalize-inventory-model.py")
    print("catalog.json и matrix.json записаны")
    return 0


if __name__ == "__main__":
    sys.exit(main())
