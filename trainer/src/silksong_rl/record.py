"""人类示范录制: 你正常打, 插件把 (观测, 你真实按下的键) 成对流过来落盘.

用法::

    uv run silksong-record --episodes 5 --out records/moss-mother

游戏侧要求: 插件已装好, 游戏在主菜单或任意存档里, 训练侧没有别的进程连着.
脚本会自己把 Boss 房载进去, 之后交给你操作; 每回合你死了或打赢了都会自动重开下一回合.
"""

from __future__ import annotations

import argparse
import logging
import sys
import time
from pathlib import Path

import numpy as np

from .client import EnvClient
from . import protocol
from .fields import find_cumulative_event_columns
from .state_ids import save_state_map

LOGGER = logging.getLogger("silksong_rl.record")


def main() -> None:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="录制人类示范")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5555)
    parser.add_argument("--episodes", type=int, default=3, help="录几个回合")
    parser.add_argument("--out", default="records/demo", help="输出前缀, 每回合一个 .npz")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    output_dir = Path(args.out)
    output_dir.mkdir(parents=True, exist_ok=True)

    # 状态机编号是每个会话重新分配的, 所以这份 "编号 -> 状态名" 必须跟示范一起落盘,
    # 否则 boss_state_id 这类字段事后无法跨会话对齐, 只能整列丢掉.
    session_id = time.strftime("%Y%m%d-%H%M%S")
    state_map_name = f"state-map-{session_id}.json"

    client = EnvClient(host=args.host, port=args.port, connect_timeout=180.0, reset_timeout=180.0)
    client.connect()

    total_samples = 0
    for episode in range(1, args.episodes + 1):
        LOGGER.info("=== 第 %d / %d 回合: 准备 Boss 房 ===", episode, args.episodes)
        client.reset()
        client.set_human_mode(True)
        LOGGER.info("现在轮到你操作了, 打完这一局 (死亡或击杀都会自动结束)")

        observations: list[np.ndarray] = []
        actions: list[list[int]] = []
        started = time.monotonic()
        last_report = started
        interrupted = False

        try:
            while True:
                kind, payload = client.read_message(timeout=None)
                if kind is protocol.MessageType.RECORD:
                    step_index, values, action = payload  # type: ignore[misc]
                    observations.append(np.asarray(values, dtype=np.float32))
                    actions.append(list(action))
                    now = time.monotonic()
                    if now - last_report >= 5.0:
                        last_report = now
                        LOGGER.info(
                            "已录 %d 个样本 (%.1f 秒), 主角血量 %.0f, Boss 血量 %.0f",
                            len(observations),
                            now - started,
                            pick(values, client.field_names, "player_health"),
                            pick(values, client.field_names, "boss_health"),
                        )
                elif kind is protocol.MessageType.OBSERVATION:
                    _, flags, _ = payload  # type: ignore[misc]
                    if flags & (protocol.FLAG_TERMINATED | protocol.FLAG_TRUNCATED):
                        break
                elif kind is protocol.MessageType.STATUS:
                    LOGGER.debug("游戏端状态: %s", payload)
                elif kind is protocol.MessageType.STATE_MAP:
                    client.merge_state_map(payload)
                elif kind is protocol.MessageType.ERROR:
                    LOGGER.warning("游戏端报错: %s", payload)
        except KeyboardInterrupt:
            # 中途 Ctrl+C 时把已经录到的样本存下来, 不白费这一局.
            interrupted = True
            LOGGER.warning("收到中断, 保存已录到的 %d 个样本", len(observations))

        client.set_human_mode(False)

        if observations:
            path = output_dir / f"episode-{episode:03d}.npz"
            observations_array = np.stack(observations)
            np.savez_compressed(
                path,
                obs=observations_array,
                action=np.asarray(actions, dtype=np.int64),
                fields=np.asarray(client.field_names),
                # 这一局属于哪份状态映射 (每一局都重写一次, 中途崩了也不至于全丢).
                state_map=np.asarray(state_map_name),
            )
            save_state_map(output_dir / state_map_name, client.state_map)
            warn_if_cumulative_damage(observations_array, client.field_names)
            total_samples += len(observations)
            LOGGER.info("第 %d 回合已保存 %d 个样本: %s", episode, len(observations), path)
        else:
            LOGGER.warning("第 %d 回合没有录到样本", episode)

        if interrupted:
            break

    client.close()
    LOGGER.info("录制结束, 共 %d 个样本, 输出目录 %s", total_samples, output_dir.resolve())
    LOGGER.info("Boss 状态映射已存: %s (%d 项)", state_map_name, len(client.state_map))
    LOGGER.info("下一步: uv run silksong-bc --data %s", output_dir)


def warn_if_cumulative_damage(observations: np.ndarray, field_names: list[str]) -> None:
    """示范里的"本步造成伤害"必须是每步增量.

    插件曾经在录制路径上漏了一次按步清零, 于是这一列变成整局的累计值, 而训练时它是增量,
    行为克隆学到的输入分布就和上场时看到的对不上. 这里做个廉价的一致性检查.
    """

    for index in find_cumulative_event_columns(observations, field_names):
        LOGGER.warning(
            "%s 整局单调不减 (0 -> %.0f), 看起来是累计值而不是每步增量: 插件录制路径的按步清零可能没生效",
            field_names[index],
            observations[:, index].max(),
        )


def pick(values, field_names: list[str], name: str) -> float:
    """按名字取一个观测值, 字段不存在时返回 -1."""

    if name not in field_names:
        return -1.0

    index = field_names.index(name)
    return float(values[index]) if index < len(values) else -1.0


if __name__ == "__main__":
    main()
