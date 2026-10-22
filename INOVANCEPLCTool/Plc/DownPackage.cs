using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace INOVANCEPLCTool.Plc
{
    public class DownPackageException : Exception
    {
        public DownPackageException(string message) : base(message) { }
    }

    /// <summary>
    /// AutoShop 导出的 <b>.down</b> 串口/触摸屏下载包（PLC.DOWN）。
    ///
    /// 与 .updown 同一族格式（结构完全一致，仅魔数与个别 tag 语义不同）：
    ///     +0   u32   目录区起始偏移（= 头部长度）
    ///     头部 TLV（从 +4 起，紧排到 dirOff）：
    ///         u32 tag + u16 长度 + 内容
    ///             tag 1000 = 魔数 "PLC.DOWN"
    ///             tag 1001 = 包格式版本   (如 "1.0.0.1")
    ///             tag 1002 = PLC 机型     (如 "H6E")
    ///             tag 1003 = 编译版本     (如 "0.16.0.0")
    ///             tag 1004 = 19 字节二进制块（非计数，.updown 里是条目数）
    ///             其它     = u32 长度 + 二进制块
    ///     目录/数据区：紧密排列的条目
    ///         u16 名字长度 + 名字(UTF-8) + u32 数据长度 + 数据
    ///     文件尾：4 字节校验
    ///
    /// 实测来源：C:\Users\Sheny\Desktop\plccx.down（50501 字节）解析出 19 个条目，
    /// 且条目顺序与串口抓包中「文件声明帧」声明的顺序一致。
    /// </summary>
    public sealed class DownPackage
    {
        private const string MAGIC = "PLC.DOWN";

        private const uint TAG_MAGIC = 1000;
        private const uint TAG_VERSION = 1001;
        private const uint TAG_MODEL = 1002;
        private const uint TAG_BUILD = 1003;

        public string Path { get; private set; }
        public int RawSize { get; private set; }

        /// <summary>
        /// <b>整个 .down 文件的原始字节流</b>。
        /// 串口烧录下发的就是它（抓包逐字节验证：50501 字节原样下发，仅摘要 tag 1005-1007 因重复导出而不同）。
        /// </summary>
        public byte[] Raw { get; private set; }

        public string Magic { get; private set; }
        public string Version { get; private set; }
        /// <summary>包声明的 PLC 机型，用于下载前防呆。</summary>
        public string Model { get; private set; }
        public string Build { get; private set; }
        public byte[] Tail { get; private set; }

        /// <summary>文件名 -> 内容。</summary>
        public Dictionary<string, byte[]> Files { get; private set; }

        /// <summary>包内的原始顺序。</summary>
        public List<string> Order { get; private set; }

        public int TotalBytes
        {
            get
            {
                int n = 0;
                foreach (string k in Order) n += Files[k].Length;
                return n;
            }
        }

        private DownPackage()
        {
            Files = new Dictionary<string, byte[]>();
            Order = new List<string>();
            Magic = "";
            Version = "";
            Model = "";
            Build = "";
            Tail = new byte[0];
        }

        public static DownPackage Load(string path)
        {
            var pkg = new DownPackage();
            pkg.Path = path;

            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch (Exception ex) { throw new DownPackageException("读取文件失败: " + ex.Message); }

            pkg.RawSize = d.Length;
            pkg.Raw = d;
            if (d.Length < 16) throw new DownPackageException("文件过小，不是有效的 .down 包");

            int dirOff = (int)ReadU32(d, 0);
            if (dirOff <= 4 || dirOff > d.Length)
                throw new DownPackageException("目录偏移越界: " + dirOff + " / 文件长 " + d.Length);

            // ---- 头部 TLV ----
            int off = 4;
            while (off + 4 <= dirOff)
            {
                uint tag = ReadU32(d, off);
                off += 4;
                if (off + 2 > dirOff) break;
                int n = ReadU16(d, off);
                off += 2;
                if (off + n > d.Length) throw new DownPackageException("头部字段越界 (tag=" + tag + ")");

                if (tag == TAG_MAGIC || tag == TAG_VERSION || tag == TAG_MODEL || tag == TAG_BUILD)
                {
                    string s = Encoding.ASCII.GetString(d, off, n).TrimEnd('\0');
                    if (tag == TAG_MAGIC) pkg.Magic = s;
                    else if (tag == TAG_VERSION) pkg.Version = s;
                    else if (tag == TAG_MODEL) pkg.Model = s;
                    else pkg.Build = s;
                }
                // 其它 tag（1004 二进制块等）跳过
                off += n;
            }

            if (pkg.Magic != MAGIC)
                throw new DownPackageException(
                    string.Format("魔数不匹配：期望 \"{0}\"，实际 \"{1}\"。\r\n" +
                                  "  .down 是串口/触摸屏下载包；.updown 才是 USB 下载包，请勿混用。",
                                  MAGIC, pkg.Magic));

            ParseEntries(pkg, d, dirOff);
            return pkg;
        }

        /// <summary>
        /// <b>格式无关的原始字节装载</b>：只读取整个文件字节流，<b>不校验魔数</b>。
        ///
        /// 供「串口烧录」使用——串口 0x43 协议下发的就是整包原始字节流，
        /// PLC 端不校验文件头魔数（实测 .down / .updown 都能烧、都能运行）。
        /// 因此串口只需拿到 Raw + 文件名 + 机型，无需按 .down/.updown 区分。
        ///
        /// 若文件恰好是合法 .down，则顺带解析出条目/机型用于日志；
        /// 解析失败（如是 .updown）也不报错，仅 Model 为空。
        /// </summary>
        public static DownPackage LoadRawAny(string path)
        {
            var pkg = new DownPackage();
            pkg.Path = path;

            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch (Exception ex) { throw new DownPackageException("读取文件失败: " + ex.Message); }

            pkg.RawSize = d.Length;
            pkg.Raw = d;
            if (d.Length < 16) throw new DownPackageException("文件过小，不是有效的程序包");

            // 尽力解析头部（不校验魔数、失败不抛异常），仅为日志/防呆提供信息
            try
            {
                int dirOff = (int)ReadU32(d, 0);
                if (dirOff > 4 && dirOff <= d.Length)
                {
                    int off = 4;
                    while (off + 4 <= dirOff)
                    {
                        uint tag = ReadU32(d, off); off += 4;
                        if (off + 2 > dirOff) break;
                        int n = ReadU16(d, off); off += 2;
                        if (off + n > d.Length) break;
                        if (tag == TAG_MAGIC || tag == TAG_VERSION || tag == TAG_MODEL || tag == TAG_BUILD)
                        {
                            string s = Encoding.ASCII.GetString(d, off, n).TrimEnd('\0');
                            if (tag == TAG_MAGIC) pkg.Magic = s;
                            else if (tag == TAG_VERSION) pkg.Version = s;
                            else if (tag == TAG_MODEL) pkg.Model = s;
                            else pkg.Build = s;
                        }
                        off += n;
                    }
                    ParseEntries(pkg, d, dirOff);
                }
            }
            catch { /* 尽力而为：解析失败不影响按原始字节烧录 */ }

            return pkg;
        }

        /// <summary>从 dirOff 起解析条目区，填充 Files / Order / Tail。</summary>
        private static void ParseEntries(DownPackage pkg, byte[] d, int dirOff)
        {
            int off = dirOff;
            while (off < d.Length - 6)
            {
                int n = ReadU16(d, off);
                if (n == 0 || n > 260 || off + 2 + n + 4 > d.Length) break;

                string name = Encoding.UTF8.GetString(d, off + 2, n);
                off += 2 + n;

                int size = (int)ReadU32(d, off);
                off += 4;
                if (size < 0 || off + size > d.Length)
                    throw new DownPackageException("条目 " + name + " 数据越界");

                byte[] body = new byte[size];
                Array.Copy(d, off, body, 0, size);
                off += size;

                if (!pkg.Files.ContainsKey(name))
                {
                    pkg.Files.Add(name, body);
                    pkg.Order.Add(name);
                }
                else
                {
                    pkg.Files[name] = body;
                }
            }

            pkg.Tail = new byte[d.Length - off];
            Array.Copy(d, off, pkg.Tail, 0, pkg.Tail.Length);
        }

        private static ushort ReadU16(byte[] d, int off)
        {
            return (ushort)(d[off] | (d[off + 1] << 8));
        }

        private static uint ReadU32(byte[] d, int off)
        {
            return (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));
        }
    }
}
