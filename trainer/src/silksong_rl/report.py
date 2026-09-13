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

LOGGER = logging.getLogger("silksong_rl.report")

# 单元格里放的统计量: (标题, 字段名, 小数位, 是否带符号)
SUMMARY_FIELDS = (
    ("回报", "reward", 2, True),
    ("长度", "length", 1, False),
    ("造成伤害", "damage_dealt", 1, False),
    ("受到伤害", "damage_taken", 1, False),
    ("挥刀步", "attack_steps", 1, False),
    ("每刀伤害", "damage_per_swing", 2, False),
    ("最近距离", "min_boss_distance", 2, False),
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
        values = [float(record.get(key, 0.0)) for record in records]
        parts.append(f"{title} {format_value(mean(values), digits, signed)}")

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
            values = [float(record.get(key, 0.0)) for record in group]
            row += f" {format_value(mean(values), digits, signed):>{max(len(key) * 2, 8)}}"
        lines.append(row)
        damage_series.append(mean([float(record.get("damage_dealt", 0.0)) for record in group]))
        first = last + 1

    lines.append("")
    lines.append("每段平均造成伤害:")
    scale = max(damage_series) if damage_series else 1.0
    for index, value in enumerate(damage_series, start=1):
        width = int(round(value / scale * 40)) if scale > 0 else 0
        lines.append(f"  第 {index} 段 {value:6.1f} {'#' * width}")

    return "\n".join(lines)


def report_run(path: Path, bins: int) -> bool:
    records = load_episodes(path)
    if not records:
        LOGGER.warning("%s 里没有回合记录", path)
        return False

    LOGGER.info("%s: %s", path.parent.name, describe(records))
    print(describe_bins(split_bins(records, bins)))
    return True


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
    parser.add_argument("--bins", type=int, default=6, help="趋势分段数")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    if args.all:
        paths = find_runs(Path(args.runs_dir))
        if not paths:
            raise SystemExit(f"{args.runs_dir} 下没有找到 episodes.jsonl")
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
