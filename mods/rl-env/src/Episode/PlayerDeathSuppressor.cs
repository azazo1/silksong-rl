using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RLEnv.Episode
{
    // 训练时直接拦掉游戏自己的死亡流程.
    //
    // 原版流程是: 主角死亡 -> GameManager.PlayerDead 协程 -> 等 4 秒 -> 写盘 -> 预载场景 -> 复活切场景.
    // 这套流程和我们自己的回合重置会互相打架 (两边同时发起场景切换, 结果卡在载入界面), 而且每次死亡
    // 白等好几秒. 因此在训练期间把 PlayerDead / PlayerDeadFromHazard 整个跳过:
    // 主角就地躺下, 不写盘, 不预载, 不切场景, 由我们紧接着的场景重载把一切恢复成干净状态.
    //
    // 只在连接了训练侧时才生效, 平时玩不受影响.
    [HarmonyPatch]
    internal static class PlayerDeathSuppressor
    {
        internal static bool Active { get; set; }

        internal static float LastSuppressedAt { get; private set; } = -1000f;

        internal static bool RecentlySuppressed(float seconds)
        {
            return Time.realtimeSinceStartup - LastSuppressedAt <= seconds;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            List<MethodBase> targets = new List<MethodBase>(2);
            MethodInfo normal = AccessTools.Method(typeof(GameManager), "PlayerDead", new Type[] { typeof(float) });
            if (normal != null)
            {
                targets.Add(normal);
            }

            MethodInfo hazard = AccessTools.Method(typeof(GameManager), "PlayerDeadFromHazard", new Type[] { typeof(float) });
            if (hazard != null)
            {
                targets.Add(hazard);
            }

            return targets;
        }

        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!Active)
            {
                return true;
            }

            LastSuppressedAt = Time.realtimeSinceStartup;
            return false;
        }
    }
}
