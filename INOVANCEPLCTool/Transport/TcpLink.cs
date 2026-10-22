using System;
using System.Net.Sockets;

namespace INOVANCEPLCTool.Transport
{
    /// <summary>
    /// 以太网 TCP 传输通道。
    ///
    /// 有两种工作模式，由端口自动选择（也可用 <see cref="PrivateFrameMode"/> 手动覆盖）：
    ///
    /// ① <b>私有帧模式</b>（端口 <b>12939</b>，默认对 12939 启用）：
    ///    汇川 PLC 的程序烧录 / 在线读写端口，帧 = `[u16 totalLen][0x01][op][seq u32][body]`，
    ///    无 CRC、无站号，长度前缀含自身 2 字节。见 <see cref="Plc.TcpPlcProtocol"/>。
    ///
    /// ② Modbus TCP 模式（端口 <b>502</b>，默认）：标准 MBAP 头（事务ID + 协议ID + 长度 + 单元ID），
    ///    无 CRC。**实测 PLC 502 端口确实应答标准 Modbus TCP**（FC3/FC4 正常）。
    ///
    /// ⚠ 注意：**烧录走 12939 私有协议，不是 Modbus**；502 只提供标准 Modbus 软元件读写。
    /// </summary>
    public sealed class TcpLink : ITransport
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient _client;
        private NetworkStream _stream;
        private ushort _transactionId;
        private readonly object _io = new object();

        /// <summary>Modbus 单元 ID（TCP 里对应 MBAP 的 Unit Identifier）。默认 1。</summary>
        public byte UnitId { get; set; }

        /// <summary>
        /// <b>私有帧模式</b>：应答按「帧首 u16 长度前缀」收（汇川 12939 烧录协议），
        /// 而非 Modbus MBAP（帧首 6 字节头 + 内部 2 字节长度）。
        ///
        /// 置 true 时 <see cref="Request"/> 用 u16 长度前缀读应答；置 false（默认）走 MBAP。
        /// 端口 12939 的程序下载/在线读写必须置 true。
        /// </summary>
        public bool PrivateFrameMode { get; set; }

        public TcpLink(string host, int port = 502)
        {
            _host = host;
            _port = port;
            UnitId = ModbusFrame.DEFAULT_UNIT_TCP;
            Timeout = 3000;
            // 12939 是汇川私有烧录端口，自动切换为长度前缀收帧
            PrivateFrameMode = (port == 12939);
        }

        public string DisplayName { get { return "TCP " + _host + ":" + _port; } }

        public bool IsOpen { get { return _client != null && _client.Connected; } }

        public int Timeout { get; set; }

        // ------------------------------------------------------------------
        // 连接管理
        // ------------------------------------------------------------------
        public void Open()
        {
            if (IsOpen) return;
            try
            {
                _client = new TcpClient();
                _client.NoDelay = true;
                IAsyncResult ar = _client.BeginConnect(_host, _port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(Timeout > 0 ? Timeout : 3000))
                {
                    try { _client.Close(); } catch { }
                    _client = null;
                    throw new TransportException("连接 " + DisplayName + " 超时（" + Timeout + "ms）。");
                }
                _client.EndConnect(ar);
                _stream = _client.GetStream();
                _stream.ReadTimeout = Timeout > 0 ? Timeout : 3000;
                _stream.WriteTimeout = Timeout > 0 ? Timeout : 3000;
            }
            catch (TransportException) { throw; }
            catch (Exception ex)
            {
                Close();
                throw new TransportException(
                    "连接 " + DisplayName + " 失败：" + ex.Message + "\r\n" +
                    "  请检查：IP 是否可达、端口是否正确（Modbus TCP 默认 502）、PLC 网口是否启用。", ex);
            }
        }

        public void Close()
        {
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_client != null) _client.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public void Dispose() { Close(); }

        // ------------------------------------------------------------------
        // 收发（一问一答）
        // ------------------------------------------------------------------
        /// <summary>发送一个已封装的完整 TCP 帧，并读取一帧应答。</summary>
        public byte[] Request(byte[] send, int timeoutMs = 0)
        {
            if (!IsOpen) throw new TransportException("TCP 未连接");
            if (send == null || send.Length == 0) throw new TransportException("发送内容为空");

            int to = timeoutMs > 0 ? timeoutMs : (Timeout > 0 ? Timeout : 3000);

            lock (_io)
            {
                try
                {
                    _stream.ReadTimeout = to;
                    _stream.WriteTimeout = to;
                    _stream.Write(send, 0, send.Length);
                    _stream.Flush();

                    if (PrivateFrameMode)
                    {
                        // 私有帧（12939）：帧首 u16 = 整帧总长（含自身 2 字节）
                        byte[] lp = ReadExact(2, to);
                        int total = lp[0] | (lp[1] << 8);
                        if (total < 2 || total > 0xFFFF)
                            throw new TransportException("私有帧长度异常：" + total);
                        byte[] rest = ReadExact(total - 2, to);
                        byte[] pf = new byte[total];
                        Array.Copy(lp, 0, pf, 0, 2);
                        Array.Copy(rest, 0, pf, 2, rest.Length);
                        return pf;
                    }

                    // MBAP：读 6 字节头 → 得到长度 → 读完剩余
                    byte[] head = ReadExact(6, to);
                    int len = (head[4] << 8) | head[5];
                    if (len <= 0 || len > 4096)
                        throw new TransportException("Modbus TCP 应答长度异常：" + len);

                    byte[] body = ReadExact(len, to);
                    byte[] frame = new byte[6 + len];
                    Array.Copy(head, 0, frame, 0, 6);
                    Array.Copy(body, 0, frame, 6, len);
                    return frame;
                }
                catch (TransportException) { throw; }
                catch (Exception ex)
                {
                    throw new TransportException("TCP 收发失败：" + ex.Message, ex);
                }
            }
        }

        private byte[] ReadExact(int count, int to)
        {
            byte[] buf = new byte[count];
            int got = 0;
            _stream.ReadTimeout = to;
            while (got < count)
            {
                int n = _stream.Read(buf, got, count - got);
                if (n <= 0) throw new TransportException("TCP 连接被对端关闭。");
                got += n;
            }
            return buf;
        }

        /// <summary>取下一个事务 ID（自增）。</summary>
        public ushort NextTransactionId()
        {
            return _transactionId++;
        }
    }
}
