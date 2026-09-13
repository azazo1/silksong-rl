using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SilksongRL.ObjectOutlines
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ObjectOutlinesPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "silksongrl.object-outlines";
        public const string PluginName = "Silksong Object Outlines";
        public const string PluginVersion = "0.2.0";

        private static readonly OutlineCategory[] Categories =
        {
            OutlineCategory.Player,
            OutlineCategory.Enemy,
            OutlineCategory.Hazard,
            OutlineCategory.Interactable,
            OutlineCategory.Breakable,
        };

        private readonly OutlineScanner _scanner = new OutlineScanner();
        private readonly OutlineDrawer _drawer = new OutlineDrawer();
        private readonly List<OutlineCategory> _activeCategories = new List<OutlineCategory>();
        private readonly Dictionary<OutlineCategory, ConfigEntry<bool>> _categoryEnabled = new Dictionary<OutlineCategory, ConfigEntry<bool>>();
        private readonly Dictionary<OutlineCategory, ConfigEntry<string>> _categoryColorText = new Dictionary<OutlineCategory, ConfigEntry<string>>();
        private readonly Dictionary<OutlineCategory, Color> _colors = new Dictionary<OutlineCategory, Color>();

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<float> _refreshInterval;
        private ConfigEntry<int> _maxObjectsPerCategory;
        private ConfigEntry<bool> _rendererFallback;
        private ConfigEntry<bool> _useMesh;
        private ConfigEntry<bool> _logCounts;
        private ConfigEntry<bool> _showStats;

        private bool _shortcutUnavailable;
        private bool _warnedMissingRenderCallback;
        private int _renderCallCount;
        private float _nextScanAt;
        private string _statsText = string.Empty;

        private void Awake()
        {
            BindConfiguration();

            if (!_drawer.Prepare())
            {
                Logger.LogWarning("未能创建描边材质 (Shader.Find 失败), 边框不会显示.");
            }

            _scanner.Scan(_maxObjectsPerCategory.Value);
            UpdateStatsText();

            Logger.LogInfo(string.Format(
                "{0} {1} 已加载. 开关快捷键 {2}. {3}",
                PluginName, PluginVersion, _toggleKey.Value, _statsText));
        }

        private void Update()
        {
            HandleToggleKey();

            if (!_enabled.Value)
            {
                return;
            }

            // 几何每帧按对象当前变换重算, 边框才跟得住移动的对象;
            // 对象集合本身由低频扫描决定.
            _scanner.BuildGeometry(_activeCategories, _rendererFallback.Value);
            _drawer.Rebuild(_scanner, _activeCategories, _colors);

            if (_useMesh.Value)
            {
                _drawer.RenderMesh();
            }
            else if (!_warnedMissingRenderCallback && Time.frameCount > 120 && _renderCallCount == 0)
            {
                _warnedMissingRenderCallback = true;
                Logger.LogWarning("游戏没有给出渲染回调 (OnRenderObject), 逐顶点绘制方式可能不生效, 请把 Draw/MeshRendering 打开.");
            }

            if (Time.unscaledTime >= _nextScanAt)
            {
                _nextScanAt = Time.unscaledTime + _refreshInterval.Value;
                _scanner.Scan(_maxObjectsPerCategory.Value);
                UpdateStatsText();

                if (_logCounts.Value)
                {
                    Logger.LogInfo("场景边框统计: " + _statsText);
                }
            }
        }

        private void OnRenderObject()
        {
            _renderCallCount++;

            if (!_enabled.Value || _useMesh.Value)
            {
                return;
            }

            _drawer.RenderImmediate(Camera.current, _scanner, _activeCategories, _colors);
        }

        private void OnGUI()
        {
            if (!_showStats.Value || !_enabled.Value)
            {
                return;
            }

            GUI.Label(new Rect(12f, 12f, 900f, 64f), _statsText);
        }

        private void HandleToggleKey()
        {
            if (_shortcutUnavailable)
            {
                return;
            }

            try
            {
                if (_toggleKey.Value.IsDown())
                {
                    _enabled.Value = !_enabled.Value;
                    Logger.LogInfo("边框显示已" + (_enabled.Value ? "开启" : "关闭"));
                }
            }
            catch (System.Exception exception)
            {
                _shortcutUnavailable = true;
                Logger.LogWarning("无法读取键盘输入, 快捷键停用, 请改用配置文件或 Configuration Manager 开关: " + exception.Message);
            }
        }

        private void UpdateStatsText()
        {
            Vector3 bounds = _drawer.LastBoundsSize;
            _statsText = string.Format(
                "{0}  | 顶点={1} 几何={2}ms 扫描={3}ms 建网格={4}ms 范围={5}x{6}\n{7}",
                _scanner.DescribeCounts(),
                _scanner.TotalVertices,
                _scanner.LastGeometryMilliseconds,
                _scanner.LastScanMilliseconds,
                _drawer.LastRebuildMilliseconds,
                Mathf.RoundToInt(bounds.x),
                Mathf.RoundToInt(bounds.y),
                _scanner.CandidateSummary);
        }

        private void BindConfiguration()
        {
            _enabled = Config.Bind("General", "Enabled", true, "是否显示对象边框");
            _toggleKey = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F9), "开关边框的快捷键");
            _refreshInterval = Config.Bind(
                "General",
                "RefreshIntervalSeconds",
                0.5f,
                new ConfigDescription("重新收集描边对象的间隔 (秒); 几何位置是每帧更新的", new AcceptableValueRange<float>(0.05f, 10f)));
            _maxObjectsPerCategory = Config.Bind(
                "General",
                "MaxObjectsPerCategory",
                300,
                new ConfigDescription("每个类别最多描边的对象数", new AcceptableValueRange<int>(1, 5000)));
            _rendererFallback = Config.Bind("Draw", "RendererFallback", true, "对象没有碰撞体时, 退而用渲染器包围盒画框");
            _useMesh = Config.Bind("Draw", "MeshRendering", true, "用 Mesh 一次性提交绘制 (推荐); 关掉则退回逐顶点 GL 绘制");
            _showStats = Config.Bind("Draw", "ShowStatsOverlay", true, "在屏幕左上角显示统计信息");
            _logCounts = Config.Bind("Log", "LogCounts", false, "每次扫描后把统计信息写入日志");

            for (int i = 0; i < Categories.Length; i++)
            {
                OutlineCategory category = Categories[i];
                string name = category.ToString();
                bool defaultEnabled = category != OutlineCategory.Breakable;

                _categoryEnabled[category] = Config.Bind(
                    "Categories",
                    name,
                    defaultEnabled,
                    "是否绘制 " + name + " 类边框");
                _categoryColorText[category] = Config.Bind(
                    "Colors",
                    name + "Color",
                    DefaultColorText(category),
                    "颜色, 格式 R,G,B,A (0-255)");
            }

            RefreshCategorySettings();
        }

        private void RefreshCategorySettings()
        {
            _activeCategories.Clear();

            for (int i = 0; i < Categories.Length; i++)
            {
                OutlineCategory category = Categories[i];
                if (_categoryEnabled[category].Value)
                {
                    _activeCategories.Add(category);
                }

                _colors[category] = ParseColor(_categoryColorText[category].Value, DefaultColor(category));
            }
        }

        private static string DefaultColorText(OutlineCategory category)
        {
            Color color = DefaultColor(category);
            return string.Format(
                "{0},{1},{2},{3}",
                Mathf.RoundToInt(color.r * 255f),
                Mathf.RoundToInt(color.g * 255f),
                Mathf.RoundToInt(color.b * 255f),
                Mathf.RoundToInt(color.a * 255f));
        }

        private static Color DefaultColor(OutlineCategory category)
        {
            switch (category)
            {
                case OutlineCategory.Player:
                    return new Color(0f, 1f, 1f, 1f);
                case OutlineCategory.Enemy:
                    return new Color(1f, 0.25f, 0.25f, 1f);
                case OutlineCategory.Hazard:
                    return new Color(1f, 0.63f, 0f, 1f);
                case OutlineCategory.Interactable:
                    return new Color(0.25f, 0.63f, 1f, 1f);
                default:
                    return new Color(0.8f, 0.8f, 0.8f, 1f);
            }
        }

        private static Color ParseColor(string text, Color fallback)
        {
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }

            string[] parts = text.Split(',');
            if (parts.Length < 3)
            {
                return fallback;
            }

            float r;
            float g;
            float b;
            float a = 255f;

            if (!float.TryParse(parts[0].Trim(), out r)
                || !float.TryParse(parts[1].Trim(), out g)
                || !float.TryParse(parts[2].Trim(), out b))
            {
                return fallback;
            }

            if (parts.Length >= 4 && !float.TryParse(parts[3].Trim(), out a))
            {
                a = 255f;
            }

            return new Color(
                Mathf.Clamp01(r / 255f),
                Mathf.Clamp01(g / 255f),
                Mathf.Clamp01(b / 255f),
                Mathf.Clamp01(a / 255f));
        }
    }
}
