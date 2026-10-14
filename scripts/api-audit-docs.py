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

THE SAME MEMBER MAY BE DOCUMENTED ON AN ANCESTOR. The help prints the inheritance chain in the
«Иерархия» line of the interface's own page (`ichamfer.html` -> «IDispatch IKompasAPIObject
IModelObject IChamfer»), and an inherited member is documented THERE (`imodelobject_update.html`),
not on the interface's own page. The chain is read from that line and NEVER guessed: an interface
without the line is judged by its own pages alone. `documented_on` names the interface whose page
carries the confirmation.

ONE PAGE MAY DOCUMENT A GROUP OF MEMBERS. The help groups related members and names them together in
the listing (`ksmassinertiaparam_props.html`: «jx, jy, jz - Осевые моменты инерции»); every name of
such a group is documented by that one page.

THE LISTING NAMES THE MEMBER, NOT ALWAYS THE PAGE. A member page is usually named
`<interface>_<member>.html`, but not always: `idrawingobject1_props.html` links `ConstraintsState` to
`constraintsstate.html`. The interface's OWN listing naming the member is the evidence, so the name
is taken from the listing TEXT as well as from the file name.

A TITLE MAY MISSPELL THE MEMBER. Where the page's ADDRESS carries the member
(`ksfeature_isvalid.html`) while its title misspells it («IsVIalid»), the page is still the member's
own page: it is confirmed with the misspelling NAMED (`doc_typo`), never reported as "the member has
no page".

Three outcomes, never two: `confirmed` (200, same interface, same member), `broken` (404 - an
instrument defect if it is cited as evidence), `not_checked` (network failure - NOT counted as good).

`--self-test` fetches one page that exists and one that does not, in the same run: a checker that
only proves the positive half cannot tell "verified" from "never looked".
"""

from __future__ import annotations

import argparse
import collections
import datetime
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
# THE HIERARCHY LINE of an interface page: one <p class="p_Hier_N_..."> per name, the deepest being
# the interface itself. The meta description repeats the same names as plain text and is read only
# when the body paragraphs are absent.
HIER_P = re.compile(r'<p class="p_Hier_[^"]*">(.*?)</p>', re.S)
HIER_META = re.compile(r"Иерархия:\s*([A-Za-z_][A-Za-z0-9_]*(?:\s+[A-Za-z_][A-Za-z0-9_]*)*)")
# The description separator of a listing entry: «Name - Описание». A link WITHOUT it is not a member
# entry (the interface links to other interfaces as «Интерфейс IX», with no separator).
ENTRY = re.compile(r"\s+[-–—]\s+")

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
# Network failures (a dropped connection, a timeout): the server is there, the request did not arrive.
RETRY_DELAYS = (3.0, 8.0)
# THE PAUSE BETWEEN REQUESTS - MEASURED, not chosen. Measured 13.10.2026 against
# `https://help.ascon.ru/KOMPAS_SDK/24/ru-RU/`: a burst of 1500 requests with NO pause (2.3
# requests/s) was served - 1467 answered 200, 33 dropped the connection and the next request
# answered 200 again - and 20 distinct pages at 0.5 s spacing answered 20/20. 1.0 s therefore sits
# below a rate the server demonstrably tolerates, and is the value the column is measured at.
POLITE_DELAY_S = 1.0
# REFUSALS THAT MEAN "SLOW DOWN", and the backoff for them. `429` is the standard status and `445` is
# this server's own; both are transient, so neither may be written down as "the member is
# undocumented". MEASURED 13.10.2026: the `445` could NOT be reproduced - 1500 rapid requests drew
# only dropped connections, which recovered on the very next request - while the earlier ~2000-request
# burst (recorded in `docs/decisions/api-audit.md`) made the server answer `445` to every page "for
# several minutes". The first wait is therefore the shortest value that record admits (one minute),
# and each further wait doubles it; a refusal that survives the first wait is not the same event as
# one that survives the third.
REFUSAL_CODES = (429, 445)
REFUSAL_BACKOFF_S = (60.0, 120.0, 240.0)
# The breaker no longer stops the run: it counts how many routes could not be measured at all, so
# `documentation_column_complete` is derived from the ROUTES rather than from the server's mood.
CIRCUIT_BREAKER_AFTER = 5
_STATE = {"refusals": 0, "open": False, "max_consecutive_refusals": 0, "refusal_backoffs": 0}


def fetch(url, cache=CACHE, timeout=30):
    """(status, body). Status is an int for a definitive answer, or a string naming the failure.

    ONLY a 200 is cached: a cached non-200 freezes a transient refusal as a fact about the help.
    A refusal (`429`/`445`) is waited out with an escalating backoff and retried; a network failure is
    retried with the shorter delays. If it persists, a `404` is reported as `404` (the page is not
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
    attempts = 1 + max(len(RETRY_DELAYS), len(REFUSAL_BACKOFF_S))
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
            if error.code in REFUSAL_CODES and attempt < len(REFUSAL_BACKOFF_S):
                _STATE["refusal_backoffs"] += 1
                time.sleep(REFUSAL_BACKOFF_S[attempt])
                continue
            if error.code == 404:
                # A `404` is a DEFINITIVE answer: the page is not there. It is retried ONCE, briefly,
                # so that a transient 404 cannot be frozen as "undocumented", and then accepted.
                # MEASURED: without this, the long network delays were applied to every absent page,
                # and a pass over the 517 routes spent over an hour on pages that answer 404 at once.
                if attempt == 0:
                    time.sleep(RETRY_DELAYS[0])
                    continue
                break
        except Exception as error:  # noqa: BLE001 - the class is reported, not swallowed
            last = f"network:{type(error).__name__}"
        if attempt < attempts - 1:
            time.sleep(RETRY_DELAYS[min(attempt, len(RETRY_DELAYS) - 1)])
    if isinstance(last, int) and last != 404:
        # A `404` is not a refusal of the SERVER - the page simply is not there - so it is not counted
        # with the throttling answers. Counting it would make `max_consecutive_refusals` name the
        # help's gaps instead of the server's mood.
        _STATE["refusals"] += 1
        _STATE["max_consecutive_refusals"] = max(_STATE["max_consecutive_refusals"],
                                                 _STATE["refusals"])
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


def mirror_info():
    """Зеркало как ВТОРАЯ ОСНОВА: каталог, дата снятия и число страниц.

    Зеркало покрывает ЧАСТЬ справки, поэтому «страницы нет» по зеркалу — утверждение о копии, а не о
    справке. Число страниц и дата снятия называются здесь, чтобы читатель видел границу копии.
    """
    if not os.path.isdir(MIRROR_DIR):
        return {"directory": os.path.relpath(MIRROR_DIR, ROOT).replace("\\", "/"), "present": False,
                "pages": 0, "captured": None,
                "note": "зеркала в репозитории нет: подтверждение возможно только по проводу"}
    pages = 0
    newest = 0.0
    for root, _dirs, files in os.walk(MIRROR_DIR):
        for name in files:
            if name.lower().endswith(".html"):
                pages += 1
                try:
                    newest = max(newest, os.path.getmtime(os.path.join(root, name)))
                except OSError:
                    pass
    captured = (datetime.datetime.fromtimestamp(newest).astimezone().date().isoformat()
                if newest else None)
    return {"directory": os.path.relpath(MIRROR_DIR, ROOT).replace("\\", "/"), "present": True,
            "pages": pages, "captured": captured,
            "note": "вторая основа, названная заказчиком 13.10.2026: страница читается из копии, когда "
                    "провод отказал, и вердикт помечается `basis = mirror`. Копия НЕПОЛНА, поэтому "
                    "«страницы нет» по ней не равно «страницы нет в справке»"}


def page_title(body):
    match = TITLE.search(body or "")
    return re.sub(r"\s+", " ", match.group(1)).strip() if match else ""


def hierarchy_entries(body):
    """Interface names of the page's own «Иерархия» line, in page order.

    The line is rendered as one `<p class="p_Hier_N_...">` per name; the meta description repeats the
    same names as plain text and is read only when the body paragraphs are absent. An empty list
    means the page carries no hierarchy line - the chain is then NOT invented.
    """
    names = [re.sub(r"<[^>]+>", "", chunk).strip() for chunk in HIER_P.findall(body or "")]
    names = [name for name in names if name]
    if names:
        return names
    match = HIER_META.search(body or "")
    return match.group(1).split() if match else []


def interface_ancestors(interface, cache):
    """(ancestors, basis, url) from the «Иерархия» line of the interface's OWN page.

    Ancestors are the names listed BEFORE the interface itself; the names AFTER it belong to other
    interfaces that share the topic page (`iapplication.html` names `IApplication` and then
    `IAttrTypeMng`, `IPropertyMng`). `IDispatch` is dropped - COM plumbing with no page of its own.
    When the interface is not named in the line, or its page cannot be read, the list is empty.
    """
    if interface in cache:
        return cache[interface]
    url = f"{HELP_ROOT}{interface.lower()}.html"
    status, body = fetch(url)
    result = ([], None, url)
    if status in (200, "mirror"):
        names = hierarchy_entries(body)
        target = interface.lower()
        index = next((i for i, name in enumerate(names) if name.lower() == target), None)
        ancestors = ([name for name in names[:index] if name.lower() != "idispatch"]
                     if index is not None else [])
        result = (ancestors, "wire" if status == 200 else "mirror", url)
    cache[interface] = result
    return result


def address_names_interface(page, interface):
    """Whether the page's OWN file name carries the interface: `<interface>_<member>.html`.

    A SECOND, independent way to establish the SAME interface, beside the interface's listing page.
    The prefix must be the interface PLUS `_`, so `ipart7_x.html` does not confirm a member of `IPart`
    and a page of another interface never confirms this one.
    """
    return os.path.basename(page or "").lower().startswith(interface.lower() + "_")


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


def names_member(title, member):
    """Whether a page title names the member (a title carries the member, e.g. «Radius - Радиус»)."""
    low = (title or "").lower()
    return any(name in low for name in candidate_names(member))


def edit_distance(left, right):
    """Levenshtein distance - used ONLY to recognise a misspelling, never to guess a member."""
    if left == right:
        return 0
    previous = list(range(len(right) + 1))
    for i, left_char in enumerate(left, 1):
        current = [i]
        for j, right_char in enumerate(right, 1):
            current.append(min(previous[j] + 1, current[j - 1] + 1,
                               previous[j - 1] + (left_char != right_char)))
        previous = current
    return previous[-1]


def near_miss_token(title, member):
    """The title token that is a ONE-MISSPELLING variant of the member, or None.

    The help has titled pages after a misspelling (`IsVIalid` for `IsValid`, `jxj0` for `jx0`,
    `jxjy` for `jxy`). The page is still the member's own page - its ADDRESS carries the member - so
    it is confirmed with the misspelling NAMED. Only a token within edit distance 2 counts: a title
    about a genuinely different member is a contradiction, not a misspelling.
    """
    member = member.lower()
    head = re.split(r"\s+[-–—]\s+", title or "", maxsplit=1)[0]
    for token in re.split(r"[,\s]+", head.strip()):
        token = token.strip().lower()
        if not token or token == member:
            continue
        if abs(len(token) - len(member)) <= 2 and edit_distance(token, member) <= 2:
            return token
    return None


def member_list(interface):
    """(listing, statuses, group_names) from the interface's OWN member-list pages.

    `listing` maps a member name to the page that documents it. The name comes from the page's slug
    (`kspart_newentity.html` -> `newentity`) AND from the link TEXT, because the help does not always
    name a member page after the interface: `idrawingobject1_props.html` links `ConstraintsState` to
    `constraintsstate.html`. The text before the description separator is a comma-separated GROUP
    («jx, jy, jz - ...»), so every name of it is registered; `group_names` records those names.
    """
    base = interface.lower()
    listing = {}
    statuses = {}
    group_names = set()
    for suffix in ("_methods", "_props", "_propers"):
        url = f"{HELP_ROOT}{base}{suffix}.html"
        status, body = fetch(url)
        statuses[suffix] = status
        if status not in (200, "mirror"):
            continue
        for href, text in LINK.findall(body):
            href = href.strip()
            if not href.lower().endswith(".html") or "://" in href:
                continue
            page = HELP_ROOT + href.lstrip("./")
            plain = re.sub(r"<[^>]+>", "", text).strip().lower()
            head = ENTRY.split(plain, maxsplit=1)[0].strip()
            names = [name.strip() for name in head.split(",") if name.strip()]
            # A page named after the interface carries its member in the file name; any page is
            # registered through the listing TEXT, which is the interface naming its own member.
            if href.lower().startswith(base + "_"):
                listing.setdefault(href.lower()[len(base) + 1:-len(".html")], page)
            if names and not any(" " in name for name in names):
                for name in names:
                    listing.setdefault(name, page)
                if len(names) > 1:
                    group_names.update(names)
    return listing, statuses, group_names


def confirm_member_on(interface, member, listing_cache):
    """How `member` is documented ON `interface`, or None.

    Two independent ways: the interface's own listing names the member, or the page's ADDRESS is
    `<interface>_<member>.html`. A page whose title misspells the member is confirmed with the
    misspelling NAMED - its address still carries the member.
    """
    if interface not in listing_cache:
        listing_cache[interface] = member_list(interface)
    listing, _statuses, group_names = listing_cache[interface]
    # THE LISTING IS SEARCHED UNDER EVERY SPELLING the interop may use (`get_PartsEx` -> `PartsEx`),
    # not only under the name the route carries: the accessor form is how the interop exposes a
    # property, and the help names the page after the property.
    listed = next((listing[name] for name in candidate_names(member) if name in listing), None)
    candidates = [(listed, "перечень интерфейса")] if listed else []
    address = f"{HELP_ROOT}{interface.lower()}_{member.lower()}.html"
    if all(page != address for page, _how in candidates):
        candidates.append((address, "имя файла страницы"))
    for page, how in candidates:
        status, body = fetch(page)
        if status not in (200, "mirror"):
            continue
        title = page_title(body)
        basis = "wire" if status == 200 else "mirror"
        if names_member(title, member):
            if how == "перечень интерфейса" and any(name in group_names
                                                   for name in candidate_names(member)):
                how = "групповая страница"
            return {"page": page, "basis": basis, "how": how, "title": title, "doc_typo": None}
        if page == address:
            typo = near_miss_token(title, member)
            if typo:
                return {"page": page, "basis": basis, "how": how, "title": title, "doc_typo": typo}
    return None


def check_route(route, listing_cache, hierarchy_cache):
    """One route: confirmed on its interface or on an ancestor, else named unconfirmed."""
    interface = route["declared_on"]
    member = route["member"]
    ancestors, _basis, _url = interface_ancestors(interface, hierarchy_cache)
    chain = [interface] + ancestors
    for position, owner in enumerate(chain):
        hit = confirm_member_on(owner, member, listing_cache)
        if hit:
            return {"route": route["route"], "member": member, "status": "Подтверждено",
                    "page": hit["page"], "title": hit["title"], "basis": hit["basis"],
                    "documented_on": owner,
                    "relation": "сам интерфейс" if position == 0 else "предок",
                    "how": hit["how"], "doc_typo": hit["doc_typo"],
                    "ancestors_checked": ancestors, "source": [hit["page"]]}

    listing, statuses, _groups = listing_cache.get(interface) or member_list(interface)
    listing_cache[interface] = (listing, statuses, _groups)
    if not listing and all(status != 200 for status in statuses.values()):
        # Ни одна из страниц перечня интерфейса не открылась: спрашивать его членов нечем, и
        # повторять запрос на каждого члена значило бы умножить один отказ сервера на число членов.
        reason = "; ".join(f"{suffix}={status}" for suffix, status in sorted(statuses.items()))
        return {"route": route["route"], "member": member,
                "page": f"{HELP_ROOT}{interface.lower()}_methods.html", "status": "не проверено",
                "reason": f"перечень интерфейса не прочитан: {reason}",
                "ancestors_checked": ancestors,
                "source": [f"{HELP_ROOT}{interface.lower()}_methods.html"]}
    page = f"{HELP_ROOT}{interface.lower()}_{member.lower()}.html"
    status, body = fetch(page)
    title = page_title(body)
    if status in (200, "mirror"):
        return {"route": route["route"], "member": member, "page": page, "status": "Противоречие",
                "reason": f"заголовок страницы не называет тот же член: {title}",
                "title": title, "ancestors_checked": ancestors, "source": [page]}
    if isinstance(status, int):
        return {"route": route["route"], "member": member, "page": page, "status": "не подтверждено",
                "reason": f"HTTP {status}", "ancestors_checked": ancestors, "source": [page]}
    return {"route": route["route"], "member": member, "page": page, "status": "не проверено",
            "reason": status, "ancestors_checked": ancestors, "source": [page]}


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
    # СОБСТВЕННЫЕ ТИПЫ АДАПТЕРА — не интерфейсы КОМПАС, и страницы справки у них нет ПО ПОСТРОЕНИЮ.
    # `*Twin` объявлены самим адаптером (`assembly: adapter-twins`); требовать для них страницу
    # означало бы требовать документ вендора на чужую конструкцию. Класс назван отдельно и НЕ слит с
    # находками «член без страницы»; в подтверждённые он не попадает.
    twins = [route for route in routes if route.get("assembly") == "adapter-twins"]
    judged = [route for route in routes if route.get("assembly") != "adapter-twins"]
    listing_cache = {}
    hierarchy_cache = {}
    checked = [check_route(route, listing_cache, hierarchy_cache) for route in judged]
    counts = collections.Counter(item["status"] for item in checked)
    bases = collections.Counter(item.get("basis", "нет") for item in checked)
    exceptions = [{"route": name, "note": note} for name, note in EXCEPTIONS.items()
                  if any(route["route"].split(".")[0] == name for route in routes)]
    unmeasured = [item for item in checked if item["status"] == "не проверено"]
    unconfirmed = [item for item in checked if item["status"] != "Подтверждено"]
    confirmed = [item for item in checked if item["status"] == "Подтверждено"]
    inherited = [item for item in confirmed if item["relation"] == "предок"]
    groups = [item for item in confirmed if item["how"] == "групповая страница"]
    typos = [item for item in confirmed if item["doc_typo"]]
    return {
        "instrument": "api-audit-docs",
        "date": stamp(),
        "source": os.path.relpath(routes_path, ROOT).replace("\\", "/"),
        "help_root": HELP_ROOT,
        "interfaces_checked": len({route["declared_on"] for route in judged}),
        "routes_checked": len(checked),
        "routes_outside_documentation": [{"route": route["route"],
                                          "reason": "собственный тип адаптера (`*Twin`), объявленный "
                                                    "самим адаптером, а не интерфейс КОМПАС: страницы "
                                                    "справки вендора у него нет по построению"}
                                         for route in twins],
        "statuses": dict(counts),
        "bases": dict(bases),
        # Полнота столбца — свойство МАРШРУТОВ, а не настроения сервера: столбец полон, когда у
        # каждого маршрута есть ответ (подтверждено / страницы нет / противоречие), и НЕ полон, пока
        # хоть один остался `не проверено`. Прежде это выводилось из предохранителя, и открытый
        # предохранитель назывался неполнотой даже там, где ответ был получен по зеркалу.
        "documentation_column_complete": not unmeasured,
        "unmeasured": [item["route"] for item in unmeasured],
        "unconfirmed": unconfirmed,
        "confirmed": sorted(item["route"] for item in confirmed),
        # HOW each confirmation was reached: the interface itself, an ancestor's page, one page
        # documenting a group, or a page whose title misspells the member. Named separately so that
        # the reader sees which part of the column rests on an inherited page.
        "confirmation_ways": {
            "own_interface": len([item for item in confirmed if item["relation"] == "сам интерфейс"]),
            "inherited": len(inherited),
            "group_page": len(groups),
            "title_typo": len(typos),
        },
        "inherited": [{"route": item["route"], "documented_on": item["documented_on"],
                       "page": item["page"]} for item in inherited],
        "group_pages": [{"route": item["route"], "documented_on": item["documented_on"],
                         "page": item["page"]} for item in groups],
        "title_typos": [{"route": item["route"], "documented_on": item["documented_on"],
                         "typo": item["doc_typo"], "title": item["title"]} for item in typos],
        "mirror": mirror_info(),
        "exceptions": exceptions,
        "object_types": check_object_types(payload.get("object_type_constants") or []),
        "circuit_breaker": {
            "open": _STATE["open"], "after_consecutive_refusals": CIRCUIT_BREAKER_AFTER,
            "max_consecutive_refusals": _STATE["max_consecutive_refusals"],
            "refusal_backoffs": _STATE["refusal_backoffs"],
            "refusal_backoff_s": list(REFUSAL_BACKOFF_S),
            "polite_delay_s": POLITE_DELAY_S,
            "note": "отказы 429/445 пережидаются отступом и повторяются; предохранитель только "
                    "СЧИТАЕТ, сколько отказов шло подряд, и больше не обрывает прогон. Полнота "
                    "столбца выводится из маршрутов (`documentation_column_complete`), а не отсюда",
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

    listing, statuses, _groups = member_list("ksPart")
    check("перечень членов читается со страниц САМОГО интерфейса", bool(listing),
          f"членов {len(listing)}, статусы {statuses}")
    check("названный член присутствует в перечне", "newentity" in listing,
          str([name for name in listing if name.startswith("new")][:5]))

    check("аксессор и свойство считаются одним именем",
          candidate_names("get_PartsEx") == ["get_partsex", "partsex"],
          str(candidate_names("get_PartsEx")))

    # Адрес страницы называет СВОЙ интерфейс. Обе половины в одной постановке: страница своего
    # интерфейса подтверждает, страница ДРУГОГО интерфейса - нет (иначе правило «тот же интерфейс»
    # перестало бы различать и подтверждало бы по имени члена).
    check("адрес страницы называет свой интерфейс",
          address_names_interface(f"{HELP_ROOT}ksdocument2d_kslineseg.html", "ksDocument2D"))
    check("адрес страницы ЧУЖОГО интерфейса не подтверждает",
          not address_names_interface(f"{HELP_ROOT}ipart7_measurement3d.html", "IPart")
          and not address_names_interface(f"{HELP_ROOT}iaxis3d_name.html", "IAxes3D"))

    # §1.1: the ancestor chain, read from the interface's OWN page, and its two controls.
    ancestors, basis, _url = interface_ancestors("IChamfer", {})
    check("цепочка предков читается со строки «Иерархия» страницы интерфейса",
          ancestors == ["IKompasAPIObject", "IModelObject"], f"{ancestors}, основа={basis}")
    owner = None
    for candidate in ["IChamfer"] + ancestors:
        if confirm_member_on(candidate, "Name", {}):
            owner = candidate
            break
    check("член предка подтверждается через цепочку", owner == "IModelObject", f"владелец={owner}")
    check("отрицательный контроль: члена нет ни у интерфейса, ни у предков",
          confirm_member_on("IChamfer", "ZzzAuditAbsent", {}) is None)
    check("чужой интерфейс с тем же именем члена вне цепочки НЕ подтверждает (урок F-06)",
          "IFeaturePattern" not in ancestors
          and confirm_member_on("IChamfer", "GeometryPattern", {}) is None)

    # §1.2: one page documenting a group of members.
    _listing, _statuses, group_names = member_list("ksMassInertiaParam")
    check("групповая страница называет каждого члена группы",
          {"jy", "jz", "jy0", "jz0"} <= set(group_names), str(sorted(group_names)))
    check("член группы подтверждается своей группой",
          (confirm_member_on("ksMassInertiaParam", "jz", {}) or {}).get("how") == "групповая страница")

    # The listing is searched under the accessor spelling too: the route carries `get_PartsEx` while
    # the help names the page after the property `PartsEx`.
    check("аксессор находит страницу свойства в перечне интерфейса",
          bool(confirm_member_on("IPart7", "get_PartsEx", {})))

    # §1.3: a page whose ADDRESS carries the member but whose title misspells it.
    typo = confirm_member_on("ksFeature", "IsValid", {})
    check("опечатка заголовка названа, а не выдана за противоречие",
          bool(typo) and typo.get("doc_typo") == "isvialid", str(typo and typo.get("doc_typo")))

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
    print(f"способы подтверждения: {result['confirmation_ways']}")
    print(f"не подтверждено: {len(result['unconfirmed'])}")
    for item in result["unconfirmed"][:10]:
        print(f"  {item['route']} - {item.get('reason')}")
    print(f"исключения: {[item['route'] for item in result['exceptions']]}")
    print(f"записано: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
