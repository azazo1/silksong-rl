using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
using HarmonyLib;

namespace RLEnv.TimeControl
{
    // 受击 / 击杀 / 钉撞都会插入 0.01 到 1.15 秒的顿帧, 把帧的语义打乱.
    // 训练时直接跳过这些顿帧, 让"一个 step 推进 N 个物理帧"是确定的.
    [HarmonyPatch]
    internal static class FreezeMomentPatch
    {
        internal static bool Suppress { get; set; }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            List<MethodBase> targets = new List<MethodBase>(2);
            MethodInfo single = AccessTools.Method(typeof(GameManager), "FreezeMoment", new Type[] { typeof(int) });
            if (single != null)
            {
                targets.Add(single);
            }

            MethodInfo typed = AccessTools.Method(
                typeof(GameManager),
                "FreezeMoment",
                new Type[] { typeof(FreezeMomentTypes), typeof(Action) });
            if (typed != null)
            {
                targets.Add(typed);
            }

            return targets;
        }

        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !Suppress;
        }
    }
}
