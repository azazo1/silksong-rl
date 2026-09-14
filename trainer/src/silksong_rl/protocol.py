"""与游戏端 RLEnv 插件的二进制协议.

帧结构: ``[int32 载荷长度][载荷]``, 载荷首部是 ``[int32 消息类型]``.
所有整数与浮点都是小端 (``struct`` 的 ``<`` 前缀), 与 C# 的 ``BitConverter`` / ``BinaryWriter`` 对齐.
"""

from __future__ import annotations

import struct
from enum import IntEnum

MAGIC = 0x524C454E
PROTOCOL_VERSION = 1
MAX_PAYLOAD_BYTES = 1 << 20


class MessageType(IntEnum):
    """收发双方的消息类型."""

    # Python -> mod
    RESET = 1
    STEP = 2
    CLOSE = 3
    PING = 4
    SET_SPEED = 5
    SET_HUMAN_MODE = 6

    # 一局结束时告诉 mod 这局是不是击杀 (1 = 保存回放画面, 0 = 丢掉)
    SAVE_CLIP = 7

    # 运行时调整决策粒度: 一个 step 几个物理帧 + 单回合步数上限 (0 表示不改这一项)
    SET_STEPPING = 8

    # 运行时调整回放录制: 帧率 + 缓冲秒数 (0 表示不改这一项)
    SET_CLIP = 9

    # mod -> Python
    HELLO = 101
    OBSERVATION = 102
    STATUS = 103
    ERROR = 104
    STATE_MAP = 105
    RECORD = 106


# 观测消息里的标志位.
FLAG_TERMINATED = 1
FLAG_TRUNCATED = 2
FLAG_EPISODE_START = 4

_HEADER = struct.Struct("<i")
_PAYLOAD_HEAD = struct.Struct("<ii")

# 动作空间的形状: [左右, 上下, 跳跃, 攻击, 缚丝].
ACTION_SHAPE = (3, 3, 2, 2, 2)


def encode_int_message(message_type: int, value: int = 0) -> bytes:
    """编码只有消息类型 (可带一个 int 参数) 的消息."""

    payload = _PAYLOAD_HEAD.pack(int(message_type), int(value))
    return _HEADER.pack(len(payload)) + payload


def encode_step(action) -> bytes:
    """编码 Step 消息: 5 个动作分量各占 4 字节."""

    horizontal, vertical, jump, attack, bind = (int(x) for x in action)
    body = struct.pack("<iiiii", horizontal, vertical, jump, attack, bind)
    payload = _HEADER.pack(int(MessageType.STEP)) + body
    return _HEADER.pack(len(payload)) + payload


def encode_set_stepping(step_frames: int = 0, max_episode_steps: int = 0) -> bytes:
    """编码 SetStepping 消息: 两个 int32, 0 表示这一项保持原样."""

    body = struct.pack("<iii", int(MessageType.SET_STEPPING), int(step_frames), int(max_episode_steps))
    return _HEADER.pack(len(body)) + body


def encode_set_clip(fps: int = 0, seconds: int = 0) -> bytes:
    """编码 SetClip 消息: 两个 int32 (帧率, 缓冲秒数), 0 表示这一项保持原样."""

    body = struct.pack("<iii", int(MessageType.SET_CLIP), int(fps), int(seconds))
    return _HEADER.pack(len(body)) + body


def encode_set_speed(speed: float) -> bytes:
    """编码 SetSpeed 消息."""

    payload = _HEADER.pack(int(MessageType.SET_SPEED)) + struct.pack("<f", float(speed))
    return _HEADER.pack(len(payload)) + payload


def decode_payload(payload: bytes) -> tuple[MessageType, object]:
    """解析一帧载荷, 返回 (消息类型, 解析结果).

    观测返回 ``(step_index, flags, values)``; 文本类消息返回 ``str``;
    其余二进制消息 (Step / SetSpeed) 原样返回 ``bytes`` 供调用方按需解包.
    """

    if len(payload) < _HEADER.size:
        raise ValueError(f"载荷太短: {len(payload)} 字节")

    (message_type,) = _HEADER.unpack_from(payload, 0)
    try:
        kind = MessageType(message_type)
    except ValueError as exc:
        raise ValueError(f"未知消息类型: {message_type}") from exc

    body = payload[_HEADER.size :]
    if kind is MessageType.OBSERVATION:
        if len(body) < 8:
            raise ValueError("观测消息头不完整")
        step_index, flags = struct.unpack_from("<ii", body, 0)
        values = struct.unpack_from(f"<{(len(body) - 8) // 4}f", body, 8)
        return kind, (step_index, flags, values)

    if kind is MessageType.RECORD:
        # [int32 stepIndex][float32 x N][int32 x 5]
        if len(body) < 4 + 4 * 5:
            raise ValueError("示范样本消息太短")
        (step_index,) = struct.unpack_from("<i", body, 0)
        obs_count = (len(body) - 4 - 4 * len(ACTION_SHAPE)) // 4
        values = struct.unpack_from(f"<{obs_count}f", body, 4)
        action = struct.unpack_from(f"<{len(ACTION_SHAPE)}i", body, 4 + obs_count * 4)
        return kind, (step_index, values, action)

    if kind in (MessageType.HELLO, MessageType.STATUS, MessageType.ERROR, MessageType.STATE_MAP):
        return kind, body.decode("utf-8", errors="replace")

    return kind, body
