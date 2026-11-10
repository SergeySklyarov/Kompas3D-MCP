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

## <a id="analyzers"></a>Анализатор `SonarAnalyzer.CSharp`: подключён, лицензия принята заказчиком (10.11.2026)

**Лицензия.** Пакет `SonarAnalyzer.CSharp` `10.35.0.4138`; `.nuspec` объявляет
`developmentDependency=true` и `license type="file">licenses\LICENSE.txt`. Имя лицензии - **SONAR
Source-Available License v1.0** (редакция 13.11.2024). Раздел 1 определяет разрешённую цель
«Non-competitive Purpose» и исключает случай (c): «employing, using, or engaging artificial
intelligence technology that is not part of the Program to ingest, interpret, analyze, train on, or
interact with the data provided by the Program, or to engage with the Program in any manner»;
раздел 2 выдаёт права только «for any Non-competitive Purpose». Замер и разбор предупреждений
выполняет ИИ-исполнитель, то есть ровно (c).

**Решение.** Заказчик в живой сессии **10.11.2026** принял SSAL v1.0 как есть: **ответственность за
условие (c) - на заказчике**, правовую оценку даёт он. Этим решением пакет включается, замер и разбор
выполняются; повторно лицензия здесь не пересказывается. Прежнее состояние (пакет не подключён,
`blocked` по §0) - `docs/STATUS.md`, запись 09.11.2026.

**Как подключено.** `Directory.Packages.props` держит версию одной строкой `PackageVersion`
(центральное управление версиями, версия в проекте не пишется). `Directory.Build.props` добавляет
`PackageReference Include="SonarAnalyzer.CSharp" PrivateAssets="all"` под условием-свойством
`EnableSonarAnalyzer`; по умолчанию свойство включается только для проектов, чей путь лежит под
`src/` или `tests/`, поэтому `tools/` и пробники анализатор не запускают. `-p:EnableSonarAnalyzer=false`
выключает его на одну сборку.

- `PrivateAssets="all"` держит пакет вне графа потребителей и вне поставки: `dotnet publish` Хоста
  дал 44 файла, ни одной сборки анализатора в них нет; `packages.lock.json` шести проектов несёт
  `SonarAnalyzer.CSharp` как прямую зависимость.
- `packages.lock.json` проекта тестов при перегенерации заодно получил версии проектных зависимостей
  `0.13.0` вместо `0.12.0` - замок был записан до подъёма `<Version>`; иначе повторный `restore`
  переписывал бы его каждый раз.

**Предупреждения не становятся ошибками.** В Release действует `TreatWarningsAsErrors=true`, поэтому
`WarningsNotAsErrors` перечисляет все ОСТАВЛЕННЫЕ правила. MEASURED: шаблон `S*` Roslyn здесь не
понимает - с ним `S1481` остаётся ошибкой, а точный id в том же списке делает её предупреждением.
Перечень поэтому именной и дополняется вручную при включении правила.

**Замер** (`dotnet build KompasMcp.sln -c Release --no-incremental -p:TreatWarningsAsErrors=false`,
09-10.11.2026): **483 предупреждения, 34 правила**; по проектам: Api5Adapter 253, Unit 110, Host 58,
Domain 43, Worker 13, Contracts 6.

**Оставлены включёнными - находки исправлены** (правило, находок, что это было): `S8969` 131 (лишний
оператор `!`, который компилятор уже не требует; удалён один символ в каждом месте), `S1481` 27
(мёртвые локальные, в том числе лишние COM-чтения), `S1172` 23 (мёртвые параметры), `S1144` 12
(мёртвые члены), `S1905` 5 (лишнее приведение), `S108` 2 (пустые блоки), `S1155` 2 (`Count(...) == 0`),
`S1854`, `S3604`, `S4487`, `S6580`, `S101`, `S2365` - по одному. Каждая находка прочитана в коде;
подробности - в отчёте наряда, попытка 2.

**Выключены - причина на строку в `.globalconfig`** (в скобках - находок): `S3358` (91, вложенная
тернарная операция у поля, которое она заполняет), `S2325` (77, `static` меняет поверхность вызова
без поведения), `S3267` (24, обход коллекции КОМПАС с фильтром и чтением в одном теле), `S6966` (23,
`*Async` сделал бы синхронный путь асинхронным), `S1125` (11, `!= true` у nullable COM-bool - это и
есть смысл), `S3218` (5, намеренное имя вложенного типа), `S1244` (5, сравниваются ЦЕЛЫЕ счётчики в
`double` и результат `Math.Clamp`), `S1075` (4, префикс JSON-pointer и адрес ленты выпусков),
`S4136` (3, только порядок), `S6444` (3, собственные литеральные шаблоны на коротком входе),
`S127` (3, `args[++i]` - разбор аргумента), `S8949` (2, токен уже передаётся в делегат; второго
перегруза `Register` нет), `S1066` (2, у условий разные отказы), `S3869` (2, `WaitForMultipleObjects`
нужен сырой массив дескрипторов), `S3398` (1, только порядок), `S1994` (1, предел повтора внутри
`SavedFileWrite.MayRepeat`), `S1199` (1, блок - это ветка разбора аргументов), `S1210` (1, операторы
были бы неиспользуемым API), `S4790` (1, SHA-1 выводит UUIDv5 по RFC 4122).

**Тесты.** `tests/Unit/KompasMcp.Unit/.editorconfig` выключает для проекта тестов `S3881` (шаблон
`IDisposable` у запечатанных двойников), `S2925` (`Thread.Sleep`, которым проверяется предел) и
`S1172` (параметры заданы интерфейсом двойника). Глобальный файл не может адресовать подкаталог:
`[tests/**.cs]` в `.globalconfig` требует абсолютного пути, и Roslyn такой раздел игнорирует с
предупреждением `InvalidGlobalSectionName` (измерено).

**Как запускать.** Анализатор работает в каждой обычной сборке `src/` и `tests/`; замер по правилам:

```
dotnet build KompasMcp.sln -c Release --no-incremental -p:TreatWarningsAsErrors=false -v m
```

Сырой вывод замера - в `scratch/`, в репозиторий не копируется. Состояние после наряда - сборка
Release без предупреждений: 0 предупреждений, 0 ошибок.
