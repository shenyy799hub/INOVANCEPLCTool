using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;

namespace INOVANCEPLCTool.Usb
{
    /// <summary>
    /// libusb-win32 (libusb0.dll) 的 P/Invoke 绑定。
    ///
    /// 汇川 H5U/H6E/Easy 系列 PLC 的 USB 口由 libusb0 驱动接管
    /// （设备管理器显示为 "H5U USB Device"，VID_0471 / PID_A531），
    /// AutoShop 本身也是通过这个 DLL 与 PLC 通信的。
    /// </summary>
    internal static class LibUsb0
    {
        private const string DLL = "libusb0.dll";
        private const CallingConvention CC = CallingConvention.Cdecl;

        // ------------------------------------------------------------------
        // 结构体内存布局
        //
        // libusb-win32 中 LIBUSB_PATH_MAX = 512：
        //   struct usb_bus    { next, prev, char dirname[512],  devices, location, root_dev }
        //   struct usb_device { next, prev, char filename[512], bus, descriptor, ... }
        //
        // 各字段偏移随指针宽度变化，故按 IntPtr.Size 推算，32/64 位通用。
        // 64 位下的取值已在真机上实测验证：
        //   bus.dirname=16  bus.devices=528
        //   device.filename=16  device.descriptor=536
        //   （filename 实际内容形如 "\\.\libusb0-0001--0x0471-0xa531"）
        // ------------------------------------------------------------------
        private const int PATH_MAX = 512;

        private static int P { get { return IntPtr.Size; } }

        private static int OffBusDirName { get { return 2 * P; } }
        private static int OffBusDevices { get { return 2 * P + PATH_MAX; } }
        private static int OffDevFileName { get { return 2 * P; } }
        private static int OffDevDescriptor { get { return 3 * P + PATH_MAX; } }

        // usb_device_descriptor 内部偏移（18 字节，无填充）
        private const int DESC_LENGTH = 0;
        private const int DESC_TYPE = 1;
        private const int DESC_VID = 8;
        private const int DESC_PID = 10;
        private const int DESC_IPRODUCT = 15;

        // 描述符类型
        public const byte DT_CONFIG = 0x02;

        // ------------------------------------------------------------------
        // 原生函数
        // ------------------------------------------------------------------
        [DllImport(DLL, CallingConvention = CC)]
        public static extern void usb_init();

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_find_busses();

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_find_devices();

        [DllImport(DLL, CallingConvention = CC)]
        public static extern IntPtr usb_get_busses();

        [DllImport(DLL, CallingConvention = CC)]
        public static extern IntPtr usb_open(IntPtr dev);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_close(IntPtr handle);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_set_configuration(IntPtr handle, int configuration);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_claim_interface(IntPtr handle, int iface);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_release_interface(IntPtr handle, int iface);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_bulk_write(IntPtr handle, int ep, byte[] bytes, int size, int timeout);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_bulk_read(IntPtr handle, int ep, byte[] bytes, int size, int timeout);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_clear_halt(IntPtr handle, int ep);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_get_descriptor(IntPtr handle, byte type, byte index, byte[] buf, int size);

        [DllImport(DLL, CallingConvention = CC)]
        public static extern int usb_get_string_simple(IntPtr handle, int index, byte[] buf, int buflen);

        [DllImport(DLL, CallingConvention = CC)]
        private static extern IntPtr usb_strerror();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------
        private static bool _preloaded;

        /// <summary>
        /// 预载 libusb0.dll（非托管原生 DLL，无法纯内存加载，需落地磁盘一次）。
        ///
        /// 该 DLL 已作为"嵌入资源"编译进本 exe，分 32 位 / 64 位两份：
        ///   INOVANCEPLCTool.libusb0.x86.dll  /  INOVANCEPLCTool.libusb0.x64.dll
        /// 运行时按当前进程位数（Environment.Is64BitProcess）选对应那份提取释放，
        /// 因此无论构建目标还是目标机是 32 位还是 64 位，都能加载匹配的 libusb0.dll，
        /// 实现单一 exe 在 32/64 位 Windows 上通用。释放出的文件名统一为 libusb0.dll
        /// （与 DllImport("libusb0.dll") 一致），保证 P/Invoke 命中同一份。
        ///
        /// 提取/释放流程：
        ///   1) 若 exe 目录下已存在 libusb0.dll（含绿色版手动放置），直接加载复用；
        ///   2) 否则从资源写入 exe 目录（大多数分发场景可写）；
        ///   3) 若 exe 目录不可写（如装在 Program Files 且无权限），回退到 %TEMP% 子目录，
        ///      并用 SetDllDirectory 把该目录加入 DLL 搜索顺序，保证 DllImport 仍能命中。
        ///
        /// DllImport("libusb0.dll") 按"完整路径"去重：本函数用与 CLR 解析一致的目录
        /// 提前 LoadLibrary 同一份，后续 P/Invoke 复用之，避免第二份独立 libusb 状态导致
        /// 设备句柄错乱。
        ///
        /// 资源缺失（极端情况）时回退到系统目录，兼容"系统已装 libusb0"的环境。
        /// </summary>
        public static void Preload()
        {
            if (_preloaded) return;
            _preloaded = true;
            // 按进程位数选择嵌入资源：64 位进程用 x64 版，32 位进程用 x86 版。
            // 工程为 AnyCPU + Prefer32Bit=false，故 64 位系统上进程即 64 位、选 x64。
            string resName = Environment.Is64BitProcess
                ? "INOVANCEPLCTool.libusb0.x64.dll"
                : "INOVANCEPLCTool.libusb0.x86.dll";
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string local = Path.Combine(baseDir, "libusb0.dll");
                if (!File.Exists(local))
                {
                    byte[] buf = LoadResource(resName);
                    if (buf != null)
                    {
                        try
                        {
                            File.WriteAllBytes(local, buf);
                        }
                        catch
                        {
                            // exe 目录不可写，回退到 Temp
                            string tmpDir = Path.Combine(
                                Path.GetTempPath(),
                                "INOVANCEPLCTool_" + Assembly.GetExecutingAssembly().GetName().Version);
                            Directory.CreateDirectory(tmpDir);
                            local = Path.Combine(tmpDir, "libusb0.dll");
                            File.WriteAllBytes(local, buf);
                            SetDllDirectory(tmpDir);
                        }
                    }
                }
                if (File.Exists(local)) LoadLibrary(local);
            }
            catch
            {
                try
                {
                    string sys = Path.Combine(Environment.SystemDirectory, "libusb0.dll");
                    if (File.Exists(sys)) LoadLibrary(sys);
                }
                catch { }
            }
        }

        /// <summary>从嵌入资源读取字节；资源不存在返回 null。</summary>
        private static byte[] LoadResource(string name)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    var buf = new byte[s.Length];
                    int off = 0, n;
                    while (off < buf.Length && (n = s.Read(buf, off, buf.Length - off)) > 0) off += n;
                    return buf;
                }
            }
            catch { return null; }
        }

        /// <summary>取 libusb 的最后一条错误描述。</summary>
        public static string LastError()
        {
            try
            {
                IntPtr p = usb_strerror();
                if (p == IntPtr.Zero) return "未知错误";
                string s = Marshal.PtrToStringAnsi(p);
                return string.IsNullOrEmpty(s) ? "未知错误" : s.Trim();
            }
            catch
            {
                return "未知错误";
            }
        }

        /// <summary>枚举所有 libusb0 接管的设备。</summary>
        public static List<UsbDeviceRef> EnumerateDevices()
        {
            Preload();
            usb_init();
            usb_find_busses();
            usb_find_devices();

            var list = new List<UsbDeviceRef>();
            IntPtr bus = usb_get_busses();
            while (bus != IntPtr.Zero)
            {
                IntPtr dev = Marshal.ReadIntPtr(bus, OffBusDevices);
                while (dev != IntPtr.Zero)
                {
                    IntPtr desc = dev + OffDevDescriptor;
                    byte bLength = Marshal.ReadByte(desc, DESC_LENGTH);
                    byte bType = Marshal.ReadByte(desc, DESC_TYPE);

                    // 合法的设备描述符必为 18 字节 / 类型 1，可借此校验偏移是否正确
                    if (bLength == 18 && bType == 1)
                    {
                        list.Add(new UsbDeviceRef
                        {
                            Handle = dev,
                            Vid = (ushort)Marshal.ReadInt16(desc, DESC_VID),
                            Pid = (ushort)Marshal.ReadInt16(desc, DESC_PID),
                            ProductIndex = Marshal.ReadByte(desc, DESC_IPRODUCT),
                            FileName = ReadAnsi(dev + OffDevFileName, PATH_MAX)
                        });
                    }
                    dev = Marshal.ReadIntPtr(dev, 0);   // next
                }
                bus = Marshal.ReadIntPtr(bus, 0);       // next
            }
            return list;
        }

        private static string ReadAnsi(IntPtr p, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < max; i++)
            {
                byte b = Marshal.ReadByte(p, i);
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }

    /// <summary>枚举到的一个 USB 设备（指向 libusb 内部的 struct usb_device）。</summary>
    internal sealed class UsbDeviceRef
    {
        public IntPtr Handle;
        public ushort Vid;
        public ushort Pid;
        public byte ProductIndex;
        public string FileName;
    }
}
