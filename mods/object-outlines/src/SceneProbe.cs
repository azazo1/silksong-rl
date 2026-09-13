using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace SilksongRL.ObjectOutlines
{
    // 排查用的探针: 把玩家周围的对象组件, 以及全场景里未被分类对象挂的组件统计写进日志.
    // 有些目标 (例如某些可打烂的门) 既不是敌人也不是我已知的可破坏物类型, 靠这个可以直接看出它挂了什么.
    internal static class SceneProbe
    {
        public static void Dump(ManualLogSource logger, OutlineScanner scanner, float radius)
        {
            if (logger == null)
            {
                return;
            }

            logger.LogInfo("===== 对象描边探针: 开始 =====");

            HeroController hero = FindHero();
            Vector3 center = hero != null ? hero.transform.position : Vector3.zero;
            logger.LogInfo(string.Format("玩家位置 {0} (半径 {1})", center, radius));

            Collider2D[] nearby = Physics2D.OverlapCircleAll(new Vector2(center.x, center.y), radius);
            HashSet<GameObject> seen = new HashSet<GameObject>();
            StringBuilder builder = new StringBuilder(256);

            for (int i = 0; i < nearby.Length; i++)
            {
                Collider2D collider = nearby[i];
                if (collider == null)
                {
                    continue;
                }

                GameObject target = collider.gameObject;
                if (!seen.Add(target))
                {
                    continue;
                }

                float distance = Vector3.Distance(center, target.transform.position);
                logger.LogInfo(string.Format(
                    "[附近 {0,5:F1}] {1} ({2})  组件: {3}",
                    distance,
                    BuildPath(target.transform),
                    collider.GetType().Name,
                    DescribeComponents(target, builder)));
            }

            Dictionary<string, int> counts = new Dictionary<string, int>();
            int examined = scanner.CollectUnclassifiedHistogram(counts, 4000);
            logger.LogInfo(string.Format("未分类对象统计: 检查了 {0} 个带碰撞体但没被描边的对象", examined));

            List<KeyValuePair<string, int>> ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            int limit = ordered.Count < 40 ? ordered.Count : 40;
            for (int i = 0; i < limit; i++)
            {
                logger.LogInfo(string.Format("  未分类组件 {0} x{1}", ordered[i].Key, ordered[i].Value));
            }

            logger.LogInfo("===== 对象描边探针: 结束 =====");
        }

        private static HeroController FindHero()
        {
            HeroController[] heroes = UnityEngine.Object.FindObjectsByType<HeroController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            return heroes.Length > 0 ? heroes[0] : null;
        }

        private static string DescribeComponents(GameObject target, StringBuilder builder)
        {
            builder.Length = 0;

            Component[] components = target.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                {
                    continue;
                }

                System.Type type = component.GetType();
                string ns = type.Namespace;
                if (!string.IsNullOrEmpty(ns) && ns.StartsWith("UnityEngine", System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(type.Name);
            }

            return builder.Length > 0 ? builder.ToString() : "(只有 Unity 组件)";
        }

        private static string BuildPath(Transform target)
        {
            StringBuilder builder = new StringBuilder(64);
            Transform current = target;
            int guard = 0;

            while (current != null && guard < 6)
            {
                if (builder.Length > 0)
                {
                    builder.Insert(0, '/');
                }

                builder.Insert(0, current.name);
                current = current.parent;
                guard++;
            }

            return builder.ToString();
        }
    }
}
