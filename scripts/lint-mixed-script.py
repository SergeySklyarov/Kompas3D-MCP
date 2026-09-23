#!/usr/bin/env python3
"""Ловит смешанные алфавиты внутри одного слова: латинская буква, похожая на кириллическую, внутри русского слова.

Соседний lint-cjk.py проверял только иероглифы, а реальный дефект оказался шире: в русскоязычный
текст артефакта попадает латинская буква, похожая на кириллическую. Такое слово выглядит цельным
глазу, но не ищется ни по русскому, ни по английскому подстрочному поиску — то есть ломает и
документацию, и grep по ней.

Исключения: идентификаторы и пути (в них латиница законна), поэтому проверка смотрит на слово
целиком и требует, чтобы в нём были и кириллица, и не менее двух латинских букв подряд.

Запуск: python scripts\\lint-mixed-script.py [файл…]   (без аргументов — текстовые артефакты репо)
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DIRS = ["docs", "coverage", "scripts", "README.md", "DEVELOPER_HANDOFF.md"]
SKIP_DIRS = {"bin", "obj", "scratch", ".git", ".qwen", "__pycache__", "sources"}
MIXED = re.compile(r"(?:[А-Яа-яЁё][A-Za-z]{2,}|[A-Za-z]{2,}[А-Яа-яЁё])")
# Слова, где латиница внутри кириллического написания законна и осознанна.
ALLOWED = {
    "eсли", "fакт",  # не встречаётся сегодня, держим как напоминание о формате исключений
}

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def candidates(paths):
    if paths:
        for p in paths:
            full = p if os.path.isabs(p) else os.path.join(ROOT, p)
            if os.path.isfile(full):
                yield full
            elif os.path.isdir(full):
                for dirpath, dirnames, filenames in os.walk(full):
                    dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
                    for name in filenames:
                        if name.endswith((".md", ".json", ".py", ".cs", ".props")):
                            yield os.path.join(dirpath, name)
        return
    for entry in DEFAULT_DIRS:
        yield from candidates([entry])


def main():
    found = 0
    for path in sorted(set(candidates(sys.argv[1:]))):
        try:
            text = open(path, encoding="utf-8").read()
        except (UnicodeDecodeError, OSError):
            continue
        for number, line in enumerate(text.splitlines(), 1):
            for match in MIXED.finditer(line):
                token = match.group(0)
                if token in ALLOWED:
                    continue
                rel = os.path.relpath(path, ROOT)
                print(f"{rel}:{number}: {token}  |  {line.strip()[:120]}")
                found += 1
    if found:
        print(f"\nподозрительных вхождений: {found}")
        return 1
    print("смешанных алфавитов не найдено")
    return 0


if __name__ == "__main__":
    sys.exit(main())
