using System.Collections.Generic;
using System.Text;

namespace RLEnv.Observation
{
    // 把 "Boss 当前处于哪个动作状态" 编码成整数.
    //
    // 一个 Boss 身上通常挂着多个 PlayMakerFSM, 这里把它们的 ActiveStateName 按 FSM 名字排序拼成一个键,
    // 每个新键分配一个自增 id. 新出现的键会通过 StateMap 消息发给 Python, 方便事后对照日志分析.
    internal sealed class StateIdRegistry
    {
        private readonly Dictionary<string, int> _ids = new Dictionary<string, int>();

        private readonly List<string> _keys = new List<string>();

        private readonly StringBuilder _builder = new StringBuilder(128);

        internal bool Dirty { get; private set; }

        internal int Count
        {
            get { return _keys.Count; }
        }

        internal int GetId(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return -1;
            }

            int id;
            if (_ids.TryGetValue(key, out id))
            {
                return id;
            }

            id = _keys.Count;
            _ids[key] = id;
            _keys.Add(key);
            Dirty = true;
            return id;
        }

        internal string KeyOf(int id)
        {
            if (id < 0 || id >= _keys.Count)
            {
                return string.Empty;
            }

            return _keys[id];
        }

        internal void ClearDirty()
        {
            Dirty = false;
        }

        internal string ToJson()
        {
            _builder.Length = 0;
            _builder.Append('{');
            for (int i = 0; i < _keys.Count; i++)
            {
                if (i > 0)
                {
                    _builder.Append(',');
                }

                _builder.Append('"').Append(i).Append("\":\"").Append(_keys[i].Replace("\"", "'")).Append('"');
            }

            _builder.Append('}');
            return _builder.ToString();
        }
    }
}
