using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace INOVANCEPLCTool.Plc
{
    public class UpdownException : Exception
    {
        public UpdownException(string message) : base(message) { }
    }

    /// <summary>
    /// AutoShop 导出的 .updown 下载包。
    ///
    /// 结构（全小端）：
    ///     +0   u32   目录区起始偏移（= 头部长度）
    ///     头部：一串 TLV
    ///         tag u32 + 内容
    ///         1000/1001/1002/1003 : u16 长度 + ASCII 字符串
    ///             1000 = 魔数 "PLC.UPDOWN"
    ///             1001 = 包格式版本
    ///             1002 = PLC 机型（如 H6E / H6ES）
    ///             1003 = 工程/编译器版本
    ///         1004                : u32 值（文件条目数）
    ///         其它                : u32 长度 + 二进制块（1005~1007 为 64 字节摘要，1008 为 20 字节预留）
    ///     目录/数据区：紧密排列的条目
    ///         u16 名字长度 + 名字(UTF-8) + u32 数据长度 + 数据
    ///     文件尾：4 字节校验
    /// </summary>
    public sealed class UpdownPackage
    {
        private const string MAGIC = "PLC.UPDOWN";

        private const uint TAG_MAGIC = 1000;
        private const uint TAG_VERSION = 1001;
        private const uint TAG_MODEL = 1002;
        private const uint TAG_BUILD = 1003;
        private const uint TAG_COUNT = 1004;

        public string Path { get; private set; }
        public int RawSize { get; private set; }

        public string Magic { get; private set; }
        public string Version { get; private set; }
        /// <summary>包声明的 PLC 机型，用于下载前防呆。</summary>
        public string Model { get; private set; }
        public string Build { get; private set; }
        public int DeclaredCount { get; private set; }
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

        private UpdownPackage()
        {
            Files = new Dictionary<string, byte[]>();
            Order = new List<string>();
            Magic = "";
            Version = "";
            Model = "";
            Build = "";
            Tail = new byte[0];
        }

        public static UpdownPackage Load(string path)
        {
            var pkg = new UpdownPackage();
            pkg.Path = path;

            byte[] d;
            try
            {
                d = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                throw new UpdownException("读取文件失败: " + ex.Message);
            }

            pkg.RawSize = d.Length;
            if (d.Length < 8) throw new UpdownException("文件过小，不是有效的 .updown 包");

            int dirOff = (int)ReadU32(d, 0);
            if (dirOff <= 4 || dirOff > d.Length)
                throw new UpdownException("目录偏移越界: " + dirOff + " / 文件长 " + d.Length);

            // ---- 头部 TLV ----
            int off = 4;
            while (off + 4 <= dirOff)
            {
                uint tag = ReadU32(d, off);
                off += 4;

                if (tag == TAG_MAGIC || tag == TAG_VERSION || tag == TAG_MODEL || tag == TAG_BUILD)
                {
                    if (off + 2 > dirOff) break;
                    int n = ReadU16(d, off);
                    off += 2;
                    if (off + n > d.Length) throw new UpdownException("头部字符串越界");
                    string s = Encoding.ASCII.GetString(d, off, n).TrimEnd('\0');
                    off += n;

                    if (tag == TAG_MAGIC) pkg.Magic = s;
                    else if (tag == TAG_VERSION) pkg.Version = s;
                    else if (tag == TAG_MODEL) pkg.Model = s;
                    else pkg.Build = s;
                }
                else if (tag == TAG_COUNT)
                {
                    if (off + 4 > dirOff) break;
                    pkg.DeclaredCount = (int)ReadU32(d, off);
                    off += 4;
                }
                else
                {
                    if (off + 4 > dirOff) break;
                    int n = (int)ReadU32(d, off);
                    off += 4;
                    if (n < 0 || off + n > d.Length) throw new UpdownException("头部数据块越界");
                    off += n;   // 摘要/预留块，下载时用不到
                }
            }

            if (pkg.Magic != MAGIC)
                throw new UpdownException(
                    string.Format("魔数不匹配：期望 \"{0}\"，实际 \"{1}\"。这可能不是 AutoShop 导出的下载文件。",
                                  MAGIC, pkg.Magic));

            // ---- 目录 / 数据区 ----
            off = dirOff;
            while (off < d.Length - 6)
            {
                int n = ReadU16(d, off);
                if (n == 0 || n > 260 || off + 2 + n + 4 > d.Length) break;

                string name = Encoding.UTF8.GetString(d, off + 2, n);
                off += 2 + n;

                int size = (int)ReadU32(d, off);
                off += 4;
                if (size < 0 || off + size > d.Length)
                    throw new UpdownException("条目 " + name + " 数据越界");

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

            if (pkg.Order.Count == 0)
                throw new UpdownException("下载包里没有解析出任何文件条目");

            return pkg;
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
