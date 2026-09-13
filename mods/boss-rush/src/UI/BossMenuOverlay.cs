using System;
using System.Collections.Generic;
using BossRush.Assets;
using BossRush.Fighting;
using UnityEngine;

namespace BossRush.UI
{
    // 自绘 IMGUI 面板: 不依赖任何额外 ModMenu 类库, 也不会动游戏原生菜单的预制体.
    // 列表里点一下 Boss 名字就会走 BossFightLauncher 的流程.
    internal sealed class BossMenuOverlay : IDisposable
    {
        private const float PanelWidth = 470f;

        private const float MaxPanelHeight = 720f;

        private readonly BossRushPlugin _plugin;

        private readonly BossSceneCatalog _catalog;

        private readonly BossFightLauncher _launcher;

        private bool _visible;

        private bool _inputUnavailable;

        private bool _stylesReady;

        private Vector2 _scroll;

        private GUIStyle _titleStyle;

        private GUIStyle _labelStyle;

        private GUIStyle _warnStyle;

        private GUIStyle _buttonStyle;

        private GUIStyle _panelStyle;

        internal BossMenuOverlay(BossRushPlugin plugin, BossSceneCatalog catalog, BossFightLauncher launcher)
        {
            _plugin = plugin;
            _catalog = catalog;
            _launcher = launcher;
            _visible = plugin.ShowMenuOnStartup.Value;
        }

        internal void Tick()
        {
            if (_inputUnavailable)
            {
                return;
            }

            try
            {
                if (_plugin.Enabled.Value && _plugin.MenuKey.Value.IsDown())
                {
                    _visible = !_visible;
                }
                else if (_plugin.ReloadKey.Value.IsDown())
                {
                    _catalog.Reload();
                    _plugin.Log.LogInfo(string.Format("Boss 清单已重新读取, 可用 {0} 个.", _catalog.AvailableCount));
                }
            }
            catch (Exception exception)
            {
                _inputUnavailable = true;
                _plugin.Log.LogWarning("读不到键盘输入, 快捷键停用: " + exception.Message);
            }
        }

        internal void Draw()
        {
            if (!_visible)
            {
                return;
            }

            EnsureStyles();

            float height = Mathf.Min(MaxPanelHeight, Screen.height - 80f);
            Rect panel = new Rect(48f, 40f, PanelWidth, height);
            GUI.Box(panel, GUIContent.none, _panelStyle);

            GUILayout.BeginArea(new Rect(panel.x + 14f, panel.y + 12f, panel.width - 28f, panel.height - 24f));

            GUILayout.Label("Boss 战斗房", _titleStyle);
            GUILayout.Label(
                string.Format(
                    "可用 {0} / {1}, 载入槽位 {2}",
                    _catalog.AvailableCount,
                    _catalog.Entries.Count,
                    _plugin.SaveSlotIndex.Value),
                _labelStyle);

            if (!string.IsNullOrEmpty(_catalog.LastError))
            {
                GUILayout.Label(_catalog.LastError, _warnStyle);
            }

            GUILayout.Label("状态: " + _launcher.Status, _labelStyle);

            _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.ExpandHeight(true));

            IList<BossSceneEntry> entries = _catalog.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                BossSceneEntry entry = entries[i];
                bool previousEnabled = GUI.enabled;
                GUI.enabled = entry.HasSaveFile && !_launcher.Busy;

                string label = entry.HasSaveFile
                    ? entry.BossName + "    (" + entry.SceneName + ")"
                    : entry.BossName + "    (缺存档)";

                if (GUILayout.Button(label, _buttonStyle, GUILayout.Height(30f)))
                {
                    // 进战斗前先收起面板, 免得遮挡过场.
                    _visible = false;
                    _launcher.Request(entry);
                }

                GUI.enabled = previousEnabled;
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("重新读取清单", _buttonStyle, GUILayout.Height(28f)))
            {
                _catalog.Reload();
            }

            if (GUILayout.Button("关闭", _buttonStyle, GUILayout.Height(28f)))
            {
                _visible = false;
            }

            GUILayout.EndHorizontal();

            GUILayout.Label("F7 开关面板, F8 重新读取清单", _labelStyle);

            GUILayout.EndArea();
        }

        public void Dispose()
        {
            _stylesReady = false;
        }

        private void EnsureStyles()
        {
            if (_stylesReady)
            {
                return;
            }

            Font font = null;
            try
            {
                // 面板要显示中文 Boss 名, 默认 IMGUI 字体没有中文字形, 这里换成系统字体.
                font = Font.CreateDynamicFontFromOSFont(
                    new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" },
                    16);
            }
            catch (Exception exception)
            {
                _plugin.Log.LogWarning("创建中文字体失败, 面板改用默认字体: " + exception.Message);
            }

            _panelStyle = new GUIStyle(GUI.skin.box);

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize = 20;
            _titleStyle.fontStyle = FontStyle.Bold;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 14;
            _labelStyle.wordWrap = true;

            _warnStyle = new GUIStyle(_labelStyle);
            _warnStyle.normal.textColor = new Color(1f, 0.55f, 0.45f);

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = 15;
            _buttonStyle.alignment = TextAnchor.MiddleLeft;

            if (font != null)
            {
                _titleStyle.font = font;
                _labelStyle.font = font;
                _warnStyle.font = font;
                _buttonStyle.font = font;
            }

            _stylesReady = true;
        }
    }
}
