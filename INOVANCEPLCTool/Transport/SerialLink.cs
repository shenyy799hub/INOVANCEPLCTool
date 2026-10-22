using System;
using System.IO.Ports;
using System.Threading;

namespace INOVANCEPLCTool.Transport
{
    /// <summary>
    /// 串口（RS485 / RS232）传输通道。
    ///
    /// 默认参数：<b>19200 / 8 数据位 / 无校验 / 1 停止位</b>。
    ///
    /// ⚠ 波特率因设备而异：实测两台 H6E，一台为 19200、另一台为 9600，
    ///   同一时刻只有正确的那一档会有应答。因此波特率<b>做成可选项由用户选</b>，
    ///   默认取 19200（沈工现场多数设备为该值）。若打开后无应答，先换波特率再查接线。
    /// 串口烧录走汇川 0x43 私有协议（非 Modbus RTU），见 <see cref="SerialProtocol"/>。
    ///
    /// ⚠ 当前阶段：打开/关闭/收发骨架已实现，可用于连通性验证与抓包比对；
    ///   真实的下载时序（寄存器地址映射、分块搬运规则）待抓到触摸屏报文后补齐，
    ///   届时只需在业务层补映射表，本文件无需改动。
    /// </summary>
    public sealed class SerialLink : ITransport
    {
        private readonly string _portName;
        private SerialPort _sp;
        private readonly object _io = new object();

        // ---- 默认串口参数（19200-8-N-1；波特率随设备而异，UI 可选）----
        public int BaudRate { get; set; }
        public int DataBits { get; set; }
        public Parity Parity { get; set; }
        public StopBits StopBits { get; set; }

        /// <summary>Modbus 从站地址（RTU）。默认 1。</summary>
        public byte UnitId { get; set; }

        /// <summary>应答帧之间的静默间隔（毫秒），RTU 3.5 字符时间以上，取保守值。</summary>
        public int SilentIntervalMs { get; set; }

        public SerialLink(string portName)
        {
            _portName = portName;
            BaudRate = 19200;
            DataBits = 8;
            Parity = Parity.None;
            StopBits = StopBits.One;
            UnitId = ModbusFrame.DEFAULT_UNIT_RTU;
            SilentIntervalMs = 20;
            Timeout = 3000;
        }

        public string DisplayName { get { return "串口 " + _portName + " (" + BaudRate + ",8," + ParityShort(Parity) + ",1)"; } }

        public bool IsOpen { get { return _sp != null && _sp.IsOpen; } }

        public int Timeout { get; set; }

        /// <summary>枚举本机可用串口名。</summary>
        public static string[] GetPortNames()
        {
            try { return SerialPort.GetPortNames(); }
            catch { return new string[0]; }
        }

        private static string ParityShort(Parity p)
        {
            switch (p)
            {
                case Parity.None: return "N";
                case Parity.Even: return "E";
                case Parity.Odd: return "O";
                case Parity.Mark: return "M";
                case Parity.Space: return "S";
                default: return "?";
            }
        }

        // ------------------------------------------------------------------
        // 连接管理
        // ------------------------------------------------------------------
        public void Open()
        {
            if (IsOpen) return;
            try
            {
                _sp = new SerialPort(_portName, BaudRate, Parity, DataBits, StopBits);
                _sp.ReadTimeout = Timeout > 0 ? Timeout : 3000;
                _sp.WriteTimeout = Timeout > 0 ? Timeout : 3000;
                _sp.ReadBufferSize = 1 << 16;
                _sp.WriteBufferSize = 1 << 16;
                _sp.Open();
                _sp.DiscardInBuffer();
                _sp.DiscardOutBuffer();
            }
            catch (Exception ex)
            {
                _sp = null;
                throw new TransportException(
                    "打开串口 " + _portName + " 失败：" + ex.Message + "\r\n" +
                    "  请检查：串口号是否正确、是否被其他软件（组态/串口助手）占用、USB转485 是否插好。", ex);
            }
        }

        public void Close()
        {
            try
            {
                if (_sp != null)
                {
                    if (_sp.IsOpen) _sp.Close();
                    _sp.Dispose();
                }
            }
            catch { }
            finally { _sp = null; }
        }

        public void Dispose() { Close(); }

        // ------------------------------------------------------------------
        // 收发（一问一答）
        // ------------------------------------------------------------------
        /// <summary>发送一个已封装好的完整帧（含地址与 CRC），并读取一帧应答。</summary>
        public byte[] Request(byte[] send, int timeoutMs = 0)
        {
            if (!IsOpen) throw new TransportException("串口未打开");
            if (send == null || send.Length == 0) throw new TransportException("发送内容为空");

            int to = timeoutMs > 0 ? timeoutMs : (Timeout > 0 ? Timeout : 3000);

            lock (_io)
            {
                try
                {
                    _sp.DiscardInBuffer();
                    _sp.Write(send, 0, send.Length);
                    Thread.Sleep(SilentIntervalMs);

                    // 读第一段，确定期望长度
                    byte[] head = ReadSome(to);
                    if (head == null || head.Length == 0)
                        throw new TransportException(
                            "串口无应答（" + _portName + "）：请确认 PLC 已上电、波特率/"
                            + ParityShort(Parity) + " 匹配、A/B 线未接反。");

                    int expect = ExpectedRtuLength(head);
                    if (expect > head.Length)
                    {
                        var buf = new byte[expect];
                        Array.Copy(head, buf, head.Length);
                        int got = head.Length;
                        // 补齐剩余字节
                        while (got < expect)
                        {
                            byte[] more = ReadSome(to);
                            if (more == null || more.Length == 0) break;
                            int take = Math.Min(more.Length, expect - got);
                            Array.Copy(more, 0, buf, got, take);
                            got += take;
                        }
                        byte[] trim = new byte[got];
                        Array.Copy(buf, trim, got);
                        return trim;
                    }
                    return head;
                }
                catch (TransportException) { throw; }
                catch (Exception ex)
                {
                    throw new TransportException("串口收发失败：" + ex.Message, ex);
                }
            }
        }

        /// <summary>
        /// 发送一帧并按<b>汇川 0x43 协议</b>的长度字段读取应答（用于串口烧录）。
        ///
        /// 0x43 帧：<c>addr | 0x43 | len(u16 小端) | payload | crc(2)</c>，整帧长 = 4 + len。
        /// 与 Modbus RTU 的「按功能码推长度」不同，这里直接用帧内的 len 字段定长，
        /// 能正确处理变长应答（数据块应答、设备信息应答等）。
        /// </summary>
        public byte[] RequestFrame(byte[] send, int timeoutMs = 0)
        {
            if (!IsOpen) throw new TransportException("串口未打开");
            if (send == null || send.Length == 0) throw new TransportException("发送内容为空");

            int to = timeoutMs > 0 ? timeoutMs : (Timeout > 0 ? Timeout : 3000);

            lock (_io)
            {
                try
                {
                    _sp.DiscardInBuffer();
                    _sp.Write(send, 0, send.Length);

                    var acc = new System.Collections.Generic.List<byte>();
                    int total = -1;   // 目标帧总长（读满为止）
                    DateTime t0 = DateTime.Now;

                    while ((DateTime.Now - t0).TotalMilliseconds < to)
                    {
                        int avail = _sp.BytesToRead;
                        if (avail <= 0)
                        {
                            Thread.Sleep(2);
                            continue;
                        }
                        byte[] buf = new byte[avail];
                        int got = _sp.Read(buf, 0, avail);
                        for (int i = 0; i < got; i++) acc.Add(buf[i]);

                        // 一旦攒够 4 字节，就能从 len 字段推出整帧长
                        if (total < 0 && acc.Count >= 4 && acc[1] == 0x43)
                        {
                            int len = acc[2] | (acc[3] << 8);
                            total = 4 + len;
                        }
                        if (total > 0 && acc.Count >= total) break;
                    }

                    if (acc.Count == 0)
                        throw new TransportException(
                            "串口无应答（" + _portName + "）：请确认 PLC 已上电、波特率/"
                            + ParityShort(Parity) + " 匹配、A/B 线未接反。");

                    if (total > 0 && acc.Count > total)
                        acc.RemoveRange(total, acc.Count - total);

                    return acc.ToArray();
                }
                catch (TransportException) { throw; }
                catch (Exception ex)
                {
                    throw new TransportException("串口收发失败：" + ex.Message, ex);
                }
            }
        }

        /// <summary>读取当前串口缓冲区可用字节（超时按 to 毫秒）。</summary>
        private byte[] ReadSome(int to)        {
            _sp.ReadTimeout = to;
            try
            {
                int avail = _sp.BytesToRead;
                if (avail <= 0)
                {
                    // 阻塞等待一个字节，触发超时
                    int b = _sp.ReadByte();
                    avail = 1;
                    byte[] one = new byte[1];
                    one[0] = (byte)b;
                    // 再取走缓冲里已经到达的
                    int more = _sp.BytesToRead;
                    if (more > 0)
                    {
                        byte[] rest = new byte[more];
                        int n = _sp.Read(rest, 0, more);
                        byte[] all = new byte[1 + n];
                        all[0] = (byte)b;
                        Array.Copy(rest, 0, all, 1, n);
                        return all;
                    }
                    return one;
                }
                byte[] buf = new byte[avail];
                int got = _sp.Read(buf, 0, avail);
                if (got == buf.Length) return buf;
                byte[] r = new byte[got];
                Array.Copy(buf, r, got);
                return r;
            }
            catch (TimeoutException) { return new byte[0]; }
        }

        /// <summary>
        /// 依据功能码推算 RTU 应答总长度（含地址与 CRC）。
        /// 读类应答：地址(1)+功能(1)+字节数(1)+数据(n)+CRC(2)；
        /// 写类应答：地址(1)+功能(1)+地址(2)+值(2)+CRC(2) = 8；
        /// 异常应答：地址(1)+功能(1)+异常码(1)+CRC(2) = 5。
        /// 长度不足时返回当前长度（无法判断）。
        /// </summary>
        private static int ExpectedRtuLength(byte[] frame)
        {
            if (frame.Length < 2) return frame.Length;
            byte fc = frame[1];
            if ((fc & 0x80) != 0) return 5;                 // 异常应答
            switch (fc)
            {
                case ModbusFrame.FC_READ_HOLDING:
                case ModbusFrame.FC_READ_INPUT:
                    if (frame.Length < 3) return frame.Length;
                    return 3 + frame[2] + 2;                // 地址+功能+字节数 + 数据 + CRC
                case ModbusFrame.FC_WRITE_SINGLE:
                case ModbusFrame.FC_WRITE_MULTIPLE:
                    return 8;
                default:
                    return frame.Length;
            }
        }
    }
}
