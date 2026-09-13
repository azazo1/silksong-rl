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

# 评估已训练模型: just eval runs/moss-mother-a/final.zip
eval model *args:
    cd trainer && uv run silksong-train --eval --model {{model}} {{args}}

# 行为克隆并直接用 PPO 微调: just bootstrap records/moss-mother-v2
bootstrap data *args:
    cd trainer && uv run silksong-bc --data {{data}} {{args}}
    cd trainer && uv run silksong-train --resume runs/bc/bc.zip --timesteps 200000 --speed 6

# 查看训练曲线
tensorboard:
    cd trainer && uv run tensorboard --logdir runs

# 重新生成反编译产物 (只用于阅读)
decompile:
    pwsh -File disassembly/decompile.ps1
