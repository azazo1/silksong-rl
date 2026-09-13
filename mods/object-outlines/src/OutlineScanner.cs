using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SilksongRL.ObjectOutlines
{
    // 扫描场景并把描边对象分类.
    // 低频扫描只决定"描哪些对象", 几何顶点每帧按对象的当前变换重算, 这样边框能跟住移动的对象.
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
        private static readonly Renderer[] EmptyRenderers = new Renderer[0];

        private sealed class OutlineTarget
        {
            public OutlineCategory Category;
            public Collider2D[] Colliders;
            public Renderer[] Renderers;
        }

        private readonly Dictionary<OutlineCategory, List<Vector3>> _vertices = new Dictionary<OutlineCategory, List<Vector3>>();
        private readonly Dictionary<OutlineCategory, int> _counts = new Dictionary<OutlineCategory, int>();
        private readonly List<OutlineTarget> _targets = new List<OutlineTarget>(1024);
        private readonly HashSet<GameObject> _candidates = new HashSet<GameObject>();
        private readonly List<Collider2D> _colliderBuffer = new List<Collider2D>(64);
        private readonly List<Renderer> _rendererBuffer = new List<Renderer>(16);
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

        // 低频调用: 决定这一轮描哪些对象, 并把它们的碰撞体缓存下来.
        public void Scan(int maxObjectsPerCategory)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            _targets.Clear();
            for (int i = 0; i < AllCategories.Length; i++)
            {
                _counts[AllCategories[i]] = 0;
            }

            CollectCandidates();

            foreach (GameObject candidate in _candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                OutlineCategory category;
                if (!TryClassify(candidate, out category))
                {
                    continue;
                }

                if (_counts[category] >= maxObjectsPerCategory)
                {
                    continue;
                }

                _counts[category] = _counts[category] + 1;
                _targets.Add(CreateTarget(candidate, category));
            }

            watch.Stop();
            LastScanMilliseconds = watch.ElapsedMilliseconds;
        }

        // 每帧调用: 按对象当前变换生成线段顶点.
        public void BuildGeometry(ICollection<OutlineCategory> enabledCategories, bool rendererFallback)
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

                AddTargetGeometry(target, _vertices[target.Category], rendererFallback);
            }

            TotalVertices = 0;
            for (int i = 0; i < AllCategories.Length; i++)
            {
                TotalVertices += _vertices[AllCategories[i]].Count;
            }

            watch.Stop();
            LastGeometryMilliseconds = watch.ElapsedMilliseconds;
        }

        private OutlineTarget CreateTarget(GameObject candidate, OutlineCategory category)
        {
            OutlineTarget target = new OutlineTarget();
            target.Category = category;

            _colliderBuffer.Clear();
            candidate.GetComponentsInChildren<Collider2D>(false, _colliderBuffer);
            target.Colliders = _colliderBuffer.Count > 0 ? _colliderBuffer.ToArray() : EmptyColliders;

            if (target.Colliders.Length > 0)
            {
                target.Renderers = EmptyRenderers;
            }
            else
            {
                _rendererBuffer.Clear();
                candidate.GetComponentsInChildren<Renderer>(false, _rendererBuffer);
                target.Renderers = _rendererBuffer.Count > 0 ? _rendererBuffer.ToArray() : EmptyRenderers;
            }

            return target;
        }

        private static void AddTargetGeometry(OutlineTarget target, List<Vector3> vertices, bool rendererFallback)
        {
            bool hasCollider = false;

            Collider2D[] colliders = target.Colliders;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider2D collider = colliders[i];
                if (collider == null || !collider.enabled)
                {
                    continue;
                }

                ColliderOutlineBuilder.AddCollider(collider, vertices);
                hasCollider = true;
            }

            if (hasCollider || !rendererFallback)
            {
                return;
            }

            Renderer[] renderers = target.Renderers;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                ColliderOutlineBuilder.AddBounds(renderer.bounds, vertices);
            }
        }

        private void CollectCandidates()
        {
            _candidates.Clear();

            int heroes = AddAll(UnityEngine.Object.FindObjectsByType<HeroController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            int healths = AddAll(UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            int interactables = AddAll(UnityEngine.Object.FindObjectsByType<InteractableBase>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            int pickups = AddAll(UnityEngine.Object.FindObjectsByType<CollectableItemPickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            int damageHeroes = AddAll(UnityEngine.Object.FindObjectsByType<DamageHero>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            int hazards = AddAll(UnityEngine.Object.FindObjectsByType<HazardRespawnTrigger>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));

            CandidateSummary = string.Format(
                "候选 hero={0} health={1} interact={2} pickup={3} damageHero={4} hazard={5}",
                heroes, healths, interactables, pickups, damageHeroes, hazards);
        }

        private int AddAll<T>(T[] components) where T : Component
        {
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component != null)
                {
                    _candidates.Add(component.gameObject);
                }
            }

            return components.Length;
        }

        // 判定对象所属类别; 不属于任何类别的返回 false.
        // 挂在敌人子节点上的伤害判定会被所属实体覆盖, 因此这里直接跳过.
        private static bool TryClassify(GameObject target, out OutlineCategory category)
        {
            category = OutlineCategory.Player;

            if (target.GetComponent<HeroController>() != null)
            {
                category = OutlineCategory.Player;
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

            if (target.GetComponent<DamageHero>() != null || target.GetComponent<HazardRespawnTrigger>() != null)
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
