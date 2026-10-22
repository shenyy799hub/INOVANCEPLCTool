using System;
using System.Reflection;

namespace INOVANCEPLCTool
{
    /// <summary>
    /// 全局产品标识：软件名称与版本号。
    ///
    /// <para>
    /// <b>单一数据源</b>：窗口标题、运行日志抬头、关于信息等一切要显示名称/版本的地方，
    /// 都必须从这里取，不要再各自硬编码字符串——否则改版本时必然漏掉某一处。
    /// </para>
    ///
    /// <para>
    /// 版本号的权威来源是 <c>AssemblyFileVersion</c>（在 Properties\AssemblyInfo.cs 里维护），
    /// 这里通过反射读回来，因此<b>只需改一处</b>：
    ///   AssemblyInfo.cs → <c>[assembly: AssemblyFileVersion("1.0.0")]</c>
    /// 界面标题与日志会跟着变。若反射失败（极少见），回退到 <see cref="FallbackVersion"/>。
    /// </para>
    /// </summary>
    internal static class AppInfo
    {
        /// <summary>软件名称（界面标题、日志抬头统一用它）。</summary>
        public const string ProductName = "汇川 PLC 工具";

        /// <summary>
        /// 归属声明：本工具为第三方非官方作品，与汇川技术无隶属关系。
        /// 挂在窗口标题行旁边，避免被误认为官方配套软件。
        /// </summary>
        public const string Attribution = "第三方非官方工具，仅供学习交流";

        /// <summary>反射失败时的兜底版本号，与 AssemblyInfo.cs 保持同步。</summary>
        private const string FallbackVersion = "1.0.0";

        /// <summary>副标题：说明这个工具能干什么（只用于界面内的说明文字，不挂到窗口标题上）。</summary>
        public const string Tagline = "Run/Stop + 程序下载/串口烧录";

        private static string _version;

        /// <summary>
        /// 版本号（形如 <c>1.0.0</c>，不带前导 v）。
        /// 取 <c>AssemblyFileVersion</c>；若末尾两段均为 0（如 1.0.0.0）则自动省略，读起来更干净。
        /// </summary>
        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    _version = ReadFileVersion(Assembly.GetExecutingAssembly());
                }
                catch
                {
                    _version = FallbackVersion;
                }
                return _version;
            }
        }

        /// <summary>形如 <c>v1.0.0</c>，用于界面显示。</summary>
        public static string VersionDisplay { get { return "v" + Version; } }

        /// <summary>
        /// 窗口标题：<c>汇川 PLC 工具 v1.0.0</c>。
        /// 刻意<b>不</b>拼 <see cref="Tagline"/>——窗口标题栏空间有限，
        /// 功能说明放在界面内的标题行与 README 里就够了，标题保持干净。
        /// </summary>
        public static string FullTitle
        {
            get { return ProductName + " " + VersionDisplay; }
        }

        /// <summary>形如 <c>汇川 PLC 工具 v1.0.0（测试版，仅 Easy320 已验证）</c>，用于日志抬头。</summary>
        public static string LogHeader
        {
            get { return ProductName + " " + VersionDisplay; }
        }

        // 单独抽出来是为了把 try/catch 收窄，避免把 VersionDisplay 之类的取值也裹进去
        private static string ReadFileVersion(Assembly asm)
        {
            object[] attrs = asm.GetCustomAttributes(typeof(AssemblyFileVersionAttribute), false);
            if (attrs == null || attrs.Length == 0) return FallbackVersion;

            string raw = ((AssemblyFileVersionAttribute)attrs[0]).Version;
            if (string.IsNullOrEmpty(raw)) return FallbackVersion;

            // "1.0.0.0" -> "1.0.0"；"1.2.0.0" -> "1.2"；"1.2.3.4" -> 原样
            string[] parts = raw.Split('.');
            if (parts.Length == 4)
            {
                if (parts[3] == "0" && parts[2] == "0") return parts[0] + "." + parts[1];
                if (parts[3] == "0") return parts[0] + "." + parts[1] + "." + parts[2];
            }
            return raw;
        }
    }
}
