using TeamCherry.SharedUtils;

namespace BossRush.Fighting
{
    // 原版 BossRushUI 里对两个 Boss 做了特殊处理, 直接照搬:
    // 这两项在自带存档里的标记会让战斗不触发, 载入前清掉即可.
    internal static class BossFlagTweaks
    {
        private const string DockForemenBoss = "西格尼斯";

        private const string SinnerBoss = "原罪者";

        internal static void Apply(string bossName, PlayerData playerData)
        {
            if (playerData == null || string.IsNullOrEmpty(bossName))
            {
                return;
            }

            if (bossName == DockForemenBoss)
            {
                playerData.SetVariable("defeatedDockForemen", false);
            }
            else if (bossName == SinnerBoss)
            {
                playerData.hasSilkBomb = false;
            }
        }
    }
}
