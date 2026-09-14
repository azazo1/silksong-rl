using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace RLEnv.Clips
{
    // 回合回放: 训练期间按固定间隔把游戏画面编码成 JPEG 放在内存环形缓冲里.
    //
    // 为什么要自己做而不是外部抓屏: Windows 上的抓屏工具 (gdigrab 之类) 抓的是"屏幕上那块区域",
    // 只要游戏窗口不是最前面的那个, 抓到的就是盖在它上面的窗口. Unity 的 ScreenCapture 读的是
    // 游戏自己的后备缓冲, 被遮挡也照样能抓到.
    //
    // 一局结束时训练侧会告诉我们是击杀还是普通结束: 击杀就把缓冲里的帧写成图片序列
    // (再由训练侧用 ffmpeg 合成 mp4), 否则直接丢掉, 所以长跑不会往磁盘堆垃圾.
    internal sealed class ClipRecorder
    {
        private readonly ManualLogSource _log;

        private readonly List<byte[]> _frames = new List<byte[]>(256);

        private float _intervalSeconds;

        private int _capacity;

        private int _jpegQuality;

        private int _fps;

        private int _seconds;

        private float _nextCaptureAt;

        private int _dropped;

        internal ClipRecorder(ManualLogSource log, int fps, int seconds, int jpegQuality)
        {
            _log = log;
            _jpegQuality = jpegQuality < 1 ? 1 : (jpegQuality > 100 ? 100 : jpegQuality);
            Reconfigure(fps, seconds);
        }

        // 训练侧可以在运行时调帧率/缓冲时长: 帧率高回放更顺, 代价是每秒要同步截屏更多次
        // (每次截屏都会等 GPU, 对训练吞吐有影响), 所以默认压得比较低.
        internal void Reconfigure(int fps, int seconds)
        {
            _fps = fps < 1 ? 1 : fps;
            _seconds = seconds < 1 ? 1 : seconds;
            _intervalSeconds = 1f / _fps;
            _capacity = _fps * _seconds;

            while (_frames.Count > _capacity)
            {
                _frames.RemoveAt(0);
                _dropped++;
            }
        }

        internal int Fps
        {
            get { return _fps; }
        }

        internal int CapacitySeconds
        {
            get { return _seconds; }
        }

        internal int FrameCount
        {
            get { return _frames.Count; }
        }

        internal float BufferSeconds
        {
            get { return _frames.Count * _intervalSeconds; }
        }

        // 新回合开始: 把上一局的画面清掉.
        internal void BeginEpisode()
        {
            _frames.Clear();
            _nextCaptureAt = 0f;
            _dropped = 0;
        }

        internal void Tick(bool capturing)
        {
            if (!capturing)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (_frames.Count > 0 && now < _nextCaptureAt)
            {
                return;
            }

            _nextCaptureAt = now + _intervalSeconds;
            Capture();
        }

        private void Capture()
        {
            Texture2D texture = null;
            try
            {
                // 同步截屏会等一下 GPU, 所以频率刻意压低 (默认 8 fps), 对训练吞吐的影响可以忽略.
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null)
                {
                    return;
                }

                byte[] jpeg = texture.EncodeToJPG(_jpegQuality);
                if (jpeg == null || jpeg.Length == 0)
                {
                    return;
                }

                _frames.Add(jpeg);
                while (_frames.Count > _capacity)
                {
                    _frames.RemoveAt(0);
                    _dropped++;
                }
            }
            catch (Exception exception)
            {
                if (_log != null)
                {
                    _log.LogWarning("截屏失败: " + exception.Message);
                }
            }
            finally
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        // 把缓冲里的帧写成图片序列, 返回目录; 没有帧就返回 null.
        internal string Save(string clipsRootDir)
        {
            if (_frames.Count == 0)
            {
                return null;
            }

            string directory = Path.Combine(clipsRootDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(directory);

            for (int i = 0; i < _frames.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(directory, string.Format("frame-{0:D4}.jpg", i)), _frames[i]);
            }

            if (_log != null)
            {
                _log.LogInfo(string.Format(
                    "回放片段已保存: {0} ({1} 帧, 约 {2:F1} 秒{3})",
                    directory,
                    _frames.Count,
                    BufferSeconds,
                    _dropped > 0 ? string.Format(", 缓冲上限丢掉 {0} 帧", _dropped) : string.Empty));
            }

            _frames.Clear();
            return directory;
        }

        internal void Discard()
        {
            _frames.Clear();
        }
    }
}
