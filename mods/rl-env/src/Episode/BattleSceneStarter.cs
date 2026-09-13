using System.Collections.Generic;
using UnityEngine;

namespace RLEnv.Episode
{
    // 波次战 (BattleScene) 的辅助: 判断战斗有没有真正开打, 以及主动开战.
    //
    // 正常玩法是主角走进竞技场触发框, 但训练时主角是被直接摆进去的, 靠物理触发不一定可靠,
    // 所以主角就位后直接调 StartBattle(). 里面的 started 判重保证不会重复开打.
    internal static class BattleSceneStarter
    {
        internal static bool TryStart(out string detail)
        {
            detail = null;
            BattleScene[] scenes = FindScenes();
            int started = 0;
            for (int i = 0; i < scenes.Length; i++)
            {
                BattleScene scene = scenes[i];
                if (scene == null || scene.waves == null || scene.waves.Count == 0)
                {
                    continue;
                }

                scene.StartBattle();
                started++;
            }

            if (started == 0)
            {
                detail = "场景里没有可用的波次战";
                return false;
            }

            detail = "已触发 " + started + " 个波次战";
            return true;
        }

        // 波次战开打后 BattleScene.currentEnemies 会被置成敌人数量, 因此它大于 0 就说明战斗已经启动.
        // 场景里没有波次战时返回 true (例如 BossSceneController 式的 Boss 战不靠这个判断).
        internal static bool AnyEngaged()
        {
            BattleScene[] scenes = FindScenes();
            if (scenes.Length == 0)
            {
                return true;
            }

            bool hasWaveScene = false;
            for (int i = 0; i < scenes.Length; i++)
            {
                BattleScene scene = scenes[i];
                if (scene == null || scene.waves == null || scene.waves.Count == 0)
                {
                    continue;
                }

                hasWaveScene = true;
                if (scene.currentEnemies > 0)
                {
                    return true;
                }
            }

            return !hasWaveScene;
        }

        internal static string Describe()
        {
            BattleScene[] scenes = FindScenes();
            if (scenes.Length == 0)
            {
                return "无波次战";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(64);
            for (int i = 0; i < scenes.Length; i++)
            {
                BattleScene scene = scenes[i];
                if (scene == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("波次 ").Append(scene.currentWave)
                    .Append(" 敌人 ").Append(scene.currentEnemies)
                    .Append(" 待清 ").Append(scene.enemiesToNext);
            }

            return builder.ToString();
        }

        private static BattleScene[] FindScenes()
        {
            BattleScene[] scenes = Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (scenes == null)
            {
                return new BattleScene[0];
            }

            List<BattleScene> active = new List<BattleScene>(scenes.Length);
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i] != null && scenes[i].gameObject.scene.IsValid())
                {
                    active.Add(scenes[i]);
                }
            }

            return active.ToArray();
        }
    }
}
