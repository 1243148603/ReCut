# pack-release.ps1 — 打绿色版 zip（NFR-03 的"解压即用"形态）
#
# 产物：dist\CutMap-<版本>-win-x64.zip
#   CutMap.App.exe 及依赖（自包含，用户机器不需要装 .NET 运行时）
#   CutMap.Cli.exe（无界面回归入口，可选）
#   ctb\            切片工具（CTB + GDAL 运行时 + PROJ/GDAL 数据，约 119 MB）
#   THIRD-PARTY-NOTICES.txt
#   README.md
#
# ctb\ 必须与 exe **同级或在其上层**：程序启动时从 exe 所在目录逐级往上找
# ctb\bin\ctb-tile.exe（CtbHome.Locate），所以 zip 解开后不要改动目录层次。
#
# 用法：powershell -ExecutionPolicy Bypass -File cutmap\pack-release.ps1

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$cutmapDir = Join-Path $RepoRoot 'cutmap'
$project = Join-Path $cutmapDir 'src\CutMap.App\CutMap.App.csproj'
$cliProject = Join-Path $cutmapDir 'src\CutMap.Cli\CutMap.Cli.csproj'
$ctbDir = Join-Path $RepoRoot 'ctb'
$distDir = Join-Path $RepoRoot 'dist'

if (-not (Test-Path $ctbDir)) {
    throw "找不到 ctb 目录：$ctbDir（NFR-02 的自包含交付物，没有它打出来的包跑不起来）"
}

Write-Host '== 1/4 发布 App（自包含，win-x64）=='
dotnet publish $project -c $Configuration -o (Join-Path $distDir 'app') --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host '== 2/4 发布 CLI =='
dotnet publish $cliProject -c $Configuration -o (Join-Path $distDir 'cli') --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (CLI) 失败' }

$version = (Select-String -Path (Join-Path $cutmapDir 'src\CutMap.Core\CutMapInfo.cs') `
    -Pattern 'Version\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
if (-not $version) { throw '读不到版本号' }

$stage = Join-Path $distDir "CutMap-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Write-Host '== 3/4 组装 =='
# App 的发布目录直接作为包根：ctb\ 放它旁边，Locate() 往上找一层就能找到。
Copy-Item (Join-Path $distDir 'app\*') $stage -Recurse -Force
Copy-Item (Join-Path $distDir 'cli\CutMap.Cli.exe') $stage -Force
Copy-Item (Join-Path $distDir 'cli\CutMap.Cli.dll') $stage -Force

Copy-Item $ctbDir (Join-Path $stage 'ctb') -Recurse -Force
Copy-Item (Join-Path $cutmapDir 'THIRD-PARTY-NOTICES.txt') $stage -Force
Copy-Item (Join-Path $cutmapDir 'README.md') $stage -Force

# 开发调试用的产物不该进发布包。
Get-ChildItem $stage -Filter '*.pdb' -Recurse | Remove-Item -Force
Get-ChildItem $stage -Filter '*.xml' -File | Where-Object { $_.Name -like 'CutMap*' } | Remove-Item -Force

$zip = Join-Path $distDir "CutMap-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host '== 4/4 压缩 =='
# 用 .NET 而不是 Compress-Archive：后者对 22 万个小文件会慢到不可用，
# 而且这里的内容（DLL/EXE）本来就没多少可压的。
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "完成：$zip（$sizeMb MB）"
Write-Host ''
Write-Host '自检建议：把 zip 解到一个没装过 .NET / Anaconda 的目录，双击 CutMap.App.exe，'
Write-Host '走一遍 拼接裁剪 → 切片 → 打包。'
