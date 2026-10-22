using System;
using System.Text;

namespace INOVANCEPLCTool.Plc
{
    /// <summary>
    /// 汇川 PLC 的 <b>以太网（TCP）私有烧录协议</b>编解码（端口 <b>12939</b>）。
    ///
    /// 协议来源：对「AutoShop 走网口下载程序」全过程的 <b>Wireshark/Npcap 抓包</b>
    /// （plc_tcp.pcap，186 包）逐帧重新解析。下载走 PLC 的 <b>12939</b> 端口，
    /// <b>不是</b> Modbus TCP（502 端口只提供标准 Modbus 软元件读写，不提供烧录）。
    ///
    /// ── 帧格式（全小端）────────────────────────────────────────────
    ///     +0   u16  totalLen   整帧总字节数（<b>含自身 2 字节</b>）
    ///     +2   u8   0x01       固定
    ///     +3   u8   op         操作码（见 OP_*）
    ///     +4   u32  seq        序号，全局递增；一次交互（请求+应答）可共用
    ///     +8   ...  body       载荷（结构随 op 而定）
    ///
    /// ── 与串口 0x43 协议的关系 ────────────────────────────────────
    /// 二者应用层语义<b>完全同构</b>，差别仅在封装：
    ///     串口: [addr u8][0x43][len u16][payload][CRC16 u16]，payload = 01|op|seq(u16)|00 00|body
    ///     TCP : [len u16][0x01][op][seq u32][body]
    /// 即 TCP 去掉了站号/功能码/CRC，seq 由 u16 扩为 u32，body 结构一模一样：
    ///     读软元件 = 01 00 | addr(u16) | area | FF | count(u32)
    ///     写软元件 = 02 00 | addr(u16) | area | FF | count(u32) | data
    ///     文件声明 = 02 00 | 00 00 | tagA(u16) | tagB(u32) | chunk(u32) | size(u32) | name\0
    ///     数据内容 = 05 00 | blk(u16) | 00 00 | datalen(u32) | data
    ///
    /// ── 抓包实证（plccx 网口下载）────────────────────────────────
    ///   · 登录帧 body = 01 00 | "user0" + 0xA7 填充至 64 字节（与 USB/串口登录完全相同）
    ///   · 文件内容为 <b>未压缩原样下发</b>（stprog.o 内容以 ELF 魔数 7F 45 4C 46 开头）
    ///   · 声明顺序与 .down/.updown 的 19 条目一致；18 个内容帧中 15 个与包条目<b>逐字节一致</b>
    ///     （另 3 个因抓包工程与本地 plccx.down 非同一份源码而有差异，非协议问题）
    ///   · 块大小字段 chunk 恒为 10000 (0x2710)
    /// </summary>
    public static class TcpPlcProtocol
    {
        // ---- 操作码 ----
        public const byte OP_HANDSHAKE = 0x00;   // 握手 / 设备信息 / 心跳
        public const byte OP_CTRL = 0x01;        // 控制 / 软元件读写
        public const byte OP_FILE = 0x04;        // 文件传输（声明 / 内容）
        public const byte OP_LOGIN = 0x80;       // 登录
        public const byte OP_SESSION = 0x81;     // 会话控制

        // ---- op=OP_FILE 的子操作码（body 前两字节）----
        public const ushort SUB_FILE_DECLARE = 0x0002;  // 文件声明
        public const ushort SUB_FILE_CONTENT = 0x0005;  // 文件内容（数据块）

        // ---- op=OP_CTRL 的子操作码（body 前两字节）----
        public const ushort SUB_CTRL_READ = 0x0001;   // 读软元件
        public const ushort SUB_CTRL_WRITE = 0x0002;  // 写软元件

        /// <summary>控制帧中表示运行状态的写入值（body 末字节）。</summary>
        public const byte CTRL_STOP = 0x00;
        public const byte CTRL_RUN = 0x01;

        /// <summary>下载完成后「提交 / 落盘」的软元件地址（addr=0x0010, area=0x00, 4 字节）。</summary>
        public const ushort COMMIT_ADDR = 0x0010;

        /// <summary>
        /// 文件内容块大小。<b>注意</b>：TCP 并非把整文件塞进一帧——
        /// 抓包中声明帧携带 chunk=10000 (0x2710)；实测本次下载各文件均 ≤ 10000 字节故为单块。
        /// 大于 10000 字节的文件按此值分块（末块取余数）。
        /// </summary>
        public const int FILE_CHUNK = 0x2710; // 10000

        // ------------------------------------------------------------------
        // 帧组装
        // ------------------------------------------------------------------
        /// <summary>
        /// 组装一整帧：[len u16][01][op][seq u32][body]。
        /// </summary>
        public static byte[] BuildFrame(byte op, uint seq, byte[] body)
        {
            int blen = body == null ? 0 : body.Length;
            int total = 2 + 1 + 1 + 4 + blen;
            byte[] f = new byte[total];
            f[0] = (byte)(total & 0xFF);
            f[1] = (byte)((total >> 8) & 0xFF);
            f[2] = 0x01;
            f[3] = op;
            f[4] = (byte)(seq & 0xFF);
            f[5] = (byte)((seq >> 8) & 0xFF);
            f[6] = (byte)((seq >> 16) & 0xFF);
            f[7] = (byte)((seq >> 24) & 0xFF);
            if (blen > 0) Array.Copy(body, 0, f, 8, blen);
            return f;
        }

        // ------------------------------------------------------------------
        // 帧解析
        // ------------------------------------------------------------------
        /// <summary>拆包结果。</summary>
        public sealed class TcpFrame
        {
            public int Total;
            public byte Op;
            public uint Seq;
            public byte[] Body;

            public TcpFrame() { Body = new byte[0]; }

            /// <summary>file/ctrl 子操作码（body 前两字节，小端）；body 不足 2 字节返回 0。</summary>
            public ushort SubOp
            {
                get { return Body != null && Body.Length >= 2 ? (ushort)(Body[0] | (Body[1] << 8)) : (ushort)0; }
            }
        }

        /// <summary>
        /// 尝试从缓冲解析一帧（基于 u16 长度前缀）。成功返回 true 并给出消耗字节数。
        /// </summary>
        public static bool TryParse(byte[] buf, int offset, int avail, out TcpFrame frame, out int consumed)
        {
            frame = null;
            consumed = 0;
            if (avail < 8) return false;

            int total = buf[offset] | (buf[offset + 1] << 8);
            if (total < 8 || total > 0xFFFF) return false;
            if (total > avail) return false;
            if (buf[offset + 2] != 0x01) return false;

            var f = new TcpFrame();
            f.Total = total;
            f.Op = buf[offset + 3];
            f.Seq = (uint)(buf[offset + 4] | (buf[offset + 5] << 8)
                         | (buf[offset + 6] << 16) | (buf[offset + 7] << 24));
            f.Body = new byte[total - 8];
            if (f.Body.Length > 0) Array.Copy(buf, offset + 8, f.Body, 0, f.Body.Length);

            frame = f;
            consumed = total;
            return true;
        }

        // ------------------------------------------------------------------
        // body 构造
        // ------------------------------------------------------------------
        /// <summary>握手 / 心跳 body = 01 00。</summary>
        public static byte[] BodyHandshake()
        {
            return new byte[] { 0x01, 0x00 };
        }

        /// <summary>登录 body = 01 00 | user + 0xA7 填充至 64 字节（与 USB/串口一致）。</summary>
        public static byte[] BodyLogin(string user)
        {
            byte[] buf = new byte[2 + 64];
            buf[0] = 0x01; buf[1] = 0x00;
            byte[] u = Encoding.ASCII.GetBytes(user ?? "user0");
            int n = Math.Min(u.Length, 64);
            Array.Copy(u, 0, buf, 2, n);
            for (int i = 2 + n; i < buf.Length; i++) buf[i] = 0xA7;
            return buf;
        }

        /// <summary>会话控制 body（抓包实证：03 00 | 01 00 00 00）。</summary>
        public static byte[] BodySession(byte sub0, byte sub1, uint val)
        {
            return new byte[] {
                sub0, sub1,
                (byte)(val & 0xFF), (byte)((val >> 8) & 0xFF),
                (byte)((val >> 16) & 0xFF), (byte)((val >> 24) & 0xFF)
            };
        }

        /// <summary>读软元件 body = 01 00 | addr(u16) | area | FF | count(u32)。</summary>
        public static byte[] BodyReadDevice(ushort addr, byte area, uint count)
        {
            byte[] b = new byte[10];
            b[0] = 0x01; b[1] = 0x00;
            b[2] = (byte)(addr & 0xFF);
            b[3] = (byte)((addr >> 8) & 0xFF);
            b[4] = area;
            b[5] = 0xFF;
            b[6] = (byte)(count & 0xFF);
            b[7] = (byte)((count >> 8) & 0xFF);
            b[8] = (byte)((count >> 16) & 0xFF);
            b[9] = (byte)((count >> 24) & 0xFF);
            return b;
        }

        /// <summary>写软元件 body = 02 00 | addr(u16) | area | FF | count(u32) | data。</summary>
        public static byte[] BodyWriteDevice(ushort addr, byte area, byte[] data)
        {
            int n = data == null ? 0 : data.Length;
            byte[] b = new byte[10 + n];
            b[0] = 0x02; b[1] = 0x00;
            b[2] = (byte)(addr & 0xFF);
            b[3] = (byte)((addr >> 8) & 0xFF);
            b[4] = area;
            b[5] = 0xFF;
            b[6] = (byte)(n & 0xFF);
            b[7] = (byte)((n >> 8) & 0xFF);
            b[8] = (byte)((n >> 16) & 0xFF);
            b[9] = (byte)((n >> 24) & 0xFF);
            if (n > 0) Array.Copy(data, 0, b, 10, n);
            return b;
        }

        /// <summary>
        /// 文件声明 body = 02 00 | 00 00 | tagA(u16) | tagB(u32) | chunk(u32) | size(u32) | name\0
        /// （抓包实证：project.hcpp 的 tagA=0x39 tagB=0x32；chunk 恒为 10000）
        /// </summary>
        public static byte[] BodyFileDeclare(string name, int size, ushort tagA = 0, uint tagB = 0, int chunk = FILE_CHUNK)
        {
            byte[] nb = Encoding.UTF8.GetBytes(name ?? "");
            byte[] b = new byte[4 + 2 + 4 + 4 + 4 + nb.Length + 1];
            int o = 0;
            b[o++] = 0x02; b[o++] = 0x00;      // subop = 声明
            b[o++] = 0x00; b[o++] = 0x00;      // 保留
            b[o++] = (byte)(tagA & 0xFF); b[o++] = (byte)((tagA >> 8) & 0xFF);
            b[o++] = (byte)(tagB & 0xFF); b[o++] = (byte)((tagB >> 8) & 0xFF);
            b[o++] = (byte)((tagB >> 16) & 0xFF); b[o++] = (byte)((tagB >> 24) & 0xFF);
            b[o++] = (byte)(chunk & 0xFF); b[o++] = (byte)((chunk >> 8) & 0xFF);
            b[o++] = (byte)((chunk >> 16) & 0xFF); b[o++] = (byte)((chunk >> 24) & 0xFF);
            b[o++] = (byte)(size & 0xFF); b[o++] = (byte)((size >> 8) & 0xFF);
            b[o++] = (byte)((size >> 16) & 0xFF); b[o++] = (byte)((size >> 24) & 0xFF);
            Array.Copy(nb, 0, b, o, nb.Length);
            // 末尾 NUL：数组已初始化为 0，天然存在
            return b;
        }

        /// <summary>文件内容 body = 05 00 | blk(u16) | 00 00 | datalen(u32) | data。</summary>
        public static byte[] BodyFileContent(ushort blk, byte[] data)
        {
            int n = data == null ? 0 : data.Length;
            byte[] b = new byte[10 + n];
            b[0] = 0x05; b[1] = 0x00;
            b[2] = (byte)(blk & 0xFF);
            b[3] = (byte)((blk >> 8) & 0xFF);
            b[4] = 0x00; b[5] = 0x00;
            b[6] = (byte)(n & 0xFF);
            b[7] = (byte)((n >> 8) & 0xFF);
            b[8] = (byte)((n >> 16) & 0xFF);
            b[9] = (byte)((n >> 24) & 0xFF);
            if (n > 0) Array.Copy(data, 0, b, 10, n);
            return b;
        }

        public static string ToHex(byte[] b, int max)
        {
            if (b == null) return "";
            var sb = new StringBuilder();
            int n = Math.Min(max, b.Length);
            for (int i = 0; i < n; i++) sb.AppendFormat("{0:X2} ", b[i]);
            if (b.Length > n) sb.Append("..");
            return sb.ToString().TrimEnd();
        }
    }
}
