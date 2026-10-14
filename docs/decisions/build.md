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

## <a id="kompasroot"></a>Машинный путь к КОМПАС: `Directory.Build.props.local` (14.10.2026)

**Ловушка.** Комментарий в `Directory.Build.props` предлагал переопределять `KompasRoot` через
`Directory.Build.props.local`, но MSBuild по имени импортирует только `Directory.Build.props`.
Файл `.local` никто не подключал: на машине, где КОМПАС установлен не в `D:\Programs\KOMPAS-3Dv24`,
созданный по совету файл ничего не менял, и сборка адаптера падала с «Не найден
Interop.Kompas6API5.dll» без подсказки, почему переопределение не сработало.

**Решение.**

- В конец `Directory.Build.props` добавлен `<Import Project="Directory.Build.props.local"
  Condition="Exists(...)" />`. Импорт последний, чтобы машинные значения побеждали значения по
  умолчанию.
- `KompasInteropDir` и `KompasTlbDir` выводятся из `KompasRoot` раньше импорта, поэтому в `.local`
  задаются все три свойства (образец - `docs/operator-guide/build.md`).
- `Directory.Build.props.local` внесён в `.gitignore`: он содержит путь конкретной машины.

**Проверено 14.10.2026** на машине с КОМПАС в `C:\Program Files\ASCON\KOMPAS-3D v24` (наряд
`NEW_MACHINE_ENV`): сборка Debug и Release без предупреждений и ошибок; `dotnet test` - Release
1132/1132 PASS, Debug 1131/1132 (единственный отказ - плавающий
`FileLockOwnerLookupTests.AProbeThatFaults_IsNamedNotThrown`, в одиночном прогоне 3/3 PASS; техдолг 24).
Утверждение о трёх свойствах проверено сборкой: с ОДНИМ `KompasRoot` в `.local` сборка падает и
называет старый путь `D:\Programs\KOMPAS-3Dv24\Libs\PolynomLib\Bin\Client`; без файла `.local` - то же
(условие `Exists`).

**Поправка 14.10.2026 (наряд `INSTALL_ROOT_TOOLS`).** Прежняя редакция этого абзаца говорила, что
прибор приёмки свойства не читает. Теперь порядок один для всех приборов: `KOMPAS_MCP_INSTALL`,
затем `KompasRoot` из `Directory.Build.props.local` (разбор XML, без MSBuild), затем прежнее
значение по умолчанию `D:/Programs/KOMPAS-3Dv24`. Общий помощник - `scripts/kompas_install.py`
(Python) и `scripts/kompas-install.ps1` (PowerShell, тот же порядок своим кодом). Каждый прибор
печатает корень и его источник, поэтому неверный корень виден в выводе.

**Почему поправка понадобилась.** Приборы аудита и выпуска зашили путь `D:\Programs\KOMPAS-3Dv24`,
и на машине, где КОМПАС стоит в `C:\Program Files\ASCON\KOMPAS-3D v24`, `python scripts/api-audit.py
--self-test` давал 1 FAIL из 5 (`api-audit-routes.py`, `InteropScan LOAD_FAILED`), а отказы
приёмки (`DEP.DPU.*`, `INT.13`) читались как дефекты продукта, хотя продукт исправен. После
поправки `--self-test` аудита - 5/5.

**Границы поправки.** `src/` и `tools/KompasMcp.Api7Probe/InteropResolver.cs` не тронуты: там
каталог interop сначала берётся из регистрации COM, а зашитый путь - лишь последний запасной
вариант. Порядок и способ проверки - `docs/operator-guide/build.md`.
