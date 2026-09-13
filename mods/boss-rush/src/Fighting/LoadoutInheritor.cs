using System.Collections.Generic;

namespace BossRush.Fighting
{
    // 把当前存档的配装复制进即将载入的 Boss 存档, 练习时用的就是自己的构筑.
    // 能力类标记采用 "有则为真" 的合并方式, 与原版保持一致.
    internal static class LoadoutInheritor
    {
        internal static void Apply(PlayerData source, PlayerData target)
        {
            if (source == null || target == null)
            {
                return;
            }

            target.UnlockedExtraBlueSlot = source.UnlockedExtraBlueSlot;
            target.UnlockedExtraYellowSlot = source.UnlockedExtraYellowSlot;

            target.hasDash = source.hasDash || target.hasDash;
            target.hasWalljump = source.hasWalljump || target.hasWalljump;
            target.hasDoubleJump = source.hasDoubleJump || target.hasDoubleJump;
            target.hasBrolly = source.hasBrolly || target.hasBrolly;
            target.hasQuill = source.hasQuill || target.hasQuill;
            target.hasChargeSlash = source.hasChargeSlash || target.hasChargeSlash;
            target.hasSuperJump = source.hasSuperJump || target.hasSuperJump;
            target.hasSilkSpecial = source.hasSilkSpecial || target.hasSilkSpecial;
            target.silkSpecialLevel = source.silkSpecialLevel;
            target.hasNeedleThrow = source.hasNeedleThrow || target.hasNeedleThrow;
            target.hasThreadSphere = source.hasThreadSphere || target.hasThreadSphere;
            target.hasParry = source.hasParry || target.hasParry;
            target.hasHarpoonDash = source.hasHarpoonDash || target.hasHarpoonDash;
            target.hasSilkCharge = source.hasSilkCharge || target.hasSilkCharge;
            target.hasSilkBomb = source.hasSilkBomb || target.hasSilkBomb;
            target.hasSilkBossNeedle = source.hasSilkBossNeedle || target.hasSilkBossNeedle;
            target.hasNeedolin = source.hasNeedolin || target.hasNeedolin;

            target.maxHealth = source.maxHealth;
            target.maxHealthBase = source.maxHealthBase;
            target.health = source.maxHealth;
            target.silkMax = source.silkMax;
            target.silkRegenMax = source.silkRegenMax;
            target.silk = 0;
            target.nailUpgrades = source.nailUpgrades;

            CopyNamedList(source.Tools, target.Tools);
            CopyNamedList(source.ToolEquips, target.ToolEquips);
            CopyNamedList(source.ExtraToolEquips, target.ExtraToolEquips);
            CopyNamedList(source.ToolLiquids, target.ToolLiquids);

            target.ToolPouchUpgrades = source.ToolPouchUpgrades;
            target.CurrentCrestID = source.CurrentCrestID;

            foreach (KeyValuePair<string, ToolItemsData.Data> pair in source.Tools.Enumerate())
            {
                target.SetToolData(pair.Key, pair.Value);
            }
        }

        private static void CopyNamedList<TData, TContainer>(SerializableNamedList<TData, TContainer> source, SerializableNamedList<TData, TContainer> target)
            where TContainer : SerializableNamedData<TData>, new()
        {
            List<string> names = source.GetValidNames(null);
            for (int i = 0; i < names.Count; i++)
            {
                target.SetData(names[i], source.GetData(names[i]));
            }
        }
    }
}
