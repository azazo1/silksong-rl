"""把人类示范当"软先验"喂进 PPO.

参考的同类项目都会这么干: 世界模型那套留了 ``DemonstrationBatchSize`` / ``UseDemonstration``
的开关, 像素 DQN 那套直接给关键动作加了先验奖励. 我们这里的用途很具体 —— RL 一直没学会
"远离 Boss 时要朝它跑": 从轨迹里量出来, 策略有 80% 的步数待在远处, 其中四成原地不动、
六成还在按住跳跃, 远离时的接近速度只有人类的 1/11. 示范数据里全是这种移动习惯,
所以每次 rollout 结束后额外做一步模仿梯度, 权重给小一点, 只当先验, 不压制 RL 自己探索.
"""

from __future__ import annotations

import logging
from pathlib import Path

import numpy as np
import torch
from stable_baselines3.common.callbacks import BaseCallback

from .dataset import load_demonstrations

LOGGER = logging.getLogger("silksong_rl.imitation")


def load_demo_arrays(directories: str | Path | list[str | Path]) -> tuple[np.ndarray, np.ndarray]:
    """读示范 (人类录像或策略自己的击杀轨迹), 按训练侧同一套规则处理观测.

    可以给多个目录: 例如人类示范一份, 加上策略自己打出来的击杀轨迹一份 —— 后者是"自模仿",
    状态分布比人类示范更贴当前策略, 格式完全一样所以能混在一起喂.
    """

    paths = directories if isinstance(directories, (list, tuple)) else [directories]
    observations: list[np.ndarray] = []
    actions: list[np.ndarray] = []
    used: list[str] = []
    for path in paths:
        target = Path(path)
        # 自模仿目录在第一局击杀之前是空的, 这种源直接跳过 (不能因为还没攒到数据就起不来).
        if target.is_dir() and not any(target.glob("*.npz")):
            LOGGER.info("示范源还没有数据, 跳过: %s", target)
            continue
        obs, act, _ = load_demonstrations(target)
        observations.append(obs)
        actions.append(act)
        used.append(str(target))

    if not observations:
        raise ValueError(f"{paths} 里没有可用的示范样本")

    all_obs = np.concatenate(observations)
    all_act = np.concatenate(actions)

    LOGGER.info(
        "示范先验: %s 共 %d 条样本, 动作维度 %d",
        " + ".join(used),
        len(all_obs),
        all_act.shape[1],
    )
    return all_obs, all_act


class DemoAnchorCallback(BaseCallback):
    """每个 rollout 结束后, 在示范样本上补一步模仿梯度 (负对数似然)."""

    def __init__(
        self,
        observations: np.ndarray,
        actions: np.ndarray,
        weight: float = 0.1,
        batch_size: int = 256,
        verbose: int = 0,
    ) -> None:
        super().__init__(verbose)
        self._observations = np.asarray(observations, dtype=np.float32)
        self._actions = np.asarray(actions, dtype=np.int64)
        self._weight = float(weight)
        self._batch_size = max(int(batch_size), 1)

    def _on_step(self) -> bool:
        return True

    def _on_rollout_end(self) -> None:
        if self._weight == 0.0 or len(self._observations) == 0:
            return

        count = min(self._batch_size, len(self._observations))
        index = np.random.randint(0, len(self._observations), size=count)
        # 示范里存的是原始观测, 策略看到的是归一化之后的, 这里必须走同一个归一化器.
        vec_env = self.model.get_env()
        raw = self._observations[index]
        normalized = vec_env.normalize_obs(raw) if hasattr(vec_env, "normalize_obs") else raw

        device = self.model.policy.device
        obs_tensor = torch.as_tensor(np.asarray(normalized, dtype=np.float32), device=device)
        action_tensor = torch.as_tensor(self._actions[index], device=device)

        distribution = self.model.policy.get_distribution(obs_tensor)
        # 多维离散的 log_prob 已经把各维求和了, 这里只要对 batch 取均值.
        log_prob = distribution.log_prob(action_tensor).mean()
        loss = -self._weight * log_prob

        self.model.policy.optimizer.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(self.model.policy.parameters(), self.model.max_grad_norm)
        self.model.policy.optimizer.step()

        self.logger.record("train/demo_nll", float(-log_prob.item()))
