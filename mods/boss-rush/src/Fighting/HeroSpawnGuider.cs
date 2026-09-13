using UnityEngine;

namespace BossRush.Fighting
{
    // 场景切换结束时, 游戏会把主角摆到存档记录的落点.
    // 这个常驻组件在切换完成事件里把主角搬到 Boss 房入口, 对应原版的 BossRushMgrComp.
    internal sealed class HeroSpawnGuider : MonoBehaviour
    {
        private static HeroSpawnGuider _instance;

        private Vector3 _target;

        private bool _armed;

        private GameManager _subscribedManager;

        internal static HeroSpawnGuider Instance
        {
            get
            {
                Ensure();
                return _instance;
            }
        }

        internal static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }

            GameObject host = new GameObject("BossRushHeroSpawnGuider");
            Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<HeroSpawnGuider>();
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

        internal void Arm(Vector3 position)
        {
            _target = position;
            _armed = true;
            Subscribe();
        }

        private void Update()
        {
            // GameManager 可能比本组件晚就绪, 因此每帧补一次订阅.
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
            GameManager gameManager = GameManager.instance;
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
            if (!_armed)
            {
                return;
            }

            MoveHero();
        }

        private void HandleEnteredScene()
        {
            if (!_armed)
            {
                return;
            }

            MoveHero();
            _armed = false;
        }

        private void MoveHero()
        {
            GameManager gameManager = GameManager.instance;
            if (gameManager == null || gameManager.hero_ctrl == null)
            {
                return;
            }

            gameManager.hero_ctrl.transform.position = _target;
        }
    }
}
