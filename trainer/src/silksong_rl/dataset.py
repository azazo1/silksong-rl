"""人类示范数据集的读取与统计."""

from __future__ import annotations

import logging
from pathlib import Path

import numpy as np

from .fields import build_mask, find_cumulative_event_columns
from .state_ids import build_vocabulary, load_state_map, remap_states, state_columns

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


def load_demonstrations(path: str | Path) -> tuple[np.ndarray, np.ndarray, dict[str, int]]:
    """把所有示范文件拼成 (观测, 动作, 状态词表) 三个值.

    每个示范文件旁边可能有一份 ``state-map-*.json`` (录制时随局落盘), 描述那个会话里
    "编号 -> Boss 状态名" 的对应. 有映射的文件按名字重映射成稳定编号, 没有的 (旧录像)
    只能把状态列清零. 返回的词表要在训练侧复用, 否则网络看到的编号含义又会对不上.
    """

    groups: list[tuple[Path, np.ndarray, np.ndarray, dict[int, str] | None]] = []
    session_maps: dict[str, dict[int, str]] = {}
    field_names: list[str] | None = None

    for file in find_files(path):
        with np.load(file, allow_pickle=False) as data:
            obs = np.asarray(data["obs"], dtype=np.float32)
            act = np.asarray(data["action"], dtype=np.int64)
            names = [str(name) for name in data["fields"]] if "fields" in data else None
            map_name = str(data["state_map"]) if "state_map" in data else None

        if obs.shape[0] != act.shape[0]:
            raise ValueError(f"{file} 的观测与动作数量不一致: {obs.shape[0]} vs {act.shape[0]}")

        if field_names is None:
            field_names = names
        elif names is not None and names != field_names:
            raise ValueError(f"{file} 的观测字段与其它文件不一致")

        session_map = None
        if map_name:
            if map_name not in session_maps:
                session_maps[map_name] = load_state_map(file.parent / map_name)
            session_map = session_maps[map_name]

        groups.append((file, obs, act, session_map))
        LOGGER.info("载入 %s: %d 条样本%s", file.name, obs.shape[0], "" if session_map else " (无状态映射)")

    vocabulary = build_vocabulary(session_maps.values()) if session_maps else {}
    always_mask = build_mask(field_names or [], include_state_fields=False)
    columns = state_columns(field_names or [])

    observations: list[np.ndarray] = []
    actions: list[np.ndarray] = []
    remapped_rows = 0
    tainted_names: set[str] = set()
    for _, obs, act, session_map in groups:
        if session_map is not None and vocabulary:
            obs = remap_states(obs, columns, session_map, vocabulary)
            remapped_rows += obs.shape[0]
        elif columns:
            # 旧录像没有映射, 只能清零, 免得把两个会话的编号混在一起.
            obs[:, columns] = 0.0

        if always_mask:
            obs[:, always_mask] = 0.0

        # 插件漏按步清零时, "本步事件"会变成整局累计值; 这种列在训练时是增量, 语义对不上, 只能丢掉.
        tainted = find_cumulative_event_columns(obs, field_names or [])
        if tainted:
            tainted_names.update((field_names or [])[index] for index in tainted)
            obs[:, tainted] = 0.0

        observations.append(obs)
        actions.append(act)

    if tainted_names:
        LOGGER.warning("示范里的 %s 是整局累计值 (插件录制时未按步清零), 已清零这几列", ", ".join(sorted(tainted_names)))

    all_obs = np.concatenate(observations, axis=0)
    all_act = np.concatenate(actions, axis=0)

    if field_names:
        LOGGER.info("观测字段 %d 个", len(field_names))
        if always_mask:
            LOGGER.info("已清零与游戏进程时长相关的字段: %s", ", ".join(field_names[i] for i in always_mask))

    if vocabulary:
        LOGGER.info(
            "Boss 状态词表 %d 项, 已重映射 %d / %d 条样本的 %d 个状态列",
            len(vocabulary),
            remapped_rows,
            all_obs.shape[0],
            len(columns),
        )
    elif columns:
        LOGGER.warning("示范里没有状态映射, 这 %d 个 Boss 状态列只能清零", len(columns))

    describe_actions(all_act)
    return all_obs, all_act, vocabulary


def describe_actions(actions: np.ndarray) -> None:
    """打印每个动作维度的取值分布, 便于发现示范里某个键几乎没按过."""

    labels = ("左右", "上下", "跳跃", "攻击", "缚丝")
    for index in range(actions.shape[1]):
        values, counts = np.unique(actions[:, index], return_counts=True)
        share = ", ".join(
            f"{int(value)}: {count / len(actions):.1%}" for value, count in zip(values, counts, strict=True)
        )
        LOGGER.info("动作维度 %s 分布: %s", labels[index] if index < len(labels) else index, share)
