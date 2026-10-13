"""Audit step 1: inventory the COM members the adapter CALLS, resolved by RECEIVER TYPE.

  python scripts/api-audit-routes.py [--out <json>] [--self-test]

The inventory is built from the CODE, not from a list: a hand-written list of routes drifts away from
the adapter the moment the adapter changes, and then the audit certifies a program that no longer
exists.

WHY BY RECEIVER TYPE, NEVER BY MEMBER NAME. A regex over the member name picks up the product's own
collections and functions: `checks.Add` is `List<NamedCheck>.Add`, and matching it against API
metadata produced a phantom COM route `Add` "confirmed" by COM collection pages. So a route is
recorded only when the RECEIVER's type is a COM interface of the installed interop and the member
exists on that type. Everything else is kept in separate buckets - "no type established" is NOT "no
route", and it must never be counted as confirmed.

TWO SHAPES OF "NO ROUTE" THAT ARE NOT THE SAME:
  * `unknown`        - the receiver's type could not be established (a local from a foreign API, a
                       chain through a property the model does not carry);
  * `unknown-member` - the receiver's type IS a COM interface but the member is not on it (a typo,
                       or a member that exists on another interface).
Both are reported with their sites; neither is a confirmation.

`--self-test` proves the negative control: a call added to a COPY of the adapter must appear in the
inventory, and the same copy with that line removed must not.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from api_audit_common import (ADAPTER_SRC, ROOT, default_out, save_json, stamp)  # noqa: E402

SCAN = os.path.join(ROOT, "tools", "KompasMcp.InteropScan", "bin", "x64", "Release",
                    "net10.0-windows", "KompasMcp.InteropScan.exe")
INTEROP_DIR = r"D:\Programs\KOMPAS-3Dv24\Libs\PolynomLib\Bin\Client"
VENDOR = (("api5", "Interop.Kompas6API5.dll"), ("api7", "Interop.KompasAPI7.dll"))
ADAPTER_DLL = os.path.join(ROOT, "src", "KompasMcp.Api5Adapter", "bin", "x64", "Release",
                           "net10.0-windows", "KompasMcp.Api5Adapter.dll")

# A TYPE NAME in this codebase is either an ordinary .NET name (`Api5Session`) or an API5 interop name
# (`ksPart`, `ksEntity`, `partClass`) - the vendor's names are LOWERCASE-prefixed, and a pattern that
# accepts only capitals silently fails to type every API5 receiver, which reads as "no route".
TYPE_NAME = r"(?:ks[A-Za-z0-9_]*|[A-Z]\w*)"

# `a.b.c().Member` - a receiver chain of simple names, each optionally a call. Nested parentheses in
# an argument list are not modelled: such an access lands in `unparsed`, which is named, not dropped.
ACCESS = re.compile(r"(?<![\w.])([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*(?:\s*\([^()]*\))?)*)"
                    r"\s*\.\s*([A-Za-z_]\w*)")
TYPED_LOCAL = re.compile(r"(?<![\w.])(" + TYPE_NAME + r")\s+([a-z_]\w*)\s*(?=[;,)=])")
CAST_ASSIGN = re.compile(r"([a-z_]\w*)\s*=\s*\(\s*(" + TYPE_NAME + r")\s*\)")
FOREACH = re.compile(r"foreach\s*\(\s*(?:var|(" + TYPE_NAME + r"))\s+([a-z_]\w*)\s+in\b")
OUT_PARAM = re.compile(r"\bout\s+(?:var|(" + TYPE_NAME + r"))\s+([a-z_]\w*)")
ASSIGN = re.compile(r"(?<![\w.])([a-z_]\w*)\s*=\s*([^=;]+);")


def run_scan(assembly, resolve_dir=None):
    """`InteropScan <assembly> --dump-members` - metadata only, no КОМПАС session, no COM."""
    args = [SCAN, assembly, "--dump-members"]
    if resolve_dir:
        args += ["--resolve", resolve_dir]
    done = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", timeout=600)
    if done.returncode != 0:
        raise SystemExit(f"InteropScan отказал на {assembly}: {done.stderr[:400]}")
    return done.stdout.splitlines()


def parse_signature(signature):
    """Kind and return type of a member, out of the scanner's one-line signature."""
    if signature.startswith("prop "):
        return {"kind": "property", "return": signature[5:].split(" ", 1)[0]}
    if signature.startswith("field "):
        return {"kind": "field", "return": signature[6:].split(" = ")[0].split(" ", 1)[0]}
    if signature.startswith("Constructor"):
        return {"kind": "constructor", "return": None}
    match = re.match(r"^([\w<>\[\],\.&]+)\s+\w+\(", signature)
    if match:
        return {"kind": "method", "return": match.group(1)}
    return {"kind": "unknown", "return": None}


class InteropModel:
    """COM interfaces of the installed interop plus the hand-written twins, and their members."""

    def __init__(self):
        self.types = {}
        self.members = {}
        self.sources = []

    def add(self, assembly, tag, resolve_dir=None):
        self.sources.append({"assembly": assembly, "tag": tag})
        for line in run_scan(assembly, resolve_dir):
            if not line.startswith("member|"):
                continue
            parts = line.split("|")
            if len(parts) < 7:
                continue
            _, enumerated, declared, name, signature, com_import, is_interface = parts[:7]
            parsed = parse_signature(signature)
            entry = self.types.setdefault(enumerated, {
                "com": com_import == "True", "interface": is_interface == "True", "tag": tag})
            entry["com"] = entry["com"] or com_import == "True"
            key = (enumerated, name)
            if key not in self.members:
                self.members[key] = {"receiver_type": enumerated, "declared_on": declared,
                                     "member": name, "signature": signature,
                                     "inherited": declared != enumerated, **parsed}

    def is_com_interface(self, name):
        entry = self.types.get(name)
        return bool(entry and entry["com"] and entry["interface"])

    def member(self, type_name, member_name):
        return self.members.get((type_name, member_name))

    def type_names(self):
        return {name for name, entry in self.types.items() if entry["com"] and entry["interface"]}


def strip_code(text):
    """Source without comments and without string/character literals, line by line.

    A `//` inside a URL literal or a `/* */` around a block would otherwise be parsed as code and
    manufacture accesses nobody wrote. Newlines are preserved so line numbers stay true.
    """
    out = []
    in_block = False
    in_verbatim = False
    for line in text.splitlines():
        result = []
        index = 0
        while index < len(line):
            chunk = line[index:]
            if in_block:
                end = chunk.find("*/")
                if end < 0:
                    index = len(line)
                    continue
                in_block = False
                index += end + 2
                continue
            if in_verbatim:
                if chunk.startswith('""'):
                    index += 2
                    continue
                if chunk.startswith('"'):
                    in_verbatim = False
                    index += 1
                    continue
                index += 1
                continue
            if chunk.startswith("//"):
                break
            if chunk.startswith("/*"):
                in_block = True
                index += 2
                continue
            if chunk.startswith('@"'):
                in_verbatim = True
                index += 2
                continue
            if chunk.startswith('"') or chunk.startswith("'"):
                quote = chunk[0]
                index += 1
                while index < len(line):
                    if line[index] == "\\":
                        index += 2
                        continue
                    if line[index] == quote:
                        index += 1
                        break
                    index += 1
                continue
            result.append(chunk[0])
            index += 1
        out.append("".join(result))
    return "\n".join(out)


def split_chain(expr):
    """Segments of a member chain, split on top-level dots. None when brackets do not balance."""
    segments = []
    depth = 0
    current = []
    for char in expr:
        if char in "([":
            depth += 1
        elif char in ")]":
            depth -= 1
            if depth < 0:
                return None
        if char == "." and depth == 0:
            segments.append("".join(current).strip())
            current = []
        else:
            current.append(char)
    segments.append("".join(current).strip())
    return [segment for segment in segments if segment]


def expression_type(expr, table, model, depth=0):
    """Type of a receiver expression, or None when it cannot be established.

    A CAST DECLARES THE TYPE OF ITS ASSIGNMENT TARGET, NEVER OF ITS OPERAND - measured on this
    project, and it cost a whole route: `parameter = (ksRasterFormatParam)document.Document.X()`
    typed the OPERAND, and every `document.Document.<member>` below resolved against the wrong type.
    """
    if expr is None or depth > 6:
        return None
    expr = expr.strip().rstrip(";").strip()
    if not expr:
        return None
    cast = re.match(r"^\(\s*(" + TYPE_NAME + r")\s*\)\s*(.+)$", expr, re.S)
    if cast:
        return cast.group(1)
    new = re.match(r"^new\s+(" + TYPE_NAME + r")", expr)
    if new:
        return new.group(1)
    as_cast = re.match(r"^(.+?)\s+as\s+(" + TYPE_NAME + r")\s*$", expr, re.S)
    if as_cast:
        return as_cast.group(2)
    segments = split_chain(expr)
    if not segments:
        return None
    head = segments[0]
    if head.endswith(")"):
        return None
    current = table.get(head)
    for segment in segments[1:]:
        if current is None:
            return None
        name = segment.split("(")[0].strip()
        record = model.member(current, name)
        if record is None:
            return None
        current = record["return"]
    return current


def file_table(lines, model):
    """`{local name: type}` for one file, from declarations, assignments and casts."""
    table = {}
    for line in lines:
        for match in TYPED_LOCAL.finditer(line):
            table.setdefault(match.group(2), match.group(1))
        for match in FOREACH.finditer(line):
            if match.group(2):
                table.setdefault(match.group(2), match.group(1))
        for match in OUT_PARAM.finditer(line):
            if match.group(2):
                table.setdefault(match.group(2), match.group(1))
        for match in CAST_ASSIGN.finditer(line):
            table[match.group(1)] = match.group(2)
    # A second pass infers the types of `var` locals and of plain assignments, now that the explicit
    # declarations are known. Two passes, because `var x = part.Body` needs `part` first.
    for line in lines:
        for match in ASSIGN.finditer(line):
            inferred = expression_type(match.group(2), table, model)
            if inferred:
                table[match.group(1)] = inferred
    return table


def collect_routes(source_dir=ADAPTER_SRC, model=None):
    """Every COM member the adapter calls, with the receiver's type and the call sites."""
    model = model or build_model()
    routes = collections.OrderedDict()
    unresolved = collections.Counter()
    unresolved_sites = collections.defaultdict(list)
    for root, _dirs, files in os.walk(source_dir):
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            try:
                relative = os.path.relpath(path, ROOT).replace("\\", "/")
            except ValueError:
                # Самопроверка работает на копии дерева на другом диске: путь от корня репозитория
                # для неё не существует, и выдумывать его нельзя - берётся настоящий.
                relative = path.replace("\\", "/")
            with open(path, encoding="utf-8-sig") as handle:
                lines = strip_code(handle.read()).splitlines()
            table = file_table(lines, model)
            for number, line in enumerate(lines, start=1):
                for match in ACCESS.finditer(line):
                    receiver_expr, member = match.group(1), match.group(2)
                    if receiver_expr.split(".")[-1] in ("System", "Math", "Console", "String",
                                                        "Convert", "Enum", "Task", "Path", "File"):
                        continue
                    receiver_type = expression_type(receiver_expr, table, model)
                    if receiver_type is None:
                        # A receiver whose HEAD is a declared local is a gap in THIS instrument: the
                        # type was known and the chain still did not resolve. A receiver on an
                        # undeclared head is a member of an object this instrument does not model
                        # (the adapter's own classes, framework types); counting those as COM gaps
                        # would inflate the number and make an ordinary `list.Count` read as a
                        # missing route.
                        head = receiver_expr.split(".")[0].split("(")[0].strip()
                        kind = "unknown" if head in table else "foreign-receiver"
                        unresolved[kind] += 1
                        unresolved_sites[kind].append(f"{relative}:{number}:{receiver_expr}")
                        continue
                    if not model.is_com_interface(receiver_type):
                        continue
                    record = model.member(receiver_type, member)
                    if record is None:
                        unresolved["unknown-member"] += 1
                        unresolved_sites["unknown-member"].append(
                            f"{relative}:{number}:{receiver_type}.{member}")
                        continue
                    key = (record["declared_on"], member)
                    route = routes.setdefault(key, {
                        "route": f"{record['declared_on']}.{member}",
                        "assembly": model.types.get(receiver_type, {}).get("tag"),
                        "interface": record["declared_on"],
                        "declared_on": record["declared_on"],
                        "receiver_type": receiver_type,
                        "inherited": record["inherited"],
                        "member": member,
                        "kind": record["kind"],
                        "signature": record["signature"],
                        "type": record["return"],
                        "sites": [],
                        "receivers": [],
                    })
                    site = f"{relative}:{number}"
                    if site not in route["sites"]:
                        route["sites"].append(site)
                    if receiver_expr not in route["receivers"]:
                        route["receivers"].append(receiver_expr)
    for route in routes.values():
        route["sites"].sort()
        route["receivers"].sort()
    return list(routes.values()), dict(unresolved), dict(unresolved_sites)


REFLECTION = re.compile(r"(?:GetMethod|GetProperty|GetField)\s*\(\s*\"(\w+)\""
                        r"|InvokeMember\s*\(\s*\"(\w+)\"")
TYPEOF = re.compile(r"typeof\s*\(\s*(\w+)\s*\)")
INDEXER = re.compile(r"([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*\[")
CONSTANT = re.compile(r"public\s+const\s+int\s+(\w+)\s*=\s*(\d+)\s*;")
DOC_NAME = re.compile(r"<c>(o3d_\w+)</c>")


def scan_extras(source_dir, model):
    """Reflection, indexers and the numeric object-type constants - the three accesses a filter over
    typed member calls cannot see, and therefore the three a "no route" verdict would be wrong about.

    Reflection and late binding are examined SEPARATELY: a member reached by name is invisible to the
    `.Member(` filter, and a branch that never ran (a dead reflection branch) reads as a live route
    unless it is named here.
    """
    reflection = []
    indexers = []
    constants = []
    by_name = collections.defaultdict(set)
    for (type_name, member) in model.members:
        by_name[member].add(type_name)
    for root, _dirs, files in os.walk(source_dir):
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(root, name)
            try:
                relative = os.path.relpath(path, ROOT).replace("\\", "/")
            except ValueError:
                relative = path.replace("\\", "/")
            with open(path, encoding="utf-8-sig") as handle:
                raw = handle.read()
            lines = strip_code(raw).splitlines()
            table = file_table(lines, model)
            raw_lines = raw.splitlines()
            for number, line in enumerate(lines, start=1):
                # Отражение ищется по СЫРОЙ строке: `strip_code` снимает строковые литералы, а имя
                # члена при обращении по имени как раз литерал - на очищенной строке остаётся
                # `GetMethod()`, и ни одно обращение не находится (первая редакция дала пустой
                # список на непустом коде).
                if line.strip():
                    raw_line = raw_lines[number - 1] if number <= len(raw_lines) else ""
                    for match in REFLECTION.finditer(raw_line):
                        member = next(group for group in match.groups() if group)
                        typed = TYPEOF.search(raw_line)
                        reflection.append({
                            "member": member, "site": f"{relative}:{number}",
                            "receiver_type": typed.group(1) if typed else None,
                            "declared_in_interop": sorted(by_name.get(member, ())),
                            "documentation": "одноимённые страницы ДРУГИХ интерфейсов - не подтверждение",
                        })
                for match in INDEXER.finditer(line):
                    chain = match.group(1)
                    segments = split_chain(chain)
                    if not segments or len(segments) < 2:
                        continue
                    holder = expression_type(".".join(segments[:-1]), table, model)
                    if holder and model.is_com_interface(holder):
                        indexers.append({"interface": holder, "member": segments[-1],
                                         "site": f"{relative}:{number}"})
            # The numeric object-type constants live in ONE class, and the scan is restricted to its
            # body: `public const int` also declares drawing formats and array kinds, and a scan over
            # the whole file would report those as object types.
            start = None
            for number, line in enumerate(raw_lines):
                if start is None and "class KompasObjectTypes" in line:
                    start = number
                elif start is not None and line.startswith("}"):
                    break
                elif start is not None:
                    match = CONSTANT.search(line)
                    if not match:
                        continue
                    # The constant's OWN doc block only: scanning all preceding text makes an
                    # explanatory mention of another id look like the constant's documented name.
                    block = []
                    index = number - 1
                    while index >= 0 and raw_lines[index].strip().startswith("///"):
                        block.append(raw_lines[index])
                        index -= 1
                    # A doc block that names an id only to say the constant is NOT that id does not
                    # state the constant's own name. Measured: `BodyRepositionFeature = 79` carries
                    # «79 is NOT 569 (o3d_BodyReposition creates the object)», and reading the name
                    # out of it manufactured a contradiction with the documented table where the
                    # constant is right and the block says so.
                    block_text = "\n".join(reversed(block))
                    documented = None
                    found = DOC_NAME.search(block_text)
                    if found and "NOT" not in block_text[max(0, found.start() - 120):found.start()]:
                        documented = found.group(1)
                    constants.append({"constant": match.group(1), "value": int(match.group(2)),
                                      "documented": documented,
                                      "doc_block": block_text[:400],
                                      "site": f"{relative}:{number + 1}"})
    return {"reflection_access": reflection, "indexer_routes": indexers,
            "object_type_constants": constants}


def build_model():
    model = InteropModel()
    for tag, dll in VENDOR:
        model.add(os.path.join(INTEROP_DIR, dll), tag)
    if os.path.isfile(ADAPTER_DLL):
        model.add(ADAPTER_DLL, "adapter-twins", resolve_dir=INTEROP_DIR)
    return model


def self_test():
    """The negative control of the inventory: an ADDED call must appear, and only it."""
    failures = []
    checks = 0

    def check(name, condition, detail=""):
        nonlocal checks
        checks += 1
        print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
        if not condition:
            failures.append(name)

    model = build_model()
    check("модель знает COM-интерфейсы установленного interop",
          {"ksPart", "ksDocument3D", "ksEntity"} <= model.type_names(),
          f"типов-интерфейсов {len(model.type_names())}")
    check("член наследуется с НАЗВАННЫМ владельцем",
          model.member("ksPart", "NewEntity") is not None
          and model.member("ksPart", "NewEntity")["declared_on"] == "ksPart")
    check("член чужого типа не находится на этом типе",
          model.member("ksPart", "Axes3D") is None)
    check("двойник из репозитория виден как COM-интерфейс",
          model.is_com_interface("IPart7Twin"), str(model.types.get("IPart7Twin")))

    check("комментарий и литерал не считаются кодом",
          strip_code('var a = "part.NewEntity(1)"; // b.NewEntity(2)\n') .count("NewEntity") == 0,
          strip_code('var a = "part.NewEntity(1)"; // b.NewEntity(2)\n'))
    check("каст задаёт тип ЦЕЛИ присваивания, а не операнда",
          expression_type("(ksRasterFormatParam)document.Document.X()", {}, model)
          == "ksRasterFormatParam")
    check("цепочка разрешается через модель",
          expression_type("part.NewEntity", {"part": "ksPart"}, model) is not None)

    with tempfile.TemporaryDirectory() as tmp:
        copy = os.path.join(tmp, "adapter")
        shutil.copytree(ADAPTER_SRC, copy, ignore=shutil.ignore_patterns("bin", "obj"))
        baseline = {route["route"] for route in collect_routes(copy, model)[0]}
        # Подсаживаемое обращение берётся ИЗ МОДЕЛИ и НЕ из инвентаря: обращение, которое в адаптере
        # уже есть (`ksPart.NewEntity`), не различает «прибор увидел подсаженное» и «прибор увидел
        # старое», и первая редакция этой проверки именно так и проходила.
        candidate = next((member for (type_name, member) in sorted(model.members)
                          if type_name == "ksPart"
                          and model.members[(type_name, member)]["kind"] == "method"
                          and f"{type_name}.{member}" not in baseline), None)
        check("найдено обращение, которого в адаптере нет", candidate is not None, str(candidate))
        probe = os.path.join(copy, "AuditProbe.cs")
        with open(probe, "w", encoding="utf-8") as handle:
            handle.write("internal static class AuditProbe {\n"
                         "    internal static void Run(ksPart part) {\n"
                         f"        part.{candidate}();\n"
                         "    }\n"
                         "}\n")
        added = {route["route"] for route in collect_routes(copy, model)[0]}
        check("подсаженный вызов появился в инвентаре", f"ksPart.{candidate}" in added,
              f"маршрутов {len(added)}")
        os.remove(probe)
        removed = {route["route"] for route in collect_routes(copy, model)[0]}
        check("после снятия строки маршрут исчез", f"ksPart.{candidate}" not in removed)
        check("остальной инвентарь не изменился", removed == baseline,
              f"разница {sorted(removed ^ baseline)[:5]}")

    print(f"\nСамопроверка прибора: {checks} проверок, "
          f"{len(failures)} FAIL" + (f" - {failures}" if failures else ""))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Инвентарь COM-маршрутов адаптера из кода.")
    parser.add_argument("--source", default=ADAPTER_SRC, help="каталог исходников адаптера")
    parser.add_argument("--out", default=None, help="куда записать измерение (JSON)")
    parser.add_argument("--self-test", action="store_true", help="проверить прибор, ничего не измеряя")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    model = build_model()
    routes, counts, sites = collect_routes(args.source, model)
    extras = scan_extras(args.source, model)
    by_assembly = collections.Counter(route["assembly"] for route in routes)
    result = {
        **extras,
        "instrument": "api-audit-routes",
        "date": stamp(),
        "scanner": os.path.relpath(SCAN, ROOT).replace("\\", "/"),
        "model_sources": model.sources,
        "adapter_sources": os.path.relpath(args.source, ROOT).replace("\\", "/"),
        "routes": routes,
        "route_count": len(routes),
        "by_assembly": dict(by_assembly),
        "receivers_without_type": {
            "total": sum(counts.values()),
            "by_kind": counts,
            "sites": {key: value[:200] for key, value in sites.items()},
            "note": "получатель не типизирован - это НЕ доказательство отсутствия маршрута; "
                    "перечислено честно, чтобы пробел прибора не читался как полнота",
        },
    }
    out = args.out or default_out("routes")
    save_json(out, result)
    print(f"COM-маршрутов: {len(routes)} ({dict(by_assembly)})")
    print(f"получателей без типа: {sum(counts.values())} {counts}")
    print(f"записано: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
