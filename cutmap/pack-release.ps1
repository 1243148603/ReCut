# ⚠ 本文件必须保存为 **UTF-8 with BOM**。
#   Windows PowerShell 5.1 在没有 BOM 时按 ANSI（本机 GBK）解析 .ps1 文本，
#   中文注释会被拆成乱码字节、引号配对错位，报的是"字符串缺少终止符"这类
#   与真实原因无关的语法错误。这与 README §6.2 记录的 .cmd 必须纯 ASCII
#   是同一类坑：解析器按本地代码页读脚本，而脚本里有非 ASCII 字符。
#   改这个文件时请确认编辑器保留了 BOM。
#
# pack-release.ps1 — 打绿色版 zip（NFR-03 的"解压即用"形态）
#
# 产物：dist\CutMap-<版本>-win-x64.zip
#   CutMap.App.exe   主程序（自包含，用户机器不需要装 .NET 运行时）
#   CutMap.Cli.exe   无界面诊断/回归入口（同样自包含）
#   ctb\             切片工具（CTB + GDAL 运行时 + PROJ/GDAL 数据，约 119 MB）
#   resources\       行政区划边界（若已备；没有就不放，程序会提示缺哪些文件）
#   THIRD-PARTY-NOTICES.txt / README.md
#
# ctb\ 必须与 exe **同级或在其上层**：程序启动时从 exe 所在目录逐级往上找
# ctb\bin\ctb-tile.exe（CtbHome.Locate），所以 zip 解开后不要改动目录层次。
#
# 两个 exe 发布到**同一个目录**：它们都是自包含的，共用同一份运行时文件，
# 只多出各自的 runtimeconfig.json。分开放会让每个目录各带一份 70 MB 的运行时。
#
# 用法：powershell -ExecutionPolicy Bypass -File cutmap\pack-release.ps1

[CmdletBinding()]
param(
    # 默认值不用 $PSScriptRoot：以相对路径（cutmap/pack-release.ps1）调用 -File 时，
    # 参数默认表达式里它可能还是空的，于是 Split-Path 报"参数为空"。
    # $PSCommandPath 在脚本体内更可靠，所以放到下面算。
    [string]$RepoRoot,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $scriptDir = Split-Path -Parent $PSCommandPath
    $RepoRoot = Split-Path -Parent $scriptDir
}
$RepoRoot = (Resolve-Path $RepoRoot).Path
Write-Host "仓库根：$RepoRoot"

# dotnet 可能只装在用户目录（官方 dotnet-install 脚本默认就是装那里），
# 那种情况下它不在系统 PATH 上，直接调会报"无法将 dotnet 识别为 cmdlet"。
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
        (Join-Path $env:USERPROFILE '.dotnet'),
        'C:\Program Files\dotnet'
    )
    $found = $candidates | Where-Object { Test-Path (Join-Path $_ 'dotnet.exe') } | Select-Object -First 1
    if (-not $found) {
        throw "找不到 dotnet。请先装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
    }
    $env:PATH = "$found;$env:PATH"
    Write-Host "dotnet：$found（用户级安装，已临时加入 PATH）"
}

$cutmapDir = Join-Path $RepoRoot 'cutmap'
$appProject = Join-Path $cutmapDir 'src\CutMap.App\CutMap.App.csproj'
$cliProject = Join-Path $cutmapDir 'src\CutMap.Cli\CutMap.Cli.csproj'
$ctbDir = Join-Path $RepoRoot 'ctb'
$resourcesDir = Join-Path $RepoRoot 'resources'
$distDir = Join-Path $RepoRoot 'dist'

if (-not (Test-Path $ctbDir)) {
    throw "找不到 ctb 目录：$ctbDir（NFR-02 的自包含交付物，没有它打出来的包跑不起来）"
}

# 版本号的唯一来源是 Directory.Build.props（见该文件注释）。
$version = (Select-String -Path (Join-Path $cutmapDir 'Directory.Build.props') `
    -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { throw '读不到版本号（cutmap\Directory.Build.props）' }

$stage = Join-Path $distDir "CutMap-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Write-Host '== 1/4 发布 App（自包含 win-x64）=='
dotnet publish $appProject -c $Configuration -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (App) 失败' }

Write-Host '== 2/4 发布 CLI（同目录，共用运行时）=='
dotnet publish $cliProject -c $Configuration -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (CLI) 失败' }

Write-Host '== 3/4 组装 =='
Copy-Item $ctbDir (Join-Path $stage 'ctb') -Recurse -Force
if (Test-Path $resourcesDir) {
    Copy-Item $resourcesDir (Join-Path $stage 'resources') -Recurse -Force
    Write-Host '   已带上 resources\（行政区划边界）'
} else {
    Write-Host '   未找到 resources\ —— 行政区划选择器在包里会提示缺数据'
}
Copy-Item (Join-Path $cutmapDir 'THIRD-PARTY-NOTICES.txt') $stage -Force
Copy-Item (Join-Path $cutmapDir 'README.md') $stage -Force

# 调试符号与 XML 文档不进发布包。
Get-ChildItem $stage -Filter '*.pdb' -Recurse -File | Remove-Item -Force
Get-ChildItem $stage -Filter 'CutMap*.xml' -File | Remove-Item -Force

$zip = Join-Path $distDir "CutMap-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host '== 4/4 压缩 =='
# 用 .NET 而不是 Compress-Archive：后者对大量小文件慢到不可用。
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "完成：$zip（$sizeMb MB）"
Write-Host ''
Write-Host '自检建议（这三条没过，包就不算能交付）：'
Write-Host '  1. 把 zip 解到一个没装过 .NET / Anaconda 的目录'
Write-Host '  2. 双击 CutMap.App.exe —— 应当直接起窗，不报 "You must install or update .NET"'
Write-Host '  3. 走一遍 拼接裁剪 → 切片 → 打包'
