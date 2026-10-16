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

SHAPES OF "NO ROUTE" THAT ARE NOT THE SAME. Each site carries a REASON, so the remainder can be read
instead of guessed at, and only two of the five are gaps in THIS instrument:
  * `foreign-receiver` / `head-not-declared`     - the head is no local this instrument tracks;
  * `unknown` / `head-type-not-established`      - the local IS tracked, its type is not (a `var` from
                                                   a call the interop model does not describe);
  * `unknown` / `head-type-not-com`              - the type is known and is not a COM interface: the
                                                   chain legitimately leaves the interop;
  * `unknown` / `chain-broken-on-com-type`       - a COM type whose chain broke: a GAP here;
  * `unknown-member` / `member-not-on-interface` - a COM type that does not carry that member.
None of the five is a confirmation.

`--self-test` proves the negative control: a call added to a COPY of the adapter must appear in the
inventory, and the same copy with that line removed must not.

A RECEIVER'S TYPE IS READ FROM WHAT THE CODE WRITES, NOT FROM ONE SPELLING OF IT: a declaration, the
RETURN of a called member, and the name an `is` pattern introduces all count. The namespace is
stripped before the model is asked, and a name's type is the binding in force at the line, because a
name is reused across methods in one file. History: docs/decisions/api-audit.md#audit-receiver-types
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
import kompas_install  # noqa: E402
from api_audit_common import (ADAPTER_SRC, ROOT, default_out, save_json, stamp)  # noqa: E402

SCAN = os.path.join(ROOT, "tools", "KompasMcp.InteropScan", "bin", "x64", "Release",
                    "net10.0-windows", "KompasMcp.InteropScan.exe")
# INVARIANT: the interop comes from the shared installation root, never from a literal here. A
# literal made the scanner report LOAD_FAILED on a machine where KOMPAS is installed elsewhere, and
# that read as "the adapter's COM members cannot be resolved" instead of "wrong machine path".
INTEROP_DIR = kompas_install.interop_dir()
VENDOR = (("api5", "Interop.Kompas6API5.dll"), ("api7", "Interop.KompasAPI7.dll"))
ADAPTER_DLL = os.path.join(ROOT, "src", "KompasMcp.Api5Adapter", "bin", "x64", "Release",
                           "net10.0-windows", "KompasMcp.Api5Adapter.dll")

# A TYPE NAME in this codebase is either an ordinary .NET name (`Api5Session`) or an API5 interop name
# (`ksPart`, `ksEntity`, `partClass`) - the vendor's names are LOWERCASE-prefixed, and a pattern that
# accepts only capitals silently fails to type every API5 receiver, which reads as "no route".
TYPE_NAME = r"(?:ks[A-Za-z0-9_]*|[A-Z]\w*)"

# THE TYPE AS IT IS WRITTEN. The adapter names an interop type behind its namespace
# (`KompasAPI7.IView`, `Kompas6API5.ksPart`) far more often than by its bare name, and a pattern that
# accepted only the bare name left every such receiver untyped - which reads as "no route". INVARIANT:
# the namespace is STRIPPED before the model is asked (`simple_type`), because the model carries the
# interop's OWN names (`IView`, not `KompasAPI7.IView`). `global::` is the same name with the alias
# spelled out; `?` is C#'s nullable annotation and not part of the type.
QUALIFIED = r"(?:global::)?(?:" + TYPE_NAME + r"\s*\.\s*)*" + TYPE_NAME + r"\s*\??"

# `a.b.c().Member` - a receiver chain of simple names, each optionally a call. Nested parentheses in
# an argument list are not modelled: such an access is not silently dropped, it lands in one of the
# named buckets below with its reason.
ACCESS = re.compile(r"(?<![\w.])([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*(?:\s*\([^()]*\))?)*)"
                    r"\s*\.\s*([A-Za-z_]\w*)")

# A C# keyword in the NAME position is not a name: without this, `x is COMException or ...` declares a
# local called `or`.
NOT_KEYWORD = r"(?!(?:or|and|is|not|null|true|false|new|in|out|ref|var|when)\b)"

TYPED_LOCAL = re.compile(r"(?<![\w.])(" + QUALIFIED + r")\s+([a-z_]\w*)\s*(?=[;,)=])")
CAST_ASSIGN = re.compile(r"([a-z_]\w*)\s*=\s*\(\s*(" + QUALIFIED + r")\s*\)")
FOREACH = re.compile(r"foreach\s*\(\s*(?:var|(" + QUALIFIED + r"))\s+([a-z_]\w*)\s+in\b")
OUT_PARAM = re.compile(r"\bout\s+(?:var|(" + QUALIFIED + r"))\s+([a-z_]\w*)")
# `is`/`is not` DECLARES the type of the name it introduces (`if (document is IKompasDocument2D d2d)`).
# Without it the declared name stays untyped, and every member read through it is lost.
PATTERN_IS = re.compile(r"\bis\s+(?:not\s+)?(" + QUALIFIED + r")\s+" + NOT_KEYWORD + r"([a-z_]\w*)\b")
ASSIGN = re.compile(r"(?<![\w.])([a-z_]\w*)\s*=\s*([^=;]+);")

# The interop NAMESPACE each model source declares its types in, so a probe can spell a type the way
# the adapter does. A tag without an entry here has no qualified spelling and is not probed that way.
NAMESPACE = {"api5": "Kompas6API5", "api7": "KompasAPI7"}


def simple_type(name):
    """The type as the interop model spells it: `global::`, the namespace and `?` removed.

    The model carries the interop's OWN names, so `KompasAPI7.IView` must be reduced before it can be
    looked up. MEASURED: the adapter writes the qualified form in every `KompasAPI7.*` declaration,
    and not reducing it left those receivers untyped - which reads as "no route".
    """
    name = name.strip()
    if name.startswith("global::"):
        name = name[len("global::"):].strip()
    name = name.rstrip("?").strip()
    return name.rsplit(".", 1)[-1] if "." in name else name


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


def expression_type(expr, table, model, line, depth=0):
    """Type of a receiver expression, or None when it cannot be established.

    `line` is where the expression is READ: a local's type is the binding in force there, not the
    first one written anywhere in the file.

    A CAST DECLARES THE TYPE OF ITS ASSIGNMENT TARGET, NEVER OF ITS OPERAND - measured on this
    project, and it cost a whole route: `parameter = (ksRasterFormatParam)document.Document.X()`
    typed the OPERAND, and every `document.Document.<member>` below resolved against the wrong type.
    """
    if expr is None or depth > 6:
        return None
    expr = expr.strip().rstrip(";").strip()
    if not expr:
        return None
    cast = re.match(r"^\(\s*(" + QUALIFIED + r")\s*\)\s*(.+)$", expr, re.S)
    if cast:
        return simple_type(cast.group(1))
    new = re.match(r"^new\s+(" + QUALIFIED + r")", expr)
    if new:
        return simple_type(new.group(1))
    as_cast = re.match(r"^(.+?)\s+as\s+(" + QUALIFIED + r")\s*$", expr, re.S)
    if as_cast:
        return simple_type(as_cast.group(2))
    segments = split_chain(expr)
    if not segments:
        return None
    head = segments[0]
    if head.endswith(")"):
        return None
    current = type_at(table, head, line)
    for segment in segments[1:]:
        if current is None:
            return None
        name = segment.split("(")[0].strip()
        record = model.member(current, name)
        if record is None:
            return None
        current = record["return"]
    return current


def bind(table, name, line, type_name):
    """Record that `name` carries `type_name` from `line` on.

    Bindings are KEPT IN ORDER and never collapsed to one value per name: a name is reused across
    methods in this adapter (`application` is `ApplicationEntry?` in one method and `IApplication` in
    the next), and one first-in-file value typed the reads of one method with the other method's type
    - MEASURED: that both LOST real routes and invented others. `type_name` is None for `var`, which
    declares a name WITHOUT a type; keeping that apart from "no binding at all" is what tells the
    `head-type-not-established` bucket from `head-not-declared`.
    """
    table.setdefault(name, []).append((line, type_name))


def type_at(table, name, line):
    """The type in force for `name` at `line`: the nearest PRECEDING binding, else the first one.

    The fallback covers a member field read by a method written above its declaration: with nothing
    bound before the use there is no nearer binding to prefer.
    """
    bindings = table.get(name)
    if not bindings:
        return None
    preceding = [entry for entry in bindings if entry[0] <= line]
    chosen = max(preceding, key=lambda entry: entry[0]) if preceding \
        else min(bindings, key=lambda entry: entry[0])
    return chosen[1]


def file_table(lines, model):
    """`{local name: [(line, type), ...]}` for one file, in source order.

    Scopes are NOT modelled: the line is the whole of what this instrument knows about where a
    binding applies, and it is enough for the reuse this adapter actually does.
    """
    table = {}
    for number, line in enumerate(lines, start=1):
        for match in TYPED_LOCAL.finditer(line):
            bind(table, match.group(2), number, simple_type(match.group(1)))
        for match in PATTERN_IS.finditer(line):
            bind(table, match.group(2), number, simple_type(match.group(1)))
        for match in FOREACH.finditer(line):
            bind(table, match.group(2), number,
                 simple_type(match.group(1)) if match.group(1) else None)
        for match in OUT_PARAM.finditer(line):
            bind(table, match.group(2), number,
                 simple_type(match.group(1)) if match.group(1) else None)
        for match in CAST_ASSIGN.finditer(line):
            bind(table, match.group(1), number, simple_type(match.group(2)))
    # A second pass infers the types of `var` locals and of plain assignments, now that the explicit
    # declarations are known. Two passes, because `var x = part.Body` needs `part` first.
    for number, line in enumerate(lines, start=1):
        for match in ASSIGN.finditer(line):
            inferred = expression_type(match.group(2), table, model, number)
            if inferred:
                bind(table, match.group(1), number, inferred)
    return table


def collect_routes(source_dir=ADAPTER_SRC, model=None):
    """Every COM member the adapter calls, with the receiver's type and the call sites.

    Returns the routes, the unresolved counts by KIND, the sites of each kind, and the same two again
    by REASON. The kind says what was missing; the reason says WHY, which is what makes the remainder
    readable instead of a single unexplained number.
    """
    model = model or build_model()
    routes = collections.OrderedDict()
    unresolved = collections.Counter()
    unresolved_sites = collections.defaultdict(list)
    reasons = collections.Counter()
    reason_sites = collections.defaultdict(list)
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
                    receiver_type = expression_type(receiver_expr, table, model, number)
                    if receiver_type is None:
                        # The head is what the chain STARTS from, and the four outcomes below are not
                        # the same thing: two of them are a gap in THIS instrument and two are not.
                        # Counting them as one number made an ordinary `list.Count` and a lost COM
                        # route look alike.
                        head = receiver_expr.split(".")[0].split("(")[0].strip()
                        bound = type_at(table, head, number)
                        if head not in table:
                            kind, reason = "foreign-receiver", "head-not-declared"
                        elif bound is None:
                            kind, reason = "unknown", "head-type-not-established"
                        elif not model.is_com_interface(bound):
                            kind, reason = "unknown", "head-type-not-com"
                        else:
                            kind, reason = "unknown", "chain-broken-on-com-type"
                        site = f"{relative}:{number}:{receiver_expr}"
                        unresolved[kind] += 1
                        unresolved_sites[kind].append(site)
                        reasons[reason] += 1
                        reason_sites[reason].append(site)
                        continue
                    if not model.is_com_interface(receiver_type):
                        continue
                    record = model.member(receiver_type, member)
                    if record is None:
                        site = f"{relative}:{number}:{receiver_type}.{member}"
                        unresolved["unknown-member"] += 1
                        unresolved_sites["unknown-member"].append(site)
                        reasons["member-not-on-interface"] += 1
                        reason_sites["member-not-on-interface"].append(site)
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
    return (list(routes.values()), dict(unresolved), dict(unresolved_sites),
            dict(reasons), dict(reason_sites))


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
                    holder = expression_type(".".join(segments[:-1]), table, model, number)
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
    """The negative control of the inventory: an ADDED call must appear, and only it.

    FOUR SPELLINGS of the same idea are probed SEPARATELY, because each one failed on its own: a bare
    declaration, a declaration behind the interop's NAMESPACE, a type taken from the RETURN of a COM
    call, and a name introduced by an `is` pattern. One shared probe could not say WHICH spelling the
    instrument read.
    """
    failures = []
    checks = 0

    def check(name, condition, detail=""):
        nonlocal checks
        checks += 1
        print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
        if not condition:
            failures.append(name)

    model = build_model()
    com = model.type_names()
    check("модель знает COM-интерфейсы установленного interop",
          {"ksPart", "ksDocument3D", "ksEntity"} <= com,
          f"типов-интерфейсов {len(com)}")
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
          expression_type("(ksRasterFormatParam)document.Document.X()", {}, model, 1)
          == "ksRasterFormatParam")
    check("цепочка разрешается через модель",
          expression_type("part.NewEntity", {"part": [(1, "ksPart")]}, model, 1) is not None)
    check("полное имя сводится к имени типа модели",
          simple_type("KompasAPI7.IView") == "IView"
          and simple_type("global::KompasAPI7.IView") == "IView"
          and simple_type("Kompas6API5.ksPart?") == "ksPart",
          simple_type("global::KompasAPI7.IView"))
    check("объявление полным именем и образец `is` типизируют имя",
          type_at(file_table(["KompasAPI7.IDrawingObject? axis = null;"], model), "axis", 1)
          == "IDrawingObject"
          and type_at(file_table(["if (x is not KompasAPI7.ICircle circle)"], model),
                      "circle", 1) == "ICircle",
          str(file_table(["KompasAPI7.IDrawingObject? axis = null;"], model)))
    check("имя, ПЕРЕИСПОЛЬЗОВАННОЕ в файле, читается по ближайшему объявлению",
          type_at(file_table(["ksPart a;", "a.X;", "ksEntity a;", "a.Y;"], model), "a", 2)
          == "ksPart"
          and type_at(file_table(["ksPart a;", "a.X;", "ksEntity a;", "a.Y;"], model), "a", 4)
          == "ksEntity")

    with tempfile.TemporaryDirectory() as tmp:
        copy = os.path.join(tmp, "adapter")
        shutil.copytree(ADAPTER_SRC, copy, ignore=shutil.ignore_patterns("bin", "obj"))
        baseline = {route["route"] for route in collect_routes(copy, model)[0]}
        used = set()

        def absent(owner, kind="method"):
            """A member of `owner` the adapter does NOT call.

            An access the adapter already contains (`ksPart.NewEntity`) cannot tell "the instrument
            saw the planted line" from "the instrument saw the old one", and the first edition of
            this check passed for exactly that reason.
            """
            member = next((m for (o, m) in sorted(model.members)
                           if o == owner and model.members[(o, m)]["kind"] == kind
                           and f"{owner}.{m}" not in baseline and (o, m) not in used), None)
            if member:
                used.add((owner, member))
            return member

        def spelling(owner):
            """`owner` as the adapter writes it: behind its interop NAMESPACE when it has one."""
            tag = (model.types.get(owner) or {}).get("tag")
            return f"{NAMESPACE[tag]}.{owner}" if tag in NAMESPACE else owner

        def call(owner, member):
            return f"{member}()" if model.members[(owner, member)]["kind"] == "method" else member

        # 1. Bare declaration - the control the instrument already had.
        candidate = absent("ksPart")
        check("найдено обращение, которого в адаптере нет", candidate is not None, str(candidate))

        # 2. The SAME kind of declaration, spelled behind the interop namespace.
        qualified = absent("ksPart")

        # 3. The receiver's type is the RETURN of a COM member call, not a spelling at all.
        hop = next(((o, m) for (o, m) in sorted(model.members)
                    if o in com and (model.members[(o, m)]["return"] or "") in com
                    and absent(model.members[(o, m)]["return"])), (None, None))
        hop_type = model.members[hop]["return"] if hop[0] else None
        inner = hop_type and absent(hop_type)
        check("найдена пара «член, возвращающий COM-интерфейс» + член на нём",
              hop[0] is not None and inner is not None,
              f"{hop[0]}.{hop[1]} -> {hop_type}.{inner}")

        # 4. The name is introduced by an `is` pattern, again behind the namespace.
        pattern = absent("ksPart")

        probes = {
            "AuditProbe.cs":
                "internal static class AuditProbe {\n"
                "    internal static void Run(ksPart part) {\n"
                f"        part.{call('ksPart', candidate)};\n"
                "    }\n"
                "}\n",
            "AuditProbeQualified.cs":
                "internal static class AuditProbeQualified {\n"
                "    internal static void Run(ksPart part) {\n"
                f"        {spelling('ksPart')} probe = part;\n"
                f"        probe.{call('ksPart', qualified)};\n"
                "    }\n"
                "}\n",
            "AuditProbeFromCall.cs":
                "internal static class AuditProbeFromCall {\n"
                f"    internal static void Run({hop[0]} source) {{\n"
                f"        var probe = source.{call(hop[0], hop[1])};\n"
                f"        probe.{call(hop_type, inner)};\n"
                "    }\n"
                "}\n",
            "AuditProbePattern.cs":
                "internal static class AuditProbePattern {\n"
                "    internal static void Run(object candidate) {\n"
                f"        if (candidate is {spelling('ksPart')} probe)\n"
                "        {\n"
                f"            probe.{call('ksPart', pattern)};\n"
                "        }\n"
                "    }\n"
                "}\n",
        }
        paths = []
        for name, text in probes.items():
            paths.append(os.path.join(copy, name))
            with open(paths[-1], "w", encoding="utf-8") as handle:
                handle.write(text)

        added, kinds, _sites, reason_counts, _reason_sites = collect_routes(copy, model)
        found = {route["route"]: route for route in added}

        def planted(route, receiver_type):
            record = found.get(route)
            return record is not None and record["receiver_type"] == receiver_type

        check("подсаженный вызов появился в инвентаре", planted(f"ksPart.{candidate}", "ksPart"),
              f"маршрутов {len(found)}")
        check("объявление ПОЛНЫМ именем разрешено",
              planted(f"ksPart.{qualified}", "ksPart"), f"Kompas6API5.ksPart probe")
        check("тип из ВОЗВРАТА вызванного члена разрешён",
              planted(f"{hop_type}.{inner}", hop_type), f"var probe = source.{hop[1]}")
        check("имя из образца `is` разрешено",
              planted(f"ksPart.{pattern}", "ksPart"), f"candidate is Kompas6API5.ksPart probe")
        check("у каждого нетипизированного получателя РОВНО одна причина",
              sum(reason_counts.values()) == sum(kinds.values()),
              f"причин {sum(reason_counts.values())} при {sum(kinds.values())}")

        for path in paths:
            os.remove(path)
        removed = {route["route"] for route in collect_routes(copy, model)[0]}
        check("после снятия строк маршруты исчезли",
              not {f"ksPart.{candidate}", f"ksPart.{qualified}", f"{hop_type}.{inner}",
                   f"ksPart.{pattern}"} & removed)
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

    # The instrument names the installation it read, and where that path came from: a wrong root
    # must be visible in the output, not only in a failure that reads as a product defect.
    print(f"КОМПАС: {kompas_install.install_root()} (источник: {kompas_install.install_source()})")

    if args.self_test:
        return self_test()

    model = build_model()
    routes, counts, sites, reasons, reason_sites = collect_routes(args.source, model)
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
            "by_reason": reasons,
            "sites": {key: value[:200] for key, value in sites.items()},
            "reason_sites": {key: value[:200] for key, value in reason_sites.items()},
            "note": "получатель не типизирован - это НЕ доказательство отсутствия маршрута; "
                    "перечислено честно, чтобы пробел прибора не читался как полнота. `by_reason` "
                    "называет ПРИЧИНУ каждой корзины; пробелы прибора - только "
                    "`chain-broken-on-com-type` и `member-not-on-interface`",
        },
    }
    out = args.out or default_out("routes")
    save_json(out, result)
    print(f"COM-маршрутов: {len(routes)} ({dict(by_assembly)})")
    print(f"получателей без типа: {sum(counts.values())} {counts}")
    print(f"причины: {reasons}")
    print(f"записано: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
