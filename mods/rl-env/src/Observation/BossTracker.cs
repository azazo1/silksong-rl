using System.Collections.Generic;
using UnityEngine;

namespace RLEnv.Observation
{
    // 找到本场 Boss 战的"本体"敌人.
    //
    // 两级来源:
    //   1) BossSceneController.bosses: 官方 Boss 战 (例如 Boss 复战房) 的权威列表;
    //   2) BattleScene.waves 下挂 HealthManager 的子对象: 教程关这种波次战 (苔藓之母就在这里).
    // 找不到就退化为"当前场景里血量上限最高的敌人", 并把它记进日志方便排查.
    internal sealed class BossTracker
    {
        private readonly List<HealthManager> _bosses = new List<HealthManager>();

        private readonly Dictionary<HealthManager, int> _maxHealth = new Dictionary<HealthManager, int>();

        internal IList<HealthManager> Bosses
        {
            get { return _bosses; }
        }

        internal HealthManager Primary { get; private set; }

        internal int PrimaryMaxHealth { get; private set; }

        internal bool HasBoss
        {
            get { return Primary != null; }
        }

        internal bool AnyAlive
        {
            get
            {
                for (int i = 0; i < _bosses.Count; i++)
                {
                    HealthManager manager = _bosses[i];
                    if (manager != null && !manager.GetIsDead() && manager.hp > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        internal bool Contains(HealthManager manager)
        {
            for (int i = 0; i < _bosses.Count; i++)
            {
                if (_bosses[i] == manager)
                {
                    return true;
                }
            }

            return false;
        }

        internal int BossCount
        {
            get { return _bosses.Count; }
        }

        // 某个敌人的血量上限估计: 记录过的用记录值, 第一次见到就把当前血量当上限.
        internal bool TryGetMaxHealth(HealthManager manager, out int maxHealth)
        {
            maxHealth = 0;
            if (manager == null)
            {
                return false;
            }

            int existing;
            if (_maxHealth.TryGetValue(manager, out existing) && existing > 0)
            {
                maxHealth = existing;
                return true;
            }

            maxHealth = Mathf.Max(1, manager.hp);
            _maxHealth[manager] = maxHealth;
            return true;
        }

        // 所有 Boss 本体的血量总和 / 上限总和, 供多阶段 Boss 用.
        internal float TotalHealthRatio
        {
            get
            {
                float current = 0f;
                float total = 0f;
                for (int i = 0; i < _bosses.Count; i++)
                {
                    HealthManager manager = _bosses[i];
                    if (manager == null)
                    {
                        continue;
                    }

                    int max;
                    if (!_maxHealth.TryGetValue(manager, out max) || max <= 0)
                    {
                        continue;
                    }

                    current += Mathf.Max(0, manager.hp);
                    total += max;
                }

                return total > 0f ? current / total : 0f;
            }
        }

        // 回合开始时调用: 重新解析并记录初始血量 (当作血量上限).
        internal void Resolve(bool force)
        {
            if (!force && Primary != null && (Primary.GetIsDead() || Primary.hp <= 0))
            {
                // Boss 已经死了, 保持原引用直到本回合结束, 免得观测里忽然丢掉目标.
                return;
            }

            List<HealthManager> found = new List<HealthManager>(4);

            if (BossSceneController.IsBossScene && BossSceneController.Instance != null)
            {
                HealthManager[] sceneBosses = BossSceneController.Instance.bosses;
                if (sceneBosses != null)
                {
                    for (int i = 0; i < sceneBosses.Length; i++)
                    {
                        if (sceneBosses[i] != null)
                        {
                            found.Add(sceneBosses[i]);
                        }
                    }
                }
            }

            if (found.Count == 0)
            {
                CollectFromBattleScenes(found);
            }

            if (found.Count == 0)
            {
                CollectLargestEnemy(found);
            }

            _bosses.Clear();
            _bosses.AddRange(found);
            Primary = null;
            PrimaryMaxHealth = 0;

            for (int i = 0; i < _bosses.Count; i++)
            {
                HealthManager manager = _bosses[i];
                int maxHealth;
                if (!_maxHealth.TryGetValue(manager, out maxHealth) || maxHealth < manager.hp)
                {
                    maxHealth = manager.hp;
                    _maxHealth[manager] = maxHealth;
                }

                if (Primary == null || maxHealth > PrimaryMaxHealth)
                {
                    Primary = manager;
                    PrimaryMaxHealth = maxHealth;
                }
            }
        }

        // 某次命中实际扣血的目标: 多部件 Boss 会把伤害归并到 sendDamageTo 上.
        internal static HealthManager ResolveDamageTarget(HealthManager manager)
        {
            if (manager == null)
            {
                return null;
            }

            HealthManager target = manager.SendDamageTo;
            return target != null ? target : manager;
        }

        // 把一个 Boss 当前所有 FSM 的状态拼成签名, 用来判断"它有没有醒过来".
        internal static string StateSignature(HealthManager manager)
        {
            if (manager == null)
            {
                return string.Empty;
            }

            PlayMakerFSM[] fsms = manager.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null || fsms.Length == 0)
            {
                return string.Empty;
            }

            System.Array.Sort(fsms, delegate(PlayMakerFSM a, PlayMakerFSM b)
            {
                return string.CompareOrdinal(a.FsmName, b.FsmName);
            });

            System.Text.StringBuilder builder = new System.Text.StringBuilder(64);
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] == null || !fsms[i].enabled)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('|');
                }

                builder.Append(fsms[i].FsmName).Append('=').Append(fsms[i].ActiveStateName);
            }

            return builder.ToString();
        }

        private static void CollectFromBattleScenes(List<HealthManager> found)
        {
            BattleScene[] scenes = Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < scenes.Length; i++)
            {
                BattleScene scene = scenes[i];
                if (scene == null || scene.waves == null)
                {
                    continue;
                }

                for (int w = 0; w < scene.waves.Count; w++)
                {
                    BattleWave wave = scene.waves[w];
                    if (wave == null)
                    {
                        continue;
                    }

                    HealthManager[] managers = wave.GetComponentsInChildren<HealthManager>(true);
                    for (int m = 0; m < managers.Length; m++)
                    {
                        if (managers[m] != null && !found.Contains(managers[m]))
                        {
                            found.Add(managers[m]);
                        }
                    }
                }
            }
        }

        private static void CollectLargestEnemy(List<HealthManager> found)
        {
            HealthManager best = null;
            // 注册表在遍历中可能因死亡被改写, 先拷一份.
            List<HealthManager> active = new List<HealthManager>(HealthManager.EnumerateActiveEnemies());
            for (int i = 0; i < active.Count; i++)
            {
                HealthManager manager = active[i];
                if (manager == null || manager.hp <= 0)
                {
                    continue;
                }

                if (best == null || manager.hp > best.hp)
                {
                    best = manager;
                }
            }

            if (best != null)
            {
                found.Add(best);
            }
        }
    }
}
