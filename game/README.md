# 丝之歌隔离子实例

`game/Hollow Knight Silksong/` 是从 Steam 安装复制出来的游戏子实例, 用来单独启动和测试 mod, 不动源安装.
源安装: `D:\games\steam\common\Hollow Knight Silksong` (符号链接, 真实位置在 `D:\Program Files (x86)\Steam\steamapps\common\`).

## 隔离了什么

| 隔离项 | 做法 | 效果 |
| --- | --- | --- |
| 游戏本体 | 只读数据目录做目录联接, 其余走真实副本 | 往实例装 mod, 改配置, 写日志都不碰源安装; 只多占约 57 MB |
| 存档与游戏设置 | 实例的 `_Data/app.info` 里公司名改成 `Team Cherry Mod` | 存档与设置落在 `%USERPROFILE%\AppData\LocalLow\Team Cherry Mod\Hollow Knight Silksong`, 真存档不会被读也不会被写 |
| Steam 接入 | 实例的 `_Data/Plugins/x86_64/steam_api64.dll` 改名为 `steam_api64.dll.disabled` | 游戏连不上 Steam, 测试期间的成就/云存档/游戏时长都不落账, 也不会触发 Steam 云同步 |

| 内容 | 形式 | 原因 |
| --- | --- | --- |
| `<exe>_Data` 下的 `Managed`, `Resources`, `StreamingAssets` | 目录联接 (junction) 指向源安装 | 游戏自带且运行期不修改, 7.7 GB 不重复占用 |
| 根目录的 `MonoBleedingEdge`, `D3D12` | 目录联接 | 同上 |
| `Hollow Knight Silksong.exe`, `UnityPlayer.dll`, `winhttp.dll`, `doorstop_config.ini` 等 | 真实副本 | 副本要能独立启动 |
| `<exe>_Data` 下的小数据文件 (`app.info`, `globalgamemanagers` 等, 约 20 MB) | 真实副本 | `app.info` 要改公司名做存档隔离 |
| `<exe>_Data\Plugins` | 真实副本 | 要单独摘掉 `steam_api64.dll` |
| `BepInEx/` | 真实副本 | mod, 配置, 日志都写在这里 |

## 常用操作

```shell
# 首次创建, 之后重跑只补齐缺失内容
pwsh -File game/prepare-instance.ps1

# 源安装更新后, 覆盖刷新可执行文件与 BepInEx 基础文件
pwsh -File game/prepare-instance.ps1 -RefreshBinaries

# 恢复 Steam 接入 (只测存档隔离时用)
pwsh -File game/prepare-instance.ps1 -KeepSteam

# 换一个存档目录名
pwsh -File game/prepare-instance.ps1 -CompanyName 'Team Cherry Mod2'

# 连数据目录也完整复制 (约 7.8 GB), 隔离最彻底
pwsh -File game/prepare-instance.ps1 -FullCopy

# 删掉重建
pwsh -File game/prepare-instance.ps1 -Force

# 启动实例
pwsh -File game/launch-instance.ps1

# 启动并等待退出, 退出后打印 BepInEx 日志尾部
pwsh -File game/launch-instance.ps1 -Wait
```

把 mod 装进实例, 而不是源安装:

```shell
pwsh -File mods/object-outlines/build.ps1 -Install -GameDir 'D:\pjs\dotnet\silksong-rl\game\Hollow Knight Silksong'
```

实例当前自带源安装里已有的插件 (`ConfigurationManager`, `ShowHealthBar`), 之后两边各自增删互不影响.
日志在 `game/Hollow Knight Silksong/BepInEx/LogOutput.log`.

## 注意

- 断开 Steam 之后, 游戏内的成就, 云存档, 联机相关功能不可用; 想让实例恢复 Steam 接入就加 `-KeepSteam` 重跑一遍准备脚本.
- 数据目录是共享的, 只适合读. 如果某个 mod 会往 `Hollow Knight Silksong_Data` 里写文件, 请用 `-FullCopy` 重建.
- 副本的可执行文件不随 Steam 自动更新, 版本落后时用 `-RefreshBinaries` 刷新 (共享的数据目录本身跟随真实安装).
