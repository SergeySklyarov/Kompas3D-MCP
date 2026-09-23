# Что здесь лежит

## Актуальный план и поручение от 12.09.2026

- [План практического выпуска](../coverage/solid-v24/implementation-plan.md): выбранные режимы 14 семейств, общие зависимости, проверки и отложенные возможности.
- `P6_PRACTICAL_MODELING_DEVELOPER_PROMPT.md`: начать реализацию по новому приоритету, не повторяя завершённые исследования. Задание наряда; в публичный набор не входит.

Это изменение плана; само по себе оно не повышает статусы возможностей в каталоге или матрице.

Первичный вход — [README.md](../README.md) (ТЗ заказчика), затем `DEVELOPER_HANDOFF.md`.
Этот файл — указатель на **результаты работ**, чтобы не искать их по дереву.

## Состояние и доказательства

Доказательства наряда — `docs/acceptance/` и сырые выгрузки — в публичный набор не входят (см.
[KOMPAS3D_MCP.md](../KOMPAS3D_MCP.md), раздел «Что намеренно не входит в публичный репозиторий»).
Строки таблицы, которые на них указывают, оставлены **названиями без ссылок**: адрес назван, чтобы
запись читалась, но открывать его в публичном репозитории нечем.

| Документ | Что это |
|---|---|
| [docs/STATUS.md](STATUS.md) | что реально работает и проверено, что реализовано без проверки, чего нет |
| `docs/acceptance/INDEX.md` | реестр приёмочных прогонов PASS/FAIL/SKIPPED/BLOCKED — доказательство наряда, не публикуется |
| `docs/acceptance/p0/p0-probe-report.md` | P0: отчёт технического исследования (генерируется) — не публикуется |
| `docs/acceptance/p2/p2-probe-report.md` | P2: отчёт измерения COM-маршрутов на живом КОМПАС (генерируется) — не публикуется |
| [../coverage/solid-v24/catalog.json](../coverage/solid-v24/catalog.json) | P6.0: каталог операций, режимов и источников (машинный, первоисточник) |
| [../coverage/solid-v24/matrix.md](../coverage/solid-v24/matrix.md) | P6.0: матрица поддержки — порождена из `matrix.json`, вручную не правится |
| [docs/adr/ADR-001-runtime-com-session.md](adr/ADR-001-runtime-com-session.md) | runtime, разрядность, COM-адаптер, привязка сеанса |
| [docs/adr/ADR-002-p2-feature-tools.md](adr/ADR-002-p2-feature-tools.md) | группировка инструментов P2, маршрут правки признака, граница между эталоном G02 и семейством SM-07 |
| [docs/adr/ADR-003-api7-research-probe.md](adr/ADR-003-api7-research-probe.md) | рамка изолированной пробы API7: область исключения, граница «один владелец сеанса и одна STA-очередь», программа и критерии |
| [docs/compatibility/kompas-api5-metadata.json](compatibility/kompas-api5-metadata.json) | выгрузка сигнатур и enum-ов API5 с машины заказчика |
| [docs/research/api5-semantics-from-tlb.md](research/api5-semantics-from-tlb.md) | отфильтрованная семантика API с указанием, что подтверждено прогоном |
| `docs/research/api5-semantics-agent-report.raw.md` | сырой разбор TLB/SDK: входной материал, не подтверждённый факт — в публичный набор не входит |
| [docs/operator-guide/](operator-guide/) | сборка, установка, конфигурация MCP, диагностика, откат |

## Код

```
src/KompasMcp.Contracts/   публичные DTO, конверт, коды ошибок, кадры IPC, строители схем
src/KompasMcp.Domain/      валидация по JSON Schema, трансформации, пути, журнал, очередь, единицы
src/KompasMcp.Api5Adapter/ единственный проект основного кода, знающий про COM (рамка пробы API7 — ADR-003)
src/KompasMcp.Worker/      STA + message pump, очередь CAD-команд, именованный канал
src/KompasMcp.Host/        MCP stdio, каталог инструментов, validation, journal, queue
tools/KompasMcp.P0Probe/   исследование на живом КОМПАС: --suite p0 и --suite p2
tests/Unit/                тесты без КОМПАС; число даёт `dotnet test tests\Unit\KompasMcp.Unit`
schemas/                   JSON Schema инструментов (генерируются из кода; список сверяется `tools/list`)
```

## Как проверить самой машиной

```powershell
dotnet build KompasMcp.sln -c Release
dotnet test tests\Unit\KompasMcp.Unit -c Debug
python scripts\lint-cjk.py                                  # текст без случайных иероглифов
dotnet run --project tools\KompasMcp.P0Probe -- --mode launch   # P0 (поднимет свой невидимый КОМПАС)
python scripts\mcp-smoke.py                                 # вертикаль через настоящий MCP
```

`P0Probe` и `mcp-smoke` пишут в `scratch/` и `docs/acceptance/p0/`; исходники моделей они не
открывают — корней записи в конфигурации для этого хватает.
