"""联调冒烟脚本: 连上游戏, 重置一个回合, 跑若干步随机动作.

用来在没有训练的情况下确认"游戏 ↔ 插件 ↔ 训练侧"整条链路是通的, 并且能看到每一帧
观测的实际取值. 需要游戏已经在跑, 并且装有 RLEnv 插件.

用法::

    uv run silksong-smoke
    uv run silksong-smoke --steps 40 --seed 0 --policy scripted
"""

from __future__ import annotations

import argparse
import logging
import sys
import time

import numpy as np

from .client import EnvClient
from .env import SilksongBossEnv
from .reward import RewardConfig

LOGGER = logging.getLogger("silksong_rl.smoke")

INTERESTING_FIELDS = (
    "player_pos_x_world",
    "player_pos_y_world",
    "player_vel_x",
    "player_vel_y",
    "player_on_ground",
    "player_attacking",
    "player_health",
    "player_silk",
    "boss_alive",
    "boss_health",
    "boss_pos_x_world",
    "boss_pos_y_world",
    "boss_distance_n",
    "enemy_count",
    "hazard_count",
    "physics_frame",
)


def describe(named: dict, state_map: dict[int, str], step_index: int) -> str:
    parts = [f"步 {step_index}"]
    for field in INTERESTING_FIELDS:
        if field in named:
            parts.append(f"{field}={named[field]:.2f}")

    state_id = int(named.get("boss_state_id", -1))
    parts.append(f"boss_state={state_map.get(state_id, state_id)}")
    return "  ".join(parts)


def scripted_action(step: int) -> tuple[int, int, int, int, int]:
    """一段固定动作序列: 先右移, 再跳, 再攻击, 用来看动作注入是否真的生效."""

    phase = (step // 5) % 4
    if phase == 0:
        return (2, 0, 0, 0, 0)
    if phase == 1:
        return (2, 0, 1, 0, 0)
    if phase == 2:
        return (2, 0, 0, 1, 0)
    return (0, 0, 0, 0, 0)


def approach_action(named: dict, step: int) -> tuple[int, int, int, int, int]:
    """朝 Boss 走过去并挥针: 用观测里的相对位置决定方向, 顺便验证闭环能打中."""

    if not named:
        return (0, 0, 0, 0, 0)

    alive = float(named.get("boss_alive", 0.0)) > 0.5
    if not alive:
        return (0, 0, 0, 0, 0)

    relative_x = float(named.get("boss_rel_x_n", 0.0))
    horizontal = 1 if relative_x < -0.05 else (2 if relative_x > 0.05 else 0)
    attack = 1 if step % 3 == 0 else 0
    jump = 1 if step % 17 == 0 else 0
    vertical = 1 if abs(relative_x) < 0.15 and step % 3 == 1 else 0
    bind = 1 if float(named.get("player_health", 5)) <= 2 and float(named.get("player_silk", 0)) >= 9 else 0
    return (horizontal, 2 - vertical if vertical else 0, jump, attack, bind)


def main() -> None:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="RLEnv 链路冒烟测试")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5555)
    parser.add_argument("--steps", type=int, default=30, help="最多跑多少步")
    parser.add_argument("--resets", type=int, default=2, help="先连续重置几次, 用来对比首次与后续重置耗时")
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--policy", choices=("random", "scripted", "approach"), default="random")
    parser.add_argument("--speed", type=float, default=3.0, help="执行动作时的时间倍率")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    client = EnvClient(host=args.host, port=args.port, connect_timeout=180.0, reset_timeout=180.0)
    client.connect()
    client.set_speed(args.speed)

    env = SilksongBossEnv(client, reward_config=RewardConfig())
    rng = np.random.default_rng(args.seed)

    LOGGER.info("观测字段 %d 个, 动作形状 %s, 每步 %d 物理帧", len(client.field_names), env.action_space.nvec, client.step_frames)

    started = time.monotonic()
    observation, info = env.reset()
    LOGGER.info("第 1 次重置耗时 %.2f 秒", time.monotonic() - started)
    LOGGER.info(describe(info["named"], client.state_map, info["step_index"]))

    # 再重置几次, 用来观察"已在游戏内"的精简重置路径耗时.
    for index in range(2, args.resets + 1):
        reset_started = time.monotonic()
        observation, info = env.reset()
        LOGGER.info("第 %d 次重置耗时 %.2f 秒", index, time.monotonic() - reset_started)
        LOGGER.info(describe(info["named"], client.state_map, info["step_index"]))

    total_reward = 0.0
    stepped = 0
    previous_named: dict = info["named"]
    for step in range(args.steps):
        if args.policy == "random":
            action = tuple(int(x) for x in rng.integers(env.action_space.nvec))
        elif args.policy == "scripted":
            action = scripted_action(step)
        else:
            action = approach_action(previous_named, step)

        step_started = time.monotonic()
        observation, reward, terminated, truncated, info = env.step(action)
        previous_named = info["named"]
        total_reward += reward
        stepped += 1
        LOGGER.info(
            "[%s] %s  血量 %s  奖励 %+.3f  单步耗时 %.3f 秒",
            "/".join(str(x) for x in action),
            describe(info["named"], client.state_map, info["step_index"]),
            f"{info['named'].get('boss_health', 0):.0f}",
            reward,
            time.monotonic() - step_started,
        )

        if terminated or truncated:
            LOGGER.info("回合结束: %s, 累计奖励 %.2f, 步数 %d", "终止" if terminated else "截断", total_reward, stepped)
            break

    LOGGER.info("冒烟结束: %d 步, 累计奖励 %.2f, 平均每步 %.3f 秒", stepped, total_reward, (time.monotonic() - started) / max(1, stepped))
    env.close()


if __name__ == "__main__":
    main()
