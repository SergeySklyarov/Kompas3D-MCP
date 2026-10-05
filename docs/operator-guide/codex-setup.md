# Подключение KompasMCP к Codex по ссылке на репозиторий

Поручение, которое можно скопировать агенту Codex вместе со ссылкой
`https://github.com/SergeySklyarov/Kompas3D-MCP`:

> Установи и подключи последнюю принятую версию KompasMCP из этого репозитория на этой
> Windows-машине; проверь зависимости, checksum и доступность инструментов; мои модели не изменяй.

Ниже то, что агент (или человек) делает по этому поручению. Собирать сервер не нужно: ставится
готовый пакет из GitHub Releases. Клонировать репозиторий тоже не нужно.

## Что нужно на машине

- Windows x64.
- Установленный и лицензированный **КОМПАС-3D v24 x64**, хотя бы раз запущенный от имени этого
  пользователя (так он регистрируется для COM). Установщик КОМПАС не ставит и реестр не меняет.
- **.NET 10 Runtime x64** (`Microsoft.NETCore.App 10.x`). Подходит и .NET 10 Desktop Runtime x64,
  он включает этот runtime. Пакет не self-contained, поэтому runtime обязателен. **.NET SDK не
  нужен.** Скачать: https://dotnet.microsoft.com/download/dotnet/10.0
- Codex: приложение, CLI или расширение IDE. Они читают одну и ту же конфигурацию
  `~/.codex/config.toml` (или `%CODEX_HOME%\config.toml`).

## Короткий путь: установщик из выпуска

Установщик `Install-KompasMcp.ps1` лежит в каждом выпуске рядом с ZIP. Он скачивается вместе с
`SHA256SUMS.txt`, сверяется по контрольной сумме и запускается как файл, а не через `iex`:

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
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\Install-KompasMcp.ps1" -RegisterCodex
```

`-ExecutionPolicy Bypass` действует только на этот запуск PowerShell и политику системы не
меняет.

Что делает установщик, по шагам (каждый шаг печатает `[OK]` или `[FAIL]`):

1. **Зависимости.** Windows x64; `HKCR\KOMPAS.Application.5` и `LocalServer32` указывают на
   существующий `KOMPAS.Exe` версии 24; `Interop.Kompas6API5.dll` в установке КОМПАС; .NET 10
   Runtime x64. Чего не хватает, называется конкретным шагом, и установка останавливается.
   Проверить только зависимости: `-CheckOnly`.
2. **Выпуск.** Последний стабильный выпуск из `releases/latest` (черновики и предварительные
   отвергаются) или явный `-Tag`. Выпуск называется тегом и объёмом из `release-manifest.json`.
3. **Контрольные суммы.** ZIP и манифест сверяются с `SHA256SUMS.txt` до распаковки. При
   несовпадении установка останавливается, и ничего не запускается. Пакет распаковывается в
   `%LOCALAPPDATA%\KompasMCP\versions\<тег>`, после чего собственные сборки сверяются с хешами
   манифеста. Уже существующий каталог версии не перезаписывается.
4. **Конфигурация.** `%LOCALAPPDATA%\KompasMCP\config\kompas-mcp.json` создаётся из шаблона пакета,
   только если его ещё нет. Запись разрешена в `Документы\KompasMCP\sandbox`, экспорт в
   `Документы\KompasMCP\export`; журнал и логи лежат в `%LOCALAPPDATA%\KompasMCP`. Папки со своими
   моделями передайте `-ReadOnlyRoot "<папка с моделями>"`: сервер будет их читать, но не писать. Запись на
   весь диск не открывается.
5. **Самопроверка.** Host из каталога версии запускается по MCP stdio: `initialize`, `tools/list`
   (число инструментов сверяется с манифестом), `kompas_health`, `kompas_session_status`,
   `kompas_capabilities`. КОМПАС при этом не запускается и модели не открываются.
6. **Регистрация в Codex** (`-RegisterCodex`). Перед записью делается копия
   `config.toml.kompasmcp-<время>.bak`. Добавляется только таблица `[mcp_servers.kompas]`, другие
   серверы не трогаются. Если запись `kompas` уже есть и ведёт не в эту установку, установщик
   останавливается: выберите другое имя (`-ServerName kompas-release`) или разрешите замену
   (`-ReplaceExistingEntry`), тогда прежняя запись сохраняется в
   `%LOCALAPPDATA%\KompasMCP\codex-entry-<имя>-<время>.previous.toml`.

## Ручной путь

Те же шаги без установщика.

1. Скачать из выпуска три файла: `KompasMCP-<тег>-win-x64.zip`, `release-manifest.json`,
   `SHA256SUMS.txt`. Не путать с «Source code (zip)»: это архив исходников, в нём нет готового
   сервера.
2. Сверить: `Get-FileHash .\KompasMCP-<тег>-win-x64.zip -Algorithm SHA256` должен совпасть со
   строкой в `SHA256SUMS.txt`. Не совпало, значит не распаковывать.
3. Распаковать в отдельный каталог версии, например `%LOCALAPPDATA%\KompasMCP\versions\<тег>`.
   `KompasMcp.Host.exe` и `KompasMcp.Worker.exe` должны лежать рядом: Host берёт Worker из своего
   каталога.
4. Скопировать `config\kompas-mcp.example.json` в свой конфиг вне каталога версии и заменить пути
   на существующие каталоги этой машины. Комментарии `#` в JSON недопустимы.
5. Зарегистрировать сервер в Codex одним из двух способов.

Пути в Codex пишутся абсолютными: вместо `<LOCALAPPDATA>` подставьте значение
`$env:LOCALAPPDATA`, переменные окружения в `config.toml` не раскрываются.

CLI (`codex mcp add`), документированный OpenAI:

```powershell
codex mcp add kompas -- "<LOCALAPPDATA>\KompasMCP\versions\<тег>\KompasMcp.Host.exe" --config "<LOCALAPPDATA>\KompasMCP\config\kompas-mcp.json"
```

Таймауты эта команда не задаёт, их добавляют в ту же таблицу `config.toml`. Или сразу блок
целиком (одинарные кавычки TOML не требуют удваивать `\`):

```toml
[mcp_servers.kompas]
command = '<LOCALAPPDATA>\KompasMCP\versions\<тег>\KompasMcp.Host.exe'
args = ["--config", '<LOCALAPPDATA>\KompasMCP\config\kompas-mcp.json']
startup_timeout_sec = 30
tool_timeout_sec = 300
```

По умолчанию у Codex `startup_timeout_sec = 10` и `tool_timeout_sec = 60`. Первый
`kompas_connect` поднимает КОМПАС, а бюджет операции сервера 120 с, поэтому 300 с на инструмент.
`KOMPAS_MCP_WORKER_PATH` задавать не нужно: Worker должен быть тот, что рядом с Host.

## Подключение и проверка в Codex

1. Применить конфигурацию: в приложении **Restart**, в IDE **Restart extension**; CLI читает
   конфигурацию заново в новой сессии.
2. `codex mcp list` или `codex mcp get kompas` показывает, что запись прочитана. Это ещё не
   доказывает, что сервер запустился.
3. Доказательство подключения - вызовы из новой сессии Codex: `/mcp` в CLI показывает сервер
   `kompas` и его инструменты; затем `kompas_health` и `kompas_capabilities`. Число инструментов
   должно совпасть с `tools_count` манифеста, а путь Host в ответе `kompas_health` с вашим
   каталогом версии. Если текущая сессия новый сервер не видит, нужна новая сессия после перезапуска.
4. КОМПАС для проверки подключения не нужен. Он запускается позже, первым `kompas_connect`
   (`mode: "launch"` поднимает свой экземпляр и не трогает уже открытый пользователем).

Codex может спрашивать подтверждение вызовов инструментов по своим правилам утверждения:
инструменты, меняющие модель, не помечены как только читающие. Это решение пользователя; установщик
его не обходит и не меняет. В неинтерактивном `codex exec` вызов, требующий подтверждения,
отклоняется. Разрешить его можно ключом `default_tools_approval_mode` (или
`tools.<имя>.approval_mode`) в таблице сервера, если вы сами так решите.

## Обновление и откат

- Обновление: тот же установщик без `-Tag`. Новый выпуск ложится в свой каталог
  `versions\<новый тег>`, прежний остаётся на месте; конфиг, журнал и логи сохраняются. Запись в
  Codex переключается на новый Host с резервной копией `config.toml`.
- Перед обновлением лучше закончить работу с моделями: незавершённая операция в журнале после
  перезапуска будет поднята как `OUTCOME_UNKNOWN`.
- Откат на установленную ранее версию без скачивания:
  `Install-KompasMcp.ps1 -UseInstalled -Tag <прежний тег> -RegisterCodex`. Установщик сверит
  каталог с его манифестом и вернёт запись Codex на него. Либо восстановить `config.toml` из
  резервной копии `config.toml.kompasmcp-<время>.bak`.

## Типовые ошибки

| Признак | Причина и что делать |
|---|---|
| `КОМПАС-3D не зарегистрирован для COM` | КОМПАС не установлен или ни разу не запускался этим пользователем. Установить КОМПАС-3D v24 x64 и запустить его один раз. |
| `.NET 10 Runtime x64 не найден`, или Host пишет `You must install or update .NET` | Поставить .NET 10 Runtime x64 (или Desktop Runtime). SDK не нужен. |
| `SHA-256 не совпал` | Файл повреждён или подменён. Скачать заново; если повторяется, не ставить. |
| Сервер не стартует в Codex, `startup timeout` | Неверный путь в `command` или `--config`, либо мал `startup_timeout_sec`. Запустить Host с тем же `--config` из PowerShell: ошибка конфигурации даёт код 64 и текст в stderr. |
| Host запустился, но `kompas_health` показывает чужой Worker | Задана `KOMPAS_MCP_WORKER_PATH`. Убрать переменную. |
| Запись `kompas` уже есть | Это прежнее подключение. `-ServerName` для второго имени или `-ReplaceExistingEntry` с резервной копией. |
| `PATH_NOT_ALLOWED` при сохранении | Путь вне корней конфига. Сохранять в `sandbox`, экспортировать в `export`, свои модели добавить в `read_only_roots`. |
| Нет записи в журнал или лог | Каталог `log_path` или `journal_path` недоступен на запись. Указать каталог своего пользователя. |
| `SESSION_BUSY` или сеанс занят | КОМПАС уже ведёт другой чат. `kompas_session_status` покажет владельца; передача сеанса: [session-handover.md](session-handover.md). |
| Вызов дольше `tool_timeout_sec` | Поднять `tool_timeout_sec`; сервер сам отвечает `status: running` после `sync_budget_ms`, повтор с тем же `operation_id` - это опрос. |

## Что эта инструкция не обещает

- Контрольная сумма проверяет целостность скачанного файла относительно опубликованного списка, а
  не личность издателя: цифровой подписи у выпуска нет.
- Установщик не ставит КОМПАС, .NET и Codex, не пишет в реестр и не меняет правила утверждения
  Codex.
- Формат подключения других клиентов (`mcpServers` в JSON) к Codex не относится: Codex читает
  только TOML.

Источник по формату Codex: https://developers.openai.com/codex/mcp
