"""Сборка машинного отчёта о поставке из журналов прогонов.

Числа берутся из отчётов, а не переписываются руками: отчёт, набранный вручную, расходится с
прогоном ровно в тот момент, когда это важнее всего.

Порядок: сначала приёмка поставки её же бинарями, затем этот генератор.

    python scripts/mcp-smoke.py --host artifacts/publish-b2-20260918/KompasMcp.Host.exe \
        --config config/kompas-mcp.local.json --report scratch/mcp-smoke/delivery-20260918/<группа>.json
    python scripts/verify-delivery.py --delivery artifacts/publish-b2-20260918
    python scripts/verify-client-entry.py
    python scripts/make-delivery-passport.py

Клиентская приёмка (`docs/acceptance/delivery-20260918/client-acceptance.json`) — **отдельный**
результат и отдельный блок паспорта. Проверка записи отвечает на вопрос «запускается ли то, что
записано в конфиге»; клиентская приёмка — «видит ли агент инструменты и что они делают». Зелёный
исход одной не подтверждает другую, поэтому они лежат рядом и не заменяют друг друга.
"""

import collections
import contextlib
import datetime
import glob
import importlib.util
import io
import json
import os
import shutil
import sys

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def find_root(start):
    """Корень ищется по файлу решения, а не по числу уровней: скрипт запускают и из scripts/, и из
    scratch/tmp/, и ошибка в счёте уровней молча указала бы на чужой каталог."""
    current = os.path.abspath(start)
    for _ in range(6):
        if os.path.exists(os.path.join(current, "KompasMcp.sln")):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    raise SystemExit("не найден корень репозитория (KompasMcp.sln)")


ROOT = find_root(os.path.dirname(os.path.abspath(__file__)))
# Значения по умолчанию описывают поставку 18.09.2026 (B2). Каждая следующая поставка задаёт свои
# каталоги ключами `--delivery/--reports/--out`: иначе паспорт новой поставки пришлось бы собирать
# копией скрипта, а копия расходится с оригиналом ровно тогда, когда это важнее всего.
DELIVERY = os.path.join(ROOT, "artifacts", "publish-b2-20260918")
REPORTS = os.path.join(ROOT, "scratch", "mcp-smoke", "delivery-20260918")
OUT_DIR = os.path.join(ROOT, "docs", "acceptance", "delivery-20260918")
# Имя прогона попадает в паспорт и в отчёты. По умолчанию выводится из каталога поставки, чтобы
# паспорт новой поставки не назывался именем прежней.
RUN_ID = "delivery-" + os.path.basename(DELIVERY)

# Клиентская приёмка ищется в двух местах: структурный результат там, где его назвал наряд (§6), и
# рядом с сырыми отчётами прогонов, где лежит client-entry-check.json. Порядок — приоритет.
CLIENT_ACCEPTANCE_PATHS = (
    os.path.join(OUT_DIR, "client-acceptance.json"),
    os.path.join(REPORTS, "client-acceptance.json"),
)

_spec = importlib.util.spec_from_file_location(
    "verify_delivery", os.path.join(ROOT, "scripts", "verify-delivery.py"))
vd = importlib.util.module_from_spec(_spec)
_saved_argv = sys.argv
sys.argv = ["verify-delivery.py"]
_spec.loader.exec_module(vd)
sys.argv = _saved_argv
sha256_of = vd.sha256_of
pe_machine = vd.pe_machine

# Правила сведения уровней и закрытия обязательного объёма читаются из ОДНОГО места. Своя копия
# здесь разошлась бы с клиентской приёмкой ровно тогда, когда расхождение опаснее всего.
_levels_spec = importlib.util.spec_from_file_location(
    "acceptance_levels", os.path.join(ROOT, "scripts", "acceptance-levels.py"))
levels = importlib.util.module_from_spec(_levels_spec)
_levels_spec.loader.exec_module(levels)


def load(name):
    # `utf-8-sig`, а не `utf-8`: отчёты части групп несут BOM (в поставке 19.09.2026 — `b3.json`,
    # `b3m.json`, `full.json`). Чтение их как `utf-8` падало с `Unexpected UTF-8 BOM` и валило
    # переиздание паспорта на ЧУЖОМ формате файла, а не на своём предмете. `utf-8-sig` читает оба
    # вида одинаково: наличие BOM перестаёт быть свойством поставки.
    path = os.path.join(REPORTS, name)
    with open(path, encoding="utf-8-sig") as fh:
        return json.load(fh)


def report_path(name):
    """Путь к отчёту группы и его имя файла — или `(None, None)`, если отчёта нет.

    Два соглашения об именах живут рядом: поставка 18.09.2026 (B2) называла отчёты `<группа>.json`,
    поставка B3 — `<группа>-acceptance.json`, а полный прогон — `smoke-report.json`. Паспорт обязан
    читать обе: иначе он молча пропускает девять групп из десяти и объявляет приёмку по одной, что
    выглядит как PASS с шестью строками вместо тысячи. Измерено 18.09.2026 на первом прогоне
    паспорта B3.
    """
    candidates = (name + ".json", name + "-acceptance.json")
    if name == "full":
        candidates = ("full.json", "smoke-report.json")
    for candidate in candidates:
        path = os.path.join(REPORTS, candidate)
        if os.path.exists(path):
            return path, candidate
    return None, None


def counts(report):
    result = {}
    for row in report.get("rows", []):
        result[row["verdict"]] = result.get(row["verdict"], 0) + 1
    return result


# Сборки, которые РАЗЛИЧАЮТ поставку. Apphost-заглушка `KompasMcp.Host.exe` побайтово одинакова в
# Debug и в Release (измерено 19.09.2026: d6517d8f… в обеих конфигурациях, размер 162304), поэтому
# «хеш Host» по одному .exe не отличает сборку, которой измеряли, от сборки, которую поставляем.
# Отличают управляемые сборки.
MEASURED_BINARY_FIELDS = ("host_dll_sha256", "worker_dll_sha256", "adapter_sha256")


def check_measured_binaries(package, groups):
    """Расхождения между сборкой, которой измеряли, и сборкой, которую поставляем.

    Пустой список — приёмка проведена ИМЕННО на поставленных двоичных файлах. Непустой — паспорт
    описывал бы не то, что кладут заказчику. `--host` у scripts/mcp-smoke.py существует ровно для
    этого («the package is only proven by the binary it actually ships»), а приёмка, прогнанная на
    Debug-выходе рядом с исходниками, приёмкой поставки не является.

    Измерено 19.09.2026 на поставке `publish-b3-20260919-read-unclosed`: все одиннадцать отчётов
    несли `adapter_sha256 = 420e977e…` (Debug), тогда как в поставке лежал `949c2dbf…`
    (Release/win-x64), и паспорт публиковал оба числа РЯДОМ, ничем их не сверяя, — при том что в
    его же `notes` было записано «Приёмка идёт по бинарям поставки». Утверждение, которого никто не
    проверял, оказалось неверным.
    """
    out = []
    for field in MEASURED_BINARY_FIELDS:
        expected = package.get(field)
        if not expected:
            out.append(f"в отчёте проверки пакета нет {field} — сверять нечем")
            continue
        for group in groups:
            measured = group.get(field)
            if not measured:
                out.append(f"группа {group['group']}: в отчёте нет {field} — "
                           f"неизвестно, какой сборкой измеряли")
            elif measured != expected:
                out.append(f"группа {group['group']}: {field} = {measured[:12]}…, "
                           f"а в поставке {expected[:12]}…")
    return out


def self_test_measured_binaries():
    """Отрицательный контроль сторожа сборок: подмена в ОДНОЙ группе обязана быть названа.

    Сторож без отрицательного контроля не доказан: он может возвращать пустой список ВСЕГДА, и
    тогда «согласовано» не значит ничего. Обе половины ставятся в одной постановке: чистый набор
    обязан пройти, испорченный — быть назван.
    """
    package = {"host_dll_sha256": "a" * 64, "worker_dll_sha256": "b" * 64,
               "adapter_sha256": "c" * 64}
    clean = [{"group": "full", **package}]
    clean_problems = check_measured_binaries(package, clean)
    if clean_problems:
        print("самопроверка сторожа сборок: ЧИСТЫЙ набор назван расхождением —", clean_problems)
        return 1
    broken = json.loads(json.dumps(clean))
    broken[0]["adapter_sha256"] = "d" * 64
    problems = check_measured_binaries(package, broken)
    if not any(p.startswith("группа full:") and "adapter_sha256" in p for p in problems):
        print("самопроверка сторожа сборок: подмена adapter_sha256 в группе full НЕ названа —",
              problems)
        return 1
    print(f"самопроверка сторожа сборок: чисто — [], подмена названа — {len(problems)} сообщение")
    return 0


# АРТЕФАКТЫ СЕАНСА 2 ОТ 21.09.2026 — предмет контроля вердикта. Каталог отчётов прогонов лежит в
# `scratch` и потому может быть вычищен; отсутствие артефактов — НАЗВАННОЕ состояние контроля, а не
# молчаливый пропуск: контроль, «проходящий» при отсутствии данных, не измеряет ничего.
SESSION2_REPORTS = "scratch/mcp-smoke/sketchplane-20260921"
SESSION2_ACCEPTANCE = "docs/acceptance/delivery-sketchplane-20260921-plane/client-acceptance.json"
SESSION2_ENTRY = "docs/acceptance/delivery-sketchplane-20260921-plane/client-entry-check.json"
SESSION2_GROUP_REPORTS = ("full.json", "f08.json", "mania.json", "dep.json", "image.json")
CLIENT_GATE_SELFTEST_DIR = "scratch/_passport-selftest/client-gate"


def _run_generator(argv):
    """Прогон генератора В ЭТОМ ЖЕ процессе с подменённым `sys.argv`.

    Глобальные DELIVERY/REPORTS/OUT_DIR/RUN_ID сохраняются и возвращаются на место: контроль не
    имеет права оставить прибор настроенным на временный каталог.
    """
    saved = (sys.argv, DELIVERY, REPORTS, OUT_DIR, CLIENT_ACCEPTANCE_PATHS, RUN_ID)
    sys.argv = ["make-delivery-passport.py"] + argv
    captured = io.StringIO()
    try:
        with contextlib.redirect_stdout(captured):
            code = main()
    finally:
        (sys.argv, globals()["DELIVERY"], globals()["REPORTS"], globals()["OUT_DIR"],
         globals()["CLIENT_ACCEPTANCE_PATHS"], globals()["RUN_ID"]) = saved
    return code, captured.getvalue()


def self_test_client_gate():
    """Контроль вердикта: на артефактах сеанса 2 итог обязан быть FAIL, и он обязан ПЕРЕВЕРНУТЬСЯ
    в PASS, если ровно один вход — клиентская приёмка — заменён на пройденную.

    ПОЧЕМУ НА АРТЕФАКТАХ, А НЕ НА СИНТЕТИКЕ. Синтетика проверяет правило, но не проверяет, что
    ГЕНЕРАТОР паспорта это правило спрашивает: блок `client_acceptance` вычислялся в литерале
    паспорта уже ПОСЛЕ вердикта, и правило, живущее рядом, само по себе расхождение не ловит.
    Здесь прогоняется сам генератор, и оба плеча идут на ОДНИХ И ТЕХ ЖЕ отчётах: (а) с настоящим
    отчётом клиентской приёмки сеанса 2 (`verdict FAIL`) — итог FAIL; (б) с тем же отчётом, у
    которого вердикт заменён на PASS, — итог PASS. Расхождение возможно только по клиентской
    приёмке, и это делает контроль способным отказать: если генератор снова станет игнорировать
    блок, плечо (а) даст PASS и контроль назовёт это.

    Предпосылки проверяются ЯВНО: если артефакты сеанса 2 отсутствуют или уже не описывают то
    состояние (entry PASS + client FAIL), контроль НЕ выполняется и говорит об этом отказом.
    """
    reports = os.path.join(ROOT, *SESSION2_REPORTS.split("/"))
    acceptance_path = os.path.join(ROOT, *SESSION2_ACCEPTANCE.split("/"))
    entry_path = os.path.join(ROOT, *SESSION2_ENTRY.split("/"))
    missing = [p for p in [os.path.join(reports, name) for name in SESSION2_GROUP_REPORTS]
               + [os.path.join(reports, "package-check.json"), acceptance_path, entry_path]
               if not os.path.exists(p)]
    if missing:
        print("контроль вердикта по клиентской приёмке НЕ ВЫПОЛНЕН: нет артефактов сеанса 2 —")
        for path in missing:
            print("  -", rel(path))
        print("Контроль, «проходящий» без данных, ничего не измеряет; восстановите артефакты "
              "сеанса 2 (отчёты прогонов в " + rel(reports) + " и два отчёта клиента в "
              + rel(os.path.dirname(acceptance_path)) + ").")
        return 1

    with open(acceptance_path, encoding="utf-8-sig") as fh:
        acceptance = json.load(fh)
    with open(entry_path, encoding="utf-8-sig") as fh:
        entry = json.load(fh)
    with open(os.path.join(reports, "package-check.json"), encoding="utf-8-sig") as fh:
        package = json.load(fh)
    if acceptance.get("verdict") != "FAIL":
        print("контроль вердикта по клиентской приёмке НЕ ВЫПОЛНЕН: отчёт сеанса 2 больше не "
              f"несёт FAIL (verdict={acceptance.get('verdict')!r}) — предпосылка контроля "
              "изменилась, и проверять на нём нечего.")
        return 1
    if entry.get("verdict") != "PASS":
        print("контроль вердикта по клиентской приёмке НЕ ВЫПОЛНЕН: проверка записи сеанса 2 "
              f"больше не PASS (verdict={entry.get('verdict')!r}).")
        return 1
    if acceptance.get("host_dll_sha256") != package.get("host_dll_sha256"):
        print("контроль вердикта по клиентской приёмке НЕ ВЫПОЛНЕН: клиентская приёмка снята не с "
              "той сборки, что описана отчётами прогонов — блок был бы назван расхождением, и "
              "отказ пришёл бы не от гейта.")
        return 1

    tmp = os.path.join(ROOT, *CLIENT_GATE_SELFTEST_DIR.split("/"))
    if os.path.exists(tmp):
        shutil.rmtree(tmp)
    work = os.path.join(tmp, "reports")
    os.makedirs(work)
    for name in list(SESSION2_GROUP_REPORTS) + ["package-check.json"]:
        shutil.copy2(os.path.join(reports, name), os.path.join(work, name))
    shutil.copy2(acceptance_path, os.path.join(work, "client-acceptance.json"))
    shutil.copy2(entry_path, os.path.join(work, "client-entry-check.json"))

    def read_passport(out_dir):
        with open(os.path.join(out_dir, "delivery-passport.json"), encoding="utf-8-sig") as fh:
            return json.load(fh)

    def run(out_dir):
        code, output = _run_generator(["--reports", work, "--out", out_dir])
        path = os.path.join(out_dir, "delivery-passport.json")
        if code is not None or not os.path.exists(path):
            return None, output
        return read_passport(out_dir), output

    failures = []
    fail_out = os.path.join(tmp, "out-fail")
    passport, output = run(fail_out)
    if passport is None:
        failures.append("плечо (а): генератор не записал паспорт\n" + output)
    else:
        got = passport["acceptance"]
        levels_seen = {k: got[k]["verdict"] for k in
                       ("transport_and_delivery", "functional_acceptance", "mandatory_scope",
                        "client_acceptance")}
        if got["verdict"] != "FAIL" or got["fully_ready"] is not False:
            failures.append(f"плечо (а): итог {got['verdict']!r} / fully_ready "
                            f"{got['fully_ready']!r}, ожидание FAIL / False — генератор снова "
                            f"игнорирует клиентскую приёмку (уровни: {levels_seen})")
        elif got["client_acceptance"]["verdict"] != "FAIL":
            failures.append(f"плечо (а): итог FAIL, но уровень клиентской приёмки назван "
                            f"{got['client_acceptance']['verdict']!r} — отказ пришёл не от него")
        elif passport["client_entry"].get("verdict") != "PASS":
            failures.append("плечо (а): проверка записи клиента не PASS — предпосылка изменилась")
        elif levels_seen["transport_and_delivery"] != "PASS" \
                or levels_seen["functional_acceptance"] != "PASS" \
                or levels_seen["mandatory_scope"] != "COMPLETE":
            failures.append(f"плечо (а): отказ пришёл не от клиентской приёмки, а от уровней "
                            f"{levels_seen} — тогда контроль не проверяет гейт")

    # Плечо (б): тот же отчёт, но клиент принял. Меняется РОВНО один вход.
    patched = json.loads(json.dumps(acceptance))
    patched["verdict"] = "PASS"
    if isinstance(patched.get("transport_and_delivery"), dict):
        patched["transport_and_delivery"]["verdict"] = "PASS"
    with open(os.path.join(work, "client-acceptance.json"), "w", encoding="utf-8") as fh:
        json.dump(patched, fh, ensure_ascii=False, indent=2)
    pass_out = os.path.join(tmp, "out-pass")
    passport, output = run(pass_out)
    if passport is None:
        failures.append("плечо (б): генератор не записал паспорт\n" + output)
    else:
        got = passport["acceptance"]
        if got["verdict"] != "PASS" or got["fully_ready"] is not True:
            failures.append(f"плечо (б): итог {got['verdict']!r} / fully_ready "
                            f"{got['fully_ready']!r}, ожидание PASS / True — итог не следует за "
                            f"клиентской приёмкой (причина уровня: "
                            f"{got['client_acceptance'].get('reason')!r})")

    if failures:
        print("КОНТРОЛЬ ВЕРДИКТА НЕ ПРОЙДЕН:")
        for line in failures:
            print("  -", line)
        return 1
    print("контроль вердикта по клиентской приёмке пройден: на артефактах сеанса 2 итог FAIL "
          "(уровень client_acceptance FAIL, entry PASS), с пройденной клиентской приёмкой — PASS")
    return 0


def kompas_facts():
    """Факты об установленном КОМПАСе: хеш и разрядность — измерены сейчас, версия — из паспорта
    окружения, снятого на этой же машине 18.09.2026 (проверять версию продукта заново незачем,
    а выдумывать её нельзя)."""
    exe = r"D:\Programs\KOMPAS-3Dv24\Bin\KOMPAS.Exe"
    facts = {"install_root": r"D:\Programs\KOMPAS-3Dv24", "local_server_path": exe}
    facts["kompas_sha256"] = sha256_of(exe) if os.path.exists(exe) else None
    facts["local_server_pe_machine"] = f"0x{pe_machine(exe):04X}" if os.path.exists(exe) else None
    facts["kompas_file_version"] = None
    facts["kompas_file_version_source"] = None
    env_passport = os.path.join(ROOT, "docs", "acceptance", "api7", "env-passport.json")
    if os.path.exists(env_passport):
        with open(env_passport, encoding="utf-8-sig") as fh:
            env = json.load(fh).get("environment", {})
        if env.get("kompas_sha256") == facts["kompas_sha256"]:
            facts["kompas_file_version"] = env.get("kompas_file_version")
            facts["kompas_file_version_source"] = \
                "docs/acceptance/api7/env-passport.json (тот же хеш KOMPAS.Exe)"
    return facts


def client_entry(reports_dir):
    """Проверка записи клиента: запускается ли то, что записано в конфиге.

    Отчёт ищется в двух местах: рядом с сырыми отчётами прогонов и в каталоге приёмки самой
    поставки. `verify-client-entry.py` без `--out` кладёт его рядом с паспортом, которым сверялся, —
    то есть в каталог приёмки, и паспорт, смотревший только в каталог прогонов, отвечал `not_run`
    на УЖЕ ВЫПОЛНЕННУЮ проверку.
    """
    candidates = (os.path.join(reports_dir, "client-entry-check.json"),
                  os.path.join(OUT_DIR, "client-entry-check.json"))
    path = next((p for p in candidates if os.path.exists(p)), None)
    if path is None:
        return {"status": "not_run"}
    with open(path, encoding="utf-8-sig") as fh:
        entry = json.load(fh)
    return {
        "config_path": entry.get("config_path"),
        "entry": entry.get("entry"),
        "command_sha256": entry.get("command_sha256"),
        "recorded_host_sha256": entry.get("recorded_host_sha256"),
        "tools_count": entry.get("tools_count"),
        "model": entry.get("model"),
        "rows": len(entry.get("rows", [])),
        "verdicts": counts(entry),
        "verdict": entry.get("verdict"),
        "report": os.path.relpath(path, ROOT).replace("\\", "/"),
        "note": "вызовы инструментов выполнены проверкой, а не клиентом: включение записи "
                "требует действия пользователя",
    }


def rel(path):
    """Относительный путь для отчёта. `os.path.relpath` бросает ValueError, если файл лежит на
    другом диске, — генератор паспорта падать из-за этого не должен."""
    try:
        return os.path.relpath(path, ROOT).replace("\\", "/")
    except ValueError:
        return path.replace("\\", "/")


def _continuation_prompts(limit=3):
    """Задания на продолжение, которые действительно лежат в репозитории.

    Прежняя редакция перечисляла имена константами, и паспорт отправлял читателя к наряду
    предыдущей очереди: к моменту приёмки B3 в корне уже лежал
    `B3_FINAL_CLIENT_CONTINUATION_PROMPT.md`, а паспорт его не называл вовсе. Список читается с
    диска и упорядочен по времени правки — свежайшее задание идёт первым.
    """
    found = glob.glob(os.path.join(ROOT, "*CONTINUATION_PROMPT*.md"))
    found.sort(key=os.path.getmtime, reverse=True)
    return [os.path.basename(p) for p in found[:limit]] or ["RESUME.md"]


def _diagnosis_report():
    """Отчёт, в котором ИЗМЕРЕНА причина отсутствия клиентской приёмки.

    Очередь меняется (B2 закрыт, идёт B3), поэтому документ ищется по свежести, а не по имени:
    паспорт поставки B3 не должен отправлять читателя к отчёту предыдущей очереди.
    """
    candidates = glob.glob(os.path.join(ROOT, "docs", "acceptance", "B*_CLIENT_ACCEPTANCE.md"))
    if not candidates:
        return "docs/acceptance/INDEX.md"
    return rel(max(candidates, key=os.path.getmtime))


def client_acceptance(package):
    """Вызовы `kompas_*`, выполненные РАБОЧИМ КЛИЕНТОМ, — отдельно от проверки записи.

    Файла нет — блок честно отвечает `not_run`. Подставлять сюда прежний `client_entry` или
    выводить `PASS` по предположению запрещено: это ровно та подмена, ради неразличения которой
    наряд §6 и требует двух разных результатов.
    """
    path = next((p for p in CLIENT_ACCEPTANCE_PATHS if os.path.exists(p)), None)
    if path is None:
        # ИСПРАВЛЕНО 18.09.2026: прежде этот блок утверждал конкретную причину — «подтверждение не
        # выдано, mcp-approvals.json пуст». Утверждение оказалось неверным как общее: подтверждение
        # было выдано (ключ 4efcde36…::kompas), клиент Host запустил, а приёмки всё равно не было —
        # потому что вызовы падали на дефекте транспорта, а после его исправления ждали новой сессии.
        # Паспорт не прибор и причину измерить не может: теперь он её НЕ называет, а отправляет к
        # отчёту, где причина измерена. Иначе отсутствие файла снова получило бы правдоподобное
        # объяснение, которое некому опровергнуть.
        return {
            "status": "not_run",
            "verdict": None,
            "report": None,
            "note": "вызовы инструментов клиентом не выполнены: файл client-acceptance.json "
                    "отсутствует; ожидаемые места — " + rel(OUT_DIR) + "/ и " + rel(REPORTS) + "/. "
                    "Задание на продолжение — " + ", ".join(_continuation_prompts()),
            "blocker": "причина в этом блоке не измеряется. Состояние подтверждений смотри в "
                       "scripts/verify-mcp-approval.py, состояние запуска клиентом — в "
                       "scratch/logs/host.jsonl (записи с клиентом 'kompas'), измеренную причину "
                       "отсутствия приёмки — в " + _diagnosis_report(),
            "diagnosis_report": _diagnosis_report(),
        }

    with open(path, encoding="utf-8-sig") as fh:
        report = json.load(fh)

    rows = report.get("rows") or []
    verdicts = {}
    for row in rows:
        key = row.get("verdict")
        verdicts[key] = verdicts.get(key, 0) + 1

    # Клиентская приёмка, снятая с ДРУГОЙ поставки, эту не закрывает: хеши отчёта сверяются с
    # хешами пакета. Расхождение — отказ, а не примечание.
    problems = []
    for field in ("host_dll_sha256", "worker_dll_sha256", "adapter_sha256"):
        measured = report.get(field)
        expected = package.get(field)
        if measured and expected and measured != expected:
            problems.append(f"{field}: в отчёте {measured}, в пакете {expected}")
    verdict = report.get("verdict")
    if problems:
        # УЖЕСТОЧЕНО 18.09.2026, но не для всех вердиктов. Отказом расхождение хешей становится
        # только тогда, когда отчёт УТВЕРЖДАЕТ успех: «PASS, снятый с другой поставки» — это ровно
        # та подмена, ради неразличения которой наряд §6 требует двух разных результатов. Отчёт,
        # который успеха не утверждает, от расхождения FAIL'ом не делается: FAIL по словарю проекта
        # значит «проверено и не пройдено», а тут не проверялось ничего. Измерено в тот же день:
        # клиентская приёмка BLOCKED снята с поставки 90144e18…, паспорт описывает 4de6f121… —
        # верный ответ BLOCKED плюс список problems, а не FAIL.
        if verdict == "PASS":
            verdict = "FAIL"
        problems.append("клиентская приёмка снята не с той поставки, что описана в паспорте")

    return {
        "status": "run",
        "client": report.get("client"),
        "client_version": report.get("client_version"),
        "session_id": report.get("session_id"),
        "host_pid": report.get("host_pid"),
        "worker_pid": report.get("worker_pid"),
        "checked_at": report.get("checked_at"),
        "delivery_dir": report.get("delivery_dir"),
        "host_dll_sha256": report.get("host_dll_sha256"),
        # Хеш поставки ПОСЛЕ исправления текстового канала: клиент его ещё не запускал, но читателю
        # паспорта нужно видеть, что расхождение с problems — это «ещё не перемерено», а не «стало
        # хуже». Добавлено 18.09.2026.
        "host_dll_sha256_after_text_channel_fix": report.get("host_dll_sha256_after_text_channel_fix"),
        "delivery_republished_same_path": report.get("delivery_republished_same_path"),
        "worker_dll_sha256": report.get("worker_dll_sha256"),
        "adapter_sha256": report.get("adapter_sha256"),
        "config_path": report.get("config_path"),
        "tool_source": report.get("tool_source"),
        "tools_visible": report.get("tools_visible"),
        "tools_invoked": report.get("tools_invoked"),
        "trust": report.get("trust"),
        "scenarios": report.get("scenarios"),
        "models": report.get("models"),
        "rows": len(rows),
        "verdicts": verdicts,
        "evidence_limit": report.get("evidence_limit"),
        # Причина незавершённости едет в паспорт вместе с приёмкой: без неё «BLOCKED» читается как
        # «что-то не так с продуктом», а с ней — как «осталось одно внешнее действие, и вот какое».
        "blocker": report.get("blocker"),
        # ИСПРАВЛЕНО 18.09.2026 (19:40). Прежде здесь читался только ключ `defect_found`, а отчёт
        # сеанса 19:10 назвал дефект транспорта `defect_found_transport` — паспорт молча отдавал
        # `null`, то есть читатель паспорта не видел, что IPC-CONCURRENT-READ закрыт клиентским
        # подтверждением. Ровно класс «прибор читает то, чего в отчёте нет»: отсутствие ключа
        # выглядело как отсутствие дефекта. Теперь читаются оба имени, новое первым.
        "defect_found": report.get("defect_found_transport") or report.get("defect_found"),
        # Дефект ТЕКСТОВОГО канала едет отдельным блоком: он найден ПОСЛЕ дефекта транспорта, той же
        # клиентской приёмкой, и исправлен уже в этой поставке. Добавлено 18.09.2026.
        "defect_found_text_channel": report.get("defect_found_text_channel"),
        # Дефект НАРЯДА (не продукта и не прибора) едет отдельным блоком по той же причине, по
        # которой отделены дефекты приборов: иначе читатель искал бы в поставке то, чего в ней нет.
        # Измерено 18.09.2026: «дословный» блок наряда задавал ось вращения перпендикулярно эскизу.
        "instruction_defects": report.get("instruction_defects"),
        # Дефекты ПРИБОРОВ едут отдельно от дефектов продукта: смешав их, паспорт заставил бы
        # читателя искать в поставке то, чего в ней нет. Добавлено 18.09.2026 — прибор приёмки
        # затирал журнал измеряемого клиентского сеанса.
        "instrument_defects": report.get("instrument_defects"),
        "report": rel(path),
        "delivery_matches_package": not problems,
        "problems": problems,
        "verdict": verdict,
        # Уровни клиентской приёмки едут отдельно и целиком: «канал клиента работал» и «обязательный
        # объём выполнен» — разные утверждения, и паспорт не имеет права их сливать.
        "transport_and_delivery": report.get("transport_and_delivery"),
        "functional_acceptance": report.get("functional_acceptance"),
        "mandatory_scope": report.get("mandatory_scope"),
        "fully_ready": report.get("fully_ready"),
        "verdict_rule": report.get("verdict_rule"),
        "verdict_basis": report.get("verdict_basis"),
        "corrections": report.get("corrections"),
        "reacceptance": report.get("reacceptance"),
    }


def main():
    global DELIVERY, REPORTS, OUT_DIR, CLIENT_ACCEPTANCE_PATHS, RUN_ID

    # Каталоги поставки, её отчётов и вывода задаются ключами. Значения по умолчанию — поставка
    # 18.09.2026 (B2); поставка B3 лежит рядом и задаётся явно.
    argv = sys.argv[1:]
    opts = {}
    index = 0
    while index + 1 < len(argv):
        if argv[index].startswith("--"):
            opts[argv[index]] = argv[index + 1]
            index += 2
        else:
            index += 1
    if "--delivery" in opts:
        DELIVERY = os.path.abspath(opts["--delivery"])
        RUN_ID = "delivery-" + os.path.basename(DELIVERY)
    if "--reports" in opts:
        REPORTS = os.path.abspath(opts["--reports"])
    if "--out" in opts:
        OUT_DIR = os.path.abspath(opts["--out"])
    run_id_from_cli = "--run-id" in opts
    if run_id_from_cli:
        RUN_ID = opts["--run-id"]
    # Пересобирается ЗДЕСЬ: константа вычислена при импорте из значений по умолчанию, и без
    # пересборки паспорт B3 искал бы клиентскую приёмку в каталоге B2 — то есть отвечал бы
    # `not_run` на существующий отчёт.
    CLIENT_ACCEPTANCE_PATHS = (
        os.path.join(OUT_DIR, "client-acceptance.json"),
        os.path.join(REPORTS, "client-acceptance.json"),
    )

    package = load("package-check.json")

    # Каталог поставки берётся из отчёта проверки пакета, а не из константы. Константа означала бы,
    # что паспорт описывает имя, набранное в скрипте, а не ту поставку, которую действительно
    # проверили: 18.09.2026 после переиздания поставки паспорт уже разошёлся с отчётами — хеши были
    # новые, а `delivery_dir` и `launch_command` указывали на прежний каталог.
    delivery = DELIVERY
    reported_host = package.get("host_path")
    if reported_host:
        delivery = os.path.dirname(reported_host)

    # RUN_ID обязан называть ТУ поставку, которую паспорт описывает. Прежде он вычислялся из ключа
    # `--delivery` (или из значения по умолчанию) и НЕ пересчитывался, когда каталог подменялся на
    # взятый из `package-check.json`. Паспорт 18.09.2026 из-за этого назывался
    # `delivery-publish-b2-20260918`, описывал `publish-b2-20260918-b2closed` и нёс 32 инструмента
    # при 36 фактических — расхождение, на котором упала проверка входа E07. Имя — тоже утверждение.
    if not run_id_from_cli:
        RUN_ID = "delivery-" + os.path.basename(delivery)

    groups = []
    # Имена строк каждой группы — по ним считается, что «групповые прогоны» и «полный прогон» —
    # это одни и те же проверки по частям, а не два независимых набора.
    ids_by_group = {}
    # Порядок групп не произвольный: --sketch-status-only сеет документ, который затем открывают
    # другие группы, поэтому он идёт раньше полного прогона. Группы, которых нет в отчётах,
    # пропускаются молча — паспорт описывает измеренное, а не ожидаемое.
    for name in (
        "contract",
        "rotation",
        "hole",
        "fillet",
        "sketch-status",
        "chamfer",
        "extrusion",
        "b3",
        "b3l",
        "b3m",
        # Группа B3C (последовательные ОДНОИМЁННЫЕ преобразования и жизненный цикл признака) заведена
        # 19.09.2026. Пока её не было в этом перечне, её отчёт лежал рядом и в паспорт не попадал:
        # «группы, которых нет в отчётах, пропускаются молча» работало и в обратную сторону —
        # отчёт, которого нет в перечне, молча пропускался.
        "b3c",
        # Группа DL (сохранённость документа и политика закрытия) заведена нарядом F-02 от 20.09.2026.
        # Тот же класс ошибки, что и с B3C: отчёт `dirty-acceptance.json` лежал в каталоге, но имя
        # группы отсутствовало в перечне — а «группы, которых нет в отчётах, пропускаются молча»
        # работает и в обратную сторону. Доказательство §1 не попадало в паспорт, хотя прогон был.
        "dirty",
        "concurrent-calls",
        # Группа F08 (десять действий на каждый режим B1/B2 плюс строка AUX-SKETCH) заведена
        # 20.09.2026 нарядом SM07 §3.4. Тот же класс, что B3C/DL/MANIA, и он же — измеренный
        # дважды: отчёт `f08.json` лежал бы рядом, а имя группы отсутствовало бы в перечне, и
        # «группы, которых нет в отчётах, пропускаются молча» сработало бы в обратную сторону.
        # Отдельной группой она стоит потому, что действие `edit` шести строк SM-07 обязано
        # находиться ПО ИМЕНИ строки (`F08.15.edit` … `F08.20.edit`), а в полном прогоне эти строки
        # тонули бы среди тысячи чужих.
        "f08",
        # Группа MANIA (сценарий «Скоба Model Mania 2021», наряд §3.3 от 20.09.2026) — тот же класс,
        # что B3C и DL: отчёт `mania.json` лежал бы рядом, а имя группы отсутствовало бы в перечне, и
        # «группы, которых нет в отчётах, пропускаются молча» снова сработало бы в обратную сторону.
        # Строки этой группы входят и в полный прогон; здесь она стоит отдельной группой, чтобы её
        # отказ был виден по имени, а не растворялся в тысяче строк.
        "mania",
        # Группа IMG (растровый снимок модели, наряд KOMPAS_EXPORT_IMAGE от 21.09.2026) — тот же
        # класс, что B3C, DL, F08 и MANIA: отчёт `image-acceptance.json` лежал бы рядом, а имя
        # группы отсутствовало бы в перечне, и «группы, которых нет в отчётах, пропускаются молча»
        # сработало бы в обратную сторону. Строки этой группы входят и в полный прогон; здесь она
        # стоит отдельной группой, чтобы отказ был виден по имени, а не растворялся в тысяче строк.
        "image",
        # Группа DEP (общие зависимости профиля: строка на пару «зависимость × действие») —
        # ТОТ ЖЕ КЛАСС, ЧТО №28, И ЭТО ДЕФЕКТ ПРИБОРА №31, измеренный 21.09.2026 нарядом
        # `AUX_SKETCH_PLANE_EDIT_DEVELOPER_PROMPT.md` §3.3 при переиздании паспорта: перечень групп
        # здесь — ЖЁСТКИЙ СПИСОК имён, `dep.json` лежал рядом с остальными отчётами, а имени группы
        # в списке не было, и группа пропускалась МОЛЧА. Следствие измеримо: `rows_group_runs`
        # показывал 381 (= 301 + 43 + 37) вместо 461, а вердикт группы DEP (80 PASS) в паспорте не
        # был виден вовсе. `report_path("dep")` при этом находил отчёт правильно — не хватало только
        # имени в списке, то есть ровно та же ошибка, что «умолчание по шаблону имени» в №28.
        "dep",
        "full",
    ):
        path, filename = report_path(name)
        if path is None:
            continue
        report = load(filename)
        context = report.get("context") or {}
        row_ids = [str(r.get("id")) for r in report.get("rows", [])]
        ids_by_group[name] = set(row_ids)
        groups.append({
            "group": name,
            "title": report.get("title"),
            "rows": len(report.get("rows", [])),
            # УНИКАЛЬНЫЕ ИМЕНА СТРОК — рядом со числом строк, потому что разницу между «594» и «503»
            # объясняют именно имена, а не количества. Внутри группы имена тоже повторяются
            # (группа вращения: 106 строк при 83 именах — измерено), и это названо, а не скрыто.
            "unique_rows": len(set(row_ids)),
            "verdicts": counts(report),
            "report": os.path.relpath(path, ROOT).replace("\\", "/"),
            "host_path": context.get("host_path"),
            "host_sha256": context.get("host_sha256"),
            # И .dll, а не только .exe: apphost одинаков в обеих конфигурациях, и группа, у которой
            # записан один `host_sha256`, «совпадала» с поставкой другой сборки. Здесь записано то,
            # чем сторож сборок ниже действительно сверяет.
            "host_dll_sha256": context.get("host_dll_sha256"),
            "worker_sha256": context.get("worker_sha256"),
            "worker_dll_sha256": context.get("worker_dll_sha256"),
            "adapter_sha256": context.get("adapter_sha256"),
        })

    # СБОРКА, КОТОРОЙ ИЗМЕРЯЛИ, ОБЯЗАНА БЫТЬ СБОРКОЙ, КОТОРУЮ ПОСТАВЛЯЕМ. Сторож стоит ДО записи
    # файла: паспорт, собранный по чужой сборке, — это паспорт не той поставки.
    measured_binaries = check_measured_binaries(package, groups)
    if measured_binaries and "--allow-foreign-binaries" not in sys.argv[1:]:
        print("ОТКАЗ ПАСПОРТА: отчёты сняты не на той сборке, которая лежит в поставке:")
        for problem in measured_binaries:
            print("  -", problem)
        print("Приёмка поставки проводится её собственными двоичными файлами: "
              "scripts/mcp-smoke.py --host <поставка>/KompasMcp.Host.exe. "
              "Осознанное исключение — ключ --allow-foreign-binaries: тогда расхождения "
              "остаются в паспорте полем measured_binaries, а не исчезают.")
        return 1

    # ПОЛНЫЙ ПРОГОН И ГРУППОВЫЕ ПРОГОНЫ — РАЗНЫЕ ПРОГОНЫ, И ИХ НЕЛЬЗЯ СКЛАДЫВАТЬ.
    # Прежде здесь стояло `total_rows = sum(g["rows"] for g in groups)`, и паспорт публиковал
    # одно число «строк», склеенное из полного прогона И из групповых прогонов тех же самых
    # проверок. На прогоне 19.09.2026 это давало 1095 «строк» там, где полный прогон измеряет 502,
    # а групповые — подмножества его же: измерено, что объединение имён строк групповых прогонов
    # (399) ЦЕЛИКОМ входит в полный прогон (479 имён), то есть каждая групповая строка посчитана
    # в сумме дважды. Число, полученное сложением, не является числом проверок и вводило в
    # заблуждение. Правило: вердикт приёмки выводится из ПОЛНОГО прогона; групповые прогоны
    # публикуются рядом отдельно, как измерение ТЕХ ЖЕ проверок по частям.
    #
    # Числа в этом пояснении НЕ ЗАШИТЫ: они считаются из тех же отчётов, что и сам паспорт, и
    # потому не устаревают при изменении состава строк (измерение 19.09.2026 после добавления
    # строки B3.58: 400 имён групповых против 480 имён полного).
    complete = next((g for g in groups if g["group"] == "full"), None)
    group_runs = [g for g in groups if g["group"] != "full"]
    if complete is not None:
        total_counts = collections.Counter(complete["verdicts"])
        rows_complete = complete["rows"]
        complete_source = complete["group"]
        rows_note = ("Вердикт приёмки выведен из ПОЛНОГО прогона. Групповые прогоны ниже — "
                     "подмножества его же проверок и в это число НЕ входят: складывать их с "
                     "полным прогоном нельзя, это посчитало бы одни проверки дважды.")
    else:
        # Полного прогона в отчётах нет — тогда и только тогда берём объединение групп, и
        # говорим об этом прямо, а не выдаём сумму за полный прогон.
        total_counts = collections.Counter()
        for group in group_runs:
            total_counts.update(group["verdicts"])
        rows_complete = sum(g["rows"] for g in group_runs)
        complete_source = "сумма групповых прогонов (полного прогона в отчётах нет)"
        rows_note = ("Полного прогона в отчётах нет: вердикт выведен по групповым прогонам. "
                     "Группы могли пересекаться между собой, поэтому число строк — сумма, а не "
                     "число различных проверок.")
    total_failed = total_counts.get("FAIL", 0)
    rows_group_runs = sum(g["rows"] for g in group_runs)

    # ИМЕНА СТРОК, А НЕ ТОЛЬКО ЧИСЛА. Числа «594 против 503» объясняются только именами: сколько
    # имён даёт объединение групповых прогонов и входят ли они в полный прогон ЦЕЛИКОМ. Обе
    # величины измеряются здесь, а не берутся из прежнего прогона.
    union_group_ids = set()
    for group in group_runs:
        union_group_ids |= ids_by_group.get(group["group"], set())
    complete_ids = ids_by_group.get(complete_source, set())
    if complete is None:
        subset_text = ("полного прогона в отчётах нет, поэтому «входят ли группы в него» не "
                       "измерялось")
    elif union_group_ids <= complete_ids:
        subset_text = ("имена строк групповых прогонов ЦЕЛИКОМ входят в полный прогон")
    else:
        subset_text = ("имена строк групповых прогонов входят в полный прогон НЕ ВСЕ — "
                       "различающиеся названы в отчётах групп")
    double_count_note = (
        f"СТРОКИ ПОЛНОГО ПРОГОНА И ГРУППОВЫХ ПРОГОНОВ НЕ СКЛАДЫВАЮТСЯ. Полный прогон — один "
        f"процесс, покрывающий весь набор проверок; групповые прогоны — те же проверки по частям, "
        f"и {subset_text} (измерено при сборке паспорта: {len(union_group_ids)} имён групповых "
        f"против {len(complete_ids)} имён полного). Сумма дала бы "
        f"{rows_group_runs + rows_complete} «строк» там, где проверок {rows_complete}, то есть "
        f"посчитала бы одни проверки дважды. Паспорт публикует оба числа РЯДОМ: "
        f"`rows_complete_run` (он же `rows`) и `rows_group_runs`."
    )

    # ТРИ УРОВНЯ ОТДЕЛЬНО. Прежде здесь стоял один вердикт «PASS, если строк с FAIL нет», и он
    # читался как «обязательный объём выполнен» — то есть канал и объём подменяли друг друга.
    # Добавлено 19.09.2026 вместе с правилом закрытия объёма в scripts/acceptance-levels.py.
    #
    # ЧЕТВЁРТЫЙ УРОВЕНЬ — КЛИЕНТСКАЯ ПРИЁМКА — считается ЗДЕСЬ ЖЕ И ДО ВЕРДИКТА. Добавлено
    # 21.09.2026 по наряду JOURNAL_SHARING_FIX_DEVELOPER_PROMPT.md §B. Прежде блок
    # `client_acceptance` вычислялся в самом литерале паспорта, уже ПОСЛЕ вердикта, и в правило не
    # попадал вовсе; следствие измерено на артефактах сеанса 2
    # (`scratch/image-client-session-20260921-run2/passport-dryrun/delivery-passport.json`):
    # `acceptance.verdict PASS`, `fully_ready true` — при `client_acceptance.verdict FAIL` и
    # `tools_visible 0`. Заголовок «PASS» поверх провалившейся клиентской приёмки — не опечатка, а
    # ровно то, что даёт правило, не спрашивающее об уровне.
    scope = levels.evaluate_scope(ROOT)
    entry = client_entry(REPORTS)
    transport = {
        "verdict": "PASS" if (package.get("verdict") == "PASS"
                              and entry.get("verdict") == "PASS") else "FAIL",
        "subject": "поставка и запись клиента: доходят ли файлы и запускается ли записанное",
        "package_verdict": package.get("verdict"),
        "package_files": package.get("file_count"),
        "package_problems": package.get("problems"),
        "schema_mismatches": package.get("schema_mismatches"),
        "repo_drift": package.get("repo_drift"),
        "client_entry_verdict": entry.get("verdict"),
        "client_entry_report": entry.get("report"),
    }
    client_acceptance_block = client_acceptance(package)
    acceptance_levels = levels.summarize_levels(dict(total_counts), scope, transport,
                                                client_acceptance_block)
    acceptance_levels["mandatory_scope"]["profile_revision"] = scope.get("profile_revision")
    if scope["problems"]:
        acceptance_levels["mandatory_scope"]["problems"] = scope["problems"]

    passport = {
        "run_id": RUN_ID,
        "generated_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "environment": {
            "os": "Microsoft Windows 10.0.26200",
            "runtime": ".NET 10.0.12 (Microsoft.WindowsDesktop.App 10.0.12 установлен — "
                       "поставка --self-contained false)",
            "delivery_dir": delivery,
            "launch_command": os.path.join(delivery, "KompasMcp.Host.exe") + " --config "
                              + os.path.join(ROOT, "config", "kompas-mcp.local.json"),
            "local_config": os.path.join(ROOT, "config", "kompas-mcp.local.json"),
            "local_config_note": "содержит абсолютные пути этой машины и в поставку не входит; "
                                 "в поставке только шаблон config/kompas-mcp.example.json",
            "worker_path_env": package.get("worker_path_env"),
            **kompas_facts(),
        },
        "package": {
            "file_count": package.get("file_count"),
            "size_mb": package.get("size_mb"),
            "pe_machine": package.get("pe_machine"),
            "host_path": package.get("host_path"),
            "host_sha256": package.get("host_sha256"),
            "host_dll_sha256": package.get("host_dll_sha256"),
            "worker_path": package.get("worker_path"),
            "worker_sha256": package.get("worker_sha256"),
            "worker_dll_sha256": package.get("worker_dll_sha256"),
            "adapter_sha256": package.get("adapter_sha256"),
            "server_info": package.get("server_info"),
            "protocol_version": package.get("protocol_version"),
            "tools_count": package.get("tools_count"),
            "schemas_shipped_count": package.get("schemas_shipped_count"),
            "schema_mismatches": package.get("schema_mismatches"),
            "repo_drift": package.get("repo_drift"),
            "problems": package.get("problems"),
            "verdict": package.get("verdict"),
        },
        "acceptance": {
            "groups": groups,
            # `rows` — строки ПОЛНОГО прогона: это и есть полный набор проверок в одном процессе.
            "rows": rows_complete,
            "rows_complete_run": rows_complete,
            "rows_complete_run_source": complete_source,
            "rows_group_runs": rows_group_runs,
            "rows_group_runs_note": rows_note,
            "failed": total_failed,
            "measured_binaries": {
                "rule": "Приёмка поставки проводится её собственными двоичными файлами: "
                        "scripts/mcp-smoke.py --host <поставка>/KompasMcp.Host.exe, Worker берётся "
                        "рядом с Host. apphost-заглушка .exe в Debug и Release одинакова, поэтому "
                        "сверяются управляемые сборки.",
                "fields": list(MEASURED_BINARY_FIELDS),
                "package": {field: package.get(field) for field in MEASURED_BINARY_FIELDS},
                "divergences": measured_binaries,
                "verdict": "PASS" if not measured_binaries else "FAIL",
                "note": ("Пусто — отчёты групп сняты на сборке поставки. Непусто — паспорт "
                         "описывает поставку, приёмка которой проведена на другой сборке; такое "
                         "публикуется только осознанно, ключом --allow-foreign-binaries, и "
                         "расхождения остаются здесь перечисленными."),
            },
            "transport_and_delivery": acceptance_levels["transport_and_delivery"],
            "functional_acceptance": acceptance_levels["functional_acceptance"],
            "mandatory_scope": acceptance_levels["mandatory_scope"],
            # Четвёртый уровень: он входит в `fully_ready`, поэтому печатается рядом с тремя — иначе
            # читатель увидел бы FAIL, не видя, от какого уровня он пришёл.
            "client_acceptance": acceptance_levels["client_acceptance"],
            "fully_ready": acceptance_levels["fully_ready"],
            "verdict": acceptance_levels["verdict"],
            "verdict_rule": acceptance_levels["verdict_rule"],
            "verdict_note": ("Вердикт обязательного объёма НЕ выводится из строк приёмки: приёмка "
                             "покрывает часть режимов, а объём считается по строкам матрицы тем же "
                             "правилом, что и в coverage/solid-v24/matrix.md."),
        },
        "client_entry": entry,
        # Тот же объект, что ушёл в правило уровней: вердикт и блок отчёта обязаны быть ОДНИМ
        # чтением, иначе паспорт снова мог бы напечатать вердикт по одной редакции блока, а рядом
        # положить другую.
        "client_acceptance": client_acceptance_block,
        "notes": [
            "Приёмка идёт по бинарям поставки: scripts/mcp-smoke.py --host <поставка>; "
            "KOMPAS_MCP_WORKER_PATH не задаётся, Worker берётся рядом с Host. Это СВЕРЯЕТСЯ, а не "
            "заявляется: блок acceptance.measured_binaries сравнивает управляемые сборки каждой "
            "группы с поставкой и при расхождении ОТКАЗЫВАЕТ в записи паспорта (ключ "
            "--allow-foreign-binaries публикует расхождение полем, а не прячет).",
            "Полный прогон не заменяет отдельные группы вращения, отверстия и скругления — они "
            "измеряют больше строк на ту же возможность.",
            double_count_note,
            "Группа определённости эскиза идёт первой: она создаёт внешний документ, который "
            "открывает строка KS.09 полного прогона.",
            "client_entry и client_acceptance — разные утверждения: первое про запуск записи из "
            "конфига, второе про реальные вызовы инструментов агентом клиента. Одно не заменяет "
            "другое.",
            "Подтверждение записи клиентом проверяется отдельно (scripts/verify-mcp-approval.py): "
            "и client_entry, и client_acceptance запускают Host сами и потому не различают "
            "«запись рабочая» и «клиент её запустит».",
            "Вердикт паспорта — производная ЧЕТЫРЁХ уровней (транспорт/доставка, функциональная "
            "приёмка, обязательный объём, клиентская приёмка) и не может быть PASS, если хоть один "
            "не пройден. Правило и контроль к нему (положительный плюс по одному отрицательному на "
            "уровень) живут в scripts/acceptance-levels.py (--self-test); своя копия правила в "
            "паспорте разошлась бы с клиентской приёмкой. Клиентская приёмка вошла в правило "
            "21.09.2026: до этого паспорт печатал `verdict PASS` и `fully_ready true` рядом с "
            "собственным `client_acceptance.verdict FAIL` (измерено на артефактах сеанса 2), а "
            "`client_acceptance: not_run` не мешал вердикту вовсе.",
            "`client_acceptance` встречается ДВАЖДЫ и это разные вещи: в `acceptance` лежит "
            "УРОВЕНЬ (вердикт и причина отказа, он входит в `fully_ready`), в корне паспорта — "
            "ПОЛНЫЙ блок отчёта клиентской приёмки (сеанс, pid, сценарии, строки). Один без другого "
            "читается неверно: вердикт без блока не объясняет, что именно клиент не сделал, а блок "
            "без вердикта не говорит, как он повлиял на выпуск.",
        ],
    }

    os.makedirs(OUT_DIR, exist_ok=True)
    out = os.path.join(OUT_DIR, "delivery-passport.json")
    # Паспорт пишется в файл с ФИКСИРОВАННЫМ именем, поэтому прогон без ключей затирает паспорт
    # ДРУГОЙ поставки. Так 19.09.2026 был потерян прежний
    # `docs/acceptance/delivery-20260918/delivery-passport.json`: скрипт вызвали без ключей, и он
    # молча записал поверх. Теперь запись поверх паспорта другой поставки отказывает и требует
    # явного `--force` — потеря обязана быть решением, а не побочным эффектом.
    if os.path.exists(out) and "--force" not in sys.argv[1:]:
        try:
            with open(out, encoding="utf-8-sig") as fh:
                previous_run_id = (json.load(fh) or {}).get("run_id")
        except (OSError, ValueError):
            previous_run_id = None
        if previous_run_id and previous_run_id != RUN_ID:
            print(f"ОТКАЗ: {out} описывает поставку {previous_run_id!r}, "
                  f"а собирается {RUN_ID!r}.")
            print("Паспорт другой поставки не перезаписывается молча: "
                  "укажите --out для своего каталога либо --force, если потеря прежнего паспорта "
                  "осознана.")
            return 1
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(passport, fh, ensure_ascii=False, indent=2)
    print("записано:", out)
    print(json.dumps({
        "acceptance": passport["acceptance"]["verdict"],
        "fully_ready": passport["acceptance"]["fully_ready"],
        "levels": {k: passport["acceptance"][k]["verdict"]
                   for k in ("transport_and_delivery", "functional_acceptance", "mandatory_scope")},
        "mandatory_scope": f"{passport['acceptance']['mandatory_scope']['modes_closed']}/"
                           f"{passport['acceptance']['mandatory_scope']['modes_total']} режимов, "
                           f"{passport['acceptance']['mandatory_scope']['deps_closed']}/"
                           f"{passport['acceptance']['mandatory_scope']['deps_total']} зависимостей",
        "rows_complete_run": rows_complete,
        "rows_complete_run_source": complete_source,
        "rows_group_runs": rows_group_runs,
        "rows_group_runs_note": "подмножества полного прогона; НЕ прибавлять к нему",
        "failed": total_failed,
        "groups": [(g["group"], g["rows"], g["verdicts"]) for g in groups],
        # `.get`, а не `[...]`: когда проверки записи клиента не было, блок отвечает
        # `{"status": "not_run"}` и ключа `verdict` в нём НЕТ. Прямое обращение роняло генератор
        # паспорта на KeyError уже ПОСЛЕ записи файла — то есть паспорт оставался, а код возврата
        # говорил об отказе (найдено 19.09.2026 при сборке паспорта поставки
        # publish-b3-20260919-identity-fixed). Отсутствие проверки — это состояние, а не ошибка.
        "client_entry": passport["client_entry"].get("verdict"),
        "client_entry_status": passport["client_entry"].get("status"),
        "client_acceptance": passport["client_acceptance"]["status"],
        "client_acceptance_verdict": passport["client_acceptance"]["verdict"],
    }, ensure_ascii=False, indent=2))
    print("отчёты в каталоге:",
          [os.path.basename(p) for p in sorted(glob.glob(os.path.join(REPORTS, "*.json")))])


if __name__ == "__main__":
    # Самопроверка прибора: правило уровней (положительный набор и по одному отрицательному на
    # каждый уровень), сторож сборок и контроль вердикта НА АРТЕФАКТАХ СЕАНСА 2 — генератор обязан
    # дать FAIL при провалившейся клиентской приёмке и PASS при пройденной, на одних и тех же
    # отчётах. Все три обязаны уметь отказать; ни один не имеет права «пройти» молча.
    if "--self-test" in sys.argv[1:]:
        raise SystemExit(levels.self_test() or self_test_measured_binaries()
                         or self_test_client_gate())
    # `main()` возвращает код: без `SystemExit` отказ (например, защита от перезаписи чужого
    # паспорта) печатался, но наружу отдавал 0 — вызывающая цепочка видела успех там, где записи
    # НЕ было. Возврат значения обязан быть видимым.
    raise SystemExit(main())
