using System;
using HarmonyLib;

namespace RLEnv.TimeControl
{
    // 正式版的 CheatManager.IsCheatsEnabled 硬编码返回 false, 而 TimeManager.UpdateTimeScale
    // 在结果大于 1 时会据此把时间倍率压回 1. 这里只在训练侧主动加速时放行, 其余时候保持原样.
    [HarmonyPatch]
    internal static class CheatManagerPatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(CheatManager), "IsCheatsEnabled");
        }

        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            if (GameSpeedController.SpeedUpRequested)
            {
                __result = true;
            }
        }
    }
}
