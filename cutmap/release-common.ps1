# Shared release helpers; compatible with Windows PowerShell 5.1.
function Use-CutMapSdk {
    $candidateDirs = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
        (Join-Path $env:USERPROFILE '.dotnet'),
        'C:\Program Files\dotnet'
    )
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidateDirs = @((Split-Path -Parent $onPath.Source)) + $candidateDirs }
    foreach ($candidateDir in ($candidateDirs | Select-Object -Unique)) {
        $executable = Join-Path $candidateDir 'dotnet.exe'
        if (-not (Test-Path -LiteralPath $executable)) { continue }
        $sdkList = & $executable --list-sdks
        if ($LASTEXITCODE -eq 0 -and ($sdkList -match '^8\.')) {
            $env:PATH = "$candidateDir;$env:PATH"
            Write-Host "SDK: $executable"
            return
        }
    }
    throw 'A .NET 8 SDK is required to build CutMap (a runtime alone is insufficient).'
}

# Windows PowerShell 运行环境可能缺少 Get-FileHash（精简系统映像尤其常见）。
# 发布清单不能因此失效，改用 .NET BCL 计算同一份 SHA-256。
function Get-CutMapSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Write-CutMapBuildInfo([string]$Stage, [string]$Version, [string]$Revision) {
    $files = [ordered]@{}
    foreach ($relative in @('CutMap.App.dll', 'CutMap.Core.dll', 'CutMap.Cli.dll',
        'ctb/bin/ctb-tile.exe', 'resources/中国_省.geojson', 'resources/中国_市.geojson', 'resources/中国_县.geojson', 'resources/boundary-manifest.json')) {
        $files[$relative] = Get-CutMapSha256 (Join-Path $Stage $relative)
    }
    $data = [ordered]@{ version = $Version; revision = $Revision; built_at_utc = [DateTime]::UtcNow.ToString('o'); sha256 = $files }
    $json = $data | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $Stage 'build-info.json'), $json, (New-Object Text.UTF8Encoding($false)))
}
