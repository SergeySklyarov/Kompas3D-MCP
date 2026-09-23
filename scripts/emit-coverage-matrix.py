#!/usr/bin/env python3
"""Порождает coverage/solid-v24/matrix.md и сверяет целостность каталога, матрицы и профилей.

Знаменатели считаются раздельно (plan §6), и это главное отличие от прежней версии:

* **метрика 1** — обязательные режимы и общие зависимости профиля выпуска
  (`release-profiles/*.json`), по его фиксированному составу;
* **метрика 2** — весь нормализованный каталог P6: строкой считается режим, а если режимов у
  операции нет — сама операция. Строки, которых нет в `matrix.json`, существуют и имеют честный
  `not_started`: «реализации нет» не требует живой пробы, но и не выпадает из знаменателя.

Отдельно показываются `runtime_verified` и `mcp_verified` уровня каталога: статус исследовательской
пробы не переносится на основной MCP автоматом.

Проверки: валидные JSON, уникальность id, известые статусы и действия, существование ссылок
каталога и профиля, опубликованность схемы, обоснование `not_applicable`, доказательство у каждого
`verified`, отсутствие разъезда между очередями каталога и матрицы.

Запуск: python scripts\\emit-coverage-matrix.py
"""
import collections
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COVERAGE = os.path.join(ROOT, "coverage", "solid-v24")
PROFILE_DIR = os.path.join(COVERAGE, "release-profiles")
CATALOG_LEVELS = ("documented", "metadata_found", "runtime_verified", "mcp_implemented",
                  "mcp_verified")
MODE_CONTAINERS = ("modes", "variants", "copy_variants")

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def load(*parts):
    # `utf-8-sig`, а НЕ `utf-8`: файлы покрытия записаны с BOM (проектное соглашение), и обычный
    # `utf-8` оставляет BOM в первой строке — `json.load` падает на «Unexpected UTF-8 BOM». Дефект
    # был скрыт ровно до первой перезаписи файла из скрипта: пока BOM случайно совпадал с тем, что
    # ожидал парсер, чтение проходило. `utf-8-sig` снимает BOM, если он есть, и не мешает, если нет.
    with open(os.path.join(ROOT, *parts), encoding="utf-8-sig") as handle:
        return json.load(handle)


def catalog_rows(catalog):
    """{row_id: (family, operation_id, name, level)} — режим, либо операция без режимов."""
    rows = collections.OrderedDict()
    for family in catalog["families"]:
        for op in family.get("operations") or []:
            modes = []
            for key in MODE_CONTAINERS:
                modes += [m for m in (op.get(key) or []) if isinstance(m, dict) and m.get("id")]
            if modes:
                for mode in modes:
                    rows[mode["id"]] = (family["id"], op["id"], mode.get("name"),
                                        mode.get("level") or op.get("level"))
            else:
                rows[op["id"]] = (family["id"], op["id"], op.get("name"), op.get("level"))
    return rows


def main():
    problems = []
    catalog = load("coverage", "solid-v24", "catalog.json")
    matrix = load("coverage", "solid-v24", "matrix.json")
    profiles = collections.OrderedDict()
    if os.path.isdir(PROFILE_DIR):
        for name in sorted(os.listdir(PROFILE_DIR)):
            if name.endswith(".json"):
                profile = load("coverage", "solid-v24", "release-profiles", name)
                profiles[profile["meta"]["profile_id"]] = profile

    meta = matrix["meta"]
    actions = meta["actions"]
    statuses = set(meta["statuses"])
    families = {f["id"] for f in catalog["families"]}
    rows_index = catalog_rows(catalog)
    operation_ids = {op["id"] for f in catalog["families"] for op in (f.get("operations") or [])}
    family_ids = families

    # --- строки матрицы -------------------------------------------------------------
    by_id = collections.OrderedDict()
    seen = set()
    for row in matrix["rows"]:
        rid = row["operation_id"]
        if rid in seen:
            problems.append(f"дубликат operation_id: {rid}")
        seen.add(rid)

        ref = row.get("catalog_ref")
        if ref and ref not in families:
            problems.append(f"{rid}: catalog_ref {ref} нет в catalog.json")

        kind = row.get("kind", "evidence")
        if kind in ("profile_mode", "profile_operation", "catalog_mode", "evidence_from_catalog"):
            if rid not in rows_index and rid not in operation_ids:
                problems.append(f"{rid}: строка {kind} ссылается на несуществующую запись каталога")
        elif kind == "common_dependency":
            if row.get("catalog_refs"):
                for dep_ref in row["catalog_refs"]:
                    if dep_ref not in family_ids and dep_ref not in rows_index \
                            and dep_ref not in operation_ids:
                        problems.append(f"{rid}: catalog_refs → {dep_ref} не найдено в каталоге")
        elif rid not in rows_index and rid not in operation_ids and kind != "aux_family":
            problems.append(f"{rid}: ни строка профиля, ни запись каталога — неизвестный тип строки")

        schema = row.get("parameters_schema")
        if schema and not os.path.exists(os.path.join(ROOT, schema.replace("/", os.sep))):
            problems.append(f"{rid}: схема не опубликована: {schema}")

        unknown = set(row["actions"]) - set(actions)
        if unknown:
            problems.append(f"{rid}: неизвестные действия {sorted(unknown)}")
        missing = set(actions) - set(row["actions"])
        if missing:
            problems.append(f"{rid}: действий не хватает: {sorted(missing)}")
        for name, status in row["actions"].items():
            if status not in statuses:
                problems.append(f"{rid}: недопустимый статус {name}={status}")
        na_reasons = row.get("not_applicable_reasons") or {}
        for name, status in row["actions"].items():
            if status == "not_applicable" and name not in na_reasons:
                problems.append(f"{rid}: not_applicable без обоснования: {name}")
        # verified без ссылки на доказательство — ровно та подмена, которую запрещает план.
        if any(v == "verified" for v in row["actions"].values()):
            if not (row.get("tests") or row.get("evidence")):
                problems.append(f"{rid}: verified без теста и без ссылки на доказательство")
        # Уровень каталога не имеет права быть выше фактического состояния строки.
        # `mcp_verified`/`runtime_verified` означают «маршрут подтверждён приёмкой», но если в строке
        # ЕСТЬ заблокированное действие, строка заявляет о себе больше, чем измерено. Именно так
        # 17.09.2026 уровень `mcp_verified` уживался рядом с `edit = blocked_api`.
        # `mcp_implemented` — ниже: маршрут написан, подтверждения нет, поэтому он допустим.
        blocked = sorted(name for name, status in row["actions"].items()
                         if status.startswith("blocked_"))
        if blocked and row.get("catalog_level") in ("mcp_verified", "runtime_verified"):
            problems.append(
                f"{rid}: уровень каталога {row['catalog_level']} при заблокированных действиях "
                f"{blocked} — уровень не может быть выше фактического состояния строки")
        by_id[rid] = row

    # --- каталог целиком: строки по умолчанию ---------------------------------------
    complete = lambda r: all(v in ("verified", "not_applicable") for v in r["actions"].values())

    def synthesized(row_id, level):
        return {"operation_id": row_id, "kind": "catalog_row", "catalog_level": level,
                "actions": {a: "not_started" for a in actions}, "tests": [],
                "evidence": ["coverage/solid-v24/catalog.json"]}

    full_catalog = collections.OrderedDict()
    for row_id, (family, op_id, name, level) in rows_index.items():
        full_catalog[row_id] = by_id.get(row_id) or synthesized(row_id, level)
    extra_rows = [r for rid, r in by_id.items() if rid not in full_catalog]
    for row in extra_rows:
        full_catalog[row["operation_id"]] = row

    # --- инварианты очередей ---------------------------------------------------------
    covered = {row["catalog_ref"] for row in matrix["rows"] if row.get("catalog_ref")}
    pending = set(matrix.get("pending_families", []))
    missing_families = family_ids - covered - pending
    if missing_families:
        problems.append(f"семейства без строки матрицы и без явной очереди: {sorted(missing_families)}")
    unknown_pending = pending - family_ids
    if unknown_pending:
        problems.append(f"pending_families ссылается на неизвестные ID: {sorted(unknown_pending)}")
    derived_pending = {f["id"] for f in catalog["families"]} - covered
    if derived_pending != pending:
        problems.append("pending_families матрицы не равен множеству семейств без строк; "
                        f"разница: {sorted(derived_pending ^ pending)}")
    catalog_pending = set(catalog.get("families_pending_entry", {}).get("ids", []))
    overlap = catalog_pending & family_ids
    if overlap:
        problems.append(f"семейство одновременно раскрыто и в очереди каталога: {sorted(overlap)}")

    # --- метрика 1: профиль выпуска ---------------------------------------------------
    metric_profiles = collections.OrderedDict()
    for profile_id, profile in profiles.items():
        required = [m for m in profile.get("modes") or []
                    if m.get("priority") == "practical_required"]
        deps = profile.get("common_dependencies") or []
        closed_modes, open_modes = [], []
        for entry in required:
            row = by_id.get(entry["ref"])
            if row is not None and complete(row):
                closed_modes.append(entry["ref"])
            else:
                open_modes.append(entry["ref"])
        closed_deps, open_deps = [], []
        for dep in deps:
            row = by_id.get(dep["id"])
            if row is None:
                open_deps.append(dep["id"] + " (строки нет)")
                continue
            needed = dep.get("required_actions") or actions
            done = all(row["actions"].get(a) in ("verified", "not_applicable") for a in needed)
            (closed_deps if done else open_deps).append(dep["id"])
        per_queue = collections.OrderedDict()
        for entry in required:
            bucket = per_queue.setdefault(entry.get("queue") or "—", [0, 0])
            bucket[0] += 1
            if entry["ref"] in closed_modes:
                bucket[1] += 1
        total = len(required) + len(deps)
        done = len(closed_modes) + len(closed_deps)
        metric_profiles[profile_id] = {
            "profile_title": profile["meta"]["title"],
            "обязательных_режимов": len(required),
            "режимов_закрыто": len(closed_modes),
            "общих_зависимостей": len(deps),
            "зависимостей_закрыто": len(closed_deps),
            "профиль_закрыт": "да" if done == total else "нет",
            "готовность_процента": f"{done / total:.1%}" if total else "н/д",
            "по_очередям": {q: f"{c}/{n}" for q, (n, c) in sorted(per_queue.items())},
            "открытые_режимы": open_modes,
            "открытые_зависимости": open_deps,
        }

    # --- метрика 2: полный каталог ----------------------------------------------------
    per_status = collections.Counter()
    applicable_total = verified_total = 0
    closed_rows = 0
    started_rows = 0
    for row in full_catalog.values():
        for action in actions:
            status = row["actions"][action]
            per_status[status] += 1
            if status != "not_applicable":
                applicable_total += 1
                if status == "verified":
                    verified_total += 1
        if complete(row):
            closed_rows += 1
        if any(v != "not_started" for v in row["actions"].values()):
            started_rows += 1

    levels = collections.Counter()
    for family in catalog["families"]:
        levels[family.get("level")] += 1
    op_levels = collections.Counter()
    mode_levels = collections.Counter()
    for family in catalog["families"]:
        for op in family.get("operations") or []:
            op_levels[op.get("level")] += 1
            for key in MODE_CONTAINERS:
                for mode in op.get(key) or []:
                    if isinstance(mode, dict):
                        mode_levels[mode.get("level") or "не указан"] += 1

    # Порядок ключей = порядок разделов в matrix.md. Сводка метрики 1 идёт ПЕРВОЙ, отдельным
    # ключом `профиль <id>`; ключа-заголовка с тем же именем не существует намеренно — пустая
    # строка-заголовок оставляла в готовом файле раздел без содержимого.
    numbers = collections.OrderedDict()
    for profile_id, values in metric_profiles.items():
        numbers[f"профиль {profile_id}"] = values
    numbers.update({
        "Метрика 2 — полный нормализованный каталог P6": "",
        "строк_каталога": len(full_catalog),
        "строк_полностью_закрыто": closed_rows,
        "строк_с_каким_либо_прогрессом": started_rows,
        "семейств_в_каталоге": len(catalog["families"]),
        "семейств_без_строк": len(pending),
        "операций": catalog["meta"].get("counters", {}).get("operations", len(operation_ids)),
        "режимов_и_вариантов": catalog["meta"].get("counters", {}).get("modes_and_variants",
                                                                       len(rows_index)),
        "применимых_действий": applicable_total,
        "действий_verified": verified_total,
        "покрытие_действий": (f"{verified_total / applicable_total:.1%}"
                             if applicable_total else "н/д"),
        "распределение_статусов": dict(per_status),
        "уровни_каталога_операций": dict(op_levels),
        "уровни_каталога_режимов": dict(mode_levels),
        "осторожно": "проценты двух метрик не сводятся к одному числу; доля verified-действий — "
                     "по строкам каталога, а прогресс выпуска — по фиксированному составу профиля. "
                     "«начато» не означает «пригодно»",
    })

    warnings = []
    for row_id, row in full_catalog.items():
        # Наследовать можно только статус MCP: runtime_verified — это проба, у неё свой артефакт,
        # и отсутствие закрытой строки матрицы здесь ожиданием не является.
        if row.get("kind") == "catalog_row" and row.get("catalog_level") in \
                ("mcp_implemented", "mcp_verified"):
            warnings.append(f"{row_id}: уровень каталога {row['catalog_level']}, а в матрице "
                            f"not_started — маршрут MCP заявлен, но строка приёмки по нему "
                            f"не развёрнута")

    write_markdown(matrix, profiles, full_catalog, rows_index, numbers, warnings, pending)

    print("строк каталога:", len(full_catalog), "; строк матрицы с доказательством:",
          len(matrix["rows"]))
    for key, value in numbers.items():
        print(f"  {key}: {value}")
    if warnings:
        print("\nРАСХОЖДЕНИЯ КАТАЛОГ↔МАТРИЦА (не блокирующие):")
        for item in warnings:
            print("  -", item)
    if problems:
        print("\nНАРУШЕНИЯ ЦЕЛОСТНОСТИ:")
        for item in problems:
            print("  -", item)
        return 1
    print("\nцелостность: OK")
    return 0


def short_cell(status):
    return {"not_started": "—", "research": "исслед", "implemented": "код",
            "verified": "OK", "blocked_api": "b-api", "blocked_license": "b-лиц",
            "blocked_version": "b-верс", "not_applicable": "н/п"}.get(status, status)


def write_markdown(matrix, profiles, full_catalog, rows_index, numbers, warnings, pending):
    actions = matrix["meta"]["actions"]
    action_head = {
        "discover": "поиск", "create": "созд", "read": "чтен", "edit": "правк",
        "rebuild": "перестр", "save_reopen": "reopen", "suppress_restore": "подавл",
        "delete_dependencies": "удал", "negative_tests": "отказ",
        "geometry_validation": "геом",
    }
    missing_heads = [a for a in actions if a not in action_head]
    if missing_heads:
        raise SystemExit(f"матрица содержит действия без короткой подписи: {missing_heads}")

    out = ["# Матрица покрытия твердотельных операций v24", "",
           "> Файл порождён: `coverage/solid-v24/catalog.json` + `matrix.json` + "
           "`release-profiles/*.json` → `python scripts\\emit-coverage-matrix.py`.",
           "> Вручную не правится. Строка = режим (а если режимов нет — операция) в контексте.",
           "> Строки, которых нет в `matrix.json`, существуют как `not_started`: отсутствие "
           "реализации не требует живой пробы, но остаётся в знаменателе.", "",
           "## Метрики", "",
           "Числа двух метрик не сводятся к одному проценту (план §6). Ноль показывается нулём: "
           "закрытая возможность не отличается от незакрытой по громкости печати.", ""]
    # Заголовок метрики 1 печатается здесь, а не пустым ключом `numbers`: иначе порядок разделов
    # зависел бы от того, что сводка профиля кладётся в словарь раньше счётчиков каталога.
    # Признак наличия сводки — сам ключ `профиль <id>`, который кладёт `main`.
    if any(isinstance(v, dict) and "profile_title" in v for v in numbers.values()):
        out += ["", "## Метрика 1 — обязательные режимы профиля выпуска", ""]
    for key, value in numbers.items():
        if value == "":
            out += ["", f"### {key}", ""]
            continue
        if isinstance(value, dict) and "profile_title" in value:
            out += ["", f"### {key} — {value['profile_title']}", ""]
            for sub, sub_value in value.items():
                if sub == "profile_title":
                    continue
                if isinstance(sub_value, list) and sub_value:
                    out.append(f"- **{sub}:**")
                    out += [f"  - `{item}`" for item in sub_value]
                elif isinstance(sub_value, dict) and sub_value:
                    out.append(f"- **{sub}:** " + "; ".join(f"{k} {v}"
                                                            for k, v in sub_value.items()))
                else:
                    out.append(f"- **{sub}:** {sub_value}")
            continue
        out.append(f"- **{key}:** {value}")

    head = ("| режим/операция | семья | приоритет | очередь | уровень каталога | "
            + " | ".join(action_head[a] for a in actions) + " | проверки |")
    rule = "|---|---|---|---|---|" + "---|" * len(actions) + "|---|"

    for profile_id, profile in profiles.items():
        required = [m for m in profile.get("modes") or []
                    if m.get("priority") == "practical_required"]
        if not required:
            continue
        out += ["", f"## Метрика 1 — обязательные режимы профиля `{profile_id}`", "", head, rule]
        for entry in required:
            row = full_catalog.get(entry["ref"]) or {}
            cells = " | ".join(short_cell((row.get("actions") or {}).get(a, "not_started"))
                              for a in actions)
            out.append(f"| `{entry['ref']}` | {entry.get('family')} | {entry.get('priority')} | "
                       f"{entry.get('queue')} | {row.get('catalog_level') or entry.get('current_level')} "
                       f"| {cells} | {', '.join(row.get('tests') or []) or '—'} |")
        out += ["", f"### Общие зависимости профиля `{profile_id}`", "",
                "| зависимость | закрыто | приоритетные действия | проверки |",
                "|---|---|---|---|"]
        for dep in profile.get("common_dependencies") or []:
            row = full_catalog.get(dep["id"]) or {}
            done = bool(row) and all((row.get("actions") or {}).get(a)
                                     in ("verified", "not_applicable")
                                     for a in (dep.get("required_actions") or actions))
            out.append(f"| `{dep['id']}` | {'да' if done else 'нет'} | "
                       f"{', '.join(dep.get('required_actions') or [])} | "
                       f"{', '.join(row.get('tests') or []) or '—'} |")

    out += ["", "## Метрика 2 — весь нормализованный каталог", "",
            "Отложенные операции остаются видимыми: `later` и `next` не удаляются из матрицы, "
            "когда считается прогресс выпуска.", "", head, rule]
    priority_by_ref = {}
    for profile in profiles.values():
        for entry in profile.get("modes") or []:
            priority_by_ref[entry["ref"]] = (entry.get("priority"), entry.get("queue"),
                                             profile["meta"]["profile_id"])
    for row_id, row in full_catalog.items():
        family, op_id, name, level = rows_index.get(row_id, (row.get("catalog_ref"), "", "", ""))
        priority, queue, _profile = priority_by_ref.get(row_id, (None, None, None))
        row_priority = row.get("priority") or priority or "later"
        row_queue = row.get("queue") or queue or "—"
        cells = " | ".join(short_cell(row["actions"].get(a, "not_started")) for a in actions)
        mark = "" if row_id in rows_index else " *(вне каталога)*"
        out.append(f"| `{row_id}`{mark} | {family or '—'} | {row_priority} | {row_queue} | "
                   f"{row.get('catalog_level') or level or '—'} | {cells} | "
                   f"{', '.join(row.get('tests') or []) or '—'} |")

    out += ["", "## Ограничения и незакрытое", ""]
    out += ["Строка помечается «закрыт», только когда все её применимые действия `verified` "
            "(или `not_applicable` с обоснованием). Ограничения закрытой строки остаются "
            "обязательными к прочтению: они задают измеренную границу действия, а не отменяют "
            "закрытие. Ограничение и незакрытость — разные вещи.", ""]
    # Заблокированные действия печатаются ОТДЕЛЬНЫМ разделом: `blocked_api` в таблице показывает,
    # что строка открыта, но не показывает, ПОЧЕМУ. Без причины «b-api» неотличимо от «не успели»,
    # а это ровно та подмена, которую запрещает правило закрытия.
    blocked_rows = [(rid, row) for rid, row in full_catalog.items()
                    if any(str(v).startswith("blocked_") for v in (row.get("actions") or {}).values())]
    if blocked_rows:
        out += ["", "## Заблокированные действия (строки открыты, и вот чем)", "",
                "Причина названа ПОИМЁННО для каждого действия. Отказ ядра — это измеренный факт, "
                "но он НЕ закрывает положительное действие: строка остаётся открытой.", ""]
        for rid, row in blocked_rows:
            reasons = row.get("blocked_reasons") or {}
            for name, status in (row.get("actions") or {}).items():
                if not str(status).startswith("blocked_"):
                    continue
                reason = reasons.get(name)
                out.append(f"- `{rid}` / **{name}** = `{status}`")
                out.append(f"  - {reason}" if reason
                           else "  - ПРИЧИНА НЕ ЗАПИСАНА — это дефект записи, а не «нет причин»")

    for row_id, row in full_catalog.items():
        limitations = row.get("limitations") or []
        if limitations:
            # То же правило закрытия, что и у метрики (см. `complete` рядом с каталогом):
            # «ограничение есть» != «строка не закрыта».
            # Локаль названа `row_actions`: прежнее имя `actions` затирало список действий из
            # `matrix["meta"]`, и следующая правка, читающая `actions` после этого цикла, молча
            # получила бы словарь одной строки вместо десяти имён.
            row_actions = row.get("actions") or {}
            closed = bool(row_actions) and all(v in ("verified", "not_applicable")
                                              for v in row_actions.values())
            out.append(f"- `{row_id}` — {'закрыт целиком' if closed else 'не закрыт'}")
            out.extend(f"  - {item}" for item in limitations)

    if warnings:
        out += ["", "## Расхождения каталога и матрицы (не блокирующие)", "",
                "Уровень доказательства в каталоге и статус строки матрицы — разные вещи: "
                "проба или написанный код не делают режим закрытым в матрице.", ""]
        out += [f"- {item}" for item in warnings]

    out += ["", "## Семьи без строк матрицы (инвентаризация не завершена)", "",
            ", ".join(f"`{i}`" for i in matrix.get("pending_families", [])) or "—", "",
            "Полнота инвентаризации не является полнотой реализации (docs/05 §8), а закрытый "
            "профиль не является полным P6 (план §1).", ""]

    with open(os.path.join(COVERAGE, "matrix.md"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(out))


if __name__ == "__main__":
    sys.exit(main())
