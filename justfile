# 项目里常用操作的入口. 列出全部 recipe: just --list
[private]
default:
    @just --list

# 编译并安装强化学习环境插件, 同时铺设 Boss 存档资源
build-rl-env:
    pwsh -File mods/rl-env/build.ps1 -Install

# 编译并安装 Boss 复战插件
build-boss-rush:
    pwsh -File mods/boss-rush/build.ps1 -Install

# 编译并安装对象描边插件
build-object-outlines:
    pwsh -File mods/object-outlines/build.ps1 -Install

# 训练侧自检, 不需要开游戏: just selfcheck
selfcheck:
    cd trainer && uv run silksong-selfcheck

# 联调冒烟: 连上正在运行的游戏, 重置一回合并跑若干步: just smoke --steps 40
smoke *args:
    cd trainer && uv run silksong-smoke {{args}}

# 录制人类示范: just record --episodes 5 --out records/moss-mother
record *args:
    cd trainer && uv run silksong-record {{args}}

# 用人类示范做行为克隆: just bc records/moss-mother
bc data *args:
    cd trainer && uv run silksong-bc --data {{data}} {{args}}

# 开始训练: just train --timesteps 200000 --run-name moss-mother-a
train *args:
    cd trainer && uv run silksong-train {{args}}

# 分段接力训练, 夜里长时间跑用: just chain 5 100000
chain segments timesteps:
    pwsh -File tools/train-chain.ps1 -Segments {{segments}} -Timesteps {{timesteps}}

# 评估已训练模型: just eval runs/moss-mother-a/final.zip
eval model *args:
    cd trainer && uv run silksong-train --eval --model {{model}} {{args}}

# 行为克隆并直接用 PPO 微调: just bootstrap records/moss-mother-v2
bootstrap data *args:
    cd trainer && uv run silksong-bc --data {{data}} {{args}}
    cd trainer && uv run silksong-train --resume runs/bc/bc.zip --finetune --timesteps 200000 --speed 6

# 汇总某个实验的逐回合日志, 不连游戏: just report runs/bc-ft-v4
report run *args:
    cd trainer && uv run silksong-report --run {{run}} {{args}}

# 汇总 runs 下所有实验的逐回合日志
report-all:
    cd trainer && uv run silksong-report --all

# 把 runs 下的实验并排成一张窄表, 用来比较不同配置
report-compare:
    cd trainer && uv run silksong-report --compare

# 对比示范与策略轨迹 (策略轨迹用 just eval <模型> --save-episodes .tmp/policy-traces 生成)
# 用法: just traces trainer/records/moss-mother-v3
#       just traces trainer/records/moss-mother-v3 .tmp/policy-traces
traces dir against='':
    cd trainer && uv run silksong-traces --dir {{dir}} {{ if against == '' { '' } else { '--against ' + against } }}

# 查看训练曲线
tensorboard:
    cd trainer && uv run tensorboard --logdir runs

# 重新生成反编译产物 (只用于阅读)
decompile:
    pwsh -File disassembly/decompile.ps1
