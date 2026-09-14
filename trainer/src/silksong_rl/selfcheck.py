"""不连游戏也能跑的自检.

覆盖三件事:

1. 协议编解码往返;
2. C# 侧观测字段表与 Python 解析是否对得上 (直接从 ObservationSchema.cs 里读字段名);
3. 用一个假游戏端跑通 EnvClient 与 SilksongBossEnv 的 reset/step/终止流程.

用法::

    uv run silksong-selfcheck
"""

from __future__ import annotations

import json
import logging
import re
import socket
import struct
import sys
import threading
import time
from pathlib import Path

import numpy as np

from . import protocol
from .client import EnvClient
from .env import SilksongBossEnv
from .reward import RewardConfig, compute_reward
from .state_ids import VOCAB_FILE, load_vocabulary, save_state_map

LOGGER = logging.getLogger("silksong_rl.selfcheck")

_PAYLOAD_HEAD = struct.Struct("<ii")
_LENGTH = struct.Struct("<i")

SCHEMA_PATH = Path(__file__).resolve().parents[3] / "mods" / "rl-env" / "src" / "Observation" / "ObservationSchema.cs"


def load_csharp_schema() -> list[str]:
    """从 C# 的 ObservationSchema.cs 还原出字段名列表, 保证两边不会悄悄跑偏.

    字段表的构造方式是 "固定段 + 重复段 (小怪 / 危险框 / Boss FSM)", 重复段的字段名要按同样的
    顺序和下标的拼接规则还原, 因此这里把 C# 里的构造逻辑照抄一遍.
    """

    if not SCHEMA_PATH.exists():
        raise FileNotFoundError(f"找不到 C# 观测字段表: {SCHEMA_PATH}")

    text = SCHEMA_PATH.read_text(encoding="utf-8")

    def array_literal(name: str) -> list[str]:
        marker = f"{name} = new string[]"
        start = text.index(marker)
        body = text[start:]
        end = body.index("};")
        values = re.findall(r'"([A-Za-z0-9_]+)"', body[:end])
        if not values:
            raise AssertionError(f"没有从 {name} 里解析出任何字段")
        return values

    def const_int(name: str) -> int:
        match = re.search(rf"{name} = (\d+);", text)
        if not match:
            raise AssertionError(f"没有解析出常量 {name}")
        return int(match.group(1))

    fixed = array_literal("private static readonly string[] FixedNames")
    enemy_fields = array_literal("internal static readonly string[] EnemyFieldNames")
    hazard_fields = array_literal("internal static readonly string[] HazardFieldNames")
    boss_fsm_fields = array_literal("internal static readonly string[] BossFsmFieldNames")

    names = list(fixed)
    for slot in range(const_int("MaxEnemies")):
        names.extend(f"enemy{slot}_{field}" for field in enemy_fields)
    for slot in range(const_int("MaxHazards")):
        names.extend(f"hazard{slot}_{field}" for field in hazard_fields)
    for slot in range(const_int("MaxBossFsms")):
        names.extend(f"boss_fsm{slot}_{field}" for field in boss_fsm_fields)

    enum_count = count_enum_members("ObsField")
    if enum_count != len(fixed):
        raise AssertionError(f"固定字段名 {len(fixed)} 个, ObsField 枚举 {enum_count} 项, 两边不一致")

    return names


def count_enum_members(enum_name: str) -> int:
    """数一个枚举有多少项 (枚举定义在 ObsField.cs 里)."""

    enum_path = SCHEMA_PATH.parent / "ObsField.cs"
    if not enum_path.exists():
        raise FileNotFoundError(f"找不到枚举定义: {enum_path}")

    text = enum_path.read_text(encoding="utf-8")
    marker = f"internal enum {enum_name}"
    start = text.index(marker)
    body = text[start:]
    end = body.index("}")
    return len(re.findall(r"^\s+[A-Z][A-Za-z0-9]*,", body[:end], flags=re.MULTILINE))


def test_protocol_roundtrip() -> None:
    reset_frame = protocol.encode_int_message(protocol.MessageType.RESET)
    (length,) = _LENGTH.unpack_from(reset_frame, 0)
    assert length == len(reset_frame) - _LENGTH.size, "帧长度前缀不对"
    kind, body = protocol.decode_payload(reset_frame[_LENGTH.size :])
    assert kind is protocol.MessageType.RESET, kind

    step_frame = protocol.encode_step((1, 2, 1, 0, 1))
    kind, body = protocol.decode_payload(step_frame[_LENGTH.size :])
    assert kind is protocol.MessageType.STEP, kind
    assert struct.unpack("<iiiii", body) == (1, 2, 1, 0, 1), "Step 动作解码不对"

    speed_frame = protocol.encode_set_speed(3.5)
    kind, body = protocol.decode_payload(speed_frame[_LENGTH.size :])
    assert kind is protocol.MessageType.SET_SPEED, kind
    assert abs(struct.unpack("<f", body)[0] - 3.5) < 1e-6

    LOGGER.info("协议编解码往返: 通过")


def test_reward() -> None:
    names = load_csharp_schema()
    config = RewardConfig()

    def observation(**overrides) -> "object":
        values = {name: 0.0 for name in names}
        values.update(overrides)
        array = np.asarray([values[name] for name in names], dtype=np.float32)
        from .client import Observation

        return Observation(values=array, named=values, step_index=1, flags=0)

    hit = observation(damage_dealt_step=4.0, boss_health_ratio=0.6, boss_alive=1.0)
    reward, components = compute_reward(None, hit, config)
    assert abs(reward - (4.0 + config.step_penalty)) < 1e-6, (reward, components)

    death = observation(player_died_step=1.0, damage_taken_step=2.0)
    reward, _ = compute_reward(None, death, config)
    assert abs(reward - (config.player_death + config.damage_taken * 2.0 + config.step_penalty)) < 1e-6

    kill = observation(boss_killed_step=1.0, boss_alive=0.0)
    reward, _ = compute_reward(None, kill, config)
    assert reward > config.boss_kill * 0.9

    # 贴身奖励只在"活着 + 进入阈值"时给, 而且要能被关掉.
    close_config = RewardConfig(close_reward=0.05, close_distance=0.3)
    near = observation(boss_alive=1.0, boss_distance_n=0.2)
    reward, components = compute_reward(None, near, close_config)
    assert abs(components["close"] - 0.05) < 1e-9, components
    far = observation(boss_alive=1.0, boss_distance_n=0.5)
    _, components = compute_reward(None, far, close_config)
    assert components["close"] == 0.0, components
    dead = observation(boss_alive=0.0, boss_distance_n=0.2)
    _, components = compute_reward(None, dead, close_config)
    assert components["close"] == 0.0, components
    _, components = compute_reward(None, near, config)
    assert components["close"] == 0.0, components

    LOGGER.info("奖励计算: 通过")


class FakeGameServer:
    """最小可用的假游戏端: 只实现 Hello / 观测 / 几种命令."""

    def __init__(self, field_names: list[str], terminate_after: int = 3, clip_dir: str = "") -> None:
        self.field_names = field_names
        self.terminate_after = terminate_after
        self.clip_dir = clip_dir
        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind(("127.0.0.1", 0))
        self._server.listen(1)
        self.port = self._server.getsockname()[1]
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._stop = threading.Event()
        self.step_count = 0

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        try:
            self._server.close()
        except OSError:
            pass

    def _serve(self) -> None:
        try:
            connection, _ = self._server.accept()
        except OSError:
            return

        try:
            with connection:
                hello = {
                    "protocol": protocol.PROTOCOL_VERSION,
                    "magic": protocol.MAGIC,
                    "observations": self.field_names,
                    "action": {"horizontal": 3, "vertical": 3, "jump": 2, "attack": 2, "bind": 2},
                    "step_frames": 4,
                    "max_episode_steps": 100,
                    "boss": "苔藓之母",
                    "scene": "Tut_03",
                }
                self._send_text(connection, protocol.MessageType.HELLO, json.dumps(hello, ensure_ascii=False))
                self._send_text(
                    connection,
                    protocol.MessageType.STATE_MAP,
                    json.dumps({"0": "health_manager_enemy=Idle"}, ensure_ascii=False),
                )

                while not self._stop.is_set():
                    header = self._recv(connection, _LENGTH.size)
                    if header is None:
                        return
                    (length,) = _LENGTH.unpack(header)
                    payload = self._recv(connection, length)
                    if payload is None:
                        return

                    (message_type, _unused) = _PAYLOAD_HEAD.unpack_from(payload, 0)
                    if message_type == protocol.MessageType.RESET:
                        self.step_count = 0
                        self._send_observation(connection, flags=protocol.FLAG_EPISODE_START)
                    elif message_type == protocol.MessageType.STEP:
                        self.step_count += 1
                        flags = protocol.FLAG_TERMINATED if self.step_count >= self.terminate_after else 0
                        self._send_observation(connection, flags=flags, damage=2.0 if self.step_count == 1 else 0.0)
                    elif message_type == protocol.MessageType.SET_HUMAN_MODE:
                        self._send_text(connection, protocol.MessageType.STATUS, json.dumps({"human": True}))
                    elif message_type == protocol.MessageType.SAVE_CLIP:
                        # 回放片段由插件落盘, 路径通过 Status 回传; 这里用一个假路径走通链路.
                        body = (int.from_bytes(payload[4:8], "little", signed=True) != 0)
                        if body:
                            self._send_text(
                                connection,
                                protocol.MessageType.STATUS,
                                json.dumps({"state": "Stepping", "message": "clip:" + str(self.clip_dir)}),
                            )
                        else:
                            self._send_text(
                                connection,
                                protocol.MessageType.STATUS,
                                json.dumps({"state": "Stepping", "message": "回放片段已丢弃"}),
                            )
                    elif message_type == protocol.MessageType.CLOSE:
                        return
        except OSError:
            return

    def _send_observation(self, connection: socket.socket, flags: int, damage: float = 0.0) -> None:
        values = {name: 0.0 for name in self.field_names}
        values["player_health"] = 5.0
        values["player_health_ratio"] = 1.0
        values["boss_alive"] = 1.0
        values["boss_health"] = 30.0 - damage
        values["boss_health_max"] = 30.0
        values["boss_health_ratio"] = (30.0 - damage) / 30.0
        values["boss_distance_n"] = max(0.0, 0.5 - 0.1 * self.step_count)
        values["damage_dealt_step"] = damage
        values["boss_killed_step"] = 1.0 if flags & protocol.FLAG_TERMINATED else 0.0

        vector = [values[name] for name in self.field_names]
        body = struct.pack(f"<iii{len(vector)}f", int(protocol.MessageType.OBSERVATION), self.step_count, flags, *vector)
        connection.sendall(_LENGTH.pack(len(body)) + body)

    @staticmethod
    def _send_text(connection: socket.socket, message_type: protocol.MessageType, text: str) -> None:
        encoded = text.encode("utf-8")
        body = struct.pack("<i", int(message_type)) + encoded
        connection.sendall(_LENGTH.pack(len(body)) + body)

    @staticmethod
    def _recv(connection: socket.socket, count: int) -> bytes | None:
        buffer = bytearray()
        while len(buffer) < count:
            chunk = connection.recv(count - len(buffer))
            if not chunk:
                return None
            buffer.extend(chunk)
        return bytes(buffer)


def test_env_against_fake_server() -> None:
    field_names = load_csharp_schema()
    clip_dir = str(Path(__file__).resolve().parents[3] / ".tmp" / "selfcheck-clips")
    server = FakeGameServer(field_names, terminate_after=3, clip_dir=clip_dir)
    server.start()
    time.sleep(0.1)

    client = EnvClient(port=server.port, connect_timeout=5.0, reset_timeout=5.0, step_timeout=5.0)
    try:
        env = SilksongBossEnv(client, RewardConfig())
        assert env.observation_space.shape == (len(field_names),), env.observation_space
        assert tuple(env.action_space.nvec) == (3, 3, 2, 2, 2), env.action_space

        observation, info = env.reset()
        assert observation.shape == (len(field_names),)
        assert info["step_index"] == 0, info

        total = 0.0
        terminated = False
        steps = 0
        while not terminated:
            observation, reward, terminated, truncated, info = env.step((2, 0, 1, 1, 0))
            total += reward
            steps += 1

        assert steps == 3, steps
        assert terminated and not truncated
        assert total > 0.0, total
        assert info["episode"]["boss_kills"] == 1.0, info["episode"]

        # 再跑一个回合, 验证终止之后还能 reset.
        env.reset()
        _, _, terminated, _, _ = env.step((0, 0, 0, 0, 0))
        assert not terminated

        # 有词表时 Boss 状态列按名字重映射 (假游戏端发的映射是 0 -> health_manager_enemy=Idle),
        # 没有词表时同一列清零; 与游戏进程时长有关的列无论哪种情况都清零.
        state_column = client.field_names.index("boss_state_id")
        physics_column = client.field_names.index("physics_frame")
        assert observation[state_column] == 0.0, observation[state_column]

        with_vocabulary = SilksongBossEnv(
            client,
            RewardConfig(),
            state_vocabulary={"health_manager_enemy=Idle": 1},
        )
        remapped, _ = with_vocabulary.reset()
        assert remapped[state_column] == 1.0, remapped[state_column]
        assert remapped[physics_column] == 0.0, remapped[physics_column]

        # 回放: 击杀时 mod 会把画面目录通过 Status 回传, 训练侧照单收下.
        assert client.save_clip(True) == clip_dir, "回放片段路径没有正确回传"
        assert client.save_clip(False) is None

        env.close()
    finally:
        server.stop()

    LOGGER.info("假游戏端联调 (reset/step/终止/再重置): 通过")


def test_training_dry_run() -> None:
    """用假游戏端把 train.py 的完整训练路径 (VecNormalize + PPO + Monitor) 真跑几十步."""

    import argparse
    import shutil

    from . import train as train_module

    field_names = load_csharp_schema()
    server = FakeGameServer(field_names, terminate_after=16)
    server.start()
    time.sleep(0.1)

    run_dir = Path(__file__).resolve().parents[3] / ".tmp" / "selfcheck-training"
    if run_dir.exists():
        shutil.rmtree(run_dir, ignore_errors=True)

    parser = train_module.build_parser()
    args = parser.parse_args(
        [
            "--port", str(server.port),
            "--timesteps", "64",
            "--run-name", "dry-run",
            "--runs-dir", str(run_dir),
            "--n-steps", "32",
            "--batch-size", "16",
            "--log-interval", "32",
            # 顺手验证周期存档: 存档旁边必须同时落一份观测归一化统计, 否则从 checkpoint 续训
            # 会因为找不到统计而从零开始, 网络看到的输入分布与训练时不一致.
            "--checkpoint-every", "32",
        ]
    )

    try:
        model_path = train_module.run_training(args, train_module.RewardConfig())
        assert model_path.exists(), model_path

        checkpoints = sorted((run_dir / "dry-run" / "checkpoints").glob("ppo_*_steps.zip"))
        assert checkpoints, "没有生成周期存档"
        assert checkpoints[0].with_name("vecnormalize.pkl").is_file(), "周期存档旁边缺少归一化统计"
        LOGGER.info("训练干跑: 通过 (模型 %s, 周期存档 %s)", model_path.name, checkpoints[0].name)
    finally:
        server.stop()


def test_state_vocabulary() -> None:
    """跨会话对齐: 同一个 Boss 状态在两次会话里拿到不同原始编号, 重映射后必须落到同一个值."""

    from .state_ids import build_vocabulary, remap_states, save_state_map, state_columns

    # 会话 A 与 B 的"首次发现顺序"不同, 于是同一个状态在两边的原始编号不同.
    session_a = {0: "Control=Idle", 1: "Control=Swoop Antic", 2: "Control=Idle|Death="}
    session_b = {0: "Control=Swoop Antic", 1: "Control=Idle", 2: "Control=Swoop Recovery"}

    vocabulary = build_vocabulary([session_a, session_b])
    assert vocabulary["Control=Idle"] != vocabulary["Control=Swoop Antic"], vocabulary
    assert "Control=Swoop Recovery" in vocabulary, vocabulary

    field_names = ["player_health", "boss_state_id", "boss_fsm0_state_id"]
    columns = state_columns(field_names)
    assert columns == [1, 2], columns

    # A 里编号 1 与 B 里编号 0 是同一个状态, 重映射后必须相等.
    row_a = np.asarray([5.0, 1.0, 2.0], dtype=np.float32)
    row_b = np.asarray([5.0, 0.0, 1.0], dtype=np.float32)
    mapped_a = remap_states(row_a, columns, session_a, vocabulary)
    mapped_b = remap_states(row_b, columns, session_b, vocabulary)
    assert mapped_a[1] == mapped_b[1], (mapped_a, mapped_b)
    # 两个不同的状态不能被压成同一个值.
    assert mapped_a[1] != mapped_a[2], mapped_a
    # 非状态列原样保留.
    assert mapped_a[0] == 5.0, mapped_a

    # -1 表示"没有 Boss", 必须落到 0, 不能当成一个合法编号去查表 (0 是合法编号).
    missing = np.asarray([1.0, -1.0, -1.0], dtype=np.float32)
    mapped_missing = remap_states(missing, columns, session_a, vocabulary)
    assert mapped_missing[1] == 0.0 and mapped_missing[2] == 0.0, mapped_missing

    # 二维样本表 (行为克隆的数据) 走同一条路径: 同一个会话的若干行可以一次重映射.
    batch = np.stack([row_a, row_a])
    mapped_batch = remap_states(batch, columns, session_a, vocabulary)
    assert mapped_batch.shape == batch.shape
    assert np.allclose(mapped_batch[0], mapped_a) and np.allclose(mapped_batch[1], mapped_a)

    # 会话里没出现过的编号只能落到 0, 不能撞上别的状态.
    unseen = np.asarray([1.0, 7.0, 0.0], dtype=np.float32)
    mapped_unseen = remap_states(unseen, columns, session_a, vocabulary)
    assert mapped_unseen[1] == 0.0, mapped_unseen

    test_demonstration_state_maps()
    LOGGER.info("Boss 状态跨会话对齐: 通过 (词表 %d 项)", len(vocabulary))


def test_demonstration_state_maps() -> None:
    """数据集层: 两个会话的示范各带一份映射, 载入后同一个状态必须落到同一个值."""

    import shutil

    from .dataset import load_demonstrations

    field_names = ["boss_state_id", "physics_frame", "player_health"]
    demo_dir = Path(__file__).resolve().parents[3] / ".tmp" / "selfcheck-demos"
    if demo_dir.exists():
        shutil.rmtree(demo_dir, ignore_errors=True)
    demo_dir.mkdir(parents=True)

    def write_episode(name: str, rows: list[list[float]], state_map: dict[int, str], map_file: str) -> None:
        np.savez_compressed(
            demo_dir / name,
            obs=np.asarray(rows, dtype=np.float32),
            action=np.zeros((len(rows), 5), dtype=np.int64),
            fields=np.asarray(field_names),
            state_map=np.asarray(map_file),
        )
        save_state_map(demo_dir / map_file, state_map)

    # 两个会话对同一批状态给出了相反的编号.
    write_episode("episode-001.npz", [[0.0, 7.0, 5.0], [1.0, 7.0, 5.0]], {0: "Control=Idle", 1: "Control=Swoop Antic"}, "state-map-a.json")
    write_episode("episode-002.npz", [[0.0, 9.0, 5.0], [1.0, 9.0, 5.0]], {0: "Control=Swoop Antic", 1: "Control=Idle"}, "state-map-b.json")

    observations, _, vocabulary = load_demonstrations(demo_dir)
    assert len(vocabulary) == 2, vocabulary
    # 第 1 局第 1 行与第 2 局第 2 行都是 Control=Idle, 必须相等; 同理另外两行.
    assert observations[0, 0] == observations[3, 0], observations[:, 0]
    assert observations[1, 0] == observations[2, 0], observations[:, 0]
    assert observations[0, 0] != observations[1, 0], observations[:, 0]
    # 编号本身不能再是原始值 (0/1 是会话内的编号, 重映射后应该落到词表编号上).
    assert observations[1, 0] != 1.0, observations[:, 0]
    # 与游戏进程时长有关的列无论如何都清零.
    assert np.all(observations[:, 1] == 0.0), observations[:, 1]

    LOGGER.info("示范状态映射载入: 通过")


def test_bc_and_train_state_vocab() -> None:
    """把"带状态映射的示范 -> 行为克隆 -> 微调"这条链路真跑一遍, 不需要游戏.

    这一段的关键产物是放在模型旁边的 `state_vocab.json`: 训练侧找不到它就会退回清零状态列,
    而这是下次录制示范后必然要走的路, 所以要在自检里盯住.
    """

    import shutil

    from . import bc as bc_module
    from . import train as train_module

    field_names = load_csharp_schema()
    demo_dir = Path(__file__).resolve().parents[3] / ".tmp" / "selfcheck-bc-demos"
    run_dir = Path(__file__).resolve().parents[3] / ".tmp" / "selfcheck-bc-runs"
    for directory in (demo_dir, run_dir):
        if directory.exists():
            shutil.rmtree(directory, ignore_errors=True)
    demo_dir.mkdir(parents=True)

    def row(state_id: float, attacking: float) -> list[float]:
        values = {name: 0.0 for name in field_names}
        values["player_health"] = 5.0
        values["player_health_ratio"] = 1.0
        values["boss_alive"] = 1.0
        values["boss_health_ratio"] = 1.0
        values["boss_distance_n"] = 0.2
        values["player_attacking"] = attacking
        values["boss_state_id"] = state_id
        values["boss_fsm0_state_id"] = state_id
        return [values[name] for name in field_names]

    state_map = {0: "Control=Idle", 1: "Control=Swoop Antic"}
    observations = [row(0.0, 1.0), row(1.0, 0.0)] * 24
    np.savez_compressed(
        demo_dir / "episode-001.npz",
        obs=np.asarray(observations, dtype=np.float32),
        action=np.tile(np.asarray([2, 0, 0, 1, 0], dtype=np.int64), (len(observations), 1)),
        fields=np.asarray(field_names),
        state_map=np.asarray("state-map-test.json"),
    )
    save_state_map(demo_dir / "state-map-test.json", state_map)

    bc_args = bc_module.build_parser().parse_args(
        [
            "--data", str(demo_dir),
            "--out", str(run_dir / "bc"),
            "--epochs", "3",
            "--batch-size", "16",
            "--patience", "0",
        ]
    )
    model_path = bc_module.run_bc(bc_args)
    assert model_path.is_file(), model_path

    vocab_path = model_path.parent / VOCAB_FILE
    assert vocab_path.is_file(), f"行为克隆没有写出状态词表: {vocab_path}"
    vocabulary = load_vocabulary(vocab_path)
    assert vocabulary["Control=Idle"] != vocabulary["Control=Swoop Antic"], vocabulary

    # 训练侧要从 --resume 旁边的词表自动找到它, 不需要额外参数.
    train_args = train_module.build_parser().parse_args(["--resume", str(model_path)])
    assert train_module.load_state_vocabulary(train_args) == vocabulary

    LOGGER.info("示范 -> 克隆 -> 微调的状态词表链路: 通过 (%d 项)", len(vocabulary))


def test_normalizer_wrapper_depth() -> None:
    """续训时的观测归一化只能包一层.

    套两层的话, 训练看到的观测是"外层统计(内层统计(原始观测))", 而评估与部署只有一层,
    同一个权重拿到的输入不是一个东西: 训练日志里有伤害有击杀, 一评估就站着不动.
    """

    import shutil

    from stable_baselines3.common.vec_env import DummyVecEnv, VecNormalize

    from .bc import SpecEnv
    from .train import build_normalized_env

    work_dir = Path(__file__).resolve().parents[3] / ".tmp" / "selfcheck-normalizer"
    if work_dir.exists():
        shutil.rmtree(work_dir, ignore_errors=True)
    work_dir.mkdir(parents=True)

    def raw_env():
        return DummyVecEnv([lambda: SpecEnv(216, tuple(int(x) for x in protocol.ACTION_SHAPE))])

    saved = work_dir / "vecnormalize.pkl"
    VecNormalize(raw_env(), norm_obs=True, norm_reward=False, clip_obs=10.0).save(str(saved))

    loaded = build_normalized_env(raw_env(), saved)
    assert isinstance(loaded, VecNormalize)
    assert not isinstance(loaded.venv, VecNormalize), "归一化被套了两层, 训练与评估看到的观测会不一致"

    fresh = build_normalized_env(raw_env(), None)
    assert isinstance(fresh, VecNormalize) and not isinstance(fresh.venv, VecNormalize)

    missing = build_normalized_env(raw_env(), work_dir / "不存在.pkl")
    assert isinstance(missing, VecNormalize) and not isinstance(missing.venv, VecNormalize)

    LOGGER.info("观测归一化只包一层: 通过")


def main() -> None:
    # Windows 控制台默认不是 UTF-8, 中文日志会变乱码.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    logging.basicConfig(level=logging.INFO, format="%(levelname)-7s %(name)s: %(message)s")
    names = load_csharp_schema()
    LOGGER.info("C# 观测字段表: %d 个字段", len(names))

    test_protocol_roundtrip()
    test_reward()
    test_state_vocabulary()
    test_bc_and_train_state_vocab()
    test_normalizer_wrapper_depth()
    test_env_against_fake_server()
    test_training_dry_run()
    LOGGER.info("全部自检通过")


if __name__ == "__main__":
    main()
