using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using HutongGames.PlayMaker;
using UnityEngine;

namespace RLEnv.Diagnostics
{
    // 场景诊断: 把当前场景里的敌人, 波次战与 Boss 识别结果打进 BepInEx 日志.
    //
    // 训练本身不需要它, 但换 Boss 或者"战斗不触发"这类问题时, 这份输出能直接看出
    // 场景里到底有哪些对象, 谁是 Boss, 落点与 Boss 的实际距离.
    internal static class SceneDiagnostics
    {
        internal static void Dump(ManualLogSource log, Observation.BossTracker bosses)
        {
            if (log == null)
            {
                return;
            }

            StringBuilder builder = new StringBuilder(1024);
            GameManager gameManager = GameManager.UnsafeInstance;
            string sceneName = gameManager != null ? gameManager.GetSceneNameString() : "(未知)";
            builder.Append("===== 场景战斗对象诊断: ").Append(sceneName).Append(" =====");

            HeroController hero = HeroController.instance;
            if (hero != null)
            {
                Vector3 position = hero.transform.position;
                builder.Append("\n主角 位置 (").Append(Format(position.x)).Append(", ").Append(Format(position.y)).Append(')');
            }

            List<HealthManager> enemies = new List<HealthManager>(HealthManager.EnumerateActiveEnemies());
            builder.Append("\n活动 HealthManager 共 ").Append(enemies.Count).Append(" 个:");
            for (int i = 0; i < enemies.Count; i++)
            {
                HealthManager enemy = enemies[i];
                if (enemy == null)
                {
                    continue;
                }

                Vector3 position = enemy.transform.position;
                builder.Append("\n  [层 ").Append(enemy.gameObject.layer).Append(']')
                    .Append(enemy.gameObject.name)
                    .Append("  血量 ").Append(enemy.hp)
                    .Append("  类型 ").Append(enemy.EnemyType)
                    .Append("  死亡 ").Append(enemy.GetIsDead() ? "是" : "否")
                    .Append("  激活 ").Append(enemy.gameObject.activeInHierarchy ? "是" : "否")
                    .Append("  位置 (").Append(Format(position.x)).Append(", ").Append(Format(position.y)).Append(')')
                    .Append("  归并到 ").Append(enemy.SendDamageTo != null ? enemy.SendDamageTo.gameObject.name : "-");
            }

            BattleScene[] scenes = Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            builder.Append("\n波次战 BattleScene 共 ").Append(scenes != null ? scenes.Length : 0).Append(" 个:");
            if (scenes != null)
            {
                for (int i = 0; i < scenes.Length; i++)
                {
                    BattleScene scene = scenes[i];
                    if (scene == null)
                    {
                        continue;
                    }

                    builder.Append("\n  [").Append(i).Append("] ").Append(scene.gameObject.name)
                        .Append("  当前波次 ").Append(scene.currentWave)
                        .Append("  敌人数 ").Append(scene.currentEnemies)
                        .Append("  待清 ").Append(scene.enemiesToNext)
                        .Append("  场景有效 ").Append(scene.gameObject.scene.IsValid() ? "是" : "否");

                    if (scene.waves == null)
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

                        builder.Append("\n      波 ").Append(w).Append(" 子对象 ").Append(wave.transform.childCount).Append(':');
                        for (int c = 0; c < wave.transform.childCount; c++)
                        {
                            Transform child = wave.transform.GetChild(c);
                            if (child == null)
                            {
                                continue;
                            }

                            HealthManager childHealth = child.GetComponent<HealthManager>();
                            builder.Append(' ').Append(child.name)
                                .Append(childHealth != null ? "(有血量)" : "(无血量)")
                                .Append(child.gameObject.activeInHierarchy ? "[激活]" : "[未激活]");
                        }
                    }
                }
            }

            builder.Append("\nBossSceneController: 是 Boss 场景 = ")
                .Append(BossSceneController.IsBossScene ? "是" : "否");
            if (BossSceneController.IsBossScene && BossSceneController.Instance != null)
            {
                HealthManager[] sceneBosses = BossSceneController.Instance.bosses;
                builder.Append("  本体数 ").Append(sceneBosses != null ? sceneBosses.Length : 0);
            }

            if (bosses != null)
            {
                builder.Append("\n识别到的 Boss 目标: ");
                IList<HealthManager> found = bosses.Bosses;
                if (found.Count == 0)
                {
                    builder.Append("(无)");
                }
                else
                {
                    for (int i = 0; i < found.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(", ");
                        }

                        builder.Append(found[i] != null ? found[i].gameObject.name : "null");
                    }

                    builder.Append("  主目标血量上限 ").Append(bosses.PrimaryMaxHealth);
                }
            }

            log.LogInfo(builder.ToString());
            BlockerDump(log);
        }

        private static void BlockerDump(ManualLogSource log)
        {
            try
            {
                ProbeSceneObjects(log);
            }
            catch (System.Exception exception)
            {
                log.LogWarning("打烂物诊断失败: " + exception.Message);
            }
        }

        // 找名字里带 vine / cluster / gate / door 的对象, 与其在门口一带挂了什么组件.
        // 排查"门到底是什么东西"时很有用: 藤蔓门既不是 Breakable, 也可能挂在别的组件族上.
        private static void ProbeSceneObjects(ManualLogSource log)
        {
            // 1) 所有类型名里带 Break 的组件, 逐个列出宿主对象.
            StringBuilder breakBuilder = new StringBuilder(1024);
            breakBuilder.Append("类型名含 Break 的组件:");
            MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            int breakCount = 0;
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || !behaviour.gameObject.scene.IsValid())
                {
                    continue;
                }

                string typeName = behaviour.GetType().Name;
                if (typeName.IndexOf("Break", System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                breakCount++;
                if (breakCount > 40)
                {
                    continue;
                }

                Vector3 position = behaviour.transform.position;
                breakBuilder.Append("\n  ").Append(typeName).Append(" @ ").Append(behaviour.gameObject.name)
                    .Append(behaviour.gameObject.activeInHierarchy ? " [激活]" : " [未激活]")
                    .AppendFormat(" ({0:F1}, {1:F1})", position.x, position.y);
            }

            breakBuilder.Append("\n共 ").Append(breakCount).Append(" 个");
            log.LogInfo(breakBuilder.ToString());

            // 2) 名字里带 vine 的对象, 连同父链一起列出来.
            string[] nameHints = new string[] { "vine" };
            Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
            StringBuilder builder = new StringBuilder(1024);
            builder.Append("名字含 vine 的对象 (含父链):");
            int named = 0;
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform == null || !transform.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (!ContainsAny(transform.gameObject.name, nameHints))
                {
                    continue;
                }

                named++;
                if (named > 25)
                {
                    continue;
                }

                Vector3 position = transform.position;
                builder.Append("\n  ").Append(transform.gameObject.name)
                    .Append(transform.gameObject.activeInHierarchy ? " [激活]" : " [未激活]")
                    .AppendFormat(" ({0:F1}, {1:F1})  自身组件: ", position.x, position.y)
                    .Append(DescribeComponents(transform.gameObject));

                Transform parent = transform.parent;
                int depth = 0;
                while (parent != null && depth < 4)
                {
                    builder.Append("\n      父[").Append(depth).Append("] ").Append(parent.name)
                        .Append(parent.gameObject.activeInHierarchy ? " [激活]" : " [未激活]")
                        .Append(" 组件: ").Append(DescribeComponents(parent.gameObject));
                    parent = parent.parent;
                    depth++;
                }
            }

            builder.Append("\n命中 ").Append(named).Append(" 个");
            log.LogInfo(builder.ToString());

            ProbePersistentItems(log);
        }

        // 存档里记的持久化项 (例如 Tut_03 的 "Moss Vine Cluster") 对应场景里的哪个对象,
        // 只有 PersistentItem.GetId() 能直接看出来, GameObject 名字往往对不上.
        private static void ProbePersistentItems(ManualLogSource log)
        {
            StringBuilder builder = new StringBuilder(1024);
            builder.Append("场景持久化项:");

            PersistentBoolItem[] boolItems = Resources.FindObjectsOfTypeAll<PersistentBoolItem>();
            AppendItems(builder, boolItems);
            PersistentIntItem[] intItems = Resources.FindObjectsOfTypeAll<PersistentIntItem>();
            AppendItems(builder, intItems);

            log.LogInfo(builder.ToString());
        }

        private static void AppendItems<T>(StringBuilder builder, T[] items) where T : MonoBehaviour
        {
            if (items == null)
            {
                return;
            }

            for (int i = 0; i < items.Length; i++)
            {
                T item = items[i];
                if (item == null || !item.gameObject.scene.IsValid())
                {
                    continue;
                }

                string id = "-";
                IPersistentItem persistent = item as IPersistentItem;
                if (persistent != null)
                {
                    try
                    {
                        id = persistent.GetId();
                    }
                    catch (System.Exception)
                    {
                    }
                }

                Vector3 position = item.transform.position;
                builder.Append("\n  [").Append(id).Append("] ").Append(item.gameObject.name)
                    .Append(item.gameObject.activeInHierarchy ? " [激活]" : " [未激活]")
                    .AppendFormat(" ({0:F1}, {1:F1})  组件: ", position.x, position.y)
                    .Append(DescribeComponents(item.gameObject));
            }
        }

        private static bool ContainsAny(string name, string[] hints)
        {
            for (int i = 0; i < hints.Length; i++)
            {
                if (name.IndexOf(hints[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string DescribeComponents(GameObject target)
        {
            Component[] components = target.GetComponents<Component>();
            StringBuilder builder = new StringBuilder(64);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    continue;
                }

                System.Type type = component.GetType();
                string ns = type.Namespace;
                if (!string.IsNullOrEmpty(ns) && ns.StartsWith("UnityEngine", System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append(type.Name);
            }

            return builder.Length > 0 ? builder.ToString() : "(只有 Unity 组件)";
        }

        private static string Format(float value)
        {
            return value.ToString("F2");
        }

        // 把 Boss 身上每个 FSM 的当前状态与它现在能响应的事件打出来, 用来确定"该怎么把它叫醒".
        internal static void DumpBossFsm(ManualLogSource log, HealthManager boss)
        {
            if (log == null || boss == null)
            {
                return;
            }

            StringBuilder builder = new StringBuilder(768);
            builder.Append("Boss FSM 诊断: ").Append(boss.gameObject.name);
            PlayMakerFSM[] fsms = boss.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.Fsm == null)
                {
                    continue;
                }

                builder.Append("\n  FSM ").Append(fsm.FsmName)
                    .Append(" 当前状态 ").Append(fsm.ActiveStateName)
                    .Append(fsm.enabled ? " [启用]" : " [禁用]");

                FsmState state = fsm.Fsm.ActiveState;
                if (state != null && state.Transitions != null)
                {
                    builder.Append("\n    该状态的转移:");
                    for (int t = 0; t < state.Transitions.Length; t++)
                    {
                        builder.Append(' ').Append(state.Transitions[t].EventName)
                            .Append("->").Append(state.Transitions[t].ToState);
                    }
                }

                if (state != null && state.Actions != null)
                {
                    builder.Append("\n    该状态的动作: ");
                    for (int a = 0; a < state.Actions.Length; a++)
                    {
                        if (state.Actions[a] == null)
                        {
                            continue;
                        }

                        if (a > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append(state.Actions[a].GetType().Name);
                    }
                }

                FsmEvent[] events = fsm.Fsm.Events;
                if (events != null && events.Length > 0)
                {
                    builder.Append("\n    FSM 事件表: ");
                    for (int e = 0; e < events.Length; e++)
                    {
                        if (e > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append(events[e] != null ? events[e].Name : "?");
                    }
                }
            }

            log.LogInfo(builder.ToString());
        }
    }
}
