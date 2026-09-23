<#
.SYNOPSIS
  Сравнение одного и того же члена API7 в двух источниках типов: prebuilt-обёртке из каталога
  КОМПАСа и обёртке, сгенерированной из авторитетного TLB установленной версии.

.DESCRIPTION
  Нужен потому, что «члена нет» и «члена нет в ЭТОЙ obёртке» — разные утверждения, и проект по
  ADR-003 §4 обязан различать первое от второго. Прецедент: живой объект эскиза отвечает на имена
  ModelObjects (dispid 2006) и Curves (28) (проба L, docs/acceptance/api7/sketch-lifecycle.md),
  тогда как ISketch в Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll не объявляет ни того, ни
  другого, а обёртка из Bin\kAPI7.tlb объявляет Curves и Update(). Пока это не измерено, нельзя
  ни объявлять маршрут недоступным, ни писать код, который его «найдёт» рефлексией.

  Скрипт ничего не меняет в установке и КОМПАС не запускает.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\compare-api7-interop-tlb.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\compare-api7-interop-tlb.ps1 -Member Curves
#>
param(
    [string]$KompasRoot = 'D:\Programs\KOMPAS-3Dv24',
    [string]$TlbImp = 'C:\Program Files (x86)\Microsoft SDKs\Windows\v8.1A\bin\NETFX 4.5.1 Tools\x64\TlbImp.exe',
    [string]$Member = 'ModelObjects',
    [string]$Type = 'ISketch',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\.qwen\tmp')
)

$ErrorActionPreference = 'Stop'
$prebuilt = Join-Path $KompasRoot 'Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll'
$tlb = Join-Path $KompasRoot 'Bin\kAPI7.tlb'

foreach ($required in @(@($prebuilt, 'prebuilt-обёртка'), @($tlb, 'kAPI7.tlb'), @($TlbImp, 'TlbImp.exe'))) {
    if (-not (Test-Path $required[0])) {
        Write-Output "НЕТ: $($required[1]) → $($required[0])"
        Write-Output 'Без него сравнение невозможно; вывод скрипта в этом случае ничего не утверждает.'
        exit 3
    }
}

function Show-Source([string]$label, [string]$path, [System.Reflection.Assembly]$asm) {
    Write-Output ''
    Write-Output ("=== {0} ===" -f $label)
    Write-Output ("    файл: {0}" -f $path)
    Write-Output ("    sha256: {0}" -f (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant())

    $declaring = @()
    foreach ($t in $asm.GetTypes()) {
        if (-not $t.IsInterface) { continue }
        foreach ($p in $t.GetProperties()) {
            if ($p.Name -match [regex]::Escape($Member)) {
                $declaring += ("    {0}.{1} : {2}" -f $t.Name, $p.Name, $p.PropertyType.Name)
            }
        }
    }
    if ($declaring.Count -eq 0) {
        Write-Output ("    ни один интерфейс не объявляет свойство «{0}»" -f $Member)
    } else {
        $declaring | Select-Object -First 15 | ForEach-Object { Write-Output $_ }
        if ($declaring.Count -gt 15) { Write-Output ("    … ещё {0}" -f ($declaring.Count - 15)) }
    }

    $target = $asm.GetTypes() | Where-Object { $_.Name -eq $Type } | Select-Object -First 1
    if ($null -eq $target) {
        Write-Output ("    тип {0} в сборке не найден" -f $Type)
        return
    }
    $members = @($target.GetMembers() | Where-Object { $_.Name -match [regex]::Escape($Member) } |
        Sort-Object Name | ForEach-Object { "    {0} {1}" -f $_.MemberType, $_.ToString() })
    if ($members.Count -eq 0) {
        Write-Output ("    {0}: членов, содержащих «{1}», нет" -f $Type, $Member)
    } else {
        Write-Output ("    {0}:" -f $Type)
        $members | ForEach-Object { Write-Output ("   " + $_) }
    }
}

Show-Source 'обёртка из каталога приложения (та, на которой собран проект)' $prebuilt `
    ([System.Reflection.Assembly]::LoadFrom($prebuilt))

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$regenerated = Join-Path $OutDir 'regen-kAPI7.dll'
& $TlbImp $tlb ("/out:" + $regenerated) /silent /namespace:RegenAPI7 | Out-Null
if (-not (Test-Path $regenerated)) {
    Write-Output 'TlbImp не создал сборку — сравнение не состоялось.'
    exit 4
}
Show-Source ("обёртка, сгенерированная из TLB: " + $tlb) $regenerated `
    ([System.Reflection.Assembly]::LoadFrom($regenerated))

Write-Output ''
Write-Output 'Чтение вывода: отсутствие члена в первой колонке — отсутствие в obёртке, а не в КОМПАСе.'
Write-Output 'Если член есть во второй колонке, типизированный маршрут возможен, но требует решить,'
Write-Output 'какой interop считается источником типов (Q-SKETCH-ENTITIES в coverage/solid-v24/open-questions.md).'
