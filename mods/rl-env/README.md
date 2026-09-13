# RL Env - 丝之歌 Boss 强化学习环境插件

把丝之歌的 Boss 战变成一个可被外部程序驱动的强化学习环境: 插件负责

1. 采集观测 (主角与 Boss 的位置, 速度, 血量, 状态机阶段, 战场范围, 本步战斗事件);
2. 注入动作 (在 InControl 动作层覆写左右 / 上下 / 跳跃 / 攻击);
3. 编排回合 (载入 Boss 存档, 同场景重载, 判定就绪与终止);
4. 通过本机 TCP 与 Python 训练进程通信.

奖励计算与训练算法都在 Python 侧 (`trainer/`), 改奖励不需要重编本插件.

## 与其它 mod 的关系

- Boss 清单与 42 份 Boss 存档与 `mods/boss-rush` 共用一套资源, 构建时从那里复制并还原成 `.dat`.
- 重置流程参考 `Silksong.DebugMod` 的做法 (屏蔽 `SaveLevelState` + 换干净存档数据 + 重载场景),
  但**不依赖** DebugMod, 运行时不引用它的任何类型.

## 构建与安装

```shell
# 只编译
pwsh -File mods/rl-env/build.ps1

# 编译并把插件与 Boss 资源铺进游戏实例
pwsh -File mods/rl-env/build.ps1 -Install

# 指定别的游戏目录 (相对路径按本 mod 目录解析)
pwsh -File mods/rl-env/build.ps1 -Install -GameDir '/path/to/Hollow Knight Silksong'
```

安装后插件位于 `<游戏目录>/BepInEx/plugins/RLEnv/`, 配置在
`<游戏目录>/BepInEx/config/silksongrl.rl-env.cfg`.

本机没有装 .NET SDK, 因此 `build.ps1` 直接调用 Visual Studio 自带的 Roslyn `csc.exe`.

## 配置

| 配置项 | 默认 | 说明 |
| --- | --- | --- |
| `General/Enabled` | true | 总开关 |
| `General/MuteAudio` | true | 训练时把游戏音量静音 (只改运行时音量) |
| `General/Port` | 5555 | 训练侧连接端口, 只绑定回环地址 |
| `General/BossName` | 苔藓之母 | 要训练的 Boss, 名字对应 `BossScenes/BossSave/<名字>.dat` |
| `General/SceneName` | 空 | 该 Boss 所在场景, 留空时以清单里的为准 |
| `General/SpawnX` / `SpawnY` | 87.07 / 17.57 | 每回合把主角摆到的位置 |
| `General/Speed` | 3 | 执行动作时的时间倍率 |
| `General/StepFrames` | 6 | 一个 RL step 对应多少个物理帧 |
| `General/IdleTimeScale` | 0.0005 | 等 Python 决策时的时间倍率, 必须是非零极小值 |
| `General/MaxEpisodeSteps` | 600 | 单回合 step 上限 (超时截断) |
| `General/SaveSlotIndex` | 5 | 载入 Boss 存档用的槽位, 不要改成 0-4 |
| `Episode/ResetTimeout` | 60 | 单次重置超时秒数 |
| `Episode/SettleFrames` | 3 | Boss 出现后再空转多少渲染帧才算就绪 |
| `Episode/MinimalReset` | true | 已在游戏内时用精简重置 (换数据 + `ReadyForRespawn`) |
| `Episode/SkipWakeUpAnimation` | false | 跳过复活动画, 每回合省一点时间 |
| `Episode/BlockerNamePatterns` | Moss Vine | 重置时程序化打烂的挡门障碍物名字片段 |
| `Episode/BlockerSpeed` | 4 | 破门阶段的时间倍率 (破门是纯体力活, 加速安全) |
| `UI/ShowOverlay` | true | 显示调试面板 |
| `UI/OverlayKey` | F9 | 开关调试面板 |
| `UI/ShowObservationBoxes` | false | 把观测里用到的矩形画成屏幕线框 |
| `UI/BoxesKey` | F10 | 开关观测线框的快捷键 |

## 工作方式

### 动作注入

游戏的输入心跳是 `InControlManager.Update -> InputManager.UpdateInternal -> PlayerActionSet.Update
-> PlayerAction.Update`. 插件在 `PlayerAction.Update` 的 postfix 里, 用
`OneAxisInputControl.SetValue(value, InputManager.CurrentTick)` + `Commit()` 覆写
`HeroActions` 的动作状态, 因此同帧内两轴动作 `MoveVector` 会自然重算, 硬件也不会把它盖回去.
只在 `GameState == PLAYING`, 未暂停且背包关闭时注入, 免得污染菜单输入.

### 回合重置

1. 把 `<Boss>.dat` 解析一次成 JSON 常驻内存;
2. 每回合重新反序列化出一份全新的 `SaveGameData` (游戏会把传入对象直接挂成单例, 复用等于没重置);
3. 改写落点 (`respawnScene` / `respawnMarkerName` 指向插件自建的 `RespawnMarker`), 重置血量;
4. 整个重置窗口内屏蔽 `GameManager.SaveLevelState()`, 否则旧场景的持久化项会把新数据写脏,
   新场景里的 Boss 会被 `SetActive(false)` 而战斗不触发;
5. 已在游戏内时走精简路径 (`GameManager.SetLoadedGameData` + `ReadyForRespawn`), 否则退回
   `UIManager.UIContinueGame`;
6. 等 `HasFinishedEnteringScene && !IsInSceneTransition && !GameManager.IsWaitingForSceneReady
   && GameState == PLAYING && hero.isHeroInPosition`, 再等 Boss 的 `HealthManager` 激活并空转几帧;
7. 必要时调用 `BattleScene.StartBattle()` 主动开战 (不依赖主角走进触发框).

### 观测线框 (排查用)

按 `F10` 打开: 插件会把"这一步观测里实际用到的世界空间矩形"画成线框, 用来肉眼核对
观测与画面是否一致. 配色:

| 颜色 | 含义 |
| --- | --- |
| 青色 | 主角碰撞箱 |
| 品红 | Boss 碰撞箱 |
| 绿色 | 进入观测名额的最近小怪 |
| 红色 | 当前生效的危险框 (`DamageHero` 且已启用) |
| 黄色 | 附近存在但未生效的危险框 |
| 蓝色 | 本回合固定的场地矩形 |

线框与训练采样是两条独立路径 (每帧刷新, 不注入输入), 开着它不影响训练, 但会有一点额外开销.

### 人类示范录制

训练侧发 `SetHumanMode(1)` 后, 插件不再注入任何输入, 游戏回到常速, 并且每 `StepFrames`
个物理帧把"当前观测 + 玩家真实按下的键"作为 `Record` 消息发给训练侧. 于是示范样本与训练时
的 MDP 定义完全一致 (同样的观测字段, 同样的动作分档, 同样的步长), 可以直接拿去做行为克隆.
玩家死亡或 Boss 被击杀时照常发终止观测, 训练侧据此收尾一局.

### 步进与采样
- 一个 step: 按当前动作放开 `StepFrames` 个物理帧, 然后回到 `IdleTimeScale` 等下一步.
- 不用 `Time.timeScale = 0` 暂停: 那会让 `FixedUpdate` 与游戏协程整体停摆. 改用极小非零倍率.
- 时间倍率走游戏自带的 `TimeManager.TimeControlInstance`, 不直接写 `Time.timeScale`
  (会被 `TimeManager.UpdateTimeScale` 覆盖).
- 观测采样挂在 `CustomPlayerLoop` 的 super-late fixed update 上, 保证伤害与死亡结算都已落定.
- 训练期间屏蔽 `GameManager.FreezeMoment` 顿帧, 让"一个 step 等于固定帧数"成立.

## 通信协议

帧结构 `[int32 载荷长度][载荷]`, 载荷首部 `[int32 消息类型]`, 小端.

| 方向 | 类型 | 说明 |
| --- | --- | --- |
| Python -> mod | 1 `Reset` | 重置回合 |
| Python -> mod | 2 `Step` | 4 个 int32 的动作分量 |
| Python -> mod | 3 `Close` | 关闭连接 |
| Python -> mod | 4 `Ping` | 查询状态 |
| Python -> mod | 5 `SetSpeed` | 改运行倍率 (float) |
| Python -> mod | 6 `SetHumanMode` | 进入/退出人类示范录制 (int32: 1 开, 0 关) |
| mod -> Python | 101 `Hello` | JSON: 协议版本, 观测字段表, 动作形状, 每步帧数 |
| mod -> Python | 102 `Observation` | `[int32 stepIndex][int32 flags][float32 x N]` |
| mod -> Python | 103 `Status` | JSON 状态 |
| mod -> Python | 104 `Error` | JSON 错误 |
| mod -> Python | 105 `StateMap` | JSON: Boss 状态 id 到 `FSM名=状态名` 的映射 |
| mod -> Python | 106 `Record` | 人类示范样本: `[int32 stepIndex][float32 x N][int32 x 5]` |

观测字段的权威定义在 `src/Observation/ObservationSchema.cs`, 训练侧从 `Hello` 里读名字,
不硬编码下标. 训练侧的对应实现在 `trainer/src/silksong_rl/protocol.py`.

字段分四段 (共 216 个): 主角 40, Boss 18, 场地 4, 时间与事件 8, 最近 6 个小怪各 11 项,
最近 8 个危险框各 9 项, Boss 前 4 个 FSM 各 2 项. 小怪取自 `HealthManager` 注册表
(只算敌意层级), 危险框取自场景里所有 `DamageHero` (敌人攻击判定, 陷阱, 掉落物, 投射物),
两者都按"离主角最近"截断.

## 排查

训练侧连不上或行为不对时, 先看 `<游戏目录>/BepInEx/LogOutput.log`, 插件的关键阶段都会打日志.

| 现象 | 原因与处理 |
| --- | --- |
| 连接超时 | 端口被占 (同一游戏实例同时只服务一个训练进程), 或者游戏还没启动到主循环; 训练侧默认会等 120 秒 |
| `上一次重置还没结束` | 上一个训练进程断开时留下了没跑完的重置; 现在断开时会自动取消, 若仍出现说明是旧版本 |
| Boss 一直是 `Dormant` | 门口的藤蔓门没破干净, 或者没发 `WAKE`; 看日志里有没有 `障碍物处理完成` 与 `已把主角送进竞技场` |
| 卡在载入界面 | 重置撞上了游戏自己的死亡/复活流程; 确认 `PlayerDeathSuppressor` 已加载 (日志里没有它说明补丁没打上) |
| 新回合里房间空无一人 | 场景持久化项被写脏了 (Boss 被 `SetActive(false)`); 检查 `SaveLevelStatePatch` 是否生效 |
| 日志里噪声太多 | `Diagnostics/DumpSceneOnReset` 关掉 (默认就是关的) |

想让插件把场景里有什么对象打出来, 把 `Diagnostics/DumpSceneOnReset` 设为 true 再重置一次即可.

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/Actions` | 虚拟输入面板与 InControl 注入补丁 |
| `src/Observation` | Boss 识别, 战斗事件统计, 观测采集与字段表 |
| `src/Episode` | 回合重置, 落点, 存档仓库, 相关补丁 |
| `src/TimeControl` | 时间倍率与顿帧屏蔽 |
| `src/Transport` | 协议与 TCP 服务端 |
| `src/Session` | 会话状态机 (命令分发 + 步进 + 采样) |
| `src/Diagnostics` | IMGUI 调试面板 |
| `build.ps1` | 编译与安装 |

## 已知限制

- 一次只服务一个训练客户端, 每个游戏实例一个环境; 多环境要开多个游戏实例.
- `BossScenes` 里的存档来自 2025 年 9 月的游戏版本, 字段语义是否与当前版本一致需要在游戏里实测.
- 精简重置依赖 private 的 `GameManager.SetLoadedGameData`; 找不到时会自动退回 `UIContinueGame`.
