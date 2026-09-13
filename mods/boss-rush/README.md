# Boss 战斗房 (BossRush) - BepInEx 移植版

一键进入任意 Boss 战. 面板里点一下 Boss 名字, 就会载入该 Boss 对应的存档,
落在 Boss 房门口, 并把当前存档的配装与能力带过去, 省掉跑图.

## 来源

原版 `BossRush 1.0` (作者 yunxuan, 中文圈常叫 `BOSS复战`) 是一个 MelonLoader 插件,
本目录是它的 BepInEx 移植, 流程与数据都沿用原版:

| 原版 | 本移植 |
| --- | --- |
| `MelonMod` 入口, `MelonInfo` / `MelonGame` 标记 | `BaseUnityPlugin` + `[BepInPlugin]` |
| `MelonLogger` | BepInEx `ManualLogSource` |
| 挂在暂停菜单上的运行时 uGUI 列表 | 自绘 IMGUI 面板 (零额外依赖) |
| 由 `userId` 拼出 `user5.dat` 路径后直接读写文件 | 走 `Platform.Current.WriteSaveSlot` |
| 只对 5 号槽位放行的 `IsSaveSlotIndexValid` 补丁 | 保留, 但只放行 5 号 |

Boss 清单 `BossScenes/BossSceneConfig.json` 与 42 份存档都是原版随包发布的资源, 已收进本目录.
原始压缩包来自作者发布页 (B 站 `禽兽-云轩` 的 `Boss复战Mod正式发布` 视频) 里的网盘下载,
仓库根目录若还留着 `mods/BossRush_1.0.zip`, 那只是最初的下载件, 构建不再依赖它.

## 存档格式与仓库存法

游戏存档的编码链是:

```
.dat = BinaryFormatter 字符串( Base64( AES-256-ECB-PKCS7( UTF-8( JSON ) ) ) )
```

密钥写死在 `TeamCherry.SharedUtils.Encryption` 里, 明文 JSON 含 `playerData` 与 `sceneData` 两部分.
`.dat` 外层是密文加 base64, 几乎压不动 (10.54 MB 只能压到 6.21 MB); 而明文 JSON 的压缩率是 13%.
因此仓库里存的是解出来的 JSON: `BossScenes/BossSave/<Boss 名>.json.gz`, 共 1.03 MB,
安装时再由 `build.ps1` 还原成游戏认的 `.dat` 铺进插件目录.

编解码在 `tools/SaveCodec.ps1` 里, 只实现上面这一种字节布局, 不依赖 BinaryFormatter 本身.
它可以用往返比对自证正确:

```shell
# 把 .dat 解出来再编回去, 逐字节比对
pwsh -File mods/boss-rush/tools/convert-saves.ps1 -SelfTest -Source <含 .dat 的目录>

# 两个方向的手工转换
pwsh -File mods/boss-rush/tools/convert-saves.ps1 -ToGzip -Source <含 .dat 的目录> -Destination <输出目录>
pwsh -File mods/boss-rush/tools/convert-saves.ps1 -ToDat -Source <含 .json.gz 的目录> -Destination <输出目录>
```

42 份存档在这个自检下与原文件逐字节一致, 因此安装期还原出来的 `.dat` 与作者原包完全相同.

## 用法

```shell
# 编译, 并把插件与 Boss 资源铺进游戏实例
pwsh -File mods/boss-rush/build.ps1 -Install

# 指定别的游戏目录 (支持相对路径, 相对本 mod 目录解析)
pwsh -File mods/boss-rush/build.ps1 -Install -GameDir '/path/to/Hollow Knight Silksong'
```

游戏内:

- `F7` 开关 Boss 列表.
- 列表里点一下 Boss 名字即进入该 Boss 房, 面板会自动收起.
- `F8` 重新读取 Boss 清单.
- 快捷键与槽位都在 `BepInEx/config/silksongrl.boss-rush.cfg` 里改.

装好后启动游戏, BepInEx 日志里应当出现
`Silksong Boss Rush 1.0.0 已加载. 可用 Boss 38 / 39, 菜单快捷键 F7, 存档槽位 5.`
计数对不上就说明 Boss 清单或存档没有铺到位.

## 工作方式

1. 把 `BossSave/<Boss 名>.dat` 写进 5 号槽位 (游戏本体只用 0 到 4 号, 因此不会碰到自己的存档).
2. 让游戏自己解析该槽位, 拿到 `SaveGameData`.
3. 在目标场景里放一个 `RespawnMarker` 并注册进 `SceneTeleportMap`, 把存档的 `respawnScene`
   与 `respawnMarkerName` 指向它, 主角就会落在 Boss 房门口.
4. 把当前存档的配装, 工具, 能力复制进这份数据.
5. `UIManager.UIContinueGame(5, saveGameData)` 直接载入.

配置项 `InheritLoadout` 关掉后, 用的就是原版存档自带的配装.

## 已知缺口

- `西格尼斯` 在原版清单里有条目, 但没有对应的存档文件, 面板里会显示为 `缺存档` 且不可点.
  要用它得先补一个 `西格尼斯.dat`, 或者拿社区补丁里的同项替换.
- `失格大厨卢戈利`, `监工兄弟`, `痛苦的特罗比奥`, `针姬` 这四个存档在包里存在,
  但原版清单没有给它们场景与坐标, 因此没有列进面板.
- 自带存档来自 2025 年 9 月的游戏版本. 当前实例是 1.0.30000, 存档是 JSON 加固定密钥的
  AES 加密, 缺字段会走默认值, 但标记语义是否与新版一致需要在游戏里实测.

## 目录

| 路径 | 内容 |
| --- | --- |
| `BossScenes` | 原版的 Boss 清单与 42 份存档 (JSON 压缩件), 随仓库提供 |
| `tools` | 存档编解码与两种存储格式的互转脚本 |
| `src/Assets` | Boss 清单模型与读取, 存档文件是否齐备的判断 |
| `src/Fighting` | 战斗流程编排, 复活点, 主角落点, 配装继承 |
| `src/Hooks` | 槽位校验补丁 |
| `src/UI` | IMGUI 面板 |
| `build.ps1` | 编译与安装 (调用 VS 自带 csc, 不依赖 .NET SDK) |
