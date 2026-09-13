"""Gymnasium 环境封装.

一个环境实例对应一个游戏进程 (一条 TCP 连接). 动作是 4 维离散:
``[左右(3), 上下(3), 跳跃(2), 攻击(2)]``.
"""

from __future__ import annotations

import logging
import time
from typing import Any

import gymnasium as gym
import numpy as np
from gymnasium import spaces

from .client import EnvClient, EnvProtocolError, Observation
from .fields import build_mask
from .reward import RewardConfig, compute_reward

LOGGER = logging.getLogger(__name__)


class SilksongBossEnv(gym.Env):
    """把游戏里的 Boss 战包装成 Gymnasium 环境."""

    metadata = {"render_modes": []}

    def __init__(
        self,
        client: EnvClient,
        reward_config: RewardConfig | None = None,
        connect: bool = True,
        reconnect_attempts: int = 3,
    ) -> None:
        super().__init__()
        self.client = client
        self.reward_config = reward_config or RewardConfig()
        self.reconnect_attempts = reconnect_attempts
        self._mask = np.asarray([], dtype=np.int64)

        if connect and not client.connected:
            client.connect()

        if client.connected:
            self._configure_spaces()

        self._previous: Observation | None = None
        self._episode_steps = 0
        self._episode_reward = 0.0
        self.episode_stats: dict[str, float] = {}

        self._last_reset_seconds = 0.0

    # --- gym 接口 ---------------------------------------------------------

    def _configure_spaces(self) -> None:
        field_count = len(self.client.field_names)
        if field_count == 0:
            raise EnvProtocolError("游戏端没有上报观测字段, 无法构建观测空间")

        self.observation_space = spaces.Box(
            low=-np.inf,
            high=np.inf,
            shape=(field_count,),
            dtype=np.float32,
        )
        self.action_space = spaces.MultiDiscrete(np.asarray(self.client.action_shape, dtype=np.int64))

        # 会话相关字段 (例如 physics_frame) 在示范与训练之间取值范围不同, 清零让策略忽略它们.
        mask = build_mask(self.client.field_names)
        self._mask = np.asarray(mask, dtype=np.int64)
        if mask:
            LOGGER.info("已屏蔽观测字段: %s", ", ".join(self.client.field_names[i] for i in mask))

    def _postprocess(self, values: np.ndarray) -> np.ndarray:
        """清零被屏蔽的列, 返回给策略看的观测."""

        array = np.asarray(values, dtype=np.float32)
        if self._mask.size:
            array = array.copy()
            array[self._mask] = 0.0

        return array

    def reset(self, *, seed: int | None = None, options: dict | None = None) -> tuple[np.ndarray, dict]:
        super().reset(seed=seed)

        if not self.client.connected:
            self.client.connect()
            self._configure_spaces()

        started = time.monotonic()
        observation = self._with_reconnect(self.client.reset, "reset")
        self._last_reset_seconds = time.monotonic() - started

        self._previous = observation
        self._episode_steps = 0
        self._episode_reward = 0.0
        self.episode_stats = {
            "damage_dealt": 0.0,
            "damage_taken": 0.0,
            "boss_kills": 0.0,
            "player_deaths": 0.0,
            "reset_seconds": self._last_reset_seconds,
            # 诊断量: 只靠"造成伤害"看不出打不动的原因, 这两个数能区分
            # "根本不出手" 与 "一直挥空": 前者 attack_steps 很小, 后者挥刀多但每刀伤害低.
            "attack_steps": 0.0,
            "attack_pressed_steps": 0.0,
            "bind_pressed_steps": 0.0,
            "min_boss_distance": float("inf"),
        }

        return self._postprocess(observation.values), self._build_info(observation)

    def step(self, action) -> tuple[np.ndarray, float, bool, bool, dict]:
        observation = self._with_reconnect(lambda: self.client.step(action), "step")
        reward, components = compute_reward(self._previous, observation, self.reward_config)

        self._episode_steps += 1
        self._episode_reward += reward
        self.episode_stats["damage_dealt"] += float(observation.named.get("damage_dealt_step", 0.0))
        self.episode_stats["damage_taken"] += float(observation.named.get("damage_taken_step", 0.0))
        self.episode_stats["boss_kills"] += float(observation.named.get("boss_killed_step", 0.0))
        self.episode_stats["player_deaths"] += float(observation.named.get("player_died_step", 0.0))
        self._accumulate_swing_stats(observation, action)

        terminated = observation.terminated
        truncated = observation.truncated
        self._previous = observation

        info = self._build_info(observation)
        info["reward_components"] = components
        info["episode_reward"] = self._episode_reward
        info["reset_seconds"] = self._last_reset_seconds

        if terminated or truncated:
            self._finalize_stats()
            info["episode"] = {
                "r": self._episode_reward,
                "l": self._episode_steps,
                **self.episode_stats,
            }
            # sb3 的 Monitor(info_keywords=...) 只认 info 顶层的字段.
            info.update(self.episode_stats)

        return self._postprocess(observation.values), float(reward), terminated, truncated, info

    def close(self) -> None:
        self.client.close()

    # --- 内部 -------------------------------------------------------------

    def _accumulate_swing_stats(self, observation: Observation, action) -> None:
        """记录这一回合挥了多少刀, 真的贴到 Boss 身边时距离是多少."""

        stats = self.episode_stats
        named = observation.named

        if float(named.get("player_attacking", 0.0)) > 0.5:
            stats["attack_steps"] += 1.0

        flat = np.asarray(action).reshape(-1)
        if flat.size >= 4 and int(flat[3]) == 1:
            stats["attack_pressed_steps"] += 1.0
        if flat.size >= 5 and int(flat[4]) == 1:
            stats["bind_pressed_steps"] += 1.0

        distance = float(named.get("boss_distance_n", -1.0))
        if distance >= 0.0 and distance < stats["min_boss_distance"]:
            stats["min_boss_distance"] = distance

    def _finalize_stats(self) -> None:
        """回合结束时把区间量换算成便于比较的派生量."""

        stats = self.episode_stats
        swings = stats["attack_steps"]
        stats["damage_per_swing"] = stats["damage_dealt"] / swings if swings > 0.0 else 0.0
        if stats["min_boss_distance"] == float("inf"):
            stats["min_boss_distance"] = -1.0

    def _build_info(self, observation: Observation) -> dict[str, Any]:
        info: dict[str, Any] = {
            "named": observation.named,
            "step_index": observation.step_index,
            "boss_state": self.client.state_map.get(int(observation.named.get("boss_state_id", -1)), ""),
            "status": self.client.last_status,
        }
        return info

    def _with_reconnect(self, call, phase: str):
        """连接断了就重连再试一次: 游戏进程还在, 插件会重新接受连接."""

        attempt = 0
        while True:
            try:
                return call()
            except EnvProtocolError as exc:
                attempt += 1
                if attempt > self.reconnect_attempts:
                    LOGGER.error("%s 阶段连续失败 %d 次, 放弃: %s", phase, attempt - 1, exc)
                    raise

                LOGGER.warning("%s 阶段出错 (%s), 第 %d 次重连", phase, exc, attempt)
                self.client.close()
                time.sleep(1.0)
                self.client.connect()
                self._configure_spaces()
