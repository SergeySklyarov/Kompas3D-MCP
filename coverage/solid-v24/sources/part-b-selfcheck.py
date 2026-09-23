"""Self-check for the part-B deliverables (offline)."""
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
d = json.load(io.open(os.path.join(HERE, "entries.json"), encoding="utf-8"))
ops = d["operations"]
print("JSON VALID: operations=%d families=%d" % (len(ops), len(d["families"])))

by_fam = {}
for o in ops:
    f = o["family"]
    m = len(o.get("modes", []))
    cv = len(o.get("copy_variants", []))
    by_fam.setdefault(f, [0, 0, 0, []])
    by_fam[f][0] += 1
    by_fam[f][1] += m
    by_fam[f][2] += cv
    by_fam[f][3].append(o["level"])
tot = [0, 0, 0]
for f in sorted(by_fam):
    n, m, cv, lv = by_fam[f]
    md = sum(1 for x in lv if x == "metadata_found")
    doc = sum(1 for x in lv if x == "documented")
    tot[0] += n
    tot[1] += m
    tot[2] += cv
    print("  %-7s ops=%-2d modes=%-2d copy_variants=%-2d metadata_found=%d documented=%d" % (f, n, m, cv, md, doc))
print("  TOTAL   ops=%d modes=%d copy_variants=%d" % tuple(tot))

bad = [o["id"] for o in ops if o["level"] in ("runtime_verified", "mcp_verified", "mcp_implemented")]
print("forbidden-level records:", bad or "none")
print("classification values:", sorted({o["classification"] for o in ops}))
print("outside_scope records:", [o["id"] for o in ops if o["classification"] == "outside_scope"] or "none")

# every OQ referenced in entries.json must exist in open-questions.md
refs = set(re.findall(r"OQ-B-\d+", json.dumps(d, ensure_ascii=False)))
oq_md = io.open(os.path.join(HERE, "open-questions.md"), encoding="utf-8").read()
declared = set(re.findall(r"OQ-B-\d+", oq_md))
print("referenced OQ:", len(refs), "declared headings:", len(set(re.findall(r"### (OQ-B-\d+)", oq_md))))
missing = sorted(refs - declared)
print("referenced but NOT documented in open-questions.md:", missing or "none")
