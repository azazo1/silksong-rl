using System;
using System.IO;
using BepInEx.Logging;
using BossRush.Assets;
using UnityEngine;

namespace BossRush.Fighting
{
    // Boss 战流程编排:
    // 把 mod 自带的存档写进空槽位, 让游戏自己解析成 SaveGameData,
    // 再改写复活点与配装, 最后交给 UIManager 直接载入该槽位.
    internal sealed class BossFightLauncher : IDisposable
    {
        private readonly BossRushPlugin _plugin;

        private readonly ManualLogSource _log;

        private BossSceneEntry _pending;

        internal BossFightLauncher(BossRushPlugin plugin, BossSceneCatalog catalog)
        {
            _plugin = plugin;
            _log = plugin.Log;
            Status = "就绪, 选一个 Boss 开始";
            HeroSpawnGuider.Ensure();
        }

        internal string Status { get; private set; }

        internal bool Busy { get; private set; }

        internal void Request(BossSceneEntry entry)
        {
            if (entry == null || Busy)
            {
                return;
            }

            if (!entry.HasSaveFile)
            {
                Status = "缺少存档文件: " + entry.SaveFileName;
                return;
            }

            // 真正的载入放在下一次 Tick, 避免在 IMGUI 绘制过程中做重活.
            _pending = entry;
            Status = "准备 " + entry.BossName;
        }

        internal void Tick()
        {
            if (Busy || _pending == null)
            {
                return;
            }

            BossSceneEntry entry = _pending;
            _pending = null;
            Begin(entry);
        }

        public void Dispose()
        {
            HeroSpawnGuider.Shutdown();
        }

        private void Begin(BossSceneEntry entry)
        {
            Platform platform = Platform.Current;
            if (platform == null)
            {
                Fail("Platform 还没准备好, 请先进入游戏再选 Boss");
                return;
            }

            byte[] saveBytes;
            try
            {
                saveBytes = File.ReadAllBytes(entry.SavePath);
            }
            catch (Exception exception)
            {
                Fail("读取自带存档失败: " + exception.Message);
                return;
            }

            int slot = _plugin.SaveSlotIndex.Value;
            Busy = true;
            Status = "写入槽位 " + slot;
            _log.LogInfo(string.Format("开始 {0}: 场景 {1}, 槽位 {2}", entry.BossName, entry.SceneName, slot));

            platform.WriteSaveSlot(slot, saveBytes, delegate(bool written)
            {
                OnSlotWritten(entry, slot, written);
            });
        }

        private void OnSlotWritten(BossSceneEntry entry, int slot, bool written)
        {
            if (!written)
            {
                Fail("写入槽位 " + slot + " 失败");
                return;
            }

            GameManager gameManager = GameManager.instance;
            if (gameManager == null)
            {
                Fail("GameManager 不可用");
                return;
            }

            gameManager.HasSaveFile(slot, delegate(bool inUse)
            {
                if (!inUse)
                {
                    Fail("槽位 " + slot + " 上读不到刚写入的存档");
                    return;
                }

                Status = "解析存档数据";
                gameManager.GetSaveStatsForSlot(slot, delegate(SaveStats stats, string message)
                {
                    OnSaveStatsReady(entry, slot, stats, message);
                });
            });
        }

        private void OnSaveStatsReady(BossSceneEntry entry, int slot, SaveStats stats, string message)
        {
            try
            {
                if (stats == null || stats.saveGameData == null || stats.saveGameData.playerData == null)
                {
                    Fail("存档数据解析失败: " + (message ?? "未知原因"));
                    return;
                }

                PlayerData target = stats.saveGameData.playerData;
                string markerName = RespawnPointBuilder.Ensure(entry.SceneName, entry.BossName, entry.Position);
                HeroSpawnGuider.Instance.Arm(entry.Position);

                target.respawnType = 0;
                target.respawnScene = entry.SceneName;
                target.respawnMarkerName = markerName;

                if (_plugin.InheritLoadout.Value)
                {
                    GameManager gameManager = GameManager.instance;
                    LoadoutInheritor.Apply(gameManager != null ? gameManager.playerData : null, target);
                }

                BossFlagTweaks.Apply(entry.BossName, target);

                Status = "进入 " + entry.BossName;
                _log.LogInfo(string.Format("{0} 存档已就绪, 载入槽位 {1} 进入场景 {2}", entry.BossName, slot, entry.SceneName));

                Busy = false;
                UIManager.instance.UIContinueGame(slot, stats.saveGameData);
            }
            catch (Exception exception)
            {
                Fail("进入 Boss 房失败: " + exception);
            }
        }

        private void Fail(string message)
        {
            Busy = false;
            Status = message;
            _log.LogWarning(message);
        }
    }
}
