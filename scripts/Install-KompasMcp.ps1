<#
.SYNOPSIS
Installs a released KompasMCP binary package on this Windows machine and, on request, registers it
in Codex as a stdio MCP server.

.DESCRIPTION
Steps, each one checked before the next: dependencies (Windows x64, KOMPAS-3D v24 COM registration,
vendor interop, .NET 10 runtime x64) -> release selection (GitHub Releases, stable only) -> download
-> SHA-256 check -> unpack into versions\<tag> -> local server config -> self-test over MCP stdio ->
optional Codex registration with a backup of config.toml.

Nothing is installed system-wide: no registry writes, no KOMPAS changes, no runtime installs.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KompasMcp.ps1 -RegisterCodex

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-KompasMcp.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    # GitHub repository "owner/name" that publishes the releases.
    [string]$Repo = "SergeySklyarov/Kompas3D-MCP",
    # Release tag. Empty means the latest stable release reported by GitHub.
    [string]$Tag = "",
    # Where versions, config, logs and journal live.
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "KompasMCP"),
    # Where the server may write models and exports.
    [string]$WorkRoot = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "KompasMCP"),
    # Folders with the user's own models: the server may read them but never write.
    [string[]]$ReadOnlyRoot = @(),
    # MCP server name in the Codex config.
    [string]$ServerName = "kompas",
    # Codex home directory. Empty means $env:CODEX_HOME, then ~\.codex.
    [string]$CodexHome = "",
    # Write the [mcp_servers.<ServerName>] entry into Codex config.toml.
    [switch]$RegisterCodex,
    # Allow replacing an entry with the same name that points outside this InstallRoot.
    [switch]$ReplaceExistingEntry,
    # Offline install from files already on disk (all three are required together).
    [string]$ZipPath = "",
    [string]$SumsPath = "",
    [string]$ManifestPath = "",
    # Switch to an already installed versions\<Tag> without downloading (rollback).
    [switch]$UseInstalled,
    # Only check dependencies and stop.
    [switch]$CheckOnly,
    [switch]$SkipSelfTest
)

$ErrorActionPreference = "Stop"

function Say([string]$text) { Write-Host $text }
function Ok([string]$text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Bad([string]$text) { Write-Host "  [FAIL] $text" -ForegroundColor Red }
function Stop-Install([string]$text) { Write-Host ""; Write-Host "ОСТАНОВЛЕНО: $text" -ForegroundColor Red; exit 1 }

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

# --- 1. Dependencies -------------------------------------------------------------------------

function Test-Dependencies {
    Say "1. Проверка зависимостей"
    $problems = New-Object System.Collections.Generic.List[string]
    $result = @{}

    if ([Environment]::Is64BitOperatingSystem) { Ok "Windows x64" }
    else { Bad "Windows не 64-битная"; $problems.Add("Нужна 64-битная Windows.") }

    # INVARIANT: the same lookup the Worker uses (KompasInteropResolver): HKCR ProgID -> CLSID ->
    # LocalServer32 in the 64-bit view. Registration is read here and never written.
    $exe = $null
    try {
        $hkcr = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::ClassesRoot,
            [Microsoft.Win32.RegistryView]::Registry64)
        $clsidKey = $hkcr.OpenSubKey("KOMPAS.Application.5\CLSID")
        $clsid = if ($clsidKey) { $clsidKey.GetValue($null) } else { $null }
        if ($clsid) {
            $server = $hkcr.OpenSubKey("CLSID\$clsid\LocalServer32")
            $raw = if ($server) { [string]$server.GetValue($null) } else { $null }
            if ($raw) {
                $raw = $raw.Trim()
                if ($raw.StartsWith('"')) { $exe = $raw.Substring(1, $raw.IndexOf('"', 1) - 1) }
                elseif ($raw.IndexOf(' ') -gt 0) { $exe = $raw.Substring(0, $raw.IndexOf(' ')) }
                else { $exe = $raw }
            }
        }
    } catch { $exe = $null }

    if (-not $exe) {
        Bad "КОМПАС-3D не зарегистрирован для COM (нет HKCR\KOMPAS.Application.5)"
        $problems.Add("Установите лицензированный КОМПАС-3D v24 x64 и запустите его один раз от имени этого пользователя, чтобы он зарегистрировался для COM. Скрипт КОМПАС не ставит и реестр не меняет.")
    } elseif (-not (Test-Path -LiteralPath $exe)) {
        Bad "LocalServer32 указывает на несуществующий файл: $exe"
        $problems.Add("Регистрация КОМПАС указывает на отсутствующий KOMPAS.Exe. Переустановите или восстановите КОМПАС-3D v24.")
    } else {
        $ver = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
        $result.KompasExe = $exe
        $result.KompasVersion = $ver
        if ($ver -and $ver.StartsWith("24.")) { Ok "КОМПАС-3D $ver ($exe)" }
        else {
            Bad "КОМПАС найден, но версия $ver, а выпуск проверен на v24"
            $problems.Add("Нужен КОМПАС-3D v24 x64; найдена версия $ver.")
        }
        $root = Split-Path -Parent (Split-Path -Parent $exe)
        $interop = @(
            (Join-Path $root "Libs\PolynomLib\Bin\Client\Interop.Kompas6API5.dll"),
            (Join-Path $root "Libs\Interop\Interop.Kompas6API5.dll"),
            (Join-Path (Split-Path -Parent $exe) "Interop.Kompas6API5.dll")
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if ($interop) { Ok "interop КОМПАС: $interop" }
        else {
            Bad "Interop.Kompas6API5.dll не найден в каталоге установки КОМПАС"
            $problems.Add("В установке КОМПАС нет Interop.Kompas6API5.dll (ожидался Libs\PolynomLib\Bin\Client). Проверьте полноту установки КОМПАС-3D v24.")
        }
    }

    # INVARIANT: the package is framework-dependent and its runtimeconfig asks for
    # Microsoft.NETCore.App 10.x; the Desktop Runtime contains it, so either installer satisfies it.
    $dotnetRoot = Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.NETCore.App"
    $runtimes = @()
    if (Test-Path -LiteralPath $dotnetRoot) {
        $runtimes = @(Get-ChildItem -LiteralPath $dotnetRoot -Directory | Where-Object { $_.Name -like "10.*" })
    }
    if ($runtimes.Count -gt 0) { Ok ".NET Runtime x64: Microsoft.NETCore.App $($runtimes[-1].Name)" }
    else {
        Bad ".NET 10 Runtime x64 не найден ($dotnetRoot\10.*)"
        $problems.Add("Установите .NET 10 Runtime x64 или .NET 10 Desktop Runtime x64: https://dotnet.microsoft.com/download/dotnet/10.0 . .NET SDK не нужен.")
    }

    $result.Problems = $problems
    return $result
}

# --- 2. Release selection --------------------------------------------------------------------

function Invoke-GitHubJson([string]$url) {
    $headers = @{ "User-Agent" = "KompasMCP-installer"; "Accept" = "application/vnd.github+json" }
    return Invoke-RestMethod -Uri $url -Headers $headers -UseBasicParsing
}

function Save-Asset($release, [string]$name, [string]$dir) {
    $asset = @($release.assets | Where-Object { $_.name -eq $name }) | Select-Object -First 1
    if (-not $asset) { Stop-Install "в выпуске $($release.tag_name) нет файла $name" }
    $target = Join-Path $dir $name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $target -UseBasicParsing `
        -Headers @{ "User-Agent" = "KompasMCP-installer" }
    return $target
}

function Read-Sums([string]$path) {
    $map = @{}
    foreach ($line in Get-Content -LiteralPath $path) {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$') { $map[$Matches[2]] = $Matches[1].ToLowerInvariant() }
    }
    return $map
}

function Assert-Checksum([hashtable]$sums, [string]$file) {
    $name = Split-Path -Leaf $file
    if (-not $sums.ContainsKey($name)) { Stop-Install "в SHA256SUMS.txt нет строки для $name" }
    $actual = Get-Sha256 $file
    if ($actual -ne $sums[$name]) {
        Stop-Install "SHA-256 не совпал для ${name}: ожидалось $($sums[$name]), получено $actual. Файл не распакован и не запускался."
    }
    Ok "SHA-256 совпал: $name $actual"
}

# --- 3. Unpack and verify the package --------------------------------------------------------

function Test-PackageDir([string]$dir, $manifest) {
    # INVARIANT: own assemblies are compared with the manifest, so a stale or foreign Host/Worker
    # in versions\<tag> is detected instead of silently reused.
    foreach ($p in $manifest.own_assemblies.PSObject.Properties) {
        $f = Join-Path $dir $p.Name
        if (-not (Test-Path -LiteralPath $f)) { return "нет файла $($p.Name)" }
        if ((Get-Sha256 $f) -ne $p.Value) { return "хеш $($p.Name) не совпал с манифестом" }
    }
    $schemas = @(Get-ChildItem -LiteralPath (Join-Path $dir "schemas") -Filter "kompas_*.json" -File).Count
    if ($schemas -ne [int]$manifest.tools_count) { return "схем $schemas, а в манифесте $($manifest.tools_count)" }
    if (-not (Test-Path -LiteralPath (Join-Path $dir "config\kompas-mcp.example.json"))) { return "нет шаблона конфигурации" }
    return $null
}

# --- 4. Local config -------------------------------------------------------------------------

function New-LocalConfig([string]$versionDir, [string]$configPath) {
    $template = Get-Content -LiteralPath (Join-Path $versionDir "config\kompas-mcp.example.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $ro = @($ReadOnlyRoot | ForEach-Object { [IO.Path]::GetFullPath($_) })
    if ($ro.Count -eq 0) { $ro = @(Join-Path $WorkRoot "read-only-models") }
    $dirs = @{
        sandbox = Join-Path $WorkRoot "sandbox"; export = Join-Path $WorkRoot "export"
        logs = Join-Path $InstallRoot "logs"; journal = Join-Path $InstallRoot "journal"
        artifacts = Join-Path $InstallRoot "artifacts"
    }
    foreach ($d in @($dirs.Values) + $ro) {
        if (-not (Test-Path -LiteralPath $d)) {
            if ($ReadOnlyRoot -contains $d) { Stop-Install "каталог только для чтения не существует: $d" }
            New-Item -ItemType Directory -Force -Path $d | Out-Null
        }
    }
    $template._comment = @("Создано Install-KompasMcp.ps1 из шаблона пакета. Пути этой машины; правьте под себя.")
    $template.read_only_roots = $ro
    $template.writable_roots = @($dirs.sandbox)
    $template.export_roots = @($dirs.export)
    $template.worker_path = $null
    $template.log_path = Join-Path $dirs.logs "host.jsonl"
    $template.worker_log_path = Join-Path $dirs.logs "worker.jsonl"
    $template.journal_path = Join-Path $dirs.journal "operations.jsonl"
    $template.artifact_directory = $dirs.artifacts
    $template.control_copy_directory = $InstallRoot
    $json = $template | ConvertTo-Json -Depth 5
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $configPath) | Out-Null
    [IO.File]::WriteAllText($configPath, $json, (New-Object Text.UTF8Encoding($false)))
}

# --- 5. Self-test over MCP stdio -------------------------------------------------------------

function Invoke-SelfTest([string]$hostExe, [string]$configPath, $manifest) {
    Say "5. Самопроверка: запуск Host по MCP stdio (КОМПАС не запускается)"
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $hostExe
    $psi.Arguments = "--config `"$configPath`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    # INVARIANT: the Worker must be the one beside this Host; an inherited override would test
    # a different build. LIMIT: only this child process is affected.
    $psi.EnvironmentVariables.Remove("KOMPAS_MCP_WORKER_PATH")
    $p = [Diagnostics.Process]::Start($psi)
    $stderr = $p.StandardError.ReadToEndAsync()
    $utf8 = New-Object Text.UTF8Encoding($false)
    $script:rpcId = 0
    function Send($method, $params, [switch]$Notify) {
        $msg = @{ jsonrpc = "2.0"; method = $method }
        if ($null -ne $params) { $msg.params = $params }
        if (-not $Notify) { $script:rpcId++; $msg.id = $script:rpcId }
        $bytes = $utf8.GetBytes(($msg | ConvertTo-Json -Depth 10 -Compress) + "`n")
        $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $p.StandardInput.BaseStream.Flush()
        if ($Notify) { return $null }
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            $task = $p.StandardOutput.ReadLineAsync()
            if (-not $task.Wait(60000) -or $null -eq $task.Result) { break }
            $obj = $task.Result | ConvertFrom-Json
            if ($obj.PSObject.Properties.Name -contains "id" -and $obj.id -eq $script:rpcId) { return $obj }
        }
        throw "нет ответа на $method"
    }
    $report = @{}
    try {
        $init = Send "initialize" @{ protocolVersion = "2025-06-18"; capabilities = @{}; clientInfo = @{ name = "kompasmcp-installer"; version = "1" } }
        $report.server = "$($init.result.serverInfo.name) $($init.result.serverInfo.version), протокол $($init.result.protocolVersion)"
        Ok "initialize: $($report.server)"
        Send "notifications/initialized" $null -Notify | Out-Null
        $list = Send "tools/list" @{}
        $names = @($list.result.tools | ForEach-Object { $_.name })
        $report.tools = $names.Count
        if ($names.Count -eq [int]$manifest.tools_count) { Ok "tools/list: $($names.Count) инструментов (манифест: $($manifest.tools_count))" }
        else { Bad "tools/list: $($names.Count), а в манифесте $($manifest.tools_count)"; $report.failed = $true }
        foreach ($tool in @("kompas_health", "kompas_session_status", "kompas_capabilities")) {
            $r = Send "tools/call" @{ name = $tool; arguments = @{} }
            $text = ($r.result.content | Where-Object { $_.type -eq "text" } | Select-Object -First 1).text
            $status = $null
            if ($r.result.PSObject.Properties.Name -contains "structuredContent" -and $r.result.structuredContent) {
                $status = $r.result.structuredContent.status
            }
            $isError = ($r.result.PSObject.Properties.Name -contains "isError") -and $r.result.isError
            if ($isError) { Bad "${tool}: ошибка: $text"; $report.failed = $true }
            else { Ok "${tool}: $status" }
        }
    } catch {
        Bad "самопроверка не прошла: $($_.Exception.Message)"
        $report.failed = $true
    } finally {
        try { $p.StandardInput.Close() } catch { }
        if (-not $p.WaitForExit(15000)) { $p.Kill() }
    }
    if ($report.failed) {
        $err = $stderr.Result
        if ($err) { Say "stderr Host (последние строки):"; ($err -split "`n" | Select-Object -Last 15) | ForEach-Object { Say "    $_" } }
    }
    return $report
}

# --- 6. Codex registration -------------------------------------------------------------------

function Get-TomlBlockRange([string[]]$lines, [string]$name) {
    # The block is the [mcp_servers.<name>] table plus its [mcp_servers.<name>.*] subtables.
    $head = "[mcp_servers.$name]"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq $head) { $start = $i; break } }
    if ($start -lt 0) { return $null }
    $end = $lines.Count
    for ($j = $start + 1; $j -lt $lines.Count; $j++) {
        $t = $lines[$j].Trim()
        if ($t.StartsWith("[") -and -not $t.StartsWith("[mcp_servers.$name.")) { $end = $j; break }
    }
    return @($start, $end)
}

function Register-Codex([string]$hostExe, [string]$configPath) {
    Say "6. Регистрация в Codex"
    $home_ = if ($CodexHome) { $CodexHome } elseif ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE ".codex" }
    $toml = Join-Path $home_ "config.toml"
    foreach ($v in @($hostExe, $configPath)) { if ($v.Contains("'")) { Stop-Install "путь содержит апостроф, TOML-строка не записывается: $v" } }
    $block = @(
        "[mcp_servers.$ServerName]",
        "command = '$hostExe'",
        "args = [""--config"", '$configPath']",
        "startup_timeout_sec = 30",
        "tool_timeout_sec = 300"
    )
    $lines = @()
    if (Test-Path -LiteralPath $toml) { $lines = @(Get-Content -LiteralPath $toml -Encoding UTF8) }
    $range = Get-TomlBlockRange $lines $ServerName
    $previous = $null
    if ($range) {
        $previous = $lines[$range[0]..($range[1] - 1)]
        $cmdLine = $previous | Where-Object { $_ -match '^\s*command\s*=' } | Select-Object -First 1
        $ours = $cmdLine -and $cmdLine.ToLowerInvariant().Contains((Join-Path $InstallRoot "versions").ToLowerInvariant())
        if (-not $ours -and -not $ReplaceExistingEntry) {
            Say "  В $toml уже есть [mcp_servers.$ServerName] не из этой установки:"
            $previous | ForEach-Object { Say "    $_" }
            Stop-Install "запись не заменена. Выберите другое имя (-ServerName kompas-release) или явно разрешите замену (-ReplaceExistingEntry); прежняя запись будет сохранена в резервной копии."
        }
        if (($previous -join "`n").Trim() -eq ($block -join "`n").Trim()) { Ok "запись уже актуальна, config.toml не менялся"; return $toml }
    }
    New-Item -ItemType Directory -Force -Path $home_ | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    if (Test-Path -LiteralPath $toml) {
        $backup = "$toml.kompasmcp-$stamp.bak"
        Copy-Item -LiteralPath $toml -Destination $backup
        Ok "резервная копия: $backup"
    }
    if ($previous) {
        $prevFile = Join-Path $InstallRoot "codex-entry-$ServerName-$stamp.previous.toml"
        [IO.File]::WriteAllLines($prevFile, [string[]]$previous, (New-Object Text.UTF8Encoding($false)))
        Ok "прежняя запись сохранена: $prevFile"
        $before = if ($range[0] -gt 0) { $lines[0..($range[0] - 1)] } else { @() }
        $after = if ($range[1] -lt $lines.Count) { $lines[$range[1]..($lines.Count - 1)] } else { @() }
        $new = @($before) + $block + @("") + @($after)
    } else {
        $new = @($lines) + @("") + $block
    }
    [IO.File]::WriteAllLines($toml, [string[]]$new, (New-Object Text.UTF8Encoding($false)))
    Ok "записано [mcp_servers.$ServerName] в $toml"
    return $toml
}

# --- main ------------------------------------------------------------------------------------

Say "KompasMCP: установка выпуска ($Repo)"
$deps = Test-Dependencies
if ($deps.Problems.Count -gt 0) {
    Say ""
    Say "Чего не хватает:"
    $deps.Problems | ForEach-Object { Say "  - $_" }
    Stop-Install "зависимости не выполнены, подключение не выполнено."
}
if ($CheckOnly) { Say ""; Say "Зависимости в порядке (-CheckOnly: дальше не иду)."; exit 0 }

$versionsDir = Join-Path $InstallRoot "versions"
New-Item -ItemType Directory -Force -Path $versionsDir | Out-Null

if ($UseInstalled) {
    if (-not $Tag) { Stop-Install "-UseInstalled требует -Tag" }
    $versionDir = Join-Path $versionsDir $Tag
    $mf = Join-Path $versionDir "release-manifest.json"
    if (-not (Test-Path -LiteralPath $mf)) { Stop-Install "не найден установленный выпуск $versionDir" }
    $manifest = Get-Content -LiteralPath $mf -Raw -Encoding UTF8 | ConvertFrom-Json
    $why = Test-PackageDir $versionDir $manifest
    if ($why) { Stop-Install "установленный $Tag не прошёл сверку: $why" }
    Say "2-3. Используется установленный выпуск $Tag"
    Ok "сверка с манифестом пройдена"
} else {
    Say "2. Выбор выпуска"
    $download = Join-Path $InstallRoot "downloads"
    if ($ZipPath) {
        if (-not ($SumsPath -and $ManifestPath)) { Stop-Install "-ZipPath требует -SumsPath и -ManifestPath" }
        $zip = (Resolve-Path -LiteralPath $ZipPath).Path
        $sumsFile = (Resolve-Path -LiteralPath $SumsPath).Path
        $manifestFile = (Resolve-Path -LiteralPath $ManifestPath).Path
        Ok "локальные файлы: $zip"
    } else {
        $api = "https://api.github.com/repos/$Repo/releases"
        $release = if ($Tag) { Invoke-GitHubJson "$api/tags/$Tag" } else { Invoke-GitHubJson "$api/latest" }
        if ($release.draft -or $release.prerelease) { Stop-Install "выпуск $($release.tag_name) - черновик или предварительный, не ставлю" }
        Ok "выпуск $($release.tag_name) ($($release.name)), опубликован $($release.published_at)"
        $dir = Join-Path $download $release.tag_name
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $sumsFile = Save-Asset $release "SHA256SUMS.txt" $dir
        $manifestFile = Save-Asset $release "release-manifest.json" $dir
    }
    $sums = Read-Sums $sumsFile
    Say "3. Проверка SHA-256 и распаковка"
    Assert-Checksum $sums $manifestFile
    $manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $ZipPath) {
        if ($manifest.tag -ne $release.tag_name) { Stop-Install "манифест описывает $($manifest.tag), а выпуск $($release.tag_name)" }
        $zip = Save-Asset $release $manifest.zip $dir
    }
    if ((Split-Path -Leaf $zip) -ne $manifest.zip) { Stop-Install "архив $(Split-Path -Leaf $zip) не тот, что назван в манифесте: $($manifest.zip)" }
    Assert-Checksum $sums $zip
    $Tag = $manifest.tag
    Say "  Выпуск: $Tag; объём: $($manifest.scope); инструментов: $($manifest.tools_count)"

    $versionDir = Join-Path $versionsDir $Tag
    if (Test-Path -LiteralPath $versionDir) {
        $why = Test-PackageDir $versionDir $manifest
        if ($why) { Stop-Install "каталог $versionDir уже есть и не совпадает с выпуском ($why). Он не перезаписывается: переименуйте или удалите его сами." }
        Ok "выпуск $Tag уже установлен и совпадает с манифестом, повторная распаковка не нужна"
    } else {
        $partial = Join-Path $versionsDir ".$Tag.partial"
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Recurse -Force }
        Expand-Archive -LiteralPath $zip -DestinationPath $partial
        $why = Test-PackageDir $partial $manifest
        if ($why) { Stop-Install "распакованный пакет не совпал с манифестом: $why" }
        Copy-Item -LiteralPath $manifestFile -Destination (Join-Path $partial "release-manifest.json") -Force
        Rename-Item -LiteralPath $partial -NewName $Tag
        Ok "распаковано в $versionDir; собственные сборки совпали с манифестом"
    }
}

$hostExe = Join-Path $versionDir "KompasMcp.Host.exe"
Say "4. Конфигурация сервера"
$configPath = Join-Path $InstallRoot "config\kompas-mcp.json"
if (Test-Path -LiteralPath $configPath) {
    Ok "оставлен существующий конфиг: $configPath"
} else {
    New-LocalConfig $versionDir $configPath
    Ok "создан конфиг: $configPath"
}
$cfg = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
Say "    только чтение: $($cfg.read_only_roots -join '; ')"
Say "    запись:        $($cfg.writable_roots -join '; ')"
Say "    экспорт:       $($cfg.export_roots -join '; ')"
Say "    журнал:        $($cfg.journal_path)"

$selfTestFailed = $false
if (-not $SkipSelfTest) {
    $report = Invoke-SelfTest $hostExe $configPath $manifest
    $selfTestFailed = [bool]$report.failed
    if ($selfTestFailed) { Stop-Install "самопроверка не прошла; запись в Codex не делалась." }
}

$toml = $null
if ($RegisterCodex) { $toml = Register-Codex $hostExe $configPath }

$state = [ordered]@{ tag = $Tag; host = $hostExe; config = $configPath; codex_config = $toml; server_name = $ServerName; installed_utc = (Get-Date).ToUniversalTime().ToString("o") }
[IO.File]::WriteAllText((Join-Path $InstallRoot "installed.json"), ($state | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

Say ""
Say "Готово: KompasMCP $Tag"
Say "  Host:   $hostExe"
Say "  Конфиг: $configPath"
if ($toml) {
    Say "  Codex:  [mcp_servers.$ServerName] в $toml"
    Say ""
    Say "Дальше: перезапустите Codex (в приложении - Restart, в IDE - Restart extension; CLI подхватит"
    Say "сам в новой сессии) и в новой сессии вызовите kompas_health и kompas_capabilities."
} else {
    Say ""
    Say "В Codex не регистрировалось (нет -RegisterCodex). Блок для config.toml:"
    Say "  [mcp_servers.$ServerName]"
    Say "  command = '$hostExe'"
    Say "  args = [""--config"", '$configPath']"
    Say "  startup_timeout_sec = 30"
    Say "  tool_timeout_sec = 300"
}
