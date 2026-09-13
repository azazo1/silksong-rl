using System;
using System.Reflection;
using BepInEx.Logging;
using GlobalEnums;
using HarmonyLib;
using RLEnv.Observation;
using UnityEngine;

namespace RLEnv.Episode
{
    internal enum ResetPhase
    {
        Idle,
        Stabilizing,
        Preparing,
        LoadingScene,
        WaitingScene,
        WaitingBoss,
        Done,
        Failed
    }

    // 一个回合的重置流程.
    //
    // 做法参考 .tmp/research/03-reset.md 的推荐方案: 存档只解析一次成 JSON 常驻内存, 每回合重新
    // 反序列化出一份全新的 SaveGameData 挂给游戏, 再用 ReadyForRespawn 发起一次同场景重载.
    // 重置窗口内屏蔽 SaveLevelState, 避免旧场景的持久化项把新数据写脏 (否则 Boss 会隐形且不触发战斗).
    //
    // 若当前还没有主角 (例如停在主菜单), 退化为 UIManager.UIContinueGame 这条完整载入流程.
    internal sealed class EpisodeResetter
    {
        private readonly BossSaveRepository _saves;

        private readonly BossTracker _bosses;

        private readonly ManualLogSource _log;

        private readonly Action<string> _statusSink;

        private readonly MethodInfo _setLoadedGameData;

        private readonly BlockerBreaker _breaker;

        private string _cachedBossName;

        private string _cachedJson;

        private string _markerName;

        private ResetPhase _phase;

        private string _status;

        private Action<bool, string> _callback;

        private BossSaveEntry _entry;

        private float _deadline;

        private float _timeoutSeconds;

        private int _settleFrames;

        private int _bossWaitFrames;

        private bool _battleStartAttempted;

        private bool _usedMinimalPath;

        private bool _movedIntoArena;

        private bool _blockersBroken;

        private bool _blockersScanned;

        private int _arenaResolveFrames;

        private string[] _blockerPatterns = new string[0];

        private int _framesSincArenaMove;

        private string _initialBossState;

        private string _pendingBossName;

        private string _sceneDataJson;

        private bool _pendingStart;

        private float _stableDeadline;

        private float _lastStableLog;

        private int _stableFrames;

        // 等游戏自身死亡/复活/切场景流程结束的最长时间.
        private const float StabilizeTimeoutSeconds = 20f;

        // 稳定状态需要连续保持多少帧才开始重置: 主角"刚复活"的那一瞬间各项标志位可能已经干净,
        // 但游戏的复活流程还在跑, 这时候插进去重载场景会把双方都卡住.
        private const int StableFramesRequired = 40;

        private string DescribeGameState()
        {
            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null)
            {
                return "GameManager=null";
            }

            HeroController hero = HeroController.instance;
            string heroInfo = "hero=null";
            if (hero != null)
            {
                HeroControllerStates state = hero.cState;
                heroInfo = string.Format(
                    "hero(dead={0}, hazDead={1}, hazResp={2}, trans={3}, inPos={4}, pos=({5:F1},{6:F1}))",
                    state != null && state.dead,
                    state != null && state.hazardDeath,
                    state != null && state.hazardRespawning,
                    state != null && state.transitioning,
                    hero.isHeroInPosition,
                    hero.transform.position.x,
                    hero.transform.position.y);
            }

            return string.Format(
                "state={0} inTransition={1} finishedEntering={2} loadingTransition={3} paused={4} timeScale={5:F3} scene={6} {7}",
                gameManager.GameState,
                gameManager.IsInSceneTransition,
                gameManager.HasFinishedEnteringScene,
                gameManager.IsLoadingSceneTransition,
                gameManager.isPaused,
                Time.timeScale,
                gameManager.GetSceneNameString(),
                heroInfo);
        }

        // 主角进场后最多等多少物理帧还看不到 Boss 状态变化, 就按就绪处理.
        private const int WakeTimeoutFrames = 120;

        internal EpisodeResetter(BossSaveRepository saves, BossTracker bosses, ManualLogSource log, Action<string> statusSink)
        {
            _saves = saves;
            _bosses = bosses;
            _log = log;
            _statusSink = statusSink;
            _phase = ResetPhase.Idle;
            _status = "待机";
            _breaker = new BlockerBreaker(log);

            _setLoadedGameData = AccessTools.Method(
                typeof(GameManager),
                "SetLoadedGameData",
                new Type[] { typeof(SaveGameData), typeof(int) });
            if (_setLoadedGameData == null)
            {
                _log.LogWarning("找不到 GameManager.SetLoadedGameData, 重置将退回 UIContinueGame 流程");
            }
        }

        internal ResetPhase Phase
        {
            get { return _phase; }
        }

        internal string Status
        {
            get { return _status; }
        }

        internal bool Busy
        {
            get { return _phase != ResetPhase.Idle && _phase != ResetPhase.Done && _phase != ResetPhase.Failed; }
        }

        internal int SaveSlotIndex { get; set; }

        internal float SpawnHoldFrames { get; set; }

        internal int SettleFrames { get; set; }

        internal bool AllowMinimalPath { get; set; }

        internal bool SkipWakeUpAnimation { get; set; }
        // 排查用: 重置结束时把场景对象打进日志 (平时关掉, 否则日志会很吵).
        internal bool DumpSceneOnReset { get; set; }

        // 需要在重置时程序化打烂的挡门障碍物名字片段 (逗号分隔).
        internal string BlockerNamePatterns { get; set; }

        // 破门阶段的游戏速度倍率 (破门本身是纯体力活, 加速不影响正确性).
        internal float BlockerSpeed { get; set; } = 3f;

        // 由会话注入的时间倍率设置入口.
        internal Action<float> SetSpeed { get; set; }

        internal void Begin(string bossName, float timeoutSeconds, Action<bool, string> onFinished)
        {
            if (Busy)
            {
                onFinished(false, "上一次重置还没结束");
                return;
            }

            _callback = onFinished;
            _timeoutSeconds = timeoutSeconds;
            _deadline = Time.realtimeSinceStartup + timeoutSeconds;
            _battleStartAttempted = false;
            _bossWaitFrames = 0;
            _settleFrames = 0;
            _usedMinimalPath = false;
            _movedIntoArena = false;
            _blockersBroken = false;
            _blockersScanned = false;
            _arenaResolveFrames = 0;
            _framesSincArenaMove = 0;
            _initialBossState = null;
            _blockerPatterns = BlockerBreaker.ParsePatterns(BlockerNamePatterns);
            _pendingBossName = bossName;
            _pendingStart = true;
            _stableFrames = 0;
            _stableDeadline = Time.realtimeSinceStartup + StabilizeTimeoutSeconds;

            SetStatus(ResetPhase.Stabilizing, "等待游戏进入稳定状态");
            TryStartPreparation();
        }

        // 主角刚死掉时游戏正在跑自己的死亡/复活流程, 这时发起场景重载会把两边都卡住
        // (表现为永远停在载入界面). 因此先等游戏把死亡流程走完再重置.
        private bool IsGameStable(out string reason)
        {
            reason = null;

            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null)
            {
                reason = "GameManager 未就绪";
                return false;
            }

            HeroController hero = HeroController.instance;
            if (hero == null)
            {
                // 还没进游戏 (主菜单): 没有死亡中间态可言, 交给完整的继续游戏流程.
                return true;
            }

            if (gameManager.IsInSceneTransition)
            {
                reason = "场景切换中";
                return false;
            }

            if (!gameManager.HasFinishedEnteringScene)
            {
                reason = "入场未完成";
                return false;
            }

            if (gameManager.GameState != GameState.PLAYING)
            {
                reason = "游戏状态 " + gameManager.GameState;
                return false;
            }

            HeroControllerStates state = hero.cState;
            bool deathSuppressed = PlayerDeathSuppressor.RecentlySuppressed(15f);
            if (!deathSuppressed && state != null && (state.dead || state.hazardDeath || state.hazardRespawning || state.transitioning))
            {
                reason = "主角处于死亡或切换流程中";
                return false;
            }

            if (state != null && state.transitioning)
            {
                reason = "主角正在场景切换中";
                return false;
            }

            if (Time.timeScale <= 0.0001f)
            {
                reason = "游戏时间被冻结";
                return false;
            }

            return true;
        }

        // 游戏可能还没启动完 (插件比 GameManager 先就绪), 这时不报错, 每帧重试到超时为止.
        private bool TryStartPreparation()
        {
            if (Platform.Current == null)
            {
                return false;
            }

            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null)
            {
                return false;
            }

            byte[] bytes;
            string error;
            if (!_saves.TryRead(_pendingBossName, out _entry, out bytes, out error))
            {
                _pendingStart = false;
                Finish(false, error);
                return true;
            }

            SetStatus(ResetPhase.Preparing, "准备存档数据");

            string json = GetPristineJson(_pendingBossName, bytes);
            if (json == null)
            {
                _pendingStart = false;
                return true;
            }

            _markerName = RespawnPointBuilder.Ensure(_entry.SceneName, _entry.BossName, _entry.Position, SkipWakeUpAnimation);
            HeroSpawnGuider.Ensure().Arm(_entry.Position, (int)Mathf.Max(1f, SpawnHoldFrames));

            SaveGameData fresh = Deserialize(json);
            if (fresh == null)
            {
                _pendingStart = false;
                Finish(false, "存档反序列化失败");
                return true;
            }

            ApplyPlayerData(fresh);

            // 从这里开始到场景就绪为止都属于重置窗口.
            if (SetSpeed != null)
            {
                // 场景切换期间回到常速, 避免和游戏的加载/淡入协程打架.
                SetSpeed(1f);
            }

            SaveLevelStatePatch.Suppress = true;
            CleanupBeforeTransition();

            bool canUseMinimal = AllowMinimalPath
                && _setLoadedGameData != null
                && gameManager.hero_ctrl != null
                && gameManager.IsGameplayScene();

            if (canUseMinimal)
            {
                // 已经在游戏里: 直接换数据 + 原地复活式重载, 跳过 RunContinueGame 的池与主角实例化.
                _usedMinimalPath = true;
                SetStatus(ResetPhase.LoadingScene, "换存档数据并重载场景");
                try
                {
                    _setLoadedGameData.Invoke(gameManager, new object[] { fresh, SaveSlotIndex });
                    CheatManager.SceneEntryWait = 0f;
                    gameManager.ReadyForRespawn(false);
                }
                catch (Exception exception)
                {
                    SaveLevelStatePatch.Suppress = false;
                    _pendingStart = false;
                    Finish(false, "精简重置失败: " + exception.Message);
                    return true;
                }
            }
            else
            {
                // 还在菜单/没有主角: 只能走完整的继续游戏流程.
                SetStatus(ResetPhase.LoadingScene, "走 UIContinueGame 载入槽位 " + SaveSlotIndex);
                try
                {
                    UIManager.instance.UIContinueGame(SaveSlotIndex, fresh);
                }
                catch (Exception exception)
                {
                    SaveLevelStatePatch.Suppress = false;
                    _pendingStart = false;
                    Finish(false, "UIContinueGame 失败: " + exception.Message);
                    return true;
                }
            }

            _pendingStart = false;
            SetStatus(ResetPhase.WaitingScene, "等待场景就绪");
            return true;
        }

        internal void Tick()
        {
            if (!Busy)
            {
                return;
            }

            if (Time.realtimeSinceStartup > _deadline)
            {
                SaveLevelStatePatch.Suppress = false;
                Finish(false, string.Format("重置超时 ({0} 秒), 停在阶段 {1}", _timeoutSeconds, _phase));
                return;
            }

            if (_pendingStart)
            {
                string reason;
                bool stable = IsGameStable(out reason);
                if (stable)
                {
                    _stableFrames++;
                }
                else
                {
                    _stableFrames = 0;
                }

                bool stableTimedOut = Time.realtimeSinceStartup > _stableDeadline;
                bool enoughQuiet = _stableFrames >= StableFramesRequired;
                if (!enoughQuiet && !stableTimedOut)
                {
                    SetStatus(ResetPhase.Stabilizing, "等待游戏稳定: " + (reason ?? "刚复活, 观察中"));
                    if (Time.realtimeSinceStartup - _lastStableLog > 2f)
                    {
                        _lastStableLog = Time.realtimeSinceStartup;
                        _log.LogInfo(string.Format(
                            "等待游戏稳定 ({0}/{1} 帧): {2} | {3}",
                            _stableFrames,
                            StableFramesRequired,
                            reason ?? "刚复活, 观察中",
                            DescribeGameState()));
                    }

                    return;
                }

                if (!enoughQuiet)
                {
                    _log.LogWarning("等稳定超时, 仍然开始重置 (最后原因: " + (reason ?? "-") + ")");
                }

                SetStatus(ResetPhase.Preparing, "准备重置");
                TryStartPreparation();
                return;
            }

            if (_phase == ResetPhase.WaitingScene)
            {
                TickWaitingScene();
            }
            else if (_phase == ResetPhase.WaitingBoss)
            {
                TickWaitingBoss();
            }
        }

        internal void Abort()
        {
            SaveLevelStatePatch.Suppress = false;
            if (Busy)
            {
                Finish(false, "已取消");
            }
        }

        private string GetPristineJson(string bossName, byte[] bytes)
        {
            if (_cachedJson != null && _cachedBossName == bossName)
            {
                return _cachedJson;
            }

            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null)
            {
                Finish(false, "GameManager 不可用, 无法解析存档");
                return null;
            }

            try
            {
                _cachedJson = gameManager.GetJsonForSaveBytes(bytes);
                _cachedBossName = bossName;
            }
            catch (Exception exception)
            {
                Finish(false, "解析存档失败: " + exception.Message);
                return null;
            }

            if (string.IsNullOrEmpty(_cachedJson))
            {
                Finish(false, "存档解析结果为空");
                return null;
            }

            _log.LogInfo(string.Format("{0}: 存档已解析为内存态, {1} 字符", bossName, _cachedJson.Length));
            return _cachedJson;
        }

        private SaveGameData Deserialize(string json)
        {
            try
            {
                return SaveDataUtility.DeserializeSaveData<SaveGameData>(json);
            }
            catch (Exception exception)
            {
                _log.LogError("反序列化存档失败: " + exception.Message);
                return null;
            }
        }

        private void ApplyPlayerData(SaveGameData data)
        {
            PlayerData playerData = data != null ? data.playerData : null;
            if (playerData == null)
            {
                return;
            }

            // 用"门已经打烂"的那份场景数据, 之后每回合都不用再重打一遍门.
            if (_sceneDataJson != null)
            {
                try
                {
                    data.sceneData = UnityEngine.JsonUtility.FromJson<SceneData>(_sceneDataJson);
                }
                catch (Exception exception)
                {
                    _log.LogWarning("套用已破门的场景数据失败, 本回合重新破门: " + exception.Message);
                    _sceneDataJson = null;
                }
            }

            playerData.respawnType = 0;
            playerData.respawnScene = _entry.SceneName;
            playerData.respawnMarkerName = _markerName;
            playerData.ResetTempRespawn();
            playerData.ResetCutsceneBools();
            playerData.atBench = false;
            playerData.health = playerData.maxHealth;
            playerData.silk = 0;
        }

        // 把"门已破"这一刻的场景持久化项存下来: 之后每回合直接套用, 省掉重新破门的时间.
        private void CacheBrokenSceneData()
        {
            try
            {
                SceneData sceneData = SceneData.instance;
                if (sceneData == null)
                {
                    return;
                }

                _sceneDataJson = UnityEngine.JsonUtility.ToJson(sceneData);
                _log.LogInfo(string.Format("已缓存破门后的场景数据 ({0} 字符), 后续回合不再重复破门", _sceneDataJson.Length));
            }
            catch (Exception exception)
            {
                _log.LogWarning("缓存场景数据失败: " + exception.Message);
            }
        }

        private void CleanupBeforeTransition()
        {
            try
            {
                EventRegister.SendEvent("INVENTORY CANCEL", null);
                EventRegister.SendEvent("HAZARD RESPAWN RESET", null);
            }
            catch (Exception)
            {
            }

            HeroController hero = HeroController.instance;
            if (hero != null)
            {
                try
                {
                    hero.CancelDamageRecoil();
                }
                catch (Exception)
                {
                }
            }

            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager != null && gameManager.isPaused)
            {
                gameManager.isPaused = false;
            }
        }

        private void TickWaitingScene()
        {
            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null)
            {
                Heartbeat("等待场景: GameManager 为空");
                return;
            }

            HeroController hero = HeroController.instance;
            if (hero == null)
            {
                Heartbeat("等待场景: 主角为空");
                return;
            }

            if (gameManager.IsInSceneTransition
                || !gameManager.HasFinishedEnteringScene
                || GameManager.IsWaitingForSceneReady
                || gameManager.GameState != GameState.PLAYING)
            {
                Heartbeat("等待场景: 场景尚未就绪");
                return;
            }

            if (!string.Equals(gameManager.GetSceneNameString(), _entry.SceneName, StringComparison.Ordinal))
            {
                Heartbeat("等待场景: 当前场景不是 " + _entry.SceneName);
                return;
            }

            if (!hero.isHeroInPosition || hero.cState == null || hero.cState.dead || hero.cState.transitioning)
            {
                Heartbeat("等待场景: 主角尚未就位");
                return;
            }

            SetStatus(ResetPhase.WaitingBoss, "场景就绪, 等待 Boss 出现");
        }

        private void TickWaitingBoss()
        {
            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null || gameManager.IsInSceneTransition)
            {
                return;
            }

            _bosses.Resolve(true);
            if (!_bosses.HasBoss)
            {
                _bossWaitFrames++;
                TryStartBattle(false);
                return;
            }

            HealthManager boss = _bosses.Primary;
            if (!_bosses.AnyAlive || boss == null || !boss.gameObject.activeInHierarchy)
            {
                _bossWaitFrames++;
                return;
            }

            string signature = BossTracker.StateSignature(boss);
            if (_initialBossState == null)
            {
                _initialBossState = signature;
            }

            // 第一步: 先把挡门的障碍物打烂 (藤蔓门之类), 否则战斗根本不会触发.
            // 注意扫描必须等新场景加载完再做, 不然扫的是上一局的对象.
            if (!_blockersScanned)
            {
                _blockersScanned = true;
                _breaker.Scan(_blockerPatterns);
                if (_breaker.HasUnbroken && SetSpeed != null)
                {
                    SetSpeed(BlockerSpeed);
                    _log.LogInfo(string.Format("破门阶段加速: 时间倍率 {0}", BlockerSpeed));
                }
            }

            if (!_blockersBroken)
            {
                if (_bossWaitFrames < 10)
                {
                    _bossWaitFrames++;
                    return;
                }

                if (!_breaker.Tick())
                {
                    _bossWaitFrames++;
                    return;
                }

                _blockersBroken = true;
                _breaker.Release();
                CacheBrokenSceneData();
                _log.LogInfo("障碍物处理完成: " + _breaker.Describe());
                _movedIntoArena = false;
                _framesSincArenaMove = 0;
                _bossWaitFrames++;
                return;
            }

            // 第二步: 把主角送进竞技场触发框. 清单里的落点是门口, 停在那里战斗不会触发.
            if (!_movedIntoArena)
            {
                if (_bossWaitFrames < 20)
                {
                    _bossWaitFrames++;
                    return;
                }

                Vector3 position;
                string detail;
                bool resolved = ArenaSpawnResolver.TryResolve(boss, _entry.Position.y, out position, out detail);
                if (!resolved)
                {
                    resolved = ArenaSpawnResolver.TryResolveNearBoss(boss, _entry.Position.y, out position, out detail);
                }

                if (resolved)
                {
                    _movedIntoArena = true;
                    _framesSincArenaMove = 0;

                    HeroController hero = HeroController.instance;
                    if (hero != null)
                    {
                        HeroSpawnGuider.Instance.Disarm();
                        hero.transform.position = position;
                        if (hero.Body != null)
                        {
                            hero.Body.linearVelocity = Vector2.zero;
                        }

                        BlockerBreaker.SnapCamera();
                    }

                    _log.LogInfo("已把主角送进竞技场: " + detail);
                    SendWakeEvent(boss);
                }
                else if (_arenaResolveFrames++ > 60)
                {
                    // 落点一直算不出来: 记一次日志后按原落点继续, 免得整个回合卡死.
                    _movedIntoArena = true;
                    _framesSincArenaMove = 0;
                    _log.LogWarning("找不到可用的竞技场落点, 主角留在原落点: " + detail);
                }

                _bossWaitFrames++;
                return;
            }

            _framesSincArenaMove++;

            // 第三步: 等 Boss 醒过来. 判据是它的 FSM 状态签名发生变化, 超时则按就绪处理并告警.
            bool awake = signature != _initialBossState;
            if (!awake && _framesSincArenaMove < WakeTimeoutFrames)
            {
                // 每 30 帧补一次唤醒事件: Dormant 状态的条件 (主角是否在场地内) 需要几帧才成立.
                if (_framesSincArenaMove % 30 == 0)
                {
                    SendWakeEvent(boss);
                }

                return;
            }

            if (!awake)
            {
                _log.LogWarning(string.Format(
                    "主角进场 {0} 帧后 Boss 状态仍未变化 ({1}), 仍按就绪处理",
                    _framesSincArenaMove,
                    signature));
                Diagnostics.SceneDiagnostics.DumpBossFsm(_log, boss);
            }

            _settleFrames++;
            if (_settleFrames >= Mathf.Max(1, SettleFrames))
            {
                SaveLevelStatePatch.Suppress = false;
                if (DumpSceneOnReset)
                {
                    Diagnostics.SceneDiagnostics.Dump(_log, _bosses);
                }
                Finish(true, string.Format(
                    "回合就绪 ({0} 个 Boss 目标, {1}, {2})",
                    _bosses.Bosses.Count,
                    _usedMinimalPath ? "精简重置" : "完整载入",
                    BattleSceneStarter.Describe()));
            }
        }

        // 把 Boss 叫醒.
        //
        // 苔藓之母的 Control FSM 在 Dormant 状态里的转移是 WAKE->Start Battle, 触发条件是主角
        // 待在场地里 (状态动作里挂着 CheckAlertRangeByName / CheckHeroPerformanceRegion 之类的判定).
        // 所以先把主角摆进场地, 再补发 WAKE; BATTLE START 顺带也发一次, 兼容别的房间.
        private void SendWakeEvent(HealthManager boss)
        {
            if (boss == null)
            {
                return;
            }

            try
            {
                FSMUtility.SendEventToGameObject(boss.gameObject, "WAKE", false);
                FSMUtility.SendEventToGameObject(boss.gameObject, "BATTLE START", false);
            }
            catch (Exception exception)
            {
                _log.LogWarning("补发 WAKE 失败: " + exception.Message);
            }
        }

        private void TryStartBattle(bool force)
        {
            if (_battleStartAttempted && !force)
            {
                return;
            }

            if (!force && _bossWaitFrames <= 30)
            {
                return;
            }

            _battleStartAttempted = true;
            string detail;
            bool started = BattleSceneStarter.TryStart(out detail);
            _log.LogInfo("主动开战: " + detail);
            if (!started)
            {
                _battleStartAttempted = false;
            }
        }

        private void SetStatus(ResetPhase phase, string message)
        {
            bool changed = _phase != phase;
            _phase = phase;
            _status = message;
            if (changed)
            {
                _lastStableLog = Time.realtimeSinceStartup;
                _log.LogInfo("重置阶段: " + phase + " (" + message + ")");
            }

            if (_statusSink != null)
            {
                _statusSink(message);
            }
        }

        // 卡在等待阶段时每 2 秒打一次现场, 方便定位是游戏没就绪还是我们判断错了.
        private void Heartbeat(string tag)
        {
            if (Time.realtimeSinceStartup - _lastStableLog < 2f)
            {
                return;
            }

            _lastStableLog = Time.realtimeSinceStartup;
            _log.LogInfo(tag + " | " + DescribeGameState());
        }

        private void Finish(bool success, string message)
        {
            SaveLevelStatePatch.Suppress = false;
            _phase = success ? ResetPhase.Done : ResetPhase.Failed;
            _status = message;
            _log.Log(success ? LogLevel.Info : LogLevel.Warning, "重置结束: " + message);

            Action<bool, string> callback = _callback;
            _callback = null;
            if (callback != null)
            {
                callback(success, message);
            }
        }
    }
}
