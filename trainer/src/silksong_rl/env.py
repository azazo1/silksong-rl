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
from .reward import REFERENCE_STEP_FRAMES, RewardConfig, compute_reward, out_of_reach
from .state_ids import remap_states, state_columns

LOGGER = logging.getLogger(__name__)

# 观测里的 boss_distance_n 是"按场地半宽/半高归一化后的距离", 0.3 大约对应人贴身挥刀的距离
# (人在这段距离内的挥刀率最高), 用来统计"有多少步真的贴到了 Boss 身边".
CLOSE_DISTANCE = 0.3

# 插件旧版的回放抓帧频率 (新版会在 Hello 里上报, 训练侧也可以自己指定).
DEFAULT_CLIP_FPS = 8

# 站位诊断用的距离分档: 人类示范里 90% 的命中都发生在 0.5 以内, 只看 0.3 以内会漏掉大半.
DISTANCE_BUCKETS = (0.2, 0.3, 0.4, 0.5)

# 动作分布诊断: 人类示范里按住攻击只占 19.3%, 按住跳跃 23.5%, 下压 10.3%, 缚丝 0.8%.
# 策略如果长期把某一维压在同一个值上, 这组计数一眼就能看出来.
ACTION_TALLY_KEYS = (
    "act_h0",
    "act_h1",
    "act_h2",
    "act_v0",
    "act_v1",
    "act_v2",
    "act_jump",
    "act_attack",
    "act_bind",
)


def bucket_key(threshold: float) -> str:
    """距离分档在回合日志里的字段名, 例如 0.5 -> steps_within_05."""

    return "steps_within_{0:02d}".format(int(round(threshold * 10)))


class SilksongBossEnv(gym.Env):
    """把游戏里的 Boss 战包装成 Gymnasium 环境."""

    metadata = {"render_modes": []}

    def __init__(
        self,
        client: EnvClient,
        reward_config: RewardConfig | None = None,
        connect: bool = True,
        reconnect_attempts: int = 3,
        state_vocabulary: dict[str, int] | None = None,
    ) -> None:
        super().__init__()
        self.client = client
        self.reward_config = reward_config or RewardConfig()
        self.reconnect_attempts = reconnect_attempts
        self._mask = np.asarray([], dtype=np.int64)
        self._state_columns: list[int] = []
        self._state_vocabulary = state_vocabulary or {}

        if connect and not client.connected:
            client.connect()

        if client.connected:
            self._configure_spaces()

        self._previous: Observation | None = None
        self._episode_steps = 0
        self._episode_reward = 0.0
        self.episode_stats: dict[str, float] = {}

        self._last_reset_seconds = 0.0

        # 划水计时与上一次的面罩数 (见 _extra_rewards).
        self._steps_since_damage = 0
        self._previous_health: float | None = None

        # 一个决策步对应多少物理帧 (build_env 会按 --step-frames 覆盖), 用来按粒度折算超参.
        self.step_frames = REFERENCE_STEP_FRAMES

        # 回放抓帧频率 (build_env 会按 --clip-fps 覆盖), 合成 mp4 时要跟它对齐.
        self.clip_fps = DEFAULT_CLIP_FPS

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

        # 会话相关字段在示范与训练之间取值范围不同: 与游戏进程时长有关的那几列永远清零;
        # Boss 状态编号如果有词表就按名字重映射 (两边对齐), 没有才清零.
        using_states = bool(self._state_vocabulary)
        self._state_columns = state_columns(self.client.field_names) if using_states else []
        mask = build_mask(self.client.field_names, include_state_fields=not using_states)
        self._mask = np.asarray(mask, dtype=np.int64)
        if mask:
            LOGGER.info("已屏蔽观测字段: %s", ", ".join(self.client.field_names[i] for i in mask))
        if using_states:
            LOGGER.info(
                "Boss 状态按词表重映射 (%d 项, %d 列), 不再清零",
                len(self._state_vocabulary),
                len(self._state_columns),
            )

    @property
    def latest_raw_values(self) -> np.ndarray | None:
        """最近一次 reset/step 收到的原始观测 (未屏蔽, 未重映射状态编号).

        轨迹记录要和人类示范同口径, 所以拿原始值而不是策略看到的那份.
        """

        return None if self._previous is None else self._previous.values

    def _postprocess(self, values: np.ndarray) -> np.ndarray:
        """清零被屏蔽的列 / 重映射状态编号, 返回给策略看的观测."""

        array = np.asarray(values, dtype=np.float32)
        if self._mask.size or self._state_columns:
            array = array.copy()
            array[self._mask] = 0.0
            if self._state_columns:
                array = remap_states(array, self._state_columns, self.client.state_map, self._state_vocabulary)

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
        self._steps_since_damage = 0
        self._previous_health = float(observation.named.get("player_health", 0.0))
        self.episode_stats = {
            "damage_dealt": 0.0,
            "damage_taken": 0.0,
            "boss_kills": 0.0,
            "player_deaths": 0.0,
            "reset_seconds": self._last_reset_seconds,
            # 诊断量: 只靠"造成伤害"看不出打不动的原因, 这几个数能区分
            # "根本不出手" 与 "一直挥空": 前者 attack_steps 很小, 后者挥刀多但每刀伤害低.
            "attack_steps": 0.0,
            "attack_pressed_steps": 0.0,
            "bind_pressed_steps": 0.0,
            "close_steps": 0.0,
            "close_attack_steps": 0.0,
            "hit_steps": 0.0,
            "whiff_steps": 0.0,
            # 每个决策步对应多少游戏时间: 换过决策粒度之后, "挥刀步/命中步" 这类按步计的
            # 数字只有除以它才能跟人类示范比.
            "step_seconds": self.step_frames / 60.0,
            "inactivity_penalties": 0.0,
            "healed_masks": 0.0,
            "bind_waste_steps": 0.0,
            "min_boss_distance": float("inf"),
            "clip_dir": "",
        }
        for threshold in DISTANCE_BUCKETS:
            self.episode_stats[bucket_key(threshold)] = 0.0
        for key in ACTION_TALLY_KEYS:
            self.episode_stats[key] = 0.0

        return self._postprocess(observation.values), self._build_info(observation)

    def step(self, action) -> tuple[np.ndarray, float, bool, bool, dict]:
        observation = self._with_reconnect(lambda: self.client.step(action), "step")
        reward, components = compute_reward(self._previous, observation, self.reward_config)
        extra_reward, extra_components = self._extra_rewards(observation, action)
        reward += extra_reward
        components.update(extra_components)

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
            # 击杀那局把 mod 缓冲里的画面取回来 (没击杀就让 mod 直接丢掉, 免得长跑堆垃圾).
            killed = self.episode_stats["boss_kills"] > 0.0
            self.episode_stats["clip_dir"] = self._request_clip(killed) or ""
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
        pressed = flat.size >= 4 and int(flat[3]) == 1
        if pressed:
            stats["attack_pressed_steps"] += 1.0
        if flat.size >= 5 and int(flat[4]) == 1:
            stats["bind_pressed_steps"] += 1.0

        distance = float(named.get("boss_distance_n", -1.0))
        if distance >= 0.0:
            if distance < stats["min_boss_distance"]:
                stats["min_boss_distance"] = distance
            for threshold in DISTANCE_BUCKETS:
                if distance < threshold:
                    stats[bucket_key(threshold)] += 1.0
            if distance < CLOSE_DISTANCE:
                stats["close_steps"] += 1.0
                if pressed:
                    stats["close_attack_steps"] += 1.0

        if float(named.get("damage_dealt_step", 0.0)) > 0.0:
            stats["hit_steps"] += 1.0

        # 出刀了却够不着 (判定与奖励里的挥空惩罚同一套), 用来观察惩罚有没有把废刀压下去.
        if float(named.get("player_attacking", 0.0)) > 0.5 and out_of_reach(named):
            stats["whiff_steps"] += 1.0

        if flat.size >= 5:
            for index, value in ((0, int(flat[0])), (1, int(flat[1]))):
                key = ("act_h" if index == 0 else "act_v") + str(value)
                if key in stats:
                    stats[key] += 1.0
            if int(flat[2]) != 0:
                stats["act_jump"] += 1.0
            if int(flat[3]) != 0:
                stats["act_attack"] += 1.0
            if int(flat[4]) != 0:
                stats["act_bind"] += 1.0

    def _extra_rewards(self, observation: Observation, action) -> tuple[float, dict[str, float]]:
        """三项"行为卫生"奖励: 划水太久, 回血成功, 以及按住当前根本用不出来的缚丝.

        参考同类项目 (alvin/environment.py) 的做法: 5 秒没造成伤害罚 0.3 防止策略学会躲着不出手,
        成功回血给 0.7 (回血换来更多输出机会), 按住执行不了的动作给一点负值.
        """

        named = observation.named
        stats = self.episode_stats
        config = self.reward_config
        components = {"inactivity": 0.0, "heal": 0.0, "bind_waste": 0.0}

        # 1. 长时间没造成伤害.
        if config.inactivity_penalty != 0.0:
            if float(named.get("damage_dealt_step", 0.0)) > 0.0:
                self._steps_since_damage = 0
            else:
                self._steps_since_damage += 1
                window = max(int(round(config.inactivity_window / max(self.step_frames / 60.0, 1e-6))), 1)
                if self._steps_since_damage >= window:
                    self._steps_since_damage = 0
                    stats["inactivity_penalties"] += 1.0
                    components["inactivity"] = config.inactivity_penalty

        # 2. 面罩增加 = 回血成功.
        if config.heal_reward != 0.0:
            health = float(named.get("player_health", 0.0))
            if self._previous_health is not None and health > self._previous_health:
                gained = health - self._previous_health
                stats["healed_masks"] += gained
                components["heal"] = config.heal_reward * gained
            self._previous_health = health

        # 3. 按住缚丝但丝量不够: 人类示范里从不这样按 (策略有近四成步数在空按).
        if config.bind_waste_penalty != 0.0:
            flat = np.asarray(action).reshape(-1)
            if flat.size >= 5 and int(flat[4]) != 0:
                if float(named.get("player_silk_ratio", 1.0)) < config.bind_silk_threshold:
                    stats["bind_waste_steps"] += 1.0
                    components["bind_waste"] = config.bind_waste_penalty * config.dense_scale

        return float(sum(components.values())), components

    def _finalize_stats(self) -> None:
        """回合结束时把区间量换算成便于比较的派生量."""

        stats = self.episode_stats
        swings = stats["attack_steps"]
        presses = stats["attack_pressed_steps"]
        stats["damage_per_swing"] = stats["damage_dealt"] / swings if swings > 0.0 else 0.0
        stats["damage_per_press"] = stats["damage_dealt"] / presses if presses > 0.0 else 0.0
        if stats["min_boss_distance"] == float("inf"):
            stats["min_boss_distance"] = -1.0

    def _request_clip(self, killed: bool) -> str | None:
        """回合结束时处理 mod 侧的画面缓冲; 出错不该影响训练主流程."""

        try:
            return self.client.save_clip(killed)
        except Exception as exc:  # noqa: BLE001 - 回放只是附加功能
            LOGGER.warning("取回放片段失败: %s", exc)
            return None

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
