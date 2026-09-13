using System;
using System.Reflection;
using GlobalEnums;
using HarmonyLib;
using InControl;

namespace RLEnv.Actions
{
    // 把虚拟输入覆盖到 InControl 的动作状态上.
    //
    // 选点说明: 游戏的输入心跳是 InControlManager.Update -> InputManager.UpdateInternal
    // -> PlayerActionSet.Update -> PlayerAction.Update(每个动作), 两轴动作 MoveVector 在
    // 单轴动作之后由 Left/Right/Up/Down 合成, 因此在这一步之后覆写单轴动作最省事, 也不会
    // 被本帧的硬件采样盖回去. 详见 .tmp/research/01-input.md.
    [HarmonyPatch]
    internal static class PlayerActionUpdatePatch
    {
        private static Action<OneAxisInputControl, float, ulong> _setValue;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(PlayerAction),
                "Update",
                new Type[] { typeof(ulong), typeof(float), typeof(InputDevice) });
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerAction __instance)
        {
            float value;
            if (!VirtualPad.TryGetTarget(__instance, out value))
            {
                return;
            }

            Action<OneAxisInputControl, float, ulong> setValue = ResolveSetValue();
            if (setValue == null)
            {
                return;
            }

            setValue(__instance, value, InputManager.CurrentTick);
            __instance.Commit();
        }

        // OneAxisInputControl.SetValue(float, ulong) 是 internal, 这里转成委托避免每帧反射装箱.
        private static Action<OneAxisInputControl, float, ulong> ResolveSetValue()
        {
            if (_setValue != null)
            {
                return _setValue;
            }

            MethodInfo method = AccessTools.Method(
                typeof(OneAxisInputControl),
                "SetValue",
                new Type[] { typeof(float), typeof(ulong) });
            if (method == null)
            {
                return null;
            }

            try
            {
                _setValue = AccessTools.MethodDelegate<Action<OneAxisInputControl, float, ulong>>(method);
            }
            catch (Exception)
            {
                _setValue = null;
            }

            return _setValue;
        }
    }

    // 门控: 只在正常游玩并且没有打开菜单/背包时注入, 否则输入会被 UI 吃掉.
    internal static class InjectionGate
    {
        internal static bool Ready()
        {
            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null || gameManager.GameState != GameState.PLAYING || gameManager.isPaused)
            {
                return false;
            }

            PlayerData playerData = gameManager.playerData;
            return playerData == null || !playerData.isInventoryOpen;
        }
    }
}
