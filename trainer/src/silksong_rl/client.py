"""与游戏内 RLEnv 插件通信的 TCP 客户端."""

from __future__ import annotations

import json
import logging
import socket
import struct
import time
from dataclasses import dataclass, field

import numpy as np

from . import protocol

LOGGER = logging.getLogger(__name__)

_HEADER = struct.Struct("<i")


class EnvProtocolError(RuntimeError):
    """协议层错误 (消息异常, 连接断开等)."""


@dataclass
class Observation:
    """一帧观测: 原始向量加按名字索引的取值."""

    values: np.ndarray
    named: dict[str, float] = field(default_factory=dict)
    step_index: int = 0
    flags: int = 0

    @property
    def terminated(self) -> bool:
        return bool(self.flags & protocol.FLAG_TERMINATED)

    @property
    def truncated(self) -> bool:
        return bool(self.flags & protocol.FLAG_TRUNCATED)

    @property
    def episode_start(self) -> bool:
        return bool(self.flags & protocol.FLAG_EPISODE_START)


class EnvClient:
    """一条到游戏实例的连接.

    一个游戏进程同一时刻只服务一个客户端, 因此每个环境实例各自持有一个 EnvClient.
    """

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 5555,
        connect_timeout: float = 20.0,
        reset_timeout: float = 90.0,
        step_timeout: float = 60.0,
    ) -> None:
        self.host = host
        self.port = port
        self.connect_timeout = connect_timeout
        self.reset_timeout = reset_timeout
        self.step_timeout = step_timeout

        self._socket: socket.socket | None = None
        self._hello: dict = {}
        self._field_names: list[str] = []
        self._state_map: dict[int, str] = {}
        self.last_status: str = ""
        self.action_shape: tuple[int, ...] = protocol.ACTION_SHAPE
        self.step_frames: int = 0
        self.max_episode_steps: int = 0

    # --- 生命周期 ---------------------------------------------------------

    @property
    def connected(self) -> bool:
        return self._socket is not None

    @property
    def field_names(self) -> list[str]:
        return self._field_names

    @property
    def state_map(self) -> dict[int, str]:
        return self._state_map

    @property
    def hello(self) -> dict:
        return self._hello

    def connect(self) -> None:
        """建立连接并等待插件的 Hello 消息."""

        if self._socket is not None:
            return

        deadline = time.monotonic() + self.connect_timeout
        last_error: Exception | None = None
        while time.monotonic() < deadline:
            try:
                sock = socket.create_connection((self.host, self.port), timeout=2.0)
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                self._socket = sock
                LOGGER.info("已连接游戏端 %s:%s", self.host, self.port)
                break
            except OSError as exc:  # 游戏还没起来时不停重试
                last_error = exc
                time.sleep(0.5)

        if self._socket is None:
            raise EnvProtocolError(f"连接 {self.host}:{self.port} 失败: {last_error}")

        self._await_hello(self.connect_timeout)

    def close(self) -> None:
        """向游戏端发 Close 并断开."""

        if self._socket is None:
            return

        try:
            self._send(protocol.encode_int_message(protocol.MessageType.CLOSE))
        except OSError:
            pass

        try:
            self._socket.close()
        finally:
            self._socket = None

    # --- 命令 -------------------------------------------------------------

    def set_speed(self, speed: float) -> None:
        self._send(protocol.encode_set_speed(speed))

    def set_human_mode(self, enabled: bool) -> None:
        """进入/退出人类示范录制模式 (mod 侧不注入输入, 按常速运行)."""

        self._send(protocol.encode_int_message(protocol.MessageType.SET_HUMAN_MODE, 1 if enabled else 0))

    def set_stepping(self, step_frames: int = 0, max_episode_steps: int = 0) -> None:
        """调整决策粒度: 一个 step 对应几个物理帧, 以及单回合步数上限 (0 = 不改)."""

        if step_frames <= 0 and max_episode_steps <= 0:
            return

        self._send(protocol.encode_set_stepping(step_frames, max_episode_steps))
        LOGGER.info("已请求决策粒度: 每步 %s 物理帧, 单回合上限 %s 步", step_frames or "不变", max_episode_steps or "不变")

    def set_clip(self, fps: int = 0, seconds: int = 0) -> None:
        """调整回放录制: 抓帧频率与内存缓冲时长 (0 = 不改)."""

        if fps <= 0 and seconds <= 0:
            return

        self._send(protocol.encode_set_clip(fps, seconds))
        LOGGER.info("已请求回放录制: %s 帧/秒, 缓冲 %s 秒", fps or "不变", seconds or "不变")

    def save_clip(self, save: bool, timeout: float = 5.0) -> str | None:
        """告诉 mod 这一局是不是击杀; 击杀时返回它落盘的画面目录, 否则返回 None.

        mod 会把最近若干秒的画面缓冲写成 JPEG 序列, 目录路径通过 Status 消息回传.
        """

        self._send(protocol.encode_int_message(protocol.MessageType.SAVE_CLIP, 1 if save else 0))
        if not save:
            return None

        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                LOGGER.warning("等回放片段路径超时")
                return None

            kind, body = self.read_message(timeout=remaining)
            if kind is not protocol.MessageType.STATUS:
                continue

            # Status 是 {"state": ..., "message": ...} 的 JSON, 路径在 message 里.
            try:
                message = str(json.loads(str(body)).get("message", ""))
            except json.JSONDecodeError:
                message = str(body)

            if message.startswith("clip:"):
                return message[len("clip:") :]

            if "片段" in message:
                # 例如"没有可保存的回放片段": 不用再等.
                LOGGER.debug("回放状态: %s", message)
                return None

            LOGGER.debug("回放相关状态: %s", message)

    def read_message(self, timeout: float | None = 30.0) -> tuple[protocol.MessageType, object]:
        """读一条消息, timeout 为 None 时一直等."""

        if timeout is None:
            if self._socket is not None:
                self._socket.settimeout(None)
            kind, body = self._read_message(None)
        else:
            kind, body = self._read_message(timeout)

        if kind is protocol.MessageType.STATE_MAP:
            self.merge_state_map(body)
        elif kind is protocol.MessageType.STATUS:
            self.last_status = str(body)

        return kind, body

    def merge_state_map(self, body: object) -> None:
        """把 mod 发来的 Boss 状态 id 映射并进来."""

        self._merge_state_map(body)

    def reset(self) -> Observation:
        """请求重置回合, 返回回合的第一帧观测."""

        started = time.monotonic()
        self._send(protocol.encode_int_message(protocol.MessageType.RESET))
        observation = self._await_observation(self.reset_timeout, want_episode_start=True, phase="reset")
        LOGGER.debug("重置耗时 %.2f 秒", time.monotonic() - started)
        return observation

    def step(self, action) -> Observation:
        """发送一个动作并等待这一步的结果."""

        self._send(protocol.encode_step(action))
        return self._await_observation(self.step_timeout, want_episode_start=False, phase="step")

    # --- 内部 -------------------------------------------------------------

    def _send(self, data: bytes) -> None:
        if self._socket is None:
            raise EnvProtocolError("连接尚未建立")

        try:
            self._socket.sendall(data)
        except OSError as exc:
            self._socket = None
            raise EnvProtocolError(f"发送失败: {exc}") from exc

    def _recv_exactly(self, count: int, timeout: float | None) -> bytes:
        if self._socket is None:
            raise EnvProtocolError("连接尚未建立")

        self._socket.settimeout(timeout)
        buffer = bytearray()
        while len(buffer) < count:
            try:
                chunk = self._socket.recv(count - len(buffer))
            except socket.timeout as exc:
                raise EnvProtocolError(f"等待游戏端响应超时 ({timeout} 秒)") from exc
            except OSError as exc:
                self._socket = None
                raise EnvProtocolError(f"接收失败: {exc}") from exc

            if not chunk:
                self._socket = None
                raise EnvProtocolError("游戏端关闭了连接")

            buffer.extend(chunk)

        return bytes(buffer)

    def _read_message(self, timeout: float | None) -> tuple[protocol.MessageType, object]:
        header = self._recv_exactly(_HEADER.size, timeout)
        (length,) = _HEADER.unpack(header)
        if length < _HEADER.size or length > protocol.MAX_PAYLOAD_BYTES:
            raise EnvProtocolError(f"非法载荷长度: {length}")

        payload = self._recv_exactly(length, timeout)
        return protocol.decode_payload(payload)

    def _await_hello(self, timeout: float) -> None:
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise EnvProtocolError("等待 Hello 消息超时")

            kind, body = self._read_message(remaining)
            if kind is protocol.MessageType.HELLO:
                self._apply_hello(body)
                return
            if kind is protocol.MessageType.STATUS:
                self.last_status = str(body)
                continue
            if kind is protocol.MessageType.ERROR:
                raise EnvProtocolError(f"游戏端报错: {body}")

    def _apply_hello(self, body: object) -> None:
        if not isinstance(body, str):
            raise EnvProtocolError("Hello 消息不是文本")

        hello = json.loads(body)
        if hello.get("magic") != protocol.MAGIC:
            raise EnvProtocolError(f"协议魔数不匹配: {hello.get('magic')}")
        if hello.get("protocol") != protocol.PROTOCOL_VERSION:
            raise EnvProtocolError(
                f"协议版本不匹配: 游戏端 {hello.get('protocol')}, 训练端 {protocol.PROTOCOL_VERSION}"
            )

        self._hello = hello
        self._field_names = list(hello.get("observations", []))
        action = hello.get("action", {})
        self.action_shape = (
            int(action.get("horizontal", 3)),
            int(action.get("vertical", 3)),
            int(action.get("jump", 2)),
            int(action.get("attack", 2)),
            int(action.get("bind", 2)),
        )
        self.step_frames = int(hello.get("step_frames", 0))
        self.max_episode_steps = int(hello.get("max_episode_steps", 0))
        LOGGER.info(
            "游戏端就绪: Boss %s, 场景 %s, 观测 %d 维, 每步 %d 物理帧",
            hello.get("boss"),
            hello.get("scene"),
            len(self._field_names),
            self.step_frames,
        )

    def _await_observation(self, timeout: float, want_episode_start: bool, phase: str) -> Observation:
        deadline = time.monotonic() + timeout
        skipped = 0
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise EnvProtocolError(f"{phase} 阶段等待观测超时 ({timeout} 秒)")

            kind, body = self._read_message(remaining)
            if kind is protocol.MessageType.OBSERVATION:
                observation = self._build_observation(body)  # type: ignore[arg-type]
                if want_episode_start and not observation.episode_start:
                    skipped += 1
                    continue
                if skipped:
                    LOGGER.debug("%s 阶段跳过了 %d 帧陈旧观测", phase, skipped)
                return observation
            if kind is protocol.MessageType.STATUS:
                self.last_status = str(body)
                LOGGER.debug("游戏端状态: %s", self.last_status)
                continue
            if kind is protocol.MessageType.STATE_MAP:
                self._merge_state_map(body)
                continue
            if kind is protocol.MessageType.ERROR:
                raise EnvProtocolError(f"游戏端报错: {body}")
            if kind is protocol.MessageType.HELLO:
                self._apply_hello(body)

    def _merge_state_map(self, body: object) -> None:
        if not isinstance(body, str) or not body:
            return

        try:
            mapping = json.loads(body)
        except json.JSONDecodeError:
            LOGGER.warning("StateMap 解析失败: %s", body[:120])
            return

        for key, value in mapping.items():
            try:
                self._state_map[int(key)] = str(value)
            except ValueError:
                continue

        LOGGER.debug("新增 Boss 状态映射, 当前共 %d 个", len(self._state_map))

    def _build_observation(self, body: tuple[int, int, tuple[float, ...]]) -> Observation:
        step_index, flags, values = body
        array = np.asarray(values, dtype=np.float32)
        named: dict[str, float] = {}
        if self._field_names:
            limit = min(len(self._field_names), array.size)
            for index in range(limit):
                named[self._field_names[index]] = float(array[index])

        return Observation(values=array, named=named, step_index=step_index, flags=flags)
