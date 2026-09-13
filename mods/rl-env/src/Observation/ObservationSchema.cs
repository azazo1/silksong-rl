namespace RLEnv.Observation
{
    // 观测向量的字段定义.
    //
    // 顺序即协议顺序: mod 侧按这个顺序填 float, Python 侧按 Hello 消息里给出的同名列表解析.
    // 归一化尽量放在 Python 侧做, 这里除了少量无量纲比值之外都发原始值, 方便改奖励塑形时不用重编 mod.
    internal enum ObsField
    {
        PlayerPosXN,
        PlayerPosYN,
        PlayerVelX,
        PlayerVelY,
        PlayerFacing,
        PlayerOnGround,
        PlayerHealthRatio,
        PlayerHealth,
        PlayerSilk,
        PlayerSilkRatio,
        PlayerBinding,
        PlayerInvulnerable,
        PlayerAttacking,
        PlayerRecoiling,
        PlayerDead,
        PlayerPosXWorld,
        PlayerPosYWorld,

        BossAlive,
        BossRelXN,
        BossRelYN,
        BossVelX,
        BossVelY,
        BossFacing,
        BossHealthRatio,
        BossHealth,
        BossHealthMax,
        BossDistanceN,
        BossStateId,
        BossPosXWorld,
        BossPosYWorld,

        ArenaCenterX,
        ArenaCenterY,
        ArenaHalfWidth,
        ArenaHalfHeight,

        PhysicsFrame,
        EpisodeStep,

        DamageDealtStep,
        DamageTakenStep,
        BossKilledStep,
        PlayerDiedStep,

        Count
    }

    internal static class ObservationSchema
    {
        // 与 ObsField 一一对应, 改动时两边一起改; Count 会做长度校验.
        private static readonly string[] Names = new string[]
        {
            "player_pos_x_n",
            "player_pos_y_n",
            "player_vel_x",
            "player_vel_y",
            "player_facing",
            "player_on_ground",
            "player_health_ratio",
            "player_health",
            "player_silk",
            "player_silk_ratio",
            "player_binding",
            "player_invulnerable",
            "player_attacking",
            "player_recoiling",
            "player_dead",
            "player_pos_x_world",
            "player_pos_y_world",

            "boss_alive",
            "boss_rel_x_n",
            "boss_rel_y_n",
            "boss_vel_x",
            "boss_vel_y",
            "boss_facing",
            "boss_health_ratio",
            "boss_health",
            "boss_health_max",
            "boss_distance_n",
            "boss_state_id",
            "boss_pos_x_world",
            "boss_pos_y_world",

            "arena_center_x",
            "arena_center_y",
            "arena_half_width",
            "arena_half_height",

            "physics_frame",
            "episode_step",

            "damage_dealt_step",
            "damage_taken_step",
            "boss_killed_step",
            "player_died_step"
        };

        internal static int FieldCount
        {
            get { return (int)ObsField.Count; }
        }

        internal static string[] FieldNames
        {
            get { return Names; }
        }

        // 字段表写错时尽早发现, 免得 Python 侧解析时才发现对不上.
        internal static string Validate()
        {
            if (Names.Length != (int)ObsField.Count)
            {
                return string.Format("观测字段数量不匹配: 名称 {0} 个, 枚举 {1} 个", Names.Length, (int)ObsField.Count);
            }

            return null;
        }

        internal static string ToJson()
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder(1024);
            builder.Append('[');
            for (int i = 0; i < Names.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"').Append(Names[i]).Append('"');
            }

            builder.Append(']');
            return builder.ToString();
        }
    }
}
