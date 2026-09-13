# 启动 game/ 下的丝之歌隔离子实例.
#
# 用法:
#   pwsh -File game/launch-instance.ps1          # 后台启动, 立即返回
#   pwsh -File game/launch-instance.ps1 -Wait    # 等游戏退出, 然后打印 BepInEx 日志尾部
#
# 说明: 实例里的 BepInEx 会加载 <实例>/BepInEx/plugins 下的 mod,
# 与 Steam 安装的那一份互不影响; 但存档目录由 Windows 用户决定, 两份实例共用.

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

if (-not (Get-Process -Name 'steam' -ErrorAction SilentlyContinue)) {
    Write-Host '[instance] 提示: Steam 客户端没有运行, 建议先启动 Steam 再进游戏.'
}

Write-Host "[instance] 启动: $exe"
$process = Start-Process -FilePath $exe -WorkingDirectory $Target -PassThru

if (-not $Wait) {
    Write-Host "[instance] 进程号 $($process.Id), 日志: $(Join-Path $Target 'BepInEx\LogOutput.log')"
    return
}

Write-Host "[instance] 等待游戏退出 ..."
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
