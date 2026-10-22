using System;
using System.Text;

namespace INOVANCEPLCTool.Transport
{
    /// <summary>传输层异常（打开失败 / 收发失败 / 超时）。</summary>
    public class TransportException : Exception
    {
        public TransportException(string message) : base(message) { }
        public TransportException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// 到 PLC 的<b>传输通道</b>抽象。
    ///
    /// 设计目的：把「业务协议」与「物理/链路载体」彻底解耦，使同一套上层逻辑
    /// （登录、软元件读写、运行状态、文件分块下发、完整下载流程）可运行在 USB /
    /// 串口(RS485) / 以太网(TCP) 任一通道上。
    ///
    /// 实现类：
    ///   - <c>UsbLink</c>     —— USB Bulk（libusb0），帧 = 汇川私有帧（见 PlcFrame）
    ///   - <c>SerialLink</c>  —— RS485/RS232 串口，帧 = Modbus RTU（地址 + PDU + CRC16）
    ///   - <c>TcpLink</c>     —— 以太网，帧 = Modbus TCP（MBAP 头 + PDU）
    ///
    /// 关于「协议可能一致」：汇川 PLC 的网口与 485 口大概率跑同一套 Modbus 应用层，
    /// 仅封装不同（TCP 用 MBAP、RTU 用 CRC16）。故本接口只约定「收发一帧」，
    /// 具体编解码交由各自实现 / Frame 层，业务层无需感知差异。
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>通道类型名，用于日志，如 "USB" / "串口 COM3" / "TCP 192.168.1.88:502"。</summary>
        string DisplayName { get; }

        /// <summary>通道是否已打开。</summary>
        bool IsOpen { get; }

        /// <summary>收发超时（毫秒）。</summary>
        int Timeout { get; set; }

        /// <summary>打开通道。失败抛 <see cref="TransportException"/>。</summary>
        void Open();

        /// <summary>关闭通道（可重复调用）。</summary>
        void Close();

        /// <summary>
        /// 发送一帧并读取一帧应答（一问一答模式）。
        /// </summary>
        /// <param name="send">待发送的完整帧字节（已按各自协议封装好）。</param>
        /// <param name="timeoutMs">本次超时；&lt;=0 时使用 <see cref="Timeout"/>。</param>
        /// <returns>应答的完整帧字节。</returns>
        byte[] Request(byte[] send, int timeoutMs = 0);
    }
}
