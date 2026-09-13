using System.Collections.Generic;
using System.Text;

namespace RLEnv.Observation
{
    // 观测字段表: 名字与顺序就是协议里的顺序, Python 侧按名字取值, 不硬编码下标.
    //
    // 结构:
    //   [固定段] 主角 / Boss / 场地 / 时间与事件
    //   [重复段] 最近的 MaxEnemies 个小怪, 每个 EnemyStride 项
    //   [重复段] 最近的 MaxHazards 个危险框, 每个 HazardStride 项
    //   [重复段] 最近的 MaxBossFsms 个 Boss FSM 状态, 每个 BossFsmStride 项
    internal static class ObservationSchema
    {
        internal const int MaxEnemies = 6;

        internal const int MaxHazards = 8;

        internal const int MaxBossFsms = 4;

        internal static readonly string[] EnemyFieldNames = new string[]
        {
            "valid", "rel_x_n", "rel_y_n", "vel_x", "vel_y",
            "health", "health_ratio", "half_w", "half_h", "facing", "enemy_type"
        };

        internal static readonly string[] HazardFieldNames = new string[]
        {
            "valid", "rel_x_n", "rel_y_n", "half_w", "half_h", "distance_n", "damage", "hazard_type", "enabled"
        };

        internal static readonly string[] BossFsmFieldNames = new string[]
        {
            "valid", "state_id"
        };

        private static readonly string[] FixedNames = new string[]
        {
            "player_pos_x_n",
            "player_pos_y_n",
            "player_pos_x_world",
            "player_pos_y_world",
            "player_vel_x",
            "player_vel_y",
            "player_facing",
            "player_on_ground",
            "player_was_on_ground",
            "player_jumping",
            "player_double_jumping",
            "player_falling",
            "player_dashing",
            "player_air_dashing",
            "player_wall_sliding",
            "player_wall_clinging",
            "player_touching_wall",
            "player_looking_up",
            "player_looking_down",
            "player_attacking",
            "player_up_attacking",
            "player_down_attacking",
            "player_nail_charging",
            "player_recoiling",
            "player_invulnerable",
            "player_binding",
            "player_dead",
            "player_hazard_death",
            "player_transitioning",
            "player_accepting_input",
            "player_control_relinquished",
            "player_hero_state",
            "player_health",
            "player_health_ratio",
            "player_health_max",
            "player_silk",
            "player_silk_ratio",
            "player_silk_max",
            "player_half_w",
            "player_half_h",

            "boss_alive",
            "boss_rel_x_n",
            "boss_rel_y_n",
            "boss_vel_x",
            "boss_vel_y",
            "boss_facing",
            "boss_health",
            "boss_health_ratio",
            "boss_health_max",
            "boss_distance_n",
            "boss_state_id",
            "boss_health_total_ratio",
            "boss_count",
            "boss_invincible",
            "boss_pos_x_world",
            "boss_pos_y_world",
            "boss_half_w",
            "boss_half_h",

            "arena_center_x",
            "arena_center_y",
            "arena_half_width",
            "arena_half_height",

            "physics_frame",
            "episode_step",
            "damage_dealt_step",
            "damage_taken_step",
            "boss_killed_step",
            "player_died_step",
            "enemy_count",
            "hazard_count"
        };

        private static readonly string[] Names = BuildNames();

        internal static readonly int EnemyStride = EnemyFieldNames.Length;

        internal static readonly int HazardStride = HazardFieldNames.Length;

        internal static readonly int BossFsmStride = BossFsmFieldNames.Length;

        internal static readonly int EnemyBase = FixedNames.Length;

        internal static readonly int HazardBase = EnemyBase + MaxEnemies * EnemyStride;

        internal static readonly int BossFsmBase = HazardBase + MaxHazards * HazardStride;

        internal static int FieldCount
        {
            get { return Names.Length; }
        }

        internal static string[] FieldNames
        {
            get { return Names; }
        }

        internal static int EnemyField(int slot, EnemyObsField field)
        {
            return EnemyBase + slot * EnemyStride + (int)field;
        }

        internal static int HazardField(int slot, HazardObsField field)
        {
            return HazardBase + slot * HazardStride + (int)field;
        }

        internal static int BossFsmField(int slot, BossFsmObsField field)
        {
            return BossFsmBase + slot * BossFsmStride + (int)field;
        }

        internal static string ToJson()
        {
            StringBuilder builder = new StringBuilder(Names.Length * 24);
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

        // 字段表与枚举写错时尽早报错, 免得 Python 侧解析时才发现对不上.
        internal static string Validate()
        {
            if (FixedNames.Length != (int)ObsField.Count)
            {
                return string.Format("固定字段数量不匹配: 名称 {0} 个, 枚举 {1} 个", FixedNames.Length, (int)ObsField.Count);
            }

            if (EnemyFieldNames.Length != (int)EnemyObsField.Count)
            {
                return string.Format("敌人字段数量不匹配: 名称 {0} 个, 枚举 {1} 个", EnemyFieldNames.Length, (int)EnemyObsField.Count);
            }

            if (HazardFieldNames.Length != (int)HazardObsField.Count)
            {
                return string.Format("危险框字段数量不匹配: 名称 {0} 个, 枚举 {1} 个", HazardFieldNames.Length, (int)HazardObsField.Count);
            }

            if (BossFsmFieldNames.Length != (int)BossFsmObsField.Count)
            {
                return string.Format("Boss FSM 字段数量不匹配: 名称 {0} 个, 枚举 {1} 个", BossFsmFieldNames.Length, (int)BossFsmObsField.Count);
            }

            return null;
        }

        private static string[] BuildNames()
        {
            List<string> names = new List<string>(FixedNames.Length + MaxEnemies * EnemyFieldNames.Length + MaxHazards * HazardFieldNames.Length + MaxBossFsms * BossFsmFieldNames.Length);
            names.AddRange(FixedNames);

            for (int slot = 0; slot < MaxEnemies; slot++)
            {
                for (int i = 0; i < EnemyFieldNames.Length; i++)
                {
                    names.Add("enemy" + slot + "_" + EnemyFieldNames[i]);
                }
            }

            for (int slot = 0; slot < MaxHazards; slot++)
            {
                for (int i = 0; i < HazardFieldNames.Length; i++)
                {
                    names.Add("hazard" + slot + "_" + HazardFieldNames[i]);
                }
            }

            for (int slot = 0; slot < MaxBossFsms; slot++)
            {
                for (int i = 0; i < BossFsmFieldNames.Length; i++)
                {
                    names.Add("boss_fsm" + slot + "_" + BossFsmFieldNames[i]);
                }
            }

            return names.ToArray();
        }
    }
}
