using System;
using System.Text;
using INOVANCEPLCTool.Transport;

namespace INOVANCEPLCTool.Plc
{
    /// <summary>
    /// 汇川 Easy 系列 PLC 的<b>串口烧录客户端</b>（0x43 私有协议）。
    ///
    /// 时序完全按「触摸屏 → PLC」真实抓包还原（code.txt，226 帧），
    /// 每一步的帧结构都已逐字节比对通过。
    ///
    /// 完整时序（seq 全局递增，一次交互的请求与应答共用同一 seq）：
    ///   [0]  seq=0   握手              op=00 body= 01 00
    ///                 ↔ 应答 body= 05 00 + 设备信息(INOVANCE / H6E / 版本 …)
    ///   [1]  seq=1   读运行状态        op=01 body= 01 00 | 00 00 | 00 FF | 01 00 00 00
    ///                 ↔ 应答 body= 05 00 | 01        （1=运行中）
    ///   [2]  seq=2   写运行状态=停止   op=01 body= 02 00 | 00 00 | 00 FF | 01 00 00 00 | 00
    ///                 ↔ 应答 body= 06
    ///   [3]  seq=3   回读确认已停止    op=01 body= 01 00 | 00 00 | 00 FF | 01 00 00 00
    ///                 ↔ 应答 body= 05 00 | 00        （0=已停止）
    ///   [4]  seq=4   文件声明          op=04 body= 02 00 | 8×00 | 块大小u32 | 文件大小u32 | "project/PLCProgram.down"\0
    ///                 ↔ 应答 body= 06 00 | 文件大小u32
    ///   [5..] seq=5.. 数据块 ×N        op=04 body= 05 00 | 块号u16 | 00 00 | 本块长度u32 | data
    ///                 ↔ 应答 body= 06 00 | 块号u16 | 00 00
    ///   [末]  seq=N+1 写 addr=0x0010     op=01 body= 02 00 | 10 00 | 00 FF | 04 00 00 00 | 01 00 00 00
    ///                 ↔ 应答 body= 06                  （提交 / 落盘）
    ///   [末]  seq=N+2 读 addr=0x0010     op=01 body= 01 00 | 10 00 | 00 FF | 04 00 00 00
    ///                 ↔ 应答 body= 05 00 | 01 00 64 00
    ///   [末]  seq=N+3 读运行状态       op=01 body= 01 00 | 00 00 | 00 FF | 01 00 00 00
    ///   [末]  seq=N+4 写运行状态=运行  op=01 body= 02 00 | 00 00 | 00 FF | 01 00 00 00 | 01（可选）
    ///
    /// ⚠ 三个最容易踩的坑（第一版实现全踩了，导致 PLC 毫无反应）：
    ///   ① 必须先握手（seq=0），握手应答里带机型信息，可用于防呆；
    ///   ② 末块必须发<b>实际余数长度</b>，且 body 里的 u32 字段要写该长度（不是固定 490）；
    ///   ③ 数据发完必须做「写/读 area=0x10」的提交动作，再写运行态。
    /// </summary>
    public sealed class SerialBurnClient
    {
        private readonly SerialLink _link;
        private ushort _seq;
        private Action<string> _log;

        /// <summary>完整块字节数（抓包实测 490）。</summary>
        public int BlockSize = SerialProtocol.FILE_BLOCK;

        /// <summary>两次请求之间的最小间隔（毫秒）。</summary>
        public int InterFrameDelayMs = 5;

        /// <summary>单帧应答超时。</summary>
        public int ResponseTimeoutMs = 3000;

        /// <summary>握手应答里解析出的机型（如 "H6E"），解析不到为空串。</summary>
        public string DeviceModel = "";

        public SerialBurnClient(SerialLink link)
        {
            if (link == null) throw new ArgumentNullException("link");
            _link = link;
        }

        // ------------------------------------------------------------------
        // 底层：发一帧 + 收一帧（一问一答）
        // ------------------------------------------------------------------
        private SerialProtocol.SerialFrame Send(byte op, byte[] body, string what)
        {
            ushort seq = _seq;
            byte[] payload = SerialProtocol.Payload(op, seq, body);
            byte[] frame = SerialProtocol.Build(payload, SerialProtocol.DEFAULT_ADDR);

            if (_log != null && what != null)
                _log(string.Format("  → seq={0}  {1}  {2}", seq, what, SerialProtocol.ToHex(frame, 20)));

            byte[] rsp = _link.RequestFrame(frame, ResponseTimeoutMs);
            if (rsp == null || rsp.Length < 6)
                throw new PlcClientException("串口无应答（" + what + "）。请确认 PLC 在线、波特率/校验匹配、A/B 未接反。");

            int consumed;
            SerialProtocol.SerialFrame sf;
            if (!SerialProtocol.TryParse(rsp, 0, rsp.Length, out sf, out consumed))
                throw new PlcClientException(
                    "应答帧校验失败（" + what + "）：" + SerialProtocol.ToHex(rsp, 32));

            if (_log != null)
                _log(string.Format("  ← seq={0}  type=0x{1:X2}  {2}", sf.Seq, sf.Type, SerialProtocol.ToHex(rsp, 20)));

            if (sf.Seq != seq && _log != null)
                _log(string.Format("      ! 应答 seq={0} 与请求 seq={1} 不一致", sf.Seq, seq));

            // 应答类型字节：0x05=带数据成功，0x06=纯 ACK 成功；其它视为错误
            if (sf.Type != SerialProtocol.ACK_DATA && sf.Type != SerialProtocol.ACK_DONE)
                throw new PlcClientException(string.Format(
                    "PLC 返回错误应答（{0}）：type=0x{1:X2} body={2}", what, sf.Type, SerialProtocol.ToHex(sf.Body, 16)));

            _seq++;
            return sf;
        }

        // ------------------------------------------------------------------
        // 控制帧：软元件读写
        // ------------------------------------------------------------------
        /// <summary>读软元件：body = 01 00 | addr(u16) | area | 0xFF | count(u32)；返回应答数据区。</summary>
        private byte[] ReadDevice(ushort addr, byte area, uint count, string what)
        {
            var body = new byte[2 + 2 + 2 + 4];
            int o = 0;
            body[o++] = 0x01; body[o++] = 0x00;
            body[o++] = (byte)(addr & 0xFF);
            body[o++] = (byte)((addr >> 8) & 0xFF);
            body[o++] = area;
            body[o++] = 0xFF;
            body[o++] = (byte)(count & 0xFF);
            body[o++] = (byte)((count >> 8) & 0xFF);
            body[o++] = (byte)((count >> 16) & 0xFF);
            body[o++] = (byte)((count >> 24) & 0xFF);
            var sf = Send(SerialProtocol.OP_CTRL, body, what);
            return sf.Data;
        }

        /// <summary>写软元件：body = 02 00 | addr(u16) | area | 0xFF | count(u32) | 数据。</summary>
        private void WriteDevice(ushort addr, byte area, byte[] data, string what)
        {
            uint count = (uint)data.Length;
            var body = new byte[2 + 2 + 2 + 4 + data.Length];
            int o = 0;
            body[o++] = 0x02; body[o++] = 0x00;
            body[o++] = (byte)(addr & 0xFF);
            body[o++] = (byte)((addr >> 8) & 0xFF);
            body[o++] = area;
            body[o++] = 0xFF;
            body[o++] = (byte)(count & 0xFF);
            body[o++] = (byte)((count >> 8) & 0xFF);
            body[o++] = (byte)((count >> 16) & 0xFF);
            body[o++] = (byte)((count >> 24) & 0xFF);
            Array.Copy(data, 0, body, o, data.Length);
            Send(SerialProtocol.OP_CTRL, body, what);
        }

        /// <summary>
        /// 读运行状态（area=0x00 addr=0x0000，<b>必须读 1 字节</b>）。返回 0/1，读不到返回 -1。
        ///
        /// ⚠ 不要改成读 2/4 字节：实测读 1 字节返回 `05 00 &lt;状态&gt;`（真实状态），
        ///   而读 2/4 字节走的另一种应答格式 `0F 00 40 00 …`，该值恒定不变、**不反映运行态**。
        /// </summary>
        public int ReadRunState()
        {
            byte[] d = ReadDevice(0x0000, 0x00, 1, "读运行状态");
            return d.Length >= 1 ? d[d.Length - 1] : -1;
        }

        /// <summary>写运行状态（area=0x00 addr=0x0000，1 字节）。0=停止 / 1=运行。</summary>
        public void WriteRunState(byte value)
        {
            WriteDevice(0x0000, 0x00, new byte[] { value },
                        value == SerialProtocol.CTRL_RUN ? "写运行状态=运行" : "写运行状态=停止");
        }

        // ------------------------------------------------------------------
        // 文件声明 / 数据块
        // ------------------------------------------------------------------
        private void DeclareFile(string name, int size)
        {
            byte[] nb = Encoding.UTF8.GetBytes(name);
            // 抓包实测（seq=4）：02 00 | 00×8 | EA 01 00 00 | <u32 文件大小> | <文件名> 00
            var body = new byte[2 + 8 + 4 + 4 + nb.Length + 1];
            int o = 0;
            body[o++] = 0x02; body[o++] = 0x00;
            o += 8;
            body[o++] = (byte)(BlockSize & 0xFF);
            body[o++] = (byte)((BlockSize >> 8) & 0xFF);
            body[o++] = (byte)((BlockSize >> 16) & 0xFF);
            body[o++] = (byte)((BlockSize >> 24) & 0xFF);
            body[o++] = (byte)(size & 0xFF);
            body[o++] = (byte)((size >> 8) & 0xFF);
            body[o++] = (byte)((size >> 16) & 0xFF);
            body[o++] = (byte)((size >> 24) & 0xFF);
            Array.Copy(nb, 0, body, o, nb.Length);

            Send(SerialProtocol.OP_FILE, body, "文件声明 " + name);
        }

        /// <summary>
        /// 写一个数据块。抓包实测（seq=5，块号 0）：05 00 | 块号u16 | 00 00 | 本块长度u32 | 数据。
        /// 完整块长度=490；<b>末块 = 实际余数</b>（示例末块 31）。
        /// </summary>
        private void WriteBlock(ushort blkIdx, byte[] data)
        {
            int n = data.Length;
            var body = new byte[2 + 2 + 2 + 4 + n];
            int o = 0;
            body[o++] = 0x05; body[o++] = 0x00;
            body[o++] = (byte)(blkIdx & 0xFF);
            body[o++] = (byte)((blkIdx >> 8) & 0xFF);
            body[o++] = 0x00; body[o++] = 0x00;
            body[o++] = (byte)(n & 0xFF);
            body[o++] = (byte)((n >> 8) & 0xFF);
            body[o++] = (byte)((n >> 16) & 0xFF);
            body[o++] = (byte)((n >> 24) & 0xFF);
            Array.Copy(data, 0, body, o, n);

            Send(SerialProtocol.OP_FILE, body, "数据块 #" + blkIdx + " (" + n + "B)");
            if (InterFrameDelayMs > 0) System.Threading.Thread.Sleep(InterFrameDelayMs);
        }

        // ------------------------------------------------------------------
        // 完整烧录流程
        // ------------------------------------------------------------------
        /// <summary>把 .down 包按串口 0x43 协议烧录进 PLC。</summary>
        public void Burn(DownPackage pkg, Action<string> log, Action<int> progress, bool resumeRun)
        {
            if (log == null) log = delegate { };
            _log = log;
            if (pkg == null) throw new PlcClientException("下载包为空");
            if (!_link.IsOpen) throw new PlcClientException("串口未打开");

            _seq = 0;

            // 下发的就是 .down 文件的【完整原始字节流】（抓包逐字节验证：50501 字节原样下发）
            byte[] stream = pkg.Raw;
            if (stream == null || stream.Length == 0)
                throw new PlcClientException(".down 包内容为空");
            int total = stream.Length;
            log(string.Format("下载包：{0}  机型={1}  条目 {2} 个  合计 {3} 字节",
                              System.IO.Path.GetFileName(pkg.Path), pkg.Model, pkg.Order.Count, total));

            // [0] 握手
            log("[1/6] 握手 ...");
            Handshake();
            log(string.Format("      已连接，PLC 机型={0}",
                              string.IsNullOrEmpty(DeviceModel) ? "(未解析)" : DeviceModel));
            if (!string.IsNullOrEmpty(DeviceModel) && !string.IsNullOrEmpty(pkg.Model)
                && DeviceModel != pkg.Model)
                log(string.Format("      ⚠ 注意：包机型 {0} 与 PLC 机型 {1} 不一致，请确认。", pkg.Model, DeviceModel));

            // [1] 读状态 → 停机 → 回读
            log("[2/6] 停止 PLC ...");
            int orig = ReadRunState();
            log(string.Format("      当前状态={0}（1=运行 0=停止）", orig < 0 ? "?" : orig.ToString()));
            WriteRunState(SerialProtocol.CTRL_STOP);
            int after = ReadRunState();
            log(string.Format("      已停止（回读={0}）", after < 0 ? "?" : after.ToString()));
            if (after != 0)
                log("      ⚠ 回读未确认为 0，PLC 可能未进入停止态，但仍继续。");

            // [2] 文件声明
            log("[3/6] 声明文件 ...");
            DeclareFile("project/PLCProgram.down", total);

            // [3] 逐块写入
            int blocks = (total + BlockSize - 1) / BlockSize;
            int lastLen = total - (blocks - 1) * BlockSize;
            log(string.Format("[4/6] 写入 {0} 个数据块（完整块 {1} B，末块 {2} B）...",
                              blocks, BlockSize, lastLen));
            int sent = 0;
            ushort blk = 0;
            while (sent < total)
            {
                int n = total - sent;
                if (n > BlockSize) n = BlockSize;
                var chunk = new byte[n];
                Array.Copy(stream, sent, chunk, 0, n);
                WriteBlock(blk, chunk);
                sent += n;
                blk++;
                if (progress != null) progress(sent * 100 / Math.Max(total, 1));
            }
            log(string.Format("      已写入 {0} 字节 / {1} 块", sent, blk));

            // [4] 提交 / 落盘：抓包实测是读写 addr=0x0010（area=0x00）的 4 字节软元件
            log("[5/6] 提交并校验 ...");
            WriteDevice(SerialProtocol.COMMIT_ADDR, 0x00, new byte[] { 0x01, 0x00, 0x00, 0x00 }, "写提交标志");
            byte[] commit = ReadDevice(SerialProtocol.COMMIT_ADDR, 0x00, 4, "读提交状态");
            log(string.Format("      提交返回 {0}", SerialProtocol.ToHex(commit, 8)));

            // [5] 恢复运行（可选）
            if (resumeRun)
            {
                log("[6/6] 恢复运行 ...");
                WriteRunState(SerialProtocol.CTRL_RUN);

                // 写后轮询确认：PLC 置位可能有延迟，写一次读一次容易误判。
                // 实测本机型写后 <1s 即生效，这里重试 3 次、每次间隔 400ms。
                int st = -1;
                for (int i = 0; i < 3; i++)
                {
                    System.Threading.Thread.Sleep(400);
                    st = ReadRunState();
                    if (st == 1) break;
                    if (i < 2) WriteRunState(SerialProtocol.CTRL_RUN); // 未确认成功则重发
                }

                if (st == 1)
                {
                    log("      ✓ 已确认 PLC 进入运行态（RUN，回读=1）。");
                }
                else if (st == 0)
                {
                    log("      ✗ PLC 仍处于停止态（回读=0）！");
                    log("        可能原因：① 程序下载后校验未通过被拒绝运行；");
                    log("                  ② PLC 面板的 RUN/STOP 硬件开关拨在 STOP；");
                    log("                  ③ 程序中存在致命错误导致无法启动。");
                    log("        可点上方「RUN 运行」按钮重试，或断电重启 PLC。");
                }
                else
                {
                    log("      ? 运行态回读失败（无应答），无法确认是否已运行，请观察 PLC 面板 RUN 灯。");
                }
            }
            else
            {
                log("[6/6] 未恢复运行（保持停止，请手动启动）。");
            }

            log(string.Format("烧录完成，共 {0} 字节。", sent));
        }

        /// <summary>握手：op=00 body= 01 00，应答为设备信息（含厂商/机型/版本）。顺带解析机型。</summary>
        public void Handshake()
        {
            var body = new byte[] { 0x01, 0x00 };
            var sf = Send(SerialProtocol.OP_HANDSHAKE, body, "握手");
            DeviceModel = ExtractModel(sf.Data);
        }

        /// <summary>
        /// 从握手应答里解析机型。样本："INOVANCE"(偏移 36) … "H6E"(偏移 70) … "6.12.0.5"。
        /// 策略：厂商字段之后出现的第一个 2~16 字节可见 ASCII 串即机型。
        /// </summary>
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
