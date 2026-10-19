"""Каталог текста MCP: размер опубликованного `tools/list` и общих инструкций, по частям.

  python scripts/catalog-text-baseline.py [--host <KompasMcp.Host.exe>] [--out-dir <каталог>] [--self-test]

Читает каталог тем же способом, которым его публикует Host: `initialize` + `tools/list` по stdio,
своим JSON-RPC клиентом (не библиотекой), и пишет:

  catalog-dump.json      - сырой массив `tools` (вход `scripts/contract-diff.py`);
  catalog-text-baseline.csv - строка на инструмент: описания, объект, структура, метод подсчёта;
  catalog-text-baseline.json - итоги, разбивка, топ-15 и повтор.

Разбивка не пересекается: `prose` - весь текст описаний, `structure` - тот же каталог с удалёнными
ключами `description`, `scaffolding` - разница (кавычки и ключи снятых описаний). Сумма двух первых
частей и есть весь каталог. Мера - символы `json.dumps(..., ensure_ascii=False)` с разделителями по
умолчанию: так текст считает клиент, а не экранированная форма.
History: docs/decisions/host.md#tools-list-size-guard"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from localconfig import isolate_config, local_config  # noqa: E402

DEFAULT_HOST = os.path.join(
    ROOT, "src", "KompasMcp.Host", "bin", "x64", "Release", "net10.0-windows", "KompasMcp.Host.exe")
LOG_DIR = os.path.join(ROOT, "scratch", "catalog-text-baseline")
PROTOCOL = "2025-06-18"

# A sentence ends at a period followed by whitespace. Whitespace is collapsed BEFORE the split, so a
# phrase wrapped over several source lines is one phrase and is not counted as a repetition of itself.
# Fragments shorter than 20 characters are dropped: "мм." is not a repeated instruction.
_SENTENCE = re.compile(r"(?<=\.)\s+")


def dumps(node):
    return json.dumps(node, ensure_ascii=False)


def strip_prose(node):
    """The same node with every `description` key removed, nested objects included."""
    if isinstance(node, dict):
        return {key: strip_prose(value) for key, value in node.items() if key != "description"}
    if isinstance(node, list):
        return [strip_prose(item) for item in node]
    return node


def prose_texts(node, where):
    """(place, text) for every `description` inside a node; the place names the field path."""
    found = []
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "description" and isinstance(value, str):
                found.append((where, value))
            else:
                found.extend(prose_texts(value, f"{where}.{key}"))
    elif isinstance(node, list):
        for index, item in enumerate(node):
            found.extend(prose_texts(item, f"{where}[{index}]"))
    return found


def sentences(text):
    flat = re.sub(r"\s+", " ", text or "").strip()
    return [piece.strip() for piece in _SENTENCE.split(flat) if len(piece.strip()) >= 20]


def measure_tool(tool):
    schema = tool.get("inputSchema") or {}
    top = tool.get("description") or ""
    field_chars = sum(len(text) for _, text in prose_texts(schema, "inputSchema"))
    return {
        "name": tool["name"],
        "description": len(top),
        "field_descriptions": field_chars,
        "object": len(dumps(tool)),
        "structure": len(dumps(strip_prose(tool))),
        "schema": len(dumps(schema)),
        "schema_structure": len(dumps(strip_prose(schema))),
    }


def repetition(tools):
    """Sentences written in MORE THAN ONE place, counted over tool AND field descriptions."""
    places = {}
    for tool in tools:
        for where, text in prose_texts({"tool": tool}, "tool"):
            for sentence in set(sentences(text)):
                places.setdefault(sentence, set()).add(f"{tool['name']}:{where}")
    repeated = {sentence: owners for sentence, owners in places.items() if len(owners) > 1}
    all_chars = sum(len(s) for s in places)
    repeated_chars = sum(len(s) for s in repeated)
    top = sorted(repeated.items(), key=lambda item: (-len(item[1]), -len(item[0])))
    return {
        "sentence_chars": all_chars,
        "repeated_sentences": len(repeated),
        "repeated_chars": repeated_chars,
        "repeated_share": round(repeated_chars / all_chars, 4) if all_chars else 0.0,
        "repeated_instances": sum(len(owners) - 1 for owners in repeated.values()),
        # What the duplicated copies cost in the published listing: one copy per extra place.
        "repeated_chars_instances": sum(len(s) * (len(owners) - 1) for s, owners in repeated.items()),
        "top": [{"places": len(owners), "chars": len(sentence), "sentence": sentence[:200]}
                for sentence, owners in top[:15]],
    }


def measure(tools, instructions):
    rows = [measure_tool(tool) for tool in tools]
    catalog = len(dumps(tools))
    structure = len(dumps(strip_prose(tools)))
    prose_cost = catalog - structure
    prose_top = sum(row["description"] for row in rows)
    prose_fields = sum(row["field_descriptions"] for row in rows)
    return {
        "tools": len(tools),
        "catalog_chars": catalog,
        "catalog_chars_compact": len(json.dumps(tools, ensure_ascii=False, separators=(",", ":"))),
        "catalog_chars_ascii_escaped": len(json.dumps(tools, ensure_ascii=True)),
        "catalog_bytes_utf8": len(dumps(tools).encode("utf-8")),
        "structure_chars": structure,
        "prose_chars": prose_cost,
        "prose_share": round(prose_cost / catalog, 4) if catalog else 0.0,
        "prose_tool_descriptions": prose_top,
        "prose_field_descriptions": prose_fields,
        "prose_scaffolding": prose_cost - prose_top - prose_fields,
        "instructions_chars": len(instructions),
        "instructions_bytes_utf8": len(instructions.encode("utf-8")),
        "model_visible_chars": catalog + len(instructions),
        "rows": rows,
        "repetition": repetition(tools),
    }


def self_test():
    """Asserts on synthetic catalogues: the numbers are definitions, not observations."""
    tool = {"name": "t", "title": "T", "description": "Первое предложение достаточно длинное. Второе.",
            "inputSchema": {"type": "object", "properties": {
                "a": {"type": "string", "description": "Поле а описано подробно."},
                "b": {"type": "string", "description": "Поле а описано подробно."}}},
            "annotations": {"title": "T"}}
    row = measure_tool(tool)
    assert row["description"] == len(tool["description"]), row
    assert row["field_descriptions"] == 2 * len("Поле а описано подробно."), row
    assert row["structure"] < row["object"], row
    assert "description" not in dumps(strip_prose(tool)), "prose survived the strip"
    assert strip_prose({"x": [{"description": "d", "y": 1}]}) == {"x": [{"y": 1}]}

    assert sentences("Абвгд ей жзий клмн опр. Опрс туфх цчшщ абвгд.") == [
        "Абвгд ей жзий клмн опр.", "Опрс туфх цчшщ абвгд."], sentences("Абвгд ей жзий клмн опр. Опрс туфх цчшщ абвгд.")
    assert sentences("Коротко. Совсем коротко.") == [], sentences("Коротко. Совсем коротко.")
    assert sentences("Одна фраза\nпереносом строки длинная.") == ["Одна фраза переносом строки длинная."]

    totals = measure([tool], "инструкция")
    assert totals["catalog_chars"] == len(dumps([tool])), totals
    assert totals["prose_chars"] + totals["structure_chars"] == totals["catalog_chars"], totals
    assert (totals["prose_tool_descriptions"] + totals["prose_field_descriptions"]
            + totals["prose_scaffolding"] == totals["prose_chars"]), totals
    assert totals["model_visible_chars"] == totals["catalog_chars"] + len("инструкция")

    # The same field text in three places is one repeated sentence with two extra instances.
    other = {"name": "u", "description": "Другое описание другой длины здесь.",
             "inputSchema": {"type": "object", "properties": {
                 "a": {"type": "string", "description": "Поле а описано подробно."}}}}
    repeat = measure([tool, other], "")["repetition"]
    assert repeat["repeated_sentences"] == 1, repeat
    assert repeat["repeated_instances"] == 2, repeat
    assert repeat["repeated_share"] > 0.2, repeat
    print("self-test: 13 assertions, 0 failures")


def read_wire(host):
    from api_audit_common import HostClient  # noqa: E402 - shared stdio client, not a second one
    os.makedirs(LOG_DIR, exist_ok=True)
    config = isolate_config(local_config(ROOT), LOG_DIR, "catalog-text")
    client = HostClient(host, config)
    try:
        init = client.call("initialize", {"protocolVersion": PROTOCOL, "capabilities": {},
                                          "clientInfo": {"name": "catalog-text-baseline", "version": "0"}},
                           timeout=120)
        client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        tools = client.call("tools/list", {}, timeout=180).get("tools", [])
        return init, tools
    finally:
        client.close()


def default_out_dir():
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True,
                            text=True).stdout.strip() or "unknown"
    day = datetime.date.today().strftime("%Y%m%d")
    return os.path.join(ROOT, "docs", "acceptance", "evidence", f"{day}-catalog-text-{commit}")


def write_outputs(result, tools, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "catalog-dump.json"), "w", encoding="utf-8") as handle:
        json.dump(tools, handle, ensure_ascii=False)
    header = ("name,description_chars,field_description_chars,prose_chars,object_chars,"
              "structure_chars,schema_chars,share_of_catalog,method")
    method = ("json.dumps(ensure_ascii=False) с разделителями по умолчанию; "
              "description=len(tool.description); field=сумма len() вложенных description; "
              "object=весь объект; structure=объект без ключей description")
    lines = [header]
    for row in sorted(result["rows"], key=lambda item: -item["object"]):
        lines.append(",".join(str(value) for value in (
            row["name"], row["description"], row["field_descriptions"],
            row["description"] + row["field_descriptions"], row["object"], row["structure"],
            row["schema"], round(row["object"] / result["catalog_chars"], 5),
            f'"{method}"')))
    with open(os.path.join(out_dir, "catalog-text-baseline.csv"), "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")
    with open(os.path.join(out_dir, "catalog-text-baseline.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=1)
    return out_dir


def main():
    parser = argparse.ArgumentParser(description="Размер каталога MCP и его частей.")
    parser.add_argument("--host", default=DEFAULT_HOST, help="KompasMcp.Host.exe (по умолчанию Release x64)")
    parser.add_argument("--out-dir", default=None, help="каталог доказательств (по умолчанию под docs/acceptance)")
    parser.add_argument("--self-test", action="store_true", help="только проверка определений, без Host")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return

    if not os.path.isfile(args.host):
        raise SystemExit(f"нет сборки Хоста: {args.host}")

    init, tools = read_wire(args.host)
    instructions = (init.get("instructions") or "")
    result = measure(tools, instructions)
    result["host"] = os.path.abspath(args.host)
    result["server_info"] = init.get("serverInfo") or {}
    result["instructions_chars_wire"] = len(instructions)
    out_dir = write_outputs(result, tools, os.path.abspath(args.out_dir or default_out_dir()))

    print(f"host={result['host']} server={result['server_info'].get('version')}")
    for key in ("tools", "catalog_chars", "catalog_chars_compact", "catalog_chars_ascii_escaped",
                "catalog_bytes_utf8", "structure_chars", "prose_chars", "prose_share",
                "prose_tool_descriptions", "prose_field_descriptions", "prose_scaffolding",
                "instructions_chars", "model_visible_chars"):
        print(f"{key:>28} = {result[key]}")
    print("top 15 tools by published object:")
    for row in sorted(result["rows"], key=lambda item: -item["object"])[:15]:
        print("  {name:<32} object={object:>7} description={description:>6} "
              "field_descriptions={field_descriptions:>7} structure={structure:>7}".format(**row))
    repeat = result["repetition"]
    print("repetition over all prose:")
    for key in ("sentence_chars", "repeated_sentences", "repeated_chars", "repeated_share",
                "repeated_instances"):
        print(f"{key:>28} = {repeat[key]}")
    for item in repeat["top"]:
        print(f"  places={item['places']:>3} chars={item['chars']:>5}  {item['sentence']}")
    print(f"out={out_dir}")


if __name__ == "__main__":
    main()
