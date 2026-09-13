# 创建或刷新一份与源安装隔离的丝之歌游戏子实例.
#
# 实例目录: <本脚本所在目录>/Hollow Knight Silksong/
#   - 共享 (目录联接, 不占额外空间): Hollow Knight Silksong_Data, MonoBleedingEdge, D3D12
#   - 独立 (真实副本): Hollow Knight Silksong.exe, UnityPlayer.dll, winhttp.dll 等根目录文件, BepInEx/
#   - 因此往实例里装 mod, 改 BepInEx 配置, 写日志都只影响这一份, 源安装保持干净.
#
# 用法:
#   pwsh -File game/prepare-instance.ps1                   # 首次创建; 之后重跑只补齐缺失内容
#   pwsh -File game/prepare-instance.ps1 -RefreshBinaries  # 源安装更新后, 覆盖刷新可执行文件与 BepInEx 基础文件
#   pwsh -File game/prepare-instance.ps1 -FullCopy         # 连数据目录也完整复制 (约 7.8 GB), 隔离最彻底
#   pwsh -File game/prepare-instance.ps1 -Force            # 删除已有实例后重建
#
# 注意: 共享目录是只读用的, 如果某个 mod 会往数据目录里写文件, 请改用 -FullCopy.

[CmdletBinding()]
param(
    [string]$Source = 'D:\games\steam\common\Hollow Knight Silksong',
    [string]$Target = (Join-Path $PSScriptRoot 'Hollow Knight Silksong'),
    [switch]$FullCopy,
    [switch]$RefreshBinaries,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# 这些目录由游戏自己携带且运行期间不会被修改, 默认用目录联接共享.
$sharedDirectoryNames = @('Hollow Knight Silksong_Data', 'MonoBleedingEdge', 'D3D12')

# 这些名字不复制, 属于运行期产物.
$skipFileNames = @('LogOutput.log')

function Write-Step {
    param([string]$Message)
    Write-Host "[instance] $Message"
}

function Resolve-SourceDirectory {
    param([string]$Path)

    $exe = Join-Path $Path 'Hollow Knight Silksong.exe'
    $managed = Join-Path $Path 'Hollow Knight Silksong_Data\Managed'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "源安装看起来不对, 找不到: $exe"
    }
    if (-not (Test-Path -LiteralPath $managed)) {
        throw "源安装看起来不对, 找不到: $managed"
    }
    return (Get-Item -LiteralPath $Path).FullName
}

function Remove-InstanceTree {
    param([string]$Path)

    # 先只删除联接本身, 避免递归删除穿透到源安装.
    $links = Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.LinkType -in @('Junction', 'SymbolicLink') }
    $links = @($links | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($link in $links) {
        Write-Step "移除联接: $($link.FullName)"
        [System.IO.Directory]::Delete($link.FullName, $false)
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Test-LinkTarget {
    param([string]$LinkPath, [string]$TargetPath)

    $item = Get-Item -LiteralPath $LinkPath -Force -ErrorAction SilentlyContinue
    if (-not $item) {
        return $false
    }
    if ($item.LinkType -notin @('Junction', 'SymbolicLink')) {
        return $false
    }
    $targets = @($item.Target)
    return ($targets -contains $TargetPath)
}

function New-DirectoryLink {
    param([string]$LinkPath, [string]$TargetPath)

    if (Test-Path -LiteralPath $LinkPath) {
        $item = Get-Item -LiteralPath $LinkPath -Force
        if ($item.LinkType -in @('Junction', 'SymbolicLink')) {
            if (Test-LinkTarget -LinkPath $LinkPath -TargetPath $TargetPath) {
                Write-Step "联接已存在: $(Split-Path -Leaf $LinkPath)"
                return
            }
            Write-Step "联接目标不符, 重建: $(Split-Path -Leaf $LinkPath)"
            [System.IO.Directory]::Delete($LinkPath, $false)
        }
        else {
            throw "目标已存在且不是联接, 请先处理: $LinkPath"
        }
    }

    $parent = Split-Path -Parent $LinkPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    New-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null
    Write-Step "新建联接: $(Split-Path -Leaf $LinkPath) -> $TargetPath"
}

function Copy-DirectoryContents {
    param(
        [string]$FromDirectory,
        [string]$ToDirectory,
        [switch]$Overwrite
    )

    $directories = Get-ChildItem -LiteralPath $FromDirectory -Recurse -Force -Directory |
        Where-Object { -not $_.LinkType }
    foreach ($directory in $directories) {
        $relative = $directory.FullName.Substring($FromDirectory.Length).TrimStart('\')
        $destination = Join-Path $ToDirectory $relative
        if (-not (Test-Path -LiteralPath $destination)) {
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
        }
    }

    $copied = 0
    $skipped = 0
    $files = Get-ChildItem -LiteralPath $FromDirectory -Recurse -Force -File
    foreach ($file in $files) {
        if ($skipFileNames -contains $file.Name) {
            continue
        }
        $relative = $file.FullName.Substring($FromDirectory.Length).TrimStart('\')
        $destination = Join-Path $ToDirectory $relative
        if ((Test-Path -LiteralPath $destination) -and -not $Overwrite) {
            $skipped++
            continue
        }
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $copied++
    }

    Write-Step "复制 $(Split-Path -Leaf $FromDirectory): 新增/覆盖 $copied 个文件, 保留已有 $skipped 个文件"
}

$source = Resolve-SourceDirectory -Path $Source
$target = $Target

Write-Step "源安装: $source"
Write-Step "实例目录: $target"

if ($Force -and (Test-Path -LiteralPath $target)) {
    Write-Step "按 -Force 重建, 先删除已有实例"
    Remove-InstanceTree -Path $target
}

if (-not (Test-Path -LiteralPath $target)) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
}

foreach ($name in $sharedDirectoryNames) {
    $from = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $from)) {
        Write-Step "源安装没有 $name, 跳过"
        continue
    }
    $to = Join-Path $target $name
    if ($FullCopy) {
        if (-not (Test-Path -LiteralPath $to)) {
            New-Item -ItemType Directory -Force -Path $to | Out-Null
        }
        Copy-DirectoryContents -FromDirectory $from -ToDirectory $to -Overwrite:$RefreshBinaries
    }
    else {
        New-DirectoryLink -LinkPath $to -TargetPath $from
    }
}

$rootFiles = Get-ChildItem -LiteralPath $source -Force -File
$rootCopied = 0
foreach ($file in $rootFiles) {
    $destination = Join-Path $target $file.Name
    if ((Test-Path -LiteralPath $destination) -and -not $RefreshBinaries) {
        continue
    }
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    $rootCopied++
}
Write-Step "复制根目录文件: $rootCopied 个"

$bepInExFrom = Join-Path $source 'BepInEx'
if (Test-Path -LiteralPath $bepInExFrom) {
    $bepInExTo = Join-Path $target 'BepInEx'
    if (-not (Test-Path -LiteralPath $bepInExTo)) {
        New-Item -ItemType Directory -Force -Path $bepInExTo | Out-Null
    }
    Copy-DirectoryContents -FromDirectory $bepInExFrom -ToDirectory $bepInExTo -Overwrite:$RefreshBinaries
}

$exe = Join-Path $target 'Hollow Knight Silksong.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "实例不完整, 找不到: $exe"
}

Write-Step '实例就绪.'
Write-Step "启动: pwsh -File game/launch-instance.ps1"
Write-Step "装 mod: pwsh -File mods/object-outlines/build.ps1 -Install -GameDir '$target'"
