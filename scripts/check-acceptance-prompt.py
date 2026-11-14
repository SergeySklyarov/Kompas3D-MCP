"""Сверка текста промпта клиентской приёмки с опубликованным контрактом.

  python scripts/check-acceptance-prompt.py <промпт> [--schemas <каталог>] [--root <корень>]
  python scripts/check-acceptance-prompt.py --self-test

Читает промпт и контракт, который клиент видит без запуска КОМПАС: входные схемы `schemas/`,
выгрузку каталога `docs/TOOLS.md`, опубликованные описания инструментов
(`src/KompasMcp.Host/Catalog/ToolCatalog.cs`, `CompactCatalog.cs`) и конверт результата
(`docs/02_TOOL_CONTRACTS.md` §2.2). Каждое имя в обратных кавычках относится к инструменту строки -
к ближайшему упоминанию `kompas_*` слева, иначе к первому в строке; строка без упоминания
инструмента не сверяется (в ней говорит процедура приёмки, а не контракт продукта).

Три вида находок:
  ИНСТРУМЕНТ - имени `kompas_*` нет в каталоге.
  АРГУМЕНТ   - имя стоит в перечне аргументов инструмента строки (сразу за его именем, через
               `,` `/` `и` `с`) и не является ни полем его входной схемы, ни значением `enum`.
               Если имя - поле ДРУГОГО инструмента, находка его называет.
  НЕ НАЗВАНО - имя не встречается в контракте нигде; оно выводится отдельным списком и не меняет
               код возврата: так выглядит имя ответа, которого контракт не описывает.

Код возврата 0 - находок ИНСТРУМЕНТ и АРГУМЕНТ нет; 1 - есть; 2 - вход не прочитан.

Что не проверяется (предел, названный, а не угаданный):
  - имена полей ОТВЕТА: опубликованного описания ответа нет ни у одного инструмента (у инструментов
    нет `outputSchema`, а описания перечисляют ответ прозой и неполно), поэтому «поля ответа» прибор
    не судит - он называет их списком «НЕ НАЗВАНО» и требует источника-измерения;
  - имена проверок (`checks[].name`) и коды отказа: их множество из `schemas/` не выводится, а
    написание совпадает с вердиктами процедуры приёмки (`NOT_PERFORMED_BY_CLIENT`), поэтому проба
    дала бы ложные отказы;
  - слова без подчёркивания (`notes`, `dirty`, `sandbox`), файлы, пути, `--ключи`, константы
    (`UPPER_SNAKE`) и хеши: правило написания, а не перечень исключений под один промпт.
History: docs/decisions/releases.md#acceptance-prompt-text"""

import difflib
import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

WORD = re.compile(r"[a-z][a-z0-9_]*")
TOOL_NAME = re.compile(r"^kompas_[a-z0-9_]+$")
SNAKE = re.compile(r"^[a-z][a-z0-9_]*$")
PATH = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")
BACKTICK = re.compile(r"`([^`\n]+)`")
TOOL_IN_TEXT = re.compile(r"\bkompas_[a-z0-9_]+\b")
HEX = re.compile(r"^[0-9a-f]{7,40}$")
FILE_SUFFIX = re.compile(
    r"\.(spw|m3d|a3d|cdw|frw|kdw|json|md|py|ps1|exe|dll|yml|yaml|zip|log|txt|dxf|stl|csv|png|mcpb)$")


class Contract:
    """Published contract: per-tool input paths, enum values, words; plus the result envelope."""

    def __init__(self):
        self.tools = {}
        self.envelope_paths = set()
        self.envelope_words = set()
        self.all_words = set()
        self.sources = []

    def tool(self, name):
        return self.tools.setdefault(
            name, {"paths": set(), "enums": set(), "words": set(), "has_schema": False})

    def finish(self):
        self.all_words = set(self.envelope_words)
        for entry in self.tools.values():
            self.all_words |= entry["words"]


def read_text(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        return handle.read()


def schema_paths(node, prefix, found):
    """Every property path of one schema; recurses through items/anyOf/oneOf/allOf.

    `$defs` is not walked on its own: a definition counts where a property uses it (the reference is a
    property of the schema, not a nesting level of it).
    """
    if not isinstance(node, dict):
        return
    for key, child in (node.get("properties") or {}).items():
        found.add(key if not prefix else prefix + "." + key)
        schema_paths(child, key if not prefix else prefix + "." + key, found)
    if isinstance(node.get("items"), dict):
        schema_paths(node["items"], prefix, found)
    for key in ("anyOf", "oneOf", "allOf"):
        for branch in node.get(key) or []:
            schema_paths(branch, prefix, found)


def enum_values(node, found):
    if isinstance(node, dict):
        for key, child in node.items():
            if key == "enum" and isinstance(child, list):
                found.update(str(item) for item in child)
            else:
                enum_values(child, found)
    elif isinstance(node, list):
        for child in node:
            enum_values(child, found)


def schema_words(node, found):
    """Words of every string in a parsed schema: descriptions carry the names of the answer.

    The parsed form is used, not the file bytes: the JSON on disk escapes non-ASCII as `\\u04xx` and
    those escapes are not words of the contract.
    """
    if isinstance(node, dict):
        for key, child in node.items():
            found.update(WORD.findall(key))
            schema_words(child, found)
    elif isinstance(node, list):
        for child in node:
            schema_words(child, found)
    elif isinstance(node, str):
        found.update(WORD.findall(node))


def _read_literal(text, index):
    """Consume one C# string literal starting at a quote; return (next index, value)."""
    index += 1
    parts = []
    while index < len(text):
        char = text[index]
        if char == "\\" and index + 1 < len(text):
            parts.append({"n": "\n", "t": "\t", "r": "", '"': '"', "\\": "\\"}.get(text[index + 1], ""))
            index += 2
            continue
        if char == '"':
            return index + 1, "".join(parts)
        parts.append(char)
        index += 1
    raise ValueError("unterminated literal")


def _skip_balanced(text, index):
    depth = 0
    while index < len(text):
        char = text[index]
        if char == '"':
            index, _ = _read_literal(text, index)
            continue
        if char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
            if depth == 0:
                return index + 1
        index += 1
    return index


def catalog_descriptions(path):
    """Tool name -> published description text, read from the catalog source without building it.

    The description is the third argument of the catalog's factory calls and is written as a chain of
    string literals; only the literals are taken, so a description built from a named constant is
    shorter here than in `tools/list`. That is why the contract also keeps the schema text and
    `docs/TOOLS.md`: a name is accepted when ANY published source states it.
    """
    text = read_text(path)
    text = "".join("\n" if line.lstrip().startswith("//") else line for line in text.splitlines(True))
    found = []
    for match in re.finditer(r'\b(?:ReadOnly|Mutation|Control)\(\s*"([a-z0-9_]+)"\s*,\s*"[^"\n]*"\s*,', text):
        index, parts = match.end(), []
        while index < len(text):
            char = text[index]
            if char == '"':
                index, value = _read_literal(text, index)
                parts.append(value)
            elif char in "([{":
                index = _skip_balanced(text, index)
            elif char in ",)":
                break
            else:
                index += 1
        found.append((match.group(1), "".join(parts)))
    return found


def load_contract(root, schemas_dir):
    contract = Contract()
    schema_files = sorted(glob.glob(os.path.join(schemas_dir, "*.json")))
    if not schema_files:
        raise FileNotFoundError("в %s нет ни одной схемы" % schemas_dir)
    for path in schema_files:
        name = os.path.basename(path)[:-len(".json")]
        schema = json.loads(read_text(path))
        entry = contract.tool(name)
        entry["has_schema"] = True
        schema_paths(schema, "", entry["paths"])
        enum_values(schema, entry["enums"])
        schema_words(schema, entry["words"])
        entry["words"].add(name)
    contract.sources.append("%d схем" % len(schema_files))

    tools_md = os.path.join(root, "docs", "TOOLS.md")
    if os.path.exists(tools_md):
        rows = 0
        for line in read_text(tools_md).splitlines():
            match = re.match(r"^\|\s*`(kompas_[a-z0-9_]+)`\s*\|", line)
            if not match:
                continue
            entry = contract.tool(match.group(1))
            entry["words"] |= set(WORD.findall(line))
            entry["words"].add(match.group(1))
            rows += 1
        contract.sources.append("docs/TOOLS.md (%d строк)" % rows)

    catalog = os.path.join(root, "src", "KompasMcp.Host", "Catalog", "ToolCatalog.cs")
    if os.path.exists(catalog):
        pairs = catalog_descriptions(catalog)
        for name, description in pairs:
            entry = contract.tool(name)
            entry["words"] |= set(WORD.findall(description))
        contract.sources.append("описания каталога (%d)" % len(pairs))

    compact = os.path.join(root, "src", "KompasMcp.Host", "Catalog", "CompactCatalog.cs")
    if os.path.exists(compact):
        source = read_text(compact)
        names = re.findall(r'public const string \w+Tool = "((?:kompas_)[a-z0-9_]+)"', source)
        for name in names:
            contract.tool(name)
            contract.tools[name]["words"].add(name)
        contract.sources.append("короткий каталог (%d)" % len(names))

    contracts_md = os.path.join(root, "docs", "02_TOOL_CONTRACTS.md")
    if os.path.exists(contracts_md):
        document = read_text(contracts_md)
        section = re.search(r"## 2\.2\..*?```json\n(.*?)```", document, re.S)
        if not section:
            raise ValueError("docs/02_TOOL_CONTRACTS.md: не найден пример конверта в §2.2")
        block = section.group(1)
        contract.envelope_words |= set(WORD.findall(block))
        # The whole document counts as the published contract: a name stated there is stated for
        # the client, even when §2.2 does not repeat it (e.g. `byte_length` of a saved file).
        contract.envelope_words |= set(WORD.findall(document)) - set(WORD.findall(block))

        def walk(node, prefix):
            if isinstance(node, dict):
                for key, child in node.items():
                    path = key if not prefix else prefix + "." + key
                    contract.envelope_paths.add(path)
                    walk(child, path)
            elif isinstance(node, list):
                for child in node:
                    walk(child, prefix)

        walk(json.loads(block), "")
        contract.sources.append("конверт результата §2.2")

    contract.finish()
    return contract


def token_kind(token):
    """Classify one backticked token: tool, path, name, value or None (not a contract name)."""
    if TOOL_NAME.match(token):
        return "tool"
    if PATH.match(token) and not FILE_SUFFIX.search(token):
        return "path"
    if SNAKE.match(token) and "_" in token and not HEX.match(token):
        return "name"
    if SNAKE.match(token) or re.match(r"^[a-z][a-z0-9_]*(=[^\s]+)?$", token) or re.match(r"^-?\d", token):
        return "value"
    return None


def check(prompt_text, contract):
    """Findings of a prompt against the contract: (kind, line, token, tool, detail).

    A tool token opens an argument chain; the chain continues over following backticked tokens whose
    gap to the chain end holds only separators, or one value already accepted as a value (an `enum`
    member). Any other gap closes the chain, and the token is judged as a name elsewhere in the line.
    """
    found = []
    checked = 0
    for lineno, line in enumerate(prompt_text.splitlines(), 1):
        mentions = [(m.start(), m.end(), m.group(0)) for m in TOOL_IN_TEXT.finditer(line)]
        if not mentions:
            continue
        chain_tool, chain_end = None, 0
        for match in BACKTICK.finditer(line):
            token = match.group(1).strip()
            kind = token_kind(token)
            if kind is None:
                chain_tool = None
                continue
            before = [m for m in mentions if m[1] <= match.start()]
            bound = before[-1][2] if before else mentions[0][2]
            entry = contract.tools.get(bound)
            checked += 1

            if kind == "tool":
                # An unknown tool opens no argument chain: its schema is unknown, so its arguments
                # cannot be judged at all.
                known_tool = token in contract.tools
                chain_tool, chain_end = (token, match.end()) if known_tool else (None, 0)
                if not known_tool:
                    found.append(("ИНСТРУМЕНТ", lineno, token, bound, nearest(token, contract.tools)))
                continue

            if kind == "value":
                chain_tool = None
                continue

            gap = line[chain_end:match.start()] if chain_tool else ""
            in_chain = bool(chain_tool) and chain_tool == bound and (
                separators_only(gap) or value_only(gap, contract.tools[chain_tool]))
            if in_chain:
                chain_end = match.end()
            else:
                chain_tool = None

            if kind == "path":
                # INVARIANT: a path is judged only as an argument (it stands in the tool's argument
                # list). A path inside a description of an answer is a response path, and the contract
                # does not describe answers; judging it would flag `result.checks` and
                # `details.failure_snapshot`, which exist but are not in any schema.
                if in_chain:
                    known = token in contract.envelope_paths or (entry and token in entry["paths"])
                    if not known:
                        found.append(("АРГУМЕНТ", lineno, token, bound, nearest_path(token, entry)))
                continue

            if entry is None or not entry["has_schema"]:
                # LIMIT: without an input schema of its own (kompas_run, kompas_find_operations,
                # kompas_describe_operation) the argument list cannot be judged at all.
                continue
            if in_chain:
                if token in entry["paths"] or token in entry["enums"]:
                    continue
                owners = sorted(name for name, other in contract.tools.items()
                                if token in other["paths"])
                detail = ("поле другого инструмента: " + ", ".join(owners[:4]) if owners
                          else nearest_path(token, entry))
                found.append(("АРГУМЕНТ", lineno, token, bound, detail))
                continue
            if token not in contract.all_words and token not in contract.envelope_words:
                found.append(("НЕ НАЗВАНО", lineno, token, bound, nearest(token, contract.tools)))
    return found, checked


def separators_only(gap):
    """True when the gap holds only punctuation, spaces and short connectors ("с", "и", "по").

    The allowed word is short by SHAPE, not by a dictionary: a field name or a verb is longer, so the
    chain closes on it. That keeps the rule independent of the prompt's language.
    """
    words = [word for word in re.split(r"[\s,;/()\u00ab\u00bb]+", gap) if word]
    return all(len(word) <= 2 for word in words)


def value_only(gap, entry):
    """True when the gap holds one backticked value accepted by the tool (an `enum` member).

    That is how "`kompas_hole` `blind_flat` с `expected_volume_delta_mm3`" reads: the mode is named
    before its arguments, and the chain must survive it.
    """
    parts = re.split(r"`([^`]+)`", gap)
    if len(parts) != 3:
        return False
    return separators_only(parts[0] + " " + parts[2]) and parts[1].strip() in entry["enums"]


def nearest(name, tools):
    """Closest contract names for a name that is not one, so the report is actionable."""
    pool = set()
    for entry in tools.values():
        pool |= entry["paths"] | entry["words"]
    candidates = sorted(pool)
    close = difflib_close(name, candidates)
    return ", ".join(close) if close else "ближайшего имени нет"


def nearest_path(name, entry):
    if not entry:
        return "нет схемы инструмента"
    close = difflib_close(name, sorted(entry["paths"]))
    return ("ближайшее имя схемы: " + ", ".join(close)) if close else "ближайшего имени схемы нет"


def difflib_close(name, candidates, limit=3):
    return difflib.get_close_matches(name, candidates, n=limit, cutoff=0.6)


def report(prompt_path, contract, found, checked):
    print("Промпт: %s" % prompt_path)
    print("Контракт: " + "; ".join(contract.sources))
    errors = [row for row in found if row[0] != "НЕ НАЗВАНО"]
    unstated = [row for row in found if row[0] == "НЕ НАЗВАНО"]
    for kind, lineno, token, tool, detail in errors:
        print("%s:%d: %s `%s` у %s - %s" % (prompt_path, lineno, kind, token, tool, detail))
    if unstated:
        print("НЕ НАЗВАНО контрактом (поля ответа контракт не описывает - нужен источник-измерение):")
        for _kind, lineno, token, tool, _detail in unstated:
            print("  %s:%d: `%s` у %s" % (prompt_path, lineno, token, tool))
    print("Имён в обратных кавычках сверено: %d; находок: %d (инструмент/аргумент), "
          "не названо контрактом: %d" % (checked, len(errors), len(unstated)))
    return 1 if errors else 0


def self_test():
    """Bilateral self-check: one positive case proves nothing, so every kind has both signs."""
    contract = Contract()
    grid = contract.tool("kompas_pattern_grid")
    grid["paths"] |= {"document_id", "copy_kind", "count1", "source_refs"}
    grid["enums"] |= {"components", "bodies"}
    stl = contract.tool("kompas_export_stl")
    stl["paths"] |= {"document_id", "max_edge_length_mm", "normal_angle_deg"}
    hole = contract.tool("kompas_hole")
    hole["paths"] |= {"document_id", "depth_mm"}
    hole["enums"] |= {"blind_flat"}
    for entry in (grid, stl, hole):
        entry["has_schema"] = True
    grid["words"] |= {"kompas_pattern_grid", "readout", "count1", "exemplar_counts"}
    stl["words"] |= {"kompas_export_stl", "mesh_step_mm"}
    contract.envelope_paths |= {"result.checks", "verification.checks", "verification.level"}
    contract.envelope_words |= {"verification", "checks", "result", "notes"}
    contract.finish()

    cases = []

    def add(name, text, expected):
        found, checked = check(text, contract)
        got = [(kind, token) for kind, _line, token, _tool, _detail in found]
        cases.append((got == expected, name, got, expected))
        return found

    add("чистый промпт - находок нет", "`kompas_pattern_grid` с `copy_kind=components`, `count1`=3\n", [])
    add("чужой инструмент назван", "`kompas_no_such_tool` и всё\n", [("ИНСТРУМЕНТ", "kompas_no_such_tool")])
    add("у чужого инструмента аргументы не судятся", "`kompas_no_such_tool` с `length_mm`\n",
        [("ИНСТРУМЕНТ", "kompas_no_such_tool")])
    add("поле ответа не судится, но названо", "`kompas_pattern_grid` даёт `readout`\n",
        [])
    add("поле другого инструмента в перечне аргументов",
        "`kompas_export_stl` с `depth_mm`\n", [("АРГУМЕНТ", "depth_mm")])
    add("несуществующее поле существующего инструмента",
        "`kompas_export_stl` с `length_mm`\n", [("АРГУМЕНТ", "length_mm")])
    add("имя без источника в контракте - отдельный список", "`kompas_export_stl` с `made_up_name`\n",
        [("АРГУМЕНТ", "made_up_name")])
    add("имена без упоминания инструмента не сверяются", "`все_поля` и `verification.checks`\n", [])
    add("путь конверта принят", "`kompas_export_stl`, ответ в `verification.checks`\n", [])
    add("файл, путь, код и хеш - не имена контракта",
        "`C:\\publish\\KompasMcp.Host.exe`, `spec.spw`, `--all`, `PATH_NOT_ALLOWED`, `0123456789abcdef`\n",
        [])
    detail = add("поле другого инструмента называет владельца",
                 "`kompas_export_stl` с `depth_mm`\n", [("АРГУМЕНТ", "depth_mm")])
    cases.append((detail and "kompas_hole" in detail[0][4], "владелец поля назван", detail, "kompas_hole"))

    unstated = check("`kompas_pattern_grid` отдаёт `exemplar_counts` и `stray_name`\n", contract)[0]
    cases.append(([(k, t) for k, _l, t, _b, _d in unstated] == [("НЕ НАЗВАНО", "stray_name")],
                  "не названное контрактом не меняет вердикта", unstated, "stray_name"))
    cases.append((report_code("`kompas_pattern_grid` отдаёт `stray_name`\n", contract) == 0,
                  "только НЕ НАЗВАНО - код 0", "unstated-only", 0))

    unreadable = 0
    try:
        load_contract(ROOT, os.path.join(ROOT, "no-such-schemas-dir"))
    except (FileNotFoundError, ValueError):
        unreadable = 2
    cases.append((unreadable == 2, "нечитаемый вход - код 2", unreadable, 2))
    cases.append((check_main([]) == 2, "нет аргумента - код 2", 2, 2))

    for ok, name, got, expected in cases:
        print(("PASS " if ok else "FAIL ") + name)
        if not ok:
            print("      получено: %r, ожидалось: %r" % (got, expected))
    return 0 if all(ok for ok, _name, _got, _expected in cases) else 1


def report_code(text, contract):
    found, _checked = check(text, contract)
    return 1 if [row for row in found if row[0] != "НЕ НАЗВАНО"] else 0


def check_main(argv):
    if not argv:
        print(__doc__)
        return 2
    prompt_path = argv[0]
    root, schemas = ROOT, None
    for index, arg in enumerate(argv):
        if arg == "--root" and index + 1 < len(argv):
            root = os.path.abspath(argv[index + 1])
        if arg == "--schemas" and index + 1 < len(argv):
            schemas = os.path.abspath(argv[index + 1])
    schemas = schemas or os.path.join(root, "schemas")
    try:
        text = read_text(prompt_path)
    except (OSError, UnicodeDecodeError) as error:
        print("Промпт не прочитан: %s: %s" % (prompt_path, error))
        return 2
    try:
        contract = load_contract(root, schemas)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print("Контракт не прочитан: %s" % error)
        return 2
    found, checked = check(text, contract)
    return report(prompt_path, contract, found, checked)


def main(argv):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    if "--self-test" in argv:
        return self_test()
    return check_main([arg for arg in argv if arg != "--self-test"])


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
