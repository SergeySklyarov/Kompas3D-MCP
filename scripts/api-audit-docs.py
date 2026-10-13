"""Audit step 2: is every COM member the adapter calls DOCUMENTED in the v24 SDK help?

  python scripts/api-audit-docs.py [--routes <routes.json>] [--out <json>] [--self-test]

"Documented" means one thing here: a page of the OFFICIAL v24 SDK help, cited by URL. A member found
in the TLB, in the interop assembly or by reflection is NOT evidence of documentation; it is recorded
as "member found in the interface" and does not make the route official.

A CONFIRMATION NEEDS THE SAME INTERFACE AND THE SAME MEMBER. HTTP 200, a matching `<title>`, or
membership in the same family confer nothing. The member lists are taken from the interface's OWN
pages (`<iface>_methods.html`, `<iface>_props.html`, `<iface>_propers.html`) rather than guessed, and
the member page is matched by its `<title>` - never by the first `<h1>`, which on every page of this
site is the header «Справочная система SDK КОМПАС-3D».

Three outcomes, never two: `confirmed` (200, same interface, same member), `broken` (404 - an
instrument defect if it is cited as evidence), `not_checked` (network failure - NOT counted as good).

`--self-test` fetches one page that exists and one that does not, in the same run: a checker that
only proves the positive half cannot tell "verified" from "never looked".
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from api_audit_common import ROOT, default_out, load_json, save_json, stamp  # noqa: E402

HELP_ROOT = "https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/"
CACHE = os.path.join(ROOT, "scratch", "api-audit", "help-cache")
# ЗЕРКАЛО СПРАВКИ — ВТОРАЯ ОСНОВА, И ОНА НАЗЫВАЕТСЯ. `reference/kompas-sdk-v24/` лежит в
# репозитории (не публикуется) и покрывает ЧАСТЬ справки: 2977 файлов против полного перечня в
# ~25 800. Когда провод отвечает отказом, страница ищется в зеркале, и у вердикта появляется
# `basis`: `wire` (страница открыта по проводу, HTTP 200) или `mirror` (страница прочитана из
# зеркала). Смешивать их нельзя: подтверждение по зеркалу — это подтверждение по копии, и оно
# названо, а не выдано за провод.
MIRROR_DIR = os.path.join(ROOT, "reference", "kompas-sdk-v24", "sdk-docs")
TITLE = re.compile(r"<title[^>]*>(.*?)</title>", re.S | re.I)
LINK = re.compile(r"""<a\s[^>]*href\s*=\s*["']([^"'#]+)["'][^>]*>(.*?)</a>""", re.S | re.I)

# Customer exception of 11.10.2026: `ksCBisector` is supported on a MEASURED role assignment (the
# object is the bisector itself, the partner and the direction are named separately), and that
# measurement - not a help page - is its basis. It is named here as an EXCEPTION with a reference to
# the record, never as "documented".
EXCEPTIONS = {
    "ksCBisector": "исключение заказчика 11.10.2026: измеренная постановка ролей (AGENTS.md, "
                   "раздел о маршрутах); основание - измерение, а не страница справки",
}


# A BROWSER-LIKE USER AGENT, AND RETRIES. MEASURED: this help server answers `445` (a non-standard
# status) to a script-like agent and to bursts, and answers the same URL `200` a moment later. A
# single GET whose non-200 answer is written down turns the server's throttling into "the member is
# undocumented" - 66 such findings were manufactured before this was caught, and the audit's whole
# documentation column would have been a measurement of the network instead of the help.
USER_AGENT = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
              "Chrome/124.0 Safari/537.36")
RETRY_DELAYS = (3.0, 8.0)
# One request per second. MEASURED: a burst of ~2000 requests made the server answer `445` to EVERY
# page for several minutes, including pages it had served a moment before; at this rate it serves.
POLITE_DELAY_S = 1.0
# CIRCUIT BREAKER. When the server refuses page after page, retrying each one four times turns the run
# into an all-night wait whose result is already known. After this many consecutive refusals the
# instrument stops retrying and records the refusal ONCE per page: a `not_checked` column with a named
# reason is honest, a column that takes 20 hours to say the same thing is not more honest.
CIRCUIT_BREAKER_AFTER = 5
_STATE = {"refusals": 0, "open": False}


def fetch(url, cache=CACHE, timeout=30):
    """(status, body). Status is an int for a definitive answer, or a string naming the failure.

    ONLY a 200 is cached: a cached non-200 freezes a transient refusal as a fact about the help.
    A non-200 is retried with backoff; if it persists, a `404` is reported as `404` (the page is not
    there) and any OTHER code as `http:<code>` - which the caller keeps in `not_checked`, never in
    "undocumented".
    """
    os.makedirs(cache, exist_ok=True)
    key = re.sub(r"[^A-Za-z0-9_.-]", "_", url)
    path = os.path.join(cache, key)
    if os.path.isfile(path):
        with open(path, encoding="utf-8", errors="replace") as handle:
            return 200, handle.read()
    last = None
    attempts = 1 if _STATE["open"] else len(RETRY_DELAYS) + 1
    for attempt in range(attempts):
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT,
                                                       "Accept-Language": "ru,en;q=0.8"})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                body = response.read().decode("utf-8", "replace")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(body)
            _STATE["refusals"] = 0
            time.sleep(POLITE_DELAY_S)
            return 200, body
        except urllib.error.HTTPError as error:
            last = error.code
        except Exception as error:  # noqa: BLE001 - the class is reported, not swallowed
            last = f"network:{type(error).__name__}"
        if attempt < attempts - 1:
            time.sleep(RETRY_DELAYS[attempt])
    if isinstance(last, int):
        _STATE["refusals"] += 1
        if _STATE["refusals"] >= CIRCUIT_BREAKER_AFTER:
            _STATE["open"] = True
    if last == 404:
        return 404, ""
    if isinstance(last, int) or str(last).startswith("network:"):
        # Провод не отдал страницу. Зеркало — НАЗВАННАЯ вторая основа, а не подмена: вердикт по ней
        # помечается `basis = mirror`, и читатель видит, чем именно подтверждён каждый член.
        mirrored = mirror_page(url)
        if mirrored is not None:
            return "mirror", mirrored
    return (last if isinstance(last, str) else f"http:{last}"), ""


def mirror_page(url):
    """Тело страницы из зеркала справки, или None. Имя файла — последний сегмент URL."""
    if not os.path.isdir(MIRROR_DIR):
        return None
    name = url.rstrip("/").rsplit("/", 1)[-1]
    path = os.path.join(MIRROR_DIR, name)
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8", errors="replace") as handle:
        return handle.read()


def page_title(body):
    match = TITLE.search(body or "")
    return re.sub(r"\s+", " ", match.group(1)).strip() if match else ""


def member_list(interface):
    """`{member name lowercased: url}` from the interface's OWN member-list pages.

    The pages are named `_methods`, `_props` and `_propers` on this site; all three are read because
    a member may be listed on any of them, and guessing one of them loses the others.
    """
    base = interface.lower()
    listing = {}
    statuses = {}
    for suffix in ("_methods", "_props", "_propers"):
        url = f"{HELP_ROOT}{base}{suffix}.html"
        status, body = fetch(url)
        statuses[suffix] = status
        if status not in (200, "mirror"):
            continue
        for href, text in LINK.findall(body):
            href = href.strip()
            if not href.lower().startswith(base + "_") or not href.lower().endswith(".html"):
                continue
            # The member NAME is taken from the page's own slug (`kspart_newentity.html` ->
            # `newentity`), not from the link text: the text carries the description as well
            # ("NewEntity - Создать новый интерфейс ..."), and keying on it made every member read
            # as absent from its own interface listing.
            slug = href.lower()[len(base) + 1:-len(".html")]
            listing.setdefault(slug, HELP_ROOT + href.lstrip("./"))
            plain = re.sub(r"<[^>]+>", "", text).strip().lower()
            plain = re.split(r"\s+[-–—]\s+", plain, maxsplit=1)[0].strip()
            if plain:
                listing.setdefault(plain, HELP_ROOT + href.lstrip("./"))
    return listing, statuses


def candidate_names(member):
    """Names a help page may use for a member.

    The interop exposes a property through its accessors (`get_PartsEx`), while the help page is
    named after the PROPERTY. Both spellings are tried, because a page missed for a naming reason
    would read as an undocumented member.
    """
    names = [member.lower()]
    for prefix in ("get_", "set_"):
        if member.lower().startswith(prefix):
            names.append(member.lower()[len(prefix):])
    return names


def check_interface(interface, routes, listing_cache):
    """Every route on one interface: confirmed, broken or not checked."""
    if interface not in listing_cache:
        listing_cache[interface] = member_list(interface)
    listing, statuses = listing_cache[interface]
    results = []
    if not listing and all(status != 200 for status in statuses.values()):
        # Ни одна из страниц перечня интерфейса не открылась: спрашивать его членов нечем, и
        # повторять запрос на каждого члена значило бы умножить один отказ сервера на число членов.
        reason = "; ".join(f"{suffix}={status}" for suffix, status in sorted(statuses.items()))
        return [{"route": route["route"], "member": route["member"],
                 "page": f"{HELP_ROOT}{interface.lower()}_methods.html",
                 "status": "не проверено", "reason": f"перечень интерфейса не прочитан: {reason}",
                 "source": [f"{HELP_ROOT}{interface.lower()}_methods.html"]}
                for route in routes]
    for route in routes:
        member = route["member"]
        page = None
        for name in candidate_names(member):
            if name in listing:
                page = listing[name]
                break
        # THE INTERFACE IS ESTABLISHED BY THE LISTING PAGE, not by the title: the titles of this help
        # carry only the member ("Radius - Радиус дуги"), so demanding the interface name in the
        # title marked 440 of 517 confirmed members as contradictions - an instrument defect that
        # read as a mass API discrepancy.
        listed = page is not None
        if page is None:
            page = f"{HELP_ROOT}{interface.lower()}_{member.lower()}.html"
        status, body = fetch(page)
        title = page_title(body)
        same_member = any(name in title.lower() for name in candidate_names(member))
        basis = {200: "wire", "mirror": "mirror"}.get(status)
        if basis and listed and same_member:
            results.append({"route": route["route"], "member": member, "page": page,
                            "status": "Подтверждено", "title": title, "basis": basis,
                            "owner_established_by": f"{HELP_ROOT}{interface.lower()}_methods.html",
                            "source": [page]})
        elif basis and not listed:
            results.append({"route": route["route"], "member": member, "page": page,
                            "status": "не подтверждено",
                            "reason": "страница не найдена в перечне членов своего интерфейса",
                            "listing_statuses": statuses, "title": title,
                            "source": [f"{HELP_ROOT}{interface.lower()}_methods.html"]})
        elif status == 200:
            results.append({"route": route["route"], "member": member, "page": page,
                            "status": "Противоречие",
                            "reason": f"заголовок страницы не называет тот же член: {title}",
                            "source": [page]})
        elif isinstance(status, int):
            results.append({"route": route["route"], "member": member, "page": page,
                            "status": "не подтверждено", "reason": f"HTTP {status}",
                            "source": [page]})
        else:
            results.append({"route": route["route"], "member": member, "page": page,
                            "status": "не проверено", "reason": status, "source": [page]})
    return results


TABLE_CELL = re.compile(r'<span class="f_Table_Cell">([^<]+)</span>')
OBJECT_TYPE_PAGE = HELP_ROOT + "obj3dtype.html"


def documented_object_types():
    """`{o3d_name lowercased: value}` out of the documented table `obj3dtype.html`.

    A numeric id is checked against the table by the constant's OWN documented name: two numbering
    systems live side by side in API5 (a creation/factory number and a tree number), and resolving a
    constant by a number alone confuses them.
    """
    status, body = fetch(OBJECT_TYPE_PAGE)
    if status != 200:
        return None, status
    table = {}
    cells = [cell.strip() for cell in TABLE_CELL.findall(body)]
    for index, cell in enumerate(cells):
        if cell.lower().startswith("o3d_") and index + 1 < len(cells):
            value = cells[index + 1].strip()
            if value.isdigit():
                table.setdefault(cell.lower(), int(value))
    return table, 200


def check_object_types(constants):
    table, status = documented_object_types()
    if table is None:
        return {"documented_table": OBJECT_TYPE_PAGE, "status": status,
                "matched": [], "mismatched": [], "unnamed": [],
                "note": "таблица не прочитана - проверки НЕ БЫЛО, а не «всё сходится»"}
    matched, mismatched, unnamed = [], [], []
    for item in constants:
        name = (item.get("documented") or "").lower()
        if not name:
            unnamed.append(item)
            continue
        if name in table:
            record = {"constant": item["constant"], "value": item["value"],
                      "documented": item["documented"], "site": item["site"],
                      "documented_value": table[name]}
            (matched if table[name] == item["value"] else mismatched).append(record)
        else:
            mismatched.append({**item, "reason": "имя не названо таблицей obj3dtype.html"})
    return {"documented_table": OBJECT_TYPE_PAGE, "status": 200, "table_entries": len(table),
            "matched": matched, "mismatched": mismatched,
            "unnamed": [{"constant": item["constant"], "value": item["value"], "site": item["site"]}
                        for item in unnamed],
            "note": "константа без названного в её собственном doc-блоке имени o3d_* проверена быть не "
                    "может: имя не выводится из числа"}


def collect(routes_path):
    payload = load_json(routes_path)
    routes = payload["routes"]
    by_interface = collections.OrderedDict()
    for route in routes:
        by_interface.setdefault(route["declared_on"], []).append(route)
    listing_cache = {}
    checked = []
    for interface in sorted(by_interface):
        checked.extend(check_interface(interface, by_interface[interface], listing_cache))
    counts = collections.Counter(item["status"] for item in checked)
    bases = collections.Counter(item.get("basis", "нет") for item in checked)
    exceptions = [{"route": name, "note": note} for name, note in EXCEPTIONS.items()
                  if any(route["route"].split(".")[0] == name for route in routes)]
    return {
        "instrument": "api-audit-docs",
        "date": stamp(),
        "source": os.path.relpath(routes_path, ROOT).replace("\\", "/"),
        "help_root": HELP_ROOT,
        "interfaces_checked": len(by_interface),
        "routes_checked": len(checked),
        "statuses": dict(counts),
        "bases": dict(bases),
        "confirmed": sorted(item["route"] for item in checked
                            if item["status"] == "Подтверждено"),
        "unconfirmed": [item for item in checked if item["status"] != "Подтверждено"],
        "exceptions": exceptions,
        "object_types": check_object_types(payload.get("object_type_constants") or []),
        "circuit_breaker": {
            "open": _STATE["open"], "after_consecutive_refusals": CIRCUIT_BREAKER_AFTER,
            "note": "сервер справки отвечал отказом на страницу за страницей; повтор запроса на каждом "
                    "члене умножал бы один отказ на число членов. Открытый предохранитель означает, что "
                    "столбец документации НЕПОЛОН, и это названо, а не выдано за чистый результат",
        },
        "routes": checked,
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

    status_ok, body = fetch(f"{HELP_ROOT}kspart_newentity.html")
    # Основа НАЗЫВАЕТСЯ: `200` - страница открыта по проводу, `mirror` - прочитана из зеркала.
    # Проверка «получена» без этого различала бы провод и зеркало только на глаз.
    check("страница справки получена, и основа названа", status_ok in (200, "mirror") and body,
          f"основа={status_ok}, {len(body)} байт")
    check("заголовок страницы читается", "NewEntity" in page_title(body), page_title(body))
    missing = f"{HELP_ROOT}zzz-audit-selftest-404.html"
    status_bad, _ = fetch(missing)
    check("отрицательный контроль: несуществующая страница НЕ открывается", status_bad != 200,
          f"ответ {status_bad}")
    check("404 отличается от сетевого отказа и от ограничения сервера",
          status_bad == 404 or (isinstance(status_bad, str) and status_bad.startswith("http:")),
          f"ответ {status_bad}")
    # Неудача НЕ кэшируется: закэшированный не-200 замораживает временный отказ сервера как факт о
    # справке, и повторный прогон его не перепроверит.
    key = re.sub(r"[^A-Za-z0-9_.-]", "_", missing)
    check("неудачный ответ не попал в кэш", not os.path.isfile(os.path.join(CACHE, key)),
          os.path.join(CACHE, key))

    listing, statuses = member_list("ksPart")
    check("перечень членов читается со страниц САМОГО интерфейса", bool(listing),
          f"членов {len(listing)}, статусы {statuses}")
    check("названный член присутствует в перечне", "newentity" in listing,
          str([name for name in listing if name.startswith("new")][:5]))

    check("аксессор и свойство считаются одним именем",
          candidate_names("get_PartsEx") == ["get_partsex", "partsex"], str(candidate_names("get_PartsEx")))

    print(f"\nСамопроверка прибора: {checks} проверок, "
          f"{len(failures)} FAIL" + (f" - {failures}" if failures else ""))
    return 1 if failures else 0


def main():
    parser = argparse.ArgumentParser(description="Сверка COM-маршрутов со справкой v24 по проводу.")
    parser.add_argument("--routes", default=None, help="измерение api-audit-routes (JSON)")
    parser.add_argument("--out", default=None, help="куда записать измерение (JSON)")
    parser.add_argument("--self-test", action="store_true", help="проверить прибор, ничего не измеряя")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    routes_path = args.routes or default_out("routes")
    if not os.path.isfile(routes_path):
        raise SystemExit(f"нет инвентаря маршрутов: {routes_path} (сначала api-audit-routes.py)")
    started = time.time()
    result = collect(routes_path)
    result["seconds"] = round(time.time() - started, 1)
    out = args.out or default_out("docs")
    save_json(out, result)
    print(f"интерфейсов проверено: {result['interfaces_checked']}, маршрутов: {result['routes_checked']}")
    print(f"статусы: {result['statuses']}")
    print(f"не подтверждено: {len(result['unconfirmed'])}")
    for item in result["unconfirmed"][:10]:
        print(f"  {item['route']} - {item.get('reason')}")
    print(f"исключения: {[item['route'] for item in result['exceptions']]}")
    print(f"записано: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
