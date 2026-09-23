# Сборка

Требования: Windows x64, .NET SDK 10 (`global.json` закрепляет 10.0.401 с
`rollForward: latestFeature`), установленный КОМПАС-3D v24 x64.

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

Если файл не найден, сборка `KompasMcp.Api5Adapter` падает с внятным сообщением. Адаптер
**не** собирается в заглушку: молча неработающий COM-слой хуже отсутствующего. Остальные проекты
(`Contracts`, `Domain`, `Host`, unit-тесты) КОМПАС не требуют и собираются без него.

То же самое работает и во время исполнения: `KompasInteropResolver` ищет сборки в порядке
`KOMPAS_MCP_INTEROP_DIR` → каталог из регистрации COM → исторически наблюдавшийся путь. Найденный
каталог показывается в ответе `kompas_capabilities`.

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
  расходились с «объявленными» идентификаторами. Ошибка срабатывает в **обе** стороны — и на
  лишний RID в lock-файле, и на недостающий.
* `RuntimeIdentifier` (единственное число) действительно не задан, потому что он заставляет
  собирать проект под конкретный RID всегда, а это и ломает `dotnet test`.

Так что читатель, увидевший в `Directory.Build.props` строку `RuntimeIdentifiers`, не должен
считать эту страницу устаревшей: она говорит про другое свойство. Обновлять lock-файлы после
правки объявления — `dotnet restore <проект> -r win-x64 --force-evaluate`: обычный restore, даже
с `--force-evaluate`, ключ RID в lock-файл **не добавляет**.

## Пакеты NuGet

Версии зафиксированы централизованно в `Directory.Packages.props` (Central Package Management),
`packages.lock.json` на проект, источник — только nuget.org (`NuGet.config` с `<clear/>`).
Обновление — правкой одного файла, не «latest» в csproj.
