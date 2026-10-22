using System;
using System.Text;

namespace INOVANCEPLCTool.Plc
{
    /// <summary>
    /// 汇川 Easy 系列 PLC 的<b>串口（RS485/RS232）烧录协议</b>编解码（0x43 私有协议）。
    ///
    /// 协议来源：对「触摸屏串口直连 PLC 烧录」全过程的 485 总线报文抓包
    /// （C:\Users\Sheny\Desktop\code.txt）逐帧重新解析。
    ///
    /// ⚠ 抓包说明：监控工具每行显示的是<b>总线上的全部收发</b>，一行里常把
    ///   「请求帧 + 应答帧」粘在一起（长帧还会被折行）。因此解析必须先按
    ///   CRC 逐帧切开，再按 (op, seq) 配对成"一问一答"，不能当成单向流。
    ///
    /// 帧格式（全小端）：
    ///     +0  u8   addr     站号，默认 0x01
    ///     +1  u8   0x43     功能码（固定，非 Modbus）
    ///     +2  u16  len      长度字段 = payload 长度 + 2（含 CRC 两字节）
    ///     +4  ...  payload  载荷，长度 = len - 2
    ///     末尾 u16  CRC16（标准 Modbus 算法：初值 0xFFFF，多项式 0xA001，低字节在前）
    ///   整帧总长 = 4 + len。
    ///
    /// 载荷结构：
    ///     +0  u8   0x01     固定（协议版本 / 方向位）
    ///     +1  u8   op       操作码：0x00=握手/设备信息, 0x01=控制/软元件读写, 0x04=文件传输
    ///     +2  u16  seq      序号；<b>一次交互（请求+应答）共用同一个 seq</b>，全局递增
    ///     +4  u16  0x0000   保留
    ///     +6  ...  body     操作体（首 2 字节为子操作码 subop）
    ///
    /// body 首字段：
    ///   请求：subop(u16)  0x0001=CTRL_READ（控制/软元件读）、0x0002=CTRL_WRITE（写/文件声明）；
    ///   应答：类型(u8)    0x05=ACK_DATA（成功带数据）、0x06=ACK_DONE（成功、纯 ACK，仅 1 字节）。
    ///
    /// 帧体的结构取决于方向：
    ///   <b>请求</b>（PC→PLC）：`body = subop(u16) + 参数…`，subop 见 SUB_CTRL_READ/WRITE 等；
    ///   <b>应答</b>（PLC→PC）：`body = 类型字节(u8) + [数据…]`——
    ///     类型 0x05 = 成功带数据（如 `05 00 01` 读到的值、`05 00 01 00 64 00` 提交回读）；
    ///     类型 0x06 = 纯 ACK（<b>body 只有这 1 个字节</b>，如 `06`）。
    /// ⚠ 因此读应答类型必须按<b>单字节</b>读，不能当 u16（纯 ACK 只有 1 字节，按 u16 会误判为 0x0000）。
    /// </summary>
    public static class SerialProtocol
    {
        public const byte DEFAULT_ADDR = 0x01;
        public const byte FUNC = 0x43;

        // ---- 操作码 ----
        public const byte OP_HANDSHAKE = 0x00;   // 握手 / 设备信息
        public const byte OP_CTRL = 0x01;        // 控制 / 软元件读写
        public const byte OP_FILE = 0x04;        // 文件传输

        // ---- 请求的子操作码（u16，body 前两字节）----
        public const ushort SUB_CTRL_READ = 0x0001;   // 读请求
        public const ushort SUB_CTRL_WRITE = 0x0002;  // 写请求 / 文件声明

        // ---- 应答的类型字节（u8，body 首字节）----
        public const byte ACK_DATA = 0x05;   // 成功，带数据（body = 05 00 + 数据）
        public const byte ACK_DONE = 0x06;   // 成功，无数据（body = 06，仅 1 字节）

        /// <summary>
        /// 文件数据块<b>完整块</b>大小：抓包实测恒为 0x01EA = 490 字节。
        /// 末块取余数（示例：50501 = 490×103 + 31，共 104 块，末块 31 字节）。
        /// </summary>
        public const int FILE_BLOCK = 0x01EA; // 490

        /// <summary>控制帧中表示运行状态的写入值（body 末字节）。</summary>
        public const byte CTRL_STOP = 0x00;
        public const byte CTRL_RUN = 0x01;

        /// <summary>
        /// 下载完成后需要读写的「提交 / 落盘」软元件<b>地址</b>（addr=0x0010, area=0x00, 4 字节）。
        /// 抓包实测：写 `02 00 | 10 00 | 00 FF | 04 00 00 00 | 01 00 00 00`，
        /// 读 `01 00 | 10 00 | 00 FF | 04 00 00 00` ↔ `05 00 | 01 00 64 00`。
        /// </summary>
        public const ushort COMMIT_ADDR = 0x0010;

        // ------------------------------------------------------------------
        // CRC16（标准 Modbus：初值 0xFFFF，多项式 0xA001）
        // ------------------------------------------------------------------
        public static ushort Crc16(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 1) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                    else crc = (ushort)(crc >> 1);
                }
            }
            return crc;
        }

        // ------------------------------------------------------------------
        // 组包 / 拆包
        // ------------------------------------------------------------------
        /// <summary>把载荷封装成一整帧（含站号、功能码、长度、CRC）。</summary>
        public static byte[] Build(byte[] payload, byte addr = DEFAULT_ADDR)
        {
            int plen = payload == null ? 0 : payload.Length;
            int len = plen + 2;              // len 字段 = 载荷 + CRC(2)
            int total = 4 + len;
            byte[] f = new byte[total];
            f[0] = addr;
            f[1] = FUNC;
            f[2] = (byte)(len & 0xFF);
            f[3] = (byte)((len >> 8) & 0xFF);
            if (plen > 0) Array.Copy(payload, 0, f, 4, plen);
            ushort crc = Crc16(f, 0, total - 2);
            f[total - 2] = (byte)(crc & 0xFF);
            f[total - 1] = (byte)((crc >> 8) & 0xFF);
            return f;
        }

        /// <summary>
        /// 构造载荷：01 | op | seq(u16) | 00 00 | body
        /// 其中 body 传入「subop + 参数」。
        /// </summary>
        public static byte[] Payload(byte op, ushort seq, byte[] body)
        {
            int blen = body == null ? 0 : body.Length;
            byte[] p = new byte[6 + blen];
            p[0] = 0x01;
            p[1] = op;
            p[2] = (byte)(seq & 0xFF);
            p[3] = (byte)((seq >> 8) & 0xFF);
            p[4] = 0x00; p[5] = 0x00;
            if (blen > 0) Array.Copy(body, 0, p, 6, blen);
            return p;
        }

        /// <summary>拆包结果。</summary>
        public sealed class SerialFrame
        {
            public byte Addr;
            public ushort Len;
            public byte[] Payload;
            public byte Op;
            public ushort Seq;
            public byte[] Body;
            public bool CrcOk;

            /// <summary>
            /// 应答类型字节（body 首字节）。
            ///
            /// ⚠ 关键：应答的 body 长度<b>不定</b>——
            ///   - 纯 ACK（成功、无数据）：body 只有 <b>1 字节</b>，如 `06`；
            ///   - 带数据应答：body = <b>3 字节以上</b>，如 `05 00 01`（读到的值）/`05 00 01 00 64 00`。
            /// 因此不能像"请求的 subop"那样按 u16 读两字节——纯 ACK 会越界失效。
            /// </summary>
            public byte Type
            {
                get { return Body != null && Body.Length >= 1 ? Body[0] : (byte)0; }
            }

            /// <summary>带数据应答（Type=0x05）时，body[2:] 即数据区；纯 ACK 时为 0 字节。</summary>
            public byte[] Data
            {
                get
                {
                    if (Body == null || Body.Length <= 2) return new byte[0];
                    byte[] d = new byte[Body.Length - 2];
                    Array.Copy(Body, 2, d, 0, d.Length);
                    return d;
                }
            }
        }

        /// <summary>
        /// 尝试从缓冲解析一帧。true 表示成功；false 表示数据不足一帧、帧头不合法或 CRC 失败。
        /// </summary>
        public static bool TryParse(byte[] buf, int offset, int avail, out SerialFrame frame, out int consumed)
        {
            frame = null;
            consumed = 0;
            if (avail < 6) return false;
            if (buf[offset] != DEFAULT_ADDR && buf[offset] != 0x00) return false;
            if (buf[offset + 1] != FUNC) return false;

            ushort len = (ushort)(buf[offset + 2] | (buf[offset + 3] << 8));
            int total = 4 + len;
            if (len < 2 || total > avail) return false;

            byte[] fr = new byte[total];
            Array.Copy(buf, offset, fr, 0, total);

            ushort crcCalc = Crc16(fr, 0, total - 2);
            ushort crcGot = (ushort)(fr[total - 2] | (fr[total - 1] << 8));
            if (crcCalc != crcGot) return false;

            var sf = new SerialFrame();
            sf.Addr = fr[0];
            sf.Len = len;
            sf.Payload = new byte[len - 2];
            if (sf.Payload.Length > 0) Array.Copy(fr, 4, sf.Payload, 0, len - 2);
            sf.CrcOk = true;
            if (sf.Payload.Length >= 6)
            {
                sf.Op = sf.Payload[1];
                sf.Seq = (ushort)(sf.Payload[2] | (sf.Payload[3] << 8));
                sf.Body = new byte[sf.Payload.Length - 6];
                Array.Copy(sf.Payload, 6, sf.Body, 0, sf.Body.Length);
            }
            else
            {
                sf.Body = new byte[0];
            }
            frame = sf;
            consumed = total;
            return true;
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
