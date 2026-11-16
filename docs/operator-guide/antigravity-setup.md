# Подключение KompasMCP к Google Antigravity по ссылке на репозиторий

Поручение, которое можно скопировать агенту Antigravity вместе со ссылкой на репозиторий:

> Установи и подключи последнюю принятую версию KompasMCP из этого репозитория на этой
> Windows-машине; проверь зависимости, checksum и доступность инструментов; мои модели не изменяй.

Ниже то, что агент (или человек) делает по этому поручению. Собирать сервер не нужно: ставится
готовый пакет из GitHub Releases. Клонировать репозиторий тоже не требуется.

## Что нужно на машине

- Windows x64.
- Установленный и лицензированный **КОМПАС-3D v24 x64** (или v25 при запуске с флагом `-SkipKompasVersionCheck`),
  хотя бы раз запущенный от имени этого пользователя (для регистрации в COM). Установщик КОМПАС не ставит
  и реестр не меняет.
- **.NET 10 Runtime x64** (`Microsoft.NETCore.App 10.x`). Подходит и .NET 10 Desktop Runtime x64,
  он включает этот runtime. Пакет не self-contained, поэтому runtime обязателен. **.NET SDK не
  нужен.** Установка через winget:
  ```powershell
  winget install Microsoft.DotNet.DesktopRuntime.10
  ```
  Или скачать вручную: https://dotnet.microsoft.com/download/dotnet/10.0
- Google Antigravity: Antigravity IDE, CLI (`agy`) или Antigravity 2.0. Они читают общую конфигурацию
  `~/.gemini/config/mcp_config.json` (путь `%USERPROFILE%\.gemini\config\mcp_config.json`).

## Короткий путь: установщик из выпуска

Установщик `Install-KompasMcp.ps1` публикуется в каждом выпуске рядом с ZIP. Он скачивается вместе с
`SHA256SUMS.txt`, сверяется по контрольной сумме и запускается:

```powershell
$repo = "SergeySklyarov/Kompas3D-MCP" # для форка укажите "Diesel-x/Kompas3D-MCP"
$rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ "User-Agent" = "KompasMCP" }
if ($rel.draft -or $rel.prerelease) { throw "это не стабильный выпуск" }
$dir = Join-Path $env:TEMP "KompasMCP-$($rel.tag_name)"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
foreach ($name in "SHA256SUMS.txt", "Install-KompasMcp.ps1") {
    $asset = $rel.assets | Where-Object { $_.name -eq $name }
    Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $dir $name) -UseBasicParsing
}
$want = ((Get-Content "$dir\SHA256SUMS.txt") -match "  Install-KompasMcp.ps1$").Split(" ")[0]
$got = (Get-FileHash "$dir\Install-KompasMcp.ps1" -Algorithm SHA256).Hash.ToLower()
if ($want -ne $got) { throw "контрольная сумма установщика не совпала" }
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\Install-KompasMcp.ps1" -RegisterAntigravity
```

Если на машине установлен КОМПАС-3D v25 (вместо проверенного v24), добавьте флаг `-SkipKompasVersionCheck`:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\Install-KompasMcp.ps1" -RegisterAntigravity -SkipKompasVersionCheck
```

Свои папки с моделями можно передать установщику через `-ReadOnlyRoot "<папка с моделями>"`: сервер будет их читать,
но не писать.

Что делает установщик:
1. **Зависимости.** Проверяет Windows x64, регистрацию КОМПАС-3D COM, `Interop.Kompas6API5.dll` и наличие .NET 10 Runtime x64.
2. **Выпуск.** Получает последний стабильный релиз (или указанный в `-Tag`).
3. **Контрольные суммы.** Сверяет хеши ZIP и манифеста до распаковки.
4. **Конфигурация.** Создает рабочий конфиг `%LOCALAPPDATA%\KompasMCP\config\kompas-mcp.json`.
5. **Самопроверка.** Проверяет stdio-рукопожатие с `KompasMcp.Host.exe`.
6. **Регистрация в Antigravity.** Записывает секцию `kompas` в `%USERPROFILE%\.gemini\config\mcp_config.json`,
   сохраняя резервную копию `mcp_config.json.kompasmcp-<время>.bak`.

## Ручной путь

Те же шаги без автоматического установщика:

1. Скачать из выпуска `KompasMCP-<тег>-win-x64.zip`, `release-manifest.json` и `SHA256SUMS.txt`.
2. Сверить контрольную сумму SHA-256 скачанного ZIP-архива.
3. Распаковать в `%LOCALAPPDATA%\KompasMCP\versions\<тег>`.
4. Создать точку соединения (junction) `%LOCALAPPDATA%\KompasMCP\current`, указывающую на эту версию:
   ```powershell
   New-Item -ItemType Junction -Path "$env:LOCALAPPDATA\KompasMCP\current" -Target "$env:LOCALAPPDATA\KompasMCP\versions\<тег>"
   ```
5. Скопировать шаблон конфигурации в `%LOCALAPPDATA%\KompasMCP\config\kompas-mcp.json`.
6. Зарегистрировать сервер в Antigravity одним из способов ниже.

### Способ A: через CLI agy

```powershell
agy mcp add kompas "$env:LOCALAPPDATA\KompasMCP\current\KompasMcp.Host.exe" -- --config "$env:LOCALAPPDATA\KompasMCP\config\kompas-mcp.json"
```

### Способ B: через файл mcp_config.json

В файле `%USERPROFILE%\.gemini\config\mcp_config.json` добавить секцию:

```json
{
  "mcpServers": {
    "kompas": {
      "command": "C:\\Users\\<User>\\AppData\\Local\\KompasMCP\\current\\KompasMcp.Host.exe",
      "args": [
        "--config",
        "C:\\Users\\<User>\\AppData\\Local\\KompasMCP\\config\\kompas-mcp.json"
      ],
      "disabled": false
    }
  }
}
```

(Замените `<User>` на имя вашего пользователя Windows, либо укажите точный путь).

## Проверка подключения

1. В CLI `agy`:
   ```powershell
   agy mcp list
   ```
   Должен отображаться сервер `kompas` (stdio).
2. В интерфейсе Antigravity:
   Перейдите в **Дополнительные параметры (...) > MCP Servers** (или Settings > Skills & Customizations) - сервер `kompas` отобразится в списке активных.
3. В чате с агентом Antigravity:
   Начните новый диалог и попросите: «вызови kompas_health и kompas_capabilities». Сервер должен ответить версией Host и списком из 81 инструмента.
4. Сам КОМПАС-3D запустится автоматически только при первом обращении к `kompas_connect`.

## Обновление и откат

- **Обновление**: повторный запуск установщика без ключа `-Tag`. Новый релиз встает в `versions\<новый тег>`,
  а точка `current` переключается на него. Путь в `mcp_config.json` менять не нужно.
- **Откат**: `Install-KompasMcp.ps1 -UseInstalled -Tag <прежний тег> -RegisterAntigravity`.

## Типовые ошибки

| Симптом | Причина и решение |
|---|---|
| `КОМПАС найден, но версия...` | Если установлен КОМПАС v25 или Study, добавьте флаг `-SkipKompasVersionCheck`. |
| `.NET 10 Runtime x64 не найден` | Выполните `winget install Microsoft.DotNet.DesktopRuntime.10`. |
| `SHA-256 не совпал` | Файл поврежден при загрузке. Запустите скрипт повторно. |
| Сервер не появляется в списке | Проверьте наличие `%USERPROFILE%\.gemini\config\mcp_config.json` и правильность путей к `Host.exe`. Перезапустите Antigravity. |
| `PATH_NOT_ALLOWED` | Попытка записи вне разрешенных путей. Сохраняйте файлы в `sandbox` или укажите рабочую папку в `kompas-mcp.json`. |
