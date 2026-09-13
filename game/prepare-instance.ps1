# 创建或刷新一份与源安装隔离的丝之歌游戏子实例.
#
# 实例目录: <本脚本所在目录>/Hollow Knight Silksong/
#   - 共享 (目录联接, 不占额外空间): <exe>_Data 下的 Managed/Resources/StreamingAssets, 以及根目录的 MonoBleedingEdge/D3D12
#   - 独立 (真实副本): 可执行文件, doorstop 文件, BepInEx/, <exe>_Data 下的配置与小数据文件
#   - 隔离存档: 改写实例的 _Data/app.info 里的公司名 (默认 "Team Cherry Mod"),
#     实例的存档与游戏设置落到 %USERPROFILE%\AppData\LocalLow\<公司名>\<产品名>, 与源安装互不影响
#   - 隔离 Steam: 默认把实例的 steam_api64.dll 改名为 steam_api64.dll.disabled,
#     使 SteamAPI.Init 失败, 测试期间的成就/云存档/时长都不会落到你的账号上
#
# 用法:
#   pwsh -File game/prepare-instance.ps1                   # 首次创建; 之后重跑只补齐缺失内容
#   pwsh -File game/prepare-instance.ps1 -RefreshBinaries  # 源安装更新后, 覆盖刷新可执行文件与 BepInEx 基础文件
#   pwsh -File game/prepare-instance.ps1 -FullCopy         # 连数据目录也完整复制 (约 7.8 GB), 隔离最彻底
#   pwsh -File game/prepare-instance.ps1 -KeepSteam        # 保留 Steam 接入, 只做存档隔离
#   pwsh -File game/prepare-instance.ps1 -Force            # 删除已有实例后重建
#
# 注意: 共享目录是只读用的, 如果某个 mod 会往数据目录里写文件, 请改用 -FullCopy.

[CmdletBinding()]
param(
    [string]$Source = 'D:\games\steam\common\Hollow Knight Silksong',
    [string]$Target = (Join-Path $PSScriptRoot 'Hollow Knight Silksong'),
    [string]$CompanyName = 'Team Cherry Mod',
    [switch]$KeepSteam,
    [switch]$FullCopy,
    [switch]$RefreshBinaries,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# 实例根目录下由游戏自带且运行期不修改的目录.
$sharedRootNames = @('MonoBleedingEdge', 'D3D12')

# <exe>_Data 下由游戏自带且运行期不修改的目录.
$sharedDataNames = @('Managed', 'Resources', 'StreamingAssets')

# 运行期产物, 不复制.
$skipFileNames = @('LogOutput.log')

$steamDllName = 'steam_api64.dll'

function Write-Step {
    param([string]$Message)
    Write-Host "[instance] $Message"
}

function Resolve-SourceDirectory {
    param([string]$Path)

    $exe = Join-Path $Path 'Hollow Knight Silksong.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "源安装看起来不对, 找不到: $exe"
    }
    $data = Get-ChildItem -LiteralPath $Path -Force -Directory |
        Where-Object { $_.Name -like '*_Data' } |
        Select-Object -First 1
    if (-not $data) {
        throw "源安装看起来不对, 找不到 *_Data 目录: $Path"
    }
    $managed = Join-Path $data.FullName 'Managed'
    if (-not (Test-Path -LiteralPath $managed)) {
        throw "源安装看起来不对, 找不到: $managed"
    }
    return (Get-Item -LiteralPath $Path).FullName
}

function Remove-InstanceTree {
    param([string]$Path)

    # 先只删联接本身, 避免递归删除穿透到源安装.
    $links = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.LinkType -in @('Junction', 'SymbolicLink') } |
        Sort-Object { $_.FullName.Length } -Descending)
    foreach ($link in $links) {
        Write-Step "移除联接: $($link.FullName)"
        [System.IO.Directory]::Delete($link.FullName, $false)
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Test-LinkTarget {
    param([string]$LinkPath, [string]$TargetPath)

    $item = Get-Item -LiteralPath $LinkPath -Force -ErrorAction SilentlyContinue
    if (-not $item -or $item.LinkType -notin @('Junction', 'SymbolicLink')) {
        return $false
    }
    return (@($item.Target) -contains $TargetPath)
}

function Remove-LinkIfPresent {
    param([string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and $item.LinkType -in @('Junction', 'SymbolicLink')) {
        Write-Step "移除联接: $Path"
        [System.IO.Directory]::Delete($Path, $false)
    }
}

function Ensure-RealDirectory {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.LinkType -in @('Junction', 'SymbolicLink')) {
            Remove-LinkIfPresent -Path $Path
        }
        elseif (-not $item.PSIsContainer) {
            throw "同名文件挡住了目录, 请先处理: $Path"
        }
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function New-DirectoryLink {
    param([string]$LinkPath, [string]$TargetPath)

    Remove-LinkIfPresent -Path $LinkPath
    if (Test-Path -LiteralPath $LinkPath) {
        throw "目标已存在且不是联接, 请先处理: $LinkPath"
    }

    $parent = Split-Path -Parent $LinkPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    New-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null
    Write-Step "新建联接: $(Split-Path -Leaf $LinkPath) -> $TargetPath"
}

function Copy-FileIfNeeded {
    param(
        [string]$From,
        [string]$To,
        [switch]$Overwrite
    )

    if ((Test-Path -LiteralPath $To) -and -not $Overwrite) {
        return $false
    }
    $parent = Split-Path -Parent $To
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    Copy-Item -LiteralPath $From -Destination $To -Force
    return $true
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
    $kept = 0
    foreach ($file in (Get-ChildItem -LiteralPath $FromDirectory -Recurse -Force -File)) {
        if ($skipFileNames -contains $file.Name) {
            continue
        }
        $relative = $file.FullName.Substring($FromDirectory.Length).TrimStart('\')
        if (Copy-FileIfNeeded -From $file.FullName -To (Join-Path $ToDirectory $relative) -Overwrite:$Overwrite) {
            $copied++
        }
        else {
            $kept++
        }
    }

    Write-Step "复制 $(Split-Path -Leaf $FromDirectory): 新增/覆盖 $copied 个, 保留已有 $kept 个"
}

function Set-InstanceAppInfo {
    param(
        [string]$SourceAppInfo,
        [string]$InstanceAppInfo,
        [string]$CompanyName
    )

    $lines = @(Get-Content -LiteralPath $SourceAppInfo)
    if ($lines.Count -lt 2) {
        throw "app.info 格式不认识: $SourceAppInfo"
    }
    $productName = $lines[1].Trim()
    $content = "$CompanyName`n$productName`n"
    $current = if (Test-Path -LiteralPath $InstanceAppInfo) { Get-Content -LiteralPath $InstanceAppInfo -Raw } else { '' }
    if ($current -ne $content) {
        Set-Content -LiteralPath $InstanceAppInfo -Value $content -NoNewline -Encoding utf8
        Write-Step "改写 app.info: 公司名 $CompanyName, 产品名 $productName"
    }
    return $productName
}

function Set-SteamDllState {
    param(
        [string]$PluginsDirectory,
        [switch]$KeepSteam
    )

    $active = Join-Path $PluginsDirectory $steamDllName
    $disabled = "$active.disabled"

    if ($KeepSteam) {
        if ((Test-Path -LiteralPath $disabled) -and -not (Test-Path -LiteralPath $active)) {
            Move-Item -LiteralPath $disabled -Destination $active -Force
            Write-Step '恢复 steam_api64.dll, 实例恢复 Steam 接入'
        }
        return
    }

    if (Test-Path -LiteralPath $active) {
        Move-Item -LiteralPath $active -Destination $disabled -Force
        Write-Step '禁用 steam_api64.dll (改名成 steam_api64.dll.disabled)'
    }
    else {
        Write-Step 'steam_api64.dll 已处于禁用状态'
    }
}

$source = Resolve-SourceDirectory -Path $Source
$target = $Target

$dataName = (Get-ChildItem -LiteralPath $source -Force -Directory |
    Where-Object { $_.Name -like '*_Data' } |
    Select-Object -First 1).Name

Write-Step "源安装: $source"
Write-Step "实例目录: $target"

if ($Force -and (Test-Path -LiteralPath $target)) {
    Write-Step '按 -Force 重建, 先删除已有实例'
    Remove-InstanceTree -Path $target
}

Ensure-RealDirectory -Path $target

foreach ($name in $sharedRootNames) {
    $from = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $from)) {
        Write-Step "源安装没有 $name, 跳过"
        continue
    }
    $to = Join-Path $target $name
    if ($FullCopy) {
        Ensure-RealDirectory -Path $to
        Copy-DirectoryContents -FromDirectory $from -ToDirectory $to -Overwrite:$RefreshBinaries
    }
    else {
        New-DirectoryLink -LinkPath $to -TargetPath $from
    }
}

$sourceData = Join-Path $source $dataName
$instanceData = Join-Path $target $dataName
Ensure-RealDirectory -Path $instanceData

foreach ($name in $sharedDataNames) {
    $from = Join-Path $sourceData $name
    if (-not (Test-Path -LiteralPath $from)) {
        Write-Step "源安装没有 $dataName\$name, 跳过"
        continue
    }
    $to = Join-Path $instanceData $name
    if ($FullCopy) {
        Ensure-RealDirectory -Path $to
        Copy-DirectoryContents -FromDirectory $from -ToDirectory $to -Overwrite:$RefreshBinaries
    }
    else {
        New-DirectoryLink -LinkPath $to -TargetPath $from
    }
}

# _Data 顶层的小文件 (app.info, globalgamemanagers 等) 走真实副本.
$dataFileCount = 0
foreach ($file in (Get-ChildItem -LiteralPath $sourceData -Force -File)) {
    if (Copy-FileIfNeeded -From $file.FullName -To (Join-Path $instanceData $file.Name) -Overwrite:$RefreshBinaries) {
        $dataFileCount++
    }
}
Write-Step "复制 $dataName 顶层文件: $dataFileCount 个"

$sourcePlugins = Join-Path $sourceData 'Plugins'
$instancePlugins = Join-Path $instanceData 'Plugins'
if (Test-Path -LiteralPath $sourcePlugins) {
    Ensure-RealDirectory -Path $instancePlugins
    Copy-DirectoryContents -FromDirectory $sourcePlugins -ToDirectory $instancePlugins -Overwrite:$RefreshBinaries
}

$productName = Set-InstanceAppInfo -SourceAppInfo (Join-Path $sourceData 'app.info') `
    -InstanceAppInfo (Join-Path $instanceData 'app.info') -CompanyName $CompanyName

Set-SteamDllState -PluginsDirectory (Join-Path $instancePlugins 'x86_64') -KeepSteam:$KeepSteam

$rootCopied = 0
foreach ($file in (Get-ChildItem -LiteralPath $source -Force -File)) {
    if (Copy-FileIfNeeded -From $file.FullName -To (Join-Path $target $file.Name) -Overwrite:$RefreshBinaries) {
        $rootCopied++
    }
}
Write-Step "复制根目录文件: $rootCopied 个"

$bepInExFrom = Join-Path $source 'BepInEx'
if (Test-Path -LiteralPath $bepInExFrom) {
    Ensure-RealDirectory -Path (Join-Path $target 'BepInEx')
    Copy-DirectoryContents -FromDirectory $bepInExFrom -ToDirectory (Join-Path $target 'BepInEx') -Overwrite:$RefreshBinaries
}

$exe = Join-Path $target 'Hollow Knight Silksong.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "实例不完整, 找不到: $exe"
}

$realFiles = Get-ChildItem -LiteralPath $target -Recurse -Force -File |
    Where-Object { $_.FullName -notlike "$target\$dataName\Managed\*" -and
                   $_.FullName -notlike "$target\$dataName\Resources\*" -and
                   $_.FullName -notlike "$target\$dataName\StreamingAssets\*" -and
                   $_.FullName -notlike "$target\MonoBleedingEdge\*" -and
                   $_.FullName -notlike "$target\D3D12\*" }

$saveRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\$CompanyName\$productName"

Write-Step '实例就绪.'
Write-Step "真实占用: $($realFiles.Count) 个文件, $([math]::Round((($realFiles | Measure-Object Length -Sum).Sum)/1MB, 1)) MB"
Write-Step "存档目录: $saveRoot"
Write-Step "Steam 接入: $(if ($KeepSteam -or (Test-Path -LiteralPath (Join-Path $instancePlugins "x86_64\$steamDllName"))) { '启用' } else { '已断开' })"
Write-Step '启动: pwsh -File game/launch-instance.ps1'
Write-Step "插件目录: $(Join-Path $target 'BepInEx\plugins')"
