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

КУДА ПИШЕТСЯ ОТЧЁТ (дефект прибора №25, исправлен 21.09.2026). Прежде умолчание `--out` было
ЖЁСТКО прописано в каталог поставки 18.09.2026 (`scratch/mcp-smoke/delivery-20260918/`), то есть
доказательство клиентской записи ложилось не туда, где его ищут паспорт и приёмка, — и прогон без
`--out` на текущей поставке создавал отчёт в каталоге ЧУЖОГО, давно пройденного прогона. Умолчание
теперь ВЫЧИСЛЯЕТСЯ из источника аудита (`docs/API_COMPLIANCE.json` →
`claimed_actions_check.source`, то есть каталог отчётов ТЕКУЩЕГО прогона), а `--out` по-прежнему
переопределяет его. Если источник аудита не разрешается, прибор ОТКАЗЫВАЕТ и называет причину, а не
падает обратно в прежний каталог: молчаливый откат к 18.09 был бы тем же дефектом, только тише.

Usage:
  python scripts/verify-mcp-approval.py [--config <mcp.json>] [--name kompas]
      [--approvals <mcp-approvals.json>]
      [--out <путь>]            # умолчание — каталог источника аудита
      [--self-test]
"""

import hashlib
import json
import os
import sys
from datetime import datetime, timezone

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

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


# ─────────────────────── УМОЛЧАНИЕ `--out`: КАТАЛОГ ТЕКУЩЕГО ПРОГОНА ───────────────────────
#
# Дефект прибора №25 (назван в `SMALL_RECORD_FIXES_REPORT_20260921.md` §5, исправлен 21.09.2026).
# Прежняя строка умолчания знала ровно один каталог — `delivery-20260918`, — и после каждой новой
# поставки он оставался прежним. Доказательство «клиент подтвердил запись» при этом создавалось, и
# прибор возвращал rc=0; ошибочным было только МЕСТО. Поэтому лечится не заменой строки на другую
# строку (следующая поставка повторила бы дефект в третий раз), а ВЫЧИСЛЕНИЕМ: умолчание берётся из
# того же источника, из которого его берёт аудит, — `claimed_actions_check.source`.
AUDIT_JSON = os.environ.get("KOMPAS_AUDIT_JSON", os.path.join(ROOT, "docs", "API_COMPLIANCE.json"))


def audit_reports_dir(audit_path=None):
    """Каталог отчётов ТЕКУЩЕГО прогона по источнику аудита. → (каталог|None, источник, ошибка|None).

    Источник в `docs/API_COMPLIANCE.json` записан маской файлов текущего прогона
    (`scratch/mcp-smoke/<прогон>/*.json`). Каталог — часть источника ДО маски, и маска обязана стоять
    в ИМЕНИ ФАЙЛА, а не в каталоге: маска в каталоге (`scratch/mcp-smoke/*/full.json`) называет
    МНОГО прогонов, и выбрать из них один — догадка. Такой источник прибор ОТКЛОНЯЕТ и называет
    причину; пустой «каталога нет» здесь означал бы «пиши куда хочешь», а это и есть дефект №25.
    """
    path = os.path.abspath(audit_path or AUDIT_JSON)
    try:
        with open(path, encoding="utf-8-sig") as fh:
            doc = json.load(fh)
    except Exception as exc:  # noqa: BLE001 — отказ обязан быть назван, а не уронить прибор
        return None, None, "источник аудита не прочитан (%s: %s)" % (type(exc).__name__, exc)
    if not isinstance(doc, dict):
        return None, None, "источник аудита не словарь: %s" % path
    src = ((doc.get("claimed_actions_check") or {}).get("source") or "").strip()
    if not src:
        return None, None, "в %s нет поля claimed_actions_check.source" % path
    rel = src.replace("\\", "/")
    head, sep, tail = rel.rpartition("/")
    if not sep:
        return None, src, "источник не называет каталога: %s" % src
    if "*" in head or "?" in head:
        return None, src, "маска стоит в КАТАЛОГЕ источника — прогон неоднозначен: %s" % src
    resolved = os.path.normpath(os.path.join(ROOT, head))
    if not os.path.isdir(resolved):
        return None, src, "каталог источника аудита не существует: %s" % resolved
    return resolved, src, None


def default_out():
    """Путь отчёта по умолчанию. → (путь|None, источник|None, ошибка|None)."""
    directory, src, err = audit_reports_dir()
    if directory is None:
        return None, src, err
    return os.path.join(directory, "mcp-approval-check.json"), src, None


def self_test():
    """Батарея прибора: обе половины по КАЖДОМУ из двух свойств умолчания.

    Свойство первое — умолчание СЛЕДУЕТ за источником аудита. Половина «обязано разрешиться»:
    синтетический аудит с источником в существующем каталоге даёт путь в ЭТОМ каталоге. Половина
    «обязано отказать»: синтетический аудит с источником в НЕсуществующем каталоге даёт отказ, а не
    подстановку.

    Свойство второе — прежний каталог (`delivery-20260918`) не возвращается НИКОГДА. Это и есть
    отрицательный контроль дефекта №25: он обязан сработать на любом входе, включая тот, на котором
    прежняя редакция давала ровно `delivery-20260918`.
    """
    scratch = os.path.join(ROOT, "scratch", "_mcp-approval-selftest")
    os.makedirs(scratch, exist_ok=True)
    live_dir = os.path.join(ROOT, "scratch", "mcp-smoke")
    checks, results = [], []

    def probe(label, source, expect_dir, expect_error):
        path = os.path.join(scratch, "audit-%s.json" % label)
        doc = {"claimed_actions_check": {"source": source}} if source is not None else {"meta": {}}
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh, ensure_ascii=False)
        directory, _src, err = audit_reports_dir(path)
        got_dir = os.path.normpath(directory) if directory else None
        ok = ((got_dir == os.path.normpath(expect_dir)) if expect_dir
              else (got_dir is None and bool(err) == expect_error))
        checks.append(ok)
        results.append((label, source, got_dir, err))
        return ok

    # (1) следует за источником: каталог существует
    probe("follows", "scratch/mcp-smoke/sketchplane-20260921/*.json",
          os.path.join(live_dir, "sketchplane-20260921"), None)
    # (2) обязан отказать: каталога нет — подстановка запрещена
    probe("refuses_absent", "scratch/mcp-smoke/__zzz_absent_run/*.json", None, True)
    # (3) обязан отказать: поля нет вовсе (прежняя редакция здесь молча шла в 18.09)
    probe("refuses_missing_field", None, None, True)
    # (4) обязан отказать: маска стоит в КАТАЛОГЕ — прогон неоднозначен, выбирать нельзя
    probe("refuses_glob_dir", "scratch/mcp-smoke/*/full.json", None, True)
    # (5) источник без маски, но с названным каталогом — каталог однозначен и принимается
    probe("plain_file", "scratch/mcp-smoke/sketchplane-20260921/full.json",
          os.path.join(live_dir, "sketchplane-20260921"), None)

    # (6) ОТРИЦАТЕЛЬНЫЙ КОНТРОЛЬ ДЕФЕКТА №25: прежний каталог не возвращается ни на одном входе.
    legacy = "delivery-20260918"
    leaked = [r for r in results if r[2] and legacy in r[2]]
    checks.append(not leaked)

    print("=== батарея прибора (самопроверка) ===")
    for label, source, got_dir, err in results:
        print("  %-18s источник %-46s → %s"
              % (label, str(source)[:46], os.path.relpath(got_dir, ROOT) if got_dir else "ОТКАЗ: " + str(err)))
    print("  %-18s прежний каталог `%s` не возвращается: %s"
          % ("№25-контроль", legacy, "ДА" if not leaked else "НЕТ — ДЕФЕКТ НЕ ВЫЛЕЧЕН"))
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
    if out_arg:
        out = os.path.abspath(out_arg)
        out_from = "переопределение --out"
    else:
        out, src, err = default_out()
        if out is None:
            print("ОТКАЗ: умолчание --out не разрешено — %s" % err)
            print("Задайте --out явно или переиздайте аудит (`docs/API_COMPLIANCE.json`).")
            return 2
        out_from = "источник аудита: %s" % src

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

    entry = None
    digest = None
    try:
        with open(config_path, encoding="utf-8-sig") as fh:
            config = json.load(fh)
        entry = (config.get("mcpServers") or {}).get(name)
        row("A01", "запись '%s' найдена в конфигурации клиента" % name,
            entry is not None, config_path)
    except Exception as exc:  # noqa: BLE001 — отчёт обязан появиться даже при сбое чтения
        row("A01", "запись '%s' найдена в конфигурации клиента" % name, False,
            "%s: %s" % (type(exc).__name__, exc))

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
