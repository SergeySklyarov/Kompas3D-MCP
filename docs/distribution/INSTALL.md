# KompasMCP: установка готового пакета

Этот архив - готовый сервер MCP для КОМПАС-3D v24 (Windows x64). Собирать ничего не нужно.

## Требования

- Windows x64.
- КОМПАС-3D v24 x64: установлен, лицензирован и хотя бы раз запущен этим пользователем (так он
  регистрируется для COM). Его DLL и interop в архив не входят, они берутся из вашей установки.
- .NET 10 Runtime x64 (`Microsoft.NETCore.App 10.x`) или .NET 10 Desktop Runtime x64.
  .NET SDK не нужен. https://dotnet.microsoft.com/download/dotnet/10.0

## Установка установщиком

Архив уже проверен по `SHA256SUMS.txt`? Тогда из распакованного каталога:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KompasMcp.ps1 -RegisterCodex
```

Установщик сам скачает и проверит выпуск, создаст конфигурацию и запишет сервер в Codex с
резервной копией `config.toml`. Подробно, вместе с ручным путём, обновлением, откатом и
диагностикой: https://github.com/SergeySklyarov/Kompas3D-MCP/blob/main/docs/operator-guide/codex-setup.md

## Установка вручную

1. Распаковать архив в отдельный каталог версии. `KompasMcp.Host.exe` и `KompasMcp.Worker.exe`
   должны лежать рядом.
2. Скопировать `config\kompas-mcp.example.json` в свой файл вне каталога версии и указать
   существующие каталоги этой машины: `read_only_roots` (свои модели, только чтение),
   `writable_roots` (песочница), `export_roots`, пути журнала и логов.
3. Подключить в MCP-клиенте stdio-сервер: команда `<каталог>\KompasMcp.Host.exe`, аргументы
   `--config <ваш конфиг>`. Для Codex блок `config.toml`:

   ```toml
   [mcp_servers.kompas]
   command = 'C:\путь\KompasMcp.Host.exe'
   args = ["--config", 'C:\путь\kompas-mcp.json']
   startup_timeout_sec = 30
   tool_timeout_sec = 300
   ```

4. Проверить из клиента: `kompas_health`, `kompas_capabilities`, затем
   `kompas_connect {"mode": "launch"}`.

## Что в архиве

Host и Worker (framework-dependent, win-x64), собственные сборки и зависимости NuGet, `schemas\`
(JSON-схемы всех инструментов), `config\kompas-mcp.example.json`, этот файл, `Install-KompasMcp.ps1`,
`LICENSE.txt` (MIT) и `THIRD-PARTY-NOTICES.txt`.

`SHA256SUMS.txt` проверяет целостность скачанного архива, а не личность издателя: цифровой подписи
у выпуска нет.
