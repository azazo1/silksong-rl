# Object Outlines

丝之歌 (Hollow Knight: Silksong) 的调试用 mod: 给玩家, 敌人, 陷阱, 可交互物描出碰撞体边框, 不同类别用不同颜色区分.

## 使用

- 加载后默认开启, 按 `F9` 开关 (快捷键可在配置文件里改).
- 配置文件: `BepInEx/config/silksongrl.object-outlines.cfg`; 装了 Configuration Manager 时游戏内按 `F5` 改, 改完下一帧生效, 不用重启.
- 左上角默认显示统计信息, 不需要就把 `Draw/ShowStatsOverlay` 关掉.

统计信息长这样:

```text
Player=1  Enemy=23  Hazard=7  Interactable=9  Breakable=31  | 顶点=1832 几何=1ms 扫描=4ms 建网格=1ms 范围=640x360
候选 hero=1 health=57 interact=9 pickup=3 damageHero=12 hazard=2
```

| 字段 | 含义 |
| --- | --- |
| 各类别数量 | 这一轮被分类并描边的对象数 |
| 顶点 | 当前提交的线段顶点数 (每两个顶点一条线) |
| 几何 | 每帧按对象当前位置重算顶点的耗时 |
| 扫描 | 重新收集对象集合的耗时 |
| 建网格 | 把顶点写进 Mesh 的耗时 |
| 范围 | 当前 Mesh 的包围盒尺寸 |
| 候选 | 场景里各类标记组件的原始数量, 用来判断分类是不是漏掉了对象 |

## 类别判定

| 类别 | 默认颜色 | 判定依据 |
| --- | --- | --- |
| Player | 青色 | 对象上有 `HeroController` |
| Enemy | 红色 | 对象上有 `HealthManager`, 且 `EnemyType` 属于 Regular / Shade / Armoured |
| Hazard | 橙色 | 对象上有 `DamageHero` 或 `HazardRespawnTrigger`, 且不属于某个敌人实体 |
| Interactable | 蓝色 | 对象上有 `InteractableBase` (含 NPC, 门, 场景过渡点) 或 `CollectableItemPickup` |
| Breakable | 灰色 | 有 `HealthManager` 但不是敌人; 默认不绘制 |

绘制范围是标记对象自身及其所有子对象的 `Collider2D` 轮廓; 没有任何碰撞体时退回到渲染器包围盒, 粒子系统的渲染器会被跳过.

## 绘制方式

- 默认 (Mesh): 对象集合低频刷新, 顶点每帧按对象当前变换重算, 合成一个 Mesh 一次提交. 材质走覆盖层渲染队列, 保证线条压在游戏画面之上.
- 关掉 `Draw/MeshRendering` 后退回逐顶点 GL 绘制 (在相机渲染末尾执行), 兼容性更好, 但目标多时每帧的 native 调用开销明显.

## 配置项

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `General/Enabled` | `true` | 是否显示边框 |
| `General/ToggleKey` | `F9` | 开关快捷键 |
| `General/RefreshIntervalSeconds` | `0.5` | 重新收集描边对象的间隔; 位置是每帧更新的, 这个值只影响目标增删的及时性 |
| `General/MaxObjectsPerCategory` | `300` | 每个类别最多描边的对象数 |
| `Draw/RendererFallback` | `true` | 没有碰撞体时用渲染器包围盒画框 |
| `Draw/MeshRendering` | `true` | 用 Mesh 一次性提交; 关掉则退回逐顶点 GL 绘制 |
| `Draw/ShowStatsOverlay` | `true` | 左上角显示统计信息 |
| `Log/LogCounts` | `false` | 每次扫描把统计信息写入日志 |
| `Categories/*` | 见上表 | 每个类别单独的开关 |
| `Colors/*Color` | 见上表 | 颜色, 格式 `R,G,B,A` (0-255) |

## 构建

本机没有安装 .NET SDK, 因此构建脚本直接调用 Visual Studio 自带的 Roslyn 编译器, 并引用游戏目录中的程序集:

```shell
pwsh -File mods/object-outlines/build.ps1            # 只编译, 产物在 bin/
pwsh -File mods/object-outlines/build.ps1 -Install   # 编译并复制到游戏 BepInEx/plugins
```

装到隔离子实例而不是源安装:

```shell
pwsh -File mods/object-outlines/build.ps1 -Install -GameDir 'game/Hollow Knight Silksong'
```

游戏目录默认来自 `SilksongPath.props` (可从 `SilksongPath.props.example` 复制一份再改); 也可以用 `-GameDir` 参数指定.

以后若装了 .NET SDK, 也可以走标准工程: `dotnet build -p:InstallToGame=true` (该工程在本机未经实测).

## 已知限制

- 只收集激活中的对象, 未激活对象不出现在边框里.
- 每个类别默认上限 300 个对象, 场景里目标过多时超出的部分不画.
- 线宽固定 1 像素, 这是线段绘制的限制.
- 复合碰撞体 (Tilemap / Composite) 用世界包围盒近似, 不是精确轮廓.
- 对象集合是周期性重扫的, 所以刚生成或刚消失的目标最多滞后 `RefreshIntervalSeconds` 才反映到边框上.
