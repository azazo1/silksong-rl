using System;
using System.Collections.Generic;

// 这些字段由 Newtonsoft 反序列化时填充, 编译器看不到赋值点.
#pragma warning disable 0649

namespace RLEnv.Episode
{
    // 与 BossScenes/BossSceneConfig.json 对应的数据模型.
    [Serializable]
    internal sealed class BossSceneConfig
    {
        public List<BossSceneConfigItem> BossScenes;
    }

    [Serializable]
    internal sealed class BossSceneConfigItem
    {
        public string SceneName;

        public float PosX;

        public float PosY;

        public float PosZ;

        public string BossName;
    }
}
