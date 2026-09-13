# 编译 BossRush 的 BepInEx 版本.
#
# 本机没有安装 .NET SDK, 因此直接调用 Visual Studio 自带的 Roslyn csc.exe 编译,
# 引用游戏目录中的程序集与 BepInEx 自带的 Harmony.
# 若以后装了 .NET SDK, 也可以按 ObjectOutlines.csproj 的形式改用 dotnet build.
#
# 用法:
#   pwsh -File mods/boss-rush/build.ps1
#   pwsh -File mods/boss-rush/build.ps1 -Install
#   pwsh -File mods/boss-rush/build.ps1 -Install -GameDir 'D:\games\steam\common\Hollow Knight Silksong'
#
# -Install 会把编译结果复制到 <游戏目录>/BepInEx/plugins/BossRush/,
# 并把本目录下 BossScenes 里的 Boss 清单与 42 个存档一并铺到该目录的同名子目录下.
# 资源随仓库提供, 安装过程不需要联网, 也不需要额外的压缩包.
#
# -GameDir 可以给相对路径, 相对本 mod 目录解析,
# 与 MSBuild 导入 SilksongPath.props 时的基准一致.
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Install,
    [string]$Tool
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$outputDir = Join-Path $root 'bin'
$outputDll = Join-Path $outputDir 'BossRush.dll'
$sourceDir = Join-Path $root 'src'
$pluginFolderName = 'BossRush'

function Write-Step {
    param([string]$Message)

    Write-Host "[boss-rush] $Message"
}

function Resolve-RelativePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    # 相对路径按本 mod 目录解析, 与 MSBuild 导入 SilksongPath.props 时的基准保持一致.
    return [System.IO.Path]::GetFullPath((Join-Path $root $Path))
}

function Resolve-GameDir {
    param([string]$Explicit)

    if ($Explicit) {
        return (Resolve-RelativePath $Explicit)
    }

    $propsPath = Join-Path $root 'SilksongPath.props'
    if (Test-Path -LiteralPath $propsPath) {
        $content = Get-Content -LiteralPath $propsPath -Raw
        $match = [regex]::Match($content, '<GameDir>\s*([^<]+?)\s*</GameDir>')
        if ($match.Success) {
            return (Resolve-RelativePath $match.Groups[1].Value)
        }
    }

    throw '找不到游戏目录: 请用 -GameDir 指定, 或先准备 SilksongPath.props.'
}

function Resolve-Compiler {
    param([string]$Explicit)

    if ($Explicit) {
        return $Explicit
    }

    $candidates = Get-ChildItem -Path 'C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio' -Filter 'csc.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'Roslyn' } |
        Sort-Object FullName -Descending

    if ($candidates.Count -gt 0) {
        return $candidates[0].FullName
    }

    $frameworkCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (Test-Path -LiteralPath $frameworkCompiler) {
        return $frameworkCompiler
    }

    throw '找不到 csc.exe: 需要 Visual Studio (Roslyn) 或 .NET Framework 自带的编译器.'
}

$gameDir = Resolve-GameDir -Explicit $GameDir
$managedDir = Join-Path $gameDir 'Hollow Knight Silksong_Data\Managed'
$bepInExCoreDir = Join-Path $gameDir 'BepInEx\core'

if (-not (Test-Path -LiteralPath $managedDir)) {
    throw "游戏目录看起来不对, 找不到: $managedDir"
}

$compiler = Resolve-Compiler -Explicit $Tool
Write-Step "游戏目录: $gameDir"
Write-Step "编译器: $compiler"

$references = @(
    # 游戏程序集以 netstandard2.1 为目标, 需要 netstandard facade 才能解析基础类型.
    (Join-Path $managedDir 'netstandard.dll'),
    (Join-Path $bepInExCoreDir 'BepInEx.dll'),
    (Join-Path $bepInExCoreDir '0Harmony.dll'),
    (Join-Path $managedDir 'Assembly-CSharp.dll'),
    (Join-Path $managedDir 'TeamCherry.SharedUtils.dll'),
    (Join-Path $managedDir 'Newtonsoft.Json.dll'),
    (Join-Path $managedDir 'UnityEngine.dll'),
    (Join-Path $managedDir 'UnityEngine.CoreModule.dll'),
    (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managedDir 'UnityEngine.InputLegacyModule.dll'),
    (Join-Path $managedDir 'UnityEngine.TextRenderingModule.dll'),
    (Join-Path $managedDir 'UnityEngine.UIModule.dll'),
    (Join-Path $managedDir 'UnityEngine.Physics2DModule.dll')
)

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "缺少引用程序集: $reference"
    }
}

$sources = Get-ChildItem -LiteralPath $sourceDir -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) {
    throw "没有找到源文件: $sourceDir"
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$arguments = @(
    '/nologo'
    '/target:library'
    '/langversion:7.3'
    '/optimize+'
    '/deterministic+'
    "/out:$outputDll"
)
foreach ($reference in $references) {
    $arguments += "/r:$reference"
}
$arguments += $sources

Write-Step "编译 $($sources.Count) 个源文件 ..."
& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "csc 编译失败 (exit=$LASTEXITCODE)"
}

Write-Step "产物: $outputDll"

if (-not $Install) {
    return
}

$pluginDir = Join-Path $gameDir "BepInEx\plugins\$pluginFolderName"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $outputDll -Destination (Join-Path $pluginDir 'BossRush.dll') -Force
Write-Step "已安装插件: $pluginDir"

$sourceScenes = Join-Path $root 'BossScenes'
if (-not (Test-Path -LiteralPath $sourceScenes)) {
    throw "找不到随仓库提供的 Boss 资源目录: $sourceScenes"
}

$sourceSaves = Join-Path $sourceScenes 'BossSave'
$packedSaves = Get-ChildItem -LiteralPath $sourceSaves -Filter '*.json.gz'
if ($packedSaves.Count -eq 0) {
    throw "Boss 存档目录里没有 .json.gz: $sourceSaves"
}

. (Join-Path $root 'tools\SaveCodec.ps1')

$targetScenes = Join-Path $pluginDir 'BossScenes'
$targetSaves = Join-Path $targetScenes 'BossSave'
if (Test-Path -LiteralPath $targetScenes) {
    Remove-Item -LiteralPath $targetScenes -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $targetSaves | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceScenes 'BossSceneConfig.json') -Destination $targetScenes -Force

# 仓库里存的是 gzip 过的 JSON, 游戏只认 .dat, 这里在安装期还原.
$decoded = 0
foreach ($packed in $packedSaves) {
    $json = ConvertFrom-GameSaveGzip -Bytes ([System.IO.File]::ReadAllBytes($packed.FullName))
    $bytes = ConvertTo-GameSaveBytes -Json $json
    $name = $packed.Name -replace '\.json\.gz$', ''
    [System.IO.File]::WriteAllBytes((Join-Path $targetSaves ($name + '.dat')), $bytes)
    $decoded++
    if ($decoded % 10 -eq 0) {
        Write-Step "已还原 $decoded / $($packedSaves.Count) 份存档 ..."
    }
}

Write-Step "已铺设 Boss 清单与 $decoded 个 Boss 存档: $targetScenes"
