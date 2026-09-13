# 编译 Object Outlines.
#
# 本机没有安装 .NET SDK, 因此这个脚本直接调用 Visual Studio 自带的 Roslyn csc.exe 编译,
# 并引用游戏目录中的程序集. 若以后装了 .NET SDK, 也可以用 ObjectOutlines.csproj 构建.
#
# 用法:
#   pwsh -File mods/object-outlines/build.ps1
#   pwsh -File mods/object-outlines/build.ps1 -Install
#   pwsh -File mods/object-outlines/build.ps1 -Install -GameDir 'D:\games\steam\common\Hollow Knight Silksong'
#
# -Install 会把编译结果复制到 <游戏目录>/BepInEx/plugins/ObjectOutlines/.
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$outputDir = Join-Path $root 'bin'
$outputDll = Join-Path $outputDir 'ObjectOutlines.dll'
$sourceDir = Join-Path $root 'src'

function Write-Step {
    param([string]$Message)
    Write-Host "[object-outlines] $Message"
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

$compiler = Resolve-Compiler
Write-Step "游戏目录: $gameDir"
Write-Step "编译器: $compiler"

$references = @(
    # 游戏程序集以 netstandard2.1 为目标, 需要 netstandard facade 才能解析基础类型.
    (Join-Path $managedDir 'netstandard.dll'),
    (Join-Path $bepInExCoreDir 'BepInEx.dll'),
    (Join-Path $managedDir 'Assembly-CSharp.dll'),
    (Join-Path $managedDir 'UnityEngine.dll'),
    (Join-Path $managedDir 'UnityEngine.CoreModule.dll'),
    (Join-Path $managedDir 'UnityEngine.Physics2DModule.dll'),
    (Join-Path $managedDir 'UnityEngine.ParticleSystemModule.dll'),
    (Join-Path $managedDir 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managedDir 'UnityEngine.InputLegacyModule.dll')
)

foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "缺少引用程序集: $reference"
    }
}

$sources = Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
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

if ($Install) {
    $pluginDir = Join-Path $gameDir 'BepInEx\plugins\ObjectOutlines'
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    Copy-Item -LiteralPath $outputDll -Destination (Join-Path $pluginDir 'ObjectOutlines.dll') -Force
    Write-Step "已安装到: $pluginDir"
}
