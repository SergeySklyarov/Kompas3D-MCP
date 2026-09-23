"""Query the dumped API5 metadata for exact member signatures.

The adapter must not be written against remembered signatures, and guessing them produces
runtime InvalidCast/missing-member failures that look like КОМПАС bugs. P0.2 dumps every
interface and enum of Interop.Kompas6API5.dll to docs/compatibility/kompas-api5-metadata.json;
this script is the lookup on top of it.

Usage:
  python scripts/query-api-metadata.py ksSketchDefinition
  python scripts/query-api-metadata.py ksBody --grep Volume
  python scripts/query-api-metadata.py --enum ksObj3dTypeEnum --grep extrusion
"""

import argparse
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DB = os.path.join(ROOT, "docs", "compatibility", "kompas-api5-metadata.json")


def load(path):
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("type", nargs="?", help="type name, e.g. ksSketchDefinition")
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--grep", default=None, help="substring filter on member name")
    ap.add_argument("--enum", default=None, help="dump an enum's numeric values")
    ap.add_argument("--list-types", action="store_true")
    ap.add_argument("--max", type=int, default=200)
    args = ap.parse_args()

    db = load(args.db)
    types = db["types"]

    if args.list_types:
        for t in types:
            print(f"{t['kind']:10} {t['full_name']}")
        return 0

    target = args.enum or args.type
    if not target:
        print("give a type name, --enum NAME, or --list-types", file=sys.stderr)
        return 2

    needle = target.lower()
    shown = 0
    for t in types:
        name = (t.get("full_name") or "").lower()
        short = t.get("full_name", "").split(".")[-1].lower()
        if short != needle and needle not in name:
            continue

        if t["kind"] == "enum":
            values = t.get("values") or {}
            items = [(k, v) for k, v in values.items()]
            if args.grep:
                items = [(k, v) for k, v in items if args.grep.lower() in k.lower()]
            print(f"ENUM {t['full_name']} ({len(items)} members shown)")
            for k, v in sorted(items, key=lambda kv: kv[1]):
                print(f"  {v:>10}  {k}")
            shown += 1
            continue

        members = t.get("members") or []
        printed_header = False
        for m in members:
            label = m.get("name") or ""
            if args.grep and args.grep.lower() not in label.lower():
                continue
            if not printed_header:
                print(f"=== {t['kind']} {t['full_name']}  guid={t.get('interface_guid')}")
                printed_header = True
            if m["kind"] == "method":
                print(f"    {m.get('signature')}")
            elif m["kind"] == "property":
                rw = "get;set" if m.get("can_write") else "get"
                print(f"    prop {m.get('type')} {label} {{{rw}}}")
            else:
                print(f"    {m['kind']} {label} {m.get('type','')} {m.get('constant') or ''}")
            shown += 1
            if shown > args.max:
                print("... (truncated; raise --max)")
                return 0

    if shown == 0:
        print(f"no match for {target!r} (try --list-types)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
