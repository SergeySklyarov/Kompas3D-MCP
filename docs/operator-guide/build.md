# Сборка

Требования: Windows x64, .NET SDK 10 (`global.json` закрепляет 10.0.401 с
`rollForward: latestFeature`), установленный КОМПАС-3D v24 x64, Windows SDK.

**Windows SDK нужен не для сборки, а для тестов.** Модульный тест
`ComDisconnectClassificationTests.EveryCodeTheClassifierRecognisesMatchesTheWindowsSdkHeader`
сверяет распознаваемые коды обрыва COM с `winerror.h` и на отсутствие заголовка ОТКАЗЫВАЕТ -
намеренно: сторож, который молча ничего не делает, неотличим от прошедшего. Ставится отдельно
(`Microsoft.WindowsSDK.10.0.18362` или новее); ищется в
`<Program Files (x86)>\Windows Kits\10\Include\<версия>\shared\winerror.h`.

## Проверить окружение

```powershell
dotnet --version                       # 10.0.4xx
Get-Process KOMPAS -ErrorAction SilentlyContinue   # сколько экземпляров уже висит
```

Если КОМПАС запущен «осиротевшими» скриптами процессами без окна, они не мешают сборке, но
мешают тестам attach: см. `docs/adr/ADR-001-runtime-com-session.md` §3.

## Где берётся Interop.Kompas6API5

Сборка адаптера ссылается на вендорские interop-сборки **по пути, не копируя их**
(`Private=false` в `build/KompasInterop.props`). Поэтому для компиляции нужен доступ к каталогу
установки КОМПАС:

```powershell
dotnet build KompasMcp.sln -p:KompasRoot=D:\Programs\KOMPAS-3Dv24
```

Путь к каталогу с `Interop.Kompas6API5.dll` выводится из `KompasRoot`
(`Libs\PolynomLib\Bin\Client` по умолчанию) и переопределяется отдельно:

```powershell
dotnet build -p:KompasInteropDir="D:\Programs\KOMPAS-3Dv24\Libs\PolynomLib\Bin\Client"
```

### Задать путь один раз для машины

Передавать `-p:KompasRoot=` при каждой сборке не нужно: создайте в корне репозитория
`Directory.Build.props.local` (он не отслеживается git, см. `.gitignore`), и он подхватится сам.

**Задавать в нём нужно ТРИ свойства, а не одно.** `KompasInteropDir` и `KompasTlbDir` выводятся из
`KompasRoot` в `Directory.Build.props` с условием «если ещё не задано», и эта выводимость
вычисляется ДО импорта `.local`. Поэтому одного `KompasRoot` недостаточно: выведенные свойства уже
запомнили прежний корень, и сборка продолжит искать `Interop.Kompas6API5.dll` там, хотя в ошибке
будет стоять старый путь и увести это может далеко в сторону.

```xml
<Project>
  <PropertyGroup>
    <KompasRoot>C:\Program Files\ASCON\KOMPAS-3D v24</KompasRoot>
    <KompasInteropDir>C:\Program Files\ASCON\KOMPAS-3D v24\Libs\PolynomLib\Bin\Client</KompasInteropDir>
    <KompasTlbDir>C:\Program Files\ASCON\KOMPAS-3D v24\Bin</KompasTlbDir>
  </PropertyGroup>
</Project>
```

Если файл не найден, сборка `KompasMcp.Api5Adapter` падает с внятным сообщением. Адаптер
**не** собирается в заглушку: молча неработающий COM-слой хуже отсутствующего. Остальные проекты
(`Contracts`, `Domain`, `Host`, unit-тесты) КОМПАС не требуют и собираются без него.

То же самое работает и во время исполнения: `KompasInteropResolver` ищет сборки в порядке
`KOMPAS_MCP_INTEROP_DIR` → каталог из регистрации COM → исторически наблюдавшийся путь. Найденный
каталог показывается в ответе `kompas_capabilities`.

## Путь к установке КОМПАС: один порядок для всех приборов

Сборка берёт путь к установке из `KompasRoot` (см. выше). Приборы берут его из ОДНОГО места:
`scripts/kompas_install.py` для Python и `scripts/kompas-install.ps1` для PowerShell (тот же
порядок своим кодом: импортировать Python из PowerShell нельзя). Порядок:

1. переменная окружения `KOMPAS_MCP_INSTALL`, если задана;
2. иначе `KompasRoot` из `Directory.Build.props.local` в корне репозитория (разбор XML, без
   MSBuild);
3. иначе прежнее значение по умолчанию `D:/Programs/KOMPAS-3Dv24`.

Производные пути (`Bin\KOMPAS.Exe`, `Bin\kAPI7.tlb`, `Libs\PolynomLib\Bin\Client`) считаются от
этого корня. Каждый прибор печатает корень и его источник строкой
`КОМПАС: <путь> (источник: <откуда>)`: неверный корень видно в выводе, а не только в отказе,
который выглядит как дефект продукта. На машине с `Directory.Build.props.local` отдельно задавать
`KOMPAS_MCP_INSTALL` не нужно - корень уже верен:

```powershell
python scripts/mcp-smoke.py --all          # корень взят из .local
$env:KOMPAS_MCP_INSTALL = "C:\Program Files\ASCON\KOMPAS-3D v24"
python scripts/mcp-smoke.py --all          # разовый прогон по явному пути
```

Какой корень видит прибор, видно и без прогона:

```powershell
python scripts/kompas_install.py           # печатает корень и его источник
```

Ключ нужен, когда надо отклониться от `.local` на один прогон. Если строки `DEP.DPU.*` или
`INT.13` отказывают по «нет файла» / «не найдена», а файл в установке есть, - сверяйтесь с
корнем, который прибор назвал в своём выводе.

## Варианты сборки

```powershell
dotnet build KompasMcp.sln -c Debug
dotnet build KompasMcp.sln -c Release          # Release TreatWarningsAsErrors=true
dotnet test tests\Unit\KompasMcp.Unit -c Debug  # deterministic, без КОМПАС
```

`RuntimeIdentifier` (в единственном числе) в `Directory.Build.props` не задан намеренно: он ломает
`dotnet test`/`dotnet pack` в графах проектов. Битность фиксируется `PlatformTarget=x64`, а RID
передаётся при публикации (`scripts/publish.ps1`).

**Не путать с `RuntimeIdentifiers` (во множественном числе), который там ЕСТЬ.** Это разные
свойства с разным действием, и различие измерено 23.09.2026:

* `RuntimeIdentifiers` (список разрешённых RID для restore) объявлен как `win-x64`. Без него
  `CI=true dotnet restore` отказывал с **NU1004**: lock-файлы проектов, восстановленные под RID,
  расходились с «объявленными» идентификаторами. Ошибка срабатывает в **обе** стороны - и на
  лишний RID в lock-файле, и на недостающий.
* `RuntimeIdentifier` (единственное число) действительно не задан, потому что он заставляет
  собирать проект под конкретный RID всегда, а это и ломает `dotnet test`.

Так что читатель, увидевший в `Directory.Build.props` строку `RuntimeIdentifiers`, не должен
считать эту страницу устаревшей: она говорит про другое свойство. Обновлять lock-файлы после
правки объявления - `dotnet restore <проект> -r win-x64 --force-evaluate`: обычный restore, даже
с `--force-evaluate`, ключ RID в lock-файл **не добавляет**.

## Пакеты NuGet

Версии зафиксированы централизованно в `Directory.Packages.props` (Central Package Management),
`packages.lock.json` на проект, источник - только nuget.org (`NuGet.config` с `<clear/>`).
Обновление - правкой одного файла, не «latest» в csproj.
