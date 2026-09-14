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
    pwsh -File tools/train-chain.ps1 -Segments 5 -StepFrames 3 -MaxEpisodeSteps 1200

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
    [string]$CloseDistance = '0.3',
    [string]$Approach = '1.0',
    [string]$WhiffPenalty = '0',
    [int]$StepFrames = 0,
    [int]$MaxEpisodeSteps = 0,
    [int]$NSteps = 1024,
    [string]$EntCoef = '0',
    [int]$ClipFps = 0,
    [int]$ClipSeconds = 0,
    [string]$LearningRate = '0',
    [string]$TargetKl = '0',
    [string]$InactivityPenalty = '0',
    [string]$HealReward = '0',
    [string]$BindWastePenalty = '0',
    [string]$DemoAnchor = '',
    [string]$DemoWeight = '0.1',
    [string]$HeightReward = '0',
    [string]$ContactReward = '0',
    [string]$SwingWhiffPenalty = '0',
    [string]$NEpochs = '0',
    [string]$SaveKills = '',
    [string]$ExtraFeature = '',
    [string]$NStepsOverride = '0'
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
    $extraArgs = @()
    if ($CloseReward -ne '0') {
        $extraArgs = @('--close-reward', $CloseReward, '--close-distance', $CloseDistance)
        Write-ChainLog "贴身奖励: $CloseReward / 步 (距离阈值 $CloseDistance)"
    }
    if ($WhiffPenalty -ne '0') {
        $extraArgs += @('--whiff-penalty', $WhiffPenalty)
        Write-ChainLog "挥空惩罚: $WhiffPenalty / 步"
    }
    if ($StepFrames -gt 0) {
        $extraArgs += @('--step-frames', "$StepFrames")
        Write-ChainLog "决策粒度: 每步 $StepFrames 物理帧"
    }
    if ($MaxEpisodeSteps -gt 0) {
        $extraArgs += @('--max-episode-steps', "$MaxEpisodeSteps")
        Write-ChainLog "单回合步数上限: $MaxEpisodeSteps"
    }
    if ($EntCoef -ne '0') {
        $extraArgs += @('--ent-coef', $EntCoef)
        Write-ChainLog "熵系数: $EntCoef"
    }
    if ($ClipFps -gt 0) {
        $extraArgs += @('--clip-fps', "$ClipFps")
        Write-ChainLog "回放帧率: $ClipFps"
    }
    if ($ClipSeconds -gt 0) {
        $extraArgs += @('--clip-seconds', "$ClipSeconds")
        Write-ChainLog "回放缓冲: $ClipSeconds 秒"
    }
    if ($LearningRate -ne '0') {
        $extraArgs += @('--learning-rate', $LearningRate)
        Write-ChainLog "学习率: $LearningRate"
    }
    if ($TargetKl -ne '0') {
        $extraArgs += @('--target-kl', $TargetKl)
        Write-ChainLog "KL 上限: $TargetKl"
    }
    if ($InactivityPenalty -ne '0') {
        $extraArgs += @('--inactivity-penalty', $InactivityPenalty)
        Write-ChainLog "划水惩罚: $InactivityPenalty"
    }
    if ($HealReward -ne '0') {
        $extraArgs += @('--heal-reward', $HealReward)
        Write-ChainLog "回血奖励: $HealReward"
    }
    if ($BindWastePenalty -ne '0') {
        $extraArgs += @('--bind-waste-penalty', $BindWastePenalty)
        Write-ChainLog "空按缚丝惩罚: $BindWastePenalty"
    }
    if ($DemoAnchor -ne '') {
        foreach ($anchor in ($DemoAnchor -split ',')) {
            $trimmed = $anchor.Trim()
            if ($trimmed -ne '') {
                $extraArgs += @('--demo-anchor', $trimmed)
                Write-ChainLog "示范先验: $trimmed (权重 $DemoWeight)"
            }
        }
        $extraArgs += @('--demo-weight', $DemoWeight)
    }
    if ($HeightReward -ne '0') {
        $extraArgs += @('--height-reward', $HeightReward)
        Write-ChainLog "同高奖励: $HeightReward"
    }
    if ($ContactReward -ne '0') {
        $extraArgs += @('--contact-reward', $ContactReward)
        Write-ChainLog "贴脸奖励: $ContactReward"
    }
    if ($SwingWhiffPenalty -ne '0') {
        $extraArgs += @('--swing-whiff-penalty', $SwingWhiffPenalty)
        Write-ChainLog "按刀挥空惩罚: $SwingWhiffPenalty"
    }
    if ($NEpochs -ne '0') {
        $extraArgs += @('--n-epochs', $NEpochs)
        Write-ChainLog "每次更新轮数: $NEpochs"
    }
    if ($SaveKills -ne '') {
        $extraArgs += @('--save-kills', $SaveKills)
        Write-ChainLog "击杀轨迹 (自模仿) 存到: $SaveKills"
    }
    if ($ExtraFeature -ne '') {
        $extraArgs += @('--extra-feature', $ExtraFeature)
        Write-ChainLog "工程化特征: $ExtraFeature"
    }
    Push-Location $trainerDir
    try {
        & uv run silksong-train `
            --resume $resume `
            --finetune `
            --approach $Approach `
            --timesteps $Timesteps `
            --run-name $runName `
            --speed $Speed `
            --n-steps $NSteps `
            --batch-size 256 `
            --log-interval $LogInterval `
            --checkpoint-every $CheckpointEvery `
            @extraArgs *>&1 |
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
