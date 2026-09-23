#!/usr/bin/env python3
"""Пересчитать числа приёмки ИЗ МАССИВА `rows` отчёта, а не из консольного потока.

Зачем отдельный скрипт. Отчёт приёмки — это JSON с массивом `rows`, и только он является
доказательством: консольный поток конвейера (`| tail`) отдаёт код возврата ХВОСТА, а не прибора, и
число «строк PASS», прочитанное из прокрутки экрана, ничем не подтверждается. Проектное правило —
«число берётся из массива `rows` отчёта» — выполняется только тогда, когда пересчёт делается
машиной по файлу.

Печатается: имя отчёта, число строк, распределение вердиктов и — отдельным блоком — ИМЕНА строк,
которые НЕ PASS (их и надо разбирать). Порядок и группировка по префиксу идентификатора помогают
увидеть, что удаление строки требования не унесло чужие проверки с тем же числовым префиксом.

Запуск:
    python scripts/acceptance-numbers.py                      # отчёты B3/B3L/B3M/B3C из scratch
    python scripts/acceptance-numbers.py <report.json> ...     # конкретные файлы
"""
from __future__ import annotations

import collections
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Значения по умолчанию называют ПОСЛЕДНЮЮ фактически проверенную поставку, а не каталог
# `scratch/mcp-smoke/` верхнего уровня: там лежат отчёты прежних поставок, и молчаливый пересчёт по
# ним публиковал бы числа другой сборки (измерено 20.09.2026: `b3m-acceptance.json` верхнего уровня
# давал 4 FAIL эпохи дефекта знака на полюсе при нуле отказов на текущей поставке).
DEFAULT_REPORTS = (
    "scratch/mcp-smoke/delivery-20260920-apifix/b3.json",
    "scratch/mcp-smoke/delivery-20260920-apifix/b3l.json",
    "scratch/mcp-smoke/delivery-20260920-apifix/b3m.json",
    "scratch/mcp-smoke/delivery-20260920-apifix/b3c.json",
)


def rows_of(path: Path) -> list[dict]:
    # `utf-8-sig`: отчёты пишутся с BOM (проектное соглашение), обычный `utf-8` оставляет BOM в
    # первой строке и `json.loads` падает на «Unexpected UTF-8 BOM».
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    rows = data.get("rows")
    if not isinstance(rows, list):
        raise SystemExit(f"{path}: в отчёте нет массива `rows` — это не отчёт приёмки")
    return rows


def main(argv: list[str]) -> int:
    names = argv[1:] or list(DEFAULT_REPORTS)
    total_rows = 0
    total_verdicts: collections.Counter = collections.Counter()
    exit_code = 0
    for name in names:
        path = Path(name) if Path(name).is_absolute() else ROOT / name
        if not path.exists():
            print(f"{name}: отчёта нет — прогон не выполнялся, числа взять неоткуда")
            continue
        rows = rows_of(path)
        verdicts = collections.Counter(str(r.get("verdict")) for r in rows)
        by_prefix: collections.Counter = collections.Counter()
        for row in rows:
            by_prefix[re.split(r"[.\d]", str(row.get("id")))[0]] += 1
        total_rows += len(rows)
        total_verdicts.update(verdicts)
        print(f"{name}: строк {len(rows)} · " + " · ".join(
            f"{k} {v}" for k, v in sorted(verdicts.items())))
        print("   по префиксам: " + ", ".join(
            f"{k or '—'}={v}" for k, v in sorted(by_prefix.items())))
        bad = [r for r in rows if str(r.get("verdict")) not in ("PASS", "N/A")]
        for row in bad:
            print(f"   НЕ PASS: {row.get('id')} [{row.get('verdict')}] "
                  f"{str(row.get('description'))[:100]}")
        if bad:
            exit_code = 1
    print("ИТОГО: строк " + str(total_rows) + " · " + " · ".join(
        f"{k} {v}" for k, v in sorted(total_verdicts.items())))
    return exit_code


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    raise SystemExit(main(sys.argv))
