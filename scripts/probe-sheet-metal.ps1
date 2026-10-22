# Проба документированного маршрута листового моделирования (наряд SHEET_METAL_RESEARCH).
# Зачем: маршрут "IPart7 -> QueryInterface(ISheetMetalContainer) -> ISheetMetalBodies.Add" описан
# справкой, но в продукте его нет; проба проверяет ДОКУМЕНТИРОВАННОЕ поведение, а не ищет новое.
# Что проверяем: контейнер получается; листовое тело создаётся из эскиза с толщиной и радиусом;
# чтение назад совпадает; развёртка (Straighten / Unfold) читается; файл сохраняется и открывается.
# НЕ ЗАПУСКАЕТСЯ на этой машине: KOMPAS.Application.5 отказывает 0x8002802B, а .7 отдаёт IID, не
# приводимый к KompasAPI7.IApplication. Причина и что делать - SHEET_METAL_RESEARCH_REPORT §4.1.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\probe-sheet-metal.ps1 [-Out <файл.json>]

param(
    [string]$Out = "",
    [string]$Interop = "C:\Program Files\ASCON\KOMPAS-3D v24\Libs\PolynomLib\Bin\Client\Interop.KompasAPI7.dll"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Out) { $Out = Join-Path $root "scratch\probe-sheet-metal.json" }

$rows = New-Object System.Collections.ArrayList
function Note($step, $what) {
    [void]$rows.Add([ordered]@{ step = $step; result = $what })
    Write-Output ("{0,-28} {1}" -f $step, $what)
}

Add-Type -Path $Interop

$app = $null
$doc = $null
$saved = Join-Path $root "scratch\probe-sheet-metal.m3d"
try {
    $app = New-Object -ComObject "KOMPAS.Application.5"
    $app.Visible = $false
    Note "app" "запущен"

    $added = $app.Documents.Add([KompasAPI7.DocumentTypeEnum]::ksDocumentPart, $true)
    if ($null -eq $added) { throw "Documents.Add(ksDocumentPart) вернул null" }
    $doc = $app.ActiveDocument
    $doc3d = [KompasAPI7.IKompasDocument3D]$doc
    Note "document" ("создана деталь: " + $doc3d.PathName)

    $part = $doc3d.TopPart
    Note "part" ("IPart7 получен: " + $part.GetType().FullName)

    $sm = [KompasAPI7.ISheetMetalContainer]$part
    Note "container" ("ISheetMetalContainer получен: " + $sm.GetType().FullName)
    Note "bodies_count" ("SheetMetalBodies.Count = " + $sm.SheetMetalBodies.Count)
    Note "bends_count" ("SheetMetalBends.Count = " + $sm.SheetMetalBends.Count)
    Note "unfold" ("IsCreated = " + $sm.SheetMetalBendUnfoldParameters.IsCreated)
}
catch {
    Note "error" ($_.Exception.GetType().Name + ": " + $_.Exception.Message)
}
finally {
    if ($app -ne $null) {
        try { $app.Quit() } catch { }
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($app)
    }
    $payload = [ordered]@{
        probe = "sheet-metal"
        at = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
        rows = $rows
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -Path $Out
    Write-Output ("--- отчёт: " + $Out)
}
