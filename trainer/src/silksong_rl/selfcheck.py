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

    LOGGER.info("奖励计算: 通过")


class FakeGameServer:
    """最小可用的假游戏端: 只实现 Hello / 观测 / 三种命令."""

    def __init__(self, field_names: list[str], terminate_after: int = 3) -> None:
        self.field_names = field_names
        self.terminate_after = terminate_after
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
    server = FakeGameServer(field_names, terminate_after=3)
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
            "--checkpoint-every", "0",
        ]
    )

    try:
        model_path = train_module.run_training(args, train_module.RewardConfig())
        assert model_path.exists(), model_path
        LOGGER.info("训练干跑: 通过 (模型 %s)", model_path.name)
    finally:
        server.stop()


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
    test_env_against_fake_server()
    test_training_dry_run()
    LOGGER.info("全部自检通过")


if __name__ == "__main__":
    main()
