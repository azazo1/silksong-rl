"""把插件落盘的 JPEG 序列合成回放视频, 并清掉原始帧.

插件在回合进行中把游戏画面编码成 JPEG 存在内存里 (被遮挡也能抓), 击杀那局才写盘成一串
``frame-NNNN.jpg``. 这里用 ffmpeg 把它们拼成 mp4 放到 ``runs/<实验>/kills/``, 然后删掉原始帧.
"""

from __future__ import annotations

import logging
import shutil
import subprocess
from pathlib import Path

LOGGER = logging.getLogger("silksong_rl.clips")

DEFAULT_FPS = 8


def find_ffmpeg() -> str | None:
    return shutil.which("ffmpeg")


def build_clip(clip_dir: str | Path, destination: Path, fps: int = DEFAULT_FPS) -> Path | None:
    """把一个片段目录里的帧合成 mp4; 成功后删掉原始帧目录."""

    source = Path(clip_dir)
    if not source.is_dir():
        LOGGER.warning("片段目录不存在: %s", source)
        return None

    frames = sorted(source.glob("frame-*.jpg"))
    if not frames:
        LOGGER.warning("片段目录里没有帧: %s", source)
        return None

    ffmpeg = find_ffmpeg()
    if ffmpeg is None:
        LOGGER.warning("找不到 ffmpeg, 保留原始帧以便事后手工合成: %s", source)
        return None

    destination.parent.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(
        [
            ffmpeg,
            "-hide_banner",
            "-loglevel", "error",
            "-framerate", str(fps),
            "-i", str(source / "frame-%04d.jpg"),
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-y",
            str(destination),
        ],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        LOGGER.warning("合成回放失败 (%s): %s", destination.name, result.stderr.strip()[:200])
        return None

    shutil.rmtree(source, ignore_errors=True)
    LOGGER.info("击杀回放已保存: %s (%.1f 秒)", destination, len(frames) / fps)
    return destination
