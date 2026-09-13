"""丝之歌 Boss 强化学习训练代码.

模块划分:

- ``protocol``: 与游戏端 mod 的二进制协议 (消息类型, 帧格式, 标志位).
- ``client``: 与游戏内插件通信的 TCP 客户端.
- ``env``: Gymnasium 环境封装.
- ``reward``: 奖励计算 (全部在 Python 侧, 改奖励不需要重编 mod).
- ``train``: 训练入口.
- ``selfcheck``: 不连游戏也能跑的自检 (协议与奖励).
"""

__version__ = "0.1.0"
