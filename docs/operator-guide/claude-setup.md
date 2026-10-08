# Подключение KompasMCP к Claude по ссылке на репозиторий

Поручение, которое можно скопировать в Claude Code (вкладка **Code** приложения Claude, CLI или
расширение IDE) вместе со ссылкой `https://github.com/SergeySklyarov/Kompas3D-MCP`:

> Установи и подключи последнюю принятую версию KompasMCP из этого репозитория на этой
> Windows-машине по инструкции docs/operator-guide/claude-setup.md; проверь зависимости, checksum
> и доступность инструментов; мои модели не изменяй.

Ставит и подключает сервер Claude Code: ему нужны команды PowerShell, поэтому обычный чат без
вкладки Code сделать это сам не может (ручной путь для него - в конце). Собирать сервер не нужно,
ставится готовый пакет из GitHub Releases.

## Что нужно на машине

- Windows x64.
- Установленный и лицензированный **КОМПАС-3D v24 x64**, хотя бы раз запущенный этим пользователем.
- **.NET 10 Runtime x64** (или .NET 10 Desktop Runtime x64). .NET SDK не нужен.
  https://dotnet.microsoft.com/download/dotnet/10.0
- Claude Code: вкладка Code в приложении Claude, CLI `claude` или расширение IDE.

## Шаг 1. Пакет: скачать, проверить, распаковать, настроить

Тот же установщик, что и для Codex, только без регистрации в Codex. Он проверяет зависимости,
выбирает последний стабильный выпуск, сверяет SHA-256 до распаковки, кладёт пакет в
`%LOCALAPPDATA%\KompasMCP\versions\<тег>`, создаёт конфиг и делает самопроверку по MCP stdio:

```powershell
$repo = "SergeySklyarov/Kompas3D-MCP"
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
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\Install-KompasMcp.ps1"
```

Свои модели передайте установщику как `-ReadOnlyRoot "<папка с моделями>"`: сервер будет их читать,
но не писать. Подробности шагов, обновление и откат описаны в [codex-setup.md](codex-setup.md):
пакет и конфиг у обоих клиентов общие.

В конце установщик печатает путь к Host и к конфигу. Дальше они нужны как есть, абсолютными:

- Host: `<LOCALAPPDATA>\KompasMCP\versions\<тег>\KompasMcp.Host.exe`
- конфиг: `<LOCALAPPDATA>\KompasMCP\config\kompas-mcp.json`

## Шаг 2. Регистрация в Claude Code

Сначала проверить, нет ли уже сервера с этим именем: `claude mcp get kompas`. Если он есть и ведёт
на другую сборку, не затирать молча: сохранить вывод команды для отката и спросить пользователя,
заменить его или взять другое имя (`kompas-release`).

Регистрация на уровне пользователя (сервер доступен во всех проектах):

```powershell
claude mcp add --transport stdio --scope user kompas -- "<LOCALAPPDATA>\KompasMCP\versions\<тег>\KompasMcp.Host.exe" --config "<LOCALAPPDATA>\KompasMCP\config\kompas-mcp.json"
```

Вместо `<LOCALAPPDATA>` подставьте значение `$env:LOCALAPPDATA`. Всё после `--` уходит серверу без
изменений. Обёртка `cmd /c` для `.exe` не нужна. Запись ложится в `~/.claude.json`, остальные серверы
не трогаются. Замена существующей записи: `claude mcp remove kompas -s user`, затем `add` заново.

Если команды `claude` нет в `PATH` (Claude Code из приложения Claude), используйте ту, что лежит в
каталоге приложения, или попросите Claude Code выполнить регистрацию самому: он знает, где его CLI.

## Шаг 3. Проверка подключения

1. `claude mcp get kompas` должен показать `Status: ✔ Connected`: Claude Code запустил Host и
   прошёл рукопожатие MCP.
2. Начните новую сессию Claude Code (в приложении новый чат во вкладке Code). Команда `/mcp`
   покажет сервер `kompas`.
3. Попросите вызвать `kompas_health` и `kompas_capabilities`: должно прийти 81 инструмент.
   КОМПАС для этого не запускается, он поднимется позже первым `kompas_connect`.

Claude Code спрашивает разрешение на вызов инструментов MCP. Это ваше решение в его интерфейсе;
установщик правила разрешений не меняет.

Таймауты: долгую операцию сервер сам переводит в `status: running` после `sync_budget_ms`, и повтор
того же вызова с тем же `operation_id` работает как опрос. Если первый `kompas_connect` не успевает,
увеличьте `MCP_TOOL_TIMEOUT` (миллисекунды) в окружении Claude Code.

## Обычный чат Claude Desktop (без вкладки Code)

Чат сам ставить пакет не умеет. Установите пакет по шагу 1 в PowerShell, затем в Claude Desktop
откройте Settings → Developer → Edit Config. Добавьте сервер в `mcpServers` файла
`claude_desktop_config.json`, не удаляя остальные, и перезапустите приложение:

```json
{
  "mcpServers": {
    "kompas": {
      "command": "<LOCALAPPDATA>\\KompasMCP\\versions\\<тег>\\KompasMcp.Host.exe",
      "args": ["--config", "<LOCALAPPDATA>\\KompasMCP\\config\\kompas-mcp.json"]
    }
  }
}
```

## Типовые ошибки

Те же, что для Codex: [codex-setup.md, раздел «Типовые ошибки»](codex-setup.md#типовые-ошибки).
Отдельно для Claude Code: `✘ Failed to connect` в `claude mcp get` означает, что Host не запустился.
Запустите ту же команду из PowerShell: ошибка конфигурации даёт код 64 и текст в stderr.

## Что эта инструкция не обещает

- Контрольная сумма проверяет целостность скачанного файла, а не личность издателя: цифровой подписи
  у выпуска нет.
- Установщик не ставит КОМПАС, .NET и Claude и не меняет правила разрешений Claude Code.

Источник по формату Claude Code: https://code.claude.com/docs/en/mcp
