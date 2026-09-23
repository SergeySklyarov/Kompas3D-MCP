"""Четыре уровня готовности: транспорт/доставка, функциональная приёмка, обязательный объём,
клиентская приёмка.

ЗАЧЕМ ОТДЕЛЬНЫЙ МОДУЛЬ. Четыре отчёта отвечают на один и тот же вопрос «готово ли», и до
19.09.2026 они отвечали на него по-разному: клиентская приёмка не отвечала вовсе (вердикт ставился
константой), паспорт считал «FAIL по строкам», `emit-coverage-matrix.py` считал закрытие по
`actions` строки матрицы, а HTML-прогресс — по проценту. Один вердикт на четыре разных вопроса
читается как «готово» там, где готово не всё: 18.09.2026 клиентская приёмка стояла с PASS, имея два
дефекта продукта и незакрытый обязательный режим. Поэтому правило живёт ЗДЕСЬ, в одной функции, а
потребители читают её результат.

УРОВНИ НЕ ПОДМЕНЯЮТ ДРУГ ДРУГА:
  * транспорт/доставка — «запускается ли то, что записано, и доходят ли вызовы»;
  * функциональная приёмка — «соответствует ли поведение объявленному ожиданию» (по строкам);
  * обязательный объём — «закрыты ли обязательные режимы профиля» (по правилу `mode_closed`);
  * клиентская приёмка — «приняла ли поставку РАБОЧАЯ КЛИЕНТСКАЯ СЕССИЯ, а не прибор».
`fully_ready` истинно только когда пройдены ВСЕ ЧЕТЫРЕ.

ПОЧЕМУ ЧЕТВЁРТЫЙ УРОВЕНЬ ПОЯВИЛСЯ 21.09.2026. Три уровня выше измеряют поставку ПРИБОРАМИ, а
клиентская приёмка — единственный уровень, который измеряет её клиентом. Пока он не входил в
правило, паспорт печатал «PASS» поверх собственного блока `client_acceptance: FAIL`. ИЗМЕРЕНО на
артефактах сеанса 2 (`scratch/image-client-session-20260921-run2/passport-dryrun/`):
`acceptance.verdict PASS`, `fully_ready true` — при `client_acceptance.verdict FAIL`, `tools_visible
0` и нуле вызовов. Это не опечатка генератора, а следствие правила: `summarize_levels` о
клиентской приёмке не спрашивал вовсе, и «не спросил» было неотличимо от «пройдено». Заголовок
«PASS» над провалившейся клиентской приёмкой читается как разрешение выпускать — ровно та
подмена, ради неразличения которой уровни и разделены.

`not_run` — это НЕ «не проверено, пропускаем»: отсутствие проверки не является PASS, поэтому
невыполненная клиентская приёмка даёт уровню вердикт FAIL и НАЗВАННУЮ причину. Обратное правило
(«нет отчёта — не мешает») делало бы выпуск тем легче, чем меньше измерено.

ПРАВИЛО ЗАКРЫТИЯ ОБЪЁМА. Режим закрыт, когда все применимые действия его строки матрицы имеют
статус `verified` или `not_applicable` (профиль `closed_definition.mode_closed`). Статус
`blocked_api` НЕ закрывает: он означает, что возможность недостижима, а не что она проверена.
Общая зависимость закрыта по действиям, которые она объявляет в `required_actions`.

КОНТРОЛЬ ПРИБОРА (`--self-test`). Один ПОЛОЖИТЕЛЬНЫЙ набор (пройдено всё — обязан дать PASS) и
по одному отрицательному на каждый уровень: (а) один обязательный FAIL в строках при закрытом
объёме; (б) незакрытое положительное создание (`create = blocked_api`) с N/A-зависимыми
действиями — ровно то состояние, которое до 19.09.2026 закрывалось правилом
`all(v in ("verified", "not_applicable"))`; (в) инструменты клиенту не видны; (г) клиентская
приёмка FAIL при трёх пройденных уровнях; (д) клиентская приёмка `not_run`; (е) клиентская приёмка
не передана в правило. Ни один отрицательный набор не имеет права дать «полностью готово», и
каждый называет СВОЙ уровень-виновника. Положительный набор обязателен ровно так же:
отрицательный контроль сам по себе проходится правилом, всегда отвечающим FAIL.
"""

import json
import os

PROFILE_REL = "coverage/solid-v24/release-profiles/mechanical-core-v1.json"
MATRIX_REL = "coverage/solid-v24/matrix.json"

CLOSING_STATUSES = ("verified", "not_applicable")

LEVEL_NAMES = ("transport_and_delivery", "functional_acceptance", "mandatory_scope",
               "client_acceptance")

# Строка «self» вместо блока: вызывающий САМ является клиентской приёмкой (его отчёт — предмет, а
# не гейт). Иначе отчёт клиентской приёмки гейтился бы собственным вердиктом — то есть правилом,
# которое он же и вычисляет.
CLIENT_ACCEPTANCE_SELF = "self"

VERDICT_RULE = ("PASS только когда пройдены ВСЕ уровни: транспорт/доставка, функциональная "
                "приёмка, обязательный объём И клиентская приёмка; иначе FAIL. Уровни не подменяют "
                "друг друга: «канал работает» не означает «объём выполнен», а «объём выполнен» не "
                "означает «клиент принял». Невыполненная клиентская приёмка (`not_run`) даёт FAIL, "
                "а не пропускается: отсутствие проверки не является PASS")


def load(root, relative):
    # `utf-8-sig`, а не `utf-8`: файлы покрытия записаны С BOM (проектное соглашение).
    with open(os.path.join(root, relative), encoding="utf-8-sig") as handle:
        return json.load(handle)


def evaluate_scope(root):
    """Состояние обязательного объёма профиля выпуска.

    Возвращает словарь, а не вердикт-строку: читателю нужны и счётчики, и ИМЕНА незакрытых строк с
    названной причиной. Вердикт — производная, и он не может быть «PASS» по одному числу строк.
    """
    profile = load(root, PROFILE_REL)
    matrix = load(root, MATRIX_REL)
    actions = matrix["meta"]["actions"]
    index = {row["operation_id"]: row for row in matrix["rows"]}
    problems = []

    def row_state(ref):
        row = index.get(ref)
        if row is None:
            return None, {}, None
        return row.get("actions") or {}, row.get("blocked_reasons") or {}, row

    def state_of(ref):
        state, blocked, row = row_state(ref)
        closed = bool(state) and all(v in CLOSING_STATUSES for v in state.values())
        return state, blocked, row, closed

    modes = [m for m in profile.get("modes") or []
             if m.get("priority") == "practical_required"]
    closed_modes, open_modes = [], []
    for mode in modes:
        ref = mode["ref"]
        state, blocked_reasons, row, closed = state_of(ref)
        if closed:
            closed_modes.append(ref)
        else:
            blocked = sorted(a for a, v in (state or {}).items() if v.startswith("blocked_"))
            missing = sorted(a for a in actions if a not in (state or {}))
            open_modes.append({
                "ref": ref,
                "queue": mode.get("queue"),
                "title": mode.get("title"),
                "row_present": row is not None,
                "blocked_actions": blocked,
                "missing_actions": missing,
                "blocked_reason": blocked_reasons.get(blocked[0]) if blocked else None,
            })
        # Профиль — производная от строки матрицы; расхождение обязано быть видно, а не сглажено.
        level = mode.get("current_level")
        if state and ((closed and level != "mcp_verified")
                      or (not closed and level == "mcp_verified")):
            problems.append(f"{ref}: строка матрицы говорит "
                            f"{'закрыто' if closed else 'открыто'}, а профиль — {level!r}")

    deps = profile.get("common_dependencies") or []
    closed_deps, open_deps = [], []
    for dep in deps:
        state, blocked_reasons, row, _ = state_of(dep["id"])
        needed = dep.get("required_actions") or actions
        if state and all(state.get(a) in CLOSING_STATUSES for a in needed):
            closed_deps.append(dep["id"])
        else:
            blocked = sorted(a for a in needed
                             if (state or {}).get(a, "").startswith("blocked_"))
            # ЧТО ИМЕННО ДЕРЖИТ ЗАВИСИМОСТЬ — НАЗЫВАЕТСЯ. Здесь стоял только `blocked_actions`, и он
            # пуст у зависимости, открытой по `not_started`: прежний вывод печатал
            # `blocked_actions: [], blocked_reason: null` — то есть открытая зависимость была названа,
            # а ЧЕМ она открыта — нет. «Молчание — тоже утверждение»: пустой список читается как
            # «ничего не мешает», хотя мешают ровно те действия, которых нет. ИЗМЕРЕНО 21.09.2026:
            # так выглядели ВСЕ 10 открытых зависимостей при 24 незакрытых действиях.
            statuses = {a: (state or {}).get(a, "нет в строке") for a in needed}
            missing = sorted(a for a in needed if statuses[a] not in CLOSING_STATUSES)
            if blocked:
                reason = blocked_reasons.get(blocked[0])
            elif missing:
                reason = "открыты действия: " + ", ".join(f"{a}={statuses[a]}" for a in missing)
            else:
                reason = "строка отсутствует в матрице" if row is None else None
            open_deps.append({
                "id": dep["id"],
                "queue": dep.get("queue"),
                "row_present": row is not None,
                "blocked_actions": blocked,
                "missing_actions": missing,
                "action_statuses": statuses,
                "blocked_reason": reason,
            })

    open_total = len(open_modes) + len(open_deps)
    return {
        "profile_id": profile["meta"]["profile_id"],
        "profile_revision": profile["meta"].get("revision"),
        "profile_artifact": PROFILE_REL,
        "matrix_artifact": MATRIX_REL,
        "rule": "closed_definition.mode_closed: все применимые действия verified или "
                "not_applicable с обоснованием; blocked_api НЕ закрывает",
        "modes_total": len(modes),
        "modes_closed": len(closed_modes),
        "modes_open": open_modes,
        "deps_total": len(deps),
        "deps_closed": len(closed_deps),
        "deps_open": open_deps,
        "open_total": open_total,
        "verdict": "COMPLETE" if open_total == 0 else "INCOMPLETE",
        "problems": problems,
    }


def client_acceptance_gate(block):
    """Вердикт уровня «клиентская приёмка» и НАЗВАННАЯ причина отказа.

    Состояний ровно три, и ни одно не означает «пропустить»:
      * `block is None` — приёмка не передана в правило: состояния нет вовсе;
      * `status == "not_run"` — вызовов клиента не было;
      * `status == "run"` — вердикт берётся из самого отчёта клиентской приёмки.
    Всё, кроме `verdict == "PASS"`, даёт FAIL. Возврат `(None, None)` — только для строки
    `CLIENT_ACCEPTANCE_SELF`: тогда гейт не применяется, и это записано в самом результате, а не
    остаётся молчанием.
    """
    if block is None:
        return "FAIL", "клиентская приёмка не передана в правило: состояние не измерено"
    if isinstance(block, str):
        if block == CLIENT_ACCEPTANCE_SELF:
            return None, None
        return "FAIL", f"клиентская приёмка передана строкой {block!r}, а не блоком отчёта"
    status = block.get("status")
    if status == "not_run":
        return "FAIL", ("клиентская приёмка не выполнена (status=not_run): отсутствие проверки "
                        "не является PASS")
    verdict = block.get("verdict")
    if verdict == "PASS":
        return "PASS", None
    return "FAIL", f"клиентская приёмка: status={status!r}, verdict={verdict!r}"


def summarize_levels(counts, scope, transport, client_acceptance=None):
    """Четыре независимых вердикта и производный общий, который не может быть PASS, если хоть один
    уровень не пройден. `counts` — счётчики вердиктов строк ({'PASS': n, 'FAIL': n, 'N/A': n});
    `client_acceptance` — блок отчёта клиентской приёмки (`{'status':…, 'verdict':…}`) либо строка
    `CLIENT_ACCEPTANCE_SELF`, если вызывающий сам является этим отчётом.

    Умолчание `None` НЕ значит «без гейта»: это состояние «клиентская приёмка не передана», и оно
    даёт FAIL. Так исключён тихий пропуск — потребитель, забывший передать блок, увидит отказ, а не
    «PASS по трём уровням».
    """
    fail = counts.get("FAIL", 0)
    passed = counts.get("PASS", 0)
    functional = "PASS" if (fail == 0 and passed > 0) else "FAIL"
    transport_verdict = transport.get("verdict") or (
        "PASS" if (transport.get("client_calls_observed", 0) > 0
                   and transport.get("client_calls_foreign_pid", 0) == 0
                   and transport.get("tools_visible") == transport.get("tools_total")) else "FAIL")
    scope_verdict = scope["verdict"]
    gate_verdict, gate_reason = client_acceptance_gate(client_acceptance)
    gate_applies = gate_verdict is not None
    fully_ready = (transport_verdict == "PASS" and functional == "PASS"
                   and scope_verdict == "COMPLETE"
                   and (not gate_applies or gate_verdict == "PASS"))
    return {
        "transport_and_delivery": dict(
            transport,
            verdict=transport_verdict,
            subject=transport.get("subject") or "канал и запуск поставки, а не поведение инструментов",
        ),
        "functional_acceptance": {
            "verdict": functional,
            "subject": "строки приёмки: соответствие объявленному ожиданию",
            "rows": sum(counts.values()),
            "pass": passed,
            "fail": fail,
            "not_applicable": counts.get("N/A", 0),
            "other": {k: v for k, v in counts.items() if k not in ("PASS", "FAIL", "N/A")},
        },
        "mandatory_scope": {
            "verdict": scope_verdict,
            "subject": "обязательные режимы профиля выпуска и его общие зависимости",
            "rule": scope["rule"],
            "modes_closed": scope["modes_closed"],
            "modes_total": scope["modes_total"],
            "deps_closed": scope["deps_closed"],
            "deps_total": scope["deps_total"],
            "open_total": scope["open_total"],
            # СПИСОК ПУБЛИКУЕТСЯ ЦЕЛИКОМ. Здесь стоял срез `[:12]` — БЕЗ ПОМЕТКИ об обрезке, и это
            # было молчанием, которое читается как утверждение «это всё»: при 20 открытых записях
            # (6 режимов SM-07 + 14 зависимостей) восемь из них не попадали ни в паспорт, ни в
            # INDEX, а `open_total` рядом называл другое число, и расхождение приходилось замечать
            # читателю. Срез без пометки неотличим от «список полон»; поэтому либо полный список,
            # либо названное усечение — выбран полный.
            "open": ([{"ref": m["ref"], "queue": m.get("queue"),
                       "blocked_actions": m["blocked_actions"],
                       "missing_actions": m.get("missing_actions"),
                       "blocked_reason": m["blocked_reason"]} for m in scope["modes_open"]]
                     + [{"ref": d["id"], "queue": d.get("queue"),
                         "blocked_actions": d["blocked_actions"],
                         "missing_actions": d.get("missing_actions"),
                         "action_statuses": d.get("action_statuses"),
                         "blocked_reason": d["blocked_reason"]} for d in scope["deps_open"]]),
            "open_published": len(scope["modes_open"]) + len(scope["deps_open"]),
            "open_note": "`open` совпадает по длине с `open_total`: список открытого печатается "
                         "ПОЛНОСТЬЮ. Прежняя редакция обрезала его до 12 записей без пометки, и "
                         "усечение без пометки читается как «это всё»",
            "profile_artifact": scope["profile_artifact"],
            "matrix_artifact": scope["matrix_artifact"],
            "note": "вердикт объёма НЕ выводится из строк приёмки: приёмка покрывает часть режимов, "
                    "а объём считается по строкам матрицы",
        },
        # ЧЕТВЁРТЫЙ УРОВЕНЬ ПУБЛИКУЕТСЯ РЯДОМ С ТРЕМЯ. Он входит в `fully_ready`, поэтому обязан
        # быть виден и назван — иначе читатель паспорта увидит FAIL, не видя, откуда он.
        "client_acceptance": {
            "verdict": gate_verdict,
            "subject": "вызовы инструментов РАБОЧИМ КЛИЕНТОМ: приняла ли поставку клиентская "
                       "сессия, а не прибор",
            "gate": ("не применяется: этот отчёт и есть клиентская приёмка" if not gate_applies
                     else "применяется: PASS только при status=run и verdict=PASS"),
            "status": client_acceptance.get("status")
                      if isinstance(client_acceptance, dict) else None,
            "report_verdict": client_acceptance.get("verdict")
                              if isinstance(client_acceptance, dict) else None,
            "reason": gate_reason,
        },
        "fully_ready": fully_ready,
        "verdict": "PASS" if fully_ready else "FAIL",
        "verdict_rule": VERDICT_RULE,
    }


def self_test():
    """Контроль правила: один положительный набор и по одному отрицательному на КАЖДЫЙ уровень.

    ПОЛОЖИТЕЛЬНЫЙ НАБОР СТОИТ ПЕРВЫМ И ОБЯЗАТЕЛЕН. Правило, всегда отвечающее FAIL, проходит любой
    отрицательный контроль, ничего при этом не измеряя; поэтому «пройдено всё» обязано дать PASS —
    иначе контроль не различает «отказ по делу» и «отказ всегда».

    Каждый отрицательный набор строится так, чтобы отказать мог ТОЛЬКО проверяемый уровень, и
    ожидаемый виновник назван по имени: без этого набор с незакрытым объёмом «проходил» бы за счёт
    клиентской приёмки, и контроль перестал бы проверять объём. Набор (в) воспроизводит состояние,
    которое до 19.09.2026 закрывалось правилом `all(v in ("verified", "not_applicable"))`:
    положительное создание недостижимо (`blocked_api`), а зависимые от него действия предметно
    неприменимы. Наборы (д) и (е) воспроизводят состояние сеанса 2 от 21.09.2026, когда три уровня
    были пройдены, а клиент не видел НИ ОДНОГО инструмента (`tools_visible 0`).

    Все наборы СИНТЕТИЧЕСКИЕ и живут только здесь: это контроль ПРАВИЛА, а не измерение продукта.
    Ссылка на реальный режим здесь не нужна и вредна: правило проверяется на составе статусов, и
    привязка контроля к режиму, снятому из требований, пережила бы сам режим.
    """
    # Числа знаменателя здесь — ЗЕРКАЛО текущего профиля, а не измерение: 54 режима и 15
    # зависимостей после снятия `dep.bodies.remove_selected` (наряд
    # DEPENDENCIES_FULL_CLOSURE_DEVELOPER_PROMPT.md §2, решение заказчика 21.09.2026). Прежняя
    # редакция держала здесь 16 — знаменатель ДО снятия; держать его дальше значило бы печатать
    # в контроле число, которого в профиле уже нет. Контроль проверяет ПРАВИЛО, и от знаменателя
    # его вердикт не зависит — но читаемое число обязано совпадать с действительностью.
    closed = {"verdict": "COMPLETE", "modes_closed": 54, "modes_total": 54, "deps_closed": 15,
              "deps_total": 15, "open_total": 0, "modes_open": [], "deps_open": [], "rule": "test",
              "problems": [], "profile_artifact": "test", "matrix_artifact": "test"}
    opened = dict(closed, verdict="INCOMPLETE", modes_closed=53, open_total=1,
                  modes_open=[{"ref": "example.mode.create_blocked",
                               "blocked_actions": ["create", "geometry_validation"],
                               "blocked_reason": "положительное создание недостижимо (синтетика)"}])
    # Числа транспорта — тоже ЗЕРКАЛО текущей поставки (48 инструментов/схем на проводе), а не
    # измерение: контроль проверяет правило вывода вердикта, а не канал. Прежняя редакция держала
    # 36 — размер более ранней поставки.
    transport_ok = {"client_calls_observed": 435, "client_calls_foreign_pid": 0,
                    "tools_visible": 48, "tools_total": 48, "tools_invoked": 24}
    transport_bad = dict(transport_ok, client_calls_observed=0, tools_visible=0, tools_invoked=0)
    clean_rows = {"PASS": 178, "N/A": 6}
    client_ok = {"status": "run", "verdict": "PASS"}
    client_fail = {"status": "run", "verdict": "FAIL"}
    client_absent = {"status": "not_run", "verdict": None}
    # (имя, счётчики строк, объём, транспорт, клиентская приёмка, ожидаемый вердикт, виновник)
    cases = [
        ("пройдено всё (положительный набор)",
         clean_rows, closed, transport_ok, client_ok, "PASS", None),
        ("один обязательный FAIL в строках при закрытом объёме",
         {"PASS": 178, "FAIL": 1, "N/A": 6}, closed, transport_ok, client_ok, "FAIL",
         "functional_acceptance"),
        ("незакрытое положительное создание + N/A-зависимые действия",
         {"PASS": 6, "N/A": 6}, opened, transport_ok, client_ok, "FAIL", "mandatory_scope"),
        ("инструменты клиенту не видны (транспорт)",
         clean_rows, closed, transport_bad, client_ok, "FAIL", "transport_and_delivery"),
        ("три уровня пройдены, клиентская приёмка FAIL — состояние сеанса 2",
         clean_rows, closed, transport_ok, client_fail, "FAIL", "client_acceptance"),
        ("три уровня пройдены, клиентская приёмка not_run",
         clean_rows, closed, transport_ok, client_absent, "FAIL", "client_acceptance"),
        ("клиентская приёмка не передана в правило",
         clean_rows, closed, transport_ok, None, "FAIL", "client_acceptance"),
    ]
    failures = []
    for name, counts, scope, transport, client, expected, blamed in cases:
        got = summarize_levels(counts, scope, transport, client)
        if got["verdict"] != expected:
            failures.append(f"{name}: вердикт {got['verdict']!r}, ожидание {expected!r}")
        if got["fully_ready"] is not (expected == "PASS"):
            failures.append(f"{name}: fully_ready={got['fully_ready']!r}, "
                            f"ожидание {expected == 'PASS'}")
        if blamed is not None:
            actual = (got.get(blamed) or {}).get("verdict")
            if actual not in ("FAIL", "INCOMPLETE"):
                failures.append(f"{name}: виновник назван {blamed}, а его вердикт {actual!r} — "
                                f"отказ пришёл не от него")
    if failures:
        print("КОНТРОЛЬ НЕ ПРОЙДЕН:")
        for line in failures:
            print("  -", line)
        return 1
    print(f"контроль пройден: {len(cases)} наборов — 1 положительный (обязан дать PASS) и "
          f"{len(cases) - 1} отрицательных, каждый отказал по своему уровню")
    return 0


if __name__ == "__main__":
    import sys
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    if "--self-test" in sys.argv[1:]:
        raise SystemExit(self_test())
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    state = evaluate_scope(root)
    print(json.dumps(state, ensure_ascii=False, indent=2))
    raise SystemExit(1 if state["problems"] else 0)
