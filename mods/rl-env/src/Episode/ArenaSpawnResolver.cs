using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RLEnv.Episode
{
    // 解析"真正能触发战斗"的落点.
    //
    // BossRush 清单里的落点是 Boss 房门口, 有些房间门口还隔着可打烂的藤蔓门 (苔藓之母就是),
    // 直接把主角摆在那里会离 Boss 很远, 战斗永远不触发. 这里改成:
    // 找波次战的触发框 (BattleScene 上的碰撞体), 把主角摆进框内接近 Boss 水平位置的地方.
    //
    // 注意 BattleScene 上可能同时挂着 BoxCollider2D 与 PolygonCollider2D, 而且战斗开打后
    // 或战斗已完成时它们会被禁用, 禁用的 Collider2D 读出来的 bounds 是全 0 的退化值,
    // 所以必须逐个筛"启用且尺寸非零"的那一个.
    internal static class ArenaSpawnResolver
    {
        internal static bool TryResolve(HealthManager boss, float fallbackY, out Vector3 position, out string detail)
        {
            position = Vector3.zero;
            detail = null;

            BattleScene[] scenes = Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (scenes == null || scenes.Length == 0)
            {
                detail = "场景里没有波次战";
                return false;
            }

            for (int i = 0; i < scenes.Length; i++)
            {
                BattleScene scene = scenes[i];
                if (scene == null || !scene.gameObject.scene.IsValid())
                {
                    continue;
                }

                Collider2D collider = PickUsableCollider(scene.gameObject, out string colliderDetail);
                if (collider == null)
                {
                    detail = "波次战上没有可用的触发框 (" + colliderDetail + ")";
                    continue;
                }

                Bounds bounds = collider.bounds;
                float targetX = boss != null ? boss.transform.position.x : bounds.center.x;
                const float Margin = 1f;
                float x = Mathf.Clamp(targetX, bounds.min.x + Margin, bounds.max.x - Margin);
                float y = Mathf.Max(fallbackY, bounds.min.y + Margin);

                position = new Vector3(x, y, 0f);
                detail = string.Format(
                    "触发框 {0} x[{1:F1}, {2:F1}] y[{3:F1}, {4:F1}], 其它碰撞体: {5} -> 落点 ({6:F2}, {7:F2})",
                    collider.GetType().Name,
                    bounds.min.x,
                    bounds.max.x,
                    bounds.min.y,
                    bounds.max.y,
                    colliderDetail,
                    x,
                    y);
                return true;
            }

            return false;
        }

        // 竞技场触发框不可用 (例如被 FSM 锁着) 时的退路: 直接站到 Boss 身前.
        // 只求把主角放进场地, 之后由插件补发 WAKE 事件唤醒 Boss.
        internal static bool TryResolveNearBoss(HealthManager boss, float fallbackY, out Vector3 position, out string detail)
        {
            position = Vector3.zero;
            if (boss == null)
            {
                detail = "没有 Boss 目标";
                return false;
            }

            Vector3 bossPosition = boss.transform.position;
            float side = 1f;
            HeroController hero = HeroController.instance;
            if (hero != null && hero.transform.position.x < bossPosition.x)
            {
                side = -1f;
            }

            float x = bossPosition.x + side * 4f;
            float y = fallbackY;
            position = new Vector3(x, y, 0f);
            detail = string.Format("触发框不可用, 退化为站到 Boss 身前 ({0:F1}, {1:F1})", x, y);
            return true;
        }

        private static Collider2D PickUsableCollider(GameObject host, out string detail)        {
            Collider2D[] colliders = host.GetComponents<Collider2D>();
            Collider2D picked = null;
            StringBuilder builder = new StringBuilder(128);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider2D collider = colliders[i];
                if (collider == null)
                {
                    continue;
                }

                Bounds bounds = collider.bounds;
                bool usable = collider.enabled && bounds.size.x > 0.01f && bounds.size.y > 0.01f;
                if (builder.Length > 0)
                {
                    builder.Append(';');
                }

                builder.Append(collider.GetType().Name)
                    .Append(collider.enabled ? "[启用]" : "[禁用]")
                    .Append(collider.isTrigger ? "[触发]" : "[实体]")
                    .AppendFormat(" 尺寸({0:F1}x{1:F1})", bounds.size.x, bounds.size.y);

                if (usable && picked == null)
                {
                    picked = collider;
                }
            }

            detail = builder.Length > 0 ? builder.ToString() : "无碰撞体";
            return picked;
        }
    }
}
