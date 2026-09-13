"""奖励计算.

奖励全部在 Python 侧算: mod 只上报"这一步实际造成的伤害 / 受到的伤害 / 死亡事件",
调奖励塑形不需要重编插件.
"""

from __future__ import annotations

from dataclasses import dataclass

from .client import Observation


@dataclass
class RewardConfig:
    """奖励各项权重."""

    damage_dealt: float = 1.0
    damage_taken: float = -1.0
    boss_kill: float = 25.0
    player_death: float = -25.0
    step_penalty: float = -0.002
    approach: float = 0.0
    boss_hp_ratio_bonus: float = 0.0
    clip: float = 0.0

    def describe(self) -> str:
        return (
            f"伤害 {self.damage_dealt:+.2f}/点, 受伤 {self.damage_taken:+.2f}/次, "
            f"击杀 {self.boss_kill:+.1f}, 阵亡 {self.player_death:+.1f}, "
            f"每步 {self.step_penalty:+.4f}, 接近 {self.approach:+.3f}, "
            f"血量奖励 {self.boss_hp_ratio_bonus:+.2f}"
        )


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
        "step_penalty": config.step_penalty,
        "approach": 0.0,
        "boss_hp_ratio": 0.0,
    }

    if config.approach != 0.0 and previous is not None:
        current_distance = float(named.get("boss_distance_n", 0.0))
        previous_distance = float(previous.named.get("boss_distance_n", current_distance))
        components["approach"] = config.approach * (previous_distance - current_distance)

    if config.boss_hp_ratio_bonus != 0.0:
        alive = float(named.get("boss_alive", 0.0))
        health_ratio = float(named.get("boss_health_ratio", 0.0))
        components["boss_hp_ratio"] = config.boss_hp_ratio_bonus * (1.0 - health_ratio) * (1.0 if alive > 0.5 else 0.0)

    reward = float(sum(components.values()))
    if config.clip > 0.0:
        reward = max(-config.clip, min(config.clip, reward))

    return reward, components
