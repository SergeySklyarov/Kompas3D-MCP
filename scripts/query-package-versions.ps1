# Reports the latest STABLE (non-prerelease) version of each package we intend to pin.
# Used once during dependency selection; the chosen versions then go into the csproj files
# and PackageVersions.props so a build never floats to "latest".
param(
    [string[]]$Ids = @(
        'Microsoft.NET.Test.Sdk',
        'xunit',
        'xunit.runner.visualstudio',
        'coverlet.collector',
        'Microsoft.Extensions.Hosting',
        'ModelContextProtocol'
    )
)

foreach ($id in $Ids) {
    $url = "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json"
    try {
        $json = (Invoke-WebRequest $url -UseBasicParsing).Content
        $versions = ($json | ConvertFrom-Json).versions
        $stable = @($versions | Where-Object { $_ -notmatch '[-]' })
        if ($stable.Count -eq 0) { Write-Output "$id => no stable release" }
        else { Write-Output "$id => $($stable[-1])   (recent: $($stable[-3..-1] -join ', '))" }
    }
    catch {
        Write-Output "$id => ERROR $($_.Exception.Message)"
    }
}
