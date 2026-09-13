# 丝之歌 (Hollow Knight: Silksong) Mod 制作方法汇总

资料收集日期: 2026-09-13. 生态仍在快速变化, 具体版本号以各官方页面为准.

## 1. 生态概览

| 项目 | 说明 |
| --- | --- |
| 游戏 | Hollow Knight: Silksong, Team Cherry, 2025-09-04 发售 |
| 引擎 | Unity + C# (Mono 后端, 可被 dnSpy/ILSpy 直接反编译) |
| Mod 加载器 | BepInEx 5 (x64), 由社区打包为 `BepInExPack Silksong` |
| 主要社区组织 | silksong-modding (Thunderstore 命名空间 `silksong_modding`) |
| 官方文档站 | <https://docs.silksong-modding.org/> (首页标注仍在建设中, 各库文档已可用) |
| 主要分发平台 | Thunderstore 的 Hollow Knight: Silksong 社区 (400+ 包), 其次 Nexus Mods, 中文站 (3DM 等) |
| 交流渠道 | Silksong Modding Discord <https://discord.gg/Bhsxurh2sU> |

Mod 的形态就是一个 C# 插件 dll, 放进 `BepInEx/plugins` 后在游戏启动时被加载, 通过运行时代码补丁 (detour/hook) 改游戏行为, 不需要改动游戏原始文件.

前作 Hollow Knight 的 <https://prashantmohta.github.io/ModdingDocs/> 仍可用于理解通用概念 (Mod 生命周期, FSM, 调查工具), 但它的 Modding API, `Mod` 基类, Satchel 等只适用于一代, 不能直接搬到丝之歌.

## 2. 技术前提

- C# / .NET 基础, 看得懂 IL 或至少看得懂反编译出来的 C#.
- Unity 基础概念: GameObject, Component, MonoBehaviour 生命周期 (Awake/Update), 协程, Scene.
- 会读反编译代码并定位目标方法, 这是丝之歌 mod 开发的主要工作量所在.

## 3. 环境准备

### 3.1 游戏侧

1. 安装 BepInExPack Silksong (Thunderstore 页面: <https://thunderstore.io/c/hollow-knight-silksong/p/BepInEx/BepInExPack_Silksong/>). 手动安装时解压到游戏根目录 (与游戏 exe 同级), 不要解压到子文件夹.
2. 先启动一次游戏, 让 BepInEx 生成 `BepInEx/` 目录结构与配置.
3. 开发期建议打开控制台日志, 编辑 `BepInEx/config/BepInEx.cfg`:

```ini
[Logging.Console]
Enabled = true
```

4. 常用辅助插件:
   - UnityExplorer: 运行时查看/修改 Unity 对象树, 定位组件与字段的利器.
   - BepInEx Configuration Manager (游戏内 F5 打开): 调试自己 mod 的配置项.
   - Silksong DebugMod (<https://github.com/hk-speedrunning/Silksong.DebugMod>, 游戏内 F2): 无敌, timescale 缩放, 逐帧, hitbox 显示, 存档状态 (savestate), 一键重试等.

### 3.2 开发侧

- IDE: Visual Studio 2026 或 Rider 2025.3 及以上 (模板 README 的推荐), VS Code 也可用.
- .NET SDK: 模板要求 .NET 10 SDK 才能让全部分析器正常工作.
- 工程模板 (官方推荐入口):

```shell
dotnet new install Silksong.Modding.Templates
dotnet new silksongplugin --username YourGitHubUsername
```

- 反编译与资源工具:
  - dnSpy / dnSpyEx: 反编译并调试游戏程序集.
  - ILSpy: 纯阅读用.
  - FSMExpress (<https://github.com/nesrak1/FSMExpress>, 丝之歌支持在 `skong` 分支): 查看 PlayMaker 状态机.
  - AssetStudioMod / AssetRipper: 解包贴图, 音频, 预制体等资源.
  - 官方 CLI 打包工具 Thunderstore CLI (模板已集成).

### 3.3 自查游戏版本与后端

Mod 兼容性以游戏补丁版本为准, 自查方式:

- 版本号: 在 `<游戏目录>/Hollow Knight Silksong_Data/globalgamemanagers` 里检索形如 `1.0.30000` 的字符串, 这是 Unity 写入的 bundleVersion; 也可以对照 Steam 商店页的更新公告.
- 后端类型: 游戏目录里存在 `MonoBleedingEdge/` 说明是 Mono 后端 (不是 IL2CPP), 这正是丝之歌要用 BepInEx 5 (x64) 而不是 BepInEx 6 的原因.
- 游戏若更新到社区库尚未跟进的新补丁, 依赖具体方法签名的 mod 会失效, 此时要么等库更新, 要么用模板的 `-gv` 参数把 mod 锁定到旧游戏版本.

## 4. 从模板创建第一个 Mod

模板生成的内容 (按官方 README):

| 文件 | 作用 |
| --- | --- |
| `Plugin.cs` | 插件入口, 继承 `BaseUnityPlugin` |
| `Directory.Build.props` | 版本号与元数据的唯一来源, 构建和 Thunderstore 打包共用 |
| `SilksongPath.props` | 指向本机游戏目录, 避免把本地路径提交进 Git |
| `*.csproj` | 构建后自动把 dll 复制到游戏 `BepInEx/plugins`, 并生成 Thunderstore 包 |
| `thunderstore.toml` | Thunderstore 包元数据 (图标, 描述, 依赖) |
| `.github/workflows/build-publish.yml` | 版本号变化时自动发布到 GitHub Release / Thunderstore / NuGet |

入口文件形态:

```csharp
using BepInEx;

namespace AlwaysCompass;

[BepInAutoPlugin(id: "io.github.用户名.插件名")]
public partial class AlwaysCompassPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");
        new Harmony(Id).PatchAll();
    }
}
```

要点:

- `id` 是 BepInEx 插件 GUID, 必须全局唯一, 首次发布后不要再改, 惯例以自己拥有的域名倒序作前缀 (GitHub 用户默认有 `用户名.github.io`, 所以是 `io.github.用户名.xxx`).
- `BaseUnityPlugin` 继承自 `MonoBehaviour`, 因此 Awake/Update/协程等生命周期方法都能用.
- 把 `SilksongPath.props` 里的游戏目录改成本机路径后, `dotnet build` 会编译并自动复制到 `BepInEx/plugins`.

### 4.1 定位要改的逻辑

以 "让罗盘常驻生效" 为例 (社区中文教程的示例):

1. dnSpy 搜索 `CompassTool`, 分析方法调用点.
2. 找到 `GameMap.PositionCompassAndCorpse()` 依据 `compassTool.IsEquipped` 分支.
3. 对该属性打运行时补丁.

### 4.2 打补丁 (HarmonyX)

BepInEx 自带 HarmonyX, 补丁返回 `false` 表示跳过原方法:

```csharp
[HarmonyPatch(typeof(ToolItem), "IsEquipped", MethodType.Getter)]
public static class ToolItemIsEquippedPatch
{
    public static bool Prefix(ToolItem __instance, ref bool __result)
    {
        if (__instance == Gameplay.CompassTool)
        {
            __result = true;
            return false;
        }
        return true;
    }
}
```

真正生效前要在插件启动时执行 `new Harmony(Id).PatchAll()`.

### 4.3 配置项

```csharp
private ConfigEntry<bool> IsEnabled;

private void Awake()
{
    IsEnabled = Config.Bind("General", "Enabled", true, "启用或禁用该 mod");
}
```

配置落到 `BepInEx/config/<插件 GUID>.cfg`; 装了 Configuration Manager 时游戏内按 F5 可视化开关.

### 4.4 声明依赖

编译期引用其他库用 csproj 的 `PackageReference`, 运行时依赖用 `BepInDependency`:

```csharp
[BepInAutoPlugin(id: "io.github.用户名.插件名")]
[BepInDependency("org.silksong-modding.fsmutil")]
public partial class MyPlugin : BaseUnityPlugin { }
```

同时要在 `thunderstore.toml` 里补依赖字符串 (例如 `silksong_modding-FsmUtil = "0.3.17"`), 否则用户装 mod 时不会自动带上依赖.

## 5. 修改游戏逻辑的几种手法

### 5.1 Harmony (BepInEx 自带, 最通用)

- `Prefix` / `Postfix`: 在原方法前后插入逻辑, 可改参数与返回值, Prefix 返回 false 可跳过原方法.
- `Transpiler` / `ILHook`: 直接改 IL, 适合必须改方法内部流程的场景, 难度也最高.

### 5.2 MonoDetour (社区新库)

- <https://github.com/MonoDetour/MonoDetour>, 文档 <https://monodetour.github.io/>.
- 底层是 MonoMod.RuntimeDetour, 在它之上用 C# 源生成器 (HookGen) 生成类型安全的钩子助手: 签名写错在编译期就会报错, 重载方法也能直接区分.
- 借用了 Harmony 的 Prefix/Postfix 概念, 与 Harmony/MonoMod 可以共存于同一个工程.
- 引入方式:

```xml
<ItemGroup>
  <PackageReference Include="MonoDetour.HookGen" Version="0.7.*" PrivateAssets="all" />
  <PackageReference Include="MonoDetour" Version="[*,2.0)" />
</ItemGroup>
```

- 适用判断: 只在确实没有别的办法时才去 hook 别人 (包括游戏) 的程序集; 官方文档明确不建议 hook 自己的程序集.

### 5.3 PlayMaker FSM

丝之歌大量游戏逻辑写在 PlayMaker 状态机里, 光看 C# 反编译代码看不到这些流程. 做法是:

- 用 FSMExpress 打开游戏的 FSM 数据, 查看状态与转移.
- 代码里用 `Silksong.FsmUtil` 修改 FSM: 增删状态, 改转移, 监听事件等.

```xml
<PackageReference Include="Silksong.FsmUtil" Version="0.3.17" />
```

### 5.4 PlayerData (玩家存档数据)

`Silksong.Prepatcher` 是一个在游戏启动前改写代码的预补丁器, 它把所有 PlayerData 字段的读写替换成对应的 Get/Set 调用. 由此:

- 想全局监听 PlayerData 变化的 mod 需要依赖 Prepatcher (`PrepatcherPlugin` 提供事件).
- 只是自己改 PlayerData 的 mod 不需要依赖 Prepatcher, 但应当沿用游戏原有的 Get/Set 变量函数, 这样其他监听者才看得到你的改动.

```xml
<PackageReference Include="Silksong.PrepatcherPlugin" Version="1.2.0" />
```

## 6. 周边核心库 (官方文档站 <https://docs.silksong-modding.org/>)

| 库 | BepInEx 依赖 GUID | 用途 |
| --- | --- | --- |
| Silksong.FsmUtil | `org.silksong-modding.fsmutil` | 代码里操作 PlayMakerFSM |
| Silksong.AssetHelper | `org.silksong-modding.assethelper` | 加载游戏资产 (场景内/非场景资产) |
| Silksong.DataManager | `Silksong.DataManager.DataManagerPlugin.Id` | 代 mod 保存/读取全局与存档槽数据 |
| Silksong.ModMenu | `Silksong.ModMenu.ModMenuPlugin.Id` | 在主菜单生成 Mod Options 页面 |
| Silksong.I18N | - | 自动为其他 mod 加载本地化文本 |
| Silksong.UnityHelper | `org.silksong-modding.unityhelper` | 常用 Unity 操作封装 |
| Silksong.Prepatcher | `org.silksong-modding.prepatcher` | 启动前代码改写, PlayerData 访问拦截等 |

AssetHelper 的关键约束 (来自其 Quickstart):

- 资产的请求必须在插件 `Awake` 里发起 (`ManagedAsset<T>.FromSceneAsset(sceneName, objPath)` 等).
- 加载最早在 bundle 创建回调之后, 实践中通常等到玩家进入对应场景或进入游戏再 `Load()`.
- 不要去改原始资产对象, 那会污染游戏本体和其他 mod; 需要改就先 `Instantiate`.

## 7. 游戏代码结构与调查工具链

游戏代码位于 `<游戏目录>/Hollow Knight Silksong_Data/Managed/`:

| 程序集 | 内容 |
| --- | --- |
| `Assembly-CSharp.dll` | 游戏主逻辑 |
| `Assembly-CSharp-firstpass.dll` | 首帧程序集 |
| `PlayMaker.dll` | PlayMaker FSM 运行时 |
| `TeamCherry.BuildBot / Cinematics / Localization / NestedFadeGroup / SharedUtils / Splines / TK2D .dll` | 官方拆出的功能模块 |
| `UnityEngine*.dll` | Unity 引擎模块 |

BepInEx 自身在 `<游戏目录>/BepInEx/core/`, 其中的 `0Harmony.dll` 就是补丁库 HarmonyX. 自己搭 msbuild 工程时, 直接引用上面这些 dll 即可 (官方模板则通过 `Silksong.GameLibs` 这类 bundler 包间接引用).

本仓库把上面除 `PlayMaker.dll` 与 Unity 模块以外的程序集反编译了一份, 放在 `disassembly/`: 全局命名空间的类平铺在顶层, 其余按命名空间分到子目录, `Silksong-decompiled.sln` 可以把它们一起在 IDE 里打开. 这些工程只用于阅读和跳转, 不要试图编译, 反编译产物与 Team Cherry 的原始工程并不等价. 该目录不入版本库, 换机器或游戏更新后重新生成即可:

```shell
pwsh -File disassembly/decompile.ps1
```

脚本默认读仓库内的隔离子实例, 也可以用 `-GameDir` 指向源安装, 或用 `-Assemblies PlayMaker.dll` 只补某个第三方程序集.

典型工作流: dnSpy 找到类和方法 -> UnityExplorer 在运行时确认对象与字段的实际取值 -> FSMExpress 补上看不到的状态机逻辑 -> 打补丁 -> 控制台日志验证.

| 工具 | 用途 |
| --- | --- |
| dnSpy / dnSpyEx | 反编译, 搜索类型与方法, 附加调试 |
| ILSpy | 纯代码阅读 |
| FSMExpress | PlayMaker FSM 反编译与可视化 |
| UnityExplorer | 运行时对象树浏览与修改 |
| AssetStudioMod / AssetRipper | 解包资源, 查看贴图/音频 |
| BepInEx Configuration Manager | 游戏内改配置 |
| Silksong DebugMod | 训练/测试用调试面板, savestate, timescale |

## 8. 打包与发布

### 8.1 打包

- `dotnet build` 会编译并生成 Thunderstore 包, 产物在 `thunderstore/dist` 目录.
- 上传前用 Thunderstore 的 manifest validator 校验自动生成的 manifest.
- 想让别人把自己的 mod 当依赖库引用时, 额外发布到 NuGet: `dotnet pack -o nuget`.

### 8.2 自动发布 (模板自带)

1. 在 `Directory.Build.props` 里改版本号触发工作流.
2. 首次发布前把工作流里的 `allow-release` 从 `false` 改成 `true`.
3. 需要的仓库 secret:
   - `THUNDERSTORE_API_KEY`: 在 Thunderstore 的 team -> Service Accounts 建服务账号后获得.
   - `NUGET_API_KEY`: 需要发布到 NuGet 时才配.
4. GitHub Release 默认开启, 建议打开仓库的 release immutability.

### 8.3 用户侧安装

- 推荐用 Gale, r2modman 或 Thunderstore Mod Manager 安装与管理, 会自动处理依赖.
- 也可以手动把 `BepInEx/plugins` 下的文件夹复制进游戏目录.

## 9. 常见坑

- 游戏更新会让 mod 失效甚至崩游戏; 模板支持用 `-gv` 参数指定目标游戏版本, 面向旧版本开发时用得上.
- 编译期引用了某个 mod, 但运行时它不一定在, 可能引发运行时错误; 这类情况可能需要 Prepatcher.
- 游戏里有若干函数会遍历所有已加载程序集; 大量 mod 程序集会拖慢场景加载, Prepatcher 会默认跳过 mod 程序集. 如果确实需要自己的程序集被遍历到, 在 csproj 里加:

```xml
<AssemblyMetadata Include="SilksongPrepatcher.IncludeInUnmoddedTypeSearch" Value="True" />
```

- 自己定义 Newtonsoft `JsonConverter` 时要注意游戏的扫描行为, 可能需要 Prepatcher.
- 用的是定制版 BepInEx, 遇到问题去 BepInEx 官方 Discord 通常得不到支持, 应去 Silksong Modding Discord.
- DebugMod 的 savestate 载入会覆盖存档且不可撤销, 用前备份.
- 修改共享资源对象 (AssetHelper) 会连带影响游戏本体.

## 10. 与外部程序通信 (例: 强化学习训练)

社区已有把丝之歌当训练环境的项目, 做法是 mod 侧采集状态并用 socket 与外部 Python 进程通信:

- <https://github.com/jimmie-jams/SilksongRL>: BepInEx mod 采集游戏状态并把动作写回游戏, Python 侧跑 PPO 训练; 依赖 BepInEx 与 Silksong DebugMod, mod 工程用 .NET Framework 4.7.2 + MSBuild.
- <https://github.com/deeean/silksong-agent>: 面向丝之歌 Boss 战的 RL agent.

可复用的点:

- 观测通常来自游戏内对象状态, 屏幕截图会把 UI 一起拍进去, 需要额外处理.
- DebugMod 提供的 timescale 缩放, 逐帧, savestate, 自动死亡重试, 对训练循环很有价值.
- 状态采集与动作注入都在同一个插件里完成, 用 `MonoBehaviour` 的 `Update` 或协程驱动即可, 不需要额外框架.

## 11. 参考资料

官方与社区:

- 官方文档站: <https://docs.silksong-modding.org/>
- BepInExPack Silksong: <https://thunderstore.io/c/hollow-knight-silksong/p/BepInEx/BepInExPack_Silksong/>
- 工程模板 NuGet: <https://www.nuget.org/packages/Silksong.Modding.Templates>
- BepInEx 文档: <https://docs.bepinex.dev/>
- MonoDetour: <https://github.com/MonoDetour/MonoDetour>, 文档 <https://monodetour.github.io/>
- FSMExpress: <https://github.com/nesrak1/FSMExpress>
- Silksong DebugMod: <https://github.com/hk-speedrunning/Silksong.DebugMod>
- 前作 Modding 文档 (概念参考): <https://prashantmohta.github.io/ModdingDocs/>
- Modding Discord: <https://discord.gg/Bhsxurh2sU>

中文资料:

- nov1ce 的丝之歌 mod 笔记 (环境准备 / 开发第一个 Mod, 2026-01 更新):
  - <https://nov1ce-lee.github.io/notes/games/silk-song/modding/env-prepare/>
  - <https://nov1ce-lee.github.io/notes/games/silk-song/modding/first-mod/>
- 对应 B 站视频: 如何制作丝之歌的模组 之 开发环境准备篇 (UP: 不想睡也不想起的nov)
