using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using RLEnv.Actions;
using UnityEngine;

namespace RLEnv.Episode
{
    // 程序化打烂挡在路上的藤蔓门之类障碍物.
    //
    // 这些门不是 Breakable: 场景里它们挂着 PersistentBoolItem + PlayMakerFSM + PlayMakerTriggerEnter2D
    // + ReceivedDamageProxy, 破门逻辑写在 FSM 里, 靠"被针打中"的触发器事件推进. 与其去猜 FSM 事件名,
    // 不如让主角自己走到门边用真实攻击打烂它: 走的是游戏自己的判定链路, 也不需要逆推任何东西.
    //
    // 用法: 每回合 Scan() 一次, 之后每帧 Tick(), 全部处理完返回 true.
    internal sealed class BlockerBreaker
    {
        private const int FramesPerAttack = 8;

        private const int AttackHoldFrames = 2;

        // 一扇门最多给多少帧去砍. 藤蔓门要打好几下, 帧数给宽一点.
        private const int PositionToleranceFrames = 400;

        private readonly List<Target> _targets = new List<Target>(4);

        private readonly ManualLogSource _log;

        private int _index;

        private int _framesOnTarget;

        private bool _positioned;

        private int _attackPhase;

        internal BlockerBreaker(ManualLogSource log)
        {
            _log = log;
        }

        internal int TargetCount
        {
            get { return _targets.Count; }
        }

        internal bool HasUnbroken
        {
            get
            {
                for (int i = 0; i < _targets.Count; i++)
                {
                    if (!_targets[i].Broken)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        internal string Describe()
        {
            if (_targets.Count == 0)
            {
                return "无";
            }

            StringBuilder builder = new StringBuilder(128);
            for (int i = 0; i < _targets.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(_targets[i].Name).Append(_targets[i].Broken ? " 已破" : " 完好");
            }

            return builder.ToString();
        }

        // 找出名字或持久化项 ID 命中给定片段的场景对象.
        internal int Scan(string[] patterns)
        {
            _targets.Clear();
            _index = 0;
            _framesOnTarget = 0;
            _positioned = false;
            _attackPhase = 0;

            if (patterns == null || patterns.Length == 0)
            {
                return 0;
            }

            List<GameObject> candidates = new List<GameObject>(8);
            PersistentBoolItem[] items = Resources.FindObjectsOfTypeAll<PersistentBoolItem>();
            for (int i = 0; i < items.Length; i++)
            {
                PersistentBoolItem item = items[i];
                if (item == null || !item.gameObject.scene.IsValid())
                {
                    continue;
                }

                string name = item.gameObject.name;
                string id = SafeId(item);
                if (!Matches(name, patterns) && !Matches(id, patterns))
                {
                    continue;
                }

                if (!candidates.Contains(item.gameObject))
                {
                    candidates.Add(item.gameObject);
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] == null)
                {
                    continue;
                }

                _targets.Add(new Target(candidates[i]));
            }

            if (_log != null)
            {
                _log.LogInfo(string.Format("障碍物扫描: 命中 {0} 个 ({1})", _targets.Count, Describe()));
            }

            return _targets.Count;
        }

        // 每帧推进一步: 走到门边, 面向它, 反复挥针.
        internal bool Tick()
        {
            if (!HasUnbroken)
            {
                VirtualPad.Release(2);
                return true;
            }

            Target target = CurrentTarget();
            if (target == null)
            {
                VirtualPad.Release(2);
                return true;
            }

            if (target.Refresh())
            {
                if (_log != null)
                {
                    _log.LogInfo("障碍物已破: " + target.Name);
                }

                _index++;
                _positioned = false;
                _framesOnTarget = 0;
                _attackPhase = 0;
                VirtualPad.Release(2);
                return !HasUnbroken;
            }

            _framesOnTarget++;
            if (_framesOnTarget > PositionToleranceFrames)
            {
                if (_log != null)
                {
                    _log.LogWarning("障碍物打不掉, 跳过: " + target.Name);
                }

                target.GiveUp();
                VirtualPad.Release(2);
                return !HasUnbroken;
            }

            HeroController hero = HeroController.instance;
            if (hero == null)
            {
                VirtualPad.Release(1);
                return false;
            }

            Vector3 position = target.Position;
            if (!_positioned)
            {
                Vector3 heroPosition = hero.transform.position;
                float side = heroPosition.x >= position.x ? 1f : -1f;
                Vector3 standPosition = new Vector3(position.x + side * 1.4f, position.y - 0.3f, 0f);
                hero.transform.position = standPosition;
                if (hero.Body != null)
                {
                    hero.Body.linearVelocity = Vector2.zero;
                }

                if (side > 0f)
                {
                    hero.FaceLeft();
                }
                else
                {
                    hero.FaceRight();
                }

                SnapCamera();

                _positioned = true;
                _framesOnTarget = 0;
                if (_log != null)
                {
                    _log.LogInfo(string.Format(
                        "靠近障碍物 {0} ({1:F1}, {2:F1}), 站到 ({3:F1}, {4:F1})",
                        target.Name,
                        position.x,
                        position.y,
                        standPosition.x,
                        standPosition.y));
                }

                return false;
            }

            // 挥针: 按住两三帧再松开, 让 WasPressed 能一次次重新触发.
            _attackPhase++;
            if (_attackPhase % FramesPerAttack < AttackHoldFrames)
            {
                EnvAction action = EnvAction.Neutral;
                action.Attack = true;
                VirtualPad.Arm(action);
            }
            else
            {
                VirtualPad.Release(1);
            }

            return false;
        }

        internal void Release()
        {
            VirtualPad.Release(2);
        }

        // 瞬移主角后相机还留在原地, 手动让它立刻跟过去, 免得整段流程都在黑屏/离屏状态下进行.
        internal static void SnapCamera()
        {
            GameCameras gameCameras = GameCameras.SilentInstance;
            CameraController camera = gameCameras != null ? gameCameras.cameraController : null;
            if (camera == null)
            {
                return;
            }

            try
            {
                camera.PositionToHeroInstant(false);
            }
            catch (Exception)
            {
            }
        }

        private Target CurrentTarget()
        {
            for (int i = _index; i < _targets.Count; i++)
            {
                if (!_targets[i].Broken)
                {
                    return _targets[i];
                }
            }

            return null;
        }

        private static string SafeId(PersistentBoolItem item)
        {
            try
            {
                return item.GetId();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static string[] ParsePatterns(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return new string[0];
            }

            List<string> patterns = new List<string>(4);
            string[] parts = raw.Split(new char[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string trimmed = parts[i].Trim();
                if (trimmed.Length > 0)
                {
                    patterns.Add(trimmed);
                }
            }

            return patterns.ToArray();
        }

        private static bool Matches(string text, string[] patterns)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < patterns.Length; i++)
            {
                if (text.IndexOf(patterns[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class Target
        {
            private readonly PlayMakerFSM[] _fsms;

            private readonly PersistentBoolItem _persistentItem;

            internal Target(GameObject gameObject)
            {
                GameObject = gameObject;
                Name = gameObject.name;
                Position = gameObject.transform.position;
                _fsms = gameObject.GetComponents<PlayMakerFSM>();
                _persistentItem = gameObject.GetComponent<PersistentBoolItem>();
            }

            internal GameObject GameObject { get; private set; }

            internal string Name { get; private set; }

            internal Vector3 Position { get; private set; }

            internal bool Broken { get; private set; }

            internal void GiveUp()
            {
                Broken = true;
            }

            // 判定是否已经破掉. 可靠信号是这个对象自己的持久化项翻成 true
            // (藤蔓门被彻底打烂时游戏会把 "Moss Vine Cluster" 写成 true);
            // 对象被停用也算破. 注意不能用"FSM 状态变了"来判断: 打第一下就会换状态,
            // 门其实还在, 会误判成已破.
            internal bool Refresh()
            {
                if (Broken)
                {
                    return true;
                }

                if (GameObject == null)
                {
                    Broken = true;
                    return true;
                }

                if (_persistentItem != null)
                {
                    try
                    {
                        if (_persistentItem.GetCurrentValue())
                        {
                            Broken = true;
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                if (!GameObject.activeInHierarchy)
                {
                    Broken = true;
                    return true;
                }

                Position = GameObject.transform.position;
                return false;
            }
        }
    }
}
