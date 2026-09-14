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
from .state_ids import VOCAB_FILE, save_vocabulary

LOGGER = logging.getLogger("silksong_rl.bc")

# 观测里既有世界坐标 (几十), 也有比例 (0 到 1), 必须归一化后网络才学得动.
# 这里用的裁剪范围与 stable-baselines3 的 VecNormalize 默认值一致.
CLIP_OBS = 10.0

# 动作维度的中文名, 只用于日志.
ACTION_LABELS = ("左右", "上下", "跳跃", "攻击", "缚丝")


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
    parser.add_argument("--weight-decay", type=float, default=1e-4, help="权重衰减, 示范数据少时能压一压过拟合")
    parser.add_argument("--val-ratio", type=float, default=0.1, help="留出多少比例做验证")
    parser.add_argument("--patience", type=int, default=30, help="验证损失连续多少轮不改善就提前停 (0 表示不启用)")
    parser.add_argument(
        "--class-weight-power",
        type=float,
        default=0.5,
        help="类别加权强度: 权重 = (1/频率)^power, 用来对付'跳跃/攻击/缚丝几乎不出手'的塌缩, 0 表示不加权",
    )
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

    run_bc(args)


def run_bc(args) -> Path:
    """跑一遍行为克隆, 返回模型路径 (自检要能直接调用, 所以和命令行解析分开)."""

    observations, actions, state_vocabulary = load_demonstrations(args.data)
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
    optimizer = torch.optim.Adam(policy.parameters(), lr=args.learning_rate, weight_decay=args.weight_decay)
    batch_size = max(1, min(args.batch_size, len(obs_train)))

    weights = build_class_weights(actions[train_indices], args.class_weight_power)
    if weights is not None:
        weights = weights.to(device)
        LOGGER.info("类别加权已启用 (power=%s): 每个动作维度内权重归一到均值 1", args.class_weight_power)

    # 网络在 cuda 上时, 数据也要跟过去.
    obs_train = obs_train.to(device)
    act_train = act_train.to(device)
    if obs_val is not None:
        obs_val = obs_val.to(device)
        act_val = act_val.to(device)

    output_dir = Path(args.out)
    output_dir.mkdir(parents=True, exist_ok=True)

    best_loss = float("inf")
    best_state: dict | None = None
    best_epoch = 0
    stale_epochs = 0

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
            per_dim_log_prob = per_dim_log_probability(distribution, act_batch)
            if weights is not None:
                sample_weights = gather_class_weights(weights, act_batch)
                loss = -(per_dim_log_prob * sample_weights).sum(dim=1).mean() / per_dim_log_prob.shape[1]
            else:
                loss = -per_dim_log_prob.mean()

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
                    per_dim = (predicted == act_val).float().mean(dim=0).tolist()
                message += f", 验证损失 {val_loss:.4f}, 单维准确率 {accuracy:.3f}"
                message += " [" + " ".join(
                    f"{label}:{value:.2f}" for label, value in zip(ACTION_LABELS, per_dim, strict=False)
                ) + "]"
                # "按下去" 的召回率才是行为层面的关键: 这三个键不出手就打不死 Boss.
                recalls = []
                for dim, label in enumerate(ACTION_LABELS):
                    pressed = act_val[:, dim] == 1
                    if bool(pressed.any()):
                        hit = (predicted[:, dim][pressed] == 1).float().mean().item()
                        recalls.append(f"{label}按下:{hit:.2f}")
                if recalls:
                    message += " [" + " ".join(recalls) + "]"
            LOGGER.info(message)

        # 按验证损失留最好的那一版权重: 示范数据通常只有几千条, 训练久了会过拟合.
        if obs_val is not None and len(obs_val):
            with torch.no_grad():
                distribution = policy.get_distribution(obs_val)
                current_loss = float(-distribution.log_prob(act_val).mean().item())

            if current_loss < best_loss - 1e-4:
                best_loss = current_loss
                best_epoch = epoch
                best_state = {key: value.detach().clone() for key, value in policy.state_dict().items()}
                stale_epochs = 0
            else:
                stale_epochs += 1
                if args.patience > 0 and stale_epochs >= args.patience:
                    LOGGER.info("验证损失连续 %d 轮没有改善, 提前停在第 %d 轮", stale_epochs, epoch)
                    break

    if best_state is not None:
        policy.load_state_dict(best_state)
        LOGGER.info("已回滚到验证损失最低的第 %d 轮 (验证损失 %.4f)", best_epoch, best_loss)

    model.save(str(output_dir / "bc.zip"))
    save_normalizer(output_dir / "vecnormalize.pkl", observations.shape[1], obs_mean, obs_var, len(normalized))

    if state_vocabulary:
        # 训练侧必须用同一份词表把当前会话的状态编号映射过来, 否则那些列的含义又对不上了.
        save_vocabulary(output_dir / VOCAB_FILE, state_vocabulary)
        LOGGER.info("Boss 状态词表已保存: %s (%d 项)", (output_dir / VOCAB_FILE).resolve(), len(state_vocabulary))

    LOGGER.info("行为克隆完成, 用时 %.1f 分钟", (time.monotonic() - started) / 60.0)
    LOGGER.info("模型已保存: %s", (output_dir / "bc.zip").resolve())
    LOGGER.info("归一化统计已保存: %s", (output_dir / "vecnormalize.pkl").resolve())
    LOGGER.info("下一步: uv run silksong-train --resume %s --finetune --timesteps 200000", output_dir / "bc.zip")

    return output_dir / "bc.zip"


def build_class_weights(actions: np.ndarray, power: float):
    """按每个动作维度的类别频率算权重: (1/频率)^power, 再在维度内归一到均值 1.

    示范里 "不按" 占绝大多数 (跳跃 74%, 攻击 81%, 缚丝 98%), 不加权时模型倾向于永远输出多数类,
    表现出来就是智能体上场后几乎不出手.
    """

    if power <= 0.0:
        return None

    dims = actions.shape[1]
    weights = np.zeros((dims, max(int(actions.max()) + 1, 2)), dtype=np.float32)
    for dim in range(dims):
        counts = np.bincount(actions[:, dim], minlength=weights.shape[1]).astype(np.float64)
        counts = np.maximum(counts, 1.0)
        frequency = counts / counts.sum()
        raw = np.power(1.0 / frequency, power)
        weights[dim] = (raw / raw.mean()).astype(np.float32)

    return torch.as_tensor(weights, dtype=torch.float32)


def gather_class_weights(weights: torch.Tensor, actions: torch.Tensor) -> torch.Tensor:
    """取出每个样本在每个维度上的权重, 形状 [B, 维度]."""

    return torch.stack([weights[dim][actions[:, dim]] for dim in range(actions.shape[1])], dim=1)


def per_dim_log_probability(distribution, actions: torch.Tensor) -> torch.Tensor:
    """MultiCategorical 分布下每个动作维度各自的对数概率, 形状 [B, 维度]."""

    categoricals = getattr(distribution, "distribution", None)
    if categoricals is None:
        return distribution.log_prob(actions).unsqueeze(1)

    return torch.stack(
        [categorical.log_prob(actions[:, dim]) for dim, categorical in enumerate(categoricals)],
        dim=1,
    )


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
