using HarmonyLib;

namespace BossRush.Hooks
{
    // 游戏本身只承认 0 到 4 号槽位 (Platform.IsSaveSlotIndexValid),
    // 而 Boss 存档固定放在 5 号槽位, 因此这里只给 5 号放行, 其余判断保持原样.
    [HarmonyPatch(typeof(Platform), "IsSaveSlotIndexValid")]
    internal static class SaveSlotIndexPatch
    {
        internal const int BossSlotIndex = 5;

        private static void Postfix(int slotIndex, ref bool __result)
        {
            if (slotIndex == BossSlotIndex)
            {
                __result = true;
            }
        }
    }
}
