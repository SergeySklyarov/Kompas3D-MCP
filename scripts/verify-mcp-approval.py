"""Проверка того, что РАБОЧИЙ КЛИЕНТ подтвердил запись `kompas` (шаг «Trust»).

Зачем отдельным шагом. `verify-client-entry.py` доказывает, что запись клиента рабочая: сервер
отвечает, инструментов 32, CAD-канал живой. Но клиент запускает запись не потому, что она верна, а
потому, что пользователь её подтвердил. Между этими двумя утверждениями стоит хранилище
подтверждений клиента, и разница видна только в нём.

Что именно измеряется. Клиент WorkBuddy AI хранит подтверждения в `mcp-approvals.json` — словарь
вида `<configHash>::<serverName>` → время выдачи. Ключ считается по САМОЙ записи:

    sha256( command + "|" + sorted(args).join(",") + "|" + sorted(env.keys()).join(",") )

(`calculateConfigHash` в `ConnectorService`; env — только имена переменных, без значений). Поэтому
любая правка команды, аргументов или набора переменных СНИМАЕТ прежнее подтверждение: старая запись
остаётся в файле, но под новым хешем её нет. Отсюда строка A05.

Почему это не «проверка настройки» и не дубль `verify-client-entry.py`. Тот скрипт запускает Host
сам и потому проходит при ЛЮБОМ состоянии подтверждений. Здесь Host не запускается вовсе: строка
A04 отвечает на вопрос «запустит ли клиент эту запись в сессии агента», и при пустом хранилище
ответ «нет» — что и наблюдается: 32 инструмента есть у сервера и ни одного у агента.

Подтверждение выдаёт только интерфейс клиента (`approveMcpServer` / `toggleMcpServer`). Записывать
его скриптом запрещено: это и есть обход подтверждения доверия. Скрипт только читает.

КУДА ПИШЕТСЯ ОТЧЁТ (долг 61, исправлен 22.10.2026). Умолчание `--out` - каталог доказательств
поставки, на которую указывает запись клиента: `<каталог доказательств поставки>/installed/`.
Каталог поставки разрешается общим правилом (`localconfig.client_delivery`), тем же, что у
`verify-client-entry.py`, - включая запись через `<InstallRoot>/current`. Поставку определить
нельзя - прибор ОТКАЗЫВАЕТ и просит задать `--out`; в каталог «источника аудита» (последнего
отчёта `--all`) он больше не пишет: 22.10.2026 отчёт лёг в каталог чужого наряда, и его переносили
руками.

Usage:
  PY -I scripts/verify-mcp-approval.py [--config <mcp.json>] [--name kompas]
      [--approvals <mcp-approvals.json>]
      [--out <путь>]            # умолчание - <каталог доказательств поставки>/installed/
      [--self-test]
"""

import hashlib
import json
import os
import sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# INVARIANT: the instrument runs as `PY -I scripts/<name>.py`; `-I` keeps the script directory off
# `sys.path`, so the shared module is reachable only by adding that directory here.
sys.path.insert(0, os.path.join(ROOT, "scripts"))
from localconfig import client_delivery  # noqa: E402

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

REQUIRED_ACTION = (
    "Подтверждение выдаётся только интерфейсом клиента, и только в самом приложении WorkBuddy AI "
    "(не в КОМПАС-3D и не в терминале): панель «Connector Management» → кнопка «Custom connectors» "
    "(иконка «+» в шапке панели) → окно «MCP Server Management» → карточка kompas → текст «This MCP "
    "server requires your trust before first connection.» → кнопка «Trust». Второй вход в то же "
    "окно: панель плагинов → вкладка Skills → кнопка «MCP Servers» в правом верхнем углу (она "
    "показывается не всегда, поэтому первый путь надёжнее). После этого нужна новая сессия: список "
    "инструментов агента собирается при запуске сессии."
)


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def config_hash(entry):
    """Хеш записи по алгоритму клиента — буква в букву, иначе ключ не совпадёт.

    `entry.args` сортируется как строки (в клиенте — `Array.prototype.sort()` по умолчанию), имена
    переменных окружения сортируются отдельно и склеиваются запятыми. Значения переменных в хеш НЕ
    входят — так задумано клиентом, и это надо повторять, а не «улучшать».
    """
    if entry.get("command"):
        args = sorted(str(a) for a in (entry.get("args") or []))
        env_keys = sorted(str(k) for k in (entry.get("env") or {}))
        material = "%s|%s|%s" % (entry.get("command") or "", ",".join(args), ",".join(env_keys))
    elif entry.get("url"):
        try:
            from urllib.parse import urlsplit
            parts = urlsplit(entry["url"])
            material = "%s://%s" % (parts.scheme, parts.netloc)
        except Exception:  # noqa: BLE001 — правило разбора URL не должно ронять проверку
            material = entry["url"]
    else:
        material = json.dumps(entry, sort_keys=True)
    return hashlib.sha256(material.encode("utf-8")).hexdigest(), material


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
    """Instrument battery: both halves of the default-`--out` rule, on trees built HERE.

    Arm one: an entry through `<InstallRoot>/current` resolves to the delivery's evidence directory,
    and the report lands in its `installed/` subdirectory. Arm two: a delivery that cannot be
    resolved is a NAMED refusal, not a path - the negative control of debt 61, which must fire on
    every input. The tree sits in a temporary directory because a control reading the real
    installation would measure that machine, not the rule.
    History: docs/decisions/tests.md#approval-report-path
    """
    import json as _json
    import tempfile

    checks, results = [], []
    with tempfile.TemporaryDirectory(prefix="_mcp-approval-selftest-") as scratch:
        install = os.path.join(scratch, "install")
        repo = os.path.join(scratch, "repo")
        os.makedirs(os.path.join(install, "current"))
        os.makedirs(os.path.join(install, "versions", "v9.9.9"))
        passport_dir = os.path.join(repo, "docs", "acceptance", "delivery-v9.9.9-selftest")
        evidence_dir = os.path.join(repo, "docs", "acceptance", "evidence",
                                    "delivery-v9.9.9-selftest")
        os.makedirs(passport_dir)
        os.makedirs(evidence_dir)
        with open(os.path.join(install, "installed.json"), "w", encoding="utf-8") as fh:
            _json.dump({"tag": "v9.9.9"}, fh)
        with open(os.path.join(passport_dir, "delivery-passport.json"), "w", encoding="utf-8") as fh:
            _json.dump({"package": {"host_dll_sha256": "b"}}, fh)
        command = os.path.join(install, "current", "KompasMcp.Host.exe")

        out, source, problem = default_out(command, repo)
        expected = os.path.join(evidence_dir, "installed", "mcp-approval-check.json")
        checks.append(out is not None and os.path.normcase(out) == os.path.normcase(expected))
        results.append(("follows_delivery", command, out or problem))

        # (2) отказ: тег установки не найден - пути нет, причина названа
        os.remove(os.path.join(install, "installed.json"))
        out, source, problem = default_out(command, repo)
        checks.append(out is None and bool(problem) and "тег установки не найден" in problem)
        results.append(("refuses_unknown", command, out or problem))

        # (3) ОТРИЦАТЕЛЬНЫЙ КОНТРОЛЬ ДОЛГА 61: каталог прогона аудита не возвращается ни на одном входе.
        legacy = os.path.join("scratch", "mcp-smoke")
        leaked = [r for r in results if r[2] and legacy in str(r[2])]
        checks.append(not leaked)

        print("=== батарея прибора (самопроверка) ===")
        for label, subject, got in results:
            print("  %-18s %-46s → %s" % (label, os.path.basename(subject), got))
        print("  %-18s каталог прогона аудита `%s` не возвращается: %s"
              % ("долг-61-контроль", legacy, "ДА" if not leaked else "НЕТ — ДЕФЕКТ НЕ ВЫЛЕЧЕН"))
        ok = all(checks)
        print("  проверок пройдено: %d из %d" % (sum(1 for c in checks if c), len(checks)))
        print("  батарея: %s" % ("ЗЕЛЁНАЯ" if ok else "КРАСНАЯ"))
        return 0 if ok else 1


def main():
    if "--self-test" in sys.argv:
        return self_test()
    config_path = os.path.abspath(argument("--config") or os.path.join(os.path.expanduser("~"), ".workbuddy-ai", "mcp.json"))
    name = argument("--name") or "kompas"
    approvals_path = os.path.abspath(argument("--approvals") or os.path.join(os.path.dirname(config_path), "mcp-approvals.json"))
    out_arg = argument("--out")

    # INVARIANT: the entry is read BEFORE the report path is chosen - the default path is the
    # delivery the entry points at, so without the entry there is no path. LIMIT: no report is
    # written then; a directory taken by guesswork is exactly what the defect below was.
    entry = None
    config_error = None
    try:
        with open(config_path, encoding="utf-8-sig") as fh:
            config = json.load(fh)
        entry = (config.get("mcpServers") or {}).get(name)
    except Exception as exc:  # noqa: BLE001 — причина отказа обязана быть названа
        config_error = "%s: %s" % (type(exc).__name__, exc)

    if out_arg:
        out = os.path.abspath(out_arg)
        out_from = "переопределение --out"
    else:
        command = (entry or {}).get("command") or ""
        out, out_from, problem = default_out(command) if command else (
            None, None, "в записи клиента нет команды - поставку определить нельзя")
        if out is None:
            print("ОТКАЗ: путь отчёта по умолчанию не разрешён — %s" % problem)
            print("Задайте --out явно: прибор не пишет отчёт в каталог постороннего прогона.")
            return 2

    report = {
        "run_id": datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ"),
        "checked_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "server_name": name,
        "config_path": config_path,
        "approvals_path": approvals_path,
        "required_action": REQUIRED_ACTION,
        # ОТКУДА ВЗЯТ ПУТЬ ОТЧЁТА — названо в самом отчёте, а не подразумевается. Читатель обязан
        # видеть, легло ли доказательство в каталог текущего прогона или туда, куда указал --out.
        "report_path": out,
        "report_path_from": out_from,
        "rows": [],
    }
    rows = report["rows"]

    def row(cid, description, ok, detail=""):
        rows.append({"id": cid, "check": description, "verdict": "PASS" if ok else "FAIL", "detail": detail})
        print("  [%s] %-4s %s" % ("PASS" if ok else "FAIL", cid, description))
        if detail:
            print("         %s" % detail)

    digest = None
    row("A01", "запись '%s' найдена в конфигурации клиента" % name, entry is not None,
        config_error or config_path)

    if entry is not None:
        digest, material = config_hash(entry)
        report["config_hash"] = digest
        report["hash_material"] = material
        row("A02", "запись — stdio-команда, хеш клиента вычислим",
            bool(entry.get("command")), "command=%s hash=%s" % (entry.get("command"), digest))

        approvals = None
        try:
            with open(approvals_path, encoding="utf-8") as fh:
                approvals = json.load(fh)
            row("A03", "хранилище подтверждений клиента прочитано",
                isinstance(approvals, dict), "%s, записей %d" % (approvals_path, len(approvals)))
        except FileNotFoundError:
            row("A03", "хранилище подтверждений клиента прочитано", False,
                "файла нет: %s — ни одна запись не подтверждена" % approvals_path)
            approvals = {}
        except Exception as exc:  # noqa: BLE001
            row("A03", "хранилище подтверждений клиента прочитано", False,
                "%s: %s" % (type(exc).__name__, exc))
            approvals = {}

        key = "%s::%s" % (digest, name)
        report["expected_key"] = key
        approved_at = approvals.get(key)
        report["approved_at_ms"] = approved_at
        row("A04", "подтверждение '%s' выдано пользователем (ключ %s…)" % (name, digest[:12]),
            approved_at is not None,
            ("выдано, время %s" % datetime.fromtimestamp(approved_at / 1000, timezone.utc).isoformat(timespec="seconds")
             if approved_at is not None
             else "НЕ выдано: клиент пропускает эту запись при сборке конфигурации сессии "
                  "(`buildDesiredConfigs: skipping untrusted server`), Host не запускается, "
                  "инструменты агенту не видны"))

        stale = sorted(k for k in approvals if k.endswith("::" + name) and k != key)
        row("A05", "устаревших подтверждений того же имени нет",
            not stale,
            ("нет" if not stale
             else "есть %d под другим хешем — запись правилась после подтверждения: %s"
                  % (len(stale), ", ".join(k.split("::")[0][:12] + "…" for k in stale))))

    os.makedirs(os.path.dirname(out), exist_ok=True)
    failed = [r for r in rows if r["verdict"] != "PASS"]
    report["verdict"] = "PASS" if rows and not failed else "FAIL"
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)

    if failed:
        print("\nЧто нужно сделать: " + REQUIRED_ACTION)
    print("\nИтог: %s — строк %d, отказов %d" % (report["verdict"], len(rows), len(failed)))
    print("Отчёт: %s  [путь: %s]" % (out, out_from))
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
