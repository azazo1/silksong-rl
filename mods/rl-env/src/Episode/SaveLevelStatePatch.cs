using HarmonyLib;

namespace RLEnv.Episode
{
    // 重置窗口内屏蔽 GameManager.SaveLevelState().
    //
    // 场景切换一开始, BeginSceneTransitionRoutine 就会调 SaveLevelState(), 把"当前(已经打完的)场景"
    // 的持久化项写进 SceneData. 如果此时 SceneData 已经换成了干净的副本, 这一步会把 Boss 的
    // death 标记重新写脏, 于是新场景里 Boss 会被 SetActive(false) 而战斗不触发.
    // 只在重置窗口内屏蔽, 就绪后立刻恢复.
    [HarmonyPatch(typeof(GameManager), "SaveLevelState")]
    internal static class SaveLevelStatePatch
    {
        internal static bool Suppress { get; set; }

        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !Suppress;
        }
    }
}
