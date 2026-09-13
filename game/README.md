# 丝之歌隔离子实例

`game/Hollow Knight Silksong/` 是从 Steam 安装复制出来的一份游戏子实例, 用来单独启动和测试 mod, 不动源安装.
源安装: `D:\games\steam\common\Hollow Knight Silksong` (符号链接, 真实位置在 `D:\Program Files (x86)\Steam\steamapps\common\`).

## 结构

| 内容 | 形式 | 原因 |
| --- | --- | --- |
| `Hollow Knight Silksong_Data`, `MonoBleedingEdge`, `D3D12` | 目录联接 (junction) 指向源安装 | 游戏自带且运行期不修改, 7.8 GB 不重复占用 |
| `Hollow Knight Silksong.exe`, `UnityPlayer.dll`, `winhttp.dll`, `doorstop_config.ini` 等根目录文件 | 真实副本 | 副本要能独立启动 |
| `BepInEx/` | 真实副本 | mod, 配置, 日志都写在这里, 与源安装完全分开 |

实测: 实例只占 36.4 MB 真实空间, 其余走联接.

## 常用操作

```shell
# 首次创建, 之后重跑只补齐缺失内容
pwsh -File game/prepare-instance.ps1

# 源安装更新后, 覆盖刷新可执行文件与 BepInEx 基础文件
pwsh -File game/prepare-instance.ps1 -RefreshBinaries

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

- 存档不隔离: 两份实例都读写 `%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight Silksong`, 测试前先备份该目录, 或者用游戏内的另一个存档位.
- 数据目录是共享的, 只适合读. 如果某个 mod 会往 `Hollow Knight Silksong_Data` 里写文件, 请用 `-FullCopy` 重建.
- 建议保持 Steam 客户端运行, 成就和云存档的行为与正常启动一致.
- 副本的可执行文件不随 Steam 自动更新, 版本落后时用 `-RefreshBinaries` 刷新.
