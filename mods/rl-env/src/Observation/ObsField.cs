namespace RLEnv.Observation
{
    // 固定段 (每个回合一组) 的观测字段.
    internal enum ObsField
    {
        // --- 主角 ---
        PlayerPosXN,
        PlayerPosYN,
        PlayerPosXWorld,
        PlayerPosYWorld,
        PlayerVelX,
        PlayerVelY,
        PlayerFacing,
        PlayerOnGround,
        PlayerWasOnGround,
        PlayerJumping,
        PlayerDoubleJumping,
        PlayerFalling,
        PlayerDashing,
        PlayerAirDashing,
        PlayerWallSliding,
        PlayerWallClinging,
        PlayerTouchingWall,
        PlayerLookingUp,
        PlayerLookingDown,
        PlayerAttacking,
        PlayerUpAttacking,
        PlayerDownAttacking,
        PlayerNailCharging,
        PlayerRecoiling,
        PlayerInvulnerable,
        PlayerBinding,
        PlayerDead,
        PlayerHazardDeath,
        PlayerTransitioning,
        PlayerAcceptingInput,
        PlayerControlRelinquished,
        PlayerHeroState,
        PlayerHealth,
        PlayerHealthRatio,
        PlayerHealthMax,
        PlayerSilk,
        PlayerSilkRatio,
        PlayerSilkMax,
        PlayerHalfWidth,
        PlayerHalfHeight,

        // --- Boss ---
        BossAlive,
        BossRelXN,
        BossRelYN,
        BossVelX,
        BossVelY,
        BossFacing,
        BossHealth,
        BossHealthRatio,
        BossHealthMax,
        BossDistanceN,
        BossStateId,
        BossHealthTotalRatio,
        BossCount,
        BossInvincible,
        BossPosXWorld,
        BossPosYWorld,
        BossHalfWidth,
        BossHalfHeight,

        // --- 场地 ---
        ArenaCenterX,
        ArenaCenterY,
        ArenaHalfWidth,
        ArenaHalfHeight,

        // --- 时间与事件 ---
        PhysicsFrame,
        EpisodeStep,
        DamageDealtStep,
        DamageTakenStep,
        BossKilledStep,
        PlayerDiedStep,
        EnemyCount,
        HazardCount,

        Count
    }

    // 每个最近敌人 (小怪 / 召唤物) 一组.
    internal enum EnemyObsField
    {
        Valid,
        RelXN,
        RelYN,
        VelX,
        VelY,
        Health,
        HealthRatio,
        HalfWidth,
        HalfHeight,
        Facing,
        EnemyType,

        Count
    }

    // 每个最近危险框 (敌人攻击判定 / 陷阱 / 投射物) 一组.
    internal enum HazardObsField
    {
        Valid,
        RelXN,
        RelYN,
        HalfWidth,
        HalfHeight,
        DistanceN,
        Damage,
        HazardType,
        Enabled,

        Count
    }

    // 每个 Boss 的 FSM (按名字排序取前几个) 的当前状态 id.
    internal enum BossFsmObsField
    {
        Valid,
        StateId,

        Count
    }
}
