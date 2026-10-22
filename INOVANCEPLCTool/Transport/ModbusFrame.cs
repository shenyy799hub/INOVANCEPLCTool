using System;

namespace INOVANCEPLCTool.Transport
{
    /// <summary>
    /// Modbus 帧编解码工具。
    ///
    /// 汇川 PLC 的串口(RS485)与网口大概率跑同一套 Modbus 应用层，差别只在封装：
    ///
    ///   Modbus RTU（串口）: [ 从站地址(1) ][ 功能码(1) ][ 数据(n) ][ CRC16(2, 低字节在前) ]
    ///   Modbus TCP（网口）: [ 事务ID(2) ][ 协议ID(2)=0 ][ 长度(2) ][ 单元ID(1) ][ 功能码(1) ][ 数据(n) ]
    ///                       其中「长度」= 单元ID + 之后的全部字节数；无 CRC。
    ///
    /// 两者 PDU（功能码 + 数据）完全一致，因此业务层可以共用同一套 PDU 构造/解析，
    /// 仅通过 <see cref="WrapRtu"/> / <see cref="WrapTcp"/> 切换外壳。
    ///
    /// ⚠ 说明：本文件目前按<b>标准 Modbus</b>实现。等抓到触摸屏实际下载报文后，
    ///   若发现是汇川私有协议（非 Modbus），只需改这里，不动业务层与传输层。
    /// </summary>
    public static class ModbusFrame
    {
        // ---- 常用功能码 ----
        public const byte FC_READ_HOLDING = 0x03;    // 读保持寄存器
        public const byte FC_READ_INPUT = 0x04;      // 读输入寄存器
        public const byte FC_WRITE_SINGLE = 0x06;    // 写单个寄存器
        public const byte FC_WRITE_MULTIPLE = 0x10;  // 写多个寄存器

        public const byte DEFAULT_UNIT_RTU = 0x01;   // RTU 从站地址（默认 1，可改）
        public const byte DEFAULT_UNIT_TCP = 0x01;   // TCP 单元 ID

        // ==================================================================
        // CRC16（Modbus，多项式 0xA001，初值 0xFFFF，低字节在前）
        // ==================================================================
        public static ushort Crc16(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x0001) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                    else crc = (ushort)(crc >> 1);
                }
            }
            return crc;
        }

        public static ushort Crc16(byte[] data)
        {
            return Crc16(data, 0, data == null ? 0 : data.Length);
        }

        // ==================================================================
        // RTU 封装 / 解析
        // ==================================================================
        /// <summary>把 PDU（功能码+数据）封装成 RTU 帧（追加从站地址与 CRC16）。</summary>
        public static byte[] WrapRtu(byte unitId, byte[] pdu)
        {
            if (pdu == null) pdu = new byte[0];
            byte[] f = new byte[1 + pdu.Length + 2];
            f[0] = unitId;
            Array.Copy(pdu, 0, f, 1, pdu.Length);
            ushort crc = Crc16(f, 0, 1 + pdu.Length);
            f[1 + pdu.Length] = (byte)(crc & 0xFF);          // 低字节在前
            f[1 + pdu.Length + 1] = (byte)((crc >> 8) & 0xFF);
            return f;
        }

        /// <summary>从 RTU 帧中取出 PDU（去掉地址与 CRC），并校验 CRC。校验失败返回 null。</summary>
        public static byte[] UnwrapRtu(byte[] frame)
        {
            if (frame == null || frame.Length < 4) return null;
            int n = frame.Length;
            ushort got = (ushort)(frame[n - 2] | (frame[n - 1] << 8));
            ushort calc = Crc16(frame, 0, n - 2);
            if (got != calc) return null;
            byte[] pdu = new byte[n - 3];
            Array.Copy(frame, 1, pdu, 0, n - 3);
            return pdu;
        }

        // ==================================================================
        // TCP 封装 / 解析（MBAP）
        // ==================================================================
        /// <summary>把 PDU 封装成 Modbus TCP 帧（MBAP 头 + PDU）。</summary>
        public static byte[] WrapTcp(ushort transactionId, byte unitId, byte[] pdu)
        {
            if (pdu == null) pdu = new byte[0];
            int len = 1 + pdu.Length;                       // 单元ID + PDU
            byte[] f = new byte[6 + len];
            f[0] = (byte)(transactionId >> 8);
            f[1] = (byte)(transactionId & 0xFF);
            f[2] = 0; f[3] = 0;                             // 协议 ID = 0
            f[4] = (byte)(len >> 8);
            f[5] = (byte)(len & 0xFF);
            f[6] = unitId;
            Array.Copy(pdu, 0, f, 7, pdu.Length);
            return f;
        }

        /// <summary>从 Modbus TCP 帧取出 PDU（跳过 7 字节 MBAP 头）。太短返回 null。</summary>
        public static byte[] UnwrapTcp(byte[] frame)
        {
            if (frame == null || frame.Length < 8) return null;
            int len = (frame[4] << 8) | frame[5];
            int pduLen = len - 1;
            if (pduLen <= 0 || 7 + pduLen > frame.Length) return null;
            byte[] pdu = new byte[pduLen];
            Array.Copy(frame, 7, pdu, 0, pduLen);
            return pdu;
        }

        // ==================================================================
        // PDU 构造（业务层共用）
        // ==================================================================
        /// <summary>读保持寄存器 PDU：FC3 + 起始地址(u16) + 数量(u16)。</summary>
        public static byte[] PduReadHolding(ushort startAddr, ushort count)
        {
            return new byte[]
            {
                FC_READ_HOLDING,
                (byte)(startAddr >> 8), (byte)(startAddr & 0xFF),
                (byte)(count >> 8),     (byte)(count & 0xFF),
            };
        }

        /// <summary>写多个寄存器 PDU：FC16 + 起始地址(u16) + 数量(u16) + 字节数(1) + 数据。</summary>
        public static byte[] PduWriteMultiple(ushort startAddr, byte[] regBytes)
        {
            if (regBytes == null) regBytes = new byte[0];
            int regCount = regBytes.Length / 2;
            byte[] p = new byte[6 + regBytes.Length];
            p[0] = FC_WRITE_MULTIPLE;
            p[1] = (byte)(startAddr >> 8); p[2] = (byte)(startAddr & 0xFF);
            p[3] = (byte)(regCount >> 8);  p[4] = (byte)(regCount & 0xFF);
            p[5] = (byte)(regBytes.Length & 0xFF);
            Array.Copy(regBytes, 0, p, 6, regBytes.Length);
            return p;
        }

        /// <summary>写单个寄存器 PDU：FC6 + 地址(u16) + 值(u16)。</summary>
        public static byte[] PduWriteSingle(ushort addr, ushort value)
        {
            return new byte[]
            {
                FC_WRITE_SINGLE,
                (byte)(addr >> 8), (byte)(addr & 0xFF),
                (byte)(value >> 8), (byte)(value & 0xFF),
            };
        }

        /// <summary>PDU 是否为异常应答（功能码最高位置 1）。</summary>
        public static bool IsException(byte[] pdu)
        {
            return pdu != null && pdu.Length >= 2 && (pdu[0] & 0x80) != 0;
        }

        /// <summary>提取异常码（非异常返回 -1）。</summary>
        public static int ExceptionCode(byte[] pdu)
        {
            if (!IsException(pdu)) return -1;
            return pdu.Length >= 2 ? pdu[1] : -1;
        }

        public static string ToHex(byte[] b, int max)
        {
            if (b == null) return "";
            var sb = new System.Text.StringBuilder();
            int n = Math.Min(max, b.Length);
            for (int i = 0; i < n; i++) sb.AppendFormat("{0:X2} ", b[i]);
            if (b.Length > n) sb.Append("..");
            return sb.ToString().TrimEnd();
        }
    }
}
