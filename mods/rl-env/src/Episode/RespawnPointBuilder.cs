using GlobalEnums;
using TeamCherry.SharedUtils;
using UnityEngine;

namespace RLEnv.Episode
{
    // 在目标场景里放一个复活点标记并注册进 SceneTeleportMap, 让存档里的 respawnMarkerName 有落点.
    // 逻辑与 mods/boss-rush 的同名类一致.
    internal static class RespawnPointBuilder
    {
        private const string MarkerPrefix = "RLEnvRespawnMarker_";

        internal static string Ensure(string sceneName, string bossName, Vector3 position, bool skipWakeUpAnimation)
        {
            string markerName = MarkerPrefix + bossName;
            GameObject marker = GameObject.Find(markerName);
            bool created = marker == null;

            if (created)
            {
                marker = new GameObject(markerName);
                Object.DontDestroyOnLoad(marker);
            }

            marker.transform.position = position;
            marker.transform.rotation = Quaternion.identity;
            marker.transform.localScale = Vector3.one;

            if (created)
            {
                RespawnMarker respawnMarker = marker.AddComponent<RespawnMarker>();
                respawnMarker.respawnFacingRight = true;
                // customWakeUp = true 会让复活协程跳过整段 Wake Up Ground 动画, 明显省时间,
                // 但入场表现会变, 因此默认关闭, 由配置决定要不要用.
                respawnMarker.customWakeUp = skipWakeUpAnimation;
                respawnMarker.customFadeDuration = new OverrideFloat
                {
                    IsEnabled = false,
                    Value = 0f
                };
                respawnMarker.overrideMapZone = new OverrideMapZone
                {
                    IsEnabled = false,
                    Value = MapZone.NONE
                };

                SceneTeleportMap.AddRespawnPoint(sceneName, markerName);
            }

            return markerName;
        }
    }
}
