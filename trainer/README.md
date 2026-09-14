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

# 提高决策频率: 每个决策只推进 3 个物理帧 (默认 6), 单回合步数上限相应放大
uv run silksong-train --step-frames 3 --max-episode-steps 1200

# 训练时另开一个终端看曲线
uv run tensorboard --logdir runs
```

`--step-frames` / `--max-episode-steps` 走运行时协议 (插件侧 `SetStepping`), 不需要改游戏目录
里的配置文件; 步长越小, 每个决策跨的游戏时间越短 (出手时机能卡得更准), 同样的墙钟时间能采到
更多样本, 代价是一局的步数按比例变多. 录制示范时用同样的 `--step-frames`, 否则克隆出来的策略
节奏会和训练时对不上.

### PPO 超参

`--n-steps` / `--n-epochs` / `--batch-size` / `--learning-rate` / `--target-kl` / `--ent-coef` /
`--gamma` 都是标准 PPO 的旋钮; 不显式给时, `--finetune` 模式用一套保守值 (lr 1e-4, 熵 0.003,
KL 上限 0.03, `--gamma` 按决策粒度折算成"约 15 秒视界"). 长时间接着练时值得显式给:

```shell
# 实测: lr 1e-4 时 approx_kl 只有 0.01-0.02 (上限却是 0.03), 策略每步挪得太小;
# 放开到 3e-4 / KL 上限 0.05 之后每刀命中率 30% -> 44%, 每游戏秒伤害 1.68 -> 2.14
uv run silksong-train --resume runs/<实验>/final.zip --finetune --timesteps 500000 \
    --learning-rate 3e-4 --target-kl 0.05 --n-steps 2048
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
所以长时间训练不会堆垃圾.

```shell
# 回放帧率与缓冲时长 (不写就沿用插件配置; 帧率越高越顺, 实测对训练吞吐几乎没影响)
uv run silksong-train --clip-fps 30 --clip-seconds 20
```

合成 mp4 时的帧率与插件抓帧的频率始终一致 (插件在 Hello 里上报自己的设置), 所以回放是按真实
时间播放的, 不会忽快忽慢.

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

想看"到底能不能稳定击杀", 用 20 个回合并把动作按策略分布采样 (`--stochastic`): 多维离散动作的
逐维 argmax 会退化 (实测经常站着不动), 而训练时本来就是按分布采样. 决策粒度也要跟训练时一致
(显式给 `--step-frames`), 否则等于换了环境:

```shell
uv run silksong-train --eval --model runs/<实验>/final.zip --episodes 20 \
    --stochastic --step-frames 3 --max-episode-steps 1200
```

不评估也能从训练日志里估击杀率: `episodes.jsonl` 每回合都有 `boss_kills`, 用
`silksong-report --run runs/<实验>` 或后台看门脚本 (见 `.tmp/watch-run.ps1`) 都能看.

### 轨迹对比

回合日志只说明"每局打了多少伤害", 要定位"刀为什么空"得看逐步轨迹. 评估时加
`--save-episodes` 会把每个回合的 (观测, 动作) 按人类示范的格式落盘, 再用同一个工具
把两边放在一起比:

```shell
uv run silksong-train --eval --model runs/moss-mother-a/final.zip --episodes 6 \
    --stochastic --save-episodes .tmp/policy-traces
uv run silksong-traces --dir records/moss-mother-v3 --against .tmp/policy-traces
```

输出包含命中步占比, 挥刀步占比, 每刀命中率, 各距离档位的站位占比, 命中时的距离分位,
以及各维动作的取值分布; 训练侧的 `steps_within_*` / `hit_steps` / `act_*` 字段是同一批
指标的逐回合版本 (见 `report.py` 的"站位"一行).

## 奖励

奖励在 Python 侧算 (`reward.py`), 修改不需要重编插件. 默认各项:

| 项 | 默认 | 含义 |
| --- | --- | --- |
| `--damage-dealt` | 1.0 | 每对 Boss 造成 1 点伤害 |
| `--damage-taken` | -1.0 | 每次被打中 |
| `--boss-kill` | 25.0 | 击杀 Boss |
| `--player-death` | -25.0 | 自己阵亡 |
| `--step-penalty` | -0.002 | 每个 step 的固定惩罚, 鼓励速战 |
| `--approach` | 0.0 | 可选塑形: 靠近 Boss 的奖励系数 (势函数形式, 不改变最优策略) |
| `--close-reward` | 0.0 | 可选塑形: 停在 `--close-distance` (归一化距离) 以内的每步奖励 |
| `--whiff-penalty` | 0.0 | 可选塑形: 够不着还出刀的每步惩罚 (用世界坐标判定) |
| `--height-reward` | 0.0 | 可选塑形: 与 Boss 的竖直边缘间距在 `--height-tolerance` 内的每步奖励 |
| `--contact-reward` | 0.0 | 可选塑形: 两个方向都几乎贴上 (`--contact-distance`) 的每步奖励 |
| `--inactivity-penalty` | 0.0 | 可选塑形: 超过 `--inactivity-window` 秒没造成伤害的惩罚 (防止学会躲着不打) |
| `--heal-reward` | 0.0 | 可选塑形: 每回一点血的奖励 |
| `--bind-waste-penalty` | 0.0 | 可选塑形: 丝量不够还按住缚丝的每步惩罚 |

`--step-penalty` / `--close-reward` / `--whiff-penalty` / `--height-reward` / `--contact-reward` /
`--bind-waste-penalty` 都是"每步固定量", 会按 `--step-frames` 折算 (`dense_scale = 步长 / 6`),
换决策粒度时每游戏秒的权重不变; 事件型奖励 (伤害/击杀/阵亡/回血/划水) 不受影响.

为什么会有这么多塑形项: 逐条都是从人类示范里量出来的差距, 不是拍脑袋加的 —— 例如
`--height-reward` 对应"人类 38% 的步数挂在 Boss 高度上, 而策略一直贴地面", `--whiff-penalty`
对应"策略一半的挥刀发生在够不着的距离外". 每项的标定过程与实测数字记在
`.tmp/kill-progress.md`; 打 Boss 时先只用伤害/击杀, 需要时再逐项打开.

伤害数值来自插件对 `HealthManager.Hit` 的 hp 前后差统计, 不是面板数值, 已经过游戏的
伤害缩放与免疫判定.

### 示范先验

```shell
uv run silksong-train --resume runs/bc/bc.zip --demo-anchor records/moss-mother-v3 --demo-weight 0.1
```

`--demo-anchor` 会在每个 rollout 结束后, 用随机一批示范样本算一遍负对数似然并补一步梯度
(观测走与训练完全相同的屏蔽/状态重映射/归一化流程). 它是"软先验"而不是硬模仿: 权重给小一点,
只用来补 RL 自己不容易探索到的习惯 (例如"远离 Boss 时朝它跑"), 具体见 `src/silksong_rl/imitation.py`.

同一个参数可以给多次, 也可以配 `--save-kills` 做**自模仿**: 每次击杀那局的 (观测, 动作) 会按示范
格式落盘 (只留最近 4 局, 免得把人类示范淹掉), 下一段训练时把它一起当先验 —— 策略自己打出来的
成功轨迹, 状态分布比人类示范更贴当前策略.

```shell
uv run silksong-train --resume runs/bc/bc.zip \
    --demo-anchor records/moss-mother-v3 --demo-anchor runs/self-imitation \
    --save-kills runs/self-imitation
```

### 工程化特征 (可选, 不动观测维度)

`--extra-feature range` 会把"离够得着还差多远"写进观测里那列被屏蔽的 `physics_frame`
(负值表示已经贴在挥刀范围内). 这列本来没有任何可用信息, 而插件 schema 的列数是固定的,
所以征用它不改变观测维度, 老 checkpoint 照常能 load, 适合拿来做 A/B.

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
| `src/silksong_rl/trace.py` | 评估回合的轨迹落盘 |
| `src/silksong_rl/traces.py` | 示范与策略轨迹的同口径对比 |
| `src/silksong_rl/selfcheck.py` | 不依赖游戏的自检 |
