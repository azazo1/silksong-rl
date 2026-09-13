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
                        values[client.field_names.index("player_health")] if "player_health" in client.field_names else -1,
                        values[client.field_names.index("boss_health")] if "boss_health" in client.field_names else -1,
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

        client.set_human_mode(False)

        if observations:
            path = output_dir / f"episode-{episode:03d}.npz"
            np.savez_compressed(
                path,
                obs=np.stack(observations),
                action=np.asarray(actions, dtype=np.int64),
                fields=np.asarray(client.field_names),
            )
            total_samples += len(observations)
            LOGGER.info("第 %d 回合已保存 %d 个样本: %s", episode, len(observations), path)
        else:
            LOGGER.warning("第 %d 回合没有录到样本", episode)

    client.close()
    LOGGER.info("录制结束, 共 %d 个样本, 输出目录 %s", total_samples, output_dir.resolve())
    LOGGER.info("下一步: uv run silksong-bc --data %s", output_dir)


if __name__ == "__main__":
    main()
