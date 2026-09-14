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

## 推荐流程: 先录示范, 再做强化学习

从零开始靠随机探索打赢 Boss 非常慢, 因此先录几局自己的操作做行为克隆, 再用 PPO 微调.

### 1. 录制人类示范

游戏开好, 插件装好, 确保没有别的训练进程连着, 然后:

```shell
uv run silksong-record --episodes 5 --out records/moss-mother
```

脚本会自己把 Boss 房准备好并交给你操作: 死了或打赢了都会自动开下一局, 每局存一个
`episode-NNN.npz`. 录制期间不注入任何输入, 游戏按常速运行.

### 2. 行为克隆

```shell
uv run silksong-bc --data records/moss-mother --epochs 200
```

产物是标准的 stable-baselines3 PPO 模型 `runs/bc/bc.zip`, 可以直接评估, 也可以接着做强化学习.

示范数据通常只有几千条, 训久了会过拟合, 因此克隆默认带权重衰减并按验证损失早停 (会回滚到
验证损失最低的那一版权重), 日志里按维度报准确率, 例如
`单维准确率 0.931 [左右:0.92 上下:0.98 跳跃:0.93 攻击:0.84 缚丝:0.99]`.

### 3. PPO 微调

```shell
uv run silksong-train --resume runs/bc/bc.zip --finetune --timesteps 200000 --speed 6
```

`--finetune` 会用保守的超参 (学习率 1e-4, 熵系数 0.003, KL 上限 0.03): 行为克隆出来的策略
是个能用的起点, 一上来用默认学习率容易把它冲掉, 变成"重新随机探索".

训练产物里除了模型与归一化统计, 还有 `episodes.jsonl`: 每个回合一行, 记录步数, 回报, 长度,
造成/受到伤害, 击杀与阵亡, 便于训练结束后离线分析. 里面还有几个诊断量, 用来区分"打不动"的
两种原因: `attack_steps` (主角真的挥出刀的步数) 与 `damage_per_swing` (每挥一刀平均造成多少伤害).
挥刀少说明根本不出手, 挥刀多而每刀伤害低说明一直在够不着的距离上空挥.

`--resume` 时命令行给的超参必须走 `PPO.load(..., **kwargs)`: SB3 的 load 会先用存档里的值覆盖
`model.__dict__`, 再用 kwargs 覆盖一次, 所以写成"load 完再赋值"或"干脆不传"都会被存档里的
`n_steps` / `batch_size` / `learning_rate` / `ent_coef` / `target_kl` 悄悄顶掉 (行为克隆产出的
存档里带的是 `n_steps=2048, batch_size=64, ent_coef=0, target_kl=None`), 后果是微调以远大于
预期的步长更新, 策略在几千步内被冲坏. 每次训练开始都会打印一行 `生效超参`, 拿它对照预期即可.

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
`vecnormalize.pkl` (观测归一化统计), `state_vocab.json` (Boss 状态词表), `tb/` (TensorBoard),
`episodes.jsonl` (逐回合日志), `kills/` (击杀回放 mp4).
周期存档旁边会同时落一份同一时刻的归一化统计与状态词表, 因此
`--resume runs/<实验名>/checkpoints/ppo_20000_steps.zip` 可以直接续训, 不会因为找不到这两样
而让网络看到不同的输入 (统计从头来过 / 状态列被静默清零).

### 击杀回放

训练时插件会把最近若干秒的游戏画面滚动存在内存里 (其它窗口盖在上面也能抓到), 一局击杀才落盘,
由训练侧用 ffmpeg 合成 `runs/<实验名>/kills/kill-NNN-HHMMSS.mp4` 并删掉原始帧; 没击杀就丢掉,
所以长时间训练不会堆垃圾. 抓帧频率等参数在插件的 `ClipFps` / `ClipSeconds` 配置里.

随时看某个实验的分段趋势 (不连游戏, 训练进行中也能看):

```shell
uv run silksong-report --run runs/moss-mother-a --bins 6
uv run silksong-report --all
```

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

`MultiDiscrete([3, 3, 2, 2, 2])`, 含义是 `[左右, 上下, 跳跃, 攻击, 缚丝]`:

| 维度 | 取值 |
| --- | --- |
| 左右 | 0 不动, 1 左, 2 右 |
| 上下 | 0 不动, 1 上, 2 下 |
| 跳跃 | 0 松开, 1 按住 |
| 攻击 | 0 松开, 1 按住 |
| 缚丝 | 0 松开, 1 按住 (满丝时缚丝回 3 点血) |

一个 step 内按键保持按住, 与真人握手柄一致. 攻击方向由"上下"与主角朝向决定
(上劈 / 前劈 / 下劈). 跳跃松开会把上升速度砍半, 所以想跳高就得按住.

## 观测

观测字段由插件在连接时通过 `Hello` 消息上报 (`mods/rl-env/src/Observation/ObservationSchema.cs`
是唯一权威定义), 训练侧按名字取值, 不硬编码下标. 训练用 `VecNormalize` 做观测归一化.

其中 `physics_frame` 这一列在训练侧被固定清零 (见 `fields.py`): 它的取值取决于这局游戏从启动
到现在跑了多久, 示范与训练的取值范围完全不同. `boss_state_id` / `boss_fsm0..3_state_id` 则是
"每个会话重新编号"的, 直接当特征用会把同一个数字的不同含义喂给网络, 所以:

- 录制时插件给出的 `编号 -> 状态名` 会随示范一起存成 `state-map-*.json`, 每局 `.npz` 里也记着
  自己属于哪份映射;
- 行为克隆按状态名建一份稳定词表 (`state_vocab.json`, 与 `bc.zip` 放在一起) 并把示范重映射过去;
- 训练与评估时 `train.py` 自动找 `--resume` / `--model` 旁边的词表, 用当前会话的映射把同一批
  状态映射到同一套编号上, 于是"Boss 处于哪个动作状态"就成了可用特征.

拿不到映射的旧录像 (没有 `state-map-*.json`) 只能退回清零这几列. 没有映射时训练侧同样清零,
两边行为一致.

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/silksong_rl/protocol.py` | 二进制协议定义 (与 C# 侧一一对应) |
| `src/silksong_rl/client.py` | TCP 客户端, 负责收发与消息分发 |
| `src/silksong_rl/env.py` | Gymnasium 环境 |
| `src/silksong_rl/reward.py` | 奖励计算 |
| `src/silksong_rl/fields.py` | 需要在训练侧屏蔽的会话相关观测列 |
| `src/silksong_rl/state_ids.py` | Boss 状态编号的跨会话对齐 (词表与重映射) |
| `src/silksong_rl/clips.py` | 把插件落盘的画面帧合成成回放 mp4 |
| `src/silksong_rl/train.py` | 训练与评估入口 |
| `src/silksong_rl/record.py` | 人类示范录制 |
| `src/silksong_rl/dataset.py` | 示范数据的载入与归一化 |
| `src/silksong_rl/bc.py` | 行为克隆 |
| `src/silksong_rl/smoke.py` | 与游戏联调的冒烟测试 |
| `src/silksong_rl/selfcheck.py` | 不依赖游戏的自检 |
