using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace RLEnv.Episode
{
    // Boss 存档仓库: 读取随插件安装的 Boss 清单与 .dat 存档.
    // 数据与 mods/boss-rush 共用一套资源, 构建脚本会把它们铺到本插件目录下.
    internal sealed class BossSaveRepository
    {
        private const string ConfigRelativePath = "BossScenes/BossSceneConfig.json";

        private const string SaveDirectoryRelativePath = "BossScenes/BossSave";

        private readonly string _pluginDirectory;

        private readonly ManualLogSource _log;

        private readonly Dictionary<string, BossSaveEntry> _entries = new Dictionary<string, BossSaveEntry>(StringComparer.Ordinal);

        internal BossSaveRepository(string pluginDirectory, ManualLogSource log)
        {
            _pluginDirectory = pluginDirectory;
            _log = log;
        }

        internal string ConfigPath
        {
            get { return Path.Combine(_pluginDirectory, ConfigRelativePath); }
        }

        internal void Reload()
        {
            _entries.Clear();
            LastError = null;

            string configPath = ConfigPath;
            if (!File.Exists(configPath))
            {
                LastError = "找不到 Boss 清单: " + configPath;
                return;
            }

            try
            {
                BossSceneConfig config = JsonConvert.DeserializeObject<BossSceneConfig>(File.ReadAllText(configPath));
                if (config == null || config.BossScenes == null)
                {
                    LastError = "Boss 清单内容为空: " + configPath;
                    return;
                }

                string saveDirectory = Path.Combine(_pluginDirectory, SaveDirectoryRelativePath);
                for (int i = 0; i < config.BossScenes.Count; i++)
                {
                    BossSceneConfigItem item = config.BossScenes[i];
                    if (item == null || string.IsNullOrEmpty(item.BossName))
                    {
                        continue;
                    }

                    BossSaveEntry entry = new BossSaveEntry(item, saveDirectory);
                    if (!entry.HasSaveFile)
                    {
                        continue;
                    }

                    if (_entries.ContainsKey(entry.BossName))
                    {
                        continue;
                    }

                    _entries[entry.BossName] = entry;
                }
            }
            catch (Exception exception)
            {
                LastError = "读取 Boss 清单失败: " + exception.Message;
                _log.LogError(LastError);
            }
        }

        internal string LastError { get; private set; }

        internal int Count
        {
            get { return _entries.Count; }
        }

        internal IEnumerable<string> BossNames
        {
            get { return _entries.Keys; }
        }

        internal bool TryGet(string bossName, out BossSaveEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(bossName))
            {
                return false;
            }

            return _entries.TryGetValue(bossName, out entry);
        }

        internal bool TryRead(string bossName, out BossSaveEntry entry, out byte[] bytes, out string error)
        {
            entry = null;
            bytes = null;
            error = null;

            if (!TryGet(bossName, out entry))
            {
                error = string.Format("Boss 清单里没有 {0}, 可用项 {1} 个", bossName, _entries.Count);
                return false;
            }

            try
            {
                bytes = File.ReadAllBytes(entry.SavePath);
            }
            catch (Exception exception)
            {
                error = "读取存档失败: " + exception.Message;
                return false;
            }

            return true;
        }
    }

    internal sealed class BossSaveEntry
    {
        internal BossSaveEntry(BossSceneConfigItem item, string saveDirectory)
        {
            BossName = item.BossName;
            SceneName = item.SceneName;
            Position = new Vector3(item.PosX, item.PosY, item.PosZ);
            SavePath = Path.Combine(saveDirectory, item.BossName + ".dat");
            HasSaveFile = File.Exists(SavePath);
        }

        internal string BossName { get; private set; }

        internal string SceneName { get; private set; }

        internal Vector3 Position { get; private set; }

        internal string SavePath { get; private set; }

        internal bool HasSaveFile { get; private set; }
    }
}
