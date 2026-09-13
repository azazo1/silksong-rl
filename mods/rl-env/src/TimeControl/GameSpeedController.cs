using System;

namespace RLEnv.TimeControl
{
    // 用游戏自带的 TimeManager.TimeControlInstance 做乘性时间缩放.
    //
    // 不直接写 Time.timeScale: 游戏每帧都会用 TimeManager.UpdateTimeScale 重算并覆盖它,
    // 而乘性控制实例是游戏自己的机制, 会被正确计入. 详见 disassembly 的 TimeManager.cs.
    //
    // 注意 TimeManager.UpdateTimeScale 在结果大于 1 时会检查 CheatManager.IsCheatsEnabled,
    // 该属性在正式版里硬编码为 false, 因此加速需要 CheatManagerPatch 配合.
    internal sealed class GameSpeedController : IDisposable
    {
        private TimeManager.TimeControlInstance _instance;

        // 是否正在请求大于 1 的速度, 供 CheatManagerPatch 判断要不要放行.
        internal static bool SpeedUpRequested { get; private set; }

        internal float Speed { get; private set; }

        internal void Ensure()
        {
            if (_instance == null)
            {
                _instance = TimeManager.CreateTimeControl(1f, TimeManager.TimeControlInstance.Type.Multiplicative);
            }
        }

        internal void Apply(float speed)
        {
            Ensure();
            if (speed < 0f)
            {
                speed = 0f;
            }

            Speed = speed;
            SpeedUpRequested = speed > 1f;
            _instance.TimeScale = speed;
        }

        public void Dispose()
        {
            SpeedUpRequested = false;
            if (_instance != null)
            {
                _instance.Release();
                _instance = null;
            }

            Speed = 1f;
        }
    }
}
