using System;
using System.Collections.Generic;
using BepInEx.Logging;
using RLEnv.Actions;
using RLEnv.Clips;
using RLEnv.Config;
using RLEnv.Diagnostics;
using RLEnv.Episode;
using RLEnv.Observation;
using RLEnv.TimeControl;
using RLEnv.Transport;

namespace RLEnv.Session
{
    // 训练会话的状态机: 收发协议消息, 编排回合, 在物理帧末采样观测.
    //
    // 采样点选在 CustomPlayerLoop 的 super-late fixed update, 保证伤害与死亡结算都已经落定.
    // 等待 Python 决策时不会把时间缩放设成 0 (会让 FixedUpdate 与游戏协程整体停摆),
    // 而是压到一个极小值, 让世界几乎不动但仍然活着.
    internal sealed class EnvSession : CustomPlayerLoop.ILateFixedUpdate, IDisposable
    {
        internal enum Phase
        {
            Booting,
            WaitingClient,
            Idle,
            Resetting,
            Running,
            Stepping,
            Stopped
        }

        private readonly RLEnvPlugin _plugin;

        private readonly EnvConfig _config;

        private readonly EnvServer _server;

        private readonly ManualLogSource _log;

        private readonly BossSaveRepository _saves;

        private readonly BossTracker _bosses = new BossTracker();

        private readonly CombatTracker _combat;

        private readonly ObservationCollector _collector;

        private readonly EpisodeResetter _resetter;

        private readonly GameSpeedController _speed = new GameSpeedController();

        private readonly TrainingGraphics _graphics = new TrainingGraphics();

        private readonly ClipRecorder _clips;

        private Phase _phase;

        private string _statusMessage;

        private int _stepIndex;

        private int _framesRemaining;

        private bool _registered;

        private float _runtimeSpeed;

        private bool _humanMode;

        private int _recordFrames;

        private int _recordStepIndex;

        internal EnvSession(RLEnvPlugin plugin, EnvConfig config, EnvServer server, ManualLogSource log, BossSaveRepository saves)
        {
            _plugin = plugin;
            _config = config;
            _server = server;
            _log = log;
            _saves = saves;

            _combat = new CombatTracker(_bosses);
            _collector = new ObservationCollector(_bosses, _combat);
            _resetter = new EpisodeResetter(saves, _bosses, log, delegate(string message)
            {
                _statusMessage = message;
            });

            _runtimeSpeed = config.Speed.Value;
            _clips = new ClipRecorder(
                log,
                config.ClipFps.Value,
                config.ClipSeconds.Value,
                config.ClipQuality.Value);
        }

        public bool isActiveAndEnabled
        {
            get { return _registered && _phase != Phase.Stopped; }
        }

        internal Phase CurrentPhase
        {
            get { return _phase; }
        }

        internal ObservationCollector Collector
        {
            get { return _collector; }
        }

        // 调试用: 打开后每帧刷新"观测到的矩形", 供屏幕线框渲染.
        internal void SetCollectDebugBoxes(bool enabled)
        {
            _collector.CollectDebugBoxes = enabled;
            if (enabled)
            {
                _collector.RescanHazards();
            }
        }

        internal void CollectDebugView()
        {
            _collector.CollectDebugView();
        }

        internal List<Diagnostics.ObservationBox> DebugBoxes
        {
            get { return _collector.DebugBoxes; }
        }

        internal string StatusMessage
        {
            get { return _statusMessage; }
        }

        internal void Start()
        {
            string schemaError = ObservationSchema.Validate();
            if (schemaError != null)
            {
                _log.LogError(schemaError);
            }

            _combat.Attach();
            _bosses.Resolve(true);
            _collector.CaptureArena();
            _speed.Ensure();
            // 没连上训练侧时保持原速, 免得装了插件正常玩也被加速.
            _speed.Apply(1f);

            _resetter.SpawnHoldFrames = 3f;
            _resetter.SaveSlotIndex = _config.SaveSlotIndex.Value;
            _resetter.SettleFrames = _config.SettleFrames.Value;
            _resetter.AllowMinimalPath = _config.MinimalReset.Value;
            _resetter.SkipWakeUpAnimation = _config.SkipWakeUpAnimation.Value;
            _resetter.BlockerNamePatterns = _config.BlockerNamePatterns.Value;
            _resetter.BlockerSpeed = _config.BlockerSpeed.Value;
            _resetter.DumpSceneOnReset = _config.DumpSceneOnReset.Value;
            _resetter.SetSpeed = delegate(float value)
            {
                _speed.Apply(value);
            };

            VirtualPad.Gate = InjectionGate.Ready;
            CustomPlayerLoop.RegisterSuperLateFixedUpdate(this);
            _registered = true;

            SetPhase(Phase.WaitingClient, "等待训练侧连接");
        }

        internal void Tick()
        {
            VirtualPad.Tick();
            FreezeMomentPatch.Suppress = _phase != Phase.WaitingClient && _phase != Phase.Booting && _phase != Phase.Stopped;
            // 只在训练侧连着的时候拦死亡流程, 平时玩不受影响.
            PlayerDeathSuppressor.Active = _phase != Phase.WaitingClient && _phase != Phase.Booting && _phase != Phase.Stopped;

            if (_phase == Phase.Stopped)
            {
                return;
            }

            if (_server.HasClient && _phase == Phase.WaitingClient)
            {
                OnClientConnected();
            }
            else if (!_server.HasClient && _phase != Phase.WaitingClient)
            {
                SetPhase(Phase.WaitingClient, "训练侧已断开");
                VirtualPad.Release(2);
                _speed.Apply(1f);
                _humanMode = false;
                ApplyTrainingGraphics(false);

                // 训练侧中途断开时把没跑完的重置收掉, 否则下一个连接会被"上一次重置还没结束"挡住.
                if (_resetter.Busy)
                {
                    _resetter.Abort();
                }
            }

            EnvCommand command;
            while (_server.TryDequeueCommand(out command))
            {
                HandleCommand(command);
            }

            if (_phase == Phase.Resetting)
            {
                _resetter.Tick();
            }
        }

        public void LateFixedUpdate()
        {
            if (_phase == Phase.Stepping)
            {
                TickStepping();
                return;
            }

            if (_humanMode && _phase == Phase.Running)
            {
                TickRecording();
                return;
            }

            if (_phase == Phase.Running || _phase == Phase.Resetting)
            {
                _combat.Refresh();
            }
        }

        public void Dispose()
        {
            if (_registered)
            {
                UnregisterFromPlayerLoop();
            }

            ApplyTrainingGraphics(false);
            VirtualPad.Release(1);
            _combat.Dispose();
            _speed.Dispose();
            SetPhase(Phase.Stopped, "已停止");
        }

        private void UnregisterFromPlayerLoop()
        {
            try
            {
                CustomPlayerLoop.UnregisterSuperLateFixedUpdate(this);
            }
            catch (Exception)
            {
            }

            _registered = false;
        }

        private void OnClientConnected()
        {
            _server.SendHello(BuildHelloJson());
            _server.SendStateMap(_collector.States.ToJson());
            _speed.Apply(_config.IdleTimeScale.Value);
            ApplyTrainingGraphics(true);
            SetPhase(Phase.Idle, "已连接, 等 Reset");
        }

        // 训练侧连着时降画质提速, 断开就恢复.
        private void ApplyTrainingGraphics(bool training)
        {
            if (_graphics == null || !_config.TrainingGraphics.Value)
            {
                return;
            }

            if (training && !_graphics.Applied)
            {
                _graphics.Apply();
                _log.LogInfo("训练期画质降级: " + _graphics.Describe());
            }
            else if (!training && _graphics.Applied)
            {
                _graphics.Restore();
                _log.LogInfo("已恢复训练前的画质设置");
            }
        }

        private void HandleCommand(EnvCommand command)
        {
            switch (command.Type)
            {
                case Protocol.MessageType.Ping:
                    SendStatusNow();
                    break;
                case Protocol.MessageType.Close:
                    _log.LogInfo("训练侧主动关闭连接");
                    SetPhase(Phase.Idle, "训练侧关闭");
                    break;
                case Protocol.MessageType.SetSpeed:
                    _runtimeSpeed = command.Speed;
                    if (_phase == Phase.Running && !_humanMode)
                    {
                        _speed.Apply(_runtimeSpeed);
                    }

                    _log.LogInfo("运行速度已改为 " + _runtimeSpeed);
                    break;
                case Protocol.MessageType.SetHumanMode:
                    SetHumanMode(command.Flag);
                    break;
                case Protocol.MessageType.SetStepping:
                    ApplyStepping(command.StepFrames, command.MaxEpisodeSteps);
                    break;
                case Protocol.MessageType.SetClip:
                    ApplyClip(command.ClipFps, command.ClipSeconds);
                    break;
                case Protocol.MessageType.SaveClip:
                    HandleSaveClip(command.Flag);
                    break;
                case Protocol.MessageType.Reset:
                    BeginReset();
                    break;
                case Protocol.MessageType.Step:
                    BeginStep(command.Action);
                    break;
            }
        }

        // 决策粒度可以在训练中途改: 步长越小, 每个决策跨的游戏时间越短, 出手时机能卡得更准,
        // 代价是一局要的步数按比例变多. 传 0 表示这一项保持原样.
        private void ApplyStepping(int stepFrames, int maxEpisodeSteps)
        {
            if (stepFrames > 0)
            {
                _config.StepFrames.Value = Math.Max(1, Math.Min(200, stepFrames));
            }

            if (maxEpisodeSteps > 0)
            {
                _config.MaxEpisodeSteps.Value = Math.Max(10, Math.Min(100000, maxEpisodeSteps));
            }

            _log.LogInfo(
                string.Format(
                    "决策粒度已改为 {0} 物理帧/步 (约 {1:F3} 秒游戏时间), 单回合上限 {2} 步",
                    _config.StepFrames.Value,
                    _config.StepFrames.Value / 60f,
                    _config.MaxEpisodeSteps.Value));
        }

        // 回放帧率与缓冲时长也能在训练中途改: 帧率越高回放越顺, 但每秒同步截屏的次数也越多
        // (每次截屏都会等 GPU), 训练吞吐会掉一点. 传 0 表示这一项保持原样.
        private void ApplyClip(int fps, int seconds)
        {
            if (fps > 0)
            {
                _config.ClipFps.Value = Math.Max(1, Math.Min(60, fps));
            }

            if (seconds > 0)
            {
                _config.ClipSeconds.Value = Math.Max(1, Math.Min(600, seconds));
            }

            _clips.Reconfigure(_config.ClipFps.Value, _config.ClipSeconds.Value);
            _log.LogInfo(
                string.Format(
                    "回放录制已改为 {0} 帧/秒, 缓冲 {1} 秒 ({2} 帧)",
                    _clips.Fps,
                    _clips.CapacitySeconds,
                    _clips.Fps * _clips.CapacitySeconds));
        }

        private void BeginReset()
        {
            if (_phase == Phase.Resetting)
            {
                _log.LogWarning("上一次重置还没结束, 忽略本次 Reset");
                return;
            }

            if (_phase == Phase.WaitingClient)
            {
                return;
            }

            VirtualPad.Release(2);
            _speed.Apply(1f);
            _combat.ResetTotals();
            _clips.BeginEpisode();
            _stepIndex = 0;
            _recordFrames = 0;
            _recordStepIndex = 0;
            SetPhase(Phase.Resetting, "开始重置回合");

            string bossName = _config.BossName.Value;
            _resetter.Begin(bossName, _config.ResetTimeout.Value, OnResetFinished);
        }

        private void OnResetFinished(bool success, string message)
        {
            if (!success)
            {
                SetPhase(Phase.Idle, "重置失败: " + message);
                _server.SendError(message);
                return;
            }

            _bosses.Resolve(true);
            _collector.CaptureArena();
            _collector.RescanHazards();
            _combat.BeginStep();
            _combat.Refresh();

            _speed.Apply(_config.IdleTimeScale.Value);
            SetPhase(Phase.Running, "回合就绪");
            _server.SendStatus(Protocol.SessionState.EpisodeReady, message);

            // 回合的初始观测: Python 侧的 reset() 靠它返回.
            _collector.Collect(0);
            _server.SendObservation(0, Protocol.FlagEpisodeStart, _collector.Buffer);
            if (_collector.States.Dirty)
            {
                _server.SendStateMap(_collector.States.ToJson());
                _collector.States.ClearDirty();
            }
        }

        // 训练侧在一局结束时告诉我们这局是不是击杀: 击杀就把缓冲里的画面落盘, 否则丢掉.
        private void HandleSaveClip(bool save)
        {
            if (!save)
            {
                _clips.Discard();
                _server.SendStatus(ToSessionState(_phase), "回放片段已丢弃");
                return;
            }

            string directory = _clips.Save(System.IO.Path.Combine(_plugin.ClipsRoot, "raw"));
            if (directory == null)
            {
                _server.SendStatus(ToSessionState(_phase), "没有可保存的回放片段");
                return;
            }

            _server.SendStatus(ToSessionState(_phase), "clip:" + directory);
        }

        private void BeginStep(EnvAction action)
        {
            if (_phase != Phase.Running)
            {
                _server.SendError("当前状态不能执行 Step: " + _phase);
                return;
            }

            if (_humanMode)
            {
                _server.SendError("正在录制人类示范, 不接受策略动作");
                return;
            }

            _combat.Refresh();
            VirtualPad.Arm(action);
            _combat.BeginStep();
            _framesRemaining = Math.Max(1, _config.StepFrames.Value);
            _speed.Apply(_runtimeSpeed);
            SetPhase(Phase.Stepping, "执行动作 " + action);
        }

        private void TickStepping()
        {
            _combat.Refresh();
            // 回合进行中才抓帧: 缓冲区只保留最近若干秒, 击杀时由训练侧决定是否落盘.
            _clips.Tick(_config.ClipEnabled.Value);

            if (_combat.BossKilledStep || _combat.PlayerDiedStep)
            {
                CompleteStep(true, false);
                return;
            }

            _framesRemaining--;
            if (_framesRemaining > 0)
            {
                return;
            }

            _stepIndex++;
            if (_stepIndex >= Math.Max(1, _config.MaxEpisodeSteps.Value))
            {
                CompleteStep(false, true);
                return;
            }

            CompleteStep(false, false);
        }

        // 人类示范模式: 不注入任何输入, 游戏按常速跑, 每 StepFrames 个物理帧把
        // (观测, 玩家真实按键) 成对发给训练侧.
        private void SetHumanMode(bool enabled)
        {
            _humanMode = enabled;
            _recordFrames = 0;
            _recordStepIndex = 0;
            VirtualPad.Release(2);
            _speed.Apply(enabled ? 1f : _runtimeSpeed);
            _log.LogInfo(enabled ? "进入人类示范录制模式 (常速, 不注入输入)" : "退出人类示范录制模式");
            _server.SendStatus(ToSessionState(_phase), enabled ? "录制中" : "已退出录制");
        }

        private void TickRecording()
        {
            _combat.Refresh();

            if (_combat.BossKilledStep || _combat.PlayerDiedStep)
            {
                int flags = Protocol.FlagTerminated;
                _collector.Collect(_recordStepIndex);
                _server.SendObservation(_recordStepIndex, flags, _collector.Buffer);
                SetPhase(Phase.Idle, _combat.BossKilledStep ? "Boss 已击杀 (录制)" : "主角已死亡 (录制)");
                return;
            }

            _recordFrames++;
            if (_recordFrames < Math.Max(1, _config.StepFrames.Value))
            {
                return;
            }

            _recordFrames = 0;
            _recordStepIndex++;

            EnvAction action;
            int[] indices;
            if (!HumanInputReader.TryRead(out action, out indices))
            {
                return;
            }

            _collector.Collect(_recordStepIndex);
            _server.SendRecord(_recordStepIndex, _collector.Buffer, indices);

            // 一个记录步到此结束: 把"本步"的事件计数清零.
            // 漏掉这一句的话, 示范里的 damage_dealt_step 会变成整局的累计值 (0 一路涨到 Boss 总血量),
            // 而训练时同一列是每步增量, 行为克隆学到的输入分布与上场时看到的完全对不上.
            _combat.BeginStep();

            if (_collector.States.Dirty)
            {
                _server.SendStateMap(_collector.States.ToJson());
                _collector.States.ClearDirty();
            }
        }

        private void CompleteStep(bool terminated, bool truncated)
        {
            VirtualPad.Release(2);

            // 回合因为死亡而结束时, 游戏自己还要跑死亡与复活流程. 这一步必须用正常速度,
            // 否则那个流程会以 IdleTimeScale 爬行, 表现为永远卡在载入界面.
            _speed.Apply(terminated ? 1f : _config.IdleTimeScale.Value);

            _collector.Collect(_stepIndex);

            int flags = 0;
            if (terminated)
            {
                flags |= 1;
            }

            if (truncated)
            {
                flags |= 2;
            }

            _server.SendObservation(_stepIndex, flags, _collector.Buffer);

            if (_collector.States.Dirty)
            {
                _server.SendStateMap(_collector.States.ToJson());
                _collector.States.ClearDirty();
            }

            if (terminated || truncated)
            {
                SetPhase(Phase.Idle, terminated
                    ? (_combat.BossKilledStep ? "Boss 已击杀" : "主角已死亡")
                    : "达到最大步数");
                return;
            }

            SetPhase(Phase.Running, "等待下一步");
        }

        private string BuildHelloJson()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder(512);
            builder.Append('{');
            builder.Append("\"protocol\":").Append(Protocol.Version);
            builder.Append(",\"magic\":").Append(Protocol.Magic);
            builder.Append(",\"observations\":").Append(ObservationSchema.ToJson());
            builder.Append(",\"action\":{\"horizontal\":3,\"vertical\":3,\"jump\":2,\"attack\":2,\"bind\":2}");
            builder.Append(",\"step_frames\":").Append(_config.StepFrames.Value);
            builder.Append(",\"max_episode_steps\":").Append(_config.MaxEpisodeSteps.Value);
            builder.Append(",\"clip_fps\":").Append(_clips.Fps);
            builder.Append(",\"clip_seconds\":").Append(_clips.CapacitySeconds);
            builder.Append(",\"boss\":\"").Append(_config.BossName.Value).Append('"');
            builder.Append(",\"scene\":\"").Append(_config.SceneName.Value).Append('"');
            builder.Append(",\"bosses\":[");
            int index = 0;
            foreach (string name in _saves.BossNames)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(name).Append('"');
                index++;
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private void SetPhase(Phase phase, string message)
        {
            bool changed = _phase != phase;
            _phase = phase;
            _statusMessage = message;
            if (changed)
            {
                _log.LogInfo("会话状态: " + phase + " (" + message + ")");
            }
        }

        private void SendStatusNow()
        {
            _server.SendStatus(ToSessionState(_phase), _statusMessage);
        }

        private static Protocol.SessionState ToSessionState(Phase phase)
        {
            switch (phase)
            {
                case Phase.WaitingClient:
                    return Protocol.SessionState.WaitingForClient;
                case Phase.Idle:
                    return Protocol.SessionState.Idle;
                case Phase.Resetting:
                    return Protocol.SessionState.Resetting;
                case Phase.Stepping:
                    return Protocol.SessionState.Stepping;
                case Phase.Running:
                    return Protocol.SessionState.EpisodeReady;
                case Phase.Stopped:
                    return Protocol.SessionState.Closed;
                default:
                    return Protocol.SessionState.Booting;
            }
        }
    }
}
