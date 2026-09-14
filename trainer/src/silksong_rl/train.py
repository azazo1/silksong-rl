"""训练入口: 用 stable-baselines3 的 PPO 训练丝之歌 Boss 战策略.

用法示例::

    uv run silksong-train --timesteps 200000
    uv run silksong-train --timesteps 100000 --port 5555 --speed 4 --run-name moss-mother-a
    uv run silksong-train --eval --model runs/moss-mother-a/final.zip --episodes 5
"""

from __future__ import annotations

import argparse
import json
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
from . import trace as trace_module
from .client import EnvClient
from .clips import build_clip
from .env import DEFAULT_CLIP_FPS, SilksongBossEnv
from .fields import EXTRA_FEATURES
from .imitation import DemoAnchorCallback, load_demo_arrays
from .reward import HORIZON_SECONDS, REFERENCE_STEP_FRAMES, RewardConfig
from .state_ids import VOCAB_FILE, load_vocabulary, save_vocabulary

LOGGER = logging.getLogger("silksong_rl.train")

# 自定义的回合统计字段: Monitor 默认会用自己那份 episode 字典覆盖 info["episode"],
# 要保留这些字段就必须显式声明.
EPISODE_INFO_KEYS = (
    "damage_dealt",
    "damage_taken",
    "boss_kills",
    "player_deaths",
    "reset_seconds",
    "attack_steps",
    "attack_pressed_steps",
    "bind_pressed_steps",
    "close_steps",
    "close_attack_steps",
    "hit_steps",
    "whiff_steps",
    "step_seconds",
    "inactivity_penalties",
    "healed_masks",
    "bind_waste_steps",
    "steps_at_height",
    "max_player_y",
    "swings",
    "whiffed_swings",
    "steps_within_02",
    "steps_within_03",
    "steps_within_04",
    "steps_within_05",
    "min_boss_distance",
    "act_h0",
    "act_h1",
    "act_h2",
    "act_v0",
    "act_v1",
    "act_v2",
    "act_jump",
    "act_attack",
    "act_bind",
    "damage_per_swing",
    "damage_per_press",
    "clip_dir",
)


class ProgressCallback(BaseCallback):
    """按固定步数间隔输出训练进度, 并把每个回合的结果追加到 episodes.jsonl."""

    def __init__(
        self,
        log_interval: int = 2000,
        window: int = 20,
        episode_log: Path | None = None,
        clips_dir: Path | None = None,
        clip_fps: int = 0,
    ) -> None:
        super().__init__()
        self.log_interval = log_interval
        self.window = window
        self.episode_log = episode_log
        self.clips_dir = clips_dir or Path("kills")
        # 合成 mp4 时的帧率必须和插件抓帧的频率一致, 否则回放速度会不对.
        self.clip_fps = clip_fps or 0
        self._current_clip_dir: str | None = None
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
        self._attack_steps = 0.0

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
            self._attack_steps += float(episode.get("attack_steps", 0.0))
            self._current_clip_dir = str(episode.get("clip_dir", "")) or None

            if episode.get("boss_kills", 0.0) > 0.0:
                self._wins += 1
                self._trigger_killcam()
            elif episode.get("player_deaths", 0.0) > 0.0:
                self._losses += 1
            else:
                self._timeouts += 1

            self._append_episode_log(episode)

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
                "累计 造成伤害 %.0f, 受到伤害 %.0f, 挥刀 %d 步, 平均每刀伤害 %.2f",
                self._damage_dealt,
                self._damage_taken,
                int(self._attack_steps),
                self._damage_dealt / self._attack_steps if self._attack_steps > 0 else 0.0,
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

    def _trigger_killcam(self) -> None:
        """击杀时把插件落盘的画面合成成 mp4."""

        clip_dir = str(self._current_clip_dir or "")
        if not clip_dir:
            return

        destination = self.clips_dir / f"kill-{self._wins:03d}-{time.strftime('%H%M%S')}.mp4"
        if self.clip_fps > 0:
            build_clip(clip_dir, destination, fps=self.clip_fps)
        else:
            build_clip(clip_dir, destination)

    def _append_episode_log(self, episode: dict) -> None:
        """每个回合追加一行 JSON, 便于训练结束后做离线分析."""

        if self.episode_log is None:
            return

        record = {
            "step": int(self.num_timesteps),
            "episode": self._episodes,
            "reward": float(episode.get("r", 0.0)),
            "length": int(episode.get("l", 0)),
            "damage_dealt": float(episode.get("damage_dealt", 0.0)),
            "damage_taken": float(episode.get("damage_taken", 0.0)),
            "boss_kills": float(episode.get("boss_kills", 0.0)),
            "player_deaths": float(episode.get("player_deaths", 0.0)),
            "reset_seconds": float(episode.get("reset_seconds", 0.0)),
            "attack_steps": float(episode.get("attack_steps", 0.0)),
            "attack_pressed_steps": float(episode.get("attack_pressed_steps", 0.0)),
            "bind_pressed_steps": float(episode.get("bind_pressed_steps", 0.0)),
            "close_steps": float(episode.get("close_steps", 0.0)),
            "close_attack_steps": float(episode.get("close_attack_steps", 0.0)),
            "min_boss_distance": float(episode.get("min_boss_distance", -1.0)),
            "damage_per_swing": float(episode.get("damage_per_swing", 0.0)),
            "damage_per_press": float(episode.get("damage_per_press", 0.0)),
        }

        # 其余诊断字段 (站位分档 / 命中步 / 动作占比) 按 info 里有什么就记什么,
        # 这样以后加诊断量只需要改 EPISODE_INFO_KEYS, 不用再来动这里.
        for key in EPISODE_INFO_KEYS:
            if key in record or key == "clip_dir" or key not in episode:
                continue
            record[key] = float(episode[key])

        try:
            with self.episode_log.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        except OSError as exc:
            LOGGER.warning("写 episodes.jsonl 失败: %s", exc)


class CheckpointWithNormalizerCallback(CheckpointCallback):
    """周期存档时顺手把观测归一化统计与 Boss 状态词表存到同一个目录.

    只存模型的话, 从 checkpoint 续训会因为旁边找不到 vecnormalize.pkl 而从头统计, 网络看到的
    输入分布就和训练时不一致 (BC 与微调都依赖固定的归一化统计); 状态词表同理: 找不到就会退回
    把 Boss 状态列清零, 观测的语义直接变了.
    """

    def __init__(self, save_freq: int, save_path: str, name_prefix: str, vec_normalize, vocabulary) -> None:
        super().__init__(save_freq=save_freq, save_path=save_path, name_prefix=name_prefix)
        self._vec_normalize = vec_normalize
        self._vocabulary = vocabulary or {}

    def _on_step(self) -> bool:
        if self.n_calls % self.save_freq == 0:
            if self._vec_normalize is not None:
                self._vec_normalize.save(str(Path(self.save_path) / "vecnormalize.pkl"))
            if self._vocabulary:
                save_vocabulary(Path(self.save_path) / VOCAB_FILE, self._vocabulary)

        return super()._on_step()


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="丝之歌 Boss 强化学习训练")
    parser.add_argument("--host", default="127.0.0.1", help="游戏端地址")
    parser.add_argument("--port", type=int, default=5555, help="游戏端端口")
    parser.add_argument("--timesteps", type=int, default=200_000, help="总训练步数")
    parser.add_argument("--run-name", default=None, help="实验名, 默认按时间戳生成")
    parser.add_argument("--runs-dir", default="runs", help="输出目录")
    parser.add_argument("--resume", default=None, help="从 checkpoint 继续训练 (.zip)")
    parser.add_argument("--vecnormalize", default=None, help="继续训练时载入的 VecNormalize 统计文件")
    parser.add_argument(
        "--state-vocab",
        default=None,
        help="Boss 状态词表 (行为克隆产物 state_vocab.json); 不给就按 --resume/--model 旁边的找",
    )
    parser.add_argument(
        "--finetune",
        action="store_true",
        help="从行为克隆模型微调时的保守超参 (更小学习率 + KL 约束), 避免一下子把模仿出来的策略冲掉",
    )

    parser.add_argument("--speed", type=float, default=4.0, help="游戏内时间倍率, 只影响采集速度")
    parser.add_argument(
        "--step-frames",
        type=int,
        default=0,
        help="一个决策步对应几个物理帧 (默认沿用插件配置的 6); 调小等于提高决策频率",
    )
    parser.add_argument(
        "--max-episode-steps",
        type=int,
        default=0,
        help="单回合步数上限 (默认沿用插件配置的 600); 改小决策步长时要相应放大",
    )
    parser.add_argument(
        "--demo-anchor",
        action="append",
        default=None,
        help="人类示范 (或策略击杀轨迹) 目录: 每个 rollout 后用示范样本补一步模仿梯度; 可给多次",
    )
    parser.add_argument(
        "--extra-feature",
        choices=EXTRA_FEATURES,
        default="none",
        help="占用被屏蔽的 physics_frame 列塞一个工程化特征 (range = 离够得着还差多远); 维度不变",
    )
    parser.add_argument(
        "--save-kills",
        default=None,
        help="把击杀那局的 (观测, 动作) 存成示范格式到这个目录, 供自模仿",
    )
    parser.add_argument("--demo-weight", type=float, default=0.1, help="模仿损失的权重")
    parser.add_argument("--demo-batch", type=int, default=256, help="每步模仿用的示范批大小")
    parser.add_argument(
        "--clip-fps",
        type=int,
        default=0,
        help="击杀回放的抓帧频率 (默认沿用插件配置); 帧率高回放更顺, 但会挤一点训练吞吐",
    )
    parser.add_argument(
        "--clip-seconds",
        type=int,
        default=0,
        help="回放缓冲保留多少秒 (默认沿用插件配置)",
    )
    parser.add_argument("--n-steps", type=int, default=1024, help="PPO 每次采样的步数")
    parser.add_argument("--n-epochs", type=int, default=10, help="PPO 每批数据重复训练多少轮")
    parser.add_argument("--batch-size", type=int, default=256, help="PPO 批大小")
    parser.add_argument("--learning-rate", type=float, default=None, help="学习率; 微调模式默认 1e-4, 否则 3e-4")
    parser.add_argument("--gamma", type=float, default=None, help="折扣因子; 不给就按决策粒度自动折算")
    parser.add_argument("--ent-coef", type=float, default=None, help="熵系数; 微调模式默认 0.003, 否则 0.01")
    parser.add_argument("--target-kl", type=float, default=None, help="KL 早停阈值, 不设则不启用")
    parser.add_argument("--net-arch", default="256,256", help="策略网络层宽, 例如 256,256")

    parser.add_argument("--damage-dealt", type=float, default=1.0, help="每点对 Boss 伤害的奖励")
    parser.add_argument("--damage-taken", type=float, default=-1.0, help="每次受伤的奖励")
    parser.add_argument("--boss-kill", type=float, default=25.0, help="击杀 Boss 的奖励")
    parser.add_argument("--player-death", type=float, default=-25.0, help="阵亡的奖励")
    parser.add_argument("--step-penalty", type=float, default=-0.002, help="每步固定惩罚")
    parser.add_argument("--approach", type=float, default=0.0, help="靠近 Boss 的塑形系数")
    parser.add_argument(
        "--close-reward",
        type=float,
        default=0.0,
        help="每步停在攻击距离内就给这么多奖励 (密集项, 用来治'一直够不着')",
    )
    parser.add_argument("--close-distance", type=float, default=0.3, help="--close-reward 判定的归一化距离阈值")
    parser.add_argument(
        "--whiff-penalty",
        type=float,
        default=0.0,
        help="够不着还挥刀时每步的奖励 (负数); 挥空占着出刀冷却, 等 Boss 进范围反而没刀可出",
    )
    parser.add_argument("--inactivity-penalty", type=float, default=0.0, help="超过窗口时间没造成伤害的惩罚 (负数)")
    parser.add_argument("--inactivity-window", type=float, default=5.0, help="上面那个窗口的秒数")
    parser.add_argument("--heal-reward", type=float, default=0.0, help="每回一点血的奖励")
    parser.add_argument("--bind-waste-penalty", type=float, default=0.0, help="丝量不够还按住缚丝的每步惩罚 (负数)")
    parser.add_argument("--bind-silk-threshold", type=float, default=0.9, help="判定丝量够不够回血的比例阈值")
    parser.add_argument(
        "--height-reward",
        type=float,
        default=0.0,
        help="与 Boss 高度差在容差内的每步奖励; 光靠距离势函数拿不到 (来回跳是净零)",
    )
    parser.add_argument("--height-tolerance", type=float, default=1.5, help="上面那个高度差的容差 (世界单位)")
    parser.add_argument("--contact-reward", type=float, default=0.0, help="双方碰撞盒几乎挨上时的每步奖励")
    parser.add_argument("--contact-distance", type=float, default=0.75, help="上面那个挨上的边缘间距阈值")
    parser.add_argument(
        "--swing-whiff-penalty",
        type=float,
        default=0.0,
        help="一次出刀打完却没造成任何伤害的惩罚 (按刀结算, 不随决策粒度折算)",
    )

    parser.add_argument("--checkpoint-every", type=int, default=20_000, help="每多少步存一次 checkpoint")
    parser.add_argument("--log-interval", type=int, default=2_000, help="每多少步打印一次进度")
    parser.add_argument("--seed", type=int, default=1, help="随机种子")

    parser.add_argument("--eval", action="store_true", help="只评估, 需要 --model")
    parser.add_argument("--model", default=None, help="评估用的模型 (.zip)")
    parser.add_argument("--episodes", type=int, default=5, help="评估回合数")
    parser.add_argument(
        "--save-episodes",
        default=None,
        help="评估时把每个回合的 (观测, 动作) 存成示范那样的 npz 到这个目录, 便于逐步对比",
    )
    parser.add_argument(
        "--stochastic",
        action="store_true",
        help="评估时按策略分布采样而不是取 argmax; 多维离散动作空间下 argmax 得到的联合动作可能落在分布之外",
    )
    parser.add_argument("--verbose", action="store_true", help="输出更详细的日志")
    return parser


def default_gamma(step_frames: float) -> float:
    """按决策粒度折算折扣因子, 让"有效视界"在游戏时间里保持约 15 秒.

    6 个物理帧 (约 0.105 秒) 配 0.99 的视界只有 100 步 (约 10 秒); 步长减半以后同样的 100 步
    只剩 5 秒, 一局 700 步的终局奖励 (击杀 / 阵亡) 会被折到几乎看不见, 策略会变得只顾眼前.
    这里把视界统一到约 15 秒, 换粒度时"能看多远"不变.
    """

    return 1.0 - (max(step_frames, 1) / 60.0) / HORIZON_SECONDS


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


def build_env(
    args: argparse.Namespace,
    reward_config: RewardConfig,
    vocabulary: dict[str, int] | None = None,
) -> SilksongBossEnv:
    # 游戏刚启动时插件要等 Unity 主循环跑起来才会回 Hello, 这里给足等待时间.
    client = EnvClient(host=args.host, port=args.port, connect_timeout=120.0, reset_timeout=180.0)
    client.connect()
    if args.speed > 0:
        client.set_speed(args.speed)
        LOGGER.info("已把游戏时间倍率设为 %s", args.speed)

    # 决策粒度: 步长越小, 每个决策跨的游戏时间越短 (出手时机能卡得更准), 但一局要的步数按比例变多.
    client.set_stepping(args.step_frames or 0, args.max_episode_steps or 0)
    effective_frames = args.step_frames or int(client.hello.get("step_frames", REFERENCE_STEP_FRAMES))
    reward_config.dense_scale = max(effective_frames, 1) / REFERENCE_STEP_FRAMES
    if reward_config.dense_scale != 1.0:
        LOGGER.info("密集奖励折算系数 %.3f (每步 %d 物理帧)", reward_config.dense_scale, effective_frames)

    # 回放抓帧频率: 帧率越高回放越顺, 但每秒同步截屏的次数也越多, 会挤训练吞吐.
    client.set_clip(args.clip_fps or 0, args.clip_seconds or 0)
    effective_clip_fps = int(args.clip_fps or client.hello.get("clip_fps", DEFAULT_CLIP_FPS))
    if args.clip_fps:
        LOGGER.info("回放录制: %d 帧/秒", effective_clip_fps)
    if vocabulary is None:
        vocabulary = load_state_vocabulary(args)

    env = SilksongBossEnv(client, reward_config=reward_config, state_vocabulary=vocabulary)
    # 折扣因子要按实际生效的步长折算, 回放合成要按实际抓帧频率, 后面 run_training 会读这两个值.
    env.step_frames = float(effective_frames)
    env.clip_fps = effective_clip_fps
    env.extra_feature = args.extra_feature
    if args.save_kills:
        env.kill_trace_dir = Path(args.save_kills)
        LOGGER.info("击杀轨迹 (自模仿数据) 将存到: %s", env.kill_trace_dir)
    return env


def resolve_state_vocabulary(args: argparse.Namespace, run_dir: Path) -> dict[str, int]:
    """找 Boss 状态词表, 并把它放进本次实验目录.

    分段接力训练用的是上一段的 final.zip / checkpoint, 只有把词表跟着模型放在同一个目录,
    下一段才会用同一套编号; 否则它会退回把状态列清零, 观测语义直接变了 (而且是静默的).
    """

    vocabulary = load_state_vocabulary(args)
    if not vocabulary and (run_dir / VOCAB_FILE).is_file():
        vocabulary = load_vocabulary(run_dir / VOCAB_FILE)
        LOGGER.info("已载入 Boss 状态词表: %s (%d 项)", run_dir / VOCAB_FILE, len(vocabulary))

    if vocabulary:
        save_vocabulary(run_dir / VOCAB_FILE, vocabulary)

    return vocabulary


def load_state_vocabulary(args: argparse.Namespace) -> dict[str, int]:
    """找行为克隆留下的 Boss 状态词表.

    有词表才能把当前会话的 Boss 状态编号映射成示范里的同一套编号; 找不到就照旧清零那些列.
    """

    candidates: list[Path] = []
    if args.state_vocab:
        candidates.append(Path(args.state_vocab))
    if args.resume:
        candidates.append(Path(args.resume).parent / VOCAB_FILE)
    if args.model:
        candidates.append(Path(args.model).parent / VOCAB_FILE)

    for candidate in candidates:
        if candidate.is_file():
            vocabulary = load_vocabulary(candidate)
            LOGGER.info("已载入 Boss 状态词表: %s (%d 项)", candidate, len(vocabulary))
            return vocabulary

    return {}


def build_normalized_env(raw_env, normalizer_path: Path | None):
    """给原始 vec env 包上观测归一化, 且**只包一层**.

    续训时容易写成"先 VecNormalize(raw) 再 VecNormalize.load(saved, 那一层)", 于是两层叠在一起:
    训练看到的观测是 外层统计(内层统计(原始观测)), 而评估与部署只有一层. 同一个权重拿到的输入
    不是一个东西, 表现就是训练日志里能打出伤害与击杀, 一评估却站着不动.
    """

    if normalizer_path is not None and normalizer_path.exists():
        LOGGER.info("已载入观测归一化统计: %s", normalizer_path)
        return VecNormalize.load(str(normalizer_path), raw_env)

    if normalizer_path is not None:
        LOGGER.warning("没有找到归一化统计 (%s), 将从头统计", normalizer_path)

    return VecNormalize(raw_env, norm_obs=True, norm_reward=False, clip_obs=10.0)


def run_training(args: argparse.Namespace, reward_config: RewardConfig) -> Path:
    run_name = args.run_name or time.strftime("run-%Y%m%d-%H%M%S")
    run_dir = Path(args.runs_dir) / run_name
    run_dir.mkdir(parents=True, exist_ok=True)
    LOGGER.info("实验目录: %s", run_dir.resolve())
    LOGGER.info("奖励配置: %s", reward_config.describe())

    vocabulary = resolve_state_vocabulary(args, run_dir)
    env = build_env(args, reward_config, vocabulary)
    # Monitor 默认会用自己那份 episode 统计覆盖 info["episode"], 这里把自定义字段带上.
    monitored = Monitor(env, info_keywords=EPISODE_INFO_KEYS)
    raw_env = DummyVecEnv([lambda: monitored])

    net_arch = [int(width) for width in args.net_arch.split(",") if width.strip()]

    client_clip_fps = int(env.clip_fps)

    learning_rate = 3e-4 if args.learning_rate is None else args.learning_rate
    ent_coef = 0.01 if args.ent_coef is None else args.ent_coef
    target_kl = args.target_kl
    gamma = args.gamma if args.gamma is not None else default_gamma(env.step_frames)
    if args.gamma is None and abs(gamma - 0.99) > 1e-9:
        LOGGER.info("折扣因子按粒度折算为 %.4f (每步 %.0f 物理帧)", gamma, env.step_frames)
    if args.finetune:
        # 从人类示范克隆出来的策略是个"能用"的起点, 一上来用大学习率会把它冲掉,
        # 这里给一套保守值: 小学习率, 小熵, 并限制每次更新的 KL.
        # 显式给了参数就按给的来 (跑到几十万步之后起点早就不是重点了, 该加速就加速).
        learning_rate = 1e-4 if args.learning_rate is None else args.learning_rate
        ent_coef = 0.003 if args.ent_coef is None else args.ent_coef
        target_kl = 0.03 if args.target_kl is None else args.target_kl
        LOGGER.info("微调模式: 学习率 %s, 熵系数 %s, KL 上限 %s", learning_rate, ent_coef, target_kl)

    # 观测归一化只包一层: 续训时把上一段留下的统计装进这一层, 而不是在已经包好的外面再套一层.
    # 套两层的话训练看到的观测是"外层统计(内层统计(原始观测))", 评估与部署只有一层, 同一个权重
    # 拿到的输入不是一个东西 —— 表现就是训练日志里能打出伤害, 一评估就站着不动.
    normalizer_path = None
    if args.resume:
        normalizer_path = Path(args.vecnormalize) if args.vecnormalize else Path(args.resume).parent / "vecnormalize.pkl"

    vec_env = build_normalized_env(raw_env, normalizer_path)

    if args.resume:
        LOGGER.info("从 %s 继续训练", args.resume)
        # 注意: PPO.load 会把存档里的超参原样恢复 (内部先 update(data) 再 update(kwargs)),
        # 所以命令行给的 n_steps / batch_size / learning_rate 等等必须作为 kwargs 传进 load,
        # 在 load 之后再赋值则不会重建 rollout buffer, 而完全不传就会被存档里的值悄悄覆盖.
        model = PPO.load(
            args.resume,
            env=vec_env,
            tensorboard_log=str(run_dir / "tb"),
            n_steps=args.n_steps,
            n_epochs=args.n_epochs,
            batch_size=args.batch_size,
            learning_rate=learning_rate,
            gamma=gamma,
            ent_coef=ent_coef,
            target_kl=target_kl,
        )
        LOGGER.info(
            "生效超参: n_steps=%d, batch_size=%d, n_epochs=%d, learning_rate=%s, gamma=%s, "
            "ent_coef=%s, target_kl=%s, 策略网络=%s",
            model.n_steps,
            model.batch_size,
            model.n_epochs,
            model.learning_rate,
            model.gamma,
            model.ent_coef,
            model.target_kl,
            model.policy_kwargs.get("net_arch") if isinstance(model.policy_kwargs, dict) else None,
        )
    else:
        model = PPO(
            "MlpPolicy",
            vec_env,
            n_steps=args.n_steps,
            n_epochs=args.n_epochs,
            batch_size=args.batch_size,
            learning_rate=learning_rate,
            gamma=gamma,
            ent_coef=ent_coef,
            target_kl=target_kl,
            policy_kwargs={"net_arch": net_arch},
            tensorboard_log=str(run_dir / "tb"),
            seed=args.seed,
            verbose=0,
        )

    callbacks = [
        ProgressCallback(
            log_interval=args.log_interval,
            episode_log=run_dir / "episodes.jsonl",
            # 击杀那局的画面由插件的内存缓冲提供, 这里合成 mp4.
            clips_dir=run_dir / "kills",
            clip_fps=int(client_clip_fps),
        )
    ]
    if args.checkpoint_every > 0:
        callbacks.append(
            CheckpointWithNormalizerCallback(
                save_freq=args.checkpoint_every,
                save_path=str(run_dir / "checkpoints"),
                name_prefix="ppo",
                vec_normalize=vec_env,
                vocabulary=vocabulary,
            )
        )

    if args.demo_anchor:
        demo_observations, demo_actions = load_demo_arrays(args.demo_anchor)
        callbacks.append(
            DemoAnchorCallback(
                demo_observations,
                demo_actions,
                weight=args.demo_weight,
                batch_size=args.demo_batch,
            )
        )
        LOGGER.info("示范先验: 权重 %s, 每步批大小 %s", args.demo_weight, args.demo_batch)

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
    vec_env = DummyVecEnv([lambda: Monitor(env, info_keywords=EPISODE_INFO_KEYS)])
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

    if args.seed is not None:
        vec_env.seed(args.seed)
    # 多维离散动作空间里, 各维取 argmax 拼出来的联合动作可能压根不是策略采样得到过的动作,
    # 于是"确定性策略"经常是个退化行为 (例如一直往一个方向走而不出手), 而训练时按分布采样
    # 才是它真正的水平. 想看真实水平就用 --stochastic.
    deterministic = not args.stochastic
    LOGGER.info("评估动作: %s", "确定性 argmax" if deterministic else "按策略分布采样")

    wins = 0
    trace_dir = Path(args.save_episodes) if args.save_episodes else None
    trace_map_name = time.strftime("state-map-%Y%m%d-%H%M%S.json")
    for episode in range(1, args.episodes + 1):
        observation = vec_env.reset()
        trace = trace_module.EpisodeTrace() if trace_dir else None
        total_reward = 0.0
        steps = 0
        while True:
            action, _ = model.predict(observation, deterministic=deterministic)
            if trace is not None and env.latest_raw_values is not None:
                trace.add(env.latest_raw_values, action)
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
                if trace is not None:
                    trace.save(trace_dir, episode, env.client.field_names, env.client.state_map, trace_map_name)
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
        close_reward=args.close_reward,
        close_distance=args.close_distance,
        whiff_penalty=args.whiff_penalty,
        inactivity_penalty=args.inactivity_penalty,
        inactivity_window=args.inactivity_window,
        heal_reward=args.heal_reward,
        bind_waste_penalty=args.bind_waste_penalty,
        bind_silk_threshold=args.bind_silk_threshold,
        height_reward=args.height_reward,
        height_tolerance=args.height_tolerance,
        contact_reward=args.contact_reward,
        contact_distance=args.contact_distance,
        swing_whiff_penalty=args.swing_whiff_penalty,
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
