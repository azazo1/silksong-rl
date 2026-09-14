<#
分段接力训练.

为什么需要它: 这个环境的采样速度由游戏实时运行决定 (实测约 20 步/秒), 而 PPO 要的步数是百万
量级, 中途还可能因为游戏或连接问题中断. 靠人盯着"跑完一段再启动下一段"不现实, 所以把两件事
写成一个常驻循环:

- 每段训练结束后, 从这一段的 final.zip 接着练下一段;
- 每段开始前确认游戏进程还在, 不在就通过 game-control.ps1 的请求文件把它拉起来.

用法:

    pwsh -File tools/train-chain.ps1 -Segments 5 -Timesteps 100000
    pwsh -File tools/train-chain.ps1 -Segments 5 -CloseReward 0.3

日志: .tmp/train-chain.log (每段的起止), .tmp/train-<实验名>.log (每段的训练输出).
#>
[CmdletBinding()]
param(
    [int]$Segments = 5,
    [int]$Timesteps = 100000,
    [string]$Start = 'runs/bc-moss-mother-v2/bc.zip',
    [string]$Prefix = 'bc-chain',
    [double]$Speed = 6,
    [int]$LogInterval = 2000,
    [int]$CheckpointEvery = 10000,
    [int]$GameBootSeconds = 45,
    [string]$CloseReward = '0',
    [string]$CloseDistance = '0.3'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$trainerDir = Join-Path $repoRoot 'trainer'
$tmpDir = Join-Path $repoRoot '.tmp'
$chainLog = Join-Path $tmpDir 'train-chain.log'
$requestFile = Join-Path $tmpDir 'game-control.request'

New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

function Write-ChainLog {
    param([string]$Message)

    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -LiteralPath $chainLog -Value $line -Encoding utf8
    Write-Host $line
}

function Test-GameRunning {
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'Hollow Knight Silksong' })
    return $running.Count -gt 0
}

function Ensure-Game {
    if (Test-GameRunning) {
        return $true
    }

    Write-ChainLog '游戏没在运行, 通过 game-control 助手请求启动'
    Set-Content -LiteralPath $requestFile -Value 'start' -Encoding utf8

    for ($waited = 0; $waited -lt 120; $waited += 5) {
        Start-Sleep -Seconds 5
        if (Test-GameRunning) {
            Write-ChainLog "游戏已启动, 等 $GameBootSeconds 秒让它进到主菜单"
            Start-Sleep -Seconds $GameBootSeconds
            return $true
        }
    }

    Write-ChainLog '等游戏启动超时, 这一段跳过'
    return $false
}

Write-ChainLog "接力训练开始: $Segments 段 x $Timesteps 步, 起点 $Start"

$resume = $Start
for ($segment = 1; $segment -le $Segments; $segment++) {
    if (-not (Ensure-Game)) {
        break
    }

    $runName = '{0}-{1:d2}' -f $Prefix, $segment
    $segmentLog = Join-Path $tmpDir "train-$runName.log"
    Write-ChainLog "第 $segment/$Segments 段开始: run=$runName resume=$resume"

    $started = Get-Date
    # 贴身奖励是可选塑形项, 只在显式给出时才透传给训练脚本.
    $rewardArgs = @()
    if ($CloseReward -ne '0') {
        $rewardArgs = @('--close-reward', $CloseReward, '--close-distance', $CloseDistance)
        Write-ChainLog "贴身奖励: $CloseReward / 步 (距离阈值 $CloseDistance)"
    }
    Push-Location $trainerDir
    try {
        & uv run silksong-train `
            --resume $resume `
            --finetune `
            --approach 1.0 `
            --timesteps $Timesteps `
            --run-name $runName `
            --speed $Speed `
            --n-steps 1024 `
            --batch-size 256 `
            --log-interval $LogInterval `
            --checkpoint-every $CheckpointEvery `
            @rewardArgs *>&1 |
            Out-File -LiteralPath $segmentLog -Encoding utf8
    }
    finally {
        Pop-Location
    }

    $exitCode = $LASTEXITCODE
    $minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    Write-ChainLog "第 $segment/$Segments 段结束: exit=$exitCode, 用时 $minutes 分钟"

    $next = Join-Path $trainerDir "runs\$runName\final.zip"
    if (Test-Path -LiteralPath $next) {
        $resume = "runs/$runName/final.zip"
        Write-ChainLog "下一段从 $resume 接着练"
    }
    else {
        Write-ChainLog "这一段没有产出 final.zip, 下一段仍从 $resume 开始"
    }

    Start-Sleep -Seconds 5
}

Write-ChainLog '接力训练结束'
