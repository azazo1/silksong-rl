using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace BossRush.Assets
{
    // 读取 mod 目录下的 Boss 清单, 并逐个确认对应的存档文件是否齐备.
    internal sealed class BossSceneCatalog
    {
        private const string ConfigRelativePath = "BossScenes/BossSceneConfig.json";

        private const string SaveDirectoryRelativePath = "BossScenes/BossSave";

        private readonly string _pluginDirectory;

        private readonly ManualLogSource _log;

        private readonly List<BossSceneEntry> _entries = new List<BossSceneEntry>();

        internal BossSceneCatalog(string pluginDirectory, ManualLogSource log)
        {
            _pluginDirectory = pluginDirectory;
            _log = log;
        }

        internal IList<BossSceneEntry> Entries
        {
            get { return _entries; }
        }

        internal string LastError { get; private set; }

        internal string ConfigPath
        {
            get { return Path.Combine(_pluginDirectory, ConfigRelativePath); }
        }

        internal int AvailableCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].HasSaveFile)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        internal void Reload()
        {
            _entries.Clear();
            LastError = null;

            try
            {
                if (!File.Exists(ConfigPath))
                {
                    LastError = "找不到 Boss 清单: " + ConfigPath;
                    return;
                }

                string json = File.ReadAllText(ConfigPath);
                BossSceneConfig config = JsonConvert.DeserializeObject<BossSceneConfig>(json);
                if (config == null || config.BossScenes == null)
                {
                    LastError = "Boss 清单内容为空: " + ConfigPath;
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

                    _entries.Add(new BossSceneEntry(item, saveDirectory));
                    if (!_entries[_entries.Count - 1].HasSaveFile)
                    {
                        _log.LogWarning("Boss 存档缺失, 该项不可用: " + item.BossName);
                    }
                }
            }
            catch (Exception exception)
            {
                LastError = "读取 Boss 清单失败: " + exception.Message;
                _log.LogError(LastError);
            }
        }
    }

    internal sealed class BossSceneEntry
    {
        internal BossSceneEntry(BossSceneConfigItem item, string saveDirectory)
        {
            BossName = item.BossName;
            SceneName = item.SceneName;
            Position = new Vector3(item.PosX, item.PosY, item.PosZ);
            SaveFileName = item.BossName + ".dat";
            SavePath = Path.Combine(saveDirectory, SaveFileName);
            HasSaveFile = File.Exists(SavePath);
        }

        internal string BossName { get; private set; }

        internal string SceneName { get; private set; }

        internal Vector3 Position { get; private set; }

        internal string SaveFileName { get; private set; }

        internal string SavePath { get; private set; }

        internal bool HasSaveFile { get; private set; }
    }
}
