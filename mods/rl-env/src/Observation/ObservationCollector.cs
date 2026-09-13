using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RLEnv.Observation
{
    // 每步把游戏状态写进一个定长 float 数组, 顺序与 ObservationSchema 一致.
    //
    // 内容分四段: 固定段 (主角 / Boss / 场地 / 时间与事件), 最近若干小怪, 最近若干危险框,
    // Boss 各 FSM 的状态 id. 列表段按"离主角最近"排序, 空位填 0 并把 valid 置 0.
    internal sealed class ObservationCollector
    {
        private readonly BossTracker _bosses;

        private readonly CombatTracker _combat;

        private readonly HazardScanner _hazards = new HazardScanner();

        private readonly float[] _buffer = new float[ObservationSchema.FieldCount];

        private readonly List<PlayMakerFSM> _bossFsms = new List<PlayMakerFSM>(4);

        private readonly List<HealthManager> _enemyCandidates = new List<HealthManager>(32);

        private readonly List<EnemyCandidate> _enemySlots = new List<EnemyCandidate>(32);

        private readonly List<HazardCandidate> _hazardSlots = new List<HazardCandidate>(64);

        private HealthManager _fsmOwner;

        internal ObservationCollector(BossTracker bosses, CombatTracker combat)
        {
            _bosses = bosses;
            _combat = combat;
        }

        internal float[] Buffer
        {
            get { return _buffer; }
        }

        internal StateIdRegistry States { get; private set; } = new StateIdRegistry();

        internal ArenaBounds Arena { get; private set; }

        internal void CaptureArena()
        {
            Arena = ArenaBounds.Capture();
        }

        internal void RescanHazards()
        {
            _hazards.Rescan();
        }

        internal void Collect(int episodeStep)
        {
            for (int i = 0; i < _buffer.Length; i++)
            {
                _buffer[i] = 0f;
            }

            HeroController hero = HeroController.instance;
            PlayerData playerData = PlayerData.instance;

            CollectPlayer(hero, playerData);
            CollectBoss();
            CollectArena();

            _buffer[(int)ObsField.PhysicsFrame] = CustomPlayerLoop.FixedUpdateCycle;
            _buffer[(int)ObsField.EpisodeStep] = episodeStep;
            _buffer[(int)ObsField.DamageDealtStep] = _combat != null ? _combat.DamageDealtStep : 0f;
            _buffer[(int)ObsField.DamageTakenStep] = _combat != null ? _combat.DamageTakenStep : 0f;
            _buffer[(int)ObsField.BossKilledStep] = _combat != null && _combat.BossKilledStep ? 1f : 0f;
            _buffer[(int)ObsField.PlayerDiedStep] = _combat != null && _combat.PlayerDiedStep ? 1f : 0f;

            CollectEnemies();
            CollectHazards(hero);
        }

        private void CollectPlayer(HeroController hero, PlayerData playerData)
        {
            Vector2 heroCenter = Vector2.zero;
            bool hasHero = hero != null;

            if (hasHero)
            {
                Vector3 position = hero.transform.position;
                Vector2 velocity = hero.Body != null ? hero.Body.linearVelocity : Vector2.zero;
                HeroControllerStates cState = hero.cState;
                Bounds bounds = hero.Bounds;

                heroCenter = new Vector2(position.x, position.y);

                _buffer[(int)ObsField.PlayerPosXWorld] = position.x;
                _buffer[(int)ObsField.PlayerPosYWorld] = position.y;
                _buffer[(int)ObsField.PlayerPosXN] = Arena.NormalizeX(position.x);
                _buffer[(int)ObsField.PlayerPosYN] = Arena.NormalizeY(position.y);
                _buffer[(int)ObsField.PlayerVelX] = velocity.x;
                _buffer[(int)ObsField.PlayerVelY] = velocity.y;
                _buffer[(int)ObsField.PlayerHalfWidth] = bounds.extents.x;
                _buffer[(int)ObsField.PlayerHalfHeight] = bounds.extents.y;

                if (cState != null)
                {
                    _buffer[(int)ObsField.PlayerFacing] = cState.facingRight ? 1f : -1f;
                    _buffer[(int)ObsField.PlayerOnGround] = cState.onGround ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerWasOnGround] = cState.wasOnGround ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerJumping] = cState.jumping ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerDoubleJumping] = cState.doubleJumping ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerFalling] = cState.falling ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerDashing] = cState.dashing ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerAirDashing] = cState.airDashing ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerWallSliding] = cState.wallSliding ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerWallClinging] = cState.wallClinging ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerTouchingWall] = cState.touchingWall ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerLookingUp] = cState.lookingUp ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerLookingDown] = cState.lookingDown ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerAttacking] = cState.attacking ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerUpAttacking] = cState.upAttacking ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerDownAttacking] = cState.downAttacking ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerNailCharging] = cState.nailCharging ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerRecoiling] = (cState.recoiling || cState.recoilingLeft || cState.recoilingRight) ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerInvulnerable] = cState.Invulnerable ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerBinding] = (cState.isBinding || cState.focusing) ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerDead] = cState.dead ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerHazardDeath] = (cState.hazardDeath || cState.hazardRespawning) ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerTransitioning] = cState.transitioning ? 1f : 0f;
                }

                _buffer[(int)ObsField.PlayerAcceptingInput] = hero.acceptingInput ? 1f : 0f;
                _buffer[(int)ObsField.PlayerControlRelinquished] = hero.controlReqlinquished ? 1f : 0f;
                _buffer[(int)ObsField.PlayerHeroState] = (float)(int)hero.hero_state;
            }

            if (playerData != null)
            {
                int maxHealth = Mathf.Max(1, playerData.CurrentMaxHealth);
                int silkMax = Mathf.Max(1, playerData.CurrentSilkMax);
                _buffer[(int)ObsField.PlayerHealth] = playerData.health;
                _buffer[(int)ObsField.PlayerHealthMax] = maxHealth;
                _buffer[(int)ObsField.PlayerHealthRatio] = playerData.health / (float)maxHealth;
                _buffer[(int)ObsField.PlayerSilk] = playerData.silk;
                _buffer[(int)ObsField.PlayerSilkMax] = silkMax;
                _buffer[(int)ObsField.PlayerSilkRatio] = playerData.silk / (float)silkMax;
            }

            _heroCenter = heroCenter;
            _hasHero = hasHero;
        }

        private void CollectBoss()
        {
            HealthManager boss = _bosses.Primary;
            if (boss == null)
            {
                _buffer[(int)ObsField.BossStateId] = -1f;
                _buffer[(int)ObsField.BossHealthTotalRatio] = 0f;
                _buffer[(int)ObsField.BossCount] = 0f;
                return;
            }

            Vector3 bossPosition = boss.transform.position;
            Vector2 bossVelocity = Vector2.zero;
            Rigidbody2D body = boss.GetComponent<Rigidbody2D>();
            if (body != null)
            {
                bossVelocity = body.linearVelocity;
            }

            bool alive = !boss.GetIsDead() && boss.hp > 0;
            int maxHealth = _bosses.PrimaryMaxHealth;

            _buffer[(int)ObsField.BossAlive] = alive ? 1f : 0f;
            _buffer[(int)ObsField.BossPosXWorld] = bossPosition.x;
            _buffer[(int)ObsField.BossPosYWorld] = bossPosition.y;
            _buffer[(int)ObsField.BossRelXN] = Arena.NormalizeX(bossPosition.x) - _buffer[(int)ObsField.PlayerPosXN];
            _buffer[(int)ObsField.BossRelYN] = Arena.NormalizeY(bossPosition.y) - _buffer[(int)ObsField.PlayerPosYN];
            _buffer[(int)ObsField.BossVelX] = bossVelocity.x;
            _buffer[(int)ObsField.BossVelY] = bossVelocity.y;
            _buffer[(int)ObsField.BossFacing] = boss.transform.localScale.x >= 0f ? 1f : -1f;
            _buffer[(int)ObsField.BossHealth] = boss.hp;
            _buffer[(int)ObsField.BossHealthMax] = maxHealth;
            _buffer[(int)ObsField.BossHealthRatio] = maxHealth > 0 ? boss.hp / (float)maxHealth : 0f;
            _buffer[(int)ObsField.BossHealthTotalRatio] = _bosses.TotalHealthRatio;
            _buffer[(int)ObsField.BossCount] = _bosses.BossCount;
            _buffer[(int)ObsField.BossInvincible] = (boss.IsInvincible || boss.CheckInvincible()) ? 1f : 0f;

            float dx = _buffer[(int)ObsField.BossRelXN];
            float dy = _buffer[(int)ObsField.BossRelYN];
            _buffer[(int)ObsField.BossDistanceN] = Mathf.Sqrt(dx * dx + dy * dy);

            Bounds bounds;
            if (TryGetBounds(boss.gameObject, out bounds))
            {
                _buffer[(int)ObsField.BossHalfWidth] = bounds.extents.x;
                _buffer[(int)ObsField.BossHalfHeight] = bounds.extents.y;
            }

            CollectBossFsms(boss);
        }

        private void CollectBossFsms(HealthManager boss)
        {
            if (_fsmOwner != boss)
            {
                _fsmOwner = boss;
                _bossFsms.Clear();
                PlayMakerFSM[] fsms = boss.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    if (fsms[i] != null && fsms[i].enabled)
                    {
                        _bossFsms.Add(fsms[i]);
                    }
                }

                _bossFsms.Sort(delegate(PlayMakerFSM a, PlayMakerFSM b)
                {
                    return string.CompareOrdinal(a.FsmName, b.FsmName);
                });
            }

            if (_bossFsms.Count == 0)
            {
                _buffer[(int)ObsField.BossStateId] = -1f;
                return;
            }

            StringBuilder builder = new StringBuilder(64);
            for (int i = 0; i < _bossFsms.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('|');
                }

                builder.Append(_bossFsms[i].FsmName).Append('=').Append(_bossFsms[i].ActiveStateName);
            }

            _buffer[(int)ObsField.BossStateId] = States.GetId(builder.ToString());

            int limit = _bossFsms.Count < ObservationSchema.MaxBossFsms ? _bossFsms.Count : ObservationSchema.MaxBossFsms;
            for (int slot = 0; slot < limit; slot++)
            {
                PlayMakerFSM fsm = _bossFsms[slot];
                string key = fsm.FsmName + "=" + fsm.ActiveStateName;
                _buffer[ObservationSchema.BossFsmField(slot, BossFsmObsField.Valid)] = 1f;
                _buffer[ObservationSchema.BossFsmField(slot, BossFsmObsField.StateId)] = States.GetId(key);
            }
        }

        private void CollectArena()
        {
            _buffer[(int)ObsField.ArenaCenterX] = Arena.CenterX;
            _buffer[(int)ObsField.ArenaCenterY] = Arena.CenterY;
            _buffer[(int)ObsField.ArenaHalfWidth] = Arena.HalfWidth;
            _buffer[(int)ObsField.ArenaHalfHeight] = Arena.HalfHeight;
        }

        // 最近的若干小怪 / 召唤物 (不含 Boss 本体, 也不含没有敌意层级的东西).
        private void CollectEnemies()
        {
            _enemyCandidates.Clear();
            _enemySlots.Clear();

            List<HealthManager> active = new List<HealthManager>(HealthManager.EnumerateActiveEnemies());
            for (int i = 0; i < active.Count; i++)
            {
                HealthManager manager = active[i];
                if (manager == null || _bosses.Contains(manager))
                {
                    continue;
                }

                if (manager.gameObject.layer != EnemyLayer)
                {
                    continue;
                }

                if (manager.GetIsDead() || manager.hp <= 0 || !manager.gameObject.activeInHierarchy)
                {
                    continue;
                }

                float distance = DistanceSquared(manager.transform.position);
                EnemyCandidate candidate;
                candidate.Manager = manager;
                candidate.DistanceSquared = distance;
                _enemySlots.Add(candidate);
            }

            _enemySlots.Sort(CompareEnemy);
            _buffer[(int)ObsField.EnemyCount] = _enemySlots.Count;

            int limit = _enemySlots.Count < ObservationSchema.MaxEnemies ? _enemySlots.Count : ObservationSchema.MaxEnemies;
            for (int slot = 0; slot < limit; slot++)
            {
                HealthManager manager = _enemySlots[slot].Manager;
                Vector3 position = manager.transform.position;

                _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.Valid)] = 1f;
                _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.RelXN)] = Arena.NormalizeX(position.x) - _buffer[(int)ObsField.PlayerPosXN];
                _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.RelYN)] = Arena.NormalizeY(position.y) - _buffer[(int)ObsField.PlayerPosYN];
                _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.Health)] = manager.hp;
                _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.Facing)] = manager.transform.localScale.x >= 0f ? 1f : -1f;
                _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.EnemyType)] = (float)(int)manager.EnemyType;

                Rigidbody2D body = manager.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.VelX)] = body.linearVelocity.x;
                    _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.VelY)] = body.linearVelocity.y;
                }

                Bounds bounds;
                if (TryGetBounds(manager.gameObject, out bounds))
                {
                    _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.HalfWidth)] = bounds.extents.x;
                    _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.HalfHeight)] = bounds.extents.y;
                }

                int maxHealth;
                if (_bosses.TryGetMaxHealth(manager, out maxHealth) && maxHealth > 0)
                {
                    _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.HealthRatio)] = manager.hp / (float)maxHealth;
                }
                else
                {
                    // 小怪没记录过上限时, 用当前血量当上限, 至少保证首次出现时比例是 1.
                    _buffer[ObservationSchema.EnemyField(slot, EnemyObsField.HealthRatio)] = 1f;
                }
            }
        }

        // 最近的若干危险框: 敌人攻击判定, 陷阱, 掉落物, 投射物都挂 DamageHero.
        private void CollectHazards(HeroController hero)
        {
            _hazards.EnsureFresh(false);
            _hazardSlots.Clear();

            IList<DamageHero> hazards = _hazards.Hazards;
            for (int i = 0; i < hazards.Count; i++)
            {
                DamageHero hazard = hazards[i];
                if (hazard == null)
                {
                    continue;
                }

                Collider2D collider = hazard.GetComponent<Collider2D>();
                if (collider == null)
                {
                    continue;
                }

                Bounds bounds = collider.bounds;
                if (bounds.size.x <= 0.001f && bounds.size.y <= 0.001f)
                {
                    continue;
                }

                float distance = DistanceSquared(bounds.center);
                // 只保留主角附近的, 免得远处没关系的判定体把名额占满.
                if (distance > HazardKeepRadiusSquared)
                {
                    continue;
                }

                HazardCandidate candidate;
                candidate.Hazard = hazard;
                candidate.Collider = collider;
                candidate.DistanceSquared = distance;
                _hazardSlots.Add(candidate);
            }

            _hazardSlots.Sort(CompareHazard);
            _buffer[(int)ObsField.HazardCount] = _hazardSlots.Count;

            int limit = _hazardSlots.Count < ObservationSchema.MaxHazards ? _hazardSlots.Count : ObservationSchema.MaxHazards;
            for (int slot = 0; slot < limit; slot++)
            {
                HazardCandidate candidate = _hazardSlots[slot];
                Bounds bounds = candidate.Collider.bounds;
                bool enabled = candidate.Collider.enabled && candidate.Hazard.enabled;

                _buffer[ObservationSchema.HazardField(slot, HazardObsField.Valid)] = 1f;
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.Enabled)] = enabled ? 1f : 0f;
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.RelXN)] = Arena.NormalizeX(bounds.center.x) - _buffer[(int)ObsField.PlayerPosXN];
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.RelYN)] = Arena.NormalizeY(bounds.center.y) - _buffer[(int)ObsField.PlayerPosYN];
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.HalfWidth)] = bounds.extents.x;
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.HalfHeight)] = bounds.extents.y;
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.DistanceN)] = Mathf.Sqrt(candidate.DistanceSquared) / Mathf.Max(1f, Arena.HalfWidth);
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.Damage)] = candidate.Hazard.damageDealt;
                _buffer[ObservationSchema.HazardField(slot, HazardObsField.HazardType)] = (float)(int)candidate.Hazard.hazardType;
            }
        }

        private const int EnemyLayer = 11;

        private const float HazardKeepRadius = 40f;

        private static readonly float HazardKeepRadiusSquared = HazardKeepRadius * HazardKeepRadius;

        private Vector2 _heroCenter;

        private bool _hasHero;

        private float DistanceSquared(Vector3 position)
        {
            if (!_hasHero)
            {
                return 0f;
            }

            float dx = position.x - _heroCenter.x;
            float dy = position.y - _heroCenter.y;
            return dx * dx + dy * dy;
        }

        private static int CompareEnemy(EnemyCandidate a, EnemyCandidate b)
        {
            return a.DistanceSquared.CompareTo(b.DistanceSquared);
        }

        private static int CompareHazard(HazardCandidate a, HazardCandidate b)
        {
            return a.DistanceSquared.CompareTo(b.DistanceSquared);
        }

        private static bool TryGetBounds(GameObject target, out Bounds bounds)
        {
            bounds = default(Bounds);
            Collider2D[] colliders = target.GetComponents<Collider2D>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider2D collider = colliders[i];
                if (collider == null || !collider.enabled)
                {
                    continue;
                }

                Bounds candidate = collider.bounds;
                if (candidate.size.x <= 0.001f && candidate.size.y <= 0.001f)
                {
                    continue;
                }

                bounds = candidate;
                return true;
            }

            return false;
        }

        private struct EnemyCandidate
        {
            internal HealthManager Manager;

            internal float DistanceSquared;
        }

        private struct HazardCandidate
        {
            internal DamageHero Hazard;

            internal Collider2D Collider;

            internal float DistanceSquared;
        }
    }
}
