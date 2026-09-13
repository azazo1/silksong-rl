using InControl;

namespace RLEnv.Actions
{
    // 把玩家当前真实按下的键读成动作编码.
    //
    // 录制人类示范时用它: 同一帧里游戏读到什么输入, 我们就记什么动作,
    // 这样"观测 -> 动作"样本和训练时的 MDP 完全一致.
    internal static class HumanInputReader
    {
        internal static bool TryRead(out EnvAction action, out int[] indices)
        {
            action = EnvAction.Neutral;
            indices = new int[5];

            GameManager gameManager = GameManager.UnsafeInstance;
            InputHandler inputHandler = gameManager != null ? gameManager.inputHandler : null;
            HeroActions actions = inputHandler != null ? inputHandler.inputActions : null;
            if (actions == null)
            {
                return false;
            }

            int horizontal = actions.Left.IsPressed ? -1 : (actions.Right.IsPressed ? 1 : 0);
            int vertical = actions.Up.IsPressed ? 1 : (actions.Down.IsPressed ? -1 : 0);
            bool jump = actions.Jump.IsPressed;
            bool attack = actions.Attack.IsPressed;
            bool bind = actions.Cast.IsPressed;

            action.Horizontal = horizontal;
            action.Vertical = vertical;
            action.Jump = jump;
            action.Attack = attack;
            action.Bind = bind;

            indices[0] = horizontal + 1;
            indices[1] = 1 - vertical;
            indices[2] = jump ? 1 : 0;
            indices[3] = attack ? 1 : 0;
            indices[4] = bind ? 1 : 0;
            return true;
        }
    }
}
