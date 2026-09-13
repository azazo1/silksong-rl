"""训练入口: 用 stable-baselines3 的 PPO 训练丝之歌 Boss 战策略.

用法示例::

    uv run silksong-train --timesteps 200000
    uv run silksong-train --timesteps 100000 --port 5555 --speed 4 --run-name moss-mother-a
    uv run silksong-train --eval --model runs/moss-mother-a/final.zip --episodes 5
"""

from __future__ import annotations

import argparse
import logging
import os
import time
from pathlib import Path

import numpy as np
from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import BaseCallback, CheckpointCallback
from stable_baselines3.common.monitor import Monitor
from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize

from . import __version__
from .client import EnvClient
from .env import SilksongBossEnv
from .reward import RewardConfig

LOGGER = logging.getLogger("silksong_rl.train")


class ProgressCallback(BaseCallback):
    """按固定步数间隔输出训练进度: 速度, 回合数, 平均回报, 胜负与伤害统计."""

    def __init__(self, log_interval: int = 2000, window: int = 20) -> None:
        super().__init__()
        self.log_interval = log_interval
        self.window = window
        self._started = time.monotonic()
        self._last_report_step = 0
        self._last_report_time = self._started
        self._recent_returns: list[float] = []
        self._recent_lengths: list[int] = []
        self._wins = 0
        self._losses = 0
        self._timeouts = 0
        self._episodes = 0
        self._damage_dealt = 0.0
        self._damage_taken = 0.0

    def _on_step(self) -> bool:
        infos = self.locals.get("infos") or []
        for info in infos:
            episode = info.get("episode")
            if episode is None:
                continue

            self._episodes += 1
            self._recent_returns.append(float(episode["r"]))
            self._recent_lengths.append(int(episode["l"]))
            self._recent_returns = self._recent_returns[-self.window :]
            self._recent_lengths = self._recent_lengths[-self.window :]
            self._damage_dealt += float(episode.get("damage_dealt", 0.0))
            self._damage_taken += float(episode.get("damage_taken", 0.0))

            if episode.get("boss_kills", 0.0) > 0.0:
                self._wins += 1
            elif episode.get("player_deaths", 0.0) > 0.0:
                self._losses += 1
            else:
                self._timeouts += 1

        if self.num_timesteps - self._last_report_step < self.log_interval:
            return True

        now = time.monotonic()
        elapsed = now - self._last_report_time
        steps = self.num_timesteps - self._last_report_step
        self._last_report_step = self.num_timesteps
        self._last_report_time = now

        mean_return = float(np.mean(self._recent_returns)) if self._recent_returns else float("nan")
        mean_length = float(np.mean(self._recent_lengths)) if self._recent_lengths else float("nan")
        LOGGER.info(
            "步数 %d | %.1f 步/秒 | 回合 %d (胜 %d / 负 %d / 超时 %d) | 近 %d 回合平均回报 %.2f, 长度 %.1f",
            self.num_timesteps,
            steps / elapsed if elapsed > 0 else 0.0,
            self._episodes,
            self._wins,
            self._losses,
            self._timeouts,
            len(self._recent_returns),
            mean_return,
            mean_length,
        )
        if self._episodes:
            LOGGER.info(
                "累计 造成伤害 %.0f, 受到伤害 %.0f",
                self._damage_dealt,
                self._damage_taken,
            )

        for key, value in (
            ("rollout/ep_rew_mean_custom", mean_return),
            ("rollout/ep_len_mean_custom", mean_length),
            ("rollout/wins", float(self._wins)),
            ("rollout/losses", float(self._losses)),
            ("rollout/timeouts", float(self._timeouts)),
            ("rollout/damage_dealt", self._damage_dealt),
            ("rollout/damage_taken", self._damage_taken),
        ):
            if value == value:  # 过滤 NaN
                self.logger.record(key, value)

        return True


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="丝之歌 Boss 强化学习训练")
    parser.add_argument("--host", default="127.0.0.1", help="游戏端地址")
    parser.add_argument("--port", type=int, default=5555, help="游戏端端口")
    parser.add_argument("--timesteps", type=int, default=200_000, help="总训练步数")
    parser.add_argument("--run-name", default=None, help="实验名, 默认按时间戳生成")
    parser.add_argument("--runs-dir", default="runs", help="输出目录")
    parser.add_argument("--resume", default=None, help="从 checkpoint 继续训练 (.zip)")
    parser.add_argument("--vecnormalize", default=None, help="继续训练时载入的 VecNormalize 统计文件")

    parser.add_argument("--speed", type=float, default=4.0, help="游戏内时间倍率, 只影响采集速度")
    parser.add_argument("--n-steps", type=int, default=1024, help="PPO 每次采样的步数")
    parser.add_argument("--batch-size", type=int, default=256, help="PPO 批大小")
    parser.add_argument("--learning-rate", type=float, default=3e-4, help="学习率")
    parser.add_argument("--gamma", type=float, default=0.99, help="折扣因子")
    parser.add_argument("--ent-coef", type=float, default=0.01, help="熵系数")
    parser.add_argument("--net-arch", default="256,256", help="策略网络层宽, 例如 256,256")

    parser.add_argument("--damage-dealt", type=float, default=1.0, help="每点对 Boss 伤害的奖励")
    parser.add_argument("--damage-taken", type=float, default=-1.0, help="每次受伤的奖励")
    parser.add_argument("--boss-kill", type=float, default=25.0, help="击杀 Boss 的奖励")
    parser.add_argument("--player-death", type=float, default=-25.0, help="阵亡的奖励")
    parser.add_argument("--step-penalty", type=float, default=-0.002, help="每步固定惩罚")
    parser.add_argument("--approach", type=float, default=0.0, help="靠近 Boss 的塑形系数")

    parser.add_argument("--checkpoint-every", type=int, default=20_000, help="每多少步存一次 checkpoint")
    parser.add_argument("--log-interval", type=int, default=2_000, help="每多少步打印一次进度")
    parser.add_argument("--seed", type=int, default=1, help="随机种子")

    parser.add_argument("--eval", action="store_true", help="只评估, 需要 --model")
    parser.add_argument("--model", default=None, help="评估用的模型 (.zip)")
    parser.add_argument("--episodes", type=int, default=5, help="评估回合数")
    parser.add_argument("--verbose", action="store_true", help="输出更详细的日志")
    return parser


def setup_logging(verbose: bool) -> None:
    # Windows 控制台默认不是 UTF-8, 中文日志会变乱码.
    import sys

    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    logging.basicConfig(
        level=logging.DEBUG if verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )


def build_env(args: argparse.Namespace, reward_config: RewardConfig) -> SilksongBossEnv:
    # 游戏刚启动时插件要等 Unity 主循环跑起来才会回 Hello, 这里给足等待时间.
    client = EnvClient(host=args.host, port=args.port, connect_timeout=120.0, reset_timeout=180.0)
    client.connect()
    if args.speed > 0:
        client.set_speed(args.speed)
        LOGGER.info("已把游戏时间倍率设为 %s", args.speed)

    return SilksongBossEnv(client, reward_config=reward_config)


def run_training(args: argparse.Namespace, reward_config: RewardConfig) -> Path:
    run_name = args.run_name or time.strftime("run-%Y%m%d-%H%M%S")
    run_dir = Path(args.runs_dir) / run_name
    run_dir.mkdir(parents=True, exist_ok=True)
    LOGGER.info("实验目录: %s", run_dir.resolve())
    LOGGER.info("奖励配置: %s", reward_config.describe())

    env = build_env(args, reward_config)
    # Monitor 默认会用自己那份 episode 统计覆盖 info["episode"], 这里把自定义字段带上.
    monitored = Monitor(
        env,
        info_keywords=("damage_dealt", "damage_taken", "boss_kills", "player_deaths", "reset_seconds"),
    )
    vec_env = DummyVecEnv([lambda: monitored])
    vec_env = VecNormalize(vec_env, norm_obs=True, norm_reward=False, clip_obs=10.0)

    net_arch = [int(width) for width in args.net_arch.split(",") if width.strip()]

    if args.resume:
        LOGGER.info("从 %s 继续训练", args.resume)
        model = PPO.load(args.resume, env=vec_env, tensorboard_log=str(run_dir / "tb"))
        # 行为克隆产出的模型旁边会带一份归一化统计, 微调时必须沿用, 否则网络看到的输入分布不一致.
        normalizer_path = Path(args.vecnormalize) if args.vecnormalize else Path(args.resume).parent / "vecnormalize.pkl"
        if normalizer_path.exists():
            vec_env = VecNormalize.load(str(normalizer_path), vec_env)
            model.set_env(vec_env)
            LOGGER.info("已载入观测归一化统计: %s", normalizer_path)
        else:
            LOGGER.warning("没有找到归一化统计 (%s), 将从头统计", normalizer_path)
    else:
        model = PPO(
            "MlpPolicy",
            vec_env,
            n_steps=args.n_steps,
            batch_size=args.batch_size,
            learning_rate=args.learning_rate,
            gamma=args.gamma,
            ent_coef=args.ent_coef,
            policy_kwargs={"net_arch": net_arch},
            tensorboard_log=str(run_dir / "tb"),
            seed=args.seed,
            verbose=0,
        )

    callbacks = [ProgressCallback(log_interval=args.log_interval)]
    if args.checkpoint_every > 0:
        callbacks.append(
            CheckpointCallback(
                save_freq=args.checkpoint_every,
                save_path=str(run_dir / "checkpoints"),
                name_prefix="ppo",
            )
        )

    LOGGER.info("开始训练, 总步数 %d", args.timesteps)
    started = time.monotonic()
    model.learn(total_timesteps=args.timesteps, callback=callbacks, progress_bar=False)
    LOGGER.info("训练结束, 用时 %.1f 分钟", (time.monotonic() - started) / 60.0)

    final_path = run_dir / "final.zip"
    model.save(str(final_path))
    vec_env.save(str(run_dir / "vecnormalize.pkl"))
    LOGGER.info("模型已保存: %s", final_path.resolve())
    LOGGER.info("归一化统计已保存: %s", (run_dir / "vecnormalize.pkl").resolve())
    return final_path


def run_evaluation(args: argparse.Namespace, reward_config: RewardConfig) -> None:
    if not args.model:
        raise SystemExit("--eval 需要同时给出 --model")

    run_dir = Path(args.runs_dir) / (args.run_name or "eval")
    run_dir.mkdir(parents=True, exist_ok=True)

    env = build_env(args, reward_config)
    vec_env = DummyVecEnv([lambda: Monitor(env)])
    normalizer_path = Path(args.model).parent / "vecnormalize.pkl"
    if args.vecnormalize:
        normalizer_path = Path(args.vecnormalize)
    if normalizer_path.exists():
        vec_env = VecNormalize.load(str(normalizer_path), vec_env)
        vec_env.training = False
        vec_env.norm_reward = False
        LOGGER.info("已载入归一化统计: %s", normalizer_path)
    else:
        vec_env = VecNormalize(vec_env, norm_obs=True, norm_reward=False, training=False)

    model = PPO.load(args.model, env=vec_env)

    wins = 0
    for episode in range(1, args.episodes + 1):
        observation = vec_env.reset()
        total_reward = 0.0
        steps = 0
        while True:
            action, _ = model.predict(observation, deterministic=True)
            observation, reward, done, infos = vec_env.step(action)
            total_reward += float(reward[0])
            steps += 1
            if done[0]:
                info = infos[0]
                stats = info.get("episode", {})
                killed = stats.get("boss_kills", 0.0) > 0.0
                wins += int(killed)
                LOGGER.info(
                    "第 %d 回合: 回报 %.2f, 步数 %d, %s, 造成伤害 %.0f, 受到伤害 %.0f",
                    episode,
                    total_reward,
                    steps,
                    "击杀 Boss" if killed else ("阵亡" if stats.get("player_deaths", 0.0) > 0 else "超时"),
                    stats.get("damage_dealt", 0.0),
                    stats.get("damage_taken", 0.0),
                )
                break

    LOGGER.info("评估完成: %d/%d 回合击杀 Boss", wins, args.episodes)
    vec_env.close()


def main() -> None:
    args = build_parser().parse_args()
    setup_logging(args.verbose)
    LOGGER.info("silksong-rl %s", __version__)

    reward_config = RewardConfig(
        damage_dealt=args.damage_dealt,
        damage_taken=args.damage_taken,
        boss_kill=args.boss_kill,
        player_death=args.player_death,
        step_penalty=args.step_penalty,
        approach=args.approach,
    )

    if os.environ.get("SILKSONG_RL_DRY_RUN"):
        LOGGER.info("干跑模式: 只打印配置, 不连接游戏")
        return

    if args.eval:
        run_evaluation(args, reward_config)
    else:
        run_training(args, reward_config)


if __name__ == "__main__":
    main()
