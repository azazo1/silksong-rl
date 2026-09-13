"""行为克隆: 用人类示范先把策略拉到"会打"的水平, 再交给 PPO 微调.

产物是一个标准的 stable-baselines3 PPO 模型, 可以直接用于评估, 也可以用 train.py 的
`--resume` 继续做强化学习微调.

用法::

    uv run silksong-bc --data records/moss-mother
    uv run silksong-bc --data records/moss-mother --epochs 40 --out runs/bc-moss
"""

from __future__ import annotations

import argparse
import logging
import sys
import time
from pathlib import Path

import gymnasium as gym
import numpy as np
import torch
from gymnasium import spaces
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize

from . import protocol
from .dataset import load_demonstrations

LOGGER = logging.getLogger("silksong_rl.bc")

# 观测里既有世界坐标 (几十), 也有比例 (0 到 1), 必须归一化后网络才学得动.
# 这里用的裁剪范围与 stable-baselines3 的 VecNormalize 默认值一致.
CLIP_OBS = 10.0


class SpecEnv(gym.Env):
    """只提供 observation_space / action_space 的空环境, 给 PPO 建网络用."""

    def __init__(self, obs_dim: int, action_shape: tuple[int, ...]) -> None:
        super().__init__()
        self.observation_space = spaces.Box(low=-np.inf, high=np.inf, shape=(obs_dim,), dtype=np.float32)
        self.action_space = spaces.MultiDiscrete(np.asarray(action_shape, dtype=np.int64))

    def reset(self, *, seed=None, options=None):  # pragma: no cover - 不会真的被调用
        return np.zeros(self.observation_space.shape, dtype=np.float32), {}

    def step(self, action):  # pragma: no cover
        raise NotImplementedError


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="用人类示范做行为克隆")
    parser.add_argument("--data", required=True, help="示范数据目录或单个 .npz")
    parser.add_argument("--out", default="runs/bc", help="输出目录")
    parser.add_argument("--epochs", type=int, default=30, help="训练轮数")
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--learning-rate", type=float, default=3e-4)
    parser.add_argument("--val-ratio", type=float, default=0.1, help="留出多少比例做验证")
    parser.add_argument("--net-arch", default="256,256")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--verbose", action="store_true")
    return parser


def main() -> None:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    args = build_parser().parse_args()
    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    observations, actions = load_demonstrations(args.data)
    LOGGER.info("载入示范 %d 条, 观测 %d 维, 动作 %d 维", len(observations), observations.shape[1], actions.shape[1])

    # 用示范数据本身的均值方差做观测归一化, 并把统计量随模型一起存下来,
    # 这样 PPO 微调时用同一份 VecNormalize, 网络看到的输入分布是一致的.
    obs_mean = observations.mean(axis=0)
    obs_var = observations.var(axis=0)
    obs_std = np.sqrt(np.maximum(obs_var, 1e-8))
    normalized = np.clip((observations - obs_mean) / obs_std, -CLIP_OBS, CLIP_OBS).astype(np.float32)

    generator = np.random.default_rng(args.seed)
    indices = generator.permutation(len(normalized))
    val_count = int(len(indices) * args.val_ratio)
    val_indices = indices[:val_count]
    train_indices = indices[val_count:]

    obs_train = torch.as_tensor(normalized[train_indices], dtype=torch.float32)
    act_train = torch.as_tensor(actions[train_indices], dtype=torch.long)
    obs_val = torch.as_tensor(normalized[val_indices], dtype=torch.float32) if val_count else None
    act_val = torch.as_tensor(actions[val_indices], dtype=torch.long) if val_count else None

    net_arch = [int(width) for width in args.net_arch.split(",") if width.strip()]
    spec_env = SpecEnv(observations.shape[1], tuple(int(x) for x in protocol.ACTION_SHAPE))
    model = PPO(
        "MlpPolicy",
        spec_env,
        learning_rate=args.learning_rate,
        policy_kwargs={"net_arch": net_arch},
        seed=args.seed,
        verbose=0,
    )

    policy = model.policy
    policy.train()
    device = policy.device
    LOGGER.info("训练设备: %s", device)
    optimizer = torch.optim.Adam(policy.parameters(), lr=args.learning_rate)
    batch_size = max(1, min(args.batch_size, len(obs_train)))

    # 网络在 cuda 上时, 数据也要跟过去.
    obs_train = obs_train.to(device)
    act_train = act_train.to(device)
    if obs_val is not None:
        obs_val = obs_val.to(device)
        act_val = act_val.to(device)

    output_dir = Path(args.out)
    output_dir.mkdir(parents=True, exist_ok=True)

    started = time.monotonic()
    for epoch in range(1, args.epochs + 1):
        permutation = torch.randperm(len(obs_train), device=device)
        total_loss = 0.0
        batches = 0
        for start in range(0, len(obs_train), batch_size):
            batch = permutation[start : start + batch_size]
            obs_batch = obs_train[batch]
            act_batch = act_train[batch]

            distribution = policy.get_distribution(obs_batch)
            log_prob = distribution.log_prob(act_batch)
            loss = -log_prob.mean()

            optimizer.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(policy.parameters(), 1.0)
            optimizer.step()

            total_loss += float(loss.item())
            batches += 1

        if epoch % 5 == 0 or epoch == args.epochs:
            message = f"第 {epoch}/{args.epochs} 轮: 训练损失 {total_loss / max(1, batches):.4f}"
            if obs_val is not None and len(obs_val):
                with torch.no_grad():
                    distribution = policy.get_distribution(obs_val)
                    val_loss = -distribution.log_prob(act_val).mean().item()
                    predicted = distribution.get_actions(deterministic=True)
                    accuracy = float((predicted == act_val).float().mean().item())
                message += f", 验证损失 {val_loss:.4f}, 单维准确率 {accuracy:.3f}"
            LOGGER.info(message)

    model.save(str(output_dir / "bc.zip"))
    save_normalizer(output_dir / "vecnormalize.pkl", observations.shape[1], obs_mean, obs_var, len(normalized))

    LOGGER.info("行为克隆完成, 用时 %.1f 分钟", (time.monotonic() - started) / 60.0)
    LOGGER.info("模型已保存: %s", (output_dir / "bc.zip").resolve())
    LOGGER.info("归一化统计已保存: %s", (output_dir / "vecnormalize.pkl").resolve())
    LOGGER.info("下一步: uv run silksong-train --resume %s --timesteps 200000", output_dir / "bc.zip")


def save_normalizer(path: Path, obs_dim: int, mean: np.ndarray, var: np.ndarray, count: int) -> None:
    """存一份与训练侧完全一致的 VecNormalize 统计, 供 PPO 微调时继续使用."""

    spec_env = SpecEnv(obs_dim, tuple(int(x) for x in protocol.ACTION_SHAPE))
    vec_env = DummyVecEnv([lambda: spec_env])
    normalizer = VecNormalize(vec_env, norm_obs=True, norm_reward=False, clip_obs=CLIP_OBS)
    normalizer.obs_rms.mean = np.asarray(mean, dtype=np.float64)
    normalizer.obs_rms.var = np.asarray(var, dtype=np.float64)
    normalizer.obs_rms.count = float(count)
    normalizer.save(str(path))
    vec_env.close()


if __name__ == "__main__":
    main()
