"""观测里需要在训练侧处理的字段.

两类问题分开看:

- ``physics_frame``: 值取决于"这局游戏从启动到现在跑了多久", 示范与训练的取值范围完全不同,
  没有任何补救办法, 只能清零.
- ``boss_state_id`` / ``boss_fsm*_state_id``: 是插件按"发现顺序"自增分配的编号, 每个会话重新
  编号, 直接用会把"同一个数字在示范与训练里含义不同"喂给网络. 但它们有稳定的名字 (插件通过
  ``StateMap`` 消息给出编号到状态名的对应), 因此正确做法是按名字重映射成稳定编号, 见
  ``state_ids.py``; 只有在拿不到映射 (例如旧录像) 时才退回清零.

两个模块共用同一个字段表, 避免两处各写一份而对不上.
"""

from __future__ import annotations

import numpy as np

from .state_ids import STATE_FIELDS

# 无论如何都要清零的列.
ALWAYS_IGNORED_FIELDS: tuple[str, ...] = ("physics_frame",)

# 拿不到状态映射时才清零的列.
IGNORED_FIELDS: tuple[str, ...] = ALWAYS_IGNORED_FIELDS + STATE_FIELDS

# 这些列应当是"本步增量", 一旦在整局里单调不减就说明插件没有按步清零, 值会变成整局累计.
STEP_EVENT_FIELDS: tuple[str, ...] = ("damage_dealt_step", "damage_taken_step")


def find_cumulative_event_columns(observations: np.ndarray, field_names: list[str]) -> list[int]:
    """找出"本步事件"却整局单调不减的列 (说明它其实是累计值).

    训练时这两列是每步增量, 示范里如果是累计值, 行为克隆学到的输入分布与上场时看到的
    完全对不上, 因此必须识别出来清零, 而不是照着学.
    """

    columns: list[int] = []
    for name in STEP_EVENT_FIELDS:
        if name not in field_names:
            continue

        index = field_names.index(name)
        column = observations[:, index]
        if column.size >= 2 and column.max() > 0.0 and bool(np.all(np.diff(column) >= -1e-6)):
            columns.append(index)

    return columns


def build_mask(field_names: list[str], include_state_fields: bool = True) -> list[int]:
    """返回需要清零的列下标.

    ``include_state_fields=False`` 表示这些列会走 ``state_ids.remap_states`` 重映射, 不要清零.
    """

    names = IGNORED_FIELDS if include_state_fields else ALWAYS_IGNORED_FIELDS
    return [index for index, name in enumerate(field_names) if name in names]
