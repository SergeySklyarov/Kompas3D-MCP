"""The whole API compliance audit in one command (work order `API_AUDIT_TOOLS`, item 6).

  python scripts/api-audit.py --report <all-report.json> [--host <KompasMcp.Host.exe>]
  python scripts/api-audit.py --self-test

Runs the four measurements in order and then the generator, so `docs/API_COMPLIANCE.json` and
`docs/API_COMPLIANCE.md` are always rebuilt from measurements of the same run:

  1. `api-audit-wire.py`    - tools on the wire against `schemas/` and the coverage records;
  2. `api-audit-routes.py`  - the COM members the adapter calls, by receiver type;
  3. `api-audit-docs.py`    - every one of those members against the v24 SDK help, over the wire;
  4. `api-audit-claims.py`  - the matrix's own claims against the acceptance run and its call journal;
  5. `api-audit-build.py`   - the machine-readable audit and the Markdown rendered FROM it.

The acceptance report of step 4 is the `--all` run of the delivery; without it the claims cannot be
judged, and the audit refuses rather than reporting an empty, "clean" claim table.

INVARIANT: `--report` is an argument, not a default. A report belongs to ONE run; a search over
`docs/acceptance/evidence/*` answers with whatever file is newest, and nothing in the result says
which run that was.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from api_audit_common import ROOT, default_out, evidence_dir  # noqa: E402

PYTHON = sys.executable
STEPS = ("api-audit-wire.py", "api-audit-routes.py", "api-audit-docs.py", "api-audit-claims.py",
         "api-audit-build.py")


def run(script, extra):
    path = os.path.join(ROOT, "scripts", script)
    print(f"\n=== {script} " + "-" * max(0, 60 - len(script)))
    done = subprocess.run([PYTHON, path] + extra, cwd=ROOT)
    if done.returncode != 0:
        raise SystemExit(f"{script} завершился с кодом {done.returncode}")
    return done.returncode


def resolve_report(named):
    """The acceptance report the claims are judged by, or a refusal naming what is missing.

    MEASURED: a hand-written default read a report taken before a whole block of rows existed, and
    the matrix claims then read as over-claims that do not exist; the result never said which run it
    came from. So the caller names the run, and the instrument only checks that the file is there.
    History: docs/decisions/api-audit.md#report-is-an-argument
    """
    if not named:
        raise SystemExit("нужен --report: отчёт приёмки --all, на котором судятся заявки матрицы "
                         "(прибор не выбирает отчёт за вызывающего)")
    if not os.path.isfile(named):
        raise SystemExit(f"нет отчёта приёмки по указанному пути: {named}")
    return os.path.abspath(named)


def refuses(call):
    """True when the call refuses through `SystemExit`: the refusal is what the arm checks."""
    try:
        call()
    except SystemExit:
        return True
    return False


def self_test():
    failures = []

    def check(name, condition, detail=""):
        print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
        if not condition:
            failures.append(name)

    with tempfile.TemporaryDirectory(prefix="_api-audit-selftest-") as scratch:
        stale = os.path.join(scratch, "docs", "acceptance", "evidence", "20260101-all-0000000")
        os.makedirs(stale)
        stale_report = os.path.join(stale, "all-report.json")
        named = os.path.join(scratch, "other-run.json")
        for path in (stale_report, named):
            with open(path, "w", encoding="utf-8") as fh:
                json.dump({"rows": [], "calls": []}, fh)

        check("без --report прибор отказывает",
              refuses(lambda: resolve_report(None)))
        check("названный отчёт берётся как указан, а не самый свежий на диске",
              resolve_report(named) == os.path.abspath(named), named)
        check("несуществующий отчёт назван отказом",
              refuses(lambda: resolve_report(os.path.join(scratch, "нет.json"))))

    for script in STEPS:
        path = os.path.join(ROOT, "scripts", script)
        done = subprocess.run([PYTHON, path, "--self-test"], cwd=ROOT, capture_output=True, text=True)
        tail = [line for line in (done.stdout or "").splitlines() if line.startswith("Самопроверка")]
        ok = done.returncode == 0
        print(f"  [{'PASS' if ok else 'FAIL'}] {script}: "
              f"{tail[-1] if tail else 'нет итоговой строки'}")
        if not ok:
            failures.append(script)
    print(f"\nСамопроверка аудита: {len(STEPS)} приборов и правило отчёта приёмки, {len(failures)} FAIL"
          + (f" - {failures}" if failures else ""))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Аудит соответствия API одной командой.")
    parser.add_argument("--host", default=None, help="KompasMcp.Host.exe измеряемой сборки")
    parser.add_argument("--report", default=None, help="отчёт приёмки --all")
    parser.add_argument("--evidence", default=None, help="каталог доказательств этого аудита")
    parser.add_argument("--self-test", action="store_true", help="проверить все приборы")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    report = resolve_report(args.report)
    print(f"Отчёт приёмки: {report}")
    evidence = args.evidence or evidence_dir()

    host_args = ["--host", args.host] if args.host else []
    run("api-audit-wire.py", host_args + ["--out", os.path.join(evidence, "wire.json")])
    run("api-audit-routes.py", ["--out", os.path.join(evidence, "routes.json")])
    run("api-audit-docs.py", ["--routes", os.path.join(evidence, "routes.json"),
                              "--out", os.path.join(evidence, "docs.json")])
    run("api-audit-claims.py", ["--report", report,
                                "--out", os.path.join(evidence, "claims.json")])
    run("api-audit-build.py", ["--evidence", evidence])
    print(f"\nАудит собран. Доказательства: {evidence}")
    print("Результат: docs/API_COMPLIANCE.json и docs/API_COMPLIANCE.md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
