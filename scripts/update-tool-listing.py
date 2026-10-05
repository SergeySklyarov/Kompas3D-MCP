#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Обновляет СГЕНЕРИРОВАННЫЙ список инструментов между метками в обзорных документах.

Зачем прибор, а не ручная таблица.
----------------------------------
Перечень инструментов, написанный руками, гниёт молча. Измерено 23.09.2026: `KOMPAS3D_MCP.md`
и `README.md` называли 50 инструментов, тогда как реестр `ToolCatalog.cs` регистрировал 50
(совпало случайно), а 05.10.2026 — 63. Ни сборка, ни тесты не видят расхождения документа с
реестром, потому что документ ни с чем не сверяется. Здесь список берётся ИЗ РЕЕСТРА: Host
умеет режим `--print-tool-listing`, который печатает имена, заголовки и первую фразу описания
каждого инструмента прямо из `ToolCatalog.All`.

Что делает.
-----------
Находит в документе блок между `<!-- BEGIN TOOL LISTING -->` и `<!-- END TOOL LISTING -->` и
заменяет его содержимое выводом Host. Правка внутри меток руками запрещена: следующая генерация
её затрёт, поэтому между метками ничего, кроме сгенерированных строк, быть не должно.

Запуск:
    python scripts/update-tool-listing.py            # обновить docs/TOOLS.md
    python scripts/update-tool-listing.py --check     # только проверить, ничего не писать
    python scripts/update-tool-listing.py docs/TOOLS.md

Хост берётся из сборки Release x64. Если его нет, прибор называет это, а не догадывается:
    src/KompasMcp.Host/bin/x64/Release/net10.0-windows/KompasMcp.Host.exe

Код возврата: 0 — блоки совпадают (или успешно обновлены), 1 — расхождение при --check,
2 — окружение не готово (нет Host или в документе нет меток).
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

BEGIN = "<!-- BEGIN TOOL LISTING -->"
END = "<!-- END TOOL LISTING -->"

HOST = os.path.join(
    ROOT, "src", "KompasMcp.Host", "bin", "x64", "Release", "net10.0-windows",
    "KompasMcp.Host.exe",
)

DEFAULT_DOCS = ("docs/TOOLS.md",)

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def generated_listing() -> str:
    """The block body straight from the catalog. The Host prints UTF-8 (its own invariant)."""
    if not os.path.exists(HOST):
        print(f"нет сборки Host: {HOST}\n"
              f"  соберите:  dotnet build src/KompasMcp.Host/KompasMcp.Host.csproj "
              f"-c Release -p:Platform=x64", file=sys.stderr)
        raise SystemExit(2)
    result = subprocess.run(
        [HOST, "--print-tool-listing"],
        capture_output=True, check=False,
    )
    if result.returncode != 0:
        print(f"Host --print-tool-listing вернул {result.returncode}: "
              f"{result.stderr.decode('utf-8', 'replace')}", file=sys.stderr)
        raise SystemExit(2)
    return result.stdout.decode("utf-8")


def splice(text: str, body: str) -> tuple[str, str | None]:
    """Replaces the text between the markers. Returns (new_text, error)."""
    begin = text.find(BEGIN)
    end = text.find(END)
    if begin < 0 or end < 0:
        return text, f"нет меток {BEGIN} / {END}"
    if end < begin:
        return text, "метка END стоит раньше BEGIN"
    head = text[: begin + len(BEGIN)]
    tail = text[end:]
    return head + "\n" + body.rstrip("\n") + "\n" + tail, None


def current_block(text: str) -> str | None:
    begin = text.find(BEGIN)
    end = text.find(END)
    if begin < 0 or end < 0 or end < begin:
        return None
    return text[begin + len(BEGIN):end].strip("\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("documents", nargs="*", default=list(DEFAULT_DOCS),
                        help="документы с метками (по умолчанию docs/TOOLS.md)")
    parser.add_argument("--check", action="store_true",
                        help="только проверить расхождение, ничего не записывать")
    args = parser.parse_args()

    body = generated_listing()
    expected = body.rstrip("\n")
    problems = 0

    for rel in args.documents:
        path = os.path.join(ROOT, rel)
        if not os.path.exists(path):
            print(f"{rel}: файла нет — пропущено")
            continue
        with open(path, encoding="utf-8") as handle:
            text = handle.read()

        block = current_block(text)
        if block is None:
            print(f"{rel}: метки не найдены — блок не вставлен ({BEGIN} / {END})")
            problems += 1
            continue

        if block == expected:
            print(f"{rel}: блок совпадает с реестром ({len(expected.splitlines())} строк)")
            continue

        if args.check:
            print(f"{rel}: РАСХОЖДЕНИЕ блока с реестром — запустите без --check, чтобы обновить")
            problems += 1
            continue

        new_text, error = splice(text, body)
        if error is not None:
            print(f"{rel}: {error}", file=sys.stderr)
            problems += 1
            continue
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(new_text)
        print(f"{rel}: блок обновлён ({len(expected.splitlines())} строк)")

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
