# Сборка - решения и измерения

## <a id="single-platform"></a>Одна платформа x64 для сборки и тестов (06.10.2026)

**Ловушка.** `KompasMcp.sln` объявляет только платформу `x64`, поэтому сборка решения пишет
выходы в `bin\x64\<Configuration>\`. Но сборка или тест отдельного проекта
(`dotnet test tests/Unit/KompasMcp.Unit --no-build`) не читает карту платформ решения, и
`$(Platform)` по умолчанию становился `AnyCPU`. Выходной путь SDK для `AnyCPU` - `bin\<Configuration>\`,
без платформы. В результате `dotnet test --no-build` запускал `KompasMcp.Unit.dll` от 11.09.2026
(48 КБ) из `bin\Debug\net10.0-windows`, а не свежую сборку из `bin\x64\Debug\net10.0-windows`
и отчитывался зелёным по старому коду. Без `--no-build` тот же вызов
падал на страже Host: `KompasMcp.Worker output not found at bin\AnyCPU\Debug\...`.

Удаление `AnyCPU` из `<Platforms>` (сентябрь 2026) эту ловушку не закрыло: `<Platforms>`
ограничивает только выбор в IDE и решении, а значение `$(Platform)` по умолчанию SDK задаёт
независимо.

**Решение.**

- `Directory.Build.props`: `<Platform Condition="'$(Platform)' == ''">x64</Platform>`. Файл
  импортируется раньше `Microsoft.NET.DefaultOutputPaths.targets`, поэтому SDK берёт `x64` и
  строит путь `bin\x64\<Configuration>\` и для решения, и для отдельного проекта.
- `Directory.Build.targets`: цель `KompasMcpRequireX64Platform` перед `Build` и `VSTest` падает,
  если платформа не `x64` (явный `-p:Platform=AnyCPU` - глобальное свойство, его props не
  переопределяет). `VSTest` нужен отдельно: `dotnet test --no-build` не выполняет `Build`.
- Старые папки `bin\Debug`, `bin\Release`, `obj\Debug`, `obj\Release` без `x64` удалены из
  `src`, `tools`, `tests`.

**Проверено 06.10.2026.** После `dotnet build KompasMcp.sln -c Debug`:
`dotnet test tests/Unit/KompasMcp.Unit -c Debug --no-build` и
`dotnet test KompasMcp.sln -c Debug --no-build` оба запускают
`bin\x64\Debug\net10.0-windows\KompasMcp.Unit.dll`, 589/589 PASS; `dotnet test` проекта со сборкой
без `-p:Platform` - 589/589 PASS; с `-p:Platform=AnyCPU` - ошибка стража.

Правило для разработчиков - в `AGENTS.md`, раздел «Сборка и тесты».
