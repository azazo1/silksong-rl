using System;
using System.Reflection;
using HarmonyLib;

namespace RLEnv.Observation
{
    // 战斗事件统计: 本回合内玩家对 Boss 造成的伤害 / 自己挨的伤害 / Boss 死亡 / 自己死亡.
    //
    // 伤害来源用 patch `HealthManager.Hit` 的 hp 前后差, 而不是 HitInstance.DamageDealt,
    // 因为后续还有伤害缩放与免疫判定. 玩家受伤不 patch TakeDamage (内部提前 return 太多),
    // 而是订阅 HeroController.OnTakenDamage.
    internal sealed class CombatTracker : IDisposable
    {
        private readonly BossTracker _bosses;

        private HeroController _hero;

        private bool _bossAliveLastPoll;

        internal CombatTracker(BossTracker bosses)
        {
            _bosses = bosses;
        }

        internal static CombatTracker Current { get; private set; }

        internal float DamageDealtStep { get; private set; }

        internal float DamageTakenStep { get; private set; }

        internal bool BossKilledStep { get; private set; }

        internal bool PlayerDiedStep { get; private set; }

        internal float TotalDamageDealt { get; private set; }

        internal void Attach()
        {
            Current = this;
        }

        internal void BeginStep()
        {
            DamageDealtStep = 0f;
            DamageTakenStep = 0f;
            BossKilledStep = false;
            PlayerDiedStep = false;
            _bossAliveLastPoll = _bosses.AnyAlive;
        }

        internal void ResetTotals()
        {
            TotalDamageDealt = 0f;
        }

        // 每帧调用: HeroController 会在换场景时换实例, 事件订阅要跟着走.
        internal void Refresh()
        {
            HeroController hero = HeroController.instance;
            if (hero != _hero)
            {
                DetachHero();
                _hero = hero;
                if (_hero != null)
                {
                    _hero.OnTakenDamage += HandlePlayerDamaged;
                    _hero.OnDeath += HandlePlayerDied;
                    // 尖刺/酸/岩浆这类 hazard 死亡不走 Die, 必须单独订.
                    _hero.OnHazardDeath += HandlePlayerDied;
                }
            }

            if (!BossKilledStep && _bossAliveLastPoll && !_bosses.AnyAlive)
            {
                BossKilledStep = true;
            }

            _bossAliveLastPoll = _bosses.AnyAlive;

            if (!PlayerDiedStep && _hero != null && _hero.cState != null && _hero.cState.dead)
            {
                PlayerDiedStep = true;
            }
        }

        public void Dispose()
        {
            DetachHero();
            if (Current == this)
            {
                Current = null;
            }
        }

        internal void ReportHeroDamageTo(HealthManager target, int damage)
        {
            if (damage <= 0)
            {
                return;
            }

            if (!_bosses.Contains(target) && !_bosses.Contains(HealthManagerPatch.ResolveSource(target)))
            {
                return;
            }

            DamageDealtStep += damage;
            TotalDamageDealt += damage;
        }

        private void HandlePlayerDamaged()
        {
            DamageTakenStep += 1f;
        }

        private void HandlePlayerDied()
        {
            PlayerDiedStep = true;
        }

        private void DetachHero()
        {
            if (_hero == null)
            {
                return;
            }

            _hero.OnTakenDamage -= HandlePlayerDamaged;
            _hero.OnDeath -= HandlePlayerDied;
            _hero.OnHazardDeath -= HandlePlayerDied;
            _hero = null;
        }
    }

    // HealthManager.Hit(HitInstance) 是玩家伤害的必经之路, 用 hp 前后差取真实伤害.
    [HarmonyPatch]
    internal static class HealthManagerPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(HealthManager), "Hit", new Type[] { typeof(HitInstance) });
        }

        [HarmonyPrefix]
        private static void Prefix(HealthManager __instance, HitInstance hitInstance, out int __state)
        {
            __state = 0;
            if (!hitInstance.IsHeroDamage)
            {
                return;
            }

            HealthManager target = BossTracker.ResolveDamageTarget(__instance);
            __state = target != null ? target.hp : 0;
        }

        [HarmonyPostfix]
        private static void Postfix(HealthManager __instance, HitInstance hitInstance, int __state)
        {
            CombatTracker tracker = CombatTracker.Current;
            if (tracker == null || !hitInstance.IsHeroDamage)
            {
                return;
            }

            HealthManager target = BossTracker.ResolveDamageTarget(__instance);
            if (target == null)
            {
                return;
            }

            tracker.ReportHeroDamageTo(target, __state - target.hp);
        }

        // 命中子部件时, 真实扣血对象是它的归并目标.
        internal static HealthManager ResolveSource(HealthManager manager)
        {
            return BossTracker.ResolveDamageTarget(manager);
        }
    }
}
