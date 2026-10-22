using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using INOVANCEPLCTool.Plc;
using INOVANCEPLCTool.Usb;
using INOVANCEPLCTool.Transport;

namespace INOVANCEPLCTool
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑。
    ///
    /// 功能：
    ///   1) 连接通道：USB / 串口(RS485) / TCP，统一「打开 / 关闭」
    ///   2) PLC Run / Stop 控制
    ///   3) .updown 程序下载
    ///
    /// 所有耗时操作放到后台线程（Task.Run），通过 Dispatcher 把状态/日志
    /// 安全地写回 UI 线程的 TextBox 与进度条，避免界面假死。
    ///
    /// 说明：三条通道的「打开」均已实现，且<b>均支持下载</b>：
    ///   USB 走 PlcClient 私有帧（条目化下发）；
    ///   串口走 SerialBurnClient 0x43 协议（整包原样分块）；
    ///   TCP 走 TcpBurnClient 12939 私有协议（按条目「声明+内容」下发）。
    /// </summary>
    public partial class MainWindow : Window
    {
        // 0 = 空闲，1 = 占用中。用 Interlocked 防止重复点击重入。
        private int _busy = 0;

        // 当前已打开的通道（null 表示未连接）
        private ITransport _active;

        // 当前已打开通道对应的类型。未连接时为 null。
        // 用于「已连接则必须先关闭才能切换通道」以及「连接后锁死参数面板」。
        private TransportKind? _activeKind;

        // 重入保护：程序内部回滚 RadioButton 勾选状态时，抑制 RbKind_Checked 里的切换逻辑。
        private bool _suppressKindCheck;

        // 配置（exe 同级 config.ini）。构造函数里 Load，参数变更时 Save。
        private AppConfig _cfg;

        // 初始化期间抑制配置保存（避免 LoadConfig 回填控件时触发一堆无谓的写盘）。
        private bool _configReady;

        // 当前是否处于简易模式（启动默认 true；点「更多功能」切到完整界面）。
        private bool _simpleMode = true;

        // 启动时 config.ini 是否已存在。false = 首次运行 / 配置被删 → 启动后弹提示并直接进完整界面。
        // 必须在任何界面改动（会触发 Save 生成 config.ini）之前取值，故在构造函数最早处定格。
        private bool _configExistedOnStart;

        public MainWindow()
        {
            InitializeComponent();

            // ★ 先定格「启动瞬间配置文件是否存在」。
            //   注意：LoadConfigIntoUi 之后任何界面改动都会写盘生成 config.ini，
            //   所以这个判断必须放在最前面，否则永远为 true。
            _configExistedOnStart = File.Exists(AppConfig.ConfigPath);

            // 软件名称 + 版本号统一来自 AppInfo（版本号反射自 AssemblyInfo.cs 的
            // AssemblyFileVersion，改版本只需改那一行）。XAML 里的 Title 只是设计期占位。
            Title = AppInfo.FullTitle;
            if (lblVersionSimple != null) lblVersionSimple.Text = AppInfo.VersionDisplay;
            if (lblVersionFull != null) lblVersionFull.Text = AppInfo.ProductName + " " + AppInfo.VersionDisplay;
            // 归属声明（第三方非官方工具）：文案统一来自 AppInfo，两处界面同步
            if (lblAttributionSimple != null) lblAttributionSimple.Text = AppInfo.Attribution;
            if (runAttributionFull != null) runAttributionFull.Text = AppInfo.Attribution;
            RefreshPorts();
            LoadConfigIntoUi();       // 先回填（内部会刷新通道面板/提示/锁状态）
            UpdateUiLockState();
            // 启动默认进简易模式（一键下载）。若本次启动没有 config.ini，
            // 说明还没做过任何配置，无法一键下载 → 直接进完整界面，并在窗口显示后弹提示。
            bool noConfig = !_configExistedOnStart;
            _simpleMode = !noConfig;
            ApplyViewMode(_simpleMode);

            AppendLog("=== " + AppInfo.LogHeader + " 启动 ===");
            AppendLog("就绪。请选择连接通道（USB / 串口 / TCP）后点「打开」。");
            AppendLog("程序文件支持 .updown / .down 两种格式：USB、串口、TCP 均可下发（串口烧 .updown 会先弹确认框）。");
            AppendLog("提示：USB 通道需先关闭 AutoShop（libusb0 驱动独占设备）；TCP 烧录端口填 12939（非 Modbus 502）。");
            AppendLog("配置：" + AppConfig.ConfigPath +
                      (_configExistedOnStart ? "（已加载）" : "（不存在，使用默认值；改动后会自动生成）"));
            if (noConfig)
                AppendLog("未检测到配置文件 → 已直接进入完整界面。请手动选择连接通道与程序文件。");
            else
                AppendLog("简易模式：点「开始下载」将按下方顺序自动尝试连接，无需手动选通道。");

            // ★ 首次运行（无 config.ini）的提示必须等窗口真正显示后再弹：
            //   在构造函数里弹 MessageBox 没有 Owner，会出现「弹窗躲在主窗口后面」的怪现象。
            if (noConfig)
            {
                ContentRendered += MainWindow_FirstRunNotice;
            }
        }

        /// <summary>
        /// 首次运行提示：只在「启动时没有 config.ini」这一种情况下挂到 ContentRendered 上，
        /// 弹完即摘（事件只触发一次，但显式摘除更清楚，避免将来被别处复用）。
        /// </summary>
        private void MainWindow_FirstRunNotice(object sender, EventArgs e)
        {
            ContentRendered -= MainWindow_FirstRunNotice;

            MessageBox.Show(this,
                "未检测到预设配置文件，已自动进入「完整功能」界面。\n\n" +
                "你可以在这里手动选择连接通道（USB / 串口 / TCP）和程序文件。\n" +
                "各项参数改动后会自动保存到 exe 同级的 config.ini，\n" +
                "下次启动就会按这次的设置直接进入「一键下载」界面。\n\n" +
                "配置文件位置：\n" + AppConfig.ConfigPath,
                AppInfo.ProductName + " — 首次运行",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ------------------------------------------------------------------
        // 线程安全的 UI 更新
        // ------------------------------------------------------------------
        private void AppendLog(string msg)
        {
            if (Dispatcher.CheckAccess()) DoAppendLog(msg);
            else Dispatcher.Invoke(() => DoAppendLog(msg));
        }
        /// <summary>
        /// 写一行日志。多行消息（含 \n / \r\n）会自动拆成多行，
        /// 第二行起统一缩进到与首行正文对齐，避免时间戳只出现在第一行、后续行贴左边。
        /// </summary>
        private void DoAppendLog(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            string prefix = "[" + ts + "] ";

            if (string.IsNullOrEmpty(msg))
            {
                txtLog.AppendText(prefix + "\r\n");
                txtLog.ScrollToEnd();
                return;
            }

            // 统一换行符后逐行输出；续行用同宽空格占位（时间戳长度固定，视觉自然对齐）
            string[] lines = msg.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string contIndent = new string(' ', prefix.Length);
            for (int i = 0; i < lines.Length; i++)
                txtLog.AppendText((i == 0 ? prefix : contIndent) + lines[i] + "\r\n");

            txtLog.ScrollToEnd();
        }

        // ------------------------------------------------------------------
        // 进度条
        // ------------------------------------------------------------------

        private void SetProgress(int pct)
        {
            if (Dispatcher.CheckAccess()) DoSetProgress(pct);
            else Dispatcher.Invoke(() => DoSetProgress(pct));
        }

        /// <summary>两个视图各有一条进度条，始终同步（切视图时进度不丢）。</summary>
        private void DoSetProgress(int pct)
        {
            progress.Value = pct;
            progressSimple.Value = pct;
        }

        // ------------------------------------------------------------------
        // 配置：config.ini 加载 / 保存
        //
        // 策略：
        //   - 启动 Load：把 ini 值回填到各控件（通道单选 / 串口参数 / TCP 参数 /
        //     文件路径 / 强制继续勾选）；
        //   - 变更即 Save：由各控件的事件（通道切换、下拉变更、文本框失焦、
        //     浏览文件后、勾选框变更）触发，无需手动保存按钮；
        //   - 程序包路径一律用「相对 exe 的路径」存，拷走整个文件夹仍可用。
        // ------------------------------------------------------------------

        /// <summary>把配置回填到界面控件。初始化期间抑制保存。</summary>
        private void LoadConfigIntoUi()
        {
            _cfg = AppConfig.Load();

            // 通道单选
            if (string.Equals(_cfg.Channel, "Serial", StringComparison.OrdinalIgnoreCase))
                rbSerial.IsChecked = true;
            else if (string.Equals(_cfg.Channel, "Tcp", StringComparison.OrdinalIgnoreCase))
                rbTcp.IsChecked = true;
            else
                rbUsb.IsChecked = true;

            // 串口参数
            if (!string.IsNullOrEmpty(_cfg.SerialPort))
            {
                // 端口可能已不存在（换了电脑），存在才选中
                foreach (object o in cmbPort.Items)
                {
                    if (string.Equals(o.ToString(), _cfg.SerialPort, StringComparison.OrdinalIgnoreCase))
                    { cmbPort.SelectedItem = o; break; }
                }
            }
            SelectComboText(cmbBaud, _cfg.BaudRate.ToString());
            SelectComboText(cmbDataBits, _cfg.DataBits.ToString());
            SelectComboText(cmbParity, _cfg.Parity);
            SelectComboText(cmbStopBits, _cfg.StopBits);
            txtUnitId.Text = _cfg.SerialUnitId.ToString();

            // TCP 参数
            txtTcpHost.Text = _cfg.TcpHost;
            txtTcpPort.Text = _cfg.TcpPort.ToString();
            txtTcpUnitId.Text = _cfg.TcpUnitId.ToString();

            // 文件路径与勾选
            string prog = AppConfig.ToAbsolute(_cfg.LastProgramPath);
            if (!string.IsNullOrEmpty(prog)) txtFile.Text = prog;
            chkForce.IsChecked = _cfg.ForceContinue;

            // 刷新依赖通道/文件状态的 UI
            UpdateChannelPanel();
            UpdateFileHint();
            UpdateSimpleHint();

            _configReady = true;
        }

        /// <summary>从界面收集当前参数并写盘。任何异常都吞掉（配置失败不影响主功能）。</summary>
        private void SaveConfigFromUi()
        {
            if (!_configReady || _cfg == null) return;
            try
            {
                _cfg.Channel = CurrentKind.ToString();

                string port = cmbPort.SelectedItem == null ? "" : cmbPort.SelectedItem.ToString();
                _cfg.SerialPort = port;
                _cfg.BaudRate = int.Parse(ComboText(cmbBaud, "19200"));
                _cfg.DataBits = int.Parse(ComboText(cmbDataBits, "8"));
                _cfg.Parity = ComboText(cmbParity, "None");
                _cfg.StopBits = ComboText(cmbStopBits, "1");
                _cfg.SerialUnitId = ParseByte(txtUnitId.Text, 1);

                _cfg.TcpHost = txtTcpHost.Text == null ? "" : txtTcpHost.Text.Trim();
                _cfg.TcpPort = int.Parse(string.IsNullOrWhiteSpace(txtTcpPort.Text) ? "12939" : txtTcpPort.Text.Trim());
                _cfg.TcpUnitId = ParseByte(txtTcpUnitId.Text, 1);

                _cfg.ForceContinue = chkForce.IsChecked == true;

                // 程序包路径：转为相对 exe 的路径后再存
                string path = txtFile.Text == null ? "" : txtFile.Text.Trim();
                _cfg.LastProgramPath = string.IsNullOrEmpty(path)
                                     ? ""
                                     : AppConfig.ToRelative(path);

                _cfg.Save();
            }
            catch { /* 忽略 */ }
        }

        /// <summary>在 ComboBox 中按文本选中一项（找不到则不动）。</summary>
        private static void SelectComboText(ComboBox cb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (object o in cb.Items)
            {
                var item = o as ComboBoxItem;
                string s = item != null ? item.Content.ToString() : (o == null ? "" : o.ToString());
                if (string.Equals(s, text, StringComparison.OrdinalIgnoreCase))
                { cb.SelectedItem = o; return; }
            }
        }

        /// <summary>
        /// 参数控件的通用「变更即保存」处理器。
        /// 由通道参数下拉框（SelectionChanged）、参数文本框（LostFocus）、
        /// 「机型不匹配也继续」勾选框（Checked/Unchecked）共同触发。
        /// 初始化回填期间由 _configReady 拦住，不会误写盘。
        /// </summary>
        private void Cfg_Changed(object sender, RoutedEventArgs e)
        {
            SaveConfigFromUi();
        }

        private void SetBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) DoSetBusy(busy);
            else Dispatcher.Invoke(() => DoSetBusy(busy));
        }
        private void DoSetBusy(bool busy)
        {
            btnRun.IsEnabled = !busy;
            btnStop.IsEnabled = !busy;
            btnBrowse.IsEnabled = !busy;
            btnDownload.IsEnabled = !busy;
            btnFixDriver.IsEnabled = !busy;

            // 连接态相关控件（打开/关闭/通道单选/参数面板）统一由锁状态计算，
            // 避免这里与 DoUpdateUiLockState 互相覆盖导致按钮状态不一致。
            if (busy)
            {
                btnOpenPort.IsEnabled = false;
                btnClosePort.IsEnabled = false;
                SetKindPanelsEnabled(false);
            }
            else
            {
                DoUpdateUiLockState();
            }
        }

        // ------------------------------------------------------------------
        // 连接态 UI 锁定
        //
        // 规则（连接建立后）：
        //   1) 通道单选 & 打开按钮 -> 禁用（要换通道必须先点「关闭」）
        //   2) 各通道参数面板 -> 禁用（IP / 端口 / 波特率 / 从站地址 等不可再改）
        //   3) 「刷新串口」-> 禁用（避免连接中枚举串口）
        // ------------------------------------------------------------------
        private void UpdateUiLockState()
        {
            if (Dispatcher.CheckAccess()) DoUpdateUiLockState();
            else Dispatcher.Invoke(() => DoUpdateUiLockState());
        }

        /// <summary>
        /// ★ 所有会读写 UI 控件的公开入口必须经这里：
        /// 后台线程直接碰 WPF 控件会抛 <c>System.InvalidOperationException</c>
        /// （报错位置是 WindowsBase.dll，信息形如「调用线程无法访问此对象，
        /// 因为该对象的所有者为其他线程」）。
        /// </summary>
        private void RunOnUi(Action action)
        {
            if (action == null) return;
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.Invoke(action);
        }

        private void DoUpdateUiLockState()
        {
            bool connected = _active != null;

            rbUsb.IsEnabled = !connected;
            rbSerial.IsEnabled = !connected;
            rbTcp.IsEnabled = !connected;
            btnOpenPort.IsEnabled = !connected && (_busy == 0);

            pnlUsb.IsEnabled = !connected;
            pnlSerial.IsEnabled = !connected;
            pnlTcp.IsEnabled = !connected;
            btnRefreshPort.IsEnabled = !connected;

            btnClosePort.IsEnabled = connected && _busy == 0;
        }

        /// <summary>批量启用/禁用三个通道参数面板与单选按钮（供 SetBusy 调用）。</summary>
        /// <remarks>
        /// ★ 只允许在 UI 线程调用：内部读写 <c>rb*</c> / <c>pnl*</c> / <c>btnRefreshPort</c> 等控件。
        /// <see cref="SetBusy"/> 已做 Dispatcher 保护，故 <c>Task.Run</c> 里的 <c>SetBusy(false)</c>
        /// 会先切回 UI 线程再走到这里。
        /// </remarks>
        private void SetKindPanelsEnabled(bool enabled)
        {
            if (_active != null) return;   // 已连接时保持锁定，不因 SetBusy 被误放开
            rbUsb.IsEnabled = enabled;
            rbSerial.IsEnabled = enabled;
            rbTcp.IsEnabled = enabled;
            pnlUsb.IsEnabled = enabled;
            pnlSerial.IsEnabled = enabled;
            pnlTcp.IsEnabled = enabled;
            btnRefreshPort.IsEnabled = enabled;
        }

        // ------------------------------------------------------------------
        // 通道参数
        // ------------------------------------------------------------------
        private TransportKind CurrentKind
        {
            get
            {
                if (rbSerial.IsChecked == true) return TransportKind.Serial;
                if (rbTcp.IsChecked == true) return TransportKind.Tcp;
                return TransportKind.Usb;
            }
        }

        private void UpdateChannelPanel()
        {
            pnlUsb.Visibility = CurrentKind == TransportKind.Usb ? Visibility.Visible : Visibility.Collapsed;
            pnlSerial.Visibility = CurrentKind == TransportKind.Serial ? Visibility.Visible : Visibility.Collapsed;
            pnlTcp.Visibility = CurrentKind == TransportKind.Tcp ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RbKind_Checked(object sender, RoutedEventArgs e)
        {
            // 初始化期间控件引用可能尚未就绪，跳过；构造函数末尾会显式刷新一次。
            if (pnlUsb == null || pnlSerial == null || pnlTcp == null) return;

            // 内部回滚勾选/关闭通道时会触发本事件，直接跳过避免递归。
            if (_suppressKindCheck) return;

            // ★ 已连接时不允许直接切换通道：必须先「关闭」当前通道。
            if (_active != null)
            {
                TransportKind want = CurrentKind;
                if (_activeKind.HasValue && want != _activeKind.Value)
                {
                    _suppressKindCheck = true;
                    try
                    {
                        // 把选中项回滚到当前已打开的通道
                        if (_activeKind.Value == TransportKind.Serial) rbSerial.IsChecked = true;
                        else if (_activeKind.Value == TransportKind.Tcp) rbTcp.IsChecked = true;
                        else rbUsb.IsChecked = true;
                    }
                    finally { _suppressKindCheck = false; }

                    AppendLog(string.Format(
                        "当前 {0} 通道已连接，无法直接切换到 {1}。请先点「关闭」断开，再切换通道。",
                        KindName(_activeKind.Value), KindName(want)));
                    MessageBox.Show(
                        "当前通道已连接，无法直接切换。\n\n" +
                        "请先点「关闭」断开当前连接，再选择其他通道。",
                        "通道已连接", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            UpdateChannelPanel();
            UpdateFileHint();   // 通道变化时同步刷新"文件 → 下发方式"提示
            SaveConfigFromUi(); // 通道切换后记住选择
        }

        private static string KindName(TransportKind k)
        {
            switch (k)
            {
                case TransportKind.Serial: return "串口";
                case TransportKind.Tcp:    return "TCP";
                default:                   return "USB";
            }
        }

        private void RefreshPorts()
        {
            string[] ports = SerialLink.GetPortNames();
            cmbPort.Items.Clear();
            foreach (string p in ports) cmbPort.Items.Add(p);
            if (cmbPort.Items.Count > 0) cmbPort.SelectedIndex = 0;
            AppendLog("检测到 " + ports.Length + " 个串口：" +
                      (ports.Length > 0 ? string.Join(", ", ports) : "无"));
        }

        private void BtnRefreshPort_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
            SaveConfigFromUi();   // 端口列表变化可能改变选中项，同步保存
        }

        /// <summary>按当前 UI 配置生成通道参数。</summary>
        private TransportOptions BuildOptions()
        {
            var o = new TransportOptions();
            o.Kind = CurrentKind;

            if (CurrentKind == TransportKind.Serial)
            {
                if (cmbPort.SelectedItem == null)
                    throw new InvalidOperationException("请选择串口（未检测到可用串口时可点「刷新」）。");
                o.PortName = cmbPort.SelectedItem.ToString();
                o.BaudRate = int.Parse(ComboText(cmbBaud, "19200"));
                o.DataBits = int.Parse(ComboText(cmbDataBits, "8"));
                o.Parity = ParseParity(ComboText(cmbParity, "None"));
                o.StopBits = ComboText(cmbStopBits, "1") == "2" ? StopBits.Two : StopBits.One;
                o.UnitId = ParseByte(txtUnitId.Text, 1);
                o.Timeout = 3000;
            }
            else if (CurrentKind == TransportKind.Tcp)
            {
                if (string.IsNullOrWhiteSpace(txtTcpHost.Text))
                    throw new InvalidOperationException("请填写 PLC 的 IP 地址。");
                o.Host = txtTcpHost.Text.Trim();
                o.Port = int.Parse(string.IsNullOrWhiteSpace(txtTcpPort.Text) ? "12939" : txtTcpPort.Text.Trim());
                o.UnitId = ParseByte(txtTcpUnitId.Text, 1);
                o.Timeout = 3000;
            }
            else
            {
                o.Timeout = 10000;
            }
            return o;
        }

        private static string ComboText(ComboBox cb, string fallback)
        {
            var item = cb.SelectedItem as ComboBoxItem;
            return item != null ? item.Content.ToString() : fallback;
        }

        private static Parity ParseParity(string s)
        {
            switch (s)
            {
                case "Even": return Parity.Even;
                case "Odd": return Parity.Odd;
                default: return Parity.None;
            }
        }

        private static byte ParseByte(string s, byte fallback)
        {
            byte v;
            if (byte.TryParse((s ?? "").Trim(), out v)) return v;
            return fallback;
        }

        // ------------------------------------------------------------------
        // 打开 / 关闭通道
        // ------------------------------------------------------------------
        private void BtnOpenPort_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            SetBusy(true);

            TransportOptions opt;
            try { opt = BuildOptions(); }
            catch (Exception ex)
            {
                AppendLog("参数错误：" + ex.Message);
                Interlocked.Exchange(ref _busy, 0);
                SetBusy(false);
                UpdateUiLockState();
                return;
            }

            AppendLog("--- 打开通道 开始 ---");
            Task.Run(() =>
            {
                try
                {
                    // USB 通道需要先确保驱动就绪
                    if (opt.Kind == TransportKind.Usb && !DriverInstaller.EnsureDriver(AppendLog))
                    {
                        AppendLog("--- 打开通道 中止（缺少 libusb0 驱动）---");
                        return;
                    }

                    ITransport t = opt.Create();
                    AppendLog("正在打开：" + t.DisplayName);
                    t.Open();

                    RunOnUi(() =>
                    {
                        _active = t;
                        _activeKind = opt.Kind;
                        lblPortState.Text = "已连接：" + t.DisplayName;
                        lblPortState.Foreground = System.Windows.Media.Brushes.Green;
                    });
                    UpdateUiLockState();   // 连接建立 -> 锁定通道单选与参数面板

                    // 打开后做一次连通性探测（USB 走私有协议信息读取；
                    // 串口/TCP 暂只提示协议映射待补，不做业务请求以免误发）
                    if (t is UsbLink)
                    {
                        using (var client = new PlcClient(t))
                        {
                            client.Login();
                            PlcDeviceInfo info = client.DeviceInfo();
                            AppendLog(string.Format("已连接 PLC：机型={0}  IP={1}  状态={2}",
                                string.IsNullOrEmpty(info.Model) ? "?" : info.Model,
                                string.IsNullOrEmpty(info.Ip) ? "?" : info.Ip,
                                Proto.RunStateName(client.GetRunState())));
                        }
                    }
                    else if (t is TcpLink)
                    {
                        // TCP 12939 私有协议：握手探测，读取机型与运行态
                        try
                        {
                            var tc = new TcpBurnClient(t);
                            tc.Handshake();
                            tc.Login();
                            AppendLog(string.Format("已连接 PLC：机型={0}  状态={1}",
                                string.IsNullOrEmpty(tc.DeviceModel) ? "?" : tc.DeviceModel,
                                tc.ReadRunState() == 1 ? "运行" : "停止"));
                        }
                        catch (Exception ex)
                        {
                            AppendLog("TCP 握手探测失败（不影响后续可重试）：" + ex.Message);
                        }
                    }
                    else
                    {
                        AppendLog("通道已打开。注意：该通道的程序下载协议尚在解析中，");
                        AppendLog("      需先用触摸屏执行一次下载并抓取 485 报文后再启用。");
                    }

                    AppendLog("--- 打开通道 完成 ---");
                }
                catch (Exception ex)
                {
                    AppendLog("打开失败：" + ex.Message);
                    RunOnUi(() =>
                    {
                        _active = null;
                        _activeKind = null;
                        lblPortState.Text = "未连接";
                        lblPortState.Foreground = System.Windows.Media.Brushes.Firebrick;
                    });
                    UpdateUiLockState();
                    AppendLog("--- 打开通道 失败 ---");
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                    SetBusy(false);
                    UpdateUiLockState();
                }
            });
        }

        /// <summary>
        /// 关闭当前已打开的通道，reason 用于日志区分调用来源。
        /// ★ 线程安全：可在后台线程调用（简易模式的一键下载在 <c>Task.Run</c> 的
        /// finally 里收尾时会调它）。实际读写控件与 <c>_active</c> 的部分在 UI 线程执行。
        /// </summary>
        private void CloseActive(string reason)
        {
            RunOnUi(() => DoCloseActive(reason));
        }

        private void DoCloseActive(string reason)
        {
            ITransport t = _active;
            if (t == null) return;
            try
            {
                t.Close();
                t.Dispose();
            }
            catch (Exception ex)
            {
                AppendLog("关闭异常：" + ex.Message);
            }
            _active = null;
            _activeKind = null;
            lblPortState.Text = "未连接";
            lblPortState.Foreground = System.Windows.Media.Brushes.Firebrick;
            AppendLog("通道已关闭：" + t.DisplayName + (string.IsNullOrEmpty(reason) ? "" : "（" + reason + "）"));
            UpdateUiLockState();   // 断开 -> 放开通道单选与参数面板
        }

        private void BtnClosePort_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            SetBusy(true);
            try { CloseActive(null); }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                SetBusy(false);
                UpdateUiLockState();
            }
        }

        // ------------------------------------------------------------------
        // 通用：确保已连接 -> 执行动作
        // ------------------------------------------------------------------
        /// <summary>
        /// 使用当前已打开的通道执行动作。若未连接则提示先打开。
        /// </summary>
        private void RunOnActive(Action<PlcClient> action, string opName)
        {
            ITransport t = _active;
            if (t == null)
            {
                AppendLog("请先在顶部「连接通道」中选择并点「打开」。");
                return;
            }
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            SetBusy(true);
            AppendLog("--- " + opName + " 开始 ---");
            Task.Run(() =>
            {
                try
                {
                    using (var client = new PlcClient(t))
                    {
                        action(client);
                    }
                    AppendLog("--- " + opName + " 完成 ---");
                }
                catch (Exception ex)
                {
                    AppendLog("错误：" + ex.Message);
                    AppendLog("--- " + opName + " 失败 ---");
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                    SetBusy(false);
                }
            });
        }

        // ------------------------------------------------------------------
        // 按钮事件
        // ------------------------------------------------------------------
        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            SetRunStateSerialAware(SerialProtocol.CTRL_RUN, "RUN 运行");
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            SetRunStateSerialAware(SerialProtocol.CTRL_STOP, "STOP 停止");
        }

        /// <summary>
        /// 设置运行/停止。串口通道必须走 0x43 私有协议（<see cref="SerialBurnClient"/>），
        /// 不能用 USB 那套 Modbus 的 <see cref="PlcClient"/>——两者协议完全不同。
        /// </summary>
        private void SetRunStateSerialAware(byte value, string opName)
        {
            ITransport t = _active;
            if (t == null)
            {
                AppendLog("请先在顶部「连接通道」中选择并点「打开」。");
                return;
            }

            // ---- 串口通道：走 0x43 协议 ----
            if (t is SerialLink)
            {
                if (Interlocked.Exchange(ref _busy, 1) == 1) return;
                SetBusy(true);
                AppendLog("--- " + opName + " 开始 ---");
                Task.Run(() =>
                {
                    try
                    {
                        var sc = new SerialBurnClient((SerialLink)t);
                        sc.Handshake();                     // 握手
                        sc.WriteRunState(value);            // 写运行/停止
                        int st = -1;
                        for (int i = 0; i < 3; i++)         // 轮询确认
                        {
                            Thread.Sleep(400);
                            st = sc.ReadRunState();
                            if (st == (value == SerialProtocol.CTRL_RUN ? 1 : 0)) break;
                            sc.WriteRunState(value);
                        }
                        if (st == (value == SerialProtocol.CTRL_RUN ? 1 : 0))
                            AppendLog(string.Format("      ✓ 已确认 PLC 状态 = {0}（{1}）。",
                                st, st == 1 ? "运行" : "停止"));
                        else
                            AppendLog(string.Format("      ✗ 未确认（回读={0}，期望={1}），请检查 PLC。",
                                st < 0 ? "无应答" : st.ToString(),
                                value == SerialProtocol.CTRL_RUN ? "1(运行)" : "0(停止)"));
                        AppendLog("--- " + opName + " 完成 ---");
                    }
                    catch (Exception ex)
                    {
                        AppendLog("错误：" + ex.Message);
                        AppendLog("--- " + opName + " 失败 ---");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                        SetBusy(false);
                    }
                });
                return;
            }

            // ---- TCP 通道：走 12939 私有协议 ----
            if (t is TcpLink)
            {
                if (Interlocked.Exchange(ref _busy, 1) == 1) return;
                SetBusy(true);
                AppendLog("--- " + opName + " 开始 ---");
                Task.Run(() =>
                {
                    try
                    {
                        var tc = new TcpBurnClient(t);
                        tc.Handshake();                     // 握手
                        tc.Login();                         // 登录
                        tc.Session();                       // 会话控制
                        tc.WriteRunState(value);            // 写运行/停止
                        int st = -1;
                        for (int i = 0; i < 3; i++)         // 轮询确认
                        {
                            Thread.Sleep(400);
                            st = tc.ReadRunState();
                            if (st == (value == TcpPlcProtocol.CTRL_RUN ? 1 : 0)) break;
                            tc.WriteRunState(value);
                        }
                        if (st == (value == TcpPlcProtocol.CTRL_RUN ? 1 : 0))
                            AppendLog(string.Format("      ✓ 已确认 PLC 状态 = {0}（{1}）。",
                                st, st == 1 ? "运行" : "停止"));
                        else
                            AppendLog(string.Format("      ✗ 未确认（回读={0}，期望={1}），请检查 PLC。",
                                st < 0 ? "无应答" : st.ToString(),
                                value == TcpPlcProtocol.CTRL_RUN ? "1(运行)" : "0(停止)"));
                        AppendLog("--- " + opName + " 完成 ---");
                    }
                    catch (Exception ex)
                    {
                        AppendLog("错误：" + ex.Message);
                        AppendLog("--- " + opName + " 失败 ---");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                        SetBusy(false);
                    }
                });
                return;
            }

            // ---- USB 通道：沿用原有 PlcClient ----
            RunOnActive(client =>
            {
                if (client.Transport is UsbLink) client.Login();
                if (value == SerialProtocol.CTRL_RUN) client.Run();
                else client.Stop();
                AppendLog("已发送 " + opName + " 命令。");
            }, opName);
        }

        private void BtnFixDriver_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            SetBusy(true);
            AppendLog("--- 安装/修复驱动 开始 ---");
            Task.Run(() =>
            {
                try
                {
                    // silent:false -> 安装完成后 cmd 窗口停留显示结果，便于观察
                    bool ok = DriverInstaller.InstallDriver(AppendLog, silent: false);
                    AppendLog(ok
                        ? "--- 安装/修复驱动 完成 ---"
                        : "--- 安装/修复驱动 未完成（见上方提示，可手动以管理员身份运行 INOVANCE_Drivers\\install_driver.bat）---");
                }
                catch (Exception ex)
                {
                    AppendLog("错误：" + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                    SetBusy(false);
                }
            });
        }

        /// <summary>
        /// 选择程序包。选中后会**自动拷贝一份到 exe 同级的 Programs 目录**，
        /// 并把「相对 exe 的路径」写进配置 —— 这样整个文件夹拷给别人后依然能找到文件。
        /// </summary>
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            string progDir;
            if (!AppConfig.EnsureProgramsDir(out progDir))
                progDir = "";

            var dlg = new OpenFileDialog
            {
                Filter = "支持的程序包 (*.updown;*.down)|*.updown;*.down|" +
                         "USB 下载包 (*.updown)|*.updown|" +
                         "串口烧录包 (*.down)|*.down|" +
                         "所有文件 (*.*)|*.*",
                Title = "选择程序包（.updown / .down）"
            };
            // 默认打开「内置程序目录」，方便直接挑已内置的程序
            if (!string.IsNullOrEmpty(progDir) && Directory.Exists(progDir))
                dlg.InitialDirectory = progDir;

            if (dlg.ShowDialog() != true) return;

            string selected = dlg.FileName;

            // 拷贝进 Programs 目录（失败不阻断：仍可用原路径下载）
            try
            {
                string dest = AppConfig.CopyIntoPrograms(selected);
                if (!string.Equals(dest, selected, StringComparison.OrdinalIgnoreCase))
                    AppendLog("已拷入内置程序目录：" + Path.GetFileName(dest));
                selected = dest;
            }
            catch (Exception ex)
            {
                AppendLog("⚠ 拷入内置程序目录失败（不影响本次下载）：" + ex.Message);
            }

            txtFile.Text = selected;
            UpdateFileHint();
            UpdateSimpleHint();
            SaveConfigFromUi();
        }

        /// <summary>
        /// 刷新简易模式提示卡里的「当前程序」一行：告诉操作者「这次会下载哪个程序」。
        /// 因为简易模式不再显示文件选择框，程序包名称必须在这里让操作者看得见。
        /// </summary>
        private void UpdateSimpleHint()
        {
            if (lblSimpleChannel == null) return;

            string path = txtFile.Text == null ? "" : txtFile.Text.Trim();
            if (path.Length == 0)
            {
                lblSimpleChannel.Text = "尚未选择程序 —— 请点「更多功能 ▸」选择要下载的程序包。";
                return;
            }

            string name = Path.GetFileName(path);
            string ver = "";
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".down" || ext == ".updown")
                {
                    DownPackage pkg = DownPackage.LoadRawAny(path);
                    if (pkg != null && !string.IsNullOrEmpty(pkg.Version)) ver = "（版本 " + pkg.Version + "）";
                }
            }
            catch { /* 解析失败不影响显示文件名 */ }

            lblSimpleChannel.Text = "本次将下载：" + name + ver;
        }

        /// <summary>按当前通道 + 已选文件，更新按钮下方的提示文字。</summary>
        private void UpdateFileHint()
        {
            if (lblFileHint == null) return;
            string path = txtFile.Text == null ? "" : txtFile.Text.Trim();
            if (string.IsNullOrEmpty(path)) { lblFileHint.Text = ""; return; }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".down" && ext != ".updown")
            {
                lblFileHint.Text = "⚠ 未知格式（" + ext + "），仅支持 .updown / .down";
                return;
            }

            string kind = ext == ".down" ? ".down（串口包）" : ".updown（USB 包）";
            string route;
            if (CurrentKind == TransportKind.Serial)
                route = ext == ".down"
                        ? "→ 串口烧录（0x43 协议，已验证）"
                        : "→ 串口烧录（0x43 协议，实测可烧；将弹确认框）";
            else if (CurrentKind == TransportKind.Tcp)
                route = "→ TCP 烧录（12939 私有协议，按条目下发；端口需填 12939）";
            else // USB
                route = ext == ".updown"
                        ? "→ USB 下载（条目化下发，已验证）"
                        : "→ USB 下载（按条目拆分下发；.down 走串口更稳）";

            lblFileHint.Text = kind + "  " + route;
        }

        /// <summary>
        /// 统一的「下载 / 烧录」入口：按 <b>当前通道 + 文件扩展名</b> 自动选择下发方式。
        ///
        ///   USB  + .updown → PlcClient.Download（条目化下发）
        ///   USB  + .down   → PlcClient.Download（.down 与 .updown 同族，同样条目化下发）
        ///   串口 + .down   → SerialBurnClient.Burn（0x43 协议，整包原样分块下发）
        ///   串口 + .updown → 同上（实测可行：串口协议不校验魔数，.updown 烧录后同样能运行；
        ///                     仅弹一次确认提示，提醒这不是官方推荐组合）
        ///   TCP  + 任意    → TcpBurnClient.Burn（12939 私有协议，按条目「声明+内容」下发）
        ///
        /// ⚠ 串口 + .updown 的可行性来自<b>真机实测</b>（2026-09-21，H6E @COM9 19200）：
        ///   完整烧录 .updown（50523B）→ 逐块 ACK 06 → 提交读回 02 00 58 00 → 运行态回读 1（RUN OK）。
        ///   官方支持矩阵仍是「串口↔.down / USB↔.updown」，故这里保留一个提示框而非静默执行。
        /// </summary>
        private void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            string path = txtFile.Text == null ? "" : txtFile.Text.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                AppendLog("请先通过「浏览...」选择有效的程序包（.updown 或 .down）。");
                return;
            }

            ITransport t = _active;
            if (t == null)
            {
                AppendLog("请先在顶部「连接通道」中选择通道并点「打开」。");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isDown = ext == ".down";
            bool isUpdown = ext == ".updown";
            if (!isDown && !isUpdown)
            {
                AppendLog("不支持的文件类型：" + ext + "。请选择 .updown 或 .down 程序包。");
                return;
            }

            // ---- 串口通道 ----
            if (t is SerialLink)
            {
                if (isUpdown)
                {
                    // 串口 + .updown：实测可行（PLC 不校验头部魔数，烧完能运行），
                    // 但这不是官方推荐组合，弹一次确认框让操作者知情、可取消。
                    var r = MessageBox.Show(
                        "你正在用串口烧录 .updown 包。\n\n" +
                        "• 官方推荐：串口用 .down、USB 用 .updown。\n" +
                        "• 实测结论：串口能烧 .updown，烧录后 PLC 同样能进入运行态（已真机验证）。\n" +
                        "• 风险：非官方组合，个别机型/版本未逐一验证，请确认后继续。\n\n" +
                        "是否继续用串口烧录这个 .updown 包？",
                        "串口烧录 .updown（需确认）",
                        MessageBoxButton.OKCancel, MessageBoxImage.Information);
                    if (r != MessageBoxResult.OK)
                    {
                        AppendLog("已取消：串口烧录 .updown 被操作者取消。");
                        return;
                    }
                }

                if (Interlocked.Exchange(ref _busy, 1) == 1) return;
                SetBusy(true);
                SetProgress(0);
                AppendLog(string.Format("--- 串口烧录 开始（0x43 协议，{0}）---", ext));
                Task.Run(() =>
                {
                    try
                    {
                        // 串口下发的就是整包原始字节流；用 LoadRawAny 避免 .updown 被魔数校验拦下
                        DownPackage pkg = DownPackage.LoadRawAny(path);
                        AppendLog(string.Format("已载入下载包：{0} 版本 {1} 机型 {2}，条目 {3} 个，合计 {4} 字节",
                            Path.GetFileName(pkg.Path), pkg.Version, pkg.Model, pkg.Order.Count, pkg.TotalBytes));

                        // ① 下载前握手 + 读运行态（用于决定是否弹确认框）
                        var client = new SerialBurnClient((SerialLink)t);
                        client.Handshake();
                        AppendLog(string.Format("      已连接，PLC 机型={0}",
                            string.IsNullOrEmpty(client.DeviceModel) ? "(未解析)" : client.DeviceModel));
                        int before = client.ReadRunState();
                        AppendLog(string.Format("      下载前运行态={0}（1=运行 0=停止）",
                            before < 0 ? "?" : before.ToString()));

                        // ② 运行中 → 弹窗确认是否停机继续；取消则整个操作中止
                        if (before == 1 && !ConfirmStopBeforeDownload())
                        {
                            AppendLog("已取消：操作者在「PLC 处于运行状态」提示中选择取消。");
                            AppendLog("--- 串口烧录 取消 ---");
                            return;
                        }

                        // ③ 执行烧录（resumeRun=false：恢复运行交界面层询问，不自动执行）
                        client.Burn(pkg, msg => AppendLog(msg), pct => SetProgress(pct), false);
                        AppendLog("--- 串口烧录 完成 ---");

                        // ④ 下载前是运行态 → 弹窗询问是否恢复运行
                        if (before == 1) ConfirmResumeRunAfterDownload(() => ResumeSerial(client));
                    }
                    catch (Exception ex)
                    {
                        AppendLog("烧录错误：" + ex.Message);
                        AppendLog("--- 串口烧录 失败 ---");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                        SetBusy(false);
                    }
                });
                return;
            }

            // ---- TCP 通道：12939 私有协议，按条目「声明+内容」下发 ----
            if (t is TcpLink)
            {
                if (Interlocked.Exchange(ref _busy, 1) == 1) return;
                SetBusy(true);
                SetProgress(0);
                AppendLog(string.Format("--- TCP 烧录 开始（12939 私有协议，{0}）---", ext));
                Task.Run(() =>
                {
                    try
                    {
                        // .down 与 .updown 同族（TLV 头 + 条目区），条目化下发方式一致，用 LoadRawAny 兼容两种
                        DownPackage pkg = DownPackage.LoadRawAny(path);
                        AppendLog(string.Format("已载入下载包：{0} 版本 {1} 机型 {2}，条目 {3} 个，合计 {4} 字节",
                            Path.GetFileName(pkg.Path), pkg.Version, pkg.Model, pkg.Order.Count, pkg.TotalBytes));

                        // ① 下载前探测运行态。
                        //    注意：这里只做「握手 + 读状态」，登录/会话由后面 Burn() 内部完整重做，
                        //    避免探测阶段改变会话状态影响烧录时序。
                        var client = new TcpBurnClient(t);
                        client.Handshake();
                        client.Login();
                        client.Session();
                        AppendLog(string.Format("      已连接，PLC 机型={0}",
                            string.IsNullOrEmpty(client.DeviceModel) ? "(未解析)" : client.DeviceModel));
                        int before = client.ReadRunState();
                        AppendLog(string.Format("      下载前运行态={0}（1=运行 0=停止）",
                            before < 0 ? "?" : before.ToString()));

                        // ② 运行中 → 弹窗确认
                        if (before == 1 && !ConfirmStopBeforeDownload())
                        {
                            AppendLog("已取消：操作者在「PLC 处于运行状态」提示中选择取消。");
                            AppendLog("--- TCP 烧录 取消 ---");
                            return;
                        }

                        // ③ 烧录（Burn 内部会重新握手/登录/会话/停机；resumeRun=false）
                        client.Burn(Path.GetFileName(pkg.Path), pkg.Order, pkg.Files,
                                    msg => AppendLog(msg), pct => SetProgress(pct), false);
                        AppendLog("--- TCP 烧录 完成 ---");

                        // ④ 恢复运行询问
                        if (before == 1) ConfirmResumeRunAfterDownload(() => ResumeTcp(client));
                    }
                    catch (Exception ex)
                    {
                        AppendLog("烧录错误：" + ex.Message);
                        AppendLog("--- TCP 烧录 失败 ---");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                        SetBusy(false);
                    }
                });
                return;
            }

            // ---- USB 通道：.updown 与 .down 都走 PlcClient（条目化下发）----
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            SetBusy(true);
            SetProgress(0);
            bool force = chkForce.IsChecked == true;

            AppendLog(string.Format("--- 程序下载 开始（USB / {0}）---", ext));
            Task.Run(() =>
            {
                try
                {
                    using (var client = new PlcClient(t))
                    {
                        // ① 先登录并读下载前运行态（决定是否弹确认框）
                        client.Login();
                        int before = client.GetRunState();
                        AppendLog(string.Format("      下载前运行态={0}（1=运行 0=停止）",
                            before < 0 ? "?" : before.ToString()));

                        // ② 运行中 → 弹窗确认是否停机继续
                        if (before == 1 && !ConfirmStopBeforeDownload())
                        {
                            AppendLog("已取消：操作者在「PLC 处于运行状态」提示中选择取消。");
                            AppendLog("--- 程序下载 取消 ---");
                            return;
                        }

                        // ③ 下载：resumeRunState=-1 表示不自动恢复，交下面按需询问
                        if (isDown)
                        {
                            DownPackage pkg = DownPackage.Load(path);
                            AppendLog(string.Format("已解析 .down 包：{0} 版本 {1} 机型 {2}，条目 {3} 个",
                                Path.GetFileName(pkg.Path), pkg.Version, pkg.Model, pkg.Order.Count));
                            client.Download(pkg, force, msg => AppendLog(msg), pct => SetProgress(pct), -1);
                        }
                        else
                        {
                            UpdownPackage pkg = UpdownPackage.Load(path);
                            AppendLog(string.Format("已解析 .updown 包：{0} 版本 {1} 机型 {2}，条目 {3} 个",
                                Path.GetFileName(pkg.Path), pkg.Version, pkg.Model, pkg.Order.Count));
                            client.Download(pkg, force, msg => AppendLog(msg), pct => SetProgress(pct), -1);
                        }

                        // ④ 恢复运行询问
                        if (before == 1) ConfirmResumeRunAfterDownload(() => ResumeUsb(client));
                    }
                    AppendLog("--- 程序下载 完成 ---");
                }
                catch (Exception ex)
                {
                    AppendLog("下载错误：" + ex.Message);
                    AppendLog("--- 程序下载 失败 ---");
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                    SetBusy(false);
                }
            });
        }

        // ------------------------------------------------------------------
        // 下载前后的运行态确认（三个通道共用）
        // ------------------------------------------------------------------
        /// <summary>
        /// 下载前：PLC 处于运行态时的确认框。
        /// 返回 true = 用户同意「停机并继续」，false = 用户取消（调用方应中止整个操作）。
        /// </summary>
        private bool ConfirmStopBeforeDownload()
        {
            return ShowModal(() =>
                MessageBox.Show(
                    "PLC处于运行状态，确定要将PLC停止并且继续操作吗？",
                    "PLC 运行中", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                == MessageBoxResult.OK);
        }

        /// <summary>
        /// 下载后：若下载前 PLC 是运行态，询问是否恢复运行（仅在用户点「确定」时执行）。
        /// 三个通道共用一个回调，<paramref name="resumeAction"/> 里做各自协议的「写运行」+ 轮询确认。
        /// </summary>
        private void ConfirmResumeRunAfterDownload(Action resumeAction)
        {
            bool ok = ShowModal(() =>
                MessageBox.Show(
                    "PLC处于停止状态，确定要将PLC运行并且继续操作吗？",
                    "PLC 已停止", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                == MessageBoxResult.OK);

            if (!ok)
            {
                AppendLog("操作者选择「不恢复运行」，PLC 保持停止态。可稍后用顶部「RUN 运行」按钮手动启动。");
                return;
            }
            try { resumeAction(); }
            catch (Exception ex) { AppendLog("恢复运行失败：" + ex.Message); }
        }

        /// <summary>线程安全的模态框调用（后台线程调用时切回 UI 线程执行）。</summary>
        private T ShowModal<T>(Func<T> func)
        {
            if (Dispatcher.CheckAccess()) return func();
            return Dispatcher.Invoke(func);
        }

        // ------------------------------------------------------------------
        // 恢复运行（三个通道各自的协议实现，由确认框回调触发）
        // ------------------------------------------------------------------
        /// <summary>串口：写运行态并轮询确认（复用已握手的 client，避免重复握手）。</summary>
        private void ResumeSerial(SerialBurnClient client)
        {
            AppendLog("恢复运行 ...");
            client.WriteRunState(SerialProtocol.CTRL_RUN);
            int st = -1;
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(400);
                st = client.ReadRunState();
                if (st == 1) break;
                client.WriteRunState(SerialProtocol.CTRL_RUN);
            }
            LogResumeResult(st);
        }

        /// <summary>TCP：写运行态并轮询确认。</summary>
        private void ResumeTcp(TcpBurnClient client)
        {
            AppendLog("恢复运行 ...");
            client.WriteRunState(TcpPlcProtocol.CTRL_RUN);
            int st = -1;
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(400);
                st = client.ReadRunState();
                if (st == 1) break;
                client.WriteRunState(TcpPlcProtocol.CTRL_RUN);
            }
            LogResumeResult(st);
        }

        /// <summary>USB：写运行态并轮询确认。</summary>
        private void ResumeUsb(PlcClient client)
        {
            AppendLog("恢复运行 ...");
            client.Run();
            bool ok = client.WaitRunState(1, 3, 400);
            LogResumeResult(ok ? 1 : client.GetRunState());
        }

        /// <summary>统一打印恢复运行的结果（st: 1=已运行 0=仍停止 -1=无应答）。</summary>
        private void LogResumeResult(int st)
        {
            if (st == 1)
            {
                AppendLog("      ✓ 已确认 PLC 进入运行态（RUN，回读=1）。");
            }
            else if (st == 0)
            {
                AppendLog("      ✗ PLC 仍处于停止态（回读=0）！");
                AppendLog("        可能原因：① 程序校验未通过被拒绝运行；② 面板 RUN/STOP 开关在 STOP；③ 程序存在致命错误。");
                AppendLog("        可点顶部「RUN 运行」重试，或断电重启 PLC。");
            }
            else
            {
                AppendLog("      ? 运行态回读失败（无应答），请观察 PLC 面板 RUN 灯确认。");
            }
        }

        // ==================================================================
        // 简易模式 / 完整模式（同一窗口双视图）
        //
        //   启动默认进入简易模式：只有「程序包 + 开始下载 + 进度条 + 状态 + 说明」。
        //   通道不在这里选——按 config.ini 里记录的参数，点下载后自动依次尝试连接。
        //   点「更多功能 ▸」切到完整界面（窗口放大），点「◂ 简易模式」切回（窗口缩小）。
        // ==================================================================

        /// <summary>简易模式窗口尺寸（紧凑：下载按钮 + 进度条 + 测试版提示卡 + 更多功能）。</summary>
        private const double SimpleWidth = 520;
        private const double SimpleHeight = 390;

        /// <summary>完整模式窗口尺寸。</summary>
        private const double FullWidth = 850;
        private const double FullHeight = 850;

        private void BtnMore_Click(object sender, RoutedEventArgs e)
        {
            ApplyViewMode(false);
        }

        private void BtnSimpleMode_Click(object sender, RoutedEventArgs e)
        {
            ApplyViewMode(true);
        }

        /// <summary>
        /// 切换视图：显示对应 Grid，并把窗口尺寸跟着调整。
        /// 尺寸基于窗口当前所在显示器的工作区做钳制，避免小屏/缩放时露出屏幕外。
        /// </summary>
        private void ApplyViewMode(bool simple)
        {
            _simpleMode = simple;

            viewSimple.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
            viewFull.Visibility = simple ? Visibility.Collapsed : Visibility.Visible;

            double w = simple ? SimpleWidth : FullWidth;
            double h = simple ? SimpleHeight : FullHeight;

            // 钳到桌面工作区，避免小屏 / 高 DPI 缩放下窗口跑到屏幕外
            double maxW = SystemParameters.WorkArea.Width - 20;
            double maxH = SystemParameters.WorkArea.Height - 20;
            if (maxW > 200 && w > maxW) w = maxW;
            if (maxH > 200 && h > maxH) h = maxH;

            MinWidth = 0; MinHeight = 0;   // 临时放开下限，否则缩小会被已有下限卡住
            Width = w;
            Height = h;

            // 居中显示
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - w) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - h) / 2;

            // 同步一次连接态锁定：从简易模式切回来时，完整界面的
            // 「通道单选 / 参数面板 / 打开关闭按钮」必须反映真实连接状态，
            // 不能停留在切换前的旧状态。
            UpdateUiLockState();

            if (simple)
            {
                AppendLog("已切换到简易模式（只显示下载与状态；点「更多功能」回到完整界面）。");
                UpdateSimpleHint();
            }
            else
            {
                AppendLog("已切换到完整功能界面。");
            }
        }

        /// <summary>
        /// 自动尝试连接的顺序：上次用成功的通道排第一，其余按 USB → 串口 → TCP。
        /// 这样正常使用时第一枪就命中，只有当设备换了接口/换了通道时才需要多试几次。
        /// </summary>
        private List<TransportKind> BuildAutoTryOrder()
        {
            var order = new List<TransportKind>();

            TransportKind last = TransportKind.Usb;
            if (string.Equals(_cfg.Channel, "Serial", StringComparison.OrdinalIgnoreCase)) last = TransportKind.Serial;
            else if (string.Equals(_cfg.Channel, "Tcp", StringComparison.OrdinalIgnoreCase)) last = TransportKind.Tcp;

            order.Add(last);
            foreach (TransportKind k in new[] { TransportKind.Usb, TransportKind.Serial, TransportKind.Tcp })
                if (k != last) order.Add(k);

            return order;
        }

        /// <summary>
        /// 简易模式下的「开始下载」：确认 → 依次尝试各通道连接 → 连上后自动下载。
        /// 全程不需要操作者理解 USB / 串口 / TCP 的区别。
        /// </summary>
        private void BtnDownloadSimple_Click(object sender, RoutedEventArgs e)
        {
            // 简易模式不显示文件选择框，程序包来自 config.ini（上次选过的那个）。
            string path = txtFile.Text == null ? "" : txtFile.Text.Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                MessageBox.Show(
                    "还没有选择要下载的程序。\n\n" +
                    "请点右下角「更多功能 ▸」，在「程序文件」一栏点「浏览...」选择 .down / .updown，\n" +
                    "选好后回到简易模式即可一键下载（下次打开会自动记住）。",
                    "请先选择程序", MessageBoxButton.OK, MessageBoxImage.Information);
                ApplyViewMode(false);   // 直接带他去选
                return;
            }

            // 若已连好某个通道，就直接复用，不再重复连接。
            // ★ 注意：_active 只在 UI 线程读写，这里（UI 线程）先取快照，
            //   后台线程只用这个快照，避免跨线程读字段造成状态错乱。
            bool alreadyConnected = (_active != null);

            MessageBoxResult r = MessageBox.Show(
                "即将把下面这个程序下载到 PLC：\n\n" +
                "    " + Path.GetFileName(path) + "\n\n" +
                (alreadyConnected
                    ? "• 已连接 PLC，将直接开始下载。\n"
                    : "• 尚未连接，将自动连接 PLC（USB / 串口 / 网口依次尝试）。\n"
                      + "  走 USB 时若缺驱动会自动安装（会弹 UAC，请点「是」）。\n") +
                "• 如果 PLC 正在运行，会自动停止后再下载。\n" +
                "• 下载期间请不要断电、不要拔通信线。\n" +
                "• 本工具为测试版，仅在 Easy320 上完成验证。\n\n" +
                "确定开始吗？",
                "确认下载", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK)
            {
                AppendLog("已取消：操作者在下载确认框中选择「取消」。");
                return;
            }

            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            SetBusy(true);
            SetProgress(0);
            btnDownloadSimple.Content = "下载中...";
            btnDownloadSimple.IsEnabled = false;

            AppendLog("--- 一键下载 开始 ---");
            AppendLog(alreadyConnected ? "      已连接 PLC，直接开始下载。" : "      未连接，自动连接 PLC ...");

            // ★ 进后台前把「本次要复用的已连接通道」快照下来：
            //   _active 是 UI 线程字段，后台线程直接读它属于跨线程访问。
            ITransport reused = alreadyConnected ? _active : null;

            Task.Run(() =>
            {
                // 本次下载是否由简易模式自己建立连接（决定结束时要不要断开）
                bool openedHere = false;
                try
                {
                    // ① 未连接则自动依次尝试（不限通道类型，也不需要用户选）
                    ITransport t = reused;
                    if (t == null)
                    {
                        t = TryConnectAny();
                        openedHere = (t != null);
                    }

                    if (t == null)
                    {
                        AppendLog("✗ 连接失败：USB / 串口 / 网口都没能连上 PLC。");
                        AppendLog("      请检查：① USB 线/串口线/网线是否插好；"
                                + "② USB 通道是否已关闭 AutoShop；③ 串口波特率（19200 ⇄ 9600）；"
                                + "④ 网口 IP 与端口 12939。");
                        AppendLog("      也可点「更多功能」→ 运行日志看每个通道的具体失败原因。");
                        AppendLog("--- 一键下载 失败 ---");
                        return;
                    }

                    // ② 按通道分派下载（与完整界面的「下载 / 烧录」同一条路径）
                    DoDownloadOn(t, path, true);
                }
                catch (Exception ex)
                {
                    AppendLog("✗ 下载失败：" + ex.Message);
                    AppendLog("--- 一键下载 失败 ---");
                }
                finally
                {
                    // 只断开「本次自己建立的」连接：
                    // 若进来时已连好（完整界面连的），保持连接不动，避免打断用户操作。
                    if (openedHere)
                    {
                        try { CloseActive("一键下载结束"); }
                        catch { }
                    }

                    Interlocked.Exchange(ref _busy, 0);
                    SetBusy(false);
                    RunOnUi(() =>
                    {
                        btnDownloadSimple.Content = "开始下载";
                        btnDownloadSimple.IsEnabled = true;
                    });
                }
            });
        }

        /// <summary>
        /// 依次尝试各通道连接，返回第一个成功的通道（并已登记为当前连接）。
        /// 每个通道的失败原因都写进日志，便于操作者把日志发给工程师排查。
        /// </summary>
        private ITransport TryConnectAny()
        {
            foreach (TransportKind kind in BuildAutoTryOrder())
            {
                ITransport t = null;
                try
                {
                    // USB 需要先保证 libusb0 驱动就绪（首次在裸机上会自动弹 UAC 安装）。
                    // 驱动装完必须重新插拔 USB 线，PLC 才会以 libusb0 身份枚举 —— 所以
                    // 这里首次自动安装后通常仍会失败，需要提示操作者「拔插一次再点下载」。
                    if (kind == TransportKind.Usb && !DriverInstaller.EnsureDriver(AppendLog))
                    {
                        if (!DriverInstaller.IsDriverInstalled())
                            throw new InvalidOperationException(
                                "libusb0 驱动尚未就绪：" + DriverInstaller.LastInstallHint);
                        throw new InvalidOperationException(
                            "libusb0 驱动刚装好，需重新插拔 PLC 的 USB 线后才生效。" +
                            "请拔掉再插上 USB 线（或点「更多功能」→「安装 / 修复驱动」手动重装），然后重新点「开始下载」。");
                    }

                    TransportOptions opt = BuildOptionsFor(kind);
                    t = opt.Create();

                    AppendLog("      尝试 " + KindName(kind) + "：" + t.DisplayName);
                    t.Open();

                    RunOnUi(() =>
                    {
                        _active = t;
                        _activeKind = kind;
                        lblPortState.Text = "已连接：" + t.DisplayName;
                        lblPortState.Foreground = System.Windows.Media.Brushes.Green;
                    });
                    UpdateUiLockState();

                    AppendLog("✓ 已连接：" + KindName(kind) + " / " + t.DisplayName);
                    return t;
                }
                catch (Exception ex)
                {
                    // 关键：Open() 可能在半途抛异常（如驱动缺失、端口被占用），
                    // 此时句柄已经拿到但没建立成功，必须显式释放，否则下一次尝试
                    // （甚至用户点「打开」）会报「设备被占用」。
                    if (t != null)
                    {
                        try { t.Close(); } catch { }
                        try { t.Dispose(); } catch { }
                    }
                    AppendLog("      ✗ " + KindName(kind) + " 不可用：" + ex.Message);
                }
            }
            return null;
        }

        /// <summary>
        /// 按指定通道构造连接参数（用界面/配置里的值，不依赖单选框当前选中项）。
        /// 与 <see cref="BuildOptions"/> 的差别仅在通道来源，参数值完全一致。
        ///
        /// ★ 本方法在后台线程被调用（简易模式一键下载），因此<b>不允许直接读控件</b>：
        ///   串口端口名先经 <see cref="PickSerialPortName"/> 切回 UI 线程取，其余全部走 <c>_cfg</c>。
        /// </summary>
        private TransportOptions BuildOptionsFor(TransportKind kind)
        {
            var o = new TransportOptions();
            o.Kind = kind;

            if (kind == TransportKind.Serial)
            {
                o.PortName = PickSerialPortName();
                o.BaudRate = _cfg.BaudRate;
                o.DataBits = _cfg.DataBits;
                o.Parity = ParseParity(_cfg.Parity);
                o.StopBits = _cfg.StopBits == "2" ? StopBits.Two : StopBits.One;
                o.UnitId = (byte)_cfg.SerialUnitId;
                o.Timeout = 3000;
            }
            else if (kind == TransportKind.Tcp)
            {
                if (string.IsNullOrWhiteSpace(_cfg.TcpHost))
                    throw new InvalidOperationException("未配置 PLC 的 IP 地址（可在「更多功能」里填写）。");
                o.Host = _cfg.TcpHost;
                o.Port = _cfg.TcpPort;
                o.UnitId = (byte)_cfg.TcpUnitId;
                o.Timeout = 3000;
            }
            else
            {
                o.Timeout = 10000;
            }
            return o;
        }

        /// <summary>
        /// 决定本次串口连接用哪个端口名。优先用 config.ini 里记住的端口；
        /// 若该端口已不在当前下拉列表中（换了电脑/换了 USB 口），退回到界面上选中的端口。
        ///
        /// ★ 读 <c>cmbPort.Items</c> / <c>cmbPort.SelectedItem</c> 必须回到 UI 线程，
        ///   否则后台线程访问会抛 InvalidOperationException（WindowsBase.dll）。
        /// </summary>
        private string PickSerialPortName()
        {
            return ShowModal(() =>
            {
                string port = _cfg.SerialPort;
                bool exists = false;
                foreach (object item in cmbPort.Items)
                {
                    if (item != null && string.Equals(item.ToString(), port, StringComparison.OrdinalIgnoreCase))
                    { exists = true; break; }
                }
                if (exists) return port;

                if (cmbPort.SelectedItem == null)
                    throw new InvalidOperationException("未找到可用串口（可在「更多功能」里点「刷新」）。");
                return cmbPort.SelectedItem.ToString();
            });
        }

        /// <summary>
        /// 在已连接的通道上执行下载。逻辑与完整界面 <see cref="BtnDownload_Click"/> 一致，
        /// 差别只有一处：通道已确定（不需要再判 null）。
        /// 所有输出统一进运行日志（简易模式不再有独立状态区）。
        ///
        /// ★ 本方法由后台线程执行（简易模式一键下载）。所有需要读界面控件的地方都必须
        ///   先经 Dispatcher 取值，绝不能在这里直接访问 <c>chkForce</c> 等控件。
        /// 「机型不匹配也继续」的勾选值在 UI 线程先取好，再传进来。
        /// </summary>
        private void DoDownloadOn(ITransport t, string path, bool simple)
        {
            // ★ 界面勾选值只能在 UI 线程读：先取值缓存，后面纯用这个 bool
            bool force = ShowModal(() => chkForce.IsChecked == true);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isDown = ext == ".down";
            bool isUpdown = ext == ".updown";
            if (!isDown && !isUpdown)
                throw new InvalidOperationException("不支持的程序包类型：" + ext + "（只支持 .down / .updown）。");

            // ---- 串口 / TCP：不依赖 USB 私有协议，走各自的烧录客户端 ----
            if (t is SerialLink || t is TcpLink)
            {
                if (t is SerialLink && isUpdown)
                {
                    MessageBoxResult rr = ShowModal(() => MessageBox.Show(
                        "你正在用串口烧录 .updown 包。\n\n" +
                        "• 官方推荐：串口用 .down、USB 用 .updown。\n" +
                        "• 实测结论：串口能烧 .updown，烧录后 PLC 同样能进入运行态（已真机验证）。\n" +
                        "• 风险：非官方组合，个别机型/版本未逐一验证，请确认后继续。\n\n" +
                        "是否继续用串口烧录这个 .updown 包？",
                        "串口烧录 .updown（需确认）",
                        MessageBoxButton.OKCancel, MessageBoxImage.Information));
                    if (rr != MessageBoxResult.OK)
                        throw new InvalidOperationException("操作者取消了串口烧录 .updown。");
                }

                DownPackage pkg = DownPackage.LoadRawAny(path);
                AppendLog(string.Format("已载入下载包：{0} 版本 {1} 机型 {2}，条目 {3} 个，合计 {4} 字节",
                    Path.GetFileName(pkg.Path), pkg.Version, pkg.Model, pkg.Order.Count, pkg.TotalBytes));

                if (t is SerialLink)
                {
                    AppendLog("--- 串口烧录 开始（0x43 协议，" + ext + "）---");
                    var sc = new SerialBurnClient((SerialLink)t);
                    sc.Handshake();
                    AppendLog("      已连接串口，PLC 机型=" + (string.IsNullOrEmpty(sc.DeviceModel) ? "(未解析)" : sc.DeviceModel));

                    int before = sc.ReadRunState();
                    AppendLog("      下载前运行态=" + (before < 0 ? "?" : before.ToString()) + "（1=运行 0=停止）");
                    if (before == 1 && !ConfirmStopBeforeDownload())
                        throw new InvalidOperationException("操作者取消了下载（PLC 处于运行状态）。");

                    sc.Burn(pkg, msg => AppendLog(msg), pct => SetProgress(pct), false);
                    AppendLog("--- 串口烧录 完成 ---");

                    if (before == 1) ConfirmResumeRunAfterDownload(() => ResumeSerial(sc));
                }
                else
                {
                    AppendLog("--- TCP 烧录 开始（12939 私有协议，" + ext + "）---");
                    var tc = new TcpBurnClient(t);
                    tc.Handshake();
                    tc.Login();
                    tc.Session();
                    AppendLog("      已连接网口，PLC 机型=" + (string.IsNullOrEmpty(tc.DeviceModel) ? "(未解析)" : tc.DeviceModel));

                    int before = tc.ReadRunState();
                    AppendLog("      下载前运行态=" + (before < 0 ? "?" : before.ToString()) + "（1=运行 0=停止）");
                    if (before == 1 && !ConfirmStopBeforeDownload())
                        throw new InvalidOperationException("操作者取消了下载（PLC 处于运行状态）。");

                    tc.Burn(Path.GetFileName(pkg.Path), pkg.Order, pkg.Files,
                            msg => AppendLog(msg), pct => SetProgress(pct), false);
                    AppendLog("--- TCP 烧录 完成 ---");

                    if (before == 1) ConfirmResumeRunAfterDownload(() => ResumeTcp(tc));
                }
                return;
            }

            // ---- USB：走 PlcClient 私有帧，.down 与 .updown 都按条目化下发 ----
            using (var client = new PlcClient(t))
            {
                AppendLog("--- 程序下载 开始（USB / " + ext + "）---");

                client.Login();
                int before = client.GetRunState();
                AppendLog("      下载前运行态=" + (before < 0 ? "?" : before.ToString()) + "（1=运行 0=停止）");
                if (before == 1 && !ConfirmStopBeforeDownload())
                    throw new InvalidOperationException("操作者取消了下载（PLC 处于运行状态）。");

                // resumeRunState = -1：不自动恢复运行，改由下面按需询问
                if (isDown)
                {
                    DownPackage pkg = DownPackage.Load(path);
                    AppendLog(string.Format("已载入下载包：{0} 版本 {1} 机型 {2}，条目 {3} 个，合计 {4} 字节",
                        Path.GetFileName(pkg.Path), pkg.Version, pkg.Model, pkg.Order.Count, pkg.TotalBytes));
                    client.Download(pkg, force, msg => AppendLog(msg), pct => SetProgress(pct), -1);
                }
                else
                {
                    UpdownPackage pkg = UpdownPackage.Load(path);
                    AppendLog(string.Format("已载入下载包：{0} 版本 {1} 机型 {2}，条目 {3} 个，合计 {4} 字节",
                        Path.GetFileName(pkg.Path), pkg.Version, pkg.Model, pkg.Order.Count, pkg.TotalBytes));
                    client.Download(pkg, force, msg => AppendLog(msg), pct => SetProgress(pct), -1);
                }

                AppendLog("--- 程序下载 完成 ---");

                if (before == 1) ConfirmResumeRunAfterDownload(() => ResumeUsb(client));
            }
        }
    }
}
