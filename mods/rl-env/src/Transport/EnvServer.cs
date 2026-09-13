using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using RLEnv.Actions;

namespace RLEnv.Transport
{
    // 一条从 Python 侧发来的命令.
    internal struct EnvCommand
    {
        internal Protocol.MessageType Type;

        internal EnvAction Action;

        internal float Speed;

        internal bool Flag;
    }

    // 训练侧的 TCP 服务端: 只监听本机回环地址, 同一时刻只服务一个客户端.
    //
    // 读放在后台线程并转成命令队列, 主线程 (Unity) 每帧取; 写由主线程直接完成,
    // 消息都很小, 回环写不会阻塞.
    internal sealed class EnvServer : IDisposable
    {
        private readonly int _port;

        private readonly ManualLogSource _log;

        private readonly ConcurrentQueue<EnvCommand> _commands = new ConcurrentQueue<EnvCommand>();

        private readonly object _writeLock = new object();

        private TcpListener _listener;

        private TcpClient _client;

        private NetworkStream _stream;

        private Thread _thread;

        private volatile bool _running;

        internal EnvServer(int port, ManualLogSource log)
        {
            _port = port;
            _log = log;
        }

        internal bool HasClient
        {
            get { return _client != null && _client.Connected; }
        }

        internal int Port
        {
            get { return _port; }
        }

        internal void Start()
        {
            if (_running)
            {
                return;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
            }
            catch (Exception exception)
            {
                _listener = null;
                _log.LogError("训练侧 TCP 服务启动失败 (端口 " + _port + "): " + exception.Message);
                return;
            }

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "RLEnvServer";
            _thread.Start();
            _log.LogInfo("训练侧 TCP 服务已启动: 127.0.0.1:" + _port);
        }

        internal bool TryDequeueCommand(out EnvCommand command)
        {
            return _commands.TryDequeue(out command);
        }

        internal void SendHello(string json)
        {
            SendText(Protocol.MessageType.Hello, json);
        }

        internal void SendStatus(Protocol.SessionState state, string message)
        {
            string json = string.Format("{{\"state\":\"{0}\",\"message\":\"{1}\"}}", state, Escape(message));
            SendText(Protocol.MessageType.Status, json);
        }

        internal void SendError(string message)
        {
            SendText(Protocol.MessageType.Error, "{\"message\":\"" + Escape(message) + "\"}");
        }

        // Boss 状态名到整数 id 的映射, 只在出现新状态时发一次.
        internal void SendStateMap(string json)
        {
            SendText(Protocol.MessageType.StateMap, json);
        }

        // 观测: [int32 stepIndex][int32 flags][float32 x N], 字段顺序由 Hello 里的 schema 给出.
        internal void SendObservation(int stepIndex, int flags, float[] values)
        {
            int length = 12 + values.Length * 4;
            byte[] payload = new byte[length];
            WriteInt32(payload, 0, (int)Protocol.MessageType.Observation);
            WriteInt32(payload, 4, stepIndex);
            WriteInt32(payload, 8, flags);
            for (int i = 0; i < values.Length; i++)
            {
                WriteSingle(payload, 12 + i * 4, values[i]);
            }

            SendFrame(payload);
        }

        // 人类示范样本: 观测 + 玩家当时实际按下的键.
        internal void SendRecord(int stepIndex, float[] values, int[] action)
        {
            int length = 8 + values.Length * 4 + action.Length * 4;
            byte[] payload = new byte[length];
            WriteInt32(payload, 0, (int)Protocol.MessageType.Record);
            WriteInt32(payload, 4, stepIndex);
            int offset = 8;
            for (int i = 0; i < values.Length; i++)
            {
                WriteSingle(payload, offset, values[i]);
                offset += 4;
            }

            for (int i = 0; i < action.Length; i++)
            {
                WriteInt32(payload, offset, action[i]);
                offset += 4;
            }

            SendFrame(payload);
        }

        public void Dispose()
        {
            _running = false;
            DropClient();
            if (_listener != null)
            {
                try
                {
                    _listener.Stop();
                }
                catch (Exception)
                {
                }

                _listener = null;
            }

            if (_thread != null)
            {
                _thread.Join(500);
                _thread = null;
            }
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    _client = client;
                    _stream = client.GetStream();
                    _log.LogInfo("训练侧已连接: " + client.Client.RemoteEndPoint);
                    ReadLoop(_stream);
                }
                catch (Exception exception)
                {
                    if (_running)
                    {
                        _log.LogWarning("训练侧连接异常: " + exception.Message);
                    }
                }

                DropClient();
                if (_running)
                {
                    Thread.Sleep(200);
                }
            }
        }

        private void ReadLoop(NetworkStream stream)
        {
            byte[] header = new byte[4];
            while (_running)
            {
                if (!ReadExactly(stream, header, 4))
                {
                    _log.LogInfo("训练侧已断开");
                    return;
                }

                int length = BitConverter.ToInt32(header, 0);
                if (length < 4 || length > Protocol.MaxPayloadBytes)
                {
                    _log.LogWarning("收到非法帧长度: " + length);
                    return;
                }

                byte[] payload = new byte[length];
                if (!ReadExactly(stream, payload, length))
                {
                    _log.LogInfo("训练侧在帧中途断开");
                    return;
                }

                EnvCommand command;
                if (TryParse(payload, out command))
                {
                    _commands.Enqueue(command);
                }
            }
        }

        private bool TryParse(byte[] payload, out EnvCommand command)
        {
            command = default(EnvCommand);
            Protocol.MessageType type = (Protocol.MessageType)BitConverter.ToInt32(payload, 0);
            switch (type)
            {
                case Protocol.MessageType.Reset:
                case Protocol.MessageType.Close:
                case Protocol.MessageType.Ping:
                    command.Type = type;
                    return true;
                case Protocol.MessageType.Step:
                    if (payload.Length < 24)
                    {
                        _log.LogWarning("Step 消息长度不足: " + payload.Length);
                        return false;
                    }

                    command.Type = type;
                    command.Action = EnvAction.FromIndices(
                        BitConverter.ToInt32(payload, 4),
                        BitConverter.ToInt32(payload, 8),
                        BitConverter.ToInt32(payload, 12),
                        BitConverter.ToInt32(payload, 16),
                        BitConverter.ToInt32(payload, 20));
                    return true;
                case Protocol.MessageType.SetSpeed:
                    if (payload.Length < 8)
                    {
                        return false;
                    }

                    command.Type = type;
                    command.Speed = BitConverter.ToSingle(payload, 4);
                    return true;
                case Protocol.MessageType.SetHumanMode:
                    if (payload.Length < 8)
                    {
                        return false;
                    }

                    command.Type = type;
                    command.Flag = BitConverter.ToInt32(payload, 4) != 0;
                    return true;
                default:
                    _log.LogWarning("未知消息类型: " + (int)type);
                    return false;
            }
        }

        private void DropClient()
        {
            NetworkStream stream = _stream;
            _stream = null;
            if (stream != null)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception)
                {
                }
            }

            TcpClient client = _client;
            _client = null;
            if (client != null)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                }
            }

            EnvCommand discarded;
            while (_commands.TryDequeue(out discarded))
            {
            }
        }

        private void SendText(Protocol.MessageType type, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] payload = new byte[4 + body.Length];
            WriteInt32(payload, 0, (int)type);
            Buffer.BlockCopy(body, 0, payload, 4, body.Length);
            SendFrame(payload);
        }

        private void SendFrame(byte[] payload)
        {
            NetworkStream stream = _stream;
            if (stream == null)
            {
                return;
            }

            byte[] header = new byte[4];
            WriteInt32(header, 0, payload.Length);
            lock (_writeLock)
            {
                try
                {
                    stream.Write(header, 0, 4);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
                catch (Exception exception)
                {
                    _log.LogWarning("向训练侧发送失败: " + exception.Message);
                    DropClient();
                }
            }
        }

        private static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, offset, count - offset);
                }
                catch (Exception)
                {
                    return false;
                }

                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteSingle(byte[] buffer, int offset, float value)
        {
            byte[] raw = BitConverter.GetBytes(value);
            buffer[offset] = raw[0];
            buffer[offset + 1] = raw[1];
            buffer[offset + 2] = raw[2];
            buffer[offset + 3] = raw[3];
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        }
    }
}
