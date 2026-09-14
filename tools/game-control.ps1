<#
游戏启停助手.

设计约束 (刻意做得很小, 避免被滥用):

- 只认两个动词: `start` 与 `stop`, 其它任何内容一律拒绝并记日志.
- `start` 只会启动本仓库游戏实例里的那一个 exe;
  `stop` 只结束可执行文件路径等于该 exe 的进程, 不会碰别的进程.
- 不接受任何形式的命令行/脚本执行, 不读环境变量里的可执行路径, 不联网.

工作方式: 每 PollSeconds 秒看一次请求文件, 内容为 start 或 stop 就执行一次,
并把结果写到日志与结果文件, 然后删掉请求文件.

存活时间: 默认最多活 MaxLifetimeMinutes 分钟 (常驻的提权进程有上限更安全).
加上 -UntilGameExit 则改成"跟着游戏走": 见过游戏跑起来之后, 游戏退出 (崩溃或被关掉)
再过 GameGoneGraceSeconds 秒就自己收工 —— 适合整夜训练, 游戏一挂助手也不必继续占着.

运行方式: 由 DSH 以提权后台任务的方式常驻 (游戏实例本身是提权启动的, 非提权的进程
既启动不了它, 也结束不掉它). 不需要 UAC, 也不需要 sudo 类工具.

请求启停 (普通权限即可, 只是写一个文件):

    'start' | Set-Content .tmp/game-control.request
    'stop'  | Set-Content .tmp/game-control.request
    Get-Content .tmp/game-control-result.txt
#>
[CmdletBinding()]
param(
    [string]$GameDir = '',
    [string]$RequestFile = '',
    [string]$ResultFile = '',
    [string]$LogFile = '',
    [int]$PollSeconds = 1,
    [int]$MaxLifetimeMinutes = 720,
    [switch]$UntilGameExit,
    [int]$GameGoneGraceSeconds = 30
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $GameDir) {
    $GameDir = Join-Path $repoRoot 'game\Hollow Knight Silksong'
}

$exePath = Join-Path $GameDir 'Hollow Knight Silksong.exe'
$tmpDir = Join-Path $repoRoot '.tmp'
if (-not $RequestFile) { $RequestFile = Join-Path $tmpDir 'game-control.request' }
if (-not $ResultFile) { $ResultFile = Join-Path $tmpDir 'game-control-result.txt' }
if (-not $LogFile) { $LogFile = Join-Path $tmpDir 'game-control.log' }

New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

function Write-Log {
    param([string]$Message)

    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -LiteralPath $LogFile -Value $line -Encoding utf8
}

function Write-Result {
    param([string]$Message)

    Set-Content -LiteralPath $ResultFile -Value $Message -Encoding utf8
}

function Get-GameProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exePath }
}

function Start-Game {
    if (-not (Test-Path -LiteralPath $exePath)) {
        return "error 找不到游戏可执行文件: $exePath"
    }

    $running = @(Get-GameProcesses)
    if ($running.Count -gt 0) {
        return "ok 已经在运行 (pid $($running[0].Id))"
    }

    $process = Start-Process -FilePath $exePath -WorkingDirectory $GameDir -PassThru
    return "ok 已启动 (pid $($process.Id))"
}

function Stop-Game {
    $running = @(Get-GameProcesses)
    if ($running.Count -eq 0) {
        return 'ok 本来就没在运行'
    }

    $ids = @()
    foreach ($process in $running) {
        $ids += $process.Id
        Stop-Process -Id $process.Id -Force
    }

    return "ok 已结束 (pid $($ids -join ', '))"
}

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Log "启动失败: 找不到 $exePath"
    throw "找不到游戏可执行文件: $exePath"
}

Write-Log "助手已启动 (pid $PID), 监听 $RequestFile"
Write-Result 'ready'

$deadline = (Get-Date).AddMinutes($MaxLifetimeMinutes)
$seenGame = $false
$goneSince = $null
$exitReason = '助手已到最长存活时间, 退出'

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $PollSeconds

    if ($UntilGameExit) {
        # 见过游戏跑起来之后, 游戏退出 (崩溃或被关掉) 再等一会儿就收工: 训练期间助手的存在意义
        # 就是"能把游戏拉起来", 游戏都没了它也就不用占着了.
        if (@(Get-GameProcesses).Count -gt 0) {
            $seenGame = $true
            $goneSince = $null
        }
        elseif ($seenGame) {
            if (-not $goneSince) {
                $goneSince = Get-Date
            }
            elseif (((Get-Date) - $goneSince).TotalSeconds -ge $GameGoneGraceSeconds) {
                $exitReason = "游戏已退出超过 $GameGoneGraceSeconds 秒, 助手收工"
                break
            }
        }
    }

    if (-not (Test-Path -LiteralPath $RequestFile)) {
        continue
    }

    $request = ''
    try {
        $request = (Get-Content -LiteralPath $RequestFile -Raw -ErrorAction Stop).Trim().ToLowerInvariant()
    }
    catch {
        continue
    }

    if (-not $request) {
        Remove-Item -LiteralPath $RequestFile -Force -ErrorAction SilentlyContinue
        continue
    }

    $outcome = ''
    switch ($request) {
        'start' { $outcome = Start-Game }
        'stop' { $outcome = Stop-Game }
        default { $outcome = "error 只接受 start 与 stop, 收到: $request" }
    }

    Write-Log $outcome
    Write-Result $outcome
    Remove-Item -LiteralPath $RequestFile -Force -ErrorAction SilentlyContinue
}

Write-Log $exitReason
