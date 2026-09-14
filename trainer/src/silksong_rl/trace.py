"""把策略自己的回合也落成示范那种 npz, 好和人类示范用同一套脚本逐步对比.

训练日志里只有每回合的汇总 (每局伤害, 贴近步, 按键数), 但"这一刀为什么空"这类问题
必须看逐步轨迹: 命中发生在多远的距离上, 出手时是不是在攻击距离内, 动作各维怎么分布.
评估时加 ``--save-episodes <目录>`` 就会顺手存一份, 格式与人类示范完全一致,
因此可以直接套用分析示范的脚本.

用法::

    uv run silksong-train --eval --model runs/<实验>/final.zip --episodes 6 \
        --save-episodes .tmp/policy-traces --stochastic
"""

from __future__ import annotations

import logging
from pathlib import Path

import numpy as np

from .state_ids import save_state_map

LOGGER = logging.getLogger("silksong_rl.trace")


class EpisodeTrace:
    """一个回合的 (观测, 动作) 序列, 结束时写成 npz."""

    def __init__(self) -> None:
        self.observations: list[np.ndarray] = []
        self.actions: list[list[int]] = []

    def add(self, values, action) -> None:
        self.observations.append(np.asarray(values, dtype=np.float32))
        self.actions.append([int(item) for item in np.asarray(action).reshape(-1)])

    def __len__(self) -> int:
        return len(self.observations)

    def save(
        self,
        directory: Path,
        index: int,
        field_names: list[str],
        state_map: dict[int, str],
        state_map_name: str,
    ) -> Path | None:
        if not self.observations:
            LOGGER.warning("第 %d 回合没有轨迹可存", index)
            return None

        directory.mkdir(parents=True, exist_ok=True)
        path = directory / f"episode-{index:03d}.npz"
        np.savez_compressed(
            path,
            obs=np.stack(self.observations),
            action=np.asarray(self.actions, dtype=np.int64),
            fields=np.asarray(field_names),
            state_map=np.asarray(state_map_name),
        )
        save_state_map(directory / state_map_name, state_map)
        LOGGER.info("策略轨迹已保存: %s (%d 步)", path, len(self.observations))
        return path
