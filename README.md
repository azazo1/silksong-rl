# Silksong-RL

丝之歌强化学习训练.

游戏子实例在 game 当中, 而不是 steam 目录.

反编译代码在 disassebmly 当中.

scoop 有安装 dnspy, 可以使用这个来进行反编译.

子实例需要提权启动, 不然外部有些文件无法写入.

## 目录

| 路径 | 内容 |
| --- | --- |
| `mods/rl-env` | 强化学习环境插件: 观测采集, 动作注入, 回合重置, 与训练进程通信 |
| `mods/boss-rush` | Boss 复战插件, 提供 42 份 Boss 存档与清单, `rl-env` 复用这套资源 |
| `mods/object-outlines` | 排查用的对象描边插件 |
| `trainer` | Python 训练侧: 环境封装, 奖励计算, PPO 训练与评估 |
| `docs` | 丝之歌 mod 制作方法汇总, 训练设计说明 |
| `disassembly` | 反编译产物, 只用于阅读 |

## 快速上手

```shell
# 编译并安装 RL 环境插件 (含 Boss 存档资源)
just build-rl-env

# 训练侧自检, 不需要开游戏
just selfcheck

# 游戏内: 启动游戏进入任意存档, 插件会等训练侧连接

# 开始训练
just train --timesteps 200000 --run-name moss-mother-a
```

详细说明见 `mods/rl-env/README.md`, `trainer/README.md` 与 `docs/rl-training.md`.
