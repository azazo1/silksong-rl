# 启动 game/ 下的丝之歌隔离子实例.
#
# 用法:
#   pwsh -File game/launch-instance.ps1          # 后台启动, 立即返回
#   pwsh -File game/launch-instance.ps1 -Wait    # 等游戏退出, 然后打印 BepInEx 日志尾部
#
# 实例已经做过两层隔离:
#   - 存档与游戏设置: 由实例自己的 _Data/app.info 决定, 落在 LocalLow\<公司名>\<产品名>, 与源安装分开.
#   - Steam 接入: steam_api64.dll 被改名为 .disabled 时, 游戏不会连 Steam,
#     测试期间的成就/云存档/游戏时长都不会落到你的账号上; 需要恢复就用 prepare-instance.ps1 -KeepSteam.

[CmdletBinding()]
param(
    [string]$Target = (Join-Path $PSScriptRoot 'Hollow Knight Silksong'),
    [switch]$Wait,
    [int]$LogTailLines = 40
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $Target 'Hollow Knight Silksong.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "找不到实例: $exe, 先运行 pwsh -File game/prepare-instance.ps1"
}

$dataDirectory = Get-ChildItem -LiteralPath $Target -Force -Directory |
    Where-Object { $_.Name -like '*_Data' } |
    Select-Object -First 1
$appInfo = Join-Path $dataDirectory.FullName 'app.info'
if (Test-Path -LiteralPath $appInfo) {
    $lines = @(Get-Content -LiteralPath $appInfo)
    $saveRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\$($lines[0].Trim())\$($lines[1].Trim())"
    Write-Host "[instance] 存档与设置目录: $saveRoot"
}

$steamDll = Join-Path $dataDirectory.FullName 'Plugins\x86_64\steam_api64.dll'
if (Test-Path -LiteralPath $steamDll) {
    Write-Host '[instance] Steam 接入: 启用 (steam_api64.dll 在位)'
}
else {
    Write-Host '[instance] Steam 接入: 已断开 (steam_api64.dll 被改名)'
}

Write-Host "[instance] 启动: $exe"
$process = Start-Process -FilePath $exe -WorkingDirectory $Target -PassThru

if (-not $Wait) {
    Write-Host "[instance] 进程号 $($process.Id), 日志: $(Join-Path $Target 'BepInEx\LogOutput.log')"
    return
}

Write-Host '[instance] 等待游戏退出 ...'
$process.WaitForExit()
Write-Host "[instance] 游戏已退出 (exit=$($process.ExitCode))"

$log = Join-Path $Target 'BepInEx\LogOutput.log'
if (Test-Path -LiteralPath $log) {
    Write-Host "[instance] BepInEx 日志尾部 ($log):"
    Get-Content -LiteralPath $log -Tail $LogTailLines | ForEach-Object { "  $_" }
}
else {
    Write-Host "[instance] 没有找到 BepInEx 日志: $log"
}
