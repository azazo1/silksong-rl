using System.Collections.Generic;
using UnityEngine;

namespace RLEnv.Observation
{
    // 危险框扫描: 场景里所有 DamageHero (敌人攻击判定, 陷阱, 掉落物, 投射物) 的缓存列表.
    //
    // 全量扫描很贵, 所以按秒节流: 每帧只做距离计算, 场景对象很少变.
    internal sealed class HazardScanner
    {
        private const float RescanIntervalSeconds = 1.5f;

        private readonly List<DamageHero> _hazards = new List<DamageHero>(32);

        private float _lastScanAt = -1000f;

        internal IList<DamageHero> Hazards
        {
            get { return _hazards; }
        }

        internal void EnsureFresh(bool force)
        {
            if (!force && Time.realtimeSinceStartup - _lastScanAt < RescanIntervalSeconds)
            {
                return;
            }

            Rescan();
        }

        internal void Rescan()
        {
            _lastScanAt = Time.realtimeSinceStartup;
            _hazards.Clear();

            DamageHero[] found = Object.FindObjectsByType<DamageHero>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (found == null)
            {
                return;
            }

            for (int i = 0; i < found.Length; i++)
            {
                DamageHero hazard = found[i];
                if (hazard == null || !hazard.gameObject.scene.IsValid())
                {
                    continue;
                }

                _hazards.Add(hazard);
            }
        }
    }
}
