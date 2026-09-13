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
        }

        return observation.values.astype(np.float32), self._build_info(observation)

    def step(self, action) -> tuple[np.ndarray, float, bool, bool, dict]:
        observation = self._with_reconnect(lambda: self.client.step(action), "step")
        reward, components = compute_reward(self._previous, observation, self.reward_config)

        self._episode_steps += 1
        self._episode_reward += reward
        self.episode_stats["damage_dealt"] += float(observation.named.get("damage_dealt_step", 0.0))
        self.episode_stats["damage_taken"] += float(observation.named.get("damage_taken_step", 0.0))
        self.episode_stats["boss_kills"] += float(observation.named.get("boss_killed_step", 0.0))
        self.episode_stats["player_deaths"] += float(observation.named.get("player_died_step", 0.0))

        terminated = observation.terminated
        truncated = observation.truncated
        self._previous = observation

        info = self._build_info(observation)
        info["reward_components"] = components
        info["episode_reward"] = self._episode_reward
        info["reset_seconds"] = self._last_reset_seconds

        if terminated or truncated:
            info["episode"] = {
                "r": self._episode_reward,
                "l": self._episode_steps,
                **self.episode_stats,
            }
            # sb3 的 Monitor(info_keywords=...) 只认 info 顶层的字段.
            info.update(self.episode_stats)

        return observation.values.astype(np.float32), float(reward), terminated, truncated, info

    def close(self) -> None:
        self.client.close()

    # --- 内部 -------------------------------------------------------------

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
