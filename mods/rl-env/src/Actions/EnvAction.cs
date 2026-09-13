namespace RLEnv.Actions
{
    // 一次 RL 决策对应的输入意图, 与 Python 侧的动作编码一一对应.
    //
    // 动作空间: MultiDiscrete([3, 3, 2, 2, 2])
    //   Horizontal: 0 = 不动, 1 = 左, 2 = 右
    //   Vertical:   0 = 不动, 1 = 上, 2 = 下
    //   Jump:       0 = 松开, 1 = 按住
    //   Attack:     0 = 松开, 1 = 按住
    //   Bind:       0 = 松开, 1 = 按住 (缚丝回血, 对应游戏的 Cast 键)
    //
    // 攻击方向由 Vertical 与主角朝向共同决定 (上劈 / 前劈 / 下劈), 与游戏输入层一致.
    internal struct EnvAction
    {
        internal int Horizontal;

        internal int Vertical;

        internal bool Jump;

        internal bool Attack;

        internal bool Bind;

        internal static EnvAction FromIndices(int horizontal, int vertical, int jump, int attack, int bind)
        {
            EnvAction action;
            action.Horizontal = horizontal - 1;
            action.Vertical = 1 - vertical;
            action.Jump = jump != 0;
            action.Attack = attack != 0;
            action.Bind = bind != 0;
            return action;
        }

        internal static EnvAction Neutral
        {
            get
            {
                EnvAction action;
                action.Horizontal = 0;
                action.Vertical = 0;
                action.Jump = false;
                action.Attack = false;
                action.Bind = false;
                return action;
            }
        }

        public override string ToString()
        {
            string horizontal = Horizontal < 0 ? "左" : (Horizontal > 0 ? "右" : "-");
            string vertical = Vertical < 0 ? "下" : (Vertical > 0 ? "上" : "-");
            return string.Format(
                "移动 {0}/{1}{2}{3}{4}",
                horizontal,
                vertical,
                Jump ? " 跳" : string.Empty,
                Attack ? " 攻击" : string.Empty,
                Bind ? " 缚丝" : string.Empty);
        }
    }
}
