using BepInEx.Configuration;
using UnityEngine;

namespace RLEnv.Config
{
    // 插件配置. 全部落在 BepInEx/config/silksongrl.rl-env.cfg.
    internal sealed class EnvConfig
    {
        internal EnvConfig(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true, "总开关, 关掉后不注入任何输入");

            MuteAudio = config.Bind("General", "MuteAudio", true, "训练时把游戏音量静音 (只改运行时音量, 不动游戏设置)");

            Port = config.Bind(
                "General",
                "Port",
                5555,
                new ConfigDescription(
                    "训练侧监听的端口, 只绑定本机回环地址",
                    new AcceptableValueRange<int>(1024, 65535)));

            BossName = config.Bind("General", "BossName", "苔藓之母", "要训练的 Boss, 对应 BossScenes/BossSave 下的存档文件名");            SceneName = config.Bind("General", "SceneName", string.Empty, "该 Boss 所在场景名, 留空则从 BossSceneConfig.json 里查");
            SpawnX = config.Bind("General", "SpawnX", 87.07f, "每回合把主角摆到的位置 X, 留空场景默认值见 BossSceneConfig.json");
            SpawnY = config.Bind("General", "SpawnY", 17.57f, "每回合把主角摆到的位置 Y");

            Speed = config.Bind(
                "General",
                "Speed",
                3f,
                new ConfigDescription(
                    "执行动作时的时间倍率, 1 为原速",
                    new AcceptableValueRange<float>(0.1f, 20f)));

            IdleTimeScale = config.Bind(
                "General",
                "IdleTimeScale",
                0.0005f,
                new ConfigDescription(
                    "等 Python 决策时的时间倍率, 只能是极小非零值: 设成 0 会让 FixedUpdate 与游戏协程整体停摆",
                    new AcceptableValueRange<float>(0.00001f, 1f)));

            StepFrames = config.Bind(
                "General",
                "StepFrames",
                6,
                new ConfigDescription(
                    "一个 RL step 对应多少个物理帧",
                    new AcceptableValueRange<int>(1, 200)));

            MaxEpisodeSteps = config.Bind(
                "General",
                "MaxEpisodeSteps",
                600,
                new ConfigDescription(
                    "单回合最多多少 step, 超过按超时截断",
                    new AcceptableValueRange<int>(10, 100000)));

            SaveSlotIndex = config.Bind(
                "General",
                "SaveSlotIndex",
                5,
                new ConfigDescription(
                    "载入 Boss 存档用的槽位, 默认 5 是游戏本体不使用的槽位, 不要改成 0 到 4",
                    new AcceptableValueRange<int>(5, 9)));

            ShowOverlay = config.Bind("UI", "ShowOverlay", true, "显示调试用的状态面板");
            OverlayKey = config.Bind("UI", "OverlayKey", new KeyboardShortcut(KeyCode.F9), "开关状态面板的快捷键");

            ResetTimeout = config.Bind(
                "Episode",
                "ResetTimeout",
                60f,
                new ConfigDescription(
                    "单次重置的超时秒数, 超时按失败处理",
                    new AcceptableValueRange<float>(5f, 600f)));

            SettleFrames = config.Bind(
                "Episode",
                "SettleFrames",
                3,
                new ConfigDescription(
                    "Boss 出现后再空转多少个渲染帧才认为回合就绪",
                    new AcceptableValueRange<int>(1, 60)));

            MinimalReset = config.Bind(
                "Episode",
                "MinimalReset",
                true,
                "已在游戏内时用精简重置 (换存档数据 + ReadyForRespawn), 关掉则走 UIContinueGame 完整流程");

            SkipWakeUpAnimation = config.Bind(
                "Episode",
                "SkipWakeUpAnimation",
                false,
                "跳过复活时的 Wake Up Ground 动画, 每回合省一点时间, 但入场表现会变");

            BlockerNamePatterns = config.Bind(
                "Episode",
                "BlockerNamePatterns",
                "Moss Vine",
                "重置时程序化打烂的挡门障碍物名字片段, 逗号分隔; 留空则不打烂任何东西");

            BlockerSpeed = config.Bind(
                "Episode",
                "BlockerSpeed",
                4f,
                new ConfigDescription(
                    "打烂挡门障碍物时用的时间倍率 (破门是纯体力活, 加速不影响正确性)",
                    new AcceptableValueRange<float>(1f, 20f)));

            DumpSceneOnReset = config.Bind(
                "Diagnostics",
                "DumpSceneOnReset",
                false,
                "每次重置结束时把场景里的敌人, 波次战与持久化项写进日志 (排查用, 平时关掉)");
        }

        internal ConfigEntry<bool> Enabled { get; private set; }

        internal ConfigEntry<bool> MuteAudio { get; private set; }

        internal ConfigEntry<int> Port { get; private set; }

        internal ConfigEntry<string> BossName { get; private set; }

        internal ConfigEntry<string> SceneName { get; private set; }

        internal ConfigEntry<float> SpawnX { get; private set; }

        internal ConfigEntry<float> SpawnY { get; private set; }

        internal ConfigEntry<float> Speed { get; private set; }

        internal ConfigEntry<float> IdleTimeScale { get; private set; }

        internal ConfigEntry<int> StepFrames { get; private set; }

        internal ConfigEntry<int> MaxEpisodeSteps { get; private set; }

        internal ConfigEntry<int> SaveSlotIndex { get; private set; }

        internal ConfigEntry<bool> ShowOverlay { get; private set; }

        internal ConfigEntry<KeyboardShortcut> OverlayKey { get; private set; }

        internal ConfigEntry<float> ResetTimeout { get; private set; }

        internal ConfigEntry<int> SettleFrames { get; private set; }

        internal ConfigEntry<bool> MinimalReset { get; private set; }

        internal ConfigEntry<bool> SkipWakeUpAnimation { get; private set; }

        internal ConfigEntry<string> BlockerNamePatterns { get; private set; }

        internal ConfigEntry<float> BlockerSpeed { get; private set; }

        internal ConfigEntry<bool> DumpSceneOnReset { get; private set; }

        internal Vector3 SpawnPosition
        {
            get { return new Vector3(SpawnX.Value, SpawnY.Value, 0f); }
        }
    }
}
