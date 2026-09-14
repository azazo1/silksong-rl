"""Boss 状态机编号的跨会话对齐.

观测里的 ``boss_state_id`` / ``boss_fsm*_state_id`` 是插件按"发现顺序"自增分配的编号, 每个
游戏会话重新编号, 因此同一个数字在示范里和训练里可能是完全不同的状态, 之前只能整列清零.

但这些编号旁边还有一份稳定的东西: 插件会把 "编号 -> 状态名" 通过 ``StateMap`` 消息不断发过来,
状态名形如 ``Control=Swoop Antic`` (复合状态是若干个这样的键用 ``|`` 连起来), 它只取决于游戏
本身, 跨会话不变. 于是可以:

1. 录制时把整份 StateMap 存进示范目录;
2. 用示范的 StateMap 建一份 "状态名 -> 稳定编号" 的词表 (按名字排序, 编号 1 起, 0 表示未知);
3. 训练时拿当前会话的 StateMap 把观测里的编号查成状态名, 再查成词表编号.

这样两头看到的是同一套编号, 这类字段就能当特征用了.
"""

from __future__ import annotations

import json
import logging
from collections.abc import Iterable, Mapping
from pathlib import Path

import numpy as np

LOGGER = logging.getLogger(__name__)

# 观测里所有"会话内编号"性质的 Boss 状态字段.
STATE_FIELDS: tuple[str, ...] = (
    "boss_state_id",
    "boss_fsm0_state_id",
    "boss_fsm1_state_id",
    "boss_fsm2_state_id",
    "boss_fsm3_state_id",
)

STATE_MAP_FILE = "state-map.json"

VOCAB_FILE = "state_vocab.json"


def state_columns(field_names: list[str]) -> list[int]:
    """返回这些字段在观测向量里的列下标 (字段不存在就跳过)."""

    return [index for index, name in enumerate(field_names) if name in STATE_FIELDS]


def load_state_map(path: Path | str) -> dict[int, str]:
    """读录制时存下的 "编号 -> 状态名"; 给目录就找里面的 state-map.json."""

    target = Path(path)
    if target.is_dir():
        target = target / STATE_MAP_FILE

    with target.open("r", encoding="utf-8") as handle:
        raw = json.load(handle)

    return {int(key): str(value) for key, value in raw.items()}


def save_state_map(path: Path | str, state_map: Mapping[int, str]) -> None:
    target = Path(path)
    if target.is_dir():
        target = target / STATE_MAP_FILE

    with target.open("w", encoding="utf-8") as handle:
        json.dump({str(key): value for key, value in sorted(state_map.items())}, handle, ensure_ascii=False, indent=2)


def build_vocabulary(maps: Iterable[Mapping[int, str]]) -> dict[str, int]:
    """把若干份会话内的编号表合成一份稳定的 "状态名 -> 编号".

    按状态名排序后从 1 开始编号, 所以词表只取决于出现过哪些状态名, 与录制会话的发现顺序无关.
    同一个状态名在不同会话里被分到不同原始编号, 到了这里都会落到同一个词表编号上.
    """

    names: set[str] = set()
    for mapping in maps:
        names.update(name for name in mapping.values() if name)

    return {name: index for index, name in enumerate(sorted(names), start=1)}


def save_vocabulary(path: Path | str, vocabulary: Mapping[str, int]) -> None:
    with Path(path).open("w", encoding="utf-8") as handle:
        json.dump(dict(sorted(vocabulary.items(), key=lambda item: item[1])), handle, ensure_ascii=False, indent=2)


def load_vocabulary(path: Path | str) -> dict[str, int]:
    with Path(path).open("r", encoding="utf-8") as handle:
        return {str(key): int(value) for key, value in json.load(handle).items()}


def build_lookup(session_map: Mapping[int, str], vocabulary: Mapping[str, int], size: int) -> np.ndarray:
    """会话内编号 -> 词表编号的查表数组, 查不到的落 0."""

    table = np.zeros(max(size, 1), dtype=np.float32)
    for raw_id, name in session_map.items():
        if 0 <= raw_id < table.size:
            table[raw_id] = vocabulary.get(name, 0)

    return table


def remap_states(
    values: np.ndarray,
    columns: list[int],
    session_map: Mapping[int, str],
    vocabulary: Mapping[str, int],
) -> np.ndarray:
    """把观测里的状态编号换成词表编号, 返回新数组 (一维观测与二维样本表都支持)."""

    if not columns:
        return values

    result = np.array(values, dtype=np.float32, copy=True)
    raw = np.rint(result[..., columns]).astype(np.int64)

    size = int(raw.max()) + 1 if raw.size else 1
    for raw_id in session_map:
        size = max(size, raw_id + 1)

    table = build_lookup(session_map, vocabulary, size)
    # 原始值里的 -1 表示"没有 Boss", 不能当成编号 0 去查表 (0 是一个合法的编号).
    invalid = raw < 0
    mapped = table[np.where(invalid, 0, raw)]
    mapped[invalid] = 0.0
    result[..., columns] = mapped

    return result
