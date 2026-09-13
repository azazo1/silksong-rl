using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using RLEnv.Actions;
using RLEnv.Config;
using RLEnv.Diagnostics;
using RLEnv.Episode;
using RLEnv.Observation;
using RLEnv.Session;
using RLEnv.TimeControl;
using RLEnv.Transport;

namespace RLEnv
{
    // 丝之歌 Boss 强化学习训练用的环境插件.
    //
    // 职责边界: 本插件只做四件事 -- 采集观测, 注入动作, 编排回合, 与 Python 侧通信.
    // 训练算法与奖励计算都在 Python 侧, 改奖励不需要重编这个插件.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class RLEnvPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "silksongrl.rl-env";

        public const string PluginName = "Silksong RL Env";

        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        private EnvConfig _config;

        private EnvServer _server;

        private EnvSession _session;

        private EnvOverlay _overlay;

        private BossSaveRepository _saves;

        private float _previousAudioVolume = 1f;

        internal static RLEnvPlugin Instance { get; private set; }

        internal ManualLogSource Log
        {
            get { return Logger; }
        }

        internal EnvConfig Settings
        {
            get { return _config; }
        }

        internal EnvSession Session
        {
            get { return _session; }
        }

        internal BossSaveRepository Saves
        {
            get { return _saves; }
        }

        internal EnvServer Server
        {
            get { return _server; }
        }

        private void Awake()
        {
            Instance = this;
            _config = new EnvConfig(Config);

            string pluginDirectory = Path.GetDirectoryName(Info.Location);
            _saves = new BossSaveRepository(pluginDirectory, Logger);
            _saves.Reload();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(PlayerActionUpdatePatch));
            _harmony.PatchAll(typeof(CheatManagerPatch));
            _harmony.PatchAll(typeof(HealthManagerPatch));
            _harmony.PatchAll(typeof(FreezeMomentPatch));
            _harmony.PatchAll(typeof(SaveLevelStatePatch));
            _harmony.PatchAll(typeof(PlayerDeathSuppressor));
            _server = new EnvServer(_config.Port.Value, Logger);
            _session = new EnvSession(this, _config, _server, Logger, _saves);
            _overlay = new EnvOverlay(this);

            if (_config.Enabled.Value)
            {
                _server.Start();
                _session.Start();
            }

            if (_config.MuteAudio.Value)
            {
                _previousAudioVolume = AudioListener.volume;
                AudioListener.volume = 0f;
                Logger.LogInfo("已静音 (AudioListener.volume = 0)");
            }

            Logger.LogInfo(string.Format(
                "{0} {1} 已加载. 端口 {2}, Boss {3}, 可用 Boss 存档 {4} 份.",
                PluginName,
                PluginVersion,
                _config.Port.Value,
                _config.BossName.Value,
                _saves.Count));

            if (_saves.LastError != null)
            {
                Logger.LogWarning(_saves.LastError);
            }
        }

        private void Update()
        {
            if (_session != null)
            {
                _session.Tick();
            }

            if (_overlay != null)
            {
                _overlay.Tick();
            }
        }

        private void OnGUI()
        {
            if (_overlay != null)
            {
                _overlay.Draw();
            }
        }

        private void OnDestroy()
        {
            if (_overlay != null)
            {
                _overlay.Dispose();
                _overlay = null;
            }

            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }

            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }

            HeroSpawnGuider.Shutdown();
            FreezeMomentPatch.Suppress = false;

            if (_config != null && _config.MuteAudio.Value)
            {
                AudioListener.volume = _previousAudioVolume;
            }

            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
