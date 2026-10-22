using System;
using System.Collections.Generic;
using System.Text;
using INOVANCEPLCTool.Usb;
using INOVANCEPLCTool.Transport;

namespace INOVANCEPLCTool.Plc
{
    /// <summary>协议层（Plc 封装）抛出的异常。</summary>
    public class PlcClientException : Exception
    {
        public PlcClientException(string message) : base(message) { }
    }

    /// <summary>
    /// 协议层封装 —— 把底层传输通道（<see cref="ITransport"/>）的「发帧/等应答」
    /// 封装成业务动作：登录、读设备信息、软元件读写、运行状态控制、文件下发、完整下载流程。
    ///
    /// 协议逻辑完全对应 autoshop_re/plc/protocol.py 的 Plc 类，并在 Easy320(H6E) /
    /// Easy301(H6ES) 上实机验证过。默认使用 USB 通道；上层可注入串口/TCP 通道复用同一套流程。
    /// </summary>
    public sealed class PlcClient : IDisposable
    {
        private readonly UsbLink _usb;
        private readonly ITransport _link;
        private readonly bool _ownsTransport;

        /// <summary>默认构造：使用 USB 通道（保持既有行为不变）。</summary>
        public PlcClient()
        {
            _usb = new UsbLink();
            _link = _usb;
            _ownsTransport = true;
        }

        /// <summary>注入任意传输通道（串口 / TCP / 自定义）。通道生命周期由调用方管理，
        /// Dispose 本对象<b>不会</b>关闭注入的通道。</summary>
        public PlcClient(ITransport transport)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _link = transport;
            _usb = transport as UsbLink;
            _ownsTransport = false;
        }

        /// <summary>当前使用的传输通道。</summary>
        public ITransport Transport { get { return _link; } }

        // ------------------------------------------------------------------
        // 连接管理
        // ------------------------------------------------------------------
        public void Open() { _link.Open(); }
        public void Close() { _link.Close(); }
        public void Dispose()
        {
            // 仅关闭自身创建的通道；外部注入的通道由调用方负责关闭。
            if (_ownsTransport && _link != null) _link.Dispose();
        }

        /// <summary>当前通道的收发超时（毫秒）。</summary>
        public int Timeout
        {
            get { return _link.Timeout; }
            set { _link.Timeout = value; }
        }

        // ------------------------------------------------------------------
        // 会话
        // ------------------------------------------------------------------
        /// <summary>
        /// 登录。AutoShop 对无密码工程发送 "user0" + 0xA7 填充至 64 字节；
        /// 0xA7 是固定填充字节，PLC 端只认长度与末位。
        /// </summary>
        public void Login(string user = "user0")
        {
            byte[] buf = new byte[64];
            byte[] u = Encoding.ASCII.GetBytes(user);
            int n = Math.Min(u.Length, 64);
            Array.Copy(u, 0, buf, 0, n);
            for (int i = n; i < 64; i++) buf[i] = 0xA7;
            Exchange(Proto.CMD_SESSION, Proto.SUB_LOGIN, buf);
        }

        // ------------------------------------------------------------------
        // 统一收发分派
        // ------------------------------------------------------------------
        /// <summary>
        /// 按「命令 + 子命令 + 负载」发起一次请求并返回应答帧。
        ///
        /// - USB 通道：按汇川私有帧封装（PlcFrame），对应逆向出的真实协议；
        /// - 串口通道：按 Modbus RTU 封装（地址 + PDU + CRC16）；
        /// - TCP 通道 ：按 Modbus TCP 封装（MBAP + PDU）。
        ///
        /// ⚠ 串口/TCP 的具体「cmd/sub ↔ Modbus 功能码/寄存器」映射，
        ///   待抓到触摸屏实际下载报文后在此补齐。当前 USB 路径行为与既有实现完全一致。
        /// </summary>
        private PlcFrame Exchange(byte cmd, byte sub, byte[] payload, int timeoutMs = 0)
        {
            if (_usb != null)
            {
                // 原 USB 私有帧路径（保持既有行为）
                return _usb.Request(cmd, sub, payload, timeoutMs);
            }

            // ---- 非 USB 通道：按 Modbus 语义封装（预留，待协议确认后细化）----
            throw new PlcClientException(
                "当前通道（" + _link.DisplayName + "）的协议映射尚未实现。\r\n" +
                "  串口/TCP 的下载协议需先抓到真实报文后补齐，详见项目说明。");
        }

        /// <summary>会话控制：心跳。</summary>
        public void KeepAlive(uint val = 1)
        {
            Exchange(Proto.CMD_SESSCTL, Proto.SUB_KEEPALIVE, PackU32(val));
        }

        /// <summary>下载收尾提交。</summary>
        public void Commit()
        {
            Exchange(Proto.CMD_SESSCTL, Proto.SUB_COMMIT, new byte[0]);
        }

        // ------------------------------------------------------------------
        // 设备信息
        // ------------------------------------------------------------------
        public PlcDeviceInfo DeviceInfo()
        {
            var rsp = Exchange(Proto.CMD_DEVINFO, Proto.SUB_INFO_READ, new byte[0]);
            return new PlcDeviceInfo(rsp.Payload);
        }

        // ------------------------------------------------------------------
        // 软元件读写
        // ------------------------------------------------------------------
        /// <summary>
        /// 读软元件。
        /// 报文体：u16 addr + u8 area + u8 dtype + u32 count。
        /// 注意参数顺序与 Python read_device(addr, area, count) 一致。
        /// </summary>
        public byte[] ReadDevice(ushort addr, byte area, int count, byte dtype = 0xFF)
        {
            byte[] body = new byte[8];
            body[0] = (byte)(addr & 0xFF);
            body[1] = (byte)((addr >> 8) & 0xFF);
            body[2] = area;
            body[3] = dtype;
            body[4] = (byte)(count & 0xFF);
            body[5] = (byte)((count >> 8) & 0xFF);
            body[6] = (byte)((count >> 16) & 0xFF);
            body[7] = (byte)((count >> 24) & 0xFF);
            var rsp = Exchange(Proto.CMD_DEVICE, Proto.SUB_DEV_READ, body);
            return rsp.Payload;
        }

        /// <summary>写软元件。报文体：u16 addr + u8 area + u8 dtype + u32 len + data。</summary>
        public void WriteDevice(ushort addr, byte area, byte[] data, byte dtype = 0xFF)
        {
            byte[] body = new byte[8 + data.Length];
            body[0] = (byte)(addr & 0xFF);
            body[1] = (byte)((addr >> 8) & 0xFF);
            body[2] = area;
            body[3] = dtype;
            int len = data.Length;
            body[4] = (byte)(len & 0xFF);
            body[5] = (byte)((len >> 8) & 0xFF);
            body[6] = (byte)((len >> 16) & 0xFF);
            body[7] = (byte)((len >> 24) & 0xFF);
            Array.Copy(data, 0, body, 8, data.Length);
            Exchange(Proto.CMD_DEVICE, Proto.SUB_DEV_WRITE, body);
        }

        // ------------------------------------------------------------------
        // 运行状态
        // ------------------------------------------------------------------
        /// <summary>读运行状态：area=0x00 addr=0x0000 的 1 字节。</summary>
        public int GetRunState()
        {
            byte[] d = ReadDevice(0x0000, 0x00, 1);
            if (d == null || d.Length == 0) return -1;
            return d[0];
        }

        /// <summary>写运行状态：area=0x00 addr=0x0000。</summary>
        public void SetRunState(int state)
        {
            WriteDevice(0x0000, 0x00, new byte[] { (byte)(state & 0xFF) });
        }

        public void Stop() { SetRunState(Proto.RUN_STOP); }
        public void Run() { SetRunState(Proto.RUN_RUN); }

        /// <summary>轮询直到达到目标状态，超时返回 false。</summary>
        public bool WaitRunState(int state, double timeoutSec = 10, int intervalMs = 50)
        {
            DateTime t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < timeoutSec)
            {
                if (GetRunState() == state) return true;
                System.Threading.Thread.Sleep(intervalMs);
            }
            return false;
        }

        /// <summary>
        /// 进入下载模式前的资源查询（area=0x10 addr=0x0020，返回 4 字节）。
        /// 为与 AutoShop 原始时序一致而保留，失败不影响后续流程。
        /// </summary>
        public uint QueryDownloadResource()
        {
            byte[] d = ReadDevice(0x0020, 0x10, 4);
            if (d == null || d.Length < 4) return 0;
            return (uint)(d[0] | (d[1] << 8) | (d[2] << 16) | (d[3] << 24));
        }

        // ------------------------------------------------------------------
        // 文件下发
        // ------------------------------------------------------------------
        /// <summary>
        /// 单块字节数。AutoShop 真实抓包(usb_20260731_204645.jsonl)确认：
        /// 文件按 0xA000=40960 字节分块，最后一块为余数。
        /// 单块帧长 = 头(10) + 数据帧体(8) + 40960 = 40978 &lt; 0xFFFF，恒可装入单帧。
        /// </summary>
        private const int FILE_CHUNK = 0xA000; // 40960

        /// <summary>
        /// 下发一个文件：
        ///   1) 声明帧：12 字节属性 + u32 大小 + 文件名(NUL 结尾)
        ///   2) 数据帧：u32 偏移(=块序号 0,1,2…) + u32 长度(=本块字节数) + 内容（按 40960 分块下发）
        ///
        /// 分块格式源自 AutoShop 实机抓包，非猜测：
        ///   - 偏移字段是【块序号】，不是字节偏移；
        ///   - 长度字段是【本块字节数】，不是文件总大小；
        ///   - 块大小固定 40960，末块取余数；
        ///   - 物证校验：stprog.o(149500)=40960×3+26620、project.hcpp(66049)=40960+25089，
        ///     各数据帧 length 求和精确等于声明大小。
        ///   文件 ≤ 40960 时退化为单帧（offset=0、length=文件大小），与抓包中单文件行为一致。
        /// </summary>
        /// <param name="chunkProgress">可选：回调已发送字节数（本文件内），用于细粒度进度。</param>
        public void SendFile(string name, byte[] data, Action<int> chunkProgress = null)
        {
            // 12 字节属性：前 8 字节 PLC 不校验，末 4 字节恒为 0x0000A000
            byte[] attr = new byte[12];
            attr[8] = (byte)(Proto.FILE_ATTR_TAIL & 0xFF);
            attr[9] = (byte)((Proto.FILE_ATTR_TAIL >> 8) & 0xFF);
            attr[10] = (byte)((Proto.FILE_ATTR_TAIL >> 16) & 0xFF);
            attr[11] = (byte)((Proto.FILE_ATTR_TAIL >> 24) & 0xFF);

            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] head = new byte[attr.Length + 4 + nameBytes.Length + 1];
            Array.Copy(attr, 0, head, 0, attr.Length);
            int off = attr.Length;
            head[off++] = (byte)(data.Length & 0xFF);
            head[off++] = (byte)((data.Length >> 8) & 0xFF);
            head[off++] = (byte)((data.Length >> 16) & 0xFF);
            head[off++] = (byte)((data.Length >> 24) & 0xFF);
            Array.Copy(nameBytes, 0, head, off, nameBytes.Length);
            // 末尾 NUL 已因 head 初始化为 0 而天然存在

            Exchange(Proto.CMD_FILE, Proto.SUB_FILE_BEGIN, head);

            // 数据帧：u32 偏移(块序号) + u32 长度(本块字节数) + 内容
            int sent = 0, idx = 0;
            do
            {
                int n = data.Length - sent;
                if (n > FILE_CHUNK) n = FILE_CHUNK;   // 末块取余数，单块恒 ≤ 40960

                byte[] body = new byte[8 + n];
                // 偏移 = 块序号（小端）
                body[0] = (byte)(idx & 0xFF);
                body[1] = (byte)((idx >> 8) & 0xFF);
                body[2] = (byte)((idx >> 16) & 0xFF);
                body[3] = (byte)((idx >> 24) & 0xFF);
                // 长度 = 本块字节数（小端）
                body[4] = (byte)(n & 0xFF);
                body[5] = (byte)((n >> 8) & 0xFF);
                body[6] = (byte)((n >> 16) & 0xFF);
                body[7] = (byte)((n >> 24) & 0xFF);
                Array.Copy(data, sent, body, 8, n);

                // 单块已变小，按块体积给适度超时兜底
                int to = Math.Max(_link.Timeout, 3000 + n / 20);
                Exchange(Proto.CMD_FILE, Proto.SUB_FILE_DATA, body, to);

                sent += n; idx++;
                if (chunkProgress != null) chunkProgress(sent);
            } while (sent < data.Length);
        }

        // ------------------------------------------------------------------
        // 完整下载流程
        // ------------------------------------------------------------------
        /// <summary>
        /// 把 .updown 包下载进 PLC。
        ///
        /// 时序（与 AutoShop 抓取一致）：
        ///   停 -> 资源查询 -> 进下载模式 -> 逐文件下发 -> 写完成 -> 提交 -> 恢复下载前状态
        ///
        /// <paramref name="log"/> 用于逐条状态提示（界面写入 TextBox）；
        /// <paramref name="progress"/> 反馈 0~100 的整体进度。
        /// </summary>
        /// <param name="force">机型不匹配时仍继续（风险自负）。</param>
        /// <param name="resumeRunState">
        /// 下载完成后要恢复到的运行状态：
        /// <c>-1</c> = <b>不自动恢复</b>（保持停机，由界面层另行询问/操作，本工具默认走这条）；
        /// 其它值 = 恢复到该状态（0=停止 / 1=运行）。
        /// </param>
        /// <remarks>下载完成后是否恢复运行由调用方决定；传 -1 时保持停止，不会擅自启动 PLC。</remarks>
        public void Download(UpdownPackage pkg, bool force,
                             Action<string> log, Action<int> progress,
                             int resumeRunState = -1)
        {
            if (pkg == null) throw new PlcClientException("下载包为空");
            DownloadCore(pkg.Model, pkg.Order, pkg.Files, force, log, progress, resumeRunState);
        }

        /// <summary>
        /// 把 <b>.down</b> 包下载进 PLC（USB / TCP 通道）。
        ///
        /// .down 与 .updown 是同一族格式（同为「TLV 头 + 条目区」），条目名完全一致
        /// （project/project.hcpp、stprog.o、symbol.sym …），因此 USB 下发方式完全相同：
        /// 解析出条目后逐文件 <see cref="SendFile"/>，时序与 .updown 一致。
        ///
        /// 注意：走串口时应改用 <see cref="SerialBurnClient"/>（0x43 协议、整包原样下发）；
        /// 本方法只在 USB/TCP 通道上被调用。
        /// </summary>
        /// <param name="resumeRunState">
        /// 下载完成后要恢复到的运行状态：<c>-1</c> = 不自动恢复（保持停机）；其它值 = 恢复到该状态。
        /// </param>
        public void Download(DownPackage pkg, bool force,
                             Action<string> log, Action<int> progress,
                             int resumeRunState = -1)
        {
            if (pkg == null) throw new PlcClientException("下载包为空");
            DownloadCore(pkg.Model, pkg.Order, pkg.Files, force, log, progress, resumeRunState);
        }

        /// <summary>
        /// 统一下载流程（.updown / .down 共用）。三者参数已把"包"抽象成
        /// 「机型 + 有序条目名 + 文件名到内容」。
        ///
        /// 时序（与 AutoShop 抓取一致）：
        ///   停 -> 资源查询 -> 进下载模式 -> 逐文件下发 -> 写完成 -> 提交 -> 恢复下载前状态
        /// </summary>
        private void DownloadCore(string pkgModel, List<string> order,
                                  Dictionary<string, byte[]> files, bool force,
                                  Action<string> log, Action<int> progress,
                                  int resumeRunState = -1)
        {
            if (log == null) log = delegate { };
            if (_usb == null)
                throw new PlcClientException(
                    "当前通道（" + _link.DisplayName + "）的下载协议尚未实现。\r\n" +
                    "  串口/TCP 程序下载需先抓到触摸屏的真实报文、确认协议后再启用。");
            if (order == null || order.Count == 0)
                throw new PlcClientException("下载包里没有可下发的文件条目");

            log("连接并登录 PLC ...");
            Login();

            PlcDeviceInfo info = DeviceInfo();
            int origState = GetRunState();
            log(string.Format("目标 PLC : 机型={0}  下载前状态={1}",
                              string.IsNullOrEmpty(info.Model) ? "?" : info.Model,
                              Proto.RunStateName(origState)));

            // 机型防呆
            if (!string.IsNullOrEmpty(pkgModel) && !string.IsNullOrEmpty(info.Model)
                && pkgModel.Trim().ToUpper() != info.Model.Trim().ToUpper())
            {
                if (!force)
                    throw new PlcClientException(
                        string.Format("机型不匹配：下载包为 {0}，PLC 为 {1}。\r\n" +
                                      "如确认无误，请在界面勾选\"机型不匹配也继续\"后重试。",
                                      pkgModel, info.Model));
                log(string.Format("[警告] 机型不匹配（包={0} / PLC={1}），已强制继续。", pkgModel, info.Model));
            }
            else if (!string.IsNullOrEmpty(pkgModel) && !string.IsNullOrEmpty(info.Model))
            {
                log("机型校验通过：" + pkgModel);
            }

            List<string> names = SortFiles(order);
            int total = 0;
            foreach (string n in names) total += files[n].Length;
            log(string.Format("待下发文件 {0} 个，合计 {1} 字节", names.Count, total));

            // [1/5] 停机
            log("[1/5] 停止 PLC ...");
            SetRunState(Proto.RUN_STOP);
            if (!WaitRunState(Proto.RUN_STOP, 10, 50))
                throw new PlcClientException("PLC 未能进入停止状态，已中止下载。");
            log("      已停止");

            // [2/5] 资源查询 + 进入下载模式
            log("[2/5] 进入下载模式 ...");
            try
            {
                uint res = QueryDownloadResource();
                log(string.Format("      资源查询: 0x{0:X8}", res));
            }
            catch (Exception ex)
            {
                log("      资源查询跳过 (" + ex.Message + ")");
            }
            SetRunState(Proto.RUN_DOWNLOAD);
            log("      OK");

            // [3/5] 逐文件下发
            log(string.Format("[3/5] 传输 {0} 个文件 ...", names.Count));
            int sent = 0;
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                byte[] data = files[name];
                int fileBase = sent;   // 本文件之前已发送的字节数，用于块级进度合成全局百分比
                SendFile(name, data, bs =>
                {
                    if (progress != null)
                        progress((fileBase + bs) * 100 / Math.Max(total, 1));
                });
                sent += data.Length;
                int pct = sent * 100 / Math.Max(total, 1);
                if (progress != null) progress(pct);
                log(string.Format("      [{0}/{1}] {2}  ({3} 字节, 累计 {4}%)",
                                  i + 1, names.Count, name, data.Length, pct));
            }

            // [4/5] 结束传输并提交
            log("[4/5] 提交 ...");
            SetRunState(Proto.RUN_DOWNDONE);
            Commit();
            log("      OK");

            // [5/5] 恢复运行状态
            //   resumeRunState >= 0 : 由调用方指定恢复到该状态
            //   resumeRunState < 0  : 不自动恢复（保持停止），交界面层询问用户
            if (resumeRunState < 0)
            {
                log("[5/5] 未自动恢复运行，PLC 保持停止态。");
            }
            else
            {
                log(string.Format("[5/5] 恢复运行状态：{0} ({1}) ...",
                    resumeRunState, Proto.RunStateName(resumeRunState)));
                System.Threading.Thread.Sleep(500);
                SetRunState(resumeRunState);
                bool ok = WaitRunState(resumeRunState, 10, 50);
                int st = GetRunState();
                log(string.Format("      当前状态: {0} ({1}) {2}",
                    st, Proto.RunStateName(st), ok ? "OK" : "-- 未确认"));
            }

            log(string.Format("下载完成，共 {0} 字节。", sent));
        }

        // ------------------------------------------------------------------
        // 文件顺序（AutoShop 实际抓取顺序）
        // ------------------------------------------------------------------
        private static readonly string[] CAPTURE_ORDER = new string[]
        {
            "project/project.hcpp",
            "userprog.foid",
            "stprog.o",
            "stuserprog.o",
            "symbol.sym",
            "varsinfo.foid",
            "syscfg.foid",
            "ethercat.foid",
            "modbus.foid",
            "canlink.foid",
            "modcfg.foid",
            "ethip.foid",
            "EIPIns.foid",
            "ecttrans.foid",
            "OpcUAServer.foid",
            "sftpserver.foid",
            "Ecam.foid",
        };

        private static List<string> SortFiles(List<string> names)
        {
            var idx = new Dictionary<string, int>();
            for (int i = 0; i < CAPTURE_ORDER.Length; i++) idx[CAPTURE_ORDER[i]] = i;

            var known = new List<string>();
            var rest = new List<string>();
            foreach (string n in names)
            {
                if (idx.ContainsKey(n)) known.Add(n);
                else rest.Add(n);
            }
            known.Sort((a, b) => idx[a].CompareTo(idx[b]));
            var result = new List<string>(known);
            result.AddRange(rest);
            return result;
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------
        private static byte[] PackU32(uint v)
        {
            return new byte[] {
                (byte)(v & 0xFF),
                (byte)((v >> 8) & 0xFF),
                (byte)((v >> 16) & 0xFF),
                (byte)((v >> 24) & 0xFF),
            };
        }
    }
}
