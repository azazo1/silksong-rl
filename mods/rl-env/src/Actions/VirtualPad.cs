using System;
using System.Collections.Generic;
using InControl;

namespace RLEnv.Actions
{
    // 虚拟输入面板.
    //
    // 语义与真人握手柄一致: 一个 step 内按键保持按住状态, 想让跳跃/攻击这类边沿动作再次触发,
    // 必须让下一个 step 把按键松开 (游戏用 WasPressed 判定, 一直按住只会触发一次).
    // 这样跳跃的可变高度 (松开跳键会把上升速度砍半) 也保持原样.
    internal static class VirtualPad
    {
        private static readonly Dictionary<PlayerAction, float> Targets = new Dictionary<PlayerAction, float>(8);

        private static HeroActions _actions;

        private static EnvAction _current;

        private static bool _armed;

        private static int _releaseTicksLeft;

        // 只有真正在训练时才注入, 免得污染菜单与过场.
        internal static Func<bool> Gate { get; set; }

        internal static EnvAction Current
        {
            get { return _current; }
        }

        internal static bool IsArmed
        {
            get { return _armed; }
        }

        // 每帧调用: 刷新 HeroActions 引用, 并推进"松开按键"的收尾计时.
        internal static void Tick()
        {
            Refresh();
            if (!_armed)
            {
                return;
            }

            if (_releaseTicksLeft > 0)
            {
                _releaseTicksLeft--;
                if (_releaseTicksLeft == 0)
                {
                    _armed = false;
                }
            }
        }

        internal static void Arm(EnvAction action)
        {
            Refresh();
            _current = action;
            _armed = true;
            _releaseTicksLeft = 0;
            RebuildTargets();
        }

        // 停止注入: 先保持若干 tick 的"全部松开"让游戏看到 WasReleased, 再彻底停手.
        internal static void Release(int releaseTicks)
        {
            if (!_armed)
            {
                return;
            }

            _current = EnvAction.Neutral;
            _releaseTicksLeft = releaseTicks < 1 ? 1 : releaseTicks;
            RebuildTargets();
        }

        // 注入补丁的查询入口: 返回本 tick 该动作要被覆盖成的值.
        internal static bool TryGetTarget(PlayerAction action, out float value)
        {
            value = 0f;
            if (!_armed || action == null || Gate == null || !Gate())
            {
                return false;
            }

            return Targets.TryGetValue(action, out value);
        }

        private static void Refresh()
        {
            GameManager gameManager = GameManager.UnsafeInstance;
            InputHandler inputHandler = gameManager != null ? gameManager.inputHandler : null;
            HeroActions actions = inputHandler != null ? inputHandler.inputActions : null;
            if (actions == _actions)
            {
                return;
            }

            _actions = actions;
            RebuildTargets();
        }

        private static void RebuildTargets()
        {
            Targets.Clear();
            if (_actions == null)
            {
                return;
            }

            Targets[_actions.Left] = _current.Horizontal < 0 ? 1f : 0f;
            Targets[_actions.Right] = _current.Horizontal > 0 ? 1f : 0f;
            Targets[_actions.Up] = _current.Vertical > 0 ? 1f : 0f;
            Targets[_actions.Down] = _current.Vertical < 0 ? 1f : 0f;
            Targets[_actions.Jump] = _current.Jump ? 1f : 0f;
            Targets[_actions.Attack] = _current.Attack ? 1f : 0f;
            Targets[_actions.Cast] = _current.Bind ? 1f : 0f;
        }
    }
}
