"""Структурная сверка контракта между двумя дампами `tools/list`.

  python scripts/contract-diff.py <before.json> <after.json> [tool ...]

Дампы снимает `scripts/tools-list-size.py --dump`. Без перечня инструментов сверяются все из
<before.json>. Сверяется то, на что клиент ДЕЙСТВУЕТ и что прозой не восстановить:
  - пути полей входной схемы и списки `required` - не должны меняться вовсе;
  - наборы значений `enum` (режимы, которые клиент может послать);
  - коды вида ERROR_CODE в описаниях инструмента и полей (отказы, которые клиент должен знать);
  - размеры описания инструмента и описаний полей - для справки.

Код возврата 0 - структурных ломок нет; 1 - есть (изменён набор полей, `required`, пропал режим
или код отказа). Нужен при сокращении описаний: правка текста не должна задеть контракт.
History: docs/decisions/host.md#tools-list-size-guard"""

from __future__ import annotations

import json
import pathlib
import re
import sys

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

CODE = re.compile(r"\b[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)+\b")


def load(path: str) -> dict:
    return {tool["name"]: tool for tool in json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))}


def objects(schema, prefix="") -> dict[str, tuple[set[str], list[str]]]:
    """object path -> (its property names, its sorted required list); recurses through
    items/additionalProperties/anyOf/oneOf/allOf. `$defs` is skipped: a definition counts where it is used."""
    found: dict[str, tuple[set[str], list[str]]] = {}
    if not isinstance(schema, dict):
        return found
    props = schema.get("properties")
    if isinstance(props, dict):
        found[prefix or "/"] = (set(props), sorted(schema.get("required") or []))
        for name, child in props.items():
            found.update(objects(child, f"{prefix}/{name}"))
    for key in ("items", "additionalProperties"):
        if key in schema:
            found.update(objects(schema[key], prefix + "[]"))
    for key in ("anyOf", "oneOf", "allOf"):
        for index, branch in enumerate(schema.get(key) or []):
            found.update(objects(branch, f"{prefix}<{key}{index}>"))
    return found


def properties(found: dict[str, tuple[set[str], list[str]]]) -> set[str]:
    return {f"{path.rstrip('/')}/{name}" for path, (names, _) in found.items() for name in names}


def enums(schema) -> set[str]:
    found: set[str] = set()

    def walk(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "enum" and isinstance(value, list):
                    found.update(str(item) for item in value)
                else:
                    walk(value)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(schema)
    return found


def descriptions(tool: dict) -> list[str]:
    found = [tool.get("description") or ""]

    def walk(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "description" and isinstance(value, str):
                    found.append(value)
                else:
                    walk(value)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(tool.get("inputSchema") or {})
    return found


def codes(tool: dict) -> set[str]:
    found: set[str] = set()
    for text in descriptions(tool):
        found.update(CODE.findall(text))
    return found


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    before, after = load(sys.argv[1]), load(sys.argv[2])
    names = sys.argv[3:] or sorted(before)
    broken = 0

    for name in names:
        if name not in after:
            print(f"{name}: ИСЧЕЗ ИЗ КАТАЛОГА")
            broken += 1
            continue
        old, new = before[name], after[name]
        problems = []

        old_objects, new_objects = objects(old.get("inputSchema") or {}), objects(new.get("inputSchema") or {})
        old_props, new_props = properties(old_objects), properties(new_objects)
        if old_props != new_props:
            problems.append("поля: -" + ",-".join(sorted(old_props - new_props))
                            + " +" + ",+".join(sorted(new_props - old_props)))
        for path in sorted(set(old_objects) & set(new_objects)):
            old_required, new_required = old_objects[path][1], new_objects[path][1]
            if old_required != new_required:
                problems.append(f"required у {path}: {old_required} -> {new_required}")

        lost_enums = enums(old.get("inputSchema") or {}) - enums(new.get("inputSchema") or {})
        if lost_enums:
            problems.append("режимы enum пропали: " + ", ".join(sorted(lost_enums)))

        lost_codes = codes(old) - codes(new)
        if lost_codes:
            problems.append("коды отказов пропали: " + ", ".join(sorted(lost_codes)))

        old_text, new_text = descriptions(old), descriptions(new)
        sizes = (f"description {len(old_text[0])} -> {len(new_text[0])}, "
                 f"fields {sum(len(t) for t in old_text[1:])} -> {sum(len(t) for t in new_text[1:])}")
        status = "OK  " if not problems else "СТОП"
        print(f"{status} {name}: {sizes}")
        for problem in problems:
            print(f"       {problem}")
        if problems:
            broken += 1

    print(f"tools with a structural break: {broken}")
    return 1 if broken else 0


if __name__ == "__main__":
    raise SystemExit(main())
