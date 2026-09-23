# Extracts the final assistant text of one subagent transcript into a Markdown file.
# Purpose: research done by a subagent is evidence for ADRs; it must not live only in a
# transient transcript file. Read-only with respect to the transcript.
param(
    [Parameter(Mandatory = $true)][string]$Transcript,
    [Parameter(Mandatory = $true)][string]$OutFile
)

$lines = Get-Content -LiteralPath $Transcript -Encoding UTF8
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    if (-not $lines[$i].Trim()) { continue }
    try { $obj = $lines[$i] | ConvertFrom-Json } catch { continue }
    if ($obj.type -ne 'assistant') { continue }
    $parts = $obj.message.parts
    if (-not $parts) { continue }
    $texts = @($parts | Where-Object { $_.text } | ForEach-Object { $_.text })
    if ($texts.Count -eq 0) { continue }
    $body = ($texts -join [Environment]::NewLine)
    # Ignore internal reasoning dumps that carry no report.
    if ($body.Length -lt 400) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
    Set-Content -LiteralPath $OutFile -Value $body -Encoding UTF8
    Write-Output "Saved $($body.Length) chars from record index $i to $OutFile"
    exit 0
}

Write-Output 'No substantive assistant report found.'
exit 1
