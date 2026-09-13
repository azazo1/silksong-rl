"""观测里需要屏蔽的字段.

这些字段看起来像特征, 实际上在不同会话之间不可比, 行为克隆会把它们当成有效输入学进去,
到了训练环境里同一个维度却是另一套含义, 表现为策略一上场就崩:

- ``physics_frame``: 值取决于"这局游戏从启动到现在跑了多久", 示范与训练的取值范围完全不同.
- ``boss_state_id`` / ``boss_fsm*_state_id``: 是插件按"发现顺序"自增分配的 id, 每个会话重新编号,
  同一个 Boss 状态在示范里和训练里可能是不同的数字. 状态名到 id 的映射由 ``StateMap`` 消息单独给出,
  要用来当特征得先按名字重映射成稳定编号 (见 ``state_ids.py``).

训练与克隆两侧都把这些列清零, 让网络彻底忽略它们.
"""

from __future__ import annotations

IGNORED_FIELDS: tuple[str, ...] = (
    "physics_frame",
    "boss_state_id",
    "boss_fsm0_state_id",
    "boss_fsm1_state_id",
    "boss_fsm2_state_id",
    "boss_fsm3_state_id",
)


def build_mask(field_names: list[str]) -> list[int]:
    """返回需要清零的列下标."""

    return [index for index, name in enumerate(field_names) if name in IGNORED_FIELDS]

