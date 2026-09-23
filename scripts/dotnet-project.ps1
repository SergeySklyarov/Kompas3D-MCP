# Forward all arguments to dotnet, repairing only missing process environment paths.
# Run from a normal Windows session with access to the project, SDK and NuGet cache.
# Does not elevate, change registry/user environment, or bypass filesystem permissions.
$ErrorActionPreference = 'Stop'
$dotnetArguments = @($args)
if ($dotnetArguments.Count -eq 0) {
    $dotnetArguments = @('restore', (Join-Path $PSScriptRoot '..\KompasMcp.sln'))
}
$folderMap = [ordered]@{
    APPDATA = 'ApplicationData'
    LOCALAPPDATA = 'LocalApplicationData'
    USERPROFILE = 'UserProfile'
    ProgramData = 'CommonApplicationData'
    ALLUSERSPROFILE = 'CommonApplicationData'
    ProgramFiles = 'ProgramFiles'
    'ProgramFiles(x86)' = 'ProgramFilesX86'
}
$repaired = @{}
$resultCode = 1
try {
    foreach ($variableName in $folderMap.Keys) {
        $oldValue = [Environment]::GetEnvironmentVariable($variableName, 'Process')
        if (-not [string]::IsNullOrWhiteSpace($oldValue)) { continue }
        $folder = [Environment+SpecialFolder]$folderMap[$variableName]
        $resolved = [Environment]::GetFolderPath($folder)
        if ([string]::IsNullOrWhiteSpace($resolved)) {
            throw "Windows cannot resolve $variableName. Run in the signed-in user's normal Windows session."
        }
        $repaired[$variableName] = $oldValue
        [Environment]::SetEnvironmentVariable($variableName, $resolved, 'Process')
        Write-Host "Restored missing process variable: $variableName"
    }
    & dotnet @dotnetArguments
    $resultCode = $LASTEXITCODE
}
finally {
    foreach ($variableName in $repaired.Keys) {
        [Environment]::SetEnvironmentVariable($variableName, $repaired[$variableName], 'Process')
    }
}
exit $resultCode
