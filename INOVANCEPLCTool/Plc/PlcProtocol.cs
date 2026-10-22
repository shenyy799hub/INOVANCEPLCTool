using System;
using System.Collections.Generic;
using System.Text;

namespace INOVANCEPLCTool.Plc
{
    /// <summary>
    /// 汇川 H5U / H6E / H6ES（Easy 系列）PLC 的 USB 私有协议常量。
    ///
    /// 协议来源：对 AutoShop 下载过程的 USB 报文抓包逆向还原，
    /// 并已在 Easy320(H6E) 与 Easy301(H6ES) 上实机验证。
    ///
    /// 帧格式（全小端）：
    ///     +0  u16  len      整帧总长度（含本字段）
    ///     +2  u8   ver      恒为 0x01
    ///     +3  u8   cmd      命令类别
    ///     +4  u32  seq      序列号，请求与应答一致
    ///     +8  u8   sub      子命令；在应答中复用为状态码
    ///     +9  u8   rsv      保留，恒为 0x00
    ///     +10 ...  payload
    /// </summary>
    public static class Proto
    {
        public const byte VER = 0x01;

        // ---- 命令类别 ----
        public const byte CMD_DEVINFO = 0x00;   // 设备信息
        public const byte CMD_DEVICE = 0x01;    // 软元件 / 状态读写
        public const byte CMD_FILE = 0x04;      // 文件传输
        public const byte CMD_SESSION = 0x80;   // 会话（登录）
        public const byte CMD_SESSCTL = 0x81;   // 会话控制（提交 / 心跳）

        // ---- 子命令 ----
        public const byte SUB_INFO_READ = 0x01;
        public const byte SUB_DEV_READ = 0x01;
        public const byte SUB_DEV_WRITE = 0x02;
        public const byte SUB_FILE_BEGIN = 0x02;
        public const byte SUB_FILE_DATA = 0x05;
        public const byte SUB_LOGIN = 0x01;
        public const byte SUB_COMMIT = 0x02;
        public const byte SUB_KEEPALIVE = 0x03;

        // ---- 应答状态码 ----
        public const byte ST_DATA = 0x05;       // 成功，带数据
        public const byte ST_OK = 0x06;         // 成功，无数据
        public const byte ST_DATA2 = 0x0F;      // 成功，带数据（另一种形式）

        // ---- PLC 运行状态（软元件 area=0x00 addr=0x0000 type=0xFF）----
        public const byte RUN_STOP = 0x00;
        public const byte RUN_RUN = 0x01;
        public const byte RUN_DOWNLOAD = 0x02;  // 进入下载模式
        public const byte RUN_DOWNDONE = 0x03;  // 下载完成，触发校验/落盘

        /// <summary>
        /// 文件声明帧中 12 字节属性区的末 4 字节。
        /// 抓包显示前 8 字节是 AutoShop 未初始化的栈内存
        /// （同一文件每次都不同，出现过 "Auto"、"clQob" 之类的残留字符串），
        /// PLC 端并不校验；只有这个 0x0000A000 是稳定有效值。
        /// </summary>
        public const uint FILE_ATTR_TAIL = 0x0000A000;

        public static string RunStateName(int state)
        {
            switch (state)
            {
                case RUN_STOP: return "停止";
                case RUN_RUN: return "运行";
                case RUN_DOWNLOAD: return "下载模式";
                case RUN_DOWNDONE: return "下载完成";
                default: return "未知";
            }
        }
    }

    /// <summary>一帧协议报文。</summary>
    public sealed class PlcFrame
    {
        public byte Cmd;
        public uint Seq;
        public byte Status;         // 请求时为 sub，应答时为状态码
        public byte[] Payload;

        public bool IsOk
        {
            get
            {
                return Status == Proto.ST_DATA
                    || Status == Proto.ST_OK
                    || Status == Proto.ST_DATA2;
            }
        }

        /// <summary>组包。</summary>
        public static byte[] Build(byte cmd, uint seq, byte sub, byte[] payload)
        {
            int plen = payload == null ? 0 : payload.Length;
            int total = 10 + plen;
            byte[] f = new byte[total];
            f[0] = (byte)(total & 0xFF);
            f[1] = (byte)((total >> 8) & 0xFF);
            f[2] = Proto.VER;
            f[3] = cmd;
            f[4] = (byte)(seq & 0xFF);
            f[5] = (byte)((seq >> 8) & 0xFF);
            f[6] = (byte)((seq >> 16) & 0xFF);
            f[7] = (byte)((seq >> 24) & 0xFF);
            f[8] = sub;
            f[9] = 0;
            if (plen > 0) Array.Copy(payload, 0, f, 10, plen);
            return f;
        }

        /// <summary>
        /// 拆包。
        ///
        /// 应答帧头与请求同构，但没有 payload 时 PLC 会省略末尾的保留字节，
        /// 此时 len = 9。实测登录成功的应答就是 09 00 01 80 00000000 06。
        /// </summary>
        public static PlcFrame Parse(byte[] buf)
        {
            if (buf == null || buf.Length < 9)
                throw new InvalidOperationException(
                    "应答过短：" + (buf == null ? 0 : buf.Length) + " 字节 -> " + ToHex(buf, 16));

            int total = buf[0] | (buf[1] << 8);
            byte ver = buf[2];
            if (ver != Proto.VER)
                throw new InvalidOperationException(
                    string.Format("协议版本异常: 0x{0:X2}（原始: {1}）", ver, ToHex(buf, 16)));

            var f = new PlcFrame();
            f.Cmd = buf[3];
            f.Seq = (uint)(buf[4] | (buf[5] << 8) | (buf[6] << 16) | (buf[7] << 24));
            f.Status = buf[8];

            if (total > 10 && total <= buf.Length)
            {
                f.Payload = new byte[total - 10];
                Array.Copy(buf, 10, f.Payload, 0, total - 10);
            }
            else
            {
                f.Payload = new byte[0];
            }
            return f;
        }

        public static string ToHex(byte[] b, int max)
        {
            if (b == null) return "";
            var sb = new StringBuilder();
            int n = Math.Min(max, b.Length);
            for (int i = 0; i < n; i++) sb.AppendFormat("{0:x2}", b[i]);
            if (b.Length > n) sb.Append("..");
            return sb.ToString();
        }
    }

    /// <summary>设备信息应答（256 字节）的解析结果。</summary>
    public sealed class PlcDeviceInfo
    {
        public byte[] Raw { get; private set; }
        public string Vendor { get; private set; }
        public string Model { get; private set; }
        public string Ip { get; private set; }
        public string Mask { get; private set; }
        public string Gateway { get; private set; }

        public PlcDeviceInfo(byte[] raw)
        {
            Raw = raw ?? new byte[0];
            Vendor = ReadStr(0x22, 16);
            Model = ReadStr(0x44, 16);
            Ip = ReadIp(0x6E);
            Mask = ReadIp(0x72);
            Gateway = ReadIp(0x76);
        }

        private string ReadStr(int off, int len)
        {
            if (Raw.Length < off + len) return "";
            int end = off;
            while (end < off + len && Raw[end] != 0) end++;
            return Encoding.ASCII.GetString(Raw, off, end - off).Trim();
        }

        private string ReadIp(int off)
        {
            if (Raw.Length < off + 4) return "";
            return string.Format("{0}.{1}.{2}.{3}", Raw[off], Raw[off + 1], Raw[off + 2], Raw[off + 3]);
        }
    }
}
