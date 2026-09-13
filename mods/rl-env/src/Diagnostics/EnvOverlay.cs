using UnityEngine;
using RLEnv.Actions;
using RLEnv.Observation;

namespace RLEnv.Diagnostics
{
    // 排查用的状态面板: 显示会话阶段, 端口, 连接情况, Boss 血量与最近一次观测的关键字段.
    // 训练时用不上, 关掉即可 (配置 UI/ShowOverlay).
    internal sealed class EnvOverlay : System.IDisposable
    {
        private readonly RLEnvPlugin _plugin;

        private bool _visible;

        private GUIStyle _style;

        private float _fps;

        private float _fpsTimer;

        private int _fpsFrames;

        internal EnvOverlay(RLEnvPlugin plugin)
        {
            _plugin = plugin;
            _visible = plugin.Settings.ShowOverlay.Value;
        }

        internal void Tick()
        {
            if (Input.GetKeyDown(_plugin.Settings.OverlayKey.Value.MainKey))
            {
                _visible = !_visible;
            }

            _fpsFrames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                _fps = _fpsFrames / _fpsTimer;
                _fpsFrames = 0;
                _fpsTimer = 0f;
            }
        }

        internal void Draw()
        {
            if (!_visible)
            {
                return;
            }

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = 13;
                _style.richText = false;
            }

            GUILayout.BeginArea(new Rect(12f, 12f, 460f, 320f), GUI.skin.box);
            GUILayout.Label("Silksong RL Env " + RLEnvPlugin.PluginVersion, _style);

            Session.EnvSession session = _plugin.Session;
            GUILayout.Label(string.Format("阶段 {0}   {1}",
                session != null ? session.CurrentPhase.ToString() : "(未启动)",
                session != null ? session.StatusMessage : string.Empty), _style);
            GUILayout.Label(string.Format("FPS {0:F0}   游戏时间倍率 {1}", _fps, Time.timeScale), _style);

            ObservationCollector collector = session != null ? session.Collector : null;
            if (collector != null)
            {
                float[] buffer = collector.Buffer;
                GUILayout.Label(string.Format(
                    "主角 位置 ({0:F1}, {1:F1})  速度 ({2:F1}, {3:F1})  血 {4:F0}/{5:F0}",
                    buffer[(int)ObsField.PlayerPosXWorld],
                    buffer[(int)ObsField.PlayerPosYWorld],
                    buffer[(int)ObsField.PlayerVelX],
                    buffer[(int)ObsField.PlayerVelY],
                    buffer[(int)ObsField.PlayerHealth],
                    buffer[(int)ObsField.PlayerHealth] / Mathf.Max(0.01f, buffer[(int)ObsField.PlayerHealthRatio])),
                    _style);
                GUILayout.Label(string.Format(
                    "Boss 存活 {0}  血量 {1:F0}/{2:F0}  相对距离 {3:F2}  状态 id {4:F0}",
                    buffer[(int)ObsField.BossAlive] > 0.5f ? "是" : "否",
                    buffer[(int)ObsField.BossHealth],
                    buffer[(int)ObsField.BossHealthMax],
                    buffer[(int)ObsField.BossDistanceN],
                    buffer[(int)ObsField.BossStateId]),
                    _style);
                GUILayout.Label(string.Format(
                    "本步 造成伤害 {0:F0}  受到伤害 {1:F0}  Boss 死亡 {2}  主角死亡 {3}",
                    buffer[(int)ObsField.DamageDealtStep],
                    buffer[(int)ObsField.DamageTakenStep],
                    buffer[(int)ObsField.BossKilledStep] > 0.5f ? "是" : "否",
                    buffer[(int)ObsField.PlayerDiedStep] > 0.5f ? "是" : "否"),
                    _style);
                GUILayout.Label(string.Format(
                    "战场 中心 ({0:F1}, {1:F1})  半宽 {2:F1}  半高 {3:F1}  锁框 {4}",
                    buffer[(int)ObsField.ArenaCenterX],
                    buffer[(int)ObsField.ArenaCenterY],
                    buffer[(int)ObsField.ArenaHalfWidth],
                    buffer[(int)ObsField.ArenaHalfHeight],
                    collector.Arena.HasLock ? "有" : "无"),
                    _style);
                GUILayout.Label(string.Format(
                    "物理帧 {0:F0}  状态种类 {1}  当前 Boss 状态 {2}",
                    buffer[(int)ObsField.PhysicsFrame],
                    collector.States.Count,
                    collector.States.KeyOf((int)buffer[(int)ObsField.BossStateId])),
                    _style);
            }

            GUILayout.Label("虚拟输入: " + VirtualPad.Current, _style);
            GUILayout.Label(string.Format("Boss 存档目录可用 {0} 份, 当前 Boss {1}",
                _plugin.Saves.Count,
                _plugin.Settings.BossName.Value), _style);
            GUILayout.Label(string.Format("端口 {0}  客户端 {1}  面板快捷键 {2}",
                _plugin.Settings.Port.Value,
                _plugin.Server != null && _plugin.Server.HasClient ? "已连接" : "未连接",
                _plugin.Settings.OverlayKey.Value), _style);

            if (_plugin.Boxes != null && _plugin.Boxes.Enabled)
            {
                GUILayout.Label(string.Format(
                    "观测线框 {0} 个矩形 (青=主角 品红=Boss 绿=小怪 红=生效危险框 黄=未生效 蓝=场地), 快捷键 {1}",
                    _plugin.Boxes.BoxCount,
                    _plugin.Settings.BoxesKey.Value), _style);
            }
            else
            {
                GUILayout.Label(string.Format("观测线框已关闭, 快捷键 {0}", _plugin.Settings.BoxesKey.Value), _style);
            }

            GUILayout.EndArea();
        }

        public void Dispose()
        {
        }
    }
}
