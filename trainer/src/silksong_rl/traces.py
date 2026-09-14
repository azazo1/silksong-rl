"""轨迹对比: 把人类示范和策略评估回合放在同一套口径上比.

训练日志只有每回合的汇总 (每局伤害 / 贴近步 / 按键数), 看不出"刀空在哪". 这个工具读
示范或评估留下的 npz 轨迹, 直接算命中发生在多远的距离上, 出手时站得够不够近, 以及
各维动作的取值分布, 用来定位"伤害上不去"到底卡在站位还是卡在出手时机.

用法::

    uv run silksong-traces --dir trainer/records/moss-mother-v3
    uv run silksong-traces --dir trainer/records/moss-mother-v3 --against .tmp/policy-traces
"""

from __future__ import annotations

import argparse
import logging
import sys
from dataclasses import dataclass
from pathlib import Path

import numpy as np

LOGGER = logging.getLogger("silksong_rl.traces")

DISTANCE_THRESHOLDS = (0.2, 0.3, 0.4, 0.5)
ACTION_NAMES = ("水平", "垂直", "跳跃", "攻击", "缚丝")


@dataclass
class TraceStats:
    """一个目录里所有回合的统计量."""

    directory: Path
    episodes: int
    steps: int
    damage: float
    hits: int
    hit_distance: np.ndarray
    in_range: dict[float, float]
    slash_share: float
    slash_in_range_share: float
    hit_per_slash: float
    action_shares: list[np.ndarray]

    @property
    def steps_per_episode(self) -> float:
        return self.steps / self.episodes if self.episodes else float("nan")


def load_traces(directory: Path) -> tuple[np.ndarray, np.ndarray, list[str]]:
    """把一个目录里的 npz 轨迹拼成 (观测, 动作, 字段名)."""

    observations: list[np.ndarray] = []
    actions: list[np.ndarray] = []
    fields: list[str] = []

    for path in sorted(directory.glob("*.npz")):
        with np.load(path, allow_pickle=True) as data:
            observations.append(np.asarray(data["obs"], dtype=np.float32))
            actions.append(np.asarray(data["action"], dtype=np.int64))
            if not fields:
                fields = [str(name) for name in data["fields"]]

    if not observations:
        raise SystemExit(f"{directory} 里没有 npz 轨迹")

    return np.concatenate(observations), np.concatenate(actions), fields


def column(observations: np.ndarray, fields: list[str], name: str, default: float = 0.0) -> np.ndarray:
    if name not in fields:
        return np.full(len(observations), default, dtype=np.float64)
    return observations[:, fields.index(name)].astype(np.float64)


def summarize(directory: Path) -> TraceStats:
    observations, actions, fields = load_traces(directory)

    boss_alive = column(observations, fields, "boss_alive", 1.0) > 0.5
    distance = column(observations, fields, "boss_distance_n", 1.0)
    damage_step = column(observations, fields, "damage_dealt_step")
    attacking = column(observations, fields, "player_attacking") > 0.5

    hits = (damage_step > 0.0) & boss_alive
    in_range = {threshold: float(np.mean((distance < threshold) & boss_alive)) for threshold in DISTANCE_THRESHOLDS}

    action_shares = []
    for index in range(actions.shape[1]):
        values, counts = np.unique(actions[:, index], return_counts=True)
        shares = np.zeros(int(max(values.max(), 1)) + 1)
        for value, count in zip(values, counts):
            shares[int(value)] = count / len(actions)
        action_shares.append(shares)

    episodes = len(list(directory.glob("*.npz")))
    return TraceStats(
        directory=directory,
        episodes=episodes,
        steps=len(observations),
        damage=float(damage_step.sum()),
        hits=int(hits.sum()),
        hit_distance=distance[hits],
        in_range=in_range,
        slash_share=float(np.mean(attacking)),
        slash_in_range_share=float(np.mean(attacking & (distance < 0.5) & boss_alive)),
        hit_per_slash=float(hits.sum() / attacking.sum()) if attacking.sum() else float("nan"),
        action_shares=action_shares,
    )


def describe(stats: TraceStats) -> str:
    """一个目录一份摘要, 每行一个角度."""

    lines = [
        f"{stats.directory}: {stats.episodes} 个回合, {stats.steps} 步 "
        f"({stats.steps_per_episode:.0f} 步/回合), 每回合伤害 {stats.damage / max(stats.episodes, 1):.1f}"
    ]
    lines.append(
        "  命中: 每回合 {0:.1f} 刀, 命中步占比 {1:.1f}%, 挥刀步占比 {2:.1f}%, 每刀命中率 {3:.1f}%".format(
            stats.hits / max(stats.episodes, 1),
            stats.hits / stats.steps * 100,
            stats.slash_share * 100,
            stats.hit_per_slash * 100,
        )
    )
    lines.append("  站位: " + " ".join(f"<{t:g} {stats.in_range[t] * 100:.1f}%" for t in DISTANCE_THRESHOLDS))
    lines.append(
        "  挥刀时贴身的占比 {0:.1f}%".format(stats.slash_in_range_share / max(stats.slash_share, 1e-9) * 100)
    )
    if stats.hit_distance.size:
        low, middle, high = np.percentile(stats.hit_distance, [10, 50, 90])
        within = " ".join(
            f"<{t:g} {np.mean(stats.hit_distance < t) * 100:.0f}%" for t in DISTANCE_THRESHOLDS
        )
        lines.append(f"  命中时距离: p10 {low:.2f} 中位 {middle:.2f} p90 {high:.2f} | {within}")
    else:
        lines.append("  命中时距离: 这一批轨迹一次都没打中")

    for name, shares in zip(ACTION_NAMES, stats.action_shares):
        lines.append(
            f"  动作 {name}: " + " ".join(f"{value}:{share * 100:.1f}%" for value, share in enumerate(shares))
        )

    return "\n".join(lines)


def main() -> None:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="对比示范与策略轨迹")
    parser.add_argument("--dir", required=True, help="轨迹目录 (示范或 --save-episodes 的产物)")
    parser.add_argument("--against", default=None, help="再给一个目录, 两份摘要并排打印")
    args = parser.parse_args()

    logging.basicConfig(level=logging.WARNING, format="%(levelname)-7s %(name)s: %(message)s")

    print(describe(summarize(Path(args.dir))))
    if args.against:
        print()
        print(describe(summarize(Path(args.against))))


if __name__ == "__main__":
    main()
