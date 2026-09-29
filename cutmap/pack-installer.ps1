# ⚠ 本文件必须保存为 **UTF-8 with BOM**。
#   Windows PowerShell 5.1 在没有 BOM 时按 ANSI（本机 GBK）解析 .ps1 文本，
#   中文注释会被拆成乱码字节、引号配对错位，报的是"字符串缺少终止符"这类
#   与真实原因无关的语法错误。改这个文件时请确认编辑器保留了 BOM。
#
# pack-installer.ps1 — 产出可安装的 setup.exe（NFR-03 的第二形态）
#
# 产物：dist\CutMap-<版本>-Setup-win-x64.exe
#
# 做法：把「安装器 exe」与「载荷 zip」首尾相接，再写一段定长尾部记录：
#
#     [ CutMap-Setup.exe ][ payload.zip ][ int64 载荷长度 ][ "CUTMAPPA" ]
#
# 安装器运行时读自己的文件、定位载荷并解压（见 CutMap.Setup/Payload.cs）。
# 选追加而不是嵌入资源，是为了让这个脚本能**只重跑打包、不重编译安装器**——
# 几百 MB 的 EmbeddedResource 会让每次改一点内容就重编一遍。
#
# 安装到 %LOCALAPPDATA%\Programs\CutMap，建开始菜单与桌面快捷方式，
# 在「应用和功能」里登记卸载项。**全程不需要管理员权限。**
#
# 用法：powershell -ExecutionPolicy Bypass -File cutmap\pack-installer.ps1

[CmdletBinding()]
param(
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
    if (-not $found) { throw "找不到 dotnet，请先装 .NET 8 SDK。" }
    $env:PATH = "$found;$env:PATH"
    Write-Host "dotnet：$found（用户级安装，已临时加入 PATH）"
}

$cutmapDir = Join-Path $RepoRoot 'cutmap'
$ctbDir = Join-Path $RepoRoot 'ctb'
$resourcesDir = Join-Path $RepoRoot 'resources'
$distDir = Join-Path $RepoRoot 'dist'

if (-not (Test-Path $ctbDir)) {
    throw "找不到 ctb 目录：$ctbDir（NFR-02 的自包含交付物，没有它装出来的程序跑不起来）"
}

# 版本号的唯一来源是 Directory.Build.props（见该文件注释）。
$version = (Select-String -Path (Join-Path $cutmapDir 'Directory.Build.props') `
    -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { throw '读不到版本号（cutmap\Directory.Build.props）' }

$stage = Join-Path $distDir "payload-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Write-Host '== 1/5 发布 App（自包含 win-x64）=='
dotnet publish (Join-Path $cutmapDir 'src\CutMap.App\CutMap.App.csproj') `
    -c $Configuration -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (App) 失败' }

Write-Host '== 2/5 发布 CLI（同目录，共用运行时）=='
dotnet publish (Join-Path $cutmapDir 'src\CutMap.Cli\CutMap.Cli.csproj') `
    -c $Configuration -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (CLI) 失败' }

Write-Host '== 3/5 组装载荷 =='
Copy-Item $ctbDir (Join-Path $stage 'ctb') -Recurse -Force
if (Test-Path $resourcesDir) {
    # 只带边界数据，不带抓取脚本与缓存——用户不需要它们，缓存还有几百个文件。
    $resStage = Join-Path $stage 'resources'
    New-Item -ItemType Directory -Path $resStage -Force | Out-Null
    Get-ChildItem $resourcesDir -Filter '*.geojson' -File |
        Copy-Item -Destination $resStage -Force
    Write-Host '   已带 resources\*.geojson（行政区划边界）'
} else {
    Write-Host '   未找到 resources\ —— 行政区划选择器在装好的程序里会提示缺数据'
}
Copy-Item (Join-Path $cutmapDir 'THIRD-PARTY-NOTICES.txt') $stage -Force
Copy-Item (Join-Path $cutmapDir 'README.md') $stage -Force

Get-ChildItem $stage -Filter '*.pdb' -Recurse -File | Remove-Item -Force
Get-ChildItem $stage -Filter 'CutMap*.xml' -File | Remove-Item -Force

$payloadZip = Join-Path $distDir "payload-$version.zip"
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Write-Host '   压缩载荷（几分钟，约 400 MB）…'
Add-Type -AssemblyName System.IO.Compression.FileSystem
# includeBaseDirectory 必须是 $false：安装载荷的**根目录就是程序根目录**。
# 传 $true 会把 "payload-0.1.0/" 套在最外层，安装器解压后找不到 CutMap.App.exe。
# （绿色版 zip 那边相反，那个要 $true——用户解压出来应该是一个文件夹。）
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$payloadMb = [math]::Round((Get-Item $payloadZip).Length / 1MB, 1)
Write-Host "   载荷：$payloadMb MB"

Write-Host '== 4/5 发布安装器 =='
$setupStage = Join-Path $distDir "setup-$version"
if (Test-Path $setupStage) { Remove-Item $setupStage -Recurse -Force }
# -p:Version 必须传：安装器用程序集版本填注册表的 DisplayVersion。
# 不传的话它取默认的 1.0.0，与 CutMapInfo.Version 对不上——
# 「应用和功能」里显示的版本会是错的。
dotnet publish (Join-Path $cutmapDir 'src\CutMap.Setup\CutMap.Setup.csproj') `
    -c $Configuration -o $setupStage -p:Version=$version --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (Setup) 失败' }

Write-Host '== 5/5 拼接 =='
$setupExe = Join-Path $distDir "CutMap-$version-Setup-win-x64.exe"
if (Test-Path $setupExe) { Remove-Item $setupExe -Force }

# 流式拼接，不把几百 MB 读进内存。
$out = [System.IO.File]::Create($setupExe)
try {
    $head = [System.IO.File]::OpenRead((Join-Path $setupStage 'CutMap-Setup.exe'))
    try { $head.CopyTo($out) } finally { $head.Close() }

    $body = [System.IO.File]::OpenRead($payloadZip)
    try { $body.CopyTo($out) } finally { $body.Close() }

    $length = [BitConverter]::GetBytes([int64](Get-Item $payloadZip).Length)
    $out.Write($length, 0, $length.Length)
    $magic = [System.Text.Encoding]::ASCII.GetBytes('CUTMAPPA')
    $out.Write($magic, 0, $magic.Length)
}
finally { $out.Close() }

# 清掉中间物：载荷 zip 与两个发布目录都能由本脚本重建。
Remove-Item $payloadZip -Force
Remove-Item $stage -Recurse -Force
Remove-Item $setupStage -Recurse -Force

$exeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host ''
Write-Host "完成：$setupExe（$exeMb MB）"
Write-Host ''
Write-Host '自检建议：'
Write-Host '  1. 双击 setup.exe —— 应当直接开始安装，不弹 UAC'
Write-Host '  2. 装完开始菜单与桌面有快捷方式，程序能起窗'
Write-Host '  3. 设置 → 应用 → 已安装的应用 里能看到 CutMap，能从那里卸载'
Write-Host '  4. 卸载后程序目录应当自行消失（进程退出后由延迟命令删除）'
