# 在 "游戏存档格式 (.dat)" 与 "仓库存储格式 (.json.gz)" 之间转换 Boss 存档.
#
# 用法:
#   # 自检: 把 .dat 解出来再编回去, 逐字节比对, 证明编解码与游戏一致
#   pwsh -File mods/boss-rush/tools/convert-saves.ps1 -SelfTest -Source mods/boss-rush/BossScenes/BossSave
#
#   # 收进仓库: .dat -> .json.gz
#   pwsh -File mods/boss-rush/tools/convert-saves.ps1 -ToGzip -Source <含 .dat 的目录> -Destination <输出目录>
#
#   # 铺到游戏: .json.gz -> .dat
#   pwsh -File mods/boss-rush/tools/convert-saves.ps1 -ToDat -Source mods/boss-rush/BossScenes/BossSave -Destination <游戏插件目录>/BossScenes/BossSave
#
# -ToDat 默认会顺手校验: 生成的 .dat 解回来必须与源 JSON 完全相同.
[CmdletBinding(DefaultParameterSetName = 'ToGzip')]
param(
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$SelfTest,
    [Parameter(Mandatory, ParameterSetName = 'ToGzip')][switch]$ToGzip,
    [Parameter(Mandatory, ParameterSetName = 'ToDat')][switch]$ToDat,
    [Parameter(Mandatory)][string]$Source,
    [string]$Destination,
    [switch]$NoVerify
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'SaveCodec.ps1')

function Resolve-Directory {
    param([string]$Path, [string]$What)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "找不到$What`: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

if ($SelfTest) {
    $sourceDir = Resolve-Directory -Path $Source -What '目录'
    $files = Get-ChildItem -LiteralPath $sourceDir -Filter '*.dat'
    if ($files.Count -eq 0) {
        throw "目录里没有 .dat: $sourceDir"
    }

    $failed = 0
    foreach ($file in $files) {
        $original = [System.IO.File]::ReadAllBytes($file.FullName)
        $json = ConvertFrom-GameSaveBytes -Bytes $original
        $rebuilt = ConvertTo-GameSaveBytes -Json $json

        $same = $rebuilt.Length -eq $original.Length -and
            (Get-ByteFingerprint -Bytes $rebuilt) -eq (Get-ByteFingerprint -Bytes $original)

        if ($same) {
            Write-Host ("[self-test] 一致  {0}  ({1} 字节, JSON {2} 字符)" -f $file.Name, $original.Length, $json.Length)
        }
        else {
            $failed++
            Write-Host ("[self-test] 不一致 {0}: 原始 {1} 字节, 重建 {2} 字节" -f $file.Name, $original.Length, $rebuilt.Length)
        }
    }

    if ($failed -gt 0) {
        throw "自检失败: $failed / $($files.Count) 个文件重建后与原文件不同."
    }

    Write-Host "[self-test] $($files.Count) 个文件全部逐字节一致."
    return
}

$sourceDir = Resolve-Directory -Path $Source -What '源目录'
if (-not $Destination) {
    throw '缺少 -Destination.'
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$destinationDir = (Resolve-Path -LiteralPath $Destination).Path

if ($ToGzip) {
    $files = Get-ChildItem -LiteralPath $sourceDir -Filter '*.dat'
    if ($files.Count -eq 0) {
        throw "源目录里没有 .dat: $sourceDir"
    }

    $rawTotal = 0
    $gzTotal = 0
    foreach ($file in $files) {
        $json = ConvertFrom-GameSaveBytes -Bytes ([System.IO.File]::ReadAllBytes($file.FullName))
        $gz = ConvertTo-GameSaveGzip -Json $json
        $target = Join-Path $destinationDir ($file.BaseName + '.json.gz')
        [System.IO.File]::WriteAllBytes($target, $gz)
        $rawTotal += $file.Length
        $gzTotal += $gz.Length
    }

    Write-Host ("[convert] {0} 份 .dat -> .json.gz: {1:N2} MB -> {2:N2} MB" -f $files.Count, ($rawTotal / 1MB), ($gzTotal / 1MB))
    return
}

$files = Get-ChildItem -LiteralPath $sourceDir -Filter '*.json.gz'
if ($files.Count -eq 0) {
    throw "源目录里没有 .json.gz: $sourceDir"
}

$verified = 0
foreach ($file in $files) {
    $json = ConvertFrom-GameSaveGzip -Bytes ([System.IO.File]::ReadAllBytes($file.FullName))
    $bytes = ConvertTo-GameSaveBytes -Json $json
    $name = $file.Name -replace '\.json\.gz$', ''
    $target = Join-Path $destinationDir ($name + '.dat')
    [System.IO.File]::WriteAllBytes($target, $bytes)

    if (-not $NoVerify) {
        $roundTrip = ConvertFrom-GameSaveBytes -Bytes ([System.IO.File]::ReadAllBytes($target))
        if ($roundTrip -ne $json) {
            throw "校验失败: $name 写出的 .dat 解回来与源 JSON 不一致."
        }

        $verified++
    }
}

if ($NoVerify) {
    Write-Host ("[convert] {0} 份 .json.gz -> .dat (未校验)" -f $files.Count)
}
else {
    Write-Host ("[convert] {0} 份 .json.gz -> .dat, 全部通过解回校验" -f $verified)
}
