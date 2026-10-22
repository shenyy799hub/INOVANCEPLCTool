using System;
using System.Collections.Generic;
using System.Text;
using INOVANCEPLCTool.Plc;
using INOVANCEPLCTool.Transport;

namespace INOVANCEPLCTool.Usb
{
    /// <summary>PLC USB 通信异常。</summary>
    public class PlcUsbException : Exception
    {
        public PlcUsbException(string message) : base(message) { }
    }

    /// <summary>
    /// 到 PLC 的 USB Bulk 通道。
    ///
    /// 物理层：VID 0x0471 / PID 0xA531，Bulk OUT = 0x02，Bulk IN = 0x81，最大包 512 字节。
    /// libusb0 对设备是独占的 —— AutoShop 正在联机时本通道无法打开。
    ///
    /// 同时实现 <see cref="ITransport"/>：请求/应答帧为汇川私有帧（见 PlcFrame），
    /// 供上层按统一接口调用。
    /// </summary>
    public sealed class UsbLink : ITransport
    {
        public const int VID = 0x0471;
        public const int PID = 0xA531;
        public const int EP_OUT = 0x02;
        public const int EP_IN = 0x81;

        private const int READ_BUF = 4096;

        private IntPtr _handle = IntPtr.Zero;
        private int _iface = -1;
        private uint _seq;

        /// <summary>默认收发超时（毫秒）。</summary>
        public int Timeout { get; set; }

        /// <summary>设备产品字符串，例如 "Easy USB Device v2.4"。</summary>
        public string ProductName { get; private set; }

        /// <summary>libusb 设备路径，例如 "\\.\libusb0-0001--0x0471-0xa531"。</summary>
        public string DevicePath { get; private set; }

        /// <summary>实际占用的接口号。</summary>
        public int InterfaceNumber { get { return _iface; } }

        public UsbLink()
        {
            Timeout = 10000;
            ProductName = "";
            DevicePath = "";
        }

        public bool IsOpen { get { return _handle != IntPtr.Zero; } }

        /// <summary>通道显示名（ITransport）。</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(ProductName)) return "USB " + ProductName;
                return "USB 0x0471/0xA531";
            }
        }

        // ------------------------------------------------------------------
        // 连接管理
        // ------------------------------------------------------------------
        public void Open()
        {
            if (IsOpen) return;

            List<UsbDeviceRef> all = LibUsb0.EnumerateDevices();
            UsbDeviceRef target = null;
            foreach (UsbDeviceRef d in all)
            {
                if (d.Vid == VID && d.Pid == PID) { target = d; break; }
            }

            if (target == null)
            {
                throw new PlcUsbException(
                    string.Format(
                        "未找到 PLC（VID_{0:X4} / PID_{1:X4}）。请检查：\r\n" +
                        "  1. USB 线是否插好、PLC 是否上电\r\n" +
                        "  2. 设备管理器里是否有 \"H5U USB Device\"（需已安装 libusb0 驱动）\r\n" +
                        "  当前 libusb0 可见设备数：{2}", VID, PID, all.Count));
            }

            DevicePath = target.FileName;

            _handle = LibUsb0.usb_open(target.Handle);
            if (_handle == IntPtr.Zero)
                throw new PlcUsbException("打开设备失败：" + LibUsb0.LastError());

            try
            {
                ProductName = GetStringDescriptor(target.ProductIndex);

                // 已配置的设备重复设置会返回错误，忽略即可
                LibUsb0.usb_set_configuration(_handle, 1);

                _iface = ClaimDataInterface();
                Drain();
            }
            catch
            {
                Close();
                throw;
            }
        }

        public void Close()
        {
            if (_handle == IntPtr.Zero) return;
            try
            {
                if (_iface >= 0) LibUsb0.usb_release_interface(_handle, _iface);
            }
            catch { }
            try { LibUsb0.usb_close(_handle); }
            catch { }
            _handle = IntPtr.Zero;
            _iface = -1;
        }

        public void Dispose()
        {
            Close();
        }

        private string GetStringDescriptor(byte index)
        {
            if (index == 0) return "";
            byte[] buf = new byte[256];
            int n = LibUsb0.usb_get_string_simple(_handle, index, buf, buf.Length);
            if (n <= 0) return "";
            return Encoding.ASCII.GetString(buf, 0, n).TrimEnd('\0').Trim();
        }

        /// <summary>
        /// 找到同时具备 Bulk OUT 0x02 与 Bulk IN 0x81 的接口并占用。
        ///
        /// 设备是 CDC 复合设备：接口 0 是通信类（只有中断端点），
        /// 数据端点在接口 1 上。这里不写死接口号，而是读配置描述符定位，
        /// 以兼容端点布局不同的固件。
        /// </summary>
        private int ClaimDataInterface()
        {
            var candidates = new List<int>();

            byte[] head = new byte[9];
            int n = LibUsb0.usb_get_descriptor(_handle, LibUsb0.DT_CONFIG, 0, head, head.Length);
            if (n >= 9)
            {
                int total = head[2] | (head[3] << 8);
                if (total > 9 && total < 4096)
                {
                    byte[] cfg = new byte[total];
                    n = LibUsb0.usb_get_descriptor(_handle, LibUsb0.DT_CONFIG, 0, cfg, total);
                    if (n >= total)
                    {
                        int cur = -1;
                        bool hasOut = false, hasIn = false;
                        int i = 0;
                        while (i + 1 < total)
                        {
                            int len = cfg[i];
                            int type = cfg[i + 1];
                            if (len < 2) break;

                            if (type == 0x04 && i + 2 < total)          // INTERFACE
                            {
                                if (cur >= 0 && hasOut && hasIn) candidates.Add(cur);
                                cur = cfg[i + 2];
                                hasOut = false;
                                hasIn = false;
                            }
                            else if (type == 0x05 && i + 2 < total)     // ENDPOINT
                            {
                                int ep = cfg[i + 2];
                                if (ep == EP_OUT) hasOut = true;
                                if (ep == EP_IN) hasIn = true;
                            }
                            i += len;
                        }
                        if (cur >= 0 && hasOut && hasIn) candidates.Add(cur);
                    }
                }
            }

            // 描述符读取失败时退回经验值：接口 1 优先，其次 0、2、3
            if (candidates.Count == 0)
            {
                candidates.Add(1);
                candidates.Add(0);
                candidates.Add(2);
                candidates.Add(3);
            }

            foreach (int itf in candidates)
            {
                if (LibUsb0.usb_claim_interface(_handle, itf) >= 0) return itf;
            }

            throw new PlcUsbException(
                "占用 USB 接口失败：" + LibUsb0.LastError() + "\r\n" +
                "  最常见原因：AutoShop 正在运行且已连接 PLC —— libusb0 驱动是独占的。\r\n" +
                "  请先在 AutoShop 里断开连接（或直接关闭 AutoShop）再试。");
        }

        /// <summary>清空 IN 端点里的残留数据，避免上一次会话的尾巴串到本次应答里。</summary>
        private void Drain()
        {
            byte[] buf = new byte[READ_BUF];
            for (int i = 0; i < 8; i++)
            {
                int n = LibUsb0.usb_bulk_read(_handle, EP_IN, buf, buf.Length, 50);
                if (n <= 0) break;
            }
        }

        // ------------------------------------------------------------------
        // 收发
        // ------------------------------------------------------------------
        public uint NextSeq()
        {
            return _seq++;
        }

        /// <summary>发送一帧并等待应答。</summary>
        public PlcFrame Request(byte cmd, byte sub, byte[] payload, int timeoutMs = 0, bool checkStatus = true)
        {
            if (!IsOpen) throw new PlcUsbException("USB 通道未打开");

            int to = timeoutMs > 0 ? timeoutMs : Timeout;
            uint seq = NextSeq();
            byte[] frame = PlcFrame.Build(cmd, seq, sub, payload);

            int written = LibUsb0.usb_bulk_write(_handle, EP_OUT, frame, frame.Length, to);
            if (written < 0)
                throw new PlcUsbException(string.Format(
                    "发送失败 (cmd=0x{0:X2} sub=0x{1:X2}): {2}", cmd, sub, LibUsb0.LastError()));
            if (written != frame.Length)
                throw new PlcUsbException(string.Format(
                    "发送不完整: {0}/{1} 字节", written, frame.Length));

            byte[] buf = new byte[READ_BUF];
            int n = LibUsb0.usb_bulk_read(_handle, EP_IN, buf, buf.Length, to);
            if (n < 0)
                throw new PlcUsbException(string.Format(
                    "接收失败 (cmd=0x{0:X2} sub=0x{1:X2}): {2}", cmd, sub, LibUsb0.LastError()));

            byte[] raw = new byte[n];
            Array.Copy(buf, raw, n);

            PlcFrame rsp = PlcFrame.Parse(raw);
            if (rsp.Seq != seq)
                throw new PlcUsbException(string.Format(
                    "应答序列号不匹配：期望 {0}，收到 {1}", seq, rsp.Seq));
            if (checkStatus && !rsp.IsOk)
                throw new PlcUsbException(string.Format(
                    "PLC 返回错误状态 0x{0:X2} (cmd=0x{1:X2} sub=0x{2:X2})", rsp.Status, cmd, sub));

            return rsp;
        }

        /// <summary>
        /// ITransport 统一入口：发送一个已完成协议组装的完整帧，读回一帧。
        /// 对 USB 而言，入参就是 <see cref="PlcFrame.Build"/> 产出的字节。
        /// 与 <see cref="Request(byte,byte,byte[],int,bool)"/> 的区别是不做状态码校验
        /// （上层若需校验状态，自行解析返回帧）。
        /// </summary>
        public byte[] Request(byte[] send, int timeoutMs = 0)
        {
            if (!IsOpen) throw new PlcUsbException("USB 通道未打开");
            if (send == null || send.Length == 0) throw new PlcUsbException("发送内容为空");

            int to = timeoutMs > 0 ? timeoutMs : Timeout;

            int written = LibUsb0.usb_bulk_write(_handle, EP_OUT, send, send.Length, to);
            if (written < 0)
                throw new PlcUsbException("发送失败: " + LibUsb0.LastError());
            if (written != send.Length)
                throw new PlcUsbException(string.Format("发送不完整: {0}/{1} 字节", written, send.Length));

            byte[] buf = new byte[READ_BUF];
            int n = LibUsb0.usb_bulk_read(_handle, EP_IN, buf, buf.Length, to);
            if (n < 0)
                throw new PlcUsbException("接收失败: " + LibUsb0.LastError());

            byte[] raw = new byte[n];
            Array.Copy(buf, raw, n);
            return raw;
        }
    }
}
