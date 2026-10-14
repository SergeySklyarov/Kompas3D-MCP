<#
.SYNOPSIS
  The KOMPAS installation root - the same order the Python helper uses.

.DESCRIPTION
  WHY A SECOND COPY. The Python instruments share `scripts/kompas_install.py`; PowerShell cannot
  import it, so the order is repeated here in PowerShell instead of being derived from Python. The
  order itself must not be re-invented inside each script: two copies of it drift apart exactly when
  it matters, and then the build and the instruments disagree about which installation is installed.

  ORDER, and why it is this one:
    1. `KOMPAS_MCP_INSTALL` - the operator's explicit instruction for one run wins over everything;
    2. `KompasRoot` of `Directory.Build.props.local` - the file the build itself reads;
    3. `D:\Programs\KOMPAS-3Dv24` - the historically observed default, kept last so an unknown
       machine behaves exactly as it did before.

  INVARIANT: only `KompasRoot` is read from the props file. `KompasInteropDir` and `KompasTlbDir` are
  derived from the root by the caller, not taken from the file.

.EXAMPLE
  . (Join-Path $PSScriptRoot 'kompas-install.ps1')
  Get-KompasInstallRoot
#>

$script:KompasInstallRepoRoot = Split-Path -Parent $PSScriptRoot
$script:KompasInstallDefault = 'D:\Programs\KOMPAS-3Dv24'

function Get-KompasInstallInfo {
    [CmdletBinding()]
    param([string]$RepoRoot = $script:KompasInstallRepoRoot)

    $fromEnv = $env:KOMPAS_MCP_INSTALL
    if ($fromEnv -and $fromEnv.Trim()) {
        return @{ Root = $fromEnv.Trim(); Source = 'переменная окружения KOMPAS_MCP_INSTALL' }
    }
    $props = Join-Path $RepoRoot 'Directory.Build.props.local'
    if (Test-Path -LiteralPath $props) {
        try {
            $xml = [xml](Get-Content -LiteralPath $props -Raw)
            $node = $xml.SelectSingleNode('//KompasRoot')
            if ($node -and $node.InnerText.Trim()) {
                return @{ Root = $node.InnerText.Trim()
                          Source = 'Directory.Build.props.local: KompasRoot' }
            }
        } catch {
            # An unreadable or malformed props file is not a root: fall through to the default, the
            # same way the Python helper does, instead of stopping on a file that is not needed.
        }
    }
    return @{ Root = $script:KompasInstallDefault
              Source = 'значение по умолчанию (ни KOMPAS_MCP_INSTALL, ни Directory.Build.props.local)' }
}

function Get-KompasInstallRoot {
    (Get-KompasInstallInfo).Root
}

function Get-KompasInstallSource {
    (Get-KompasInstallInfo).Source
}
