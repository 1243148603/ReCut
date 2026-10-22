[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Iscc,
    [string]$SourceRevision = 'working-tree',
    [switch]$TestBuild,
    [switch]$ReuseStage
)
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if (-not $Iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Iscc = $command.Source }
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) { throw 'Specify -Iscc with an installed Inno Setup compiler (6.7+).' }
if (-not $ReuseStage) {
    & (Join-Path $PSScriptRoot 'pack-release.ps1') -RepoRoot $RepoRoot -SourceRevision $SourceRevision
}
$version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $RepoRoot 'cutmap\Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ }
$stage = Join-Path $RepoRoot "dist\CutMap-$version"
foreach ($required in @('CutMap.App.exe','CutMap.Cli.exe','coreclr.dll','build-info.json','ctb\bin\ctb-tile.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) { throw "Missing release input: $required" }
}
$arguments = @('/Qp', "/DStage=$stage", "/DAppVersion=$version", "/DOutputDir=$(Join-Path $RepoRoot 'dist')")
if ($TestBuild) { $arguments += '/DTestBuild=1' }
$arguments += (Join-Path $PSScriptRoot 'installer\CutMap.iss')
& $Iscc @arguments
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
