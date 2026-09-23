#!/usr/bin/env python3
"""Нормализует модель данных coverage/solid-v24/catalog.json (шаг A.2 плана).

Что чинится и почему это дефект данных, а не косметики:

1. **Строка-ссылка и объект с одним id — одна операция.** Слияние драфтов (part-b пришёл
   плоским: `families[*].operations` был списком имён, а сами операции лежали рядом) оставило
   в семье и `"SM-18.grid"`, и объект с тем же id. 36 таких пар. Пока `operations` — смешанный
   список, любое «число операций» зависит от того, считает ли скрипт строки.
2. **Канонический id.** SM-02 хранит операции голыми именами (`base_extrusion`), остальной
   каталог — с префиксом семьи. Прежняя форма сохраняется в `aliases`, чтобы ссылки в отчётах
   не осиротели.
3. **Режимы:** часть-a пишет ключ `id`, часть-b — `mode_id`. Генератор матрицы обязан видеть
   одно имя, иначе режимы молча считаются пустыми.
4. **Счётчики** пишутся в `meta.counters` как данные и сверяются этим же скриптом: прежде
   «59 операций» было получено подсчётом только объектов, то есть три операции SM-02 в знаменатель
   не попали, а 36 двойных представлений одной операции — нет.

Скрипт идемпотентен: повторный запуск ничего не меняет и печатает «изменений нет».

Запуск: python scripts\\normalize-inventory-model.py [--dry-run]
"""
import json
import os
import sys
from collections import OrderedDict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = os.path.join(ROOT, "coverage", "solid-v24", "catalog.json")
MODE_CONTAINERS = ("modes", "variants", "copy_variants")
LEVELS = ("documented", "metadata_found", "runtime_verified", "mcp_implemented", "mcp_verified")

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def load():
    with open(CATALOG, encoding="utf-8") as handle:
        return json.load(handle, object_pairs_hook=OrderedDict)


def canonical_id(family_id, raw):
    """`base_extrusion` → `SM-02.base_extrusion`; уже канонический остаётся собой."""
    if raw.startswith(family_id + "."):
        return raw
    return family_id + "." + raw


def normalize_operation(family_id, op, report):
    """Режимы: mode_id → id, префикс операции обязательн; возвращает число режимов."""
    prefix = op["id"]
    count = 0
    for key in MODE_CONTAINERS:
        items = op.get(key)
        if not isinstance(items, list):
            continue
        for item in items:
            if not isinstance(item, dict):
                # Строковый режим — тоже ссылка, но без источника и уровня: считаем, не молчим.
                report["modes_string_only"] += 1
                count += 1
                continue
            raw = item.get("id") or item.get("mode_id")
            if not raw:
                report["modes_without_id"].append(f"{prefix}.{key}")
                continue
            if "mode_id" in item:
                item["id"] = raw
                del item["mode_id"]
                report["modes_mode_id_renamed"] += 1
            full = raw if raw.startswith(prefix + ".") else prefix + "." + raw
            if full != raw:
                report["modes_id_prefixed"] += 1
                item["id"] = full
            count += 1
    return count


def normalize_family(family, report):
    family_id = family["id"]
    raw_ops = family.get("operations") or []
    objects = OrderedDict()
    for op in raw_ops:
        if isinstance(op, dict):
            if not op.get("id"):
                report["operations_without_id"].append(family_id)
                continue
            op_id = canonical_id(family_id, op["id"])
            if op_id != op["id"]:
                op.setdefault("aliases", []).append(op["id"])
                op["id"] = op_id
                report["operations_reprefixed"] += 1
            if op_id in objects:
                report["collisions"].append(f"{op_id} (объект на объект)")
            objects[op_id] = op
        else:
            report["string_refs"] += 1

    for op in objects.values():
        op.setdefault("aliases", [])

    # Строковые ссылки, для которых объекта нет, — не «ещё одна операция», а незаполненная запись.
    for op in raw_ops:
        if isinstance(op, str):
            op_id = canonical_id(family_id, op)
            if op_id in objects:
                aliases = objects[op_id]["aliases"]
                # Само себя в aliases не записываем: форма записающего драфта была уже канонической.
                if op != op_id and op not in aliases:
                    aliases.append(op)
                continue
            if op.startswith(family_id + "."):
                report["dangling_refs"].append(op_id)
                continue
            stub = OrderedDict()
            stub["id"] = op_id
            stub["name"] = op
            stub["level_note"] = ("запись создана из строки-ссылки при нормализации модели "
                                  "каталога: уровня доказательства здесь нет, пока операция "
                                  "не описана и не измерена")
            stub["sources_of_truth"] = list(family.get("inventory_sources") or
                                            ["docs/05_SOLID_MODELING_FULL_COVERAGE.md"])
            objects[op_id] = stub
            report["stubs_created"].append(op_id)

    # Порядок записей сохраняется (он из источника), добавляются только новые: перестановка
    # существующих строк создала бы шум в diff без изменения данных.
    ordered = objects
    for op_id, op in list(ordered.items()):
        if not op.get("id", "").startswith(family_id + "."):
            report["bad_prefix"].append(op_id)
        level = op.get("level")
        if level is None:
            report["unfilled_operations"].append(op_id)
        elif level not in LEVELS:
            report["unknown_levels"].append(f"{op_id}:{level}")
        normalize_operation(family_id, op, report)
        if op.get("aliases"):
            # Ссылка, совпадающая с каноническим id, aliases не является.
            op["aliases"] = [a for a in op["aliases"] if a != op_id]
        if not op.get("aliases"):
            op.pop("aliases", None)

    if raw_ops:
        family["operations"] = list(ordered.values())
    return len(ordered)


def counters(catalog):
    ops = set()
    modes = set()
    raw_strings = 0
    modes_without_level = 0
    bad_levels = []
    families = [f["id"] for f in catalog["families"]]
    for family in catalog["families"]:
        if family.get("level") not in LEVELS:
            bad_levels.append("%s (семья)" % family["id"])
    for family in catalog["families"]:
        for op in family.get("operations") or []:
            if not isinstance(op, dict):
                raw_strings += 1
                continue
            ops.add(op["id"])
            if op.get("level") not in LEVELS:
                bad_levels.append("%s (операция)" % op["id"])
            for key in MODE_CONTAINERS:
                for index, item in enumerate(op.get(key) or []):
                    if isinstance(item, dict) and item.get("id"):
                        modes.add(item["id"])
                        if item.get("level") not in LEVELS:
                            # Отсутствие уровня — это «не описано», а не «не поддержано».
                            modes_without_level += 1
                    else:
                        # У строкового режима нет ни id, ни уровня — он всё равно занимает место
                        # в инвентаре и обязан попасть в знаменатель, а не исчезнуть из счёта.
                        modes.add(f"{op['id']}.{key}[{index}]")
    dup = ops & modes
    return {
        "families": len(families),
        "operations": len(ops),
        "operations_as_bare_strings": raw_strings,
        "modes_and_variants": len(modes),
        "operations_without_level": sum(
            1 for f in catalog["families"] for o in (f.get("operations") or [])
            if isinstance(o, dict) and not o.get("level")),
        "modes_without_level": modes_without_level,
        "levels_out_of_vocabulary": sorted(set(bad_levels)),
        "id_collision_operation_and_mode": sorted(dup),
    }


def main():
    dry = "--dry-run" in sys.argv
    catalog = load()
    before = counters(catalog)
    report = {
        "string_refs": 0, "operations_reprefixed": 0, "modes_mode_id_renamed": 0,
        "modes_id_prefixed": 0, "modes_string_only": 0,
        "collisions": [], "dangling_refs": [], "stubs_created": [], "bad_prefix": [],
        "unfilled_operations": [], "unknown_levels": [], "modes_without_id": [],
        "operations_without_id": [],
    }
    total_ops = 0
    for family in catalog["families"]:
        total_ops += normalize_family(family, report)
        family_level = family.get("level")
        if family_level not in LEVELS:
            report["unknown_levels"].append("%s (семья):%s" % (family["id"], family_level))

    after = counters(catalog)
    catalog["meta"]["schema_version"] = "1.1"
    catalog["meta"]["model_rules"] = {
        "operations": "operations — всегда объекты; строковых ссылок в списке нет. "
                      "Прежняя форма записи хранится в aliases той же операции.",
        "operation_id": f"{{семья}}.{{операция}}, единый префикс для всего каталога",
        "modes": "режимы и варианты копирования лежат в modes/variants/copy_variants и имеют id "
                 "вида {{семья}}.{{операция}}.{{режим}}; ключ mode_id выведен в 1.1",
        "counters": "meta.counters — машинные числа, их сверяет scripts\\normalize-inventory-model.py; "
                    "в прозе числа не являются источником",
        "level": "отсутствие уровня у записи означает «не описано», а не «не поддержано» и не "
                 "«verified»",
    }
    catalog["meta"]["counters"] = after
    if "counters_before_normalization" not in catalog["meta"]:
        # Снимок «до» — исторический, он пишется один раз при миграции на 1.1 и не должен
        # поползти вверх при повторных прогонах, иначе идемпотентность теряется.
        catalog["meta"]["counters_before_normalization"] = dict(before, **{
            "note": "до 1.1 операции считались только по объектам, поэтому три операции SM-02, "
                    "описанные строками, в знаменатель не попадали, а 36 строк-ссылок дублировали "
                    "уже описанные операции",
        })

    payload = json.dumps(catalog, ensure_ascii=False, indent=2) + "\n"
    changed = payload != open(CATALOG, encoding="utf-8").read()

    print(f"операций после нормализации: {total_ops}")
    print(f"до: {before}")
    print(f"после: {after}")
    for key in ("string_refs", "operations_reprefixed", "modes_mode_id_renamed",
                "modes_id_prefixed", "modes_string_only"):
        print(f"  {key}: {report[key]}")
    for key in ("collisions", "dangling_refs", "bad_prefix", "unknown_levels",
                "modes_without_id", "operations_without_id"):
        if report[key]:
            print(f"  ПРОБЛЕМЫ {key}: {report[key]}")
    if after["levels_out_of_vocabulary"]:
        print("  ПРОБЛЕМЫ уровень вне словаря: %s" % after["levels_out_of_vocabulary"])
    if report["stubs_created"]:
        print("  созданы незаполненные записи (заполняются по мере сопоставления с выпуском):")
        for item in report["stubs_created"]:
            print(f"    - {item}")
    if report["unfilled_operations"]:
        print(f"  операций без уровня доказательства: {len(report['unfilled_operations'])}")
        for item in report["unfilled_operations"]:
            print(f"    - {item}")

    if dry:
        print("(--dry-run: catalog.json не изменён)")
        return 0
    if not changed:
        print("изменений нет: каталог уже в нормализованной модели")
        return 0
    with open(CATALOG, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(payload)
    print("catalog.json записан (schema_version 1.1)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
