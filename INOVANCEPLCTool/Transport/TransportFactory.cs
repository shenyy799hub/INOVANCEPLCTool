using System;
using INOVANCEPLCTool.Usb;

namespace INOVANCEPLCTool.Transport
{
    /// <summary>通道类型。</summary>
    public enum TransportKind
    {
        Usb = 0,
        Serial = 1,
        Tcp = 2,
    }

    /// <summary>通道配置（各类型共用，未用到的字段忽略）。</summary>
    public sealed class TransportOptions
    {
        public TransportKind Kind = TransportKind.Usb;

        // ---- 串口 ----
        public string PortName = "COM1";
        public int BaudRate = 19200;   // 随设备而异（实测有 19200 / 9600 两种），UI 可选
        public int DataBits = 8;
        public System.IO.Ports.Parity Parity = System.IO.Ports.Parity.None;
        public System.IO.Ports.StopBits StopBits = System.IO.Ports.StopBits.One;
        public byte UnitId = 1;

        // ---- TCP ----
        public string Host = "192.168.1.88";
        public int Port = 502;

        /// <summary>收发超时（毫秒）。</summary>
        public int Timeout = 3000;

        /// <summary>按当前配置创建通道实例（不打开）。</summary>
        public ITransport Create()
        {
            ITransport t;
            switch (Kind)
            {
                case TransportKind.Serial:
                    t = new SerialLink(PortName)
                    {
                        BaudRate = BaudRate,
                        DataBits = DataBits,
                        Parity = Parity,
                        StopBits = StopBits,
                        UnitId = UnitId,
                        Timeout = Timeout,
                    };
                    break;

                case TransportKind.Tcp:
                    t = new TcpLink(Host, Port)
                    {
                        UnitId = UnitId,
                        Timeout = Timeout,
                    };
                    break;

                default:
                    t = new UsbLink { Timeout = Timeout };
                    break;
            }
            return t;
        }
    }
}
