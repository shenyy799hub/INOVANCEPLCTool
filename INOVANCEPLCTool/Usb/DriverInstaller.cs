using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace INOVANCEPLCTool.Usb
{
    /// <summary>
    /// 驱动自检与按需安装。
    ///
    /// 设计：整套 H5U USB 驱动（libusb-win32：amd64/x86/ia64 三套内核驱动 + 带签名的
    /// H5U_USB_Device.inf / .cat）已作为嵌入资源 drivers.zip 编译进 exe。
    ///   - 程序启动时不做任何事；
    ///   - 仅在“检测到目标机未安装 libusb0 内核驱动”时，才把驱动文件释放到磁盘、
    ///     生成 install_driver.bat 并以管理员权限执行它（pnputil 安装 inf）。
    /// 装过 AutoShop / 已装 libusb-win32 驱动的目标机不会被打扰。
    ///
    /// 注意：内核驱动无法随托管程序“内存加载”，必须落地磁盘 + 进系统驱动目录 + 注册，
    /// 因此“检测缺失→释放文件→输出安装脚本→提权执行”是这套技术栈下的标准做法。
    /// </summary>
    internal static class DriverInstaller
    {
        private const string RES_DRIVERS = "INOVANCEPLCTool.drivers.zip";
        private const string SERVICE_KEY = @"SYSTEM\CurrentControlSet\Services\libusb0";

        /// <summary>
        /// 最近一次安装失败的原因（给调用方拼用户可读的提示用）。
        /// <see cref="InstallDriver"/> 的日志只进运行日志，界面提示需要一句干净的结论，
        /// 因此额外把「为什么没装成」记在这里。安装成功时置为 null。
        /// </summary>
        public static string LastInstallHint { get; private set; }

        /// <summary>
        /// 检测 libusb0 内核驱动是否已安装。
        /// libusb-win32 安装后会在服务控制管理器注册名为 "libusb0" 的内核服务，
        /// 该注册表键存在即代表驱动已就位（与 PLC 是否插着无关）。
        /// </summary>
        public static bool IsDriverInstalled()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(SERVICE_KEY))
                    return k != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从嵌入资源把驱动解压到工作目录，返回工作根目录（其中含 H5U_usb_driver\ 子目录）。
        /// 优先写到 exe 旁边；若不可写（如装在 Program Files 且未提权），回退到 %TEMP%。
        /// 已解压过则直接复用，不重复释放。
        /// </summary>
        public static string ExtractDrivers()
        {
            string workRoot = ChooseWorkRoot();
            string infPath = Path.Combine(workRoot, "H5U_usb_driver", "H5U_USB_Device.inf");
            if (!File.Exists(infPath))
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(RES_DRIVERS))
                {
                    if (s == null)
                        throw new Exception("内嵌驱动资源缺失：" + RES_DRIVERS);
                    // 注意：.NET Framework 4.7.2 的 ZipFile.ExtractToDirectory 只有
                    // (string, string) 重载，没有 (Stream, string)。这里改用 ZipArchive
                    // 直接从流解压。zip 内条目以 "H5U_usb_driver/..." 开头，会自然形成子目录。
                    using (var archive = new ZipArchive(s, ZipArchiveMode.Read))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue; // 跳过纯目录条目
                            string dest = Path.Combine(workRoot, entry.FullName);
                            string fullDest = Path.GetFullPath(dest);
                            string rootFull = Path.GetFullPath(workRoot);
                            if (!fullDest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                                continue; // 防御 zip 路径穿越
                            string dir = Path.GetDirectoryName(fullDest);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            using (var es = entry.Open())
                            using (var fs = File.Create(fullDest))
                            {
                                es.CopyTo(fs);
                            }
                        }
                    }
                }
            }
            return workRoot;
        }

        /// <summary>
        /// 生成安装脚本 install_driver.bat（含 pnputil 安装命令）。
        /// 返回脚本完整路径。
        /// </summary>
        public static string GenerateInstallScript(string workRoot)
        {
            string batPath = Path.Combine(workRoot, "install_driver.bat");
            // 自动调用时传 "silent" 参数，不弹 pause；手动双击则等待用户按键。
            // 兼容 Win7 / Win10+：先试 Win7 语法 (pnputil -i -a)，失败再回退 Win10 语法
            // (pnputil /add-driver ... /install)。该 inf 无 DefaultInstall 节，只能用 pnputil 装。
            //
            // 关键坑：提权后的 cmd 在某些 Win7 环境下 PATH 里搜不到 pnputil.exe，会报
            // “不是内部或外部命令”。因此改用完整路径 %SystemRoot%\System32\pnputil.exe
            // （由 64 位提权 cmd 解析，天然对位、绕过 PATH 问题）。
            const string script =
                "@echo off\r\n" +
                "set SILENT=0\r\n" +
                "if \"%~1\"==\"silent\" set SILENT=1\r\n" +
                "set INF=\"%~dp0H5U_usb_driver\\H5U_USB_Device.inf\"\r\n" +
                "set PNP=%SystemRoot%\\System32\\pnputil.exe\r\n" +
                "echo 正在安装 H5U USB 驱动 (libusb-win32) ...\r\n" +
                "if exist \"%PNP%\" (\r\n" +
                "  \"%PNP%\" -i -a %INF%\r\n" +
                ") else (\r\n" +
                "  echo 未找到 %PNP%，回退到 PATH 中的 pnputil ...\r\n" +
                "  pnputil -i -a %INF%\r\n" +
                ")\r\n" +
                "set RC=%errorlevel%\r\n" +
                "if %RC%==0 goto OK\r\n" +
                "echo 旧语法失败，尝试新语法 (Win10+)...\r\n" +
                "if exist \"%PNP%\" (\r\n" +
                "  \"%PNP%\" /add-driver %INF% /install\r\n" +
                ") else (\r\n" +
                "  pnputil /add-driver %INF% /install\r\n" +
                ")\r\n" +
                "set RC=%errorlevel%\r\n" +
                "if %RC%==0 goto OK\r\n" +
                "echo 安装失败（错误码 %RC%），请右键本文件选择“以管理员身份运行”。\r\n" +
                "goto END\r\n" +
                ":OK\r\n" +
                "echo 驱动安装完成。\r\n" +
                ":END\r\n" +
                "if %SILENT%==0 pause\r\n" +
                "exit /b %RC%\r\n";
            // 用系统 ANSI 编码写入（中文 Windows 下为 GBK），使 cmd 控制台能正确显示中文，
            // 避免 UTF-8 中文在 GBK 代码页下变成乱码。
            File.WriteAllText(batPath, script, Encoding.Default);
            return batPath;
        }

        /// <summary>
        /// 自检并（必要时）安装驱动。驱动已存在时直接返回 true，不打扰用户。
        /// 返回 true 表示驱动当前可用（原本已装，或本次安装已成功）。
        /// </summary>
        public static bool EnsureDriver(Action<string> log)
        {
            if (IsDriverInstalled())
            {
                LastInstallHint = null;
                log("驱动检测：libusb0 驱动已安装，无需操作。");
                return true;
            }
            log("驱动检测：未安装 libusb0 驱动，准备安装（将弹出 UAC 与管理员 cmd 窗口）...");
            return InstallDriver(log, silent: true);
        }

        /// <summary>
        /// 强制安装 / 修复驱动（无论是否已安装都执行）。供「安装/修复驱动」按钮调用，
        /// 用于在本机已装 AutoShop 的情况下也能验证安装流程，或用于驱动损坏时修复。
        /// silent=true 时不保留 cmd 窗口（自动安装场景）；silent=false 时 cmd 窗口会
        /// 停留显示结果（手动点击场景，便于观察）。
        /// </summary>
        public static bool InstallDriver(Action<string> log, bool silent = false)
        {
            try
            {
                string workRoot = ExtractDrivers();
                string bat = GenerateInstallScript(workRoot);
                log("驱动文件已释放到：" + workRoot);
                if (IsRunningAsAdmin())
                    log("当前进程已是管理员，直接执行安装脚本（将弹出 cmd 窗口）...");
                else
                    log("即将弹出 UAC 管理员运行申请，请点击“是”（随后会弹出 cmd 窗口显示安装进度）...");

                bool ok = RunAsAdmin(bat, silent);
                if (ok)
                {
                    // 给 pnputil 一点时间把驱动写入系统目录
                    System.Threading.Thread.Sleep(1500);
                    if (IsDriverInstalled())
                    {
                        LastInstallHint = null;
                        log("驱动安装成功。请重新插拔 PLC 的 USB 线后再继续操作。");
                        return true;
                    }
                    LastInstallHint =
                        "安装脚本已执行成功，但系统里仍未注册 libusb0 服务（可能受驱动签名策略或安全软件拦截）。\n"
                      + "  可手动右键「以管理员身份运行」：" + bat;
                    log("安装脚本已执行，但驱动尚未生效。请手动以管理员身份运行：" + bat);
                    return false;
                }
                LastInstallHint =
                    "未获得管理员权限，安装被取消或失败（UAC 被拒绝）。\n"
                  + "  请点「更多功能」→「安装 / 修复驱动」重试，或在弹出的 UAC 里点「是」。";
                log("未获得管理员权限，安装未执行。请手动以管理员身份运行：" + bat);
                return false;
            }
            catch (Exception ex)
            {
                LastInstallHint = "驱动释放/安装失败：" + ex.Message;
                log("驱动释放/安装失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>以管理员身份运行安装脚本。</summary>
        /// 关键坑：直接对 .bat 设 Verb="runas" 在不少 Windows 上【不会弹出 UAC】
        /// （.bat 不是可执行文件，Shell 对其 runas 的处理不可靠，常被静默忽略）。
        /// 正确做法：提升一个真正的可执行宿主（cmd.exe），再由它去运行脚本。
        /// 这里用 cmd.exe（目标环境为 Win7，cmd 比 PowerShell 更稳）。
        /// 若当前进程已是管理员则直接运行（不再弹 UAC，省一次确认）。
        /// silent=true 时给批处理传 "silent" 参数使其执行后不暂停关闭；
        /// false 时批处理执行完会 pause，cmd 窗口停留便于观察。
        /// 返回脚本退出码是否为 0。
        private static bool RunAsAdmin(string batPath, bool silent)
        {
            try
            {
                bool needElevate = !IsRunningAsAdmin();
                var psi = new ProcessStartInfo
                {
                    // 提升 cmd.exe 这个真·可执行文件，由它再去跑批处理
                    FileName = "cmd.exe",
                    // /c 执行批处理；路径用引号包裹以防空格；silent 时透传 silent 参数
                    Arguments = "/c \"" + batPath + "\"" + (silent ? " silent" : ""),
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                if (needElevate)
                    psi.Verb = "runas";   // 触发 UAC 管理员运行申请

                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                // 用户拒绝 UAC 或启动失败
                return false;
            }
        }

        /// <summary>判断当前进程是否以管理员身份运行。</summary>
        private static bool IsRunningAsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>选择可写的工作根目录；exe 目录不可写时回退到 %TEMP%。</summary>
        private static string ChooseWorkRoot()
        {
            string candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "INOVANCE_Drivers");
            try
            {
                Directory.CreateDirectory(candidate);
                string probe = Path.Combine(candidate, ".writetest");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                string tmp = Path.Combine(Path.GetTempPath(), "INOVANCE_Drivers");
                Directory.CreateDirectory(tmp);
                return tmp;
            }
        }
    }
}
