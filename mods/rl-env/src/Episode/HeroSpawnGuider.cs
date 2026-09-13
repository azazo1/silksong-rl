using UnityEngine;

namespace RLEnv.Episode
{
    // 场景切换结束后把主角摆到指定落点.
    // 载入 Boss 存档时游戏会按 respawnMarkerName 放置主角, 但那个落点不一定正好在 Boss 房入口,
    // 因此这里再强制对齐一次, 顺便保证每回合起始位置完全一致.
    internal sealed class HeroSpawnGuider : MonoBehaviour
    {
        private static HeroSpawnGuider _instance;

        private Vector3 _target;

        private bool _armed;

        private int _framesToHold;

        private GameManager _subscribedManager;

        internal static HeroSpawnGuider Instance
        {
            get
            {
                Ensure();
                return _instance;
            }
        }

        internal static HeroSpawnGuider Ensure()
        {
            if (_instance != null)
            {
                return _instance;
            }

            GameObject host = new GameObject("RLEnvHeroSpawnGuider");
            Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<HeroSpawnGuider>();
            return _instance;
        }

        internal static void Shutdown()
        {
            if (_instance == null)
            {
                return;
            }

            HeroSpawnGuider instance = _instance;
            _instance = null;
            instance.Unsubscribe();
            Object.Destroy(instance.gameObject);
        }

        // holdFrames: 场景切换结束后继续摆正多少帧, 防止游戏自己的入场景逻辑把主角又挪走.
        internal void Arm(Vector3 position, int holdFrames)
        {
            _target = position;
            _armed = true;
            _framesToHold = holdFrames < 1 ? 1 : holdFrames;
            Subscribe();
        }

        internal void Disarm()
        {
            _armed = false;
            _framesToHold = 0;
        }

        private void Update()
        {
            if (_armed)
            {
                Subscribe();
            }
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void Subscribe()
        {
            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null || _subscribedManager == gameManager)
            {
                return;
            }

            Unsubscribe();
            gameManager.OnFinishedSceneTransition += HandleSceneTransitionFinished;
            gameManager.OnFinishedEnteringScene += HandleEnteredScene;
            _subscribedManager = gameManager;
        }

        private void Unsubscribe()
        {
            if (_subscribedManager == null)
            {
                return;
            }

            _subscribedManager.OnFinishedSceneTransition -= HandleSceneTransitionFinished;
            _subscribedManager.OnFinishedEnteringScene -= HandleEnteredScene;
            _subscribedManager = null;
        }

        private void HandleSceneTransitionFinished()
        {
            if (_armed)
            {
                MoveHero();
            }
        }

        private void HandleEnteredScene()
        {
            if (_armed)
            {
                MoveHero();
            }
        }

        private void MoveHero()
        {
            GameManager gameManager = GameManager.UnsafeInstance;
            if (gameManager == null || gameManager.hero_ctrl == null)
            {
                return;
            }

            gameManager.hero_ctrl.transform.position = _target;
        }
    }
}
