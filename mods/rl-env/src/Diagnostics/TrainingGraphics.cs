using UnityEngine;

namespace RLEnv.Diagnostics
{
    // 训练期的画质降级.
    //
    // 训练吞吐的瓶颈不是 Python, 而是游戏在 6 倍速战斗时的帧率 (实测掉到 10 fps 上下):
    // 一个 step 要推进 6 个物理帧, 而物理帧是按渲染帧批量推进的, 渲染帧越慢, 每步的真实耗时越长.
    // 因此训练期间把分辨率压到很小, 关垂直同步并把画质档位降到最低, 断开后原样恢复.
    internal sealed class TrainingGraphics
    {
        private const int TrainingWidth = 640;

        private const int TrainingHeight = 360;

        private bool _applied;

        private int _originalWidth;

        private int _originalHeight;

        private bool _originalFullScreen;

        private int _originalVSync;

        private int _originalQuality;

        internal bool Applied
        {
            get { return _applied; }
        }

        internal string Describe()
        {
            if (!_applied)
            {
                return "未启用";
            }

            return string.Format("{0}x{1}, 画质档 {2} -> 0, 垂直同步 {3} -> 0", _originalWidth, _originalHeight, _originalQuality, _originalVSync);
        }

        internal void Apply()
        {
            if (_applied)
            {
                return;
            }

            _originalWidth = Screen.width;
            _originalHeight = Screen.height;
            _originalFullScreen = Screen.fullScreen;
            _originalVSync = QualitySettings.vSyncCount;
            _originalQuality = QualitySettings.GetQualityLevel();
            _applied = true;

            Screen.SetResolution(TrainingWidth, TrainingHeight, false);
            QualitySettings.vSyncCount = 0;
            try
            {
                QualitySettings.SetQualityLevel(0, true);
            }
            catch (System.Exception)
            {
            }
        }

        internal void Restore()
        {
            if (!_applied)
            {
                return;
            }

            _applied = false;

            try
            {
                QualitySettings.SetQualityLevel(_originalQuality, true);
                QualitySettings.vSyncCount = _originalVSync;
                Screen.SetResolution(_originalWidth, _originalHeight, _originalFullScreen);
            }
            catch (System.Exception)
            {
            }
        }
    }
}
