# 反编译丝之歌的游戏程序集, 输出到本目录.
#
# 产物只用于阅读参考: 反编译出的 C# 无法重新编译, 也不进入版本库.
# 反编译整个 Assembly-CSharp 大约需要一到两分钟, 期间反编译器不打印进度.
#
# 用法:
#   pwsh -File disassembly/decompile.ps1
#   pwsh -File disassembly/decompile.ps1 -GameDir 'D:\games\steam\common\Hollow Knight Silksong'
#   pwsh -File disassembly/decompile.ps1 -Assemblies TeamCherry.TK2D.dll,PlayMaker.dll
[CmdletBinding()]
param(
    [string]$GameDir,
    [string[]]$Assemblies,
    [string]$Tool
)

$ErrorActionPreference = 'Stop'

$outputDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $outputDir

$defaultAssemblies = @(
    'Assembly-CSharp.dll'
    'Assembly-CSharp-firstpass.dll'
    'TeamCherry.SharedUtils.dll'
    'TeamCherry.Splines.dll'
    'TeamCherry.NestedFadeGroup.dll'
    'TeamCherry.Localization.dll'
    'TeamCherry.Cinematics.dll'
    'TeamCherry.TK2D.dll'
    'TeamCherry.BuildBot.dll'
)

function Write-Step {
    param([string]$Message)
    Write-Host "[decompile] $Message"
}

function Resolve-GameDir {
    param([string]$Explicit)

    if ($Explicit) {
        return $Explicit
    }

    $instance = Join-Path $repoRoot 'game/Hollow Knight Silksong'
    if (Test-Path -LiteralPath (Join-Path $instance 'Hollow Knight Silksong_Data/Managed')) {
        return $instance
    }

    throw '找不到游戏目录: 请用 -GameDir 指定游戏根目录.'
}

function Resolve-Tool {
    param([string]$Explicit)

    if ($Explicit) {
        return $Explicit
    }

    $command = Get-Command 'dnSpy.Console.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path $env:USERPROFILE 'scoop/shims/dnSpy.Console.exe')
        (Join-Path $env:USERPROFILE 'scoop/apps/dnspyex/current/dnSpy.Console.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw '找不到 dnSpy.Console.exe: 用 -Tool 指定, 或先 scoop install dnspyex.'
}

$gameDir = Resolve-GameDir -Explicit $GameDir
$managedDir = Join-Path $gameDir 'Hollow Knight Silksong_Data/Managed'
if (-not (Test-Path -LiteralPath $managedDir)) {
    throw "游戏目录看起来不对, 找不到: $managedDir"
}

$compiler = Resolve-Tool -Explicit $Tool
$names = if ($Assemblies) { $Assemblies } else { $defaultAssemblies }

$paths = @()
foreach ($name in $names) {
    $path = Join-Path $managedDir $name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "找不到程序集: $path"
    }
    $paths += $path
}

Write-Step "游戏目录: $gameDir"
Write-Step "反编译器: $compiler"
Write-Step "输出目录: $outputDir"
Write-Step "开始反编译 $($paths.Count) 个程序集, 请稍候 ..."

$start = Get-Date
& $compiler --asm-path $managedDir --sdk-project --no-tokens --no-resources --sln-name 'Silksong-decompiled.sln' -o $outputDir @paths
if ($LASTEXITCODE -ne 0) {
    throw "dnSpy.Console 反编译失败 (exit=$LASTEXITCODE)"
}
$elapsed = (Get-Date) - $start

foreach ($path in $paths) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($path)
    $dir = Join-Path $outputDir $name
    $count = (Get-ChildItem -LiteralPath $dir -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue).Count
    Write-Step ("{0,-28} {1,5} 个源文件" -f $name, $count)
}

Write-Step ("完成, 耗时 {0:N1} 秒." -f $elapsed.TotalSeconds)
