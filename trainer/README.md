# 训练侧 (Python)

与 `mods/rl-env` 插件配套的强化学习训练代码. 插件负责采集观测 / 注入动作 / 编排回合,
这里负责环境封装, 奖励计算与 PPO 训练.

## 环境要求

- Python 3.11 或 3.12 (由 uv 管理, 不需要手动装).
- NVIDIA 显卡 (torch 从 PyTorch 官方 CUDA 源安装, 见 `pyproject.toml`).
- 游戏已经在跑, 并且装有 `RLEnv` 插件 (见 `mods/rl-env/README.md`).

## 安装依赖

```shell
cd trainer
uv sync
```

## 自检 (不需要开游戏)

自检会解析 C# 侧的观测字段表, 跑一遍协议编解码, 并用一个假游戏端把
`reset / step / 终止 / 再重置` 全流程走通:

```shell
uv run silksong-selfcheck
```

## 联调冒烟 (需要游戏在跑)

先确认整条链路通了, 再谈训练:

```shell
uv run silksong-smoke --steps 30 --policy scripted
```

它会连上插件, 重置一个回合, 然后按固定动作序列 (右移 / 跳跃 / 攻击) 跑若干步,
每步打印关键观测与奖励, 用来肉眼确认动作真的注入了、观测真的在变、每步耗时是多少.

## 训练

```shell
# 默认连 127.0.0.1:5555
uv run silksong-train --timesteps 200000

# 指定实验名与游戏内时间倍率
uv run silksong-train --timesteps 500000 --run-name moss-mother-a --speed 4

# 从 checkpoint 继续
uv run silksong-train --resume runs/moss-mother-a/final.zip \
  --vecnormalize runs/moss-mother-a/vecnormalize.pkl --timesteps 500000

# 训练时另开一个终端看曲线
uv run tensorboard --logdir runs
```

产物都落在 `runs/<实验名>/`: `final.zip` (模型), `checkpoints/` (周期存档),
`vecnormalize.pkl` (观测归一化统计), `tb/` (TensorBoard).

## 评估

```shell
uv run silksong-train --eval --model runs/moss-mother-a/final.zip --episodes 5
```

评估同样需要游戏在跑, 且插件处于等待 Reset 的状态.

## 奖励

奖励在 Python 侧算 (`reward.py`), 修改不需要重编插件. 默认各项:

| 项 | 默认 | 含义 |
| --- | --- | --- |
| `--damage-dealt` | 1.0 | 每对 Boss 造成 1 点伤害 |
| `--damage-taken` | -1.0 | 每次被打中 |
| `--boss-kill` | 25.0 | 击杀 Boss |
| `--player-death` | -25.0 | 自己阵亡 |
| `--step-penalty` | -0.002 | 每个 step 的固定惩罚, 鼓励速战 |
| `--approach` | 0.0 | 可选塑形: 靠近 Boss 的奖励系数 |

伤害数值来自插件对 `HealthManager.Hit` 的 hp 前后差统计, 不是面板数值, 已经过游戏的
伤害缩放与免疫判定.

## 动作空间

`MultiDiscrete([3, 3, 2, 2])`, 含义是 `[左右, 上下, 跳跃, 攻击]`:

| 维度 | 取值 |
| --- | --- |
| 左右 | 0 不动, 1 左, 2 右 |
| 上下 | 0 不动, 1 上, 2 下 |
| 跳跃 | 0 松开, 1 按住 |
| 攻击 | 0 松开, 1 按住 |

一个 step 内按键保持按住, 与真人握手柄一致. 攻击方向由"上下"与主角朝向决定
(上劈 / 前劈 / 下劈). 跳跃松开会把上升速度砍半, 所以想跳高就得按住.

## 观测

观测字段由插件在连接时通过 `Hello` 消息上报 (`mods/rl-env/src/Observation/ObservationSchema.cs`
是唯一权威定义), 训练侧按名字取值, 不硬编码下标. 训练用 `VecNormalize` 做观测归一化.

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/silksong_rl/protocol.py` | 二进制协议定义 (与 C# 侧一一对应) |
| `src/silksong_rl/client.py` | TCP 客户端, 负责收发与消息分发 |
| `src/silksong_rl/env.py` | Gymnasium 环境 |
| `src/silksong_rl/reward.py` | 奖励计算 |
| `src/silksong_rl/train.py` | 训练与评估入口 |
| `src/silksong_rl/selfcheck.py` | 不依赖游戏的自检 |
