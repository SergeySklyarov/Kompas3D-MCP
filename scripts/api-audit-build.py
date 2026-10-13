"""Audit step 5: build `docs/API_COMPLIANCE.json` and render `docs/API_COMPLIANCE.md` FROM it.

  python scripts/api-audit-build.py [--evidence <dir>] [--json <path>] [--md <path>] [--self-test]

The two files cannot drift apart: the Markdown is rendered from the JSON in the same run, and the
Markdown is never edited by hand - exactly like `coverage/solid-v24/matrix.md`, which is rendered by
its generator.

EVERY NUMBER COMES FROM A MEASUREMENT OF THIS RUN. `meta.date` is a build stamp taken at build time,
never a constant: a date that does not move makes a stale audit indistinguishable from a fresh one.
Placeholders are substituted from the measured data and the run REFUSES if any placeholder survives,
because a surviving `{...}` manufactures a plausible-looking false statement.

Sections the instruments do not measure yet are named in `meta.not_measured` with the reason. Silence
would read as "measured and clean".
"""

from __future__ import annotations

import argparse
import collections
import datetime
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from api_audit_common import (CATALOG, MATRIX, PROFILES_DIR, ROOT, SCHEMAS_DIR, build_identity,  # noqa: E402
                              file_sha256, load_json, route_tool_names, save_json, stamp)

JSON_OUT = os.path.join(ROOT, "docs", "API_COMPLIANCE.json")
MD_OUT = os.path.join(ROOT, "docs", "API_COMPLIANCE.md")
KOMPAS_EXE = r"D:\Programs\KOMPAS-3Dv24\Bin\KOMPAS.Exe"
INTEROP_DIR = r"D:\Programs\KOMPAS-3Dv24\Libs\PolynomLib\Bin\Client"
VOCABULARY = {
    "documentation": ["Подтверждено", "Противоречие", "Не подтверждено"],
    "implementation": ["Подтверждено", "Покрыто проверками", "Дефект", "Недостаточно проверки",
                       "Не реализовано"],
}
# Sections of the audit that the current instruments do not measure. Named with the reason, because a
# missing section and a clean section look identical to a reader.
NOT_MEASURED = {
    "report_route_check": "в текущих отчётах приёмки поле режима аргументов (`args_keys`) есть, но "
                          "отдельный сторож «отчёт прежней редакции прибора» не строился: "
                          "перевыпуск отчёта на текущей поставке делает его неприменимым",
    "com_snapshot_check": "снапшот COM-маршрутов больше не отдельный файл: инвентарь строится в "
                          "самом аудите (`api-audit-routes.py`) и сверяется по времени правки "
                          "исходников адаптера",
}


def load_evidence(evidence_dir):
    result = {}
    for name in ("wire", "routes", "docs", "claims"):
        path = os.path.join(evidence_dir, name + ".json")
        if os.path.isfile(path):
            result[name] = load_json(path)
    return result


def newest_mtime(paths):
    """Newest modification time among the inputs - files and directories alike."""
    stamps = []
    for path in paths:
        if os.path.isfile(path):
            try:
                stamps.append((os.path.getmtime(path), path))
            except OSError:
                pass
            continue
        for root, _dirs, files in os.walk(path):
            for name in files:
                try:
                    stamps.append((os.path.getmtime(os.path.join(root, name)),
                                   os.path.join(root, name)))
                except OSError:
                    pass
    return max(stamps) if stamps else (None, None)


def freshness(evidence_path, inputs):
    """Is the measurement newer than everything it describes? A stale input reads as a clean audit."""
    if not os.path.isfile(evidence_path):
        return {"fresh": None, "reason": "измерение отсутствует"}
    measured = os.path.getmtime(evidence_path)
    newest, newest_path = newest_mtime(inputs)
    if newest is None:
        return {"fresh": None, "reason": "входы не читаются"}
    try:
        newest_input = os.path.relpath(newest_path, ROOT).replace("\\", "/")
    except ValueError:
        newest_input = newest_path.replace("\\", "/")
    return {
        "fresh": measured >= newest,
        "lag_seconds": round(measured - newest, 1),
        "newest_input": newest_input,
        "reason": "свеж" if measured >= newest else "измерение старше своего входа",
    }


def families_of(catalog):
    """Families and their level; membership of the "verified"/"planned" sections is derived from the
    DATA, never from a curated list - a curated list has already called implemented families planned."""
    rows = []
    for family in catalog.get("families") or []:
        operations = family.get("operations") or []
        levels = collections.Counter(op.get("level") for op in operations
                                     if isinstance(op, dict))
        rows.append({
            "family": family.get("id"),
            "title": family.get("title"),
            "catalog_level": family.get("level"),
            "operations": len(operations),
            "operation_levels": dict(levels),
            "mcp_support": family.get("mcp_support"),
        })
    return rows


def modes_of(matrix, catalog, routes, docs_by_route):
    """Every mode/variant of the matrix, with the COM members its declared route names."""
    catalog_level = {}
    for family in catalog.get("families") or []:
        for operation in family.get("operations") or []:
            if not isinstance(operation, dict):
                continue
            catalog_level[operation.get("id")] = operation.get("level")
            for container in ("modes", "variants", "copy_variants"):
                for mode in operation.get(container) or []:
                    if isinstance(mode, dict) and mode.get("id"):
                        catalog_level[mode["id"]] = mode.get("level")
    member_index = collections.defaultdict(list)
    for route in routes:
        member_index[route["member"].lower()].append(route["route"])
    rows = []
    for row in matrix.get("rows") or []:
        text = " ".join(str(value) for key, value in row.items() if key.endswith("_route"))
        tokens = {token.lower() for token in re.findall(r"[A-Za-z_]\w+", text)}
        members = []
        for token in sorted(tokens):
            for route_name in member_index.get(token, []):
                members.append({"member": route_name,
                                "documentation": docs_by_route.get(route_name, "не проверено")})
        schemas = sorted({os.path.splitext(os.path.basename(str(value)))[0]
                          for key, value in row.items()
                          if (key.endswith("_schema") or key == "schemas")
                          for value in ([value] if isinstance(value, str) else value or [])})
        rows.append({
            "operation_id": row.get("operation_id"),
            "family": row.get("catalog_ref") or row.get("operation_id", "?").split(".")[0],
            "queue": row.get("queue"),
            "kind": row.get("kind"),
            "context": row.get("context"),
            "catalog_level": row.get("catalog_level") or catalog_level.get(row.get("operation_id")),
            "tools": sorted({name for key, value in row.items()
                             if key.endswith("_route") or key == "adapter_route"
                             for name in route_tool_names(value)} | set(schemas)),
            "schemas": [f"schemas/{name}.json" for name in schemas],
            "declared_route": row.get("adapter_route"),
            "com": members,
            "documentation": documentation_verdict(members),
            "actions": row.get("actions") or {},
            "tests": row.get("tests") or [],
        })
    return rows


def documentation_verdict(members):
    """Verdict over the COM members a mode names: a mode is confirmed only when all of them are."""
    if not members:
        return {"status": "Не подтверждено", "note": "маршрут не назвал ни одного COM-члена"}
    statuses = collections.Counter(member["documentation"] for member in members)
    if statuses.get("Подтверждено") == len(members):
        return {"status": "Подтверждено", "members": len(members)}
    if statuses.get("Противоречие"):
        return {"status": "Противоречие", "members": len(members), "counts": dict(statuses)}
    return {"status": "Не подтверждено", "members": len(members), "counts": dict(statuses)}


def findings_of(evidence, matrix, catalog, claims):
    """Findings derived from the measurements. Each names its class, severity and fix."""
    items = []

    def add(identifier, klass, severity, statement, fix, evidence_paths):
        items.append({"id": identifier, "class": klass, "severity": severity,
                      "statement": statement, "fix": fix, "evidence": evidence_paths})

    docs = evidence.get("docs") or {}
    unconfirmed = docs.get("unconfirmed") or []
    without_page = [item for item in unconfirmed
                    if "не назван на страницах своего интерфейса" in (item.get("reason") or "")]
    if without_page:
        add("F-01", "несоответствие документации (член вызывается, страницы нет)", "высокая",
            f"{len(without_page)} COM-член(ов), вызываемых адаптером, не названы на страницах своего "
            f"интерфейса: " + "; ".join(item["route"] for item in without_page[:10]),
            "перечень заказчику: либо документированный маршрут, либо исключение, названное как "
            "исключение",
            [docs.get("source")])
    broken = [item for item in unconfirmed if (item.get("reason") or "").startswith("HTTP ")]
    if broken:
        add("F-02", "дефект прибора аудита (битая ссылка)", "средняя",
            f"{len(broken)} страниц(а) справки отвечают не 200: "
            + "; ".join(f"{item['page']} ({item['reason']})" for item in broken[:5]),
            "битая ссылка не является подтверждением; страницу заменить или член назвать находкой",
            [docs.get("source")])
    object_types = docs.get("object_types") or {}
    if object_types.get("mismatched"):
        names = ", ".join(f"{item['constant']}={item['value']} при {item['documented']}"
                          for item in object_types["mismatched"][:5])
        add("F-03", "дефект документации в коде (названо имя, которого нет в таблице)", "низкая",
            f"doc-блок константы называет идентификатор, отсутствующий в документированной таблице "
            f"типов объектов: {names}",
            "назвать документированное имя; значение при этом измерено и не меняется",
            [object_types.get("documented_table")])
    counters = claims.get("counters") or {}
    for key, klass, severity, fix in (
            ("rows_verified_without_any_linked_row", "приписка (verified без связанной строки приёмки)",
             "высокая", "связать строку с приёмкой или понизить заявку"),
            ("rows_verified_contradicted_by_fail", "противоречие (verified при FAIL в связанной строке)",
             "высокая", "разобрать противоречие до снятия заявки"),
            ("rows_actions_without_required_calls", "заявка не подтверждена вызовами (отказ сторожа)",
             "средняя", "маршрут строки, посвящённая строка приёмки или понижение заявки"),
            ("rows_action_without_dedicated_row", "пробел трассируемости (действие не названо строкой)",
             "низкая", "назвать действие в имени строки или объявить маршрут строки")):
        values = counters.get(key) or []
        if values:
            add(f"F-{4 + len([item for item in items if item['id'].startswith('F-')]):02d}", klass,
                severity, f"{len(values)} строк(и): " + ", ".join(values[:12]),
                fix, [claims.get("source")])
    wire = evidence.get("wire") or {}
    without_presence = wire.get("tools_without_matrix_presence") or []
    if without_presence:
        add("F-20", "инструмент на проводе без присутствия в записях покрытия", "средняя",
            f"{len(without_presence)} инструмент(ов) не названы ни своей строкой, ни маршрутом, ни "
            f"схемой: " + ", ".join(without_presence),
            "назвать инструмент в маршруте строки, которая его исполняет, или объявить его вне "
            "покрытия режимов",
            [wire.get("source") or "docs/acceptance/evidence/"])
    return items


def completeness(evidence, matrix, catalog, claims, wire):
    docs = evidence.get("docs") or {}
    counters = claims.get("counters") or {}
    return {
        "tools_on_wire": (wire.get("counts") or {}).get("tools_on_wire"),
        "schemas_on_disk": (wire.get("counts") or {}).get("schemas_on_disk"),
        "tools_with_schema": (wire.get("counts") or {}).get("tools_on_wire", 0)
        - len((wire.get("counts") or {}).get("tools_without_schema") or []),
        "schemas_matching_wire_by_property_names": (wire.get("reconciliation") or {}).get(
            "schemas_match_wire_by_names"),
        "schemas_matching_wire_by_shape": (wire.get("reconciliation") or {}).get(
            "schemas_match_wire_by_shape"),
        "tools_without_matrix_presence": wire.get("tools_without_matrix_presence") or [],
        "presence_classes": {key: len(value) for key, value
                             in (wire.get("presence_classes") or {}).items()},
        "matrix_rows": len(matrix.get("rows") or []),
        "catalog_modes_and_variants": len(wire.get("modes") or []),
        "com_routes": (evidence.get("routes") or {}).get("route_count"),
        "com_routes_by_assembly": (evidence.get("routes") or {}).get("by_assembly"),
        "com_routes_confirmed_by_documentation": (docs.get("statuses") or {}).get("Подтверждено", 0),
        "com_routes_confirmed_over_wire": (docs.get("bases") or {}).get("wire", 0),
        "com_routes_confirmed_from_mirror": (docs.get("bases") or {}).get("mirror", 0),
        "com_routes_unconfirmed": len(docs.get("unconfirmed") or []),
        "documentation_column_complete": not (docs.get("circuit_breaker") or {}).get("open"),
        "object_type_constants_checked": len((docs.get("object_types") or {}).get("matched") or []),
        "object_type_constants_mismatched": len(
            (docs.get("object_types") or {}).get("mismatched") or []),
        "rows_claiming_verified": counters.get("rows_claiming_verified"),
        "rows_verified_without_any_linked_row": len(
            counters.get("rows_verified_without_any_linked_row") or []),
        "rows_verified_contradicted_by_fail": len(
            counters.get("rows_verified_contradicted_by_fail") or []),
        "rows_action_without_dedicated_row": len(
            counters.get("rows_action_without_dedicated_row") or []),
        "rows_actions_without_required_calls": len(
            counters.get("rows_actions_without_required_calls") or []),
        "rows_status_lowered_by_unsupported_actions": counters.get(
            "rows_status_lowered_by_unsupported_actions"),
        "acceptance_rows": claims.get("acceptance_rows"),
        "acceptance_calls_journal": claims.get("acceptance_calls_journal"),
        "acceptance_rows_with_call_window": claims.get("acceptance_rows_with_call_window"),
        "required_of_mismatches_matrix_vs_profile": len(
            (claims.get("required_of_check") or {}).get("mismatches") or []),
        "dependency_verdict_control_proved": None,
    }


def repo_relative(path):
    """Путь от корня репозитория, а не от личного каталога разработчика.

    Публикуемый аудит не должен называть личный каталог проектов: это среда разработчика, и сторож
    `verify-publish-set.py` (проверка 5a, уровень ОТКАЗ) справедливо отказывает на ней.
    Абсолютный путь остаётся только там, где он и есть предмет измерения - каталог установки вендора.
    """
    if not path:
        return path
    try:
        return os.path.relpath(path, ROOT).replace("\\", "/")
    except ValueError:
        return path.replace("\\", "/")


def build(evidence_dir):
    evidence = load_evidence(evidence_dir)
    matrix = load_json(MATRIX)
    catalog = load_json(CATALOG)
    wire = evidence.get("wire") or {}
    routes = (evidence.get("routes") or {}).get("routes") or []
    docs = evidence.get("docs") or {}
    claims = evidence.get("claims") or {}
    docs_by_route = {item["route"]: item["status"] for item in docs.get("routes") or []}

    build_id = build_identity()
    build_id["host_dll_sha256"] = (wire.get("build") or {}).get("host_dll_sha256") \
        or build_id["host_dll_sha256"]
    for key in ("dir", "host_path", "worker_path"):
        build_id[key] = repo_relative(build_id.get(key))
    document = {
        "meta": {
            "artifact": "docs/API_COMPLIANCE.json",
            "audit": "соответствие MCP официальной документации КОМПАС",
            "date": stamp(),
            "target": {
                "product": "КОМПАС-3D v24",
                "build": "24.0.0.2799",
                "install_root": r"D:\Programs\KOMPAS-3Dv24",
                "kompas_exe_sha256": file_sha256(KOMPAS_EXE),
                "interop": "Interop.Kompas6API5 + Interop.KompasAPI7 (PolynomLib/Bin/Client)",
                "interop_dir": INTEROP_DIR,
            },
            "documentation_source": {
                "url": docs.get("help_root"),
                "version": "24",
                "retrieved": datetime.datetime.now().astimezone().date().isoformat(),
                "pages_read": docs.get("interfaces_checked"),
                "routes_checked": docs.get("routes_checked"),
                "note": "страницы читались по проводу; подтверждающей считается только страница "
                        "СВОЕГО интерфейса и СВОЕГО члена",
                "basis": {"wire": (docs.get("bases") or {}).get("wire", 0),
                          "mirror": (docs.get("bases") or {}).get("mirror", 0),
                          "note": "`wire` - страница открыта по проводу (HTTP 200); `mirror` - "
                                  "прочитана из зеркала справки в репозитории, потому что провод "
                                  "отказал. Подтверждение по зеркалу названо отдельно: это "
                                  "подтверждение по копии, а не по проводу"},
                "circuit_breaker": docs.get("circuit_breaker"),
                "links": {"checked": docs.get("routes_checked"),
                          "ok": (docs.get("statuses") or {}).get("Подтверждено", 0),
                          "failed": len([item for item in docs.get("unconfirmed") or []
                                         if (item.get("reason") or "").startswith("HTTP ")]),
                          "not_verified": (docs.get("statuses") or {}).get("не проверено", 0)},
            },
            "delivery": {**build_id, "tools_count": (wire.get("counts") or {}).get("tools_on_wire"),
                         "rows_complete_run": claims.get("acceptance_rows"),
                         "failed": None,
                         "identity_source": "сборка Release чистого коммита (поставка 0.8.0 ещё не "
                                            "выпущена)"},
            "verdict_vocabulary": VOCABULARY,
            "scope": "Все опубликованные инструменты на проводе, все строки матрицы покрытия, все "
                     "режимы и варианты каталога, все COM-маршруты адаптера с установленным типом "
                     "получателя и числовые идентификаторы типов объектов, названные в коде.",
            "not_measured": NOT_MEASURED,
            "completeness": completeness(evidence, matrix, catalog, claims, wire),
        },
        "tools": wire.get("tools") or [],
        "modes": modes_of(matrix, catalog, routes, docs_by_route),
        "families": families_of(catalog),
        "com_routes": [{**route, "documentation": {
            "status": docs_by_route.get(route["route"], "не проверено"),
            "source": [item["page"] for item in docs.get("routes") or []
                       if item["route"] == route["route"] and item["status"] == "Подтверждено"],
        }} for route in routes],
        "com_snapshot_check": {
            "source": os.path.relpath(os.path.join(evidence_dir, "routes.json"), ROOT).replace("\\", "/"),
            "scanner": (evidence.get("routes") or {}).get("scanner"),
            "adapter_sources": (evidence.get("routes") or {}).get("adapter_sources"),
            **freshness(os.path.join(evidence_dir, "routes.json"),
                        [os.path.join(ROOT, "src", "KompasMcp.Api5Adapter")]),
        },
        "inventory_check": {
            "source": os.path.relpath(os.path.join(evidence_dir, "wire.json"), ROOT).replace("\\", "/"),
            "inputs": ["coverage/solid-v24/matrix.json", "coverage/solid-v24/catalog.json",
                       "schemas/"],
            **freshness(os.path.join(evidence_dir, "wire.json"),
                        [MATRIX, CATALOG, SCHEMAS_DIR]),
        },
        "reflection_access": {
            "entries": (evidence.get("routes") or {}).get("reflection_access") or [],
            "note": "обращения по ИМЕНИ через отражение: поиск по `.Член(` их не видит; попадание "
                    "имени в другой интерфейс подтверждением не считается",
        },
        "receivers_without_type": (evidence.get("routes") or {}).get("receivers_without_type") or {},
        "indexer_routes": (evidence.get("routes") or {}).get("indexer_routes") or [],
        "object_types": docs.get("object_types") or {},
        "required_of_check": claims.get("required_of_check") or {},
        "claimed_actions_check": {
            "source": claims.get("source"),
            "rows_claiming_verified": (claims.get("counters") or {}).get("rows_claiming_verified"),
            "rows_without_any_linked_row": (claims.get("counters") or {}).get(
                "rows_verified_without_any_linked_row"),
            "rows_contradicted_by_fail": (claims.get("counters") or {}).get(
                "rows_verified_contradicted_by_fail"),
            "rows_action_without_dedicated_row": (claims.get("counters") or {}).get(
                "rows_action_without_dedicated_row"),
            "rows_actions_without_required_calls": (claims.get("counters") or {}).get(
                "rows_actions_without_required_calls"),
            "guard_control": claims.get("guard_control"),
        },
        "calls_calibration": claims.get("calls_calibration") or {},
        "findings": findings_of(evidence, matrix, catalog, claims),
        "addenda": [],
    }
    return document


# ── Рендер ────────────────────────────────────────────────────────────────────────────────────────

def render(document):
    meta = document["meta"]
    completeness_ = meta["completeness"]
    lines = [
        "# Соответствие MCP официальной документации КОМПАС-3D v24",
        "",
        f"Артефакт: `docs/API_COMPLIANCE.json` (машинный) и этот файл, ПОРОЖДЁННЫЙ из него. "
        f"Вручную не правится: правится источник, потом перегенерируется.",
        "",
        f"Дата сборки аудита: {meta['date']}.",
        "",
        "## 1. Что измерено",
        "",
        f"- Целевая версия: {meta['target']['product']}, сборка {meta['target']['build']}, "
        f"`KOMPAS.Exe` sha256 `{meta['target']['kompas_exe_sha256']}`.",
        f"- Документация: {meta['documentation_source']['url']}, прочитано по проводу; "
        f"интерфейсов {meta['documentation_source']['pages_read']}, маршрутов "
        f"{meta['documentation_source']['routes_checked']}.",
        f"- Измеряемая сборка: `{meta['delivery']['kind']}`, коммит "
        f"`{meta['delivery']['source_commit']}`, дерево "
        f"{'ГРЯЗНОЕ' if meta['delivery']['source_tree_dirty'] else 'чистое'}; "
        f"`KompasMcp.Host.dll` sha256 `{meta['delivery']['host_dll_sha256']}`.",
        "",
        "## 2. Сверка полноты",
        "",
        "| Показатель | Значение |",
        "|---|---|",
    ]
    for key in ("tools_on_wire", "schemas_on_disk", "tools_with_schema",
                "schemas_matching_wire_by_property_names", "schemas_matching_wire_by_shape",
                "matrix_rows", "catalog_modes_and_variants", "com_routes",
                "com_routes_confirmed_by_documentation", "com_routes_unconfirmed",
                "object_type_constants_checked", "object_type_constants_mismatched",
                "rows_claiming_verified", "rows_verified_without_any_linked_row",
                "rows_verified_contradicted_by_fail", "rows_action_without_dedicated_row",
                "rows_actions_without_required_calls", "acceptance_rows",
                "acceptance_calls_journal", "acceptance_rows_with_call_window",
                "required_of_mismatches_matrix_vs_profile"):
        lines.append(f"| {key} | {completeness_.get(key)} |")
    lines += [
        "",
        f"Инструментов без присутствия в записях покрытия: "
        f"{len(completeness_.get('tools_without_matrix_presence') or [])}.",
        f"Классы присутствия: {completeness_.get('presence_classes')}.",
        f"COM-маршрутов по сборкам: {completeness_.get('com_routes_by_assembly')}.",
        "",
        "## 3. Инструменты",
        "",
        "| Инструмент | Свойств | Обязательных | Схема совпала по именам | по форме | Присутствие |",
        "|---|---|---|---|---|---|",
    ]
    for tool in document["tools"]:
        presence = tool.get("presence") or {}
        lines.append(
            f"| `{tool['name']}` | {tool['properties_on_wire']} | "
            f"{', '.join(tool.get('required_on_wire') or []) or '-'} | "
            f"{'да' if tool.get('schema_property_names_match_wire') else 'НЕТ'} | "
            f"{'да' if tool.get('schema_shape_matches_wire') else 'НЕТ'} | "
            f"{'+'.join(presence.get('modes') or []) or 'нет'} |")
    lines += [
        "",
        "## 4. Режимы и варианты каталога",
        "",
        f"Всего строк матрицы: {len(document['modes'])}.",
        "",
        "| Операция | Семейство | Уровень каталога | Инструменты | Маршрут | Документация |",
        "|---|---|---|---|---|---|",
    ]
    for mode in document["modes"]:
        lines.append(
            f"| `{mode['operation_id']}` | {mode.get('family')} | {mode.get('catalog_level')} | "
            f"{', '.join(mode.get('tools') or []) or '-'} | "
            f"{(mode.get('declared_route') or '-')[:60]} | {mode['documentation']['status']} |")
    lines += [
        "",
        "## 5. COM-маршруты адаптера",
        "",
        "| Маршрут | Сборка | Вид | Сигнатура | Мест | Документация |",
        "|---|---|---|---|---|---|",
    ]
    for route in document["com_routes"]:
        lines.append(f"| `{route['route']}` | {route.get('assembly')} | {route.get('kind')} | "
                     f"`{(route.get('signature') or '')[:70]}` | {len(route.get('sites') or [])} | "
                     f"{route['documentation']['status']} |")
    lines += [
        "",
        "## 6. Типы объектов",
        "",
        f"Проверено по документированной таблице: "
        f"{completeness_.get('object_type_constants_checked')}, расхождений "
        f"{completeness_.get('object_type_constants_mismatched')}, "
        f"констант без названного имени "
        f"{len((document['object_types'] or {}).get('unnamed') or [])}.",
        "",
        "## 7. Находки",
        "",
    ]
    if not document["findings"]:
        lines.append("Находок нет: измерения не дали ни одного несоответствия.")
    for finding in document["findings"]:
        lines += [f"### {finding['id']} - {finding['class']}", "",
                  f"Серьёзность: {finding['severity']}.", "",
                  finding["statement"], "",
                  f"Исправление: {finding['fix']}", ""]
    lines += ["## 8. Что осталось неподтверждённым", ""]
    for key, reason in (meta.get("not_measured") or {}).items():
        lines.append(f"- `{key}` - {reason}")
    lines.append("")
    return "\n".join(lines)


def assert_no_placeholders(text):
    leftover = re.findall(r"\{[a-z_]+\}", text)
    if leftover:
        raise SystemExit(f"в готовом файле остались подстановки: {sorted(set(leftover))}")


def self_test():
    failures = []
    checks = 0

    def check(name, condition, detail=""):
        nonlocal checks
        checks += 1
        print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
        if not condition:
            failures.append(name)

    check("подстановки в готовом тексте ловятся",
          _leftover("значение {tools_on_wire} осталось") == ["{tools_on_wire}"])
    check("чистый текст подстановок не имеет", _leftover("всё подставлено") == [])
    try:
        assert_no_placeholders("осталось {x}")
        check("сборка отказывает на оставшейся подстановке", False)
    except SystemExit:
        check("сборка отказывает на оставшейся подстановке", True)
    check("вердикт по документации складывается из членов",
          documentation_verdict([{"documentation": "Подтверждено"}])["status"] == "Подтверждено"
          and documentation_verdict([{"documentation": "не проверено"}])["status"] == "Не подтверждено"
          and documentation_verdict([])["status"] == "Не подтверждено")
    # Обе половины сторожа свежести на ОДНОЙ паре: состаренная копия входа обязана быть отвергнута,
    # омоложенная - пропущена. Сторож, проходящий только положительную половину, не отличает
    # «измерение свежо» от «сторож ничего не сравнивает».
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        measured = os.path.join(tmp, "measured.json")
        source = os.path.join(tmp, "source.txt")
        with open(measured, "w", encoding="utf-8") as handle:
            handle.write("{}")
        with open(source, "w", encoding="utf-8") as handle:
            handle.write("x")
        os.utime(measured, (1_000_000, 1_000_000))
        os.utime(source, (2_000_000, 2_000_000))
        stale = freshness(measured, [source])
        check("состаренное измерение отвергнуто", stale["fresh"] is False, str(stale))
        os.utime(measured, (3_000_000, 3_000_000))
        fresh = freshness(measured, [source])
        check("омоложенное измерение пропущено", fresh["fresh"] is True, str(fresh))
    print(f"\nСамопроверка прибора: {checks} проверок, "
          f"{len(failures)} FAIL" + (f" - {failures}" if failures else ""))
    return 1 if failures else 0


def _leftover(text):
    return re.findall(r"\{[a-z_]+\}", text)


def main():
    parser = argparse.ArgumentParser(description="Сборка docs/API_COMPLIANCE.json и рендер .md.")
    parser.add_argument("--evidence", default=None, help="каталог измерений этого аудита")
    parser.add_argument("--json", default=JSON_OUT, help="куда писать машинный аудит")
    parser.add_argument("--md", default=MD_OUT, help="куда писать человекочитаемый аудит")
    parser.add_argument("--self-test", action="store_true", help="проверить прибор, ничего не измеряя")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    from api_audit_common import evidence_dir
    directory = args.evidence or evidence_dir()
    document = build(directory)
    text = render(document)
    assert_no_placeholders(json.dumps(document, ensure_ascii=False))
    assert_no_placeholders(text)
    save_json(args.json, document)
    with open(args.md, "w", encoding="utf-8") as handle:
        handle.write(text + "\n")
    print(f"инструментов: {len(document['tools'])}, режимов: {len(document['modes'])}, "
          f"COM-маршрутов: {len(document['com_routes'])}, находок: {len(document['findings'])}")
    print(f"записано: {args.json} и {args.md}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
