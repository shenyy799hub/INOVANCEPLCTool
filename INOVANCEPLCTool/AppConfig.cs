using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace INOVANCEPLCTool
{
    /// <summary>
    /// 轻量 INI 配置（exe 同级 <c>config.ini</c>）。
    ///
    /// 设计目标（与沈工约定）：
    ///   1) 启动时若 exe 同级存在 <c>config.ini</c> 则自动加载；
    ///   2) 界面参数变更后自动保存，无需手动点「保存」；
    ///   3) 与程序包相关的路径一律存 <b>相对 exe 的路径</b>，
    ///      这样整个文件夹拷给别人后依然能正确找到文件（不依赖盘符/用户名）。
    ///
    /// 说明：本类**不抛异常**——配置读写出问题只静默降级（用默认值 / 放弃保存），
    ///       绝不能因为一个 ini 文件让主功能起不来。
    /// </summary>
    internal sealed class AppConfig
    {
        // ---------------- 配置项（内存中的当前值） ----------------

        /// <summary>上次选择的程序包路径（相对 exe 的路径，或绝对路径）。</summary>
        public string LastProgramPath = "";

        /// <summary>上次使用的连接通道：Usb / Serial / Tcp。</summary>
        public string Channel = "Usb";

        /// <summary>串口名（如 COM9）。</summary>
        public string SerialPort = "";

        /// <summary>波特率。</summary>
        public int BaudRate = 19200;

        /// <summary>数据位。</summary>
        public int DataBits = 8;

        /// <summary>校验（None / Even / Odd）。</summary>
        public string Parity = "None";

        /// <summary>停止位（1 / 2）。</summary>
        public string StopBits = "1";

        /// <summary>串口从站地址。</summary>
        public int SerialUnitId = 1;

        /// <summary>TCP 目标 IP。</summary>
        public string TcpHost = "192.168.1.88";

        /// <summary>TCP 端口（烧录/在线读写 12939）。</summary>
        public int TcpPort = 12939;

        /// <summary>TCP 单元 ID。</summary>
        public int TcpUnitId = 1;

        /// <summary>「机型不匹配也继续」勾选状态。</summary>
        public bool ForceContinue = false;

        // ---------------- 路径 ----------------

        /// <summary>config.ini 的完整路径（exe 同级）。</summary>
        public static string ConfigPath
        {
            get { return Path.Combine(AppDir, "config.ini"); }
        }

        /// <summary>程序包存放目录（exe 同级 Programs）。</summary>
        public static string ProgramsDir
        {
            get { return Path.Combine(AppDir, "Programs"); }
        }

        /// <summary>exe 所在目录（用 Assembly 位置而非 CurrentDirectory，避免快捷方式改变工作目录）。</summary>
        private static string AppDir
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                }
                catch { return Environment.CurrentDirectory; }
            }
        }

        // ---------------- 读写 ----------------

        /// <summary>
        /// 加载配置。文件不存在或解析失败都返回一个全默认的实例（不抛异常）。
        /// </summary>
        public static AppConfig Load()
        {
            var cfg = new AppConfig();
            try
            {
                if (!File.Exists(ConfigPath)) return cfg;

                foreach (string raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[') continue;   // 忽略节名

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();

                    switch (k.ToLowerInvariant())
                    {
                        case "lastprogram": cfg.LastProgramPath = v; break;
                        case "channel": cfg.Channel = v; break;
                        case "serialport": cfg.SerialPort = v; break;
                        case "baudrate": cfg.BaudRate = ParseInt(v, cfg.BaudRate); break;
                        case "databits": cfg.DataBits = ParseInt(v, cfg.DataBits); break;
                        case "parity": cfg.Parity = v; break;
                        case "stopbits": cfg.StopBits = v; break;
                        case "serialunitid": cfg.SerialUnitId = ParseInt(v, cfg.SerialUnitId); break;
                        case "tcphost": cfg.TcpHost = v; break;
                        case "tcpport": cfg.TcpPort = ParseInt(v, cfg.TcpPort); break;
                        case "tcpunitid": cfg.TcpUnitId = ParseInt(v, cfg.TcpUnitId); break;
                        case "forcecontinue": cfg.ForceContinue = ParseBool(v); break;
                    }
                }
            }
            catch { /* 读失败：用默认值 */ }
            return cfg;
        }

        /// <summary>
        /// 保存配置。写失败（目录只读等）时静默放弃，不影响主流程。
        /// </summary>
        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; " + AppInfo.ProductName + " " + AppInfo.VersionDisplay + " 配置文件");
                sb.AppendLine("; 由程序自动生成 / 更新，一般不需要手动编辑。");
                sb.AppendLine("; 程序包路径一律使用相对路径（相对本 exe），整个文件夹拷走仍可用。");
                sb.AppendLine();
                sb.AppendLine("[General]");
                sb.AppendLine("LastProgram=" + LastProgramPath);
                sb.AppendLine("Channel=" + Channel);
                sb.AppendLine("ForceContinue=" + (ForceContinue ? "1" : "0"));
                sb.AppendLine();
                sb.AppendLine("[Serial]");
                sb.AppendLine("SerialPort=" + SerialPort);
                sb.AppendLine("BaudRate=" + BaudRate);
                sb.AppendLine("DataBits=" + DataBits);
                sb.AppendLine("Parity=" + Parity);
                sb.AppendLine("StopBits=" + StopBits);
                sb.AppendLine("SerialUnitId=" + SerialUnitId);
                sb.AppendLine();
                sb.AppendLine("[Tcp]");
                sb.AppendLine("TcpHost=" + TcpHost);
                sb.AppendLine("TcpPort=" + TcpPort);
                sb.AppendLine("TcpUnitId=" + TcpUnitId);

                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch { /* 写失败：忽略 */ }
        }

        // ---------------- 相对路径换算 ----------------

        /// <summary>
        /// 把绝对路径转成「相对 exe 目录」的路径（若在 exe 目录内或在其子目录内）；
        /// 不在 exe 目录内则原样返回绝对路径。
        /// </summary>
        public static string ToRelative(string absPath)
        {
            try
            {
                if (string.IsNullOrEmpty(absPath)) return "";
                string full = Path.GetFullPath(absPath);
                string root = Path.GetFullPath(AppDir);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    root += Path.DirectorySeparatorChar;

                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = full.Substring(root.Length);
                    return ".\\" + rel;   // 统一写成 .\Programs\xxx.down
                }
                return full;              // exe 目录之外：只能存绝对路径
            }
            catch { return absPath; }
        }

        /// <summary>
        /// 把配置里的路径还原成绝对路径。
        /// 相对路径以 exe 目录为基准解析（这正是「拷走仍可用」的关键）。
        /// </summary>
        public static string ToAbsolute(string cfgPath)
        {
            try
            {
                if (string.IsNullOrEmpty(cfgPath)) return "";
                if (Path.IsPathRooted(cfgPath)) return Path.GetFullPath(cfgPath);
                return Path.GetFullPath(Path.Combine(AppDir, cfgPath));
            }
            catch { return cfgPath; }
        }

        // ---------------- 小工具 ----------------

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), out v) ? v : fallback;
        }

        private static bool ParseBool(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "yes" || s == "on";
        }

        // ---------------- 程序包目录 ----------------

        /// <summary>确保程序包目录存在；失败返回 false（不抛异常）。</summary>
        public static bool EnsureProgramsDir(out string dir)
        {
            dir = ProgramsDir;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把已选的程序包拷贝到 <c>Programs\</c> 目录；返回拷贝后的完整路径。
        ///
        /// 规则：
        ///   - 若源文件已在 Programs 目录内，直接返回原路径（不重复拷贝）；
        ///   - 同名文件已存在且内容一致（长度+最后写入时间）则跳过拷贝；
        ///   - 其余情况直接覆盖拷贝，保证「同名即最新」。
        /// 失败时抛出异常，由调用方决定是否提示。
        /// </summary>
        public static string CopyIntoPrograms(string srcPath)
        {
            string full = Path.GetFullPath(srcPath);
            string dir;
            if (!EnsureProgramsDir(out dir))
                throw new IOException("无法创建程序包目录：" + ProgramsDir);

            string destDir = Path.GetFullPath(dir);
            string srcDir = Path.GetDirectoryName(full) ?? "";
            if (string.Equals(srcDir.TrimEnd('\\'), destDir.TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase))
                return full;   // 已经在该目录内

            string dest = Path.Combine(destDir, Path.GetFileName(full));

            var si = new FileInfo(full);
            var di = File.Exists(dest) ? new FileInfo(dest) : null;
            if (di != null && di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc)
                return dest;   // 内容看起来一致，跳过

            File.Copy(full, dest, true);
            return dest;
        }

        /// <summary>
        /// 列出 Programs 目录内的所有程序包（.down / .updown），按名称排序。
        /// 目录不存在返回空列表。
        /// </summary>
        public static List<string> ListPrograms()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(ProgramsDir)) return list;
                foreach (string f in Directory.GetFiles(ProgramsDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".down" || ext == ".updown") list.Add(f);
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return list;
        }
    }
}
