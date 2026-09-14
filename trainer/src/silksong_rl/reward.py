"""奖励计算.

奖励全部在 Python 侧算: mod 只上报"这一步实际造成的伤害 / 受到的伤害 / 死亡事件",
调奖励塑形不需要重编插件.
"""

from __future__ import annotations

from dataclasses import dataclass

from .client import Observation

# 够得着的判定: 用世界坐标算双方碰撞盒的边缘间距. 人类示范里命中几乎都发生在
# dx < 3 且 dy < 3 的范围内, 超过这个范围挥刀是纯浪费 (还占着出刀冷却).
REACH_DX = 3.5
REACH_DY = 3.5

# 密集奖励 (每步固定量) 的参照步长: 插件默认 6 个物理帧, 约 0.1 秒游戏时间.
REFERENCE_STEP_FRAMES = 6.0

# 折扣因子的换算参照: 不管一步跨多少游戏时间, "能看多远"都保持约 15 秒 (见 train.default_gamma).
HORIZON_SECONDS = 15.0


@dataclass
class RewardConfig:
    """奖励各项权重."""

    damage_dealt: float = 1.0
    damage_taken: float = -1.0
    boss_kill: float = 25.0
    player_death: float = -25.0
    step_penalty: float = -0.002
    approach: float = 0.0
    close_reward: float = 0.0
    close_distance: float = 0.3
    whiff_penalty: float = 0.0
    # 一次挥刀打完却没造成任何伤害时的惩罚. 按"步"算的惩罚摊薄了责任, 而一次挥空真正
    # 浪费的是整段出刀动画 (期间动不了也砍不出第二刀), 所以额外给一个按刀结算的项.
    swing_whiff_penalty: float = 0.0
    # 下面三项参考同类项目 (pixel DQN) 的奖励设计: 长时间不造成伤害要罚 (防止学会站着不动),
    # 成功回血要奖 (回血直接换来更多输出机会), 按住当前根本执行不了的键也要罚一点.
    inactivity_penalty: float = 0.0
    inactivity_window: float = 5.0
    heal_reward: float = 0.0
    bind_waste_penalty: float = 0.0
    bind_silk_threshold: float = 0.9
    # 与 Boss 同高的奖励: 苔藓之母大部分时间飞在主角头顶 (边缘间距中位 3.4 个单位, 而一次满跳
    # 只上升约 1.7), 人类靠"连着跳"把自己挂在 Boss 的高度上 (38% 的步数在 y>20 的空中),
    # 而策略一直贴地面 (y 的 p90 只有 18.7). 距离势函数只能奖励"净缩短", 来回跳是净零,
    # 所以这里单独给"高度差在容差内"一个每步奖励.
    height_reward: float = 0.0
    height_tolerance: float = 1.5
    # 贴脸奖励: 双方碰撞盒几乎挨上时给. 人类 62% 的挥刀都发生在盒子重叠的位置, 而策略只有 28%
    # (它常在"够得着但差一截"的距离出手), 这是命中率上不去的最后一段距离.
    contact_reward: float = 0.0
    contact_distance: float = 0.75
    boss_hp_ratio_bonus: float = 0.0
    clip: float = 0.0
    # 密集奖励按 "每 0.1 秒游戏时间" 折算: 换了决策粒度 (--step-frames) 之后,
    # 每步固定量的奖励在每游戏秒里的总权重不会跟着变, 比较不同粒度才不会被搅混.
    dense_scale: float = 1.0

    def describe(self) -> str:
        return (
            f"伤害 {self.damage_dealt:+.2f}/点, 受伤 {self.damage_taken:+.2f}/次, "
            f"击杀 {self.boss_kill:+.1f}, 阵亡 {self.player_death:+.1f}, "
            f"每步 {self.step_penalty:+.4f}, 接近 {self.approach:+.3f}, "
            f"贴身 {self.close_reward:+.3f}/步 (<{self.close_distance}), "
            f"挥空 {self.whiff_penalty:+.3f}/步, "
            f"划水 {self.inactivity_penalty:+.2f}/{self.inactivity_window:g}秒, "
            f"同高 {self.height_reward:+.3f}/步 (<{self.height_tolerance}), "
            f"贴脸 {self.contact_reward:+.3f}/步 (<{self.contact_distance}), "
            f"回血 {self.heal_reward:+.2f}/点, 空按缚丝 {self.bind_waste_penalty:+.3f}/步, "
            f"按刀挥空 {self.swing_whiff_penalty:+.2f}/刀, "
            f"血量奖励 {self.boss_hp_ratio_bonus:+.2f}"
        )


def vertical_gap(named: dict) -> float | None:
    """双方碰撞盒的竖直边缘间距 (负数=重叠), 观测里没有世界坐标时返回 None."""

    needed = ("player_pos_y_world", "boss_pos_y_world", "player_half_h", "boss_half_h")
    if any(name not in named for name in needed):
        return None

    return abs(named["player_pos_y_world"] - named["boss_pos_y_world"]) - (named["player_half_h"] + named["boss_half_h"])


def edge_gaps(named: dict) -> tuple[float, float] | None:
    """双方碰撞盒在两个方向上的边缘间距 (负数=重叠); 缺世界坐标时返回 None."""

    needed = (
        "player_pos_x_world",
        "player_pos_y_world",
        "boss_pos_x_world",
        "boss_pos_y_world",
        "player_half_w",
        "player_half_h",
        "boss_half_w",
        "boss_half_h",
    )
    if any(name not in named for name in needed):
        return None

    dx = abs(named["player_pos_x_world"] - named["boss_pos_x_world"]) - (named["player_half_w"] + named["boss_half_w"])
    dy = abs(named["player_pos_y_world"] - named["boss_pos_y_world"]) - (named["player_half_h"] + named["boss_half_h"])
    return dx, dy


def out_of_reach(named: dict) -> bool:
    """这一步的挥刀够不够得着 Boss (Boss 不在场时不算挥空)."""

    if float(named.get("boss_alive", 0.0)) <= 0.5:
        return False

    gaps = edge_gaps(named)
    if gaps is None:
        return False  # 观测里没有世界坐标就没法判, 宁可不算

    dx, dy = gaps
    return dx > REACH_DX or dy > REACH_DY


def compute_reward(previous: Observation | None, current: Observation, config: RewardConfig) -> tuple[float, dict]:
    """按前后两帧观测计算这一步的奖励, 同时回传各项分量便于日志分析."""

    named = current.named
    damage_dealt = float(named.get("damage_dealt_step", 0.0))
    damage_taken = float(named.get("damage_taken_step", 0.0))
    boss_killed = float(named.get("boss_killed_step", 0.0))
    player_died = float(named.get("player_died_step", 0.0))

    components = {
        "damage_dealt": config.damage_dealt * damage_dealt,
        "damage_taken": config.damage_taken * damage_taken,
        "boss_kill": config.boss_kill * boss_killed,
        "player_death": config.player_death * player_died,
        "step_penalty": config.step_penalty * config.dense_scale,
        "approach": 0.0,
        "close": 0.0,
        "whiff": 0.0,
        "height": 0.0,
        "contact": 0.0,
        "boss_hp_ratio": 0.0,
    }

    if config.approach != 0.0 and previous is not None:
        current_distance = float(named.get("boss_distance_n", 0.0))
        previous_distance = float(previous.named.get("boss_distance_n", current_distance))
        components["approach"] = config.approach * (previous_distance - current_distance)

    # 接近项是望远镜式求和, 一回合的总收益被"初始距离"卡死, 只能提供"往哪边走"的方向;
    # 想让策略真的停在攻击距离里, 得靠这个每步都给奖励的贴身项.
    if config.close_reward != 0.0:
        alive = float(named.get("boss_alive", 0.0))
        distance = float(named.get("boss_distance_n", 1.0))
        if alive > 0.5 and 0.0 <= distance < config.close_distance:
            components["close"] = config.close_reward * config.dense_scale

    # 够不着还挥刀: 每刀都占着出刀冷却, 等 Boss 真进范围时反而没刀可出.
    if config.whiff_penalty != 0.0:
        if float(named.get("player_attacking", 0.0)) > 0.5 and out_of_reach(named):
            components["whiff"] = config.whiff_penalty * config.dense_scale

    # 和 Boss 同高: 这条是给"跳上去贴着打"用的, 光靠距离势函数拿不到 (来回跳是净零).
    if config.height_reward != 0.0 and float(named.get("boss_alive", 0.0)) > 0.5:
        gap = vertical_gap(named)
        if gap is not None and abs(gap) < config.height_tolerance:
            components["height"] = config.height_reward * config.dense_scale

    # 贴脸: 两个方向都几乎挨上才算, 这是命中率最后卡住的那一段距离.
    if config.contact_reward != 0.0 and float(named.get("boss_alive", 0.0)) > 0.5:
        gaps = edge_gaps(named)
        if gaps is not None and max(gaps) < config.contact_distance:
            components["contact"] = config.contact_reward * config.dense_scale

    if config.boss_hp_ratio_bonus != 0.0:
        alive = float(named.get("boss_alive", 0.0))
        health_ratio = float(named.get("boss_health_ratio", 0.0))
        components["boss_hp_ratio"] = config.boss_hp_ratio_bonus * (1.0 - health_ratio) * (1.0 if alive > 0.5 else 0.0)

    reward = float(sum(components.values()))
    if config.clip > 0.0:
        reward = max(-config.clip, min(config.clip, reward))

    return reward, components
