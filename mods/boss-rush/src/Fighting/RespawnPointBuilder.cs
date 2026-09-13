using GlobalEnums;
using TeamCherry.SharedUtils;
using UnityEngine;

namespace BossRush.Fighting
{
    // 在目标场景里放一个复活点标记, 并把标记注册进 SceneTeleportMap,
    // 这样存档里的 respawnMarkerName 才有落点. 移植自原版 BossRushUI 中的同名逻辑.
    internal static class RespawnPointBuilder
    {
        private const string MarkerPrefix = "BossRushRespawnMarker_";

        internal static string Ensure(string sceneName, string bossName, Vector3 position)
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
                respawnMarker.customWakeUp = false;
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
