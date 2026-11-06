"""Применимость шага подтверждения записи клиента (Trust) у `dsh web`.

Что изменилось. Прежний клиент приёмки (WorkBuddy AI) не запускал запись без подтверждения
владельца: хранилище `mcp-approvals.json` держало ключ `<configHash>::<serverName>`, и прибор сверял
с ним действующую запись. Клиент приёмки - `dsh web` (решение заказчика 06.11.2026), и такого
хранилища у него НЕТ.

Почему нет. Подтверждение у dsh - не свойство записи, а разовое решение на запрос: сессионная
политика `ask`/`never`, ответчик спрашивается на каждый вызов (`@deepseek-ai/dsh-user-approval`,
README «Setting the policy»); пакет `@deepseek-ai/dsh-mcp-client` поля подтверждения не имеет и
описывает только подключение (README «Minimal configuration»). Записи, которую можно подтвердить
заранее, не существует.

Что измеряется. Прибор не печатает PASS по несуществующему хранилищу: он называет ПРИМЕНИМОСТЬ шага
и проверяет то, что проверить можно, - запись сервера в профиле dsh разобрана, транспорт stdio,
команда названа. Отдельной строкой - отказ, если в известных местах появится хранилище
подтверждений: тогда шаг снова нужен, а прибор его ещё не читает.

КУДА ПИШЕТСЯ ОТЧЁТ (долг 61, исправлен 22.10.2026). Умолчание `--out` - каталог доказательств
поставки, на которую указывает запись клиента: `<каталог доказательств поставки>/installed/`.
Поставку определить нельзя - прибор ОТКАЗЫВАЕТ и просит задать `--out`.

Usage:
  PY -I scripts/verify-mcp-approval.py [--config <cordis.patch.yml>] [--name kompas]
      [--out <путь>]            # умолчание - <каталог доказательств поставки>/installed/
      [--self-test]
"""

import json
import os
import sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# INVARIANT: the instrument runs as `PY -I scripts/<name>.py`; `-I` keeps the script directory off
# `sys.path`, so the shared module is reachable only by adding that directory here.
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from localconfig import client_delivery, dsh_mcp_entry, dsh_profile_path  # noqa: E402

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# The whole answer of the instrument, said once and quoted into the report: the client has no
# per-record approval, so there is nothing to be "not yet approved".
NOT_APPLICABLE = (
    "подтверждение записи у dsh не хранится: это разовое решение на запрос (сессионная политика "
    "ask/never, пакет @deepseek-ai/dsh-user-approval), а не свойство записи; пакет "
    "@deepseek-ai/dsh-mcp-client поля подтверждения не имеет. Шаг Trust не применим.")


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def approval_store(config_path, home=None):
    """First existing per-server approval store near the profile → path, else None.

    INVARIANT: the check stays SENSITIVE - a store that appears must turn the row into a FAIL that
    names the file, not into a permanent "not applicable". LIMIT: only the name the previous client
    used and the two obvious dsh names are looked for; a store elsewhere is not discovered.
    """
    profile_dir = os.path.dirname(os.path.abspath(config_path))
    dsh_home = os.path.join(home or os.path.expanduser("~"), ".dsh")
    for path in (os.path.join(profile_dir, "mcp-approvals.json"),
                 os.path.join(profile_dir, "approvals.json"),
                 os.path.join(dsh_home, "mcp-approvals.json"),
                 os.path.join(dsh_home, "approvals.json")):
        if os.path.isfile(path):
            return path
    return None


# ─────────────────────── DEFAULT `--out`: THE DELIVERY'S EVIDENCE DIRECTORY ───────────────────────
#
# MEASURED: the default used to follow the audit source, i.e. the last `--all` run, so the report
# landed in a foreign run directory and was moved by hand. The directory now comes from the delivery
# the entry points at, through the SAME function `verify-client-entry.py` calls - one implementation
# for both instruments, otherwise the passport and the approval report name different deliveries.
# History: docs/decisions/tests.md#approval-report-path


def default_out(command, root=ROOT):
    """Путь отчёта по умолчанию: `<каталог доказательств поставки>/installed/`. → (путь, источник, отказ).

    INVARIANT: the directory is the delivery the client entry points at, never the run that happens
    to be the audit source. LIMIT: an unresolvable delivery is a refusal asking for `--out`, not a
    silent fallback - a fallback would be the same defect, only quieter.
    History: docs/decisions/tests.md#approval-report-path
    """
    info, problem = client_delivery(command, root)
    if info is None:
        return None, None, problem
    return (os.path.join(info["evidence_dir"], "installed", "mcp-approval-check.json"),
            "поставка записи клиента: " + info["evidence_dir"], None)


def self_test():
    """Instrument battery: the applicability rule and the store-sensitivity control, on trees built HERE.

    Arm one: a profile layer yields the entry, and a missing one is a NAMED refusal. Arm two: a
    store file at a known name makes `approval_store` find it - the row would then FAIL instead of
    claiming "not applicable" forever. Arm three: the default `--out` follows the delivery the entry
    points at and refuses to guess when the delivery cannot be resolved. The tree sits in a
    temporary directory: a control reading the real installation would measure that machine.
    """
    import tempfile

    checks, results = [], []

    def check(name, condition, detail=""):
        checks.append(condition)
        results.append((name, detail if not condition else "ok"))

    layer = (
        "- insert:\n"
        "    - id: mcp-kompas\n"
        "      name: '@deepseek-ai/dsh-mcp-client'\n"
        "      config:\n"
        "        serverName: kompas\n"
        "        transport: stdio\n"
        "        command: C:\\install\\current\\KompasMcp.Host.exe\n"
        "        args: ['--config', 'C:\\install\\kompas.json']\n"
    )
    flat_layer = (
        "- id: mcp-kompas\n"
        "  name: '@deepseek-ai/dsh-mcp-client'\n"
        "  config:\n"
        "    serverName: kompas\n"
        "    transport: stdio\n"
        "    command: C:\\install\\current\\KompasMcp.Host.exe\n"
    )
    with tempfile.TemporaryDirectory(prefix="_mcp-approval-selftest-") as scratch:
        home = os.path.join(scratch, "home")
        os.makedirs(home)
        profile = os.path.join(scratch, "cordis.patch.yml")
        with open(profile, "w", encoding="utf-8") as handle:
            handle.write(layer)
        entry, problem = dsh_mcp_entry(profile, "kompas")
        check("запись клиента разобрана из слоя dsh",
              entry is not None and entry["command"].endswith("KompasMcp.Host.exe"), str(problem))
        entry, problem = dsh_mcp_entry(profile, "absent")
        check("нет записи - названный отказ, а не пустая запись",
              entry is None and "нет записи" in problem, str(problem))
        with open(profile, "w", encoding="utf-8") as handle:
            handle.write(flat_layer)
        entry, problem = dsh_mcp_entry(profile, "kompas")
        check("плоская запись - FAIL с подсказкой формы insert",
              entry is None and "ПЛОСКОЙ" in (problem or "") and "insert" in (problem or ""),
              str(problem))
        with open(profile, "w", encoding="utf-8") as handle:
            handle.write(layer)

        check("хранилища нет - шаг неприменим (прибор не выдумывает PASS)",
              approval_store(profile, home) is None)
        store = os.path.join(home, ".dsh", "mcp-approvals.json")
        os.makedirs(os.path.dirname(store))
        with open(store, "w", encoding="utf-8") as handle:
            json.dump({}, handle)
        check("появившееся хранилище прибор находит - строка станет отказом",
              approval_store(profile, home) == store, str(approval_store(profile, home)))

        install = os.path.join(scratch, "install")
        repo = os.path.join(scratch, "repo")
        os.makedirs(os.path.join(install, "current"))
        os.makedirs(os.path.join(install, "versions", "v9.9.9"))
        passport_dir = os.path.join(repo, "docs", "acceptance", "delivery-v9.9.9-selftest")
        evidence_dir = os.path.join(repo, "docs", "acceptance", "evidence",
                                    "delivery-v9.9.9-selftest")
        os.makedirs(passport_dir)
        os.makedirs(evidence_dir)
        with open(os.path.join(install, "installed.json"), "w", encoding="utf-8") as handle:
            json.dump({"tag": "v9.9.9"}, handle)
        with open(os.path.join(passport_dir, "delivery-passport.json"), "w", encoding="utf-8") as handle:
            json.dump({"package": {"host_dll_sha256": "b"}}, handle)
        command = os.path.join(install, "current", "KompasMcp.Host.exe")

        out, source, problem = default_out(command, repo)
        expected = os.path.join(evidence_dir, "installed", "mcp-approval-check.json")
        check("отчёт по умолчанию идёт в каталог доказательств поставки",
              out is not None and os.path.normcase(out) == os.path.normcase(expected), str(problem))

        os.remove(os.path.join(install, "installed.json"))
        out, source, problem = default_out(command, repo)
        check("поставка не опознана - отказ назван, а не угадан путь",
              out is None and bool(problem) and "тег установки не найден" in problem, str(problem))
        legacy = os.path.join("scratch", "mcp-smoke")
        check("каталог прогона аудита не возвращается ни на одном входе",
              not any(legacy in str(detail) for _name, detail in results))

    print("=== батарея прибора (самопроверка) ===")
    for name, detail in results:
        print("  [%s] %s%s" % ("PASS" if detail == "ok" else "FAIL", name,
                               "" if detail == "ok" else " - " + detail))
    ok = all(checks)
    print("  проверок пройдено: %d из %d" % (sum(1 for item in checks if item), len(checks)))
    print("  батарея: %s" % ("ЗЕЛЁНАЯ" if ok else "КРАСНАЯ"))
    return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()
    config_path = os.path.abspath(argument("--config") or dsh_profile_path())
    name = argument("--name") or "kompas"
    out_arg = argument("--out")
    entry, entry_problem = dsh_mcp_entry(config_path, name)
    command = (entry or {}).get("command") or ""

    # INVARIANT: the entry is read BEFORE the report path is chosen - the default path is the
    # delivery the entry points at, so without the entry there is no path. LIMIT: no report is
    # written then; a directory taken by guesswork is exactly what the defect below was.
    if out_arg:
        out, out_from = os.path.abspath(out_arg), "переопределение --out"
    else:
        out, out_from, problem = default_out(command) if command else (
            None, None, "в записи клиента нет команды - поставку определить нельзя")
        if out is None:
            print("ОТКАЗ: путь отчёта по умолчанию не разрешён — %s" % problem)
            print("Задайте --out явно: прибор не пишет отчёт в каталог постороннего прогона.")
            return 2

    report = {
        "run_id": datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ"),
        "checked_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "client": "dsh web",
        "server_name": name,
        "profile": config_path,
        "report_path": out,
        "report_path_from": out_from,
        # Named in the report itself, not implied: the reader must see that "not applicable" is a
        # measured fact about this client, not a missing check.
        "trust_required": False,
        "not_applicable_reason": NOT_APPLICABLE,
        "approval_store": None,
        "rows": [],
    }
    rows = report["rows"]

    def row(cid, description, ok, detail=""):
        rows.append({"id": cid, "check": description, "verdict": "PASS" if ok else "FAIL",
                     "detail": detail})
        print("  [%s] %-4s %s" % ("PASS" if ok else "FAIL", cid, description))
        if detail:
            print("         %s" % detail)

    row("A01", "запись '%s' найдена в профиле клиента dsh" % name, entry is not None,
        entry_problem or config_path)
    if entry is not None:
        row("A02", "запись — stdio-команда существующего Host",
            entry.get("transport") == "stdio" and os.path.isfile(command),
            "transport=%s command=%s" % (entry.get("transport"), command))

    store = approval_store(config_path)
    report["approval_store"] = store
    row("A03", "хранилища подтверждений записи у dsh нет — шаг не применим", store is None,
        NOT_APPLICABLE if store is None else
        "НАЙДЕНО хранилище %s: шаг снова применим, прибор его не читает - нужна доработка" % store)

    os.makedirs(os.path.dirname(out), exist_ok=True)
    failed = [item for item in rows if item["verdict"] != "PASS"]
    report["verdict"] = "PASS" if rows and not failed else "FAIL"
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(report, handle, ensure_ascii=False, indent=2)

    print("\n" + NOT_APPLICABLE)
    print("\nИтог: %s — строк %d, отказов %d" % (report["verdict"], len(rows), len(failed)))
    print("Отчёт: %s  [путь: %s]" % (out, out_from))
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
