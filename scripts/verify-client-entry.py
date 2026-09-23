"""Проверка записи о сервере в конфигурации рабочего MCP-клиента.

Зачем отдельным шагом. Приёмка поставки запускает Host «руками» скрипта (`--host <путь>`) и потому
не проверяет главное: **запускается ли та запись, которую получит клиент**. Ошибка в пути, в
относительном аргументе, в рабочем каталоге или в лишней переменной окружения проявится не в отчёте
приёмки, а у пользователя — и выглядеть будет как «сервер не отвечает».

Здесь запись берётся ИЗ КОНФИГА КЛИЕНТА и запускается ровно так, как её запустит клиент: та же
команда, те же аргументы, рабочий каталог — домашний (клиент другого не обещает). Проверяются
контракт (`initialize`/`tools/list`), самодиагностика, а затем реальный CAD-канал: подключение,
создание документа, чтение контекста, отсоединение.

Это НЕ заменяет §6.3 наряда: там инструменты вызывает клиент, а здесь — проверка того, что клиенту
будет что вызывать. Разница названа в отчёте отдельной строкой.

Usage:
  python scripts/verify-client-entry.py [--config <mcp.json>] [--name kompas]
      [--passport docs/acceptance/<поставка>/delivery-passport.json]
      [--out docs/acceptance/<поставка>/client-entry-check.json]

Без `--passport` паспорт берётся по поставке, НА КОТОРУЮ УКАЗЫВАЕТ ЗАПИСЬ, а без `--out` отчёт
ложится рядом с ним. Прежняя редакция держала оба пути константами поставки B2: к 19.09.2026 запись
B3 сверялась с паспортом B2, и проверка отказывала записи за то, что она ведёт на ДРУГУЮ поставку —
два FAIL (`E03b`, `E07`) описывали операнд прибора, а не предмет.

ПОПРАВКА 21.09.2026, 20:4x (наряд `JOURNAL_SHARING_FIX_DEVELOPER_PROMPT.md`). Здесь стоял выбор
паспорта ПО СВЕЖЕСТИ (`newest_passport()`): брался самый новый паспорт в `docs/acceptance/*/`, а не
паспорт поставки, на которую ведёт запись. Пока у каждой поставки паспорт издавался сразу, это
случайно совпадало; но наряд §0.4 и §7 ЗАПРЕЩАЕТ издавать паспорт новой поставки до повторной
клиентской приёмки — и тогда проверка входа упиралась в паспорт ЧУЖОЙ поставки, то есть отказывала
записи за дефект своего операнда. Это второй прибор того же класса, что и `_build_api_compliance.py`
(там паспорт читался безусловно и сборка падала `FileNotFoundError`).

Теперь опознание поставки берётся ИЗМЕРЕНИЕМ (`scratch/mcp-smoke/delivery-<поставка>/package-check.json`,
тот же вход, из которого паспорт строит свой блок `package`), а паспорт ЭТОЙ поставки используется,
ЕСЛИ издан, — и ищется он ПО ХЕШУ Host.dll, а не по свежести файла. Расхождение паспорта с
измерением — названный отказ (`E03b`), а не молчаливый выбор одного из двух.
"""

import datetime
import glob
import importlib.util
import json
import os
import sys
import uuid

from localconfig import isolate_config as shared_isolate_config

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

_spec = importlib.util.spec_from_file_location("verify_delivery", os.path.join(ROOT, "scripts", "verify-delivery.py"))
vd = importlib.util.module_from_spec(_spec)
_saved_argv = sys.argv
sys.argv = ["verify-delivery.py"]
_spec.loader.exec_module(vd)
sys.argv = _saved_argv


def argument(name, default=None):
    if name in sys.argv:
        index = sys.argv.index(name)
        if index + 1 < len(sys.argv):
            return sys.argv[index + 1]
    return default


def isolate_config(config, out):
    """Копия конфигурации сервера с журналами в каталоге проверки.

    Зачем. Запись клиента передаёт серверу `--config <kompas-mcp.local.json>`, а тот указывает
    `log_path` на `scratch/logs/host.jsonl` — ТОТ ЖЕ файл, куда пишет сервер, запущенный КЛИЕНТОМ.
    Проверка, запускающая Host по записи, обрезала бы журнал клиентского сеанса, то есть стирала бы
    доказательства ровно того сеанса, ради которого её и запускают.

    Измерено 18.09.2026: прогон этой проверки сменил `sha256(scratch/logs/host.jsonl)` с `9755e3de…`
    на `0dbb4c28…` — журнал клиента перезаписан. Тот же класс дефекта, что был у
    `probe-concurrent-calls.py` (см. `instrument_defects` в `client-acceptance.json`).

    Путь к копии подставляется только в аргумент `--config`. Команда и остальные аргументы берутся
    из записи буквально: проверяется запись, а не удобный её вариант. Сам исходный файл не меняется.

    С 18.09.2026 реализация ОДНА на весь проект — `localconfig.isolate_config`: этот же дефект
    измерялся у четырёх приборов подряд, и четвёртая копия функции была бы четвёртым местом, где
    её можно забыть.
    """
    return shared_isolate_config(config, os.path.dirname(out), "entry")


def newest_passport():
    """Паспорт самой свежей поставки — вместо зашитого имени предыдущей очереди.

    Измерено 19.09.2026: в `docs/acceptance/` лежали паспорта четырёх поставок, а проверка записи
    B3 брала константой паспорт поставки B2. Она не «ошибалась» — она честно сравнивала запись с
    чужим паспортом и отказывала (`E03b`: запись `c51ee6b0…` / паспорт `4de6f121…`; `E07`: 36
    инструментов против 32 в паспорте). Оба отказа принадлежали операнду прибора.

    ОСТАВЛЕНА ТОЛЬКО ДЛЯ ЯВНОГО ОПЕРАНДА И ДЛЯ ОТЧЁТНОСТИ. В опознании поставки больше НЕ
    участвует: свежесть файла не говорит, о какой поставке он написан (см. поправку 21.09.2026 в
    заголовке). Ищется паспорт ЭТОЙ поставки по хешу Host.dll — `passport_for_delivery`.
    """
    found = glob.glob(os.path.join(ROOT, "docs", "acceptance", "*", "delivery-passport.json"))
    if not found:
        return os.path.join(ROOT, "docs", "acceptance", "delivery-passport.json")
    return max(found, key=os.path.getmtime)


def delivery_dir_of(command):
    """Каталог поставки по команде записи: `<...>/artifacts/<поставка>/KompasMcp.Host.exe`."""
    return os.path.dirname(os.path.abspath(command))


def measurement_path_for(delivery_dir):
    """`package-check.json` этой поставки.

    Путь выводится ИЗ ИМЕНИ КАТАЛОГА ПОСТАВКИ тем же правилом, что у `scripts/verify-delivery.py`:
    `artifacts/publish-<стадия>` → `scratch/mcp-smoke/delivery-<стадия>/package-check.json`.
    """
    name = os.path.basename(os.path.normpath(delivery_dir))
    stage = name[len("publish-"):] if name.startswith("publish-") else name
    return os.path.join(ROOT, "scratch", "mcp-smoke", "delivery-" + stage, "package-check.json")


def passport_for_delivery(measurement):
    """Паспорт ЭТОЙ поставки, если издан. Признак — хеш Host.dll, а не время изменения файла.

    Паспорт — ПРОИЗВОДНЫЙ документ, и наряд §0.4 запрещает издавать его до повторной клиентской
    приёмки. Поэтому «паспорта нет» здесь — ЗАКОННОЕ состояние, а не отказ: опознание берётся из
    измерения поставки. Паспорт, описывающий ДРУГУЮ поставку, не подставляется молча.
    """
    target = (measurement or {}).get("host_dll_sha256")
    if not target:
        return None
    for path in sorted(glob.glob(os.path.join(ROOT, "docs", "acceptance", "*", "delivery-passport.json"))):
        try:
            with open(path, encoding="utf-8") as fh:
                package = (json.load(fh).get("package") or {})
        except (OSError, ValueError):
            continue
        if package.get("host_dll_sha256") == target:
            return path
    return None


IDENTITY_FIELDS = ("host_sha256", "host_dll_sha256", "tools_count")


def delivery_identity(command, passport_arg):
    """Опознание поставки для строк `E03`/`E03b`/`E07`: поля + ИЗ КАКОГО ИСТОЧНИКА они взяты.

    Явный `--passport` — это операнд: он берётся как указано, и его отсутствие — отказ.
    Без `--passport`: измерение поставки обязательно (иначе опознать нечем — отказ с причиной),
    а паспорт подключается, если он ИЗДАН и описывает ТУ ЖЕ поставку. Если издан и расходится —
    отказ: молчаливый выбор одного из двух дал бы проверку с числами одной поставки и другой.
    """
    delivery_dir = delivery_dir_of(command)
    measure_path = measurement_path_for(delivery_dir)
    measurement = None
    if os.path.isfile(measure_path):
        with open(measure_path, encoding="utf-8-sig") as fh:
            measurement = json.load(fh)
    if passport_arg:
        passport_path = os.path.abspath(passport_arg)
        if not os.path.isfile(passport_path):
            return None, None, None, passport_path, f"указан --passport, но файла нет: {passport_path}"
        with open(passport_path, encoding="utf-8") as fh:
            package = (json.load(fh).get("package") or {})
        return ({f: package.get(f) for f in IDENTITY_FIELDS}, passport_path, passport_path,
                passport_path, None)
    if measurement is None:
        return None, None, None, None, (
            f"поставку нечем опознать: нет ни измерения {measure_path}, ни --passport.\n"
            f"         Переснимите: python scripts\\verify-delivery.py --delivery {delivery_dir}")
    passport_path = passport_for_delivery(measurement)
    if passport_path is None:
        # Паспорт не издан — законное состояние (наряд §0.4), а не пробел: опознание из измерения.
        return ({f: measurement.get(f) for f in IDENTITY_FIELDS}, measure_path, None,
                measure_path, None)
    with open(passport_path, encoding="utf-8") as fh:
        package = (json.load(fh).get("package") or {})
    mismatch = [f for f in IDENTITY_FIELDS if package.get(f) != measurement.get(f)]
    if mismatch:
        return None, None, passport_path, measure_path, (
            "паспорт и измерение поставки РАСХОДЯТСЯ — опознание неоднозначно: " + ", ".join(
                f"{f} (паспорт {package.get(f)!r} / измерение {measurement.get(f)!r})" for f in mismatch))
    return ({f: package.get(f) for f in IDENTITY_FIELDS}, passport_path, passport_path,
            measure_path, None)


def main():
    config_path = os.path.abspath(argument("--config") or os.path.join(os.path.expanduser("~"), ".workbuddy-ai", "mcp.json"))
    wanted = argument("--name")
    passport_arg = argument("--passport")
    out_arg = argument("--out")
    passport_path = os.path.abspath(passport_arg) if passport_arg else newest_passport()
    # Отчёт ложится рядом с паспортом, по которому сверяли: иначе проверка новой поставки
    # перезаписывала бы отчёт предыдущей. Паспорт ЭТОЙ поставки не издан — рядом с ИЗМЕРЕНИЕМ
    # поставки; умолчание уточняется ниже, когда опознание уже разрешено. Ранние отказы (нет
    # конфигурации, нет записи, нет команды) печатают отчёт по прежнему пути — он не должен
    # пропасть из-за того, что предмет не опознан.
    out = os.path.abspath(out_arg) if out_arg else os.path.join(os.path.dirname(passport_path),
                                                                "client-entry-check.json")

    rows = []

    def row(cid, description, ok, detail=""):
        rows.append({"id": cid, "description": description, "verdict": "PASS" if ok else "FAIL", "detail": detail})
        print(f"  [{'PASS' if ok else 'FAIL':4}] {cid:5} {description}" + (f"\n         {detail[:240]}" if detail else ""))
        return ok

    report = {"config_path": config_path, "rows": rows, "calls": []}

    if not os.path.isfile(config_path):
        row("E01", "конфигурация клиента существует", False, f"нет файла {config_path}")
        return finish(report, out)

    with open(config_path, encoding="utf-8-sig") as fh:
        config = json.load(fh)
    servers = config.get("mcpServers") or {}
    if wanted:
        entry_name = wanted if wanted in servers else None
    else:
        candidates = [name for name, entry in servers.items()
                      if os.path.basename(str(entry.get("command", ""))).lower() == "kompasmcp.host.exe"]
        entry_name = candidates[0] if candidates else None
    if entry_name is None:
        row("E01", "в конфигурации клиента есть запись сервера КОМПАС", False,
            f"записи с командой KompasMcp.Host.exe нет; есть: {sorted(servers)}")
        return finish(report, out)

    entry = servers[entry_name]
    command = entry.get("command")
    extra_args = entry.get("args") or []
    report["entry"] = {"name": entry_name, "command": command, "args": extra_args,
                       "timeout": entry.get("timeout"), "env_keys": sorted((entry.get("env") or {}).keys())}
    row("E01", f"запись '{entry_name}' найдена и разобрана", True,
        f"command={command} args={extra_args} timeout={entry.get('timeout')}")

    exists = bool(command) and os.path.isfile(command)
    row("E02", "команда записи — существующий файл", exists, str(command))
    if not exists:
        return finish(report, out)

    identity, identity_path, identity_passport, identity_measure, identity_problem = delivery_identity(command, passport_arg)
    report["identity"] = {
        "source": identity_path,
        "passport": identity_passport,
        "measurement": identity_measure,
        "note": ("паспорт ЭТОЙ поставки не издан — опознание взято измерением поставки. Это законное "
                 "состояние: наряд §0.4 запрещает издавать паспорт до повторной клиентской приёмки"
                 if identity is not None and identity_passport is None else None),
    }
    if identity is None:
        row("E03", "поставка для сверки опознана", False, identity_problem)
        row("E03b", "поставка для сверки опознана", False, identity_problem)
        report["identity_problem"] = identity_problem
        return finish(report, out)
    if out_arg is None:
        out = os.path.join(os.path.dirname(identity_path), "client-entry-check.json")
    print(f"  опознание поставки: {identity_path}"
          + (f" (паспорт ЭТОЙ поставки: {identity_passport})" if identity_passport
             else " (паспорт ЭТОЙ поставки не издан — опознание измерением)"))

    recorded = identity.get("host_sha256")
    actual = vd.sha256_of(command)
    report["command_sha256"] = actual
    report["recorded_host_sha256"] = recorded
    if recorded:
        row("E03", "запись указывает на тот же Host, что принят в поставке", actual == recorded,
            f"запись {actual} / поставка {recorded} [{identity_path}]")
    else:
        row("E03", "хеш поставки для сверки найден", False, f"нет в {identity_path}")

    # E03 сверяет apphost-ЗАГЛУШКУ. Он одинаков у ВСЕХ поставок: `KompasMcp.Host.exe` — apphost,
    # который не пересобирается при правке кода, поэтому расхождение поставок он поймать НЕ может.
    # 19.09.2026 это и произошло: E03 давал PASS, пока запись вела на прежнюю поставку
    # (`publish-b2-20260918-b2closed`), а паспорт описывал новую. Настоящая опора — хеш РАБОЧЕЙ
    # `.dll` рядом с записью: доказательство поставки это `.dll`, а `.exe` — только обёртка.
    dll_path = os.path.join(os.path.dirname(command), "KompasMcp.Host.dll")
    recorded_dll = identity.get("host_dll_sha256")
    actual_dll = vd.sha256_of(dll_path) if os.path.isfile(dll_path) else None
    report["host_dll_sha256"] = actual_dll
    report["recorded_host_dll_sha256"] = recorded_dll
    same_delivery = bool(recorded_dll) and actual_dll == recorded_dll
    if recorded_dll:
        detail = f"запись {actual_dll} / поставка {recorded_dll} [{identity_path}]"
        if not same_delivery:
            # Расхождение может означать и «запись ведёт не туда», и «взят документ другой
            # поставки». Различить их прибор обязан сам: иначе отказ читается как факт о продукте.
            detail += (f". Сверяли с {identity_path}; если он описывает ДРУГУЮ поставку, "
                       f"это дефект операнда, а не записи — укажите --passport паспорта этой поставки")
        row("E03b", "запись ведёт на ТУ ЖЕ поставку, что описывает опознание (хеш Host.dll)",
            same_delivery, detail)
    else:
        row("E03b", "хеш рабочей .dll поставки для сверки найден", False, f"нет в {identity_path}")

    # Пути — только те аргументы, что на путь похожи. Первая редакция считала путём КАЖДЫЙ
    # аргумент и объявила отказом флаг `--config`: проверка описывала себя, а не запись.
    def looks_like_path(value):
        return bool(value) and not value.startswith("-") and ("\\" in value or "/" in value or ":" in value)

    relative = [a for a in [command] + list(extra_args) if looks_like_path(a) and not os.path.isabs(a)]
    row("E04", "в записи нет относительных путей", not relative,
        f"относительные: {relative}" if relative else "все пути абсолютные")

    env_from_entry = entry.get("env") or {}
    worker_env = env_from_entry.get("KOMPAS_MCP_WORKER_PATH") or os.environ.get("KOMPAS_MCP_WORKER_PATH")
    row("E05", "KOMPAS_MCP_WORKER_PATH не задан (Worker берётся рядом с Host)", not worker_env,
        f"задан: {worker_env}" if worker_env else "не задан ни в записи, ни в окружении")

    # Рабочий каталог — домашний: клиент не обещает никакого другого, а значит запись не имеет права
    # на него полагаться.
    home = os.path.expanduser("~")
    child_env = dict(os.environ)
    child_env.update({k: str(v) for k, v in env_from_entry.items()})

    # Журналы сервера уводим в каталог проверки. Запись передаёт серверу `--config`, а его
    # `log_path` — тот же `scratch/logs/host.jsonl`, куда пишет сервер клиента. Без этой подмены
    # проверка обрезает журнал измеряемого клиентского сеанса (измерено 18.09.2026).
    launch_args = list(extra_args)
    config_value = None
    if "--config" in launch_args:
        index = launch_args.index("--config")
        if index + 1 < len(launch_args):
            config_value = launch_args[index + 1]
    if config_value and os.path.isfile(config_value):
        isolated = isolate_config(config_value, out)
        launch_args[launch_args.index("--config") + 1] = isolated
        report["config_for_launch"] = isolated
        report["logs_isolated"] = True
        row("E06a", "журналы сервера уведены из общего файла клиента", True,
            f"подставлена копия конфигурации: {isolated}")
    else:
        report["logs_isolated"] = False
        row("E06a", "журналы сервера уведены из общего файла клиента", False,
            f"конфигурация сервера не найдена: {config_value}")

    client = vd.Host(command, extra_args=launch_args, cwd=home, env=child_env)
    try:
        init = client.call("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "verify-client-entry", "version": "0"},
        })
        info = init.get("serverInfo", {})
        row("E06", "initialize проходит: сервер отвечает по stdio из домашнего каталога",
            bool(info.get("name")), f"cwd={home} serverInfo={json.dumps(info, ensure_ascii=False)}")
        client.notify("notifications/initialized")

        tools = client.call("tools/list", {}).get("tools", [])
        names = sorted(t["name"] for t in tools)
        report["tools_count"] = len(names)
        report["tools"] = names
        expected = identity.get("tools_count")
        # Сверка с документом ДРУГОЙ поставки ничего не измеряет: совпавшее число инструментов у
        # двух поставок — совпадение, а не подтверждение (у обеих по 36). Признак сравнимости —
        # E03b, хеш рабочей `.dll`.
        if not same_delivery:
            row("E07", "tools/list отвечает и совпадает с принятой поставкой", False,
                f"сравнение с опознанием ДРУГОЙ поставки: запись Host.dll {actual_dll}, "
                f"опознание {recorded_dll}; инструментов {len(names)}, в опознании {expected}. "
                f"Число инструментов совпасть может и при чужой поставке — судить по нему нельзя.")
        else:
            row("E07", "tools/list отвечает и совпадает с принятой поставкой",
                bool(names) and (expected is None or len(names) == expected),
                f"инструментов {len(names)}, в опознании поставки {expected} [{identity_path}]")

        def call(tool, arguments, timeout=180):
            is_error, envelope, raw = client.tool(tool, arguments, timeout=timeout)
            report["calls"].append({"tool": tool, "arguments": arguments, "is_error": is_error,
                                    "envelope": envelope})
            return envelope or {}

        health = call("kompas_health", {"detail": "diagnostic"})
        row("E08", "kompas_health отвечает без подключения к КОМПАС",
            (health.get("status") == "succeeded"), f"status={health.get('status')}")

        caps = call("kompas_capabilities", {})
        caps_result = caps.get("result") or {}
        # `capabilities` отдаёт окружение, а не список инструментов: первая редакция печатала
        # «инструментов_поддержано=0», то есть выдуманное поле. Печатаем то, что там есть.
        row("E09", "kompas_capabilities отвечает",
            (caps.get("status") == "succeeded"),
            f"status={caps.get('status')} worker={caps_result.get('worker_runtime')} "
            f"process={caps_result.get('process_bitness')} local_server={caps_result.get('local_server')} "
            f"running_instances={caps_result.get('running_instances')}")

        connect = call("kompas_connect", {"mode": "launch", "make_visible": False,
                                         "operation_id": str(uuid.uuid4())}, timeout=300)
        connect_result = connect.get("result") or {}
        app_id = connect.get("application_id") or connect_result.get("application_id")
        row("E10", "kompas_connect(launch) поднимает сеанс КОМПАС",
            (connect.get("status") == "succeeded") and bool(app_id),
            f"app_id={app_id} ownership={connect_result.get('ownership')} pid={connect_result.get('process_id')}")

        if app_id:
            created = call("kompas_create_document", {"application_id": app_id, "kind": "part",
                                                     "name": "entry-check",
                                                     "operation_id": str(uuid.uuid4())}, timeout=300)
            doc_id = created.get("document_id") or (created.get("result") or {}).get("document_id")
            row("E11", "kompas_create_document создаёт деталь через этот же запуск",
                (created.get("status") == "succeeded") and bool(doc_id), f"document_id={doc_id}")

            if doc_id:
                context = call("kompas_get_context", {"document_id": doc_id, "detail": "minimal"})
                result = context.get("result") or {}
                report["model"] = {"document_id": doc_id, "revision": context.get("revision_after")}
                row("E12", "kompas_get_context читает контекст и ревизию",
                    (context.get("status") == "succeeded"),
                    f"ревизия={context.get('revision_after')} kind={result.get('kind')}")

            disconnect = call("kompas_disconnect", {"application_id": app_id,
                                                   "close_owned_application": True,
                                                   "operation_id": str(uuid.uuid4())}, timeout=180)
            row("E13", "kompas_disconnect закрывает только свой экземпляр КОМПАС",
                disconnect.get("status") == "succeeded", f"status={disconnect.get('status')}")
    except Exception as exc:  # noqa: BLE001 — отчёт обязан появиться даже при сбое проверки
        # Иначе сбой проверки неотличим от «отчёта нет»: падение скрипта не должно прятать
        # уже собранные строки.
        row("E99", "проверка дошла до конца без исключения", False, f"{type(exc).__name__}: {exc}")
    finally:
        client.close()

    report["stderr_tail"] = client.tail()
    return finish(report, out)


def finish(report, out):
    os.makedirs(os.path.dirname(out), exist_ok=True)
    failed = [r for r in report["rows"] if r["verdict"] != "PASS"]
    report["verdict"] = "PASS" if report["rows"] and not failed else "FAIL"
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    print(f"\nИтог: {report['verdict']} — строк {len(report['rows'])}, отказов {len(failed)}")
    print("Отчёт: " + out)
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
