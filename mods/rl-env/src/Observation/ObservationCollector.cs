using System.Collections.Generic;
using UnityEngine;

namespace RLEnv.Observation
{
    // 每步把游戏状态写进一个定长 float 数组, 顺序与 ObservationSchema 一致.
    internal sealed class ObservationCollector
    {
        private readonly BossTracker _bosses;

        private readonly CombatTracker _combat;

        private readonly float[] _buffer = new float[(int)ObsField.Count];

        private readonly List<PlayMakerFSM> _bossFsms = new List<PlayMakerFSM>();

        private HealthManager _fsmOwner;

        private int _physicsFrameAtStepStart;

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

        internal void MarkStepStart()
        {
            _physicsFrameAtStepStart = CustomPlayerLoop.FixedUpdateCycle;
        }

        internal void Collect(int episodeStep)
        {
            for (int i = 0; i < _buffer.Length; i++)
            {
                _buffer[i] = 0f;
            }

            HeroController hero = HeroController.instance;
            PlayerData playerData = PlayerData.instance;

            if (hero != null)
            {
                Vector3 position = hero.transform.position;
                Vector2 velocity = hero.Body != null ? hero.Body.linearVelocity : Vector2.zero;
                HeroControllerStates cState = hero.cState;

                _buffer[(int)ObsField.PlayerPosXN] = Arena.NormalizeX(position.x);
                _buffer[(int)ObsField.PlayerPosYN] = Arena.NormalizeY(position.y);
                _buffer[(int)ObsField.PlayerVelX] = velocity.x;
                _buffer[(int)ObsField.PlayerVelY] = velocity.y;
                _buffer[(int)ObsField.PlayerPosXWorld] = position.x;
                _buffer[(int)ObsField.PlayerPosYWorld] = position.y;

                if (cState != null)
                {
                    _buffer[(int)ObsField.PlayerFacing] = cState.facingRight ? 1f : -1f;
                    _buffer[(int)ObsField.PlayerOnGround] = cState.onGround ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerInvulnerable] = cState.Invulnerable ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerAttacking] = (cState.attacking || cState.upAttacking || cState.downAttacking) ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerRecoiling] = (cState.recoiling || cState.recoilingLeft || cState.recoilingRight) ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerBinding] = (cState.isBinding || cState.focusing) ? 1f : 0f;
                    _buffer[(int)ObsField.PlayerDead] = cState.dead ? 1f : 0f;
                }
            }

            if (playerData != null)
            {
                int maxHealth = Mathf.Max(1, playerData.CurrentMaxHealth);
                int silkMax = Mathf.Max(1, playerData.CurrentSilkMax);
                _buffer[(int)ObsField.PlayerHealth] = playerData.health;
                _buffer[(int)ObsField.PlayerHealthRatio] = playerData.health / (float)maxHealth;
                _buffer[(int)ObsField.PlayerSilk] = playerData.silk;
                _buffer[(int)ObsField.PlayerSilkRatio] = playerData.silk / (float)silkMax;
            }

            HealthManager boss = _bosses.Primary;
            if (boss != null)
            {
                Vector3 bossPosition = boss.transform.position;
                Vector2 bossVelocity = Vector2.zero;
                Rigidbody2D body = boss.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    bossVelocity = body.linearVelocity;
                }

                bool alive = !boss.GetIsDead() && boss.hp > 0;
                float playerX = _buffer[(int)ObsField.PlayerPosXWorld];
                float playerY = _buffer[(int)ObsField.PlayerPosYWorld];

                _buffer[(int)ObsField.BossAlive] = alive ? 1f : 0f;
                _buffer[(int)ObsField.BossPosXWorld] = bossPosition.x;
                _buffer[(int)ObsField.BossPosYWorld] = bossPosition.y;
                _buffer[(int)ObsField.BossRelXN] = Arena.NormalizeX(bossPosition.x) - Arena.NormalizeX(playerX);
                _buffer[(int)ObsField.BossRelYN] = Arena.NormalizeY(bossPosition.y) - Arena.NormalizeY(playerY);
                _buffer[(int)ObsField.BossVelX] = bossVelocity.x;
                _buffer[(int)ObsField.BossVelY] = bossVelocity.y;
                _buffer[(int)ObsField.BossFacing] = boss.transform.localScale.x >= 0f ? 1f : -1f;
                _buffer[(int)ObsField.BossHealth] = boss.hp;
                _buffer[(int)ObsField.BossHealthMax] = _bosses.PrimaryMaxHealth;
                _buffer[(int)ObsField.BossHealthRatio] = _bosses.PrimaryMaxHealth > 0
                    ? boss.hp / (float)_bosses.PrimaryMaxHealth
                    : 0f;
                float dx = _buffer[(int)ObsField.BossRelXN];
                float dy = _buffer[(int)ObsField.BossRelYN];
                _buffer[(int)ObsField.BossDistanceN] = Mathf.Sqrt(dx * dx + dy * dy);
                _buffer[(int)ObsField.BossStateId] = ResolveBossStateId(boss);
            }
            else
            {
                _buffer[(int)ObsField.BossStateId] = -1f;
            }

            _buffer[(int)ObsField.ArenaCenterX] = Arena.CenterX;
            _buffer[(int)ObsField.ArenaCenterY] = Arena.CenterY;
            _buffer[(int)ObsField.ArenaHalfWidth] = Arena.HalfWidth;
            _buffer[(int)ObsField.ArenaHalfHeight] = Arena.HalfHeight;

            _buffer[(int)ObsField.PhysicsFrame] = CustomPlayerLoop.FixedUpdateCycle;
            _buffer[(int)ObsField.EpisodeStep] = episodeStep;

            _buffer[(int)ObsField.DamageDealtStep] = _combat != null ? _combat.DamageDealtStep : 0f;
            _buffer[(int)ObsField.DamageTakenStep] = _combat != null ? _combat.DamageTakenStep : 0f;
            _buffer[(int)ObsField.BossKilledStep] = _combat != null && _combat.BossKilledStep ? 1f : 0f;
            _buffer[(int)ObsField.PlayerDiedStep] = _combat != null && _combat.PlayerDiedStep ? 1f : 0f;
        }

        // 把所有 FSM 的当前状态拼成一个键, 用整数 id 表示.
        private int ResolveBossStateId(HealthManager boss)
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
                return -1;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(64);
            for (int i = 0; i < _bossFsms.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('|');
                }

                builder.Append(_bossFsms[i].FsmName).Append('=').Append(_bossFsms[i].ActiveStateName);
            }

            return States.GetId(builder.ToString());
        }
    }
}
