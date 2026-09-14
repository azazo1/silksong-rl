namespace RLEnv.Transport
{
    // 与 Python 训练侧的通信协议.
    //
    // 帧结构: [int32 payloadLength][payload], payload 首部是 [int32 messageType], 其余为消息体.
    // 字节序为小端 (.NET BinaryWriter 默认), Python 侧用 struct 的 "<" 前缀对齐.
    internal static class Protocol
    {
        internal const int Version = 1;

        // "RLEN", 用来在连错端口时快速失败.
        internal const int Magic = 0x524C454E;

        // 单帧上限, 防止异常长度把内存吃爆.
        internal const int MaxPayloadBytes = 1 << 20;

        internal enum MessageType
        {
            // Python -> mod
            Reset = 1,
            Step = 2,
            Close = 3,
            Ping = 4,
            SetSpeed = 5,

            // 进入/退出人类示范录制模式 (body 是 int32: 1 开, 0 关)
            SetHumanMode = 6,

            // 本回合的回放片段: 1 = 把缓冲里的画面落盘 (击杀), 0 = 丢掉 (没击杀)
            SaveClip = 7,

            // mod -> Python
            Hello = 101,
            Observation = 102,
            Status = 103,
            Error = 104,
            StateMap = 105,

            // 人类示范样本: [int32 stepIndex][float32 x N][int32 x 5 动作]
            Record = 106
        }

        // 观测消息里的标志位.
        internal const int FlagTerminated = 1;

        internal const int FlagTruncated = 2;

        internal const int FlagEpisodeStart = 4;

        internal enum SessionState
        {
            Booting = 0,
            WaitingForClient = 1,
            Idle = 2,
            Resetting = 3,
            Stepping = 4,
            EpisodeReady = 5,
            Closed = 6,
            Error = 7
        }
    }
}
