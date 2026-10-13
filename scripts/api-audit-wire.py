"""Audit step 1 and 3: what the delivery publishes on the wire, and how the records cover it.

  python scripts/api-audit-wire.py [--host <KompasMcp.Host.exe>] [--out <json>] [--self-test]

Reads the audited build over stdio (`initialize` + `tools/list`), hashes its files, reads
`schemas/*.json`, the coverage matrix and the catalog, and reconciles them:

  * every tool on the wire has a schema, and the schema matches the wire by property names AND shape;
  * every schema on disk is a tool on the wire (a schema without a tool is a promise nobody keeps);
  * every tool is PRESENT in the coverage records - as a row's own `tool`, or as the tool of a
    declared route (`*_route`, `adapter_route`), in either of the two shapes a route takes.

NOT counted as presence: the catalog's `mcp_support`. That field is a statement of intent, and its
dict form carries `level: "none"` with `notes` that EXPLAIN WHAT IS MISSING - reading tool names out
of `notes` would report the catalog as declaring support for a tool it denies.

`--self-test` runs the presence rules on synthetic records with both halves: a tool present as a
declared route must be counted, and the same tool with its route field stripped in a copy of the
records must not be.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from api_audit_common import (ADAPTER_SRC, CATALOG, MATRIX, SCHEMAS_DIR, build_identity,  # noqa: E402
                              declared_route_tools, default_out, load_json, property_names,
                              read_wire, route_tool_names, save_json, schema_shape, stamp)

# Синтетические имена самопроверки несут настоящий префикс инструментов: правило чтения маршрутов
# отбирает имена по нему, и подставное имя без префикса проверяло бы не прибор, а свою выдумку.
SELF_TEST_PREFIX = "kompas_zz_selftest_"


def catalog_support(catalog):
    """(tool names the catalog's `mcp_support` names, number of unread `notes`).

    The field has TWO shapes and the second one may mean ABSENCE: a string (`"none"`, or a tool name)
    and a dict `{"level": ..., "notes": ...}` where every instance carries `level: "none"` and the
    notes explain what is missing. The string form is read whole; the dict form is read by `level`
    ONLY, never by `notes`. The count of unread notes is returned so that silence cannot be read as
    "everything was read".
    """
    mentions = {}
    notes_not_read = 0

    def walk(node):
        nonlocal notes_not_read
        if isinstance(node, dict):
            value = node.get("mcp_support")
            if isinstance(value, str):
                token = value.strip()
                if token.startswith("kompas_") and " " not in token:
                    mentions[token] = "string"
            elif isinstance(value, dict):
                level = value.get("level")
                if isinstance(level, str) and level.startswith("kompas_"):
                    mentions[level] = "level"
                if value.get("notes"):
                    notes_not_read += 1
            for item in node.values():
                walk(item)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(catalog)
    return mentions, notes_not_read


def schema_field_tools(row):
    """Tools a row declares by naming their SCHEMA (`parameters_schema`, `parameter_schema`, `schemas`).

    A row that names `schemas/kompas_boolean.json` declares the tool that serves it even when it
    declares no route: the schema IS the contract of that tool. The field travels under three names
    in the current records, so it is read by NAME PATTERN, never from a hand-written list.
    """
    names = set()

    def take(value):
        if isinstance(value, str):
            base = os.path.basename(value.strip())
            if base.endswith(".json") and base[:-5].startswith("kompas_"):
                names.add(base[:-5])
        elif isinstance(value, list):
            for item in value:
                take(item)
        elif isinstance(value, dict):
            for item in value.values():
                take(item)

    for key, value in row.items():
        if key == "schema" or key == "schemas" or key.endswith("_schema"):
            take(value)
    return names


def presence(tools, rows, support):
    """`{tool: presence record}` - how each published tool is present in the coverage records.

    THREE presence modes, and a counter that measures fewer of them lies. Measured 21.09.2026: a
    counter reading only the row's own `tool` field reported six uncovered tools where three were
    genuinely uncovered; the current records carry no `tool` field at all, so a counter reading only
    that field would report all 90.

      * `own_row`      - a row's own `tool` field names it;
      * `route_field`  - a row's declared route names it (structured `*_route`, or `adapter_route` text);
      * `schema_field` - a row names the tool's schema, which declares the tool that serves the mode.

    NOT a mode: the catalog's `mcp_support`. That field is a statement of intent, and its dict form
    carries `level: "none"` with notes that explain what is missing.
    """
    result = {}
    for tool in tools:
        record = {"own_row": [], "route_field": [], "schema_field": [], "mcp_support": support.get(tool)}
        for row in rows:
            operation = row.get("operation_id") or row.get("id") or "?"
            if row.get("tool") == tool:
                record["own_row"].append(operation)
            for key, value in row.items():
                if (key.endswith("_route") or key == "adapter_route") \
                        and tool in route_tool_names(value):
                    record["route_field"].append(f"{operation}:{key}")
            if tool in schema_field_tools(row):
                record["schema_field"].append(operation)
        record["present"] = bool(record["own_row"] or record["route_field"] or record["schema_field"])
        record["modes"] = [mode for mode in ("own_row", "route_field", "schema_field") if record[mode]]
        result[tool] = record
    return result


def presence_classes(presence_map):
    """Non-overlapping classes of presence, summing to the whole count.

    A bare counter reads as the wrong claim ("rows without a schema link: 25" reads as "25 rows
    without evidence"). The classes are asserted to sum to the number of tools.
    """
    classes = {}
    for tool, record in presence_map.items():
        key = "+".join(record["modes"]) if record["modes"] else "absent"
        classes.setdefault(key, []).append(tool)
    total = sum(len(items) for items in classes.values())
    assert total == len(presence_map), (total, len(presence_map))
    return {key: sorted(items) for key, items in sorted(classes.items())}


def only_in(rows, tools, mode):
    """Tools whose ONLY presence is the named mode - the half a widened reading must be checked on."""
    presence_map = presence(tools, rows, {})
    others = {"own_row", "route_field", "schema_field"} - {mode}
    return sorted(tool for tool, record in presence_map.items()
                  if record[mode] and not any(record[key] for key in others))


def reconcile(tools, schemas, rows):
    """Counts that must agree, each with the reason it is allowed to differ."""
    wire = sorted(tool["name"] for tool in tools)
    on_disk = sorted(os.path.splitext(name)[0] for name in schemas)
    return {
        "tools_on_wire": len(wire),
        "schemas_on_disk": len(on_disk),
        "matrix_rows": len(rows),
        "tools_without_schema": sorted(set(wire) - set(on_disk)),
        "schemas_without_tool": sorted(set(on_disk) - set(wire)),
    }


def schema_comparison(tools, schemas_dir):
    """Per tool: the wire schema against `schemas/<tool>.json`, by names and by shape."""
    rows = []
    for tool in sorted(tools, key=lambda item: item["name"]):
        name = tool["name"]
        wire_schema = tool.get("inputSchema") or {}
        path = os.path.join(schemas_dir, name + ".json")
        row = {
            "name": name,
            "description_len": len(tool.get("description") or ""),
            "properties_on_wire": len(property_names(wire_schema)),
            "required_on_wire": list(wire_schema.get("required") or []),
            "additional_properties": wire_schema.get("additionalProperties"),
            "schema_file": os.path.relpath(path, os.path.dirname(schemas_dir)).replace("\\", "/"),
            "schema_present": os.path.isfile(path),
        }
        if row["schema_present"]:
            file_schema = load_json(path)
            row["schema_property_names_match_wire"] = (
                property_names(file_schema) == property_names(wire_schema))
            row["schema_shape_matches_wire"] = (
                schema_shape(file_schema) == schema_shape(wire_schema))
        else:
            row["schema_property_names_match_wire"] = False
            row["schema_shape_matches_wire"] = False
        rows.append(row)
    return rows


def collect(host=None):
    """Everything this instrument measures, as one document."""
    server_info, tools = read_wire(host=host)
    schemas = sorted(name for name in os.listdir(SCHEMAS_DIR) if name.endswith(".json"))
    matrix = load_json(MATRIX)
    catalog = load_json(CATALOG)
    rows = matrix.get("rows") or []
    support, notes_not_read = catalog_support(catalog)
    tools_rows = schema_comparison(tools, SCHEMAS_DIR)
    presence_map = presence([tool["name"] for tool in tools], rows, support)
    counts = reconcile(tools, schemas, rows)
    without_presence = sorted(name for name, record in presence_map.items()
                              if not record["present"])
    return {
        "instrument": "api-audit-wire",
        "date": stamp(),
        "build": build_identity(host=host),
        "server_info": server_info,
        "counts": counts,
        "catalog_support_notes_not_read": notes_not_read,
        "catalog_support_mentions": sorted(support),
        "tools": [{**row, "presence": presence_map[row["name"]]} for row in tools_rows],
        "tools_without_matrix_presence": without_presence,
        "presence_classes": presence_classes(presence_map),
        "tools_only_in_declared_route": only_in(rows, [t["name"] for t in tools], "route_field"),
        "tools_only_in_schema_field": only_in(rows, [t["name"] for t in tools], "schema_field"),
        "modes": catalog_modes(catalog, presence_map),
        "reconciliation": {
            "tools_equal_schemas": counts["tools_on_wire"] == counts["schemas_on_disk"],
            "schemas_match_wire_by_names": sum(
                1 for row in tools_rows if row.get("schema_property_names_match_wire")),
            "schemas_match_wire_by_shape": sum(
                1 for row in tools_rows if row.get("schema_shape_matches_wire")),
        },
    }


def catalog_modes(catalog, presence_map):
    """Every catalog mode/variant with the tool that serves it, when one is named.

    The catalog names MCP tools in prose (`mcp_support`, `route`, `next_step`); the tool of a mode is
    recovered from the family and the operation it belongs to. A mode with no named tool is reported
    as such rather than dropped - the audit's scope statement counts modes.
    """
    rows = []
    for family in catalog.get("families") or []:
        for operation in family.get("operations") or []:
            if not isinstance(operation, dict):
                continue
            for container in ("modes", "variants", "copy_variants"):
                for mode in operation.get(container) or []:
                    if isinstance(mode, dict):
                        mode_id = mode.get("id") or f"{operation.get('id')}.{container}"
                        level = mode.get("level")
                    else:
                        mode_id = f"{operation.get('id')}.{container}[{mode}]"
                        level = None
                    rows.append({"mode_id": mode_id, "family": family.get("id"),
                                 "operation_id": operation.get("id"),
                                 "container": container, "level": level})
    return rows


def self_test():
    """Both halves of every presence rule, on synthetic records."""
    failures = []
    checks = 0

    def check(name, condition, detail=""):
        nonlocal checks
        checks += 1
        print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
        if not condition:
            failures.append(name)

    own = SELF_TEST_PREFIX + "own"
    routed = SELF_TEST_PREFIX + "routed"
    nowhere = SELF_TEST_PREFIX + "nowhere"
    only_support = SELF_TEST_PREFIX + "support"
    denied = SELF_TEST_PREFIX + "denied"

    rows = [
        {"operation_id": "T1", "tool": own},
        {"operation_id": "T2", "read_route": {"mode": "x",
                                              "routes": [{"tool": routed, "require_args": []}]}},
    ]
    support = {only_support: "string", denied: "level"}
    result = presence([own, routed, nowhere, only_support, denied], rows, support)

    check("инструмент со СВОЕЙ строкой считается присутствующим", result[own]["present"],
          str(result[own]))
    check("инструмент объявленного маршрута считается присутствующим", result[routed]["present"],
          str(result[routed]))
    check("инструмент, названный только в mcp_support, НЕ считается присутствующим",
          not result[only_support]["present"], str(result[only_support]))
    check("инструмент, названный только в перечне отсутствия, НЕ считается присутствующим",
          not result[denied]["present"], str(result[denied]))
    check("инструмент, не названный нигде, НЕ считается присутствующим", not result[nowhere]["present"])

    # Отрицательный контроль ОБРАТНОГО направления: у копии записей объявленный маршрут СНЯТ, и тот же
    # инструмент обязан выпасть из присутствия. Без этой половины счётчик может читать поле строки и
    # не читать маршрут - снаружи это неотличимо.
    stripped = [{"operation_id": "T2"}]
    stripped_result = presence([routed], stripped, {})
    check("тот же инструмент со снятым маршрутом выпадает из присутствия",
          not stripped_result[routed]["present"], str(stripped_result[routed]))

    # Два вида записи `mcp_support`: строка читается ЦЕЛИКОМ, словарь - только по `level`; имена в
    # `notes` не читаются, а их число называется.
    catalog = {"families": [{"operations": [
        {"id": "A", "mcp_support": only_support},
        {"id": "B", "mcp_support": {"level": "none",
                                    "notes": f"нет {denied} в ToolCatalog.cs"}}]}]}
    mentions, notes_not_read = catalog_support(catalog)
    check("строковая форма mcp_support читается целиком", list(mentions) == [only_support],
          str(mentions))
    check("имена из notes не читаются, но их число названо", notes_not_read == 1,
          f"notes_not_read={notes_not_read}")

    # Маршрут в СТРОКОВОЙ форме (adapter_route) обязан читаться так же, как в структурной.
    text_rows = [{"operation_id": "T3",
                  "adapter_route": f"API5 o3d_baseExtrusion(24) + {SELF_TEST_PREFIX}text(...)"}]
    text_result = presence([SELF_TEST_PREFIX + "text"], text_rows, {})
    check("маршрут строковой формы читается", text_result[SELF_TEST_PREFIX + "text"]["present"])

    # Третий вид присутствия - названная СХЕМА. Обе половины: строка, называющая схему, считается
    # присутствием, а та же строка со снятым полем схемы - нет.
    schema_rows = [{"operation_id": "T4",
                    "parameters_schema": f"schemas/{SELF_TEST_PREFIX}schema.json"}]
    schema_result = presence([SELF_TEST_PREFIX + "schema"], schema_rows, {})
    check("инструмент, названный схемой строки, считается присутствующим",
          schema_result[SELF_TEST_PREFIX + "schema"]["present"])
    stripped_schema = presence([SELF_TEST_PREFIX + "schema"], [{"operation_id": "T4"}], {})
    check("тот же инструмент со снятой схемой выпадает из присутствия",
          not stripped_schema[SELF_TEST_PREFIX + "schema"]["present"])
    classes = presence_classes(schema_result)
    check("классы присутствия не пересекаются и дают целое",
          sum(len(items) for items in classes.values()) == 1 and list(classes) == ["schema_field"],
          str({key: len(items) for key, items in classes.items()}))

    print(f"\nСамопроверка прибора: {checks} проверок, "
          f"{len(failures)} FAIL" + (f" - {failures}" if failures else ""))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Инвентарь провода и сверка со схемами и реестром.")
    parser.add_argument("--host", default=None, help="KompasMcp.Host.exe (по умолчанию Release x64)")
    parser.add_argument("--out", default=None, help="куда записать измерение (JSON)")
    parser.add_argument("--self-test", action="store_true", help="проверить прибор, ничего не измеряя")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    result = collect(host=args.host)
    out = args.out or default_out("wire")
    save_json(out, result)
    counts = result["counts"]
    print(f"Хост: {result['build']['host_path']}")
    print(f"штамп сборки: {result['server_info'].get('version')}")
    print(f"инструментов на проводе: {counts['tools_on_wire']}, схем на диске: "
          f"{counts['schemas_on_disk']}, строк матрицы: {counts['matrix_rows']}")
    print(f"без схемы: {counts['tools_without_schema']}")
    print(f"схем без инструмента: {counts['schemas_without_tool']}")
    print(f"имён не совпало: {counts['tools_on_wire'] - result['reconciliation']['schemas_match_wire_by_names']}")
    print(f"форм не совпало: {counts['tools_on_wire'] - result['reconciliation']['schemas_match_wire_by_shape']}")
    print(f"без присутствия в записях покрытия: {result['tools_without_matrix_presence']}")
    print(f"только в объявленном маршруте: {result['tools_only_in_declared_route']}")
    print(f"режимов и вариантов каталога: {len(result['modes'])}")
    print(f"записано: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
