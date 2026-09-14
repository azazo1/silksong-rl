"""训练结果速览: 把 runs/<实验>/episodes.jsonl 汇总成一张表.

用法::

    uv run silksong-report --run runs/bc-ft-v3
    uv run silksong-report --all

只读日志, 不连游戏, 训练进行中也能随时看.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
from pathlib import Path

from .env import DISTANCE_BUCKETS, bucket_key

LOGGER = logging.getLogger("silksong_rl.report")

# 一次挥刀的动画时长 (秒): 人类示范里 300 次出刀平均 2.7 步, 每步 0.1 秒.
SWING_SECONDS = 0.24

# 单元格里放的统计量: (标题, 字段名, 小数位, 是否带符号)
# 列宽有限, 只放判断"打不动"最关键的几个: 按下刀 (出不出手), 贴近步 (有没有贴上去),
# 每按伤害 (挥了到底打中没有). 更细的挥刀步 / 每刀伤害 / 最近距离在 episodes.jsonl 里.
SUMMARY_FIELDS = (
    ("回报", "reward", 2, True),
    ("长度", "length", 1, False),
    ("造成伤害", "damage_dealt", 1, False),
    ("受到伤害", "damage_taken", 1, False),
    ("按下刀", "attack_pressed_steps", 1, False),
    ("贴近步", "close_steps", 1, False),
    ("每按伤害", "damage_per_press", 2, False),
    ("重置秒", "reset_seconds", 1, False),
)


def load_episodes(path: Path) -> list[dict]:
    """读 episodes.jsonl, 跳过写了一半的行."""

    records: list[dict] = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError:
                LOGGER.warning("跳过无法解析的一行: %s", line[:80])
    return records


def split_bins(records: list[dict], bins: int) -> list[list[dict]]:
    """按回合顺序均分成若干段, 便于看趋势."""

    if bins <= 1 or len(records) <= bins:
        return [records]

    size = len(records) / bins
    return [records[int(index * size) : int((index + 1) * size)] for index in range(bins)]


def mean(values: list[float]) -> float:
    return sum(values) / len(values) if values else float("nan")


def field_mean(records: list[dict], key: str) -> float:
    """只统计真的带这个字段的回合; 老日志里没有的字段返回 NaN, 显示成 '-'."""

    return mean([float(record[key]) for record in records if key in record])


def format_value(value: float, digits: int, signed: bool) -> str:
    if value != value:  # NaN
        return "-"

    text = f"{value:.{digits}f}"
    return f"+{text}" if signed and value >= 0 else text


def describe(records: list[dict]) -> str:
    """整体一行: 回合数, 胜负, 以及各统计量的均值."""

    wins = sum(1 for record in records if record.get("boss_kills", 0.0) > 0.0)
    losses = sum(1 for record in records if record.get("player_deaths", 0.0) > 0.0)
    timeouts = len(records) - wins - losses

    parts = [
        f"{len(records)} 回合",
        f"胜 {wins}",
        f"负 {losses}",
        f"超时 {timeouts}",
    ]
    for title, key, digits, signed in SUMMARY_FIELDS:
        parts.append(f"{title} {format_value(field_mean(records, key), digits, signed)}")

    return ", ".join(parts)


def describe_bins(bins: list[list[dict]]) -> str:
    """分段表格: 每段一行, 最后附一条用 # 画的分段柱状图."""

    lines = ["", f"分段趋势 (共 {sum(len(b) for b in bins)} 回合, 每段约 {len(bins[0])} 回合):"]

    header = f"{'段':>3} {'回合':>11} {'步数':>11}"
    for title, _, _, _ in SUMMARY_FIELDS:
        header += f" {title:>{max(len(title) * 2, 8)}}"
    lines.append(header)

    first = 1
    damage_series: list[float] = []
    for index, group in enumerate(bins, start=1):
        last = first + len(group) - 1
        step_range = f"{group[0].get('step', 0) // 1000}-{group[-1].get('step', 0) // 1000}k"
        row = f"{index:>3} {f'{first}-{last}':>11} {step_range:>11}"
        for _, key, digits, signed in SUMMARY_FIELDS:
            row += f" {format_value(field_mean(group, key), digits, signed):>{max(len(key) * 2, 8)}}"
        lines.append(row)
        damage_series.append(field_mean(group, "damage_dealt"))
        first = last + 1

    lines.append("")
    lines.append("每段平均造成伤害:")
    scale = max(damage_series) if damage_series else 1.0
    for index, value in enumerate(damage_series, start=1):
        width = int(round(value / scale * 40)) if scale > 0 else 0
        lines.append(f"  第 {index} 段 {value:6.1f} {'#' * width}")

    return "\n".join(lines)


def describe_placement(records: list[dict]) -> str:
    """站位诊断行: 各距离档位占了多少步, 以及真正打中的步占多少.

    人类示范的参照值 (12 局全击杀): 距离 <0.3 占 18.6%, <0.5 占 40.1%, 命中步占 8.6%,
    而"造成伤害"上不去基本都卡在站位, 所以这行比回报更能说明问题.
    """

    steps = field_mean(records, "length")
    if steps != steps or steps <= 0:
        return "站位: 回合日志里没有步数, 跳过"

    parts = []
    for threshold in DISTANCE_BUCKETS:
        key = bucket_key(threshold)
        if not any(key in record for record in records):
            continue
        parts.append(f"<{threshold:g} {field_mean(records, key) / steps * 100:.1f}%")
    if not parts:
        return "站位: 这份日志没有距离分档 (旧版本写的), 重新跑一轮才有"

    if any("hit_steps" in record for record in records):
        hits = field_mean(records, "hit_steps")
        parts.append(f"命中步 {hits / steps * 100:.1f}%")
        # 每次出刀打中多少: 人类示范是 92% (按刀算). 挥刀动画约 0.24 秒, 除以步长就知道
        # 一局出了多少刀 —— 换过决策粒度之后 "挥刀步" 不能直接跟人比.
        dt = field_mean(records, "step_seconds")
        swings = field_mean(records, "attack_steps")
        if dt == dt and dt > 0 and swings > 0:
            parts.append(f"每刀命中 {hits / (swings * dt / SWING_SECONDS) * 100:.0f}%")
        whiffs = field_mean(records, "whiff_steps")
        if whiffs == whiffs:
            parts.append(f"挥空 {whiffs / steps * 100:.1f}%")

    return "站位: " + " ".join(parts)


def report_run(path: Path, bins: int) -> bool:
    records = load_episodes(path)
    if not records:
        LOGGER.warning("%s 里没有回合记录", path)
        return False

    LOGGER.info("%s: %s", path.parent.name, describe(records))
    print(describe_placement(records))
    print(describe_bins(split_bins(records, bins)))
    return True


def per_swing_hit_rate(records: list[dict]) -> float:
    """每刀命中率 (按刀算, 不是按步): 需要日志里有 swings / whiffed_swings."""

    if not any("swings" in record for record in records):
        return float("nan")

    swings = field_mean(records, "swings")
    whiffs = field_mean(records, "whiffed_swings")
    if swings <= 0:
        return float("nan")
    return (swings - whiffs) / swings


def damage_per_second(records: list[dict]) -> float:
    """每游戏秒伤害: 换过决策粒度之后, "每步伤害" 没法跨实验比, 这个可以."""

    seconds = field_mean(records, "step_seconds")
    length = field_mean(records, "length")
    if seconds != seconds or seconds <= 0 or length <= 0:
        return float("nan")
    return field_mean(records, "damage_dealt") / (length * seconds)


def compare_runs(paths: list[Path]) -> None:
    """把多个实验并排成一张窄表, 用来看"换了配置之后到底有没有变化".

    除了胜场与伤害, 还给出每游戏秒伤害与每刀命中率 —— 前者跨决策粒度可比, 后者是"执行精度"
    唯一直接的量. 趋势列取后两段的造成伤害之差: 还在涨 / 差不多 / 在掉.
    """

    header = (
        f"{'实验':<16} {'回合':>5} {'胜':>4} {'击杀率':>7} {'伤害/秒':>8} {'每刀命中':>8} "
        f"{'伤害':>7} {'贴近步':>7} {'每按伤害':>8} {'后段趋势':>10}"
    )
    print(header)
    print("-" * len(header))

    for path in paths:
        records = load_episodes(path)
        if not records:
            continue

        wins = sum(1 for record in records if record.get("boss_kills", 0.0) > 0.0)
        groups = split_bins(records, 3)
        if len(groups) >= 2:
            earlier = field_mean(groups[-2], "damage_dealt")
            later = field_mean(groups[-1], "damage_dealt")
            delta = later - earlier
            trend = f"{delta:+.1f}"
        else:
            trend = "-"

        print(
            f"{path.parent.name:<16} {len(records):>5} {wins:>4} "
            f"{wins / len(records) * 100:>6.1f}% "
            f"{format_value(damage_per_second(records), 2, False):>8} "
            f"{format_value(per_swing_hit_rate(records) * 100, 0, False):>7}% "
            f"{format_value(field_mean(records, 'damage_dealt'), 1, False):>7} "
            f"{format_value(field_mean(records, 'close_steps'), 0, False):>7} "
            f"{format_value(field_mean(records, 'damage_per_press'), 2, False):>8} "
            f"{trend:>10}"
        )


def find_runs(runs_dir: Path) -> list[Path]:
    return sorted(
        (child / "episodes.jsonl" for child in runs_dir.iterdir() if (child / "episodes.jsonl").is_file()),
        key=lambda item: item.stat().st_mtime,
    )


def main() -> None:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="汇总训练日志")
    parser.add_argument("--run", default=None, help="实验目录, 或直接给 episodes.jsonl")
    parser.add_argument("--runs-dir", default="runs", help="--all 时扫描的目录")
    parser.add_argument("--all", action="store_true", help="汇总 runs 下所有有回合日志的实验")
    parser.add_argument("--compare", action="store_true", help="把 runs 下的实验并排成一张窄表")
    parser.add_argument("--bins", type=int, default=6, help="趋势分段数")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    if args.all or args.compare:
        paths = find_runs(Path(args.runs_dir))
        if not paths:
            raise SystemExit(f"{args.runs_dir} 下没有找到 episodes.jsonl")
        if args.compare:
            compare_runs(paths)
            return
        for path in paths:
            report_run(path, args.bins)
            print()
        return

    if not args.run:
        raise SystemExit("需要 --run <实验目录> 或 --all")

    target = Path(args.run)
    path = target / "episodes.jsonl" if target.is_dir() else target
    if not path.is_file():
        raise SystemExit(f"找不到回合日志: {path}")
    report_run(path, args.bins)


if __name__ == "__main__":
    main()
