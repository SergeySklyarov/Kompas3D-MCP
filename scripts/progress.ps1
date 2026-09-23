# Open with -Serve for automatic updates, or run without arguments to rebuild the HTML snapshot.
param([switch]$Serve, [int]$Port = 8766)
$ErrorActionPreference = 'Stop'
$progressScript = Join-Path $PSScriptRoot 'project-progress.mjs'
if ($Serve) { & node $progressScript --serve --port $Port }
else { & node $progressScript }
exit $LASTEXITCODE
