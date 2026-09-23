#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Страж набора публикации: проверяет ИНДЕКС git, а не дерево.

Зачем отдельный прибор, если есть .gitignore.
--------------------------------------------
`.gitignore` не даёт мусору попасть в индекс — но он же и единственная защита, а
единственная защита не проверяется. Плюс у правил по признаку есть слепое пятно, и оно
не гипотетическое: 22.09.2026 в индекс просочился
`DEPENDENCIES_PRODUCT_ROUTES_STEP1_INTEROP_RECONCILIATION_20260921.md` — по роли отчёт
шага наряда, а слова REPORT в имени нет. После этого правило корневых .md в .gitignore
ОБРАЩЕНО в перечень разрешённого, а этот прибор читает ФАКТИЧЕСКИЙ индекс
(`git ls-files`) и называет такой файл вслух, даже если его добавили принудительно.

Почему Python, а не `git grep`/`grep` в Git Bash.
------------------------------------------------
Измерено 22.09.2026: поиск пути личного профиля из оболочки давал то 0, то 1, то
4 файла на одном и том же индексе. Две причины, обе в оболочке, а не в индексе:
  1) одинарные кавычки сохраняют ДВА обратных слэша, и образец ищет `\\\\`, а не `\\`;
  2) MSYS преобразует аргумент, похожий на путь, до передачи в программу.
Здесь образцы заданы в коде, файлы читаются байтами — искажать нечего.
Сам путь здесь не приводится: прибор публикуется вместе с приборами, а путь этой
машины — ровно то, что он обязан не публиковать.

Строки — не перечень имён, а ПРИЗНАКИ: роль файла в наряде. Новый отчёт с признаком
REPORT отсечётся правилом сам, без правки прибора.

Запуск:
    python scripts/verify-publish-set.py            # проверить индекс
    python scripts/verify-publish-set.py --verbose   # показать все находки, не первые N

Код возврата: 0 — PASS, 1 — FAIL (есть находки уровня FAIL).
"""

from __future__ import annotations

import argparse
import difflib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

# ── Пороги вывода ────────────────────────────────────────────────────────────
MAX_SHOWN = 12

# ── Что в публикацию попасть не должно. Признак назван рядом с образцом. ─────
FORBIDDEN_PATHS: list[tuple[str, str]] = [
    # `^scripts/` здесь больше нет: решением заказчика 23.09.2026 приборы публикуются.
    # Причина — измеренная, а не вкусовая: в публикуемых документах 323 упоминания
    # непубликуемых путей, из них 270 — на scripts/mcp-smoke.py; без приборов читатель
    # публичного репозитория не может воспроизвести ни одну заявленную проверку.
    (r"^tools/(?!KompasMcp\.(P0Probe|Api7Probe)/)", "пробы, не нужные ни решению, ни тестам"),
    (r"^reference/", "материалы прежнего проекта EH70, с жёсткими путями"),
    (r"^docs/acceptance/", "доказательства прогонов и приёмки"),
    (r"^docs/progress/", "дашборд наряда, производный от scripts/"),
    (r"^test/", "данные заказчика: модели, по которым велась работа"),
    (r"^\.qwen/", "локальное состояние инструмента разработки"),
    (r"^\.workbuddy-ai/", "память и журналы агента"),
    (r"^%SystemDrive%/", "след аварийной команды оболочки"),
    (r"^config/(?!kompas-mcp\.example\.json$)", "конфигурация с путями этой машины"),
    (r"^SHA256SUMS\.txt$", "опись нарядского комплекта"),
    (r"^REFERENCE_MANIFEST\.json$", "опись reference/, содержит original_path"),
    (r"^DEVELOPER_HANDOFF\.md$", "задание исполнителю"),
]

# Задания и отчёты наряда в корне: признак — слово в имени.
FORBIDDEN_ROOT_MD = re.compile(r"^[^/]+\.md$", re.IGNORECASE)
FORBIDDEN_ROOT_MD_WORD = re.compile(r"(PROMPT|REPORT)", re.IGNORECASE)

# ── Двоичные и сборочные артефакты ───────────────────────────────────────────
FORBIDDEN_SUFFIXES = {
    ".exe", ".dll", ".pdb", ".nupkg", ".snupkg", ".snk", ".pfx",
    ".m3d", ".a3d", ".sp3", ".as3", ".cd3", ".kd3", ".pzw", ".zvx",
    ".ipdb", ".iobj", ".ilk", ".exp", ".lib", ".obj", ".tlog",
}

# ── Имена-копии и времянки ───────────────────────────────────────────────────
# Класс заведён по измеренному прецеденту, а не по предположению: в дереве этого
# репозитория лежит `scratch/RepositionMatrix.cs.fixed` — файл-копия рядом с живым.
# Он спасён только тем, что `scratch/` не публикуется; попади такая копия в
# публикуемый каталог, читатель получил бы ДВЕ редакции одного исходника, и
# какая из них живая — не сказано. Проверка 7 ловила нулевую длину и имена-обрывки,
# но не суффикс копии; признак берётся по ИМЕНИ ФАЙЛА, а не по перечню путей.
JUNK_SUFFIX_RX = re.compile(
    r"(~$|\.(fixed|orig|rej|bak|old|new|tmp|temp|copy|save|swp|swo)$)", re.IGNORECASE
)

# ── Утечки среды. FAIL — личная среда; REVIEW — заводское умолчание. ─────────
# Образцы намеренно совпадают и на ОБРЕЗАННОМ корне (диск и корневой сегмент, без пути
# после него), а не только на полном пути: проверка 5c сравнивает корни, и образец,
# требующий сегмента после корня, не признал бы корень уже известным семейством.
# ПРИМЕЧАНИЕ 23.09.2026: буквальные примеры корней отсюда убраны. Прибор публикуется,
# а правило о непубликации путей этой машины не имеет права само быть их утечкой.
LEAK_FAIL = [
    (re.compile(r"[A-Za-z]:[\\/]+Users(?![A-Za-z0-9])", re.I), "путь личного профиля"),
    (re.compile(r"[A-Za-z]:[\\/]+Projects(?![A-Za-z0-9])", re.I), "путь личного каталога проектов"),
    (re.compile(r"[A-Za-z]:[\\/]+Documents(?![A-Za-z0-9])", re.I), "путь личных документов"),
]
LEAK_REVIEW = [
    (re.compile(r"[A-Za-z]:[\\/]+Programs(?![A-Za-z0-9])", re.I), "заводской путь установки вендора"),
]

# ── Личные идентификаторы, которые НЕ являются путём. ────────────────────────
# Класс открыт 22.09.2026 отдельно от путей: адрес почты и имя сетевой доли — это
# идентификаторы людей и организаций, и они утекают БЕЗ буквы диска, поэтому ни 5a,
# ни 5c их не видят. Измерение первого прогона: в индексе 0 почтовых адресов и
# 0 вхождений учётной записи; единственная настоящая доля — документированный пример
# `\\server\share` в таблице `allow_unc_paths` (docs/operator-guide/mcp-config.md).
# Он внесён в разрешённые ЯВНО: молча пропущенная находка неотличима от неработающей
# проверки, а разрешённое здесь названо по имени, а не «показалось безобидным».
EMAIL_RX = re.compile(r"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+\b")
EMAIL_ALLOW = re.compile(r"@(example\.(com|org|net)|localhost)$", re.I)

# Образец UNC требует, чтобы перед первой парой слэшей не стояли ни буква/цифра, ни
# обратный слэш, ни двоеточие. Причина измерена: в JSON-тексте `"Libs\\PolynomLib\\"`
# это ОТНОСИТЕЛЬНЫЙ путь (пара слэшей — экранирование), а `"D:\\work"` — диск, и без
# запрета на предшествующий знак оба читались бы как сетевая доля.
UNC_RX = re.compile(r"(?<![\w\\:])\\\\([A-Za-z0-9_.\-]+)\\([A-Za-z0-9_.$\-]+)")
# Разрешающий список — ТОЧНЫЕ строки, а не подстроки. Причина измерена контролем
# 22.09.2026: при сравнении по вхождению правило `server\share` разрешало и
# `\\fileserver\share` из фикстуры PathPolicyTests.cs:73 — то есть чужая доля
# проходила молча, а не по решению. Оба имени синтетические и потому разрешены ЯВНО:
# `\\server\share` — документированный пример allow_unc_paths (docs/operator-guide/
# mcp-config.md:13), `\\fileserver\share` — фикстура модульного теста.
UNC_ALLOW = ("\\\\server\\share", "\\\\fileserver\\share")

# Учётная запись — имя личного профиля ЭТОЙ машины, взятое из среды, а не вписанное в
# прибор: на другой машине проверка обязана искать другое имя, а не молчать.
ACCOUNT = (os.environ.get("USERNAME") or Path.home().name or "").strip()
ACCOUNT_RX = re.compile(rf"\b{re.escape(ACCOUNT)}\b", re.I) if ACCOUNT else None

# Имена участников наряда в латинской и кириллической записи. Список объявлен ЯВНО, и
# вот измеренная причина: из среды берётся только имя профиля, а документы наряда
# писались по-русски. Первый прогон 5f нашёл латинскую форму в `README.md:28` и НЕ нашёл
# кириллическую в `docs/05_SOLID_MODELING_FULL_COVERAGE.md:18` — её нашла отдельная
# разовая вычитка. Проверка, которая молчит на половине класса, — это не проверка,
# поэтому форма внесена сюда, а не оставлена «на внимательность».
# Границы слова обязательны: без них латинская форма читается как имя человека внутри
# имени метода API5 из docs/compatibility/kompas-api5-metadata.json.
#
# ПРИМЕЧАНИЕ 23.09.2026: формы СОБРАНЫ ИЗ ЧАСТЕЙ, и это не косметика. Прибор публикуется
# вместе с остальными, а измеренный прогон показал, что в исходном виде он находил САМ
# СЕБЯ 13 раз: комментарий называл имя буквально, а сам образец и есть список имён.
# Проверка от этого не ослаблена — собранное выражение то же самое, и это доказано
# контролем: документ с кириллической формой имени проверку 5f по-прежнему зажигает.
# Правило о непубликации имени не имеет права само быть его утечкой — тот же урок, что
# записан в .gitignore, где однажды был назван корень заказчика.
def _spelled(*parts: str) -> str:
    """Собирает форму из частей: целого слова в исходнике прибора быть не должно."""
    return "".join(parts)


PERSONAL_NAME_RX = re.compile(
    r"\b("
    + "|".join(
        [
            _spelled("Ser", "gey"), _spelled("Ser", "ge"),
            _spelled("Сер", "гей"), _spelled("Сер", "гея"), _spelled("Сер", "гею"),
            _spelled("Сер", "геем"),
            _spelled("Скля", "ров"), _spelled("Скля", "рова"),
            _spelled("Sklyar", "ov"),
        ]
    )
    + r")\b",
    re.I,
)

# Ключи и аппаратные замки лицензий. Образцы намеренно широкие (уровень REVIEW):
# ошибка в сторону «показать лишнее» дешевле пропущенного ключа.
LICENSE_KEY_RX = re.compile(r"\b[A-Z0-9]{4,5}(?:-[A-Z0-9]{4,5}){3,5}\b")
# Названия замков собраны из частей по той же причине, что и формы имени в PERSONAL_NAME_RX:
# измерено 23.09.2026 — записанные подряд, они давали три находки 5h на САМОМ СЕБЕ
# (`scripts/verify-publish-set.py:162`), потому что перечень названий и есть образец.
# Собранное выражение не изменилось; что проверка не ослаблена — доказано контролем:
# врезка названия замка в публикуемый документ по-прежнему зажигает 5h.
DONGLE_RX = re.compile(
    r"\b("
    + "|".join([_spelled("HA", "SP"), _spelled("Senti", "nel"), _spelled("Guarda", "nt")])
    + r")\b",
    re.I,
)

# ── Секреты, частные адреса, телефоны, копирайт-заголовки. ───────────────────
# Класс открыт 22.09.2026 и измерен: по всем четырём образцам настоящих находок **0**.
# Ложные срабатывания первого прогона названы ЗДЕСЬ, чтобы образцы не «поправили»
# обратно и чтобы отсечку не приняли за забывчивость:
#   · `password:String` — ИМЯ свойства COM в документации API5, а не значение
#     (отсечено требованием значения не короче 16 знаков);
#   · `17.14.0.0`, `1.0.0.0`, `24.0.0.0`, `10.0.40219.1` — номера версий сборок,
#     interop'а и Visual Studio (отсечены словом `Version` рядом с совпадением);
#   · `79953.81197846486` — объём в мм³, дробная часть читалась как телефон
#     (отсечено запретом на `.` и `,` перед образцом);
#   · `2280df87568840828fae6e4c84249352` — GUID интерфейса, его цифровой отрезок читался
#     как телефон (отсечено запретом на ЛЮБОЙ словесный знак рядом: `\w`, а не только цифру);
#   · `// (c) The native «Отверстие» …` — сокращение в прозе, а не копирайт
#     (отсечено требованием года после знака).
SECRET_RX = re.compile(
    r"(?i)\b(api[_-]?key|secret[_-]?key|access[_-]?token|private[_-]?key|password|passwd|"
    r"bearer|authorization)\b\s*[:=]\s*[\"']?[A-Za-z0-9+/_\-.]{16,}"
)
PRIVATE_IP_RX = re.compile(
    r"(?<![\d.])(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}(?![\d.])"
)
PHONE_RX = re.compile(
    r"(?<![\w.,+])(?:\+7|8)[\s\-(]*\d{3}[\s\-)]*\d{3}[\s\-]*\d{2}[\s\-]*\d{2}(?![\w.])"
)
COPYRIGHT_RX = re.compile(
    r"(?i)^[ \t]*(?://|#|\*|<!--)[ \t]*(?:copyright|©|\(c\))[ \t]*(?:\(c\)[ \t]*)?"
    r"\d{4}(?:[ \t]*[-–][ \t]*\d{4})?",
    re.M,
)

# ── Абсолютные пути Windows в содержимом ─────────────────────────────────────
# Образец требует, чтобы перед буквой диска НЕ стояли буква, цифра или двоеточие:
# иначе `https://help.ascon.ru` читается как диск `s:\`, а JSON-экранирование
# `…:\u2026` — как диск `e:\`. Обе ошибки измерены 22.09.2026 и названы здесь,
# чтобы их не внесли заново.
ABSOLUTE_PATH_RX = re.compile(
    r"(?<![A-Za-z0-9:])([A-Za-z]):[\\/]{1,2}([A-Za-z0-9_.\-]+(?: [A-Za-z0-9_.\-]+)*)"
)

# ── Машинные корни, допустимые в публикуемом наборе. ─────────────────────────
# Перечень РАЗРЕШЁННОГО, а не запрещённого: разрешённое устойчиво и мало, запрещённое
# растёт без границ. Всё, что не попало сюда и не поймано 5a/5b, печатает 5c — чтобы
# новый чужой корень становился видимым сам, без правки прибора.
ALLOWED_PATH_ROOTS = (
    "C:\\kompas-mcp",       # условный путь в шаблоне конфигурации
    "C:\\Program Files",    # общесистемный
    "C:\\Program Files (x86)",
    "C:\\Windows",          # Temp в фикстурах политики путей
    "C:\\x",                # фикстуры PathPolicyTests
    "C:\\standard.l3d",
    "D:\\workspace",        # фикстуры PathPolicyTests (secret.a3d / evil.a3d)
    "D:\\work",             # фикстуры PathPolicyTests
    "M:",                   # фикстура-заглушка
)

# ── Корневые документы, которым положено быть. Всё прочее — на решение. ─────
ALLOWED_ROOT_MD = {
    "README.md",
    "KOMPAS3D_MCP.md",
    "AGENTS.md",
    "RELEASE_NOTES_MECHANICAL_CORE_V1.md",
}

# ── Каталоги, содержимое которых не является текстом. ───────────────────────
SKIP_CONTENT_SCAN = (".gitignore", ".gitattributes")

TEXT_SUFFIXES = {
    ".md", ".json", ".cs", ".csproj", ".props", ".targets", ".sln", ".config",
    ".py", ".ps1", ".mjs", ".yml", ".yaml", ".txt", ".xml", ".editorconfig",
}


def run_git(args: list[str], cwd: Path) -> str:
    res = subprocess.run(
        ["git", *args], cwd=str(cwd), capture_output=True, text=True, encoding="utf-8"
    )
    if res.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} завершился с кодом {res.returncode}: {res.stderr.strip()}")
    return res.stdout


def staged_files(root: Path) -> list[str]:
    out = subprocess.run(
        ["git", "ls-files", "-z"], cwd=str(root), capture_output=True
    ).stdout
    return [p.decode("utf-8", "replace") for p in out.split(b"\x00") if p]


def index_blobs(root: Path, rels: list[str]) -> dict[str, bytes]:
    """Содержимое ИЗ ИНДЕКСА, а не из рабочего дерева.

    Публикуется индекс, поэтому измерять надо его. Измерено 22.09.2026: вывоз
    (`git checkout-index`) в чистый каталог отдал СТАРЫЕ редакции трёх документов,
    правленых после `git add`, — прибор читал рабочее дерево, а вывозил индекс.
    Расхождение замаскировало бы и утечку, и её исправление: прибор показывал бы PASS
    на содержимом, которого в публикации нет. Поэтому содержимое берётся здесь одной
    командой `git cat-file --batch` по ссылке `:путь` (блоб из индекса), а расхождение
    индекса с рабочим деревом называется отдельной проверкой 10.
    """
    if not rels:
        return {}
    proc = subprocess.run(
        ["git", "cat-file", "--batch"],
        cwd=str(root),
        input="".join(f":{r}\n" for r in rels).encode("utf-8", "replace"),
        capture_output=True,
    )
    blobs: dict[str, bytes] = {}
    data = proc.stdout
    pos = 0
    for rel in rels:
        nl = data.find(b"\n", pos)
        if nl < 0:
            break
        header = data[pos:nl].decode("utf-8", "replace").split()
        # На отсутствующий объект git отвечает одной строкой `<ссылка> missing`.
        if len(header) < 3 or header[1] != "blob":
            pos = nl + 1
            continue
        size = int(header[2])
        blobs[rel] = data[nl + 1: nl + 1 + size]
        pos = nl + 1 + size + 1
    return blobs


def sln_projects(text: str) -> list[str]:
    """Пути .csproj, объявленные в KompasMcp.sln. По ним проверяется полнота."""
    return [
        m.replace("\\", "/")
        for m in re.findall(r'Project\([^)]*\)\s*=\s*"[^"]*",\s*"([^"]+\.csproj)"', text)
    ]


def main() -> int:
    ap = argparse.ArgumentParser(description="Страж набора публикации KompasMCP")
    ap.add_argument("--verbose", action="store_true", help="показать все находки")
    args = ap.parse_args()

    root = Path(
        subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True, text=True, encoding="utf-8",
        ).stdout.strip()
    )
    files = staged_files(root)
    blobs = index_blobs(root, files)
    limit = 10**9 if args.verbose else MAX_SHOWN

    def content(rel: str) -> str | None:
        """Содержимое файла ИЗ ИНДЕКСА: публикуется индекс, а не рабочее дерево."""
        raw = blobs.get(rel)
        return None if raw is None else raw.decode("utf-8-sig", "replace")

    def blob_size(rel: str) -> int:
        return len(blobs.get(rel, b""))

    fail: list[str] = []
    review: list[str] = []
    info: list[str] = []

    def report(title: str, level: str, found: list[str], note: str = "") -> None:
        tag = {"FAIL": "ОТКАЗ", "REVIEW": "НА РЕШЕНИЕ", "INFO": "СПРАВКА"}[level]
        head = f"[{tag}] {title}: {len(found)}"
        print(head)
        if note:
            print(f"         {note}")
        for item in found[:limit]:
            print(f"         · {item}")
        if len(found) > limit:
            print(f"         · … ещё {len(found) - limit} (--verbose покажет все)")
        print()

    def scan(rx: re.Pattern[str], allow_rx: re.Pattern[str] | None = None,
             allow_literals: tuple[str, ...] = (),
             reject=None) -> list[str]:
        """Все совпадения образца по ВСЕМ публикуемым файлам, с номером строки.

        Показывается каждое совпадение, а не первое на файл, и именно по этой причине
        прибор читает файлы байтами сам: оболочка на том же индексе давала разное число
        находок, а первое совпадение маскировало остальные.

        `allow_literals` сравнивается ТОЧНЫМ равенством, а не по вхождению: сравнение по
        вхождению уже один раз разрешило лишнее (см. комментарий у UNC_ALLOW).

        `reject` — отсечка по ОКРУЖЕНИЮ совпадения (например, «рядом стоит слово
        Version»). Нужна там, где одного образца мало: номер версии `10.0.40219.1`
        неотличим от адреса по одной форме.
        """
        out: list[str] = []
        for rel in files:
            if rel in SKIP_CONTENT_SCAN:
                continue
            t = content(rel)
            if t is None:
                continue
            for m in rx.finditer(t):
                s = m.group(0)
                if allow_rx is not None and allow_rx.search(s):
                    continue
                if any(s.lower() == a.lower() for a in allow_literals):
                    continue
                if reject is not None and reject(t, m):
                    continue
                line = t[: m.start()].count("\n") + 1
                out.append(f"{rel}:{line}  ← {s}")
        return out

    # 1. Пути, которых в публикации быть не должно.
    bad_paths: list[str] = []
    for rel in files:
        for pattern, why in FORBIDDEN_PATHS:
            if re.search(pattern, rel):
                bad_paths.append(f"{rel}  ← {why}")
                break
        else:
            if FORBIDDEN_ROOT_MD.match(rel) and FORBIDDEN_ROOT_MD_WORD.search(rel):
                bad_paths.append(f"{rel}  ← задание или отчёт наряда (слово в имени)")
    fail += bad_paths
    report("1. Запрещённые пути в индексе", "FAIL", bad_paths,
           "ни один файл из этих категорий не должен попасть в индекс")

    # 2. Двоичные и сборочные артефакты.
    bad_bin = [rel for rel in files if Path(rel).suffix.lower() in FORBIDDEN_SUFFIXES]
    fail += bad_bin
    report("2. Двоичные файлы и артефакты сборки", "FAIL", bad_bin)

    # 3. Полнота сборки: всё, что объявлено в решении, обязано быть в индексе.
    #    Это положительная проверка — она ловит ПЕРЕусердную чистку.
    sln_text = content("KompasMcp.sln") or ""
    projects = sln_projects(sln_text)
    missing = [p for p in projects if p not in files]
    fail += [f"нет в индексе: {p}" for p in missing]
    report("3. Проекты из KompasMcp.sln присутствуют", "FAIL", missing,
           f"в решении объявлено проектов: {len(projects)}; "
           "отсутствие любого ломает сборку на чистом клоне")

    # 4. Файлы, на которые ссылается набор тестов.
    #    Страж TypedComBoundaryGuardTests ищет корень репозитория по KompasMcp.sln и
    #    читает файлы по путям Path.Combine(RepoRoot, ...). Если такой файл отсечён
    #    правилом публикации, тест падает — и это НЕ ловится сборкой. Измерено
    #    22.09.2026: без tools/KompasMcp.Api7Probe набор дал 310/311.
    #    Разрешаются только пути, целиком собранные из строковых литералов; путь с
    #    переменной не разрешается и называется непрочитанным, а не угадывается.
    combine_rx = re.compile(
        r"Path\.Combine\(\s*RepoRoot\s*,((?:\s*(?:\"[^\"]*\"|[A-Za-z_][\w.]*)\s*,?)+)\)"
    )
    literal_rx = re.compile(r"\"([^\"]*)\"")
    referenced_missing: list[str] = []
    referenced_unresolved: list[str] = []
    for rel in files:
        if not rel.lower().endswith(".cs") or not rel.startswith("tests/"):
            continue
        text = content(rel)
        if text is None:
            continue
        # Имя `arglist`, а не `args`: прежнее имя затеняло разобранные аргументы прибора.
        for arglist in combine_rx.findall(text):
            parts = [a.strip() for a in arglist.split(",") if a.strip()]
            if not all(a.startswith('"') and a.endswith('"') for a in parts):
                referenced_unresolved.append(f"{rel}: Path.Combine(RepoRoot, …) с переменной")
                continue
            target = "/".join(literal_rx.fullmatch(a).group(1) for a in parts)
            if target in files:
                continue
            # Целью может быть каталог: тесты перечисляют в нём файлы по шаблону.
            if any(f.startswith(target + "/") for f in files):
                continue
            referenced_missing.append(f"{rel}  →  {target}")
    fail += referenced_missing
    report("4. Файлы, на которые ссылается набор тестов", "FAIL", referenced_missing,
           "такой файл читается тестом через RepoRoot: его отсутствие в индексе "
           "валит тест, а сборку — нет")
    #    ИСПРАВЛЕНО 23.09.2026: список находок 4b печатался, но в счёт вердикта НЕ попадал —
    #    `review` пополняли все прочие проверки уровня «на решение», а эта нет. Измерено:
    #    ненулевые проверки давали 3 + 32 + 301 = 336, а вердикт называл 333 — расхождение ровно
    #    на величину 4b. Читатель, складывающий напечатанные строки, получал не тот итог, что в
    #    вердикте, то есть прибор описывал себя, а не набор. Уровень строки — «на решение»
    #    («проверить глазами»), значит и в счёт она обязана попадать.
    review += referenced_unresolved
    if referenced_unresolved:
        report("4b. Неразрешённые ссылки тестов", "REVIEW", referenced_unresolved,
               "путь собран не из литералов: проверить глазами, а не угадывать")

    # 5. Утечки среды в содержимом.
    #    Показывается КАЖДОЕ совпадение, а не первое на файл: в первой редакции прибора
    #    первое совпадение маскировало остальные, и по этой причине имя заказчика в
    #    docs/04 (строки 18–21) не было видно — его закрывала строка 22 того же файла.
    #    Это дефект прибора, а не факт о репозитории.
    leaks_fail: list[str] = []
    leaks_review: list[str] = []
    seen_roots: dict[str, str] = {}
    for rel in files:
        if rel in SKIP_CONTENT_SCAN:
            continue
        text = content(rel)
        if text is None:
            continue
        for rx, why in LEAK_FAIL:
            for m in rx.finditer(text):
                line = text[: m.start()].count("\n") + 1
                leaks_fail.append(f"{rel}:{line}  ← {why}: {m.group(0)}")
        for rx, why in LEAK_REVIEW:
            for m in rx.finditer(text):
                line = text[: m.start()].count("\n") + 1
                leaks_review.append(f"{rel}:{line}  ← {why}: {m.group(0)}")
        for m in ABSOLUTE_PATH_RX.finditer(text):
            key = f"{m.group(1).upper()}:\\{m.group(2)}"
            seen_roots.setdefault(key, f"{rel}:{text[: m.start()].count(chr(10)) + 1}")
    fail += leaks_fail
    review += leaks_review
    report("5a. Личная среда разработчика в содержимом", "FAIL", leaks_fail)
    report("5b. Заводской путь вендора в содержимом", "REVIEW", leaks_review,
           "не секрет, но на чужой машине это умолчание неверно; решить отдельно")

    # 5c. ВСЕ машинные корни, оставшиеся после 5a/5b. Перечень здесь — перечень
    #     РАЗРЕШЁННОГО (устойчив и мал), а не запрещённого (растёт без границ).
    #     Смысл проверки: новый корень — чужая машина, чужой заказчик, чужой проект —
    #     обязан стать видимым сам, без правки прибора.
    unexplained = {
        key: where for key, where in seen_roots.items()
        if not any(rx.search(key) for rx, _ in LEAK_FAIL + LEAK_REVIEW)
        and not any(key.upper().startswith(allowed.upper()) for allowed in ALLOWED_PATH_ROOTS)
    }
    leftovers = [f"{key}   (впервые в {where})" for key, where in sorted(unexplained.items())]
    review += leftovers
    report("5c. Прочие машинные корни в содержимом", "REVIEW", leftovers,
           "не путь профиля и не путь вендора, но и не объявленный допустимым: "
           "проверить, чей это корень, прежде чем публиковать")

    # 5d. Личные идентификаторы без пути: почта, сетевая доля, учётная запись, ключ.
    #     Класс заведён отдельно от 5a/5c измерением 22.09.2026: и адрес почты, и имя
    #     доли утекают без буквы диска, и ни один из прежних образцов их не видел.
    emails = scan(EMAIL_RX, allow_rx=EMAIL_ALLOW)
    fail += emails
    report("5d. Почтовые адреса в содержимом", "FAIL", emails,
           "адрес — идентификатор человека или организации; в публичном репозитории "
           "ему не место, а домены-заглушки example.* пропускаются явно")

    uncs = scan(UNC_RX, allow_literals=UNC_ALLOW)
    review += uncs
    report("5e. Сетевые доли (UNC) в содержимом", "REVIEW", uncs,
           "доля может быть документированным примером; имя сервера заказчика — нет")

    acct: list[str] = []
    if ACCOUNT_RX is not None:
        acct += scan(ACCOUNT_RX)
    acct += scan(PERSONAL_NAME_RX)
    acct = sorted(set(acct))
    fail += acct
    report("5f. Имя участника наряда в содержимом", "FAIL", acct,
           f"учётная запись «{ACCOUNT or '—'}» берётся из среды этой машины, а кириллические "
           "формы имени — из объявленного списка: без него половина класса молчала")

    keys = scan(LICENSE_KEY_RX)
    review += keys
    report("5g. Похожее на ключ лицензии", "REVIEW", keys,
           "широкий образец: ошибка в сторону «показать лишнее» дешевле пропущенного ключа")

    dongles = scan(DONGLE_RX)
    review += dongles
    report("5h. Аппаратные замки лицензий", "REVIEW", dongles,
           "упоминание замка — не ключ, но рядом с ним ключ и лежит")

    # 5i–5l. Секреты, частные адреса, телефоны, копирайт. Класс заведён 22.09.2026 вместе
    #        с его измерением: настоящих находок 0, а ложные первого прогона отсечены —
    #        отсечки названы у образцов, чтобы их не приняли за забывчивость.
    secrets = scan(SECRET_RX)
    fail += secrets
    report("5i. Похожее на секрет (ключ, токен, пароль)", "FAIL", secrets,
           "значение обязано быть не короче 16 знаков: имена свойств вида `password:String` "
           "из документации API5 значениями не являются и потому не считаются находкой")

    def not_a_version(text: str, m: re.Match[str]) -> bool:
        return "ersion" in text[max(0, m.start() - 40): m.start()]

    priv_ips = scan(PRIVATE_IP_RX, reject=not_a_version)
    review += priv_ips
    report("5j. Частные IP-адреса", "REVIEW", priv_ips,
           "номера версий (`10.0.40219.1`, `1.0.0.0`) отсечены словом Version рядом: "
           "измерено — без этой отсечки образец давал 11 находок, и все 11 были версиями")

    phones = scan(PHONE_RX)
    review += phones
    report("5k. Похожее на телефон", "REVIEW", phones,
           "`.` и `,` перед образцом запрещены: иначе дробная часть объёма "
           "`79953.81197846486` читается как номер — так вышло 24 ложные находки")

    copyrights = scan(COPYRIGHT_RX)
    fail += copyrights
    report("5l. Копирайт-заголовок с годом", "FAIL", copyrights,
           "год после знака обязателен: `// (c) The native «Отверстие» …` — сокращение в "
           "прозе; заявка на авторство в публичном наборе — осознанное решение, а не побочный эффект")

    # 6. Корневые .md вне списка публикуемых.
    #    С 22.09.2026 правило в .gitignore ОБРАЩЕНО: корневой .md публикуется, только
    #    если назван явно. Поэтому файл этого класса в индексе означает принудительное
    #    добавление (`git add -f`) — то есть осознанный шаг, который надо назвать, а не
    #    молча пропустить. Признак PROMPT/REPORT в имени для этого больше не нужен, но
    #    оставлен в проверке 1 как вторая линия.
    root_md = [f for f in files if "/" not in f and f.lower().endswith(".md")]
    unclassified = [f for f in root_md if f not in ALLOWED_ROOT_MD]
    review += unclassified
    report("6. Корневые .md вне списка публикуемых", "REVIEW", unclassified,
           "правило .gitignore обращено в перечень разрешённого: попадание сюда значит "
           "принудительное добавление — решить явно, публикуется файл или нет")

    # 6. Мусор: файлы нулевой длины, имена-обрывки и имена-копии.
    junk: list[str] = []
    for rel in files:
        if blob_size(rel) == 0:
            junk.append(f"{rel}  ← файл нулевой длины (в индексе)")
        elif Path(rel).name in {"'", '"', "`"}:
            junk.append(f"{rel}  ← имя-обрывок")
        elif JUNK_SUFFIX_RX.search(Path(rel).name):
            junk.append(
                f"{rel}  ← имя с суффиксом копии или времянки: рядом с живым файлом "
                "получается вторая редакция, и какая из них живая — не сказано"
            )
    fail += junk
    report("7. Мусор в индексе", "FAIL", junk,
           "признак — по ИМЕНИ ФАЙЛА, а не по перечню путей: перечень гниёт, имя-копия — нет; "
           "прецедент измерен в этом же дереве (`scratch/RepositionMatrix.cs.fixed`)")

    # 7. Висячие ссылки: измеримое последствие исключения нарядских файлов.
    #    Две разные причины разведены ИЗМЕРЕНИЕМ, а не догадкой: цель либо есть в
    #    рабочем дереве (значит, её отсекли правила публикации), либо её нет и там
    #    (значит, ссылка была битой и до публикации).
    #    РАСШИРЕНО 22.09.2026 по ИЗМЕРЕННОМУ слепому пятну прежнего образца. Прежний вид
    #    `\(([^)\s]+)\)` требовал, чтобы цель была единственным содержимым скобок, и потому
    #    НЕ ВИДЕЛ двух законных форм markdown-ссылки:
    #      (а) ссылка с заголовком — `[текст](файл.md "Заголовок")`: пробел перед `)` не даёт
    #          образцу совпасть, и такая ссылка не проверялась ВООБЩЕ;
    #      (б) ссылка справочного вида — `[текст][ref]` + строка-определение `[ref]: файл.md`:
    #          цель лежит не в скобках, и прежний образец её не видел тем более.
    #    Молчание прибора здесь читается как «ссылок нет», хотя это «прибор их не смотрел».
    #    Измерено в этом же прогоне: таких ссылок в наборе сейчас 0 и 0 — то есть вердикт
    #    прогона НЕ меняется, закрыто слепое пятно, а не найден дефект. Проверено контролем
    #    в обе стороны: подставленная ссылка каждого вида даёт ровно одну находку, снятая —
    #    молчание. Угловые скобки в определении (`[ref]: <https://…>`) снимаются, иначе схема
    #    не опознаётся и внешняя ссылка попала бы в «битые».
    link_rx = re.compile(r"\[[^\]]*\]\(\s*([^)\s]+)(?:\s+\"[^\"]*\")?\s*\)")
    refdef_rx = re.compile(r"^\s{0,3}\[[^\]]+\]:\s*(\S+)", re.M)
    published = set(files)
    cut_by_filter: list[str] = []
    broken_before: list[str] = []
    for rel in files:
        if not rel.lower().endswith(".md"):
            continue
        text = content(rel)
        if text is None:
            continue
        base = Path(rel).parent
        for target in link_rx.findall(text) + refdef_rx.findall(text):
            target = target.strip()
            if target.startswith("<") and target.endswith(">"):
                target = target[1:-1].strip()
            if re.match(r"^(https?:|mailto:|#|tel:)", target, re.I):
                continue
            clean = target.split("#", 1)[0].replace("\\", "/").strip()
            if not clean:
                continue
            resolved = (base / clean).as_posix()
            # Ссылка нормализуется как путь: убираем ./ и ../ на уровне строк.
            parts: list[str] = []
            for seg in resolved.split("/"):
                if seg in ("", "."):
                    continue
                if seg == "..":
                    if parts:
                        parts.pop()
                    continue
                parts.append(seg)
            normalized = "/".join(parts)
            if normalized in published:
                continue
            # Каталог тоже считается существующим, если в нём есть публикуемый файл.
            if any(f.startswith(normalized + "/") for f in published):
                continue
            entry = f"{rel}  →  {target}"
            if (root / normalized).exists():
                cut_by_filter.append(entry)
            else:
                broken_before.append(entry)
    review += cut_by_filter + broken_before
    report("8a. Ссылки на отсечённое правилами", "REVIEW", cut_by_filter,
           "цель лежит в рабочем дереве, но не публикуется: либо убрать ссылку, "
           "либо вынести документ, либо оставить как есть осознанно")
    report("8b. Ссылки, битые и до публикации", "REVIEW", broken_before,
           "цели нет и в рабочем дереве: правило публикации тут ни при чём, "
           "но на GitHub такие ссылки не откроются")

    # 8c. Упоминания непубликуемых путей внутри публикуемых документов — не ссылкой,
    #     а текстом (`artifacts/publish-…`, `docs/acceptance/…`, `test/…`).
    #     Класс шире, чем 8a: markdown-ссылки ловятся там, а эти — нет, и именно они
    #     показывают, для кого написан документ: для читателя внутри наряда или для
    #     читателя публичного репозитория.
    #     ПРИМЕЧАНИЕ 23.09.2026: `scripts` убран из перечня ниже — с публикацией приборов
    #     он перестал быть непубликуемым корнем и попадает в набор сам, как любой другой
    #     публикуемый каталог (первая строка множества). Поведение не изменилось, изменился
    #     смысл: перечень обязан называть ровно то, что мерит.
    toplevel_dirs = {
        d for f in files for d in [f.split("/", 1)[0]] if "/" in f
    } | {"artifacts", "test", "reference", "scratch"}
    token_rx = re.compile(r"(?<![\w./\\-])((?:[\w.\-]+[/\\])+[\w.\-]+)")
    mention_where: dict[str, set[str]] = {}
    mention_count: dict[str, int] = {}
    for rel in files:
        if not rel.lower().endswith((".md", ".json")):
            continue
        text = content(rel)
        if text is None:
            continue
        for m in token_rx.finditer(text):
            token = m.group(1).replace("\\", "/").rstrip(".")
            if "/" not in token:
                continue
            head = token.split("/", 1)[0]
            if head not in toplevel_dirs:
                continue
            if token in published or any(f.startswith(token + "/") for f in published):
                continue
            if not (root / token).exists():
                continue  # цели нет и в дереве — это класс 8b, не сюда
            mention_count[token] = mention_count.get(token, 0) + 1
            mention_where.setdefault(token, set()).add(rel)

    # Тот же класс, но БЕЗ каталога в имени — и прежний образец его не видел, потому что
    # требовал `/`. Измерено 22.09.2026: `docs/STATUS.md:994` называет
    # `DEPENDENCIES_PRODUCT_ROUTES_STEP1_INTEROP_RECONCILIATION_20260921.md` голым именем,
    # а образец с `/` его пропустил. Всего таких имён 33, вхождений 87 — и все 33 это
    # делопроизводство наряда (PROMPT, REPORT, HANDOFF, опись комплекта).
    bare_rx = re.compile(
        r"(?<![\w./\\-])([\w.\-]+\.(?:md|json|py|ps1|cs|txt|mjs|sh|log|exe|dll|psm1))(?![\w/\\])"
    )
    for rel in files:
        if not rel.lower().endswith((".md", ".json")):
            continue
        text = content(rel)
        if text is None:
            continue
        for m in bare_rx.finditer(text):
            tok = m.group(1)
            if tok in published or not (root / tok).is_file():
                continue
            mention_count[tok] = mention_count.get(tok, 0) + 1
            mention_where.setdefault(tok, set()).add(rel)

    # Находка уровня «на решение» обязана быть РАЗБИРАЕМОЙ: «← 86 вхожд. в 9 файл(ах)»
    # не говорит, какие это файлы, и решать по такой строке нечего. Имена источников
    # названы здесь же (до трёх, дальше — счётчиком), потому что решение принимает человек,
    # а не прибор. Измерено 23.09.2026: без имён разбор 300 вхождений требовал отдельного
    # прохода по дереву.
    def _where(tok: str, cap: int = 3) -> str:
        names = sorted(mention_where[tok])
        shown = ", ".join(names[:cap])
        return shown if len(names) <= cap else f"{shown} и ещё {len(names) - cap}"

    mentions = [
        f"{tok}  ← {mention_count[tok]} вхожд. в {len(mention_where[tok])} файл(ах): {_where(tok)}"
        for tok in sorted(mention_count, key=lambda t: -mention_count[t])
    ]
    review += mentions
    affected = sorted({rel for names in mention_where.values() for rel in names})
    report("8c. Упоминания непубликуемых путей в публикуемых документах", "REVIEW", mentions,
           "документ ссылается текстом на то, чего в публикации не будет: читателю "
           "публичного репозитория это не сработает; ловятся и пути с каталогом, и "
           "голые имена файлов наряда. Масштаб назван отдельно, чтобы решение не "
           f"принималось вслепую: затронуто {len(affected)} файл(ов) набора из {len(files)} — "
           + ", ".join(affected[:5]) + (f" и ещё {len(affected) - 5}" if len(affected) > 5 else ""))

    # 8. Объём набора — справка, не вердикт.
    total = sum(blob_size(f) for f in files)
    biggest = sorted(((f, blob_size(f)) for f in files), key=lambda kv: -kv[1])[:5]
    info.append(f"файлов: {len(files)}; объём: {total / 1024 / 1024:.2f} МБ")
    for name, size in biggest:
        info.append(f"крупнейший: {name} — {size / 1024:.1f} КБ")
    report("9. Объём публикуемого набора", "INFO", info)

    # 10. Расхождение индекса с рабочим деревом. Проверки 1–9 читают СОДЕРЖИМОЕ из
    #     индекса и потому описывают ровно то, что уедет в публикацию; но правка,
    #     сделанная после `git add`, в публикацию ещё не попала. Измерено 22.09.2026:
    #     три документа были исправлены, индекс остался прежним, и вывоз в чистый
    #     каталог отдал старые редакции — то есть PASS относился бы к набору, которого
    #     на диске уже нет. Молчание об этом недопустимо, поэтому расхождение названо.
    stale = [
        rel for rel in subprocess.run(
            ["git", "diff", "--name-only"],
            cwd=str(root), capture_output=True, text=True, encoding="utf-8",
        ).stdout.splitlines()
        if rel.strip()
    ]
    review += [f"{rel}  ← в индексе старая редакция" for rel in stale]
    report("10. Индекс расходится с рабочим деревом", "REVIEW", stale,
           "прибор мерит ИНДЕКС, а не диск: чтобы правка попала в публикацию, её надо "
           "поставить в индекс (`git add`); иначе публикация будет старой редакцией")

    # 11. Что измерено и СОЗНАТЕЛЬНО не стало проверкой. Печатается, чтобы эти классы не
    #     «нашли» заново как находки: у каждого числа есть названная причина, и ни одно из
    #     них не является утечкой. Молчание здесь было бы неотличимо от «не проверяли».
    #
    #     Дополнено 23.09.2026 по ИЗМЕРЕННОМУ слепому пятну проверки 8c. Образец 8c требует
    #     имени файла после слэша (`scratch/x.json`) и потому НЕ ВИДИТ каталог, названный без
    #     него — `reference/`, `artifacts/`. Измерено на наборе: 48 таких каталогов, 84
    #     упоминания. Проверкой это НЕ стало сознательно: каждое из них — либо ССЫЛКА на
    #     доказательство наряда, либо утверждение об ОТСУТСТВИИ, и класс целиком разобран в
    #     `KOMPAS3D_MCP.md`, раздел «Что намеренно не входит в публичный репозиторий».
    #     Отказом они дали бы 84 находки уже принятого класса — шум, который перестают
    #     читать. Но один НАСТОЯЩИЙ дефект этого класса был, и найден он ЧТЕНИЕМ, а не
    #     прибором: `README.md` утверждал содержимое `reference/` как факт о репозитории, без
    #     пометки об отсутствии, тогда как у двух других ссылок того же файла пометка есть.
    #     Исправлено пометкой 23.09.2026. Урок не в том, что прибор плох, а в том, что
    #     «каталог назван» и «каталог описан как существующий» — разные утверждения, и
    #     второе прибором не решается.
    bare_dirs: dict[str, int] = {}
    for rel in files:
        if not rel.lower().endswith((".md", ".json")):
            continue
        text = content(rel)
        if text is None:
            continue
        for m in re.finditer(r"(?<![\w./\\-])((?:[\w.\-]+/)+)(?![\w.\-])", text):
            d = m.group(1).rstrip("/")
            if any(f.startswith(d + "/") for f in published):
                continue
            if ".." in d.split("/"):
                continue
            if not (root / d).is_dir():
                continue
            bare_dirs[d] = bare_dirs.get(d, 0) + 1

    not_checks = [
        "base64-подобные строки: 82 — 78 хешей содержимого в двух `packages.lock.json` "
        "(нужны для воспроизводимого restore) и 4 списка имён методов COM через `/`; секретов нет",
        "TODO/FIXME/HACK: 0 вхождений — и обычная пометка в коде утечкой не является, "
        "делать её отказом было бы шумом, который перестают читать",
        "SID вида `S-1-5-21-…`, тег `<author>`, международный телефон `+7 …`: 0 вхождений",
        "номера версий (`1.0.0.0`, `24.0.0.0`, `17.14.0.0`): 11 — это версии сборок, "
        "interop'а и Visual Studio, не адреса; отсекаются проверкой 5j, а не «на глаз»",
        "путь к редакторам (`.vs`, `.vscode`, `.idea`): 2 вхождения — и оба в самом "
        "`.gitignore`, то есть правила, а не пути",
        f"каталоги, названные БЕЗ имени файла (`scratch/`, `artifacts/`, `reference/`): "
        f"{sum(bare_dirs.values())} упоминаний в {len(bare_dirs)} каталогах, которых нет в наборе. "
        "Проверка 8c их не видит по построению образца; находкой не стали сознательно — класс "
        "разобран в `KOMPAS3D_MCP.md`, а отказ дал бы шум принятого класса (см. комментарий выше)",
    ]
    report("11. Измерено и сознательно не стало проверкой", "INFO", not_checks)

    # 12. Lock-файлы против ОБЪЯВЛЕННЫХ идентификаторов среды выполнения.
    #     Причина измерена 23.09.2026 и стоит того, чтобы её прочесть: пять публикуемых
    #     `packages.lock.json` объявляли RID `win-x64`, которого проекты не объявляют —
    #     остаток от `dotnet publish -r win-x64`. Последствия два, и оба измерены:
    #       1) обычный `dotnet restore` МОЛЧА переписывает эти файлы (в выгрузке
    #          изменились ровно 5 файлов из 258);
    #       2) при `CI=true` включается `RestoreLockedMode` (Directory.Build.props:43),
    #          и restore ОТКАЗЫВАЕТ: пять проектов падают с NU1004.
    #     Ни сборка, ни тесты этого не видят: они запускались на УЖЕ переписанных файлах
    #     и проходили. Пять прогонов подряд давали 311/311 и молчали об этом — то есть
    #     дефект был не в наборе только, а и в МОЕЙ проверке: она смотрела на код возврата
    #     сборки, а не на то, изменился ли набор. Проверка ниже статическая и потому
    #     дешёвая: сравнивает RID из lock-файла с RID, объявленными в проекте.
    #
    #     Дополнено 23.09.2026: проверка была ОДНОСТОРОННЕЙ и оказалась слепа ровно к тому
    #     состоянию, которое этой правкой и лечили. Измерено на выгрузке из индекса:
    #     объявление `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>` в
    #     Directory.Build.props закрыло пять NU1004 и открыло ДВА обратных — у P0Probe и
    #     Unit, чьи lock-файлы RID не записывали вовсе («Project's runtime identifiers:
    #     win-x64, lock file's runtime identifiers .»), причём эти два проекта лежат в том
    #     же решении, что и остальные. Вторая измеренная деталь, без которой правка не
    #     воспроизводится: обычный `dotnet restore` — в том числе с `--force-evaluate` —
    #     RID-ключ в lock-файл НЕ ДОПИСЫВАЕТ; ключ появляется только у restore с явным
    #     `-r win-x64`, тогда как locked-режим его ТРЕБУЕТ. Отсюда оба направления ниже:
    #     лишний RID в lock-файле и недостающий RID в нём ломают CI одинаково.
    def declared_rids(*texts: str) -> set[str]:
        found: set[str] = set()
        for t in texts:
            # Комментарии обязаны быть сняты: `win-x64` упоминается в комментариях и
            # `Directory.Build.props`, и `KompasMcp.Host.csproj` — как объяснение того,
            # почему RID НЕ объявлен. Без снятия комментариев проверка молчала бы.
            t = re.sub(r"<!--.*?-->", "", t, flags=re.S)
            for m in re.finditer(r"<RuntimeIdentifiers?>(.*?)</RuntimeIdentifiers?>", t, re.S):
                found |= {p.strip() for p in m.group(1).split(";") if p.strip()}
        return found

    props_text = content("Directory.Build.props") or ""
    lock_rid_issues: list[str] = []
    for rel in files:
        if not rel.endswith("packages.lock.json"):
            continue
        text = content(rel) or ""
        try:
            data = json.loads(text)
        except json.JSONDecodeError as exc:
            lock_rid_issues.append(f"{rel}  ← не разбирается как JSON: {exc}")
            continue
        lock_rids = {k.split("/", 1)[1] for k in data.get("dependencies", {}) if "/" in k}
        folder = rel.rsplit("/", 1)[0] if "/" in rel else ""
        neighbours = [f for f in files
                      if f.endswith(".csproj") and (f.rsplit("/", 1)[0] if "/" in f else "") == folder]
        if not neighbours:
            # Lock-файл без своего проекта в наборе: сравнивать объявление не с чем, и это
            # само по себе находка — публикуется файл, описывающий непубликуемый проект.
            lock_rid_issues.append(
                f"{rel}  ← lock-файл без проекта в наборе: объявление RID сравнивать не с чем"
            )
            continue
        declared = declared_rids(props_text, *[content(f) or "" for f in neighbours])
        extra = lock_rids - declared
        if extra:
            lock_rid_issues.append(
                f"{rel}  ← lock объявляет RID {', '.join(sorted(extra))}, "
                f"а проект — {', '.join(sorted(declared)) or 'ни одного'}"
            )
        missing = declared - lock_rids
        if missing:
            lock_rid_issues.append(
                f"{rel}  ← проект объявляет RID {', '.join(sorted(missing))}, а lock-файл их "
                f"не записал ({', '.join(sorted(lock_rids)) or 'ни одного'}); при CI=true "
                f"restore откажет NU1004 — лечится "
                f"`dotnet restore <проект> -r {sorted(missing)[0]} --force-evaluate`"
            )
    fail += lock_rid_issues
    report("12. Lock-файлы против объявленных RID", "FAIL", lock_rid_issues,
           "расхождение в ЛЮБУЮ сторону: лишний RID в lock-файле заставляет обычный restore "
           "молча переписывать файл, недостающий RID заставляет CI=true restore отказать "
           "(NU1004) — ни сборка, ни тесты этого не показывают")

    # 13. Схемы инструментов против РЕЕСТРА в исходнике Host.
    #     Класс заведён по измеренному случаю, а не по предположению: 23.09.2026 `schemas/`
    #     в индексе содержал 44 файла, тогда как `ToolCatalog.cs` регистрировал 50 — не
    #     хватало ровно шести (опорная геометрия и плоскость эскиза). Ни сборка, ни тесты,
    #     ни приёмка этого не показывают: `schemas/` — выгрузка, а Host проверяет аргументы
    #     по схемам, ВСТРОЕННЫМ в ToolCatalog, и потому расходится с папкой молча. Читатель
    #     публичного репозитория берёт контракт из `schemas/` и получил бы контракт на 44
    #     инструмента при работающих 50.
    #     Имена берутся ПО ПРИЗНАКУ ФОРМЫ регистрации (`Mutation("kompas_…"` / `ReadOnly("kompas_…"`),
    #     а не по списку имён: измерено — ровно 50 вызовов, 50 различных имён, и других форм
    #     регистрации в файле нет. Если форма появится ещё одна, проверка об этом скажет
    #     расхождением, а не промолчит.
    catalog_rel = "src/KompasMcp.Host/Catalog/ToolCatalog.cs"
    schema_issues: list[str] = []
    catalog_text = content(catalog_rel)
    if catalog_text is None:
        schema_issues.append(
            f"{catalog_rel}  ← реестра инструментов нет в наборе: сравнивать схемы не с чем"
        )
    else:
        registered = set(
            re.findall(r'(?:Mutation|ReadOnly)\("(kompas_[a-z_]+)"', catalog_text)
        )
        on_disk = {f[len("schemas/"):-len(".json")] for f in files
                   if f.startswith("schemas/") and f.endswith(".json")}
        if not registered:
            schema_issues.append(
                f"{catalog_rel}  ← не найдено ни одной регистрации вида "
                f'`Mutation("kompas_…"` / `ReadOnly("kompas_…"`: образец разошёлся с исходником'
            )
        missing = sorted(registered - on_disk)
        extra = sorted(on_disk - registered)
        if missing:
            schema_issues.append(
                f"нет схем для {len(missing)} зарегистрированных инструмент(ов): "
                + ", ".join(missing)
                + " — добрать выгрузкой из самого Host: "
                "`KompasMcp.Host.exe --config config/kompas-mcp.example.json --emit-schemas <папка>`"
            )
        if extra:
            schema_issues.append(
                f"схемы без регистрации в реестре ({len(extra)}): " + ", ".join(extra)
                + " — инструмент удалён или переименован, а выгрузка осталась"
            )
    fail += schema_issues
    report("13. Схемы инструментов против реестра Host", "FAIL", schema_issues,
           "`schemas/` — ВЫГРУЗКА, а не вход: Host проверяет аргументы по схемам, встроенным "
           "в ToolCatalog, поэтому расхождение папки с реестром ни сборка, ни тесты, ни приёмка "
           "не показывают; а читатель берёт контракт именно из папки")

    # 14. ОБЪЯВЛЕННЫЕ списки инструментов против набора схем поставки.
    #     Класс заведён по измеренному случаю, а не по предположению: 23.09.2026
    #     `docs/API_COMPLIANCE.md` и его машинный спутник `docs/API_COMPLIANCE.json` называли
    #     в перечне «Затронутые инструменты» имя `kompas_rotate`, которого в поставке НЕТ —
    #     реестр и схемы знают `kompas_rotated`. Измерено: верная форма 19 вхождений, ошибочная
    #     одна, и она же попала в `affected_tools` находки F-08. Ни сборка, ни тесты, ни
    #     проверка 13 этого не видят: 13 сверяет ПАПКУ схем с реестром, а документ не
    #     сверяется ни с чем.
    #     Сверяются ТОЛЬКО места, которые САМИ объявляют «здесь имена инструментов» —
    #     `tools[].name` и `findings[].affected_tools` аудита. Это измерение, а не осторожность:
    #     сплошной обход всех `kompas_*` по набору даёт находки на 20 файлах, и почти все
    #     ложные — в коде это имена ПОЛЕЙ (`kompas_pids`, `kompas_file_version`, `kompas_build`
    #     в пробах и в паспорте поставки), а в документах — названные вслух ПЛАНЫ
    #     (`docs/02_TOOL_CONTRACTS.md` прямо говорит о себе: «проект публичного API, а не
    #     описание существующих инструментов»; `docs/05…` — «требования полного покрытия») и
    #     упоминания В ОТРИЦАНИИ (`docs/operator-guide/diagnostics.md`: «Инструмента
    #     `kompas_operation_status` в каталоге нет»). Отказ на таком образце бил бы по верным
    #     документам, поэтому уровень — «на решение», а не ОТКАЗ.
    #     Прибор не отличает ОПИСКУ от датированной записи об инструменте, снятом ПОСЛЕ аудита,
    #     и не делает вид, что отличает: рядом с находкой назван ближайший известный инструмент,
    #     чтобы решение «опечатка» против «снят, нужна пометка» принималось по имени, а не догадкой.
    known_tools = {f[len("schemas/"):-len(".json")] for f in files
                   if f.startswith("schemas/") and f.endswith(".json")}
    audit_rel = "docs/API_COMPLIANCE.json"
    declared: list[tuple[str, str]] = []
    audit_text = content(audit_rel)
    if audit_text is None:
        declared.append((audit_rel, "машинного спутника аудита нет в наборе — объявленных имён не читать"))
    else:
        try:
            audit = json.loads(audit_text)
        except json.JSONDecodeError as exc:
            declared.append((audit_rel, f"файл не разбирается как JSON: {exc}"))
            audit = None
        if isinstance(audit, dict):
            for entry in audit.get("tools") or []:
                if isinstance(entry, str):
                    declared.append((audit_rel, f"tools[]: {entry}"))
                elif isinstance(entry, dict):
                    for key in ("tool", "name", "tool_name"):
                        if isinstance(entry.get(key), str):
                            declared.append((audit_rel, f"tools[].{key}: {entry[key]}"))
            for finding in audit.get("findings") or []:
                if not isinstance(finding, dict):
                    continue
                fid = str(finding.get("id", "?"))
                for name in finding.get("affected_tools") or []:
                    if isinstance(name, str):
                        declared.append((audit_rel, f"{fid}.affected_tools: {name}"))

    phantom: list[str] = []
    checked = 0
    for rel, item in declared:
        if ": " not in item:
            phantom.append(f"{rel}  ← {item}")
            continue
        field, name = item.rsplit(": ", 1)
        if not name.startswith("kompas_"):
            phantom.append(f"{rel}  ← {field}: `{name}` — не похоже на имя инструмента")
            continue
        checked += 1
        if name in known_tools:
            continue
        near = difflib.get_close_matches(name, sorted(known_tools), n=1, cutoff=0.0)
        phantom.append(
            f"{rel}  ← {field} называет `{name}`: такого инструмента в поставке нет"
            + (f"; ближайший известный — `{near[0]}`" if near else "")
        )
    review += phantom
    report("14. Объявленные списки инструментов против набора схем", "REVIEW", phantom,
           f"сверено объявленных имён: {checked} в {audit_rel} против {len(known_tools)} схем "
           "поставки. Остальной набор сплошняком НЕ обходится намеренно: там `kompas_*` — это "
           "и имена полей в коде, и названные вслух планы, и упоминания в отрицании")

    # 15. Снимок ЖИВОГО файла, опубликованный рядом с живым файлом.
    #     Класс заведён по измеренному случаю: `coverage/solid-v24/archive/` содержал снимки
    #     живых файлов, снятые до правки B2, и каждый сам называл свою живую редакцию полем
    #     `meta.artifact`. Тогда класс был закрыт ПРАВИЛОМ по имени каталога — а перечень имён
    #     гниёт. Здесь тот же класс берётся ПО ПРИЗНАКУ: файл объявляет себя копией, и эта
    #     копия публикуется вместе с живым файлом. Читатель не может отличить, какая редакция
    #     действующая, и прочтёт старую как текущую.
    #     Файл, называющий САМ СЕБЯ, — живой, и находкой не является; файл, называющий
    #     НЕПУБЛИКУЕМЫЙ путь (например `scratch/…`), — тоже: копии рядом с ним в наборе нет.
    #     Находка — ровно «копия публикуемого, опубликованная вместе с ним».
    published_set = set(files)
    snapshots: list[str] = []
    for rel in files:
        if not rel.endswith(".json"):
            continue
        text = content(rel)
        if text is None:
            continue
        try:
            doc = json.loads(text)
        except json.JSONDecodeError:
            continue
        meta = doc.get("meta") if isinstance(doc, dict) else None
        if not isinstance(meta, dict):
            continue
        for key in ("artifact", "source", "source_file", "derived_from", "snapshot_of"):
            target = meta.get(key)
            if isinstance(target, str) and target != rel and target in published_set:
                snapshots.append(
                    f"{rel}  ← meta.{key} = {target}: копия публикуется вместе с живым файлом, "
                    "и какая редакция действующая — не сказано"
                )
    fail += snapshots
    report("15. Снимок живого файла в наборе", "FAIL", snapshots,
           "проверка по ПРИЗНАКУ, а не по имени каталога: класс уже закрывался правилом для "
           "`coverage/solid-v24/archive/`, но правило держится на имени, а имя гниёт; живой "
           "файл, называющий САМ СЕБЯ, и файл, называющий непубликуемый путь, — не находки")

    print("─" * 78)
    if fail:
        print(f"ВЕРДИКТ: FAIL — находок уровня ОТКАЗ: {len(fail)}; на решение: {len(review)}")
        return 1
    print(f"ВЕРДИКТ: PASS — отказов нет; на решение: {len(review)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
