using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SilksongRL.ObjectOutlines
{
    // 扫描场景并把描边对象分类.
    // 扫描每轮只做一次全场景遍历 (拿全部 Collider2D, 再从碰撞体往上找它属于哪个标记对象),
    // 否则每种标记组件各来一次 FindObjectsByType 会把帧时间拖出肉眼可见的卡顿.
    // 几何顶点每帧按对象的当前变换重算, 所以边框能跟住移动的对象.
    internal sealed class OutlineScanner
    {
        private static readonly OutlineCategory[] AllCategories =
        {
            OutlineCategory.Player,
            OutlineCategory.Enemy,
            OutlineCategory.Hazard,
            OutlineCategory.Interactable,
            OutlineCategory.Breakable,
        };

        private static readonly Collider2D[] EmptyColliders = new Collider2D[0];

        private static Type[] _breakableTypes;

        private sealed class Group
        {
            public OutlineCategory Category;
            public readonly List<Collider2D> Colliders = new List<Collider2D>(4);
        }

        private sealed class OutlineTarget
        {
            public OutlineCategory Category;
            public GameObject GameObject;
            public Collider2D[] Colliders;
        }

        private readonly Dictionary<OutlineCategory, List<Vector3>> _vertices = new Dictionary<OutlineCategory, List<Vector3>>();
        private readonly Dictionary<OutlineCategory, int> _counts = new Dictionary<OutlineCategory, int>();
        private readonly List<OutlineTarget> _targets = new List<OutlineTarget>(1024);
        private readonly Dictionary<GameObject, Group> _groups = new Dictionary<GameObject, Group>(1024);
        private readonly Dictionary<Transform, GameObject> _ownerLookup = new Dictionary<Transform, GameObject>(4096);
        private readonly Dictionary<GameObject, OutlineCategory> _ownerCategories = new Dictionary<GameObject, OutlineCategory>(1024);
        private readonly HashSet<GameObject> _classified = new HashSet<GameObject>();
        private readonly StringBuilder _builder = new StringBuilder(256);

        public long LastScanMilliseconds { get; private set; }

        public long LastGeometryMilliseconds { get; private set; }

        public int TotalVertices { get; private set; }

        public string CandidateSummary { get; private set; }

        public OutlineScanner()
        {
            for (int i = 0; i < AllCategories.Length; i++)
            {
                _vertices[AllCategories[i]] = new List<Vector3>(8192);
                _counts[AllCategories[i]] = 0;
            }

            CandidateSummary = string.Empty;
        }

        // 可破坏物 (藤蔓, 可破坏墙, 罐子之类) 各挂各的组件, 没有共同基类,
        // 所以按类名前缀把这一族都找出来, 运行时用 GetComponent(Type) 判断.
        private static Type[] BreakableTypes
        {
            get
            {
                if (_breakableTypes == null)
                {
                    _breakableTypes = ResolveBreakableTypes();
                }

                return _breakableTypes;
            }
        }

        public List<Vector3> Vertices(OutlineCategory category)
        {
            return _vertices[category];
        }

        public int Count(OutlineCategory category)
        {
            return _counts[category];
        }

        public string DescribeCounts()
        {
            _builder.Length = 0;
            for (int i = 0; i < AllCategories.Length; i++)
            {
                OutlineCategory category = AllCategories[i];
                _builder.Append(category).Append('=').Append(_counts[category]);
                if (i < AllCategories.Length - 1)
                {
                    _builder.Append("  ");
                }
            }
            return _builder.ToString();
        }

        // 低频调用: 决定这一轮描哪些对象.
        public void Scan(int maxObjectsPerCategory)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            _targets.Clear();
            _groups.Clear();
            _ownerLookup.Clear();
            _ownerCategories.Clear();
            _classified.Clear();
            for (int i = 0; i < AllCategories.Length; i++)
            {
                _counts[AllCategories[i]] = 0;
            }

            Collider2D[] colliders = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            int unmatched = 0;

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider2D collider = colliders[i];
                if (collider == null)
                {
                    continue;
                }

                GameObject owner;
                OutlineCategory category;
                if (!ResolveOwner(collider.transform, out owner, out category))
                {
                    unmatched++;
                    continue;
                }

                Group group;
                if (!_groups.TryGetValue(owner, out group))
                {
                    if (_counts[category] >= maxObjectsPerCategory)
                    {
                        continue;
                    }

                    group = new Group();
                    group.Category = category;
                    _groups[owner] = group;
                    _classified.Add(owner);
                    _counts[category] = _counts[category] + 1;
                }

                group.Colliders.Add(collider);
            }

            foreach (KeyValuePair<GameObject, Group> pair in _groups)
            {
                OutlineTarget target = new OutlineTarget();
                target.GameObject = pair.Key;
                target.Category = pair.Value.Category;
                target.Colliders = pair.Value.Colliders.Count > 0 ? pair.Value.Colliders.ToArray() : EmptyColliders;
                _targets.Add(target);
            }

            CandidateSummary = string.Format(
                "扫描: 碰撞体={0} 已分类对象={1} 未匹配碰撞体={2}",
                colliders.Length, _groups.Count, unmatched);

            watch.Stop();
            LastScanMilliseconds = watch.ElapsedMilliseconds;
        }

        // 每帧调用: 按对象当前变换生成线段顶点.
        public void BuildGeometry(ICollection<OutlineCategory> enabledCategories)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < AllCategories.Length; i++)
            {
                _vertices[AllCategories[i]].Clear();
            }

            for (int i = 0; i < _targets.Count; i++)
            {
                OutlineTarget target = _targets[i];
                if (!enabledCategories.Contains(target.Category))
                {
                    continue;
                }

                AddTargetGeometry(target, _vertices[target.Category]);
            }

            TotalVertices = 0;
            for (int i = 0; i < AllCategories.Length; i++)
            {
                TotalVertices += _vertices[AllCategories[i]].Count;
            }

            watch.Stop();
            LastGeometryMilliseconds = watch.ElapsedMilliseconds;
        }

        // 排查用: 统计场景里"带碰撞体但没被描边"的对象都挂了哪些游戏脚本.
        public int CollectUnclassifiedHistogram(Dictionary<string, int> counts, int maxObjects)
        {
            int examined = 0;

            Collider2D[] colliders = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            HashSet<GameObject> seen = new HashSet<GameObject>();

            for (int i = 0; i < colliders.Length && examined < maxObjects; i++)
            {
                Collider2D collider = colliders[i];
                if (collider == null)
                {
                    continue;
                }

                GameObject target = collider.gameObject;
                if (_classified.Contains(target) || !seen.Add(target))
                {
                    continue;
                }

                examined++;

                Component[] components = target.GetComponents<Component>();
                for (int c = 0; c < components.Length; c++)
                {
                    Component component = components[c];
                    if (component == null)
                    {
                        continue;
                    }

                    Type type = component.GetType();
                    string ns = type.Namespace;
                    if (!string.IsNullOrEmpty(ns) && ns.StartsWith("UnityEngine", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int value;
                    counts.TryGetValue(type.Name, out value);
                    counts[type.Name] = value + 1;
                }
            }

            return examined;
        }

        private static Type[] ResolveBreakableTypes()
        {
            List<Type> found = new List<Type>();

            try
            {
                Assembly assembly = typeof(HeroController).Assembly;
                foreach (Type type in assembly.GetTypes())
                {
                    if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    string name = type.Name;
                    if (name.StartsWith("Breakable", StringComparison.Ordinal)
                        || name.StartsWith("Destructible", StringComparison.Ordinal))
                    {
                        found.Add(type);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ObjectOutlines] 解析可破坏物类型失败: " + exception.Message);
            }

            return found.ToArray();
        }

        // 从碰撞体往上找它属于哪个标记对象; 同一个 Transform 的结果在一轮扫描内复用.
        private bool ResolveOwner(Transform start, out GameObject owner, out OutlineCategory category)
        {
            GameObject cached;
            if (_ownerLookup.TryGetValue(start, out cached))
            {
                owner = cached;
                if (owner == null)
                {
                    category = OutlineCategory.Player;
                    return false;
                }

                category = _ownerCategories[owner];
                return true;
            }

            Transform current = start;
            int guard = 0;

            while (current != null && guard < 8)
            {
                OutlineCategory found;
                if (TryClassify(current.gameObject, out found))
                {
                    _ownerLookup[start] = current.gameObject;
                    _ownerCategories[current.gameObject] = found;
                    owner = current.gameObject;
                    category = found;
                    return true;
                }

                current = current.parent;
                guard++;
            }

            _ownerLookup[start] = null;
            owner = null;
            category = OutlineCategory.Player;
            return false;
        }

        private static void AddTargetGeometry(OutlineTarget target, List<Vector3> vertices)
        {
            Collider2D[] colliders = target.Colliders;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider2D collider = colliders[i];
                if (collider == null || !collider.enabled)
                {
                    continue;
                }

                ColliderOutlineBuilder.AddCollider(collider, vertices);
            }
        }

        // 判定对象所属类别; 不属于任何类别的返回 false.
        private static bool TryClassify(GameObject target, out OutlineCategory category)
        {
            category = OutlineCategory.Player;

            if (target.GetComponent<HeroController>() != null)
            {
                category = OutlineCategory.Player;
                return true;
            }

            Type[] breakableTypes = BreakableTypes;
            for (int i = 0; i < breakableTypes.Length; i++)
            {
                Component component = target.GetComponent(breakableTypes[i]);
                if (component == null)
                {
                    continue;
                }

                // 背景层的可破坏物副本会在 Start 里把自己禁用掉, 这些不算可打烂的目标.
                Behaviour behaviour = component as Behaviour;
                if (behaviour != null && !behaviour.enabled)
                {
                    continue;
                }

                category = OutlineCategory.Breakable;
                return true;
            }

            // 有些可打烂的东西 (例如苔藓区的藤蔓门) 身上没有任何 Breakable 组件,
            // 只挂一个把攻击转发给 PlayMaker 状态机的代理, 这类也算可破坏物.
            if (target.GetComponent<ReceivedDamageProxy>() != null)
            {
                category = OutlineCategory.Breakable;
                return true;
            }

            if (target.GetComponent<InteractableBase>() != null || target.GetComponent<CollectableItemPickup>() != null)
            {
                category = OutlineCategory.Interactable;
                return true;
            }

            HealthManager health = target.GetComponent<HealthManager>();
            if (health != null)
            {
                category = IsEnemy(health) ? OutlineCategory.Enemy : OutlineCategory.Breakable;
                return true;
            }

            if (target.GetComponent<DamageHero>() != null
                || target.GetComponent<HazardRespawnTrigger>() != null
                || target.GetComponent<KillOnContact>() != null)
            {
                if (HasHealthManagerAncestor(target.transform))
                {
                    return false;
                }

                category = OutlineCategory.Hazard;
                return true;
            }

            return false;
        }

        private static bool IsEnemy(HealthManager health)
        {
            HealthManager.EnemyTypes type = health.EnemyType;
            return type == HealthManager.EnemyTypes.Regular
                || type == HealthManager.EnemyTypes.Shade
                || type == HealthManager.EnemyTypes.Armoured;
        }

        private static bool HasHealthManagerAncestor(Transform target)
        {
            Transform current = target.parent;
            while (current != null)
            {
                if (current.GetComponent<HealthManager>() != null)
                {
                    return true;
                }
                current = current.parent;
            }
            return false;
        }
    }
}
