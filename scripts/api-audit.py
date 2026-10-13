"""The whole API compliance audit in one command (work order `API_AUDIT_TOOLS`, item 6).

  python scripts/api-audit.py [--host <KompasMcp.Host.exe>] [--report <all-report.json>]
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
"""

from __future__ import annotations

import argparse
import glob
import os
import subprocess
import sys

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


def latest_acceptance_report(evidence):
    """The `--all` report of the audited run: this audit's own copy first, then the newest on disk.

    A report left over from ANOTHER run is what a hand-written default would silently read, and a
    stale report makes the claims read as over-claims that do not exist.
    """
    local = os.path.join(evidence, "all-report.json")
    if os.path.isfile(local):
        return local
    found = glob.glob(os.path.join(ROOT, "docs", "acceptance", "evidence", "*", "all-report.json"))
    found += glob.glob(os.path.join(ROOT, "docs", "acceptance", "evidence", "*", "*all*.json"))
    found = [path for path in found if os.path.isfile(path)]
    return max(found, key=os.path.getmtime) if found else None


def self_test():
    failures = []
    for script in STEPS:
        path = os.path.join(ROOT, "scripts", script)
        done = subprocess.run([PYTHON, path, "--self-test"], cwd=ROOT, capture_output=True, text=True)
        tail = [line for line in (done.stdout or "").splitlines() if line.startswith("Самопроверка")]
        ok = done.returncode == 0
        print(f"  [{'PASS' if ok else 'FAIL'}] {script}: "
              f"{tail[-1] if tail else 'нет итоговой строки'}")
        if not ok:
            failures.append(script)
    print(f"\nСамопроверка аудита: {len(STEPS)} приборов, {len(failures)} FAIL"
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

    evidence = args.evidence or evidence_dir()
    report = args.report or latest_acceptance_report(evidence)
    if not report:
        raise SystemExit("не найден отчёт приёмки --all: заявки матрицы судить не по чему "
                         "(нужен --report)")

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
