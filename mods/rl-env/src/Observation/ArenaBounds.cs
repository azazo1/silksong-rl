using UnityEngine;

namespace RLEnv.Observation
{
    // 一个回合内固定不变的战场矩形.
    //
    // 回合开始时抓一次就不再变: 相机在战斗中会跟着主角动, 用实时视野做归一化会让观测非平稳.
    // 优先用 Boss 战的相机锁框 (CameraController.CurrentLockArea), 没有就退化为当时的视野矩形.
    internal struct ArenaBounds
    {
        internal float CenterX;

        internal float CenterY;

        internal float HalfWidth;

        internal float HalfHeight;

        internal bool HasLock;

        internal static ArenaBounds Capture()
        {
            ArenaBounds bounds;
            bounds.CenterX = 0f;
            bounds.CenterY = 0f;
            bounds.HalfWidth = 14.6f;
            bounds.HalfHeight = 8.3f;
            bounds.HasLock = false;

            CameraInfoCache.UpdateCache();
            float cameraHalfWidth = CameraInfoCache.HalfWidth > 0.01f ? CameraInfoCache.HalfWidth : 14.6f;
            float cameraHalfHeight = CameraInfoCache.HalfHeight > 0.01f ? CameraInfoCache.HalfHeight : 8.3f;

            GameCameras gameCameras = GameCameras.SilentInstance;
            CameraController controller = gameCameras != null ? gameCameras.cameraController : null;
            CameraLockArea lockArea = controller != null ? controller.CurrentLockArea : null;
            if (lockArea != null)
            {
                float minX;
                float minY;
                float maxX;
                float maxY;
                float lookMin;
                float lookMax;
                lockArea.GetWorldBounds(out minX, out minY, out maxX, out maxY, out lookMin, out lookMax);
                if (maxX > minX && maxY > minY)
                {
                    bounds.CenterX = (minX + maxX) * 0.5f;
                    bounds.CenterY = (minY + maxY) * 0.5f;
                    bounds.HalfWidth = (maxX - minX) * 0.5f + cameraHalfWidth;
                    bounds.HalfHeight = (maxY - minY) * 0.5f + cameraHalfHeight;
                    bounds.HasLock = true;
                    return bounds;
                }
            }

            bounds.CenterX = CameraInfoCache.PosX;
            bounds.CenterY = CameraInfoCache.PosY;
            bounds.HalfWidth = cameraHalfWidth;
            bounds.HalfHeight = cameraHalfHeight;
            return bounds;
        }

        internal float NormalizeX(float worldX)
        {
            return (worldX - CenterX) / HalfWidth;
        }

        internal float NormalizeY(float worldY)
        {
            return (worldY - CenterY) / HalfHeight;
        }
    }
}
