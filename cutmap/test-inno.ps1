[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Installer, [string]$RepoRoot)
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$Installer = (Resolve-Path -LiteralPath $Installer).Path
if ([IO.Path]::GetFileName($Installer) -notmatch '-Validation-Setup-') { throw 'Only an isolated TestBuild installer may be used here.' }
$gateRoot = Join-Path $RepoRoot 'cutmap\_gate'
$testRoot = Join-Path $gateRoot ('inno-validation-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $testRoot 'app'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CutMapAcceptance_is1'
if (Test-Path $registry) { throw 'An earlier validation installation exists; refusing to overwrite it.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results = New-Object 'Collections.Generic.List[string]'
function Check([string]$Name, [bool]$Pass) {
    if (-not $Pass) { throw "FAIL $Name" }
    $results.Add("PASS $Name")
}
function Install-Validation([string]$Directory, [string]$Log) {
    $process = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/TASKS=!desktopicon',"/DIR=$Directory", "/LOG=$Log") -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
try {
    Check 'Initial installation exits successfully' ((Install-Validation $target (Join-Path $testRoot 'install.log')) -eq 0)
    Check 'Application and bundled runtime exist' ((Test-Path (Join-Path $target 'CutMap.App.exe')) -and (Test-Path (Join-Path $target 'coreclr.dll')))
    Check 'Bundled CTB exists' (Test-Path (Join-Path $target 'ctb\bin\ctb-tile.exe'))
    Check 'Correct version registered in isolated HKCU key' ((Get-ItemProperty -Path $registry).DisplayVersion -eq '0.1.2')
    & (Join-Path $target 'CutMap.Cli.exe') version
    Check 'Installed CLI starts with bundled GIS runtime' ($LASTEXITCODE -eq 0)
    $coreHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $target 'CutMap.Core.dll')).Hash
    [IO.File]::WriteAllText((Join-Path $target 'operator-dem.tif'), 'keep user data')
    Check 'Upgrade exits successfully' ((Install-Validation $target (Join-Path $testRoot 'upgrade.log')) -eq 0)
    Check 'Upgrade preserves unknown user data' ([IO.File]::ReadAllText((Join-Path $target 'operator-dem.tif')) -eq 'keep user data')
    Check 'Upgrade preserves expected program content' ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $target 'CutMap.Core.dll')).Hash -eq $coreHash)
    $foreign = Join-Path $testRoot 'foreign'
    New-Item -ItemType Directory -Path $foreign | Out-Null
    [IO.File]::WriteAllText((Join-Path $foreign 'important.txt'), 'keep')
    Check 'Non-program directory install is rejected' ((Install-Validation $foreign (Join-Path $testRoot 'rejected.log')) -ne 0)
    Check 'Rejected install preserves foreign file' ([IO.File]::ReadAllText((Join-Path $foreign 'important.txt')) -eq 'keep')
    $uninstall = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=$(Join-Path $testRoot 'uninstall.log')") -WindowStyle Hidden -Wait -PassThru
    Check 'Uninstall exits successfully' ($uninstall.ExitCode -eq 0)
    Check 'Uninstall removes application and registry entry' ((-not (Test-Path (Join-Path $target 'CutMap.App.exe'))) -and (-not (Test-Path $registry)))
    Check 'Uninstall preserves files not installed by CutMap' (Test-Path (Join-Path $target 'operator-dem.tif'))
} finally {
    $results.Add("Evidence directory: $testRoot")
    $results | Set-Content -LiteralPath (Join-Path $RepoRoot 'outputs\stage4-installation-gate.txt') -Encoding utf8
    $results | Write-Output
}
