<#
Verify a delivery folder: no vendor binaries, both executables present and x64, schemas present,
config template present, no machine-local config leaked.

Why this is a script and not a checklist item: "we did not redistribute ASCON's files" is a
licensing claim about build output, and build output is exactly where an accidental CopyLocal
appears. It is checked mechanically so the claim stays true after someone edits a csproj.

Usage:  .\scripts\inspect-package.ps1 [-Path artifacts\publish]
#>
param(
    [string]$Path = "artifacts\publish"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $Path)) { throw "папка не найдена: $Path" }

$problems = @()

# 1. No vendor assemblies may ship.
$forbidden = Get-ChildItem -Path $Path -Recurse -File | Where-Object {
    $_.Name -match '^(Interop\.Kompas|Kompas6|KompasAPI7|KAPITypes|stdole)' -or $_.Extension -eq '.tlb'
}
foreach ($file in $forbidden) {
    $problems += "в поставку попал вендорский файл: $($file.FullName)"
}

# 2. Both processes must be present: the Host cannot do CAD work without its child.
foreach ($required in @("KompasMcp.Host.exe", "KompasMcp.Worker.exe", "ModelContextProtocol.dll")) {
    if (-not (Get-ChildItem -Path $Path -Recurse -Filter $required -ErrorAction SilentlyContinue)) {
        $problems += "не найден обязательный файл: $required"
    }
}

# 3. Schemas are part of the contract handed to the customer.
if (-not (Get-ChildItem -Path $Path -Recurse -Filter "kompas_*.json" -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*schemas*" })) {
    $problems += "нет JSON Schema инструментов (ожидается каталог schemas/)"
}

# 4. Executables must be x64 — a 32-bit build cannot talk to КОМПАС v24 at all.
foreach ($exe in @("KompasMcp.Host.exe", "KompasMcp.Worker.exe")) {
    $found = Get-ChildItem -Path $Path -Recurse -Filter $exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        $bytes = [System.IO.File]::ReadAllBytes($found.FullName)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
        if ($machine -ne 0x8664) {
            $problems += "$exe имеет machine=0x$($machine.ToString('X4')), ожидается 0x8664 (x64)"
        }
    }
}

# 5. No machine-local configuration may ship. config\*.local.json carries absolute paths of the
#    machine that ran the build; a delivery that contains one starts the customer with roots that
#    do not exist on their disk, and publishes the build workstation's directory layout. The
#    template (*.example.json) is required instead — a delivery without it is not installable.
$localConfigs = Get-ChildItem -Path $Path -Recurse -File -Filter "*.local.json" -ErrorAction SilentlyContinue
foreach ($file in $localConfigs) {
    $problems += "в поставку попал локальный конфиг: $($file.FullName)"
}

if (-not (Get-ChildItem -Path $Path -Recurse -File -Filter "*.example.json" -ErrorAction SilentlyContinue)) {
    $problems += "нет шаблона конфигурации (*.example.json): оператор не сможет задать корни"
}

$fileCount = (Get-ChildItem -Path $Path -Recurse -File).Count
$sizeMb = [math]::Round(((Get-ChildItem -Path $Path -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

# Write-Output, not Write-Host: the verdict of a package check has to be capturable by the caller
# (`& inspect-package.ps1 > report.txt`), otherwise "проверка пройдена" lives only in a console
# scrollback and cannot be attached as evidence.
Write-Output "Проверка $Path : файлов $fileCount, размер $sizeMb МБ"

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Output "  ПРОБЛЕМА: $_" }
    exit 1
}

Write-Output " OK: вендорских бинарей нет, состав и разрядность соответствуют контракту поставки."
exit 0
