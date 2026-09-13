"""人类示范数据集的读取与统计."""

from __future__ import annotations

import logging
from pathlib import Path

import numpy as np

LOGGER = logging.getLogger("silksong_rl.dataset")


def find_files(path: str | Path) -> list[Path]:
    """接受单个 .npz 或者一个目录 (取目录下全部 .npz)."""

    target = Path(path)
    if target.is_file():
        return [target]

    if not target.is_dir():
        raise FileNotFoundError(f"找不到示范数据: {target}")

    files = sorted(target.glob("*.npz"))
    if not files:
        raise FileNotFoundError(f"{target} 里没有 .npz 示范数据")

    return files


def load_demonstrations(path: str | Path) -> tuple[np.ndarray, np.ndarray]:
    """把所有示范文件拼成 (观测, 动作) 两个数组."""

    observations: list[np.ndarray] = []
    actions: list[np.ndarray] = []
    field_names: list[str] | None = None

    for file in find_files(path):
        with np.load(file, allow_pickle=False) as data:
            obs = np.asarray(data["obs"], dtype=np.float32)
            act = np.asarray(data["action"], dtype=np.int64)
            names = [str(name) for name in data["fields"]] if "fields" in data else None

        if obs.shape[0] != act.shape[0]:
            raise ValueError(f"{file} 的观测与动作数量不一致: {obs.shape[0]} vs {act.shape[0]}")

        if field_names is None:
            field_names = names
        elif names is not None and names != field_names:
            raise ValueError(f"{file} 的观测字段与其它文件不一致")

        observations.append(obs)
        actions.append(act)
        LOGGER.info("载入 %s: %d 条样本", file.name, obs.shape[0])

    all_obs = np.concatenate(observations, axis=0)
    all_act = np.concatenate(actions, axis=0)
    if field_names:
        LOGGER.info("观测字段: %s", ", ".join(field_names))

    describe_actions(all_act)
    return all_obs, all_act


def describe_actions(actions: np.ndarray) -> None:
    """打印每个动作维度的取值分布, 便于发现示范里某个键几乎没按过."""

    labels = ("左右", "上下", "跳跃", "攻击", "缚丝")
    for index in range(actions.shape[1]):
        values, counts = np.unique(actions[:, index], return_counts=True)
        share = ", ".join(
            f"{int(value)}: {count / len(actions):.1%}" for value, count in zip(values, counts, strict=True)
        )
        LOGGER.info("动作维度 %s 分布: %s", labels[index] if index < len(labels) else index, share)
