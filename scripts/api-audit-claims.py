"""Audit step 4: does the coverage matrix's own claim survive the acceptance run?

  python scripts/api-audit-claims.py [--report <all-report.json>] [--out <json>] [--self-test]

A matrix row's `actions` are CLAIMS. This instrument checks them against the run that the delivery
was accepted by, and reports four things SEPARATELY, because they are four different faults:

  * `rows_verified_without_any_linked_row` - the row claims `verified` and its `tests[]` resolve to no
    acceptance row at all (over-claim);
  * `rows_verified_contradicted_by_fail` - a linked acceptance row is FAIL (contradiction);
  * `rows_action_without_dedicated_row` - no linked row is NAMED after the action, so the claim cannot
    be found by name (a traceability gap, not proof the check is missing);
  * `rows_actions_without_required_calls` - the linked rows' CALL WINDOWS contain no call the action
    requires (a refusal).

The call-derived signature is a NECESSARY condition, never a confirmation. It is calibrated on the
rows whose NAME carries the action (`B3M.<mode>.<action>`), where the right answer is known, and the
calibration is part of the output: a signature that fires on foreign rows distinguishes nothing, and
then the refusal is reported as the instrument's narrowness, not as a fact about the coverage.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from api_audit_common import (MATRIX, PROFILES_DIR, default_out, is_route_key, load_json,  # noqa: E402
                              save_json, stamp)

ACTIONS = ("discover", "create", "read", "edit", "rebuild", "save_reopen", "suppress_restore",
           "delete_dependencies", "negative_tests", "geometry_validation")

# What a call of each action LOOKS like in the journal. `None` means "the definition names no call":
# a refusal by it would be a refusal by guesswork, and `rebuild` is exactly that case (it is defined
# as checking that dependents rebuild, which no single tool call expresses).
# INVARIANT: a list names the tool that PERFORMS the action, so a domain with its own tool is named
# by that tool. A prefix wider than the domain is not used: `kompas_get_` would credit any read to
# every row, which is the false confirmation the sweep of false checks removed.
# History: docs/decisions/api-audit.md#section-view-calls
ACTION_CALLS = {
    "discover": {"any": [("prefix", "kompas_list_"), ("exact", "kompas_get_context")]},
    "create": {"any": [("prefix", "kompas_create_"), ("prefix", "kompas_pattern_"),
                       ("prefix", "kompas_insert_"), ("prefix", "kompas_add_"),
                       ("prefix", "kompas_bind_"), ("exact", "kompas_extrude"),
                       ("exact", "kompas_rotated"), ("exact", "kompas_hole"),
                       ("exact", "kompas_fillet"), ("exact", "kompas_chamfer"),
                       ("exact", "kompas_boolean"), ("exact", "kompas_split"),
                       ("exact", "kompas_cut_by_plane"), ("exact", "kompas_reposition"),
                       ("exact", "kompas_shell"), ("exact", "kompas_loft"),
                       ("exact", "kompas_sweep"), ("exact", "kompas_set_material"),
                       ("exact", "kompas_set_sketch_plane"), ("exact", "kompas_set_variable")]},
    "read": {"any": [("exact", "kompas_get_feature"), ("exact", "kompas_get_pattern"),
                     ("exact", "kompas_get_material"), ("exact", "kompas_list_bodies"),
                     ("exact", "kompas_read_topology"), ("exact", "kompas_get_sketch_status"),
                     ("exact", "kompas_get_section_view")]},
    "edit": {"any": [("exact", "kompas_update_feature"), ("exact", "kompas_update_plane"),
                     ("exact", "kompas_edit_sketch"), ("exact", "kompas_edit_sketch_entity"),
                     ("exact", "kompas_edit_view"), ("exact", "kompas_update_section_view")]},
    "rebuild": None,
    "save_reopen": {"all": [("exact", "kompas_save_document")],
                    "any": [("exact", "kompas_open_document"), ("exact", "kompas_close_document")]},
    "suppress_restore": {"any": [("exact", "kompas_set_feature_suppressed")]},
    "delete_dependencies": {"any": [("exact", "kompas_delete_feature"),
                                    ("exact", "kompas_delete_component"),
                                    ("exact", "kompas_delete_mate"),
                                    ("exact", "kompas_delete_sketch_constraint"),
                                    ("exact", "kompas_delete_section_view")]},
    "geometry_validation": {"any": [("exact", "kompas_measure"), ("exact", "kompas_measure_gap"),
                                    ("exact", "kompas_check_interference")]},
    "negative_tests": {"outcome": "error"},
}


def matches(call_tool, rule):
    kind, value = rule
    return call_tool == value if kind == "exact" else call_tool.startswith(value)


def signature_ok(action, calls):
    """(satisfied, reason). The OUTCOME-derived action reads the error code, never the tool name."""
    spec = ACTION_CALLS.get(action)
    if spec is None:
        return True, "у действия нет подписи вызовов: отказ по нему был бы отказом по догадке"
    if spec.get("outcome") == "error":
        # A refusal test must CONTAIN a refusal. The error code is read, not the `isError` flag:
        # measured on this project, an envelope carrying an error code can have `isError = false`.
        refused = [call for call in calls if call.get("error")]
        return bool(refused), f"вызовов с кодом отказа: {len(refused)}"
    # A call that ended in an ERROR is not an action: a row that proves a REFUSAL of the edit must not
    # be credited with the edit. Selecting candidates with the same instrument that judges them
    # excludes errored calls by construction.
    done = [call for call in calls if not call.get("error")]
    if "all" in spec and not any(matches(call.get("tool"), rule) for call in done
                                 for rule in spec["all"]):
        return False, "нет обязательного вызова " + ", ".join(v for _, v in spec["all"])
    if "any" in spec and not any(matches(call.get("tool"), rule) for call in done
                                 for rule in spec["any"]):
        return False, "нет ни одного вызова из " + ", ".join(v for _, v in spec["any"])
    return True, "подпись выполнена"


def test_ids(tests):
    """Acceptance row ids out of a matrix row's `tests[]`.

    `tests[]` is PROSE, not ids: an entry reads `FL07 - ...` or `G01 (приёмка)`. The prose tail is cut
    at whitespace, an em/en dash or an opening parenthesis - but NOT at a bare hyphen, because ids
    themselves carry one (`DRW-01.views.create_standard.create`). Splitting at the hyphen made every
    `DRW-*` row read as unlinked, i.e. it manufactured 19 over-claims out of a separator choice.
    """
    ids = []
    for entry in tests or []:
        if not isinstance(entry, str):
            continue
        token = re.split(r"[\s—–(]", entry.strip(), maxsplit=1)[0].strip(".,;:")
        if token:
            ids.append(token)
    return ids


def report_index(report):
    """`{id: {"verdicts": [...], "windows": [...], "groups": [...]}}` over the acceptance rows.

    An id may repeat (the same row id in several groups); every occurrence is kept, and "any PASS"
    answers for existence while "any FAIL" answers for contradiction.
    """
    index = {}
    for row in report.get("rows") or []:
        entry = index.setdefault(row.get("id"), {"verdicts": [], "windows": [], "groups": []})
        entry["verdicts"].append(row.get("verdict"))
        entry["windows"].append(row.get("calls_window") or [0, 0])
        entry["groups"].append(row.get("group"))
    return index


def calls_in(windows, calls):
    """Journal entries inside the union of the given windows. Group runs are NOT summed with the full
    run - they are subsets of it; the caller names which report it read."""
    seen = set()
    result = []
    for start, end in windows:
        for index in range(max(0, start), min(end, len(calls))):
            if index not in seen:
                seen.add(index)
                result.append(calls[index])
    return result


def declared_routes(row):
    """`{action: route}` for the row's own `*_route` fields, read by NAME PATTERN."""
    routes = {}
    for key, value in row.items():
        if is_route_key(key):
            routes[key[:-len("_route")]] = value
    return routes


def route_satisfied(route, calls):
    """A declared route is judged only in the row's own window, and only by its own tools.

    `require_args` demands the argument NAMES in the journal, because the journal records `args_keys`:
    a route without its arguments is a different call, and treating it as the same would credit a row
    with a call it never made. `require_args` and `forbid_args` are judged on the SAME call: a route
    names ONE call shape (an enumeration call carries the sketch and NOT the address; an addressed
    read carries both), so judging the two halves independently over the whole window refuses a route
    as soon as the same tool was called once more with the forbidden argument. Measured on
    `dep.sketch.entities`, whose `discover_route` was satisfied in its own window and still read as
    refused because a neighbouring linked row read the same list WITH the address.
    """
    problems = []
    for item in (route or {}).get("routes") or []:
        tool = item.get("tool")
        require = set(item.get("require_args") or [])
        forbid = set(item.get("forbid_args") or [])
        matched = [call for call in calls if call.get("tool") == tool and not call.get("error")]
        if not matched:
            problems.append(f"нет вызова {tool}")
            continue
        satisfying = [call for call in matched
                      if require <= set(call.get("args_keys") or [])
                      and not (forbid & set(call.get("args_keys") or []))]
        if satisfying:
            continue
        if [call for call in matched if not (require <= set(call.get("args_keys") or []))]:
            problems.append(f"{tool} без обязательных аргументов {sorted(require)}")
        else:
            problems.append(f"{tool} с запрещёнными аргументами {sorted(forbid)}")
    return (not problems), "; ".join(problems)


def named_actions(ids, action):
    """Is the action NAMED by a linked row? `B3M.01.read` names `read`; `G01` names nothing.

    A row named after an action is credited only when the row's LAST dot segment equals the action -
    a neighbouring row's call is another row's evidence.
    """
    return [rid for rid in ids if rid.rsplit(".", 1)[-1] == action]


def judge_row(row, index, calls):
    """The verdict of one matrix row's claims, action by action."""
    ids = test_ids(row.get("tests"))
    linked = [rid for rid in ids if rid in index]
    unknown = [rid for rid in ids if rid not in index]
    verdicts = [verdict for rid in linked for verdict in index[rid]["verdicts"]]
    windows = [window for rid in linked for window in index[rid]["windows"]]
    row_calls = calls_in(windows, calls)
    routes = declared_routes(row)
    result = {
        "operation_id": row.get("operation_id"),
        "tests_named": ids,
        "linked": linked,
        "unlinked": unknown,
        "linked_verdicts": dict(collections.Counter(verdicts)),
        "calls_in_window": len(row_calls),
        "claimed_actions": [a for a in ACTIONS if (row.get("actions") or {}).get(a) == "verified"],
        "without_linked_row": not linked,
        "contradicted_by_fail": "FAIL" in verdicts,
        "actions_without_dedicated_row": [],
        "actions_without_call_evidence": [],
    }
    for action in result["claimed_actions"]:
        named = named_actions(linked, action)
        # A declared route REPLACES the global signature, and is judged only in the row's own window.
        if action in routes:
            ok, reason = route_satisfied(routes[action], row_calls)
        else:
            ok, reason = signature_ok(action, row_calls)
        if not ok:
            result["actions_without_call_evidence"].append({"action": action, "reason": reason,
                                                            "declared_route": action in routes})
        if not named and not ok:
            result["actions_without_dedicated_row"].append(action)
    return result


def calibrate(report, pattern="B3M."):
    """Positive and negative controls of the call signature, on rows whose NAME carries the action.

    Positive: for each action, how many `<pattern><NN>.<action>` rows the signature derives. Negative:
    how many FOREIGN rows (named after another action) it fires on. Both halves are needed: a
    signature that derives everything on its own rows and also on every foreign row distinguishes
    nothing, and its refusal would be worthless.
    """
    index = report_index(report)
    calls = report.get("calls") or []
    by_action = collections.defaultdict(lambda: {"rows": 0, "derived": 0, "missed": []})
    foreign = collections.defaultdict(lambda: collections.Counter())
    for rid, entry in index.items():
        if not rid.startswith(pattern):
            continue
        action = rid.rsplit(".", 1)[-1]
        if action not in ACTIONS:
            continue
        row_calls = calls_in(entry["windows"], calls)
        ok, _ = signature_ok(action, row_calls)
        by_action[action]["rows"] += 1
        if ok:
            by_action[action]["derived"] += 1
        else:
            by_action[action]["missed"].append(rid)
        for other in ACTIONS:
            if other == action or ACTION_CALLS.get(other) is None:
                continue
            if signature_ok(other, row_calls)[0]:
                foreign[other][action] += 1
    return {"positive": {key: value for key, value in sorted(by_action.items())},
            "negative_foreign_derivations": {key: dict(value) for key, value in sorted(foreign.items())}}


def required_of_mismatches(matrix, profiles):
    """The matrix's `required_of` against the profiles that declare the dependency.

    The profile is the SOURCE of the dependency relation; the matrix keeps a copy. A dependency is
    matched by its `id` (its own `operation_id`), never by the value of `required_of` - that value is
    a LIST of consumers, and comparing it to a profile KEY matches nothing, so every dependency read
    as "named by no profile" while all of them were named. The two sides are compared as SETS: the
    ORDER of a consumer list is not a fact, and re-ordering it is not a discrepancy.

    Both directions are reported, because they are two different faults: a profile that declares
    consumers the matrix does not keep (a stale copy) and a matrix that claims consumers no profile
    declares (an unsupported claim).
    """
    declared = collections.OrderedDict()
    for name in sorted(profiles):
        for dep in profiles[name].get("common_dependencies") or []:
            declared.setdefault(dep.get("id"), []).append((name, set(dep.get("required_of") or [])))
    mismatches = []
    for row in matrix.get("rows") or []:
        dep_id = row.get("operation_id")
        matrix_side = set(row.get("required_of") or [])
        if dep_id in declared:
            for name, profile_side in declared[dep_id]:
                if matrix_side != profile_side:
                    mismatches.append({
                        "operation_id": dep_id, "profile": name,
                        "matrix_only": sorted(matrix_side - profile_side),
                        "profile_only": sorted(profile_side - matrix_side),
                        "reason": "состав потребителей зависимости расходится: профиль - источник"})
        elif matrix_side:
            mismatches.append({
                "operation_id": dep_id, "profile": None,
                "matrix_only": sorted(matrix_side), "profile_only": [],
                "reason": "зависимость не названа ни одним профилем выпуска"})
    return mismatches


def required_of_check(matrix, profiles_dir):
    profiles = {}
    for name in sorted(os.listdir(profiles_dir)):
        if name.endswith(".json"):
            profiles[name] = load_json(os.path.join(profiles_dir, name))
    return {"profiles_read": sorted(profiles),
            "mismatches": required_of_mismatches(matrix, profiles),
            "note": "пустой список - матрица и профили называют одних и тех же потребителей; "
                    "null - профиль не прочитан, то есть проверки НЕ БЫЛО, а не «всё сходится»"}


def guard_control(index, calls):
    """Both halves of the claim guard in ONE run.

    `must_refuse`: a row linked to a check whose window holds only a REFUSED call must be refused.
    `must_stay_silent`: a row linked to a check whose window holds a real call must stay silent.
    Zero refusals on the second half proves nothing without the first, and vice versa.
    """
    result = {"must_refuse": None, "must_stay_silent": None, "guard_proved": False,
              "note": "обе половины в ОДНОЙ постановке: синтетическая строка, чьё окно держит только "
                      "вызов с кодом отказа, обязана быть отвергнута; строка, чьё окно держит "
                      "настоящую пару save→open, обязана остаться без отказа"}
    # Синтетическая строка строится на НАСТОЯЩЕМ окне журнала: предпочтителен вызов, который сам
    # подходит под подпись, но завершился отказом, - именно он отличает «вызов был» от «действие
    # сделано». Если такого нет, берётся первый отказ.
    spec = ACTION_CALLS["create"]["any"]
    refused = [index_ for index_, call in enumerate(calls) if call.get("error")]
    pointed = [index_ for index_ in refused
               if any(matches(calls[index_].get("tool"), rule) for rule in spec)]
    chosen = (pointed or refused)
    if chosen:
        window = [chosen[0], chosen[0] + 1]
        ok, reason = signature_ok("create", calls_in([window], calls))
        result["must_refuse"] = {"row": f"СИНТЕТИЧЕСКАЯ (окно журнала {window})",
                                 "tool": calls[chosen[0]].get("tool"),
                                 "error": calls[chosen[0]].get("error"),
                                 "refused": not ok, "reason": reason}
    for rid, entry in sorted(index.items()):
        row_calls = calls_in(entry["windows"], calls)
        if row_calls and signature_ok("save_reopen", row_calls)[0]:
            result["must_stay_silent"] = {"row": rid, "silent": True,
                                          "reason": "подпись save_reopen выполнена настоящими вызовами"}
            break
    result["guard_proved"] = bool(result["must_refuse"] and result["must_refuse"]["refused"]
                                  and result["must_stay_silent"]
                                  and result["must_stay_silent"]["silent"])
    return result


def collect(report_path, matrix_path=MATRIX, profiles_dir=PROFILES_DIR):
    report = load_json(report_path)
    matrix = load_json(matrix_path)
    index = report_index(report)
    calls = report.get("calls") or []
    rows = []
    for row in matrix.get("rows") or []:
        if not any(value == "verified" for value in (row.get("actions") or {}).values()):
            continue
        rows.append(judge_row(row, index, calls))
    counters = {
        "rows_claiming_verified": len(rows),
        "rows_verified_without_any_linked_row": sorted(r["operation_id"] for r in rows
                                                       if r["without_linked_row"]),
        "rows_verified_contradicted_by_fail": sorted(r["operation_id"] for r in rows
                                                     if r["contradicted_by_fail"]),
        "rows_action_without_dedicated_row": sorted(r["operation_id"] for r in rows
                                                    if r["actions_without_dedicated_row"]),
        "rows_actions_without_required_calls": sorted(r["operation_id"] for r in rows
                                                      if r["actions_without_call_evidence"]),
    }
    counters["rows_status_lowered_by_unsupported_actions"] = len(
        counters["rows_actions_without_required_calls"])
    return {
        "instrument": "api-audit-claims",
        "date": stamp(),
        "source": os.path.relpath(report_path, os.path.dirname(os.path.dirname(
            os.path.abspath(__file__)))).replace("\\", "/"),
        "acceptance_rows": len(report.get("rows") or []),
        "acceptance_calls_journal": len(calls),
        "acceptance_rows_with_call_window": sum(1 for row in (report.get("rows") or [])
                                                if row.get("calls_window")),
        "acceptance_groups": sorted((report.get("groups") or {}).keys()),
        "rows": rows,
        "counters": counters,
        "calls_calibration": calibrate(report),
        "guard_control": guard_control(index, calls),
        "required_of_check": required_of_check(matrix, profiles_dir),
    }


def self_test():
    failures = []
    checks = 0

    def check(name, condition, detail=""):
        nonlocal checks
        checks += 1
        print(f"  [{'PASS' if condition else 'FAIL'}] {name}" + (f" - {detail}" if detail else ""))
        if not condition:
            failures.append(name)

    check("идентификатор строки отделяется от прозы",
          test_ids(["FL07 — что-то", "G01 (приёмка)", "V07"]) == ["FL07", "G01", "V07"],
          str(test_ids(["FL07 — что-то", "G01 (приёмка)", "V07"])))

    calls = [{"tool": "kompas_save_document", "error": None},
             {"tool": "kompas_open_document", "error": None}]
    check("save_reopen требует обе половины", signature_ok("save_reopen", calls)[0])
    check("save_reopen без открытия не подтверждается",
          not signature_ok("save_reopen", [{"tool": "kompas_save_document", "error": None}])[0])
    check("rebuild не судится по вызовам",
          signature_ok("rebuild", [])[0] and ACTION_CALLS["rebuild"] is None)
    check("negative_tests выводится из ИСХОДА, а не из имени инструмента",
          signature_ok("negative_tests", [{"tool": "kompas_extrude", "error": "GEOMETRY_FAILED"}])[0]
          and not signature_ok("negative_tests", [{"tool": "kompas_extrude", "error": None}])[0])
    check("ошибочный вызов не засчитывается как действие",
          not signature_ok("create", [{"tool": "kompas_extrude", "error": "GEOMETRY_FAILED"}])[0])
    check("чтение разреза засчитывается как read",
          signature_ok("read", [{"tool": "kompas_get_section_view", "error": None}])[0])
    check("ошибочное чтение разреза как read НЕ засчитывается",
          not signature_ok("read", [{"tool": "kompas_get_section_view",
                                     "error": "NOT_FOUND"}])[0])
    check("правка разреза засчитывается как edit",
          signature_ok("edit", [{"tool": "kompas_update_section_view", "error": None}])[0])
    check("удаление разреза засчитывается как delete_dependencies",
          signature_ok("delete_dependencies",
                       [{"tool": "kompas_delete_section_view", "error": None}])[0])
    check("выгрузка чертежа НЕ засчитывается как geometry_validation",
          not signature_ok("geometry_validation",
                           [{"tool": "kompas_export_drawing", "error": None}])[0])

    route = {"routes": [{"tool": "kompas_get_context", "require_args": ["application_id"]}]}
    ok, reason = route_satisfied(route, [{"tool": "kompas_get_context", "error": None,
                                          "args_keys": ["application_id"]}])
    check("объявленный маршрут засчитывается в своём окне", ok, reason)
    ok, reason = route_satisfied(route, [{"tool": "kompas_get_context", "error": None,
                                          "args_keys": []}])
    check("маршрут без обязательного аргумента НЕ засчитывается", not ok, reason)
    ok, reason = route_satisfied({"routes": [{"tool": "kompas_get_context",
                                              "forbid_args": ["force"]}]},
                                 [{"tool": "kompas_get_context", "error": None,
                                   "args_keys": ["force"]}])
    check("запрещённый аргумент отвергает маршрут", not ok, reason)

    # Обе половины маршрута судятся на ОДНОМ вызове. Окно держит два вызова того же инструмента:
    # голый (перечисление) и с адресом (адресное чтение). Маршрут перечисления обязан быть засчитан,
    # маршрут адресного чтения - тоже, а маршрут, требующий ОБА аргумента сразу, - отвергнут.
    both = [{"tool": "kompas_list_sketch_entities", "error": None, "args_keys": ["sketch_ref"]},
            {"tool": "kompas_list_sketch_entities", "error": None,
             "args_keys": ["sketch_ref", "address"]}]
    ok, reason = route_satisfied({"routes": [{"tool": "kompas_list_sketch_entities",
                                              "require_args": ["sketch_ref"],
                                              "forbid_args": ["address"]}]}, both)
    check("маршрут перечисления засчитан в окне с адресным вызовом того же инструмента", ok, reason)
    ok, reason = route_satisfied({"routes": [{"tool": "kompas_list_sketch_entities",
                                              "require_args": ["address"]}]}, both)
    check("маршрут адресного чтения засчитан в том же окне", ok, reason)
    ok, reason = route_satisfied({"routes": [{"tool": "kompas_list_sketch_entities",
                                              "require_args": ["address"],
                                              "forbid_args": ["address"]}]}, both)
    check("несовместимые половины маршрута отвергают его", not ok, reason)

    check("действие считается названным только по последнему сегменту имени",
          named_actions(["B3M.01.read", "G01"], "read") == ["B3M.01.read"]
          and named_actions(["G01", "V07"], "read") == [])

    # Обе половины сторожа в одной постановке: строка, чьё окно держит только ОТКАЗ, обязана быть
    # отвергнута; строка с настоящим вызовом обязана промолчать.
    report = {"rows": [{"id": "B3M.01.create", "verdict": "PASS", "calls_window": [0, 1]},
                       {"id": "B3M.09.save_reopen", "verdict": "PASS", "calls_window": [2, 4]}],
              "calls": [{"tool": "kompas_extrude", "error": "GEOMETRY_FAILED"},
                        {"tool": "kompas_get_feature", "error": None},
                        {"tool": "kompas_save_document", "error": None},
                        {"tool": "kompas_open_document", "error": None}]}
    control = guard_control(report_index(report), report["calls"])
    check("сторож отказывает строке с одним отказом в окне",
          control["must_refuse"] and control["must_refuse"]["refused"], str(control["must_refuse"]))
    check("сторож молчит на строке с настоящим вызовом",
          control["must_stay_silent"] and control["must_stay_silent"]["silent"],
          str(control["must_stay_silent"]))
    check("сторож доказан только обеими половинами", control["guard_proved"])

    # required_of: сверка по id зависимости и по СОСТАВУ потребителей. Обе половины в одной
    # постановке: строка, чей состав расходится с профилем, обязана быть названа; строка, где
    # различается только ПОРЯДОК, обязана промолчать. Без второй половины переупорядочивание
    # читалось бы как расхождение, без первой - прибор не различал бы «сходится» и «не смотрит».
    profile = {"p.json": {"common_dependencies": [
        {"id": "dep.a", "required_of": ["M-2", "M-1"]}]}}
    same = {"rows": [{"operation_id": "dep.a", "required_of": ["M-1", "M-2"]}]}
    other = {"rows": [{"operation_id": "dep.a", "required_of": ["M-1"]}]}
    check("required_of: расхождение состава названо",
          required_of_mismatches(other, profile) == [
              {"operation_id": "dep.a", "profile": "p.json", "matrix_only": [],
               "profile_only": ["M-2"],
               "reason": "состав потребителей зависимости расходится: профиль - источник"}],
          str(required_of_mismatches(other, profile)))
    check("required_of: различие только в порядке НЕ расхождение",
          required_of_mismatches(same, profile) == [],
          str(required_of_mismatches(same, profile)))
    check("required_of: заявка без профиля названа",
          len(required_of_mismatches({"rows": [{"operation_id": "dep.b",
                                                "required_of": ["M-1"]}]}, profile)) == 1)

    print(f"\nСамопроверка прибора: {checks} проверок, "
          f"{len(failures)} FAIL" + (f" - {failures}" if failures else ""))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Сверка заявленного в матрице с прогоном приёмки.")
    parser.add_argument("--report", default=None, help="отчёт приёмки --all (JSON)")
    parser.add_argument("--matrix", default=MATRIX, help="матрица покрытия")
    parser.add_argument("--out", default=None, help="куда записать измерение (JSON)")
    parser.add_argument("--self-test", action="store_true", help="проверить прибор, ничего не измеряя")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    if not args.report:
        raise SystemExit("нужен --report: отчёт приёмки --all, на котором судятся заявки матрицы")
    result = collect(args.report, matrix_path=args.matrix)
    out = args.out or default_out("claims")
    save_json(out, result)
    counters = result["counters"]
    print(f"источник: {result['source']} (строк {result['acceptance_rows']}, "
          f"вызовов {result['acceptance_calls_journal']})")
    print(f"строк с заявкой verified: {counters['rows_claiming_verified']}")
    print(f"без связанной строки приёмки: {len(counters['rows_verified_without_any_linked_row'])} "
          f"{counters['rows_verified_without_any_linked_row'][:5]}")
    print(f"противоречие FAIL: {len(counters['rows_verified_contradicted_by_fail'])} "
          f"{counters['rows_verified_contradicted_by_fail'][:5]}")
    print(f"действие без посвящённой строки: {len(counters['rows_action_without_dedicated_row'])} "
          f"{counters['rows_action_without_dedicated_row'][:5]}")
    print(f"действие без подтверждения вызовами: "
          f"{len(counters['rows_actions_without_required_calls'])} "
          f"{counters['rows_actions_without_required_calls'][:5]}")
    calibration = result["calls_calibration"]["positive"]
    derived = sum(item["derived"] for item in calibration.values())
    total = sum(item["rows"] for item in calibration.values())
    print(f"калибровка подписи (строки B3M): выведено {derived} из {total}")
    print(f"сторож заявки: {result['guard_control']['guard_proved']}")
    print(f"записано: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
