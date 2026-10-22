using System;
using System.Text;
using INOVANCEPLCTool.Transport;

namespace INOVANCEPLCTool.Plc
{
    /// <summary>
    /// 汇川 PLC 的 <b>以太网（TCP）烧录客户端</b>（端口 12939 私有无封装帧协议）。
    ///
    /// 时序完全按「AutoShop 走网口下载程序」真实抓包（plc_tcp.pcap）还原，
    /// 与串口 <see cref="SerialBurnClient"/> 的应用层语义同构。
    ///
    /// 完整时序（seq 全局递增）：
    ///   [0]  seq=1..4   握手             op=00 body= 01 00
    ///   [1]  seq=5      登录             op=80 body= 01 00 | "user0" + 0xA7×59
    ///                    ↔ 应答 op=81
    ///   [2]  seq=6      会话控制         op=81 body= 03 00 | 01 00 00 00
    ///   [3]  seq=7      读运行状态       op=01 body= 01 00 | 00 00 | 00 FF | 01 00 00 00
    ///   [4]  seq=9      会话控制         op=81 body= 03 00 | 01 00 00 00
    ///   [5]  seq=10     写运行状态=停止  op=01 body= 02 00 | 00 00 | 00 FF | 01 00 00 00 | 00
    ///   [6]  seq=11..12 读运行状态       op=01 body= 01 00 | 00 00 | 00 FF | 01 00 00 00
    ///   [7]  逐文件     文件声明         op=04 body= 02 00 | 00 00 | tagA | tagB | 10000 | size | name\0
    ///                   文件内容 ×N      op=04 body= 05 00 | blk | 00 00 | len | data
    ///   [末] 提交       写 addr=0x0010   op=01 body= 02 00 | 10 00 | 00 FF | 04 00 00 00 | 01 00 00 00
    ///   [末] 写运行状态=运行             op=01 body= 02 00 | 00 00 | 00 FF | 01 00 00 00 | 01（可选）
    ///
    /// ⚠ 与串口的关键差异：
    ///   ① TCP 的文件内容是 <b>按条目逐个</b>「声明 + 内容」下发（与 USB 同思路），
    ///      <b>不是</b>把整包 .down 原样下发（那是串口 0x43 的做法）；
    ///   ② 帧无 CRC、无站号；seq 为 u32；帧首 u16 = 整帧总长（含自身）；
    ///   ③ 内容帧的数据为<b>未压缩原样</b>（抓包实测 ELF 魔数直接出现）。
    /// </summary>
    public sealed class TcpBurnClient
    {
        private readonly ITransport _link;
        private uint _seq;
        private Action<string> _log;

        /// <summary>文件内容块字节数（抓包声明帧携带 chunk=10000）。</summary>
        public int BlockSize = TcpPlcProtocol.FILE_CHUNK;

        /// <summary>两次请求之间的最小间隔（毫秒）。</summary>
        public int InterFrameDelayMs = 2;

        /// <summary>单帧应答超时。</summary>
        public int ResponseTimeoutMs = 5000;

        /// <summary>握手应答里解析出的机型（如 "H6E"），解析不到为空串。</summary>
        public string DeviceModel = "";

        public TcpBurnClient(ITransport link)
        {
            if (link == null) throw new ArgumentNullException("link");
            _link = link;
        }

        // ------------------------------------------------------------------
        // 底层：发一帧 + 收一帧（一问一答）
        // ------------------------------------------------------------------
        private TcpPlcProtocol.TcpFrame Send(byte op, byte[] body, string what)
        {
            uint seq = _seq;
            byte[] frame = TcpPlcProtocol.BuildFrame(op, seq, body);

            if (_log != null && what != null)
                _log(string.Format("  → seq={0}  {1}  {2}", seq, what, TcpPlcProtocol.ToHex(frame, 20)));

            byte[] rsp = _link.Request(frame, ResponseTimeoutMs);
            if (rsp == null || rsp.Length < 8)
                throw new PlcClientException("TCP 无应答（" + what + "）。请确认 PLC 在线、端口为 12939、网线/A/B 无碍。");

            int consumed;
            TcpPlcProtocol.TcpFrame tf;
            if (!TcpPlcProtocol.TryParse(rsp, 0, rsp.Length, out tf, out consumed))
                throw new PlcClientException(
                    "应答帧解析失败（" + what + "）：" + TcpPlcProtocol.ToHex(rsp, 32));

            if (_log != null)
                _log(string.Format("  ← seq={0}  op=0x{1:X2}  body={2}", tf.Seq, tf.Op, TcpPlcProtocol.ToHex(tf.Body, 20)));

            _seq++;
            return tf;
        }

        // ------------------------------------------------------------------
        // 控制帧：软元件读写
        // ------------------------------------------------------------------
        /// <summary>读软元件；返回应答 body 数据区（body 前 2 字节为状态头，余下为数据）。</summary>
        public byte[] ReadDevice(ushort addr, byte area, uint count, string what)
        {
            byte[] body = TcpPlcProtocol.BodyReadDevice(addr, area, count);
            var tf = Send(TcpPlcProtocol.OP_CTRL, body, what);
            return ExtractData(tf.Body);
        }

        /// <summary>写软元件。</summary>
        public void WriteDevice(ushort addr, byte area, byte[] data, string what)
        {
            byte[] body = TcpPlcProtocol.BodyWriteDevice(addr, area, data);
            Send(TcpPlcProtocol.OP_CTRL, body, what);
        }

        /// <summary>
        /// 从控制应答 body 提取数据区。
        /// 抓包：带数据应答形如 `05 00 &lt;data&gt;`（与串口一致），
        /// 纯 ACK 形如 `06`（1 字节）。
        /// </summary>
        private static byte[] ExtractData(byte[] body)
        {
            if (body == null || body.Length <= 2) return new byte[0];
            byte[] d = new byte[body.Length - 2];
            Array.Copy(body, 2, d, 0, d.Length);
            return d;
        }

        /// <summary>读运行状态（area=0x00 addr=0x0000，1 字节）。返回 0/1，读不到返回 -1。</summary>
        public int ReadRunState()
        {
            byte[] d = ReadDevice(0x0000, 0x00, 1, "读运行状态");
            return d.Length >= 1 ? d[d.Length - 1] : -1;
        }

        /// <summary>写运行状态（area=0x00 addr=0x0000，1 字节）。0=停止 / 1=运行。</summary>
        public void WriteRunState(byte value)
        {
            WriteDevice(0x0000, 0x00, new byte[] { value },
                        value == TcpPlcProtocol.CTRL_RUN ? "写运行状态=运行" : "写运行状态=停止");
        }

        // ------------------------------------------------------------------
        // 会话
        // ------------------------------------------------------------------
        /// <summary>握手：op=00 body= 01 00，应答为设备信息。</summary>
        public void Handshake()
        {
            var tf = Send(TcpPlcProtocol.OP_HANDSHAKE, TcpPlcProtocol.BodyHandshake(), "握手");
            DeviceModel = ExtractModel(tf.Body);
        }

        /// <summary>登录：op=80 body= 01 00 | "user0" + 0xA7 填充（与 USB/串口一致）。</summary>
        public void Login(string user = "user0")
        {
            Send(TcpPlcProtocol.OP_LOGIN, TcpPlcProtocol.BodyLogin(user), "登录");
        }

        /// <summary>会话控制（抓包 body= 03 00 | 01 00 00 00）。</summary>
        public void Session(uint val = 1)
        {
            Send(TcpPlcProtocol.OP_SESSION, TcpPlcProtocol.BodySession(0x03, 0x00, val), "会话控制");
        }

        // ------------------------------------------------------------------
        // 文件声明 / 内容
        // ------------------------------------------------------------------
        private void DeclareFile(string name, int size, ushort tagA = 0, uint tagB = 0)
        {
            byte[] body = TcpPlcProtocol.BodyFileDeclare(name, size, tagA, tagB, BlockSize);
            Send(TcpPlcProtocol.OP_FILE, body, "文件声明 " + name);
        }

        private void WriteContent(ushort blk, byte[] data)
        {
            byte[] body = TcpPlcProtocol.BodyFileContent(blk, data);
            Send(TcpPlcProtocol.OP_FILE, body, "文件内容 #" + blk + " (" + data.Length + "B)");
            if (InterFrameDelayMs > 0) System.Threading.Thread.Sleep(InterFrameDelayMs);
        }

        // ------------------------------------------------------------------
        // 完整烧录流程（.down / .updown 条目化下发）
        // ------------------------------------------------------------------
        /// <summary>
        /// 把下载包（.down 或 .updown）按 TCP 12939 协议烧录进 PLC。
        ///
        /// 三者的「机型 + 有序条目名 + 条目内容」抽象一致，故统一入参。
        /// </summary>
        /// <param name="pkgName">日志用包名。</param>
        /// <param name="order">条目顺序（按包内原序）。</param>
        /// <param name="files">条目名 → 内容。</param>
        public void Burn(string pkgName, System.Collections.Generic.List<string> order,
                         System.Collections.Generic.Dictionary<string, byte[]> files,
                         Action<string> log, Action<int> progress, bool resumeRun)
        {
            if (log == null) log = delegate { };
            _log = log;
            if (order == null || order.Count == 0)
                throw new PlcClientException("下载包里没有可下发的文件条目");
            if (!_link.IsOpen) throw new PlcClientException("TCP 通道未打开");

            _seq = 1;

            int total = 0;
            foreach (string n in order) total += files[n].Length;
            log(string.Format("下载包：{0}  条目 {1} 个  合计 {2} 字节", pkgName, order.Count, total));

            // [1/6] 握手
            log("[1/6] 握手 ...");
            Handshake();
            log(string.Format("      已连接，PLC 机型={0}",
                              string.IsNullOrEmpty(DeviceModel) ? "(未解析)" : DeviceModel));

            // [2/6] 登录 + 会话
            log("[2/6] 登录 ...");
            Login();
            Session();
            log("      登录成功");

            // [3/6] 停 PLC
            log("[3/6] 停止 PLC ...");
            int orig = ReadRunState();
            log(string.Format("      当前状态={0}（1=运行 0=停止）", orig < 0 ? "?" : orig.ToString()));
            WriteRunState(TcpPlcProtocol.CTRL_STOP);
            System.Threading.Thread.Sleep(300);
            int after = ReadRunState();
            log(string.Format("      已停止（回读={0}）", after < 0 ? "?" : after.ToString()));
            if (after != 0)
                log("      ⚠ 回读未确认为 0，PLC 可能未进入停止态，但仍继续。");

            // [4/6] 逐文件下发
            log(string.Format("[4/6] 传输 {0} 个文件 ...", order.Count));
            int sentBytes = 0;
            for (int i = 0; i < order.Count; i++)
            {
                string name = order[i];
                byte[] data = files[name];
                int baseBytes = sentBytes;
                // 逐块回调进度
                DeclareFile(name, data.Length);
                int s = 0; ushort blk = 0;
                do
                {
                    int n = data.Length - s;
                    if (n > BlockSize) n = BlockSize;
                    byte[] chunk = new byte[n];
                    Array.Copy(data, s, chunk, 0, n);
                    WriteContent(blk, chunk);
                    s += n; blk++;
                    if (progress != null)
                        progress((baseBytes + s) * 100 / Math.Max(total, 1));
                } while (s < data.Length);

                sentBytes += data.Length;
                int pct = sentBytes * 100 / Math.Max(total, 1);
                if (progress != null) progress(pct);
                log(string.Format("      [{0}/{1}] {2}  ({3} 字节, 累计 {4}%)",
                                  i + 1, order.Count, name, data.Length, pct));
            }

            // [5/6] 提交 / 落盘
            log("[5/6] 提交并校验 ...");
            WriteDevice(TcpPlcProtocol.COMMIT_ADDR, 0x00, new byte[] { 0x01, 0x00, 0x00, 0x00 }, "写提交标志");
            byte[] commit = ReadDevice(TcpPlcProtocol.COMMIT_ADDR, 0x00, 4, "读提交状态");
            log(string.Format("      提交返回 {0}", TcpPlcProtocol.ToHex(commit, 8)));

            // [6/6] 恢复运行
            if (resumeRun)
            {
                log("[6/6] 恢复运行 ...");
                WriteRunState(TcpPlcProtocol.CTRL_RUN);
                int st = -1;
                for (int i = 0; i < 3; i++)
                {
                    System.Threading.Thread.Sleep(400);
                    st = ReadRunState();
                    if (st == 1) break;
                    if (i < 2) WriteRunState(TcpPlcProtocol.CTRL_RUN);
                }
                if (st == 1) log("      ✓ 已确认 PLC 进入运行态（RUN，回读=1）。");
                else if (st == 0)
                {
                    log("      ✗ PLC 仍处于停止态（回读=0）！");
                    log("        可能原因：① 程序校验未通过被拒绝运行；② 面板 RUN/STOP 开关在 STOP；③ 程序存在致命错误。");
                }
                else log("      ? 运行态回读失败（无应答），请观察 PLC 面板 RUN 灯。");
            }
            else log("[6/6] 未恢复运行（保持停止，请手动启动）。");

            log(string.Format("烧录完成，共 {0} 字节。", sentBytes));
        }

        /// <summary>从握手应答里解析机型（与串口同策略）。</summary>
        private static string ExtractModel(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            int p = IndexOfAscii(data, "INOVANCE", 0);
            if (p < 0) p = 0;
            int start = -1;
            for (int i = p + 8; i < data.Length; i++)
            {
                bool printable = data[i] >= 0x20 && data[i] < 0x7F;
                if (printable && start < 0) start = i;
                else if (!printable && start >= 0)
                {
                    int len = i - start;
                    if (len >= 2 && len <= 16)
                    {
                        string s = Encoding.ASCII.GetString(data, start, len);
                        if (s != "INOVANCE") return s;
                    }
                    start = -1;
                }
            }
            return "";
        }

        private static int IndexOfAscii(byte[] hay, string needle, int from)
        {
            byte[] n = Encoding.ASCII.GetBytes(needle);
            for (int i = from; i <= hay.Length - n.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < n.Length; j++) if (hay[i + j] != n[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
