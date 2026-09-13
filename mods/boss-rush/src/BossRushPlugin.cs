using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BossRush.Assets;
using BossRush.Fighting;
using BossRush.Hooks;
using BossRush.UI;
using HarmonyLib;
using UnityEngine;

namespace BossRush
{
    // 把 BossRush 1.0 (原版是 MelonLoader 插件) 移植到 BepInEx.
    //
    // 原版做法: 拾取界面挂在暂停菜单上, 点 Boss 名就把 mod 自带的存档写进
    // user5.dat, 再直接调用 UIManager.UIContinueGame 载入该槽位, 落到 Boss 房.
    // 本移植把界面换成自绘 IMGUI 面板, 其余流程保持原样.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class BossRushPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "silksongrl.boss-rush";
        public const string PluginName = "Silksong Boss Rush";
        public const string PluginVersion = "1.0.0";

        internal static BossRushPlugin Instance { get; private set; }

        private Harmony _harmony;
        private BossSceneCatalog _catalog;
        private BossFightLauncher _launcher;
        private BossMenuOverlay _overlay;

        internal ManualLogSource Log { get { return Logger; } }

        internal BossSceneCatalog Catalog { get { return _catalog; } }

        internal ConfigEntry<bool> Enabled { get; private set; }

        internal ConfigEntry<KeyboardShortcut> MenuKey { get; private set; }

        internal ConfigEntry<KeyboardShortcut> ReloadKey { get; private set; }

        internal ConfigEntry<int> SaveSlotIndex { get; private set; }

        internal ConfigEntry<bool> InheritLoadout { get; private set; }

        internal ConfigEntry<bool> ShowMenuOnStartup { get; private set; }

        private void Awake()
        {
            Instance = this;
            BindConfiguration();

            string pluginDirectory = Path.GetDirectoryName(Info.Location);
            _catalog = new BossSceneCatalog(pluginDirectory, Logger);
            _catalog.Reload();

            _launcher = new BossFightLauncher(this, _catalog);
            _overlay = new BossMenuOverlay(this, _catalog, _launcher);

            // 载入自带存档要用 5 号槽位, 游戏只认可 0 到 4, 这里给 5 号放行.
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(SaveSlotIndexPatch));

            Logger.LogInfo(string.Format(
                "{0} {1} 已加载. 可用 Boss {2} / {3}, 菜单快捷键 {4}, 存档槽位 {5}.",
                PluginName,
                PluginVersion,
                _catalog.AvailableCount,
                _catalog.Entries.Count,
                MenuKey.Value,
                SaveSlotIndex.Value));

            if (_catalog.LastError != null)
            {
                Logger.LogWarning(_catalog.LastError);
            }
        }

        private void Update()
        {
            _overlay.Tick();
            _launcher.Tick();
        }

        private void OnGUI()
        {
            _overlay.Draw();
        }

        private void OnDestroy()
        {
            if (_overlay != null)
            {
                _overlay.Dispose();
            }

            if (_launcher != null)
            {
                _launcher.Dispose();
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

        private void BindConfiguration()
        {
            Enabled = Config.Bind("General", "Enabled", true, "总开关, 关掉后不响应快捷键");
            MenuKey = Config.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.F7), "打开或关闭 Boss 列表的快捷键");
            ReloadKey = Config.Bind("General", "ReloadConfigKey", new KeyboardShortcut(KeyCode.F8), "重新读取 BossSceneConfig.json 的快捷键");
            SaveSlotIndex = Config.Bind(
                "General",
                "SaveSlotIndex",
                5,
                new ConfigDescription(
                    "载入 Boss 存档用的槽位, 默认 5 是游戏本体不使用的槽位, 不要改成 0 到 4 以免覆盖自己的存档",
                    new AcceptableValueRange<int>(5, 9)));
            InheritLoadout = Config.Bind("General", "InheritLoadout", true, "进 Boss 房之前, 把当前存档的配装, 工具与能力复制进 Boss 存档");
            ShowMenuOnStartup = Config.Bind("General", "ShowMenuOnStartup", false, "游戏启动后自动打开一次 Boss 列表");
        }
    }
}
